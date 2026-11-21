# Randomizer — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+.

Shuffles the game's fixed rewards and replaces enemies with enemies from the whole game. The same seed and settings
always produce the same game, so you can race or share seeds.

Start a **new save** for the full experience (in an existing save only places you haven't visited change).

## Presets
Pick one and you're done — a preset sets all options below. Changing any option by hand switches to *Custom*.

| | **Balanced** (default) | **Chaos** |
|---|---|---|
| Item rewards | swap with similar tier / similar amounts | anything anywhere |
| Enemies | any enemy from the game, with the health and damage of the one it replaces | any enemy, with its own stats (limited, see below) |
| Enemy faction | of the replaced enemy — no infighting | its own — navy, pirates and atomics fight each other too |
| Weapon damage, prices | unchanged | ±30% |

**Balanced** plays like the normal game: you meet new enemies and find gear in new places, but the difficulty
curve stays the same.

**Chaos** is meant to be wild but still beatable:
- An enemy can be at most **2× the health** and **1.5× the damage** of the one it replaces (*Chaos limits: Fair*).
- **Stronger enemies come in smaller numbers**: the game spends spawn points per room, and a stronger replacement
  costs more, so instead of three seabots you may get one late-game pirate. With limits on you never get *more*
  enemies than vanilla.
- Factions fight each other, so enemies thin each other out.
- Rewards come from the whole game too, so early on you can find late-game weapons (and money).
- With *Chaos limits: Loose* (3× / 2×) or *Off* (no limits) it gets harder — *Off* may be unbeatable.

## How it works
The randomizer doesn't run while you play. When you click **Apply to game**, the loader takes the *original* game
data, the randomizer edits it once using your seed, and the result is written to the game. Every Apply starts again
from the originals, so changing the seed or options never stacks on top of an earlier run.

- **Seed**: any text or number. Empty = the loader picks a random seed and saves it, so you can see and share it.
- **Same seed + same settings = the same game**, on any PC. Each part (items, enemies, damage, prices) uses its own
  random stream from the seed, so e.g. turning prices on doesn't change where the items went.
- **Spoiler log**: every Apply writes `SWH2ModLoader\Reports\Randomizer\spoiler_<seed>_<date>.txt` with every change
  (items "new (was old)", enemies "old -> new" with their health/damage/faction, damage and prices). Don't open it if
  you want surprises.

## What gets randomized

### Item rewards
The game's **fixed** rewards are collected from four kinds of places and shuffled between them:

| Where | What it is |
|---|---|
| Missions | the guaranteed loot of a mission (not random drops) |
| Bounties | the rewards you buy with bounty/reputation in each region |
| World map | artifacts and flagship rewards placed on the region maps |
| Quests | item rewards for completing quests |

It's a **swap**, not new loot: the same set of rewards exists as in vanilla, just in different places.

- **Balanced**: an item only swaps with items of a similar tier (tier 0–1, 2, 3, 4, 5+). In this game the tier goes
  with the part of the map (Western Caribbea → Arctica → Eastern Caribbea → Atomic City), so gear stays roughly at the
  same point of the game. Money only swaps with similar amounts of money, fragments with similar amounts of fragments.
- **Chaos**: everything goes in one pool — a weapon can turn into 50 gallons and the other way round.
- **Include money & fragment rewards**: if off, money and fragment rewards stay where they are and only real items move.
- A gear item that lands in a spot which gave e.g. 12 fragments is given once, not 12 times.

**Never moved**: key items and anything the story needs (items quests check for, items scripts refer to by name),
and test/unused missions, so nothing can end up somewhere you can't reach.

### Enemies
Enemies in the game's spawn tables are replaced — both the enemies at the start of a mission and reinforcements,
single enemies and packs.

- Replacements come from **all regular enemies of the game** (navy, pirates, atomics).
- **No repeats**: inside one spawn table every enemy gets a different replacement, and an enemy is never "replaced"
  by itself.
- **Flying stays flying** and **a pack stays a pack of the same size** — that's about where enemies can spawn and how
  many there are, not about difficulty.
- **Stats**
  - *Balanced*: the new enemy gets the health, armor, damage and spawn cost of the enemy it replaces. Its looks,
    weapon type, AI and abilities stay its own — so a "seabot-strength" pirate still shoots like a pirate.
  - *Chaos*: the new enemy keeps its own stats, within the *Chaos limits*, and stronger ones spawn in smaller numbers.
- **Faction**
  - *Of the enemy it replaces*: e.g. a pirate in a navy mission fights for the navy.
  - *Its own*: the pirate stays a pirate and fights the navy as well as you.

**Not changed**: bosses and their parts, scripted enemies (placed by the mission itself), the tutorial, mimics,
spawns that need a special position (e.g. snipers on their perches), and enemies that a mission checks by name
(e.g. "destroy all walking bombs").

Good to know: special abilities can't be scaled. An atomic reviver with seabot health still revives its allies,
a totem bearer still buffs — most likely its new faction's units, if it fights for another faction.

### Weapon damage (off in Balanced, ±30% in Chaos)
Every player weapon gets its own random damage change within ±N% (5–90%). Both shot and explosion damage.

### Prices (off in Balanced, ±30% in Chaos)
Every item with a price gets a random change within ±N% (rounded to 5).

## Not randomized
- **Shops**: what shops sell is not changed (only the prices, if *Prices* is on).
- Random loot drops, crew members and their classes, mission maps.

## Install
Unpack into the game folder (so you get `SteamWorld Heist 2\Mods\Randomizer\mod.json`), or install with Vortex.
Open SWH2 Mod Loader, tick *Randomizer*, pick a preset (and a seed if you want), click **Apply to game**.
The game must be closed.

Uninstall: untick it and click Apply (or *Restore original files*).
