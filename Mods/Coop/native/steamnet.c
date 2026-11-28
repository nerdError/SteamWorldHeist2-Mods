/*
 * Steam transport for netplay (docs/MULTIPLAYER.md, "Steam"): no IP addresses, no port forwarding, no VPN.
 *
 * The game is a Steam game: its steam_api64.dll (SDK ~1.55: SteamNetworkingSockets_v012, SteamMatchmaking_v009,
 * SteamFriends_v017) gives P2P connections between two Steam users that go through Valve's relays when a direct
 * route is not possible (NAT), lobbies, rich presence and the overlay's invite dialog, all under the game's app id.
 * The game inits Steam and calls SteamAPI_RunCallbacks every frame itself; this file only uses the flat API.
 *
 * nettcp.c stays as it is: Steam is a pipe in front of it.
 *   host:  nettcp.c listens on 127.0.0.1:<port>; this file makes a friends-only lobby and a P2P listen socket, and
 *          every guest connection that comes in gets its own local TCP connection to that port.
 *   guest: this file listens on 127.0.0.1:<port> and joins the host's lobby; every local connection (nettcp.c's guest
 *          thread, its save fetch) gets its own P2P connection to the lobby's owner. nettcp.c "connects" to
 *          127.0.0.1:<port> as if the host were there.
 * Bytes are sent as reliable messages (ordered, like the TCP stream they carry).
 *
 * How a guest finds the host, no typing:
 *   - Steam friends list: the host's rich presence "connect" = "+connect_lobby <id>" gives it "Join game"; an invite
 *     from the overlay (SteamNetInviteDialog) does the same. A game that is not running is started by Steam with
 *     "+connect_lobby <id>" on its command line (SteamNetLaunchLobby); a running one gets a callback
 *     (GameLobbyJoinRequested_t 333 / GameRichPresenceJoinRequested_t 337, SteamNetJoinRequest).
 *   - SteamNetFriendsHosting: friends playing the game whose lobby has our "swh2coop" key (for a list in a menu).
 * The GOG copy has no Steam (its steam_api64.dll is missing): SteamNetAvailable() = 0, the IP way stays.
 *
 * Threads: one bridge thread does everything (Steam's networking and matchmaking calls are thread-safe). The two
 * callbacks are registered on the main thread at dll start (before the game's SteamAPI_Init is fine: the game's own
 * global CCallbacks do the same) and run on the game's thread inside SteamAPI_RunCallbacks; they only store values.
 */
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <stdarg.h>
#include "steamnet.h"
#pragma comment(lib, "ws2_32.lib")

#define VPORT 7                 /* P2P "virtual port" of our listen socket (any number, same on both sides) */
#define CHUNK 65536
#define MAX_PIPES 8
#define LOBBY_KEY "swh2coop"
#define PROTOCOL "1"

typedef unsigned long long u64;
typedef unsigned int HConn;     /* HSteamNetConnection / HSteamListenSocket */

/* ---- flat API (this, ...) ---- */
typedef void *(*Iface_t)(void);
static void *user, *friends, *mm, *utils, *sockets, *nutils, *apps;
static u64 (*User_GetSteamID)(void *);
static const char *(*Friends_GetPersonaName)(void *);
static int (*Friends_GetFriendCount)(void *, int);
static u64 (*Friends_GetFriendByIndex)(void *, int, int);
static bool (*Friends_GetFriendGamePlayed)(void *, u64, void *);
static const char *(*Friends_GetFriendPersonaName)(void *, u64);
static bool (*Friends_SetRichPresence)(void *, const char *, const char *);
static void (*Friends_ActivateGameOverlayInviteDialog)(void *, u64);
static u64 (*MM_CreateLobby)(void *, int, int);
static u64 (*MM_JoinLobby)(void *, u64);
static void (*MM_LeaveLobby)(void *, u64);
static bool (*MM_SetLobbyData)(void *, u64, const char *, const char *);
static const char *(*MM_GetLobbyData)(void *, u64, const char *);
static bool (*MM_RequestLobbyData)(void *, u64);
static int (*MM_GetNumLobbyMembers)(void *, u64);
static u64 (*MM_GetLobbyMemberByIndex)(void *, u64, int);
static u64 (*MM_GetLobbyOwner)(void *, u64);
static bool (*Utils_IsAPICallCompleted)(void *, u64, bool *);
static bool (*Utils_GetAPICallResult)(void *, u64, void *, int, int, bool *);
static unsigned (*Utils_GetAppID)(void *);
static HConn (*NS_CreateListenSocketP2P)(void *, int, int, const void *);
static HConn (*NS_ConnectP2P)(void *, const void *, int, int, const void *);
static int (*NS_AcceptConnection)(void *, HConn);
static bool (*NS_CloseConnection)(void *, HConn, int, const char *, bool);
static bool (*NS_CloseListenSocket)(void *, HConn);
static int (*NS_SendMessageToConnection)(void *, HConn, const void *, unsigned, int, long long *);
static int (*NS_ReceiveMessagesOnConnection)(void *, HConn, void **, int);
static int (*NS_GetConnectionRealTimeStatus)(void *, HConn, void *, int, void *);
static void (*NS_RunCallbacks)(void *);
static void (*NU_InitRelayNetworkAccess)(void *);
static bool (*NU_SetGlobalConfigValueInt32)(void *, int, int);
static bool (*NU_SetGlobalCallback_ConnStatus)(void *, void (*)(void *));
static void (*Msg_Release)(void *);
static void (*NU_SetDebugOutputFunction)(void *, int, void (*)(int, const char *));
static void (*RegisterCallback)(void *, int);
static Iface_t getUser, getFriends, getMM, getUtils, getSockets, getNUtils, getApps;
static HMODULE api;

