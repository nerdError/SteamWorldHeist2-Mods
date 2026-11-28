/* SWH2 netplay groundwork: record/replay of tactics commands (netplay.c). */
#pragma once
#include <stddef.h>

enum { NET_OFF, NET_RECORD, NET_REPLAY };
enum { ORIGIN_OTHER, ORIGIN_PLAYER, ORIGIN_AI, ORIGIN_LUA, ORIGIN_PROJECTILE, ORIGIN_PREVIEW, ORIGIN_REPLAY };

void NetInstall(char *exeBase, const wchar_t *dir);
/* The netplay test is on (record or replay). */
int NetActive(void);

/* exemap.c: game build (Steam / GOG) and Steam-build RVA <-> this build's RVA (0 = no counterpart). */
void ExeMapInit(const char *exeBase);
const char *ExeBuildName(void);
unsigned long ExeRva(unsigned long steamRva);
unsigned long SteamRva(unsigned long rva);
void NetLog(const char *fmt, ...);
const char *NetOriginName(int origin);
int NetClassify(const unsigned long *rvas, int n);
int NetClassifyReal(const unsigned long *rvas, int n, int real);
int NetTargetBytes(const unsigned char *target, unsigned char *out, int max, int *type);
int NetBuildTarget(unsigned char *target, int type, const unsigned char *data, int len);
void NetFreeTarget(unsigned char *target);
int NetParseLine(const char *line, void *entry);
size_t NetEntrySize(void);
/* GetTickCount64 of the last tactics world update (0 = none yet): the input lock lets everyone play while it stalls. */
unsigned long long NetWorldTick(void);
/* The same, only in a combat mission (not the bar, the submarine, a shop): what the host's save counts as "in a battle". */
unsigned long long NetBattleTick(void);
/* GetTickCount64 of the last tutorial popup Show (0 = none yet): the input lock lets everyone close it. */
unsigned long long NetPopupTick(void);

/* nettcp.c: the host streams its log to one guest, the guest appends it to its replay file (background threads). */
enum { NET_TCP_OFF, NET_TCP_WAITING, NET_TCP_CONNECTED, NET_TCP_CLOSED, NET_TCP_FAILED };
int NetTcpHost(const char *bindAddr, int port, const wchar_t *logPath);   /* bindAddr "" = every interface */
int NetTcpGuest(const char *addr, int port, const wchar_t *replayPath);
int NetTcpState(void);
/* Guest -> host lines on the same connection: the guest sends (game thread, 0 = not connected), the host polls the
 * lines that arrived (1 = one copied to out). */
int NetTcpSend(const char *line);
int NetTcpPoll(char *out, int max);
/* Guest: a line sent first on every connect (the nickname). */
void NetTcpSetHello(const char *line);
/* Host: the save slot file the guest's loader fetches with "getsave" before it starts the guest's game. */
void NetTcpSetSave(const wchar_t *path);
/* Host: what "getinfo" answers (the host loader's fingerprint of game data + mods, compared by the guest's loader). */
void NetTcpSetInfo(const char *text);
/* Guest: how often the connection came back after a break; tests: break it once, ms after connecting. */
int NetTcpReconnects(void);
/* Guest: its save was made at this byte of the host's log (session "from"): the host's log is taken from there. */
void NetTcpSetFrom(long long offset);
/* Guest: the host log byte its replay file starts at; rebase it to a later one (0 = not possible). */
long long NetTcpBase(void);
int NetTcpRebase(long long to);
/* Guest: fetch the host's save in the background; take the result (1 = got it, data is the caller's to free; -1 failed;
 * 0 still running / nothing started). */
int NetTcpFetchStart(void);
int NetTcpFetchTake(char **data, int *len, long long *from, int *ends, int *battle);
/* Guest, called by swh2coop.c from the main loop (every frame, menus too): the next battle without a restart. */
/* Guest: why the last fetch failed (the host's answer or what went wrong). */
const char *NetTcpFetchError(void);
void NetIdle(void);
/* Guest: go to the main menu from the battle or the world map (as pause, Quit; the autopilot's "quit"). */
void NetQuitToMenu(void);
int NetBlockInput(void);   /* guest: the dll is about to press Continue by itself: player input dropped */
void NetLoadCheckpoint(void);   /* tests: the pause menu's "Load checkpoint" */
void NetTestAim(int ordinal, int seconds);   /* tests: the host "aims" with that crewmate (aim lines) */
/* Test autopilot: the netplay log (record / replayed) has this text (1). */
int NetLogHas(const char *text);
void NetTcpTestDrop(int ms);
