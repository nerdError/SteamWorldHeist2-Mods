/*
 * steam_probe.c: checks steamnet.c against the real Steam (no game needed, Steam must run): inits Steam under the
 * game's app id with the game's steam_api64.dll, hosts a private lobby whose guests are piped to a local echo server,
 * then connects to itself over P2P and checks the echo. Also lists friends hosting a co-op lobby.
 *   steam_probe.exe <folder with steam_api64.dll> <work folder>             (one process: host + P2P to itself)
 *   steam_probe.exe <dll folder> <work folder> both                          (host + guest pipes in one process)
 *   steam_probe.exe <dll folder> <work folder> host                          (host, 60 s)
 *   steam_probe.exe <dll folder> <work folder> guest <host steam id|lobby>   (guest pipe: 4 MB through it and back)
 * Build: cl steam_probe.c steamnet.c (see test.cmd).
 */
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <winsock2.h>
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "steamnet.h"
#pragma comment(lib, "ws2_32.lib")

#define ECHO_PORT 27190

static DWORD WINAPI Echo(LPVOID unused)
{
    (void)unused;
    SOCKET ls = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_port = htons(ECHO_PORT);
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(ls, (struct sockaddr *)&a, sizeof a) || listen(ls, 4)) { printf("echo: no port\n"); return 1; }
    for (;;)
    {
        SOCKET s = accept(ls, NULL, NULL);
        if (s == INVALID_SOCKET) break;
        printf("echo: a piped connection came in\n");
        char buf[4096];
        int n;
        while ((n = recv(s, buf, sizeof buf, 0)) > 0) send(s, buf, n, 0);
        closesocket(s);
    }
    return 0;
}

typedef int (*Init_t)(void);
typedef void (*Run_t)(void);
typedef void (*Shutdown_t)(void);
typedef void *(*Iface_t)(void);
typedef unsigned (*Connect_t)(void *, const void *, int, int, const void *);
typedef int (*Send_t)(void *, unsigned, const void *, unsigned, int, long long *);
typedef int (*Recv_t)(void *, unsigned, void **, int);
typedef int (*Status_t)(void *, unsigned, void *, int, void *);
typedef void (*Release_t)(void *);

#pragma pack(push, 1)
typedef struct { int type; int size; unsigned long long id; char pad[120]; } Identity;
#pragma pack(pop)

static int Guest(Run_t run, Shutdown_t shutdown, unsigned long long host)
{
    SteamNetJoin(host, ECHO_PORT + 1);
    ULONGLONG t0 = GetTickCount64();
    while (SteamNetState() != STEAM_LOBBY && GetTickCount64() - t0 < 20000) { run(); Sleep(10); }
    for (int i = 0; i < 300; i++) { run(); Sleep(10); }   /* Steam networking a moment to start */
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in a = {0};
    a.sin_family = AF_INET;
    a.sin_port = htons(ECHO_PORT + 1);
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(s, (struct sockaddr *)&a, sizeof a)) { printf("FAIL: guest local port\n"); return 1; }
    enum { TOTAL = 4 << 20 };
    static char out[TOTAL], in[TOTAL];
    for (int i = 0; i < TOTAL; i++) out[i] = (char)(i * 7 + (i >> 11));
    u_long nb = 1;
    ioctlsocket(s, FIONBIO, &nb);
    int put = 0, got = 0;
    ULONGLONG t1 = GetTickCount64(), first = 0;
    while (got < TOTAL && GetTickCount64() - t1 < 40000)
    {
        run();
        if (put < TOTAL) { int k = send(s, out + put, TOTAL - put > 65536 ? 65536 : TOTAL - put, 0); if (k > 0) put += k; }
        int k = recv(s, in + got, TOTAL - got, 0);
        if (k > 0) { if (!first) first = GetTickCount64(); got += k; }
        else if (k == 0) break;
        else Sleep(1);
    }
    printf("guest: sent %d, back %d in %llu ms (first bytes after %llu ms), %s\n", put, got, GetTickCount64() - t1,
           first ? first - t1 : 0, got == TOTAL && memcmp(in, out, TOTAL) == 0 ? "OK: same bytes" : "FAIL");
    /* round trip of small lines, like commands */
    ULONGLONG sum = 0;
    nb = 0;
    ioctlsocket(s, FIONBIO, &nb);
    for (int i = 0; i < 20; i++)
    {
        ULONGLONG t = GetTickCount64();
        send(s, "gcmd x\n", 7, 0);
        int n = 0;
        while (n < 7) { int k = recv(s, in + n, 7 - n, 0); if (k <= 0) break; n += k; }
        sum += GetTickCount64() - t;
    }
    printf("guest: line round trip %llu ms average\n", sum / 20);
    closesocket(s);
    SteamNetStop();
    for (int i = 0; i < 20; i++) { run(); Sleep(10); }
    shutdown();
    return got == TOTAL ? 0 : 1;
}

