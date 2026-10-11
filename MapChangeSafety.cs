using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;

namespace MatchZy;

/// <summary>
/// Códigos fijos de los WARNING de CrashGuard. Cada uno corresponde a una de
/// las condiciones de carrera alrededor de un changelevel que pueden tirar el
/// server (análisis del crash del BO3 del 2026-10-11). Se loguean con el
/// prefijo `[WARNING] [CrashGuard/CODIGO]` para poder buscarlos y, si una
/// condición deja de aparecer durante varias versiones, descartarla.
/// </summary>
public static class CrashGuardCode
{
    /// <summary>
    /// Los bots que agrega el server al cargar un nivel no entran en los slots
    /// libres. Es lo que tiró el server el 2026-10-11: 13 humanos + GOTV + el
    /// primer bot llenaron los 15 slots y el segundo bot hizo segfault.
    /// </summary>
    public const string BotSlots = "BOT-SLOTS";

    /// <summary>warmup.cfg (mp_warmup_start + bot_kick) dos veces seguidas en el mismo nivel, o con el nivel cargando.</summary>
    public const string WarmupDouble = "WARMUP-DOUBLE";

    /// <summary>round_start del motor después del fin de mapa y antes del changelevel de MatchZy.</summary>
    public const string EngineRestart = "ENGINE-RESTART";

    /// <summary>Cambio de nivel que MatchZy no pidió, o que cargó otro mapa que el pedido.</summary>
    public const string UnexpectedLevel = "UNEXPECTED-LEVEL";

    /// <summary>Dos cambios de nivel superpuestos (uno pisando a otro agendado o ya emitido).</summary>
    public const string LevelOverlap = "LEVEL-OVERLAP";

    /// <summary>Un changelevel emitido que no terminó de cargar a tiempo.</summary>
    public const string LevelTimeout = "LEVEL-TIMEOUT";

    /// <summary>Comando rechazado, cancelado o diferido por un cambio de mapa en curso.</summary>
    public const string CommandBlocked = "CMD-BLOCKED";

    /// <summary>Intento de salir en vivo (ready, countdown, HandleMatchStart) durante un cambio de mapa.</summary>
    public const string ReadyWindow = "READY-WINDOW";

    /// <summary>El timer de KillCoaches disparó durante un cambio de mapa.</summary>
    public const string CoachTimer = "COACH-TIMER";

    /// <summary>SwitchTeam/Respawn durante la reconexión posterior a un changelevel o con el nivel cargando.</summary>
    public const string TeamSwitch = "TEAMSWITCH";

    /// <summary>La demo seguía grabando cuando salió el changelevel.</summary>
    public const string GotvSameFrame = "GOTV-SAME-FRAME";

    /// <summary>Estado del nivel anterior (coaches, controllers) que no se pudo conservar.</summary>
    public const string StaleState = "STALE-STATE";

    /// <summary>Modo práctica: host_timescale o estado de práctica que cruza un cambio de nivel.</summary>
    public const string Practice = "PRACTICE";

    /// <summary>El hilo del juego quedó bloqueado esperando HTTP o la base de datos.</summary>
    public const string MainThreadStall = "MAIN-THREAD-STALL";

    /// <summary>Un restore pendiente quedó sin forma de aplicarse tras el cambio de nivel.</summary>
    public const string RestoreStranded = "RESTORE-STRANDED";
}

/// <summary>
/// Ciclo de vida de los cambios de nivel y protecciones contra las carreras
/// que pueden tirar el server mientras un nivel se descarga o se carga.
///
/// Todo changelevel de MatchZy pasa por <see cref="ChangeMap"/>:
///   Scheduled (agendado) → Issued (changelevel emitido, nivel cargando)
///   → OnMapStart (<see cref="OnLevelStarted"/>) → +1 s (<see cref="OnLevelSettled"/>) → None.
/// Mientras la fase no es None, o mientras una serie está entre mapas
/// (<see cref="isBetweenSeriesMaps"/>), no se sale en vivo, no se aceptan
/// comandos que cambien de nivel o reinicien la partida, y nada toca
/// coaches ni jugadores del nivel anterior.
/// </summary>
public partial class MatchZy
{
    private enum LevelChangePhase
    {
        None,
        Scheduled,
        Issued,
    }

    private LevelChangePhase levelChangePhase = LevelChangePhase.None;

    /// <summary>
    /// Se incrementa con cada cambio de nivel agendado o cancelado. Los timers
    /// de un cambio viejo comparan su generación y se descartan solos.
    /// </summary>
    private int levelChangeGeneration = 0;

    private string levelChangeTargetMap = "";
    private string levelChangeSource = "";
    private DateTime levelChangeRequestedAtUtc = DateTime.MinValue;
    private DateTime levelChangeIssuedAtUtc = DateTime.MinValue;

    /// <summary>Humanos conectados (o conectando) cuando salió el changelevel; son los que van a reconectar.</summary>
    private int humansAtLevelChange = 0;

    /// <summary>
    /// BO3/BO5: true desde que termina un mapa de la serie hasta que el
    /// siguiente se estabiliza. Cubre también la espera previa al changelevel
    /// (restartDelay), en la que el motor puede reiniciar la partida por su cuenta.
    /// </summary>
    private bool isBetweenSeriesMaps = false;
    private DateTime seriesMapEndedAtUtc = DateTime.MinValue;

