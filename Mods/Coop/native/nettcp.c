/*
 * Netplay transport, first step (docs/MULTIPLAYER.md, "Сеть"): plain TCP, host -> one guest, the host's log as it is
 * written. The record/replay machinery stays as it is: the host is a recording game, the guest a replaying one whose
 * replay file (swh2_net_replay.log) grows while the battle runs.
 *   host:  a thread listens on the port; a guest that connects gets the host's log from its first byte and then every
 *          complete line as it is appended (the file is polled every POLL_MS). A guest that drops can connect again.
 *   guest: a thread connects (retrying until it gets through), truncates its replay file and appends what it receives;
 *          netplay.c reads complete lines from it as they come (ReadMore).
 * The threads only touch files and sockets, never the game. Text lines only, no framing: the log is the protocol.
 *
 * Step 2, the way back (guest commands): the guest sends its player's commands and turn ends as lines on the same
 * connection (NetTcpSend, game thread); the host thread queues the lines it receives, the game thread takes them with
 * NetTcpPoll and plays them (netplay.c HostPump), so they come back to the guest in the host's log.
 *
 * Save transfer: the game reads its save slots only at start, so the guest's LOADER fetches the host's save before
 * it starts the guest's game (NetSession.cs): it connects to the same port and sends "getsave" as its first line; the
 * host answers "save <size> <file name>" + the file's bytes (or "nosave <reason>") and closes. The file is read on
 * every request, so a guest that fetches again gets the host's latest save. "getinfo" answers "info <text>" (the host
 * loader's fingerprint of its game data and mods, session "info <text>"), so the guest's loader can refuse to start a
 * game that would not stay in sync.
 *
 * Reconnect: the guest's replay file only grows and the host's log only gets lines appended, so a guest whose
 * connection broke connects again with "resume <bytes it has>" as its first line and the host goes on from there.
 * TCP keep-alive (a few seconds) notices a connection that died without a word (Wi-Fi gone, VPN down).
 *
 * Joining mid-mission: the game saves its slot by itself during a battle, so the save a loader fetches can be one from
 * inside the host's mission. The host thread watches the shared save file and notes how long the log was when it was
 * last written; "getsave" answers "save <size> <file name> <log bytes> <turn ends> <battle|map>" — the guest's game,
 * loaded from that save, takes the host's log from there ("from <log bytes> <turn ends> <battle|map>" in its session:
 * its first connect is a resume). battle = written while the host's battle ran: the guest lands right in it.
 *
 * The next battle without a restart: the guest's game (its dll) fetches the host's save the same way while it is not
 * in a battle (NetTcpFetchStart, own thread), netplay.c writes it into the guest's slot when the main menu opens, and
 * when that save's battle loads the replay file is rebased (NetTcpRebase): it then starts at that byte of the host's
 * log, as if the guest had joined there.
 */
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <mstcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "netplay.h"
#pragma comment(lib, "ws2_32.lib")

#define POLL_MS 15
#define CHUNK 65536

typedef struct { int port; char addr[128]; wchar_t path[MAX_PATH]; } TcpJob;
static TcpJob hostJob, guestJob;
static volatile LONG tcpState;   /* NET_TCP_* */
static volatile LONG tcpStop;

/* Lines from the guest (host side), filled by the host thread, emptied by the game thread. */
typedef struct Line { struct Line *next; char text[1]; } Line;
static Line *inHead, *inTail;
static CRITICAL_SECTION lock;
static CRITICAL_SECTION fileLock;            /* guest side: the replay file (thread appends, NetTcpRebase rewrites) */
static SOCKET guestSock = INVALID_SOCKET;   /* guest side: the connection to the host, for NetTcpSend */
static char hello[128];                    /* guest side: first line to the host on every connect ("hello <nickname>") */
static wchar_t sharedSave[MAX_PATH];        /* host side: the save slot file a loader may fetch (session "save <path>") */
static volatile LONG reconnects;            /* guest side: connects after the first one */
static DWORD dropTestMs;                    /* guest side, tests: break the connection once, this long after connecting */
static char shareInfo[1024];
static long long fromOffset;                /* guest side: where in the host's log its save was made (session "from") */
static long long saveAtLog;                 /* host side: the log's size when the shared save was last written */
static FILETIME saveTime;                   /* host side: that save's write time */
static int saveInBattle;                    /* host side: the tactics world was running when it was written */                 /* host side: what "getinfo" answers (session "info <text>") */

