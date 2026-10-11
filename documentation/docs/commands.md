# Usage Commands
Most of the commands can also be used using ! prefix instead of . (like !ready)

- `.ready` Marks the player ready (Alias: `.r`)
- `.unready` Marks the player unready (Alias: `.ur`, `.notready`)
- `.forceready` Force-readies the player's team (Only works when using Match setup using JSON/Get5)
- `.pause` Starts a tactical timeout of `matchzy_pause_duration` seconds (Alias: `.p`)
- `.tech` Pauses the match in freezetime with no time limit. Each team can use it `matchzy_max_tech_pauses_allowed` times per map (pauses used in the knife round don't count towards it)
- `.unpause` During a `.pause` timeout, the team that called it cancels it right away. During a `.tech` pause, both teams need to type .unpause to unpause the match (Alias: `.up`)
- `.pause`, `.tech` and `.unpause` work while the match is live and during the knife round. Like any pause, it starts in the next freezetime: if it is requested once the knife round is already being played, it is applied in the freezetime of the first live round instead
- `.stay` Stays on the same side (For knife winner, after the knife round)
- `.switch`/`.swap` Switches the side (For knife winner, after the knife round)
- `.stop` Restore the backup of the current round (Both teams need to type .stop to restore the current round)
- `.coach <side>` Starts coaching the specified side. Example: `.coach t` to start coaching terrorist side!
- `.uncoach` Leaves coaching slot

# Practice Mode Commands

- `.spawn <number>` Spawns to the provided competitive spawn number of same team
- `.ctspawn <number>` Spawns to the provided competitive spawn number of CT (Alias: `.cts`)
- `.tspawn <number>` Spawns to the provided competitive spawn number of T (Alias: `.ts`)
- `.bestspawn` Teleports you to your team's closest spawn from your current position
- `.worstspawn` Teleports you to your team's furthest spawn from your current position
- `.bestctspawn` Teleports you to CT team's closest spawn from your current position
- `.worstctspawn` Teleports you to CT team's furthest spawn from your current position
- `.besttspawn` Teleports you to T team's closest spawn from your current position
- `.worsttspawn` Teleports you to T team's furthest spawn from your current position
- `.showspawns` Highlights all the competitive spawns
- `.hidespawns` Hides the highlighted spawns
- `.bot` Adds a bot on user's current position
- `.crouchbot` Adds a crouched bot on user's current position (Alias: `.cbot`)
- `.boost` Adds a bot on current position and boosts player on it
- `.crouchboost` Adds a crouched bot on current position and boosts player on it
- `.ct`, `.t`, `.spec` Changes player team to the requested team
- `.fas` / `.watchme` Forces all players into spectator except the player who called this command
- `.nobots` Removes all the bots
- `.clear` Clears all the active smokes, molotoves and incendiaries
- `.fastforward` Fastforwards the server time to 20 seconds (Alias: `.ff`)
- `.noflash` Toggles immunity for flashbang (it will still blind others with noflash disabled. Alias: `.noblind`)
- `.dryrun` Turns on dry-run mode (Alias: `.dry`)
- `.god` Turns on god mode
- `.savenade <name> <optional description>` Saves a lineup (Alias: `.sn`)
- `.loadnade <name>` Loads a lineup (Alias: `.sn`)
- `.deletenade <name>` Deletes a lineup from file (Alias: `.dn`)
- `.importnade <code>` Upon saving a lineup a code will be printed to chat, alternatively those can be retrieved from the savednades.cfg (Alias: `.in`)
- `.listnades <optional filter>` Lists either all saved lineups ever or if given a filter only those that match the filter (Alias: `.lin`)
- `.break` Breaks all the breakable entities (glass windows, wooden doors, vents, etc)
- `.rethrow` Rethrows your last thrown grenade (Alias: `.rt`)
- `.timer` Starts a timer immediately and stops it when you type .timer again, telling you the duration of time
- `.last` Teleports you back to where you threw your last grenade from
- `.back <number>` Teleports you back to the provided position in your grenade history
- `.delay <delay_in_seconds>` Sets a delay on your last grenade. This is only used when using .rethrow or .throwindex
- `.throwindex <index> <optional index> <optional index>` Throws grenade of provided position(s) from your grenade thrown history. Example: `.throwindex 1 2` will throw your 1st and 2nd grenade. `.throwindex 4 5 8 9` will throw your 4th, 5th, 8th and 9th grenade (If you've added delay in grenades, they'll be thrown with their specific delay).
- `.lastindex` Prints index number of your last thrown grenade.
- `.rethrowsmoke` Throws your last thrown smoke grenade.
- `.rethrownade` Throws your last thrown HE grenade.
- `.rethrowflash` Throws your last thrown flash.
- `.rethrowmolotov` Throws your last thrown molotov.
- `.rethrowdecoy` Throws your last thrown decoy.
- `.solid` Toggles mp_solid_teammates
- `.impacts` Toggles sv_showimpacts
- `.traj` Toggles sv_grenade_trajectory_prac_pipreview 

# Admin Commands

- `.start` Force starts a match.
- `.restart` Force restarts/resets a match. (Alias: `.endmatch`, `.forceend`)
- `.forcepause` Pauses the match as an admin (Players cannot unpause the admin-paused match). Works from any team, including spectators, once the match has started (knife round included). (Alias: `.fp`)
- `.forceunpause` Force unpauses the match. Also cancels a pause that is still pending for the first live round. (Alias: `.fup`)
- `.timerpause` Pauses, with no time limit, the warmup countdown that cancels the match if not all players connect before each map. (Alias: `.tp`, console: `css_timerpause`)
- `.timerunpause` Resumes that countdown with the time it had left. (Alias: `.tup`, console: `css_timerunpause`)
- `.restore <round>` Restores the backup of provided round number.
- `.skipveto` / `.sv` Skips the current veto phase.
- `.roundknife` / `.rk` Toggles the knife round. If disabled, match will directly go from Warmup phase to Live phase.
- `.playout` Toggles playout (If playout is enabled, all rounds would be played irrespective of winner. Useful in scrims!)
- `.whitelist` Toggles whitelisting of players. To whitelist a player, add the steam64id in `cfg/MatchZy/whitelist.cfg`
- `.readyrequired <number>` Sets the number of ready players required to start the match. If set to 0, all connected players will have to ready-up to start the match.
- `.settings` Displays the current setting, like whether knife is enabled or not, value of readyrequired  players, etc.
- `.map <mapname>` Changes the map
- `.asay <message>` Say as an admin in all chat
- `.reload_admins` Reloads admins from `admins.json`
- `.team1 <name>` Sets name for Team 1 (CT by default)
- `.team2 <name>` Sets name for Team 2 (Terrorist by default)
- `.prac` Starts Practice Mode (Alias: `.tactics`)
- `.exitprac` Exits from practice mode and loads Match mode.
- `.rcon <command>` Sends command to the server
