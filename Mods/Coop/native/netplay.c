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
#include "netplay.h"

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
static int live;         /* guest over the network (nettcp.c): the replay file grows while the battle runs */
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
#define HELD() (hosting && !guestIn)
static int joinedMid;      /* guest: its save is from inside the host's mission (session "from"): no scene line comes */
static int readySentFor;   /* guest: the connect (NetTcpReconnects() + 1) "ready" was sent on */
static int ordSeeded;      /* guest, joinedMid: the host's ordinals are in (once per joined battle) */
/* The next battle without a restart (see NetIdle): the guest's slot file, the host's save waiting for the main menu,
 * and the one written there, whose battle the replay rebases to when it loads. */
static wchar_t guestSlotPath[MAX_PATH];
static char *nextSave;
static int nextSaveLen, nextEnds, rejoinEnds, rejoinPending;
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
 *   <host|guest> <waiting|connected|closed|failed> <host's crewmates with action points> <guest's ...> */
static wchar_t statusPath[MAX_PATH];
static void *FindActor(void *sim, int id);

static int HasPoints(void *actor)
{
    __try { return *(int *)((char *)actor + 0x90) > 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static void WriteStatus(void *sim)
{
    static const char *states[] = { "waiting", "waiting", "connected", "closed", "failed" };
    int left[3] = { 0, 0, 0 }, st = NetTcpState();
    for (int i = 0; i < crewLen; i++)
    {
        void *a = FindActor(sim, crewIds[i]);
        if (a && HasPoints(a)) left[i % 2 == 0 ? 1 : 2]++;
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
    fprintf(f, "%s %s %d %d\n", me == 1 ? "host" : "guest", word, left[1], left[2]);
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
    return !me || !o || o == me;
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

static unsigned char HookCanSelect(void *pts, int id)
{
    if (me && id && !Mine(id)) return 0;
    if (HELD()) return 0;
    return realCanSelect(pts, id);
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
    int ao, to;          /* actor / type-2 target as actor ordinal (-1 = none or not an actor) */
    int statesBefore;    /* "state" lines in the record before it: the state to correct to before it is issued */
    unsigned char data[MAX_VEC_BYTES + 8];
    int dataLen;
    char name[64];
} Entry;
static Entry *script;
static int scriptLen, scriptPos;
static int endsSeen, luaSeen, idle, gateWait;
static int luaRecordedLen, endsRecordedLen;
static ULONGLONG worldTick;

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

static int CutsceneHold(void)
{
    if (frame % 10 == 0)
    {
        FILE *f = _wfopen(cutscenePath, L"r");
        cutsceneOn = f && fgetc(f) == '1';
        if (f) fclose(f);
    }
    if (cutsceneOn) { quietFrames = 0; return 1; }
    return ++quietFrames < CUTSCENE_QUIET;
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
        NetLog("cmd %u o=%s a=%d ao=%d i=-1 n=@done t=0: to=-1 fl=0 r=%08x:%08x e=%d d=0 f=%u pv=%u%s", ++seq, OriginLabel(origin),
               id, OrdinalOf(id), r ? r[0] : 0, r ? r[1] : 0, EntityCount(sim), frame, previews,
               mainSim && sim != mainSim ? "  # not the main actor simulator" : "");
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
static int FinishFaction(void *sim)
{
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
    if (depth > 0 || origin == ORIGIN_OTHER || mainPreview)
    {
        /* Who calls RequestAction from inside an action or from an unknown place: return addresses (Steam-build RVAs;
         * x = outside the exe). */
        void *frames[8];
        int n = RtlCaptureStackBackTrace(1, 8, frames, NULL);
        strcpy(bt, " bt=");
        for (int i = 0; i < n; i++)
        {
            ULONG_PTR a = (ULONG_PTR)frames[i];
            char one[16];
            if (a > (ULONG_PTR)base && a < (ULONG_PTR)base + 0x1000000) sprintf(one, "%lx,", SteamRva((DWORD)(a - (ULONG_PTR)base)));
            else strcpy(one, "x,");
            if (strlen(bt) + strlen(one) < sizeof bt) strcat(bt, one);
        }
    }

    unsigned *r = Rng();
    NetLog("cmd %u o=%s a=%d ao=%d i=%d n=%s t=%d:%s to=%d fl=%u r=%08x:%08x e=%d d=%d f=%u pv=%u%s%s", ++seq,
           OriginLabel(origin), id, ao, index, name[0] ? name : "?", type, hex, to, flag, r ? r[0] : 0, r ? r[1] : 0,
           EntityCount(sim), depth, frame, previews, bt, depth == 0 ? bySuffix : "");
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
    if (origin != ORIGIN_PREVIEW)
    {
        if (mode == NET_REPLAY)
        {
            /* A turn end the game gives itself (player after the last action point, scripts, the turn manager) counts
             * as the recorded one at the head. Autoplay has only the player's in its script. */
            Entry *head = scriptPos < scriptLen ? &script[scriptPos] : NULL;
            int ours = guest || origin == ORIGIN_PLAYER || origin == ORIGIN_REPLAY;
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
        NetLog("end %u o=%s r=%08x:%08x e=%d f=%u%s%s", ++seq, OriginLabel(origin), r ? r[0] : 0, r ? r[1] : 0,
               EntityCount(mainSim), frame, bySuffix, !expected || expected == turn ? "" : "  # object differs from the simulator slot");
    }
    inFactionDone++;   /* it ends every actor's turn (0x390e20): consequences, not logged as commands */
    ULONG_PTR result = realFactionDone(turn);
    inFactionDone--;
    return result;
}

static int SyncDone(int wanted)
{
    FILE *f = _wfopen(syncedPath, L"r");
    int n = -1;
    if (f) { if (fscanf(f, "%d", &n) != 1) n = -1; fclose(f); }
    return n == wanted;
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
    "coop_owner_1", "coop_owner_2",
    "coop_net_host_waiting", "coop_net_host_connected", "coop_net_host_closed", "coop_net_host_failed", "coop_net_host_joining",
    "coop_net_guest_waiting", "coop_net_guest_connected", "coop_net_guest_closed", "coop_net_guest_failed",
    "coop_net_turn_both", "coop_net_turn_p1", "coop_net_turn_p2", "coop_net_turn_enemy",
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
static void HostPlay(void *sim, const char *line)
{
    if (strcmp(line, "ready") == 0)
    {
        if (!guestIn) NetLog("# the guest is in the battle: it goes on");
        guestIn = 1;
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
        void *actor = id >= 0 ? FindActor(sim, id) : NULL;
        if (!actor) { NetLog("# guest command for actor ordinal %d: no such actor here, ignored", ao); return; }
        if (OwnerOf(id) == 1) { NetLog("# guest command for the host's actor %d (ordinal %d) refused", id, ao); return; }
        if (len == 4 && type == 2 && to >= 0) { int tid = IdOfOrdinal(to, *(int *)data); memcpy(data, &tid, 4); }
        unsigned char target[TARGET_SIZE + 8];
        if (len < 0 || !NetBuildTarget(target, type, data, len)) { NetLog("# guest command with a bad target, ignored"); return; }
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
        return;
    }
    NetLog("# unknown line from the guest: %.80s", line);
}

/* Plays the guest's lines when the world is free; the commands of one guest frame together. */
static void HostPump(void *sim)
{
    static char line[2 * (MAX_VEC_BYTES + 8) + 128], next[sizeof line];
    static int have;
    if (!have && !(have = NetTcpPoll(line, sizeof line))) return;
    if (Busy(sim) || replaying) return;
    for (;;)
    {
        if (strcmp(line, "ready") != 0 && strcmp(line, "joined") != 0) NetLog("# from the guest: %.120s", line);   /* "ready" comes every few seconds */
        HostPlay(sim, line);
        have = NetTcpPoll(next, sizeof next);
        if (!have) return;
        const char *f1 = strstr(line, " f="), *f2 = strstr(next, " f=");
        int chained = strncmp(line, "gcmd", 4) == 0 && strncmp(next, "gcmd", 4) == 0 && f1 && f2 && atoi(f1 + 3) == atoi(f2 + 3);
        strcpy(line, next);
        if (!chained) return;
    }
}

static void Pump(void *sim)
{
    if (mode != NET_REPLAY || HELD()) return;
    if (live && frame % 4 == 0) ReadMore();
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
        if (guest && e->kind == 'e' && e->origin == ORIGIN_OTHER && idle >= IDLE_FRAMES && clearedFor != scriptPos + 1)
        {
            clearedFor = scriptPos + 1;
            NetLog("# turn end %d: the host's AI was done, turn of %d actors ended", scriptPos + 1, FinishFaction(sim));
        }
        if (idle < (gameGives ? END_WAIT : IDLE_FRAMES)) return;
        if (!autoplay && e->statesBefore > statesSynced)
        {
            if (syncWanted != e->statesBefore)
            {
                syncWanted = e->statesBefore;
                syncWait = 0;
                SyncRequest(syncWanted);
            }
            if (!SyncDone(syncWanted) && ++syncWait < SYNC_TIMEOUT) return;
            if (syncWait >= SYNC_TIMEOUT) NetLog("# DIFF: state %d not corrected (no answer from the mission Lua)", syncWanted);
            statesSynced = syncWanted;
            idle = 0;
            return;   /* the correction may start actions: wait for idle again */
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
        if (!actor) NetLog("# DIFF: replay %d: actor %d (ordinal %d) not found, skipped", scriptPos, e->actor, e->ao);
        else if (e->index == INDEX_DONE)
        {
            Sync(e, sim);
            HookEndActor(sim, actor);
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
        if (me == 2 && NetTcpState() == NET_TCP_CONNECTED && (readySentFor != NetTcpReconnects() + 1 || frame - readyAt >= 300) && NetTcpSend("ready"))
        {
            if (readySentFor != NetTcpReconnects() + 1) NetLog("# to host: ready (this game is in the battle)");
            readySentFor = NetTcpReconnects() + 1;
            readyAt = frame;
        }
        if (me && frame % 30 == 0) { ReadCrew(); WriteStatus(sim); }
        if (me && namesDirty && frame % 10 == 0) ApplyNames();
        if (hosting) HostPump(sim);
        Pump(sim);
    }
    return realSimUpdate(sim, arg);
}

unsigned long long NetWorldTick(void) { return worldTick; }

/* Fixed time step for the tactics world (netplay test on): the world update 0x480a10(world, game, float *dt) normally
 * gets the frame time, and projectiles / movement integrate with it — run 8: the same enemy shot at the same RNG state
 * missed in the record and hit in the replay. Real time is accumulated and the world gets whole steps of 1/60 s (up to
 * 4 per frame, a frame without a due step updates with dt 0 like a paused game). The time scale (slow motion, Turbo) is
 * applied inside, after this. The outcome preview runs this function on its copy with its own fixed step: untouched. */
typedef ULONG_PTR (*WorldUpdate_t)(void *world, void *game, float *dt);
static WorldUpdate_t realWorldUpdate;
static float stepAcc;
#define RVA_WorldUpdate 0x480a10
#define FIXED_STEP (1.0f / 60.0f)

static ULONG_PTR HookWorldUpdate(void *world, void *game, float *dt)
{
    if (mode == NET_OFF || !dt || Origin() == ORIGIN_PREVIEW) return realWorldUpdate(world, game, dt);
    float d = *dt;
    if (!(d > 0)) d = 0;
    if (d > 0.1f) d = 0.1f;
    stepAcc += d;
    ULONG_PTR r = 0;
    int n = 0;
    for (; stepAcc >= FIXED_STEP && n < 4; n++)
    {
        float step = FIXED_STEP;
        r = realWorldUpdate(world, game, &step);
        stepAcc -= FIXED_STEP;
    }
    if (n == 0)
    {
        float none = 0;
        r = realWorldUpdate(world, game, &none);
    }
    if (stepAcc > FIXED_STEP) stepAcc = 0;   /* too slow to keep up: the game slows down instead */
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
        NetLog("spawn r=%08x:%08x f=%u", z, w, frame);
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
        else WaitForHost(&scenesLen, scenePos, 180000, "mission scene");
        if (scenePos == -1) { scenePos = 0; NetLog("# joined inside the host's mission: no scene line, RNG from the commands"); }
        else if (scenePos < scenesLen) { r[0] = sceneZ[scenePos]; r[1] = sceneW[scenePos]; scenePos++; }
        else NetLog("# DIFF: mission scene %d not in the record (its RNG not set)", scenePos + 1);
    }
    if (mode != NET_OFF && r) NetLog("scene r=%08x:%08x", r[0], r[1]);
    /* A new mission: the host waits for the guest again, the guest says "ready" again from it. */
    if (hosting && guestIn) { guestIn = 0; NetLog("# new mission: waiting for the guest to enter the battle"); }
    readySentFor = 0;
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
    const char *ent = strstr(line, " e="), *fr = strstr(line, " f=");
    if (ent) e->ent = atoi(ent + 3);
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
        if (scenesLen < 16) { sceneZ[scenesLen] = z; sceneW[scenesLen] = w; scenesLen++; }
        return;
    }
    int o = NetParseLine(line, &tmp);
    if (o >= 0 && tmp.kind == 'e') endsArrived++;
    if (o >= 0 && tmp.kind == 'c' && pending && strstr(line, " by=guest")) pending--;   /* ours, back from the host */
    if (o < 0 || o == ORIGIN_PREVIEW || (tmp.kind == 'c' && tmp.depth > 0)) return;   /* nested = a consequence */
    if (o == ORIGIN_REPLAY) o = ORIGIN_PLAYER;
    if (tmp.kind == 'c' && o == ORIGIN_LUA) luaRecordedLen++;
    /* Autoplay plays the player's decisions only; the guest everything the host decided: the player's and the AI's
     * commands and every turn end. Script commands and consequences follow by themselves. */
    int take = tmp.kind == 'e' ? (o == ORIGIN_PLAYER || guest) : (o == ORIGIN_PLAYER || (guest && o == ORIGIN_AI));
    tmp.origin = o;
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
    spawnsLen = spawnPos = 0;
    earlyEnds = pending = 0;
    endAskedFor = -1;
    endsArrived = ends;
    ordSeedLen = -1;
    ordSeeded = 0;
    ordSim = NULL;
    readySentFor = 0;
    joinedMid = 1;
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
    DWORD fire = ExeRva(RVA_FireSignal);
    if (quitWanted && !quitFired && fire)
    {
        char *g = (char *)game;
        int level = *(int *)(g + 0x110);
        char *obj = level >= 0 && level <= 3 ? *(char **)(g + (level >= 2 ? 0xe8 : 0xc0)) : NULL;
        if (obj)
        {
            quitFired = 1;
            NetLog("# quit to the main menu from the %s", level >= 2 ? "world map" : "battle");
            ((FireSignal_t)(base + fire))(obj + 0x428);
        }
    }
    return r;
}

static ULONG_PTR HookQuit(void *game, void *a2, void *a3, void *a4)
{
    quitWanted = 0;
    if (me == 2 && worldTick && GetTickCount64() - worldTick < 2000)
    {
        leftMid = 1;
        NetLog("# guest: left the battle for the main menu: Continue there joins the host's battle again");
    }
    return realQuit(game, a2, a3, a4);
}

/* The guest's slot index (0-based) from its file name savegame_NNN.dat, -1 = not one. */
static int GuestSlotIndex(void)
{
    const wchar_t *p = wcsstr(guestSlotPath, L"savegame_");
    int n = -1;
    return p && swscanf(p + 9, L"%d", &n) == 1 ? n : -1;
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
            continueAt = now + 1500;
            NetLog("# main menu: the host's save from its battle (host log byte %lld) is in this game's slot %d", nextFrom, GuestSlotIndex() + 1);
        }
        else NetLog("# ERROR: main menu: the host's save could not be written to the slot");
        free(nextSave);
        nextSave = NULL;
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

void NetIdle(void)
{
    if (me != 2 || !live || !guestSlotPath[0]) return;
    static ULONGLONG last, lastFetch;
    static int wasInBattle;
    ULONGLONG now = GetTickCount64();
    if (now - last < 500) return;
    last = now;
    if (worldTick && now - worldTick < 3000) { wasInBattle = 1; return; }
    if (!wasInBattle || rejoinPending) return;   /* before the first battle the loader put the save there already */
    char *data;
    int len, ends, battle;
    long long from;
    if (NetTcpFetchTake(&data, &len, &from, &ends, &battle) == 1)
    {
        long long seen = NetTcpBase() + scriptOffset;   /* host log bytes this guest has played */
        if (battle && from > nextFrom && (from > seen || leftMid))
        {
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

/* Inline hook: the first n bytes of the function (whole instructions, position-independent, n >= 14) are checked,
 * copied into a trampoline that jumps back, and replaced by jmp [rip+0] <hook>. steamRva: see ExeRva. */
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
    NetTcpSetFrom(offset);
    endsArrived = ends;   /* the host counts turn ends from its game start */
    /* map: a save from between missions — the guest enters the next mission with the host (it waits for its scene). */
    joinedMid = strcmp(where, "map") != 0;
}

/* "save <path>" (UTF-8): the save file the host gives a guest's loader. */
static void SetSharedSave(const char *utf8)
{
    wchar_t path[MAX_PATH];
    if (MultiByteToWideChar(CP_UTF8, 0, utf8, -1, path, MAX_PATH)) NetTcpSetSave(path);
}

/* A network game started from the loader (NE Co-op → Network game…, NetSession.cs): swh2_net_session.txt
 *   host [address:]port | join address:port
 *   name <nickname>
 *   save <path of the host's save slot file>   (host: what the guest's loader fetches, nettcp.c "getsave")
 *   info <text>                                (host: game data + mods fingerprint for the guest's loader, "getinfo")
 * read once and renamed to swh2_net_session.used.txt, so the next start of the game is single-player. Gives the same
 * words as a test control file: host = "record host <port>", join = "replay connect <address:port>". */
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
        if (strncmp(line, "info ", 5) == 0) { NetTcpSetInfo(line + 5); continue; }
        if (strncmp(line, "from ", 5) == 0) { SetFrom(line + 5); continue; }
        if (strncmp(line, "slot ", 5) == 0) { MultiByteToWideChar(CP_UTF8, 0, line + 5, -1, guestSlotPath, MAX_PATH); continue; }
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
    else if (!ReadSession(dir, word, net, arg)) return;   /* no network game this time: single-player, nothing hooked */
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
    if (hostPort > 0)
        NetLog(NetTcpHost(bindAddr, hostPort, outPath) ? "# host: guests connect to %s port %d" : "# ERROR: host on %s port %d not started",
               bindAddr[0] ? bindAddr : "any address,", hostPort);
    if (live && !NetTcpGuest(arg, guestPort, replayPath)) NetLog("# ERROR: no network thread for the guest");

    if (me)
    {
        static const unsigned char canSelect[] = { 0x48,0x89,0x5c,0x24,0x18,0x56,0x48,0x83,0xec,0x20,0x48,0x8b,0xf1,0x8b,0xda };
        realCanSelect = (CanSelect_t)InlineHook(RVA_CanSelect, canSelect, sizeof canSelect, (void *)HookCanSelect);
        NetLog(realCanSelect ? "# %s: only the own crew can be selected" : "# ERROR: %s: selection hook not installed", me == 1 ? "host" : "guest");
        static const unsigned char slotUpdate[] = { 0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x7c,0x24,0x10,0x55,0x48,0x8b,0xec,0x48,0x83,0xec,0x50 };
        DWORD findChild = ExeRva(RVA_FindChild);
        if (findChild) atFindChild = base + findChild;
        realSlotUpdate = findChild ? (SlotUpdate_t)InlineHook(RVA_SlotUpdate, slotUpdate, sizeof slotUpdate, (void *)HookSlotUpdate) : NULL;
        if (!realSlotUpdate) NetLog("# ERROR: party slot hook not installed: no owner colours on the portraits");
        if (me == 2)
        {
            static const unsigned char gameUpdate[] = { 0x48,0x8b,0xc4,0x56,0x57,0x41,0x56,0x48,0x81,0xec,0x30,0x05,0x00,0x00 };
            static const unsigned char quit[] = { 0x4c,0x8b,0xdc,0x53,0x48,0x83,0xec,0x50,0x49,0xc7,0x43,0xd8,0xfe,0xff,0xff,0xff };
            static const unsigned char menuUpdate[] = { 0x40,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0x6c,0x24,0xc0 };
            realGameUpdate = (GameUpdate_t)InlineHook(RVA_GameUpdate, gameUpdate, sizeof gameUpdate, (void *)HookGameUpdate);
            realQuit = (Quit_t)InlineHook(RVA_Quit, quit, sizeof quit, (void *)HookQuit);
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
