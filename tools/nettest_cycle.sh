#!/bin/bash
# Unattended netplay test cycle (docs/MULTIPLAYER.md, "Автоматический прогон"): checks that the built Coop dll really
# is in the game folder (build.cmd skips folders whose game runs), installs the test mod set (Coop with netTest, Turbo
# fast), makes a fresh record (autoplay) unless --replay-only, replays against it and compares.
#   bash tools/nettest_cycle.sh [--replay-only] [slot]      (slot default 6 = bank "nettest start")
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
GAME="/c/Program Files (x86)/Steam/steamapps/common/SteamWorld Heist 2"
replay_only=0; slot=6
for a in "$@"; do case "$a" in --replay-only) replay_only=1 ;; *) slot="$a" ;; esac; done
built=$(ls "$REPO"/dist/SWH2-ModPack-*/Mods/Coop/swh2coop.dll | tail -1)
if ! cmp -s "$GAME/Mods/Coop/swh2coop.dll" "$built"; then
    echo "Coop dll in the game folder differs from $built: run build.cmd deploy with the game closed"; exit 1
fi
cd "$GAME/SWH2ModLoader" || exit 1
./swh2.exe install --only CheatMenu,Coop,MoreSlots,Turbo,QuickStart --set Turbo.playerSpeed=3 --set Turbo.enemySpeed=4 2>&1 | tail -1
if [ $replay_only = 0 ]; then
    echo "=== AUTOPLAY (fresh record)"
    ./swh2.exe nettest run "$slot" --autoplay --timeout 900 --stall 90 2>&1 | cut -c1-200 | tail -2
fi
echo "=== REPLAY"
./swh2.exe nettest run "$slot" --timeout 900 --stall 90 2>&1 | cut -c1-260 | tail -25
