/*
 * SWH2 world map netplay (Mods/MapCoop): loaded by the game itself (exe patch on the SDL_CreateWindow call).
 * Step 1 (now): a probe that learns the world map at run time and logs it (swh2map.log next to the dll):
 *   - WorldMapSimulator::Update (0x6d3350, sim, &dt) runs every frame on the world map;
 *   - the class of every object it points at, from the exe's RTTI (vtable[-1] = complete object locator);
 *   - the scene ([[sim+0x3e0]+0x10]: +0xe18 player entity id, +0xc90 entity store), its component tables
 *     (store->tables[type id], each an EntityComponentTable<T> with RTTI, entities in an unordered_map at +0xd0);
 *   - ships (WorldMapActorComponent table): id, definition name, position.
 * Control file swh2map.txt next to the dll (read once at start): "probe" = log the probe; "auto" = press Return until
 * the world map runs (tests: title screen, Continue). Every hook checks the bytes it replaces (Steam and GOG, exemap.c).
 */
#define _CRT_SECURE_NO_WARNINGS
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include "netplay.h"

static char *base;
static SIZE_T imageSize;
static wchar_t logPath[MAX_PATH], dirPath[MAX_PATH];
static int probeOn, autoOn;

static void Log(const char *fmt, ...)
{
    FILE *f = _wfopen(logPath, L"a");
    if (!f) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a;
    va_start(a, fmt);
    vfprintf(f, fmt, a);
    va_end(a);
    fputc('\n', f);
    fclose(f);
}

/* ---- memory helpers (all reads guarded: the probe walks structures it doesn't know yet) ---- */

static int InImage(ULONG_PTR p) { return p >= (ULONG_PTR)base && p < (ULONG_PTR)base + imageSize; }

