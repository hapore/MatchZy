using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;


namespace MatchZy
{

    public class MatchConfig
    {
        [JsonPropertyName("maplist")]
        public List<string> Maplist { get; set; } = new List<string>();

        [JsonPropertyName("maps_pool")]
        public List<string> MapsPool { get; set; } = new List<string>();

        [JsonPropertyName("maps_left_in_veto_pool")]
        public List<string> MapsLeftInVetoPool { get; set; } = new List<string>();

        [JsonPropertyName("map_ban_order")]
        public List<string> MapBanOrder { get; set; } = new List<string>();

        [JsonPropertyName("skip_veto")]
        public bool SkipVeto { get; set; } = true;

        [JsonPropertyName("match_id")]
        public long MatchId { get; set; }

        [JsonPropertyName("num_maps")]
        public int NumMaps { get; set; } = 1;

        [JsonPropertyName("players_per_team")]
        public int PlayersPerTeam { get; set; } = 5;

        [JsonPropertyName("min_players_to_ready")]
        public int MinPlayersToReady { get; set; } = 12;

        [JsonPropertyName("min_spectators_to_ready")]
        public int MinSpectatorsToReady { get; set; } = 0;

        [JsonPropertyName("current_map_number")]
        public int CurrentMapNumber { get; set; } = 0;

        [JsonPropertyName("map_sides")]
        public List<string> MapSides { get; set; } = new List<string>();

        [JsonPropertyName("series_can_clinch")]
        public bool SeriesCanClinch { get; set; } = true;

        [JsonPropertyName("scrim")]
        public bool Scrim { get; set; } = false;

        [JsonPropertyName("wingman")]
        public bool Wingman { get; set; } = false;

        [JsonPropertyName("match_side_type")]
        public string MatchSideType { get; set; } = "standard";

        /// <summary>
        /// true  = friendly fire competitivo, tal cual lo aplica el engine con las
        ///         reducciones de ff_damage_reduction_*.
        /// false = el plugin anula el daño entre compañeros salvo HE y molotov/incendiaria.
        ///
        /// La convar mp_friendlyfire se mantiene SIEMPRE en 1: apagarla en el engine
        /// tambien mataria el daño de granadas, que es justamente lo que queremos conservar.
        /// El filtro lo hace OnPlayerTakeDamagePreHandler en FriendlyFire.cs.
        /// </summary>
        [JsonPropertyName("friendly_fire")]
        public bool FriendlyFire { get; set; } = true;

        [JsonPropertyName("changed_cvars")]
        public Dictionary<string, string> ChangedCvars { get; set; } = new();

        [JsonPropertyName("original_cvars")]
        public Dictionary<string, string> OriginalCvars { get; set; } = new();

        [JsonPropertyName("spectators")]
        public JToken Spectators { get; set; } = new JObject();

        [JsonPropertyName("remote_log_url")]
        public string RemoteLogURL { get; set; } = "";

        [JsonPropertyName("remote_log_header_key")]
        public string RemoteLogHeaderKey { get; set; } = "";

        [JsonPropertyName("remote_log_header_value")]
        public string RemoteLogHeaderValue { get; set; } = "";

        /// <summary>
        /// Seconds to wait for all players in the config to connect before cancelling the match.
        /// Default: 300 (5 minutes). Set to 0 to disable the timeout.
        /// </summary>
        [JsonPropertyName("player_wait_timeout")]
        public int PlayerWaitTimeout { get; set; } = 300;

        /// <summary>
        /// Seconds of countdown shown in chat before the match starts once all players are connected.
        /// Default: 10.
        /// </summary>
        [JsonPropertyName("match_start_countdown")]
        public int MatchStartCountdown { get; set; } = 10;
    }
}
