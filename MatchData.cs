using System.Text.Json.Serialization;

namespace MatchZy;
public class Winner
{
    [JsonPropertyName("side")]
    public string Side { get; set; }

    [JsonPropertyName("team")]
    public string Team { get; set; }

    public Winner(string side, string team)
    {
        Side = side;
        Team = team;
    }
}

public class StatsPlayer
{
    [JsonPropertyName("steamid")]
    public required string SteamId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("stats")]
    public required PlayerStats Stats { get; init; }
}

// Referred from PugSharp
public class PlayerStats
{
    [JsonPropertyName("kills")]
    public int Kills { get; set; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("flash_assists")]
    public int FlashAssists { get; set; }

    [JsonPropertyName("team_kills")]
    public int TeamKills { get; set; }

    [JsonPropertyName("suicides")]
    public int Suicides { get; set; }

    [JsonPropertyName("damage")]
    public int Damage { get; set; }

    [JsonPropertyName("utility_damage")]
    public int UtilityDamage { get; set; }

    [JsonPropertyName("enemies_flashed")]
    public int EnemiesFlashed { get; set; }

    [JsonPropertyName("friendlies_flashed")]
    public int FriendliesFlashed { get; set; }

    [JsonPropertyName("knife_kills")]
    public int KnifeKills { get; set; }

    [JsonPropertyName("headshot_kills")]
    public int HeadshotKills { get; set; }

    [JsonPropertyName("rounds_played")]
    public int RoundsPlayed { get; set; }

    [JsonPropertyName("bomb_defuses")]
    public int BombDefuses { get; set; }

    [JsonPropertyName("bomb_plants")]
    public int BombPlants { get; set; }

    [JsonPropertyName("1k")]
    public int Kills1 { get; set; }

    [JsonPropertyName("2k")]
    public int Kills2 { get; set; }

    [JsonPropertyName("3k")]
    public int Kills3 { get; set; }

    [JsonPropertyName("4k")]
    public int Kills4 { get; set; }

    [JsonPropertyName("5k")]
    public int Kills5 { get; set; }

    [JsonPropertyName("1v1")]
    public int OneV1s { get; set; }

    [JsonPropertyName("1v2")]
    public int OneV2s { get; set; }

    [JsonPropertyName("1v3")]
    public int OneV3s { get; set; }

    [JsonPropertyName("1v4")]
    public int OneV4s { get; set; }

    [JsonPropertyName("1v5")]
    public int OneV5s { get; set; }

    [JsonPropertyName("first_kills_t")]
    public int FirstKillsT { get; set; }

    [JsonPropertyName("first_kills_ct")]
    public int FirstKillsCT { get; set; }

    [JsonPropertyName("first_deaths_t")]
    public int FirstDeathsT { get; set; }

    [JsonPropertyName("first_deaths_ct")]
    public int FirstDeathsCT { get; set; }

    [JsonPropertyName("trade_kills")]
    public int TradeKills { get; set; }

    [JsonPropertyName("kast")]
    public int Kast { get; set; }

    // true si el jugador tuvo K/A/S/T en ESTA ronda puntual (a diferencia de
    // "kast" arriba, que es un porcentaje acumulado del mapa). Necesario porque
    // "kast" no es diffeable entre rondas consecutivas (es un promedio redondeado
    // sobre un denominador que crece cada ronda, no un conteo crudo).
    [JsonPropertyName("kast_this_round")]
    public bool KastThisRound { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("mvp")]
    public int Mvps { get; set; }

    // Columnas que ya existen en matchzy_stats_players y que GetPlayerStatsDict
    // ya lee de player.ActionTrackingServices.MatchStats, pero que hasta ahora
    // nunca se copiaban a este DTO (por lo tanto nunca viajaban en el webhook).
    [JsonPropertyName("utility_count")]
    public int UtilityCount { get; set; }

    [JsonPropertyName("utility_successes")]
    public int UtilitySuccesses { get; set; }

    [JsonPropertyName("utility_enemies")]
    public int UtilityEnemies { get; set; }

    [JsonPropertyName("flash_count")]
    public int FlashCount { get; set; }

    [JsonPropertyName("flash_successes")]
    public int FlashSuccesses { get; set; }

    [JsonPropertyName("health_points_removed_total")]
    public int HealthPointsRemovedTotal { get; set; }

    [JsonPropertyName("health_points_dealt_total")]
    public int HealthPointsDealtTotal { get; set; }

    [JsonPropertyName("shots_fired_total")]
    public int ShotsFiredTotal { get; set; }

    [JsonPropertyName("shots_on_target_total")]
    public int ShotsOnTargetTotal { get; set; }

    [JsonPropertyName("v1_count")]
    public int V1Count { get; set; }

    [JsonPropertyName("v2_count")]
    public int V2Count { get; set; }

    [JsonPropertyName("entry_count")]
    public int EntryCount { get; set; }

    [JsonPropertyName("entry_wins")]
    public int EntryWins { get; set; }

    [JsonPropertyName("equipment_value")]
    public int EquipmentValue { get; set; }

    [JsonPropertyName("money_saved")]
    public int MoneySaved { get; set; }

    [JsonPropertyName("kill_reward")]
    public int KillReward { get; set; }

    [JsonPropertyName("live_time")]
    public int LiveTime { get; set; }

    [JsonPropertyName("cash_earned")]
    public int CashEarned { get; set; }
}

