/*
 * SWH2 Co-op input lock. Loaded by the game itself (the Coop exe patch calls LoadLibraryA("Mods\\Coop\\swh2coop.dll")
 * right before SDL_Init), it swaps five SDL imports of the exe for filters that let through only the input device of
 * the player who owns the selected bot.
 *
 * The game polls every connected controller each frame and merges them (0x7f4d18), and reads keyboard/mouse from
 * SDL_PollEvent + SDL_GetKeyboardState/GetMouseState, so a device can be muted without touching game logic.
 *
 * Which device may play is written by the mission Lua (Coop.input in Lua/Mod/coop.lua) to
 *   %APPDATA%\Thunderful\SteamWorld Heist II\swh2_coop_input.txt   ("all" | "kbm" | "pad1".."pad4")
 * and rewritten twice a second; a file older than 2 s means "all" (world map, menus, game paused or left the mission).
 * Inputs everyone keeps (Controls/default_controls.cfg): switching bots (LB/RB, Q/E/Tab, 1-6), camera (right stick,
 * arrows), pause (Start, Esc), map (right stick click, M). Menus (pause etc.) are open to every device: see MenuMode.
 * Gamepads are numbered by connection order (SDL instance id): pad1 = the first controller connected.
 * A blocked press is counted in swh2_coop_blocked.txt so the Lua side can show whose bot it is.
 *
 * The same dll carries the netplay groundwork (netplay.c: record/replay of tactics commands), active only when the
 * exe patch was made with the netTest setting.
 */
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "netplay.h"

/* Source id (build.cmd: hash of the sources): the loaders of host and guest compare it (NetSession.DllId). The dll
 * bytes themselves differ between PCs built from the same sources (another compiler version). */
#ifndef SWH2COOP_SRC
#define SWH2COOP_SRC "unknown"
#endif
__declspec(dllexport) const char Swh2CoopSource[] = "SWH2COOP_SRC=" SWH2COOP_SRC;

/* RVAs of the import address table slots in the Steam exe (SHA-256 F1C07F03...); ExeRva translates them for GOG. */
#define IAT_GetMouseState 0xb47800
#define IAT_ControllerGetAxis 0xb47830
#define IAT_PollEvent 0xb47860
#define IAT_ControllerGetButton 0xb478b8
#define IAT_GetKeyboardState 0xb47930

#define DEV_ALL (-1)
#define DEV_NONE (-2)
#define DEV_KBM 0          /* 1..4 = pad number */
#define MAX_PADS 16
#define NUM_SCANCODES 512

typedef int (__cdecl *PollEvent_t)(void *ev);
typedef const unsigned char *(__cdecl *GetKeyboardState_t)(int *numkeys);
typedef unsigned int (__cdecl *GetMouseState_t)(int *x, int *y);
typedef unsigned char (__cdecl *GetButton_t)(void *gc, int button);
typedef short (__cdecl *GetAxis_t)(void *gc, int axis);
typedef void *(__cdecl *GetJoystick_t)(void *gc);
typedef int (__cdecl *InstanceID_t)(void *joystick);
typedef const char *(__cdecl *ControllerName_t)(void *gc);

static PollEvent_t realPollEvent;
static GetKeyboardState_t realGetKeyboardState;
static GetMouseState_t realGetMouseState;
static GetButton_t realGetButton;
static GetAxis_t realGetAxis;
static GetJoystick_t sdlGetJoystick;
static InstanceID_t sdlInstanceID;
static ControllerName_t sdlControllerName;

static wchar_t inputPath[MAX_PATH], blockedPath[MAX_PATH], logPath[MAX_PATH];
static int allowed = DEV_ALL;
static ULONGLONG lastCheck;
static FILETIME lastWrite;
static unsigned char keyCopy[NUM_SCANCODES];
static int lastMouseX, lastMouseY;
static unsigned blockedCount;
static ULONGLONG lastBlockedWrite;
static int menuOpen, wasStalled, popupOpen;
static ULONGLONG popupSince, popupSeen;
static unsigned char aPrev[MAX_PADS + 1];
static unsigned char startPrev[MAX_PADS + 1], bPrev[MAX_PADS + 1];
static ULONGLONG lastMenuKey;