void NetTcpSetHello(const char *line) { strncpy(hello, line, sizeof hello - 2); }
void NetTcpSetSave(const wchar_t *path) { wcsncpy(sharedSave, path, MAX_PATH - 1); }
int NetTcpReconnects(void) { return (int)reconnects; }
void NetTcpTestDrop(int ms) { dropTestMs = ms > 0 ? (DWORD)ms : 0; }
void NetTcpSetFrom(long long offset) { fromOffset = offset > 0 ? offset : 0; }
long long NetTcpBase(void) { return fromOffset; }
void NetTcpSetInfo(const char *text) { strncpy(shareInfo, text, sizeof shareInfo - 1); }

/* No delay for small lines; keep-alive probes after 5 s quiet, every second: a dead link is noticed in ~15 s. */
static void Tune(SOCKET s)
{
    int nodelay = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&nodelay, sizeof nodelay);
    struct tcp_keepalive ka = { 1, 5000, 1000 };
    DWORD ret = 0;
    WSAIoctl(s, SIO_KEEPALIVE_VALS, &ka, sizeof ka, NULL, 0, &ret, NULL, NULL);
}

int NetTcpState(void) { return (int)tcpState; }

static int SendAll(SOCKET s, const char *p, int n)
{
    while (n > 0)
    {
        int k = send(s, p, n, 0);
        if (k <= 0) return 0;
        p += k;
        n -= k;
    }
    return 1;
}

/* Sends the complete lines of the file from *offset on; 0 when the guest is gone. */
static int SendNewLines(SOCKET s, const wchar_t *path, long long *offset)
{
    static char buf[CHUNK];
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return 1;   /* not written yet */
    LARGE_INTEGER size, at;
    int ok = 1;
    if (GetFileSizeEx(f, &size) && size.QuadPart < *offset) *offset = 0;   /* a new log (the game started over): resend */
    at.QuadPart = *offset;
    SetFilePointerEx(f, at, NULL, FILE_BEGIN);
    for (;;)
    {
        DWORD got = 0;
        if (!ReadFile(f, buf, CHUNK, &got, NULL) || got == 0) break;
        int end = (int)got;
        while (end > 0 && buf[end - 1] != '\n') end--;   /* a line still being written waits for the next poll */
        if (end == 0) break;
        if (!SendAll(s, buf, end)) { ok = 0; break; }
        *offset += end;
        if (end < (int)got) break;
    }
    CloseHandle(f);
    return ok;
}

static void Queue(const char *text, int n)
{
    Line *l = (Line *)malloc(sizeof(Line) + n);
    if (!l) return;
    memcpy(l->text, text, n);
    l->text[n] = 0;
    l->next = NULL;
    EnterCriticalSection(&lock);
    if (inTail) inTail->next = l; else inHead = l;
    inTail = l;
    LeaveCriticalSection(&lock);
}

int NetTcpPoll(char *out, int max)
{
    EnterCriticalSection(&lock);
    Line *l = inHead;
    if (l) { inHead = l->next; if (!inHead) inTail = NULL; }
    LeaveCriticalSection(&lock);
    if (!l) return 0;
    strncpy(out, l->text, max - 1);
    out[max - 1] = 0;
    free(l);
    return 1;
}

int NetTcpSend(const char *line)
{
    int ok = 0;
    EnterCriticalSection(&lock);
    if (guestSock != INVALID_SOCKET) ok = SendAll(guestSock, line, (int)strlen(line)) && SendAll(guestSock, "\n", 1);
    LeaveCriticalSection(&lock);
    return ok;
}

/* Queues the complete lines in buf, keeps the rest. */
static void SplitLines(char *buf, int *len, int max)
{
    int start = 0;
    for (int i = 0; i < *len; i++)
        if (buf[i] == '\n')
        {
            int end = i;
            if (end > start && buf[end - 1] == '\r') end--;
            if (end > start) Queue(buf + start, end - start);
            start = i + 1;
        }
    memmove(buf, buf + start, *len - start);
    *len -= start;
    if (*len == max) *len = 0;   /* a line longer than any command: dropped */
}

