using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Events;


namespace MatchZy
{
    [MinimumApiVersion(227)]
    public partial class MatchZy : BasePlugin
    {

        public override string ModuleName => "MatchZy";

        public override string ModuleVersion => "0.8.15";

        public override string ModuleAuthor => "WD- (https://github.com/shobhit-pathak/)";

        public override string ModuleDescription => "A plugin for running and managing CS2 practice/pugs/scrims/matches!";

        public string chatPrefix = $"[{ChatColors.Green}MatchZy{ChatColors.Default}]";
        public string adminChatPrefix = $"[{ChatColors.Red}ADMIN{ChatColors.Default}]";

        // Plugin start phase data
        public bool isPractice = false;
        public bool isSleep = false;
        public bool readyAvailable = false;
        public bool matchStarted = false;
        public bool isWarmup = false;
        public bool isKnifeRound = false;
        public bool isSideSelectionPhase = false;
        public bool isMatchLive = false;
        public long liveMatchId = -1;
        // True only when the current match was loaded via matchzy_loadmatch_url.
        // Used to gate the auto-start countdown so it only runs for URL-loaded matches.
        public bool matchLoadedFromUrl = false;
        public int autoStartMode = 1;

        public bool mapReloadRequired = false;

        // Latch de una sola vía: habilita reaplicar el CFG de warmup cuando el
        // servidor sale de vacío. El ciclo de vida es
        //   changelevel -> warmup -> cuchillo/live -> warmup (elección de lado) -> live
        // y la reaplicación solo tiene sentido en el PRIMER warmup, que es el
        // único donde el servidor puede quedar vacío mientras los jugadores van
        // entrando. Lo prende StartWarmup() y lo apagan StartKnifeRound() y
        // SetLiveFlags(); una vez cerrado no se reabre hasta el próximo mapa.
        //
        // Es un latch y no una lectura de fase a propósito: `isWarmup` vuelve a
        // true en la elección de lado y en caminos como ResetMatch(), así que
        // inferir la fase desde las flags dejaba huecos por los que se colaba un
        // exec de warmup.cfg (con mp_warmup_start y bot_kick adentro) sobre una
        // partida en curso.
        public bool warmupCfgReapplyEnabled = false;

        // Pause Data
        public bool isPaused = false;
        public Dictionary<string, object> unpauseData = new Dictionary<string, object> {
            { "ct", false },
            { "t", false },
            { "pauseTeam", "" }
        };

        bool isPauseCommandForTactical = false;

        // Knife Data
        public int knifeWinner = 0;
        public string knifeWinnerName = "";

        // Players Data (including admins)
        public int connectedPlayers = 0;
        private Dictionary<int, bool> playerReadyStatus = new Dictionary<int, bool>();
        private Dictionary<int, CCSPlayerController> playerData = new Dictionary<int, CCSPlayerController>();

        // Admin Data
        private Dictionary<string, string> loadedAdmins = new Dictionary<string, string>();

        // Timers
        public CounterStrikeSharp.API.Modules.Timers.Timer? unreadyPlayerMessageTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? pausedStateTimer = null;

        // Each message is kept in chat display for ~13 seconds, hence setting default chat timer to 13 seconds.
        // Configurable using matchzy_chat_messages_timer_delay <seconds>
        public int chatTimerDelay = 13;

        // Player wait / countdown system (used when match is loaded via URL)
        public bool isWaitingForPlayers = false;
        public CounterStrikeSharp.API.Modules.Timers.Timer? playerWaitTimeoutTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? matchCountdownTimer = null;
        /// <summary>
        /// Recordatorio periódico en chat ("Quedan N minutos para que se conecten…").
        /// Se arranca junto con `playerWaitTimeoutTimer` y se mata cuando todos
        /// se conectan (StartMatchCountdown), cuando se cancela (timeout) o en
        /// cualquier reset (`KillPhaseTimers`/`ResetMatch`).
        /// </summary>
        public CounterStrikeSharp.API.Modules.Timers.Timer? playerWaitReminderTimer = null;

