using System;
using System.IO;
using System.Data;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Dapper;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CsvHelper;
using CsvHelper.Configuration;
using MySqlConnector;



namespace MatchZy
{
    public class Database
    {
        private IDbConnection connection;

        // Serializa todas las operaciones async de DB que comparten la misma
        // instancia de IDbConnection. Ni SqliteConnection ni MySqlConnection
        // son thread-safe para acceso concurrente desde múltiples Task.Run.
        // Sin este lock, cuando HandlePostRoundEndEvent y HandleMatchEnd disparan
        // sus Task.Run al mismo tiempo (última ronda de un match), UpdatePlayerStatsAsync
        // compite con SetMapEndData por la conexión, lanza una excepción que se traga
        // silenciosamente y los stats de jugadores nunca se guardan.
        private readonly SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);

        DatabaseConfig? config;
        public DatabaseType databaseType { get; set; }

        public void InitializeDatabase(string directory)
        {
            ConnectDatabase(directory);
            try
            {
                connection.Open();
                string dbType = (connection is SqliteConnection) ? "SQLite" : "MySQL";
                Log($"[InitializeDatabase] {dbType} Database connection successful");

                // Create the `matchzy_stats_matches`, `matchzy_stats_players` and `matchzy_stats_maps` tables if they doesn't exist
                if (connection is SqliteConnection)
                {
                    CreateRequiredTablesSQLite();
                }
                else
                {
                    CreateRequiredTablesSQL();
                }

                Log("[InitializeDatabase] Table matchzy_stats_matches created (or already exists)");
                Log("[InitializeDatabase] Table matchzy_stats_players created (or already exists)");
                Log("[InitializeDatabase] Table matchzy_stats_maps created (or already exists)");
            }
            catch (Exception ex)
            {
                Log($"[InitializeDatabase - FATAL] Database connection or table creation error: {ex.Message}");
            }
        }

        public void ConnectDatabase(string directory)
        {
            try
            {
                SetDatabaseConfig(directory);

                if (databaseType == DatabaseType.SQLite)
                {
                    connection =
                        new SqliteConnection(
                            $"Data Source={Path.Join(directory, "matchzy.db")}");
                }
                else if (config != null && databaseType == DatabaseType.MySQL)
                {
                    string connectionString = $"Server={config.MySqlHost};Port={config.MySqlPort};Database={config.MySqlDatabase};User Id={config.MySqlUsername};Password={config.MySqlPassword};";
                    connection = new MySqlConnection(connectionString);
                }
                else
                {
                    Log($"[InitializeDatabase] Invalid database specified, using SQLite.");
                    connection = new SqliteConnection($"Data Source={Path.Join(directory, "matchzy.db")}");
                    databaseType = DatabaseType.SQLite;
                }
            }
            catch (Exception ex)
            {
                Log($"[InitializeDatabase - FATAL] Database connection error: {ex.Message}");
            }

        }

        // Ninguno de los metodos publicos de abajo verificaba el estado de
        // `connection` antes de usarla. Si la conexion se cae a mitad de un
        // match (restart de MySQL, blip de red), todas las escrituras
        // subsiguientes del resto del match fallaban en silencio (solo
        // logueadas) sin ningun intento de reconexion, hasta que el proceso
        // del plugin se reiniciara. EnsureConnectionOpen() se llama al
        // principio de cada metodo publico que toca `connection` para
        // reabrirla si quedo cerrada/rota.
        private void EnsureConnectionOpen()
        {
            if (connection.State == ConnectionState.Broken)
            {
                connection.Close();
            }
            if (connection.State != ConnectionState.Open)
            {
                Log($"[EnsureConnectionOpen] Conexion en estado {connection.State}, reabriendo...");
                connection.Open();
            }
        }