/* Complete lines received from the guest; 0 when it is gone. */
static int ReceiveLines(SOCKET s, char *buf, int *len, int max)
{
    int n = recv(s, buf + *len, max - *len, 0);
    if (n <= 0) return 0;
    *len += n;
    SplitLines(buf, len, max);
    return 1;
}

/* The first line of a connection (a guest's "hello <nick>", a loader's "getsave"), waiting up to ms; what came
 * after it stays in buf. 0 = nothing complete in time (a guest without a nickname) or the connection is gone. */
static int FirstLine(SOCKET s, char *buf, int *len, int max, char *line, int lineMax, int ms)
{
    ULONGLONG until = GetTickCount64() + ms;
    for (;;)
    {
        char *nl = (char *)memchr(buf, '\n', *len);
        if (nl)
        {
            int n = (int)(nl - buf), keep = n;
            if (n > 0 && buf[n - 1] == '\r') n--;
            if (n >= lineMax) n = lineMax - 1;
            memcpy(line, buf, n);
            line[n] = 0;
            memmove(buf, nl + 1, *len - keep - 1);
            *len -= keep + 1;
            return 1;
        }
        ULONGLONG now = GetTickCount64();
        if (now >= until || *len == max) return 0;
        fd_set r;
        FD_ZERO(&r);
        FD_SET(s, &r);
        ULONGLONG left = until - now;
        struct timeval tv = { (long)(left / 1000), (long)(left % 1000) * 1000 };
        if (select(0, &r, NULL, NULL, &tv) <= 0) return 0;
        int k = recv(s, buf + *len, max - *len, 0);
        if (k <= 0) return 0;
        *len += k;
    }
}

static long long FileSize(const wchar_t *path)
{
    WIN32_FILE_ATTRIBUTE_DATA fa;
    return GetFileAttributesExW(path, GetFileExInfoStandard, &fa) ? ((long long)fa.nFileSizeHigh << 32) | fa.nFileSizeLow : 0;
}

/* Host thread, every poll: the shared save written again = the log up to here is in it. */
static void WatchSave(void)
{
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (!sharedSave[0] || !GetFileAttributesExW(sharedSave, GetFileExInfoStandard, &fa)) return;
    if (CompareFileTime(&fa.ftLastWriteTime, &saveTime) == 0) return;
    int first = !saveTime.dwLowDateTime && !saveTime.dwHighDateTime;
    saveTime = fa.ftLastWriteTime;
    /* At the first look the save is older than this game's log: a guest takes the log from its start. */
    saveAtLog = first ? 0 : FileSize(hostJob.path);
    unsigned long long tick = NetWorldTick();
    saveInBattle = !first && tick && GetTickCount64() - tick < 3000;
}

/* Turn ends ("end " lines) in the first `bytes` of the log: the guest's count of them starts there. */
static int EndsBefore(const wchar_t *path, long long bytes)
{
    FILE *f = _wfopen(path, L"rb");
    if (!f) return 0;
    static char line[8192];
    long long at = 0;
    int ends = 0;
    while (at < bytes && fgets(line, sizeof line, f))
    {
        at += (long long)strlen(line);
        if (at <= bytes && strncmp(line, "end ", 4) == 0) ends++;
    }
    fclose(f);
    return ends;
}