static int ReadPtr(const void *at, ULONG_PTR *out)
{
    __try { *out = *(const ULONG_PTR *)at; return 1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static int ReadMem(const void *at, void *out, size_t n)
{
    __try { memcpy(out, at, n); return 1; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

/* Class name of a polymorphic object from the exe's RTTI (".?AVWorldMapPlayerSimulator@imf@@" -> without ".?AV"),
 * or NULL. *vtRva gets the Steam RVA of its vtable. */
static const char *ClassOf(const void *obj, DWORD *vtRva)
{
    ULONG_PTR vt, col;
    if (!obj || !ReadPtr(obj, &vt) || !InImage(vt)) return NULL;
    if (!ReadPtr((const char *)vt - 8, &col) || !InImage(col)) return NULL;
    DWORD td;
    if (!ReadMem((const char *)col + 12, &td, 4) || td >= imageSize) return NULL;
    const char *name = base + td + 0x10;
    char head[4];
    if (!ReadMem(name, head, 4) || memcmp(head, ".?AV", 4) != 0) return NULL;
    if (vtRva) *vtRva = SteamRva((DWORD)(vt - (ULONG_PTR)base));
    return name + 4;
}

/* std::string (MSVC: 16-byte buffer or pointer, size +0x10, capacity +0x18) -> out. */
static void ReadStdString(const char *s, char *out, int max)
{
    out[0] = 0;
    size_t size, cap;
    if (!ReadMem(s + 0x10, &size, 8) || !ReadMem(s + 0x18, &cap, 8) || size >= 4096) return;
    const char *text = s;
    if (cap >= 16 && !ReadPtr(s, (ULONG_PTR *)&text)) return;
    if ((int)size >= max) size = max - 1;
    if (!ReadMem(text, out, size)) { out[0] = 0; return; }
    out[size] = 0;
}

/* ---- game functions (Steam RVAs) ---- */

typedef ULONG_PTR (*MapUpdate_t)(void *sim, void *dt);
typedef void *(*GetTable_t)(void *store);                 /* component table of one type */
typedef void *(*FindComponent_t)(void *table, int id);    /* unordered_map lookup: component or NULL */
typedef void *(*GetDefinition_t)(void *store, int id);    /* entity definition: +8 = std::string name */

#define RVA_MapUpdate 0x6d3350        /* WorldMapSimulator::Update */
#define RVA_PositionTable 0xd2580     /* GetComponentTable<PositionComponent>(store), used by Lua GetEntityPosition */
#define RVA_FindComponent 0xcdc90
#define RVA_GetDefinition 0x742d10    /* used by Lua GetEntityDefinition */
#define IAT_PollEvent 0xb47860

static MapUpdate_t realMapUpdate;
static GetTable_t positionTable;
static FindComponent_t findComponent;
static GetDefinition_t getDefinition;

static void *Fn(DWORD steamRva) { DWORD r = ExeRva(steamRva); return r ? base + r : NULL; }

/* ---- the probe ---- */

static ULONGLONG mapTick, mapSince, probeNext;
static int probeDumps;

static int EntityPos(void *store, int id, float *xy)
{
    __try
    {
        void *table = positionTable(store);
        char *c = table ? (char *)findComponent(table, id) : NULL;
        if (!c) return 0;
        memcpy(xy, c + 0x10, 8);
        return 1;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static void EntityDef(void *store, int id, char *out, int max)
{
    out[0] = 0;
    __try
    {
        char *d = (char *)getDefinition(store, id);
        if (d) ReadStdString(d + 8, out, max);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = 0; }
}

static void Hex(const void *p, int n, char *out)
{
    unsigned char b[256];
    if (n > 256) n = 256;
    if (!ReadMem(p, b, n)) { strcpy(out, "?"); return; }
    for (int i = 0; i < n; i++) sprintf(out + 3 * i, "%02x%c", b[i], (i % 16 == 15) ? '|' : ' ');
}

static void Probe(char *sim)
{
    ULONGLONG now = GetTickCount64();
    if (now < probeNext) return;
    if (probeDumps >= 3) { probeNext = now + 3000; }
    else probeNext = now + (probeDumps == 0 ? 0 : 20000);

    ULONG_PTR sub, sceneP;
    if (!ReadPtr(sim + 0x3e0, &sub) || !ReadPtr((char *)sub + 0x10, &sceneP)) { Log("probe: no scene"); return; }
    char *scene = (char *)sceneP;
    int player = 0;
    ReadMem(scene + 0xe18, &player, 4);
    void *store = scene + 0xc90;
    float xy[2] = { 0, 0 };
    int havePos = EntityPos(store, player, xy);

    if (probeDumps >= 3)
    {
        Log("player %d pos %s (%.1f, %.1f)", player, havePos ? "" : "?", xy[0], xy[1]);
        return;
    }
    probeDumps++;
    DWORD vt = 0;
    const char *cls = ClassOf(sim, &vt);
    Log("=== probe %d: sim %p class %s vt 0x%lx, scene %p, player id %d pos (%.1f, %.1f)%s", probeDumps, sim,
        cls ? cls : "?", vt, scene, player, xy[0], xy[1], havePos ? "" : " (no position)");
    for (int off = 8; off < 0x480; off += 8)
    {
        ULONG_PTR p;
        if (!ReadPtr(sim + off, &p) || !p) continue;
        cls = ClassOf((void *)p, &vt);
        if (!cls) continue;
        ULONG_PTR w;
        const char *wcls = ReadPtr((char *)p + 0x10, &w) ? ClassOf((void *)w, NULL) : NULL;
        Log("  sim+0x%03x %p %s vt 0x%lx%s%s", off, (void *)p, cls, vt, wcls ? "  [+0x10] " : "", wcls ? wcls : "");
    }
    ULONG_PTR tables;
    if (!ReadPtr(store, &tables)) return;
    Log("  store %p tables %p", store, (void *)tables);
    for (int i = 0; i < 400; i++)
    {
        ULONG_PTR t;
        if (!ReadPtr((char *)tables + 8 * i, &t)) break;
        if (!t) continue;
        cls = ClassOf((void *)t, &vt);
        if (!cls) continue;
        size_t size = 0;
        ReadMem((char *)t + 0xd8, &size, 8);
        Log("  table[%d] %s vt 0x%lx size %zu", i, cls, vt, size);
        if (!strstr(cls, "WorldMapActorComponent") && !strstr(cls, "WorldMapPlayerShipComponent") &&
            !strstr(cls, "WorldMapBehaviorComponent")) continue;
        ULONG_PTR head, node;
        if (!ReadPtr((char *)t + 0xd0, &head) || !ReadPtr((void *)head, &node)) continue;
        for (int n = 0; n < 80 && node && node != head; n++)
        {
            int id = 0;
            ReadMem((char *)node + 0x10, &id, 4);
            char def[96];
            EntityDef(store, id, def, sizeof def);
            float p2[2] = { 0, 0 };
            int ok = EntityPos(store, id, p2);
            Log("    id %d %s pos %s(%.1f, %.1f)", id, def, ok ? "" : "? ", p2[0], p2[1]);
            if (n < 2 || id == player)
            {
                char hex[1024];
                Hex((char *)node + 0x18, 0x100, hex);
                Log("      comp %s", hex);
            }
            if (!ReadPtr((void *)node, &node)) break;
        }
    }
    if (havePos)
    {
        char hex[1024];
        void *pc = NULL;
        __try { pc = findComponent(positionTable(store), player); } __except (EXCEPTION_EXECUTE_HANDLER) { pc = NULL; }
        if (pc) { Hex(pc, 0x60, hex); Log("  player position comp %s", hex); }
    }
}

static ULONG_PTR HookMapUpdate(void *sim, void *dt)
{
    ULONGLONG now = GetTickCount64();
    if (!mapTick || now - mapTick > 2000) { mapSince = now; Log("world map runs"); }
    mapTick = now;
    if (probeOn && now - mapSince > 4000) Probe((char *)sim);
    return realMapUpdate(sim, dt);
}

/* ---- autopilot for tests (SDL_PollEvent in the IAT, chained: Coop may have hooked it first) ---- */

typedef int (__cdecl *PollEvent_t)(void *ev);
static PollEvent_t prevPollEvent;
static ULONGLONG autoPressed, autoFocus;
static int autoPhase;

static int AutoEvent(void *ev)
{
    ULONGLONG now = GetTickCount64();
    unsigned char *e = (unsigned char *)ev;
    if (now - autoFocus > 2000)
    {
        /* The game ignores keys without focus: SDL_WINDOWEVENT_FOCUS_GAINED (window 1), "focus lost" is dropped. */
        autoFocus = now;
        memset(ev, 0, 56);
        *(unsigned *)e = 0x200; *(unsigned *)(e + 4) = (unsigned)now; *(unsigned *)(e + 8) = 1; e[12] = 12;
        return 1;
    }
    if (mapTick && now - mapTick < 1000) { if (autoPhase) { autoPhase = 0; Log("auto: on the world map"); } return 0; }
    if (autoPhase == 0 && now - autoPressed < 2500) return 0;
    if (autoPhase == 2 && now - autoPressed < 100) return 0;
    memset(ev, 0, 56);
    *(unsigned *)e = autoPhase == 2 ? 0x301 : 0x300;
    *(unsigned *)(e + 4) = (unsigned)now; *(unsigned *)(e + 8) = 1;
    e[12] = autoPhase != 2;
    *(int *)(e + 16) = 40; *(int *)(e + 20) = 13;   /* Return */
    if (autoPhase == 2) autoPhase = 1;
    else { autoPhase = 2; autoPressed = now; }
    return 1;
}

static int __cdecl HookPollEvent(void *ev)
{
    if (ev && autoOn && AutoEvent(ev)) return 1;
    for (;;)
    {
        int r = prevPollEvent(ev);
        if (r && ev && autoOn && *(unsigned *)ev == 0x200 && ((unsigned char *)ev)[12] == 13) continue;   /* focus lost */
        return r;
    }
}

/* ---- install ---- */

/* Inline hook: the first n bytes (whole, position-independent instructions, n >= 14) are checked, copied into a
 * trampoline that jumps back, and replaced by jmp [rip+0] <hook>. */
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

static int HookIat(DWORD steamRva, void *hook, void **prev)
{
    DWORD rva = ExeRva(steamRva);
    if (!rva) return 0;
    void **slot = (void **)(base + rva);
    DWORD old;
    if (!VirtualProtect(slot, 8, PAGE_READWRITE, &old)) return 0;
    *prev = *slot;
    *slot = hook;
    VirtualProtect(slot, 8, old, &old);
    return 1;
}

static void ReadControl(void)
{
    wchar_t path[MAX_PATH];
    _snwprintf(path, MAX_PATH, L"%s\\swh2map.txt", dirPath);
    FILE *f = _wfopen(path, L"r");
    if (!f) return;
    char line[256];
    while (fgets(line, sizeof line, f))
    {
        char cmd[32] = "";
        if (sscanf(line, "%31s", cmd) < 1) continue;
        if (strcmp(cmd, "probe") == 0) probeOn = 1;
        else if (strcmp(cmd, "auto") == 0) autoOn = 1;
    }
    fclose(f);
}

static void Install(HINSTANCE dll)
{
    base = (char *)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    imageSize = nt->OptionalHeader.SizeOfImage;
    ExeMapInit(base);
    GetModuleFileNameW(dll, dirPath, MAX_PATH);
    wchar_t *slash = wcsrchr(dirPath, L'\\');
    if (slash) *slash = 0;
    _snwprintf(logPath, MAX_PATH, L"%s\\swh2map.log", dirPath);
    DeleteFileW(logPath);
    ReadControl();
    Log("%s exe, probe %d, auto %d", ExeBuildName(), probeOn, autoOn);

    positionTable = (GetTable_t)Fn(RVA_PositionTable);
    findComponent = (FindComponent_t)Fn(RVA_FindComponent);
    getDefinition = (GetDefinition_t)Fn(RVA_GetDefinition);
    static const unsigned char mapUpdate[] = { 0x40, 0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xec, 0x80, 0x00, 0x00, 0x00 };
    realMapUpdate = (MapUpdate_t)InlineHook(RVA_MapUpdate, mapUpdate, sizeof mapUpdate, (void *)HookMapUpdate);
    Log("map update hook %s", realMapUpdate ? "ok" : "FAILED");
    if (autoOn) Log("autopilot hook %s", HookIat(IAT_PollEvent, (void *)HookPollEvent, (void **)&prevPollEvent) ? "ok" : "FAILED");
}

BOOL WINAPI DllMain(HINSTANCE dll, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(dll);
        Install(dll);
    }
    return TRUE;
}