/* Controllers seen recently: SDL instance id -> last tick it was polled. */
static struct { int id; ULONGLONG seen; } pads[MAX_PADS];
static int padsWritten = -1;

/* SDL_GameControllerButton / SDL_GameControllerAxis values everyone may use. */
static int AlwaysButton(int b) { return b == 6 /*START*/ || b == 8 /*RIGHTSTICK*/ || b == 9 /*LB*/ || b == 10 /*RB*/; }
static int AlwaysAxis(int a) { return a == 2 || a == 3; /* right stick = camera */ }
/* SDL scancodes everyone may use: Q E Tab M Esc, 1-6, arrows. */
static int AlwaysKey(int sc)
{
    return sc == 20 || sc == 8 || sc == 43 || sc == 16 || sc == 41 || (sc >= 30 && sc <= 35) || (sc >= 79 && sc <= 82);
}

static void Log(const char *fmt, ...)
{
    FILE *f = _wfopen(logPath, L"a");
    if (!f) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(f, "%04d-%02d-%02d %02d:%02d:%02d  ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    va_list a;
    va_start(a, fmt);
    vfprintf(f, fmt, a);
    va_end(a);
    fputc('\n', f);
    fclose(f);
}

static int ParseDevice(const char *s)
{
    if (strncmp(s, "kbm", 3) == 0) return DEV_KBM;
    if (strncmp(s, "pad", 3) == 0 && s[3] >= '1' && s[3] <= '9') return s[3] - '0';
    if (strncmp(s, "none", 4) == 0) return DEV_NONE;
    return DEV_ALL;
}

/* Re-reads the control file at most 10 times a second; stale = everyone may play. */
static void Refresh(void)
{
    ULONGLONG now = GetTickCount64();
    if (now - lastCheck < 100) return;
    lastCheck = now;
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!GetFileAttributesExW(inputPath, GetFileExInfoStandard, &a)) { allowed = DEV_ALL; return; }
    FILETIME sys;
    GetSystemTimeAsFileTime(&sys);
    ULONGLONG age = (((ULONGLONG)sys.dwHighDateTime << 32) | sys.dwLowDateTime)
                  - (((ULONGLONG)a.ftLastWriteTime.dwHighDateTime << 32) | a.ftLastWriteTime.dwLowDateTime);
    if (age > 2ULL * 10000000ULL && (LONGLONG)age > 0) { allowed = DEV_ALL; return; }
    if (CompareFileTime(&a.ftLastWriteTime, &lastWrite) == 0) return;
    lastWrite = a.ftLastWriteTime;
    FILE *f = _wfopen(inputPath, L"r");
    if (!f) return;
    char line[64] = {0};
    int before = allowed;
    if (fgets(line, sizeof line, f)) allowed = ParseDevice(line);
    fclose(f);
    if (allowed != before) menuOpen = 0;   /* another owner's bot selected: back in the game */
}

/* Pause menu and other menus: everyone may navigate them.
 *  - the tactics world is not updated (paused, menus, loading): NetWorldTick() older than 250 ms;
 *  - Start / Esc from any device opens "menu mode"; it ends with the next Start / Esc, a B press, a new owner in
 *    the control file or when the world runs again after a stall. */
static int Stalled(void)
{
    ULONGLONG tick = NetWorldTick(), now = GetTickCount64();
    int stalled = tick && now - tick > 250;
    if (wasStalled && !stalled) menuOpen = 0;
    wasStalled = stalled;
    return stalled;
}

/* Tutorial popups (mission-start tutorials, Lua TutorialPopup): shown while the world runs, so neither of the
 * above sees them. netplay.c hooks the game's popup Show; from then on everyone may play until somebody confirms or
 * cancels (A / B, Enter / Space / Esc, left click) — at least 0.4 s later, at most 2 minutes. */