        /// <summary>
        /// Generación monotónica del countdown actual. Cada vez que se inicia
        /// un nuevo countdown se incrementa; los ticks one-shot ya encolados
        /// con un id viejo se descartan silenciosamente. Esto reemplaza el
        /// patrón de Timer.REPEAT + Kill() desde dentro del callback, que
        /// estaba corrompiendo memoria del engine de CS2.
        /// </summary>
        public int countdownGeneration = 0;

        /// <summary>True mientras hay un countdown activo (evita re-disparar StartMatchCountdown).</summary>
        public bool isCountdownActive = false;

        // ── Detección de abandono ────────────────────────────────────────────
        //
        // Se lleva por steamid64 y NO en `playerData` porque ese diccionario
        // está indexado por userId y el handler de desconexión borra la entrada
        // — justo el dato que hace falta conservar mientras el jugador no está.
        //
        // El acumulado es POR MAPA: se resetea al cerrar cada mapa. Lo que
        // sobrevive a la serie es `abandonFlagged`, para reportar una sola falta
        // por más que el jugador abandone en varios mapas del BO3.

        /// <summary>steamid64 → segundos acumulados fuera del servidor EN EL MAPA ACTUAL.</summary>
        public Dictionary<string, int> abandonAccumulated = new();

        /// <summary>steamid64 → instante de la desconexión (intervalo abierto, aún sin cerrar).</summary>
        public Dictionary<string, DateTime> abandonOpenSince = new();

        /// <summary>
        /// steamid64 → nick, capturado AL DESCONECTARSE. Hace falta guardarlo
        /// porque el aviso en chat lo nombra mientras está fuera, y para
        /// entonces su controller ya no existe.
        /// </summary>
        public Dictionary<string, string> abandonNames = new();

        /// <summary>
        /// Generación del recordatorio de abandono. Los ticks one-shot ya
        /// encolados con un id viejo se descartan solos — mismo mecanismo que
        /// `playerWaitGeneration`, que reemplazó al Timer.REPEAT + Kill() que
        /// estaba corrompiendo memoria del engine.
        /// </summary>
        public int abandonWarnGeneration = 0;

        /// <summary>Generación de la fase de selección de lado (ver abandonWarnGeneration).</summary>
        public int sideSelectionGeneration = 0;

        /// <summary>
        /// steamid64 → datos del mapa donde superó el umbral. Se acumula durante
        /// toda la serie y se vacía al emitir el evento en `series_end`.
        /// </summary>
        public Dictionary<string, (int MapNumber, string MapName, int Seconds)> abandonFlagged = new();

        /// <summary>
        /// UTC en que empezó el warmup actual (para forzar un mínimo de
        /// estabilización del engine antes de pasar a live). El engine de CS2
        /// crashea con SIGSEGV en `BeginMatch` si la transición live ocurre
        /// demasiado rápido después del map load.
        /// </summary>
        public DateTime warmupStartedAt = DateTime.MinValue;

        /// <summary>Segundos mínimos de warmup antes de poder pasar a live.</summary>
        public const int MinWarmupSecondsBeforeLive = 15;

        /// <summary>
        /// Flag para evitar agendar múltiples diferidos de <c>CheckLiveRequired</c>
        /// mientras el servidor termina de estabilizar el warmup. Sin esto, cada
        /// EventPlayerConnectFull dispara un nuevo AddTimer + un PrintToAllChat
        /// ("Estabilizando servidor...") generando spam visible para los jugadores.
        /// </summary>
        public bool liveDeferralScheduled = false;

        /// <summary>
        /// Generación del sistema de espera de jugadores. Cada vez que se
        /// inicia (o se cancela) un wait, se incrementa, invalidando los ticks
        /// del reminder one-shot que ya estuvieran en cola. Reemplaza el patrón
        /// de Timer.REPEAT + Kill() que corrompía memoria del engine.
        /// </summary>
        public int playerWaitGeneration = 0;

        // Game Config
        public bool isKnifeRequired = true;
        public int minimumReadyRequired = 2; // Number of ready players required start the match. If set to 0, all connected players have to ready-up to start the match.
        public bool isWhitelistRequired = false;
        public bool isSaveNadesAsGlobalEnabled = false;

