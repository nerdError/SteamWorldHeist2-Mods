# Randomizer — SteamWorld Heist 2

Requires **SWH2 Mod Loader** 1.0+.

Shuffles missions on the world map, the game's fixed rewards (story key items too, with logic), shop stock, enemies,
enemy hats, weapon stats and projectiles, utility and ability stats, class perks, crew stats, prices, and for fun: dialogue, battle barks, item
and enemy names, music, mission lighting, weapon models, crew hats and NPC looks. The same seed and settings always
produce the same game, so you can race or share seeds.

Start a **new save** for the full experience (in an existing save only places you haven't visited change).

## Presets
Pick one and you're done — a preset sets all options below. Changing any option by hand switches to *Custom*.

| | **Balanced** (default) | **Chaos** |
|---|---|---|
| Missions | shuffled inside each sea | shuffled across the whole map |
| Item rewards | swap with similar tier / similar amounts | anything anywhere |
| Story key items | stay | shuffled with logic (only to earlier places) |
| Shops | stock swapped between shops, same kind and tier | any item in the game |
| Enemies | any enemy from the game, with the health and damage of the one it replaces | any enemy, with its own stats (limited, see below) |
| Enemy faction | of the replaced enemy — no infighting | its own — navy, pirates and atomics fight each other too |
| Enemy hats, crew hats, barks, NPC looks, music | shuffled | shuffled |
| Weapon looks | inside the weapon class | any weapon |
| Mission lighting | same kind of place | anything |
| Projectiles, crew stats | unchanged | shuffled / any values |
| Utility and ability stats | unchanged (Balanced modes available) | every number ±30% |
| Names of items and enemies | unchanged | everything together |
| Weapon stats | unchanged | every stat ±30% |
| Prices | unchanged | ±30% |
| Class perks | unchanged | any perk on any class and level |
| Dialogue | unchanged | lines from the whole game |

### Quick switches
Under the preset, for building your own mix without clicking through every option:
- **All modes** — *Balanced* or *Chaos* for the Mode of every option at once (also the enemy faction and dialogue
  mode); nothing gets switched on or off.
- **Main** — on/off for missions, item rewards, shops, enemies and enemy hats.
- **Stats** — on/off for weapon, utility, ability and crew stats, prices, class perks and projectiles.
- **Cosmetic** — *All*, *Light* (everything except dialogue and names, so the story stays readable) or *Off*.

Each switch shows the option that matches the current settings, or *Mixed* when you set that part by hand.
E.g. *Chaos* preset → *Stats: Off* gives chaotic missions, loot and enemies with vanilla weapon numbers.

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

### Missions
The mission markers on the world map stay where they are, but start other missions. A mission takes everything that
belongs to it along: its map, enemies, script, loot, icon and the quests that ask for it.

- **Balanced**: only inside the same sea (Western Caribbea, Eastern Caribbea, Arctica, Atomic City), so the
  difficulty stays about the same.
- **Chaos**: across the whole map — an Atomic City mission can wait at the start of the game.
- **Never moved**: missions the story needs at a certain time (the ones that open new areas or lead to the next
  quest), bosses, heists, the parley, shops, roaming locations and missions whose loot is a story item. In this game a
  quest only counts a mission if it's active when you finish it, so moving these could block the story.

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

**Never moved** (unless *story key items* is on, see below): key items and anything the story needs (items quests check
for, items scripts refer to by name), and test/unused missions, so nothing can end up somewhere you can't reach.

### Story key items (off in Balanced, on in Chaos)
Key items such as heist keys, blueprints or the diving tank can move to another reward place, with **logic** that keeps
the game beatable (the idea comes from SWD2Randomizer). The game itself provides a story timeline: its debug starting
points list which quests and missions are done and what you own at each step of the story. A key item only moves to a
place the story finishes **earlier** than the step where the item first shows up. A place you finish before the item
even exists can't need that item, and every item can only come earlier, so whatever needed it is still reachable.

- Places whose timing isn't known take no key items: bounties, world map spawns, moved missions, and whatever the
  *skip tutorial* start already counts as done.
- Key items that cutscenes hand out (the booster, the shield, the ram, the atomic engine…), that also lie elsewhere,
  or whose timing isn't known stay where they are. In practice about 4 key items can move, each to 1–3 places.
- The spoiler log lists every key item with where it went and the story steps.

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

### Enemy hats (on in both)
Enemies wear and drop each other's hats (normal and rare ones). It's a swap, so every hat still drops from some enemy.

### Shops (Balanced or Chaos)
What the shop podiums in bars, on the shop boats and in Atomic City sell.
- **Balanced**: items swap between all shops, only with the same kind (weapon, utility, ship equipment, hat) and a
  similar tier. "Sold once" stays with the item.
- **Chaos**: every podium gets a random item from the whole game (anything with a price), no repeats in one shop.
- Not changed: fragment packs and other podiums with a fixed price, the celestial gears, the buy-back podiums.

### Weapon stats (off in Balanced, Chaos ±30%)
Every player weapon, shot and explosion damage, within ±N% (5–90%):
- **Balanced**: base damage changes, and crit (or random) damage makes up the difference, so the total stays about
  the same (blast radius changes half as much).
- **Chaos**: damage, crit, random damage and blast radius each change on their own.

### Prices (off in Balanced, ±30% in Chaos)
Every item with a price gets a random change within ±N% (rounded to 5).

### Class perks (off in Balanced, Chaos in Chaos)
The level-up perks of the six classes move between classes. The starting perk (level 0) of each class stays, and every
perk exists once, so no class gets one twice.
- **Balanced**: only perks of the same level swap, abilities with abilities and stat bonuses with stat bonuses.
- **Chaos**: any perk from level 1 up can land on any class and level.

### Dialogue and barks
- **Dialogue** (off in Balanced, Chaos: whole game): lines in conversations and cutscenes swap, either inside each
  conversation or across the whole game. Answer choices and tutorial conversations stay. Works in every language.
- **Barks** (on in both): the short lines crew and enemies shout in battle.

### NPC looks (on in both)
Bartenders, shopkeepers and townsfolk swap models. Characters that cutscenes use keep theirs.

### Utility stats (off in Balanced, Chaos in Chaos)
Grenade damage and blast radius, repair amounts, charges and cooldowns of usable utilities, and the bonuses of worn ones
(boots, armor, scopes, cog chains, crit goggles, jetpack, xp badge…), within ±N%.
- **Balanced**: each utility gets stronger or weaker as a whole, and its **price follows** (e.g. Repair Kit III heals
  6 instead of 5 and costs 720 instead of 600), so it's still worth its money.
- **Chaos**: every number on its own, price unchanged. Small numbers (1–3) move by ±1 now and then.

### Ability stats (off in Balanced, Chaos in Chaos)
Cooldown, charges, damage, healing and range of class and personal abilities, and their cog cost.
- **Balanced**: an ability that costs cogs gets one step stronger and costs one cog more, or one step weaker and one
  cog cheaper (e.g. Warcry: cooldown 4 instead of 5, 3 cogs instead of 2). Free core abilities stay.
- **Chaos**: every number on its own, cog cost anywhere from 0 to 5.
- Abilities enemies also use stay; passive +1 perks and the inner workings of buffs aren't touched.

### Crew stats (off in Balanced, Chaos in Chaos)
Health, cogs and move distance of every crew member (in vanilla they are all the same: 8 / 3 / 6). The captain stays.
- **Balanced**: one trade-off each, e.g. +2 health for -1 cog, +1 move for -2 health, or nothing.
- **Chaos**: health -3..+4, cogs -1..+2, move -1..+1, each on its own.

### Projectiles (off in Balanced, Chaos in Chaos)
What player weapons shoot: lightning, lasers, fire, the banjo sniper's slow arcing shot… The projectile brings its
speed, range and arc along, so this does change how a weapon plays; damage stays with the weapon. Grenade throws and
aim previews stay. **Balanced**: inside the weapon class; **Chaos**: any player projectile.

### Just for fun
- **Crew hats** (on in both): every crew member wears another crew member's own hat (Daisy and Wesley start with
  theirs). The captain keeps his.