static int Popup(void)
{
    ULONGLONG tick = NetPopupTick(), now = GetTickCount64();
    if (tick && tick != popupSeen) { popupSeen = tick; popupOpen = 1; popupSince = now; }
    if (popupOpen && now - popupSince > 120000) popupOpen = 0;
    return popupOpen;
}

static void PopupKey(void)
{
    if (popupOpen && GetTickCount64() - popupSince > 400) popupOpen = 0;
}

/* Menu mode lasts 0.6 s past its end: the press that ends it is released later, and UI buttons fire on the release
 * (the popup "closed" on the press, the release was muted, its button never fired). */
static ULONGLONG menuLastOn;
static int MenuMode(void)
{
    ULONGLONG now = GetTickCount64();
    if (menuOpen || Popup() || Stalled()) menuLastOn = now;
    return menuLastOn && now - menuLastOn < 600;
}

static void MenuKey(int open)   /* open: Start/Esc pressed (toggles); 0: B pressed (closes) */
{
    ULONGLONG now = GetTickCount64();
    if (now - lastMenuKey < 200) return;   /* the same press seen by polling and by events */
    lastMenuKey = now;
    menuOpen = open ? !menuOpen : 0;
}

static int Allowed(int dev) { return allowed == DEV_ALL || allowed == dev || MenuMode(); }

static void Blocked(int dev)
{
    ULONGLONG now = GetTickCount64();
    if (now - lastBlockedWrite < 500) return;   /* one report per press burst */
    lastBlockedWrite = now;
    FILE *f = _wfopen(blockedPath, L"w");
    if (f) { fprintf(f, "%u %d\n", ++blockedCount, dev); fclose(f); }
}

/* Pad number (1-based) of a controller instance id: its rank among the controllers polled in the last 1.5 s. */
static int PadNumber(int id, void *gc)
{
    ULONGLONG now = GetTickCount64();
    int slot = -1, free = -1, active = 0, rank = 1;
    for (int i = 0; i < MAX_PADS; i++)
    {
        if (pads[i].seen && pads[i].id == id) slot = i;
        else if (!pads[i].seen || now - pads[i].seen > 1500) { if (free < 0) free = i; }
    }
    if (slot < 0) { if (free < 0) return MAX_PADS; slot = free; pads[slot].id = id; }
    if (gc) pads[slot].seen = now;
    for (int i = 0; i < MAX_PADS; i++)
        if (pads[i].seen && now - pads[i].seen <= 1500)
        {
            active++;
            if (pads[i].id < id) rank++;
        }
    if (gc && active != padsWritten)
    {
        /* Lets the players find their numbers: logged when the set of controllers changes. */
        padsWritten = active;
        Log("controller %d (%s) is pad%d, %d connected", id, sdlControllerName ? sdlControllerName(gc) : "?", rank, active);
    }
    return rank;
}

static int PadOf(void *gc)
{
    if (!gc || !sdlGetJoystick || !sdlInstanceID) return MAX_PADS;
    return PadNumber(sdlInstanceID(sdlGetJoystick(gc)), gc);
}

/* Autopilot gamepad press (see the autopilot below): this button reads as held on every pad until the time. */
static int autoPadButton = -1;
static ULONGLONG autoPadUntil;

static unsigned char __cdecl HookGetButton(void *gc, int button)
{
    unsigned char v = realGetButton(gc, button);
    if (button == autoPadButton && GetTickCount64() < autoPadUntil) v = 1;
    int menu = MenuMode();            /* the press that closes a menu / popup still reaches it */
    if (button == 6 || button == 1 || button == 0)   /* START, B, A: rising edges per pad drive menu / popup mode */
    {
        int pad = PadOf(gc);
        unsigned char *prev = button == 6 ? startPrev : button == 1 ? bPrev : aPrev;
        if (pad < MAX_PADS)
        {
            if (v && !prev[pad])
            {
                if (button == 6) MenuKey(1);
                else { PopupKey(); if (button == 1 && menuOpen) MenuKey(0); }
            }
            prev[pad] = v;
        }
    }
    if (!v || AlwaysButton(button)) return v;
    Refresh();
    int pad = PadOf(gc);
    if (menu || Allowed(pad)) return v;
    Blocked(pad);
    return 0;
}