        public bool isPlayOutEnabled = false;

        public bool playerHasTakenDamage = false;

        // User command - action map
        public Dictionary<string, Action<CCSPlayerController?, CommandInfo?>>? commandActions;

        // SQLite/MySQL Database
        private Database database = new();

        // Cuando es false, el plugin deja de escribir directo a matchzy_stats_matches/
        // maps/players (y de generar el backup JSON por ronda), delegando el guardado
        // por completo a quien consuma los webhooks (matchzy_remote_log_url). Los
        // eventos siguen enviandose siempre, sin importar este flag. Ver
        // matchzy_stats_direct_save_enabled en ConfigConvars.cs.
        public bool isStatsDirectSaveEnabled = true;

        // true una vez que se abrio la conexion a la BD y se crearon las tablas.
        // Ver EnsureDatabaseInitialized(): la conexion NO se abre en Load() a
        // proposito (eso pasaria siempre, sin importar isStatsDirectSaveEnabled,
        // porque los cvars de warmup.cfg/match config todavia no se ejecutaron en
        // ese punto), sino recien en el primer uso real de `database`, momento en
        // el que isStatsDirectSaveEnabled ya tiene su valor final para el match/
        // sesion en curso. Asi, si el cvar esta en 0, el plugin no abre ninguna
        // conexion ni toca la BD en absoluto.
        private bool isDatabaseInitialized = false;

        private void EnsureDatabaseInitialized()
        {
            if (isDatabaseInitialized) return;
            database.InitializeDatabase(ModuleDirectory);
            isDatabaseInitialized = true;
        }

