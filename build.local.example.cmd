@rem Personal build settings. Copy this file to build.local.cmd (that one is not committed) and edit it.
@rem 1 = every build.cmd run also copies the loader and mods into the game folder(s) (skipped while that game is running).
@set AUTO_DEPLOY=0
@rem Game folders, separated by ";" (e.g. Steam + GOG); empty = the default Steam folder.
@set GAME_DIRS=
@rem @set GAME_DIRS=C:\Program Files (x86)\Steam\steamapps\common\SteamWorld Heist 2;C:\GOG Games\SteamWorld Heist 2
@rem 1 = if the mod loader of a game folder is open while deploying, close it, update it and start it again with
@rem --apply (it applies the enabled mods with their new versions to the game). 0 = skip that folder.
@set RESTART_LOADER=0
