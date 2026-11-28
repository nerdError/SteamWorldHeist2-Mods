/*
 * SWH2 netplay groundwork (docs/MULTIPLAYER.md, step 3): record and replay of tactics commands, to find out whether
 * replaying the same commands from the same mission save gives the same battle — before any network code.
 *
 * Every action in a mission goes through ActorSimulator::RequestAction (0x38b9b0):
 *     void RequestAction(ActorSimulator *sim, Actor *actor, int actionIndex, const Target *target, bool flag)
 * where actor->actions (vector at +0x50, 0x38-byte entries, std::string name first) is indexed by actionIndex and
 * Target is a std::variant (index byte at +0x20): 0 none, 1 position (2 floats), 2 actor/entity id, 3 aim angle
 * (float), 4 movement path (vector of 16-byte nodes + 8 bytes), 5 vector of 8-byte items.
 * The DLL hooks it (inline: 14-byte absolute jmp, trampoline with the copied prologue), plus
 *  - ActorSimulator::Update (0x3896d0, every frame of the tactics world): frame counter, replay pump;
 *  - FactionDone (0x484f60): end of a faction's turn.
 * The caller is found from the return addresses (RtlCaptureStackBackTrace): player (PlayerTurnSimulator), ai
 * (EnemyTurnSimulator), lua (mission scripts), projectile (consequences of a shot), preview (OutcomeSimulator, runs
 * on a copy of the world — counted, never logged or replayed).
 *
 * Modes (first word of %APPDATA%\Thunderful\SteamWorld Heist II\swh2_net.txt; enabled at install by the Coop
 * setting "netTest", see CoopMod.AddDllLoader):
 *   record (default) — log to swh2_net_record.log;
 *   replay           — the network guest: read the commands and turn ends of swh2_net_replay.log (a copy of a
 *                      record) and issue them in record order when the game is idle, log everything to
 *                      swh2_net_replayed.log. The guest's own AI is off (the enemy turn simulator 0x43ac30 and the AI
 *                      steps are not run): the host's AI commands and enemy turn ends come from the record like the
 *                      player's. Before every replayed command and turn end the RNG state and the entity id counter
 *                      are set to the recorded values (what a host would send its guests). Turn ends the game gives
 *                      itself (player after the last action point, scripts) are waited for, forced after END_WAIT.
 * The mission hook (Lua/mission_hook.lua) adds "state" lines (all actors: id, position, health) after every action;
 * The Coop "Netplay test…" loader window / swh2 nettest (NetTest.cs) runs the steps and compares a record with its replay.
 *
 * Log lines (one event each; f = tactics frame, ignored by the comparison):
 *   cmd <seq> o=<origin> a=<actor id> i=<action index> n=<action name> t=<type>:<hex> fl=<flag> r=<z>:<w>
 *       e=<entity id counter> d=<nesting depth> f=<frame> pv=<RequestActions on preview copies so far>
 *       [bt=<return addresses, nested calls only>]
 *   end <seq> o=<origin> r=<z>:<w> e=<entity id counter> f=<frame>
 * Entity ids come from ++[entity manager + 0x58] (0x742db0; manager = world + 0x1c48), shared with special effects,
 * so ids of actors spawned later differ between runs unless the counter is synchronized.
 */
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <limits.h>
#include <intrin.h>
#include "netplay.h"
#include "steamnet.h"
#pragma intrinsic(_ReturnAddress)

/* Steam-build RVAs (ExeRva translates them for GOG). */
#define RVA_RequestAction 0x38b9b0
#define RVA_SimUpdate 0x3896d0
#define RVA_FactionDone 0x484f60
#define RVA_IsActionsOngoing 0x38e1e0   /* bool (ActorSimulator *) — Lua IsActionsOngoing */
#define RVA_ActorStorage 0x123a00       /* (world + 0x1c48, out) -> out[0] = storage, actor list at storage + 0xd0 */
#define RVA_RngPtr 0xea1b98             /* -> { u32 z, u32 w } */
#define RVA_FactionDoneTypeId 0xea9068  /* simulator slot of the FactionDone object (Lua SetCurrentFactionDone) */

#define TARGET_SIZE 0x28
#define MAX_VEC_BYTES 4096
#define IDLE_FRAMES 45
#define END_WAIT 150
#define GATE_TIMEOUT 900   /* frames idle waiting for recorded enemy / script events before going on anyway */

typedef void (*RequestAction_t)(void *sim, void *actor, int index, const void *target, unsigned char flag);
typedef ULONG_PTR (*SimUpdate_t)(void *sim, void *arg);
typedef ULONG_PTR (*FactionDone_t)(void *turn);
typedef unsigned char (*IsActionsOngoing_t)(void *sim);
typedef void *(*ActorStorage_t)(void *components, void *out);

static char *base;
static char *atRngPtr, *atActorStorage, *atFactionDoneTypeId, *atIsActionsOngoing;  /* resolved in NetInstall */
static RequestAction_t realRequestAction;
static SimUpdate_t realSimUpdate;
static FactionDone_t realFactionDone;
static int mode = NET_OFF;
static wchar_t outPath[MAX_PATH], replayPath[MAX_PATH], modePath[MAX_PATH];
static unsigned seq, frame, previews;
static void *mainSim;
static int replaying;   /* our own call into the game is in progress */
/* Autoplay (swh2_net.txt = autoplay): replay mode that only plays the player's commands of swh2_net_replay.log and
 * otherwise behaves like a recording (no RNG / counter sync, own AI, no state correction), written as a record to
 * swh2_net_record.log with o=player — a fresh record from an old one with no one at the keyboard. */
static int autoplay;
static int guest;        /* replay, not autoplay: AI off, everything from the record */
/* live (guest over the network, nettcp.c: the replay file grows while the battle runs) is declared further down. */
static int earlyEnds;    /* live: turn ends the game gave before the host's line for them arrived */
static void ReadMore(void);
static int replayAs = -1;   /* origin of the recorded entry being issued (logged instead of "replay"; -1 = player) */
static const char *OriginLabel(int o)
{
    if (o != ORIGIN_REPLAY) return NetOriginName(o);
    return autoplay ? "player" : replayAs >= 0 ? NetOriginName(replayAs) : "replay";
}
static int depth;       /* RequestAction nesting */
static int inFactionDone;

/* Guest commands (network step 2): the live guest's own player commands and turn ends are not executed locally but
 * sent to the host (NetTcpSend), which plays them (HostPump) and logs them with " by=guest"; they come back to the
 * guest in the host's log and are replayed like the host's own. Lines guest -> host:
 *   gcmd ao=<actor ordinal> i=<action index> t=<type>:<hex> to=<target ordinal> fl=<flag> f=<guest frame>
 *   gend n=<turn ends of the host's log the guest had read>   (the host ends the turn only if it is still that one)
 * Commands sent in the same guest frame (move, then open the door at its end) are played together by the host. */
static int hosting;           /* streams its log to a guest: takes the guest's commands */
static int endsLogged;        /* turn ends logged here (host: the number a gend must match) */
static int endsArrived;       /* guest: turn ends in the host's log read so far */
static int pending;           /* guest: commands sent, not back in the host's log yet */
static unsigned pendingFrame, pendingAt;
static int endAskedFor = -1;  /* guest: endsArrived when a turn end was asked for (one per turn) */
static const char *bySuffix = "";   /* host: " by=guest" on the lines of the guest's commands */
static int guestWait;   /* autoplay host: frames waited for the guest's command at the head */
/* Waiting for both before the battle: the host's battle is held (no selection, no player commands or turn ends, the
 * autoplay script waits) until the guest's game says "ready" from its own mission world — so the host can't play
 * ahead while the guest still loads or watches the intro. The guest says it on every connect (reconnects too). */
static int guestIn;
#define HELD() (hosting && !guestIn && !guestAway)
static int joinedMid;      /* guest: its save is from inside the host's mission (session "from"): no scene line comes */
static int readySentFor;   /* guest: the connect (NetTcpReconnects() + 1) "ready" was sent on */
static int ordSeeded;      /* guest, joinedMid: the host's ordinals are in (once per joined battle) */
/* The next battle without a restart (see NetIdle): the guest's slot file, the host's save waiting for the main menu,
 * and the one written there, whose battle the replay rebases to when it loads. */
static wchar_t guestSlotPath[MAX_PATH];
static void WriteCoopSlot(void);
static int steamMode;   /* see ReadSession */
static char *nextSave;
static int nextSaveLen, nextEnds, rejoinEnds, rejoinPending;
static int scenesMade;   /* guest: scenes (battles, the world map) this game made so far */
static int leftMid;   /* the guest quit to the menu inside the host's battle: it may join that battle again */
static long long nextFrom, rejoinFrom;
static void ResetForJoin(int ends);
static int askedFor;    /* autoplay host: scriptPos + 1 the guest was asked to play */
#define GUEST_WAIT 3600   /* the guest always answers (a command or gskip): this is only for a lost connection */
#define PENDING_TIMEOUT 600   /* frames: a sent command that never came back is given up */

/* Owners (network step 2, two players): the crew in the order it came onto the field (the mission Lua writes the
 * entity ids to swh2_net_crew.txt, coop.lua C.netCrew; read twice a second) is dealt alternately: 1st host, 2nd guest,
 * 3rd host... Both games deal the same battle, so they agree without talking. Actors not in the list (allies of the
 * mission) belong to no one. A player can select only their own (CanSelect hook, also skips the others when cycling
 * with Tab); the guest sends only commands for its own and the host refuses the guest's commands for its own. */
static wchar_t crewPath[MAX_PATH];
static int crewIds[64], crewLen;
static char crewPersona[64][48];   /* persona of each (the portrait slots know only that) */
static void RepaintSlots(void);
static int me;   /* 1 host, 2 guest, 0 = no network game */

static void ReadCrew(void)
{
    FILE *f = _wfopen(crewPath, L"r");
    if (!f) return;
    /* "id=persona ..." (older: "id ...") */
    int n = 0, id;
    char persona[48];
    while (n < 64 && fscanf(f, "%d", &id) == 1)
    {
        persona[0] = 0;
        if (fgetc(f) == '=' && fscanf(f, "%47s", persona) != 1) persona[0] = 0;
        strcpy(crewPersona[n], persona);
        crewIds[n++] = id;
    }
    fclose(f);
    if (n != crewLen)
    {
        crewLen = n;
        RepaintSlots();
        char list[400] = "";
        for (int i = 0; i < n && strlen(list) < sizeof list - 16; i++)
            sprintf(list + strlen(list), " %d=%s", crewIds[i], i % 2 == 0 ? "host" : "guest");
        NetLog("# owners:%s", list);
    }
}

/* Network status for the mission Lua's HUD (coop.lua C.netStatus), twice a second:
 *   <host|guest> <waiting|connected|closed|failed> <host's crewmates with action points> <guest's ...>
 *   <event seq> <event name> <event actor id> <the other player's crewmate the camera follows when ours are done, -1>
 *   <this player's first crewmate with action points, -1> (the camera goes there when the crew's turn starts)
 * The event is the last notification (Notify); the Lua shows it when its number changes (not the one already there
 * when the battle starts): the game's popup window (tutorials_popup.xml coop_net_<name>) for a player who left, came
 * back or lost the connection, a text over the crewmate for a refused command (coop_net_<name>). */
static wchar_t statusPath[MAX_PATH];
static void *FindActor(void *sim, int id);
static int eventSeq, eventActor;
static int otherActive = -1;   /* the other player's crewmate that acted last (the camera follows it: coop.lua C.netFocus) */
static char eventName[32] = "-";

static void Notify(const char *name, int actor)
{
    eventSeq++;
    strncpy(eventName, name, sizeof eventName - 1);
    eventActor = actor;
    NetLog("# notify %d: %s %d", eventSeq, name, actor);
}

/* Host: the guest left the battle (quit to the menu, or the connection is gone): its crewmates are the host's until it
 * is back ("ready" from its battle again), and a new battle doesn't wait for it. */
static int guestAway;
static int userLeft;   /* guest: its player quit the battle (HookQuit): no Continue pressed by itself */
static int loaderSlot;   /* guest: the loader put the host's save into slot guestSlotPath (session "slot"): Continue at start */
static ULONGLONG hostLeftAt;   /* live guest: when the host's "hostleft" line came (0 = not) */