/* Answers a loader's "getsave": the shared save file, then the connection is closed by the caller. */
static void SendSave(SOCKET s)
{
    char head[MAX_PATH * 3 + 64];
    HANDLE f = sharedSave[0] ? CreateFileW(sharedSave, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL)
                             : INVALID_HANDLE_VALUE;
    LARGE_INTEGER size;
    if (f == INVALID_HANDLE_VALUE || !GetFileSizeEx(f, &size) || size.QuadPart <= 0 || size.QuadPart > 64 * 1024 * 1024)
    {
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        const char *why = sharedSave[0] ? "nosave the host's save file can't be read\n" : "nosave the host shares no save\n";
        SendAll(s, why, (int)strlen(why));
        return;
    }
    char *data = (char *)malloc((size_t)size.QuadPart);
    DWORD got = 0;
    int ok = data && ReadFile(f, data, (DWORD)size.QuadPart, &got, NULL) && got == (DWORD)size.QuadPart;
    CloseHandle(f);
    if (ok)
    {
        const wchar_t *name = wcsrchr(sharedSave, L'\\');
        char utf8[MAX_PATH * 3];
        if (!WideCharToMultiByte(CP_UTF8, 0, name ? name + 1 : sharedSave, -1, utf8, sizeof utf8, NULL, NULL)) strcpy(utf8, "savegame.dat");
        WatchSave();
        long long from = saveAtLog;
        sprintf(head, "save %lu %s %lld %d %s\n", (unsigned long)got, utf8, from, EndsBefore(hostJob.path, from), saveInBattle ? "battle" : "map");
        ok = SendAll(s, head, (int)strlen(head)) && SendAll(s, data, (int)got);
    }
    else SendAll(s, "nosave the host's save file can't be read\n", 42);
    free(data);
    /* Let the loader read everything before the close (no reset that could drop the tail). */
    shutdown(s, SD_SEND);
    char drain[256];
    fd_set r;
    FD_ZERO(&r);
    FD_SET(s, &r);
    struct timeval tv = { 2, 0 };
    if (select(0, &r, NULL, NULL, &tv) > 0) recv(s, drain, sizeof drain, 0);
}

/* One connection from the listening socket (waiting up to ms for it): "getsave" / "getinfo" are answered and closed
 * here (INVALID_SOCKET returned); anything else is a guest: its socket, first line (ok = 1 when one came) and what
 * followed it in buf. */
static SOCKET AcceptOne(SOCKET ls, int ms, char *buf, int *len, char *first, int firstMax, int *ok)
{
    fd_set ready;
    FD_ZERO(&ready);
    FD_SET(ls, &ready);
    struct timeval wait = { 0, ms * 1000 };
    if (ms > 0 && select(0, &ready, NULL, NULL, &wait) <= 0) return INVALID_SOCKET;
    SOCKET s = accept(ls, NULL, NULL);
    if (s == INVALID_SOCKET) { Sleep(ms > 0 ? 200 : 0); return INVALID_SOCKET; }
    u_long blocking = 0;
    ioctlsocket(s, FIONBIO, &blocking);
    Tune(s);
    *len = 0;
    first[0] = 0;
    *ok = FirstLine(s, buf, len, CHUNK, first, firstMax, 3000);
    if (*ok && strcmp(first, "getsave") == 0) { SendSave(s); closesocket(s); return INVALID_SOCKET; }
    if (*ok && strcmp(first, "getinfo") == 0)
    {
        static char line[sizeof shareInfo + 8];
        sprintf(line, "info %s\n", shareInfo[0] ? shareInfo : "-");
        SendAll(s, line, (int)strlen(line));
        shutdown(s, SD_SEND);
        closesocket(s);
        return INVALID_SOCKET;
    }
    return s;
}