public class MatchZyTeamWrapper
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    public MatchZyTeamWrapper(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

public class MatchZyStatsTeam : MatchZyTeamWrapper
{
    [JsonPropertyName("series_score")]
    public int SeriesScore { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("score_ct")]
    public int ScoreCT { get; set; }

    [JsonPropertyName("score_t")]
    public int ScoreT { get; set; }

    [JsonPropertyName("players")]
    public List<StatsPlayer> Players { get; set; }

    public MatchZyStatsTeam(string id, string name, int seriesScore, int score, int scoreCt, int scoreT, List<StatsPlayer> players) : base(id, name)
    {
        SeriesScore = seriesScore;
        Score = score;
        ScoreCT = scoreCt;
        ScoreT = scoreT;
        Players = players;
    }
}

// Un "duelo" = un kill individual capturado en EventPlayerDeath. Viaja en la
// lista `duels` del evento round_end y se persiste en matchzy_stats_duels.
// La ronda a la que pertenece NO viaja por duelo: la define el round_number
// del propio evento round_end; en la BD la columna round_number la agrega el
// escritor y es la FK compuesta hacia la cabecera (matchzy_stats_rounds).
// Props mutables con defaults (sin `required`): los backups de ronda viejos
// (RoundStatsBackupFile.WebhookPayload) no traen todos los campos y deben
// seguir deserializando.
public class MatchZyDuel
{
    // Segundos desde el freeze end de la ronda (0 si la kill ocurre en freezetime).
    [JsonPropertyName("round_time")]
    public int RoundTime { get; set; }

    // SteamIDs como string (igual que StatsPlayer.SteamId) para no perder
    // precision en consumidores JS. "0" = suicidio/mundo/bot.
    [JsonPropertyName("attacker_steamid")]
    public string AttackerSteamId { get; set; } = "0";

    [JsonPropertyName("attacker_name")]
    public string AttackerName { get; set; } = "";

    // "CT" | "TERRORIST" | ""
    [JsonPropertyName("attacker_side")]
    public string AttackerSide { get; set; } = "";

    // Posicion del atacante al momento de la kill, en unidades del mundo.
    // Solo el plano horizontal: en Source 2 la vertical es Z (no Y), y para
    // radar/heatmap 2D alcanza con X/Y. 0/0 = sin atacante o pawn no legible.
    [JsonPropertyName("attacker_x")]
    public float AttackerX { get; set; }

    [JsonPropertyName("attacker_y")]
    public float AttackerY { get; set; }

    // Nombre del area del mapa donde estaba el atacante (m_szLastPlaceName
    // del pawn: "BombsiteA", "Middle"...). "" si no hay atacante o no se pudo leer.
    [JsonPropertyName("attacker_place")]
    public string AttackerPlace { get; set; } = "";

    [JsonPropertyName("victim_steamid")]
    public string VictimSteamId { get; set; } = "0";

    [JsonPropertyName("victim_name")]
    public string VictimName { get; set; } = "";

    [JsonPropertyName("victim_side")]
    public string VictimSide { get; set; } = "";

    [JsonPropertyName("victim_x")]
    public float VictimX { get; set; }

    [JsonPropertyName("victim_y")]
    public float VictimY { get; set; }

    [JsonPropertyName("victim_place")]
    public string VictimPlace { get; set; } = "";

    [JsonPropertyName("assister_steamid")]
    public string AssisterSteamId { get; set; } = "0";

    [JsonPropertyName("assister_name")]
    public string AssisterName { get; set; } = "";

    // Nombre crudo del arma del evento: "ak47", "knife_butterfly", "planted_c4", "world"...
    [JsonPropertyName("weapon")]
    public string Weapon { get; set; } = "";

    [JsonPropertyName("headshot")]
    public bool Headshot { get; set; }

    [JsonPropertyName("penetrated")]
    public bool Penetrated { get; set; }

    [JsonPropertyName("noscope")]
    public bool Noscope { get; set; }

    [JsonPropertyName("thrusmoke")]
    public bool Thrusmoke { get; set; }

    [JsonPropertyName("attacker_blind")]
    public bool AttackerBlind { get; set; }

    // Attacker null o == victim: caidas, mundo, `kill` en consola, C4.
    [JsonPropertyName("is_suicide")]
    public bool IsSuicide { get; set; }

    [JsonPropertyName("is_teamkill")]
    public bool IsTeamKill { get; set; }

    // ISO 8601 UTC del momento de la kill.
    [JsonPropertyName("timestamp_utc")]
    public string TimestampUtc { get; set; } = "";
}

// Cabecera de una ronda para matchzy_stats_rounds (Database.UpsertRoundAsync).
// No viaja como objeto propio en el webhook: sus campos van en el nivel
// superior del evento round_end (reason, reason_name, winner, winner_side,
// round_time, scores, bomb_planted, bomb_site); este DTO existe para que el
// guardado directo y la restauracion desde backup compartan la misma firma.
public class MatchZyRoundHeader
{
    public int RoundNumber { get; set; }
    public int Reason { get; set; }
    public string ReasonName { get; set; } = "";
    public string WinnerSide { get; set; } = "";
    // "team1" | "team2"
    public string WinnerTeam { get; set; } = "";
    public string WinnerTeamName { get; set; } = "";
    // Segundos desde el freeze end hasta el fin de la ronda.
    public int RoundDuration { get; set; }
    // Score del mapa DESPUES de contabilizar esta ronda.
    public int Team1Score { get; set; }
    public int Team2Score { get; set; }
    public bool BombPlanted { get; set; }
    public string BombSite { get; set; } = "";
}