        public void CreateRequiredTablesSQLite()
        {
            try
            {
                connection.Execute($@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_matches (
                    matchid INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME DEFAULT NULL,
                    winner TEXT NOT NULL DEFAULT '',
                    series_type TEXT NOT NULL DEFAULT '',
                    team1_name TEXT NOT NULL DEFAULT '',
                    team1_score INTEGER NOT NULL DEFAULT 0,
                    team2_name TEXT NOT NULL DEFAULT '',
                    team2_score INTEGER NOT NULL DEFAULT 0,
                    server_ip TEXT NOT NULL DEFAULT '0'
                )");
                Log("[CreateRequiredTablesSQLite] matchzy_stats_matches OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQLite - FATAL] matchzy_stats_matches: {ex.Message}");
            }

            try
            {
                connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_maps (
                    matchid INTEGER NOT NULL,
                    mapnumber INTEGER NOT NULL,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME DEFAULT NULL,
                    winner TEXT NOT NULL DEFAULT '',
                    mapname TEXT NOT NULL DEFAULT '',
                    team1_score INTEGER NOT NULL DEFAULT 0,
                    team2_score INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (matchid, mapnumber),
                    FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid)
                )");
                Log("[CreateRequiredTablesSQLite] matchzy_stats_maps OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQLite - FATAL] matchzy_stats_maps: {ex.Message}");
            }

            try
            {
                connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_players (
                    matchid INTEGER NOT NULL,
                    mapnumber INTEGER NOT NULL,
                    steamid64 INTEGER NOT NULL,
                    team TEXT NOT NULL DEFAULT '',
                    name TEXT NOT NULL,
                    kills INTEGER NOT NULL,
                    deaths INTEGER NOT NULL,
                    damage INTEGER NOT NULL,
                    assists INTEGER NOT NULL,
                    enemy5ks INTEGER NOT NULL,
                    enemy4ks INTEGER NOT NULL,
                    enemy3ks INTEGER NOT NULL,
                    enemy2ks INTEGER NOT NULL,
                    utility_count INTEGER NOT NULL,
                    utility_damage INTEGER NOT NULL,
                    utility_successes INTEGER NOT NULL,
                    utility_enemies INTEGER NOT NULL,
                    flash_count INTEGER NOT NULL,
                    flash_successes INTEGER NOT NULL,
                    health_points_removed_total INTEGER NOT NULL,
                    health_points_dealt_total INTEGER NOT NULL,
                    shots_fired_total INTEGER NOT NULL,
                    shots_on_target_total INTEGER NOT NULL,
                    v1_count INTEGER NOT NULL,
                    v1_wins INTEGER NOT NULL,
                    v2_count INTEGER NOT NULL,
                    v2_wins INTEGER NOT NULL,
                    entry_count INTEGER NOT NULL,
                    entry_wins INTEGER NOT NULL,
                    equipment_value INTEGER NOT NULL,
                    money_saved INTEGER NOT NULL,
                    kill_reward INTEGER NOT NULL,
                    live_time INTEGER NOT NULL,
                    head_shot_kills INTEGER NOT NULL,
                    cash_earned INTEGER NOT NULL,
                    enemies_flashed INTEGER NOT NULL,
                    flash_assists INTEGER NOT NULL DEFAULT 0,
                    friendlies_flashed INTEGER NOT NULL DEFAULT 0,
                    knife_kills INTEGER NOT NULL DEFAULT 0,
                    bomb_plants INTEGER NOT NULL DEFAULT 0,
                    bomb_defuses INTEGER NOT NULL DEFAULT 0,
                    kast INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (matchid, mapnumber, steamid64),
                    FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid),
                    FOREIGN KEY (matchid, mapnumber) REFERENCES matchzy_stats_maps (matchid, mapnumber)
                )");
                Log("[CreateRequiredTablesSQLite] matchzy_stats_players OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQLite - FATAL] matchzy_stats_players: {ex.Message}");
            }

