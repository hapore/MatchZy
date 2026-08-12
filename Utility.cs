using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Admin;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Drawing;


namespace MatchZy
{
    public partial class MatchZy
    {
        public const string warmupCfgPath = "MatchZy/warmup.cfg";
        public const string knifeCfgPath = "MatchZy/knife.cfg";
        public const string liveCfgPath = "MatchZy/live.cfg";
        public const string liveWingmanCfgPath = "MatchZy/live_wingman.cfg";

        private void PrintToAllChat(string message)
        {
            Server.PrintToChatAll($"{chatPrefix} {message}");
        }

        private void PrintToPlayerChat(CCSPlayerController player, string message)
        {
            player.PrintToChat($"{chatPrefix} {message}");
        }

        /// <summary>
        /// Mensaje sólo para el equipo que ganó el cuchillo. Las instrucciones de
        /// elección de lado (quién decide, el timeout y la cuenta regresiva) no le
        /// sirven al equipo perdedor ni a los espectadores: para ellos alcanza con
        /// el aviso de quién ganó y con la decisión final, que sí van a todos.
        /// </summary>
        private void PrintToKnifeWinnerChat(string message)
        {
            foreach (var kv in playerData)
            {
                CCSPlayerController player = kv.Value;
                if (!IsPlayerValid(player)) continue;
                if (player.TeamNum != knifeWinner) continue;
                player.PrintToChat($"{chatPrefix} {message}");
            }
        }

        private void ReplyToUserCommand(CCSPlayerController? player, string message, bool console = false)
        {
            if (player == null)
            {
                Server.PrintToConsole($"{chatPrefix} {message}");
            }
            else
            {
                if (console)
                {
                    player.PrintToConsole($"{chatPrefix} {message}");
                }
                else
                {
                    player.PrintToChat($"{chatPrefix} {message}");
                }
            }
        }

        private void LoadAdmins()
        {
            string fileName = "MatchZy/admins.json";
            string filePath = Path.Join(Server.GameDirectory + "/csgo/cfg", fileName);

            if (File.Exists(filePath))
            {
                try
                {
                    using (StreamReader fileReader = File.OpenText(filePath))
                    {
                        string jsonContent = fileReader.ReadToEnd();
                        if (!string.IsNullOrEmpty(jsonContent))
                        {
                            JsonSerializerOptions options = new()
                            {
                                AllowTrailingCommas = true,
                            };
                            loadedAdmins = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent, options) ?? new Dictionary<string, string>();
                        }
                        else
                        {
                            // Handle the case where the JSON content is empty or null
                            loadedAdmins = new Dictionary<string, string>();
                        }
                    }
                    foreach (var kvp in loadedAdmins)
                    {
                        Log($"[ADMIN] Username: {kvp.Key}, Role: {kvp.Value}");
                    }
                }
                catch (Exception e)
                {
                    Log($"[LoadAdmins FATAL] An error occurred: {e.Message}");
                }
            }
            else
            {
                Log("[LoadAdmins] The JSON file does not exist. Creating one with default content");
                Dictionary<string, string> defaultAdmins = new()
                {
                    { "steamid", "" }
                };

                try
                {
                    JsonSerializerOptions options = new()
                    {
                        WriteIndented = true,
                    };
                    string defaultJson = JsonSerializer.Serialize(defaultAdmins, options);
                    string? directoryPath = Path.GetDirectoryName(filePath);
                    if (directoryPath != null)
                    {
                        if (!Directory.Exists(directoryPath))
                        {
                            Directory.CreateDirectory(directoryPath);
                        }
                    }
                    File.WriteAllText(filePath, defaultJson);

                    Log("[LoadAdmins] Created a new JSON file with default content.");
                }
                catch (Exception e)
                {
                    Log($"[LoadAdmins FATAL] Error creating the JSON file: {e.Message}");
                }
            }
        }

        private bool IsPlayerAdmin(CCSPlayerController? player, string command = "", params string[] permissions)
        {
            if (everyoneIsAdmin.Value) return true; // Everyone is treated as admin if matchzy_everyone_is_admin is true.
            string[] updatedPermissions = permissions.Concat(new[] { "@css/root" }).ToArray();
            RequiresPermissionsOr attr = new(updatedPermissions)
            {
                Command = command
            };
            if (attr.CanExecuteCommand(player)) return true; // Admin exists in admins.json of CSSharp
            if (player == null) return true; // Sent via server, hence should be treated as an admin.
            if (loadedAdmins.ContainsKey(player.SteamID.ToString())) return true; // Admin exists in admins.json of MatchZy
            return false;
        }

        private int GetRealPlayersCount()
        {
            return playerData.Count;
        }

        private void SendUnreadyPlayersMessage()
        {
            if (!isWarmup || matchStarted) return;
            List<string> unreadyPlayers = new();

            foreach (var key in playerReadyStatus.Keys)
            {
                if (playerReadyStatus[key] == false)
                {
                    unreadyPlayers.Add(playerData[key].PlayerName);
                }
            }
            if (unreadyPlayers.Count > 0)
            {
                string unreadyPlayerList = string.Join(", ", unreadyPlayers);
                string minimumReadyRequiredMessage = isMatchSetup ? "" : $"[Minimum ready players required: {ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}]";

                // Server.PrintToChatAll($"{chatPrefix} Unready players: {unreadyPlayerList}. Please type .ready to ready up! {minimumReadyRequiredMessage}");
                if (isRoundRestorePending)
                {
                    PrintToAllChat(Localizer["matchzy.ready.readytotestorebackupinfomessage", unreadyPlayerList, minimumReadyRequiredMessage]);
                }
                else
                {
                    PrintToAllChat(Localizer["matchzy.utility.unreadyplayers", unreadyPlayerList, minimumReadyRequiredMessage]);
                }
            }
            else
            {
                int countOfReadyPlayers = playerReadyStatus.Count(kv => kv.Value == true);
                if (isMatchSetup)
                {
                    // Server.PrintToChatAll($"{chatPrefix} Current ready players: {ChatColors.Green}{countOfReadyPlayers}{ChatColors.Default}");
                    PrintToAllChat(Localizer["matchzy.utility.readyplayers", countOfReadyPlayers]);
                }
                else
                {
                    // Server.PrintToChatAll($"{chatPrefix} Minimum ready players required {ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}, current ready players: {ChatColors.Green}{countOfReadyPlayers}{ChatColors.Default}");
                    PrintToAllChat(Localizer["matchzy.utility.minimumreadyplayers", minimumReadyRequired, countOfReadyPlayers]);
                }
            }
        }

        private void SendPausedStateMessage()
        {
            if (isPaused && matchStarted)
            {
                var pauseTeamName = unpauseData["pauseTeam"];
                if ((string)pauseTeamName == "Admin")
                {
                    PrintToAllChat(Localizer["matchzy.pause.adminpausedthematch"]);
                }
                else if ((string)pauseTeamName == "RoundRestore" && !(bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["matchzy.pause.pausedbecauserestore"]);
                }
                else if ((bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["matchzy.pause.teamwantstounpause", reverseTeamSides["TERRORIST"].teamName, reverseTeamSides["CT"].teamName]);
                }
                else if (!(bool)unpauseData["t"] && (bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["matchzy.pause.teamwantstounpause", reverseTeamSides["CT"].teamName, reverseTeamSides["TERRORIST"].teamName]);
                }
                else if (!(bool)unpauseData["t"] && !(bool)unpauseData["ct"])
                {
                    PrintToAllChat(Localizer["matchzy.pause.pausedthematch", pauseTeamName]);
                }
            }
        }

        /// <summary>
        /// Reaplica el CFG de warmup cuando el servidor deja de estar vacío.
        ///
        /// Verificado empíricamente: con el servidor vacío se pierden cvars del
        /// warmup. Si el único jugador conectado se va y vuelve, las granadas
        /// vuelven a ser comprables; si quedan dos y se va uno, la restricción
        /// se mantiene. O sea que el disparador no es el changelevel sino que el
        /// servidor toque cero jugadores — hibernación (`sv_hibernate_when_empty`)
        /// o la reinicialización del game rules al despertar, que reejecuta los
        /// gamemode_*.cfg y pisa lo nuestro.
        ///
        /// Esto cubre también el caso post-changelevel sin lógica aparte: tras un
        /// cambio de mapa los jugadores reconectan, así que el primero en entrar
        /// vuelve a pasar por acá.
        /// </summary>
        private void HandleFirstPlayerConnected()
        {
            if (!IsWarmupCfgReapplyAllowed()) return;

            // Un segundo de margen: `exec` en Source 2 no aplica los cvars, encola
            // las líneas en el buffer de consola. Ejecutar en el mismo tick del
            // connect deja nuestro exec compitiendo contra lo que el engine encola
            // al despertar.
            //
            // Se revalida adentro porque en ese segundo la fase puede cambiar.
            AddTimer(1.0f, () =>
            {
                if (!IsWarmupCfgReapplyAllowed()) return;
                Log("[HandleFirstPlayerConnected] Servidor salió de vacío, reaplicando warmup CFG");
                ExecWarmupCfg();
            });
        }

        /// <summary>
        /// ¿Es seguro reejecutar el CFG de warmup ahora?
        ///
        /// El que manda es `warmupCfgReapplyEnabled`: solo está abierto durante
        /// el warmup inicial. `isWarmup` se chequea igual porque puede apagarse
        /// sin cerrar el latch (modo sleep, por ejemplo), y solo puede restringir
        /// más, nunca menos.
        ///
        /// `isRoundRestorePending` cubre un caso que el latch no ve: al restaurar
        /// un backup con el servidor en warmup, el plugin deja el restore
        /// pendiente y espera el ready de los jugadores (BackupManagement.cs,
        /// rama `gameRules.WarmupPeriod`). Ahí seguimos en el warmup inicial con
        /// el latch abierto, pero hay una partida con rondas jugadas esperando.
        /// </summary>
        private bool IsWarmupCfgReapplyAllowed() =>
            warmupCfgReapplyEnabled && isWarmup && !isPractice && !isRoundRestorePending;

        private void ExecWarmupCfg()
        {
            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", warmupCfgPath);

            // Marca el momento en que arranca el warmup. CheckLiveRequired usa
            // este timestamp para asegurar un mínimo de estabilización del
            // engine antes de pasar a live (evita SIGSEGV en BeginMatch).
            warmupStartedAt = DateTime.UtcNow;

            if (File.Exists(Path.Join(Server.GameDirectory + "/csgo/cfg", warmupCfgPath)))
            {
                Log($"[StartWarmup] Starting warmup! Executing Warmup CFG from {warmupCfgPath}");
                Server.ExecuteCommand($"exec {warmupCfgPath}");
            }
            else
            {
                Log($"[StartWarmup] Starting warmup! Warmup CFG not found in {absolutePath}, using default CFG!");
                Server.ExecuteCommand("bot_kick;bot_quota 0;mp_autokick 0;mp_autoteambalance 0;mp_buy_anywhere 0;mp_buytime 15;mp_death_drop_gun 0;mp_free_armor 0;mp_ignore_round_win_conditions 0;mp_limitteams 0;mp_radar_showall 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_solid_teammates 0;mp_spectators_max 20;mp_maxmoney 16000;mp_startmoney 16000;mp_timelimit 0;sv_alltalk 0;sv_auto_full_alltalk_during_warmup_half_end 0;sv_deadtalk 1;sv_full_alltalk 0;sv_grenade_trajectory 0;sv_hibernate_when_empty 0;mp_weapons_allow_typecount -1;sv_infinite_ammo 0;sv_showimpacts 0;sv_voiceenable 1;sm_cvar sv_mute_players_with_social_penalties 0;sv_mute_players_with_social_penalties 0;tv_relayvoice 1;sv_cheats 0;mp_ct_default_melee weapon_knife;mp_ct_default_secondary weapon_hkp2000;mp_ct_default_primary \"\";mp_t_default_melee weapon_knife;mp_t_default_secondary weapon_glock;mp_t_default_primary;mp_maxrounds 24;mp_warmup_start;mp_warmup_pausetimer 1;mp_warmuptime 9999;cash_team_bonus_shorthanded 0;");
            }
        }

        /// <summary>
        /// Starts (or restarts) the player-wait timeout system for the current map.
        /// Safe to call on first map load AND on each subsequent map in a BO3/BO5 series.
        /// Does nothing if no player list is defined in the match config or timeout is disabled.
        /// </summary>
        private void StartPlayerWaitSystem()
        {
            int expectedCount = GetExpectedMatchPlayersCount();
            if (expectedCount == 0 || matchConfig.PlayerWaitTimeout <= 0)
            {
                Log($"[StartPlayerWaitSystem] Skipping: expectedCount={expectedCount}, timeout={matchConfig.PlayerWaitTimeout}");
                return;
            }

            // Kill any leftover timers (e.g. from a previous map in the series)
            playerWaitTimeoutTimer?.Kill();
            playerWaitTimeoutTimer = null;
            playerWaitReminderTimer?.Kill();
            playerWaitReminderTimer = null;
            matchCountdownTimer?.Kill();
            matchCountdownTimer = null;

            isWaitingForPlayers = true;
            int waitTimeout = matchConfig.PlayerWaitTimeout;
            int mapNumber = matchConfig.CurrentMapNumber;
            long capturedMatchId = liveMatchId;
            DateTime waitStartedAt = DateTime.UtcNow;

            Log($"[StartPlayerWaitSystem] Map {mapNumber}: waiting up to {waitTimeout}s for {expectedCount} player(s).");

            // Recordatorio periódico en chat. Antes vivía en el backend
            // (`matchPlayerCheck.ts`) usando RCON, ahora es nativo del plugin
            // para evitar la race condition de timers paralelos.
            //   • Mientras quedan > threshold: mensaje cada 60s con minutos restantes.
            //   • Cuando quedan <= threshold (def. 30s): mensaje cada segundo con segundos restantes.
            //
            // NOTA IMPORTANTE: NO usamos Timer.REPEAT. Cada tick se reagenda a sí
            // mismo con un AddTimer(1.0f) one-shot. Si la `playerWaitGeneration`
            // cambió, los ticks pendientes se descartan silenciosamente. Esto
            // evita tener que llamar Kill() sobre un Timer.REPEAT, lo que ya
            // nos provocó SIGSEGV del engine en runs previos.
            int secondsThreshold = playerWaitSecondsThresholdCvar.Value > 0 ? playerWaitSecondsThresholdCvar.Value : 30;
            int waitId = ++playerWaitGeneration;
            ScheduleWaitReminderTick(waitId, waitTimeout, secondsThreshold, waitStartedAt, lastPrintedSec: -1);
            playerWaitReminderTimer = null; // ya no usamos un timer único

            playerWaitTimeoutTimer = AddTimer(waitTimeout, () =>
            {
                if (!matchStarted && isWaitingForPlayers)
                {
                    Log($"[PlayerWaitTimeout] Map {mapNumber}: {waitTimeout}s reached. Not all players connected. Cancelling match.");
                    PrintToAllChat(Localizer["matchzy.match.playerwaittimeout"]);

                    // Calcular SteamIDs faltantes ANTES de resetear, para
                    // poder reportarlos al backend.
                    List<string> missing = GetMissingMatchPlayerSteamIds();

                    // Notificar al backend que el plugin canceló la partida.
                    // Esto reemplaza al watchdog que antes vivía en el backend.
                    if (capturedMatchId > 0)
                    {
                        var cancelledEvent = new MatchCancelledEvent
                        {
                            MatchId = capturedMatchId,
                            Reason = "players_not_connected",
                            Missing = missing,
                        };
                        Task.Run(async () => await SendEventAsync(cancelledEvent));
                    }

                    isWaitingForPlayers = false;
                    playerWaitGeneration++; // descarta ticks one-shot pendientes
                    playerWaitReminderTimer?.Kill();
                    playerWaitReminderTimer = null;
                    ResetMatch();
                }
            });
        }