int main(int argc, char **argv)
{
    if (argc < 3) { printf("usage: steam_probe <steam_api64.dll folder> <work folder>\n"); return 2; }
    setvbuf(stdout, NULL, _IONBF, 0);
    WSADATA w;
    WSAStartup(MAKEWORD(2, 2), &w);
    SetCurrentDirectoryA(argv[2]);
    FILE *f = fopen("steam_appid.txt", "w");
    if (f) { fputs("2396240", f); fclose(f); }
    char dll[MAX_PATH];
    _snprintf(dll, MAX_PATH, "%s\\steam_api64.dll", argv[1]);
    HMODULE api = LoadLibraryA(dll);
    if (!api) { printf("FAIL: no %s\n", dll); return 1; }
    Init_t init = (Init_t)GetProcAddress(api, "SteamAPI_Init");
    Run_t run = (Run_t)GetProcAddress(api, "SteamAPI_RunCallbacks");
    Shutdown_t shutdown = (Shutdown_t)GetProcAddress(api, "SteamAPI_Shutdown");
    if (!init()) { printf("FAIL: SteamAPI_Init (Steam running? game owned?)\n"); return 1; }
    wchar_t wdir[MAX_PATH];
    MultiByteToWideChar(CP_ACP, 0, argv[2], -1, wdir, MAX_PATH);
    if (!SteamNetAttach(api, wdir)) { printf("FAIL: exports missing\n"); return 1; }
    if (argc >= 5 && strcmp(argv[3], "guest") == 0) return Guest(run, shutdown, _strtoui64(argv[4], NULL, 10));
    if (argc >= 4 && strcmp(argv[3], "both") == 0)
    {
        /* host + guest in this process (Steam connects a user to itself only inside one process) */
        CloseHandle(CreateThread(NULL, 0, Echo, NULL, 0, NULL));
        SteamNetHost(ECHO_PORT, "probe", "probe-info", 0);
        ULONGLONG t0 = GetTickCount64();
        while (SteamNetState() != STEAM_LOBBY && GetTickCount64() - t0 < 20000) { run(); Sleep(10); }
        return Guest(run, shutdown, SteamNetMe());
    }
    CloseHandle(CreateThread(NULL, 0, Echo, NULL, 0, NULL));
    SteamNetHost(ECHO_PORT, "probe", "probe-info", 0);   /* private lobby: nobody sees it */
    if (argc >= 4 && strcmp(argv[3], "host") == 0)
    {
        ULONGLONG t0 = GetTickCount64();
        int told = 0;
        while (GetTickCount64() - t0 < 60000) { run(); Sleep(10); if (!told && SteamNetState() == STEAM_LOBBY) { told = 1; printf("host ready: %llu lobby %llu\n", SteamNetMe(), SteamNetLobby()); fflush(stdout); } }
        SteamNetStop();
        shutdown();
        return 0;
    }

    void *ns = ((Iface_t)GetProcAddress(api, "SteamAPI_SteamNetworkingSockets_SteamAPI_v012"))();
    Connect_t connectP2P = (Connect_t)GetProcAddress(api, "SteamAPI_ISteamNetworkingSockets_ConnectP2P");
    Send_t sendMsg = (Send_t)GetProcAddress(api, "SteamAPI_ISteamNetworkingSockets_SendMessageToConnection");
    Recv_t recvMsg = (Recv_t)GetProcAddress(api, "SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection");
    Status_t status = (Status_t)GetProcAddress(api, "SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus");
    Release_t release = (Release_t)GetProcAddress(api, "SteamAPI_SteamNetworkingMessage_t_Release");

    unsigned conn = 0;
    int ok = 0, lobbyOk = 0, sent = 0, lastSt = -2;
    ULONGLONG start = GetTickCount64();
    while (GetTickCount64() - start < 40000 && !ok)
    {
        run();
        Sleep(10);
        if (!lobbyOk && SteamNetState() == STEAM_LOBBY)
        {
            lobbyOk = 1;
            printf("OK: lobby %llu, me %llu (%s)\n", SteamNetLobby(), SteamNetMe(), SteamNetMyName());
            Identity id = {0};
            id.type = 16;
            id.size = 8;
            id.id = SteamNetMe();
            conn = connectP2P(ns, &id, 7, 0, NULL);
            printf("P2P to myself: connection %u\n", conn);
        }
        if (SteamNetState() == STEAM_FAILED) { printf("FAIL: steamnet failed (see swh2_steam.log)\n"); break; }
        if (!conn) continue;
        int st[64] = {0};
        status(ns, conn, st, 0, NULL);
        if (st[0] != lastSt) { printf("  my connection state %d\n", st[0]); lastSt = st[0]; }
        if (st[0] == 3 && !sent) { sent = sendMsg(ns, conn, "ping-steam", 10, 8, NULL) == 1; printf("  sent: %d\n", sent); }
        void *m[4];
        int n = recvMsg(ns, conn, m, 4);
        for (int i = 0; i < n; i++)
        {
            unsigned char *p = (unsigned char *)m[i];
            printf("  echo back: %.*s\n", *(int *)(p + 8), *(char **)p);
            ok = *(int *)(p + 8) == 10 && memcmp(*(char **)p, "ping-steam", 10) == 0;
            release(m[i]);
        }
        if (st[0] >= 4) { printf("  connection ended (%d)\n", st[0]); break; }
    }
    SteamNetFriend fr[16];
    int k = SteamNetFriendsHosting(fr, 16);
    printf("friends hosting a co-op lobby: %d\n", k);
    for (int i = 0; i < k; i++) printf("  %s (host %s), lobby %llu\n", fr[i].name, fr[i].host, fr[i].lobby);
    printf(ok ? "OK: P2P round trip through the host pipe\n" : "FAIL: no P2P round trip\n");
    SteamNetStop();
    for (int i = 0; i < 20; i++) { run(); Sleep(10); }
    shutdown();
    return ok ? 0 : 1;
}