    /// <summary>Contador de niveles cargados desde que arrancó el plugin.</summary>
    private int levelSerial = 0;
    private string currentLevelMapName = "";

    /// <summary>
    /// Hasta cuándo se considera que los jugadores del nivel anterior siguen
    /// reconectando. Ahí no se reaplica warmup.cfg por "primer jugador".
    /// </summary>
    private DateTime reconnectWindowUntilUtc = DateTime.MinValue;
    private bool teamSwitchDuringReconnectWarned = false;

    /// <summary>Acciones que llegaron con el nivel cargando y se ejecutan cuando se estabiliza.</summary>
    private readonly List<(string Name, Action Run)> deferredUntilLevelReady = new();

    /// <summary>LoadMatchFromJSON con cambio de nivel: la espera de jugadores arranca en el mapa nuevo.</summary>
    private bool playerWaitDeferredToMapStart = false;

    /// <summary>Valor configurado de mp_match_restart_delay antes de que MatchZy lo extendiera.</summary>
    private int? matchRestartDelayBeforeExtension = null;

    /// <summary>Bots que agregaron los cfg del server al cargar el último nivel (-1 = todavía no medido).</summary>
    private int levelLoadBotCount = -1;

    private DateTime lastWarmupCfgExecAtUtc = DateTime.MinValue;
    private string lastWarmupCfgExecSource = "";
    private int lastWarmupCfgExecLevel = -1;

    /// <summary>
    /// steamid64 → "team1"/"team2" de los coaches al emitir el changelevel.
    /// Los controllers no sobreviven al cambio de nivel (los slots se
    /// reacomodan), así que se vuelven a asociar por SteamID al reconectar.
    /// </summary>
    private readonly Dictionary<string, string> coachCarryOver = new();

    private const float LevelChangeTimeoutSeconds = 180f;

    /// <summary>
    /// Segundos extra de mp_match_restart_delay sobre el changelevel de MatchZy.
    /// El motor cuenta el intermedio desde antes de cs_win_panel_match (~5 s
    /// antes en el log del 2026-10-11), así que sin margen su reinicio de fin
    /// de partida llegaba ANTES que el changelevel del plugin.
    /// </summary>
    private const int EngineMatchEndMarginSeconds = 30;

    private const float TvBroadcastStopToChangelevelDelay = 1.0f;
    private const float DemoStopToChangelevelDelay = 3.0f;
    private const double WarmupDoubleExecWindowSeconds = 10;
    private const double ReconnectWindowSeconds = 90;
    private const int MainThreadHttpTimeoutSeconds = 20;

    private bool IsLevelChangeInProgress => levelChangePhase != LevelChangePhase.None;

    /// <summary>Hay un cambio de nivel agendado/en curso o una serie entre mapas.</summary>
    private bool IsMapTransitionActive => IsLevelChangeInProgress || isBetweenSeriesMaps;

    private bool IsInReconnectWindow => DateTime.UtcNow < reconnectWindowUntilUtc;

    private string DescribeTransition()
    {
        DateTime now = DateTime.UtcNow;
        List<string> parts = new() { $"fase={levelChangePhase}" };
        if (IsLevelChangeInProgress)
        {
            parts.Add($"destino='{levelChangeTargetMap}'");
            parts.Add($"origen={levelChangeSource}");
            if (levelChangeRequestedAtUtc != DateTime.MinValue)
                parts.Add($"agendado hace {(now - levelChangeRequestedAtUtc).TotalSeconds:0.0} s");
            if (levelChangePhase == LevelChangePhase.Issued && levelChangeIssuedAtUtc != DateTime.MinValue)
                parts.Add($"emitido hace {(now - levelChangeIssuedAtUtc).TotalSeconds:0.0} s");
        }
        if (isBetweenSeriesMaps)
            parts.Add($"serie entre mapas desde hace {(now - seriesMapEndedAtUtc).TotalSeconds:0.0} s");
        return string.Join(", ", parts);
    }

    // ── Cambio de nivel ──────────────────────────────────────────────────

    /// <summary>
    /// Único punto por el que MatchZy cambia de nivel. Agenda el cambio; el
    /// changelevel sale en <see cref="ExecuteScheduledLevelChange"/>.
    /// </summary>
    private void ChangeMap(string mapName, float delay, string source)
    {
        if (levelChangePhase == LevelChangePhase.Issued)
        {
            // Un segundo changelevel con el nivel cargando es el doble cambio
            // de nivel que corrompe el estado del motor.
            LogWarning(CrashGuardCode.LevelOverlap, $"ChangeMap('{mapName}', origen={source}) con un changelevel ya emitido ({DescribeTransition()}). Se reprograma para cuando el nivel termine de cargar.");
            DeferUntilLevelReady($"ChangeMap '{mapName}' ({source})", () => ChangeMap(mapName, delay, source));
            return;
        }
        if (levelChangePhase == LevelChangePhase.Scheduled)
        {
            LogWarning(CrashGuardCode.LevelOverlap, $"ChangeMap('{mapName}', origen={source}) reemplaza a otro cambio de nivel agendado y todavía no emitido ({DescribeTransition()}).");
        }

        int generation = ++levelChangeGeneration;
        levelChangePhase = LevelChangePhase.Scheduled;
        levelChangeTargetMap = mapName;
        levelChangeSource = source;
        levelChangeRequestedAtUtc = DateTime.UtcNow;
        levelChangeIssuedAtUtc = DateTime.MinValue;

        // Un countdown vivo llamaría a HandleMatchStart (mp_restartgame,
        // live.cfg) con el nivel nuevo cargando.
        if (isCountdownActive)
        {
            LogWarning(CrashGuardCode.ReadyWindow, $"Había un countdown para salir en vivo al agendar el cambio a '{mapName}'; se cancela.");
        }
        countdownGeneration++;
        isCountdownActive = false;

        // KillCoaches no tiene nada que hacer en el nivel siguiente.
        coachKillTimer?.Kill();
        coachKillTimer = null;

        LogInfo("MapChange", $"Cambio de nivel agendado a '{mapName}' en {delay:0.#} s (origen={source}, nivel actual='{Server.MapName}', generación={generation}).");
        AddTimer(delay, () => ExecuteScheduledLevelChange(generation));
    }