#pragma pack(push, 1)
typedef struct { int type; int size; union { u64 steamID; char raw[128]; } u; } Identity;   /* SteamNetworkingIdentity, 136 */
#pragma pack(pop)
/* FriendGameInfo_t (pack 8): CGameID, IP, ports, lobby. */
typedef struct { u64 gameID; unsigned ip; unsigned short port, queryPort; u64 lobby; } FriendGame;
/* SteamNetConnectionStatusChangedCallback_t (pack 8): hConn @0, info @8 (identityRemote @8, listen socket @152,
 * state @184). Checked by steam_probe.c against GetConnectionRealTimeStatus. */
#define CB_IDENTITY 8
#define CB_LISTEN 152
#define CB_STATE 184
enum { ST_NONE, ST_CONNECTING, ST_FINDING_ROUTE, ST_CONNECTED, ST_CLOSED_BY_PEER, ST_PROBLEM };
enum { SEND_RELIABLE_NO_NAGLE = 9, RESULT_OK = 1, RESULT_LIMIT_EXCEEDED = 25 };
enum { CFG_SEND_BUFFER = 9, FLAG_IMMEDIATE = 4 };

/* ---- state ---- */
typedef struct { HConn conn; SOCKET tcp; int up; char *pend; int pendLen; ULONGLONG since; u64 peer; } Pipe;
static Pipe pipes[MAX_PIPES];
static int npipes;
static CRITICAL_SECTION lock, logLock;
static int lockReady;
static HConn incoming[MAX_PIPES];                  /* host: accepted in the callback, piped by the thread */
static u64 incomingPeer[MAX_PIPES];
static int nincoming;
static volatile LONG state, stopping;
static HANDLE thread;
static int role;                                   /* bits: 1 host, 2 guest (both: steam_probe.c, one process) */
static int tcpPort, lobbyType = 1;
static u64 lobby, hostId, me, launchLobby;
static volatile LONG64 joinRequest;
static HConn listenSock;
static SOCKET localListen = INVALID_SOCKET;
static char myName[64], peerName[64], nick[64], info[1024], lobbyInfo[1024];
static wchar_t logPath[MAX_PATH];
static int gameThreadCallbacks;                    /* the game runs SteamAPI_RunCallbacks (not a test) */

static void Log(const char *fmt, ...)
{
    if (!logPath[0]) return;
    char text[1200];
    va_list a;
    va_start(a, fmt);
    _vsnprintf(text, sizeof text - 1, fmt, a);
    va_end(a);
    text[sizeof text - 1] = 0;
    SYSTEMTIME t;
    GetLocalTime(&t);
    if (lockReady) EnterCriticalSection(&logLock);
    FILE *f = _wfopen(logPath, L"ab");
    if (f) { fprintf(f, "%02d:%02d:%02d.%03d %s\r\n", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, text); fclose(f); }
    if (lockReady) LeaveCriticalSection(&logLock);
}