static short __cdecl HookGetAxis(void *gc, int axis)
{
    short v = realGetAxis(gc, axis);
    if (AlwaysAxis(axis)) return v;
    Refresh();
    int pad = PadOf(gc);
    if (Allowed(pad)) return v;
    if (axis >= 4 ? v > 16000 : (v > 24000 || v < -24000)) Blocked(pad);  /* trigger pulled / stick pushed */
    return 0;
}

/*
 * Autopilot for automated netplay test runs (netTest builds only): commands appended to
 * <data folder>\swh2_net_auto.txt are run in order, the file is polled 5 times a second (lines already run are skipped,
 * so a test driver can append while the game runs; the file is emptied at game start). Keys are injected as SDL key
 * events into the game's own event stream (works with the window in the background, touches no other program):
 *   key <name>       press and release: return escape up down left right space tab q e 1..6
 *   pad <button>     gamepad press on every connected pad: a b x y back start ls rs lb rb up down left right
 *   wait <ms>
 *   enter <ms>       press return every <ms> until a mission world runs (intro, title, slot list: the cursor stands
 *                    on the most recent save, loading): no fixed timings, however long the intro takes
 *   popups on|off    confirm tutorial popups (return) 1.5 s after they are shown
 *   cutscenes on|off click through dialogue: return once a second while the mission Lua says one is on
 *                    (swh2_net_cutscene.txt = 1, coop.lua C.netCutscene)
 * Every command run is logged to swh2_coop_input.log ("auto: ...").
 */
static wchar_t autoPath[MAX_PATH], cutscenePath[MAX_PATH];
static int autoCutscenes, autoEnterMs, autoOver;   /* autoOver: "over" waits for the mission's end in the netplay log */
static ULONGLONG autoEnterPressed, autoEnterSince;   /* "enter": a battle that runs after the command (not the last one) */
static ULONGLONG autoCutPolled, autoCutPressed;
static int autoOn, autoDone, autoPopups, autoKeySc, autoKeySym, autoPhase, windowId = 1;
static ULONGLONG autoNext, autoPolled, autoPopupSeen, autoKeyUntil;
/* After "quit": no keys (popups / cutscenes) until a battle runs again, the dll's Continue brings it back. */
static ULONGLONG autoQuitAt;
static int autoQuietPopups, autoQuietCutscenes;

static int KeyByName(const char *n, int *sym)
{
    static const struct { const char *name; int sc, sym; } keys[] = {
        { "return", 40, 13 }, { "escape", 41, 27 }, { "space", 44, 32 }, { "tab", 43, 9 }, { "up", 82, 0x40000052 },
        { "down", 81, 0x40000051 }, { "left", 80, 0x40000050 }, { "right", 79, 0x4000004f }, { "q", 20, 'q' }, { "e", 8, 'e' },
        { "1", 30, '1' }, { "2", 31, '2' }, { "3", 32, '3' }, { "4", 33, '4' }, { "5", 34, '5' }, { "6", 35, '6' } };
    for (int i = 0; i < (int)(sizeof keys / sizeof keys[0]); i++)
        if (strcmp(n, keys[i].name) == 0) { *sym = keys[i].sym; return keys[i].sc; }
    return 0;
}

static void AutoStartKey(int sc, int sym) { autoKeySc = sc; autoKeySym = sym; autoPhase = 1; }

