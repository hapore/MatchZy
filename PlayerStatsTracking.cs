using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;

namespace MatchZy
{
    public partial class MatchZy
    {
        // ─── Per-player cumulative stats (reset on match reset) ───────────────────
        public Dictionary<ulong, int> playerKnifeKills = new();
        public Dictionary<ulong, int> playerBombPlants = new();
        public Dictionary<ulong, int> playerBombDefuses = new();
        public Dictionary<ulong, int> playerFlashAssists = new();
        public Dictionary<ulong, int> playerTeammatesFlashed = new();

        // KAST: accumulated rounds where the player had K, A, S or T
        public Dictionary<ulong, int> kastRoundsContributed = new();

        // ─── Per-round state (reset each round start) ─────────────────────────────
        private Dictionary<ulong, HashSet<string>> kastFlags = new();

        // victim steamid → (death timestamp, killer steamid) — used to detect traded kills
        private Dictionary<ulong, (DateTime time, ulong killerSteamId)> recentDeaths = new();

        // Duelos (kills individuales) de la ronda en curso. Flush en round end
        // (FlushCurrentRoundDuels); el Clear en round start descarta ademas los
        // duelos de rondas abortadas por round restore y las kills que ocurren
        // entre round_end y round_start (decision deliberada: no pertenecen a
        // ninguna ronda contabilizada).
        private List<MatchZyDuel> currentRoundDuels = new();

        // Momento en que la ronda paso a live (freeze end). Base para el
        // round_time de cada duelo y para la duracion de la ronda en la
        // cabecera (matchzy_stats_rounds). Fallback en round start por si
        // freeze end no dispara; freeze end lo sobreescribe con el valor correcto.
        public DateTime currentRoundLiveStartUtc = DateTime.UtcNow;

        // Estado de la bomba en la ronda en curso, para la cabecera de ronda.
        // No se deduce del reason de round end (los CT pueden ganar por
        // eliminacion con la bomba plantada). Reset en round start.
        public bool currentRoundBombPlanted = false;
        public string currentRoundBombSite = "";

        // ─── Helpers ──────────────────────────────────────────────────────────────

        private void MarkKast(ulong steamId, string flag)
        {
            if (!kastFlags.ContainsKey(steamId))
                kastFlags[steamId] = new HashSet<string>();
            kastFlags[steamId].Add(flag);
        }

        private void IncrementStat(Dictionary<ulong, int> dict, ulong steamId)
        {
            dict.TryGetValue(steamId, out int v);
            dict[steamId] = v + 1;
        }

        private static string GetPlayerSide(CCSPlayerController player)
        {
            return player.TeamNum switch
            {
                (int)CsTeam.CounterTerrorist => "CT",
                (int)CsTeam.Terrorist => "TERRORIST",
                _ => "",
            };
        }

        // Posicion (plano horizontal X/Y; en Source 2 la vertical es Z y no se
        // captura) y nombre del area del mapa (m_szLastPlaceName) del pawn.
        private static (float x, float y, string place) GetPawnLocation(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;
            return (origin?.X ?? 0f, origin?.Y ?? 0f, pawn?.LastPlaceName ?? "");
        }

        // Nombre estable del codigo reason de EventRoundEnd via el enum
        // RoundEndReason de CS2 ("TargetBombed", "BombDefused", "CTsWin"...).
        // Un valor fuera del enum devuelve el numero como string.
        public static string GetRoundEndReasonName(int reason)
        {
            return ((RoundEndReason)reason).ToString();
        }

        // Llamado desde EventBombPlanted. El site se deriva del area del
        // planter ("BombsiteA"/"BombsiteB" -> "A"/"B"); si el nombre del area
        // no matchea la convencion, se guarda crudo antes que perderlo.
        public void RecordBombPlant(CCSPlayerController planter)
        {
            currentRoundBombPlanted = true;
            (_, _, string place) = GetPawnLocation(planter);
            currentRoundBombSite = place switch
            {
                "BombsiteA" => "A",
                "BombsiteB" => "B",
                _ => place,
            };
        }