static int HasPoints(void *actor)
{
    __try { return *(int *)((char *)actor + 0x90) > 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static void WriteStatus(void *sim)
{
    static const char *states[] = { "waiting", "waiting", "connected", "closed", "failed" };
    int left[3] = { 0, 0, 0 }, st = NetTcpState(), follow = -1, own = -1;
    for (int i = 0; i < crewLen; i++)
    {
        void *a = FindActor(sim, crewIds[i]);
        if (a && HasPoints(a))
        {
            left[i % 2 == 0 ? 1 : 2]++;
            if ((i % 2 == 0 ? 1 : 2) != me && (follow < 0 || crewIds[i] == otherActive)) follow = crewIds[i];
            if ((i % 2 == 0 ? 1 : 2) == me && own < 0) own = crewIds[i];
        }
    }
    static int reconnectsSeen;
    if (me == 2 && NetTcpReconnects() != reconnectsSeen)
    {
        reconnectsSeen = NetTcpReconnects();
        NetLog("# net: connected to the host again (%d), going on where the connection broke", reconnectsSeen);
    }
    FILE *f = _wfopen(statusPath, L"w");
    if (!f) return;
    /* host, guest connected but not in the battle yet: "joining" (the host's battle is held) */
    const char *word = me == 1 && st == NET_TCP_CONNECTED && !guestIn ? "joining" : st >= 0 && st <= 4 ? states[st] : "waiting";
    fprintf(f, "%s %s %d %d %d %s %d %d %d\n", me == 1 ? "host" : "guest", word, left[1], left[2], eventSeq, eventName, eventActor, follow, own);
    fclose(f);
}

/* 1 host, 2 guest, 0 no one. */
static int OwnerOf(int id)
{
    for (int i = 0; i < crewLen; i++)
        if (crewIds[i] == id) return i % 2 == 0 ? 1 : 2;
    return 0;
}

static int Mine(int id)
{
    int o = OwnerOf(id);
    return !me || !o || o == me || (me == 1 && guestAway);
}

/* Camera to an actor of the other game: the mission Lua pans to it (coop.lua C.netFocus). "<seq> <actor id>". */
static wchar_t focusPath[MAX_PATH];
static void FocusOn(int id)
{
    static int focusSeq;
    FILE *f = _wfopen(focusPath, L"w");
    if (!f) return;
    fprintf(f, "%d %d\n", ++focusSeq, id);
    fclose(f);
}


/* ---- party slots: owner colour + name on the crew portraits (tactics HUD) ----
 * Slot update 0x5fa900(slot, int, const std::string *persona) fills a portrait slot of the pool for a crewmate: persona
 * -> entity -> CombatHudPortrait, Image::SetTexture 0x745de0(slot[+0x80], &path). slot[+0x78] = the slot's button
 * (ui.me template tactics_partyslot_template). The Coop mod adds hidden children to that template per player
 * (coop_owner_frame_N: tinted slot background, coop_owner_label_N: the name); after the game's update the owner's are
 * shown. Children by name: 0x7536b0(widget, string_view *) (the slot constructor uses it); widget flags +0x2c0, bit 4 =
 * hidden (the game sets it the same way on its images). Slots are reused for other crewmates: every update sets all. */
typedef ULONG_PTR (*SlotUpdate_t)(void *slot, ULONG_PTR flag, const void *persona);
typedef void *(*FindChild_t)(void *widget, const void *name);
static SlotUpdate_t realSlotUpdate;
static char *atFindChild;
#define RVA_SlotUpdate 0x5fa900
#define RVA_FindChild 0x7536b0
#define MAX_SLOTS 8
static struct { void *slot; char persona[48]; } slots[MAX_SLOTS];

static int OwnerOfPersona(const char *persona)
{
    for (int i = 0; i < crewLen; i++)
        if (persona[0] && strcmp(crewPersona[i], persona) == 0) return i % 2 == 0 ? 1 : 2;
    return 0;
}

static void ShowChild(void *button, const char *name, int show)
{
    struct { const char *p; size_t n; } sv = { name, strlen(name) };
    __try
    {
        char *w = (char *)((FindChild_t)atFindChild)(button, &sv);
        if (!w) return;
        unsigned *flags = (unsigned *)(w + 0x2c0);
        *flags = show ? *flags & ~4u : *flags | 4u;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

static void PaintSlot(void *slot, const char *persona)
{
    void *button = NULL;
    __try { button = *(void **)((char *)slot + 0x78); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    if (!button) return;
    int owner = me ? OwnerOfPersona(persona) : 0;
    char name[40];
    for (int i = 1; i <= 2; i++)
    {
        sprintf(name, "coop_owner_frame_%d", i);
        ShowChild(button, name, owner == i);
        sprintf(name, "coop_owner_label_%d", i);
        ShowChild(button, name, owner == i);
    }
}

static ULONG_PTR HookSlotUpdate(void *slot, ULONG_PTR flag, const void *persona)
{
    ULONG_PTR r = realSlotUpdate(slot, flag, persona);
    char p[48] = "";
    __try
    {
        size_t len = *(size_t *)((char *)persona + 0x10), cap = *(size_t *)((char *)persona + 0x18);
        const char *text = cap >= 16 ? *(const char *const *)persona : (const char *)persona;
        if (len < sizeof p) { memcpy(p, text, len); p[len] = 0; }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { p[0] = 0; }
    int k = 0;
    while (k < MAX_SLOTS && slots[k].slot && slots[k].slot != slot) k++;
    if (k < MAX_SLOTS) { slots[k].slot = slot; strcpy(slots[k].persona, p); }
    PaintSlot(slot, p);
    return r;
}

/* The owners arrive half a second into the mission (after the first slot updates): paint again when they change. */
static void RepaintSlots(void)
{
    for (int k = 0; k < MAX_SLOTS && slots[k].slot; k++) PaintSlot(slots[k].slot, slots[k].persona);
}

/* PlayerTurnSimulator: "can this actor be selected" (0x45ac50(pts, id) -> bool: player faction, action points left...),
 * asked by SelectActor 0x45a210, the Tab cycling, the start-of-turn pick and the portraits. */
typedef unsigned char (*CanSelect_t)(void *pts, int id);
static CanSelect_t realCanSelect;
#define RVA_CanSelect 0x45ac50

static void *lastPts;   /* PlayerTurnSimulator, for the selected crewmate (the move target) */
static unsigned char HookCanSelect(void *pts, int id)
{
    lastPts = pts;
    if (me && id && !Mine(id)) return 0;
    if (HELD()) return 0;
    return realCanSelect(pts, id);
}

/* Camera pans of the mission Lua (coop.lua C.netFocus): CameraPanToEntity builds a CameraPan sequence
 * 0x120990(seq, position *, float hold, bool cinematic, uint16 mode) with cinematic = 1 = flags 0x13 (letterbox bars
 * for a second, skippable); the game's own pans (new enemies) pass 0 = flags 1. Ours are told apart by their hold
 * time COOP_PAN_HOLD and made like the game's (2026-11-28: the user saw the bars flash at every pan). */
typedef void *(*CameraPan_t)(void *seq, void *pos, float hold, unsigned char cinematic, unsigned short mode);
static CameraPan_t realCameraPan;
#define RVA_CameraPan 0x120990
#define COOP_PAN_HOLD 0.3071f

static void *HookCameraPan(void *pan, void *pos, float hold, unsigned char cinematic, unsigned short panMode)
{
    if (fabsf(hold - COOP_PAN_HOLD) < 0.0001f) cinematic = 0;
    return realCameraPan(pan, pos, hold, cinematic, panMode);
}

/* The skip button of the tactics HUD (0x1cfc20(ui) picks its mode [ui + 0x28]: 0 none, 1 cutscene, 2..4 skip actions
 * "ui_tactics_skip_actions" — moves and shots jump to their end). Skipped actions end at once in one game only: in a
 * network game modes 2..4 become 0 (no button); a cutscene is each player's own and stays skippable. */
typedef void (*SkipMode_t)(void *ui);
static SkipMode_t realSkipMode;
#define RVA_SkipMode 0x1cfc20

static void HookSkipMode(void *ui)
{
    realSkipMode(ui);
    __try { int *m = (int *)((char *)ui + 0x28); if (me && *m >= 2) *m = 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { }
}

/* ---- replay script ---- */
typedef struct
{
    int kind;            /* 'c' command, 'e' turn end */
    int origin;          /* ORIGIN_* of the recorded line */
    int actor, index, type, flag, endsBefore, luaBefore, depth;
    unsigned z, w;
    int ent;             /* entity id counter, -1 = not in the line */
    unsigned frame;      /* recorded tactics frame */
    long long step;      /* w=: the host's world step (see worldSteps), -1 = not in the line (older records) */
    int ao, to;          /* actor / type-2 target as actor ordinal (-1 = none or not an actor) */
    int statesBefore;    /* "state" lines in the record before it: the state to correct to before it is issued */
    int byGuest;         /* live guest: the host played it for this guest (" by=guest"): its own command back */
    unsigned char data[MAX_VEC_BYTES + 8];
    int dataLen;
    char name[64];
} Entry;
static Entry *script;
static int scriptLen, scriptPos;
static int endsSeen, luaSeen, idle, gateWait;
static int luaRecordedLen, endsRecordedLen;
static ULONGLONG worldTick;
/* World steps (2026-11-28): fixed steps of 1/60 s the tactics world ran since its scene was made (HookWorldUpdate). Logged
 * as w= on commands and turn ends, and the host's as "k <steps>" lines ~10 times a second: the guest's world runs exactly
 * as many steps between two commands as the host's did (lockstep, see HookWorldUpdate). */
static unsigned long long worldSteps;
static long long hostSteps = -1;   /* guest: the host's latest world step known (k lines, w= fields); -1 = none yet */
static long long hostStepsFirst = -1;   /* guest: the first one read since the replay (re)started: its save's step */
static int stepsAligned;           /* guest: worldSteps counts like the host's (0 after joining from a save) */

/* State correction (replay = network guest): before a recorded player command (or turn end) is issued, the world is
 * set to the state the record had just before it (positions, health, who is alive), so a divergence is repaired
 * instead of growing (what a host would send after every action). Lua does the setting (SetEntityPosition,
 * SetActorHealth, KillObject: coop.lua C.netFix); the dll asks with swh2_net_sync.txt
 *   "<state line number> <local ids by actor ordinal...>"
 * and waits until Lua writes that number to swh2_net_synced.txt (or SYNC_TIMEOUT frames). */
static wchar_t syncPath[MAX_PATH], syncedPath[MAX_PATH];
static int statesSynced, syncWanted, syncWait;
static int gatePassed;   /* scriptPos + 1 whose gate is passed (or timed out): not waited for again */
static int clearedFor;   /* scriptPos + 1 of the turn end whose faction was finished (FinishFaction) */

/* Dialogue / camera sequence on (the mission Lua writes swh2_net_cutscene.txt, coop.lua C.netCutscene): no replayed
 * command goes in then (run 7: commands during the intro dialogue left the game on an empty dialogue box), nor for
 * CUTSCENE_QUIET frames after. Read every 10 frames. */
static wchar_t cutscenePath[MAX_PATH];
static int cutsceneOn, quietFrames;
#define CUTSCENE_QUIET 90
/* Live guest: the waits of a file replay (made for a test run that matches a record to the frame) are far too long for
 * someone playing: each command of the other player came 2-5 s late (45 idle frames, a state correction, 45 again; 150
 * before an automatic one; 90 after every camera move — at 30 fps twice that). The order is all that matters live. */
#define LIVE_IDLE 2
#define LIVE_END_WAIT 40
#define LIVE_CUTSCENE_QUIET 20
static int live;

/* A live guest held by a camera sequence (not a dialogue: commands in one leave an empty dialogue box) for longer than
 * this goes on: a camera that never ended (our own follow, SetCameraTargetEntity, read as one) held the replay for good
 * while the host played on (2026-11-28, laptop test 4). */
#define LIVE_CAMERA_MAX 480
static int CutsceneHold(void)
{
    static int cameraOn, cameraFrames, cameraToldAt = -1;
    if (frame % 10 == 0)
    {
        FILE *f = _wfopen(cutscenePath, L"r");
        char s[16] = "";
        if (f) { if (!fgets(s, sizeof s, f)) s[0] = 0; fclose(f); }
        cutsceneOn = s[0] == '1';
        cameraOn = cutsceneOn && strncmp(s + 1, " camera", 7) == 0;
    }
    if (cameraOn && live && guest && scriptPos < scriptLen && ++cameraFrames > LIVE_CAMERA_MAX)
    {
        if (cameraToldAt != scriptPos) { cameraToldAt = scriptPos; NetLog("# DIFF: a camera sequence held replay %d for %d frames: going on", scriptPos + 1, LIVE_CAMERA_MAX); }
        return 0;
    }
    if (!cameraOn) cameraFrames = 0;
    if (cutsceneOn) { quietFrames = 0; return 1; }
    return ++quietFrames < (live ? LIVE_CUTSCENE_QUIET : CUTSCENE_QUIET);
}
#define SYNC_TIMEOUT 600


typedef ULONG_PTR (*AiStep_t)(void *ai, void *a2, void *a3, void *a4);
static AiStep_t realAiStep;
#define RVA_AiStep 0x4487b0

/* Enemy turn simulator update (enemyturnsimulator.cpp, every world frame from 0x480a10): starts the AI calculation for
 * the next actor (RequestOrders 0x449a30), executes finished orders (0x43be40 -> RequestAction) and ends the turn
 * (FactionDone) when every actor is done. The guest does not run it at all. */
typedef void (*EnemyTurn_t)(void *ets, void *dt);
static EnemyTurn_t realEnemyTurn;
#define RVA_EnemyTurn 0x43ac30

/* Tutorial popups (tutorialpopupsimulator.cpp): Show(sim, string_view *name, bool flag, float) 0x1520a0 — the
 * mission-start tutorials and Lua TutorialPopup. The input lock lets everyone close them (NetPopupTick). */
typedef ULONG_PTR (*PopupShow_t)(void *sim, void *name, ULONG_PTR flag, float f);
static PopupShow_t realPopupShow;
static ULONGLONG popupTick;
#define RVA_PopupShow 0x1520a0

/* TacticsScene constructor (0x488bb0, called with the new 0x2198-byte scene when a mission is entered or loaded): it
 * shuffles the reinforcement decks (missionspawntabledecks.cpp) with the shared RNG, which is seeded from the clock at
 * game start — so the same save spawned different reinforcements (run 5). The record logs "scene r=<z>:<w>"; the
 * replay sets that RNG state before the scene is built (a network host would send it with the mission). */
typedef void *(*SceneCreate_t)(void *scene, void *a2, void *a3, void *a4);
static SceneCreate_t realSceneCreate;
static unsigned sceneZ[16], sceneW[16];
static int scenesLen, scenePos;
#define RVA_SceneCreate 0x488bb0
/* Guest: the host's "won" line came (script length then, -1 = not), when, and whether the mission Lua was told
 * (swh2_net_won.txt "1": it ends the mission in victory, coop.lua C.netWon). Reset with every scene. */
static int hostWon = -1, wonTold;
static ULONGLONG hostWonAt;
static wchar_t wonPath[MAX_PATH];

/* Actor ordinals: entity ids drift between runs (special effects take numbers from the same counter), so actors are
 * named by the order they first appear in the actor list — the same on every PC that plays the same battle.
 * Logged as "actor <id> o=<ordinal>" when assigned; commands carry ao= (actor) and to= (actor target). */
#define MAX_ACTORS 4096
static int ordIds[MAX_ACTORS], ordLen;
static int ordSeed[MAX_ACTORS], ordSeedLen = -1;   /* guest: the host's actor ordinals ("ords" line), for joinedMid */
static void *ordSim;

void NetLog(const char *fmt, ...)
{
    if (!outPath[0]) return;
    FILE *f = _wfopen(outPath, L"a");
    if (!f) return;
    va_list a;
    va_start(a, fmt);
    vfprintf(f, fmt, a);
    va_end(a);
    fputc('\n', f);
    fclose(f);
}

const char *NetOriginName(int o)
{
    static const char *names[] = { "other", "player", "ai", "lua", "projectile", "preview", "replay" };
    return o >= 0 && o <= ORIGIN_REPLAY ? names[o] : "?";
}

/* Origin of a call from the game's return addresses (Steam-build RVAs, innermost first). Frames in the ActorSimulator itself
 * (move requests 0x38c2c0 call RequestAction) are skipped. */
int NetClassify(const DWORD *rvas, int n) { return NetClassifyReal(rvas, n, 0); }

/* real: the call is on the main tactics world, so it is no preview even with outcomesimulator.cpp on the stack (run 6:
 * enemy moves after a shot came that way, were counted as previews and never logged). */
int NetClassifyReal(const DWORD *rvas, int n, int real)
{
    for (int i = 0; i < n; i++)
    {
        DWORD r = rvas[i];
        if (r >= 0x388000 && r < 0x39e000) continue;              /* actorsimulator.cpp */
        if (r >= 0x4548e0 && r < 0x455eb0) { if (real) continue; return ORIGIN_PREVIEW; }  /* outcomesimulator.cpp */
        if (r >= 0x455eb0 && r < 0x45e000) return ORIGIN_PLAYER;   /* playerturnsimulator.cpp */
        if (r >= 0x43a000 && r < 0x43e000) return ORIGIN_AI;       /* enemyturnsimulator.cpp */
        if (r >= 0x460000 && r < 0x465000) return ORIGIN_PROJECTILE;
        if ((r >= 0x316000 && r < 0x32b000) || (r >= 0x26c000 && r < 0x272000)) return ORIGIN_LUA;
    }
    return ORIGIN_OTHER;
}

static int OriginOf(int real)
{
    void *frames[16];
    DWORD rvas[16];
    int n = RtlCaptureStackBackTrace(1, 16, frames, NULL), m = 0;
    for (int i = 0; i < n; i++)
    {
        ULONG_PTR a = (ULONG_PTR)frames[i];
        if (a > (ULONG_PTR)base && a < (ULONG_PTR)base + 0x1000000) rvas[m++] = SteamRva((DWORD)(a - (ULONG_PTR)base));
    }
    return NetClassifyReal(rvas, m, real);
}

static int Origin(void) { return OriginOf(0); }

static void Hex(char *out, const unsigned char *p, int n)
{
    static const char d[] = "0123456789abcdef";
    for (int i = 0; i < n; i++) { out[2 * i] = d[p[i] >> 4]; out[2 * i + 1] = d[p[i] & 15]; }
    out[2 * n] = 0;
}

/* Target variant -> type + flat bytes (vectors: their elements; path: the trailing 8 bytes, then the nodes). */
int NetTargetBytes(const unsigned char *t, unsigned char *out, int max, int *type)
{
    *type = (signed char)t[0x20];
    switch (*type)
    {
    case 0: return 0;
    case 1: memcpy(out, t, 8); return 8;
    case 2: case 3: memcpy(out, t, 4); return 4;
    case 4: case 5:
    {
        const unsigned char *first = *(unsigned char *const *)t, *last = *(unsigned char *const *)(t + 8);
        int extra = *type == 4 ? 8 : 0, n = (int)(last - first);
        if (n < 0 || n + extra > max || n % (*type == 4 ? 16 : 8)) return -1;
        if (extra) memcpy(out, t + 0x18, 8);
        memcpy(out + extra, first, n);
        /* Path nodes: the last 4 bytes are padding holding leftovers of a pointer (differ between game starts). */
        if (*type == 4) for (int k = 12; k < n; k += 16) memset(out + extra + k, 0, 4);
        return n + extra;
    }
    default: return -1;
    }
}

/* Inverse of NetTargetBytes; vectors point into a malloc'ed buffer the caller frees (NetFreeTarget): the game copies
 * the target (variant copy constructor), it never keeps ours. */
int NetBuildTarget(unsigned char *t, int type, const unsigned char *data, int len)
{
    memset(t, 0, TARGET_SIZE);
    t[0x20] = (unsigned char)type;
    switch (type)
    {
    case 0: return len == 0;
    case 1: if (len != 8) return 0; memcpy(t, data, 8); return 1;
    case 2: case 3: if (len != 4) return 0; memcpy(t, data, 4); return 1;
    case 4: case 5:
    {
        int extra = type == 4 ? 8 : 0, n = len - extra;
        if (n < 0 || n % (type == 4 ? 16 : 8)) return 0;
        unsigned char *buf = n ? (unsigned char *)malloc(n) : NULL;
        if (n && !buf) return 0;
        if (n) memcpy(buf, data + extra, n);
        *(unsigned char **)t = buf;
        *(unsigned char **)(t + 8) = buf + n;
        *(unsigned char **)(t + 16) = buf + n;
        if (extra) memcpy(t + 0x18, data, 8);
        return 1;
    }
    default: return 0;
    }
}

void NetFreeTarget(unsigned char *t)
{
    if (t[0x20] == 4 || t[0x20] == 5) free(*(void **)t);
    t[0x20] = 0xff;
}

static unsigned *Rng(void) { return *(unsigned **)atRngPtr; }

/* World RNG (2026-11-28): the game has one RNG, and between world steps the UI, particles and drawing turn it a
 * different number of times on every PC — a shot started with the same RNG had it different already at the first world
 * step after it, and now and then ended differently (the guest's shot hit, the host's missed). The fixed world steps
 * (HookWorldUpdate) run on their own RNG state: swapped in before each step, out after it. Wherever the log has an
 * RNG (cmd/end/spawn/scene, host and guest alike: the guest logs the synced value) the world RNG is set to it
 * (RngMark), so both games start each stretch of steps from the same state and the steps keep it the same. */
static unsigned worldRng[2];
static int worldRngSet;
/* Animation RNG (2026-11-28, CosmeticRng): the random start point of an animation (0x56529d) and the seed of a new
 * animation component (0x563700) — in the cosmetic RNG they differed on every PC, and with them the poses (lockstep,
 * HookWorldUpdate). Set from the RNG wherever the world RNG is (RngMark), so both games draw the same from there. */
static unsigned *animRng;
static void RngMark(void)
{
    unsigned *r = Rng();
    if (r) { worldRng[0] = r[0]; worldRng[1] = r[1]; worldRngSet = 1; }
    if (r && animRng)
    {
        animRng[0] = (r[0] ^ 0x2545f491u) | 1;
        animRng[1] = (r[1] ^ 0x9e3779b9u) | 1;
    }
}

/* The entity id counter of the world of sim (NULL if unreadable). */
static int *EntityCounter(void *sim)
{
    if (!sim) return NULL;
    __try
    {
        int *c = (int *)(*(char **)((char *)sim + 0x10) + 0x1c48 + 0x58);
        volatile int probe = *c;
        (void)probe;
        return c;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}

static int EntityCount(void *sim)
{
    int *c = EntityCounter(sim);
    return c ? *c : -1;
}

/* RNG + entity counter to the recorded values (what the host would send). */
static void Sync(const Entry *e, void *sim)
{
    if (autoplay) return;
    unsigned *r = Rng();
    if (r && (e->z || e->w)) { r[0] = e->z; r[1] = e->w; }
    /* Only forwards: a lower counter would hand out ids of entities that still exist (crashed the game in run 2). */
    /* Actors are matched by ordinal, so a counter that ran ahead (more effects here) is no longer an error. */
    int *c = EntityCounter(sim);
    if (c && e->ent > *c) *c = e->ent;
}

static int ActorId(void *actor)
{
    __try { return **(int **)actor; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

static void ActionName(void *actor, int index, char *out, int max)
{
    out[0] = 0;
    __try
    {
        unsigned char *first = *(unsigned char **)((char *)actor + 0x50), *last = *(unsigned char **)((char *)actor + 0x58);
        if (index < 0 || index >= (last - first) / 0x38) return;
        unsigned char *s = first + index * 0x38;
        size_t len = *(size_t *)(s + 0x10), cap = *(size_t *)(s + 0x18);
        const char *p = cap >= 16 ? *(const char **)s : (const char *)s;
        if (len >= (size_t)max) len = max - 1;
        for (size_t i = 0; i < len; i++) out[i] = p[i] > ' ' && p[i] < 127 ? p[i] : '_';
        out[len] = 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { strcpy(out, "?"); }
}

/* Actor with this id in the world of sim: same walk as IsActionsOngoing (std::list, actor embedded at node+0x18). */
static void *FindActor(void *sim, int id)
{
    __try
    {
        void *out[4] = {0};
        char *world = *(char **)((char *)sim + 0x10);
        ((ActorStorage_t)atActorStorage)(world + 0x1c48, out);
        char **head = *(char ***)((char *)out[0] + 0xd0);
        int guard = 0;
        for (char **node = (char **)*head; node != head && guard < 4096; node = (char **)*node, guard++)
        {
            void *actor = (char *)node + 0x18;
            if (ActorId(actor) == id) return actor;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    return NULL;
}

static int OrdinalOf(int id)
{
    for (int i = 0; i < ordLen; i++)
        if (ordIds[i] == id) return i;
    return -1;
}

static int IdOfOrdinal(int ordinal, int fallback) { return ordinal >= 0 && ordinal < ordLen ? ordIds[ordinal] : fallback; }

/* New actors get the next ordinals, in list order. A new world (another mission) starts over. */
static void ScanActors(void *sim)
{
    if (sim != ordSim)
    {
        ordSim = sim;
        ordLen = 0;
        /* Joined inside the host's mission: the host's numbering (it counts actors gone before the save too). */
        if (joinedMid && !ordSeeded && live)
        {
            /* The host writes its table when the guest (re)joins: it may still be on its way. */
            ULONGLONG until = GetTickCount64() + 2000;
            do { ReadMore(); if (ordSeedLen < 0) Sleep(20); } while (ordSeedLen < 0 && GetTickCount64() < until);
            if (ordSeedLen > 0)
            {
                ordSeeded = 1;
                memcpy(ordIds, ordSeed, ordSeedLen * sizeof ordIds[0]);
                ordLen = ordSeedLen;
                NetLog("# joined inside the host's mission: %d actor ordinals taken from the host", ordLen);
            }
        }
    }
    __try
    {
        void *out[4] = {0};
        char *world = *(char **)((char *)sim + 0x10);
        ((ActorStorage_t)atActorStorage)(world + 0x1c48, out);
        char **head = *(char ***)((char *)out[0] + 0xd0);
        /* Actors new since the last scan, numbered in id order = creation order (the list order of actors spawned in
         * the same frame differs between runs: run 4 had 177,176,175 against 179,180). */
        int fresh[64], n = 0, guard = 0;
        for (char **node = (char **)*head; node != head && guard < MAX_ACTORS; node = (char **)*node, guard++)
        {
            int id = ActorId((char *)node + 0x18);
            if (id < 0 || OrdinalOf(id) >= 0 || n >= 64) continue;
            int k = n++;
            while (k > 0 && fresh[k - 1] > id) { fresh[k] = fresh[k - 1]; k--; }
            fresh[k] = id;
        }
        for (int i = 0; i < n && ordLen < MAX_ACTORS; i++)
        {
            ordIds[ordLen] = fresh[i];
            NetLog("actor %d o=%d f=%u", fresh[i], ordLen, frame);
            ordLen++;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
}

/* Recorded target -> local bytes: an actor target (type 2 with to=) gets the local id of that ordinal. */
static void LocalTargetData(const Entry *e, unsigned char *data)
{
    memcpy(data, e->data, e->dataLen);
    if (e->type == 2 && e->dataLen == 4 && e->to >= 0)
    {
        int id = IdOfOrdinal(e->to, *(int *)e->data);
        memcpy(data, &id, 4);
    }
}

/* Guest: one command to the host (see "Guest commands"); 0 = not connected. */
static int SendCommand(int ao, int index, int type, const unsigned char *data, int len, int to, int flag, const char *name)
{
    static char hex[2 * (MAX_VEC_BYTES + 8) + 1], line[2 * (MAX_VEC_BYTES + 8) + 128];
    Hex(hex, data, len);
    sprintf(line, "gcmd ao=%d i=%d t=%d:%s to=%d fl=%d f=%u", ao, index, type, hex, to, flag, frame);
    if (!NetTcpSend(line)) { NetLog("# not connected: command %s of actor ordinal %d not sent to the host", name, ao); return 0; }
    if (!pending) { pendingFrame = frame; pendingAt = frame; }
    pending++;
    NetLog("# to host: %s %s", name, line);
    return 1;
}

/* Guest: asks the host to end the turn (once per turn). */
static void SendEnd(const char *why)
{
    if (endAskedFor == endsArrived) return;
    char line[64];
    sprintf(line, "gend n=%d", endsArrived);
    if (!NetTcpSend(line)) { NetLog("# not connected: turn end not sent to the host"); return; }
    endAskedFor = endsArrived;
    NetLog("# to host: %s (%s)", line, why);
}

static void *FactionDoneObject(void *sim)
{
    __try
    {
        int slot = *(int *)atFactionDoneTypeId;
        char *sims = *(char **)((char *)sim + 8);
        if (slot < 0 || slot > 256 || !sims) return NULL;
        return *(void **)(sims + 8 + slot * 8);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}

static int Busy(void *sim)
{
    __try { return ((IsActionsOngoing_t)atIsActionsOngoing)(sim) != 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 1; }
}

/* ---- the other player's aim: its crewmate raises the weapon and shows the sight line ----
 * Aiming is a mode of TacticsTargetUI: every UI frame it runs 0x608da0(ui, float *dt, AimState *s = ui + 0x20)
 * (ui + 0xfc actor id, + 0x100 action index; s + 0x6d angle valid, s + 0xc8 sight line shown). The aim angle lives in
 * the actor: + 0xe0 (shown, with the sway) and + 0xe4 (where the stick / mouse points); the sight line is a trace
 * projectile held by the actor (shared_ptr at + 0xf8): 0x376e30 makes it if there is none, 0x377290 points it along
 * + 0xe0 — what the UI does every frame for the selected crewmate, and charging enemies (modifier
 * ShowActorProjectileTrace, 0x3ba750) do for one that isn't selected.
 * The player's own aim is sent ~15 times a second (host: "aim ..." lines in its log, the guest reads them with it;
 * guest: "gaim ..." to the host), "off" when the aim mode is left:
 *   aim ao=<actor ordinal> i=<action index> a=<angle +0xe0> b=<angle +0xe4> s=<sight line shown> v=<angle valid>
 *   aim ao=<actor ordinal> off
 * The other game sets the angles of that crewmate, the "aim" animation input and the sight line — only while its world
 * is idle and (guest) has played everything the host sent before, and takes it all down when a command of that actor
 * starts. Not synchronized: the actor's facing (aiming behind turns it around: the angle is relative to the facing). */
typedef void (*AimUpdate_t)(void *ui, float *dt, void *state);
typedef void *(*Store_t)(void *components);
typedef void *(*Component_t)(void *storage, int id);
typedef void *(*WeaponDef_t)(void *unused, void *name);
typedef ULONG_PTR (*MapFind_t)(void *map, void *view);
typedef void *(*TraceMake_t)(void *body, void *actor, void *weapon, void *action, float a);
typedef void (*TraceAim_t)(void *body, void *actor, void *trace, float a, unsigned char valid);
typedef void (*PtrReset_t)(void *sharedPtr);
typedef void (*AnimInput_t)(void *unused, void *anim, void *view, unsigned char on);
#define RVA_AimUpdate 0x608da0
#define RVA_ActorStore 0x124a70    /* (world + 0x1c48) -> actor storage */
#define RVA_BodyStore 0xd2580      /* (world + 0x1c48) -> storage of the component the sight line code gets first */
#define RVA_AnimStore 0x27ee00     /* (world + 0x1c48) -> animation storage */
#define RVA_Component 0xcdc90      /* (storage, id) -> component or NULL */
#define RVA_WeaponDef 0x11ba20     /* (-, std::string *weapon name: actor + 0x288, or + 0x268 when that is empty) */
#define RVA_ActionMap 0xeb07c0     /* action definitions by name: find 0xbad10(map, string_view *) -> index */
#define RVA_ActionMapEnd 0xeb07d8
#define RVA_ActionMapIdx 0xeb07e8  /* entries of 40 bytes, value index at + 0x20 */
#define RVA_ActionDefs 0xeb07a8    /* value index * 2 -> definition */
#define RVA_MapFind 0xbad10
#define RVA_TraceMake 0x376e30
#define RVA_TraceSystem 0x27ec60   /* (world) -> the system the trace code wants first: what the modifier passes */
#define RVA_TraceAim 0x377290
#define RVA_PtrReset 0x18d8f0
#define RVA_AnimInput 0x565890
#define AIM_SEND_MS 40   /* 25 a second (~1.5 KB/s); the other game glides between them (AIM_GLIDE) */
#define AIM_GLIDE 0.3f   /* share of the way to the last received angle the shown one goes per frame */
#define AIM_OFF_MS 200

static AimUpdate_t realAimUpdate;
static char *atActorStore, *atBodyStore, *atAnimStore, *atComponent, *atWeaponDef, *atMapFind, *atTraceMake, *atTraceAim,
            *atPtrReset, *atAnimInput, *atActionMap, *atActionMapEnd, *atActionMapIdx, *atActionDefs, *atTraceSystem;
typedef struct { const char *p; size_t n; } View;

static void AimResolve(void)
{
    static const struct { DWORD rva; char **at; } list[] = {
        { RVA_ActorStore, &atActorStore }, { RVA_BodyStore, &atBodyStore }, { RVA_AnimStore, &atAnimStore },
        { RVA_Component, &atComponent }, { RVA_WeaponDef, &atWeaponDef }, { RVA_MapFind, &atMapFind },
        { RVA_TraceMake, &atTraceMake }, { RVA_TraceAim, &atTraceAim }, { RVA_PtrReset, &atPtrReset },
        { RVA_AnimInput, &atAnimInput }, { RVA_ActionMap, &atActionMap }, { RVA_ActionMapEnd, &atActionMapEnd },
        { RVA_ActionMapIdx, &atActionMapIdx }, { RVA_ActionDefs, &atActionDefs }, { RVA_TraceSystem, &atTraceSystem } };
    for (int i = 0; i < (int)(sizeof list / sizeof list[0]); i++)
    {
        DWORD rva = ExeRva(list[i].rva);
        *list[i].at = rva ? base + rva : NULL;
        if (!rva) atTraceMake = NULL;   /* one missing = no remote aim */
    }
}

static void *ComponentOf(void *sim, char *store, int id)
{
    char *world = *(char **)((char *)sim + 0x10);
    void *storage = ((Store_t)store)(world + 0x1c48);
    return storage ? ((Component_t)atComponent)(storage, id) : NULL;
}

/* Definition of the actor's action (by its name, as PlayerTurnSimulator and the aim UI look it up). */
static void *ActionDef(void *actor, int index)
{
    unsigned char *first = *(unsigned char **)((char *)actor + 0x50), *last = *(unsigned char **)((char *)actor + 0x58);
    if (index < 0 || index >= (last - first) / 0x38) return NULL;
    unsigned char *s = first + index * 0x38;
    __declspec(align(16)) View v;
    v.n = *(size_t *)(s + 0x10);
    v.p = *(size_t *)(s + 0x18) >= 16 ? *(const char **)s : (const char *)s;
    ULONG_PTR it = ((MapFind_t)atMapFind)(atActionMap, &v);
    if (it == *(ULONG_PTR *)atActionMapEnd) return NULL;
    int k = *(int *)(*(char **)atActionMapIdx + it * 40 + 0x20);
    return (*(void ***)atActionDefs)[k * 2];
}

static void AnimAim(void *sim, int id, int on)
{
    void *anim = ComponentOf(sim, atAnimStore, id);
    __declspec(align(16)) View v = { "aim", 3 };
    if (anim) ((AnimInput_t)atAnimInput)(NULL, anim, &v, (unsigned char)on);
}

typedef struct { int id, index, show, valid, active, drawn, traced, redo; float a, b, drawnA, da, db; } RemoteAim;   /* da/db: shown */
static RemoteAim aims[8];
/* A part of the remote aim that faulted is off for the rest of the game, the others go on (2026-11-28, laptop: the
 * first try faulted somewhere and took the whole aim down, the host's own aim was then not sent either). */
static int aimAnimOff, aimTraceOff;

/* "ao=<n> i=... a=... b=... s=... v=..." / "ao=<n> off" from the other player. */
static void AimFromLine(const char *p)
{
    if (!atTraceMake) return;
    int ao = -1, index = -1, show = 0, valid = 1;
    float a = 0, b = 0;
    if (sscanf(p, "ao=%d", &ao) != 1) return;
    int id = IdOfOrdinal(ao, -1), off = strstr(p, " off") != NULL;
    if (id < 0 || OwnerOf(id) == me) return;   /* ours: the other game shouldn't be aiming it */
    if (!off && sscanf(p, "ao=%d i=%d a=%f b=%f s=%d v=%d", &ao, &index, &a, &b, &show, &valid) != 6) return;
    RemoteAim *r = NULL;
    for (int k = 0; k < 8 && !r; k++) if (aims[k].id == id && (aims[k].active || aims[k].drawn)) r = &aims[k];
    for (int k = 0; k < 8 && !r && !off; k++) if (!aims[k].active && !aims[k].drawn) { r = &aims[k]; memset(r, 0, sizeof *r); r->id = id; }
    if (!r) return;
    if (off) { r->active = 0; return; }
    if (r->drawn && index != r->index) r->redo = 1;   /* another action (weapon): a new sight line */
    r->index = index; r->a = a; r->b = b; r->show = show; r->valid = valid; r->active = 1;
}

static void AimUndraw(void *sim, RemoteAim *r)
{
    if (!r->drawn) return;
    void *actor = FindActor(sim, r->id);
    __try { if (actor && r->traced) ((PtrReset_t)atPtrReset)((char *)actor + 0xf8); }
    __except (EXCEPTION_EXECUTE_HANDLER) { aimTraceOff = 1; NetLog("# ERROR: the other player's sight line (%d) could not be taken down: sight lines off", r->id); }
    __try { if (actor && !aimAnimOff) AnimAim(sim, r->id, 0); }
    __except (EXCEPTION_EXECUTE_HANDLER) { aimAnimOff = 1; NetLog("# ERROR: the other player's aim pose (%d) could not be taken down: poses off", r->id); }
    r->drawn = r->traced = 0;
}

/* Where a fault inside the game's code was (Steam RVA) and its address, for the log. */
static DWORD faultRva, faultCode;
static ULONG_PTR faultAddr;
static int FaultWhere(EXCEPTION_POINTERS *e)
{
    faultCode = e->ExceptionRecord->ExceptionCode;
    ULONG_PTR at = (ULONG_PTR)e->ExceptionRecord->ExceptionAddress;
    faultRva = at >= (ULONG_PTR)base && at < (ULONG_PTR)base + 0x2000000 ? SteamRva((DWORD)(at - (ULONG_PTR)base)) : 0;
    faultAddr = e->ExceptionRecord->NumberParameters >= 2 ? e->ExceptionRecord->ExceptionInformation[1] : 0;
    return EXCEPTION_EXECUTE_HANDLER;
}

/* The trace code's first argument: the aim UI passes 0x27ec60([ui + 0x18]) — a system out of a container the UI
 * holds next to its world ([ui + 0x10]); that system's [+ 0x10] is the world again (0x3795d0 reads the entity manager
 * from there). Not known from the actor simulator: candidates (the UI object when this game aimed itself, pointer
 * fields of the simulator and the world) are tried and the first whose system leads back to the world is kept. */
static void *traceSystem, *aimUi;
static const char *traceFrom = "";
static void *TraceSystem(void *sim)
{
    if (traceSystem || !atTraceSystem) return traceSystem;
    char *world = *(char **)((char *)sim + 0x10);
    static char what[64];
    void *found = NULL;
    for (int k = 0; k < 2 + 64 + 64 && !found; k++)
    {
        void *cand = NULL;
        __try
        {
            if (k == 0) { cand = aimUi ? *(void **)((char *)aimUi + 0x18) : NULL; sprintf(what, "aim UI + 0x18"); }
            else if (k == 1) continue;
            else if (k < 66) { cand = *(void **)((char *)sim + (k - 2) * 8); sprintf(what, "simulator + %#x", (k - 2) * 8); }
            else { cand = *(void **)(world + (k - 66) * 8); sprintf(what, "world + %#x", (k - 66) * 8); }
            if (!cand || (ULONG_PTR)cand < 0x10000 || ((ULONG_PTR)cand & 7)) continue;
            char *sys = (char *)((Store_t)atTraceSystem)(cand);
            if (sys && (ULONG_PTR)sys >= 0x10000 && !((ULONG_PTR)sys & 7) && *(char **)(sys + 0x10) == world) found = sys;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { }
    }
    traceSystem = found;
    traceFrom = found ? what : "";
    NetLog(found ? "# the other player's sight line: trace system found (%s)" : "# the other player's sight line: no trace system found%s", traceFrom);
    if (!found) aimTraceOff = 1;
    return found;
}

/* Every frame of the main world. */
static void AimDraw(void *sim)
{
    if (!atTraceMake) return;
    int quiet = !Busy(sim) && !(live && scriptPos < scriptLen);
    for (int k = 0; k < 8; k++)
    {
        RemoteAim *r = &aims[k];
        if (!r->active && !r->drawn) continue;
        void *actor = FindActor(sim, r->id);
        if (!actor) { memset(r, 0, sizeof *r); continue; }
        if (r->redo) { r->redo = 0; AimUndraw(sim, r); }
        if (!r->active || !quiet || (r->traced && !r->show)) { AimUndraw(sim, r); continue; }
        /* The angles come 25 times a second: shown as they are, the aim moved in steps (2026-11-28, laptop test 5). */
        if (!r->drawn) { r->da = r->a; r->db = r->b; }
        else
        {
            r->da += (r->a - r->da) * AIM_GLIDE;
            r->db += (r->b - r->db) * AIM_GLIDE;
            if (fabsf(r->a - r->da) < 0.0005f) r->da = r->a;
            if (fabsf(r->b - r->db) < 0.0005f) r->db = r->b;
        }
        if (r->drawn && r->da == r->drawnA) continue;
        char *ac = (char *)actor;
        int stage = 0;
        __try
        {
            *(float *)(ac + 0xe0) = r->da;
            *(float *)(ac + 0xe4) = r->db;
            stage = 1;
            if (!r->drawn && !aimAnimOff) AnimAim(sim, r->id, 1);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            if (stage == 0) { NetLog("# ERROR: the other player's aim angles (%d) could not be set: remote aim off", r->id); atTraceMake = NULL; return; }
            aimAnimOff = 1;
            NetLog("# ERROR: the other player's aim pose (%d) could not be shown: poses off", r->id);
        }
        r->drawn = 1;
        r->drawnA = r->da;
        if (!r->show || aimTraceOff) continue;
        stage = 0;
        __try
        {
            /* First argument as the modifier ShowActorProjectileTrace gives it for a crewmate that isn't selected
             * (0x3ba8e4: 0x27ec60(world)); the aim UI's own (a component of the selected one, 0x609784) faulted in the
             * trace code (2026-11-28, nettest --menu-join). */
            char *world = *(char **)((char *)sim + 0x10);
            (void)world;
            void *body = TraceSystem(sim), *action = ActionDef(actor, r->index);
            if (!body) continue;
            stage = 1;
            void *weapon = ((WeaponDef_t)atWeaponDef)(NULL, ac + (*(size_t *)(ac + 0x298) ? 0x288 : 0x268));
            stage = 2;
            if (body && action && weapon)
            {
                void *trace = ((TraceMake_t)atTraceMake)(body, actor, weapon, action, 0.0f);
                stage = 3;
                if (trace) ((TraceAim_t)atTraceAim)(body, actor, trace, 0.0f, (unsigned char)(r->valid != 0));
                r->traced = trace != NULL;
            }
            else if (!r->traced) NetLog("# the other player's sight line (%d): body %p action %p weapon %p", r->id, body, action, weapon);
        }
        __except (FaultWhere(GetExceptionInformation()))
        {
            static const char *what[] = { "body/action lookup", "weapon lookup", "making the trace", "pointing the trace" };
            aimTraceOff = 1;
            NetLog("# ERROR: the other player's sight line (%d) faulted in %s (code %08lx at %06lx, address %llx): sight lines off, the raised weapon stays",
                   r->id, what[stage], faultCode, faultRva, (unsigned long long)faultAddr);
        }
    }
}

/* A command of this actor starts: its aim is over. */
static void AimDropFor(void *sim, int id)
{
    for (int k = 0; k < 8; k++)
        if (aims[k].id == id && (aims[k].active || aims[k].drawn)) { aims[k].active = 0; AimUndraw(sim, &aims[k]); }
}

/* The own aim, sent to the other player. */
static int aimSentOrd = -1;
static ULONGLONG aimSeenAt, aimSentAt;

static void AimSend(const char *text)
{
    if (me == 1) { if (hosting) NetLog("aim %s", text); }
    else if (me == 2) { char line[128]; sprintf(line, "gaim %s", text); NetTcpSend(line); }
}

static void HookAimUpdate(void *ui, float *dt, void *state)
{
    realAimUpdate(ui, dt, state);
    aimUi = ui;
    if (!me || !mainSim) return;   /* the own aim is sent even when showing the other's failed */
    static int lastIndex, lastShow, lastValid;
    static float lastA;
    int id, index, show, valid;
    float a, b;
    __try
    {
        id = *(int *)((char *)ui + 0xfc);
        index = *(int *)((char *)ui + 0x100);
        show = state ? *((unsigned char *)state + 0xc8) != 0 : 0;
        valid = state ? *((unsigned char *)state + 0x6d) != 0 : 1;
        void *actor = id ? FindActor(mainSim, id) : NULL;
        if (!actor) return;
        a = *(float *)((char *)actor + 0xe0);
        b = *(float *)((char *)actor + 0xe4);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    int ao = OrdinalOf(id);
    if (ao < 0 || !Mine(id)) return;
    ULONGLONG now = GetTickCount64();
    if (aimSentOrd >= 0 && aimSentOrd != ao)
    {
        char off[32];
        sprintf(off, "ao=%d off", aimSentOrd);
        AimSend(off);
        aimSentOrd = -1;
    }
    aimSeenAt = now;
    int changed = aimSentOrd < 0 || index != lastIndex || show != lastShow || valid != lastValid || fabsf(a - lastA) > 0.002f;
    if (!changed || now - aimSentAt < AIM_SEND_MS) return;
    char text[128];
    sprintf(text, "ao=%d i=%d a=%.5f b=%.5f s=%d v=%d", ao, index, a, b, show, valid);
    AimSend(text);
    aimSentOrd = ao;
    aimSentAt = now;
    lastIndex = index; lastShow = show; lastValid = valid; lastA = a;
}

/* Tests (autopilot "aim <ordinal> <seconds>", host): aim lines for that crewmate as if its player aimed — the autopilot
 * gives commands without the aim UI, so the guest's showing of it is otherwise never seen in a test run. */
static int testAimOrd = -1;
static ULONGLONG testAimUntil;
void NetTestAim(int ordinal, int seconds)
{
    testAimOrd = ordinal;
    testAimUntil = GetTickCount64() + (ULONGLONG)seconds * 1000;
    NetLog("# test: aiming with ordinal %d for %d s", ordinal, seconds);
}
static void TestAim(void)
{
    if (testAimOrd < 0 || me != 1) return;
    ULONGLONG now = GetTickCount64();
    static ULONGLONG last;
    char text[128];
    if (now >= testAimUntil)
    {
        sprintf(text, "ao=%d off", testAimOrd);
        AimSend(text);
        testAimOrd = -1;
        return;
    }
    if (now - last < AIM_SEND_MS) return;
    last = now;
    float a = 0.35f * sinf((float)(now % 6283) / 1000.0f);   /* sweeps up and down */
    sprintf(text, "ao=%d i=4 a=%.5f b=%.5f s=1 v=1", testAimOrd, a, a);
    AimSend(text);
}

/* Every frame: the aim mode is gone (shot, cancelled, another crewmate). */
static void AimWatch(void)
{
    TestAim();
    if (aimSentOrd < 0 || GetTickCount64() - aimSeenAt < AIM_OFF_MS) return;
    char off[32];
    sprintf(off, "ao=%d off", aimSentOrd);
    AimSend(off);
    aimSentOrd = -1;
}

/* Same command as the record: actor by ordinal (by id for old records without ao=), action, target, flag. */
static int SameCommand(const Entry *e, int id, int ao, int index, int type, const unsigned char *data, int len, int to, int flag)
{
    if (e->ao >= 0 ? e->ao != ao : e->actor != id) return 0;
    if (e->index != index || e->type != type || e->dataLen != len || e->flag != flag) return 0;
    if (type == 2 && e->to >= 0) return e->to == to;
    return memcmp(e->data, data, len) == 0;
}

/* End of an actor's turn (0x390e20(ActorSimulator *, Actor *): action points to 0): the AI's order executor calls it
 * (0x43cf9c) when an enemy has nothing more to do, bypassing RequestAction; the turn manager ends the faction's turn
 * once no actor has usable points left. Without it the guest's enemy turn never ended by itself (an enemy that only
 * moved kept points). Logged as a command with action index -1:
 *   cmd <seq> o=ai a=<id> ao=<ordinal> i=-1 n=@done t=0: to=-1 fl=0 r=... e=... d=0 f=... pv=...
 * Other callers (the actor simulator, FactionDone for every actor) are consequences: not logged. */
typedef void (*EndActor_t)(void *sim, void *actor);
static EndActor_t realEndActor;
#define RVA_EndActor 0x390e20
#define INDEX_DONE (-1)

static void HookEndActor(void *sim, void *actor)
{
    int origin = replaying && depth == 0 ? ORIGIN_REPLAY : Origin();
    if (mode != NET_OFF && depth == 0 && !inFactionDone && (origin == ORIGIN_AI || origin == ORIGIN_REPLAY))
    {
        if (guest && origin == ORIGIN_AI) NetLog("# DIFF: the guest's AI ended an actor's turn (it should be off)");
        if (sim == mainSim) ScanActors(sim);
        int id = ActorId(actor);
        unsigned *r = Rng();
        NetLog("cmd %u o=%s a=%d ao=%d i=-1 n=@done t=0: to=-1 fl=0 r=%08x:%08x e=%d d=0 f=%u w=%llu pv=%u%s", ++seq, OriginLabel(origin),
               id, OrdinalOf(id), r ? r[0] : 0, r ? r[1] : 0, EntityCount(sim), frame, worldSteps, previews,
               mainSim && sim != mainSim ? "  # not the main actor simulator" : ""); RngMark();
    }
    depth++;
    realEndActor(sim, actor);
    depth--;
}

/* The turn manager (world update -> 0x483b40 -> 0x484c60) ends a faction's turn by itself (the single "end o=other" of
 * a recorded enemy turn) once nothing is going on, the list of actors still to act (turn state = [turn object + 0x18],
 * vector<int> at +0x48, emptied by the enemy turn simulator) is empty and no actor has usable action points (+0x90).
 * The AI's order executor writes those points itself ([actor + 0x90] = 0 after an order, 0x43be40), not through
 * RequestAction, so a guest that only replays the commands keeps enemies with points and the turn never ends — forcing
 * FactionDone instead gives a second, nested turn end the host did not have (it took the next recorded one). So when
 * the record says the enemy turn is over, the guest ends every actor's turn of the faction (0x390e20, as FactionDone
 * does for all of them a frame later anyway) and lets the game end the faction's turn its own way.
 * Returns the number of actors whose turn was ended (-1: turn state unreadable). */
/* " bt=<return addresses>" of the caller's caller (Steam-build RVAs; x = outside the exe). */
static void BackTrace(char *bt, size_t size)
{
    void *frames[8];
    int n = RtlCaptureStackBackTrace(2, 8, frames, NULL);
    strcpy(bt, " bt=");
    for (int i = 0; i < n; i++)
    {
        ULONG_PTR a = (ULONG_PTR)frames[i];
        char one[16];
        if (a > (ULONG_PTR)base && a < (ULONG_PTR)base + 0x1000000) sprintf(one, "%lx,", SteamRva((DWORD)(a - (ULONG_PTR)base)));
        else strcpy(one, "x,");
        if (strlen(bt) + strlen(one) < size) strcat(bt, one);
    }
}

/* Guest: frame of the last FinishFaction and the turn ends since. An enemy turn in which the host's AI gave no orders
 * (dormant enemies) ended at the guest with a second turn end in the same frame — the crew's turn, which the host's
 * game did not end: the guest then skipped its turn and fell behind (2026-11-27, nettest --late, 8/77). */
static unsigned finishedAt;
static int endsAfterFinish;
#define FINISH_FRAMES 3

static int FinishFaction(void *sim)
{
    finishedAt = frame;
    endsAfterFinish = 0;
    int ids[256], n = 0, cleared = 0;
    __try
    {
        char *turn = (char *)FactionDoneObject(sim), *state = turn ? *(char **)(turn + 0x18) : NULL;
        if (!state) return -1;
        int **first = (int **)(state + 0x48), **last = (int **)(state + 0x50);
        cleared = (int)(*last - *first);
        *last = *first;
        for (int *p = *(int **)(state + 0x28), *end = *(int **)(state + 0x30); p < end && n < 256; p++) ids[n++] = *p;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    int ended = 0;
    inFactionDone++;   /* not logged as commands */
    for (int i = 0; i < n; i++)
    {
        void *actor = FindActor(sim, ids[i]);
        if (actor) { realEndActor(sim, actor); ended++; }
    }
    inFactionDone--;
    if (cleared) NetLog("# %d actors still to act struck off", cleared);
    return ended;
}

/* Host: turn ends logged when the crew last acted (TurnWatch: only a turn the crew played in can be stuck). */
static int crewActedFor = -1;

/* Host: where its own crewmates were last sent (the path's destination node: the first node of a move target). A guest
 * move to one of these is refused — both players could move at the same moment and end up in one cell: the guest
 * clicked a cell that was free in its game while the host's crewmate was already on its way there (2026-11-28). */
#define MAX_DESTS 16
static int destActor[MAX_DESTS], destNode[MAX_DESTS], destsLen;

static int MoveDestination(int type, const unsigned char *data, int len)
{
    return type == 4 && len >= 8 + 16 ? *(const int *)(data + 8) : -1;   /* 8 bytes, then the nodes: {u32 node, u32, float cost, pad} */
}

static void NoteDestination(int id, int node)
{
    if (node < 0) return;
    int k = 0;
    while (k < destsLen && destActor[k] != id) k++;
    if (k == destsLen)
    {
        if (destsLen == MAX_DESTS) return;
        destsLen++;
    }
    destActor[k] = id;
    destNode[k] = node;
}

/* The host's crewmate (alive here) last sent to this node, 0 = none. */
static int DestinationTaken(void *sim, int node)
{
    for (int k = 0; k < destsLen; k++)
        if (destNode[k] == node && FindActor(sim, destActor[k])) return destActor[k];
    return 0;
}

/* ---- "wait" ----
 * PlayerTurnSimulator "wait" (0x45bb20(pts): the HUD button / key, a signal of the HUD the PTS constructor connects):
 * the selected crewmate gives up the rest of its turn. With an action selected that still has uses ([entry + 0x28] > 0)
 * only that action's (0x390a90(sim, actor, index): its uses to 0, 0x390b10, then 0x390dd0 ends the actor's turn if
 * nothing is left), else every action point (0x390930(sim, actor, -1) -> EndActor 0x390e20). Not a RequestAction: before,
 * a guest's "wait" ended its crewmate's turn only in its own game and the host waited for that crewmate for ever
 * (2026-11-28: both games stuck). Logged as a command with action index -2, target = the selected action's index:
 *   cmd <seq> o=player a=<id> ao=<ordinal> i=-2 n=@wait t=2:<index> to=-1 fl=0 r=... e=... d=0 f=... pv=...
 * The guest sends it to the host instead of doing it; the host and the guest's replay do what the button does (RunWait). */
typedef void (*PtsWait_t)(void *pts);
typedef void *(*PtsSelected_t)(void *pts);
typedef void (*SpendAction_t)(void *sim, void *actor, int index);
static PtsWait_t realPtsWait;
#define RVA_PtsWait 0x45bb20
#define RVA_PtsSelected 0x459c40
#define RVA_SpendAction 0x390a90
#define RVA_SpendPoints 0x390930
#define INDEX_WAIT (-2)

/* Uses left of the actor's action (entry + 0x28), -1 = no such action. */
static int ActionUses(void *actor, int index)
{
    __try
    {
        unsigned char *first = *(unsigned char **)((char *)actor + 0x50), *last = *(unsigned char **)((char *)actor + 0x58);
        if (index < 0 || index >= (last - first) / 0x38) return -1;
        return *(int *)(first + index * 0x38 + 0x28);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

static void LogWait(void *sim, void *actor, int index, int origin)
{
    char hex[16];
    unsigned *r = Rng();
    int id = ActorId(actor);
    Hex(hex, (const unsigned char *)&index, 4);
    if (hosting) crewActedFor = endsLogged;
    NetLog("cmd %u o=%s a=%d ao=%d i=%d n=@wait t=2:%s to=-1 fl=0 r=%08x:%08x e=%d d=0 f=%u w=%llu pv=%u%s", ++seq, OriginLabel(origin), id,
           OrdinalOf(id), INDEX_WAIT, hex, r ? r[0] : 0, r ? r[1] : 0, EntityCount(sim), frame, worldSteps, previews, bySuffix); RngMark();
}

/* What the button does, for a crewmate of the other game (the host: the guest's; the guest: what the host's log says). */
static void RunWait(void *sim, void *actor, int index)
{
    DWORD spendAction = ExeRva(RVA_SpendAction), spendPoints = ExeRva(RVA_SpendPoints);
    if (!spendAction || !spendPoints) { NetLog("# ERROR: wait: game addresses not found"); return; }
    LogWait(sim, actor, index, replaying ? ORIGIN_REPLAY : ORIGIN_PLAYER);
    depth++;   /* the actor's turn end inside is a consequence, not a command */
    __try
    {
        if (index >= 0 && ActionUses(actor, index) > 0) ((SpendAction_t)(base + spendAction))(sim, actor, index);
        else ((SpendAction_t)(base + spendPoints))(sim, actor, -1);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { NetLog("# ERROR: wait: the game's code failed"); }
    depth--;
}

static int SendCommand(int ao, int index, int type, const unsigned char *data, int len, int to, int flag, const char *name);

static void HookPtsWait(void *pts)
{
    void *actor = NULL;
    int index = -1;
    DWORD selected = ExeRva(RVA_PtsSelected);
    if (mode != NET_OFF && me && selected)
    {
        __try { actor = ((PtsSelected_t)(base + selected))(pts); index = *(int *)((char *)pts + 0x30); }
        __except (EXCEPTION_EXECUTE_HANDLER) { actor = NULL; }
    }
    if (!actor || !mainSim) { realPtsWait(pts); return; }
    int id = ActorId(actor);
    if (HELD()) { NetLog("# wait dropped: waiting for the guest to enter the battle"); return; }
    if (live)
    {
        /* The guest's: the host does it (it comes back in the host's log, like a command). */
        if (!Mine(id)) NetLog("# wait for actor %d dropped: the host's", id);
        else if (pending && frame != pendingFrame) NetLog("# wait dropped: the previous command is still on its way to the host");
        else SendCommand(OrdinalOf(id), INDEX_WAIT, 2, (const unsigned char *)&index, 4, -1, 0, "@wait");
        return;
    }
    LogWait(mainSim, actor, index, ORIGIN_PLAYER);
    depth++;
    realPtsWait(pts);
    depth--;
}

/* ---- the other player's move target (2026-11-28, the user's wish after the aim) ----
 * The tactics UI (0x5fe970) shows the tile under the pointer with 0x5f9de0(cursor, node, -, -) every frame while a
 * crewmate is selected ([ui + 0x1b0] = hovered nav node, -1 none): node i is entry i (0x48 bytes) of the vector at
 * [game + 0x1cc8] ([cursor] = game), its position (2 floats) at + 0x24. The own one goes to the other player as
 * "move ao=<n> x=<x> y=<y>" (host: in its log; guest: "gmove ..." to the host, played at once, not logged) when the
 * tile changes, "move ao=<n> off" 250 ms after the cursor is gone. The other game writes swh2_net_move.txt
 * "<seq> <actor id> <owner> <x> <y>" / "<seq> off" — the mission Lua puts a marker with the owner's nick there
 * (coop.lua C.netMove, entity coop_move_N) — while its world is quiet, and takes it down at any command of that actor. */
typedef void (*CursorNode_t)(void *cursor, long long node, int a, int b);
static CursorNode_t realCursorNode;
#define RVA_CursorNode 0x5f9de0
#define MOVE_SEND_MS 60
#define MOVE_OFF_MS 250
static int moveSentOrd = -1;
static float moveSentX, moveSentY;
static ULONGLONG moveSeenAt, moveSentAt;
static wchar_t movePath[MAX_PATH];
static struct { int id, shown, want; float x, y; } remoteMove = { -1, 0, 0, 0, 0 };

static void MoveSend(const char *text)
{
    if (me == 1) { if (hosting) NetLog("move %s", text); }
    else if (me == 2) { char line[128]; sprintf(line, "gmove %s", text); NetTcpSend(line); }
}

static void HookCursorNode(void *cursor, long long node, int a, int b)
{
    realCursorNode(cursor, node, a, b);
    if (!me || !mainSim || !lastPts) return;
    DWORD selected = ExeRva(RVA_PtsSelected);
    int id;
    float x, y;
    __try
    {
        void *actor = selected ? ((PtsSelected_t)(base + selected))(lastPts) : NULL;
        if (!actor) return;
        id = ActorId(actor);
        char *game = *(char **)cursor;
        char *first = *(char **)(game + 0x1cc8), *last = *(char **)(game + 0x1cc8 + 8);
        if (node < 0 || node >= (last - first) / 0x48) return;
        x = *(float *)(first + node * 0x48 + 0x24);
        y = *(float *)(first + node * 0x48 + 0x28);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    int ao = OrdinalOf(id);
    if (ao < 0 || !Mine(id) || !(x == x) || !(y == y)) return;
    ULONGLONG now = GetTickCount64();
    moveSeenAt = now;
    if (ao == moveSentOrd && x == moveSentX && y == moveSentY) return;
    if (now - moveSentAt < MOVE_SEND_MS) return;
    static int told;
    if (!told) { told = 1; NetLog("# move target: first tile %lld at %.1f,%.1f (actor %d)", node, x, y, id); }
    char text[96];
    sprintf(text, "ao=%d x=%.1f y=%.1f", ao, x, y);
    MoveSend(text);
    moveSentOrd = ao; moveSentX = x; moveSentY = y; moveSentAt = now;
}

/* Every frame: the tile cursor is gone (moved, cancelled, aiming, another crewmate). */
static void MoveWatch(void)
{
    if (moveSentOrd < 0 || GetTickCount64() - moveSeenAt < MOVE_OFF_MS) return;
    char off[32];
    sprintf(off, "ao=%d off", moveSentOrd);
    MoveSend(off);
    moveSentOrd = -1;
}

static void MoveMarkerFile(int on)
{
    static int markSeq;
    FILE *f = _wfopen(movePath, L"w");
    if (!f) return;
    if (on) fprintf(f, "%d %d %d %.1f %.1f\n", ++markSeq, remoteMove.id, OwnerOf(remoteMove.id), remoteMove.x, remoteMove.y);
    else fprintf(f, "%d off\n", ++markSeq);
    fclose(f);
}

static void MoveFromLine(const char *p)
{
    int ao = -1;
    float x, y;
    if (sscanf(p, "ao=%d", &ao) != 1) return;
    int id = IdOfOrdinal(ao, -1);
    if (id < 0 || OwnerOf(id) == me) return;   /* ours: the other game shouldn't be moving it */
    if (strstr(p, " off")) { if (remoteMove.id == id) remoteMove.want = 0; return; }
    if (sscanf(p, "ao=%d x=%f y=%f", &ao, &x, &y) != 3) return;
    if (remoteMove.shown && (remoteMove.id != id || remoteMove.x != x || remoteMove.y != y)) remoteMove.shown = 0;   /* moved: shown again below */
    remoteMove.id = id; remoteMove.x = x; remoteMove.y = y; remoteMove.want = 1;
}

/* Every frame of the main world: the marker up only while the world is quiet and this game has played what came
 * before (as the aim). */
static void MoveDraw(void *sim)
{
    int quiet = !Busy(sim) && !(live && scriptPos < scriptLen);
    int on = remoteMove.want && quiet && remoteMove.id >= 0;
    if (on && !remoteMove.shown) { remoteMove.shown = 1; MoveMarkerFile(1); }
    else if (!on && remoteMove.shown) { remoteMove.shown = 0; MoveMarkerFile(0); }
}

/* A command of this actor starts: its move target is over. */
static void MoveDropFor(int id)
{
    if (remoteMove.id != id) return;
    remoteMove.want = 0;
    if (remoteMove.shown) { remoteMove.shown = 0; MoveMarkerFile(0); }
}

static void HookRequestAction(void *sim, void *actor, int index, const void *target, unsigned char flag)
{
    int origin = replaying && depth == 0 ? ORIGIN_REPLAY : Origin(), mainPreview = 0;
    if (origin == ORIGIN_PREVIEW && sim && sim == mainSim)
    {
        /* Previews run on a copy of the world with its own ActorSimulator; this one acts on the real world. */
        origin = OriginOf(1);
        mainPreview = 1;
    }
    if (origin == ORIGIN_PREVIEW) { previews++; realRequestAction(sim, actor, index, target, flag); return; }
    if (HELD() && depth == 0 && origin == ORIGIN_PLAYER)
    {
        NetLog("# player command dropped: waiting for the guest to enter the battle");
        return;
    }
    if (depth == 0 && (origin == ORIGIN_PLAYER || origin == ORIGIN_AI || origin == ORIGIN_REPLAY)) mainSim = sim;
    if (depth == 0 && sim == mainSim) ScanActors(sim);   /* an actor spawned this frame */

    static unsigned char data[MAX_VEC_BYTES + 8];
    static char hex[2 * (MAX_VEC_BYTES + 8) + 1];
    char name[64], bt[160] = "";
    int type = -1, len = -1;
    __try { len = NetTargetBytes((const unsigned char *)target, data, sizeof data, &type); }
    __except (EXCEPTION_EXECUTE_HANDLER) { len = -1; }
    if (len >= 0) Hex(hex, data, len); else strcpy(hex, "?");
    int id = ActorId(actor), ao = OrdinalOf(id), to = type == 2 && len == 4 ? OrdinalOf(*(int *)data) : -1;
    ActionName(actor, index, name, sizeof name);
    if (depth == 0 && sim == mainSim) { AimDropFor(sim, id); MoveDropFor(id); }

    if (mode == NET_REPLAY && depth == 0 && origin == ORIGIN_PLAYER)
    {
        /* The game acting for the player by itself (automatic reload after the last shot, fl=1): the record has it
         * too; it counts as that entry, which is then not issued again. */
        Entry *head = scriptPos < scriptLen ? &script[scriptPos] : NULL;
        if (head && head->kind == 'c' && head->origin == ORIGIN_PLAYER && len >= 0 && SameCommand(head, id, ao, index, type, data, len, to, flag))
        {
            Sync(head, sim);
            scriptPos++;
            idle = 0;
            NetLog("# the game gave player command %d itself, as in the record", scriptPos);
        }
        else if (live)
        {
            /* The guest's player: the command goes to the host, which decides; it comes back in the host's log. An
             * automatic one (fl=1) the host's game gives too. A second command while one is on its way (a double
             * click) is dropped, unless it belongs to the same frame (move, then the door at its end). */
            if (flag) NetLog("# player command fl=1 (%s) not in the host's log yet: left to the host", name);
            else if (len < 0) NetLog("# DIFF: player command %s with an unreadable target, not sent", name);
            else if (!Mine(id)) NetLog("# player command %s for actor %d dropped: the host's", name, id);
            else if (pending && frame != pendingFrame) NetLog("# player command %s dropped: the previous one is still on its way to the host", name);
            else SendCommand(ao, index, type, data, len, to, flag, name);
            return;
        }
        else NetLog("# DIFF: the game gave a player command the record does not have here");
    }

    if (origin == ORIGIN_LUA && depth == 0 && sim == mainSim) luaSeen++;
    if (origin == ORIGIN_AI && depth == 0 && guest)
        NetLog("# DIFF: the guest's AI gave a command (it should be off): a=%d i=%d n=%s", id, index, name);
    /* Who calls RequestAction from inside an action or from an unknown place. */
    if (depth > 0 || origin == ORIGIN_OTHER || mainPreview) BackTrace(bt, sizeof bt);

    unsigned *r = Rng();
    NetLog("cmd %u o=%s a=%d ao=%d i=%d n=%s t=%d:%s to=%d fl=%u r=%08x:%08x e=%d d=%d f=%u w=%llu pv=%u%s%s", ++seq,
           OriginLabel(origin), id, ao, index, name[0] ? name : "?", type, hex, to, flag, r ? r[0] : 0, r ? r[1] : 0,
           EntityCount(sim), depth, frame, worldSteps, previews, bt, depth == 0 ? bySuffix : ""); RngMark();
    if (hosting && depth == 0 && sim == mainSim && (origin == ORIGIN_PLAYER || (origin == ORIGIN_REPLAY && replayAs < 0) || replayAs == ORIGIN_PLAYER))
    {
        crewActedFor = endsLogged;
        if (!bySuffix[0] && len >= 0) NoteDestination(id, MoveDestination(type, data, len));
    }
    depth++;
    realRequestAction(sim, actor, index, target, flag);
    depth--;
}

static ULONG_PTR HookFactionDone(void *turn)
{
    int origin = replaying ? ORIGIN_REPLAY : Origin();
    if (HELD() && origin == ORIGIN_PLAYER)
    {
        static unsigned told;
        if (frame - told > 60) { told = frame; NetLog("# turn end refused: waiting for the guest to enter the battle"); }
        return 0;
    }
    if (origin != ORIGIN_PREVIEW && guest && finishedAt && frame - finishedAt <= FINISH_FRAMES)
    {
        char bt[160];
        BackTrace(bt, sizeof bt);
        /* The host's game may also have ended two turns in one frame (a faction with no one left): then it's allowed. */
        Entry *head = scriptPos < scriptLen ? &script[scriptPos] : NULL;
        int hostToo = head && head->kind == 'e' && scriptPos > 0 && script[scriptPos - 1].kind == 'e' && script[scriptPos - 1].frame == head->frame;
        if (endsAfterFinish++ > 0 && !hostToo)
        {
            NetLog("# second turn end right after the enemy turn was finished (o=%s): refused, the host's game had none%s", NetOriginName(origin), bt);
            return 0;
        }
        NetLog("# turn end after the enemy turn was finished (o=%s)%s", NetOriginName(origin), bt);
    }
    if (origin != ORIGIN_PREVIEW)
    {
        if (mode == NET_REPLAY)
        {
            /* A turn end the game gives itself (player after the last action point, scripts, the turn manager) counts
             * as the recorded one at the head. Autoplay has only the player's in its script. */
            Entry *head = scriptPos < scriptLen ? &script[scriptPos] : NULL;
            int ours = guest || origin == ORIGIN_PLAYER || origin == ORIGIN_REPLAY;
            /* Automatic player commands (fl=1) the host's game gave and this one didn't (2026-11-28, laptop: the host
             * reloaded twice in one frame after the turn end, the guest's game once) stand before the turn end: they
             * count as done, else this turn end is "not in the record" and the guest falls out of step (it froze). */
            if (ours && head && head->kind == 'c' && head->flag && head->origin == ORIGIN_PLAYER)
            {
                int k = scriptPos;
                while (k < scriptLen && script[k].kind == 'c' && script[k].flag && script[k].origin == ORIGIN_PLAYER) k++;
                if (k < scriptLen && script[k].kind == 'e')
                {
                    NetLog("# %d automatic player command(s) of the record before this turn end skipped (this game didn't give them)", k - scriptPos);
                    scriptPos = k;
                    head = &script[scriptPos];
                }
            }
            if (ours && head && head->kind == 'e')
            {
                Sync(head, mainSim);
                scriptPos++;
                idle = 0;
                if (origin != ORIGIN_REPLAY) NetLog("# the game ended the turn itself, as in the record (recorded o=%s)", NetOriginName(head->origin));
                if (scriptPos == scriptLen && !live) NetLog("# replay finished: %d entries", scriptLen);
            }
            else if (live && origin == ORIGIN_PLAYER)
            {
                /* The guest's player ended the turn (or the game did after the last action point): the host decides.
                 * If the host's game ends it by itself first, the request is stale there and ignored. */
                SendEnd("the guest's game ended the turn");
                return 0;
            }
            else if (ours && live && !head)
            {
                /* The guest's game got there first (the host's line is on its way): the next turn end that arrives is
                 * this one. */
                earlyEnds++;
                NetLog("# turn ended (o=%s) before the host's turn end arrived", NetOriginName(origin));
            }
            else if (ours) NetLog("# DIFF: turn ended (o=%s) where the record has no turn end", NetOriginName(origin));
        }
        if (origin != ORIGIN_PLAYER && origin != ORIGIN_REPLAY) endsSeen++;
        void *expected = mainSim ? FactionDoneObject(mainSim) : NULL;
        unsigned *r = Rng();
        endsLogged++;
        NetLog("end %u o=%s r=%08x:%08x e=%d f=%u w=%llu%s%s", ++seq, OriginLabel(origin), r ? r[0] : 0, r ? r[1] : 0,
               EntityCount(mainSim), frame, worldSteps, bySuffix, !expected || expected == turn ? "" : "  # object differs from the simulator slot"); RngMark();
    }
    inFactionDone++;   /* it ends every actor's turn (0x390e20): consequences, not logged as commands */
    ULONG_PTR result = realFactionDone(turn);
    inFactionDone--;
    return result;
}

/* The Lua correction answers "<state> <missing>"; *missing = actors alive in the host's state but gone here. */
/* "<state> <missing> <changed>" from the Lua; changed = how many things it corrected (-1 = an older Lua: unknown). */
static int SyncDone(int wanted, int *missing, int *changed)
{
    FILE *f = _wfopen(syncedPath, L"r");
    int n = -1, m = 0, c = -1;
    if (f) { if (fscanf(f, "%d %d %d", &n, &m, &c) < 1) n = -1; fclose(f); }
    if (missing) *missing = n == wanted ? m : 0;
    if (changed) *changed = n == wanted ? c : -1;
    return n == wanted;
}

/* Hard correction (live guest): an actor died here but lives at the host — the soft correction can't bring it back
 * (2026-11-27: the guest's shot killed an enemy the same shot at the host left alive). The guest quits to the menu and
 * joins the host's battle again from the host's latest save, then catches up with its log. At most once a minute. */
static void Resync(int missing)
{
    static ULONGLONG last;
    ULONGLONG now = GetTickCount64();
    if (last && now - last < 60000)
    {
        NetLog("# DIFF: %d actors of the host's state gone here; rejoined less than a minute ago, playing on", missing);
        return;
    }
    last = now;
    NetLog("# DIFF: %d actors of the host's state gone here: rejoining the host's battle from its save", missing);
    NetQuitToMenu();
}

static void SyncRequest(int wanted)
{
    FILE *f = _wfopen(syncPath, L"w");
    if (!f) return;
    fprintf(f, "%d", wanted);
    for (int i = 0; i < ordLen; i++) fprintf(f, " %d", ordIds[i]);
    fputc('\n', f);
    fclose(f);
}

/* ---- nicknames (NE Co-op) ----
 * The host logs "name host <nick>" at start (the guest reads it with the host's log); the guest sends "hello <nick>"
 * when it connects (nettcp.c), the host logs "name guest <nick>" (it comes back to the guest the same way).
 * The texts with names (CoopMod.NameKeys: owner labels / modifiers coop_owner_N, the HUD status) are installed with
 * the default names "Host" / "Guest"; each has a template coop_tpl_<key> with {1} = host, {2} = guest. ApplyNames
 * writes the templates with the nicknames into the game's text table whenever they change:
 *   table = *[0xe9fbe8] (the localisation object; HasKey 0x729970 locks +0x90 and searches +0x100), the map at +0x100
 *   is open addressing: capacity +0x18, control bytes +0x20, entries +0x28 (0x40 bytes: key std::string, value
 *   std::string at +0x20); find 0x166110(map, const std::string *key) -> index (= capacity: not there).
 * A longer value gets a buffer from the game's operator new (0xa05748) — its heap, not ours; the old one is left. */
static char myName[64], hostName[64], guestName[64];
static int namesDirty = 1;
#define RVA_TextTable 0xe9fbe8
#define RVA_TextFind 0x166110
#define RVA_OpNew 0xa05748
typedef size_t (*TextFind_t)(void *map, const void *key);
typedef void *(*OpNew_t)(size_t);

static const char *const nameKeys[] = {
    "coop_owner_1", "coop_owner_2", "coop_move_1", "coop_move_2",
    "coop_net_host_waiting", "coop_net_host_connected", "coop_net_host_closed", "coop_net_host_failed", "coop_net_host_joining",
    "coop_net_guest_waiting", "coop_net_guest_connected", "coop_net_guest_closed", "coop_net_guest_failed",
    "coop_net_turn_both", "coop_net_turn_p1", "coop_net_turn_p2", "coop_net_turn_enemy",
    "tutorial_coop_net_guest_left", "tutorial_coop_net_guest_left_page1", "tutorial_coop_net_guest_back", "tutorial_coop_net_guest_back_page1",
    "tutorial_coop_net_guest_lost", "tutorial_coop_net_guest_lost_page1", "tutorial_coop_net_host_left", "tutorial_coop_net_host_left_page1",
    "tutorial_coop_net_host_lost", "tutorial_coop_net_host_lost_page1",
    "coop_net_refused_cell", "coop_net_refused_gone", "coop_net_refused_notyours", "coop_net_refused_bad",
    "tutorial_coop_net_version", "tutorial_coop_net_version_page1", "tutorial_coop_net_wait_host", "tutorial_coop_net_wait_host_page1",
};

/* The value std::string of a text key in the game's table (NULL: no table yet / no such key). */
static char *TextValue(const char *key)
{
    DWORD tableRva = ExeRva(RVA_TextTable), findRva = ExeRva(RVA_TextFind);
    if (!tableRva || !findRva) return NULL;
    __try
    {
        char *loc = *(char **)(base + tableRva);
        if (!loc) return NULL;
        char *map = loc + 0x100;
        size_t len = strlen(key);
        struct { const char *p; size_t pad; size_t size, cap; } k = { key, 0, len, len < 16 ? 16 : len };   /* pointer form */
        size_t i = ((TextFind_t)(base + findRva))(map, &k), cap = *(size_t *)(map + 0x18);
        if (i >= cap) return NULL;
        char *e = *(char **)(map + 0x28) + i * 0x40;
        const char *ek = *(size_t *)(e + 0x18) >= 16 ? *(char **)e : e;
        if (*(size_t *)(e + 0x10) != len || memcmp(ek, key, len) != 0) return NULL;   /* layout not as expected */
        return e + 0x20;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}

static const char *StrText(const char *s) { return *(size_t *)(s + 0x18) >= 16 ? *(char *const *)s : s; }

static int SetText(char *s, const char *text)
{
    size_t n = strlen(text);
    __try
    {
        size_t cap = *(size_t *)(s + 0x18);
        if (n > cap)
        {
            DWORD newRva = ExeRva(RVA_OpNew);
            char *buf = newRva ? (char *)((OpNew_t)(base + newRva))(n + 1) : NULL;
            if (!buf) return 0;
            *(char **)s = buf;
            *(size_t *)(s + 0x18) = n;
        }
        char *p = *(size_t *)(s + 0x18) >= 16 ? *(char **)s : s;
        memcpy(p, text, n + 1);
        *(size_t *)(s + 0x10) = n;
        return 1;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static void ApplyNames(void)
{
    char *d1 = TextValue("coop_net_default_1"), *d2 = TextValue("coop_net_default_2");
    if (!d1 || !d2) return;   /* table not there (yet), or the mod's strings missing: try again later */
    char def1[64], def2[64], tplKey[96], out[512];
    strncpy(def1, StrText(d1), 63); def1[63] = 0;
    strncpy(def2, StrText(d2), 63); def2[63] = 0;
    const char *n1 = hostName[0] ? hostName : def1, *n2 = guestName[0] ? guestName : def2;
    int done = 0;
    for (int i = 0; i < (int)(sizeof nameKeys / sizeof *nameKeys); i++)
    {
        sprintf(tplKey, "coop_tpl_%s", nameKeys[i]);
        char *tpl = TextValue(tplKey), *val = TextValue(nameKeys[i]);
        if (!tpl || !val) continue;
        const char *t = StrText(tpl);
        size_t o = 0;
        for (; *t && o < sizeof out - 1; t++)
        {
            if (t[0] == '{' && (t[1] == '1' || t[1] == '2') && t[2] == '}')
            {
                const char *n = t[1] == '1' ? n1 : n2;
                size_t l = strlen(n);
                if (o + l >= sizeof out) break;
                memcpy(out + o, n, l);
                o += l;
                t += 2;
            }
            else out[o++] = *t;
        }
        out[o] = 0;
        done += SetText(val, out);
    }
    namesDirty = 0;
    NetLog("# names: host %s, guest %s (%d texts)", n1, n2, done);
}

static void SetName(char *dst, const char *src)
{
    char clean[64];
    int n = 0;
    for (; *src && n < 60; src++)
        if ((unsigned char)*src >= ' ' && *src != '{' && *src != '}') clean[n++] = *src;
    while (n > 0 && clean[n - 1] == ' ') n--;
    clean[n] = 0;
    if (strcmp(dst, clean) != 0) { strcpy(dst, clean); namesDirty = 1; }
}

static int FromHex(const char *s, unsigned char *out, int max);
static ULONG_PTR HookFactionDone(void *turn);

/* ---- guest play (test of the guest commands, swh2_net.txt "replay connect <host> play") ----
 * No one sits at the guest: it plays its crewmates' commands of swh2_net_guestplay.log (the old record the host
 * autoplays too), sent to the host the way a player's would be. The host decides when: when the head of its autoplay
 * script is a command of the guest's crewmate it logs "await guest <n>", and the guest answers with its next own
 * command (or "gskip" when it has none). No counters on either side to agree on — a battle that went its own way
 * (an enemy turn with nothing to do, a crewmate dead earlier) cannot make both wait for each other (2026-11-27). */
static int guestPlay;
static Entry *play;
static int playLen, playPos, playIdle;
static int awaitsArrived, awaitsServed;   /* "await guest" requests in the host's log / answered */

static void LoadGuestPlay(const wchar_t *path)
{
    static char line[2 * (MAX_VEC_BYTES + 8) + 512];
    static Entry tmp;
    FILE *f = _wfopen(path, L"r");
    if (!f) { NetLog("# ERROR: guest play: no %ls", path); guestPlay = 0; return; }
    int ends = 0, lua = 0;
    while (fgets(line, sizeof line, f))
    {
        int o = NetParseLine(line, &tmp);
        if (o < 0 || o == ORIGIN_PREVIEW || (tmp.kind == 'c' && tmp.depth > 0)) continue;
        if (o == ORIGIN_REPLAY) o = ORIGIN_PLAYER;
        int take = o == ORIGIN_PLAYER && (tmp.kind == 'e' || !tmp.flag);
        tmp.origin = o;
        tmp.endsBefore = ends;
        tmp.luaBefore = lua;
        if (tmp.kind == 'e') ends++;
        if (tmp.kind == 'c' && o == ORIGIN_LUA) lua++;
        if (!take) continue;
        Entry *grown = (Entry *)realloc(play, (playLen + 1) * sizeof(Entry));
        if (!grown) break;
        play = grown;
        play[playLen++] = tmp;
    }
    fclose(f);
    int cmds = 0;
    for (int i = 0; i < playLen; i++) cmds += play[i].kind == 'c';
    NetLog("# guest play: %d player commands and %d player turn ends to send to the host", cmds, playLen - cmds);
}

static void GuestPlayPump(void *sim)
{
    if (!guestPlay || awaitsServed >= awaitsArrived) return;
    if (CutsceneHold() || Busy(sim) || pending) { playIdle = 0; return; }
    if (++playIdle < 10) return;   /* the host's command before the request has been replayed and is over */
    playIdle = 0;
    awaitsServed++;
    /* The host waits for the guest's crewmate at the head of its (same old) record: our next own command is that one. */
    while (playPos < playLen && (play[playPos].kind != 'c' || OwnerOf(IdOfOrdinal(play[playPos].ao, -1)) != 2)) playPos++;
    if (playPos >= playLen || !crewLen)
    {
        NetTcpSend("gskip");
        NetLog("# guest play: asked for a command, none left: skip");
        return;
    }
    Entry *e = &play[playPos++];
    static unsigned char data[MAX_VEC_BYTES + 8];
    memcpy(data, e->data, e->dataLen);
    SendCommand(e->ao, e->index, e->type, data, e->dataLen, e->to, e->flag, e->name);
}

/* ---- host: the guest's commands ---- */
/* A guest command the host doesn't play: the guest hears it in the host's log ("refused ao=<ordinal> <why>", it stops
 * waiting for the command and shows why over its crewmate). The rest of the same guest frame (the door at the end of a
 * refused move) goes the same way. */
static unsigned refusedFrame = ~0u;

static void Refuse(int ao, unsigned guestFrame, const char *why, const char *text)
{
    refusedFrame = guestFrame;
    NetLog("# guest command for actor ordinal %d refused: %s", ao, text);
    NetLog("refused ao=%d %s", ao, why);
}

static void HostPlay(void *sim, const char *line)
{
    if (strcmp(line, "ready") == 0)
    {
        if (!guestIn) NetLog("# the guest is in the battle: it goes on");
        guestIn = 1;
        if (guestAway)
        {
            guestAway = 0;
            NetLog("# the guest is back in the battle: its crewmates are its own again");
            Notify("guest_back", 0);
        }
        return;
    }
    if (strcmp(line, "left") == 0)
    {
        /* The guest's player quit to the main menu (not a rejoin of its own). */
        NetLog("# the guest left the battle: its crewmates are the host's until it is back");
        if (!guestAway) Notify("guest_left", 0);
        guestAway = 1;
        return;
    }
    int ao, index, type = -1, n;
    const char *t = strstr(line, " t="), *pto = strstr(line, " to="), *pfl = strstr(line, " fl=");
    if (sscanf(line, "gcmd ao=%d i=%d", &ao, &index) == 2 && t && pto && pfl)
    {
        static unsigned char data[MAX_VEC_BYTES + 8];
        const char *colon = strchr(t, ':');
        int len = colon && sscanf(t + 3, "%d", &type) == 1 ? FromHex(colon + 1, data, sizeof data) : -1;
        int to = atoi(pto + 4), flag = atoi(pfl + 4), id = IdOfOrdinal(ao, -1);
        const char *pf = strstr(line, " f=");
        unsigned gf = pf ? (unsigned)strtoul(pf + 3, NULL, 10) : ~1u;
        void *actor = id >= 0 ? FindActor(sim, id) : NULL;
        if (!actor) { Refuse(ao, gf, "gone", "no such actor here"); return; }
        if (OwnerOf(id) == 1 && !guestAway) { Refuse(ao, gf, "notyours", "the host's crewmate"); return; }
        if (gf == refusedFrame) { Refuse(ao, gf, "chain", "the command before it in that frame was refused"); return; }
        int node = MoveDestination(type, data, len), taken = node >= 0 ? DestinationTaken(sim, node) : 0;
        if (taken && taken != id) { Refuse(ao, gf, "cell", "the host's crewmate went to that cell meanwhile"); return; }
        otherActive = id;   /* the other player's crewmate: the host's camera follows it when its own crew is done */
        if (index == INDEX_WAIT)
        {
            if (len != 4) { Refuse(ao, gf, "bad", "a wait with a bad target"); return; }
            replaying = 1;
            replayAs = ORIGIN_PLAYER;
            bySuffix = " by=guest";
            RunWait(sim, actor, *(int *)data);
            bySuffix = "";
            replaying = 0;
            replayAs = -1;
            return;
        }
        if (len == 4 && type == 2 && to >= 0) { int tid = IdOfOrdinal(to, *(int *)data); memcpy(data, &tid, 4); }
        unsigned char target[TARGET_SIZE + 8];
        if (len < 0 || !NetBuildTarget(target, type, data, len)) { Refuse(ao, gf, "bad", "a bad target"); return; }
        replaying = 1;
        replayAs = ORIGIN_PLAYER;
        bySuffix = " by=guest";
        HookRequestAction(sim, actor, index, target, (unsigned char)flag);
        bySuffix = "";
        replaying = 0;
        replayAs = -1;
        NetFreeTarget(target);

        /* Autoplay host (test): the old record's command of the guest's actor that it waited for. */
        Entry *head = autoplay && scriptPos < scriptLen ? &script[scriptPos] : NULL;
        if (head && head->kind == 'c' && OwnerOf(IdOfOrdinal(head->ao, head->actor)) == 2)
        {
            scriptPos++;
            guestWait = 0;
            NetLog("# autoplay %d: played by the guest", scriptPos);
        }
        return;
    }
    if (sscanf(line, "gend n=%d", &n) == 1)
    {
        void *turn = FactionDoneObject(sim);
        if (n != endsLogged || !turn) { NetLog("# guest's turn end for turn %d ignored (this is turn %d)", n, endsLogged); return; }
        replaying = 1;
        replayAs = ORIGIN_PLAYER;
        bySuffix = " by=guest";
        HookFactionDone(turn);
        bySuffix = "";
        replaying = 0;
        replayAs = -1;
        return;
    }
    if (strcmp(line, "gskip") == 0)
    {
        Entry *head = autoplay && scriptPos < scriptLen ? &script[scriptPos] : NULL;
        if (head && head->kind == 'c' && OwnerOf(IdOfOrdinal(head->ao, head->actor)) == 2)
        {
            scriptPos++;
            guestWait = 0;
            NetLog("# autoplay %d: the guest has no command for it, skipped", scriptPos);
        }
        return;
    }
    if (strcmp(line, "joined") == 0 || strcmp(line, "rejoin") == 0)
    {
        /* A guest connected (nettcp.c). For one that joins inside the mission (it gets the log from there): the host's
         * name and, in a battle, its actor ordinals (the guest numbers the actors of its loaded save the same way). */
        if (hostName[0]) NetLog("name host %s", hostName);
        if (ordLen > 0 && GetTickCount64() - worldTick < 2000)
        {
            static char ords[MAX_ACTORS * 8 + 16];
            int k = sprintf(ords, "ords");
            for (int i = 0; i < ordLen && k < (int)sizeof ords - 12; i++) k += sprintf(ords + k, " %d", ordIds[i]);
            NetLog("%s", ords);
        }
        return;
    }
    if (strncmp(line, "hello ", 6) == 0)
    {
        SetName(guestName, line + 6);
        NetLog("name guest %s", guestName);
        WriteCoopSlot();   /* the main menu marks the slot played together with this nick */
        return;
    }
    NetLog("# unknown line from the guest: %.80s", line);
}

/* Plays the guest's lines when the world is free; the commands of one guest frame together. */
static void HostPump(void *sim)
{
    static char line[2 * (MAX_VEC_BYTES + 8) + 128], next[sizeof line];
    static int have;
    if (!have) have = NetTcpPoll(line, sizeof line);
    while (have && (strncmp(line, "gaim ", 5) == 0 || strncmp(line, "gmove ", 6) == 0))   /* aim, move target: now, not logged */
    {
        if (line[1] == 'a') AimFromLine(line + 5); else MoveFromLine(line + 6);
        have = NetTcpPoll(line, sizeof line);
    }
    if (!have) return;
    if (Busy(sim) || replaying) return;
    for (;;)
    {
        if (strcmp(line, "ready") != 0 && strcmp(line, "joined") != 0) NetLog("# from the guest: %.120s", line);   /* "ready" comes every few seconds */
        HostPlay(sim, line);
        have = NetTcpPoll(next, sizeof next);
        while (have && (strncmp(next, "gaim ", 5) == 0 || strncmp(next, "gmove ", 6) == 0))
        {
            if (next[1] == 'a') AimFromLine(next + 5); else MoveFromLine(next + 6);
            have = NetTcpPoll(next, sizeof next);
        }
        if (!have) return;
        const char *f1 = strstr(line, " f="), *f2 = strstr(next, " f=");
        int chained = strncmp(line, "gcmd", 4) == 0 && strncmp(next, "gcmd", 4) == 0 && f1 && f2 && atoi(f1 + 3) == atoi(f2 + 3);
        strcpy(line, next);
        if (!chained) return;
    }
}

static int sceneAt[16];   /* guest: the script entry each of the host's scene lines came before */
static long long sceneCheckpoint[16];   /* guest: > 0 = that scene is the host's checkpoint load, host log byte after its line */
static long long checkpointLine;        /* guest: a "checkpoint" line arrived (host log byte after it), its scene comes next */
static int saveAfterCheckpoint;         /* host: 1 = a checkpoint was loaded, 2 = its scene made (the old battle's ticks don't count) */
static ULONGLONG checkpointSceneAt;     /* host: when that scene was made */
static long long needFrom;              /* guest: the host's save must be from this host log byte on (0: any) */

/* Guest: the host won: tell the mission Lua once everything before the "won" line is played here and the world is
 * quiet — or after 10 s anyway, or at once when the host is in its next scene already (this replay is over). */
static void FollowVictory(void *sim, int movedOn)
{
    if (hostWon < 0 || wonTold) return;
    if (!movedOn && GetTickCount64() - hostWonAt < 10000 && (scriptPos < hostWon || Busy(sim))) return;
    wonTold = 1;
    FILE *f = _wfopen(wonPath, L"w");
    if (f) { fputs("1\n", f); fclose(f); }
    NetLog("# the host won the battle: this game ends it in victory too (replayed %d of %d entries before the host's win)", scriptPos, hostWon);
}

/* Guest: the host is in a scene this game hasn't made (its next mission, the world map). */
static int HostMovedOn(void)
{
    return live && scenePos >= 0 && scenePos < scenesLen && sceneAt[scenePos] <= scriptPos;
}

static void Pump(void *sim)
{
    if (mode != NET_REPLAY || HELD()) return;
    if (live) ReadMore();   /* every frame: each one skipped is lag on the other player's actions (was every 4th) */
    FollowVictory(sim, HostMovedOn());
    while (earlyEnds && scriptPos < scriptLen && script[scriptPos].kind == 'e')
    {
        earlyEnds--;
        scriptPos++;
        NetLog("# turn end %d arrived after the game had ended the turn", scriptPos);
    }
    if (pending && frame - pendingAt > PENDING_TIMEOUT)
    {
        NetLog("# DIFF: %d commands sent to the host did not come back in %d frames", pending, PENDING_TIMEOUT);
        pending = 0;
    }
    /* The host is in a scene this game hasn't made (its next mission, or the world map): what follows belongs there,
     * not to this battle (a guest on its results screen played the host's next mission into its old one). */
    if (HostMovedOn())
    {
        static int toldAt = -1;
        if (toldAt != scriptPos)
        {
            toldAt = scriptPos;
            if (sceneCheckpoint[scenePos] > 0)
            {
                /* The host loaded a checkpoint: its battle starts over from its last save; this guest follows from the
                 * save the host writes once it runs again (not an older one). */
                needFrom = sceneCheckpoint[scenePos];
                NetLog("# the host loaded a checkpoint: to the main menu, joining from its next save (host log byte >= %lld)", needFrom);
                NetQuitToMenu();
            }
            else NetLog("# the host went on to another scene: this battle's replay stops here");
        }
        return;
    }
    if (scriptPos >= scriptLen) { GuestPlayPump(sim); return; }
    Entry *e = &script[scriptPos];
    if (CutsceneHold()) { idle = 0; return; }
    /* Commands the player gave in the same frame (move, then open the door at its end) go in together: the second
     * one waits in the actor's queue, as in the record. */
    int chained = e->kind == 'c' && scriptPos > 0 && script[scriptPos - 1].kind == 'c' && script[scriptPos - 1].frame == e->frame;
    if (!chained)
    {
        if (Busy(sim)) { idle = 0; return; }
        idle++;
        /* What came before it in the record (enemy turn, turn ends, script commands such as reinforcements) must be
         * over here too — else a command lands in the wrong turn (run 4). After a divergence it may never come. */
        /* Autoplay: the battle may go its own way, only the turn counts. */
        /* Guest: commands and turn ends are all in the script, in order — only script commands (reinforcements) are
         * the game's own and must have come. */
        int behind = autoplay ? endsSeen < e->endsBefore : luaSeen < e->luaBefore;
        if (gatePassed != scriptPos + 1 && behind)
        {
            if (++gateWait < GATE_TIMEOUT) return;
            NetLog("# DIFF: replay %d: waited %d frames for turn ends %d/%d, script %d/%d — going on", scriptPos + 1,
                   gateWait, endsSeen, e->endsBefore, luaSeen, e->luaBefore);
        }
        gateWait = 0;
        gatePassed = scriptPos + 1;
        /* The player's turn ends and automatic commands (fl=1: reload after the last shot) and script turn ends are the
         * game's to give; forced late. The host's AI commands and enemy turn ends are only ours (the AI is off). */
        /* A live guest's turn ends are the host's (its own game's are sent there): issued without waiting. */
        int gameGives = e->origin != ORIGIN_AI && (e->kind == 'e' ? !live : e->flag);
        int idleFrames = live && !autoplay ? LIVE_IDLE : IDLE_FRAMES, endWait = live && !autoplay ? LIVE_END_WAIT : END_WAIT;
        /* Ending the enemy turn ourselves keeps the full wait even for a live guest: after LIVE_IDLE frames it came before
         * the enemy turn had begun here (its camera, the mission's turn-begin script): the guest's turn ended first,
         * reinforcements never came, a needless rejoin followed (2026-11-28, nettest --checkpoint, 8/20). */
        int enemyEnd = guest && e->kind == 'e' && e->origin == ORIGIN_OTHER;
        if (enemyEnd && idle >= IDLE_FRAMES && clearedFor != scriptPos + 1)
        {
            clearedFor = scriptPos + 1;
            NetLog("# turn end %d: the host's AI was done, turn of %d actors ended", scriptPos + 1, FinishFaction(sim));
        }
        if (idle < (enemyEnd ? IDLE_FRAMES : gameGives ? endWait : idleFrames)) return;
        if (!autoplay && e->statesBefore > statesSynced)
        {
            if (syncWanted != e->statesBefore)
            {
                syncWanted = e->statesBefore;
                syncWait = 0;
                SyncRequest(syncWanted);
            }
            int missing = 0, changed = -1;
            if (!SyncDone(syncWanted, &missing, &changed) && ++syncWait < SYNC_TIMEOUT) return;
            if (syncWait >= SYNC_TIMEOUT) NetLog("# DIFF: state %d not corrected (no answer from the mission Lua)", syncWanted);
            statesSynced = syncWanted;
            if (missing > 0 && live && guest) { idle = 0; Resync(missing); return; }
            /* The correction may start actions: wait for idle again — unless it changed nothing (the usual case; that
             * second wait was lag on every command of a live guest). */
            if (changed != 0 || Busy(sim)) { idle = 0; return; }
        }
    }
    if (hosting && autoplay && e->kind == 'c' && OwnerOf(IdOfOrdinal(e->ao, e->actor)) == 2 && FindActor(sim, IdOfOrdinal(e->ao, e->actor)))
    {
        /* The guest's actor: the guest plays it (HostPlay counts it as this entry); asked once per entry. */
        if (askedFor != scriptPos + 1) { askedFor = scriptPos + 1; NetLog("await guest %d n=%s ao=%d", scriptPos + 1, e->name, e->ao); }
        if (++guestWait < GUEST_WAIT) return;
        guestWait = 0;
        scriptPos++;
        NetLog("# DIFF: autoplay %d: the guest did not play its command (%s of ordinal %d), skipped", scriptPos, e->name, e->ao);
        return;
    }
    /* Lockstep: the host gave it after w world steps, so it goes in here at that step too (HookWorldUpdate runs the world
     * up to it and holds it there). A world that went past it (held free: actions, a dialogue) counts on from it. */
    if (guest && e->step >= 0 && stepsAligned)
    {
        if ((long long)worldSteps < e->step) return;
        if ((long long)worldSteps > e->step)
        {
            NetLog("# world steps: replay %d at step %llu, the host's at %lld: counted from here", scriptPos + 1, worldSteps, e->step);
            worldSteps = (unsigned long long)e->step;
        }
    }
    idle = 0;
    replaying = 1;
    replayAs = e->origin == ORIGIN_PLAYER ? -1 : e->origin;
    if (e->kind == 'e')
    {
        /* The game did not end the turn by itself: as if the player pressed "end turn". */
        void *turn = FactionDoneObject(sim);
        if (turn) HookFactionDone(turn);   /* consumes the entry, logs it as o=replay */
        else { scriptPos++; NetLog("# ERROR: replay %d: no turn object, turn end skipped", scriptPos); }
    }
    else
    {
        scriptPos++;
        void *actor = FindActor(sim, IdOfOrdinal(e->ao, e->actor));
        unsigned char t[TARGET_SIZE + 8];
        static unsigned char data[MAX_VEC_BYTES + 8];
        LocalTargetData(e, data);
        /* The host's AI: the camera goes there (the guest's enemy turn simulator, which moves the camera at the host,
         * doesn't run). The host's player's crewmate: the one the camera follows once this player's crew is done
         * (status line; 2026-11-28 the user found the jumps to every action of the other player jarring). */
        if (actor && live && !e->byGuest && e->depth == 0 && e->origin == ORIGIN_AI) FocusOn(ActorId(actor));
        if (actor && live && !e->byGuest && e->depth == 0 && e->origin == ORIGIN_PLAYER && !e->flag) otherActive = ActorId(actor);
        if (!actor) NetLog("# DIFF: replay %d: actor %d (ordinal %d) not found, skipped", scriptPos, e->actor, e->ao);
        else if (e->index == INDEX_DONE)
        {
            Sync(e, sim);
            HookEndActor(sim, actor);
        }
        else if (e->index == INDEX_WAIT)
        {
            Sync(e, sim);
            RunWait(sim, actor, e->dataLen == 4 ? *(int *)e->data : -1);
        }

        else if (!NetBuildTarget(t, e->type, data, e->dataLen)) NetLog("# ERROR: replay %d: bad target", scriptPos);
        else
        {
            Sync(e, sim);
            HookRequestAction(sim, actor, e->index, t, (unsigned char)e->flag);   /* logs it as o=replay */
            NetFreeTarget(t);
        }
        if (scriptPos == scriptLen && !live) NetLog("# replay finished: %d entries", scriptLen);
    }
    replaying = 0;
    replayAs = -1;
}

/* The dialogue / camera flag of the mission Lua without CutsceneHold's quiet count (that one is the replay's). */
static int CutsceneNow(void)
{
    static int on;
    if (frame % 10 == 0)
    {
        FILE *f = _wfopen(cutscenePath, L"r");
        on = f && fgetc(f) == '1';
        if (f) fclose(f);
    }
    return on;
}

/* Ids of the actors of the faction whose turn it is (the turn state's list, as FinishFaction reads it). */
static int CurrentFactionActors(void *sim, int *ids, int max)
{
    int n = 0;
    __try
    {
        char *turn = (char *)FactionDoneObject(sim), *state = turn ? *(char **)(turn + 0x18) : NULL;
        if (!state) return 0;
        for (int *p = *(int **)(state + 0x28), *end = *(int **)(state + 0x30); p < end && n < max; p++) ids[n++] = *p;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    return n;
}

/* Host: a crew turn no one can go on with. The game ends the crew's turn itself when the selected crewmate is out of
 * action points and no other one can be selected (PlayerTurnSimulator) — but a command that came over the network is
 * not the PTS's own, and here the guest's crewmates can't be selected: when the turn's last action was the guest's,
 * or the host's last crewmate finished while the guest's still had points, the turn sometimes never ended, in either
 * game (2026-11-28: "both moved, no one can move any more"). When the guest's game did end it, its "gend" saved the
 * day; when neither did, both waited for ever. Now the host ends it itself once the crew acted this turn, the world is
 * quiet for STUCK_FRAMES and no actor of the faction has action points left (the game ends it in a few frames when it
 * does). */
#define STUCK_FRAMES 30   /* was 120 (2 s): the user pressed Wait / Esc in that pause, it felt like a stuck turn */
static void TurnWatch(void *sim)
{
    static int quiet, endedFor = -1;
    if (!hosting || HELD() || replaying || crewActedFor != endsLogged || endedFor == endsLogged || Busy(sim) || CutsceneNow())
    {
        quiet = 0;
        return;
    }
    int ids[256], n = CurrentFactionActors(sim, ids, 256), crew = 0;
    for (int i = 0; i < n; i++)
    {
        void *a = FindActor(sim, ids[i]);
        if (!a) continue;
        if (HasPoints(a)) { quiet = 0; return; }
        if (OwnerOf(ids[i])) crew++;
    }
    if (!crew || ++quiet < STUCK_FRAMES) return;
    quiet = 0;
    endedFor = endsLogged;
    void *turn = FactionDoneObject(sim);
    if (!turn) return;
    NetLog("# no crewmate has action points left, yet the turn didn't end: the host ends it");
    replaying = 1;
    replayAs = ORIGIN_PLAYER;
    HookFactionDone(turn);
    replaying = 0;
    replayAs = -1;
}

/* The other player gone: the host takes over the guest's crewmates (after 5 s without a connection, it may come back
 * by itself); the guest goes to the main menu after 15 s — and when the host quit its battle (its "hostleft" line) —
 * where it joins the host's next battle by itself (NetIdle). Both are told with the game's popup (Notify). */
static ULONGLONG quitAt;

static void WatchOther(void)
{
    static int wasConnected, toldLost;
    static ULONGLONG lostAt;
    ULONGLONG now = GetTickCount64();
    if (me == 2 && hostLeftAt)
    {
        hostLeftAt = 0;
        NetLog("# the host left its battle: to the main menu in a few seconds (the host's next battle joins by itself)");
        Notify("host_left", 0);
        quitAt = now + 6000;
    }
    if (quitAt && now >= quitAt)
    {
        quitAt = 0;
        NetQuitToMenu();
    }
    if (NetTcpState() == NET_TCP_CONNECTED) { wasConnected = 1; lostAt = 0; toldLost = 0; return; }
    if (!wasConnected || toldLost) return;
    if (!lostAt) lostAt = now;
    if (me == 1 && now - lostAt > 5000)
    {
        toldLost = 1;
        if (guestAway) return;
        guestAway = 1;
        NetLog("# the connection to the guest is gone: its crewmates are the host's until it is back");
        Notify("guest_lost", 0);
    }
    if (me == 2 && now - lostAt > 15000)
    {
        toldLost = 1;
        NetLog("# no connection to the host for 15 s: to the main menu in a few seconds (the host's next battle joins by itself)");
        Notify("host_lost", 0);
        quitAt = now + 6000;
    }
}

static ULONG_PTR HookSimUpdate(void *sim, void *arg)
{
    worldTick = GetTickCount64();   /* heartbeat for the input lock: no update = paused, menu, loading */
    if (mode != NET_OFF && Origin() != ORIGIN_PREVIEW)
    {
        frame++;
        mainSim = sim;   /* the one tactics world (previews run on copies) */
        ScanActors(sim);
        /* Again every ~5 s: the host may load the mission after this game did (its new mission resets the wait). */
        static unsigned readyAt;
        /* Not from a battle the host has left: a guest frozen in its old mission said "ready" for the host's next one,
         * which then ran as if the guest were in (2026-11-28, laptop test 5). */
        if (me == 2 && NetTcpState() == NET_TCP_CONNECTED && !HostMovedOn() && (readySentFor != NetTcpReconnects() + 1 || frame - readyAt >= 300) && NetTcpSend("ready"))
        {
            if (readySentFor != NetTcpReconnects() + 1) NetLog("# to host: ready (this game is in the battle)");
            readySentFor = NetTcpReconnects() + 1;
            readyAt = frame;
        }
        if (me && frame % 30 == 0) { ReadCrew(); WriteStatus(sim); }
        if (me && namesDirty && frame % 10 == 0) ApplyNames();
        if (me) WatchOther();
        if (hosting) { HostPump(sim); TurnWatch(sim); }
        Pump(sim);
        if (me && sim == mainSim) { AimWatch(); AimDraw(sim); MoveWatch(); MoveDraw(sim); }

    }
    return realSimUpdate(sim, arg);
}

unsigned long long NetWorldTick(void) { return worldTick; }

/* Fixed time step for the tactics world (netplay test on): the world update 0x480a10(world, game, WorldDt *dt) normally
 * gets the frame time, and projectiles / movement integrate with it — run 8: the same enemy shot at the same RNG state
 * missed in the record and hit in the replay. Real time is accumulated and the world gets whole steps of 1/60 s (up to
 * 4 per frame, a frame without a due step updates with dt 0 like a paused game). The time scale (slow motion, Turbo) is
 * applied inside, after this. The outcome preview runs this function on its copy with its own fixed step: untouched.
 * WorldDt is { float dt; double time; } (the preview passes its copy's game time + 0x26a8 as time; the world hands it on
 * to its simulators): until 2026-11-28 the hook passed a lone float, the time was whatever lay next to it on the stack.
 * Now it is the steps' own time. */
typedef struct { float dt; float pad; double time; } WorldDt;
typedef ULONG_PTR (*WorldUpdate_t)(void *world, void *game, WorldDt *dt);
static WorldUpdate_t realWorldUpdate;
static float stepAcc;
#define RVA_WorldUpdate 0x480a10
#define FIXED_STEP (1.0f / 60.0f)
#define CATCH_UP 3
#define CATCH_UP_ENTRIES 3

/* Poses on the world steps (2026-11-28). Hits are decided per body part (bodyparts.xml: head = crits, shield = no
 * damage; a hat comes off), and the parts' polygons are where the skeleton's animation has them. The game advances the
 * skeletons once per FRAME with the frame time, outside the world update (0xc3ad0: 0x37c100(world + 0x3f8, dt, x) and
 * the skinned character simulator 0x57c4d0(world + 0x388, dt, x)); the outcome preview runs the same two after each of
 * its fixed world steps (0x4551e0 / 0x4551f7). So the same shot of the guest's at the same RNG state, angle, position
 * and facing took 1 HP on the guest and 0 on the host (hat): the target stood in another pose. Netplay: both run after
 * every fixed world step with its dt (x the time scale, as the frame does), the frame's own calls get dt 0. And
 * 0x57c4d0 skips characters outside the camera's view (world + 0x1c34: x, y, w, h — each PC's own camera) until they
 * are back in it: during our calls the view is everything. The frame's x (third argument) is passed on as it was. */
typedef void (*PoseSim_t)(void *sim, float dt, float x);
static PoseSim_t realActorPose, realSkinned;
static char *atActorPoseFrameRet, *atSkinnedFrameRet;
static float frameX = 1.0f;
static int poseSteps;   /* both hooks in: the poses go with the world steps */
#define RVA_ActorPose 0x37c100
#define RVA_Skinned 0x57c4d0
#define RVA_ActorPoseFrameRet 0xc3dec   /* return addresses of the frame's calls in 0xc3ad0 */
#define RVA_SkinnedFrameRet 0xc3e02

static void HookActorPose(void *sim, float dt, float x)
{
    if (poseSteps && mode != NET_OFF && (char *)_ReturnAddress() == atActorPoseFrameRet) dt = 0;
    realActorPose(sim, dt, x);
}

static void HookSkinned(void *sim, float dt, float x)
{
    if (poseSteps && mode != NET_OFF && (char *)_ReturnAddress() == atSkinnedFrameRet) { frameX = x; dt = 0; }
    realSkinned(sim, dt, x);
}

static void PoseStep(void *world, void *game, float step)
{
    if (!poseSteps) return;
    void *pose = NULL, *skin = NULL;
    float *view = NULL, scale = 1;
    __try
    {
        pose = *(void **)((char *)world + 0x3f8);
        skin = *(void **)((char *)world + 0x388);
        scale = *(float *)((char *)game + 0x1b70);
        if (skin) view = (float *)(*(char **)((char *)skin + 0x10) + 0x1c34);
        if (view) { volatile float probe = view[3]; (void)probe; }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    float d = step * scale;
    if (pose) realActorPose(pose, d, frameX);
    if (skin)
    {
        float keep[4];
        memcpy(keep, view, sizeof keep);
        view[0] = view[1] = -1e8f;
        view[2] = view[3] = 2e8f;
        realSkinned(skin, d, frameX);
        memcpy(view, keep, sizeof keep);
    }
}

/* Lockstep (2026-11-28). The fixed steps alone are not enough: between two commands the host's and the guest's worlds
 * ran a different number of steps (waiting for each other, the network, loading) — and with them the idle animations
 * (each starts at a random point of its loop, 0x56529d) and anything else on a timer. So the guest's world runs exactly
 * as many: the host logs its step count (w= on commands and turn ends, "k <steps>" lines every K_EVERY steps); the
 * guest's world is held (updates with dt 0, like a paused game) once it reaches the host's step of the next command
 * — or the host's latest step, if none has come — and Pump gives the command at that step. Behind the host by more
 * than CATCH_UP_STEPS it runs CATCH_UP times as fast, by more than JUMP_STEPS (the host waited for this game to load)
 * it just counts from the host's step. Not held while actions run or a dialogue / camera is on (they need the world,
 * and the host's took as long as it took there), or after HOLD_MAX frames held: a world gone past the host's step
 * counts on from the next command (Pump). Joined from the host's save: counts from the first host step after it. */
#define K_EVERY 6
#define CATCH_UP_STEPS 20
#define JUMP_STEPS 600
#define HOLD_MAX 1200
static unsigned long long kLogged;
static unsigned holdFrames;
static long long releasedCap = -1;   /* held HOLD_MAX frames at this step: not held there again */

static long long StepCap(int stopped)
{
    if (!guest || stopped) return -1;
    if (scriptPos < scriptLen) return script[scriptPos].step;   /* -1 in older records: not held */
    return live ? hostSteps : -1;
}

static ULONG_PTR HookWorldUpdate(void *world, void *game, WorldDt *dt)
{
    if (mode == NET_OFF || !dt || Origin() == ORIGIN_PREVIEW) return realWorldUpdate(world, game, dt);
    float d = dt->dt;
    if (!(d > 0)) d = 0;
    if (d > 0.1f) d = 0.1f;
    /* A live guest well behind the host (joined from the host's save: everything the host did since is replayed; or
     * after a slow spell) runs its world CATCH_UP times as fast until it is back: before, a rejoin replayed the host's
     * turn at normal speed, crewmates walking back from where the save had them (2026-11-28). Same fixed steps, more
     * of them per frame: the battle is the same. */
    int stopped = scenePos >= 0 && scenePos < scenesLen && sceneAt[scenePos] <= scriptPos;   /* the host is in another scene */
    int behind = live && guest && !stopped && scriptLen - scriptPos >= CATCH_UP_ENTRIES;
    long long cap = StepCap(stopped);
    if (cap >= 0 && !stepsAligned && hostStepsFirst >= 0)
    {
        worldSteps = (unsigned long long)hostStepsFirst;
        stepsAligned = 1;
        NetLog("# world steps: joined at the host's step %lld", hostStepsFirst);
    }
    if (!stepsAligned) cap = -1;
    if (cap >= 0 && cap - (long long)worldSteps > JUMP_STEPS && !Busy(mainSim))
    {
        NetLog("# world steps: %lld behind the host, counted from its step %lld", cap - (long long)worldSteps, cap);
        worldSteps = (unsigned long long)cap;
    }
    if (cap >= 0 && cap - (long long)worldSteps >= CATCH_UP_STEPS) behind = 1;
    if (behind) d *= CATCH_UP;
    stepAcc += d;
    ULONG_PTR r = 0;
    int n = 0, held = 0;
    for (; stepAcc >= FIXED_STEP && n < (behind ? 4 * CATCH_UP : 4); n++)
    {
        if (cap >= 0 && (long long)worldSteps >= cap && cap != releasedCap)
        {
            if (holdFrames < HOLD_MAX && !Busy(mainSim) && !CutsceneNow()) { held = 1; break; }
            if (holdFrames >= HOLD_MAX)
            {
                releasedCap = cap;
                NetLog("# world steps: held %d frames at the host's step %lld, going on", HOLD_MAX, cap);
            }
        }
        WorldDt step = *dt;
        step.dt = FIXED_STEP;
        step.time = (double)worldSteps * FIXED_STEP;
        unsigned *g = Rng(), cosmetic[2] = { 0, 0 };
        int swapped = g && worldRngSet;
        if (swapped) { cosmetic[0] = g[0]; cosmetic[1] = g[1]; g[0] = worldRng[0]; g[1] = worldRng[1]; }
        r = realWorldUpdate(world, game, &step);
        if (g)
        {
            worldRng[0] = g[0]; worldRng[1] = g[1]; worldRngSet = 1;
            if (swapped) { g[0] = cosmetic[0]; g[1] = cosmetic[1]; }
        }
        worldSteps++;
        PoseStep(world, game, FIXED_STEP);
        stepAcc -= FIXED_STEP;
    }
    if (held) { stepAcc = 0; holdFrames++; }
    else if (n > 0) holdFrames = 0;
    if (n == 0)
    {
        WorldDt none = *dt;
        none.dt = 0;
        none.time = (double)worldSteps * FIXED_STEP;
        r = realWorldUpdate(world, game, &none);
    }
    if (stepAcc > FIXED_STEP) stepAcc = 0;   /* too slow to keep up: the game slows down instead */
    if (hosting && worldSteps - kLogged >= K_EVERY) { kLogged = worldSteps; NetLog("k %llu", worldSteps); }
    return r;
}

int NetActive(void) { return mode != NET_OFF; }

/* AI order calculation (aisimulator.cpp): RequestOrders 0x449a30 sets state [ai+0x210] = 1 and steps it; the world
 * update steps it every frame (0x4487b0) until it is 0 again. Logged in a record (the steps draw from the shared RNG):
 *   ai <seq> s=<state> a=<actor id> ao=<ordinal> r=<z>:<w> e=<entity counter> f=<frame>
 * The guest's AI is off: its steps are not run (nothing should start one with the enemy turn simulator off). */
static ULONG_PTR HookAiStep(void *ai, void *a2, void *a3, void *a4)
{
    int state = 0, actor = -1;
    __try { state = *(int *)((char *)ai + 0x210); actor = *(int *)((char *)ai + 0x214); }
    __except (EXCEPTION_EXECUTE_HANDLER) { state = 0; }
    if (mode != NET_OFF && state >= 1 && state <= 3 && Origin() != ORIGIN_PREVIEW)
    {
        if (guest)
        {
            static int told;
            if (!told++) NetLog("# ai step (state %d, actor %d) not run: the guest's AI is off", state, actor);
            return 0;
        }
        unsigned *r = Rng();
        NetLog("ai %u s=%d a=%d ao=%d r=%08x:%08x e=%d f=%u", ++seq, state, actor, OrdinalOf(actor), r ? r[0] : 0, r ? r[1] : 0, EntityCount(mainSim), frame);
    }
    return realAiStep(ai, a2, a3, a4);
}

static void HookEnemyTurn(void *ets, void *dt)
{
    if (guest) return;
    realEnemyTurn(ets, dt);
}

/* Spawns (run 8): reinforcements spawned by the mission script / the turn manager draw their unit types from the RNG,
 * a frame or two after the turn end the replay synced — cosmetics use the RNG in between, so the replay spawned other
 * units. SpawnReinforcements 0x4099f0 (Lua TriggerReinforcements, the reinforcement decks, SpawnReinforcementsAction)
 * and the entrance spawn 0x40aae0 (Lua SpawnActorsAtEntrance / SpawnCrewmatesAtEntrance) log "spawn r=<z>:<w>"; the
 * replay sets the recorded RNG state before the n-th spawn. Up to 8 arguments are passed on (the hook body does
 * integer work only before the call, so float arguments in xmm registers reach the original untouched). */
typedef ULONG_PTR (*Spawn_t)(void *, void *, void *, void *, void *, void *, void *, void *);
static Spawn_t realSpawnReinf, realSpawnEntrance;
static unsigned spawnZ[256], spawnW[256];
static int spawnsLen, spawnPos;
#define RVA_SpawnReinf 0x4099f0
#define RVA_SpawnEntrance 0x40aae0

/* Live guest: blocks the game (at most ms) until the host's log has more than `have` of something (scene / spawn
 * lines) — the guest runs behind the host, so it normally is there already. */
static void WaitForHost(const int *len, int have, DWORD ms, const char *what)
{
    if (!live || *len > have) return;
    ULONGLONG until = GetTickCount64() + ms;
    while (*len <= have && GetTickCount64() < until && NetTcpState() != NET_TCP_CLOSED) { ReadMore(); if (*len <= have) Sleep(20); }
    if (*len <= have) NetLog("# DIFF: waited %lu ms for the host's %s %d, going on", ms, what, have + 1);
}

static ULONG_PTR SpawnHook(Spawn_t real, void *a, void *b, void *c, void *d, void *e, void *f, void *g, void *h)
{
    unsigned *r = Rng();
    int missing = 0;
    if (mode == NET_REPLAY && !autoplay && r)
    {
        WaitForHost(&spawnsLen, spawnPos, 20000, "spawn");
        if (spawnPos < spawnsLen) { r[0] = spawnZ[spawnPos]; r[1] = spawnW[spawnPos]; spawnPos++; }
        else missing = 1;
    }
    unsigned z = r ? r[0] : 0, w = r ? r[1] : 0;
    ULONG_PTR result = real(a, b, c, d, e, f, g, h);
    if (mode != NET_OFF)
    {
        if (missing) NetLog("# DIFF: spawn %d not in the record (its RNG not set)", spawnPos + 1);
        NetLog("spawn r=%08x:%08x f=%u", z, w, frame); RngMark();
    }
    return result;
}

static ULONG_PTR HookSpawnReinf(void *a, void *b, void *c, void *d, void *e, void *f, void *g, void *h)
{
    return SpawnHook(realSpawnReinf, a, b, c, d, e, f, g, h);
}

static ULONG_PTR HookSpawnEntrance(void *a, void *b, void *c, void *d, void *e, void *f, void *g, void *h)
{
    return SpawnHook(realSpawnEntrance, a, b, c, d, e, f, g, h);
}

static void *HookSceneCreate(void *scene, void *a2, void *a3, void *a4)
{
    unsigned *r = Rng();
    int inside = 0;   /* guest: joined inside the host's battle */
    if (rejoinPending && live)
    {
        /* The battle of the host's save written at the main menu: the replay starts over at that point of the log. */
        rejoinPending = 0;
        leftMid = 0;
        if (NetTcpRebase(rejoinFrom))
        {
            ResetForJoin(rejoinEnds);
            NetTcpSend("rejoin");
            NetLog("# joining the host's battle from its save (host log byte %lld)", rejoinFrom);
        }
        else NetLog("# ERROR: the replay file doesn't reach back to host log byte %lld: not joined", rejoinFrom);
    }
    if (mode == NET_REPLAY && !autoplay && r)
    {
        /* Loaded from the host's save inside its mission: the host doesn't load a scene, so no line is coming. */
        if (joinedMid && scenePos == 0)
        {
            ULONGLONG until = GetTickCount64() + 2000;   /* a short look (not a DIFF when nothing comes) */
            while (scenesLen == 0 && GetTickCount64() < until) { ReadMore(); if (scenesLen == 0) Sleep(20); }
            if (scenesLen == 0) scenePos = -1;
        }
        /* Only the first scene of the game waits for the host's: later ones are the world map (it is a scene too: a
         * guest back on the map hung on a black screen until the host made one) or battles joined from the host's
         * save (joinedMid, above). */
        else if (scenesMade)
        {
            NetLog("# a scene of this game alone (the world map): not the host's, nothing waited for");
            scenesMade++;
            return realSceneCreate(scene, a2, a3, a4);
        }
        else WaitForHost(&scenesLen, scenePos, 180000, "mission scene");
        if (scenePos == -1) { scenePos = 0; inside = 1; NetLog("# joined inside the host's mission: no scene line, RNG from the commands"); }
        else if (scenePos < scenesLen) { r[0] = sceneZ[scenePos]; r[1] = sceneW[scenePos]; scenePos++; }
        else NetLog("# DIFF: mission scene %d not in the record (its RNG not set)", scenePos + 1);
    }
    if (mode != NET_OFF && r) { NetLog("scene r=%08x:%08x", r[0], r[1]); RngMark(); }
    /* World steps count from the scene, as the host's do; joined inside the host's battle: from its first step read. */
    worldSteps = kLogged = 0;
    holdFrames = 0;
    releasedCap = -1;
    stepsAligned = !inside;
    /* A new mission: the host waits for the guest again (not for one that left), the guest says "ready" again from it. */
    if (hosting && guestIn) { guestIn = 0; NetLog(guestAway ? "# new mission: the guest is away, not waited for" : "# new mission: waiting for the guest to enter the battle"); }
    readySentFor = 0;
    userLeft = 0;
    hostWon = -1;
    wonTold = 0;
    DeleteFileW(wonPath);
    remoteMove.id = -1; remoteMove.want = remoteMove.shown = 0;
    DeleteFileW(movePath);
    destsLen = 0;
    scenesMade++;
    if (hosting && saveAfterCheckpoint == 1) { saveAfterCheckpoint = 2; checkpointSceneAt = GetTickCount64(); }
    return realSceneCreate(scene, a2, a3, a4);
}

static ULONG_PTR HookPopupShow(void *sim, void *name, ULONG_PTR flag, float f)
{
    popupTick = GetTickCount64();
    return realPopupShow(sim, name, flag, f);
}

unsigned long long NetPopupTick(void) { return popupTick; }

static int FromHex(const char *s, unsigned char *out, int max)
{
    int n = 0;
    while (s[0] && s[1] && s[0] != ' ')
    {
        if (n >= max) return -1;
        unsigned v;
        if (sscanf(s, "%2x", &v) != 1) return -1;
        out[n++] = (unsigned char)v;
        s += 2;
    }
    return n;
}

/* Parses one log line into e. Returns the origin (ORIGIN_*) or -1 for lines that are not commands/turn ends.
 * Fields added later (r= on turn ends, e=, d=) are optional, so older records still replay. */
int NetParseLine(const char *line, void *entry)
{
    Entry *e = (Entry *)entry;
    char origin[16];
    unsigned s;
    memset(e, 0, sizeof *e);
    e->ent = e->ao = e->to = -1;
    e->step = -1;
    if (sscanf(line, "end %u o=%15s", &s, origin) == 2)
    {
        const char *r = strstr(line, " r=");
        if (r && sscanf(r + 3, "%x:%x", &e->z, &e->w) != 2) return -1;
        e->kind = 'e';
    }
    else
    {
        if (sscanf(line, "cmd %u o=%15s", &s, origin) != 2) return -1;
        const char *a = strstr(line, " a="), *i = strstr(line, " i="), *n = strstr(line, " n="), *t = strstr(line, " t="),
                   *fl = strstr(line, " fl="), *r = strstr(line, " r="), *d = strstr(line, " d="), *ao = strstr(line, " ao="),
                   *to = strstr(line, " to=");
        if (!a || !i || !n || !t || !fl || !r) return -1;
        e->actor = atoi(a + 3);
        e->index = atoi(i + 3);
        e->ao = ao ? atoi(ao + 4) : -1;
        e->to = to ? atoi(to + 4) : -1;
        if (sscanf(n + 3, "%63s", e->name) != 1 || sscanf(t + 3, "%d", &e->type) != 1) return -1;
        t = strchr(t, ':');
        if (!t) return -1;
        t++;
        e->dataLen = *t == '?' ? -1 : FromHex(t, e->data, sizeof e->data);
        if (e->dataLen < 0) return -1;
        if (e->type == 4) for (int k = 8 + 12; k < e->dataLen; k += 16) memset(e->data + k, 0, 4);   /* padding, see NetTargetBytes */
        e->flag = atoi(fl + 4);
        if (sscanf(r + 3, "%x:%x", &e->z, &e->w) != 2) return -1;
        if (d) e->depth = atoi(d + 3);
        e->kind = 'c';
    }
    const char *ent = strstr(line, " e="), *fr = strstr(line, " f="), *ws = strstr(line, " w=");
    if (ent) e->ent = atoi(ent + 3);
    if (ws) e->step = _strtoi64(ws + 3, NULL, 10);
    if (fr) e->frame = (unsigned)strtoul(fr + 3, NULL, 10);
    for (int o = 0; o <= ORIGIN_REPLAY; o++)
        if (strcmp(origin, NetOriginName(o)) == 0) return o;
    return ORIGIN_OTHER;
}

size_t NetEntrySize(void) { return sizeof(Entry); }

static int Append(Entry ***list, int *len, const Entry *e)
{
    Entry *copy = (Entry *)malloc(sizeof(Entry));
    Entry **grown = (Entry **)realloc(*list, (*len + 1) * sizeof(Entry *));
    if (!copy || !grown) { free(copy); return 0; }
    *copy = *e;
    *list = grown;
    (*list)[(*len)++] = copy;
    return 1;
}

static wchar_t hostInfo[160];   /* live: "host:port" for the log */

static long long scriptOffset;  /* bytes of the replay file already read */
static int statesRecorded, scriptCap, aiCommands;

static void ParseScriptLine(const char *line)
{
    static Entry tmp;
    if (strncmp(line, "state ", 6) == 0) { statesRecorded++; return; }
    if (strncmp(line, "await guest", 11) == 0) { awaitsArrived++; return; }
    if (strncmp(line, "ords", 4) == 0 && (line[4] == ' ' || line[4] == 0))
    {
        if (!joinedMid || ordSeedLen >= 0) return;   /* only the first one, and only for a guest joining inside */
        ordSeedLen = 0;
        for (const char *p = line + 4; *p && ordSeedLen < MAX_ACTORS;)
        {
            char *end;
            long v = strtol(p, &end, 10);
            if (end == p) break;
            ordSeed[ordSeedLen++] = (int)v;
            p = end;
        }
        return;
    }
    if (strncmp(line, "name host ", 10) == 0 || strncmp(line, "name guest ", 11) == 0)
    {
        char n[64];
        int isHost = line[5] == 'h';
        strncpy(n, line + (isHost ? 10 : 11), 63);
        n[63] = 0;
        n[strcspn(n, "\r\n")] = 0;
        SetName(isHost ? hostName : guestName, n);
        if (isHost && guestSlotPath[0]) WriteCoopSlot();   /* the main menu marks the co-op slot with this nick */
        return;
    }
    unsigned z, w;
    if (sscanf(line, "spawn r=%x:%x", &z, &w) == 2)
    {
        if (spawnsLen < 256) { spawnZ[spawnsLen] = z; spawnW[spawnsLen] = w; spawnsLen++; }
        return;
    }
    if (sscanf(line, "scene r=%x:%x", &z, &w) == 2)
    {
        if (scenesLen < 16) { sceneZ[scenesLen] = z; sceneW[scenesLen] = w; sceneAt[scenesLen] = scriptLen; sceneCheckpoint[scenesLen] = checkpointLine; scenesLen++; }
        checkpointLine = 0;
        return;
    }
    if (strncmp(line, "checkpoint", 10) == 0 && (line[10] == '\n' || line[10] == '\r' || !line[10]))
    {
        checkpointLine = NetTcpBase() + scriptOffset;
        return;
    }
    int refusedAo;
    char why[16];
    if (sscanf(line, "refused ao=%d %15s", &refusedAo, why) == 2)
    {
        /* The host didn't play our command: stop waiting for it, say why over the crewmate. */
        if (pending > 0) pending--;
        char name[32];
        sprintf(name, "refused_%s", why);
        if (strcmp(why, "chain") != 0) Notify(name, IdOfOrdinal(refusedAo, -1));   /* the rest of a refused frame: said already */
        return;
    }
    if (line[0] == 'k' && line[1] == ' ')
    {
        hostSteps = _strtoi64(line + 2, NULL, 10);   /* the host's world step (only a hosting game writes these) */
        if (hostStepsFirst < 0) hostStepsFirst = hostSteps;
        return;
    }
    if (strncmp(line, "aim ", 4) == 0)
    {
        if (live) AimFromLine(line + 4);   /* the host's aim (a file replay: ignored) */
        return;
    }
    if (strncmp(line, "move ", 5) == 0)
    {
        if (live) MoveFromLine(line + 5);   /* the host's move target */
        return;
    }
    if (strncmp(line, "hostleft", 8) == 0)
    {
        /* The host quit its battle to the main menu: this battle is over too (HookSimUpdate goes to the menu). */
        hostLeftAt = GetTickCount64();
        return;
    }
    if (strncmp(line, "won", 3) == 0 && (!line[3] || line[3] == '\r' || line[3] == '\n'))
    {
        /* The host's battle is won (its mission Lua, OnMissionVictory): this one is won too once it has played what
         * came before (FollowVictory) — a guest whose own mission script didn't end it went into an enemy turn no one
         * plays and froze there (2026-11-28, laptop test 5). */
        if (hostWon < 0) { hostWon = scriptLen; hostWonAt = GetTickCount64(); }
        return;
    }
    int o = NetParseLine(line, &tmp);
    if (o >= 0 && tmp.step >= 0 && o != ORIGIN_PREVIEW)
    {
        hostSteps = tmp.step;
        if (hostStepsFirst < 0) hostStepsFirst = tmp.step;
    }
    if (o >= 0 && tmp.kind == 'e') endsArrived++;
    if (o >= 0 && tmp.kind == 'c' && pending && strstr(line, " by=guest")) pending--;   /* ours, back from the host */
    if (o < 0 || o == ORIGIN_PREVIEW || (tmp.kind == 'c' && tmp.depth > 0)) return;   /* nested = a consequence */
    if (o == ORIGIN_REPLAY) o = ORIGIN_PLAYER;
    if (tmp.kind == 'c' && o == ORIGIN_LUA) luaRecordedLen++;
    /* Autoplay plays the player's decisions only; the guest everything the host decided: the player's and the AI's
     * commands and every turn end. Script commands and consequences follow by themselves. */
    int take = tmp.kind == 'e' ? (o == ORIGIN_PLAYER || guest) : (o == ORIGIN_PLAYER || (guest && o == ORIGIN_AI));
    tmp.origin = o;
    tmp.byGuest = strstr(line, " by=guest") != NULL;
    tmp.statesBefore = statesRecorded;
    tmp.luaBefore = luaRecordedLen;
    tmp.endsBefore = endsRecordedLen;
    if (tmp.kind == 'e' && o != ORIGIN_PLAYER) endsRecordedLen++;
    if (!take) return;
    if (tmp.kind == 'c' && o == ORIGIN_AI) aiCommands++;
    if (scriptLen == scriptCap)
    {
        int cap = scriptCap ? scriptCap * 2 : 64;
        Entry *grown = (Entry *)realloc(script, cap * sizeof(Entry));
        if (!grown) return;
        script = grown;
        scriptCap = cap;
    }
    script[scriptLen++] = tmp;
}

/* Reads the complete lines added to the replay file since the last call (a file replay: all of it at once; live: what
 * the network thread appended meanwhile). */
static void ReadMore(void)
{
    static char line[2 * (MAX_VEC_BYTES + 8) + 512];
    FILE *f = _wfopen(replayPath, L"rb");
    if (!f) return;
    if (_fseeki64(f, scriptOffset, SEEK_SET) == 0)
        while (fgets(line, sizeof line, f))
        {
            size_t n = strlen(line);
            if (n == 0 || line[n - 1] != '\n') break;   /* still being written: next time */
            scriptOffset += (long long)n;
            ParseScriptLine(line);
        }
    fclose(f);
}

static void LoadScript(void)
{
    if (live)
    {
        NetLog("# replay: live from the host %ls, AI off", hostInfo);
        return;
    }
    FILE *f = _wfopen(replayPath, L"r");
    if (!f) { NetLog("# ERROR: no replay file (copy swh2_net_record.log to swh2_net_replay.log)"); mode = NET_RECORD; guest = 0; return; }
    fclose(f);
    ReadMore();
    NetLog("# replay: %d entries (%d ai commands), %d other turn ends, %d scenes, %d spawns%s", scriptLen, aiCommands,
           endsRecordedLen, scenesLen, spawnsLen, guest ? ", AI off" : "");
}

/* Guest: the replay starts over at the rebased replay file, as a guest that joined inside the host's battle. */
static void ResetForJoin(int ends)
{
    scriptLen = scriptPos = 0;
    scriptOffset = 0;
    statesRecorded = aiCommands = 0;
    luaRecordedLen = endsRecordedLen = 0;
    endsSeen = luaSeen = idle = gateWait = 0;
    statesSynced = syncWanted = syncWait = 0;
    gatePassed = clearedFor = 0;
    scenesLen = scenePos = 0;
    checkpointLine = 0;
    spawnsLen = spawnPos = 0;
    earlyEnds = pending = 0;
    endAskedFor = -1;
    endsArrived = ends;
    ordSeedLen = -1;
    ordSeeded = 0;
    ordSim = NULL;
    readySentFor = 0;
    joinedMid = 1;
    hostSteps = hostStepsFirst = -1;
    stepsAligned = 0;
    DeleteFileW(syncPath);
    DeleteFileW(syncedPath);
}

/* ---- the next battle without a restart (guest) ----
 * Quit to the main menu: the pause menu's "Yes" (0x1c1660) fires the signal at +0x428 of the running level, the battle
 * (GameState+0xc0) or the world map (GameState+0xe8); both connect it to GameState 0xcd030 (fade out, new
 * MainMenuState). GameState::Update 0xc2080 runs every frame in the game ([GameState+0x110]: 0-1 battle, 2-3 map) and
 * fires it when asked (NetQuitToMenu), after its own update, as the button does from the UI.
 * Continue: the profile panel's Continue (0x5f2820) on a slot in use = SetCurrentProfile 0x561d70(slot) + LoadingGame
 * 0x5eebe0(MainMenuState). The game reads its slots once (0x561ae0, no arguments, sets 0xea19c1 = loaded): the main
 * menu's update (MainMenuState vtable 0xbb5588 slot 4, 0x5ee040) writes the host's save into the guest's slot, reads
 * the slots again and presses Continue on it a moment later; its battle rebases the replay (HookSceneCreate).
 * When: while the guest is not in a battle (after its first one), NetIdle fetches the host's save every few seconds;
 * one from a newer battle of the host is kept and the guest goes to the main menu by itself. A guest that left inside
 * the host's battle (pause, Quit; HookQuit) takes the host's latest save of that battle too. */
typedef ULONG_PTR (*GameUpdate_t)(void *game, void *dt);
typedef ULONG_PTR (*Quit_t)(void *game, void *a2, void *a3, void *a4);
typedef ULONG_PTR (*MenuUpdate_t)(void *menu, void *dt);
typedef void (*FireSignal_t)(void *signal);
typedef void (*SetProfile_t)(int slot);
typedef unsigned char (*LoadingGame_t)(void *menu);
typedef void (*LoadSlots_t)(void);
static GameUpdate_t realGameUpdate;
static Quit_t realQuit;
static MenuUpdate_t realMenuUpdate;
#define RVA_GameUpdate 0xc2080
#define RVA_Quit 0xcd030
#define RVA_MenuUpdate 0x5ee040
#define RVA_FireSignal 0xcfb10
#define RVA_SetProfile 0x561d70
#define RVA_LoadingGame 0x5eebe0
#define RVA_LoadSlots 0x561ae0
#define RVA_SlotsLoaded 0xea19c1
static int quitWanted, quitFired;
static ULONGLONG menuSince, continueAt;

/* ---- checkpoint loads ----
 * The pause menu's / failure popup's "Load checkpoint" = GameState::LoadRestorePoint 0xc7410(GameState): a new GameState
 * in mode 3 (restore point; +0xb0, constructor 0xc1390) loads the battle again from the last in-mission save in memory
 * (0xc64e0), and its scene is made anew. That writes no save, so the guest had nothing to follow (it stopped at the
 * host's new scene line and the host waited for it). Now the host logs "checkpoint" and asks for a save once its battle
 * runs again: GameState::Update saves when [GameState+0xb4] is set and it is safe (0xc2124 -> 0xcc360 -> 0xccc00).
 * The guest, at the scene line after "checkpoint", quits to the main menu and joins from the host's first save written
 * after that line. A guest's own checkpoint load would leave the host's battle: it rejoins the host instead. */
typedef void (*Checkpoint_t)(void *game);
static Checkpoint_t realCheckpoint;
#define RVA_Checkpoint 0xc7410
static int checkpointWanted;

static void HookCheckpoint(void *game)
{
    if (me == 2 && live)
    {
        NetLog("# the guest loaded a checkpoint: the host's battle goes on — rejoining it instead");
        NetQuitToMenu();
        return;
    }
    NetLog("checkpoint");
    if (hosting) saveAfterCheckpoint = 1;
    realCheckpoint(game);
}

void NetLoadCheckpoint(void) { checkpointWanted = 1; }

int NetLogHas(const char *text)
{
    if (!outPath[0]) return 0;
    HANDLE f = CreateFileW(outPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER size;
    int found = 0;
    if (GetFileSizeEx(f, &size) && size.QuadPart > 0 && size.QuadPart < 64 * 1024 * 1024)
    {
        char *buf = (char *)malloc((size_t)size.QuadPart + 1);
        DWORD got = 0;
        if (buf && ReadFile(f, buf, (DWORD)size.QuadPart, &got, NULL))
        {
            buf[got] = 0;
            found = strstr(buf, text) != NULL;
        }
        free(buf);
    }
    CloseHandle(f);
    return found;
}

void NetQuitToMenu(void)
{
    if (!realGameUpdate) { NetLog("# ERROR: quit to the main menu: hook not installed"); return; }
    quitWanted = 1;
    quitFired = 0;
    NetLog("# asked to quit to the main menu");
}

static ULONG_PTR HookGameUpdate(void *game, void *dt)
{
    ULONG_PTR r = realGameUpdate(game, dt);
    menuSince = 0;
    int battle = *(int *)((char *)game + 0x110) <= 1, ticking = worldTick && GetTickCount64() - worldTick < 1000;
    if (saveAfterCheckpoint == 2 && battle && ticking && worldTick > checkpointSceneAt)
    {
        saveAfterCheckpoint = 0;
        *((unsigned char *)game + 0xb4) = 1;
        NetLog("# host: battle running again after the checkpoint: save asked for (the guest joins from it)");
    }
    if (checkpointWanted && battle && ticking)
    {
        checkpointWanted = 0;
        DWORD cp = ExeRva(RVA_Checkpoint);
        if (cp && realCheckpoint) ((Checkpoint_t)(base + cp))(game);
        else NetLog("# ERROR: checkpoint: hook not installed");
    }
    DWORD fire = ExeRva(RVA_FireSignal);
    if (quitWanted && !quitFired && fire)
    {
        char *g = (char *)game;
        int level = *(int *)(g + 0x110);
        char *obj = level >= 0 && level <= 3 ? *(char **)(g + (level >= 2 ? 0xe8 : 0xc0)) : NULL;
        static ULONGLONG told;
        if (!obj && GetTickCount64() - told > 2000)
        {
            told = GetTickCount64();
            NetLog("# quit waits: game state level %d, battle %p, map %p", level, *(void **)(g + 0xc0), *(void **)(g + 0xe8));
        }
        if (obj)
        {
            quitFired = 1;
            NetLog("# quit to the main menu from the %s", level >= 2 ? "world map" : "battle");
            ((FireSignal_t)(base + fire))(obj + 0x428);
        }
    }
    return r;
}

/* The player quit to the main menu (pause, Quit — or the dll asked for it: quitWanted). A guest's own quit (to rejoin
 * or follow the host) comes back by itself; one its player chose is told to the host, and the guest's slot gets the
 * host's save of the battle without Continue being pressed: the player's own Continue there joins the host again. A
 * host's quit from its battle sends the guest to the main menu too ("hostleft"). */
static ULONG_PTR HookQuit(void *game, void *a2, void *a3, void *a4)
{
    int ours = quitWanted, inBattle = worldTick && GetTickCount64() - worldTick < 2000;
    quitWanted = 0;
    if (me == 2 && inBattle)
    {
        leftMid = 1;
        userLeft = !ours;
        if (userLeft) NetTcpSend("left");
        NetLog(userLeft ? "# guest: the player left the battle for the main menu: Continue there joins the host's battle again"
                        : "# guest: left the battle for the main menu: Continue there joins the host's battle again");
    }
    if (me == 1 && inBattle && !ours) NetLog("hostleft");
    return realQuit(game, a2, a3, a4);
}

/* The guest's slot index (0-based) from its file name savegame_NNN.dat, -1 = not one. */
static int GuestSlotIndex(void)
{
    const wchar_t *p = wcsstr(guestSlotPath, L"savegame_");
    int n = -1;
    return p && swscanf(p + 9, L"%d", &n) == 1 ? n : -1;
}

/* Before the host's save first goes into this game's slot: the slot's old file into a save bank of the loader
 * (%APPDATA%\SWH2ModLoader\SaveBanks\<account>\auto coop <time>\savegame.dat + bank.txt, SaveBanks.cs format) —
 * a guest that came straight from Steam's "Join game" (no loader) may have its own campaign there. Once per game. */
static void BankGuestSlot(void)
{
    static int done;
    if (done) return;
    done = 1;
    if (GetFileAttributesW(guestSlotPath) == INVALID_FILE_ATTRIBUTES) return;
    const wchar_t *save = wcsstr(guestSlotPath, L"\\Save000\\");
    const wchar_t *acc = save;
    while (acc && acc > guestSlotPath && acc[-1] != L'\\') acc--;
    wchar_t account[64] = L"gog", dir[MAX_PATH], file[MAX_PATH];
    if (acc && save - acc > 0 && save - acc < 60 && iswdigit(*acc)) { wcsncpy(account, acc, save - acc); account[save - acc] = 0; }
    SYSTEMTIME t;
    GetLocalTime(&t);
    DWORD n = GetEnvironmentVariableW(L"APPDATA", dir, MAX_PATH);
    if (!n || n > MAX_PATH - 120) return;
    wcscat(dir, L"\\SWH2ModLoader");
    CreateDirectoryW(dir, NULL);
    wcscat(dir, L"\\SaveBanks");
    CreateDirectoryW(dir, NULL);
    _snwprintf(dir + wcslen(dir), MAX_PATH - wcslen(dir), L"\\%s", account);
    CreateDirectoryW(dir, NULL);
    _snwprintf(dir + wcslen(dir), MAX_PATH - wcslen(dir), L"\\auto coop %04d-%02d-%02d %02d-%02d-%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    if (!CreateDirectoryW(dir, NULL)) return;
    _snwprintf(file, MAX_PATH, L"%s\\savegame.dat", dir);
    int ok = CopyFileW(guestSlotPath, file, FALSE);
    wchar_t backup[MAX_PATH + 8];
    _snwprintf(backup, MAX_PATH + 8, L"%s.backup", guestSlotPath);
    _snwprintf(file, MAX_PATH, L"%s\\savegame.dat.backup", dir);
    CopyFileW(backup, file, FALSE);
    _snwprintf(file, MAX_PATH, L"%s\\bank.txt", dir);
    FILE *f = _wfopen(file, L"w");
    if (f)
    {
        SYSTEMTIME u;
        GetSystemTime(&u);
        fprintf(f, "slot=%d\nsaved=%04d-%02d-%02dT%02d:%02d:%02d.0000000Z\n", GuestSlotIndex(), u.wYear, u.wMonth, u.wDay, u.wHour, u.wMinute, u.wSecond);
        fclose(f);
    }
    NetLog(ok ? "# main menu: the old file of slot %d kept in the loader's save banks (auto coop ...)" : "# ERROR: main menu: slot %d could not be banked", GuestSlotIndex() + 1);
}

static ULONG_PTR HookMenuUpdate(void *menu, void *dt)
{
    ULONG_PTR r = realMenuUpdate(menu, dt);
    ULONGLONG now = GetTickCount64();
    if (!menuSince) menuSince = now;
    quitWanted = 0;
    DWORD flag = ExeRva(RVA_SlotsLoaded), load = ExeRva(RVA_LoadSlots);
    /* Once the game has read its slots itself (the menu's own job), a moment into the menu. */
    if (nextSave && guestSlotPath[0] && flag && load && base[flag] && now - menuSince > 1000)
    {
        BankGuestSlot();
        WriteCoopSlot();
        FILE *f = _wfopen(guestSlotPath, L"wb");
        int ok = f && fwrite(nextSave, 1, nextSaveLen, f) == (size_t)nextSaveLen;
        if (f) fclose(f);
        if (ok)
        {
            wchar_t backup[MAX_PATH + 8];
            _snwprintf(backup, MAX_PATH + 8, L"%s.backup", guestSlotPath);
            DeleteFileW(backup);
            ((LoadSlots_t)(base + load))();
            rejoinFrom = nextFrom;
            rejoinEnds = nextEnds;
            rejoinPending = 1;
            continueAt = userLeft ? 0 : now + 1500;
            NetLog(userLeft ? "# main menu: the host's save from its battle (host log byte %lld) is in this game's slot %d: Continue there joins it"
                            : "# main menu: the host's save from its battle (host log byte %lld) is in this game's slot %d", nextFrom, GuestSlotIndex() + 1);
        }
        else NetLog("# ERROR: main menu: the host's save could not be written to the slot");
        free(nextSave);
        nextSave = NULL;
    }
    /* Game start of a guest whose loader put the host's save into its slot: Continue on that slot by itself, as for
     * the next battles (the menu's update, not its entry: LoadingGame there gave a black screen, EXE_NOTES). */
    static int startContinued;
    if (loaderSlot && !startContinued && !worldTick && flag && base[flag] && now - menuSince > 1000)
    {
        startContinued = 1;
        continueAt = now + 1500;
        NetLog("# main menu: game start with the host's save in slot %d: Continue by itself", GuestSlotIndex() + 1);
    }
    if (continueAt && now >= continueAt)
    {
        continueAt = 0;
        unsigned char *m = (unsigned char *)menu;
        int slot = GuestSlotIndex();
        DWORD set = ExeRva(RVA_SetProfile), go = ExeRva(RVA_LoadingGame);
        if (slot < 0 || !set || !go) NetLog("# ERROR: main menu: no slot / game addresses for Continue: Continue by hand");
        else if (m[0xa0] || m[0xa2]) NetLog("# main menu: a game is loading already");
        else
        {
            ((SetProfile_t)(base + set))(slot);
            NetLog(((LoadingGame_t)(base + go))(menu) ? "# main menu: Continue on slot %d (the host's battle)"
                                                      : "# ERROR: main menu: slot %d can't be loaded: Continue by hand", slot + 1);
        }
    }
    return r;
}

/* Steam's "Join game" / an invite accepted while this game runs: the network game is set up at game start, so the game
 * starts again through Steam with "+connect_lobby <id>" (SteamNetLaunchLobby) — only outside a battle, never while
 * hosting. */
static void SteamJoinWatch(void)
{
    static ULONGLONG last;
    ULONGLONG now = GetTickCount64();
    if (now - last < 250) return;
    last = now;
    unsigned long long lobby = SteamNetJoinRequest();
    if (!lobby) return;
    if (me == 1) { NetLog("# Steam: a join request (lobby %llu) while hosting: ignored", lobby); return; }
    if (worldTick && now - worldTick < 5000) { NetLog("# Steam: a join request (lobby %llu) during a battle: ignored, leave the battle and join again", lobby); return; }
    wchar_t cmd[400];
    _snwprintf(cmd, 400, L"cmd.exe /c ping -n 4 127.0.0.1 >nul & start \"\" \"steam://run/2396240//+connect_lobby %llu/\"", lobby);
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) { NetLog("# ERROR: Steam: the game could not be started again to join"); return; }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    NetLog("# Steam: joining lobby %llu: the game starts again", lobby);
    ExitProcess(0);
}

static int loaderSave;   /* guest: the loader put the host's save into the slot before the game started (session "from") */

#ifndef SWH2COOP_SRC
#define SWH2COOP_SRC "unknown"
#endif
/* Steam guest (no loader, so no loader check): the host's lobby data "info" has "dll=src:<id>" of its Coop dll;
 * another id = another version of the mod: logged, and the game's popup in the battle (Notify "version"). */
static void SteamVersionCheck(void)
{
    static int done, popupDue;
    static ULONGLONG inBattleSince;
    if (popupDue)
    {
        /* The popup shows only for an event that comes after the battle's first status read (coop.lua netEvent). */
        ULONGLONG now = GetTickCount64();
        if (!worldTick || now - worldTick > 1000) { inBattleSince = 0; return; }
        if (!inBattleSince) inBattleSince = now;
        if (now - inBattleSince > 5000) { popupDue = 0; Notify("version", 0); }
        return;
    }
    if (done || steamMode != 2) return;
    const char *info = SteamNetLobbyInfo(), *p = strstr(info, "dll=src:");
    if (!info[0]) return;
    done = 1;
    char host[40] = "";
    if (p) sscanf(p + 8, "%39s", host);
    if (!host[0]) { NetLog("# Steam: the host's lobby has no dll id (%.80s): versions not compared", info); return; }
    if (strcmp(host, SWH2COOP_SRC) == 0) { NetLog("# Steam: same Coop dll as the host's (src %s)", host); return; }
    NetLog("# ERROR: Steam: the host's Coop dll is another version (src %s, this one %s): the battle may go differently", host, SWH2COOP_SRC);
    popupDue = 1;
}

void NetIdle(void)
{
    if (me != 1 && SteamNetAvailable()) SteamJoinWatch();
    if (me == 2) SteamVersionCheck();
    if (me != 2 || !live || !guestSlotPath[0]) return;
    static ULONGLONG last, lastFetch;
    static int wasInBattle;
    ULONGLONG now = GetTickCount64();
    if (now - last < 500) return;
    last = now;
    static int idleLogged;
    if (worldTick && now - worldTick < 3000) { wasInBattle = 1; idleLogged = 0; return; }
    /* Before the first battle the loader put the save there already — unless the game came from Steam's "Join game". */
    if ((!wasInBattle && loaderSave) || rejoinPending) return;
    if (!idleLogged) { idleLogged = 1; NetLog("# guest: out of the battle: asking the host for its save every 3 s"); }
    char *data;
    int len, ends, battle;
    long long from;
    int took = NetTcpFetchTake(&data, &len, &from, &ends, &battle);
    static long long loggedFrom = -2;
    if (took == -1 && loggedFrom != -1) { loggedFrom = -1; NetLog("# guest: the host's save could not be fetched (%s)", NetTcpFetchError()); }
    if (took == 1)
    {
        long long seen = NetTcpBase() + scriptOffset;   /* host log bytes this guest has played */
        if (from != loggedFrom)
        {
            loggedFrom = from;
            NetLog("# guest: the host's save: %d bytes, host log byte %lld, %s (this guest played to %lld)", len, from, battle ? "battle" : "map", seen);
        }
        if (battle && from > nextFrom && from >= needFrom && (from > seen || leftMid))
        {
            needFrom = 0;
            free(nextSave);
            nextSave = data;
            nextSaveLen = len;
            nextFrom = from;
            nextEnds = ends;
            if (leftMid) NetLog("# the host's save from its battle (host log byte %lld): Continue joins it", from);
            else
            {
                NetLog("# the host is in its next battle (its save at host log byte %lld): to the main menu, Continue joins it", from);
                if (!menuSince) NetQuitToMenu();
            }
        }
        else free(data);
    }
    if (now - lastFetch >= 3000) { lastFetch = now; NetTcpFetchStart(); }
}

/* Cosmetic RNG (2026-11-28). The game has one RNG; particles, sounds, music and the HUD draw from it inside the world
 * steps too, as often as the screen / camera / audio of that PC asks — the host's and the guest's games drew different
 * numbers of times while a shot flew, and the gameplay draws after them (crit rolls, damagesimulator 0x3e89c0) got
 * different values: the guest's shot hit, the host's missed. Found with a guard page on the RNG state (writers per world
 * step, host vs guest): particles 0x797a00/0x7a0530/0x7934d0..., sound 0x6f46b0 (audiocues) / 0x80c0a0 (oalsoundplayer),
 * HUD 0x61..., entity effects 0x122800. Their loads of the RNG pointer ("mov reg, [rip + -> 0xea1b98]") are
 * pointed at a cell of ours that holds a second RNG state; gameplay keeps the game's. Netplay sessions only.
 * The two of the animation simulator (0x564bc0: random start point of an animation; 0x563630: seed of a new
 * animation component) were cosmetic too until the poses turned out to decide hits: they get a third state (animRng). */
static const DWORD cosmeticSites[] = {
    0x122ade, 0x3fb275, 0x615842, 0x615cdf, 0x617e96, 0x61a8df, 0x61e89e, 0x6f4a3c, 0x6f4b0c,
    0x793503, 0x7974c7, 0x79753f, 0x7975aa, 0x797660, 0x79766e, 0x797748, 0x797b75, 0x798151, 0x7a0564, 0x80c24b };
/* The animation simulator's two (animRng, RngMark): a third RNG state in the same cell. */
static const DWORD animSites[] = { 0x563700, 0x56529d };

/* Points the RNG pointer load at steamRva ("mov reg, [rip + -> RVA_RngPtr]") at slot (within +-2 GB of the code). */
static int RedirectRng(DWORD steamRva, DWORD rngRva, unsigned char *slot)
{
    DWORD rva = ExeRva(steamRva);
    if (!rva) return 0;
    unsigned char *at = (unsigned char *)base + rva;
    if (!((at[0] == 0x48 || at[0] == 0x4c) && at[1] == 0x8b && (at[2] & 0xc7) == 0x05)) return 0;
    LONGLONG target = (LONGLONG)(rva + 7) + *(int *)(at + 3);
    if (target != (LONGLONG)rngRva) return 0;
    LONGLONG disp = (LONGLONG)(slot - (at + 7));
    if (disp < INT_MIN || disp > INT_MAX) return 0;
    DWORD old;
    if (!VirtualProtect(at + 3, 4, PAGE_EXECUTE_READWRITE, &old)) return 0;
    *(int *)(at + 3) = (int)disp;
    VirtualProtect(at + 3, 4, old, &old);
    return 1;
}

static void CosmeticRng(void)
{
    DWORD rngRva = ExeRva(RVA_RngPtr);
    if (!rngRva) return;
    unsigned char *cell = NULL;
    /* within +-2 GB of the code: rip-relative */
    for (ULONG_PTR a = ((ULONG_PTR)base - 0x10000) & ~(ULONG_PTR)0xffff; a > (ULONG_PTR)base - 0x70000000 && !cell; a -= 0x10000)
        cell = (unsigned char *)VirtualAlloc((void *)a, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!cell) { NetLog("# ERROR: cosmetic RNG: no memory near the exe"); return; }
    unsigned *state = (unsigned *)(cell + 16), *game = Rng();
    state[0] = game ? game[0] ^ 0x5bd1e995u : 0x12345678u;
    state[1] = game ? game[1] ^ 0x1b873593u : 0x9abcdef0u;
    if (!state[0]) state[0] = 1;
    if (!state[1]) state[1] = 1;
    *(unsigned **)cell = state;
    unsigned *anim = (unsigned *)(cell + 0x110);
    anim[0] = state[0] ^ 0x2545f491u;
    anim[1] = state[1] ^ 0x9e3779b9u;
    *(unsigned **)(cell + 0x100) = anim;
    int done = 0, n = (int)(sizeof cosmeticSites / sizeof cosmeticSites[0]);
    for (int i = 0; i < n; i++) done += RedirectRng(cosmeticSites[i], rngRva, cell);
    int animDone = 0, animN = (int)(sizeof animSites / sizeof animSites[0]);
    for (int i = 0; i < animN; i++) animDone += RedirectRng(animSites[i], rngRva, cell + 0x100);
    if (animDone == animN) animRng = anim;   /* else: the ones redirected draw from it unsynced, as cosmetics */
    FlushInstructionCache(GetCurrentProcess(), NULL, 0);
    NetLog(done == n ? "# cosmetic RNG: %d of %d places (particles, sound, HUD) draw from their own RNG" : "# ERROR: cosmetic RNG: only %d of %d places", done, n);
    NetLog(animDone == animN ? "# animation RNG: %d of %d places, set with the world RNG" : "# ERROR: animation RNG: only %d of %d places", animDone, animN);
}

/* Inline hook: the first n bytes of the function (whole instructions, position-independent, n >= 14) are checked,
 * copied into a trampoline that jumps back, and replaced by jmp [rip+0] <hook>. steamRva: see ExeRva. */
/* ---- main menu: the co-op slot (2026-11-28) ----
 * The slot buttons (UI/ui.me template main_menu_profile_slot) are filled by 0x5f1610(menu, ProfileSlot *slot, int i)
 * (the tail of 0x5f12d0, which makes the button): the button = [[menu + 0x40] + 0x3c8][i] (16-byte entries, widget
 * first), its label "name" (child 0x7536b0, __RTDynamicCast 0xa08489 Widget 0xe6cd90 -> Label 0xe6cdb8) gets the
 * header "<captain> - <place>" through Label::SetText 0x747ff0(label, string_view *) (current text: std::string at
 * [label + 0x570] + 0x28); slot state [slot] 0 = empty. The guest's dll notes the slot the host's save went into and
 * the host's nick, the host the slot it shares and the guest's nick (swh2_coop_slot.txt "<slot index> <nick>"), and the
 * hook makes the header "Co-op: <nick>" (ASCII: the menu font) in every start of the game. Seen on the Steam copy
 * 2026-11-28 (slot list after Start). What the slot did goes to swh2_coop_slot.txt.log. */
#define RVA_SlotFill 0x5f1610
#define RVA_LabelSetText 0x747ff0
#define RVA_DynCast 0xa08489
#define RVA_RttiWidget 0xe6cd90
#define RVA_RttiLabel 0xe6cdb8
typedef void (*SlotFill_t)(void *menu, void *slot, int index);
typedef void *(*DynCast_t)(void *p, long delta, void *src, void *dst, int ref);
typedef void (*LabelSetText_t)(void *label, View *text);
static SlotFill_t realSlotFill;
static wchar_t coopSlotPath[MAX_PATH];

/* The co-op slot of this game and the other player's nick: the guest's = where the host's save went; the host's = the
 * slot whose save it shares (session "save" line), marked once a guest said hello. */
static int hostSlotIndex = -1;
static void WriteCoopSlot(void)
{
    int slot = me == 2 ? GuestSlotIndex() : me == 1 ? hostSlotIndex : -1;
    const char *nick = me == 2 ? hostName : guestName;
    if (slot < 0 || !coopSlotPath[0]) return;
    FILE *f = _wfopen(coopSlotPath, L"wb");
    if (!f) return;
    fprintf(f, "%d %s\n", slot, nick[0] ? nick : "?");
    fclose(f);
}

/* What the slot mark did, once per slot and start of the game (it is in the main menu, where no net log runs). */
static void SlotLog(const char *fmt, ...)
{
    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%s.log", coopSlotPath);
    FILE *f = _wfopen(path, L"a");
    if (!f) return;
    va_list ap;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fputc('\n', f);
    fclose(f);
}

static void HookSlotFill(void *menu, void *slot, int index)
{
    realSlotFill(menu, slot, index);
    char line[160] = "", nick[96] = "";
    int at = -1;
    FILE *f = _wfopen(coopSlotPath, L"rb");
    if (f) { if (!fgets(line, sizeof line, f)) line[0] = 0; fclose(f); }
    if (sscanf(line, "%d %95[^\r\n]", &at, nick) != 2 || at != index) return;
    static int logged[16];
    int log = index >= 0 && index < 16 && !logged[index]++;
    DWORD find = ExeRva(RVA_FindChild), cast = ExeRva(RVA_DynCast), set = ExeRva(RVA_LabelSetText),
          rw = ExeRva(RVA_RttiWidget), rl = ExeRva(RVA_RttiLabel);
    if (!find || !cast || !set || !rw || !rl) { if (log) SlotLog("slot %d: an address is not mapped", index); return; }
    __try
    {
        if (*(int *)slot == 0) { if (log) SlotLog("slot %d: empty", index); return; }
        char *list = *(char **)((char *)menu + 0x40);
        char *first = *(char **)(list + 0x3c8), *last = *(char **)(list + 0x3d0);
        if (index < 0 || index >= (last - first) / 16) { if (log) SlotLog("slot %d: no button (%lld)", index, (long long)((last - first) / 16)); return; }
        void *button = *(void **)(first + index * 16);
        __declspec(align(16)) View v = { "name", 4 };
        void *child = button ? ((FindChild_t)(base + find))(button, &v) : NULL;
        char *label = child ? (char *)((DynCast_t)(base + cast))(child, 0, base + rw, base + rl, 0) : NULL;
        if (!label) { if (log) SlotLog("slot %d: no name label (button %p, child %p)", index, button, child); return; }
        /* "Co-op: <nick>" in place of "Game N" (the button is narrow: a longer text is cut; the slot's place in the
         * list still says which one). */
        static char out[64];
        _snprintf(out, sizeof out - 1, "Co-op: %.16s", nick);
        out[sizeof out - 1] = 0;
        v.p = out;
        v.n = strlen(out);
        ((LabelSetText_t)(base + set))(label, &v);
        if (log) SlotLog("slot %d: marked \"%.200s\"", index, out);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { SlotLog("slot %d: exception", index); }
}

static void *InlineHook(DWORD steamRva, const unsigned char *expected, int n, void *hook)
{
    DWORD rva = ExeRva(steamRva);
    if (!rva) return NULL;
    unsigned char *target = (unsigned char *)base + rva;
    if (n < 14 || memcmp(target, expected, n) != 0) return NULL;
    unsigned char *tramp = (unsigned char *)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!tramp) return NULL;
    memcpy(tramp, target, n);
    tramp[n] = 0xff; tramp[n + 1] = 0x25; memset(tramp + n + 2, 0, 4);
    *(void **)(tramp + n + 6) = target + n;
    DWORD old;
    if (!VirtualProtect(target, n, PAGE_EXECUTE_READWRITE, &old)) return NULL;
    unsigned char patch[32];
    memset(patch, 0x90, sizeof patch);
    patch[0] = 0xff; patch[1] = 0x25; memset(patch + 2, 0, 4);
    *(void **)(patch + 6) = hook;
    memcpy(target, patch, n);
    VirtualProtect(target, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), target, n);
    return tramp;
}

/* The Coop exe patch marks the loader section when the netTest setting is on: ".coop" + 0x60 = "NET1". */
static int Enabled(void)
{
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_SECTION_HEADER *s = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, s++)
        if (memcmp(s->Name, ".coop", 6) == 0 && s->Misc.VirtualSize >= 0x64)
            return memcmp(base + s->VirtualAddress + 0x60, "NET1", 4) == 0;
    return 0;
}

/* "from <log bytes> <turn ends> <battle|map>": the guest's save is the host's from that point of its log. */
static void SetFrom(const char *text)
{
    long long offset = 0;
    int ends = 0;
    char where[16] = "battle";
    if (sscanf(text, "%lld %d %15s", &offset, &ends, where) < 1 || offset <= 0) return;
    loaderSave = 1;
    NetTcpSetFrom(offset);
    endsArrived = ends;   /* the host counts turn ends from its game start */
    /* map: a save from between missions — the guest enters the next mission with the host (it waits for its scene). */
    joinedMid = strcmp(where, "map") != 0;
}

/* "save <path>" (UTF-8): the save file the host gives a guest's loader. */
static void SetSharedSave(const char *utf8)
{
    wchar_t path[MAX_PATH];
    if (!MultiByteToWideChar(CP_UTF8, 0, utf8, -1, path, MAX_PATH)) return;
    NetTcpSetSave(path);
    const wchar_t *p = wcsstr(path, L"savegame_");
    if (!p || swscanf(p + 9, L"%d", &hostSlotIndex) != 1) hostSlotIndex = -1;
}

/* A network game started from the loader (NE Co-op → Network game…, NetSession.cs): swh2_net_session.txt
 *   host [address:]port | join address:port
 *   name <nickname>
 *   save <path of the host's save slot file>   (host: what the guest's loader fetches, nettcp.c "getsave")
 *   info <text>                                (host: game data + mods fingerprint for the guest's loader, "getinfo")
 * read once and renamed to swh2_net_session.used.txt, so the next start of the game is single-player. Gives the same
 * words as a test control file: host = "record host <port>", join = "replay connect <address:port>". */
static int steamMode;                    /* session "steam host" (1) / "steam join <lobby|user id>" (2) */
static unsigned long long steamId;
static char sessionInfo[1024];
#define STEAM_GUEST_PORT 27016           /* the guest's local end of the Steam pipe (steamnet.c) */

/* The guest's slot when no loader said ("slot" line): swh2_coop_profile.txt "guestslot=N" (0-based, default the
 * last of 3) in the most recently saved Steam account's Save000 (GOG: <data>\Save000). */
static void DefaultGuestSlot(const wchar_t *dir)
{
    int slot = 2;
    wchar_t path[MAX_PATH], best[MAX_PATH] = L"";
    _snwprintf(path, MAX_PATH, L"%s\\swh2_coop_profile.txt", dir);
    FILE *f = _wfopen(path, L"r");
    if (f)
    {
        char line[256];
        while (fgets(line, sizeof line, f)) if (strncmp(line, "guestslot=", 10) == 0) slot = atoi(line + 10);
        fclose(f);
    }
    if (slot < 0 || slot > 99) slot = 2;
    FILETIME newest = {0};
    WIN32_FIND_DATAW fd;
    _snwprintf(path, MAX_PATH, L"%s\\*", dir);
    HANDLE h = FindFirstFileW(path, &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || !iswdigit(fd.cFileName[0])) continue;
            WIN32_FILE_ATTRIBUTE_DATA a;
            _snwprintf(path, MAX_PATH, L"%s\\%s\\Save000", dir, fd.cFileName);
            if (GetFileAttributesExW(path, GetFileExInfoStandard, &a) && CompareFileTime(&a.ftLastWriteTime, &newest) > 0)
            {
                newest = a.ftLastWriteTime;
                wcscpy(best, path);
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    if (!best[0]) _snwprintf(best, MAX_PATH, L"%s\\Save000", dir);
    _snwprintf(guestSlotPath, MAX_PATH, L"%s\\savegame_%03d.dat", best, slot);
}

/* No session file, but Steam started the game with "+connect_lobby <id>" (the host's "Join game" in the friends list,
 * an invite): a guest session through Steam, the nickname from the loader's profile. */
static int SteamLaunchSession(const wchar_t *dir, char *word, char *net, char *arg)
{
    unsigned long long lobby = SteamNetLaunchLobby();
    if (!lobby) return 0;
    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%s\\swh2_coop_profile.txt", dir);
    FILE *f = _wfopen(path, L"r");
    if (f)
    {
        char line[256];
        while (fgets(line, sizeof line, f))
        {
            line[strcspn(line, "\r\n")] = 0;
            if (strncmp(line, "name=", 5) == 0 && line[5]) SetName(myName, line + 5);
        }
        fclose(f);
    }
    if (!myName[0] && SteamNetPersona()[0]) SetName(myName, SteamNetPersona());   /* never opened the loader's window */
    steamMode = 2;
    steamId = lobby;
    strcpy(word, "replay");
    strcpy(net, "connect");
    sprintf(arg, "127.0.0.1:%d", STEAM_GUEST_PORT);
    return 1;
}

static int ReadSession(const wchar_t *dir, char *word, char *net, char *arg)
{
    wchar_t path[MAX_PATH], used[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%s\\swh2_net_session.txt", dir);
    _snwprintf(used, MAX_PATH, L"%s\\swh2_net_session.used.txt", dir);
    FILE *f = _wfopen(path, L"r");
    if (!f) return 0;
    char line[MAX_PATH * 3 + 16], what[16], value[128];
    int ok = 0;
    while (fgets(line, sizeof line, f))
    {
        line[strcspn(line, "\r\n")] = 0;
        if (strncmp(line, "name ", 5) == 0) { SetName(myName, line + 5); continue; }
        if (strncmp(line, "save ", 5) == 0) { SetSharedSave(line + 5); continue; }
        if (strncmp(line, "info ", 5) == 0) { NetTcpSetInfo(line + 5); strncpy(sessionInfo, line + 5, sizeof sessionInfo - 1); continue; }
        if (strncmp(line, "from ", 5) == 0) { SetFrom(line + 5); continue; }
        if (strncmp(line, "slot ", 5) == 0) { MultiByteToWideChar(CP_UTF8, 0, line + 5, -1, guestSlotPath, MAX_PATH); loaderSlot = 1; continue; }
        if (strcmp(line, "steam host") == 0) { steamMode = 1; continue; }
        if (strncmp(line, "steam join ", 11) == 0) { steamMode = 2; steamId = _strtoui64(line + 11, NULL, 10); continue; }
        if (sscanf(line, "%15s %127s", what, value) != 2) continue;
        if (_stricmp(what, "host") == 0) { strcpy(word, "record"); strcpy(net, "host"); strcpy(arg, value); ok = 1; }
        if (_stricmp(what, "join") == 0) { strcpy(word, "replay"); strcpy(net, "connect"); strcpy(arg, value); ok = 1; }
    }
    fclose(f);
    MoveFileExW(path, used, MOVEFILE_REPLACE_EXISTING);
    return ok;
}

void NetInstall(char *exeBase, const wchar_t *dir)
{
    static const unsigned char requestAction[] = { 0x40,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x56,0x41,0x57,0x48,0x8d,0xac,0x24,0xd0,0xfe,0xff,0xff };
    static const unsigned char simUpdate[] = { 0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xac,0x24,0xd0,0xfd,0xff,0xff };
    static const unsigned char factionDone[] = { 0x40,0x53,0x55,0x41,0x57,0x48,0x83,0xec,0x30,0x48,0x8b,0x51,0x18,0x45,0x33,0xff };
    static const unsigned char aiStep[] = { 0x48,0x8b,0xc4,0x55,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xa8,0x28,0xff,0xff,0xff };
    base = exeBase;
    _snwprintf(modePath, MAX_PATH, L"%s\\swh2_net_mode.txt", dir);
    DeleteFileW(modePath);   /* the mission hook reads it: no file = no state lines */

    /* Always: the world update is the input lock's "tactics running" heartbeat (NetWorldTick). The pump in it only
     * runs with the netplay test on. */
    realSimUpdate = (SimUpdate_t)InlineHook(RVA_SimUpdate, simUpdate, sizeof simUpdate, (void *)HookSimUpdate);
    static const unsigned char popupShow[] = { 0x40,0x55,0x53,0x56,0x57,0x41,0x56,0x48,0x8d,0xac,0x24,0xc0,0xfe,0xff,0xff };
    realPopupShow = (PopupShow_t)InlineHook(RVA_PopupShow, popupShow, sizeof popupShow, (void *)HookPopupShow);
    /* Always too: the co-op slot's mark in the main menu (also in single-player starts). */
    _snwprintf(coopSlotPath, MAX_PATH, L"%s\\swh2_coop_slot.txt", dir);
    static const unsigned char slotFill[] = { 0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xac,0x24,0xe0,0xfc,0xff,0xff };
    realSlotFill = (SlotFill_t)InlineHook(RVA_SlotFill, slotFill, sizeof slotFill, (void *)HookSlotFill);
    if (!realSlotFill) SlotLog("the slot hook did not go in (other bytes at 0x%x)", ExeRva(RVA_SlotFill));
    if (!Enabled()) return;

    wchar_t cfg[MAX_PATH];
    _snwprintf(cfg, MAX_PATH, L"%s\\swh2_net.txt", dir);
    _snwprintf(replayPath, MAX_PATH, L"%s\\swh2_net_replay.log", dir);
    _snwprintf(syncPath, MAX_PATH, L"%s\\swh2_net_sync.txt", dir);
    _snwprintf(syncedPath, MAX_PATH, L"%s\\swh2_net_synced.txt", dir);
    _snwprintf(cutscenePath, MAX_PATH, L"%s\\swh2_net_cutscene.txt", dir);
    DeleteFileW(syncPath);
    DeleteFileW(syncedPath);
    /* swh2_net.txt: "<mode> [host <port> | connect <address>:<port> [play]]" — mode record / replay / autoplay; host:
     * stream the log to a guest and play its commands (record, autoplay); connect: replay live what the host streams
     * (replay); play: test, the guest plays the player's commands of swh2_net_guestplay.log (see "guest play"). */
    int wanted = NET_RECORD, hostPort = 0, guestPort = 0;
    char word[32] = {0}, net[16] = {0}, arg[128] = {0}, extra[16] = {0};
    FILE *f = _wfopen(cfg, L"r");
    if (f)
    {
        char first[256] = "";   /* the first line alone: a later "name" line is not a word of it */
        if (fgets(first, sizeof first, f) && sscanf(first, "%31s %15s %127s %15s", word, net, arg, extra) >= 1 && _stricmp(word, "replay") == 0) wanted = NET_REPLAY;
        if (_stricmp(word, "autoplay") == 0) { wanted = NET_REPLAY; autoplay = 1; }
        char line[MAX_PATH * 3 + 16];   /* test runs: "name <nickname>" / "save <path>" on later lines, as in a session file */
        while (fgets(line, sizeof line, f))
        {
            line[strcspn(line, "\r\n")] = 0;
            if (strncmp(line, "name ", 5) == 0) SetName(myName, line + 5);
            if (strncmp(line, "save ", 5) == 0) SetSharedSave(line + 5);
            if (strncmp(line, "info ", 5) == 0) NetTcpSetInfo(line + 5);
            if (strncmp(line, "from ", 5) == 0) SetFrom(line + 5);
            if (strncmp(line, "slot ", 5) == 0) MultiByteToWideChar(CP_UTF8, 0, line + 5, -1, guestSlotPath, MAX_PATH);
            if (strncmp(line, "drop ", 5) == 0) NetTcpTestDrop(atoi(line + 5) * 1000);   /* test: reconnect */
        }
        fclose(f);
    }
    else if (!ReadSession(dir, word, net, arg) && !SteamLaunchSession(dir, word, net, arg)) return;   /* single-player, nothing hooked */
    if (steamMode == 2 && !guestSlotPath[0]) DefaultGuestSlot(dir);
    if (_stricmp(word, "replay") == 0) wanted = NET_REPLAY;
    guest = wanted == NET_REPLAY && !autoplay;
    char *bindAddr = "";
    if (_stricmp(net, "host") == 0 && !guest)
    {
        char *colon = strrchr(arg, ':');
        if (colon) { *colon = 0; bindAddr = arg; hostPort = atoi(colon + 1); }
        else hostPort = atoi(arg);
    }
    if (_stricmp(net, "connect") == 0 && guest)
    {
        char *colon = strrchr(arg, ':');
        guestPort = colon ? atoi(colon + 1) : 0;
        if (colon) *colon = 0;
        live = guestPort > 0;
        guestPlay = live && _stricmp(extra, "play") == 0;
        _snwprintf(hostInfo, 160, L"%S:%d", arg, guestPort);
        if (live) { FILE *empty = _wfopen(replayPath, L"wb"); if (empty) fclose(empty); }   /* no old record */
    }
    _snwprintf(outPath, MAX_PATH, L"%s\\%s", dir, wanted == NET_REPLAY && !autoplay ? L"swh2_net_replayed.log" : L"swh2_net_record.log");
    DeleteFileW(outPath);
    SYSTEMTIME t;
    GetLocalTime(&t);
    NetLog("# SWH2 netplay %s, %04d-%02d-%02d %02d:%02d:%02d, %s exe", autoplay ? "record (autoplay)" : wanted == NET_REPLAY ? "replay" : "record",
           t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, ExeBuildName());
    DWORD rng = ExeRva(RVA_RngPtr), storage = ExeRva(RVA_ActorStorage), slot = ExeRva(RVA_FactionDoneTypeId),
          ongoing = ExeRva(RVA_IsActionsOngoing);
    if (!rng || !storage || !slot || !ongoing) { NetLog("# ERROR: game addresses not found in this exe"); return; }
    atRngPtr = base + rng;
    atActorStorage = base + storage;
    atFactionDoneTypeId = base + slot;
    atIsActionsOngoing = base + ongoing;
    mode = wanted;
    if (mode == NET_REPLAY) LoadScript();
    if (guestPlay)
    {
        wchar_t playPath[MAX_PATH];
        _snwprintf(playPath, MAX_PATH, L"%s\\swh2_net_guestplay.log", dir);
        LoadGuestPlay(playPath);
    }
    hosting = hostPort > 0;
    me = hosting ? 1 : live ? 2 : 0;
    if (me == 1 && myName[0]) { strcpy(hostName, myName); NetLog("name host %s", hostName); }
    if (me == 2 && myName[0])
    {
        char hello[96];
        strcpy(guestName, myName);
        sprintf(hello, "hello %s", myName);
        NetTcpSetHello(hello);
    }
    _snwprintf(crewPath, MAX_PATH, L"%s\\swh2_net_crew.txt", dir);
    DeleteFileW(crewPath);
    _snwprintf(statusPath, MAX_PATH, L"%s\\swh2_net_status.txt", dir);
    DeleteFileW(statusPath);
    _snwprintf(focusPath, MAX_PATH, L"%s\\swh2_net_focus.txt", dir);
    DeleteFileW(focusPath);
    _snwprintf(wonPath, MAX_PATH, L"%s\\swh2_net_won.txt", dir);
    DeleteFileW(wonPath);
    _snwprintf(movePath, MAX_PATH, L"%s\\swh2_net_move.txt", dir);
    DeleteFileW(movePath);

    if (hostPort > 0)
        NetLog(NetTcpHost(bindAddr, hostPort, outPath) ? "# host: guests connect to %s port %d" : "# ERROR: host on %s port %d not started",
               bindAddr[0] ? bindAddr : "any address,", hostPort);
    if (steamMode == 1 && hostPort > 0)
        NetLog(SteamNetHost(hostPort, myName, sessionInfo, 1) ? "# host: Steam lobby for friends (Join game in the Steam friends list, swh2_steam.log)"
                                                              : "# ERROR: host: no Steam here (GOG copy?): guests join by address only");
    if (steamMode == 2 && live)
        NetLog(SteamNetJoin(steamId, guestPort) ? "# guest: through Steam (%llu, swh2_steam.log), slot %d" : "# ERROR: guest: no Steam here (%llu): no connection, slot %d",
               steamId, GuestSlotIndex() + 1);
    if (live && !NetTcpGuest(arg, guestPort, replayPath)) NetLog("# ERROR: no network thread for the guest");

    if (me)
    {
        static const unsigned char canSelect[] = { 0x48,0x89,0x5c,0x24,0x18,0x56,0x48,0x83,0xec,0x20,0x48,0x8b,0xf1,0x8b,0xda };
        realCanSelect = (CanSelect_t)InlineHook(RVA_CanSelect, canSelect, sizeof canSelect, (void *)HookCanSelect);
        NetLog(realCanSelect ? "# %s: only the own crew can be selected" : "# ERROR: %s: selection hook not installed", me == 1 ? "host" : "guest");
        static const unsigned char cameraPan[] = { 0x48,0x89,0x4c,0x24,0x08,0x48,0x83,0xec,0x48,0x48,0xc7,0x04,0x24,0xfe,0xff,0xff,0xff };
        realCameraPan = (CameraPan_t)InlineHook(RVA_CameraPan, cameraPan, sizeof cameraPan, (void *)HookCameraPan);
        if (!realCameraPan) NetLog("# ERROR: camera pan hook not installed: the co-op camera pans show the letterbox");
        static const unsigned char skipMode[] = { 0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57,0x48,0x83,0xec,0x20 };
        realSkipMode = (SkipMode_t)InlineHook(RVA_SkipMode, skipMode, sizeof skipMode, (void *)HookSkipMode);
        NetLog(realSkipMode ? "# skipping actions off (the skip button would end them in one game only)" : "# ERROR: skip button hook not installed");
        static const unsigned char slotUpdate[] = { 0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x7c,0x24,0x10,0x55,0x48,0x8b,0xec,0x48,0x83,0xec,0x50 };
        DWORD findChild = ExeRva(RVA_FindChild);
        if (findChild) atFindChild = base + findChild;
        realSlotUpdate = findChild ? (SlotUpdate_t)InlineHook(RVA_SlotUpdate, slotUpdate, sizeof slotUpdate, (void *)HookSlotUpdate) : NULL;
        if (!realSlotUpdate) NetLog("# ERROR: party slot hook not installed: no owner colours on the portraits");
        static const unsigned char gameUpdate[] = { 0x48,0x8b,0xc4,0x56,0x57,0x41,0x56,0x48,0x81,0xec,0x30,0x05,0x00,0x00 };
        static const unsigned char checkpoint[] = { 0x40,0x57,0x48,0x83,0xec,0x30,0x48,0xc7,0x44,0x24,0x20,0xfe,0xff,0xff,0xff };   /* push rdi has a REX byte */
        realGameUpdate = (GameUpdate_t)InlineHook(RVA_GameUpdate, gameUpdate, sizeof gameUpdate, (void *)HookGameUpdate);
        realCheckpoint = (Checkpoint_t)InlineHook(RVA_Checkpoint, checkpoint, sizeof checkpoint, (void *)HookCheckpoint);
        NetLog(realGameUpdate && realCheckpoint ? "# %s: checkpoint loads followed" : "# ERROR: %s: checkpoint hooks not installed", me == 1 ? "host" : "guest");
        static const unsigned char ptsWait[] = { 0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20 };
        realPtsWait = (PtsWait_t)InlineHook(RVA_PtsWait, ptsWait, sizeof ptsWait, (void *)HookPtsWait);
        if (!realPtsWait) NetLog("# ERROR: %s: wait hook not installed: a guest's wait is not sent to the host", me == 1 ? "host" : "guest");
        static const unsigned char aimUpdate[] = { 0x48,0x8b,0xc4,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57 };
        AimResolve();
        realAimUpdate = atTraceMake ? (AimUpdate_t)InlineHook(RVA_AimUpdate, aimUpdate, sizeof aimUpdate, (void *)HookAimUpdate) : NULL;
        static const unsigned char cursorNode[] = { 0x48,0x8b,0xc4,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xa8,0x38,0xff,0xff,0xff };
        realCursorNode = (CursorNode_t)InlineHook(RVA_CursorNode, cursorNode, sizeof cursorNode, (void *)HookCursorNode);
        if (!realCursorNode) NetLog("# ERROR: %s: tile cursor hook not installed: the players don't see where the other one moves", me == 1 ? "host" : "guest");
        if (!realAimUpdate) { atTraceMake = NULL; NetLog("# ERROR: %s: aim hook not installed: the players don't see each other aim", me == 1 ? "host" : "guest"); }
        static const unsigned char quit[] = { 0x4c,0x8b,0xdc,0x53,0x48,0x83,0xec,0x50,0x49,0xc7,0x43,0xd8,0xfe,0xff,0xff,0xff };
        realQuit = (Quit_t)InlineHook(RVA_Quit, quit, sizeof quit, (void *)HookQuit);
        if (me == 1 && !realQuit) NetLog("# ERROR: host: quit hook not installed: the guest isn't told when the host leaves its battle");
        if (me == 2)
        {
            static const unsigned char menuUpdate[] = { 0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0x6c,0x24,0xc0 };
            realMenuUpdate = (MenuUpdate_t)InlineHook(RVA_MenuUpdate, menuUpdate, sizeof menuUpdate, (void *)HookMenuUpdate);
            NetLog(realGameUpdate && realQuit && realMenuUpdate ? "# guest: the next battle joins without a restart (to the main menu, Continue: by itself)"
                                                                : "# ERROR: guest: main menu hooks not installed: restart the game for the next battle");
        }
    }
    realRequestAction = (RequestAction_t)InlineHook(RVA_RequestAction, requestAction, sizeof requestAction, (void *)HookRequestAction);
    realFactionDone = (FactionDone_t)InlineHook(RVA_FactionDone, factionDone, sizeof factionDone, (void *)HookFactionDone);
    realAiStep = (AiStep_t)InlineHook(RVA_AiStep, aiStep, sizeof aiStep, (void *)HookAiStep);
    static const unsigned char enemyTurn[] = { 0x48,0x8b,0xc4,0x55,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xa8,0x28,0xfd,0xff,0xff };
    realEnemyTurn = (EnemyTurn_t)InlineHook(RVA_EnemyTurn, enemyTurn, sizeof enemyTurn, (void *)HookEnemyTurn);
    static const unsigned char endActor[] = { 0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20 };
    realEndActor = (EndActor_t)InlineHook(RVA_EndActor, endActor, sizeof endActor, (void *)HookEndActor);
    static const unsigned char sceneCreate[] = { 0x48,0x89,0x4c,0x24,0x08,0x55,0x53,0x56,0x57,0x41,0x56,0x41,0x57,0x48,0x8b,0xec };
    realSceneCreate = (SceneCreate_t)InlineHook(RVA_SceneCreate, sceneCreate, sizeof sceneCreate, (void *)HookSceneCreate);
    if (!realSceneCreate) NetLog("# ERROR: mission scene hook not installed: reinforcement decks will differ");
    static const unsigned char spawnReinf[] = { 0x48,0x8b,0xc4,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57 };
    static const unsigned char spawnEntrance[] = { 0x48,0x8b,0xc4,0x4c,0x89,0x40,0x18,0x48,0x89,0x48,0x08,0x55,0x56,0x57 };
    realSpawnReinf = (Spawn_t)InlineHook(RVA_SpawnReinf, spawnReinf, sizeof spawnReinf, (void *)HookSpawnReinf);
    realSpawnEntrance = (Spawn_t)InlineHook(RVA_SpawnEntrance, spawnEntrance, sizeof spawnEntrance, (void *)HookSpawnEntrance);
    if (!realSpawnReinf || !realSpawnEntrance) NetLog("# ERROR: spawn hooks not installed: reinforcements may differ");
    static const unsigned char worldUpdate[] = { 0x48,0x8b,0xc4,0x55,0x57,0x41,0x54,0x41,0x56,0x41,0x57,0x48,0x8d,0x68,0xa1 };
    realWorldUpdate = (WorldUpdate_t)InlineHook(RVA_WorldUpdate, worldUpdate, sizeof worldUpdate, (void *)HookWorldUpdate);
    static const unsigned char actorPose[] = { 0x48,0x8b,0xc4,0x48,0x89,0x70,0x20,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0x68,0xa1 };
    static const unsigned char skinned[] = { 0x48,0x8b,0xc4,0x55,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0xa8,0xc8,0xfd,0xff,0xff };
    DWORD poseRet = ExeRva(RVA_ActorPoseFrameRet), skinRet = ExeRva(RVA_SkinnedFrameRet);
    if (realWorldUpdate && poseRet && skinRet)
    {
        atActorPoseFrameRet = base + poseRet;
        atSkinnedFrameRet = base + skinRet;
        realActorPose = (PoseSim_t)InlineHook(RVA_ActorPose, actorPose, sizeof actorPose, (void *)HookActorPose);
        realSkinned = (PoseSim_t)InlineHook(RVA_Skinned, skinned, sizeof skinned, (void *)HookSkinned);
    }
    poseSteps = realActorPose && realSkinned;   /* one without the other: both stay per frame */
    if (mode != NET_OFF) NetLog(poseSteps ? "# poses (skeletons, hit polygons) go with the world steps" : "# ERROR: pose hooks not installed: poses go by the frame time");
    if (mode != NET_OFF) CosmeticRng();   /* record and replay alike */
    if (!realWorldUpdate) NetLog("# ERROR: world update hook not installed: no fixed time step");
    if (!realRequestAction || !realFactionDone || !realSimUpdate || !realAiStep || !realEnemyTurn || !realEndActor)
    {
        /* Whatever got hooked keeps working (the hooks pass everything through); with mode off nothing is logged. */
        NetLog("# ERROR: unexpected code at a hook address (unsupported game version?): RequestAction %s, FactionDone %s, Update %s, AI %s, enemy turn %s, actor end %s",
               realRequestAction ? "ok" : "NO", realFactionDone ? "ok" : "NO", realSimUpdate ? "ok" : "NO", realAiStep ? "ok" : "NO",
               realEnemyTurn ? "ok" : "NO", realEndActor ? "ok" : "NO");
        mode = NET_OFF;
        return;
    }
    char ansi[MAX_PATH * 2];   /* read by the mission Lua (io.open takes ANSI paths) */
    f = _wfopen(modePath, L"w");
    if (f && WideCharToMultiByte(CP_ACP, 0, outPath, -1, ansi, sizeof ansi, NULL, NULL)) fprintf(f, "%s\n", ansi);
    if (f) fclose(f);
}
