using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace MatchZy;

public partial class MatchZy
{
    // Pausas técnicas (.tech) que usó cada equipo en el mapa actual. Se limpia
    // al empezar el cuchillo, en StartLive y en ResetMatch; el límite es
    // matchzy_max_tech_pauses_allowed. Las del cuchillo no descuentan del cupo
    // del mapa: StartLive las vuelve a cero.
    public Dictionary<Team, int> technicalPauseUsed = new();

    /// <summary>
    /// Fases en las que los equipos pueden usar .pause, .tech y .unpause: la
    /// partida en vivo y la ronda de cuchillo.
    /// </summary>
    private bool IsPausablePhase => isMatchLive || isKnifeRound;

    private enum CarriedPauseKind
    {
        Admin,
        Tech,
        Tactical,
    }

    /// <summary>
    /// Pausa que se pidió y todavía no llegó a aplicarse cuando terminó el
    /// cuchillo (o durante la elección de lado). mp_pause_match y los timeouts
    /// solo arrancan en el próximo freezetime, y después del cuchillo el
    /// siguiente es el de la primera ronda en vivo, con el warmup de elección de
    /// lado y el mp_restartgame de live.cfg en el medio. Se suelta en el motor y
    /// se vuelve a pedir ya en vivo (<see cref="ApplyPauseCarriedToLive"/>).
    /// </summary>
    private (CarriedPauseKind Kind, Team? Team)? pauseCarriedToLive = null;

    /// <summary>
    /// Segundos después de StartLive para volver a pedir la pausa trasladada:
    /// live.cfg reinicia con mp_restartgame 1, así que a los 3 s ya es el
    /// freezetime de la primera ronda en vivo (mp_freezetime 18).
    /// </summary>
    private const float CarriedPauseApplyDelaySeconds = 3.0f;

    // .unpause durante el timeout táctico de .pause: lo cancela antes de que
    // termine. Solo puede hacerlo el equipo que lo pidió, sin confirmación del rival.
    private void CancelTacticalPause(CCSPlayerController? player)
    {
        if (player == null) return;

        var gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First();
        var gameRules = gameRulesProxy.GameRules!;
        if (!gameRules.TerroristTimeOutActive && !gameRules.CTTimeOutActive) return;

        // El timeout es del lado que lo pidió (timeout_terrorist_start / timeout_ct_start).
        string pauseSide;
        if (player.TeamNum == 2 && gameRules.TerroristTimeOutActive)
        {
            pauseSide = "TERRORIST";
            gameRules.TerroristTimeOutActive = false;
            gameRules.TerroristTimeOutRemaining = 0;
        }
        else if (player.TeamNum == 3 && gameRules.CTTimeOutActive)
        {
            pauseSide = "CT";
            gameRules.CTTimeOutActive = false;
            gameRules.CTTimeOutRemaining = 0;
        }
        else
        {
            if (player.TeamNum == 2 || player.TeamNum == 3)
            {
                string otherSide = gameRules.TerroristTimeOutActive ? "TERRORIST" : "CT";
                PrintToPlayerChat(player, Localizer["matchzy.pause.onlypausingteamcancancel", reverseTeamSides[otherSide].teamName]);
            }
            return;
        }

        // Apagar el flag corta el timeout, igual que en RestoreRoundBackup (o
        // evita que arranque, si se pidió en plena ronda). Pero el timeout retiene
        // el freezetime con el mismo estado que mp_pause_match —por eso, según
        // RestoreRoundBackup, al terminar despausa también una pausa de admin—, y
        // cortado a mano ese estado queda puesto: sin el unpause la ronda no arranca.
        Utilities.SetStateChanged(gameRulesProxy, "CCSGameRulesProxy", "m_pGameRules");
        Server.ExecuteCommand("mp_unpause_match;");

        PrintToAllChat(Localizer["matchzy.pause.teamcancelledpause", reverseTeamSides[pauseSide].teamName]);
    }

