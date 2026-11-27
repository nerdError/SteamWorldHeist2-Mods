/* Offline test of the input lock filters with fake SDL functions: native\test.cmd (not part of the mod). */
#include "swh2coop.c"

static int fails;
#define CHECK(c) do { if (!(c)) { printf("FAIL line %d: %s\n", __LINE__, #c); fails++; } } while (0)

/* Three fake controllers: handle = &ids[i], SDL instance ids 7, 3, 12 -> pad2, pad1, pad3. */
static int ids[3] = {7, 3, 12};
static void *__cdecl FakeGetJoystick(void *gc) { return gc; }
static int __cdecl FakeInstanceID(void *js) { return *(int *)js; }
static const char *__cdecl FakeName(void *gc) { (void)gc; return "fake pad"; }
static unsigned char __cdecl FakeGetButton(void *gc, int b) { (void)gc; (void)b; return 1; }      /* everything held */
static short __cdecl FakeGetAxis(void *gc, int a) { (void)gc; (void)a; return 30000; }
static unsigned char realKeys[NUM_SCANCODES];
static const unsigned char *__cdecl FakeKeys(int *n) { if (n) *n = NUM_SCANCODES; return realKeys; }
static unsigned int __cdecl FakeMouse(int *x, int *y) { static int t; t += 10; if (x) *x = t; if (y) *y = t; return 1; }
static unsigned queue[8][14]; static int queued, taken;   /* SDL_Event = 56 bytes */
static int __cdecl FakePoll(void *ev) { if (taken >= queued) return 0; memcpy(ev, queue[taken++], 56); return 1; }
static void Push(unsigned type, int scancodeOrWhich, int button)
{
    memset(queue[queued], 0, 56);
    queue[queued][0] = type;
    if (type < 0x600) queue[queued][4] = scancodeOrWhich; else { queue[queued][2] = scancodeOrWhich; ((unsigned char *)queue[queued])[12] = (unsigned char)button; }
    queued++;
}
static int Drain(unsigned *types)
{
    unsigned ev[14]; int n = 0;
    while (HookPollEvent(ev)) types[n++] = ev[0];
    return n;
}

static void Write(const char *s)
{
    FILE *f = _wfopen(inputPath, L"w"); fputs(s, f); fclose(f);
    lastCheck = 0; memset(&lastWrite, 0, sizeof lastWrite);
}
static void Age(int seconds)
{
    HANDLE h = CreateFileW(inputPath, FILE_WRITE_ATTRIBUTES, 0, NULL, OPEN_EXISTING, 0, NULL);
    FILETIME t; GetSystemTimeAsFileTime(&t);
    ULONGLONG v = (((ULONGLONG)t.dwHighDateTime << 32) | t.dwLowDateTime) - (ULONGLONG)seconds * 10000000ULL;
    t.dwLowDateTime = (DWORD)v; t.dwHighDateTime = (DWORD)(v >> 32);
    SetFileTime(h, NULL, NULL, &t); CloseHandle(h);
    lastCheck = 0;
}
static int BlockedCounter(void)
{
    unsigned n = 0; int dev = -9; FILE *f = _wfopen(blockedPath, L"r");
    if (f) { if (fscanf(f, "%u %d", &n, &dev) != 2) n = 0; fclose(f); }
    lastBlockedWrite = 0;
    return (int)n;
}

