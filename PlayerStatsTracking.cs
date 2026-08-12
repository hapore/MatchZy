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

        // ─── Clutches ─────────────────────────────────────────────────────────────
        //
        // CS2 solo lleva 1v1 y 1v2 (I1v1Count/I1v2Count de MatchStats), asi que
        // 1v3/1v4/1v5 se calculan aca. Se lleva en vivo y no reconstruyendo desde
        // los duelos: la lista de duelos descarta lo que llega despues del
        // round_end (ver arriba), y ademas reconstruir obliga a inferir quien
        // estaba vivo en vez de saberlo.
        //
        // QUE CUENTA: la situacion arranca en la TRANSICION, el instante en que
        // un equipo pasa de dos o mas vivos a exactamente uno con al menos un
        // enemigo en pie. El versus queda congelado ahi: quedar 1v3 y matar a uno
        // sigue siendo el mismo 1v3, no abre un 1v2 nuevo.
        //
        // GANADO ES GANAR LA RONDA, no sobrevivir: matar a los tres y perder
        // igual porque exploto el C4 es un clutch perdido.

        /** Tope de la escala. Un 1v6 no existe, pero un reconecte raro no puede romper el array. */
        public const int MaxClutchVersus = 5;

        // Acumulado del MAPA, indexado [versus - 1]. Mismo criterio que el resto
        // de las stats del payload, que son acumuladas por mapa.
        private Dictionary<ulong, int[]> playerClutchCounts = new();
        private Dictionary<ulong, int[]> playerClutchWins = new();

        // Vivos de la ronda en curso, por TeamNum. Se toma en freeze end (la
        // ronda pasa a live ahi) y no en round start: antes de eso puede haber
        // gente sin spawnear o sin equipo asignado.
        private Dictionary<int, HashSet<ulong>> aliveByTeam = new();

        // Clutch abierto de cada equipo en la ronda: se resuelve en round end,
        // cuando se sabe quien gano. Un equipo entra a lo sumo una vez.
        private Dictionary<int, (ulong steamId, int versus)> pendingClutch = new();

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

        // ─── Clutches: seguimiento en vivo ────────────────────────────────────────

        // Foto de quien arranca vivo, por equipo. Se llama en freeze end.
        public void SnapshotAliveForRound()
        {
            aliveByTeam.Clear();
            pendingClutch.Clear();

            foreach (var player in Utilities.GetPlayers())
            {
                if (!IsPlayerValid(player)) continue;
                // Espectadores y coaches no juegan la ronda.
                if (player.TeamNum != (int)CsTeam.CounterTerrorist && player.TeamNum != (int)CsTeam.Terrorist) continue;
                if (matchzyTeam1.coach.Contains(player) || matchzyTeam2.coach.Contains(player)) continue;

                if (!aliveByTeam.TryGetValue(player.TeamNum, out var set))
                {
                    set = new HashSet<ulong>();
                    aliveByTeam[player.TeamNum] = set;
                }
                set.Add(player.SteamID);
            }
        }

        // Una muerte. Devuelve el clutch que se acaba de abrir, si se abrio.
        public void RecordClutchDeath(CCSPlayerController victim)
        {
            if (!aliveByTeam.TryGetValue(victim.TeamNum, out var side)) return;
            if (!side.Remove(victim.SteamID)) return;

            // Solo puede abrirse en el equipo que acaba de perder a alguien.
            if (side.Count != 1 || pendingClutch.ContainsKey(victim.TeamNum)) return;

            int enemies = 0;
            foreach (var (teamNum, players) in aliveByTeam)
            {
                if (teamNum != victim.TeamNum) enemies += players.Count;
            }
            if (enemies == 0) return;

            ulong survivor = side.First();
            pendingClutch[victim.TeamNum] = (survivor, Math.Min(MaxClutchVersus, enemies));
        }

        // Cierra los clutches de la ronda. `winnerTeamNum` es @event.Winner del
        // round end: comparar TeamNum evita tener que resolver CT/TERRORIST.
        public void FinalizeClutchesForRound(int winnerTeamNum)
        {
            foreach (var (teamNum, clutch) in pendingClutch)
            {
                if (!playerClutchCounts.TryGetValue(clutch.steamId, out var counts))
                {
                    counts = new int[MaxClutchVersus];
                    playerClutchCounts[clutch.steamId] = counts;
                }
                counts[clutch.versus - 1]++;

                if (teamNum != winnerTeamNum) continue;

                if (!playerClutchWins.TryGetValue(clutch.steamId, out var wins))
                {
                    wins = new int[MaxClutchVersus];
                    playerClutchWins[clutch.steamId] = wins;
                }
                wins[clutch.versus - 1]++;
            }
            pendingClutch.Clear();
        }

        // Acumulado del mapa para un jugador. `versus` va de 1 a MaxClutchVersus.
        public int GetClutchCount(ulong steamId, int versus)
            => playerClutchCounts.TryGetValue(steamId, out var v) ? v[versus - 1] : 0;

        public int GetClutchWins(ulong steamId, int versus)
            => playerClutchWins.TryGetValue(steamId, out var v) ? v[versus - 1] : 0;

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
            // Los clutches son acumulados POR MAPA, igual que el resto del
            // payload: sin este clear se sumarian los del mapa anterior.
            playerClutchCounts.Clear();
            playerClutchWins.Clear();
            aliveByTeam.Clear();
            pendingClutch.Clear();
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