- **Weapon looks** (on in both): weapon models and icons swap, stats stay. Balanced: inside the weapon class, so it's
  still held the right way. Chaos: any weapon — a hammer may look like a rifle.
- **Music** (on in both): mission music, the regions' world map music and the songs of the bar bands, each shuffled
  among its own kind. Music that cutscenes start stays.
- **Mission lighting** (on in both): lighting, sky and background of missions. Balanced: same kind of place (navy
  ship with navy ship, bar with bar…). Chaos: anything — Atomic City light in the Arctic.
- **Names** (off in Balanced, Chaos in Chaos): names of weapons, utilities, ship equipment, hats and enemies swap, in
  every language. Descriptions stay, so read those! Balanced: weapons with weapons, enemies with enemies… Chaos:
  everything together — an enemy may be called "Repair Kit". The spoiler log always uses the real names.

## Not randomized
- Random loot drops, which crew members you get and their classes (the tutorial is scripted around Daisy and
  Wesley), mission maps, personal perks.

## Install
Unpack into the game folder (so you get `SteamWorld Heist 2\Mods\Randomizer\mod.json`), or install with Vortex.
Open SWH2 Mod Loader, tick *Randomizer*, pick a preset (and a seed if you want), click **Apply to game**.
The game must be closed.

Uninstall: untick it and click Apply (or *Restore original files*).