static DWORD WINAPI HostThread(LPVOID unused)
{
    (void)unused;
    SOCKET ls = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_port = htons((u_short)hostJob.port);
    /* "host 27015" = every network interface (Windows asks the player about the firewall once); "host
     * 127.0.0.1:27015" = this PC only (tests with two copies on one PC: no firewall question). */
    a.sin_addr.s_addr = htonl(INADDR_ANY);
    if (hostJob.addr[0] && inet_pton(AF_INET, hostJob.addr, &a.sin_addr) != 1) a.sin_addr.s_addr = htonl(INADDR_ANY);
    BOOL yes = TRUE;
    setsockopt(ls, SOL_SOCKET, SO_REUSEADDR, (const char *)&yes, sizeof yes);
    if (ls == INVALID_SOCKET || bind(ls, (struct sockaddr *)&a, sizeof a) || listen(ls, 1))
    {
        InterlockedExchange(&tcpState, NET_TCP_FAILED);
        return 1;
    }
    /* accept() with a timeout, so the shared save is watched while no one is connected too. The listening socket is
     * watched while a guest is on as well: a guest's game asks for the host's save then ("getsave", NetTcpFetchStart:
     * the next battle without a restart), and a guest coming back may connect before the old connection is seen dead. */
    u_long nonBlocking = 1;
    ioctlsocket(ls, FIONBIO, &nonBlocking);
    static char in[CHUNK], nextIn[CHUNK];
    SOCKET next = INVALID_SOCKET;   /* a guest's connection that came while another one was on */
    char nextFirst[160];
    int nextInLen = 0, nextOk = 0;
    while (!tcpStop)
    {
        SOCKET s;
        int inLen = 0, ok;
        char first[160];
        long long offset = 0;
        if (next != INVALID_SOCKET)
        {
            s = next;
            next = INVALID_SOCKET;
            memcpy(in, nextIn, nextInLen);
            inLen = nextInLen;
            strcpy(first, nextFirst);
            ok = nextOk;
        }
        else
        {
            InterlockedExchange(&tcpState, NET_TCP_WAITING);
            WatchSave();
            s = AcceptOne(ls, 200, in, &inLen, first, sizeof first, &ok);
            if (s == INVALID_SOCKET) continue;
        }
        if (ok)
        {
            if (strncmp(first, "resume ", 7) == 0)
            {
                /* A guest coming back: it has the log up to there. Past the end = another log (the host's game started
                 * again): that guest's battle is gone, it is turned away. */
                offset = _atoi64(first + 7);
                WIN32_FILE_ATTRIBUTE_DATA fa;
                long long size = GetFileAttributesExW(hostJob.path, GetFileExInfoStandard, &fa) ? ((long long)fa.nFileSizeHigh << 32) | fa.nFileSizeLow : 0;
                if (offset < 0 || offset > size) { closesocket(s); continue; }
            }
            else if (first[0]) Queue(first, (int)strlen(first));
            SplitLines(in, &inLen, sizeof in);
        }
        Queue("joined", 6);   /* for the game thread (netplay.c HostPlay): a guest is on, maybe from inside the mission */
        InterlockedExchange(&tcpState, NET_TCP_CONNECTED);
        while (!tcpStop && SendNewLines(s, hostJob.path, &offset))
        {
            WatchSave();
            /* Wait up to POLL_MS for the guest's lines (instead of a plain sleep), or a new connection. */
            fd_set r;
            FD_ZERO(&r);
            FD_SET(s, &r);
            FD_SET(ls, &r);
            struct timeval tv = { 0, POLL_MS * 1000 };
            int k = select(0, &r, NULL, NULL, &tv);
            if (k < 0) break;
            if (k > 0 && FD_ISSET(ls, &r))
            {
                next = AcceptOne(ls, 0, nextIn, &nextInLen, nextFirst, sizeof nextFirst, &nextOk);
                if (next != INVALID_SOCKET) break;   /* the guest again, on a new connection: that one goes on */
            }
            if (k > 0 && FD_ISSET(s, &r) && !ReceiveLines(s, in, &inLen, sizeof in)) break;
        }
        closesocket(s);
    }
    if (next != INVALID_SOCKET) closesocket(next);
    closesocket(ls);
    return 0;
}

/* Guest: cuts the replay file after its last complete line (a break can leave half a line) and returns its size. */
static long long CompleteLines(const wchar_t *path)
{
    HANDLE f = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER size, at;
    long long end = 0;
    if (GetFileSizeEx(f, &size))
    {
        end = size.QuadPart;
        char c[512];
        while (end > 0)
        {
            int n = end > (long long)sizeof c ? (int)sizeof c : (int)end, i;
            DWORD got = 0;
            at.QuadPart = end - n;
            SetFilePointerEx(f, at, NULL, FILE_BEGIN);
            if (!ReadFile(f, c, n, &got, NULL) || got != (DWORD)n) { end = 0; break; }
            for (i = n; i > 0 && c[i - 1] != '\n'; i--) {}
            if (i > 0) { end = end - n + i; break; }
            end -= n;
        }
        if (end < size.QuadPart)
        {
            at.QuadPart = end;
            SetFilePointerEx(f, at, NULL, FILE_BEGIN);
            SetEndOfFile(f);
        }
    }
    CloseHandle(f);
    return end;
}