        /// <summary>
        /// Encola UN tick one-shot del recordatorio de espera de jugadores. Cada
        /// tick se reagenda a sí mismo. Si <c>playerWaitGeneration</c> cambió,
        /// el tick se descarta silenciosamente \u2014 mecanismo de cancelación que
        /// evita el patrón problemático de Timer.REPEAT + Kill().
        /// </summary>
        private void ScheduleWaitReminderTick(int waitId, int waitTimeout, int secondsThreshold, DateTime waitStartedAt, int lastPrintedSec)
        {
            AddTimer(1.0f, () =>
            {
                try
                {
                    if (waitId != playerWaitGeneration) return; // cancelado
                    if (!isWaitingForPlayers || matchStarted) return;

                    int elapsedSec = (int)(DateTime.UtcNow - waitStartedAt).TotalSeconds;
                    int remainingSec = waitTimeout - elapsedSec;
                    if (remainingSec <= 0)
                    {
                        // Se acabó el tiempo \u2014 el `playerWaitTimeoutTimer` (one-shot
                        // independiente) se encarga de cancelar la partida.
                        return;
                    }

                    int newLastPrinted = lastPrintedSec;
                    if (remainingSec != lastPrintedSec)
                    {
                        if (remainingSec <= secondsThreshold)
                        {
                            PrintToAllChat(Localizer["matchzy.match.playerwaitseconds", remainingSec]);
                            newLastPrinted = remainingSec;
                        }
                        else if (remainingSec % 60 == 0)
                        {
                            int remainingMin = remainingSec / 60;
                            PrintToAllChat(Localizer["matchzy.match.playerwaitminutes", remainingMin]);
                            newLastPrinted = remainingSec;
                        }
                    }

                    // Reagendar próximo tick.
                    ScheduleWaitReminderTick(waitId, waitTimeout, secondsThreshold, waitStartedAt, newLastPrinted);
                }
                catch (Exception ex)
                {
                    Log($"[WaitReminderTick FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        /// <summary>
        /// Devuelve los SteamIDs declarados en el match config (team1+team2)
        /// que aún no están conectados al servidor. Usado para reportar quién
        /// faltó cuando el plugin cancela una partida por timeout.
        /// </summary>
        private List<string> GetMissingMatchPlayerSteamIds()
        {
            var connected = new HashSet<string>();
            foreach (var key in playerData.Keys)
            {
                var p = playerData[key];
                if (!p.IsValid || p.IsBot) continue;
                connected.Add(p.SteamID.ToString());
            }

            var missing = new List<string>();
            void Collect(Newtonsoft.Json.Linq.JToken? teamPlayers)
            {
                if (teamPlayers is not Newtonsoft.Json.Linq.JObject obj) return;
                foreach (var prop in obj.Properties())
                {
                    if (!connected.Contains(prop.Name)) missing.Add(prop.Name);
                }
            }
            Collect(matchzyTeam1.teamPlayers);
            Collect(matchzyTeam2.teamPlayers);
            return missing;
        }

        private void StartWarmup()
        {
            unreadyPlayerMessageTimer?.Kill();
            unreadyPlayerMessageTimer = null;
            unreadyPlayerMessageTimer ??= AddTimer(chatTimerDelay, SendUnreadyPlayersMessage, TimerFlags.REPEAT);
            isWarmup = true;
            // Único punto que abre el latch: este es el warmup inicial.
            warmupCfgReapplyEnabled = true;
            ExecWarmupCfg();
        }

        private void StartKnifeRound()
        {
            // Kills unready players message timer
            if (unreadyPlayerMessageTimer != null)
            {
                unreadyPlayerMessageTimer.Kill();
                unreadyPlayerMessageTimer = null;
            }

            // Setting match phases bools
            matchStarted = true;
            isKnifeRound = true;
            readyAvailable = false;
            isWarmup = false;
            // Se terminó el warmup inicial: a partir de acá no se reaplica más.
            warmupCfgReapplyEnabled = false;

            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", knifeCfgPath);

            if (File.Exists(Path.Join(Server.GameDirectory + "/csgo/cfg", knifeCfgPath)))
            {
                Log($"[StartKnifeRound] Starting Knife! Executing Knife CFG from {knifeCfgPath}");
                Server.ExecuteCommand($"exec {knifeCfgPath}");
                Server.ExecuteCommand("mp_restartgame 1;mp_warmup_end;");
            }
            else
            {
                Log($"[StartKnifeRound] Starting Knife! Knife CFG not found in {absolutePath}, using default CFG!");
                Server.ExecuteCommand("mp_ct_default_secondary \"\";mp_free_armor 1;mp_freezetime 10;mp_give_player_c4 0;mp_maxmoney 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;mp_t_default_secondary \"\";mp_round_restart_delay 3;mp_team_intro_time 0;mp_restartgame 1;mp_warmup_end;");
            }

            PrintToAllChat($"{ChatColors.Olive}KNIFE!");
            PrintToAllChat($"{ChatColors.Lime}KNIFE!");
            PrintToAllChat($"{ChatColors.Green}KNIFE!");
        }

        /// <summary>Equipo que ganó el cuchillo (el objeto Team, no el side).</summary>
        public Team? GetKnifeWinnerTeam()
        {
            if (knifeWinner == 3) return reverseTeamSides.GetValueOrDefault("CT");
            if (knifeWinner == 2) return reverseTeamSides.GetValueOrDefault("TERRORIST");
            return null;
        }

        /// <summary>
        /// ¿Este jugador puede decidir el lado?
        ///
        /// Tiene que ser del equipo que ganó el cuchillo y, si el match config
        /// definió capitán, ser ese capitán. Sin capitán en el config se cae al
        /// comportamiento histórico (cualquiera del equipo ganador) para no
        /// romper los configs armados a mano.
        /// </summary>
        public bool CanDecideSide(CCSPlayerController player)
        {
            if (player.TeamNum != knifeWinner) return false;

            string captain = GetKnifeWinnerTeam()?.captain ?? "";
            if (string.IsNullOrEmpty(captain)) return true;

            return player.SteamID.ToString() == captain;
        }

        /// <summary>
        /// Un tick de la cuenta regresiva para elegir lado. Avisa a quién le toca
        /// decidir y cuánto le queda; al llegar a cero elige al azar y arranca.
        ///
        /// Ticks one-shot que se reagendan solos, descartando los viejos por
        /// generación: el Timer.REPEAT + Kill() desde el propio callback es el
        /// patrón que venía corrompiendo memoria del engine.
        /// </summary>
        private void SideSelectionTick(int generation, int secondsLeft)
        {
            AddTimer(1.0f, () =>
            {
                if (generation != sideSelectionGeneration) return; // ya decidió
                if (!isSideSelectionPhase) return;

                int left = secondsLeft - 1;

                if (left <= 0)
                {
                    // Nadie decidió: se elige al azar para no dejar la partida
                    // colgada en un warmup infinito.
                    bool stay = Random.Shared.Next(2) == 0;
                    // Este es el único anuncio del resultado: ApplySideDecision no
                    // repite el "ha decidido..." porque acá no hubo decisión.
                    PrintToAllChat($"{ChatColors.Green}{knifeWinnerName}{ChatColors.Default} no eligió a tiempo: se decide al azar → {ChatColors.Green}{(stay ? "mantener" : "cambiar")} bando{ChatColors.Default}");
                    Log($"[SideSelection] Timeout: eleccion aleatoria {(stay ? "stay" : "switch")} para {knifeWinnerName}.");
                    ApplySideDecision(stay, announce: false);
                    return;
                }

                // Recordatorio cada 10s y en los últimos 5.
                if (left % 10 == 0 || left <= 5)
                {
                    string who = GetSideDecider();
                    PrintToKnifeWinnerChat($"{ChatColors.Green}{who}{ChatColors.Default} debe elegir {ChatColors.Green}.stay{ChatColors.Default} o {ChatColors.Green}.switch{ChatColors.Default} — quedan {ChatColors.Green}{left}s{ChatColors.Default}");
                }

                SideSelectionTick(generation, left);
            });
        }

        /// <summary>Nombre de quien debe decidir: el capitán si lo hay, si no el equipo.</summary>
        private string GetSideDecider()
        {
            Team? team = GetKnifeWinnerTeam();
            string captain = team?.captain ?? "";
            if (string.IsNullOrEmpty(captain)) return knifeWinnerName;

            foreach (var kv in playerData)
            {
                if (kv.Value.IsValid && kv.Value.SteamID.ToString() == captain) return kv.Value.PlayerName;
            }
            // El capitán no está conectado: igual se nombra al equipo, y si no
            // vuelve a tiempo decide el azar.
            return knifeWinnerName;
        }

        /// <summary>
        /// Aplica la decisión de lado y arranca la partida. Único camino, lo use
        /// el capitán con `.stay`/`.switch` o el timeout con su elección al azar.
        ///
        /// <paramref name="announce"/> apaga el "ha decidido mantener/cambiar
        /// bando": ese mensaje afirma que el equipo eligió, y en el camino del
        /// timeout eso es falso —ahí ya se anunció que lo resolvió el azar—.
        /// </summary>
        public void ApplySideDecision(bool stay, bool announce = true)
        {
            if (!isSideSelectionPhase) return;
            sideSelectionGeneration++; // corta los ticks pendientes

            if (stay)
            {
                if (announce) PrintToAllChat(Localizer["matchzy.knife.decidedtostay", knifeWinnerName]);
            }
            else
            {
                Server.ExecuteCommand("mp_swapteams;");
                SwapSidesInTeamData(true);
                if (announce) PrintToAllChat(Localizer["matchzy.knife.decidedtoswitch", knifeWinnerName]);
            }
            StartLive();
        }

        private void StartAfterKnifeWarmup()
        {
            isWarmup = true;
            ExecWarmupCfg();
            knifeWinnerName = knifeWinner == 3 ? reverseTeamSides["CT"].teamName : reverseTeamSides["TERRORIST"].teamName;
            ShowDamageInfo();

            // El mensaje localizado habla del EQUIPO que ganó el cuchillo; quién
            // decide se aclara aparte, porque con capitán no son lo mismo.
            PrintToAllChat(Localizer["matchzy.knife.sidedecisionpending", knifeWinnerName]);

            string decider = GetSideDecider();
            if (decider != knifeWinnerName)
            {
                PrintToKnifeWinnerChat($"Sólo {ChatColors.Green}{decider}{ChatColors.Default} (capitán) puede elegir el lado.");
            }

            int timeout = sideSelectionTimeoutCvar.Value;
            if (timeout > 0)
            {
                PrintToKnifeWinnerChat($"Hay {ChatColors.Green}{timeout}s{ChatColors.Default} para decidir, o se elige al azar.");
                SideSelectionTick(++sideSelectionGeneration, timeout);
            }
        }

        private void SetLiveFlags()
        {
            // Setting match phases bools
            isWarmup = false;
            isSideSelectionPhase = false;
            matchStarted = true;
            isMatchLive = true;
            // Cierra el latch también en el camino sin cuchillo, donde el warmup
            // inicial va directo a live sin pasar por StartKnifeRound().
            warmupCfgReapplyEnabled = false;
            readyAvailable = false;
            isKnifeRound = false;
        }

        private void SetupLiveFlagsAndCfg()
        {
            Log("[SetupLiveFlagsAndCfg] ENTER -> SetLiveFlags");
            SetLiveFlags();
            Log("[SetupLiveFlagsAndCfg] SetLiveFlags OK -> KillPhaseTimers");
            KillPhaseTimers();
            Log("[SetupLiveFlagsAndCfg] KillPhaseTimers OK -> ExecLiveCFG");
            ExecLiveCFG();
            Log("[SetupLiveFlagsAndCfg] ExecLiveCFG returned, scheduling 1s post-CFG timer");
            // Adding timer here to make sure that CFG execution is completed till then
            AddTimer(1, () =>
            {
                try
                {
                    Log("[PostCFG +1s] HandlePlayoutConfig");
                    HandlePlayoutConfig();
                    Log("[PostCFG +1s] ExecuteChangedConvars");
                    ExecuteChangedConvars();
                    Log("[PostCFG +1s] done");
                }
                catch (Exception ex)
                {
                    Log($"[PostCFG FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private void StartLive()
        {
            Log("[StartLive] ENTER -> SetupLiveFlagsAndCfg");
            // Reinicia las estadísticas por-mapa (KAST, knife kills, bomb plants,
            // flash assists, etc.) al inicio de cada mapa. Sin esto, en un BO3/BO5
            // los contadores se acumulan entre mapas e inflan los valores
            // persistidos por mapa (KAST llegaba a >100%).
            ResetPerMapStats();
            SetupLiveFlagsAndCfg();
            Log("[StartLive] SetupLiveFlagsAndCfg OK -> StartDemoRecording");
            StartDemoRecording();

            // Storing 0-0 score backup file as lastBackupFileName, so that .stop functions properly in first round.
            lastBackupFileName = $"matchzy_{liveMatchId}_{matchConfig.CurrentMapNumber}_round00.txt";
            lastMatchZyBackupFileName = $"matchzy_{liveMatchId}_{matchConfig.CurrentMapNumber}_round00.json";

            // This is to reload the map once it is over so that all flags are reset accordingly
            Server.ExecuteCommand("mp_match_end_restart true");

            PrintToAllChat($"{ChatColors.Olive}LIVE!");
            PrintToAllChat($"{ChatColors.Lime}LIVE!");
            PrintToAllChat($"{ChatColors.Green}LIVE!");

            var goingLiveEvent = new GoingLiveEvent
            {
                MatchId = liveMatchId,
                MapNumber = matchConfig.CurrentMapNumber,
            };

            Task.Run(async () =>
            {
                await SendEventAsync(goingLiveEvent);
            });
        }

        private void KillPhaseTimers()
        {
            unreadyPlayerMessageTimer?.Kill();
            pausedStateTimer?.Kill();
            playerWaitTimeoutTimer?.Kill();
            playerWaitReminderTimer?.Kill();
            matchCountdownTimer?.Kill();
            unreadyPlayerMessageTimer = null;
            pausedStateTimer = null;
            playerWaitTimeoutTimer = null;
            playerWaitReminderTimer = null;
            matchCountdownTimer = null;
            // Cancela cualquier tick one-shot del countdown que esté en cola.
            countdownGeneration++;
            isCountdownActive = false;
            // Cancela cualquier tick one-shot del wait reminder que esté en cola.
            playerWaitGeneration++;
            // Idem para la seleccion de lado y el recordatorio de abandono.
            sideSelectionGeneration++;
            abandonWarnGeneration++;
        }

        private (int alivePlayers, int totalHealth) GetAlivePlayers(int team)
        {
            int count = 0;
            int totalHealth = 0;
            foreach (var key in playerData.Keys)
            {
                CCSPlayerController player = playerData[key];
                if (team == 2 && reverseTeamSides["TERRORIST"].coach.Contains(player)) continue;
                if (team == 3 && reverseTeamSides["CT"].coach.Contains(player)) continue;
                if (!IsPlayerValid(player)) continue;
                if (player.TeamNum == team)
                {
                    if (player.PlayerPawn.Value!.Health > 0) count++;
                    totalHealth += player.PlayerPawn.Value!.Health;
                }
            }
            return (count, totalHealth);
        }

        private void ResetMatch(bool warmupCfgRequired = true)
        {
            try
            {
                // We stop demo recording if a live match was restarted
                if (matchStarted && isDemoRecording)
                {
                    Server.ExecuteCommand($"tv_stoprecord");
                    isDemoRecording = false;
                }
                // Reset match data
                ResetMatchStats();
                // Sin esto, los abandonos de una partida cancelada se reportarían
                // en la serie siguiente que se juegue en este servidor.
                AbandonReset();
                matchStarted = false;
                readyAvailable = true;
                isPaused = false;
                isMatchSetup = false;

                isWarmup = true;
                isKnifeRound = false;
                isSideSelectionPhase = false;
                isMatchLive = false;
                liveMatchId = -1;
                isPractice = false;
                isDryRun = false;
                isVeto = false;
                isPreVeto = false;

                lastBackupFileName = "";
                lastMatchZyBackupFileName = "";

                isRoundRestorePending = false;
                playerHasTakenDamage = false;
                matchLoadedFromUrl = false;

                // Reset player-wait / countdown state
                isWaitingForPlayers = false;
                playerWaitTimeoutTimer?.Kill();
                playerWaitTimeoutTimer = null;
                playerWaitReminderTimer?.Kill();
                playerWaitReminderTimer = null;
                matchCountdownTimer?.Kill();
                matchCountdownTimer = null;
                countdownGeneration++; // descarta ticks pendientes del countdown
                isCountdownActive = false;
                playerWaitGeneration++; // descarta ticks pendientes del wait reminder

                // Unready all players
                foreach (var key in playerReadyStatus.Keys)
                {
                    playerReadyStatus[key] = false;
                }

                teamReadyOverride = new()
                {
                    {CsTeam.Terrorist, false},
                    {CsTeam.CounterTerrorist, false},
                    {CsTeam.Spectator, false}
                };

                HandleClanTags();

                // Reset unpause data
                Dictionary<string, object> unpauseData = new()
                {
                    { "ct", false },
                    { "t", false },
                    { "pauseTeam", "" }
                };

                // Reset stop data
                stopData["ct"] = false;
                stopData["t"] = false;

                // Reset owned bots data
                pracUsedBots = new Dictionary<int, Dictionary<string, object>>();
                noFlashList = new();
                lastGrenadesData = new();
                nadeSpecificLastGrenadeData = new();
                UnpauseMatch();

                matchzyTeam1.teamName = "COUNTER-TERRORISTS";
                matchzyTeam2.teamName = "TERRORISTS";

                matchzyTeam1.teamPlayers = null;
                matchzyTeam2.teamPlayers = null;

                HashSet<CCSPlayerController> coaches = GetAllCoaches();

                foreach (var coach in coaches)
                {
                    if (!IsPlayerValid(coach)) continue;
                    coach.Clan = "";
                    SetPlayerVisible(coach);
                }

                matchzyTeam1.coach = new();
                matchzyTeam2.coach = new();
                coachKillTimer?.Kill();
                coachKillTimer = null;

                matchzyTeam1.seriesScore = 0;
                matchzyTeam2.seriesScore = 0;

                Server.ExecuteCommand($"mp_teamname_1 {matchzyTeam1.teamName}");
                Server.ExecuteCommand($"mp_teamname_2 {matchzyTeam2.teamName}");

                teamSides[matchzyTeam1] = "CT";
                teamSides[matchzyTeam2] = "TERRORIST";
                reverseTeamSides["CT"] = matchzyTeam1;
                reverseTeamSides["TERRORIST"] = matchzyTeam2;

                // Keeping the log URLs to avoid their reset on match start.
                matchConfig = new()
                {
                    RemoteLogURL = matchConfig.RemoteLogURL,
                    RemoteLogHeaderKey = matchConfig.RemoteLogHeaderKey,
                    RemoteLogHeaderValue = matchConfig.RemoteLogHeaderValue
                };

                KillPhaseTimers();
                UpdatePlayersMap();
                if (warmupCfgRequired)
                {
                    StartWarmup();
                }
                else
                {
                    // Since we should be already in warmup phase by this point, we are just setting up the SendUnreadyPlayersMessage timer
                    unreadyPlayerMessageTimer?.Kill();
                    unreadyPlayerMessageTimer = null;
                    unreadyPlayerMessageTimer ??= AddTimer(chatTimerDelay, SendUnreadyPlayersMessage, TimerFlags.REPEAT);
                }
            }
            catch (Exception ex)
            {
                Log($"[ResetMatch - FATAL] [ERROR]: {ex.Message}");
            }
        }

        private void UpdatePlayersMap()
        {
            try
            {
                var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
                Log($"[UpdatePlayersMap] CCSPlayerController count: {playerEntities.Count<CCSPlayerController>()} matchModeOnly: {matchModeOnly}");
                connectedPlayers = 0;

                // Clear the playerData dictionary by creating a new instance to add fresh data.
                playerData = new Dictionary<int, CCSPlayerController>();
                foreach (var player in playerEntities)
                {
                    if (player == null) continue;
                    if (!player.IsValid || player.IsBot || player.IsHLTV) continue;

                    if (isMatchSetup || matchModeOnly)
                    {
                        CsTeam team = GetPlayerTeam(player);
                        if (team == CsTeam.None && player.UserId.HasValue)
                        {
                            Server.ExecuteCommand($"kickid {(ushort)player.UserId}");
                            continue;
                        }
                    }

                    // A player controller still exists after a player disconnects
                    // Hence checking whether the player is actually in the server or not
                    if (player.Connected != PlayerConnectedState.Connected) continue;

                    if (player.UserId.HasValue)
                    {

                        // Updating playerData and playerReadyStatus
                        playerData[player.UserId.Value] = player;

                        // Adding missing player in playerReadyStatus
                        if (!playerReadyStatus.ContainsKey(player.UserId.Value))
                        {
                            playerReadyStatus[player.UserId.Value] = false;
                        }
                    }
                    connectedPlayers++;
                }

                // Removing disconnected players from playerReadyStatus
                foreach (var key in playerReadyStatus.Keys.ToList())
                {
                    if (!playerData.ContainsKey(key))
                    {
                        // Key is not present in playerData, so remove it from playerReadyStatus
                        playerReadyStatus.Remove(key);
                    }
                }
                Log($"[UpdatePlayersMap] CCSPlayerController count: {playerEntities.Count<CCSPlayerController>()}, RealPlayersCount: {GetRealPlayersCount()}");
            }
            catch (Exception e)
            {
                Log($"[UpdatePlayersMap FATAL] An error occurred: {e.Message}");
            }
        }

        /// <summary>Returns the total number of players declared in the match config (team1 + team2).</summary>
        public int GetExpectedMatchPlayersCount()
        {
            int count = 0;
            if (matchzyTeam1.teamPlayers is Newtonsoft.Json.Linq.JObject t1 && t1 != null)
                count += t1.Count;
            if (matchzyTeam2.teamPlayers is Newtonsoft.Json.Linq.JObject t2 && t2 != null)
                count += t2.Count;
            return count;
        }

        /// <summary>Returns how many config-listed players (team1 or team2) are currently connected.</summary>
        public int GetConnectedMatchPlayers()
        {
            int count = 0;
            foreach (var key in playerData.Keys)
            {
                if (!playerData[key].IsValid) continue;
                CsTeam team = GetPlayerTeam(playerData[key]);
                if (team == CsTeam.CounterTerrorist || team == CsTeam.Terrorist)
                    count++;
            }
            return count;
        }

        /// <summary>Returns "team1" / "team2" / "" depending on which config team the player belongs to.</summary>
        // ── Detección de abandono ────────────────────────────────────────────

        /// <summary>
        /// Abre el intervalo de desconexión de un jugador de equipo. Sólo cuenta
        /// con la partida en vivo: irse durante el warmup o entre mapas no es
        /// abandonar nada.
        ///
        /// Los espectadores y admins quedan afuera por construcción, porque
        /// `GetPlayerMatchTeamName` sólo mira team1/team2 del config.
        /// </summary>
        public void AbandonTrackDisconnect(CCSPlayerController player)
        {
            if (abandonThresholdCvar.Value <= 0) return;
            if (!isMatchLive) return;
            if (player.IsBot || player.IsHLTV) return;
            if (string.IsNullOrEmpty(GetPlayerMatchTeamName(player))) return;

            string steamId = player.SteamID.ToString();
            // Si ya había un intervalo abierto (doble evento de desconexión), se
            // conserva el original: reabrirlo perdería el tiempo ya transcurrido.
            if (abandonOpenSince.ContainsKey(steamId)) return;

            abandonOpenSince[steamId] = DateTime.UtcNow;
            // El nick se guarda AHORA: mientras esté fuera no hay controller del
            // que sacarlo, y el aviso en chat tiene que nombrarlo.
            abandonNames[steamId] = player.PlayerName;
            Log($"[Abandon] {steamId} se desconectó en vivo (mapa {matchConfig.CurrentMapNumber}).");

            // Primer aviso inmediato; los siguientes los reagenda el propio tick.
            AbandonWarnTick(++abandonWarnGeneration, first: true);
        }

        /// <summary>
        /// Segundos que le quedan a un jugador fuera antes de que se lo reporte,
        /// contando el intervalo abierto además de lo ya acumulado en el mapa.
        /// Devuelve 0 si ya superó el umbral.
        /// </summary>
        private int AbandonSecondsLeft(string steamId, DateTime now)
        {
            int accumulated = abandonAccumulated.GetValueOrDefault(steamId);
            if (abandonOpenSince.TryGetValue(steamId, out DateTime since))
            {
                accumulated += (int)Math.Max(0, (now - since).TotalSeconds);
            }
            return Math.Max(0, abandonThresholdCvar.Value - accumulated);
        }

        /// <summary>
        /// Un tick del recordatorio en chat: nombra a cada desconectado y cuánto
        /// le queda. Se reagenda a sí mismo mientras haya alguien fuera.
        ///
        /// Los ticks viejos se descartan comparando la generación, en vez de
        /// matar un Timer.REPEAT desde su propio callback — ese patrón es el que
        /// venía provocando SIGSEGV del engine (ver ScheduleWaitReminderTick).
        /// </summary>
        private void AbandonWarnTick(int generation, bool first = false)
        {
            if (abandonWarnIntervalCvar.Value <= 0) return;

            void Run()
            {
                if (generation != abandonWarnGeneration) return; // reemplazado
                if (!isMatchLive || abandonOpenSince.Count == 0) return;

                DateTime now = DateTime.UtcNow;
                foreach (var kv in abandonOpenSince)
                {
                    int left = AbandonSecondsLeft(kv.Key, now);
                    // Ya superó el umbral: el reporte es inevitable, no tiene
                    // sentido seguir prometiéndole que puede evitarlo.
                    if (left <= 0) continue;

                    string name = abandonNames.GetValueOrDefault(kv.Key, kv.Key);
                    int minutes = (int)Math.Ceiling(left / 60.0);
                    PrintToAllChat($"{ChatColors.Green}{name}{ChatColors.Default} será sancionado si no vuelve antes de {ChatColors.Green}{minutes}{ChatColors.Default} minuto(s).");
                }

                AbandonWarnTick(generation);
            }

            if (first) Run();
            else AddTimer(abandonWarnIntervalCvar.Value, Run);
        }

        /// <summary>
        /// Cierra el intervalo abierto de un jugador y suma los segundos al
        /// acumulado del mapa actual. Idempotente: sin intervalo abierto no hace nada.
        /// </summary>
        public void AbandonTrackReconnect(CCSPlayerController player)
        {
            if (abandonThresholdCvar.Value <= 0) return;
            if (player.IsBot || player.IsHLTV) return;

            string steamId = player.SteamID.ToString();
            if (!abandonOpenSince.TryGetValue(steamId, out DateTime since)) return;

            abandonOpenSince.Remove(steamId);
            abandonNames.Remove(steamId);
            int seconds = (int)Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);
            abandonAccumulated[steamId] = abandonAccumulated.GetValueOrDefault(steamId) + seconds;

            Log($"[Abandon] {steamId} reconectó tras {seconds}s (acumulado en el mapa: {abandonAccumulated[steamId]}s).");
        }

        /// <summary>
        /// Cierra el mapa: liquida los intervalos que sigan abiertos, evalúa el
        /// acumulado contra el umbral y resetea el contador para el mapa siguiente.
        ///
        /// Cerrar los intervalos abiertos es indispensable: al que se va y NO
        /// vuelve nadie le cierra el intervalo, y es justamente el peor caso.
        ///
        /// Se llama en el hilo del juego al terminar el mapa, antes de cualquier
        /// reset de estado.
        /// </summary>
        public void AbandonFinalizeMap()
        {
            if (abandonThresholdCvar.Value <= 0) return;

            DateTime now = DateTime.UtcNow;
            foreach (var kv in abandonOpenSince)
            {
                int seconds = (int)Math.Max(0, (now - kv.Value).TotalSeconds);
                abandonAccumulated[kv.Key] = abandonAccumulated.GetValueOrDefault(kv.Key) + seconds;
                Log($"[Abandon] {kv.Key} nunca reconectó: se cierran {seconds}s contra el fin del mapa.");
            }
            abandonOpenSince.Clear();
            abandonNames.Clear();
            abandonWarnGeneration++; // corta los ticks del recordatorio

            int threshold = abandonThresholdCvar.Value;
            int mapNumber = matchConfig.CurrentMapNumber;
            string mapName = Server.MapName ?? "";

            foreach (var kv in abandonAccumulated)
            {
                if (kv.Value < threshold) continue;
                // Se conserva el PRIMER mapa donde superó el umbral: una falta
                // por serie, sin importar en cuántos mapas haya abandonado.
                if (abandonFlagged.ContainsKey(kv.Key)) continue;

                abandonFlagged[kv.Key] = (mapNumber, mapName, kv.Value);
                Log($"[Abandon] {kv.Key} marcado por abandono: {kv.Value}s >= {threshold}s (mapa {mapNumber} {mapName}).");
            }

            abandonAccumulated.Clear();
        }

        /// <summary>
        /// Arma el evento de la serie y limpia el estado. Devuelve null si no
        /// hubo abandonos, para no mandar un webhook vacío en cada partida.
        /// </summary>
        public MatchZyPlayersAbandonedEvent? BuildAbandonEvent(long matchId)
        {
            if (abandonFlagged.Count == 0)
            {
                AbandonReset();
                return null;
            }

            var players = abandonFlagged.Select(kv => new AbandonedPlayer
            {
                SteamId64 = kv.Key,
                MapNumber = kv.Value.MapNumber,
                MapName = kv.Value.MapName,
                DisconnectedSeconds = kv.Value.Seconds,
            }).ToList();

            AbandonReset();
            return new MatchZyPlayersAbandonedEvent { MatchId = matchId, Players = players };
        }

        /// <summary>Limpia todo el estado de abandono (fin de serie / reset de match).</summary>
        public void AbandonReset()
        {
            abandonAccumulated.Clear();
            abandonOpenSince.Clear();
            abandonNames.Clear();
            abandonFlagged.Clear();
            abandonWarnGeneration++;
        }

        public string GetPlayerMatchTeamName(CCSPlayerController player)
        {
            string steamId = player.SteamID.ToString();
            try
            {
                if (matchzyTeam1.teamPlayers != null && matchzyTeam1.teamPlayers[steamId] != null)
                    return "team1";
                if (matchzyTeam2.teamPlayers != null && matchzyTeam2.teamPlayers[steamId] != null)
                    return "team2";
            }
            catch { }
            return "";
        }

        /// <summary>Shows a countdown in chat and then calls HandleMatchStart() when it reaches zero.</summary>
        private void StartMatchCountdown()
        {
            if (isCountdownActive) return; // ya hay un countdown corriendo
            isCountdownActive = true;

            // IMPORTANTE: invocada típicamente desde dentro de un timer callback
            // (AddTimer en EventPlayerTeam handler). Crear timers, hacer
            // PrintToChatAll y disparar HTTP desde dentro de un timer callback
            // ha provocado segfaults del engine de CS2 (exit 139). Saltamos al
            // próximo frame del game loop para ejecutar en contexto limpio.
            Server.NextFrame(() =>
            {
                try
                {
                    Log("[StartMatchCountdown] entering NextFrame body");

                    // Apagar el sistema de espera AHORA (dentro de NextFrame, no
                    // antes), así evitamos matar un Timer.REPEAT desde el callback
                    // de otro timer — patrón que ya nos hizo crashear el engine.
                    // Se invalida la generación para que cualquier tick del reminder
                    // que ya esté en cola se descarte silenciosamente.
                    isWaitingForPlayers = false;
                    playerWaitGeneration++;
                    playerWaitTimeoutTimer?.Kill();
                    playerWaitTimeoutTimer = null;
                    playerWaitReminderTimer?.Kill();
                    playerWaitReminderTimer = null;
                    int expected = GetExpectedMatchPlayersCount();
                    Log($"[StartMatchCountdown] expected={expected}");

                    var allConnectedEvent = new MatchZyAllPlayersConnectedEvent
                    {
                        MatchId = liveMatchId,
                        ExpectedCount = expected,
                    };
                    Task.Run(async () => await SendEventAsync(allConnectedEvent));
                    Log("[StartMatchCountdown] webhook dispatched");

                    int totalSeconds = matchConfig.MatchStartCountdown > 0 ? matchConfig.MatchStartCountdown : 10;
                    Log($"[StartMatchCountdown] seconds={totalSeconds}, about to PrintToAllChat");
                    PrintToAllChat(Localizer["matchzy.match.allplayersconnected", totalSeconds]);

                    // Token de cancelación: si cambia mientras el countdown corre, todos los
                    // ticks pendientes se descartan silenciosamente. Esto reemplaza la lógica
                    // anterior de Timer.REPEAT + Kill() desde dentro del callback, que estaba
                    // corrompiendo memoria del engine y causando segfault.
                    int countdownId = ++countdownGeneration;
                    matchCountdownTimer = null; // ya no usamos un timer único
                    Log($"[StartMatchCountdown] scheduling self-rescheduling ticks (id={countdownId})");
                    ScheduleCountdownTick(totalSeconds, countdownId);
                }
                catch (Exception ex)
                {
                    Log($"[StartMatchCountdown FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        /// <summary>
        /// Encola UN tick one-shot del countdown. Cuando se ejecuta, decrementa
        /// y se reagenda a sí mismo (no usa Timer.REPEAT). Si la `countdownGeneration`
        /// cambió, el tick se descarta — esto es nuestro mecanismo de cancelación,
        /// y evita tener que llamar `Kill()` desde dentro del callback (que es lo
        /// que corrompía memoria y producía el segfault).
        /// </summary>
        private void ScheduleCountdownTick(int remaining, int countdownId)
        {
            AddTimer(1.0f, () =>
            {
                try
                {
                    // Cancelado por otro StartMatchCountdown / ResetMatch / KillPhaseTimers.
                    if (countdownId != countdownGeneration)
                    {
                        Log($"[CountdownTick] stale id={countdownId} (current={countdownGeneration}), discarding");
                        return;
                    }

                    int seconds = remaining - 1;
                    Log($"[CountdownTick] seconds={seconds} (id={countdownId})");

                    if (seconds > 0)
                    {
                        if (seconds <= 5)
                        {
                            PrintToAllChat(Localizer["matchzy.match.countdown", seconds]);
                        }
                        // Reagendar próximo tick.
                        ScheduleCountdownTick(seconds, countdownId);
                        return;
                    }

                    // seconds == 0 → fin del countdown. Invalidar la generación
                    // para que cualquier tick rezagado se descarte.
                    countdownGeneration++;
                    isCountdownActive = false;
                    isWaitingForPlayers = false;
                    PrintToAllChat(Localizer["matchzy.match.started"]);

                    // Diferir HandleMatchStart: dejarle al engine un frame
                    // limpio antes de cargar live.cfg / mp_restartgame.
                    AddTimer(0.5f, () =>
                    {
                        try { HandleMatchStart(); }
                        catch (Exception exH) { Log($"[HandleMatchStart FATAL] {exH.GetType().Name}: {exH.Message}\n{exH.StackTrace}"); }
                    });
                }
                catch (Exception exT)
                {
                    Log($"[CountdownTick FATAL] {exT.GetType().Name}: {exT.Message}\n{exT.StackTrace}");
                }
            });
        }

        public void DetermineKnifeWinner()
        {
            // Knife Round code referred from Get5, thanks to the Get5 team for their amazing job!
            (int tAlive, int tHealth) = GetAlivePlayers(2);
            (int ctAlive, int ctHealth) = GetAlivePlayers(3);
            Log($"[KNIFE OVER] CT Alive: {ctAlive} with Total Health: {ctHealth}, T Alive: {tAlive} with Total Health: {tHealth}");
            if (ctAlive > tAlive)
            {
                knifeWinner = 3;
            }
            else if (tAlive > ctAlive)
            {
                knifeWinner = 2;
            }
            else if (ctHealth > tHealth)
            {
                knifeWinner = 3;
            }
            else if (tHealth > ctHealth)
            {
                knifeWinner = 2;
            }
            else
            {
                // Choosing a winner randomly
                Random random = new();
                knifeWinner = random.Next(2, 4);
            }
        }

        private void HandleKnifeWinner(EventCsWinPanelRound @event)
        {
            DetermineKnifeWinner();
            // Below code is working partially (Winner audio plays correctly for knife winner team, but may display round winner incorrectly)
            // Hence we restart the game with StartAfterKnifeWarmup and allow the winning team to choose side

            @event.FunfactToken = "";

            // Commenting these assignments as they were crashing the server.
            // long empty = 0;
            // @event.FunfactPlayer = null;
            // @event.FunfactData1 = empty;
            // @event.FunfactData2 = empty;
            // @event.FunfactData3 = empty;
            int finalEvent = 10;
            if (knifeWinner == 3)
            {
                finalEvent = 8;
            }
            else if (knifeWinner == 2)
            {
                finalEvent = 9;
            }
            Log($"[KNIFE WINNER] Won by: {knifeWinner}, finalEvent: {@event.FinalEvent}, newFinalEvent: {finalEvent}");
            @event.FinalEvent = finalEvent;
        }

        private void HandleMapChangeCommand(CCSPlayerController? player, string mapName)
        {
            if (!IsPlayerAdmin(player, "css_map", "@css/map"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (matchStarted)
            {
                // ReplyToUserCommand(player, $"Map cannot be changed once the match is started!");
                ReplyToUserCommand(player, Localizer["matchzy.utility.matchstarted"]);
                return;
            }

            if (!long.TryParse(mapName, out _) && !mapName.Contains('_'))
            {
                mapName = "de_" + mapName;
            }

            StopTvForMapChange();
            if (long.TryParse(mapName, out _))
            { // Check if mapName is a long for workshop map ids
                Server.ExecuteCommand($"bot_kick");
                Server.ExecuteCommand($"host_workshop_map \"{mapName}\"");
            }
            else if (Server.IsMapValid(mapName))
            {
                Server.ExecuteCommand($"bot_kick");
                Server.ExecuteCommand($"changelevel \"{mapName}\"");
            }
            else
            {
                ReplyToUserCommand(player, $"Invalid map name!");
            }
        }

        private void HandleReadyRequiredCommand(CCSPlayerController? player, string commandArg)
        {
            if (!IsPlayerAdmin(player, "css_readyrequired", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }

            if (!string.IsNullOrWhiteSpace(commandArg))
            {
                if (int.TryParse(commandArg, out int readyRequired) && readyRequired >= 0 && readyRequired <= 32)
                {
                    minimumReadyRequired = readyRequired;
                    string minimumReadyRequiredFormatted = (player == null) ? $"{minimumReadyRequired}" : $"{ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}";
                    // ReplyToUserCommand(player, $"Minimum ready players required to start the match are now set to: {minimumReadyRequiredFormatted}");
                    ReplyToUserCommand(player, Localizer["matchzy.utility.minreadyplayers", minimumReadyRequiredFormatted]);
                    CheckLiveRequired();
                }
                else
                {
                    // ReplyToUserCommand(player, $"Invalid value for readyrequired. Please specify a valid non-negative number. Usage: !readyrequired <number_of_ready_players_required>");
                    ReplyToUserCommand(player, Localizer["matchzy.utility.rrinvalidvalue"]);
                }
            }
            else
            {
                string minimumReadyRequiredFormatted = (player == null) ? $"{minimumReadyRequired}" : $"{ChatColors.Green}{minimumReadyRequired}{ChatColors.Default}";
                // ReplyToUserCommand(player, $"Current Ready Required: {minimumReadyRequiredFormatted} .Usage: !readyrequired <number_of_ready_players_required>");
                ReplyToUserCommand(player, Localizer["matchzy.utility.currentreadyrequired", minimumReadyRequiredFormatted]);
            }
        }

        private void CheckLiveRequired()
        {
            if (!readyAvailable || matchStarted) return;

            // Todo: Implement a same ready system for both pug and match
            int countOfReadyPlayers = playerReadyStatus.Count(kv => kv.Value == true);
            bool liveRequired = false;
            if (isMatchSetup)
            {
                if (IsTeamsReady() && IsSpectatorsReady())
                {
                    // When match is loaded from a config, wait until ALL expected players are connected
                    int expected = GetExpectedMatchPlayersCount();
                    int connected = GetConnectedMatchPlayers();
                    Log($"[CheckLiveRequired] isMatchSetup=true, expected={expected}, connected={connected}");
                    if (expected > 0 && connected < expected)
                    {
                        // Still waiting — do not start yet
                        return;
                    }
                    // All connected (or no player list defined) → start countdown only when
                    // the match was loaded via URL. For other setup paths (file/manual) keep
                    // the legacy immediate start to avoid behavior changes.
                    if (matchLoadedFromUrl)
                    {
                        // Garantizar un mínimo de tiempo de warmup antes del countdown.
                        // Si pasamos a live demasiado rápido tras el map load, el engine
                        // de CS2 crashea con SIGSEGV en el segundo BeginMatch (mp_restartgame
                        // del live.cfg cae mientras la transición warmup→live aún no se
                        // estabilizó). Diferimos el countdown hasta cumplir el mínimo.
                        if (warmupStartedAt != DateTime.MinValue)
                        {
                            double elapsed = (DateTime.UtcNow - warmupStartedAt).TotalSeconds;
                            if (elapsed < MinWarmupSecondsBeforeLive)
                            {
                                if (isCountdownActive)
                                {
                                    Log($"[CheckLiveRequired] countdown ya activo, no reagendar.");
                                    return;
                                }
                                if (liveDeferralScheduled)
                                {
                                    // Ya hay un diferido en cola — no agendamos otro
                                    // ni spammeamos chat. El que está corriendo va
                                    // a re-llamar a CheckLiveRequired cuando expire.
                                    return;
                                }
                                float wait = (float)(MinWarmupSecondsBeforeLive - elapsed);
                                Log($"[CheckLiveRequired] Warmup solo lleva {elapsed:F1}s, difiriendo countdown {wait:F1}s más para evitar crash de engine.");
                                liveDeferralScheduled = true;
                                AddTimer(wait, () =>
                                {
                                    liveDeferralScheduled = false;
                                    try { CheckLiveRequired(); }
                                    catch (Exception ex) { Log($"[CheckLiveRequired-deferred FATAL] {ex.GetType().Name}: {ex.Message}"); }
                                });
                                return;
                            }
                        }
                        StartMatchCountdown();
                    }
                    else
                    {
                        HandleMatchStart();
                    }
                    return;
                }
            }
            else if (minimumReadyRequired == 0)
            {
                if (countOfReadyPlayers >= connectedPlayers && connectedPlayers > 0)
                {
                    liveRequired = true;
                }
            }
            else if (countOfReadyPlayers >= minimumReadyRequired)
            {
                liveRequired = true;
            }
            if (liveRequired)
            {
                HandleMatchStart();
            }
        }

        private void HandleMatchStart()
        {
            Log("[HandleMatchStart] ENTER");
            isPractice = false;
            isDryRun = false;
            if (isRoundRestorePending)
            {
                RestoreRoundBackup(null, pendingRestoreFileName);
                isRoundRestorePending = false;
                pendingRestoreFileName = "";
                return;
            }
            // If default names, we pick a player and use their name as their team name
            if (matchzyTeam1.teamName == "COUNTER-TERRORISTS")
            {
                // matchzyTeam1.teamName = teamName;
                teamSides[matchzyTeam1] = "CT";
                reverseTeamSides["CT"] = matchzyTeam1;
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 3)
                    {
                        matchzyTeam1.teamName = "team_" + RemoveSpecialCharacters(playerData[key].PlayerName.Replace(" ", "_"));
                        foreach (var coach in matchzyTeam1.coach)
                        {
                            coach.Clan = $"[{matchzyTeam1.teamName} COACH]";
                        }
                        break;
                    }
                }
                // Server.ExecuteCommand($"mp_teamname_1 {matchzyTeam1.teamName}");
            }

            if (matchzyTeam2.teamName == "TERRORISTS")
            {
                // matchzyTeam2.teamName = teamName;
                teamSides[matchzyTeam2] = "TERRORIST";
                reverseTeamSides["TERRORIST"] = matchzyTeam2;
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 2)
                    {
                        matchzyTeam2.teamName = "team_" + RemoveSpecialCharacters(playerData[key].PlayerName.Replace(" ", "_"));
                        foreach (var coach in matchzyTeam2.coach)
                        {
                            coach.Clan = $"[{matchzyTeam2.teamName} COACH]";
                        }
                        break;
                    }
                }
                // Server.ExecuteCommand($"mp_teamname_2 {matchzyTeam2.teamName}");
            }

            Log("[HandleMatchStart] setting team names");
            Server.ExecuteCommand($"mp_teamname_1 {reverseTeamSides["CT"].teamName}");
            Server.ExecuteCommand($"mp_teamname_2 {reverseTeamSides["TERRORIST"].teamName}");

            HandleClanTags();

            string seriesType = "BO" + matchConfig.NumMaps.ToString();
            if (isStatsDirectSaveEnabled)
            {
                EnsureDatabaseInitialized();
                Log("[HandleMatchStart] InitMatch in DB");
                liveMatchId = database.InitMatch(matchzyTeam1.teamName, matchzyTeam2.teamName, "-", isMatchSetup, liveMatchId, matchConfig.CurrentMapNumber, seriesType, matchConfig);
                Log($"[HandleMatchStart] InitMatch OK liveMatchId={liveMatchId}");
            }
            SetupRoundBackupFile();
            Log("[HandleMatchStart] SetupRoundBackupFile OK, SKIPPING GetSpawns (only needed for .spawn practice command)");
            // GetSpawns() solo se necesita para el comando .spawn N en
            // modo práctica. Llamarlo durante match-live causa interop
            // pesado con entidades nativas (CBodyComponent) justo cuando
            // el engine está procesando mp_restartgame y respawneando
            // jugadores — race condition que produce SIGSEGV.
            Log($"[HandleMatchStart] proceeding. isPreVeto={isPreVeto} isKnifeRequired={isKnifeRequired}");

            if (isPreVeto)
            {
                CreateVeto();
            }
            else if (isKnifeRequired)
            {
                StartKnifeRound();
            }
            else
            {
                // NOTA: NO llamar StartDemoRecording aquí. StartLive() ya lo
                // hace internamente. La doble llamada estaba dejando estado
                // inconsistente y contribuyendo al segfault al cargar live.cfg.
                Log("[HandleMatchStart] calling StartLive");
                StartLive();
                Log("[HandleMatchStart] StartLive returned");
            }
            if (showCreditsOnMatchStart.Value)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}MatchZy{ChatColors.Default} Plugin by {ChatColors.Green}WD-{ChatColors.Default}");
            }
            if (matchStartMessage.Value.Trim() != "" && matchStartMessage.Value.Trim() != "\"\"")
            {
                List<string> matchStartMessages = [.. matchStartMessage.Value.Split("$$$")];
                foreach (string message in matchStartMessages)
                {
                    PrintToAllChat(GetColorTreatedString(FormatCvarValue(message.Trim())));
                }
            }
        }

        public void HandleClanTags()
        {
            // Currently it is not possible to keep updating player tags while in warmup without restarting the match
            // Hence returning from here until we find a proper solution
            return;

            if (readyAvailable && !matchStarted)
            {
                foreach (var key in playerData.Keys)
                {
                    if (playerReadyStatus[key])
                    {
                        playerData[key].Clan = "[Ready]";
                    }
                    else
                    {
                        playerData[key].Clan = "[Unready]";
                    }
                    Server.PrintToChatAll($"PlayerName: {playerData[key].PlayerName} Clan: {playerData[key].Clan}");
                }
            }
            else if (matchStarted)
            {
                foreach (var key in playerData.Keys)
                {
                    if (playerData[key].TeamNum == 2)
                    {
                        playerData[key].Clan = reverseTeamSides["TERRORIST"].teamTag;
                    }
                    else if (playerData[key].TeamNum == 3)
                    {
                        playerData[key].Clan = reverseTeamSides["CT"].teamTag;
                    }
                    Server.PrintToChatAll($"PlayerName: {playerData[key].PlayerName} Clan: {playerData[key].Clan}");
                }
            }
        }

        private void HandleMatchEnd()
        {
            if (!isMatchLive) return;

            // This ensures that the mp_match_restart_delay is not shorter than what is required for the GOTV recording to finish.
            // Ref: Get5
            int restartDelay = ConVar.Find("mp_match_restart_delay")!.GetPrimitiveValue<int>();
            int tvDelay = GetTvDelay();
            int tvFlushDelay = tvDelay + 15;
            // El changelevel del siguiente mapa (BO3/BO5) se agenda a partir de
            // restartDelay y debe ocurrir con margen DESPUÉS del tv_stoprecord real
            // (agendado más abajo en tvFlushDelay - 0.5). Antes el +10 de colchón solo
            // se sumaba si tvDelay > 0; con tv_delay 0 (GOTV sin delay o tv_enable 0
            // con demo recording activo) restartDelay quedaba en ~15 y el changelevel
            // terminaba disparándose ANTES que el tv_stoprecord agendado, corriendo la
            // parada real de la grabación al mismo tick que el changelevel dentro de
            // ChangeMap()/StopTvForMapChange() — causa del crash intermitente del
            // engine al cambiar de mapa con la demo/GOTV todavía cerrando el archivo.
            int requiredDelay = tvFlushDelay + 10;
            if (requiredDelay > restartDelay)
            {
                Log($"Extended mp_match_restart_delay from {restartDelay} to {requiredDelay} to ensure GOTV broadcast can finish.");
                ConVar.Find("mp_match_restart_delay")!.SetValue(requiredDelay);
                restartDelay = requiredDelay;
            }
            int currentMapNumber = matchConfig.CurrentMapNumber;
            Log($"[HandleMatchEnd] MAP ENDED, isMatchSetup: {isMatchSetup} matchid: {liveMatchId} currentMapNumber: {currentMapNumber} tvFlushDelay: {tvFlushDelay}");

            StopDemoRecording(tvFlushDelay - 0.5f, activeDemoFile, liveMatchId, currentMapNumber);

            string winnerName = GetMatchWinnerName();
            (int t1score, int t2score) = GetTeamsScore();
            int team1SeriesScore = matchzyTeam1.seriesScore;
            int team2SeriesScore = matchzyTeam2.seriesScore;

            string statsPath = Server.GameDirectory + "/csgo/MatchZy_Stats/" + liveMatchId.ToString();
            string statsBackupRoot = GetStatsBackupRoot();

            // Capturados en el hilo del juego: liveMatchId puede cambiar (ResetMatch)
            // antes de que el Task.Run de abajo termine de correr en background.
            long matchId = liveMatchId;
            Task? pendingRoundTask = _lastRoundStatsTask;
            int expectedPlayers = _lastRoundExpectedPlayerCount;

            // Cierra el acumulado de desconexión de ESTE mapa antes de que se
            // resetee nada. Los que superaron el umbral quedan marcados y se
            // reportan recién en series_end (ver AbandonFinalizeMap).
            AbandonFinalizeMap();

            // Ganador del MAPA para el evento map_result. Sin overtime el mapa
            // puede terminar empatado: en ese caso winner viaja vacio (""/"")
            // y el backend lo registra como empate, en vez del team2 espurio
            // que producia el ternario anterior (t1 > t2 ? team1 : team2).
            Team? mapWinnerTeam = t1score > t2score ? matchzyTeam1 : t2score > t1score ? matchzyTeam2 : null;
            Winner mapWinner = mapWinnerTeam == null
                ? new Winner("", "")
                : new Winner(reverseTeamSides["CT"] == mapWinnerTeam ? "3" : "2", mapWinnerTeam == matchzyTeam1 ? "team1" : "team2");

            var mapResultEvent = new MapResultEvent
            {
                MatchId = liveMatchId,
                MapNumber = currentMapNumber,
                Winner = mapWinner,
                StatsTeam1 = new MatchZyStatsTeam(matchzyTeam1.id, matchzyTeam1.teamName, team1SeriesScore, t1score, 0, 0, new List<StatsPlayer>()),
                StatsTeam2 = new MatchZyStatsTeam(matchzyTeam2.id, matchzyTeam2.teamName, team2SeriesScore, t2score, 0, 0, new List<StatsPlayer>())
            };

            Task.Run(async () =>
            {
                // Arreglo de la causa raiz de la race condition documentada en
                // DatabaseStats.cs (_dbLock): esperar a que la escritura en BD de la
                // ULTIMA ronda haya terminado (con exito o falla) antes de leer/
                // regenerar el CSV, en vez de dejar que dos Task.Run independientes
                // compitan por el orden de ejecucion.
                if (pendingRoundTask != null)
                {
                    try
                    {
                        await pendingRoundTask;
                    }
                    catch (Exception e)
                    {
                        Log($"[HandleMatchEnd] La tarea de guardado de la ultima ronda fallo: {e.Message}");
                    }
                }

                await SendEventAsync(mapResultEvent);

                if (isStatsDirectSaveEnabled)
                {
                    EnsureDatabaseInitialized();
                    await database.SetMapEndData(matchId, currentMapNumber, winnerName, t1score, t2score, team1SeriesScore, team2SeriesScore);
                    await database.WritePlayerStatsToCsv(statsPath, matchId, currentMapNumber);

                    // Red de seguridad de segundo nivel: si a pesar de lo anterior las
                    // filas de matchzy_stats_players no quedaron completas (ej. la BD
                    // estuvo caida durante la ronda final), reintentar automaticamente
                    // desde el backup en disco, sin intervencion manual.
                    if (expectedPlayers > 0)
                    {
                        int actualRows = await database.CountPlayerStatsRows(matchId, currentMapNumber);
                        if (actualRows < expectedPlayers)
                        {
                            Log($"[HandleMatchEnd] Mismatch de stats para matchId {matchId} mapNumber {currentMapNumber}: esperados {expectedPlayers}, encontrados {actualRows}. Reintentando automaticamente...");
                            bool recovered = false;
                            for (int attempt = 1; attempt <= 3 && !recovered; attempt++)
                            {
                                await Task.Delay(attempt * 1000);
                                (bool success, _, string message) = await RetryStatsFromBackup(matchId, currentMapNumber, statsBackupRoot, statsPath);
                                actualRows = await database.CountPlayerStatsRows(matchId, currentMapNumber);
                                recovered = success && actualRows >= expectedPlayers;
                                if (!success)
                                {
                                    Log($"[HandleMatchEnd] Intento {attempt} de auto-reintento fallo para matchId {matchId} mapNumber {currentMapNumber}: {message}");
                                }
                            }

                            if (recovered)
                            {
                                Log($"[HandleMatchEnd] Auto-reintento exitoso para matchId {matchId} mapNumber {currentMapNumber} ({actualRows} filas).");
                            }
                            else
                            {
                                Log($"[HandleMatchEnd FATAL] Auto-reintento agotado para matchId {matchId} mapNumber {currentMapNumber} ({actualRows}/{expectedPlayers} filas). Ejecutar manualmente: matchzy_retry_stats {matchId} {currentMapNumber}");
                            }
                        }
                    }
                }
            });

            // If a match is not setup, it was supposed to be a pug/scrim with 1 map
            // Hence we reset the match once it is over
            // Todo: Support BO3/BO5 in pugs as well
            if (!isMatchSetup)
            {
                // null = empate (EndSeries lo anuncia como tie y el guardado lo
                // registra como "Draw"); pasar el string "Draw" como winnerName
                // haria que se anuncie "Draw has won the match".
                EndSeries(t1score == t2score ? null : winnerName, restartDelay - 1, t1score, t2score);
                return;
            }

            // Mapas jugados = CurrentMapNumber + 1 (0-based). NO derivarlo de la
            // suma de series scores: un mapa empatado (posible sin overtime) no
            // le suma el punto de serie a nadie, y con esa cuenta el empate
            // "no consumia" mapa - remainingMaps nunca llegaba a 0, ninguna
            // rama llamaba a EndSeries y el flujo seguia hacia
            // Maplist[CurrentMapNumber + 1], que en un BO1 no existe (throw
            // dentro del handler de win panel): la serie no terminaba nunca,
            // no se emitian series_end / demo_window_end y el lobby quedaba
            // colgado. Ver GetMatchWinnerName(): en empate no incrementa.
            int remainingMaps = matchConfig.NumMaps - (currentMapNumber + 1);
            Log($"[HandleMatchEnd] MATCH ENDED, remainingMaps: {remainingMaps}, NumMaps: {matchConfig.NumMaps}, Team1SeriesScore: {matchzyTeam1.seriesScore}, Team2SeriesScore: {matchzyTeam2.seriesScore}");

            // Ganador de la SERIE. No reutilizar winnerName (ganador del ultimo
            // MAPA): en una serie sin clinch el ultimo mapa puede ganarlo el
            // perdedor de la serie, y con empates puede ser "Draw" aunque la
            // serie tenga lider. null = serie empatada.
            string? seriesWinnerName = matchzyTeam1.seriesScore > matchzyTeam2.seriesScore
                ? matchzyTeam1.teamName
                : matchzyTeam2.seriesScore > matchzyTeam1.seriesScore ? matchzyTeam2.teamName : null;

            int mapsToWinSeries = (matchConfig.NumMaps / 2) + 1;
            bool seriesClinched = matchConfig.SeriesCanClinch &&
                (matchzyTeam1.seriesScore >= mapsToWinSeries || matchzyTeam2.seriesScore >= mapsToWinSeries);

            if (seriesClinched || remainingMaps <= 0)
            {
                EndSeries(seriesWinnerName, restartDelay - 1, t1score, t2score);
                return;
            }
            if (matchzyTeam1.seriesScore > matchzyTeam2.seriesScore)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{matchzyTeam1.teamName}{ChatColors.Default} está ganando la serie {ChatColors.Green}{matchzyTeam1.seriesScore}-{matchzyTeam2.seriesScore}{ChatColors.Default}");

            }
            else if (matchzyTeam2.seriesScore > matchzyTeam1.seriesScore)
            {
                Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{matchzyTeam2.teamName}{ChatColors.Default} está ganando la serie {ChatColors.Green}{matchzyTeam2.seriesScore}-{matchzyTeam1.seriesScore}{ChatColors.Default}");

            }
            else
            {
                Server.PrintToChatAll($"{chatPrefix} la serie está empatada en {ChatColors.Green}{matchzyTeam1.seriesScore}-{matchzyTeam2.seriesScore}{ChatColors.Default}");
            }
            matchConfig.CurrentMapNumber += 1;
            string nextMap = matchConfig.Maplist[matchConfig.CurrentMapNumber];

            if (isPaused)
                UnpauseMatch();

            stopData["ct"] = false;
            stopData["t"] = false;

            KillPhaseTimers();

            // CRÍTICO: Desactivar mp_match_end_restart ANTES del changelevel BO3/BO5.
            // StartLive() lo activa con "mp_match_end_restart true" para que el
            // engine recargue el mapa al terminar una partida. En BO1 eso es
            // inofensivo porque EndSeries/ResetMatch no emite ningún changelevel.
            // En BO3/BO5, sin embargo, el engine intenta recargar el mapa actual
            // (~1 segundo DESPUÉS de que el plugin ya ejecutó "changelevel mapa2"),
            // produciendo dos changelevel simultáneos que corrompen el estado del
            // engine y provocan el crash del servidor (SIGSEGV / exit 139).
            Server.ExecuteCommand("mp_match_end_restart false");

            AddTimer(restartDelay - 4, () =>
            {
                if (!isMatchSetup) return;

                // Resetear estado ANTES del changelevel para que los timers del
                // warmup que arranquen en OnMapStart vean el estado correcto.
                matchStarted = false;
                readyAvailable = true;
                isPaused = false;
                isWarmup = true;
                isKnifeRound = false;
                isSideSelectionPhase = false;
                isMatchLive = false;
                isPractice = false;
                isDryRun = false;
                isWaitingForPlayers = false;

                SetMapSides();

                // NO llamar StartWarmup() aquí: haría exec warmup.cfg sobre el
                // mapa antiguo (incorrecto) y crearía un TimerFlags.REPEAT que
                // disparía durante el changelevel, accediendo a entidades de
                // jugadores ya invalidadas → crash. OnMapStart ya llama
                // StartWarmup() cuando isWarmup == true.

                ChangeMap(nextMap, 3.0f);

                // BO3/BO5: el watchdog de espera de jugadores se reinicia
                // desde `OnMapStart` del mapa siguiente (junto al evento
                // `map_warmup_started`). De esa forma el timeout empieza a
                // contar cuando el mapa ya cargó de verdad y no durante el
                // `changelevel`, que tarda 15-30 s y consumía parte del
                // tiempo de espera real.
            });
        }

        private void ChangeMap(string mapName, float delay)
        {
            Log($"[ChangeMap] Changing map to {mapName} with delay {delay}");
            AddTimer(delay, () =>
            {
                StopTvForMapChange();
                if (long.TryParse(mapName, out _))
                {
                    Server.ExecuteCommand($"bot_kick");
                    Server.ExecuteCommand($"host_workshop_map \"{mapName}\"");
                }
                else if (Server.IsMapValid(mapName))
                {
                    Server.ExecuteCommand($"bot_kick");
                    Server.ExecuteCommand($"changelevel \"{mapName}\"");
                }
            });
        }

        private string GetMatchWinnerName()
        {
            (int t1score, int t2score) = GetTeamsScore();
            if (t1score > t2score)
            {
                matchzyTeam1.seriesScore++;
                return matchzyTeam1.teamName;
            }
            else if (t2score > t1score)
            {
                matchzyTeam2.seriesScore++;
                return matchzyTeam2.teamName;
            }
            else
            {
                return "Draw";
            }
        }

        private (int t1score, int t2score) GetTeamsScore()
        {
            var teamEntities = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
            int t1score = 0;
            int t2score = 0;
            foreach (var team in teamEntities)
            {
                if (team.Teamname == teamSides[matchzyTeam1])
                {
                    t1score = team.Score;
                }
                else if (team.Teamname == teamSides[matchzyTeam2])
                {
                    t2score = team.Score;
                }
            }
            return (t1score, t2score);
        }

        private int GetRoundNumer()
        {
            (int t1score, int t2score) = GetTeamsScore();

            return t1score + t2score;
        }

        public void HandlePostRoundStartEvent(EventRoundStart @event)
        {
            if (isDryRun) RandomizeSpawns();
            if (!matchStarted) return;
            playerHasTakenDamage = false;
            HandleCoaches();
            CreateMatchZyRoundDataBackup();
            InitPlayerDamageInfo();
            UpdateHostname();
            if (isMatchLive)
            {
                ResetPerRoundKastState();
                // Fallback para el round_time de los duelos; EventRoundFreezeEnd
                // lo sobreescribe con el instante real en que la ronda pasa a live.
                currentRoundLiveStartUtc = DateTime.UtcNow;
            }
        }

        private void HandlePostRoundEndEvent(EventRoundEnd @event)
        {
            try
            {
                if (isMatchLive)
                {
                    coachKillTimer?.Kill();
                    coachKillTimer = null;
                    (int t1score, int t2score) = GetTeamsScore();
                    Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{matchzyTeam1.teamName} [{t1score} - {t2score}] {matchzyTeam2.teamName}");

                    ShowDamageInfo();

                    FinalizeKastForRound();
                    // Cierra los clutches abiertos con el ganador de la ronda.
                    // ANTES de GetPlayerStatsDict: es de ahi que salen al payload.
                    FinalizeClutchesForRound(@event.Winner);
                    (Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary, List<StatsPlayer> playerStatsListTeam1, List<StatsPlayer> playerStatsListTeam2) = GetPlayerStatsDict();

                    int currentMapNumber = matchConfig.CurrentMapNumber;
                    long matchId = liveMatchId;
                    int roundNumber = GetRoundNumer();
                    List<MatchZyDuel> roundDuels = FlushCurrentRoundDuels();
                    int ctTeamNum = reverseTeamSides["CT"] == matchzyTeam1 ? 1 : 2;
                    int tTeamNum = reverseTeamSides["TERRORIST"] == matchzyTeam1 ? 1 : 2;
                    string winnerSide = @event.Winner switch
                    {
                        (int)CsTeam.CounterTerrorist => "CT",
                        (int)CsTeam.Terrorist => "TERRORIST",
                        _ => "",
                    };
                    // El equipo ganador sale del lado que gano la ronda, no del
                    // marcador acumulado (reverseTeamSides ya refleja el swap de
                    // mitades).
                    string winnerTeam = winnerSide == ""
                        ? ""
                        : reverseTeamSides[winnerSide] == matchzyTeam1 ? "team1" : "team2";
                    Winner winner = new(@event.Winner.ToString(), winnerTeam);

                    // Cabecera de la ronda (matchzy_stats_rounds + campos de
                    // nivel superior del webhook round_end). La duracion usa la
                    // misma base que el round_time de los duelos (freeze end).
                    int roundDuration = Math.Max(0, (int)(DateTime.UtcNow - currentRoundLiveStartUtc).TotalSeconds);
                    MatchZyRoundHeader roundHeader = new()
                    {
                        RoundNumber = roundNumber,
                        Reason = @event.Reason,
                        ReasonName = GetRoundEndReasonName(@event.Reason),
                        WinnerSide = winnerSide,
                        WinnerTeam = winner.Team,
                        WinnerTeamName = winner.Team == "team1" ? matchzyTeam1.teamName : winner.Team == "team2" ? matchzyTeam2.teamName : "",
                        RoundDuration = roundDuration,
                        Team1Score = t1score,
                        Team2Score = t2score,
                        BombPlanted = currentRoundBombPlanted,
                        BombSite = currentRoundBombSite,
                    };

                    var roundEndEvent = new MatchZyRoundEndedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber,
                        RoundNumber = roundNumber,
                        Reason = @event.Reason,
                        ReasonName = roundHeader.ReasonName,
                        RoundTime = roundDuration,
                        Winner = winner,
                        WinnerSide = roundHeader.WinnerSide,
                        WinnerTeamName = roundHeader.WinnerTeamName,
                        BombPlanted = roundHeader.BombPlanted,
                        BombSite = roundHeader.BombSite,
                        StatsTeam1 = new MatchZyStatsTeam(matchzyTeam1.id, matchzyTeam1.teamName, 0, t1score, 0, 0, playerStatsListTeam1),
                        StatsTeam2 = new MatchZyStatsTeam(matchzyTeam2.id, matchzyTeam2.teamName, 0, t2score, 0, 0, playerStatsListTeam2),
                        Duels = roundDuels,
                    };

                    // Sincrono y antes del Task.Run de abajo: debe sobrevivir aunque el
                    // envio del webhook o la escritura a la BD fallen. A diferencia del
                    // backup de restauracion (CreateMatchZyRoundDataBackup, disparado en
                    // RoundStart), este se dispara en RoundEnd y por eso si cubre la
                    // ultima ronda jugada. Ver StatsBackup.cs. No tiene sentido respaldar
                    // hacia una BD local que no se esta usando.
                    if (isStatsDirectSaveEnabled)
                    {
                        CreateRoundStatsBackup(roundEndEvent, playerStatsDictionary);
                    }

                    _lastRoundExpectedPlayerCount = playerStatsListTeam1.Count + playerStatsListTeam2.Count;
                    _lastRoundStatsTask = Task.Run(async () =>
                    {
                        await SendEventAsync(roundEndEvent);
                        if (isStatsDirectSaveEnabled)
                        {
                            EnsureDatabaseInitialized();
                            await database.UpdatePlayerStatsAsync(matchId, currentMapNumber, playerStatsDictionary);
                            await database.UpdateMapStatsAsync(matchId, currentMapNumber, t1score, t2score);
                            // La cabecera va primero: los duelos la referencian
                            // via la FK compuesta (si el upsert fallo se
                            // omiten, la FK los rechazaria de todas formas).
                            bool roundHeaderSaved = await database.UpsertRoundAsync(matchId, currentMapNumber, roundHeader);
                            if (roundHeaderSaved)
                                await database.InsertDuelsAsync(matchId, currentMapNumber, roundNumber, roundDuels);
                        }
                    });

                    string round = GetRoundNumer().ToString("D2");
                    lastBackupFileName = $"matchzy_{liveMatchId}_{matchConfig.CurrentMapNumber}_round{round}.txt";
                    lastMatchZyBackupFileName = $"matchzy_{liveMatchId}_{matchConfig.CurrentMapNumber}_round{round}.json";
                    Log($"[HandlePostRoundEndEvent] Setting lastBackupFileName to {lastBackupFileName} and lastMatchZyBackupFileName to {lastMatchZyBackupFileName}");

                    // One of the team did not use .stop command hence display the proper message after the round has ended.
                    if (stopData["ct"] && !stopData["t"])
                    {
                        Server.PrintToChatAll($"{chatPrefix} The round restore request by {ChatColors.Green}{reverseTeamSides["CT"].teamName}{ChatColors.Default} was cancelled as the round ended");
                    }
                    else if (!stopData["ct"] && stopData["t"])
                    {
                        Server.PrintToChatAll($"{chatPrefix} The round restore request by {ChatColors.Green}{reverseTeamSides["TERRORIST"].teamName}{ChatColors.Default} was cancelled as the round ended");
                    }

                    // Invalidate .stop requests after a round is completed.
                    stopData["ct"] = false;
                    stopData["t"] = false;

                    bool swapRequired = IsTeamSwapRequired();

                    // If isRoundRestoring is true, sides will be swapped from round restore if required!
                    if (swapRequired && !isRoundRestoring)
                    {
                        SwapSidesInTeamData(false);
                    }

                    isRoundRestoring = false;
                }
            }
            catch (Exception e)
            {
                Log($"[HandlePostRoundEndEvent FATAL] An error occurred: {e.Message}");
            }
        }

        public bool IsTeamSwapRequired()
        {
            // Handling OTs and side swaps (Referred from Get5)
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
            int roundsPlayed = gameRules.TotalRoundsPlayed;

            int roundsPerHalf = ConVar.Find("mp_maxrounds")!.GetPrimitiveValue<int>() / 2;
            int roundsPerOTHalf = ConVar.Find("mp_overtime_maxrounds")!.GetPrimitiveValue<int>() / 2;

            bool halftimeEnabled = ConVar.Find("mp_halftime")!.GetPrimitiveValue<bool>();

            if (halftimeEnabled)
            {
                if (roundsPlayed == roundsPerHalf)
                {
                    return true;
                }
                // Now in OT.
                if (roundsPlayed >= 2 * roundsPerHalf)
                {
                    int otround = roundsPlayed - 2 * roundsPerHalf;  // round 33 -> round 3, etc.
                    // Do side swaps at OT halves (rounds 3, 9, ...)
                    if ((otround + roundsPerOTHalf) % (2 * roundsPerOTHalf) == 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void PauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (isMatchLive && isPaused)
            {
                // ReplyToUserCommand(player, "Match is already paused!");
                ReplyToUserCommand(player, Localizer["matchzy.utility.paused"]);
                return;
            }
            if (IsHalfTimePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command during halftime.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.duringhalftime"]);
                return;
            }
            if (IsPostGamePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command after the game has ended.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.matchended"]);
                return;
            }
            if (IsTacticalTimeoutActive())
            {
                // ReplyToUserCommand(player, "You cannot use this command when tactical timeout is active.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.tacticaltimeout"]);
                return;
            }
            if (!techPauseEnabled.Value && player != null)
            {
                PrintToPlayerChat(player, Localizer["matchzy.pause.techpausenotenabled"]);
                return;
            }
            // Strip surrounding quotes that CSS may inject when config.cfg has: matchzy_tech_pause_flag ""
            string techPauseFlag = techPausePermission.Value.Trim().Trim('"').Trim();
            if (!string.IsNullOrEmpty(techPauseFlag))
            {
                if (!IsPlayerAdmin(player, "css_pause", techPauseFlag))
                {
                    SendPlayerNotAdminMessage(player);
                    return;
                }
            }
            if (isMatchLive && !isPaused)
            {

                string pauseTeamName = "Admin";
                unpauseData["pauseTeam"] = "Admin";
                if (player?.TeamNum == 2)
                {

                    pauseTeamName = reverseTeamSides["TERRORIST"].teamName;
                    unpauseData["pauseTeam"] = reverseTeamSides["TERRORIST"].teamName;
                }
                else if (player?.TeamNum == 3)
                {
                    pauseTeamName = reverseTeamSides["CT"].teamName;
                    unpauseData["pauseTeam"] = reverseTeamSides["CT"].teamName;
                }
                else
                {
                    return;
                }
                PrintToAllChat(Localizer["matchzy.pause.pausedthematch", pauseTeamName]);
                // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}{pauseTeamName}{ChatColors.Default} has paused the match. Type .unpause to unpause the match");

                SetMatchPausedFlags();
            }
        }

        private void ForcePauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (!matchStarted) return;
            if (!IsPlayerAdmin(player, "css_forcepause", "@css/config"))
            {
                SendPlayerNotAdminMessage(player);
                return;
            }
            if (isMatchLive && isPaused)
            {
                // ReplyToUserCommand(player, "Match is already paused!");
                ReplyToUserCommand(player, Localizer["matchzy.utility.paused"]);
                return;
            }
            if (IsHalfTimePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command during halftime.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.duringhalftime"]);
                return;
            }
            if (IsPostGamePhase())
            {
                // ReplyToUserCommand(player, "You cannot use this command after the game has ended.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.matchended"]);
                return;
            }
            if (IsTacticalTimeoutActive())
            {
                // ReplyToUserCommand(player, "You cannot use this command when tactical timeout is active.");
                ReplyToUserCommand(player, Localizer["matchzy.utility.tacticaltimeout"]);
                return;
            }
            unpauseData["pauseTeam"] = "Admin";
            PrintToAllChat(Localizer["matchzy.pause.adminpausedthematch"]);
            // Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}Admin{ChatColors.Default} has paused the match.");
            if (player == null)
            {
                Server.PrintToConsole($"[MatchZy] {Localizer["matchzy.pause.adminpausedthematch"]}");
            }
            SetMatchPausedFlags();
        }

        private void ForceUnpauseMatch(CCSPlayerController? player, CommandInfo? command)
        {
            if (matchStarted && isPaused)
            {
                if (!IsPlayerAdmin(player, "css_forceunpause", "@css/config"))
                {
                    SendPlayerNotAdminMessage(player);
                    return;
                }
                PrintToAllChat(Localizer["matchzy.pause.adminunpausedthematch"]);
                UnpauseMatch();

                if (player == null)
                {
                    Server.PrintToConsole("[MatchZy] Admin has unpaused the match, resuming the match!");
                }
            }
        }

        private void UnpauseMatch()
        {
            Server.ExecuteCommand("mp_unpause_match;");
            isPaused = false;
            unpauseData["ct"] = false;
            unpauseData["t"] = false;
            if (!isPaused && pausedStateTimer != null)
            {
                pausedStateTimer.Kill();
                pausedStateTimer = null;
            }
        }

        private void SetMatchPausedFlags()
        {
            coachKillTimer?.Kill();
            coachKillTimer = null;

            Server.ExecuteCommand("mp_pause_match;");
            isPaused = true;

            pausedStateTimer ??= AddTimer(chatTimerDelay, SendPausedStateMessage, TimerFlags.REPEAT);
        }

        private void StartMatchMode()
        {
            if (matchStarted || (!isPractice && !isSleep)) return;
            ExecUnpracCommands();
            ResetMatch();
            RemoveSpawnBeams();
            Server.PrintToChatAll($"{chatPrefix} Match mode loaded!");
        }

        private void ExecLiveCFG()
        {
            int gameMode = GetGameMode();

            var cfgPath = liveCfgPath;
            var absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", liveCfgPath);

            if (gameMode == 2)
            {
                absolutePath = Path.Join(Server.GameDirectory + "/csgo/cfg", liveWingmanCfgPath);
                cfgPath = liveWingmanCfgPath;
            }

            // We try to find the CFG in the cfg folder, if it is not there then we execute the default CFG.
            if (File.Exists(absolutePath))
            {
                Log($"[StartLive] Starting Live! Executing Live CFG from {cfgPath}");
                Server.ExecuteCommand($"exec {cfgPath}");
                // CRÍTICO: ambos comandos deben ir encolados juntos en el
                // mismo ExecuteCommand() y SIN delay, igual que upstream.
                // Si se separan o se difieren, el `mp_warmup_end` interno
                // del live.cfg dispara BeginMatch antes de que mp_restartgame
                // limpie el estado, y el engine crashea con SIGSEGV en
                // SteamAPI durante BeginMatch.
                Server.ExecuteCommand("mp_restartgame 1;mp_warmup_end;");
            }
            else
            {
                Log($"[StartLive] Starting Live! Live CFG not found in {absolutePath}, using default CFG!");
                if (gameMode == 2)
                {
                    Server.ExecuteCommand("ammo_grenade_limit_default 1;ammo_grenade_limit_flashbang 2;ammo_grenade_limit_total 4;bot_quota 0;cash_player_bomb_defused 300;cash_player_bomb_planted 300;cash_player_damage_hostage -30;cash_player_interact_with_hostage 300;cash_player_killed_enemy_default 300;cash_player_killed_enemy_factor 1;cash_player_killed_hostage -1000;cash_player_killed_teammate -300;cash_player_rescued_hostage 1000;cash_team_bonus_shorthanded 1000;cash_team_elimination_bomb_map 2750;cash_team_elimination_hostage_map_ct 2500;cash_team_elimination_hostage_map_t 2500;cash_team_hostage_alive 0;cash_team_hostage_interaction 600;cash_team_loser_bonus 2000;cash_team_loser_bonus_consecutive_rounds 300;cash_team_planted_bomb_but_defused 600;cash_team_rescued_hostage 600;cash_team_terrorist_win_bomb 3000;cash_team_win_by_defusing_bomb 3000;cash_team_win_by_hostage_rescue 2900;cash_team_win_by_time_running_out_bomb 2750;cash_team_win_by_time_running_out_hostage 2750;ff_damage_reduction_bullets 0.33;ff_damage_reduction_grenade 0.85;ff_damage_reduction_grenade_self 1;ff_damage_reduction_other 0.4;mp_afterroundmoney 0;mp_autokick 0;mp_autoteambalance 0;mp_backup_restore_load_autopause 0;mp_backup_round_auto 1;mp_buy_anywhere 0;mp_buy_during_immunity 0;mp_buytime 20;mp_c4timer 40;mp_ct_default_melee weapon_knife;mp_ct_default_primary \"\";mp_ct_default_secondary weapon_hkp2000;mp_death_drop_defuser 1;mp_death_drop_grenade 2;mp_death_drop_gun 1;mp_defuser_allocation 0;mp_display_kill_assists 1;mp_endmatch_votenextmap 0;mp_forcecamera 1;mp_free_armor 0;mp_freezetime 10;mp_friendlyfire 1;mp_give_player_c4 1;mp_halftime 1;mp_halftime_duration 15;mp_halftime_pausetimer 0;mp_ignore_round_win_conditions 0;mp_limitteams 0;mp_match_can_clinch 1;mp_match_end_restart 1;mp_maxmoney 8000;");
                    Server.ExecuteCommand("mp_maxrounds 16;mp_overtime_enable 1;mp_overtime_halftime_pausetimer 0;mp_overtime_maxrounds 4;mp_overtime_startmoney 8000;mp_playercashawards 1;mp_randomspawn 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_round_restart_delay 7;mp_roundtime 1.5;mp_roundtime_defuse 1.5;mp_roundtime_hostage 1.5;mp_solid_teammates 1;mp_starting_losses 1;mp_startmoney 800;mp_t_default_melee weapon_knife;mp_t_default_primary \"\";mp_t_default_secondary weapon_glock;mp_teamcashawards 1;mp_timelimit 0;mp_weapons_allow_map_placed 1;mp_weapons_allow_zeus 1;mp_win_panel_display_time 3;spec_freeze_deathanim_time 0;spec_freeze_time 2;spec_freeze_time_lock 2;spec_replay_enable 0;sv_allow_votes 0;sv_auto_full_alltalk_during_warmup_half_end 0;sv_damage_print_enable 0;sv_deadtalk 1;sv_hibernate_postgame_delay 300;sv_ignoregrenaderadio 0;sv_infinite_ammo 0;sv_talk_enemy_dead 0;sv_talk_enemy_living 0;sv_voiceenable 1;tv_relayvoice 0");
                }
                else
                {
                    Server.ExecuteCommand("ammo_grenade_limit_default 1;ammo_grenade_limit_flashbang 2;ammo_grenade_limit_total 4;bot_quota 0;cash_player_bomb_defused 300;cash_player_bomb_planted 300;cash_player_damage_hostage -30;cash_player_interact_with_hostage 300;cash_player_killed_enemy_default 300;cash_player_killed_enemy_factor 1;cash_player_killed_hostage -1000;cash_player_killed_teammate -300;cash_player_rescued_hostage 1000;cash_team_elimination_bomb_map 3250;cash_team_elimination_hostage_map_ct 3000;cash_team_elimination_hostage_map_t 3000;cash_team_hostage_alive 0;cash_team_hostage_interaction 600;cash_team_loser_bonus 1400;cash_team_loser_bonus_consecutive_rounds 500;cash_team_planted_bomb_but_defused 600;cash_team_rescued_hostage 600;cash_team_terrorist_win_bomb 3500;cash_team_win_by_defusing_bomb 3500;");
                    Server.ExecuteCommand("cash_team_win_by_hostage_rescue 2900;cash_team_win_by_time_running_out_bomb 3250;cash_team_win_by_time_running_out_hostage 3250;ff_damage_reduction_bullets 0.33;ff_damage_reduction_grenade 0.85;ff_damage_reduction_grenade_self 1;ff_damage_reduction_other 0.4;mp_afterroundmoney 0;mp_autokick 0;mp_autoteambalance 0;mp_backup_restore_load_autopause 1;mp_backup_round_auto 1;mp_buy_anywhere 0;mp_buy_during_immunity 0;mp_buytime 20;mp_c4timer 40;mp_ct_default_melee weapon_knife;mp_ct_default_primary \"\";mp_ct_default_secondary weapon_hkp2000;mp_death_drop_defuser 1;mp_death_drop_grenade 2;mp_death_drop_gun 1;mp_defuser_allocation 0;mp_display_kill_assists 1;mp_endmatch_votenextmap 0;mp_forcecamera 1;mp_free_armor 0;mp_freezetime 18;mp_friendlyfire 1;mp_give_player_c4 1;mp_halftime 1;mp_halftime_duration 15;mp_halftime_pausetimer 0;mp_ignore_round_win_conditions 0;mp_limitteams 0;mp_match_can_clinch 1;mp_match_end_restart 0;mp_maxmoney 16000;mp_maxrounds 24;mp_overtime_enable 1;mp_overtime_halftime_pausetimer 0;mp_overtime_maxrounds 6;mp_overtime_startmoney 10000;mp_playercashawards 1;mp_randomspawn 0;mp_respawn_immunitytime 0;mp_respawn_on_death_ct 0;mp_respawn_on_death_t 0;mp_round_restart_delay 5;mp_roundtime 1.92;mp_roundtime_defuse 1.92;mp_roundtime_hostage 1.92;mp_solid_teammates 1;mp_starting_losses 1;mp_startmoney 800;mp_t_default_melee weapon_knife;mp_t_default_primary \"\";mp_t_default_secondary weapon_glock;mp_teamcashawards 1;mp_timelimit 0;mp_weapons_allow_map_placed 1;mp_weapons_allow_zeus 1;mp_win_panel_display_time 3;spec_freeze_deathanim_time 0;spec_freeze_time 2;spec_freeze_time_lock 2;spec_replay_enable 0;sv_allow_votes 1;sv_auto_full_alltalk_during_warmup_half_end 0;sv_damage_print_enable 0;sv_deadtalk 1;sv_hibernate_postgame_delay 300;sv_ignoregrenaderadio 0;sv_infinite_ammo 0;sv_talk_enemy_dead 0;sv_talk_enemy_living 0;sv_voiceenable 1;tv_relayvoice 1;mp_team_timeout_max 3;mp_team_timeout_ot_max 1;mp_team_timeout_ot_add_each 1;mp_team_timeout_time 30;sv_vote_command_delay 0;cash_team_bonus_shorthanded 0;mp_spectators_max 20;mp_team_intro_time 0;mp_restartgame 3;mp_warmup_end;");
                }
            }
        }

        private void SendPlayerNotAdminMessage(CCSPlayerController? player)
        {
            // ReplyToUserCommand(player, "You do not have permission to use this command!");
            ReplyToUserCommand(player, Localizer["matchzy.utility.dontpermission"]);
        }

        private string GetColorTreatedString(string message)
        {
            // Adding extra space before args if message starts with a color name
            // This is because colors cannot be applied from 1st character, hence we make first character as an empty space
            if (message.StartsWith('{')) message = " " + message;

            foreach (var field in typeof(ChatColors).GetFields())
            {
                string pattern = $"{{{field.Name}}}";
                string? replacement = field.GetValue(null)?.ToString();

                if (replacement is null) return message;

                // Create a case-insensitive regular expression pattern for the color name
                string patternIgnoreCase = Regex.Escape(pattern);
                message = Regex.Replace(message, patternIgnoreCase, replacement, RegexOptions.IgnoreCase);
            }

            return message;
        }

        private void SendAvailableCommandsMessage(CCSPlayerController? player)
        {
            if (!IsPlayerValid(player)) return;

            ReplyToUserCommand(player, "Available commands:");

            if (isPractice)
            {
                player!.PrintToChat($" {ChatColors.Green}Spawns: {ChatColors.Default}.spawn, .ctspawn, .tspawn, .bestspawn, .worstspawn");
                player.PrintToChat($" {ChatColors.Green}Bots: {ChatColors.Default}.bot, .nobots, .crouchbot, .boost, .crouchboost");
                player.PrintToChat($" {ChatColors.Green}Nades: {ChatColors.Default}.loadnade, .savenade, .importnade, .listnades");
                player.PrintToChat($" {ChatColors.Green}Nade Throw: {ChatColors.Default}.rethrow, .throwindex <index>, .lastindex, .delay <number>");
                player.PrintToChat($" {ChatColors.Green}Utility & Toggles: {ChatColors.Default}.clear, .fastforward, .last, .back, .solid, .impacts, .traj");
                player.PrintToChat($" {ChatColors.Green}Utility & Toggles: {ChatColors.Default}.savepos, .loadpos");
                player.PrintToChat($" {ChatColors.Green}Sides & Others: {ChatColors.Default}.ct, .t, .spec, .fas, .god, .dryrun, .break, .exitprac");
                return;
            }
            if (readyAvailable)
            {
                player!.PrintToChat($" {ChatColors.Green}Ready/Unready: {ChatColors.Default}.ready, .unready");
                return;
            }
            if (isSideSelectionPhase)
            {
                player!.PrintToChat($" {ChatColors.Green}Side Selection: {ChatColors.Default}.stay, .switch, .ct, .t");
                return;
            }
            if (matchStarted)
            {
                string stopCommandMessage = isStopCommandAvailable ? ", .stop" : "";
                player!.PrintToChat($" {ChatColors.Green}Pause/Restore: {ChatColors.Default}.pause, .unpause, .tac, .tech{stopCommandMessage}");
                return;
            }
        }

        public void LoadClientNames()
        {
            string namesFileName = "Match_" + liveMatchId.ToString() + ".ini";
            string namesFilePath = Server.GameDirectory + "/csgo/MatchZyPlayerNames/" + namesFileName;
            string? directoryPath = Path.GetDirectoryName(namesFilePath);
            if (directoryPath != null)
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("\"Names\"");
            sb.AppendLine("{");

            WriteClientNamesInFile(sb, matchzyTeam1.teamPlayers);
            WriteClientNamesInFile(sb, matchzyTeam2.teamPlayers);
            WriteClientNamesInFile(sb, matchConfig.Spectators);

            sb.AppendLine("}");
            File.WriteAllText(namesFilePath, sb.ToString());
            Server.ExecuteCommand($"sv_load_forced_client_names_file MatchZyPlayerNames/" + namesFileName);
        }

        public void WriteClientNamesInFile(StringBuilder sb, JToken? players)
        {
            if (players == null) return;
            foreach (JProperty player in players)
            {
                string steamId = player.Name;
                string escapedName = player.Value.ToString().Replace("\"", "\\\"").Trim();

                if (string.IsNullOrEmpty(escapedName)) continue;

                sb.AppendLine($"\t\"{steamId}\"\t\t\"{escapedName}\"");
            }
        }

        static bool IsValidUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? result))
            {
                return result != null && (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);
            }
            return false;
        }

        public string GetConvarStringValue(ConVar? cvar)
        {
            try
            {
                if (cvar == null) return "";
                string convarValue = cvar.Type switch
                {
                    ConVarType.Bool => cvar.GetPrimitiveValue<bool>().ToString(),
                    ConVarType.Float32 or ConVarType.Float64 => cvar.GetPrimitiveValue<float>().ToString(),
                    ConVarType.UInt16 => cvar.GetPrimitiveValue<ushort>().ToString(),
                    ConVarType.Int16 => cvar.GetPrimitiveValue<short>().ToString(),
                    ConVarType.UInt32 => cvar.GetPrimitiveValue<uint>().ToString(),
                    ConVarType.Int32 => cvar.GetPrimitiveValue<int>().ToString(),
                    ConVarType.Int64 => cvar.GetPrimitiveValue<long>().ToString(),
                    ConVarType.UInt64 => cvar.GetPrimitiveValue<ulong>().ToString(),
                    ConVarType.String => cvar.StringValue,
                    _ => "",
                };
                return convarValue;
            }
            catch (Exception ex)
            {
                Log($"[GetConvarStringValue - FATAL] Exception occurred: {ex.Message}");
                return "";
            }

        }

        public void SetConvarValue(ConVar? cvar, string value)
        {
            if (cvar == null) return;
            Dictionary<ConVarType, Action<string>> conversionMap = new()
            {
                { ConVarType.Bool, v => cvar.SetValue(int.TryParse(v, out int intValue) && intValue >= 1 || Convert.ToBoolean(v) ) },
                { ConVarType.Float32, v => cvar.SetValue(Convert.ToSingle(v)) },
                { ConVarType.Float64, v => cvar.SetValue(Convert.ToSingle(v)) },
                { ConVarType.UInt16, v => cvar.SetValue(Convert.ToUInt16(v)) },
                { ConVarType.Int16, v => cvar.SetValue(Convert.ToInt16(v)) },
                { ConVarType.UInt32, v => cvar.SetValue(Convert.ToUInt32(v)) },
                { ConVarType.Int32, v => cvar.SetValue(Convert.ToInt32(v)) },
                { ConVarType.Int64, v => cvar.SetValue(Convert.ToInt64(v)) },
                { ConVarType.UInt64, v => cvar.SetValue(Convert.ToUInt64(v)) },
                { ConVarType.String, v => cvar.SetValue(v) },
            };

            if (conversionMap.TryGetValue(cvar.Type, out var conversion))
            {
                try
                {
                    conversion(value);
                }
                catch (Exception ex)
                {
                    Log($"[SetConvarValue - FATAL] Exception occurred: {ex.Message}");
                }
            }
        }

        public void ExecuteChangedConvars()
        {
            foreach (string key in matchConfig.ChangedCvars.Keys)
            {
                string value = matchConfig.ChangedCvars[key];
                Log($"[ExecuteChangedConvars] Execing: {key} \"{value}\"");
                Server.ExecuteCommand($"{key} \"{value}\"");
            }
        }

        public void ResetChangedConvars()
        {
            foreach (string key in matchConfig.OriginalCvars.Keys)
            {
                string value = matchConfig.OriginalCvars[key];
                Log($"[ResetChangedConvars] Execing: {key} \"{value}\"");
                Server.ExecuteCommand($"{key} {value}");
            }
        }

        public string FormatCvarValue(string value)
        {
            string formattedTime = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            (int team1Score, int team2Score) = GetTeamsScore();

            var formattedValue = value
                .Replace("{TIME}", formattedTime.Replace(" ", "_"))
                .Replace("{MATCH_ID}", $"{liveMatchId}")
                .Replace("{MAP}", Server.MapName)
                .Replace("{MAPNUMBER}", matchConfig.CurrentMapNumber.ToString())
                .Replace("{TEAM1}", matchzyTeam1.teamName.Replace(" ", "_"))
                .Replace("{TEAM2}", matchzyTeam2.teamName.Replace(" ", "_"))
                .Replace("{TEAM1_SCORE}", team1Score.ToString())
                .Replace("{TEAM2_SCORE}", team2Score.ToString());
            return formattedValue;
        }

        public void UpdateHostname()
        {
            string hostname = hostnameFormat.Value.Trim();
            if (hostname == "" || hostname == "\"\"") return;
            string formattedHostname = FormatCvarValue(hostname);
            Log($"UPDATING HOSTNAME TO: {formattedHostname}");
            Server.ExecuteCommand($"hostname {formattedHostname}");
        }

        public CCSGameRules GetGameRules()
        {
            return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
        }

        public int GetGamePhase()
        {
            return GetGameRules().GamePhase;
        }

        public bool IsHalfTimePhase()
        {
            try
            {
                return GetGamePhase() == 4;
            }
            catch (Exception e)
            {
                Log($"[IsHalfTime FATAL] An error occurred: {e.Message}");
                return false;
            }

        }

        public bool IsPostGamePhase()
        {
            try
            {
                return GetGamePhase() == 5;
            }
            catch (Exception e)
            {
                Log($"[IsPostGamePhase FATAL] An error occurred: {e.Message}");
                return false;
            }

        }

        public bool IsTacticalTimeoutActive()
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;

            return (gameRules.CTTimeOutActive || gameRules.TerroristTimeOutActive) && gameRules.FreezePeriod;
        }

        public (Dictionary<ulong, Dictionary<string, object>>, List<StatsPlayer>, List<StatsPlayer>) GetPlayerStatsDict()
        {
            Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary = new Dictionary<ulong, Dictionary<string, object>>();
            List<StatsPlayer> playerStatsListTeam1 = new();
            List<StatsPlayer> playerStatsListTeam2 = new();
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
            int roundsPlayed = gameRules.TotalRoundsPlayed;
            try
            {
                foreach (int key in playerData.Keys)
                {
                    CCSPlayerController player = playerData[key];
                    if (!player.IsValid || player.ActionTrackingServices == null) continue;

                    var playerStats = player.ActionTrackingServices.MatchStats;
                    ulong steamid64 = player.SteamID;

                    // Create a nested dictionary to store individual stats for the player
                    Dictionary<string, object> stats = new Dictionary<string, object>
                    {
                        { "PlayerName", player.PlayerName },
                        { "Kills", playerStats.Kills },
                        { "Deaths", playerStats.Deaths },
                        { "Assists", playerStats.Assists },
                        { "Damage", playerStats.Damage },
                        { "Enemy2Ks", playerStats.Enemy2Ks },
                        { "Enemy3Ks", playerStats.Enemy3Ks },
                        { "Enemy4Ks", playerStats.Enemy4Ks },
                        { "Enemy5Ks", playerStats.Enemy5Ks },
                        { "EntryCount", playerStats.EntryCount },
                        { "EntryWins", playerStats.EntryWins },
                        // Misma definicion que el payload (ver GetClutchWins):
                        // si el guardado directo escribiera los de MatchStats y
                        // el webhook los de este fork, v1_count saldria distinto
                        // segun quien haya escrito ultimo.
                        { "1v1Count", GetClutchCount(steamid64, 1) },
                        { "1v1Wins", GetClutchWins(steamid64, 1) },
                        { "1v2Count", GetClutchCount(steamid64, 2) },
                        { "1v2Wins", GetClutchWins(steamid64, 2) },
                        { "UtilityCount", playerStats.Utility_Count },
                        { "UtilitySuccess", playerStats.Utility_Successes },
                        { "UtilityDamage", playerStats.UtilityDamage },
                        { "UtilityEnemies", playerStats.Utility_Enemies },
                        { "FlashCount", playerStats.Flash_Count },
                        { "FlashSuccess", playerStats.Flash_Successes },
                        { "HealthPointsRemovedTotal", playerStats.HealthPointsRemovedTotal },
                        { "HealthPointsDealtTotal", playerStats.HealthPointsDealtTotal },
                        { "ShotsFiredTotal", playerStats.ShotsFiredTotal },
                        { "ShotsOnTargetTotal", playerStats.ShotsOnTargetTotal },
                        { "EquipmentValue", playerStats.EquipmentValue },
                        { "MoneySaved", playerStats.MoneySaved },
                        { "KillReward", playerStats.KillReward },
                        { "LiveTime", playerStats.LiveTime },
                        { "HeadShotKills", playerStats.HeadShotKills },
                        { "CashEarned", playerStats.CashEarned },
                        { "EnemiesFlashed", playerStats.EnemiesFlashed }
                    };

                    string teamName = "Spectator";
                    if (player.TeamNum == 3)
                    {
                        teamName = reverseTeamSides["CT"].teamName;
                    }
                    else if (player.TeamNum == 2)
                    {
                        teamName = reverseTeamSides["TERRORIST"].teamName;
                    }

                    stats["TeamName"] = teamName;

                    playerStatsDictionary.Add(steamid64, stats);

                    // Populate PlayerStats instance
                    playerFlashAssists.TryGetValue(steamid64, out int flashAssists);
                    playerTeammatesFlashed.TryGetValue(steamid64, out int teammatesFlashed);
                    playerKnifeKills.TryGetValue(steamid64, out int knifeKills);
                    playerBombPlants.TryGetValue(steamid64, out int bombPlants);
                    playerBombDefuses.TryGetValue(steamid64, out int bombDefuses);
                    kastRoundsContributed.TryGetValue(steamid64, out int kastRounds);
                    int kastPercent = roundsPlayed > 0 ? (int)Math.Round((double)kastRounds / roundsPlayed * 100) : 0;
                    bool kastThisRound = kastFlags.TryGetValue(steamid64, out HashSet<string>? flagsForPlayer) && flagsForPlayer.Count > 0;

                    // Persist new tracked stats in the dictionary used by the DB layer
                    stats["FlashAssists"] = flashAssists;
                    stats["FriendliesFlashed"] = teammatesFlashed;
                    stats["KnifeKills"] = knifeKills;
                    stats["BombPlants"] = bombPlants;
                    stats["BombDefuses"] = bombDefuses;
                    stats["Kast"] = kastPercent;

                    PlayerStats playerStatsInstance = new()
                    {
                        Kills = playerStats.Kills,
                        Deaths = playerStats.Deaths,
                        Assists = playerStats.Assists,
                        FlashAssists = flashAssists,
                        TeamKills = 0,
                        Suicides = 0,
                        Damage = playerStats.Damage,
                        UtilityDamage = playerStats.UtilityDamage,
                        EnemiesFlashed = playerStats.EnemiesFlashed,
                        FriendliesFlashed = teammatesFlashed,
                        KnifeKills = knifeKills,
                        HeadshotKills = playerStats.HeadShotKills,
                        RoundsPlayed = roundsPlayed,
                        BombDefuses = bombDefuses,
                        BombPlants = bombPlants,
                        Kills1 = 0,
                        Kills2 = playerStats.Enemy2Ks,
                        Kills3 = playerStats.Enemy3Ks,
                        Kills4 = playerStats.Enemy4Ks,
                        Kills5 = playerStats.Enemy5Ks,
                        // Clutches de ESTE fork, no los de MatchStats. El motor
                        // solo lleva 1v1 y 1v2, y ademas con otra definicion:
                        // cuenta cada estado por el que pasas, asi que un 1v2 que
                        // bajas a 1v1 le suma los dos. Aca cada jugador entra a lo
                        // sumo una vez por ronda, con el versus congelado en el
                        // momento en que quedo solo.
                        OneV1s = GetClutchWins(steamid64, 1),
                        OneV2s = GetClutchWins(steamid64, 2),
                        OneV3s = GetClutchWins(steamid64, 3),
                        OneV4s = GetClutchWins(steamid64, 4),
                        OneV5s = GetClutchWins(steamid64, 5),
                        FirstKillsT = 0,
                        FirstKillsCT = 0,
                        FirstDeathsT = 0,
                        FirstDeathsCT = 0,
                        TradeKills = 0,
                        Kast = kastPercent,
                        KastThisRound = kastThisRound,
                        Score = player.Score,
                        Mvps = player.MVPs,
                        UtilityCount = playerStats.Utility_Count,
                        UtilitySuccesses = playerStats.Utility_Successes,
                        UtilityEnemies = playerStats.Utility_Enemies,
                        FlashCount = playerStats.Flash_Count,
                        FlashSuccesses = playerStats.Flash_Successes,
                        HealthPointsRemovedTotal = (int)playerStats.HealthPointsRemovedTotal,
                        HealthPointsDealtTotal = (int)playerStats.HealthPointsDealtTotal,
                        ShotsFiredTotal = playerStats.ShotsFiredTotal,
                        ShotsOnTargetTotal = playerStats.ShotsOnTargetTotal,
                        V1Count = GetClutchCount(steamid64, 1),
                        V2Count = GetClutchCount(steamid64, 2),
                        V3Count = GetClutchCount(steamid64, 3),
                        V4Count = GetClutchCount(steamid64, 4),
                        V5Count = GetClutchCount(steamid64, 5),
                        EntryCount = playerStats.EntryCount,
                        EntryWins = playerStats.EntryWins,
                        EquipmentValue = playerStats.EquipmentValue,
                        MoneySaved = playerStats.MoneySaved,
                        KillReward = playerStats.KillReward,
                        LiveTime = playerStats.LiveTime,
                        CashEarned = playerStats.CashEarned,
                    };

                    StatsPlayer statsPlayer = new()
                    {
                        SteamId = steamid64.ToString(),
                        Name = player.PlayerName,
                        Stats = playerStatsInstance
                    };

                    int ctTeamNum = reverseTeamSides["CT"] == matchzyTeam1 ? 1 : 2;
                    int tTeamNum = reverseTeamSides["TERRORIST"] == matchzyTeam1 ? 1 : 2;

                    if (player.TeamNum == 3)
                    {
                        if (ctTeamNum == 1) playerStatsListTeam1.Add(statsPlayer);
                        if (ctTeamNum == 2) playerStatsListTeam2.Add(statsPlayer);
                    }
                    else if (player.TeamNum == 2)
                    {
                        if (tTeamNum == 1) playerStatsListTeam1.Add(statsPlayer);
                        if (tTeamNum == 2) playerStatsListTeam2.Add(statsPlayer);
                    }
                }
            }
            catch (Exception e)
            {
                Log($"[GetPlayerStatsDict FATAL] An error occurred: {e.Message}");
            }

            return (playerStatsDictionary, playerStatsListTeam1, playerStatsListTeam2);
        }

        static string RemoveSpecialCharacters(string input)
        {
            Regex regex = new("[^\\p{L}0-9 _-]");
            return regex.Replace(input, "");
        }

        private void Log(string message)
        {
            Console.WriteLine("[MatchZy] " + message);
        }

        private void AutoStart()
        {
            Log($"[AutoStart] autoStartMode: {autoStartMode}");
            if (autoStartMode == 0)
            {
                StartSleepMode();
            }
            if (autoStartMode == 1)
            {
                readyAvailable = true;
                isPractice = false;
                StartWarmup();
            }
            if (autoStartMode == 2)
            {
                StartPracticeMode();
            }
        }

        public int GetGameMode()
        {
            var convar = ConVar.Find("game_mode");
            if (convar != null)
            {
                return convar.GetPrimitiveValue<int>();
            }
            return -1;
        }

        public int GetGameType()
        {
            var convar = ConVar.Find("game_type");
            if (convar != null)
            {
                return convar.GetPrimitiveValue<int>();
            }
            return -1;
        }

        public void SetCorrectGameMode()
        {
            ConVar.Find("game_mode")!.SetValue(matchConfig.Wingman ? 2 : 1);
            ConVar.Find("game_type")!.SetValue(0); // Classic GameType
        }

        public bool IsMapReloadRequiredForGameMode(bool wingman)
        {
            int expectedMode = wingman ? 2 : 1;
            if (GetGameMode() != expectedMode || GetGameType() != 0)
            {
                return true;
            }
            return false;
        }

        public bool IsWingmanMode()
        {
            if (GetGameMode() == 2 && GetGameType() == 0) return true;
            return false;
        }

        public void KickPlayer(CCSPlayerController player)
        {
            if (!player.UserId.HasValue) return;
            // Defer the kick to the next frame. Calling Server.ExecuteCommand("kickid ...")
            // synchronously from inside an event handler (e.g. EventPlayerConnectFull) can
            // crash the server with a segfault, because the engine is still mid-way through
            // initializing/finalizing the client. NextFrame ensures the engine is in a stable
            // state before issuing the kick.
            ushort userId = (ushort)player.UserId.Value;
            string playerName = player.PlayerName;
            Server.NextFrame(() =>
            {
                Log($"[KickPlayer] Executing deferred kickid {userId} ({playerName})");
                Server.ExecuteCommand($"kickid {userId}");
            });
        }

        public bool IsPlayerValid(CCSPlayerController? player)
        {
            return (
                player != null &&
                player.IsValid &&
                player.PlayerPawn.IsValid &&
                player.PlayerPawn.Value != null
            );
        }

        public static Color GetPlayerTeammateColor(CCSPlayerController playerController)
        {
            return playerController.CompTeammateColor switch
            {
                1 => Color.FromArgb(50, 255, 0),
                2 => Color.FromArgb(255, 255, 0),
                3 => Color.FromArgb(255, 132, 0),
                4 => Color.FromArgb(255, 0, 255),
                0 => Color.FromArgb(0, 187, 255),
                _ => Color.Red,
            };
        }

        public static string? GetConvarValueFromCFGFile(string filePath, string convarName)
        {
            var fileContent = File.ReadAllText(filePath);

            string pattern = @$"^{convarName}\s+(.+)$";

            Regex regex = new(pattern, RegexOptions.Multiline);

            Match match = regex.Match(fileContent);
            string? value = match.Success ? match.Groups[1].Value : null;
            return value;
        }

        public async Task UploadFileAsync(string? filePath, string fileUploadURL, string headerKey, string headerValue, long matchId, int mapNumber, int roundNumber)
        {
            if (filePath == null || fileUploadURL == "")
            {
                Log($"[UploadFileAsync] Not able to upload the file, either filePath or fileUploadURL is not set. filePath: {filePath} fileUploadURL: {fileUploadURL}");
                return;
            }

            try
            {
                using var httpClient = new HttpClient();
                Log($"[UploadFileAsync] Going to upload the file on {fileUploadURL}. Complete path: {filePath}");

                if (!File.Exists(filePath))
                {
                    Log($"[UploadFileAsync ERROR] File not found: {filePath}");
                    return;
                }

                using FileStream fileStream = File.OpenRead(filePath);

                byte[] fileContent = new byte[fileStream.Length];
                await fileStream.ReadAsync(fileContent, 0, (int)fileStream.Length);

                using ByteArrayContent content = new(fileContent);
                content.Headers.Add("Content-Type", "application/octet-stream");

                content.Headers.Add("MatchZy-FileName", Path.GetFileName(filePath));
                content.Headers.Add("MatchZy-MatchId", matchId.ToString());
                content.Headers.Add("MatchZy-MapNumber", mapNumber.ToString());
                content.Headers.Add("MatchZy-RoundNumber", roundNumber.ToString());

                // For Get5 Panel
                content.Headers.Add("Get5-FileName", Path.GetFileName(filePath));
                content.Headers.Add("Get5-MatchId", matchId.ToString());
                content.Headers.Add("Get5-MapNumber", mapNumber.ToString());
                content.Headers.Add("Get5-RoundNumber", roundNumber.ToString());


                if (!string.IsNullOrEmpty(headerKey) && !string.IsNullOrEmpty(headerValue))
                {
                    httpClient.DefaultRequestHeaders.Add(headerKey, headerValue);
                }

                HttpResponseMessage response = await httpClient.PostAsync(fileUploadURL, content);

                if (response.IsSuccessStatusCode)
                {
                    Log($"[UploadFileAsync] File upload successful for matchId: {matchId} mapNumber: {mapNumber} fileName: {Path.GetFileName(filePath)}.");
                }
                else
                {
                    Log($"[UploadFileAsync ERROR] Failed to upload file. Status code: {response.StatusCode} Response: {await response.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception e)
            {
                Log($"[UploadFileAsync FATAL] An error occurred: {e.Message}");
            }
        }

        public bool HandlePlayerWhitelist(CCSPlayerController player, string steamId)
        {
            string whitelistfileName = "MatchZy/whitelist.cfg";
            string whitelistPath = Path.Join(Server.GameDirectory + "/csgo/cfg", whitelistfileName);
            string? directoryPath = Path.GetDirectoryName(whitelistPath);
            if (directoryPath != null)
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
            }
            if (!File.Exists(whitelistPath)) File.WriteAllLines(whitelistPath, new[] { "Steamid1", "Steamid2" });

            var whiteList = File.ReadAllLines(whitelistPath);

            if (isWhitelistRequired == true)
            {
                if (!whiteList.Contains(steamId.ToString()))
                {
                    Log($"[EventPlayerConnectFull] KICKING PLAYER STEAMID: {steamId}, Name: {player.PlayerName} (Not whitelisted!)");
                    PrintToAllChat($"Kicking player {player.PlayerName} - Not whitelisted.");
                    KickPlayer(player);
                    return true;
                }
            }

            return false;
        }

        public void SwitchPlayerTeam(CCSPlayerController player, CsTeam team)
        {
            if (player.Team == team) return;

            Server.NextFrame(() =>
            {
                if (team == CsTeam.Spectator)
                {
                    player.ChangeTeam(team);
                }
                else
                {
                    player.SwitchTeam(team);
                    var gameRules = GetGameRules();
                    if (gameRules.WarmupPeriod)
                    {
                        player.Respawn();
                    }
                }
            });
        }

        public void SetPlayerInvisible(CCSPlayerController player, bool setWeaponsInvisible)
        {
            if (!IsPlayerValid(player)) return;
            var playerPawnValue = player.PlayerPawn.Value;

            if (playerPawnValue != null && playerPawnValue.IsValid)
            {
                playerPawnValue.Render = Color.FromArgb(0, 0, 0, 0);
                Utilities.SetStateChanged(playerPawnValue, "CBaseModelEntity", "m_clrRender");
            }

            if (!setWeaponsInvisible) return;

            var activeWeapon = playerPawnValue!.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon != null && activeWeapon.IsValid)
            {
                activeWeapon.Render = Color.FromArgb(0, 0, 0, 0);
                activeWeapon.ShadowStrength = 0.0f;
                Utilities.SetStateChanged(activeWeapon, "CBaseModelEntity", "m_clrRender");
            }

            var myWeapons = playerPawnValue.WeaponServices?.MyWeapons;
            if (myWeapons != null)
            {
                foreach (var gun in myWeapons)
                {
                    var weapon = gun.Value;
                    if (weapon != null)
                    {
                        weapon.Render = Color.FromArgb(0, 0, 0, 0);
                        weapon.ShadowStrength = 0.0f;
                        Utilities.SetStateChanged(weapon, "CBaseModelEntity", "m_clrRender");
                    }
                }
            }
        }

        public void SetPlayerVisible(CCSPlayerController player)
        {
            if (!IsPlayerValid(player)) return;

            var playerPawnValue = player.PlayerPawn.Value;
            if (playerPawnValue == null)
                return;

            playerPawnValue.Render = Color.FromArgb(255, 255, 255, 255);
            Utilities.SetStateChanged(playerPawnValue, "CBaseModelEntity", "m_clrRender");
        }

        public void DropWeaponByDesignerName(CCSPlayerController player, string weaponName)
        {
            if (!IsPlayerValid(player) || player.PlayerPawn.Value!.WeaponServices is null) return;
            var matchedWeapon = player.PlayerPawn.Value!.WeaponServices!.MyWeapons
                .Where(weapon => weapon.Value!.DesignerName == weaponName).FirstOrDefault();

            if (matchedWeapon != null && matchedWeapon.IsValid)
            {
                player.PlayerPawn.Value.WeaponServices.ActiveWeapon.Raw = matchedWeapon.Raw;
                player.DropActiveWeapon();
            }
        }

        public void RandomizeSpawns()
        {
            List<CCSPlayerController> players = Utilities.GetPlayers();

            Dictionary<byte, List<Position>> teamSpawns = new()
            {
                { (byte)CsTeam.CounterTerrorist, spawnsData[(byte)CsTeam.CounterTerrorist].Select(position => new Position(position)).ToList() },
                { (byte)CsTeam.Terrorist, spawnsData[(byte)CsTeam.Terrorist].Select(position => new Position(position)).ToList() }
            };

            Random random = new();

            foreach (var player in players)
            {
                if (!IsPlayerValid(player)) continue;

                if (teamSpawns[player.TeamNum].Count == 0) break;

                int randomIndex = random.Next(teamSpawns[player.TeamNum].Count);
                Position spawnPosition = teamSpawns[player.TeamNum][randomIndex];
                teamSpawns[player.TeamNum].RemoveAt(randomIndex);

                spawnPosition.Teleport(player);
            }
        }
    }
}