    private void ExecuteScheduledLevelChange(int generation)
    {
        try
        {
            if (generation != levelChangeGeneration || levelChangePhase != LevelChangePhase.Scheduled)
            {
                LogInfo("MapChange", $"Cambio de nivel agendado (generación {generation}) descartado: se canceló o lo reemplazó otro ({DescribeTransition()}).");
                return;
            }

            string mapName = levelChangeTargetMap;
            if (!long.TryParse(mapName, out _) && !Server.IsMapValid(mapName))
            {
                // Antes esta rama era un no-op mudo, y es el peor final posible:
                // el flujo que llamó sigue montando la partida (warmup, cvars,
                // nombres) sobre el mapa VIEJO. Desde afuera se ve un arranque
                // normal en el mapa equivocado, y como ningún evento de MatchZy
                // lleva el mapa realmente cargado, el backend tampoco puede
                // notarlo: registra el mapa que pidió.
                Log($"[ChangeMap] ERROR: '{mapName}' no es un mapa válido en este servidor y no es un id de workshop. El mapa NO se cambió; la partida va a seguir en '{Server.MapName}'.");
                // Lo que se difirió esperando el nivel nuevo (warmup, ready,
                // espera de jugadores) se arranca sobre el nivel actual.
                OnLevelSettled(Server.MapName, fromLevelStart: false);
                return;
            }

            (bool tvBroadcastStopped, bool demoStopped) = StopTvForMapChange();
            ResetFastForwardBeforeLevelChange();

            if (demoStopped || tvBroadcastStopped)
            {
                // El cierre de la demo/GOTV y el changelevel en el mismo frame
                // es un patrón ya asociado a crashes (ver HandleMatchEnd).
                float gap = demoStopped ? DemoStopToChangelevelDelay : TvBroadcastStopToChangelevelDelay;
                LogInfo("MapChange", $"GOTV detenido (broadcast={tvBroadcastStopped}, demo={demoStopped}); el changelevel sale en {gap:0.#} s.");
                AddTimer(gap, () => IssueChangelevel(generation));
                return;
            }

            IssueChangelevel(generation);
        }
        catch (Exception ex)
        {
            Log($"[ExecuteScheduledLevelChange FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void IssueChangelevel(int generation)
    {
        if (generation != levelChangeGeneration || levelChangePhase != LevelChangePhase.Scheduled)
        {
            LogInfo("MapChange", $"Changelevel (generación {generation}) descartado antes de emitirse: se canceló o lo reemplazó otro ({DescribeTransition()}).");
            return;
        }

        string mapName = levelChangeTargetMap;
        (int connected, int connecting, _) = CountClientsBySlot();
        humansAtLevelChange = connected + connecting;
        CheckBotSlotRisk(mapName, humansAtLevelChange);
        SnapshotCoachesForLevelChange();

        levelChangePhase = LevelChangePhase.Issued;
        levelChangeIssuedAtUtc = DateTime.UtcNow;
        LogInfo("MapChange", $"Emitiendo changelevel a '{mapName}' (origen={levelChangeSource}, humanos={connected} conectados + {connecting} conectando, {(levelChangeIssuedAtUtc - levelChangeRequestedAtUtc).TotalSeconds:0.0} s después de agendarlo).");

        Server.ExecuteCommand("bot_kick");
        if (long.TryParse(mapName, out _))
        {
            Server.ExecuteCommand($"host_workshop_map \"{mapName}\"");
        }
        else
        {
            Server.ExecuteCommand($"changelevel \"{mapName}\"");
        }

        AddTimer(LevelChangeTimeoutSeconds, () =>
        {
            if (generation != levelChangeGeneration || levelChangePhase != LevelChangePhase.Issued) return;
            LogWarning(CrashGuardCode.LevelTimeout, $"'{mapName}' no terminó de cargar {LevelChangeTimeoutSeconds:0} s después del changelevel ({DescribeTransition()}). Se libera el estado de cambio de mapa sobre el nivel actual ('{Server.MapName}').");
            OnLevelSettled(Server.MapName, fromLevelStart: false);
        });
    }

    /// <summary>Cancela un cambio de nivel agendado que todavía no se emitió.</summary>
    private void CancelScheduledLevelChange(string reason)
    {
        if (levelChangePhase != LevelChangePhase.Scheduled) return;
        LogInfo("MapChange", $"Cambio de nivel agendado a '{levelChangeTargetMap}' cancelado por {reason}.");
        levelChangeGeneration++;
        levelChangePhase = LevelChangePhase.None;
        levelChangeTargetMap = "";
        levelChangeSource = "";
    }

    /// <summary>
    /// Listener OnMapEnd. Corre mientras el nivel se descarga: acá solo se
    /// toca estado administrado y se loguea, nada de entidades ni comandos.
    /// </summary>
    private void OnLevelEnding()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            switch (levelChangePhase)
            {
                case LevelChangePhase.Issued:
                    LogInfo("MapChange", $"Descargando '{currentLevelMapName}' por el changelevel a '{levelChangeTargetMap}' (origen={levelChangeSource}, emitido hace {(now - levelChangeIssuedAtUtc).TotalSeconds:0.0} s).");
                    return;

                case LevelChangePhase.Scheduled:
                    // Otro actor (el fin de partida del motor, un admin, RCON u
                    // otro plugin) cambió el nivel antes que el changelevel de
                    // MatchZy: era el doble cambio de nivel. Se cancela el
                    // pendiente; si cargó otro mapa, se vuelve a pedir el
                    // correcto cuando el nivel se estabilice.
                    LogWarning(CrashGuardCode.LevelOverlap, $"'{currentLevelMapName}' se descarga antes de que MatchZy emitiera su changelevel ({DescribeTransition()}). Otro actor cambió el nivel; se cancela el changelevel pendiente.");
                    levelChangeGeneration++;
                    levelChangePhase = LevelChangePhase.Issued;
                    levelChangeIssuedAtUtc = now;
                    levelChangeSource += " (pisado por un cambio externo)";
                    humansAtLevelChange = Math.Max(humansAtLevelChange, playerData.Count);
                    return;

                case LevelChangePhase.None:
                    if (isMatchSetup || matchStarted)
                    {
                        LogWarning(CrashGuardCode.UnexpectedLevel, $"'{currentLevelMapName}' se descarga sin que MatchZy lo pidiera, con una partida cargada (matchid {liveMatchId}, mapa {matchConfig.CurrentMapNumber}, live={isMatchLive}). Probable fin de partida del motor, changelevel por RCON/admin u otro plugin.");
                    }
                    else
                    {
                        LogInfo("MapChange", $"'{currentLevelMapName}' se descarga por un cambio de nivel externo (sin partida cargada).");
                    }
                    // Mientras carga el nivel nuevo valen los mismos bloqueos.
                    levelChangeGeneration++;
                    levelChangePhase = LevelChangePhase.Issued;
                    levelChangeTargetMap = "";
                    levelChangeSource = "externo";
                    levelChangeRequestedAtUtc = now;
                    levelChangeIssuedAtUtc = now;
                    humansAtLevelChange = playerData.Count;
                    coachCarryOver.Clear();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log($"[OnLevelEnding FATAL] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Listener OnMapStart (inmediato). Descarta el estado del nivel anterior
    /// antes de que reconecte nadie y abre la ventana de reconexión.
    /// </summary>
    private void OnLevelStarted(string mapName)
    {
        try
        {
            levelSerial++;
            currentLevelMapName = mapName;
            teamSwitchDuringReconnectWarned = false;
            DateTime now = DateTime.UtcNow;

            switch (levelChangePhase)
            {
                case LevelChangePhase.Issued:
                    // El nivel cargó: el timeout del changelevel ya no aplica
                    // (si no, una carga casi tan larga como el timeout
                    // estabilizaría el nivel dos veces).
                    levelChangeGeneration++;
                    LogInfo("MapChange", $"Nivel '{mapName}' cargado {(now - levelChangeIssuedAtUtc).TotalSeconds:0.0} s después del changelevel (origen={levelChangeSource}).");
                    if (levelChangeTargetMap != "" && !long.TryParse(levelChangeTargetMap, out _) &&
                        !string.Equals(levelChangeTargetMap, mapName, StringComparison.OrdinalIgnoreCase))
                    {
                        string retarget = levelChangeTargetMap;
                        string source = levelChangeSource;
                        LogWarning(CrashGuardCode.UnexpectedLevel, $"Se pidió '{retarget}' pero cargó '{mapName}' (origen={source}). Se vuelve a pedir '{retarget}' cuando el nivel se estabilice.");
                        DeferUntilLevelReady($"ChangeMap '{retarget}' (reintento)", () => ChangeMap(retarget, 3.0f, source + " (reintento)"));
                    }
                    break;

                case LevelChangePhase.Scheduled:
                    // OnMapStart sin OnMapEnd previo con un cambio agendado.
                    LogWarning(CrashGuardCode.LevelOverlap, $"Nivel '{mapName}' cargado con un changelevel de MatchZy todavía agendado ({DescribeTransition()}); se cancela el pendiente.");
                    levelChangeGeneration++;
                    levelChangePhase = LevelChangePhase.Issued;
                    levelChangeIssuedAtUtc = now;
                    break;

                case LevelChangePhase.None:
                    // Primer nivel del proceso o hot reload. Hasta que el nivel
                    // se estabilice valen los mismos bloqueos.
                    LogInfo("MapChange", $"Nivel '{mapName}' cargado (sin cambio de nivel de MatchZy en curso).");
                    levelChangeGeneration++;
                    levelChangePhase = LevelChangePhase.Issued;
                    levelChangeTargetMap = "";
                    levelChangeSource = "inicio de nivel";
                    levelChangeRequestedAtUtc = now;
                    levelChangeIssuedAtUtc = now;
                    break;
            }

            DiscardPreviousLevelState();

            if (humansAtLevelChange > 0)
            {
                reconnectWindowUntilUtc = now.AddSeconds(ReconnectWindowSeconds);
                LogInfo("MapChange", $"Ventana de reconexión abierta {ReconnectWindowSeconds:0} s: {humansAtLevelChange} humano(s) venían del nivel anterior.");
            }
            else
            {
                reconnectWindowUntilUtc = DateTime.MinValue;
            }
        }
        catch (Exception ex)
        {
            Log($"[OnLevelStarted FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Los controllers del nivel anterior no son válidos en el nuevo: los
    /// clientes se reactivan en otros slots ("Reactivate moving X from slot N
    /// to slot N-1") y un handle viejo puede terminar resolviendo a OTRO
    /// jugador. Se descarta todo antes de que reconecte nadie;
    /// player_connect_full lo vuelve a llenar.
    /// </summary>
    private void DiscardPreviousLevelState()
    {
        int stalePlayers = playerData.Count;
        int staleCoaches = matchzyTeam1.coach.Count + matchzyTeam2.coach.Count;

        playerData = new Dictionary<int, CCSPlayerController>();
        playerReadyStatus = new Dictionary<int, bool>();
        connectedPlayers = 0;

        if (staleCoaches > 0)
        {
            matchzyTeam1.coach = new();
            matchzyTeam2.coach = new();
            if (coachCarryOver.Count < staleCoaches)
            {
                LogWarning(CrashGuardCode.StaleState, $"{staleCoaches - coachCarryOver.Count} coach(es) del nivel anterior no se pudieron conservar (cambio de nivel no iniciado por MatchZy o controller ya inválido); tienen que volver a usar .coach.");
            }
        }

        // Spawns del mapa anterior: HandleCoaches las recarga del mapa nuevo.
        spawnsData = GetEmptySpawnsData();
        coachSpawns = GetEmptySpawnsData();

        coachKillTimer?.Kill();
        coachKillTimer = null;
        // STOP_ON_MAPCHANGE: el timer system ya lo borró; un Kill() posterior
        // podría pegarle a otro timer que reutilice la dirección.
        collisionGroupTimer = null;

        if (stalePlayers > 0 || staleCoaches > 0)
        {
            LogInfo("MapChange", $"Estado del nivel anterior descartado: {stalePlayers} jugador(es) y {staleCoaches} coach(es); se vuelven a registrar al reconectar.");
        }
    }

    /// <summary>
    /// OnMapStart + 1 s: el nivel ya está estable. Levanta los bloqueos,
    /// arranca la fase que corresponda, reabre el ready y ejecuta lo diferido.
    /// También se usa como salida de emergencia (mapa inválido, timeout).
    /// </summary>
    private void OnLevelSettled(string mapName, bool fromLevelStart)
    {
        try
        {
            string source = levelChangeSource;
            bool wasBetweenSeriesMaps = isBetweenSeriesMaps;

            // 1) Se levantan los bloqueos ANTES de arrancar el warmup del nivel
            //    nuevo, que si no se reportaría como exec durante la carga.
            levelChangePhase = LevelChangePhase.None;
            levelChangeTargetMap = "";
            isBetweenSeriesMaps = false;
            RestoreMatchRestartDelay();

            if (fromLevelStart) MeasureLevelLoadBots(mapName);

            if (isRoundRestorePending && matchStarted)
            {
                LogWarning(CrashGuardCode.RestoreStranded, $"Restore pendiente ('{pendingRestoreFileName}') con la partida marcada como iniciada al cargar '{mapName}': ni el warmup ni el ready lo van a aplicar. Usar matchzy_loadbackup de nuevo o css_endmatch.");
            }

            // 2) Lo que antes hacía el timer de OnMapStart.
            if (!isMatchSetup)
            {
                AutoStart();
            }
            else
            {
                if (isWarmup) StartWarmup("carga de nivel");
                if (isPractice) StartPracticeMode();

                // Notifica a servicios externos que el warmup del mapa actual
                // ya está activo (post `changelevel` + cfg de warmup). Permite
                // a integraciones evitar timers/race conditions tipo "esperar X
                // segundos antes de chequear jugadores en BO3".
                if (isWarmup && liveMatchId > 0)
                {
                    var warmupStartedEvent = new MapWarmupStartedEvent
                    {
                        MatchId = liveMatchId,
                        MapNumber = matchConfig.CurrentMapNumber
                    };
                    Task.Run(async () => await SendEventAsync(warmupStartedEvent));

                    // Reinicia el watchdog de espera de jugadores SOLO para
                    // los mapas posteriores de una serie BO3/BO5
                    // (CurrentMapNumber > 0). El primer mapa lo arranca el
                    // diferido de LoadMatchFromJSON (abajo) y volver a llamarlo
                    // provocaba doble inicialización + doble exec de warmup.cfg
                    // al mismo tiempo que la primera ronda de jugadores
                    // conectaba, lo que llegaba a hacer crashear al server
                    // CS2 (segfault en spawn points).
                    if (!matchStarted && matchConfig.CurrentMapNumber > 0)
                    {
                        StartPlayerWaitSystem();
                    }
                }

                if (playerWaitDeferredToMapStart)
                {
                    playerWaitDeferredToMapStart = false;
                    if (!matchStarted)
                    {
                        LogInfo("MapChange", "Arrancando la espera de jugadores diferida por LoadMatchFromJSON.");
                        StartPlayerWaitSystem();
                    }
                }
            }

            LogInfo("MapChange", $"Cambio de nivel completo en '{mapName}' (origen={source}{(wasBetweenSeriesMaps ? ", siguiente mapa de la serie" : "")}).");

            // 3) Lo que llegó con el nivel cargando. Va antes del ready: puede
            //    agendar otro cambio de nivel (reintento del mapa pedido).
            RunDeferredLevelActions();

            // 4) Ready: durante la transición quedó cerrado a propósito.
            if (isMatchSetup && isWarmup && !matchStarted && !readyAvailable && !IsLevelChangeInProgress)
            {
                readyAvailable = true;
                LogInfo("MapChange", "Ready reabierto en el nivel nuevo.");
            }

            // 5) Los que reconectaron antes de reabrir el ready no dispararon
            //    el chequeo de salida en vivo.
            if (isMatchSetup && readyAvailable && !matchStarted && !IsLevelChangeInProgress)
            {
                CheckLiveRequired();
            }
        }
        catch (Exception ex)
        {
            Log($"[OnLevelSettled FATAL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void DeferUntilLevelReady(string name, Action action)
    {
        deferredUntilLevelReady.Add((name, action));
        LogInfo("MapChange", $"'{name}' diferido hasta que termine el cambio de mapa ({DescribeTransition()}).");
    }

    private void RunDeferredLevelActions()
    {
        if (deferredUntilLevelReady.Count == 0) return;
        var pending = deferredUntilLevelReady.ToList();
        deferredUntilLevelReady.Clear();
        foreach (var (name, run) in pending)
        {
            LogInfo("MapChange", $"Ejecutando '{name}' (diferido por el cambio de mapa).");
            try
            {
                run();
            }
            catch (Exception ex)
            {
                Log($"[RunDeferredLevelActions FATAL] '{name}': {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ── Serie entre mapas (BO3/BO5) ──────────────────────────────────────

    /// <summary>
    /// Fin de un mapa que no termina la serie. Desde acá hasta que el mapa
    /// siguiente se estabiliza, lo que pase en el nivel viejo ya no es la
    /// partida: el reinicio de fin de partida del motor dispara un round_start
    /// que no tiene que crear backups, tocar coaches ni reabrir el ready.
    /// </summary>
    private void BeginSeriesMapTransition(string nextMap, int restartDelay, int configuredRestartDelay)
    {
        isBetweenSeriesMaps = true;
        seriesMapEndedAtUtc = DateTime.UtcNow;
        isMatchLive = false;

        coachKillTimer?.Kill();
        coachKillTimer = null;

        // El changelevel de MatchZy sale en restartDelay - 1. El fin de partida
        // del motor se corre bien después para que nunca le gane.
        int engineDelay = restartDelay + EngineMatchEndMarginSeconds;
        matchRestartDelayBeforeExtension ??= configuredRestartDelay;
        ConVar.Find("mp_match_restart_delay")!.SetValue(engineDelay);

        LogInfo("MapChange", $"Mapa terminado; siguiente de la serie: '{nextMap}' (mapa {matchConfig.CurrentMapNumber}). Changelevel de MatchZy en ~{restartDelay - 1} s; mp_match_restart_delay del motor en {engineDelay} s (configurado: {configuredRestartDelay} s).");

        // Red de seguridad: mientras la serie está entre mapas todo queda
        // bloqueado (ready, comandos). Si por un camino no previsto el cambio
        // de nivel nunca arranca, se libera en vez de dejar la partida trabada.
        DateTime endedAt = seriesMapEndedAtUtc;
        AddTimer(restartDelay + LevelChangeTimeoutSeconds, () =>
        {
            if (!isBetweenSeriesMaps || seriesMapEndedAtUtc != endedAt || IsLevelChangeInProgress) return;
            LogWarning(CrashGuardCode.LevelTimeout, $"La serie sigue entre mapas {(DateTime.UtcNow - endedAt).TotalSeconds:0} s después del fin de mapa sin un cambio de nivel en curso ({DescribeTransition()}); se libera el bloqueo.");
            isBetweenSeriesMaps = false;
            RestoreMatchRestartDelay();
        });
    }

    private void RestoreMatchRestartDelay()
    {
        if (matchRestartDelayBeforeExtension is not int original) return;
        matchRestartDelayBeforeExtension = null;
        ConVar.Find("mp_match_restart_delay")!.SetValue(original);
        LogInfo("MapChange", $"mp_match_restart_delay vuelve a {original} s.");
    }

    /// <summary>ResetMatch: la partida que estaba entre mapas o con un cambio agendado ya no sigue.</summary>
    private void ResetMapTransitionState()
    {
        CancelScheduledLevelChange("ResetMatch");
        if (isBetweenSeriesMaps)
        {
            LogInfo("MapChange", "Transición entre mapas de la serie terminada por ResetMatch.");
            isBetweenSeriesMaps = false;
        }
        playerWaitDeferredToMapStart = false;
        coachCarryOver.Clear();
    }

    // ── Guardas de comandos ──────────────────────────────────────────────

    /// <summary>
    /// true si el comando se rechazó por un cambio de mapa en curso. Para
    /// comandos que cambian de nivel, reinician fases o cargan partidas.
    /// </summary>
    private bool BlockedByMapTransition(CCSPlayerController? player, string commandName)
    {
        if (!IsMapTransitionActive) return false;
        LogWarning(CrashGuardCode.CommandBlocked, $"'{commandName}' rechazado: hay un cambio de mapa en curso ({DescribeTransition()}).");
        ReplyToUserCommand(player, "Hay un cambio de mapa en curso; reintentá cuando termine de cargar el mapa.");
        return true;
    }

    /// <summary>
    /// css_endmatch / css_restart durante un cambio de nivel. Con el cambio
    /// solo agendado se cancela y el comando sigue; con el changelevel ya
    /// emitido se difiere hasta que el nivel cargue. true si quedó diferido.
    /// </summary>
    private bool DeferResetDuringLevelChange(CCSPlayerController? player, string commandName, Action run)
    {
        if (levelChangePhase == LevelChangePhase.Scheduled)
        {
            LogWarning(CrashGuardCode.CommandBlocked, $"'{commandName}' con un cambio de nivel agendado ({DescribeTransition()}); se cancela ese cambio y el comando sigue.");
            CancelScheduledLevelChange($"'{commandName}'");
            return false;
        }
        if (levelChangePhase == LevelChangePhase.Issued)
        {
            LogWarning(CrashGuardCode.CommandBlocked, $"'{commandName}' con el nivel cargando ({DescribeTransition()}); se ejecuta cuando termine de cargar.");
            ReplyToUserCommand(player, "Hay un cambio de mapa en curso: el comando se va a ejecutar cuando termine de cargar el mapa.");
            DeferUntilLevelReady(commandName, run);
            return true;
        }
        return false;
    }

    // ── Bots y slots ─────────────────────────────────────────────────────

    /// <summary>Humanos conectados, humanos conectando/reconectando y bots, recorriendo todos los slots.</summary>
    private (int Connected, int Connecting, int Bots) CountClientsBySlot()
    {
        int connected = 0, connecting = 0, bots = 0;
        for (int slot = 0; slot < Server.MaxPlayers; slot++)
        {
            CCSPlayerController? client = Utilities.GetPlayerFromSlot(slot);
            if (client == null || !client.IsValid || client.IsHLTV) continue;
            if (client.IsBot)
            {
                if (client.Connected == PlayerConnectedState.Connected) bots++;
                continue;
            }
            if (client.Connected == PlayerConnectedState.Connected) connected++;
            else if (client.Connected == PlayerConnectedState.Connecting || client.Connected == PlayerConnectedState.Reconnecting) connecting++;
        }
        return (connected, connecting, bots);
    }

    private int CountGotvSlots()
    {
        int slots = 0;
        if (ConVar.Find("tv_enable")?.GetPrimitiveValue<bool>() == true) slots++;
        if (ConVar.Find("tv_enable1")?.GetPrimitiveValue<bool>() == true) slots++;
        return slots;
    }

    /// <summary>
    /// Antes del changelevel: ¿entran los humanos que van a reconectar, la GOTV
    /// y los bots que el server agrega al cargar el nivel?
    /// </summary>
    private void CheckBotSlotRisk(string mapName, int humans)
    {
        try
        {
            int maxPlayers = Server.MaxPlayers;
            int tvSlots = CountGotvSlots();
            if (levelLoadBotCount < 0)
            {
                LogInfo("MapChange", $"Slots para '{mapName}': {humans} humano(s) + {tvSlots} GOTV de {maxPlayers}. Todavía no se midió cuántos bots agrega el server al cargar un nivel.");
                return;
            }

            int needed = humans + tvSlots + levelLoadBotCount;
            if (levelLoadBotCount > 0 && needed > maxPlayers)
            {
                LogWarning(CrashGuardCode.BotSlots, $"Riesgo de crash al cargar '{mapName}': {humans} humano(s) + {tvSlots} GOTV + {levelLoadBotCount} bot(s) que el server agrega al cargar cada nivel = {needed} > maxplayers {maxPlayers}. Así se cayó el server el 2026-10-11. Poner 'bot_quota 0' en cfg/gamemode_competitive_server.cfg.");
            }
            else if (levelLoadBotCount > 0)
            {
                LogInfo("MapChange", $"Slots para '{mapName}': {needed}/{maxPlayers} ({humans} humano(s) + {tvSlots} GOTV + {levelLoadBotCount} bot(s) de carga de nivel).");
            }
            else
            {
                LogInfo("MapChange", $"Slots para '{mapName}': {humans + tvSlots}/{maxPlayers}; el server no agrega bots al cargar el nivel.");
            }
        }
        catch (Exception ex)
        {
            Log($"[CheckBotSlotRisk] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Al estabilizarse el nivel y ANTES de que warmup.cfg/sleep.cfg los saque:
    /// cuántos bots agregaron los cfg del server al cargar.
    /// </summary>
    private void MeasureLevelLoadBots(string mapName)
    {
        try
        {
            (int connected, int connecting, int bots) = CountClientsBySlot();
            levelLoadBotCount = bots;
            int used = connected + connecting + bots + CountGotvSlots();
            string message = $"'{mapName}' estable: {bots} bot(s) agregados al cargar, {connected} humano(s) conectados y {connecting} conectando; {used}/{Server.MaxPlayers} slots.";
            if (bots > 0 && used >= Server.MaxPlayers)
            {
                LogWarning(CrashGuardCode.BotSlots, message + " El server quedó lleno durante la carga.");
            }
            else
            {
                LogInfo("MapChange", message);
            }
        }
        catch (Exception ex)
        {
            Log($"[MeasureLevelLoadBots] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Al cargar el plugin: el motor ejecuta gamemode_competitive_server.cfg
    /// después de gamemode_competitive.cfg en cada carga de nivel. Sin
    /// `bot_quota 0` ahí, los cfg del server reponen la cuota y el motor
    /// agrega bots antes de que MatchZy pueda sacarlos.
    /// </summary>
    private void CheckLevelLoadBotQuotaConfig()
    {
        try
        {
            string path = Path.Join(Server.GameDirectory, "csgo", "cfg", "gamemode_competitive_server.cfg");
            if (!File.Exists(path))
            {
                LogWarning(CrashGuardCode.BotSlots, $"No existe {path}: los cfg del server reponen bot_quota al cargar cada nivel y el motor agrega bots en plena reconexión. Copiar cfg/gamemode_competitive_server.cfg de MatchZy (bot_quota 0).");
                return;
            }

            string? quota = null;
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Split("//")[0].Trim();
                if (!line.StartsWith("bot_quota ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("bot_quota\t", StringComparison.OrdinalIgnoreCase)) continue;
                quota = line["bot_quota".Length..].Trim().Trim('"');
            }

            if (quota == "0")
            {
                LogInfo("MapChange", $"{path} fija bot_quota 0 en cada carga de nivel.");
            }
            else
            {
                LogWarning(CrashGuardCode.BotSlots, $"{path} no fija 'bot_quota 0' (valor: {quota ?? "sin definir"}); el motor puede agregar bots al cargar cada nivel.");
            }
        }
        catch (Exception ex)
        {
            Log($"[CheckLevelLoadBotQuotaConfig] {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Coaches entre niveles ────────────────────────────────────────────

    private void SnapshotCoachesForLevelChange()
    {
        coachCarryOver.Clear();
        foreach ((Team team, string key) in new[] { (matchzyTeam1, "team1"), (matchzyTeam2, "team2") })
        {
            foreach (CCSPlayerController coach in team.coach)
            {
                if (!coach.IsValid) continue;
                coachCarryOver[coach.SteamID.ToString()] = key;
            }
        }
        if (coachCarryOver.Count > 0)
        {
            LogInfo("Coach", $"{coachCarryOver.Count} coach(es) se conservan por SteamID para el nivel siguiente: {string.Join(", ", coachCarryOver.Keys)}.");
        }
    }

    /// <summary>player_connect_full en el nivel nuevo: vuelve a registrar al coach.</summary>
    private void RestoreCarriedOverCoach(CCSPlayerController player)
    {
        if (coachCarryOver.Count == 0) return;
        string steamId = player.SteamID.ToString();
        if (!coachCarryOver.Remove(steamId, out string? teamKey)) return;

        Team team = teamKey == "team1" ? matchzyTeam1 : matchzyTeam2;
        team.coach.Add(player);
        player.Clan = $"[{team.teamName} COACH]";
        LogInfo("Coach", $"{player.PlayerName} ({steamId}) vuelve a ser coach de {team.teamName} tras el cambio de nivel.");
    }

    // ── Práctica ─────────────────────────────────────────────────────────

    /// <summary>
    /// El fast-forward de práctica deja host_timescale en 10 durante 20 s y
    /// lo restaura un timer que exige seguir en práctica: si el nivel cambia
    /// en el medio, el server se quedaba acelerado.
    /// </summary>
    private void ResetFastForwardBeforeLevelChange()
    {
        try
        {
            ConVar? timescale = ConVar.Find("host_timescale");
            if (timescale == null) return;
            float value = timescale.GetPrimitiveValue<float>();
            if (Math.Abs(value - 1f) < 0.001f) return;
            LogWarning(CrashGuardCode.Practice, $"host_timescale estaba en {value} al cambiar de nivel (fast-forward de práctica en curso); se vuelve a 1.");
            Server.ExecuteCommand("host_timescale 1");
        }
        catch (Exception ex)
        {
            Log($"[ResetFastForwardBeforeLevelChange] {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Bloqueos del hilo del juego ──────────────────────────────────────

    private void ReportMainThreadStall(string operation, TimeSpan elapsed, double warnAfterSeconds = 1.0)
    {
        if (elapsed.TotalSeconds >= warnAfterSeconds)
        {
            LogWarning(CrashGuardCode.MainThreadStall, $"{operation} bloqueó el hilo del juego {elapsed.TotalSeconds:0.00} s (sin ticks ni respuestas RCON mientras tanto).");
        }
        else
        {
            LogInfo("Stall", $"{operation} tardó {elapsed.TotalMilliseconds:0} ms en el hilo del juego.");
        }
    }
}