int main(void)
{
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    _snwprintf(inputPath, MAX_PATH, L"%sswh2coop_test_input.txt", tmp);
    _snwprintf(blockedPath, MAX_PATH, L"%sswh2coop_test_blocked.txt", tmp);
    _snwprintf(logPath, MAX_PATH, L"%sswh2coop_test.log", tmp);
    DeleteFileW(inputPath); DeleteFileW(blockedPath);
    sdlGetJoystick = FakeGetJoystick; sdlInstanceID = FakeInstanceID; sdlControllerName = FakeName;
    realGetButton = FakeGetButton; realGetAxis = FakeGetAxis; realGetKeyboardState = FakeKeys;
    realGetMouseState = FakeMouse; realPollEvent = FakePoll;
    memset(realKeys, 1, sizeof realKeys);
    void *pad1 = &ids[1], *pad2 = &ids[0], *pad3 = &ids[2];

    /* No file: everyone plays. Polling all three registers them (the game polls every controller each frame). */
    CHECK(HookGetButton(pad2, 0) == 1 && HookGetButton(pad1, 0) == 1 && HookGetButton(pad3, 0) == 1);
    CHECK(PadOf(pad1) == 1 && PadOf(pad2) == 2 && PadOf(pad3) == 3);

    /* pad2's bot selected. */
    Write("pad2\n");
    CHECK(HookGetButton(pad2, 0) == 1);          /* owner: A */
    CHECK(HookGetButton(pad1, 0) == 0);          /* others: A muted */
    CHECK(BlockedCounter() == 1);
    CHECK(HookGetButton(pad1, 10) == 1);         /* RB (next bot) always works */
    CHECK(HookGetButton(pad3, 6) == 1);          /* Start always works... */
    CHECK(menuOpen && HookGetButton(pad1, 0) == 1);   /* ...and opens the menu mode: everyone navigates */
    lastMenuKey = 0;
    CHECK(HookGetButton(pad1, 1) == 1 && !menuOpen);  /* B closes it (and still reaches the menu) */
    CHECK(HookGetButton(pad1, 0) == 1);          /* the release still reaches the menu (0.6 s) */
    menuLastOn = 0;                              /* ...then: */
    CHECK(HookGetButton(pad1, 0) == 0);          /* locked again */
    lastMenuKey = 0;
    queued = taken = 0;
    Push(0x300, 41, 0);                          /* Esc down: passes (pause) and opens the menu mode */
    unsigned esc[4];
    CHECK(Drain(esc) == 1 && menuOpen);
    lastMenuKey = 0;
    queued = taken = 0;
    Push(0x300, 41, 0);                          /* Esc again: closes */
    CHECK(Drain(esc) == 1 && !menuOpen);
    menuLastOn = 0;
    BlockedCounter();
    CHECK(HookGetAxis(pad1, 0) == 0 && HookGetAxis(pad2, 0) == 30000);   /* left stick */
    CHECK(HookGetAxis(pad1, 2) == 30000);        /* right stick = camera */
    CHECK(HookGetAxis(pad3, 5) == 0);            /* right trigger (next action) */
    const unsigned char *k = HookGetKeyboardState(NULL);
    CHECK(k[20] == 1 && k[43] == 1 && k[30] == 1 && k[82] == 1);   /* Q, Tab, 1, Up */
    CHECK(k[44] == 0 && k[26] == 0 && k[40] == 0);                 /* Space, W, Return */
    int x1, y1, x2, y2;
    CHECK(HookGetMouseState(&x1, &y1) == 0);
    HookGetMouseState(&x2, &y2);
    CHECK(x1 == x2 && y1 == y2);                 /* cursor frozen */
    unsigned types[8];
    queued = taken = 0;
    Push(0x300, 44, 0);   /* Space down: dropped */
    Push(0x300, 8, 0);    /* E down: kept */
    Push(0x401, 0, 0);    /* mouse click: dropped */
    Push(0x651, 3, 0);    /* pad1 A (instance 3): dropped */
    Push(0x651, 7, 0);    /* pad2 A: kept */
    Push(0x651, 12, 9);   /* pad3 LB: kept */
    Push(0x100, 0, 0);    /* SDL_QUIT: kept */
    int n = Drain(types);
    CHECK(n == 4 && types[0] == 0x300 && types[1] == 0x651 && types[2] == 0x651 && types[3] == 0x100);

    /* Keyboard + mouse player's bot. */
    Write("kbm\n");
    CHECK(HookGetKeyboardState(NULL)[44] == 1);
    CHECK(HookGetMouseState(NULL, NULL) == 1);
    CHECK(HookGetButton(pad2, 0) == 0);

    /* Stale file (game paused / world map): everyone again. */
    Age(5);
    CHECK(HookGetButton(pad2, 0) == 1 && HookGetButton(pad1, 0) == 1);
    CHECK(HookGetKeyboardState(NULL)[44] == 1);

    /* "all" (enemy turn, cutscene). */
    Write("all\n");
    CHECK(HookGetButton(pad1, 0) == 1);

    /* pad2 unplugged: after 1.5 s pad3 becomes pad2. */
    Write("pad2\n");
    for (int i = 0; i < 3; i++) pads[i].seen -= (pads[i].id == 7) ? 2000 : 0;
    CHECK(PadOf(pad3) == 2 && HookGetButton(pad3, 0) == 1);

    DeleteFileW(inputPath); DeleteFileW(blockedPath); DeleteFileW(logPath);
    printf(fails ? "%d FAILED\n" : "all input lock tests passed\n", fails);
    return fails != 0;
}
