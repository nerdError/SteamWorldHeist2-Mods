/* Offline test of the netplay recorder (netplay.c) on a fake exe image: native\test.cmd (not part of the mod). */
#include "netplay.c"
#include "nettcp.c"
#include "exemap.c"   /* never initialised: identity map (Steam addresses) */

static int fails;
#define CHECK(c) do { if (!(c)) { printf("FAIL line %d: %s\n", __LINE__, #c); fails++; } } while (0)

static void *fakeStorage;
static void *FakeStorage(void *components, void *out) { (void)components; ((void **)out)[0] = fakeStorage; return out; }

static int Roundtrip(int type, const unsigned char *data, int len)
{
    unsigned char t[TARGET_SIZE], out[64];
    int type2 = -9;
    if (!NetBuildTarget(t, type, data, len)) return 0;
    int n = NetTargetBytes(t, out, sizeof out, &type2);
    NetFreeTarget(t);
    return n == len && type2 == type && memcmp(out, data, len) == 0;
}

/* A loader's request on the test port: sends the line, returns how much came back before the host closed. */
static int Ask(const char *line, char *reply, int max)
{
    SOCKET c = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_port = htons(27199);
    inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
    if (connect(c, (struct sockaddr *)&a, sizeof a)) { closesocket(c); return -1; }
    send(c, line, (int)strlen(line), 0);
    int len = 0, k;
    while (len < max && (k = recv(c, reply + len, max - len, 0)) > 0) len += k;
    closesocket(c);
    return len;
}

static int ReadAll(const wchar_t *path, char *out, int max)
{
    FILE *f = _wfopen(path, L"rb");
    int n = 0;
    if (f) { n = (int)fread(out, 1, max - 1, f); fclose(f); }
    out[n] = 0;
    return n;
}

/* Remote aim: fakes of the game's sight line functions, counting calls. */
static int traceMakes, traceAims, ptrResets, animOn, animOff, lastValid;
static char fakeTrace[16], fakeBody[16], fakeAnim[16], fakeWeapon[16], fakeAction[16];
static ULONG_PTR fakeMapEnd = 99, fakeIdx[40 / sizeof(ULONG_PTR) + 8];
static void *fakeDefs[4];
static void *FakeStore(void *components) { (void)components; return fakeBody; }
static void *FakeComponent(void *storage, int id) { (void)storage; (void)id; return fakeAnim; }
static void *FakeWeaponDef(void *unused, void *name) { (void)unused; (void)name; return fakeWeapon; }
static ULONG_PTR FakeMapFind(void *map, void *view) { (void)map; return ((View *)view)->n == 5 ? 0 : 99; }
static void *FakeTraceMake(void *body, void *actor, void *weapon, void *action, float a)
{ (void)body; (void)a; traceMakes += weapon == fakeWeapon && action == fakeAction; *(void **)((char *)actor + 0xf8) = fakeTrace; return fakeTrace; }
static void FakeTraceAim(void *body, void *actor, void *trace, float a, unsigned char valid)
{ (void)body; (void)actor; (void)a; traceAims += trace == fakeTrace; lastValid = valid; }
static void FakePtrReset(void *p) { ptrResets++; *(void **)p = NULL; }
static void FakeAnimInput(void *unused, void *anim, void *view, unsigned char on)
{ (void)unused; (void)anim; if (((View *)view)->n == 3) { if (on) animOn++; else animOff++; } }
static unsigned char FakeIdle(void *sim) { (void)sim; return 0; }
static void FakeAimUpdate(void *ui, float *dt, void *state) { (void)ui; (void)dt; (void)state; }

static int LastLine(char *line, int max)
{
    FILE *f = _wfopen(outPath, L"r");
    int ok = 0;
    if (!f) return 0;
    while (fgets(line, max, f)) ok = 1;
    fclose(f);
    return ok;
}

