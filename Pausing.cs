using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace MatchZy;

public partial class MatchZy
{
    // Pausas técnicas (.tech) que usó cada equipo en el mapa actual. Se limpia
    // en StartLive; el límite es matchzy_max_tech_pauses_allowed.
    public Dictionary<Team, int> technicalPauseUsed = new();

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
}