    /// <summary>
    /// Fin del cuchillo o inicio de la partida en vivo: si quedó una pausa
    /// pedida que el motor todavía no aplicó, se suelta y se guarda para
    /// pedirla de nuevo en el freezetime de la primera ronda en vivo.
    /// </summary>
    private void CarryPendingPauseToLive(string phase)
    {
        var gameRulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First();
        var gameRules = gameRulesProxy.GameRules!;

        (CarriedPauseKind Kind, Team? Team)? pending = null;
        if (isPaused)
        {
            // Una pausa aplicada en el freezetime del cuchillo no deja que la
            // ronda arranque: si se llega acá con isPaused, el motor todavía no
            // la aplicó (se pidió con la ronda en juego).
            string pauseTeam = (string)unpauseData["pauseTeam"];
            Team? team = pauseTeam == matchzyTeam1.teamName ? matchzyTeam1
                : pauseTeam == matchzyTeam2.teamName ? matchzyTeam2
                : null;
            pending = team == null ? (CarriedPauseKind.Admin, null) : (CarriedPauseKind.Tech, team);
            UnpauseMatch();
        }
        else if (gameRules.TerroristTimeOutActive || gameRules.CTTimeOutActive)
        {
            // Timeout táctico pedido con la ronda en juego: queda activo
            // esperando un freezetime. Se apaga igual que en CancelTacticalPause.
            Team team = gameRules.TerroristTimeOutActive ? reverseTeamSides["TERRORIST"] : reverseTeamSides["CT"];
            gameRules.TerroristTimeOutActive = false;
            gameRules.TerroristTimeOutRemaining = 0;
            gameRules.CTTimeOutActive = false;
            gameRules.CTTimeOutRemaining = 0;
            Utilities.SetStateChanged(gameRulesProxy, "CCSGameRulesProxy", "m_pGameRules");
            Server.ExecuteCommand("mp_unpause_match;");
            pending = (CarriedPauseKind.Tactical, team);
        }

        if (pending == null) return;

        pauseCarriedToLive = pending;
        LogInfo("Pause", $"Pausa {pending.Value.Kind} ({pending.Value.Team?.teamName ?? "Admin"}) pedida en {phase} sin aplicarse todavía: se pide de nuevo en el freezetime de la primera ronda en vivo.");
        PrintToAllChat(Localizer["matchzy.pause.carriedtolive"]);
    }

    /// <summary>Vuelve a pedir, ya en vivo, la pausa que quedó pendiente del cuchillo.</summary>
    private void ApplyPauseCarriedToLive()
    {
        if (pauseCarriedToLive is not { } carried) return;
        (CarriedPauseKind kind, Team? team) = carried;
        pauseCarriedToLive = null;

        if (!isMatchLive)
        {
            LogInfo("Pause", $"Pausa {kind} trasladada del cuchillo descartada: la partida ya no está en vivo.");
            return;
        }
        if (isPaused || IsTacticalTimeoutActive())
        {
            LogInfo("Pause", $"Pausa {kind} trasladada del cuchillo descartada: ya hay una pausa activa.");
            return;
        }

        switch (kind)
        {
            case CarriedPauseKind.Admin:
                unpauseData["pauseTeam"] = "Admin";
                PrintToAllChat(Localizer["matchzy.pause.adminpausedthematch"]);
                SetMatchPausedFlags();
                break;

            case CarriedPauseKind.Tech:
                // StartLive vació el cupo del mapa: esta pausa ya cuenta en vivo.
                technicalPauseUsed.TryGetValue(team!, out int pausesUsed);
                technicalPauseUsed[team!] = pausesUsed + 1;
                unpauseData["pauseTeam"] = team!.teamName;
                PrintToAllChat(Localizer["matchzy.pause.pausedthematch", team!.teamName]);
                SetMatchPausedFlags();
                break;

            case CarriedPauseKind.Tactical:
                // El lado se resuelve ahora: el ganador del cuchillo pudo cambiar de bando.
                bool isCt = teamSides[team!] == "CT";
                var gameRules = GetGameRules();
                if ((isCt ? gameRules.CTTimeOuts : gameRules.TerroristTimeOuts) <= 0)
                {
                    LogInfo("Pause", $"Timeout táctico trasladado del cuchillo descartado: {team!.teamName} no tiene timeouts en vivo.");
                    return;
                }
                Server.ExecuteCommand($"mp_team_timeout_time {pauseDuration.Value}; {(isCt ? "timeout_ct_start" : "timeout_terrorist_start")}");
                break;
        }

        LogInfo("Pause", $"Pausa {kind} ({team?.teamName ?? "Admin"}) trasladada del cuchillo aplicada en la primera ronda en vivo.");
    }
}
