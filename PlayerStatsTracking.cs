using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
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
        // round_time de cada duelo. Fallback en round start por si freeze end
        // no dispara; freeze end lo sobreescribe con el valor correcto.
        public DateTime currentRoundLiveStartUtc = DateTime.UtcNow;

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

        // Registra un duelo (kill individual) de la ronda en curso. Los bots se
        // capturan con steamid "0"; RoundNumber se asigna recien en el flush
        // porque GetRoundNumer (suma de scores) solo es correcto en round end.
        public void RecordDuel(EventPlayerDeath @event, CCSPlayerController victim, CCSPlayerController? attacker, CCSPlayerController? assister, bool isSuicide)
        {
            bool hasAttacker = !isSuicide && IsPlayerValid(attacker);
            var duel = new MatchZyDuel
            {
                RoundTime = Math.Max(0, (int)(DateTime.UtcNow - currentRoundLiveStartUtc).TotalSeconds),
                VictimSteamId = victim.SteamID.ToString(),
                VictimName = victim.PlayerName,
                VictimSide = GetPlayerSide(victim),
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
                duel.AttackerSteamId = attacker!.SteamID.ToString();
                duel.AttackerName = attacker.PlayerName;
                duel.AttackerSide = GetPlayerSide(attacker);
                duel.IsTeamKill = attacker.TeamNum == victim.TeamNum;
            }
            if (IsPlayerValid(assister))
            {
                duel.AssisterSteamId = assister!.SteamID.ToString();
                duel.AssisterName = assister.PlayerName;
            }
            currentRoundDuels.Add(duel);
        }

        // Snapshot-and-swap: devuelve los duelos de la ronda que termina (con su
        // round_number ya asignado) y deja una lista nueva, para que el Task.Run
        // de round end no comparta la lista viva con el handler de kills.
        public List<MatchZyDuel> FlushCurrentRoundDuels(int roundNumber)
        {
            List<MatchZyDuel> duels = currentRoundDuels;
            currentRoundDuels = new List<MatchZyDuel>();
            foreach (MatchZyDuel duel in duels) duel.RoundNumber = roundNumber;
            return duels;
        }

        // ─── Called at round start ────────────────────────────────────────────────
        public void ResetPerRoundKastState()
        {
            kastFlags.Clear();
            recentDeaths.Clear();
            currentRoundDuels.Clear();
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
        }

        // ─── Called on match reset ────────────────────────────────────────────────
        public void ResetMatchStats()
        {
            // Un reset de match limpia el mismo conjunto de contadores por-mapa.
            ResetPerMapStats();
        }
    }
}