#define GET(var, name) (*(FARPROC *)&var = GetProcAddress(api, name))
static int Resolve(void)
{
    int ok = 1;
    ok &= !!GET(getUser, "SteamAPI_SteamUser_v023");
    ok &= !!GET(getFriends, "SteamAPI_SteamFriends_v017");
    ok &= !!GET(getMM, "SteamAPI_SteamMatchmaking_v009");
    ok &= !!GET(getUtils, "SteamAPI_SteamUtils_v010");
    ok &= !!GET(getSockets, "SteamAPI_SteamNetworkingSockets_SteamAPI_v012");
    ok &= !!GET(getNUtils, "SteamAPI_SteamNetworkingUtils_SteamAPI_v004");
    ok &= !!GET(getApps, "SteamAPI_SteamApps_v008");
    ok &= !!GET(User_GetSteamID, "SteamAPI_ISteamUser_GetSteamID");
    ok &= !!GET(Friends_GetPersonaName, "SteamAPI_ISteamFriends_GetPersonaName");
    ok &= !!GET(Friends_GetFriendCount, "SteamAPI_ISteamFriends_GetFriendCount");
    ok &= !!GET(Friends_GetFriendByIndex, "SteamAPI_ISteamFriends_GetFriendByIndex");
    ok &= !!GET(Friends_GetFriendGamePlayed, "SteamAPI_ISteamFriends_GetFriendGamePlayed");
    ok &= !!GET(Friends_GetFriendPersonaName, "SteamAPI_ISteamFriends_GetFriendPersonaName");
    ok &= !!GET(Friends_SetRichPresence, "SteamAPI_ISteamFriends_SetRichPresence");
    ok &= !!GET(Friends_ActivateGameOverlayInviteDialog, "SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog");
    ok &= !!GET(MM_CreateLobby, "SteamAPI_ISteamMatchmaking_CreateLobby");
    ok &= !!GET(MM_JoinLobby, "SteamAPI_ISteamMatchmaking_JoinLobby");
    ok &= !!GET(MM_LeaveLobby, "SteamAPI_ISteamMatchmaking_LeaveLobby");
    ok &= !!GET(MM_SetLobbyData, "SteamAPI_ISteamMatchmaking_SetLobbyData");
    ok &= !!GET(MM_GetLobbyData, "SteamAPI_ISteamMatchmaking_GetLobbyData");
    ok &= !!GET(MM_RequestLobbyData, "SteamAPI_ISteamMatchmaking_RequestLobbyData");
    ok &= !!GET(MM_GetNumLobbyMembers, "SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
    ok &= !!GET(MM_GetLobbyMemberByIndex, "SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
    ok &= !!GET(MM_GetLobbyOwner, "SteamAPI_ISteamMatchmaking_GetLobbyOwner");
    ok &= !!GET(Utils_IsAPICallCompleted, "SteamAPI_ISteamUtils_IsAPICallCompleted");
    ok &= !!GET(Utils_GetAPICallResult, "SteamAPI_ISteamUtils_GetAPICallResult");
    ok &= !!GET(Utils_GetAppID, "SteamAPI_ISteamUtils_GetAppID");
    ok &= !!GET(NS_CreateListenSocketP2P, "SteamAPI_ISteamNetworkingSockets_CreateListenSocketP2P");
    ok &= !!GET(NS_ConnectP2P, "SteamAPI_ISteamNetworkingSockets_ConnectP2P");
    ok &= !!GET(NS_AcceptConnection, "SteamAPI_ISteamNetworkingSockets_AcceptConnection");
    ok &= !!GET(NS_CloseConnection, "SteamAPI_ISteamNetworkingSockets_CloseConnection");
    ok &= !!GET(NS_CloseListenSocket, "SteamAPI_ISteamNetworkingSockets_CloseListenSocket");
    ok &= !!GET(NS_SendMessageToConnection, "SteamAPI_ISteamNetworkingSockets_SendMessageToConnection");
    ok &= !!GET(NS_ReceiveMessagesOnConnection, "SteamAPI_ISteamNetworkingSockets_ReceiveMessagesOnConnection");
    ok &= !!GET(NS_GetConnectionRealTimeStatus, "SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus");
    ok &= !!GET(NS_RunCallbacks, "SteamAPI_ISteamNetworkingSockets_RunCallbacks");
    ok &= !!GET(NU_InitRelayNetworkAccess, "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess");
    ok &= !!GET(NU_SetGlobalConfigValueInt32, "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32");
    ok &= !!GET(NU_SetGlobalCallback_ConnStatus, "SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_SteamNetConnectionStatusChanged");
    ok &= !!GET(Msg_Release, "SteamAPI_SteamNetworkingMessage_t_Release");
    GET(NU_SetDebugOutputFunction, "SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction");   /* optional */
    ok &= !!GET(RegisterCallback, "SteamAPI_RegisterCallback");
    return ok;
}

/* The interfaces exist once the game's SteamAPI_Init is through. */
static int Ifaces(void)
{
    if (sockets) return 1;
    void *u = getUser();
    if (!u || !User_GetSteamID(u)) return 0;
    friends = getFriends(); mm = getMM(); utils = getUtils(); nutils = getNUtils(); apps = getApps();
    if (!friends || !mm || !utils || !nutils) return 0;
    user = u;
    me = User_GetSteamID(u);
    strncpy(myName, Friends_GetPersonaName(friends), sizeof myName - 1);
    sockets = getSockets();
    return sockets != NULL;
}

/* ---- callbacks: CCallbackBase in C ---- */
/* MSVC puts overloaded virtuals in reverse order: slot 0 = Run(param, ioFailure, call), 1 = Run(param); both slots get
 * the same function (x64: the extra arguments are just registers), 2 = GetCallbackSizeBytes. */
typedef struct { void **vtbl; unsigned char flags; int id; } CallbackBase;
static u64 ParseConnect(const char *s)
{
    const char *p = s ? strstr(s, "+connect_lobby") : NULL;
    return p ? _strtoui64(p + 14, NULL, 10) : 0;
}
static void RunLobbyJoin(CallbackBase *self, void *param) { (void)self; InterlockedExchange64(&joinRequest, (LONG64)*(u64 *)param); }
static void RunPresenceJoin(CallbackBase *self, void *param)
{
    (void)self;
    u64 l = ParseConnect((const char *)param + 8);
    if (l) InterlockedExchange64(&joinRequest, (LONG64)l);
}
static int Size333(CallbackBase *self) { (void)self; return 16; }
static int Size337(CallbackBase *self) { (void)self; return 264; }
static void *vt333[3] = { (void *)RunLobbyJoin, (void *)RunLobbyJoin, (void *)Size333 };
static void *vt337[3] = { (void *)RunPresenceJoin, (void *)RunPresenceJoin, (void *)Size337 };
static CallbackBase cb333 = { vt333, 0, 0 }, cb337 = { vt337, 0, 0 };

/* Connection status (SteamNetConnectionStatusChangedCallback_t), maybe on the game's thread. Host: a guest knocking
 * on our listen socket is accepted here (the way the SDK wants it) and handed to the bridge thread. */
static int InLobby(u64 who)
{
    if (!lobby) return 1;   /* straight to a user id: no lobby to check */
    int n = MM_GetNumLobbyMembers(mm, lobby);
    for (int i = 0; i < n; i++) if (MM_GetLobbyMemberByIndex(mm, lobby, i) == who) return 1;
    return 0;
}
static void OnConnStatus(void *cb)
{
    unsigned char *p = (unsigned char *)cb;
    HConn conn = *(HConn *)p, ls = *(HConn *)(p + CB_LISTEN);
    int st = *(int *)(p + CB_STATE);
    u64 who = ((Identity *)(p + CB_IDENTITY))->u.steamID;
    if (!(role & 1) || !listenSock || ls != listenSock || st != ST_CONNECTING) return;
    if (!InLobby(who))
    {
        NS_CloseConnection(sockets, conn, 0, "not in the lobby", false);
        Log("refused a connection from %llu: not in the lobby", who);
        return;
    }
    int r = NS_AcceptConnection(sockets, conn);
    EnterCriticalSection(&lock);
    if (r == RESULT_OK && nincoming < MAX_PIPES) { incomingPeer[nincoming] = who; incoming[nincoming++] = conn; }
    else NS_CloseConnection(sockets, conn, 0, "busy", false);
    LeaveCriticalSection(&lock);
    Log("guest %llu (%s) connecting: accept %d", who, Friends_GetFriendPersonaName(friends, who), r);
}

/* Steam networking's own messages (why a connection failed) go to our log. */
static void SteamDebug(int type, const char *text) { Log("steam(%d): %s", type, text); }

/* ---- the bridge ---- */
static int ConnState(HConn c)
{
    int status[64] = {0};   /* SteamNetConnectionRealTimeStatus_t: m_eState first */
    return NS_GetConnectionRealTimeStatus(sockets, c, status, 0, NULL) == RESULT_OK ? status[0] : -1;
}

static void ClosePipe(int i, const char *why)
{
    Pipe *p = &pipes[i];
    Log("pipe %u closed: %s", p->conn, why);
    NS_CloseConnection(sockets, p->conn, 0, why, true);   /* linger: what is queued still goes */
    if (p->tcp != INVALID_SOCKET) closesocket(p->tcp);
    free(p->pend);
    pipes[i] = pipes[--npipes];
}

static int AddPipe(HConn c, SOCKET tcp, u64 peer)
{
    if (npipes >= MAX_PIPES) return 0;
    Pipe *p = &pipes[npipes++];
    memset(p, 0, sizeof *p);
    p->conn = c;
    p->tcp = tcp;
    p->since = GetTickCount64();
    p->peer = peer;
    u_long nb = 1;
    ioctlsocket(tcp, FIONBIO, &nb);
    BOOL yes = TRUE;
    setsockopt(tcp, IPPROTO_TCP, TCP_NODELAY, (const char *)&yes, sizeof yes);
    return 1;
}

static int SendAllTcp(SOCKET s, const char *d, int n)
{
    while (n > 0)
    {
        int k = send(s, d, n, 0);
        if (k == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) { Sleep(1); continue; }
        if (k <= 0) return 0;
        d += k;
        n -= k;
    }
    return 1;
}

/* One pipe: Steam -> TCP, TCP -> Steam. 0 = it is gone. */
static int Pump(Pipe *p, char *buf)
{
    int st = ConnState(p->conn);
    if (st == ST_CONNECTED && !p->up)
    {
        p->up = 1;
        Log("pipe %u connected (%llu, %s)", p->conn, p->peer, Friends_GetFriendPersonaName(friends, p->peer));
    }
    if (st == ST_CLOSED_BY_PEER || st == ST_PROBLEM || st < 0) return 0;
    if (!p->up) return GetTickCount64() - p->since < 30000;
    void *msgs[16];
    int n = NS_ReceiveMessagesOnConnection(sockets, p->conn, msgs, 16), ok = 1;
    for (int i = 0; i < n; i++)
    {
        unsigned char *m = (unsigned char *)msgs[i];   /* SteamNetworkingMessage_t: m_pData @0, m_cbSize @8 */
        if (ok) ok = SendAllTcp(p->tcp, *(char **)m, *(int *)(m + 8));
        Msg_Release(m);
    }
    if (!ok) return 0;
    for (int round = 0; round < 8; round++)
    {
        if (!p->pend)
        {
            int k = recv(p->tcp, buf, CHUNK, 0);
            if (k == 0) return 0;   /* the local side closed */
            if (k < 0) return WSAGetLastError() == WSAEWOULDBLOCK;
            p->pend = (char *)malloc(k);
            memcpy(p->pend, buf, k);
            p->pendLen = k;
        }
        int r = NS_SendMessageToConnection(sockets, p->conn, p->pend, (unsigned)p->pendLen, SEND_RELIABLE_NO_NAGLE, NULL);
        if (r == RESULT_LIMIT_EXCEEDED) return 1;   /* Steam's send buffer is full: later */
        free(p->pend);
        p->pend = NULL;
        if (r != RESULT_OK) { Log("send failed: %d", r); return 0; }
    }
    return 1;
}

static u64 CallResult(u64 call, void *out, int size, int id, ULONGLONG since, int *failed)
{
    bool f = false;
    *failed = 0;
    if (!Utils_IsAPICallCompleted(utils, call, &f))
    {
        if (GetTickCount64() - since > 20000) *failed = 1;
        return 0;
    }
    if (f || !Utils_GetAPICallResult(utils, call, out, size, id, &f) || f) { *failed = 1; return 0; }
    return 1;
}

static void HostSetup(void)
{
    static u64 call;
    static ULONGLONG since;
    if (lobby || state == STEAM_FAILED) return;
    if (!call)
    {
        call = MM_CreateLobby(mm, lobbyType, 4);
        since = GetTickCount64();
        Log("creating a lobby (type %d)", lobbyType);
        return;
    }
    struct { int result; u64 lobby; } r = {0};   /* LobbyCreated_t 513 */
    int failed;
    if (!CallResult(call, &r, sizeof r, 513, since, &failed))
    {
        if (failed) { Log("ERROR: no lobby (call failed)"); call = 0; Sleep(2000); }
        return;
    }
    call = 0;
    if (r.result != RESULT_OK || !r.lobby) { Log("ERROR: no lobby (result %d)", r.result); Sleep(2000); return; }
    lobby = r.lobby;
    MM_SetLobbyData(mm, lobby, LOBBY_KEY, PROTOCOL);
    MM_SetLobbyData(mm, lobby, "name", nick[0] ? nick : myName);
    if (info[0]) MM_SetLobbyData(mm, lobby, "info", info);
    char connect[64];
    sprintf(connect, "+connect_lobby %llu", lobby);
    Friends_SetRichPresence(friends, "connect", connect);
    Friends_SetRichPresence(friends, "status", "Co-op host (NE Co-op)");
    listenSock = NS_CreateListenSocketP2P(sockets, VPORT, 0, NULL);
    Log("lobby %llu ready, listen socket %u, guests are piped to 127.0.0.1:%d", lobby, listenSock, tcpPort);
    InterlockedExchange(&state, listenSock ? STEAM_LOBBY : STEAM_FAILED);
}

static void GuestSetup(void)
{
    static u64 call;
    static ULONGLONG since;
    if (hostId || state == STEAM_FAILED) return;
    if (!call)
    {
        call = MM_JoinLobby(mm, lobby);
        since = GetTickCount64();
        Log("joining lobby %llu", lobby);
        return;
    }
    struct { u64 lobby; unsigned perms; bool locked; unsigned response; } r = {0};   /* LobbyEnter_t 504 */
    int failed;
    if (!CallResult(call, &r, sizeof r, 504, since, &failed))
    {
        if (failed) { Log("ERROR: lobby join failed, again"); call = 0; Sleep(2000); }
        return;
    }
    call = 0;
    if (r.response != 1) { Log("ERROR: lobby %llu: enter response %u (gone? full?), again", lobby, r.response); Sleep(3000); return; }
    hostId = MM_GetLobbyOwner(mm, lobby);
    const char *v = MM_GetLobbyData(mm, lobby, "name");
    strncpy(peerName, v && *v ? v : Friends_GetFriendPersonaName(friends, hostId), sizeof peerName - 1);
    v = MM_GetLobbyData(mm, lobby, "info");
    strncpy(lobbyInfo, v ? v : "", sizeof lobbyInfo - 1);
    v = MM_GetLobbyData(mm, lobby, LOBBY_KEY);
    Log("in lobby %llu: host %llu (%s), protocol %s", lobby, hostId, peerName, v && *v ? v : "?");
    char status[96];
    _snprintf(status, sizeof status - 1, "Co-op with %s", peerName);
    Friends_SetRichPresence(friends, "status", status);
    InterlockedExchange(&state, STEAM_LOBBY);
}

static DWORD WINAPI Bridge(LPVOID unused)
{
    (void)unused;
    static char buf[CHUNK];
    int ready = 0;
    while (!stopping)
    {
        if (!ready)
        {
            if (!Ifaces()) { Sleep(250); continue; }
            ready = 1;
            NU_InitRelayNetworkAccess(nutils);
            NU_SetGlobalConfigValueInt32(nutils, CFG_SEND_BUFFER, 8 * 1024 * 1024);   /* a save file fits at once */
            NU_SetGlobalCallback_ConnStatus(nutils, OnConnStatus);
            if (NU_SetDebugOutputFunction) NU_SetDebugOutputFunction(nutils, 4, SteamDebug);   /* warnings and worse */
            Log("Steam ready: %llu (%s), app %u", me, myName, Utils_GetAppID(utils));
        }
        NS_RunCallbacks(sockets);
        if (role & 1) HostSetup();
        if ((role & 2) && !hostId && lobby) GuestSetup();
        if (role & 1)
        {
            EnterCriticalSection(&lock);
            for (int i = 0; i < nincoming; i++)
            {
                SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
                struct sockaddr_in a = {0};
                a.sin_family = AF_INET;
                a.sin_port = htons((u_short)tcpPort);
                a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
                if (s == INVALID_SOCKET || connect(s, (struct sockaddr *)&a, sizeof a) || !AddPipe(incoming[i], s, incomingPeer[i]))
                {
                    if (s != INVALID_SOCKET) closesocket(s);
                    NS_CloseConnection(sockets, incoming[i], 0, "host game not listening", false);
                    Log("ERROR: guest connection %u: no local host on port %d", incoming[i], tcpPort);
                    continue;
                }
                strncpy(peerName, Friends_GetFriendPersonaName(friends, incomingPeer[i]), sizeof peerName - 1);
            }
            nincoming = 0;
            LeaveCriticalSection(&lock);
        }
        if ((role & 2) && hostId && localListen != INVALID_SOCKET)
        {
            SOCKET s = accept(localListen, NULL, NULL);
            if (s != INVALID_SOCKET)
            {
                Identity id = {0};
                id.type = 16;   /* k_ESteamNetworkingIdentityType_SteamID */
                id.size = 8;
                id.u.steamID = hostId;
                HConn c = NS_ConnectP2P(sockets, &id, VPORT, 0, NULL);
                if (!c || !AddPipe(c, s, hostId))
                {
                    Log("ERROR: local connection: no P2P connection to the host (%u)", c);
                    closesocket(s);
                    if (c) NS_CloseConnection(sockets, c, 0, "", false);
                }
                else Log("local connection -> P2P %u to the host", c);
            }
        }
        int up = 0;
        for (int i = npipes - 1; i >= 0; i--)
        {
            if (!Pump(&pipes[i], buf)) ClosePipe(i, "closed");
            else up |= pipes[i].up;
        }
        if (state == STEAM_LOBBY && up) InterlockedExchange(&state, STEAM_CONNECTED);
        if (state == STEAM_CONNECTED && !up) InterlockedExchange(&state, STEAM_LOBBY);
        /* Pace: up to 2 ms waiting for local bytes. */
        fd_set r;
        FD_ZERO(&r);
        for (int i = 0; i < npipes; i++) if (pipes[i].up && !pipes[i].pend) FD_SET(pipes[i].tcp, &r);
        if (r.fd_count) { struct timeval tv = { 0, 2000 }; select(0, &r, NULL, NULL, &tv); }
        else Sleep(npipes ? 2 : 10);
    }
    while (npipes) ClosePipe(npipes - 1, "stopped");
    return 0;
}

static int Start(void)
{
    WSADATA w;
    if (thread) return 1;   /* host and guest in one process (tests): one bridge does both */
    if (!api || WSAStartup(MAKEWORD(2, 2), &w)) return 0;
    InterlockedExchange(&state, STEAM_WAITING);
    thread = CreateThread(NULL, 0, Bridge, NULL, 0, NULL);
    return thread != NULL;
}

/* ---- public ---- */
static void Common(const wchar_t *dir)
{
    if (!lockReady) { InitializeCriticalSection(&lock); InitializeCriticalSection(&logLock); lockReady = 1; }
    if (dir) _snwprintf(logPath, MAX_PATH, L"%s\\swh2_steam.log", dir);
}

int SteamNetAttach(void *module, const wchar_t *dir)
{
    Common(dir);
    api = (HMODULE)module;
    if (!api || !Resolve()) { api = NULL; return 0; }
    return 1;
}

int SteamNetInstall(const wchar_t *dir)
{
    HMODULE m = GetModuleHandleW(L"steam_api64.dll");
    if (!m || !SteamNetAttach(m, dir)) return 0;
    gameThreadCallbacks = 1;
    RegisterCallback(&cb333, 333);   /* GameLobbyJoinRequested_t */
    RegisterCallback(&cb337, 337);   /* GameRichPresenceJoinRequested_t */
    char cmd[1024];
    WideCharToMultiByte(CP_UTF8, 0, GetCommandLineW(), -1, cmd, sizeof cmd, NULL, NULL);
    launchLobby = ParseConnect(cmd);
    Log("---- Steam transport: dll start%s%s", launchLobby ? ", started to join a lobby: " : "", launchLobby ? cmd : "");
    return 1;
}

int SteamNetAvailable(void) { return api != NULL; }

int SteamNetHost(int port, const char *nickname, const char *fingerprint, int type)
{
    if (role & 1) return 0;
    role |= 1;
    tcpPort = port;
    lobbyType = type;
    if (nickname) strncpy(nick, nickname, sizeof nick - 1);
    if (fingerprint) strncpy(info, fingerprint, sizeof info - 1);
    Log("host: local port %d, nick %s", port, nick);
    return Start();
}

int SteamNetJoin(u64 id, int localPort)
{
    if ((role & 2) || !id) return 0;
    role |= 2;
    /* Lobby ids have the "chat" account type (4) in bits 52..55, users 1. */
    if (((id >> 52) & 0xf) == 1) hostId = id;
    else lobby = id;
    struct sockaddr_in a = {0};
    WSADATA w;
    WSAStartup(MAKEWORD(2, 2), &w);
    localListen = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    a.sin_family = AF_INET;
    a.sin_port = htons((u_short)localPort);
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (localListen == INVALID_SOCKET || bind(localListen, (struct sockaddr *)&a, sizeof a) || listen(localListen, 4))
    {
        Log("ERROR: guest: local port %d not free", localPort);
        InterlockedExchange(&state, STEAM_FAILED);
        return 0;
    }
    u_long nb = 1;
    ioctlsocket(localListen, FIONBIO, &nb);
    Log("guest: %s %llu, local port %d", lobby ? "lobby" : "host user", id, localPort);
    int ok = Start();
    if (ok && hostId) InterlockedExchange(&state, STEAM_LOBBY);   /* a user id: no lobby to join */
    return ok;
}

int SteamNetState(void) { return (int)state; }
u64 SteamNetLobby(void) { return lobby; }
u64 SteamNetMe(void) { return me; }
const char *SteamNetMyName(void) { return myName; }
const char *SteamNetPersona(void) { if (api) Ifaces(); return myName; }
const char *SteamNetPeerName(void) { return peerName; }
const char *SteamNetLobbyInfo(void) { return lobbyInfo; }
u64 SteamNetLaunchLobby(void) { return launchLobby; }
u64 SteamNetJoinRequest(void) { return (u64)InterlockedExchange64(&joinRequest, 0); }
void SteamNetInviteDialog(void) { if (friends && lobby && (role & 1)) Friends_ActivateGameOverlayInviteDialog(friends, lobby); }

int SteamNetFriendsHosting(SteamNetFriend *out, int max)
{
    if (!api || !Ifaces()) return 0;
    static u64 asked[64];
    static int nasked;
    unsigned app = Utils_GetAppID(utils);
    int n = Friends_GetFriendCount(friends, FLAG_IMMEDIATE), k = 0;
    for (int i = 0; i < n && k < max; i++)
    {
        u64 f = Friends_GetFriendByIndex(friends, i, FLAG_IMMEDIATE);
        FriendGame g = {0};
        if (!Friends_GetFriendGamePlayed(friends, f, &g) || (unsigned)(g.gameID & 0xffffff) != app || !g.lobby) continue;
        const char *v = MM_GetLobbyData(mm, g.lobby, LOBBY_KEY);
        if (!v || !*v)
        {
            int j;
            for (j = 0; j < nasked && asked[j] != g.lobby; j++) {}
            if (j == nasked && nasked < 64) { asked[nasked++] = g.lobby; MM_RequestLobbyData(mm, g.lobby); }
            continue;
        }
        SteamNetFriend *e = &out[k++];
        memset(e, 0, sizeof *e);
        e->friendId = f;
        e->lobby = g.lobby;
        strncpy(e->name, Friends_GetFriendPersonaName(friends, f), sizeof e->name - 1);
        v = MM_GetLobbyData(mm, g.lobby, "name");
        strncpy(e->host, v ? v : "", sizeof e->host - 1);
    }
    return k;
}

void SteamNetStop(void)
{
    if (!thread) return;
    InterlockedExchange(&stopping, 1);
    WaitForSingleObject(thread, 3000);
    CloseHandle(thread);
    thread = NULL;
    if (listenSock) NS_CloseListenSocket(sockets, listenSock);
    if (lobby) MM_LeaveLobby(mm, lobby);
    if (friends) { Friends_SetRichPresence(friends, "connect", ""); Friends_SetRichPresence(friends, "status", ""); }
    if (localListen != INVALID_SOCKET) closesocket(localListen);
    listenSock = 0;
    lobby = hostId = 0;
    localListen = INVALID_SOCKET;
    role = 0;
    stopping = 0;
    InterlockedExchange(&state, STEAM_OFF);
    Log("stopped");
}
