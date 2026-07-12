using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchZy
{
    // Fila de stats de un jugador, tipada 1:1 con las claves que arma
    // GetPlayerStatsDict() (Utility.cs) y que consume Database.UpdatePlayerStatsAsync
    // (DatabaseStats.cs). Deliberadamente NO se reusa el Dictionary<string, object>
    // crudo para el JSON de backup: al deserializar ese diccionario de vuelta,
    // System.Text.Json devuelve los valores como JsonElement en vez de int/string,
    // y Dapper no puede bindear un JsonElement a una columna INT. Con un DTO
    // tipado, la deserializacion entrega tipos reales listos para reinsertar.
    public class PlayerStatsRow
    {
        public string TeamName { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Damage { get; set; }
        public int Enemy2Ks { get; set; }
        public int Enemy3Ks { get; set; }
        public int Enemy4Ks { get; set; }
        public int Enemy5Ks { get; set; }
        public int EntryCount { get; set; }
        public int EntryWins { get; set; }
        public int OneV1Count { get; set; }
        public int OneV1Wins { get; set; }
        public int OneV2Count { get; set; }
        public int OneV2Wins { get; set; }
        public int UtilityCount { get; set; }
        public int UtilitySuccess { get; set; }
        public int UtilityDamage { get; set; }
        public int UtilityEnemies { get; set; }
        public int FlashCount { get; set; }
        public int FlashSuccess { get; set; }
        public int HealthPointsRemovedTotal { get; set; }
        public int HealthPointsDealtTotal { get; set; }
        public int ShotsFiredTotal { get; set; }
        public int ShotsOnTargetTotal { get; set; }
        public int EquipmentValue { get; set; }
        public int MoneySaved { get; set; }
        public int KillReward { get; set; }
        public int LiveTime { get; set; }
        public int HeadShotKills { get; set; }
        public int CashEarned { get; set; }
        public int EnemiesFlashed { get; set; }
        public int FlashAssists { get; set; }
        public int FriendliesFlashed { get; set; }
        public int KnifeKills { get; set; }
        public int BombPlants { get; set; }
        public int BombDefuses { get; set; }
        public int Kast { get; set; }
    }

    // Datos de "cabecera" (matchzy_stats_matches/matchzy_stats_maps) incluidos en
    // cada backup de ronda para que el archivo sea autosuficiente: alcanza por si
    // solo para reconstruir el match sin depender de que otra tabla ya tenga datos.
    public class RoundStatsBackupHeader
    {
        public long MatchId { get; set; }
        public int MapNumber { get; set; }
        public int RoundNumber { get; set; }
        public string MapName { get; set; } = "";
        public string SeriesType { get; set; } = "";
        public string Team1Name { get; set; } = "";
        public string Team2Name { get; set; } = "";
        public int Team1Score { get; set; }
        public int Team2Score { get; set; }

        // Series score ANTES de contabilizar el resultado de este mapa (el
        // incremento por mapa ganado ocurre recien en HandleMatchEnd/GetMatchWinnerName,
        // que todavia no corrio en el momento en que se escribe este backup).
        public int Team1SeriesScoreBeforeThisMap { get; set; }
        public int Team2SeriesScoreBeforeThisMap { get; set; }
        public string TimestampUtc { get; set; } = "";
    }

    public class RoundStatsBackupFile
    {
        public RoundStatsBackupHeader Header { get; set; } = new();

        // Unica fuente que consume la logica de reintento (RetryStatsFromBackup).
        public Dictionary<ulong, PlayerStatsRow> Players { get; set; } = new();

        // El mismo payload que ya se serializa para el webhook "round_end", incluido
        // tal cual para auditoria/debug humano. No lo consume el reintento porque
        // PlayerStats (el DTO del webhook) no trae todas las columnas de la tabla.
        public MatchZyRoundEndedEvent? WebhookPayload { get; set; }
    }

    public partial class MatchZy
    {
        // Task del Task.Run que HandlePostRoundEndEvent dispara para guardar en BD
        // las stats de la ronda que acaba de terminar, y cuantos jugadores se
        // esperaba que quedaran escritos. HandleMatchEnd los usa para esperar esa
        // tarea antes de leer/regenerar el CSV, y para saber si hace falta
        // auto-reintentar. Ver Utility.cs: HandlePostRoundEndEvent / HandleMatchEnd.
        private Task? _lastRoundStatsTask = null;
        private int _lastRoundExpectedPlayerCount = 0;

        private string GetStatsBackupRoot()
        {
            return Path.Combine(Server.GameDirectory, "csgo", "MatchZy_StatsBackup");
        }

        // Se llama de forma SINCRONA desde HandlePostRoundEndEvent, antes del
        // Task.Run que envia el webhook y escribe en la BD, para que el archivo
        // sobreviva aunque el resto del pipeline falle (DB caida, webhook caido,
        // excepcion, etc). Carpeta separada de MatchZyDataBackup (backups de
        // restauracion de ronda) para no ser detectado por matchzy_loadbackup /
        // get5_listbackups, que solo entienden ese otro formato.
        //
        // Como las stats nativas (ActionTrackingServices.MatchStats) son
        // acumulativas por mapa, el backup de la ULTIMA ronda jugada alcanza por si
        // solo para reconstruir el resultado completo del mapa - a diferencia del
        // backup de restauracion (CreateMatchZyRoundDataBackup en
        // BackupManagement.cs), que se dispara en RoundStart y por eso nunca cubre
        // la ronda final, este se dispara en RoundEnd y si la cubre.
        private void CreateRoundStatsBackup(MatchZyRoundEndedEvent roundEndEvent, Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary)
        {
            try
            {
                RoundStatsBackupHeader header = new()
                {
                    MatchId = roundEndEvent.MatchId,
                    MapNumber = roundEndEvent.MapNumber,
                    RoundNumber = roundEndEvent.RoundNumber,
                    MapName = Server.MapName,
                    SeriesType = "BO" + matchConfig.NumMaps,
                    Team1Name = matchzyTeam1.teamName,
                    Team2Name = matchzyTeam2.teamName,
                    Team1Score = roundEndEvent.StatsTeam1.Score,
                    Team2Score = roundEndEvent.StatsTeam2.Score,
                    Team1SeriesScoreBeforeThisMap = matchzyTeam1.seriesScore,
                    Team2SeriesScoreBeforeThisMap = matchzyTeam2.seriesScore,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                };

                Dictionary<ulong, PlayerStatsRow> players = new();
                foreach (KeyValuePair<ulong, Dictionary<string, object>> entry in playerStatsDictionary)
                {
                    players[entry.Key] = BuildPlayerStatsRow(entry.Value);
                }

                RoundStatsBackupFile backup = new()
                {
                    Header = header,
                    Players = players,
                    WebhookPayload = roundEndEvent,
                };

                string dir = Path.Combine(GetStatsBackupRoot(), roundEndEvent.MatchId.ToString());
                Directory.CreateDirectory(dir);

                string fileName = $"matchzy_stats_{roundEndEvent.MatchId}_{roundEndEvent.MapNumber}_round{roundEndEvent.RoundNumber:D2}.json";
                string finalPath = Path.Combine(dir, fileName);
                string tmpPath = finalPath + ".tmp";

                JsonSerializerOptions options = new() { WriteIndented = true };
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(backup, options));
                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch (Exception e)
            {
                Log($"[CreateRoundStatsBackup FATAL] Error creando backup de stats: {e.Message}");
            }
        }

        private static PlayerStatsRow BuildPlayerStatsRow(Dictionary<string, object> raw)
        {
            return new PlayerStatsRow
            {
                TeamName = Convert.ToString(raw["TeamName"]) ?? "",
                PlayerName = Convert.ToString(raw["PlayerName"]) ?? "",
                Kills = Convert.ToInt32(raw["Kills"]),
                Deaths = Convert.ToInt32(raw["Deaths"]),
                Assists = Convert.ToInt32(raw["Assists"]),
                Damage = Convert.ToInt32(raw["Damage"]),
                Enemy2Ks = Convert.ToInt32(raw["Enemy2Ks"]),
                Enemy3Ks = Convert.ToInt32(raw["Enemy3Ks"]),
                Enemy4Ks = Convert.ToInt32(raw["Enemy4Ks"]),
                Enemy5Ks = Convert.ToInt32(raw["Enemy5Ks"]),
                EntryCount = Convert.ToInt32(raw["EntryCount"]),
                EntryWins = Convert.ToInt32(raw["EntryWins"]),
                OneV1Count = Convert.ToInt32(raw["1v1Count"]),
                OneV1Wins = Convert.ToInt32(raw["1v1Wins"]),
                OneV2Count = Convert.ToInt32(raw["1v2Count"]),
                OneV2Wins = Convert.ToInt32(raw["1v2Wins"]),
                UtilityCount = Convert.ToInt32(raw["UtilityCount"]),
                UtilitySuccess = Convert.ToInt32(raw["UtilitySuccess"]),
                UtilityDamage = Convert.ToInt32(raw["UtilityDamage"]),
                UtilityEnemies = Convert.ToInt32(raw["UtilityEnemies"]),
                FlashCount = Convert.ToInt32(raw["FlashCount"]),
                FlashSuccess = Convert.ToInt32(raw["FlashSuccess"]),
                HealthPointsRemovedTotal = Convert.ToInt32(raw["HealthPointsRemovedTotal"]),
                HealthPointsDealtTotal = Convert.ToInt32(raw["HealthPointsDealtTotal"]),
                ShotsFiredTotal = Convert.ToInt32(raw["ShotsFiredTotal"]),
                ShotsOnTargetTotal = Convert.ToInt32(raw["ShotsOnTargetTotal"]),
                EquipmentValue = Convert.ToInt32(raw["EquipmentValue"]),
                MoneySaved = Convert.ToInt32(raw["MoneySaved"]),
                KillReward = Convert.ToInt32(raw["KillReward"]),
                LiveTime = Convert.ToInt32(raw["LiveTime"]),
                HeadShotKills = Convert.ToInt32(raw["HeadShotKills"]),
                CashEarned = Convert.ToInt32(raw["CashEarned"]),
                EnemiesFlashed = Convert.ToInt32(raw["EnemiesFlashed"]),
                FlashAssists = Convert.ToInt32(raw["FlashAssists"]),
                FriendliesFlashed = Convert.ToInt32(raw["FriendliesFlashed"]),
                KnifeKills = Convert.ToInt32(raw["KnifeKills"]),
                BombPlants = Convert.ToInt32(raw["BombPlants"]),
                BombDefuses = Convert.ToInt32(raw["BombDefuses"]),
                Kast = Convert.ToInt32(raw["Kast"]),
            };
        }

        // Inverso de BuildPlayerStatsRow: reconstruye el Dictionary<string, object>
        // con las claves exactas que espera Database.UpdatePlayerStatsAsync
        // (ver DatabaseStats.cs), con tipos reales (no JsonElement).
        private static Dictionary<string, object> ConvertPlayerStatsRowToRawDict(PlayerStatsRow row)
        {
            return new Dictionary<string, object>
            {
                { "TeamName", row.TeamName },
                { "PlayerName", row.PlayerName },
                { "Kills", row.Kills },
                { "Deaths", row.Deaths },
                { "Damage", row.Damage },
                { "Assists", row.Assists },
                { "Enemy5Ks", row.Enemy5Ks },
                { "Enemy4Ks", row.Enemy4Ks },
                { "Enemy3Ks", row.Enemy3Ks },
                { "Enemy2Ks", row.Enemy2Ks },
                { "UtilityCount", row.UtilityCount },
                { "UtilityDamage", row.UtilityDamage },
                { "UtilitySuccess", row.UtilitySuccess },
                { "UtilityEnemies", row.UtilityEnemies },
                { "FlashCount", row.FlashCount },
                { "FlashSuccess", row.FlashSuccess },
                { "HealthPointsRemovedTotal", row.HealthPointsRemovedTotal },
                { "HealthPointsDealtTotal", row.HealthPointsDealtTotal },
                { "ShotsFiredTotal", row.ShotsFiredTotal },
                { "ShotsOnTargetTotal", row.ShotsOnTargetTotal },
                { "1v1Count", row.OneV1Count },
                { "1v1Wins", row.OneV1Wins },
                { "1v2Count", row.OneV2Count },
                { "1v2Wins", row.OneV2Wins },
                { "EntryCount", row.EntryCount },
                { "EntryWins", row.EntryWins },
                { "EquipmentValue", row.EquipmentValue },
                { "MoneySaved", row.MoneySaved },
                { "KillReward", row.KillReward },
                { "LiveTime", row.LiveTime },
                { "HeadShotKills", row.HeadShotKills },
                { "CashEarned", row.CashEarned },
                { "EnemiesFlashed", row.EnemiesFlashed },
                { "FlashAssists", row.FlashAssists },
                { "FriendliesFlashed", row.FriendliesFlashed },
                { "KnifeKills", row.KnifeKills },
                { "BombPlants", row.BombPlants },
                { "BombDefuses", row.BombDefuses },
                { "Kast", row.Kast },
            };
        }

        private static string? FindLatestRoundBackupFile(string statsBackupRoot, long matchId, int mapNumber)
        {
            string dir = Path.Combine(statsBackupRoot, matchId.ToString());
            if (!Directory.Exists(dir)) return null;

            string prefix = $"matchzy_stats_{matchId}_{mapNumber}_round";
            string? bestPath = null;
            int bestRound = -1;

            foreach (string path in Directory.GetFiles(dir, $"{prefix}*.json"))
            {
                Match match = Regex.Match(Path.GetFileName(path), @"_round(\d+)\.json$");
                if (!match.Success) continue;

                int round = int.Parse(match.Groups[1].Value);
                if (round > bestRound)
                {
                    bestRound = round;
                    bestPath = path;
                }
            }

            return bestPath;
        }

        private static List<int> DiscoverMapNumbersForMatch(string statsBackupRoot, long matchId)
        {
            string dir = Path.Combine(statsBackupRoot, matchId.ToString());
            if (!Directory.Exists(dir)) return new List<int>();

            HashSet<int> mapNumbers = new();
            Regex fileRegex = new(@$"^matchzy_stats_{matchId}_(\d+)_round\d+\.json$");

            foreach (string path in Directory.GetFiles(dir, $"matchzy_stats_{matchId}_*_round*.json"))
            {
                Match match = fileRegex.Match(Path.GetFileName(path));
                if (match.Success)
                {
                    mapNumbers.Add(int.Parse(match.Groups[1].Value));
                }
            }

            List<int> result = mapNumbers.ToList();
            result.Sort();
            return result;
        }

        // Reconstruye matchzy_stats_matches / matchzy_stats_maps / matchzy_stats_players
        // (y el CSV) para un matchid+mapnumber a partir del backup de la ultima ronda
        // en disco. No depende de liveMatchId ni de ningun estado en memoria del match
        // actual - recibe todo por parametro, por lo que puede correr despues de que
        // el servidor ya paso a otra partida. Usado tanto por el comando RCON manual
        // (matchzy_retry_stats) como por el auto-reintento de HandleMatchEnd.
        private async Task<(bool success, int playersWritten, string message)> RetryStatsFromBackup(long matchId, int mapNumber, string statsBackupRoot, string matchStatsCsvPath)
        {
            string? backupFile = FindLatestRoundBackupFile(statsBackupRoot, matchId, mapNumber);
            if (backupFile == null) return (false, 0, "no se encontro ningun backup de stats");

            RoundStatsBackupFile? backup;
            try
            {
                backup = JsonSerializer.Deserialize<RoundStatsBackupFile>(File.ReadAllText(backupFile));
            }
            catch (Exception e)
            {
                Log($"[RetryStatsFromBackup FATAL] Error leyendo/parseando {backupFile}: {e.Message}");
                return (false, 0, "backup corrupto");
            }

            if (backup == null || backup.Players.Count == 0)
            {
                return (false, 0, "backup vacio");
            }

            RoundStatsBackupHeader h = backup.Header;

            await database.EnsureMatchRowExists(matchId, h.Team1Name, h.Team2Name, h.SeriesType);
            await database.EnsureMapRowExists(matchId, mapNumber, h.MapName);

            Dictionary<ulong, Dictionary<string, object>> rawDict = new();
            foreach (KeyValuePair<ulong, PlayerStatsRow> entry in backup.Players)
            {
                rawDict[entry.Key] = ConvertPlayerStatsRowToRawDict(entry.Value);
            }

            await database.UpdatePlayerStatsAsync(matchId, mapNumber, rawDict);

            bool draw = h.Team1Score == h.Team2Score;
            bool team1Won = h.Team1Score > h.Team2Score;
            string winnerName = draw ? "Draw" : (team1Won ? h.Team1Name : h.Team2Name);
            int team1SeriesScore = h.Team1SeriesScoreBeforeThisMap + (!draw && team1Won ? 1 : 0);
            int team2SeriesScore = h.Team2SeriesScoreBeforeThisMap + (!draw && !team1Won ? 1 : 0);

            await database.SetMapEndData(matchId, mapNumber, winnerName, h.Team1Score, h.Team2Score, team1SeriesScore, team2SeriesScore);
            await database.WritePlayerStatsToCsv(matchStatsCsvPath, matchId, mapNumber);

            Log($"[RetryStatsFromBackup] matchId {matchId} mapNumber {mapNumber}: {backup.Players.Count} jugadores reescritos desde {Path.GetFileName(backupFile)}");
            return (true, backup.Players.Count, "ok");
        }

        [ConsoleCommand("matchzy_retry_stats", "Reintenta guardar en la base de datos las stats de jugadores de un match usando el backup JSON de la ultima ronda")]
        [ConsoleCommand("css_retrystats", "Reintenta guardar en la base de datos las stats de jugadores de un match usando el backup JSON de la ultima ronda")]
        public void OnRetryStatsCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (!IsPlayerAdmin(player, "css_retrystats", "@css/rcon"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (command.ArgCount < 2 || !long.TryParse(command.ArgByIndex(1), out long matchId))
            {
                ReplyToUserCommand(player, "Usage: matchzy_retry_stats <matchid> [mapnumber]");
                return;
            }

            int? mapNumberFilter = null;
            if (command.ArgCount >= 3 && int.TryParse(command.ArgByIndex(2), out int parsedMapNumber))
            {
                mapNumberFilter = parsedMapNumber;
            }

            string statsBackupRoot = GetStatsBackupRoot();
            string matchStatsCsvPath = Server.GameDirectory + "/csgo/MatchZy_Stats/" + matchId;

            Task.Run(async () =>
            {
                List<int> mapNumbers = mapNumberFilter.HasValue
                    ? new List<int> { mapNumberFilter.Value }
                    : DiscoverMapNumbersForMatch(statsBackupRoot, matchId);

                string summary;
                if (mapNumbers.Count == 0)
                {
                    summary = $"[MatchZy] No se encontraron backups de stats para matchid {matchId}.";
                }
                else
                {
                    List<string> results = new();
                    foreach (int mapNumber in mapNumbers)
                    {
                        (bool success, int playersWritten, string message) = await RetryStatsFromBackup(matchId, mapNumber, statsBackupRoot, matchStatsCsvPath);
                        results.Add(success ? $"map {mapNumber}: OK ({playersWritten} jugadores)" : $"map {mapNumber}: FALLO ({message})");
                    }
                    summary = $"[MatchZy] Reintento de stats para matchid {matchId}: " + string.Join(" | ", results);
                }

                Server.NextFrame(() =>
                {
                    if (player == null || player.IsValid)
                    {
                        ReplyToUserCommand(player, summary);
                    }
                });
            });
        }
    }
}