/* Next command from the file (if it is time). */
static void AutoStep(void)
{
    ULONGLONG now = GetTickCount64();
    if (autoQuitAt && NetWorldTick() > autoQuitAt + 5000)
    {
        autoQuitAt = 0;
        autoPopups = autoQuietPopups;
        autoCutscenes = autoQuietCutscenes;
        autoPopupSeen = NetPopupTick();
        Log("auto: in a battle again");
    }
    if (autoPopups && autoPhase == 0)
    {
        ULONGLONG shown = NetPopupTick();
        if (shown && shown != autoPopupSeen && now - shown > 1500) { autoPopupSeen = shown; Log("auto: popup confirmed"); AutoStartKey(40, 13); }
    }
    if (autoCutscenes && autoPhase == 0 && now - autoCutPolled >= 200)
    {
        autoCutPolled = now;
        FILE *c = _wfopen(cutscenePath, L"r");
        int on = c && fgetc(c) == '1';
        if (c) fclose(c);
        if (on && now - autoCutPressed >= 1000) { autoCutPressed = now; AutoStartKey(40, 13); return; }
    }
    if (autoEnterMs && autoPhase == 0)
    {
        if (NetWorldTick() > autoEnterSince) { autoEnterMs = 0; Log("auto: in the mission"); }
        else
        {
            if (now - autoEnterPressed >= (ULONGLONG)autoEnterMs) { autoEnterPressed = now; AutoStartKey(40, 13); }
            return;
        }
    }
    if (autoOver && autoPhase == 0)
    {
        if (now - autoPolled < 1000) return;
        autoPolled = now;
        if (!NetLogHas("# mission over")) return;
        autoOver = 0;
        Log("auto: mission over");
    }
    if (autoPhase || now < autoNext || now - autoPolled < 200) return;
    autoPolled = now;
    FILE *f = _wfopen(autoPath, L"r");
    if (!f) return;
    char line[128];
    int n = 0;
    while (fgets(line, sizeof line, f))
    {
        if (n++ < autoDone) continue;
        autoDone++;
        char cmd[32] = "", arg[64] = "";
        if (sscanf(line, "%31s %63s", cmd, arg) < 1 || cmd[0] == '#') continue;
        Log("auto: %s %s", cmd, arg);
        if (strcmp(cmd, "wait") == 0) { autoNext = now + (ULONGLONG)atoi(arg); break; }
        if (strcmp(cmd, "enter") == 0) { autoEnterMs = atoi(arg) > 0 ? atoi(arg) : 2500; autoEnterPressed = now; autoEnterSince = now + 1000; break; }
        if (strcmp(cmd, "over") == 0) { autoOver = 1; break; }
        if (strcmp(cmd, "quit") == 0)
        {
            autoQuitAt = now;
            autoQuietPopups = autoPopups;
            autoQuietCutscenes = autoCutscenes;
            autoPopups = autoCutscenes = 0;
            NetQuitToMenu();
            continue;
        }
        if (strcmp(cmd, "checkpoint") == 0) { NetLoadCheckpoint(); continue; }
        if (strcmp(cmd, "cutscenes") == 0) { autoCutscenes = strcmp(arg, "on") == 0; continue; }
        if (strcmp(cmd, "popups") == 0) { autoPopups = strcmp(arg, "on") == 0; autoPopupSeen = NetPopupTick(); continue; }
        if (strcmp(cmd, "pad") == 0)
        {
            static const char *names[] = { "a", "b", "x", "y", "back", "guide", "start", "ls", "rs", "lb", "rb", "up", "down", "left", "right" };
            int b = -1;
            for (int i = 0; i < 15; i++) if (strcmp(arg, names[i]) == 0) b = i;
            if (b >= 0) { autoPadButton = b; autoPadUntil = now + 150; autoNext = now + 450; break; }
        }
        if (strcmp(cmd, "key") == 0)
        {
            int sym, sc = KeyByName(arg, &sym);
            if (sc) { AutoStartKey(sc, sym); break; }
        }
        Log("auto: unknown command");
    }
    fclose(f);
}

