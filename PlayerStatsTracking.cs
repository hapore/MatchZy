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

        // ─── Called at round start ────────────────────────────────────────────────
        public void ResetPerRoundKastState()
        {
            kastFlags.Clear();
            recentDeaths.Clear();
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
        }

        // ─── Called on match reset ────────────────────────────────────────────────
        public void ResetMatchStats()
        {
            // Un reset de match limpia el mismo conjunto de contadores por-mapa.
            ResetPerMapStats();
        }
    }
}