int main(void)
{
    /* Origins from return addresses. */
    DWORD player[] = { 0x38c900, 0x459474, 0x456000 }, ai[] = { 0x43c5ce }, preview[] = { 0x38c000, 0x454bfa, 0x459474 };
    DWORD lua[] = { 0x38d43d, 0x3195f0 }, other[] = { 0x100000 };
    CHECK(NetClassify(player, 3) == ORIGIN_PLAYER);
    CHECK(NetClassify(ai, 1) == ORIGIN_AI);
    CHECK(NetClassify(preview, 3) == ORIGIN_PREVIEW);
    CHECK(NetClassify(lua, 2) == ORIGIN_LUA);
    CHECK(NetClassify(other, 1) == ORIGIN_OTHER);

    /* Targets: none, position, actor id, angle, path (8 bytes + 2 nodes), list. */
    unsigned char d[64];
    for (int i = 0; i < 64; i++) d[i] = (unsigned char)(i * 7 + 1);
    CHECK(Roundtrip(0, d, 0));
    CHECK(Roundtrip(1, d, 8));
    CHECK(Roundtrip(2, d, 4));
    CHECK(Roundtrip(3, d, 4));
    CHECK(!Roundtrip(4, d, 8 + 32));   /* node padding (last 4 bytes of each node) comes back zeroed... */
    for (int k = 8 + 12; k < 8 + 32; k += 16) memset(d + k, 0, 4);
    CHECK(Roundtrip(4, d, 8 + 32));    /* ...the rest round-trips */
    CHECK(Roundtrip(4, d, 8));
    /* The main world is no preview even with outcomesimulator.cpp on the stack (run 6: lost enemy moves). */
    CHECK(NetClassifyReal(preview, 3, 1) == ORIGIN_PLAYER);
    CHECK(Roundtrip(5, d, 24));
    unsigned char t[TARGET_SIZE];
    CHECK(!NetBuildTarget(t, 4, d, 8 + 15));   /* not whole nodes */
    CHECK(!NetBuildTarget(t, 2, d, 8));

    /* Fake image: RequestAction = the real prologue + "inc [counter]" + epilogue, at its real RVA. */
    base = (char *)VirtualAlloc(NULL, 0x1000000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    CHECK(base != NULL);
    if (!base) return 1;
    static const unsigned char prologue[] = { 0x40,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x56,0x41,0x57,0x48,0x8d,0xac,0x24,0xd0,0xfe,0xff,0xff };
    static const unsigned char epilogue[] = { 0x41,0x5f,0x41,0x5e,0x41,0x5c,0x5f,0x5e,0x5b,0x5d,0xc3 };
    unsigned char *fn = (unsigned char *)base + RVA_RequestAction;
    int *counter = (int *)(base + 0x100);
    memcpy(fn, prologue, sizeof prologue);
    unsigned char *inc = fn + sizeof prologue;
    inc[0] = 0xff; inc[1] = 0x05;
    *(int *)(inc + 2) = (int)((char *)counter - (char *)(inc + 6));
    memcpy(inc + 6, epilogue, sizeof epilogue);

    unsigned rng[2] = { 0x12345678, 0x9abcdef0 };
    *(unsigned **)(base + RVA_RngPtr) = rng;
    atRngPtr = base + RVA_RngPtr;

    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    _snwprintf(outPath, MAX_PATH, L"%sswh2_net_test.log", tmp);
    DeleteFileW(outPath);

    unsigned char wrong[sizeof prologue];
    memcpy(wrong, prologue, sizeof prologue);
    wrong[3] ^= 1;
    CHECK(InlineHook(RVA_RequestAction, wrong, sizeof wrong, (void *)HookRequestAction) == NULL);   /* checked first */
    realRequestAction = (RequestAction_t)InlineHook(RVA_RequestAction, prologue, sizeof prologue, (void *)HookRequestAction);
    CHECK(realRequestAction != NULL);
    if (!realRequestAction) return 1;

    /* Actor: handle -> id 77; actions vector with one std::string "Shoot_Long_Name_X" (heap) at index 1. */
    struct { int id; } handle = { 77 };
    unsigned char actions[2 * 0x38] = {0};
    const char *heapName = "Shoot_Long_Name_X";
    *(const char **)(actions + 0x38) = heapName;
    *(size_t *)(actions + 0x38 + 0x10) = strlen(heapName);
    *(size_t *)(actions + 0x38 + 0x18) = 31;
    memcpy(actions, "move", 5);
    *(size_t *)(actions + 0x10) = 4;
    *(size_t *)(actions + 0x18) = 15;
    unsigned char actor[0x200] = {0};
    *(void **)actor = &handle;
    *(unsigned char **)(actor + 0x50) = actions;
    *(unsigned char **)(actor + 0x58) = actions + sizeof actions;

    /* A call through the patched function: hook logs it, trampoline runs the original. */
    float angle = 1.5f;
    unsigned char target[TARGET_SIZE];
    NetBuildTarget(target, 3, (unsigned char *)&angle, 4);
    ((RequestAction_t)fn)(NULL, actor, 1, target, 0);
    CHECK(*counter == 1);
    char line[512];
    CHECK(LastLine(line, sizeof line));
    static const char expected[] = "cmd 1 o=other a=77 ao=-1 i=1 n=Shoot_Long_Name_X t=3:0000c03f to=-1 fl=0 r=12345678:9abcdef0 e=-1 d=0 f=0 w=0 pv=0 bt=";
    CHECK(strncmp(line, expected, sizeof expected - 1) == 0);   /* unknown origin: return addresses follow */

    /* The line parses back to the same command. */
    Entry *e = (Entry *)malloc(NetEntrySize());
    CHECK(NetParseLine(line, e) == ORIGIN_OTHER);
    CHECK(e->kind == 'c' && e->actor == 77 && e->index == 1 && e->type == 3 && e->dataLen == 4 && memcmp(e->data, &angle, 4) == 0);
    CHECK(e->z == 0x12345678 && e->w == 0x9abcdef0 && e->flag == 0 && strcmp(e->name, "Shoot_Long_Name_X") == 0);

    /* A path with the flag set, short (in-place) action name. */
    unsigned char path[8 + 32];
    for (int i = 0; i < (int)sizeof path; i++) path[i] = (unsigned char)(0xa0 + i);
    NetBuildTarget(target, 4, path, sizeof path);
    for (int k = 8 + 12; k < (int)sizeof path; k += 16) memset(path + k, 0, 4);   /* node padding: zeroed in logs */
    ((RequestAction_t)fn)(NULL, actor, 0, target, 1);
    NetFreeTarget(target);
    CHECK(*counter == 2);
    CHECK(LastLine(line, sizeof line));
    CHECK(strstr(line, " n=move t=4:a0a1") != NULL && strstr(line, " fl=1 ") != NULL);
    CHECK(NetParseLine(line, e) == ORIGIN_OTHER && e->type == 4 && e->dataLen == (int)sizeof path && memcmp(e->data, path, sizeof path) == 0);
    CHECK(SameCommand(e, 77, -1, 0, 4, path, sizeof path, -1, 1) && !SameCommand(e, 77, -1, 1, 4, path, sizeof path, -1, 1));
    CHECK(!SameCommand(e, 77, -1, 0, 4, path, sizeof path, -1, 0));   /* the flag counts too */

    /* Actor ordinals: actor 190 locally is what the record calls ordinal 1; actor targets are translated too. */
    ordLen = 0;
    ordIds[ordLen++] = 41; ordIds[ordLen++] = 190; ordIds[ordLen++] = 42;
    CHECK(OrdinalOf(190) == 1 && OrdinalOf(5) == -1 && IdOfOrdinal(2, -7) == 42 && IdOfOrdinal(9, -7) == -7);
    CHECK(NetParseLine("cmd 9 o=ai a=209 ao=1 i=3 n=melee t=2:d1000000 to=2 fl=0 r=1:2 e=300 d=0 f=5 pv=0\n", e) == ORIGIN_AI);
    CHECK(e->actor == 209 && e->ao == 1 && e->to == 2 && e->index == 3 && e->type == 2 && strcmp(e->name, "melee") == 0);
    unsigned char local[8];
    LocalTargetData(e, local);
    CHECK(*(int *)local == 42);   /* recorded 0xd1 = 209 -> ordinal 2 -> local 42 */
    int recordedTarget = 0xd1;
    CHECK(SameCommand(e, 190, 1, 3, 2, (unsigned char *)&recordedTarget, 4, 2, 0));    /* same ordinals, other ids */
    CHECK(!SameCommand(e, 190, 0, 3, 2, (unsigned char *)&recordedTarget, 4, 2, 0));
    CHECK(NetParseLine("cmd 9 o=ai a=209 i=3 n=melee t=2:d1000000 fl=0 r=1:2\n", e) == ORIGIN_AI && e->ao == -1 && e->to == -1);
    /* An enemy done for the turn (HookEndActor): index -1, no target. */
    CHECK(NetParseLine("cmd 10 o=ai a=47 ao=4 i=-1 n=@done t=0: to=-1 fl=0 r=1:2 e=300 d=0 f=5 pv=0\n", e) == ORIGIN_AI);
    CHECK(e->kind == 'c' && e->index == INDEX_DONE && e->type == 0 && e->dataLen == 0 && e->ao == 4);
    ordLen = 0;

    /* ScanActors on a fake world: list 177,176,175 (spawned in one frame) gets ordinals in id order. */
    {
        static int ids[4] = { 177, 176, 175, 300 };
        static void *nodes[5][8];   /* [0] = next; the actor is at +0x18, its first field points at the id */
        for (int i = 0; i < 3; i++) { nodes[i + 1][0] = i < 2 ? nodes[i + 2] : nodes[0]; nodes[i + 1][3] = &ids[i]; }
        nodes[0][0] = nodes[1];   /* sentinel -> first */
        static char storage[0x100], scanWorld[0x2000];
        *(void **)(storage + 0xd0) = nodes[0];
        atActorStorage = (char *)FakeStorage;
        fakeStorage = storage;
        void *sim2[4] = { NULL, NULL, scanWorld, NULL };
        ScanActors(sim2);
        CHECK(ordLen == 3 && ordIds[0] == 175 && ordIds[1] == 176 && ordIds[2] == 177);
        ScanActors(sim2);
        CHECK(ordLen == 3);   /* known actors keep their numbers */
        ordLen = 0;
        ordSim = NULL;
    }

    CHECK(NetParseLine("end 5 o=player f=10\n", e) == ORIGIN_PLAYER && e->kind == 'e' && e->ent == -1 && e->z == 0);
    CHECK(NetParseLine("end 6 o=other r=0000000a:0000000b e=208 f=10\n", e) == ORIGIN_OTHER && e->z == 10 && e->w == 11 && e->ent == 208);
    CHECK(e->step == -1);   /* no w= (older records): the world is not held for it */
    /* World steps (lockstep): w= on commands and turn ends, "k <steps>" lines between them. */
    CHECK(NetParseLine("end 8 o=other r=0000000a:0000000b e=208 f=10 w=123456789012 by=guest\n", e) == ORIGIN_OTHER && e->w == 11 && e->step == 123456789012LL);
    CHECK(NetParseLine("cmd 9 o=player a=41 ao=0 i=4 n=shoot t=3:85857c3e to=-1 fl=0 r=1:2 e=300 d=0 f=5 w=4321 pv=0\n", e) == ORIGIN_PLAYER && e->step == 4321 && e->w == 2 && e->frame == 5);
    hostSteps = hostStepsFirst = -1;
    ParseScriptLine("k 600\n");
    ParseScriptLine("k 606\n");
    CHECK(hostSteps == 606 && hostStepsFirst == 600);
    hostSteps = hostStepsFirst = -1;
    CHECK(NetParseLine("cmd 7 o=replay a=41 i=4 n=shoot t=3:85857c3e fl=0 r=1:2 e=300 d=1 f=1 pv=0 bt=38c2c0,x,\n", e) == ORIGIN_REPLAY);
    CHECK(e->depth == 1 && e->ent == 300 && e->dataLen == 4 && e->frame == 1);

    /* Sync: RNG and the entity id counter of the world (sim+0x10 -> world, counter at +0x1c48+0x58). */
    static char world[0x2000];
    void *fakeSim[4] = { NULL, NULL, world, NULL };
    e->z = 0x11; e->w = 0x22; e->ent = 1234;
    Sync(e, fakeSim);
    CHECK(rng[0] == 0x11 && rng[1] == 0x22 && *(int *)(world + 0x1c48 + 0x58) == 1234 && EntityCount(fakeSim) == 1234);
    CHECK(EntityCount(NULL) == -1);
    e->ent = 1000;   /* never backwards: ids 1001.. may still be alive */
    Sync(e, fakeSim);
    CHECK(EntityCount(fakeSim) == 1234);
    CHECK(NetParseLine("state 1:2:3\n", e) == -1);
    CHECK(NetParseLine("# comment\n", e) == -1);

    /* Bad pointers in the actor never crash the game. */
    unsigned char broken[0x200];
    memset(broken, 0, sizeof broken);
    *(void **)broken = (void *)16;
    CHECK(ActorId(broken) == -1);

    /* Transport (nettcp.c): host and guest threads over loopback; the guest's file gets the host's complete lines, the
     * half-written last line only once it is finished; ReadMore takes complete lines only. */
    {
        wchar_t hostLog[MAX_PATH], guestLog[MAX_PATH];
        GetTempPathW(MAX_PATH, hostLog);
        wcscpy(guestLog, hostLog);
        wcscat(hostLog, L"swh2net_tcp_host.log");
        wcscat(guestLog, L"swh2net_tcp_guest.log");
        FILE *h = _wfopen(hostLog, L"wb");
        fputs("cmd 1 o=player a=41 ao=1 i=0 n=move t=1:0000803f00000040 fl=0 r=1:2 e=5 d=0 f=3\nscene r=0000000a:0000000b\nspawn r=", h);
        fclose(h);
        /* A loader's "getsave" before the guest's game connects: the shared save, byte for byte, then closed. */
        wchar_t savePath[MAX_PATH];
        GetTempPathW(MAX_PATH, savePath);
        wcscat(savePath, L"savegame_004.dat");
        static char save[100000];
        for (int i = 0; i < (int)sizeof save; i++) save[i] = (char)(i * 7 + (i >> 8));
        FILE *sf = _wfopen(savePath, L"wb");
        fwrite(save, 1, sizeof save, sf);
        fclose(sf);
        NetTcpSetSave(savePath);
        NetTcpSetInfo("coop=2 data=ab12 mods=A; B");
        CHECK(NetTcpHost("127.0.0.1", 27199, hostLog));
        Sleep(200);
        {
            static char reply[sizeof save + 256];
            int len = Ask("getsave\r\n", reply, (int)sizeof reply);
            char *nl = len > 0 ? (char *)memchr(reply, '\n', len) : NULL;
            CHECK(nl && strncmp(reply, "save 100000 savegame_004.dat 0 0 map\n", nl - reply + 1) == 0);
            CHECK(nl && len - (nl + 1 - reply) == (int)sizeof save && memcmp(nl + 1, save, sizeof save) == 0);
            CHECK(NetTcpState() == NET_TCP_WAITING);   /* a loader is not a guest */
            /* getinfo: the host loader's fingerprint as it is. */
            len = Ask("getinfo\n", reply, 1000);
            CHECK(len > 0 && strncmp(reply, "info coop=2 data=ab12 mods=A; B\n", len) == 0);
            CHECK(NetTcpState() == NET_TCP_WAITING);
        }
        DeleteFileW(savePath);
        NetTcpSetHello("hello Tester");   /* real guests always say hello first; the host waits for it */
        CHECK(NetTcpGuest("127.0.0.1", 27199, guestLog));
        ULONGLONG until = GetTickCount64() + 5000;
        while (NetTcpState() != NET_TCP_CONNECTED && GetTickCount64() < until) Sleep(20);
        Sleep(300);
        h = _wfopen(hostLog, L"ab");
        fputs("0000000c:0000000d f=9\n", h);
        fclose(h);
        Sleep(300);
        char got[512] = {0};
        FILE *g = _wfopen(guestLog, L"rb");
        if (g) { fread(got, 1, sizeof got - 1, g); fclose(g); }
        CHECK(strstr(got, "cmd 1 o=player") == got && strstr(got, "spawn r=0000000c:0000000d f=9\n") != NULL);

        /* The connection breaks: the guest connects again with "resume <its size>" and gets only what is new —
         * its file ends up the same as the host's log, nothing twice. */
        EnterCriticalSection(&lock);
        if (guestSock != INVALID_SOCKET) shutdown(guestSock, SD_BOTH);
        LeaveCriticalSection(&lock);
        Sleep(300);
        h = _wfopen(hostLog, L"ab");
        fputs("end 2 o=player r=1:2 e=5 f=12\n", h);   /* written while the guest is away */
        fclose(h);
        until = GetTickCount64() + 5000;
        while (GetTickCount64() < until)
        {
            Sleep(100);
            static char hostAll[1024], guestAll[1024];
            ReadAll(hostLog, hostAll, sizeof hostAll);
            ReadAll(guestLog, guestAll, sizeof guestAll);
            if (strcmp(hostAll, guestAll) == 0) break;
        }
        h = _wfopen(hostLog, L"ab");
        fputs("cmd 3 o=player a=42 f=14\n", h);
        fclose(h);
        Sleep(400);
        {
            static char hostAll[1024], guestAll[1024];
            ReadAll(hostLog, hostAll, sizeof hostAll);
            ReadAll(guestLog, guestAll, sizeof guestAll);
            CHECK(strcmp(hostAll, guestAll) == 0);
            CHECK(NetTcpState() == NET_TCP_CONNECTED);
        }
        /* The host got the guest's hello on both connects, never the resume line. */
        char line[128];
        int hellos = 0, joins = 0, others = 0;
        while (NetTcpPoll(line, sizeof line))
        {
            if (strcmp(line, "hello Tester") == 0) hellos++;
            else if (strcmp(line, "joined") == 0) joins++;   /* the host thread's notice per connection */
            else others++;
        }
        CHECK(hellos == 2 && joins == 2 && others == 0);

        /* CompleteLines: half a line at the end (the break came mid-line) is cut off. */
        g = _wfopen(guestLog, L"wb");
        fputs("line one\nline two\nhalf a li", g);
        fclose(g);
        CHECK(CompleteLines(guestLog) == 18);
        CHECK(ReadAll(guestLog, got, sizeof got) == 18 && strcmp(got, "line one\nline two\n") == 0);
        g = _wfopen(guestLog, L"wb");
        fputs("no newline at all", g);
        fclose(g);
        CHECK(CompleteLines(guestLog) == 0);

        /* NetTcpRebase: the replay file (host log bytes from 1000 on) starts at host byte 1009 afterwards. */
        g = _wfopen(guestLog, L"wb");
        fputs("line one\nline two\n", g);
        fclose(g);
        long long oldBase = NetTcpBase();
        NetTcpSetFrom(1000);
        CHECK(NetTcpRebase(1009) && NetTcpBase() == 1009);
        CHECK(ReadAll(guestLog, got, sizeof got) == 9 && strcmp(got, "line two\n") == 0);
        CHECK(!NetTcpRebase(900) && NetTcpBase() == 1009);   /* before the file's start: not possible */
        NetTcpSetFrom(oldBase);
    }
    {
        /* ReadMore: complete lines only, the rest on the next call. */
        mode = NET_REPLAY; guest = 1; scriptLen = 0; scriptOffset = 0; scenesLen = 0;
        GetTempPathW(MAX_PATH, replayPath);
        wcscat(replayPath, L"swh2net_readmore.log");
        FILE *r = _wfopen(replayPath, L"wb");
        fputs("scene r=00000001:00000002\ncmd 2 o=ai a=47 ao=4 i=-1 n=@done t=0: to=-1 fl=0 r=1:2 e=3 d=0 f=4 pv=0\nend 3 o=oth", r);
        fclose(r);
        ReadMore();
        CHECK(scenesLen == 1 && scriptLen == 1 && script[0].index == INDEX_DONE);
        r = _wfopen(replayPath, L"ab");
        fputs("er r=1:2 e=3 f=5\n", r);
        fclose(r);
        ReadMore();
        CHECK(scriptLen == 2 && script[1].kind == 'e' && script[1].origin == ORIGIN_OTHER);
        DeleteFileW(replayPath);
    }

    {
        /* The other player's aim: lines -> angles, "aim" animation input and the sight line, taken down by "off" and
         * by a command of that actor; the own aim -> "aim" lines in the host's log. */
        static char nodeBuf[2][0x400];   /* [0] sentinel; [1] actor 41 at +0x18 */
        static int id41 = 41;
        *(void **)nodeBuf[0] = nodeBuf[1];
        *(void **)nodeBuf[1] = nodeBuf[0];
        char *actor41 = nodeBuf[1] + 0x18;
        *(int **)actor41 = &id41;
        static unsigned char acts[2 * 0x38];
        memcpy(acts + 0x38, "shoot", 6);
        *(size_t *)(acts + 0x38 + 0x10) = 5;
        *(size_t *)(acts + 0x38 + 0x18) = 15;
        *(unsigned char **)(actor41 + 0x50) = acts;
        *(unsigned char **)(actor41 + 0x58) = acts + sizeof acts;
        static char storage[0x100], aimWorld[0x2000];
        *(void **)(storage + 0xd0) = nodeBuf[0];
        atActorStorage = (char *)FakeStorage;
        fakeStorage = storage;
        void *sim3[4] = { NULL, NULL, aimWorld, NULL };
        atIsActionsOngoing = (char *)FakeIdle;
        atActorStore = atBodyStore = atAnimStore = (char *)FakeStore;
        atComponent = (char *)FakeComponent;
        atWeaponDef = (char *)FakeWeaponDef;
        atMapFind = (char *)FakeMapFind;
        atTraceMake = (char *)FakeTraceMake;
        atTraceAim = (char *)FakeTraceAim;
        atPtrReset = (char *)FakePtrReset;
        atAnimInput = (char *)FakeAnimInput;
        static ULONG_PTR *mapEndPtr = &fakeMapEnd;
        static ULONG_PTR *idxPtr = fakeIdx;
        static void **defsPtr = fakeDefs;
        atActionMapEnd = (char *)mapEndPtr;
        atActionMapIdx = (char *)&idxPtr;
        atActionDefs = (char *)&defsPtr;
        *(int *)((char *)fakeIdx + 0x20) = 0;   /* "shoot": map entry 0 -> value 0 -> defs[0] */
        fakeDefs[0] = fakeAction;
        ordLen = 0; ordIds[ordLen++] = 41; ordIds[ordLen++] = 190;
        crewLen = 2; crewIds[0] = 41; crewIds[1] = 190;   /* 41 the host's, 190 the guest's */
        me = 2; live = 1; scriptPos = scriptLen = 0;
        AimFromLine("ao=1 i=1 a=0.5 b=0.25 s=1 v=1");   /* the guest's own: ignored */
        AimFromLine("ao=0 i=1 a=0.5 b=0.25 s=1 v=0");
        AimDraw(sim3);
        CHECK(*(float *)(actor41 + 0xe0) == 0.5f && *(float *)(actor41 + 0xe4) == 0.25f);
        CHECK(traceMakes == 1 && traceAims == 1 && lastValid == 0 && animOn == 1);
        AimDraw(sim3);
        CHECK(traceMakes == 1 && traceAims == 1);   /* same angle: nothing to do */
        AimFromLine("ao=0 i=1 a=0.75 b=0.25 s=1 v=1");
        AimDraw(sim3);
        CHECK(*(float *)(actor41 + 0xe0) == 0.75f && traceMakes == 2 && traceAims == 2 && lastValid == 1 && animOn == 1);   /* the game gives back the line it has */
        scriptLen = 1;   /* the host's earlier commands not played yet: not shown meanwhile */
        AimDraw(sim3);
        CHECK(ptrResets == 1 && animOff == 1 && *(void **)(actor41 + 0xf8) == NULL);
        scriptLen = 0;
        AimDraw(sim3);
        CHECK(traceMakes == 3 && animOn == 2);
        AimFromLine("ao=0 off");
        AimDraw(sim3);
        CHECK(ptrResets == 2 && animOff == 2);
        AimFromLine("ao=0 i=1 a=0.5 b=0.5 s=1 v=1");
        AimDraw(sim3);
        AimDropFor(sim3, 41);   /* its shot starts */
        CHECK(ptrResets == 3 && animOff == 3);
        AimDraw(sim3);
        CHECK(traceMakes == 4);   /* not again until a new aim line */

        /* Own aim as the host: one line per change (at most ~15 a second), "off" after the aim mode is gone. */
        me = 1; hosting = 1; live = 0;
        mainSim = sim3;
        realAimUpdate = FakeAimUpdate;
        static char ui[0x200], state[0x100];
        *(int *)(ui + 0xfc) = 41;
        *(int *)(ui + 0x100) = 1;
        state[0xc8] = 1; state[0x6d] = 1;
        *(float *)(actor41 + 0xe0) = 1.25f; *(float *)(actor41 + 0xe4) = 1.0f;
        HookAimUpdate(ui, NULL, state);
        char aimLine[256];
        CHECK(LastLine(aimLine, sizeof aimLine) && strcmp(aimLine, "aim ao=0 i=1 a=1.25000 b=1.00000 s=1 v=1\n") == 0);
        aimSeenAt = GetTickCount64() - AIM_OFF_MS - 1;
        AimWatch();
        CHECK(LastLine(aimLine, sizeof aimLine) && strcmp(aimLine, "aim ao=0 off\n") == 0 && aimSentOrd == -1);
        *(int *)(ui + 0xfc) = 190;   /* the guest's crewmate: not the host's to aim */
        HookAimUpdate(ui, NULL, state);
        CHECK(aimSentOrd == -1);
        ParseScriptLine("aim ao=0 i=1 a=0.5 b=0.5 s=1 v=1\n");   /* not live: ignored, not a command */
        CHECK(scriptLen == 0);
        me = 0; hosting = 0; crewLen = 0; ordLen = 0; mainSim = NULL;
        memset(aims, 0, sizeof aims);
    }

    free(e);
    DeleteFileW(outPath);
    printf(fails ? "%d FAILED\n" : "all netplay tests passed\n", fails);
    return fails != 0;
}