/* An injected key event (down, then up a few frames later), or 0. */
static int AutoEvent(void *ev)
{
    if (!autoOn) return 0;
    static ULONGLONG focusSent;
    ULONGLONG t = GetTickCount64();
    if (t - focusSent > 2000)
    {
        /* The game ignores keys without focus: it is told it has it (SDL_WINDOWEVENT_FOCUS_GAINED), and "focus lost"
         * events are dropped (HookPollEvent), so the test runs with the window in the background. */
        focusSent = t;
        memset(ev, 0, 56);
        *(unsigned *)ev = 0x200;
        *(unsigned *)((char *)ev + 4) = (unsigned)t;
        *(unsigned *)((char *)ev + 8) = (unsigned)windowId;
        *((unsigned char *)ev + 12) = 12;
        return 1;
    }
    AutoStep();
    if (autoPhase == 0) return 0;
    ULONGLONG now = GetTickCount64();
    if (autoPhase == 2 && now < autoKeyUntil) return 0;
    memset(ev, 0, 56);
    unsigned char *e = (unsigned char *)ev;
    *(unsigned *)e = autoPhase == 1 ? 0x300 : 0x301;
    *(unsigned *)(e + 4) = (unsigned)now;
    *(unsigned *)(e + 8) = (unsigned)windowId;
    e[12] = autoPhase == 1;
    *(int *)(e + 16) = autoKeySc;
    *(int *)(e + 20) = autoKeySym;
    if (autoPhase == 1) { autoPhase = 2; autoKeyUntil = now + 100; }
    else { autoPhase = 0; if (autoNext < now + 300) autoNext = now + 300; }   /* a running "wait" stays */
    return 1;
}

/* SDL_Event: Uint32 type at 0; keyboard: scancode at 16; controller/joystick: instance id ("which") at 8. */
static int __cdecl HookPollEvent(void *ev)
{
    NetIdle();
    if (ev && AutoEvent(ev)) return 1;
    for (;;)
    {
        int r = realPollEvent(ev);
        if (r && ev && (*(unsigned *)ev == 0x200 || *(unsigned *)ev == 0x300)) windowId = *(int *)((char *)ev + 8);
        /* Test runs play in the background, minimized by the driver: focus lost (13), hidden (2) and minimized (6)
         * never reach the game, so it does not pause itself. */
        if (r && ev && autoOn && *(unsigned *)ev == 0x200)
        {
            unsigned char we = *((unsigned char *)ev + 12);
            if (we == 13 || we == 2 || we == 6) continue;
        }
        if (!r || !ev) return r;
        Refresh();
        unsigned type = *(unsigned *)ev;
        /* Test runs need no mouse, and the player's real one must not steer them: hovering the window put the title
         * menu cursor on Options, and the autopilot's Return opened the settings (the run hung there). The game takes
         * the pointer position only from these events (SDL_GetMouseState is read for the buttons alone). */
        if (autoOn && type >= 0x400 && type <= 0x403) continue;
        int menu = MenuMode();
        /* Key down (not a repeat: SDL_KeyboardEvent.repeat at 13): Esc toggles the menu mode; Enter / Space / Esc and
         * a left click close a tutorial popup. */
        if (type == 0x300 && !*((unsigned char *)ev + 13))
        {
            int sc = *(int *)((char *)ev + 16);
            if (sc == 41) MenuKey(1);
            if (sc == 40 || sc == 44 || sc == 41) PopupKey();
        }
        if (type == 0x401 && *((unsigned char *)ev + 16) == 1) PopupKey();   /* SDL_MouseButtonEvent.button 1 = left */
        if (allowed == DEV_ALL || menu) return r;
        if (type == 0x300 || type == 0x301)                  /* KEYDOWN / KEYUP */
        {
            int sc = *(int *)((char *)ev + 16);
            if (Allowed(DEV_KBM) || AlwaysKey(sc)) return r;
            if (type == 0x300) Blocked(DEV_KBM);
            continue;
        }
        if (type == 0x302 || type == 0x303)                  /* TEXTEDITING / TEXTINPUT */
        {
            if (Allowed(DEV_KBM)) return r;
            continue;
        }
        if (type >= 0x400 && type <= 0x403)                  /* mouse motion / buttons / wheel */
        {
            if (Allowed(DEV_KBM)) return r;
            if (type == 0x401) Blocked(DEV_KBM);
            continue;
        }
        if ((type >= 0x600 && type <= 0x604) || (type >= 0x650 && type <= 0x652))  /* joystick / controller input */
        {
            int pad = PadNumber(*(int *)((char *)ev + 8), NULL);
            if (Allowed(pad)) return r;
            if (type == 0x651 && AlwaysButton(*((unsigned char *)ev + 12))) return r;
            if (type == 0x650 && AlwaysAxis(*((unsigned char *)ev + 12))) return r;
            continue;
        }
        return r;
    }
}

