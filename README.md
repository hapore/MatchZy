MatchZy - Match Plugin for CS2!
==============

MatchZy is a plugin for CS2 (Counter Strike 2) for running and managing practice/pugs/scrims/matches with easy configuration!

[![Discord](https://discordapp.com/api/guilds/1169549878490304574/widget.png?style=banner2)](https://discord.gg/2zvhy9m7qg)

## Feature Highlights:

* Pug mode with simple commands to manage!
* Support of [Get5 Panel!](https://shobhit-pathak.github.io/MatchZy/get5/)
* Support BO1/BO3/BO5 and Veto when using Match configuration or Get5 Panel!
* [Setting up matches](https://shobhit-pathak.github.io/MatchZy/match_setup/) and locking players into their team
* Practice Mode with `.bot`, `.spawn`, `.ctspawn`, `.tspawn`, `.nobots`, `.rethrow`, `.last`, `.timer`, `.clear`, `.exitprac` and many more commands!
* Knife round (With expected logic, i.e., team with most players win. If same number of players, then team with HP advantage wins. If same HP, winner is decided randomly)
* Automatically starts demo recording and stop recording when match is ended (Make sure you have tv_enable 1)
* Automatically uploads demo on map end on the given URL.
* Players whitelisting (Thanks to [DEAFPS](https://github.com/DEAFPS)!)
* Coaching system
* Damage report after every round
* Support for round restore (Currently using the vanilla valve's backup system)
* Ability to create admin and allowing them access to admin commands
* Database Stats and CSV Stats! MatchZy stores data and stats of all the matches in a local SQLite database (MySQL Database is also supported!) and also creates a CSV file for detailed stats of every player in that match!
* Provides easy configuration
* And much more!!


## Stats Backup & Recovery

Sometimes a match's player stats fail to persist to `matchzy_stats_players` (e.g. a MySQL blip mid-match), leaving an empty/incomplete CSV and no rows in the database, with no obvious error to the admin. This fork adds a self-contained recovery mechanism for that scenario:

* **Per-round stats backup**: on every `round_end` (not just round start), MatchZy writes a JSON file to `csgo/MatchZy_StatsBackup/{matchid}/matchzy_stats_{matchid}_{mapnumber}_round{NN}.json`, containing the same data that goes into `matchzy_stats_players` for that map, plus a small header (map name, series type, team names/scores) and the full `round_end` webhook payload for reference. This is a **separate** system from the round-restore backups in `csgo/MatchZyDataBackup/` (used by `.stop`/`.restore`/`matchzy_loadbackup`) — that one is written on round *start* and therefore never covers the final round of a map; this one is written on round *end*, so the last round played always has a backup. Because CS2's native match stats are cumulative per map, the file from the last round alone is enough to reconstruct the full map result.
* **Automatic self-healing**: after a map ends, the plugin verifies that `matchzy_stats_players` actually has one row per player for that match/map. If rows are missing (e.g. the DB connection dropped mid-match), it automatically retries writing them from the just-written backup JSON — no admin action needed in the common case. The plugin also proactively reopens its database connection if it was closed/broken, instead of silently failing every subsequent write for the rest of the match.
* **Manual recovery command**: `matchzy_retry_stats <matchid> [mapnumber]` (alias `css_retrystats`, requires `@css/rcon`) reprocesses a match from its backup JSON on disk — inserting/updating `matchzy_stats_matches`, `matchzy_stats_maps` and `matchzy_stats_players`, and regenerating the CSV. It takes the matchid as an explicit parameter, so it works from server RCON/console at any time afterwards, even if the server has since moved on to another match. It's safe to run more than once (the underlying upserts are idempotent).

## Demo Finalization Window

Stopping GOTV and uploading the demo after a series ends can take up to ~2 minutes (recording flush delay + the HTTP upload itself). Previously, an external backend could load a brand new match onto the same server seconds after the last round ended — while the previous demo was still being closed/uploaded — corrupting the demo file and crashing/bugging the server. This fork closes that race:

* **`isFinalizingDemo` guard**: set to `true` in `EndSeries()` for a fixed, deterministic window (`tv_delay` flush + a fixed upload grace period — not tied to whether the actual upload succeeds, fails, or is disabled) and checked by both `matchzy_loadmatch` and `matchzy_loadmatch_url`. While it's active, any attempt to load a new match is rejected with a clear log/chat message instead of silently racing the in-progress demo.
* **`demo_window_end` event**: once that fixed window closes, the plugin emits a `demo_window_end` webhook event (`matchzy_remote_log_url`) with `matchid`/`map_number`. This is the event an external backend/panel should use to know it's safe to reuse the server for a new match — **not** `series_end`, which only signals that the match *result* is final, and **not** `demo_upload_ended`, since the real upload duration isn't bounded. See [Events.md](Events.md) for the full payload.

## Database Schema

MatchZy auto-creates the 5 tables below on first connect (`CREATE TABLE IF NOT EXISTS`, both the MySQL and SQLite code paths in `DatabaseStats.cs`), so manual setup is normally unnecessary. This DDL (MySQL dialect) is provided for cases where you want to provision the schema ahead of time — e.g. pointing an external consumer (like a backend/panel) at the database before the plugin has ever connected.

* `matchzy_stats_matches`, `matchzy_stats_maps`, `matchzy_stats_players` — written directly by the plugin whenever `matchzy_stats_direct_save_enabled` is `1` (the default). See [Stats Backup & Recovery](#stats-backup--recovery) above.
* `matchzy_stats_players_rounds` — **the plugin only creates this table, it never writes rows to it.** Per-round delta stats are populated exclusively by an external consumer processing the `round_end` webhook (see [Events.md](Events.md)) — this is what lets a panel/backend reflect live match state without waiting for the map to end. `kast` here is the raw per-round `kast_this_round` boolean (0/1), **not** the cumulative percentage stored in `matchzy_stats_players.kast`.
* `matchzy_stats_duels` — one row per individual kill ("duel"): attacker/victim/assister, weapon and engine flags (headshot, wallbang, noscope, through smoke, attacker blind), suicide/teamkill markers and the in-round time of the kill. Unlike `matchzy_stats_players_rounds`, **this table IS written by the plugin** when `matchzy_stats_direct_save_enabled` is `1` (idempotent per-round DELETE+INSERT, also replayed by `matchzy_retry_stats`); the same duels always travel in the `duels` field of the `round_end` webhook, so with direct save off an external consumer can populate it instead. Auto-increment PK because a kill has no natural key; `*_steamid64 = 0` means no real attacker (suicide/world/C4) or a bot.

```sql
CREATE TABLE IF NOT EXISTS matchzy_stats_matches (
    matchid INT PRIMARY KEY AUTO_INCREMENT,
    start_time DATETIME NOT NULL,
    end_time DATETIME DEFAULT NULL,
    winner VARCHAR(255) NOT NULL DEFAULT '',
    series_type VARCHAR(255) NOT NULL DEFAULT '',
    team1_name VARCHAR(255) NOT NULL DEFAULT '',
    team1_score INT NOT NULL DEFAULT 0,
    team2_name VARCHAR(255) NOT NULL DEFAULT '',
    team2_score INT NOT NULL DEFAULT 0,
    server_ip VARCHAR(255) NOT NULL DEFAULT '0'
);

CREATE TABLE IF NOT EXISTS matchzy_stats_maps (
    matchid INT NOT NULL,
    mapnumber TINYINT(3) UNSIGNED NOT NULL,
    start_time DATETIME NOT NULL,
    end_time DATETIME DEFAULT NULL,
    winner VARCHAR(255) NOT NULL DEFAULT '',
    mapname VARCHAR(255) NOT NULL DEFAULT '',
    team1_score INT NOT NULL DEFAULT 0,
    team2_score INT NOT NULL DEFAULT 0,
    PRIMARY KEY (matchid, mapnumber),
    INDEX mapnumber_index (mapnumber),
    CONSTRAINT fk_maps_matchid FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid)
);

CREATE TABLE IF NOT EXISTS matchzy_stats_players (
    matchid INT NOT NULL,
    mapnumber TINYINT(3) UNSIGNED NOT NULL,
    steamid64 BIGINT NOT NULL,
    team VARCHAR(255) NOT NULL DEFAULT '',
    name VARCHAR(255) NOT NULL,
    kills INT NOT NULL,
    deaths INT NOT NULL,
    damage INT NOT NULL,
    assists INT NOT NULL,
    enemy5ks INT NOT NULL,
    enemy4ks INT NOT NULL,
    enemy3ks INT NOT NULL,
    enemy2ks INT NOT NULL,
    utility_count INT NOT NULL,
    utility_damage INT NOT NULL,
    utility_successes INT NOT NULL,
    utility_enemies INT NOT NULL,
    flash_count INT NOT NULL,
    flash_successes INT NOT NULL,
    health_points_removed_total INT NOT NULL,
    health_points_dealt_total INT NOT NULL,
    shots_fired_total INT NOT NULL,
    shots_on_target_total INT NOT NULL,
    v1_count INT NOT NULL,
    v1_wins INT NOT NULL,
    v2_count INT NOT NULL,
    v2_wins INT NOT NULL,
    entry_count INT NOT NULL,
    entry_wins INT NOT NULL,
    equipment_value INT NOT NULL,
    money_saved INT NOT NULL,
    kill_reward INT NOT NULL,
    live_time INT NOT NULL,
    head_shot_kills INT NOT NULL,
    cash_earned INT NOT NULL,
    enemies_flashed INT NOT NULL,
    flash_assists INT NOT NULL DEFAULT 0,
    friendlies_flashed INT NOT NULL DEFAULT 0,
    knife_kills INT NOT NULL DEFAULT 0,
    bomb_plants INT NOT NULL DEFAULT 0,
    bomb_defuses INT NOT NULL DEFAULT 0,
    kast INT NOT NULL DEFAULT 0,
    PRIMARY KEY (matchid, mapnumber, steamid64),
    CONSTRAINT fk_players_map_ref FOREIGN KEY (matchid, mapnumber)
        REFERENCES matchzy_stats_maps (matchid, mapnumber)
);

CREATE TABLE IF NOT EXISTS matchzy_stats_players_rounds (
    matchid INT NOT NULL,
    mapnumber TINYINT(3) UNSIGNED NOT NULL,
    round_number SMALLINT UNSIGNED NOT NULL,
    steamid64 BIGINT NOT NULL,
    team VARCHAR(255) NOT NULL DEFAULT '',
    name VARCHAR(255) NOT NULL DEFAULT '',
    kills INT NOT NULL DEFAULT 0,
    deaths INT NOT NULL DEFAULT 0,
    damage INT NOT NULL DEFAULT 0,
    assists INT NOT NULL DEFAULT 0,
    enemy5ks INT NOT NULL DEFAULT 0,
    enemy4ks INT NOT NULL DEFAULT 0,
    enemy3ks INT NOT NULL DEFAULT 0,
    enemy2ks INT NOT NULL DEFAULT 0,
    utility_count INT NOT NULL DEFAULT 0,
    utility_damage INT NOT NULL DEFAULT 0,
    utility_successes INT NOT NULL DEFAULT 0,
    utility_enemies INT NOT NULL DEFAULT 0,
    flash_count INT NOT NULL DEFAULT 0,
    flash_successes INT NOT NULL DEFAULT 0,
    health_points_removed_total INT NOT NULL DEFAULT 0,
    health_points_dealt_total INT NOT NULL DEFAULT 0,
    shots_fired_total INT NOT NULL DEFAULT 0,
    shots_on_target_total INT NOT NULL DEFAULT 0,
    v1_count INT NOT NULL DEFAULT 0,
    v1_wins INT NOT NULL DEFAULT 0,
    v2_count INT NOT NULL DEFAULT 0,
    v2_wins INT NOT NULL DEFAULT 0,
    entry_count INT NOT NULL DEFAULT 0,
    entry_wins INT NOT NULL DEFAULT 0,
    equipment_value INT NOT NULL DEFAULT 0,
    money_saved INT NOT NULL DEFAULT 0,
    kill_reward INT NOT NULL DEFAULT 0,
    live_time INT NOT NULL DEFAULT 0,
    head_shot_kills INT NOT NULL DEFAULT 0,
    cash_earned INT NOT NULL DEFAULT 0,
    enemies_flashed INT NOT NULL DEFAULT 0,
    flash_assists INT NOT NULL DEFAULT 0,
    friendlies_flashed INT NOT NULL DEFAULT 0,
    knife_kills INT NOT NULL DEFAULT 0,
    bomb_plants INT NOT NULL DEFAULT 0,
    bomb_defuses INT NOT NULL DEFAULT 0,
    kast INT NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (matchid, mapnumber, round_number, steamid64),
    INDEX round_match_map_index (matchid, mapnumber),
    CONSTRAINT fk_players_rounds_map_ref FOREIGN KEY (matchid, mapnumber)
        REFERENCES matchzy_stats_maps (matchid, mapnumber)
);

CREATE TABLE IF NOT EXISTS matchzy_stats_duels (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    matchid INT NOT NULL,
    mapnumber TINYINT(3) UNSIGNED NOT NULL,
    round_number SMALLINT UNSIGNED NOT NULL,
    round_time INT NOT NULL DEFAULT 0,
    attacker_steamid64 BIGINT NOT NULL DEFAULT 0,
    attacker_name VARCHAR(255) NOT NULL DEFAULT '',
    attacker_side VARCHAR(16) NOT NULL DEFAULT '',
    victim_steamid64 BIGINT NOT NULL DEFAULT 0,
    victim_name VARCHAR(255) NOT NULL DEFAULT '',
    victim_side VARCHAR(16) NOT NULL DEFAULT '',
    assister_steamid64 BIGINT NOT NULL DEFAULT 0,
    assister_name VARCHAR(255) NOT NULL DEFAULT '',
    weapon VARCHAR(64) NOT NULL DEFAULT '',
    headshot TINYINT(1) NOT NULL DEFAULT 0,
    penetrated TINYINT(1) NOT NULL DEFAULT 0,
    noscope TINYINT(1) NOT NULL DEFAULT 0,
    thrusmoke TINYINT(1) NOT NULL DEFAULT 0,
    attacker_blind TINYINT(1) NOT NULL DEFAULT 0,
    is_suicide TINYINT(1) NOT NULL DEFAULT 0,
    is_teamkill TINYINT(1) NOT NULL DEFAULT 0,
    kill_timestamp VARCHAR(40) NOT NULL DEFAULT '',
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX duels_match_map_round_index (matchid, mapnumber, round_number),
    CONSTRAINT fk_duels_map_ref FOREIGN KEY (matchid, mapnumber)
        REFERENCES matchzy_stats_maps (matchid, mapnumber)
);
```

> The SQLite code path creates the exact same columns/keys using SQLite-native syntax (`INTEGER PRIMARY KEY AUTOINCREMENT`, no `TINYINT(3) UNSIGNED`/`INDEX name (...)` clauses) — see `CreateRequiredTablesSQLite` in `DatabaseStats.cs` if you need that dialect verbatim.

## Documentation

## [shobhit-pathak.github.io/MatchZy/](https://shobhit-pathak.github.io/MatchZy/)

## Donation

Buy Me A Coffee:

[!["Buy Me A Coffee"](https://cdn.buymeacoffee.com/buttons/default-blue.png)](https://www.buymeacoffee.com/shobhitwd)

Steam Tradelink: 

https://steamcommunity.com/tradeoffer/new/?partner=194101533&token=1TI76S3p

## Want CS2 Server with MatchZy?

Buy it from DatHost (MatchZy can be installed directly on DatHost servers by using their 1-click installer from mods and plugins section!):
https://dathost.net/r/matchzy 

## License
MIT

## Credits and thanks!
* [Get5](https://github.com/splewis/get5) - A lot of functionalities and workings have been referred from Get5 and they did an amazing job for managing matches in CS:GO. Huge thanks to them!
* [G5V](https://github.com/PhlexPlexico/G5V) and [G5API](https://github.com/PhlexPlexico/G5API) - Amazing work with the web panel for managing the servers!
* [eBot](https://github.com/deStrO/eBot-CSGO) - Amazing job in CS:GO and then provided this great panel again in CS2 which is helping a lot of organizers now. Some logics have been referred from eBot as well!
* [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/) - Amazing job with development of CSSharp which gave us a platform to build our own plugins and also sparked my interest in plugin development!
* [AlliedModders and community](https://alliedmods.net/) - They are the reason this whole plugin was possible! They are very helpful and inspire a lot!
* [LOTGaming](https://lotgaming.xyz/) - Helped me a lot with initial testing and provided servers on different systems and locations!
* [CHR15cs](https://github.com/CHR15cs) - Helped me a lot with the practice mode!
* [K4ryuu](https://github.com/K4ryuu) - Awesome job on damage report!
* [DEAFPS](https://github.com/DEAFPS) - Great contribution for Practice mode!
