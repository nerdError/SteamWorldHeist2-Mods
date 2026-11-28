/* Steam transport for netplay (steamnet.c): a Steam lobby + P2P connections bridged to the local TCP of nettcp.c. */
#pragma once

enum { STEAM_OFF, STEAM_WAITING, STEAM_LOBBY, STEAM_CONNECTED, STEAM_FAILED };

/* Main thread, early (dll start): finds the game's steam_api64.dll (0 = none: GOG copy), registers the "join game"
 * callbacks, reads "+connect_lobby <id>" from the command line. dir = where swh2_steam.log goes. */
int SteamNetInstall(const wchar_t *dir);
int SteamNetAvailable(void);

/* Host: a friends-only lobby (data: swh2coop = protocol, name = nick, info = mods fingerprint), rich presence "connect"
 * (Steam's "Join game" on the host in the friends list), a P2P listen socket; each guest connection is piped to
 * 127.0.0.1:tcpPort (nettcp.c's host). */
int SteamNetHost(int tcpPort, const char *nick, const char *info, int lobbyType);
/* Guest: joins the lobby (or, id is a user: connects straight to that host) and listens on 127.0.0.1:localPort; each
 * local connection (nettcp.c's guest, its save fetch) is piped to the host over Steam. */
int SteamNetJoin(unsigned long long id, int localPort);

int SteamNetState(void);                     /* STEAM_* */
unsigned long long SteamNetLobby(void);      /* host: its lobby; guest: the joined one */
unsigned long long SteamNetMe(void);
const char *SteamNetMyName(void);
const char *SteamNetPeerName(void);          /* guest: the host's nick (lobby data); host: the last guest's persona */
const char *SteamNetLobbyInfo(void);         /* guest: the host's "info" (mods fingerprint) */
unsigned long long SteamNetLaunchLobby(void);/* "+connect_lobby <id>" this game was started with (0 = none) */
unsigned long long SteamNetJoinRequest(void);/* "Join game" / an invite accepted while this game runs: the lobby, once */
void SteamNetInviteDialog(void);             /* host: the overlay's invite dialog for its lobby */

/* Friends playing the game in a co-op lobby (polled; lobby data comes a moment after the first call). */
typedef struct { unsigned long long friendId, lobby; char name[64], host[64]; } SteamNetFriend;
int SteamNetFriendsHosting(SteamNetFriend *out, int max);

/* Tests: Steam is not the game's (steam_probe.c inits it itself), callbacks are run by the caller. */
int SteamNetAttach(void *steamApiModule, const wchar_t *dir);
void SteamNetStop(void);