static const unsigned char *__cdecl HookGetKeyboardState(int *numkeys)
{
    const unsigned char *keys = realGetKeyboardState(numkeys);
    Refresh();
    int held = autoPhase == 2 && autoKeySc > 0 && autoKeySc < NUM_SCANCODES;   /* autopilot key: also in the key state */
    if (!keys || (Allowed(DEV_KBM) && !held)) return keys;
    for (int i = 0; i < NUM_SCANCODES; i++) keyCopy[i] = Allowed(DEV_KBM) || AlwaysKey(i) ? keys[i] : 0;
    if (held) keyCopy[autoKeySc] = 1;
    return keyCopy;
}

static unsigned int __cdecl HookGetMouseState(int *x, int *y)
{
    int rx, ry;
    unsigned int buttons = realGetMouseState(&rx, &ry);
    Refresh();
    if (Allowed(DEV_KBM)) { lastMouseX = rx; lastMouseY = ry; }
    else buttons = 0;
    if (x) *x = Allowed(DEV_KBM) ? rx : lastMouseX;
    if (y) *y = Allowed(DEV_KBM) ? ry : lastMouseY;
    return buttons;
}

/* Swaps one IAT slot after checking it really holds the expected SDL function. */
static void *Hook(char *base, DWORD steamRva, HMODULE sdl, const char *name, void *hook)
{
    DWORD rva = ExeRva(steamRva);
    if (!rva) { Log("ERROR: %s: import slot not known in this exe, not hooked", name); return NULL; }
    void **slot = (void **)(base + rva);
    void *real = (void *)GetProcAddress(sdl, name);
    if (!real || *slot != real) { Log("ERROR: %s: unexpected import slot, not hooked (unsupported game version?)", name); return NULL; }
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old)) { Log("ERROR: %s: VirtualProtect failed", name); return NULL; }
    *slot = hook;
    VirtualProtect(slot, sizeof(void *), old, &old);
    return real;
}

/*
 * Several copies of the game on one PC (multiplayer tests, e.g. Steam + GOG): the first one started is instance 1
 * and uses the data folder as before; instance N >= 2 gets <data folder>\instanceN\ for every swh2_* file. The number
 * is claimed with a named mutex (freed by Windows when the game exits) and published to the mods' Lua as the
 * environment variables SWH2_INSTANCE (= N) and SWH2_INSTANCE_DIR ("" or "instanceN\", appended to the data folder).
 * The game's Lua reads them with getenv of ucrtbase.dll, whose copy of the environment is separate from the process
 * one, so both are set.
 */
static int ClaimInstance(wchar_t *dataDir)
{
    int n = 1;
    for (; n < 16; n++)
    {
        wchar_t name[64];
        _snwprintf(name, 64, L"Local\\SWH2_GameInstance_%d", n);
        HANDLE m = CreateMutexW(NULL, FALSE, name);   /* kept open for the life of the game */
        if (m && GetLastError() != ERROR_ALREADY_EXISTS) break;
        if (m) CloseHandle(m);
    }
    char num[8], sub[24] = "";
    sprintf(num, "%d", n);
    if (n > 1)
    {
        sprintf(sub, "instance%d\\", n);
        size_t len = wcslen(dataDir);
        _snwprintf(dataDir + len, MAX_PATH - len, L"\\instance%d", n);
        CreateDirectoryW(dataDir, NULL);
    }
    SetEnvironmentVariableA("SWH2_INSTANCE", num);
    SetEnvironmentVariableA("SWH2_INSTANCE_DIR", n > 1 ? sub : NULL);
    typedef int (__cdecl *putenv_s_t)(const char *, const char *);
    HMODULE crt = GetModuleHandleW(L"ucrtbase.dll");
    putenv_s_t putenv_s = crt ? (putenv_s_t)GetProcAddress(crt, "_putenv_s") : NULL;
    if (putenv_s)
    {
        putenv_s("SWH2_INSTANCE", num);
        putenv_s("SWH2_INSTANCE_DIR", sub);   /* "" removes it */
    }
    return n;
}