        public override void Load(bool hotReload)
        {

            LoadAdmins();

            // This sets default config ConVars
            Server.ExecuteCommand("execifexists MatchZy/config.cfg");

            teamSides[matchzyTeam1] = "CT";
            teamSides[matchzyTeam2] = "TERRORIST";
            reverseTeamSides["CT"] = matchzyTeam1;
            reverseTeamSides["TERRORIST"] = matchzyTeam2;

            if (!hotReload)
            {
                AutoStart();
            }
            else
            {
                // Pluign should not be reloaded while a match is live (this would messup with the match flags which were set)
                // Only hot-reload the plugin if you are testing something and don't want to restart the server time and again.
                UpdatePlayersMap();
                AutoStart();
            }

            commandActions = new Dictionary<string, Action<CCSPlayerController?, CommandInfo?>> {
                { ".ready", OnPlayerReady },
                { ".r", OnPlayerReady },
                { ".forceready", OnForceReadyCommandCommand },
                { ".unready", OnPlayerUnReady },
                { ".notready", OnPlayerUnReady },
                { ".ur", OnPlayerUnReady },
                { ".stay", OnTeamStay },
                { ".switch", OnTeamSwitch },
                { ".swap", OnTeamSwitch },
                { ".tech", OnTechCommand },
                { ".p", OnPauseCommand },
                { ".pause", OnPauseCommand },
                { ".unpause", OnUnpauseCommand },
                { ".up", OnUnpauseCommand },
                { ".forcepause", OnForcePauseCommand },
                { ".fp", OnForcePauseCommand },
                { ".forceunpause", OnForceUnpauseCommand },
                { ".fup", OnForceUnpauseCommand },
                { ".tac", OnTacCommand },
                { ".roundknife", OnKnifeCommand },
                { ".rk", OnKnifeCommand },
                { ".playout", OnPlayoutCommand },
                { ".start", OnStartCommand },
                { ".force", OnStartCommand },
                { ".forcestart", OnStartCommand },
                { ".skipveto", OnSkipVetoCommand },
                { ".sv", OnSkipVetoCommand },
                { ".restart", OnRestartMatchCommand },
                { ".rr", OnRestartMatchCommand },
                { ".endmatch", OnEndMatchCommand },
                { ".forceend", OnEndMatchCommand },
                { ".reloadmap", OnMapReloadCommand },
                { ".settings", OnMatchSettingsCommand },
                { ".whitelist", OnWLCommand },
                { ".globalnades", OnSaveNadesAsGlobalCommand },
                { ".reload_admins", OnReloadAdmins },
                { ".tactics", OnPracCommand },
                { ".prac", OnPracCommand },
                { ".showspawns", OnShowSpawnsCommand },
                { ".hidespawns", OnHideSpawnsCommand },
                { ".dryrun", OnDryRunCommand },
                { ".dry", OnDryRunCommand },
                { ".noflash", OnNoFlashCommand },
                { ".noblind", OnNoFlashCommand },
                { ".break", OnBreakCommand },
                { ".bot", OnBotCommand },
                { ".cbot", OnCrouchBotCommand },
                { ".crouchbot", OnCrouchBotCommand },
                { ".boost", OnBoostBotCommand },
                { ".crouchboost", OnCrouchBoostBotCommand },
                { ".nobots", OnNoBotsCommand },
                { ".solid", OnSolidCommand },
                { ".impacts", OnImpactsCommand },
                { ".traj", OnTrajCommand },
                { ".pip", OnTrajCommand },
                { ".god", OnGodCommand },
                { ".ff", OnFastForwardCommand },
                { ".fastforward", OnFastForwardCommand },
                { ".clear", OnClearCommand },
                { ".match", OnMatchCommand },
                { ".uncoach", OnUnCoachCommand },
                { ".exitprac", OnMatchCommand },
                { ".stop", OnStopCommand },
                { ".help", OnHelpCommand },
                { ".t", OnTCommand },
                { ".ct", OnCTCommand },
                { ".spec", OnSpecCommand },
                { ".fas", OnFASCommand },
                { ".watchme", OnFASCommand },
                { ".last", OnLastCommand },
                { ".throw", OnRethrowCommand },
                { ".rethrow", OnRethrowCommand },
                { ".rt", OnRethrowCommand },
                { ".throwsmoke", OnRethrowSmokeCommand },
                { ".rethrowsmoke", OnRethrowSmokeCommand },
                { ".thrownade", OnRethrowGrenadeCommand },
                { ".rethrownade", OnRethrowGrenadeCommand },
                { ".rethrowgrenade", OnRethrowGrenadeCommand },
                { ".throwgrenade", OnRethrowGrenadeCommand },
                { ".rethrowflash", OnRethrowFlashCommand },
                { ".throwflash", OnRethrowFlashCommand },
                { ".rethrowdecoy", OnRethrowDecoyCommand },
                { ".throwdecoy", OnRethrowDecoyCommand },
                { ".throwmolotov", OnRethrowMolotovCommand },
                { ".rethrowmolotov", OnRethrowMolotovCommand },
                { ".timer", OnTimerCommand },
                { ".lastindex", OnLastIndexCommand },
                { ".bestspawn", OnBestSpawnCommand },
                { ".worstspawn", OnWorstSpawnCommand },
                { ".bestctspawn", OnBestCTSpawnCommand },
                { ".worstctspawn", OnWorstCTSpawnCommand },
                { ".besttspawn", OnBestTSpawnCommand },
                { ".worsttspawn", OnWorstTSpawnCommand },
                { ".savepos", OnSavePosCommand},
                { ".loadpos", OnLoadPosCommand}
            };

            RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFullHandler);
            RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnectHandler);
            RegisterEventHandler<EventCsWinPanelRound>(EventCsWinPanelRoundHandler, hookMode: HookMode.Pre);
            RegisterEventHandler<EventCsWinPanelMatch>(EventCsWinPanelMatchHandler);
            RegisterEventHandler<EventRoundStart>(EventRoundStartHandler);
            RegisterEventHandler<EventRoundFreezeEnd>(EventRoundFreezeEndHandler);
            RegisterEventHandler<EventPlayerGivenC4>(EventPlayerGivenC4);
            RegisterEventHandler<EventPlayerDeath>(EventPlayerDeathPreHandler, hookMode: HookMode.Pre);
            RegisterListener<Listeners.OnClientDisconnectPost>(playerSlot =>
            {
                // May not be required, but just to be on safe side so that player data is properly updated in dictionaries
                // Update: Commenting the below function as it was being called multiple times on map change.
                // UpdatePlayersMap();
            });
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawnedHandler);
            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
                if (!IsPlayerValid(player)) return HookResult.Continue;

                if (matchzyTeam1.coach.Contains(player!) || matchzyTeam2.coach.Contains(player!))
                {
                    @event.Silent = true;
                    return HookResult.Changed;
                }
                return HookResult.Continue;
            }, HookMode.Pre);

            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                if (!isMatchSetup && !isVeto) return HookResult.Continue;

                CCSPlayerController? player = @event.Userid;

                if (!IsPlayerValid(player)) return HookResult.Continue;

                if (player!.IsHLTV || player.IsBot)
                {
                    return HookResult.Continue;
                }

                CsTeam playerTeam = GetPlayerTeam(player);

                SwitchPlayerTeam(player, playerTeam);

                // Re-evaluar el auto-start: cuando un jugador acaba de ser
                // movido a su equipo de la config, IsTeamReady() recién ahora
                // puede contarlo. Si era el último que faltaba, esto dispara
                // StartMatchCountdown(). Pequeño delay para que TeamNum se
                // actualice en el servidor antes de chequear.
                if (isMatchSetup && readyAvailable && !matchStarted)
                {
                    AddTimer(0.2f, () => CheckLiveRequired());
                }

                return HookResult.Continue;
            });

            AddCommandListener("jointeam", (player, info) =>
            {
                if ((isMatchSetup || isVeto) && player != null && player.IsValid)
                {
                    if (int.TryParse(info.ArgByIndex(1), out int joiningTeam))
                    {
                        int playerTeam = (int)GetPlayerTeam(player);
                        if (joiningTeam != playerTeam)
                        {
                            return HookResult.Stop;
                        }
                    }
                }
                return HookResult.Continue;
            });

            AddCommandListener("noclip", OnConsoleNoClip); // Override noclip

            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                if (!isKnifeRound) return HookResult.Continue;

                DetermineKnifeWinner();
                @event.Winner = knifeWinner;
                int finalEvent = 10;
                if (knifeWinner == 3)
                {
                    finalEvent = 8;
                }
                else if (knifeWinner == 2)
                {
                    finalEvent = 9;
                }
                @event.Reason = finalEvent;
                isSideSelectionPhase = true;
                isKnifeRound = false;
                StartAfterKnifeWarmup();

                return HookResult.Changed;
            }, HookMode.Pre);

            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                try
                {
                    if (isDryRun)
                    {
                        StartPracticeMode();
                        isDryRun = false;
                        return HookResult.Continue;
                    }
                    if (!isMatchLive) return HookResult.Continue;
                    HandlePostRoundEndEvent(@event);
                    return HookResult.Continue;
                }
                catch (Exception e)
                {
                    Log($"[EventRoundEnd FATAL] An error occurred: {e.Message}");
                    return HookResult.Continue;
                }

            }, HookMode.Post);

            // RegisterEventHandler<EventMapShutdown>((@event, info) => {
            //     Log($"[EventMapShutdown] Resetting match!");
            //     ResetMatch();
            //     return HookResult.Continue;
            // });

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                AddTimer(1.0f, () =>
                {
                    if (!isMatchSetup)
                    {
                        AutoStart();
                        return;
                    }
                    if (isWarmup) StartWarmup();
                    if (isPractice) StartPracticeMode();

                    // Notifica a servicios externos que el warmup del mapa actual
                    // ya está activo (post `changelevel` + cfg de warmup). Permite
                    // a integraciones evitar timers/race conditions tipo "esperar X
                    // segundos antes de chequear jugadores en BO3".
                    if (isMatchSetup && isWarmup && liveMatchId > 0)
                    {
                        var warmupStartedEvent = new MapWarmupStartedEvent
                        {
                            MatchId = liveMatchId,
                            MapNumber = matchConfig.CurrentMapNumber
                        };
                        Task.Run(async () => await SendEventAsync(warmupStartedEvent));

                        // Reinicia el watchdog de espera de jugadores SOLO para
                        // los mapas posteriores de una serie BO3/BO5
                        // (CurrentMapNumber > 0). El primer mapa ya lo arranca
                        // `LoadMatchFromJSON` y volver a llamarlo aquí provocaba
                        // doble inicialización + doble exec de warmup.cfg al
                        // mismo tiempo que la primera ronda de jugadores
                        // conectaba, lo que llegaba a hacer crashear al server
                        // CS2 (segfault en spawn points).
                        if (!matchStarted && matchConfig.CurrentMapNumber > 0)
                        {
                            StartPlayerWaitSystem();
                        }
                    }
                });
            });

            // RegisterListener<Listeners.OnMapEnd>(() => {
            //     Log($"[Listeners.OnMapEnd] Resetting match!");
            //     ResetMatch();
            // });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                // Setting money back to 16000 when a player dies in warmup
                var player = @event.Userid;
                if (!isWarmup) return HookResult.Continue;
                if (!IsPlayerValid(player)) return HookResult.Continue;
                if (player!.InGameMoneyServices != null) player.InGameMoneyServices.Account = 16000;
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerHurt>((@event, info) =>
            {
                CCSPlayerController? attacker = @event.Attacker;
                CCSPlayerController? victim = @event.Userid;

                if (!IsPlayerValid(attacker) || !IsPlayerValid(victim)) return HookResult.Continue;

                if (isPractice && victim!.IsBot)
                {
                    int damage = @event.DmgHealth;
                    int postDamageHealth = @event.Health;
                    PrintToPlayerChat(attacker!, Localizer["matchzy.pracc.damage", damage, victim.PlayerName, postDamageHealth]);
                    return HookResult.Continue;
                }

                if (!attacker!.IsValid || attacker.IsBot && !(@event.DmgHealth > 0 || @event.DmgArmor > 0))
                    return HookResult.Continue;
                if (matchStarted && victim!.TeamNum != attacker.TeamNum)
                {
                    int targetId = (int)victim.UserId!;
                    UpdatePlayerDamageInfo(@event, targetId);
                    if (attacker != victim) playerHasTakenDamage = true;
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerChat>((@event, info) =>
            {

                int currentVersion = Api.GetVersion();
                int index = @event.Userid + 1;
                var playerUserId = NativeAPI.GetUseridFromIndex(index);

                var originalMessage = @event.Text.Trim();
                var message = @event.Text.Trim().ToLower();

                var parts = originalMessage.Split(' ');
                var messageCommand = parts.Length > 0 ? parts[0] : string.Empty;
                var messageCommandArg = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

                CCSPlayerController? player = null;
                if (playerData.TryGetValue(playerUserId, out CCSPlayerController? value))
                {
                    player = value;
                }

                if (player == null)
                {
                    // Somehow we did not had the player in playerData, hence updating the maps again before getting the player
                    UpdatePlayersMap();
                    player = playerData[playerUserId];
                }

                // Handling player commands
                if (commandActions.ContainsKey(message))
                {
                    commandActions[message](player, null);
                }

                if (message.StartsWith(".map"))
                {
                    HandleMapChangeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".readyrequired"))
                {
                    HandleReadyRequiredCommand(player, messageCommandArg);
                }

                if (message.StartsWith(".restore"))
                {
                    HandleRestoreCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".asay"))
                {
                    if (IsPlayerAdmin(player, "css_asay", "@css/chat"))
                    {
                        if (messageCommandArg != "")
                        {
                            Server.PrintToChatAll($"{adminChatPrefix} {messageCommandArg}");
                        }
                        else
                        {
                            // ReplyToUserCommand(player, "Usage: .asay <message>");
                            ReplyToUserCommand(player, Localizer["matchzy.cc.usage", ".asay <message>"]);
                        }
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }
                if (message.StartsWith(".savenade") || message.StartsWith(".sn"))
                {
                    HandleSaveNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".delnade") || message.StartsWith(".dn"))
                {
                    HandleDeleteNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".deletenade"))
                {
                    HandleDeleteNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".importnade") || message.StartsWith(".in"))
                {
                    HandleImportNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".listnades") || message.StartsWith(".lin"))
                {
                    HandleListNadesCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".loadnade") || message.StartsWith(".ln"))
                {
                    HandleLoadNadeCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".spawn"))
                {
                    HandleSpawnCommand(player, messageCommandArg, player.TeamNum, "spawn");
                }
                if (message.StartsWith(".ctspawn") || message.StartsWith(".cts"))
                {
                    HandleSpawnCommand(player, messageCommandArg, (byte)CsTeam.CounterTerrorist, "ctspawn");
                }
                if (message.StartsWith(".tspawn") || message.StartsWith(".ts"))
                {
                    HandleSpawnCommand(player, messageCommandArg, (byte)CsTeam.Terrorist, "tspawn");
                }
                if (message.StartsWith(".team1"))
                {
                    HandleTeamNameChangeCommand(player, messageCommandArg, 1);
                }
                if (message.StartsWith(".team2"))
                {
                    HandleTeamNameChangeCommand(player, messageCommandArg, 2);
                }
                if (message.StartsWith(".rcon"))
                {
                    if (IsPlayerAdmin(player, "css_rcon", "@css/rcon"))
                    {
                        Server.ExecuteCommand(messageCommandArg);
                        ReplyToUserCommand(player, "Command sent successfully!");
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }
                if (message.StartsWith(".coach"))
                {
                    HandleCoachCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".ban"))
                {
                    HandeMapBanCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".pick"))
                {
                    HandeMapPickCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".back"))
                {
                    HandleBackCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".delay"))
                {
                    HandleDelayCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".throwindex"))
                {
                    HandleThrowIndexCommand(player, messageCommandArg);
                }
                if (message.StartsWith(".throwidx"))
                {
                    HandleThrowIndexCommand(player, messageCommandArg);
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerBlind>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
                CCSPlayerController? attacker = @event.Attacker;

                // Track flash stats during live match
                if (isMatchLive && IsPlayerValid(player) && IsPlayerValid(attacker) && attacker!.IsValid)
                {
                    if (@event.BlindDuration >= 1.0f)
                    {
                        ulong attackerSteamId = attacker.SteamID;
                        if (attacker.TeamNum == player!.TeamNum)
                            IncrementStat(playerTeammatesFlashed, attackerSteamId);
                        else
                            IncrementStat(playerFlashAssists, attackerSteamId);
                    }
                }

                if (!isPractice) return HookResult.Continue;

                if (!IsPlayerValid(player) || !IsPlayerValid(attacker)) return HookResult.Continue;

                if (attacker!.IsValid)
                {
                    double roundedBlindDuration = Math.Round(@event.BlindDuration, 2);
                    PrintToPlayerChat(attacker, Localizer["matchzy.pracc.blind", player!.PlayerName, roundedBlindDuration]);
                }
                var userId = player!.UserId;
                if (userId != null && noFlashList.Contains((int)userId))
                {
                    Server.NextFrame(() => KillFlashEffect(player));
                }

                return HookResult.Continue;
            });

            RegisterEventHandler<EventBombPlanted>((@event, info) =>
            {
                if (!isMatchLive) return HookResult.Continue;
                CCSPlayerController? player = @event.Userid;
                if (IsPlayerValid(player))
                {
                    IncrementStat(playerBombPlants, player!.SteamID);
                    RecordBombPlant(player);
                }
                return HookResult.Continue;
            });

            RegisterEventHandler<EventBombDefused>((@event, info) =>
            {
                if (!isMatchLive) return HookResult.Continue;
                CCSPlayerController? player = @event.Userid;
                if (IsPlayerValid(player))
                    IncrementStat(playerBombDefuses, player!.SteamID);
                return HookResult.Continue;
            });

            RegisterEventHandler<EventSmokegrenadeDetonate>(EventSmokegrenadeDetonateHandler);
            RegisterEventHandler<EventFlashbangDetonate>(EventFlashbangDetonateHandler);
            RegisterEventHandler<EventHegrenadeDetonate>(EventHegrenadeDetonateHandler);
            RegisterEventHandler<EventMolotovDetonate>(EventMolotovDetonateHandler);
            RegisterEventHandler<EventDecoyStarted>(EventDecoyDetonateHandler);

            Console.WriteLine($"[{ModuleName} {ModuleVersion} LOADED] MatchZy by WD- (https://github.com/shobhit-pathak/)");
        }
    }
}