static DWORD WINAPI GuestThread(LPVOID unused)
{
    (void)unused;
    static char buf[CHUNK];
    int connects = 0;
    char port[16];
    sprintf(port, "%d", guestJob.port);
    while (!tcpStop)
    {
        InterlockedExchange(&tcpState, NET_TCP_WAITING);
        struct addrinfo hints = {0}, *res = NULL;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        SOCKET s = INVALID_SOCKET;
        if (getaddrinfo(guestJob.addr, port, &hints, &res) == 0 && res)
        {
            s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
            if (s != INVALID_SOCKET && connect(s, res->ai_addr, (int)res->ai_addrlen)) { closesocket(s); s = INVALID_SOCKET; }
            freeaddrinfo(res);
        }
        if (s == INVALID_SOCKET) { Sleep(500); continue; }
        Tune(s);
        FILE *f;
        char resume[48] = "";
        if (!connects++)
        {
            /* The first time the host sends its log from the start (or from where this game's save was made, "from"):
             * the file starts over too. */
            f = _wfopen(guestJob.path, L"wb");
            if (f) fclose(f);
            if (fromOffset > 0) sprintf(resume, "resume %lld\n", fromOffset);
        }
        else
        {
            EnterCriticalSection(&fileLock);
            sprintf(resume, "resume %lld\n", fromOffset + CompleteLines(guestJob.path));   /* again after a break: go on from there */
            LeaveCriticalSection(&fileLock);
            InterlockedIncrement(&reconnects);
        }
        EnterCriticalSection(&lock);
        guestSock = s;
        if (resume[0]) SendAll(s, resume, (int)strlen(resume));
        if (hello[0]) { SendAll(s, hello, (int)strlen(hello)); SendAll(s, "\n", 1); }
        LeaveCriticalSection(&lock);
        InterlockedExchange(&tcpState, NET_TCP_CONNECTED);
        ULONGLONG dropAt = dropTestMs ? GetTickCount64() + dropTestMs : 0;
        for (;;)
        {
            if (dropAt)
            {
                fd_set r;
                FD_ZERO(&r);
                FD_SET(s, &r);
                struct timeval tv = { 0, 250 * 1000 };
                if (select(0, &r, NULL, NULL, &tv) == 0)
                {
                    if (GetTickCount64() >= dropAt) { dropTestMs = 0; break; }   /* the test's break: once */
                    continue;
                }
            }
            int n = recv(s, buf, sizeof buf, 0);
            if (n <= 0) break;
            EnterCriticalSection(&fileLock);
            f = _wfopen(guestJob.path, L"ab");
            if (f) { fwrite(buf, 1, n, f); fclose(f); }
            LeaveCriticalSection(&fileLock);
        }
        EnterCriticalSection(&lock);
        guestSock = INVALID_SOCKET;
        closesocket(s);
        LeaveCriticalSection(&lock);
        /* Not CLOSED: the guest waits for the host (status "waiting") and connects again. */
        InterlockedExchange(&tcpState, NET_TCP_WAITING);
        Sleep(500);
    }
    return 0;
}

/* Guest: the replay file starts at host log byte `to` from now on (what came before it is dropped); 0 = the file
 * doesn't reach back that far. Game thread. */
int NetTcpRebase(long long to)
{
    int ok = 0;
    EnterCriticalSection(&fileLock);
    long long skip = to - fromOffset;
    FILE *f = skip >= 0 ? _wfopen(guestJob.path, L"rb") : NULL;
    if (f)
    {
        _fseeki64(f, 0, SEEK_END);
        long long size = _ftelli64(f);
        if (skip <= size)
        {
            size_t keep = (size_t)(size - skip);
            char *tail = (char *)malloc(keep + 1);
            _fseeki64(f, skip, SEEK_SET);
            if (tail && fread(tail, 1, keep, f) == keep)
            {
                fclose(f);
                f = NULL;
                FILE *w = _wfopen(guestJob.path, L"wb");
                if (w) { fwrite(tail, 1, keep, w); fclose(w); fromOffset = to; ok = 1; }
            }
            free(tail);
        }
        if (f) fclose(f);
    }
    LeaveCriticalSection(&fileLock);
    return ok;
}

/* Guest: the host's save, fetched in the background (the same "getsave" as the loader's). */
static volatile LONG fetchState;   /* 0 idle, 1 running, 2 done, 3 failed */
static char *fetchData;
static int fetchLen, fetchEnds, fetchBattle;
static long long fetchFrom;