static void Install(void)
{
    char *base = (char *)GetModuleHandleW(NULL);
    ExeMapInit(base);
    wchar_t dir[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"APPDATA", dir, MAX_PATH);
    if (!n || n >= MAX_PATH - 96) return;
    wcscat(dir, L"\\Thunderful\\SteamWorld Heist II");
    int instance = ClaimInstance(dir);
    _snwprintf(inputPath, MAX_PATH, L"%s\\swh2_coop_input.txt", dir);
    _snwprintf(blockedPath, MAX_PATH, L"%s\\swh2_coop_blocked.txt", dir);
    _snwprintf(logPath, MAX_PATH, L"%s\\swh2_coop_input.log", dir);
    NetInstall(base, dir);
    if (NetActive())
    {
        /* A test driver (swh2 nettest run) leaves the whole script in swh2_net_autorun.txt before starting the game;
         * otherwise commands of an earlier game session are not run again. Without one (a network game of real
         * players, a manual test) the autopilot stays off: it keeps real mouse events and "focus lost" from the game. */
        wchar_t autorun[MAX_PATH];
        _snwprintf(autoPath, MAX_PATH, L"%s\\swh2_net_auto.txt", dir);
        _snwprintf(cutscenePath, MAX_PATH, L"%s\\swh2_net_cutscene.txt", dir);
        DeleteFileW(cutscenePath);
        _snwprintf(autorun, MAX_PATH, L"%s\\swh2_net_autorun.txt", dir);
        if (MoveFileExW(autorun, autoPath, MOVEFILE_REPLACE_EXISTING)) autoOn = 1;
        else
        {
            FILE *f = _wfopen(autoPath, L"w");
            if (f) fclose(f);
        }
    }

    HMODULE sdl = GetModuleHandleW(L"SDL2.dll");
    if (!sdl || !base) { Log("ERROR: SDL2.dll not loaded"); return; }
    Log("%s exe, game instance %d", ExeBuildName(), instance);
    sdlGetJoystick = (GetJoystick_t)GetProcAddress(sdl, "SDL_GameControllerGetJoystick");
    sdlInstanceID = (InstanceID_t)GetProcAddress(sdl, "SDL_JoystickInstanceID");
    sdlControllerName = (ControllerName_t)GetProcAddress(sdl, "SDL_GameControllerName");

    /* Pointers first, then the slots: a hook may run on the game thread as soon as its slot is written. */
    realGetButton = (GetButton_t)GetProcAddress(sdl, "SDL_GameControllerGetButton");
    realGetAxis = (GetAxis_t)GetProcAddress(sdl, "SDL_GameControllerGetAxis");
    realPollEvent = (PollEvent_t)GetProcAddress(sdl, "SDL_PollEvent");
    realGetKeyboardState = (GetKeyboardState_t)GetProcAddress(sdl, "SDL_GetKeyboardState");
    realGetMouseState = (GetMouseState_t)GetProcAddress(sdl, "SDL_GetMouseState");
    int ok = 0;
    ok += Hook(base, IAT_ControllerGetButton, sdl, "SDL_GameControllerGetButton", (void *)HookGetButton) != NULL;
    ok += Hook(base, IAT_ControllerGetAxis, sdl, "SDL_GameControllerGetAxis", (void *)HookGetAxis) != NULL;
    ok += Hook(base, IAT_PollEvent, sdl, "SDL_PollEvent", (void *)HookPollEvent) != NULL;
    ok += Hook(base, IAT_GetKeyboardState, sdl, "SDL_GetKeyboardState", (void *)HookGetKeyboardState) != NULL;
    ok += Hook(base, IAT_GetMouseState, sdl, "SDL_GetMouseState", (void *)HookGetMouseState) != NULL;
    Log("input lock loaded, %d/5 hooks", ok);
}

BOOL WINAPI DllMain(HINSTANCE dll, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(dll);
        Install();
    }
    return TRUE;
}
