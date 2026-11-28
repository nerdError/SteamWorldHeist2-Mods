#!/bin/bash
# Network test on the test stand (docs/MULTIPLAYER.md, "Тестовый стенд"): host and guest are two games of ONE GOG copy
# kept for tests, game instances 8 and 9 — the player's own games (Steam / GOG, instance 1-2) may run meanwhile and are
# never minimized or closed. Installs the test mod set into the copy (Coop with netTest, Turbo fast, CheatMenu, MoreSlots,
# QuickStart), then runs `nettest net SLOT --stand` with the given options.
#   bash tools/nettest_stand.sh [SLOT] [nettest net options...]      (SLOT default 6: host slot 7, guest slot 8)
#   e.g. bash tools/nettest_stand.sh 6 --guest-plays --win 20
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
STAND="${SWH2_STAND:-/c/GOG Games/SteamWorld Heist 2 copy}"
slot=6
if [ $# -gt 0 ] && [[ "$1" =~ ^[0-9]+$ ]]; then slot="$1"; shift; fi
built=$(ls "$REPO"/dist/SWH2-ModPack-*/Mods/Coop/swh2coop.dll | tail -1)
if ! cmp -s "$STAND/Mods/Coop/swh2coop.dll" "$built"; then
    echo "Coop dll in the stand differs from $built: run build.cmd (deploys there; skipped while the stand's game runs)"; exit 1
fi
cd "$STAND/SWH2ModLoader" || exit 1
./swh2.exe install --only CheatMenu,Coop,MoreSlots,Turbo,QuickStart --set Turbo.playerSpeed=3 --set Turbo.enemySpeed=4 2>&1 | tail -1
port=(); [[ " $* " == *" --port "* ]] || port=(--port 27115)   # not 27015: the player may be hosting a real game meanwhile
./swh2.exe nettest net "$slot" --stand "${port[@]}" "$@" 2>&1 | cut -c1-260 | tail -40