        // Registra un duelo (kill individual) de la ronda en curso. Los bots se
        // capturan con steamid "0". La ronda a la que pertenece la define el
        // round_end que hace el flush (GetRoundNumer, suma de scores, solo es
        // correcto en round end), por eso el duelo no lleva numero de ronda.
        public void RecordDuel(EventPlayerDeath @event, CCSPlayerController victim, CCSPlayerController? attacker, CCSPlayerController? assister, bool isSuicide)
        {
            bool hasAttacker = !isSuicide && IsPlayerValid(attacker);
            (float victimX, float victimY, string victimPlace) = GetPawnLocation(victim);
            var duel = new MatchZyDuel
            {
                RoundTime = Math.Max(0, (int)(DateTime.UtcNow - currentRoundLiveStartUtc).TotalSeconds),
                VictimSteamId = victim.SteamID.ToString(),
                VictimName = victim.PlayerName,
                VictimSide = GetPlayerSide(victim),
                VictimX = victimX,
                VictimY = victimY,
                VictimPlace = victimPlace,
                Weapon = @event.Weapon ?? "",
                Headshot = @event.Headshot,
                Penetrated = @event.Penetrated > 0,
                Noscope = @event.Noscope,
                Thrusmoke = @event.Thrusmoke,
                AttackerBlind = @event.Attackerblind,
                IsSuicide = isSuicide,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
            };
            if (hasAttacker)
            {
                (float attackerX, float attackerY, string attackerPlace) = GetPawnLocation(attacker!);
                duel.AttackerSteamId = attacker!.SteamID.ToString();
                duel.AttackerName = attacker.PlayerName;
                duel.AttackerSide = GetPlayerSide(attacker);
                duel.AttackerX = attackerX;
                duel.AttackerY = attackerY;
                duel.AttackerPlace = attackerPlace;
                duel.IsTeamKill = attacker.TeamNum == victim.TeamNum;
            }
            if (IsPlayerValid(assister))
            {
                duel.AssisterSteamId = assister!.SteamID.ToString();
                duel.AssisterName = assister.PlayerName;
            }
            currentRoundDuels.Add(duel);
        }

        // Snapshot-and-swap: devuelve los duelos de la ronda que termina y deja
        // una lista nueva, para que el Task.Run de round end no comparta la
        // lista viva con el handler de kills.
        public List<MatchZyDuel> FlushCurrentRoundDuels()
        {
            List<MatchZyDuel> duels = currentRoundDuels;
            currentRoundDuels = new List<MatchZyDuel>();
            return duels;
        }

        // ─── Called at round start ────────────────────────────────────────────────
        public void ResetPerRoundKastState()
        {
            kastFlags.Clear();
            recentDeaths.Clear();
            currentRoundDuels.Clear();
            currentRoundBombPlanted = false;
            currentRoundBombSite = "";
        }

        // ─── Called at round end (before GetPlayerStatsDict) ─────────────────────
        public void FinalizeKastForRound()
        {
            // S: mark players still alive at round end
            foreach (var player in playerData.Values)
            {
                if (!player.IsValid || player.IsHLTV || player.IsBot) continue;
                if (player.TeamNum != (int)CsTeam.CounterTerrorist &&
                    player.TeamNum != (int)CsTeam.Terrorist) continue;

                if (player.PlayerPawn?.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                    MarkKast(player.SteamID, "S");
            }

            // Accumulate KAST rounds for any player who had at least one flag
            foreach (var (steamId, flags) in kastFlags)
            {
                if (flags.Count > 0)
                {
                    kastRoundsContributed.TryGetValue(steamId, out int cur);
                    kastRoundsContributed[steamId] = cur + 1;
                }
            }
        }

        // ─── Called when a new map goes live (per-map stats reset) ────────────────
        // Estas estadísticas se persisten por mapa en `matchzy_stats_players`
        // (PK: matchid, mapnumber, steamid64). En un BO3/BO5 cada mapa es una fila
        // independiente, por lo que los contadores deben reiniciarse al inicio de
        // cada mapa; de lo contrario se acumulan entre mapas e inflan los valores
        // (p. ej. KAST > 100% porque kastRounds es acumulado pero roundsPlayed es
        // por mapa).
        public void ResetPerMapStats()
        {
            playerKnifeKills.Clear();
            playerBombPlants.Clear();
            playerBombDefuses.Clear();
            playerFlashAssists.Clear();
            playerTeammatesFlashed.Clear();
            kastRoundsContributed.Clear();
            kastFlags.Clear();
            recentDeaths.Clear();
            currentRoundDuels.Clear();
            currentRoundBombPlanted = false;
            currentRoundBombSite = "";
        }

        // ─── Called on match reset ────────────────────────────────────────────────
        public void ResetMatchStats()
        {
            // Un reset de match limpia el mismo conjunto de contadores por-mapa.
            ResetPerMapStats();
        }
    }
}