static int RecvAll(SOCKET s, char *p, int n)
{
    while (n > 0)
    {
        int k = recv(s, p, n, 0);
        if (k <= 0) return 0;
        p += k;
        n -= k;
    }
    return 1;
}

static char fetchError[200];
const char *NetTcpFetchError(void) { return fetchError; }

static DWORD WINAPI FetchThread(LPVOID unused)
{
    (void)unused;
    char port[16], head[1024];
    sprintf(port, "%d", guestJob.port);
    struct addrinfo hints = {0}, *res = NULL;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    SOCKET s = INVALID_SOCKET;
    if (getaddrinfo(guestJob.addr, port, &hints, &res) == 0 && res)
    {
        s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
        if (s != INVALID_SOCKET && connect(s, res->ai_addr, (int)res->ai_addrlen)) { closesocket(s); s = INVALID_SOCKET; }
        freeaddrinfo(res);
    }
    int ok = 0;
    strcpy(fetchError, s == INVALID_SOCKET ? "no connection to the host" : "no answer");
    if (s != INVALID_SOCKET)
    {
        DWORD ms = 15000;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&ms, sizeof ms);
        int n = 0;
        if (SendAll(s, "getsave\n", 8))
            while (n < (int)sizeof head - 1 && recv(s, head + n, 1, 0) == 1 && head[n] != '\n') n++;
        head[n] = 0;
        if (n) _snprintf(fetchError, sizeof fetchError - 1, "answer: %.150s", head);
        unsigned long size = 0;
        char name[MAX_PATH], where[16] = "";
        long long from = 0;
        int ends = 0;
        if (sscanf(head, "save %lu %259s %lld %d %15s", &size, name, &from, &ends, where) == 5 && size > 0 && size <= 64u * 1024 * 1024)
        {
            char *data = (char *)malloc(size);
            if (data && RecvAll(s, data, (int)size) && memcmp(data, "SWH2", 4) == 0)
            {
                fetchData = data;
                fetchLen = (int)size;
                fetchFrom = from;
                fetchEnds = ends;
                fetchBattle = strcmp(where, "battle") == 0;
                ok = 1;
            }
            else free(data);
        }
        closesocket(s);
    }
    InterlockedExchange(&fetchState, ok ? 2 : 3);
    return 0;
}

int NetTcpFetchStart(void)
{
    if (fetchState == 1 || !guestJob.port) return 0;
    if (fetchState == 2) { free(fetchData); fetchData = NULL; }
    InterlockedExchange(&fetchState, 1);
    HANDLE t = CreateThread(NULL, 0, FetchThread, NULL, 0, NULL);
    if (!t) { InterlockedExchange(&fetchState, 3); return 0; }
    CloseHandle(t);
    return 1;
}

int NetTcpFetchTake(char **data, int *len, long long *from, int *ends, int *battle)
{
    LONG st = fetchState;
    if (st == 1 || st == 0) return 0;
    InterlockedExchange(&fetchState, 0);
    if (st == 3) return -1;
    *data = fetchData;
    *len = fetchLen;
    *from = fetchFrom;
    *ends = fetchEnds;
    *battle = fetchBattle;
    fetchData = NULL;
    return 1;
}

static int Start(LPTHREAD_START_ROUTINE fn)
{
    WSADATA w;
    InitializeCriticalSection(&lock);
    InitializeCriticalSection(&fileLock);
    if (WSAStartup(MAKEWORD(2, 2), &w)) return 0;
    HANDLE t = CreateThread(NULL, 0, fn, NULL, 0, NULL);
    if (!t) return 0;
    CloseHandle(t);
    return 1;
}

int NetTcpHost(const char *bindAddr, int port, const wchar_t *logPath)
{
    hostJob.port = port;
    strncpy(hostJob.addr, bindAddr ? bindAddr : "", sizeof hostJob.addr - 1);
    wcsncpy(hostJob.path, logPath, MAX_PATH - 1);
    return Start(HostThread);
}

int NetTcpGuest(const char *addr, int port, const wchar_t *replayPath)
{
    guestJob.port = port;
    strncpy(guestJob.addr, addr, sizeof guestJob.addr - 1);
    wcsncpy(guestJob.path, replayPath, MAX_PATH - 1);
    return Start(GuestThread);
}