            // matchzy_stats_players_rounds: historial de stats DELTA por ronda,
            // usado por el backend externo (matchController.ts, handleRoundEnd)
            // para reflejar el estado del match en vivo en el frontend. El plugin
            // solo provisiona la tabla acá (idempotente, IF NOT EXISTS) - NUNCA
            // escribe filas en ella, eso es responsabilidad exclusiva del backend
            // via el webhook round_end. `kast` acá es el booleano crudo
            // kast_this_round (0/1), no el porcentaje acumulado de
            // matchzy_stats_players.kast.
            try
            {
                connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_players_rounds (
                    matchid INTEGER NOT NULL,
                    mapnumber INTEGER NOT NULL,
                    round_number INTEGER NOT NULL,
                    steamid64 INTEGER NOT NULL,
                    team TEXT NOT NULL DEFAULT '',
                    name TEXT NOT NULL DEFAULT '',
                    kills INTEGER NOT NULL DEFAULT 0,
                    deaths INTEGER NOT NULL DEFAULT 0,
                    damage INTEGER NOT NULL DEFAULT 0,
                    assists INTEGER NOT NULL DEFAULT 0,
                    enemy5ks INTEGER NOT NULL DEFAULT 0,
                    enemy4ks INTEGER NOT NULL DEFAULT 0,
                    enemy3ks INTEGER NOT NULL DEFAULT 0,
                    enemy2ks INTEGER NOT NULL DEFAULT 0,
                    utility_count INTEGER NOT NULL DEFAULT 0,
                    utility_damage INTEGER NOT NULL DEFAULT 0,
                    utility_successes INTEGER NOT NULL DEFAULT 0,
                    utility_enemies INTEGER NOT NULL DEFAULT 0,
                    flash_count INTEGER NOT NULL DEFAULT 0,
                    flash_successes INTEGER NOT NULL DEFAULT 0,
                    health_points_removed_total INTEGER NOT NULL DEFAULT 0,
                    health_points_dealt_total INTEGER NOT NULL DEFAULT 0,
                    shots_fired_total INTEGER NOT NULL DEFAULT 0,
                    shots_on_target_total INTEGER NOT NULL DEFAULT 0,
                    v1_count INTEGER NOT NULL DEFAULT 0,
                    v1_wins INTEGER NOT NULL DEFAULT 0,
                    v2_count INTEGER NOT NULL DEFAULT 0,
                    v2_wins INTEGER NOT NULL DEFAULT 0,
                    entry_count INTEGER NOT NULL DEFAULT 0,
                    entry_wins INTEGER NOT NULL DEFAULT 0,
                    equipment_value INTEGER NOT NULL DEFAULT 0,
                    money_saved INTEGER NOT NULL DEFAULT 0,
                    kill_reward INTEGER NOT NULL DEFAULT 0,
                    live_time INTEGER NOT NULL DEFAULT 0,
                    head_shot_kills INTEGER NOT NULL DEFAULT 0,
                    cash_earned INTEGER NOT NULL DEFAULT 0,
                    enemies_flashed INTEGER NOT NULL DEFAULT 0,
                    flash_assists INTEGER NOT NULL DEFAULT 0,
                    friendlies_flashed INTEGER NOT NULL DEFAULT 0,
                    knife_kills INTEGER NOT NULL DEFAULT 0,
                    bomb_plants INTEGER NOT NULL DEFAULT 0,
                    bomb_defuses INTEGER NOT NULL DEFAULT 0,
                    kast INTEGER NOT NULL DEFAULT 0,
                    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (matchid, mapnumber, round_number, steamid64),
                    FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid),
                    FOREIGN KEY (matchid, mapnumber) REFERENCES matchzy_stats_maps (matchid, mapnumber)
                )");
                Log("[CreateRequiredTablesSQLite] matchzy_stats_players_rounds OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQLite - FATAL] matchzy_stats_players_rounds: {ex.Message}");
            }
        }

        public void CreateRequiredTablesSQL()
        {
            try
            {
                connection.Execute($@"
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
                )");
                Log("[CreateRequiredTablesSQL] matchzy_stats_matches OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQL - FATAL] matchzy_stats_matches: {ex.Message}");
            }

            try
            {
                connection.Execute($@"
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
                )");
                Log("[CreateRequiredTablesSQL] matchzy_stats_maps OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQL - FATAL] matchzy_stats_maps: {ex.Message}");
            }

            try
            {
                connection.Execute($@"
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
                )");
                Log("[CreateRequiredTablesSQL] matchzy_stats_players OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQL - FATAL] matchzy_stats_players: {ex.Message}");
            }

            // matchzy_stats_players_rounds: historial de stats DELTA por ronda,
            // usado por el backend externo (matchController.ts, handleRoundEnd)
            // para reflejar el estado del match en vivo en el frontend. El plugin
            // solo provisiona la tabla acá (idempotente, IF NOT EXISTS) - NUNCA
            // escribe filas en ella, eso es responsabilidad exclusiva del backend
            // via el webhook round_end. `kast` acá es el booleano crudo
            // kast_this_round (0/1), no el porcentaje acumulado de
            // matchzy_stats_players.kast.
            try
            {
                connection.Execute($@"
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
                )");
                Log("[CreateRequiredTablesSQL] matchzy_stats_players_rounds OK");
            }
            catch (Exception ex)
            {
                Log($"[CreateRequiredTablesSQL - FATAL] matchzy_stats_players_rounds: {ex.Message}");
            }
        }

        public long InitMatch(string team1name, string team2name, string serverIp, bool isMatchSetup, long liveMatchId, int mapNumber, string seriesType, MatchConfig matchConfig)
        {
            try
            {
                EnsureConnectionOpen();
                string mapName = isMatchSetup ? matchConfig.Maplist[mapNumber] : Server.MapName;
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                if (mapNumber == 0)
                {
                    if (isMatchSetup && liveMatchId != -1)
                    {
                        connection.Execute(@"
                            INSERT INTO matchzy_stats_matches (matchid, start_time, team1_name, team2_name, series_type, server_ip)
                            VALUES (@liveMatchId, " + dateTimeExpression + ", @team1name, @team2name, @seriesType, @serverIp)",
                            new { liveMatchId, team1name, team2name, seriesType, serverIp });
                    }
                    else
                    {
                        connection.Execute(@"
                            INSERT INTO matchzy_stats_matches (start_time, team1_name, team2_name, series_type, server_ip)
                            VALUES (" + dateTimeExpression + ", @team1name, @team2name, @seriesType, @serverIp)",
                            new { team1name, team2name, seriesType, serverIp });
                    }
                }

                if (isMatchSetup && liveMatchId != -1)
                {
                    connection.Execute(@"
                        INSERT INTO matchzy_stats_maps (matchid, start_time, mapnumber, mapname)
                        VALUES (@liveMatchId, " + dateTimeExpression + ", @mapNumber, @mapName)",
                        new { liveMatchId, mapNumber, mapName });
                    return liveMatchId;
                }

                // Retrieve the last inserted match_id
                long matchId = -1;
                if (connection is SqliteConnection)
                {
                    matchId = connection.ExecuteScalar<long>("SELECT last_insert_rowid()");
                }
                else if (connection is MySqlConnection)
                {
                    matchId = connection.ExecuteScalar<long>("SELECT LAST_INSERT_ID()");
                }

                connection.Execute(@"
                    INSERT INTO matchzy_stats_maps (matchid, start_time, mapnumber, mapname)
                    VALUES (@matchId, " + dateTimeExpression + ", @mapNumber, @mapName)",
                    new { matchId, mapNumber, mapName });

                Log($"[InsertMatchData] Data inserted into matchzy_stats_matches with match_id: {matchId}");
                return matchId;
            }
            catch (Exception ex)
            {
                Log($"[InsertMatchData - FATAL] Error inserting data: {ex.Message}");
                return liveMatchId;
            }
        }

        public void UpdateTeamData(int matchId, string team1name, string team2name)
        {
            try
            {
                EnsureConnectionOpen();
                connection.Execute(@"
                    UPDATE matchzy_stats_matches
                    SET team1_name = @team1name, team2_name = @team2name
                    WHERE matchid = @matchId",
                    new { matchId, team1name, team2name });

                Log($"[UpdateTeamData] Data updated for matchId: {matchId} team1name: {team1name} team2name: {team2name}");
            }
            catch (Exception ex)
            {
                Log($"[UpdateTeamData - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
        }

        public async Task SetMapEndData(long matchId, int mapNumber, string winnerName, int t1score, int t2score, int team1SeriesScore, int team2SeriesScore)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                string sqlQuery = $@"
                    UPDATE matchzy_stats_maps
                    SET winner = @winnerName, end_time = {dateTimeExpression}, team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId AND mapNumber = @mapNumber";

                await connection.ExecuteAsync(sqlQuery, new { matchId, winnerName, t1score, t2score, mapNumber });

                sqlQuery = $@"
                    UPDATE matchzy_stats_matches
                    SET team1_score = @team1SeriesScore, team2_score = @team2SeriesScore
                    WHERE matchid = @matchId";

                await connection.ExecuteAsync(sqlQuery, new { matchId, team1SeriesScore, team2SeriesScore });

                Log($"[SetMapEndData] Data updated for matchId: {matchId} mapNumber: {mapNumber} winnerName: {winnerName}");
            }
            catch (Exception ex)
            {
                Log($"[SetMapEndData - FATAL] Error updating data of matchId: {matchId} mapNumber: {mapNumber} [ERROR]: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task SetMatchEndData(long matchId, string winnerName, int t1score, int t2score)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                string sqlQuery = $@"
                    UPDATE matchzy_stats_matches
                    SET winner = @winnerName, end_time = {dateTimeExpression}, team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId";

                await connection.ExecuteAsync(sqlQuery, new { matchId, winnerName, t1score, t2score });

                Log($"[SetMatchEndData] Data updated for matchId: {matchId} winnerName: {winnerName}");
            }
            catch (Exception ex)
            {
                Log($"[SetMatchEndData - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task UpdateMapStatsAsync(long matchId, int mapNumber, int t1score, int t2score)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string sqlQuery = $@"
                    UPDATE matchzy_stats_maps
                    SET team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId AND mapnumber = @mapNumber";

                await connection.ExecuteAsync(sqlQuery, new { matchId, mapNumber, t1score, t2score });
            }
            catch (Exception ex)
            {
                Log($"[UpdateMapStats - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task UpdatePlayerStatsAsync(long matchId, int mapNumber, Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                foreach (ulong steamid64 in playerStatsDictionary.Keys)
                {
                    Log($"[UpdatePlayerStats] Going to update data for Match: {matchId}, MapNumber: {mapNumber}, Player: {steamid64}");

                    var playerStats = playerStatsDictionary[steamid64];

                    string sqlQuery = $@"
                    INSERT INTO matchzy_stats_players (
                        matchid, mapnumber, steamid64, team, name, kills, deaths, damage, assists,
                        enemy5ks, enemy4ks, enemy3ks, enemy2ks, utility_count, utility_damage,
                        utility_successes, utility_enemies, flash_count, flash_successes,
                        health_points_removed_total, health_points_dealt_total, shots_fired_total,
                        shots_on_target_total, v1_count, v1_wins, v2_count, v2_wins, entry_count, entry_wins,
                        equipment_value, money_saved, kill_reward, live_time, head_shot_kills,
                        cash_earned, enemies_flashed, flash_assists, friendlies_flashed, knife_kills,
                        bomb_plants, bomb_defuses, kast)
                    VALUES (
                        @matchId, @mapNumber, @steamid64, @team, @name, @kills, @deaths, @damage, @assists,
                        @enemy5ks, @enemy4ks, @enemy3ks, @enemy2ks, @utility_count, @utility_damage,
                        @utility_successes, @utility_enemies, @flash_count, @flash_successes,
                        @health_points_removed_total, @health_points_dealt_total, @shots_fired_total,
                        @shots_on_target_total, @v1_count, @v1_wins, @v2_count, @v2_wins, @entry_count,
                        @entry_wins, @equipment_value, @money_saved, @kill_reward, @live_time,
                        @head_shot_kills, @cash_earned, @enemies_flashed, @flash_assists,
                        @friendlies_flashed, @knife_kills, @bomb_plants, @bomb_defuses, @kast)
                    ON DUPLICATE KEY UPDATE
                        team = @team, name = @name, kills = @kills, deaths = @deaths, damage = @damage,
                        assists = @assists, enemy5ks = @enemy5ks, enemy4ks = @enemy4ks, enemy3ks = @enemy3ks,
                        enemy2ks = @enemy2ks, utility_count = @utility_count, utility_damage = @utility_damage,
                        utility_successes = @utility_successes, utility_enemies = @utility_enemies,
                        flash_count = @flash_count, flash_successes = @flash_successes,
                        health_points_removed_total = @health_points_removed_total,
                        health_points_dealt_total = @health_points_dealt_total,
                        shots_fired_total = @shots_fired_total, shots_on_target_total = @shots_on_target_total,
                        v1_count = @v1_count, v1_wins = @v1_wins, v2_count = @v2_count, v2_wins = @v2_wins,
                        entry_count = @entry_count, entry_wins = @entry_wins,
                        equipment_value = @equipment_value, money_saved = @money_saved,
                        kill_reward = @kill_reward, live_time = @live_time, head_shot_kills = @head_shot_kills,
                        cash_earned = @cash_earned, enemies_flashed = @enemies_flashed,
                        flash_assists = @flash_assists, friendlies_flashed = @friendlies_flashed,
                        knife_kills = @knife_kills, bomb_plants = @bomb_plants,
                        bomb_defuses = @bomb_defuses, kast = @kast";

                    if (connection is SqliteConnection)
                    {
                        sqlQuery = @"
                        INSERT OR REPLACE INTO matchzy_stats_players (
                            matchid, mapnumber, steamid64, team, name, kills, deaths, damage, assists,
                            enemy5ks, enemy4ks, enemy3ks, enemy2ks, utility_count, utility_damage,
                            utility_successes, utility_enemies, flash_count, flash_successes,
                            health_points_removed_total, health_points_dealt_total, shots_fired_total,
                            shots_on_target_total, v1_count, v1_wins, v2_count, v2_wins, entry_count, entry_wins,
                            equipment_value, money_saved, kill_reward, live_time, head_shot_kills,
                            cash_earned, enemies_flashed, flash_assists, friendlies_flashed,
                            knife_kills, bomb_plants, bomb_defuses, kast)
                        VALUES (
                            @matchId, @mapNumber, @steamid64, @team, @name, @kills, @deaths, @damage, @assists,
                            @enemy5ks, @enemy4ks, @enemy3ks, @enemy2ks, @utility_count, @utility_damage,
                            @utility_successes, @utility_enemies, @flash_count, @flash_successes,
                            @health_points_removed_total, @health_points_dealt_total, @shots_fired_total,
                            @shots_on_target_total, @v1_count, @v1_wins, @v2_count, @v2_wins, @entry_count,
                            @entry_wins, @equipment_value, @money_saved, @kill_reward, @live_time,
                            @head_shot_kills, @cash_earned, @enemies_flashed, @flash_assists,
                            @friendlies_flashed, @knife_kills, @bomb_plants, @bomb_defuses, @kast)";
                    }

                    await connection.ExecuteAsync(sqlQuery,
                        new
                        {
                            matchId,
                            mapNumber,
                            steamid64,
                            team = playerStats["TeamName"],
                            name = playerStats["PlayerName"],
                            kills = playerStats["Kills"],
                            deaths = playerStats["Deaths"],
                            damage = playerStats["Damage"],
                            assists = playerStats["Assists"],
                            enemy5ks = playerStats["Enemy5Ks"],
                            enemy4ks = playerStats["Enemy4Ks"],
                            enemy3ks = playerStats["Enemy3Ks"],
                            enemy2ks = playerStats["Enemy2Ks"],
                            utility_count = playerStats["UtilityCount"],
                            utility_damage = playerStats["UtilityDamage"],
                            utility_successes = playerStats["UtilitySuccess"],
                            utility_enemies = playerStats["UtilityEnemies"],
                            flash_count = playerStats["FlashCount"],
                            flash_successes = playerStats["FlashSuccess"],
                            health_points_removed_total = playerStats["HealthPointsRemovedTotal"],
                            health_points_dealt_total = playerStats["HealthPointsDealtTotal"],
                            shots_fired_total = playerStats["ShotsFiredTotal"],
                            shots_on_target_total = playerStats["ShotsOnTargetTotal"],
                            v1_count = playerStats["1v1Count"],
                            v1_wins = playerStats["1v1Wins"],
                            v2_count = playerStats["1v2Count"],
                            v2_wins = playerStats["1v2Wins"],
                            entry_count = playerStats["EntryCount"],
                            entry_wins = playerStats["EntryWins"],
                            equipment_value = playerStats["EquipmentValue"],
                            money_saved = playerStats["MoneySaved"],
                            kill_reward = playerStats["KillReward"],
                            live_time = playerStats["LiveTime"],
                            head_shot_kills = playerStats["HeadShotKills"],
                            cash_earned = playerStats["CashEarned"],
                            enemies_flashed = playerStats["EnemiesFlashed"],
                            flash_assists = playerStats["FlashAssists"],
                            friendlies_flashed = playerStats["FriendliesFlashed"],
                            knife_kills = playerStats["KnifeKills"],
                            bomb_plants = playerStats["BombPlants"],
                            bomb_defuses = playerStats["BombDefuses"],
                            kast = playerStats["Kast"]
                        });

                    Log($"[UpdatePlayerStats] Data inserted/updated for player {steamid64} in match {matchId}");
                }
            }
            catch (Exception ex)
            {
                Log($"[UpdatePlayerStats - FATAL] Error inserting/updating data: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task WritePlayerStatsToCsv(string filePath, long matchId, int mapNumber)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string csvFilePath = $"{filePath}/match_data_map{mapNumber}_{matchId}.csv";
                string? directoryPath = Path.GetDirectoryName(csvFilePath);
                if (directoryPath != null)
                {
                    if (!Directory.Exists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }
                }

                using (var writer = new StreamWriter(csvFilePath))
                using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
                {
                    IEnumerable<dynamic> playerStatsData = await connection.QueryAsync(
                        "SELECT * FROM matchzy_stats_players WHERE matchid = @MatchId AND mapnumber = @MapNumber ORDER BY team, kills DESC", new { MatchId = matchId, MapNumber = mapNumber });

                    // Use the first data row to get the column names
                    dynamic? firstDataRow = playerStatsData.FirstOrDefault();
                    if (firstDataRow != null)
                    {
                        foreach (var propertyName in ((IDictionary<string, object>)firstDataRow).Keys)
                        {
                            csv.WriteField(propertyName);
                        }
                        csv.NextRecord(); // End of the column names row

                        // Write data to the CSV file
                        foreach (var playerStats in playerStatsData)
                        {
                            foreach (var propertyValue in ((IDictionary<string, object>)playerStats).Values)
                            {
                                csv.WriteField(propertyValue);
                            }
                            csv.NextRecord();
                        }
                    }
                }
                Log($"[WritePlayerStatsToCsv] Match stats for ID: {matchId} written successfully at: {csvFilePath}");
            }
            catch (Exception ex)
            {
                Log($"[WritePlayerStatsToCsv - FATAL] Error writing data: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        // Usado por el auto-reintento de fin de mapa (Utility.cs HandleMatchEnd) para
        // verificar si las filas de la ultima ronda realmente quedaron guardadas.
        public async Task<int> CountPlayerStatsRows(long matchId, int mapNumber)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                return await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM matchzy_stats_players WHERE matchid = @matchId AND mapnumber = @mapNumber",
                    new { matchId, mapNumber });
            }
            catch (Exception ex)
            {
                Log($"[CountPlayerStatsRows - FATAL] Error contando filas de matchId: {matchId} mapNumber: {mapNumber} [ERROR]: {ex.Message}");
                return -1;
            }
            finally
            {
                _dbLock.Release();
            }
        }

        // Insercion defensiva usada por el reintento de stats (StatsBackup.cs) para
        // reconstruir un match desde su backup en disco. No pisa la fila si ya
        // existe (creada normalmente por InitMatch al arrancar el match/mapa).
        public async Task EnsureMatchRowExists(long matchId, string team1Name, string team2Name, string seriesType)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";
                string sqlQuery = (connection is SqliteConnection)
                    ? $@"INSERT OR IGNORE INTO matchzy_stats_matches (matchid, start_time, team1_name, team2_name, series_type)
                         VALUES (@matchId, {dateTimeExpression}, @team1Name, @team2Name, @seriesType)"
                    : $@"INSERT IGNORE INTO matchzy_stats_matches (matchid, start_time, team1_name, team2_name, series_type)
                         VALUES (@matchId, {dateTimeExpression}, @team1Name, @team2Name, @seriesType)";

                await connection.ExecuteAsync(sqlQuery, new { matchId, team1Name, team2Name, seriesType });
            }
            catch (Exception ex)
            {
                Log($"[EnsureMatchRowExists - FATAL] Error asegurando fila de matchId: {matchId} [ERROR]: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        public async Task EnsureMapRowExists(long matchId, int mapNumber, string mapName)
        {
            await _dbLock.WaitAsync();
            try
            {
                EnsureConnectionOpen();
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";
                string sqlQuery = (connection is SqliteConnection)
                    ? $@"INSERT OR IGNORE INTO matchzy_stats_maps (matchid, mapnumber, start_time, mapname)
                         VALUES (@matchId, @mapNumber, {dateTimeExpression}, @mapName)"
                    : $@"INSERT IGNORE INTO matchzy_stats_maps (matchid, mapnumber, start_time, mapname)
                         VALUES (@matchId, @mapNumber, {dateTimeExpression}, @mapName)";

                await connection.ExecuteAsync(sqlQuery, new { matchId, mapNumber, mapName });
            }
            catch (Exception ex)
            {
                Log($"[EnsureMapRowExists - FATAL] Error asegurando fila de matchId: {matchId} mapNumber: {mapNumber} [ERROR]: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }

        private void CreateDefaultConfigFile(string configFile)
        {
            // Create a default configuration
            DatabaseConfig defaultConfig = new DatabaseConfig
            {
                DatabaseType = "SQLite",
                MySqlHost = "your_mysql_host",
                MySqlDatabase = "your_mysql_database",
                MySqlUsername = "your_mysql_username",
                MySqlPassword = "your_mysql_password",
                MySqlPort = 3306
            };

            // Serialize and save the default configuration to the file
            string defaultConfigJson = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFile, defaultConfigJson);

            Log($"[InitializeDatabase] Default configuration file created at: {configFile}");
        }

        private void SetDatabaseConfig(string directory)
        {
            string fileName = "database.json";
            string configFile = Path.Combine(Server.GameDirectory + "/csgo/cfg/MatchZy", fileName);
            if (!File.Exists(configFile))
            {
                // Create a default configuration if the file doesn't exist
                Log($"[InitializeDatabase] database.json doesn't exist, creating default!");
                CreateDefaultConfigFile(configFile);
            }

            try
            {
                string jsonContent = File.ReadAllText(configFile);
                config = JsonSerializer.Deserialize<DatabaseConfig>(jsonContent);
                // Set the database type
                if (config != null && config.DatabaseType?.Trim().ToLower() == "mysql")
                {
                    databaseType = DatabaseType.MySQL;
                }
                else
                {
                    databaseType = DatabaseType.SQLite;
                }

            }
            catch (JsonException ex)
            {
                Log($"[TryDeserializeConfig - ERROR] Error deserializing database.json: {ex.Message}. Using SQLite DB");
                databaseType = DatabaseType.SQLite;
            }
        }

        private void Log(string message)
        {
            Console.WriteLine("[MatchZy] " + message);
        }

        public enum DatabaseType
        {
            SQLite,
            MySQL
        }
    }

    public class DatabaseConfig
    {
        public string? DatabaseType { get; set; }
        public string? MySqlHost { get; set; }
        public string? MySqlDatabase { get; set; }
        public string? MySqlUsername { get; set; }
        public string? MySqlPassword { get; set; }
        public int? MySqlPort { get; set; }
    }

}
