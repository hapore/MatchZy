using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace MatchZy
{
    public partial class MatchZy
    {
        // El panel HTML central (show_survival_respawn_status, lo que usa
        // PrintToCenterHtml) se apaga solo si no se lo vuelve a mandar: por eso
        // cs2-retakes-allocator y CS2-Deathmatch lo reenvían en cada tick. Cada
        // envío lleva una duración corta para que desaparezca enseguida después
        // del último.
        private const int HaporeBrandingEventDuration = 1;

        // Hasta cuándo se reenvía el banner; null = apagado. En hora real y no en
        // Server.CurrentTime, que vuelve a empezar con cada mapa.
        private DateTime? haporeBrandingHideAtUtc = null;
        private string haporeBrandingHtml = "";

        // Invalida el NextFrame/timer del delay si entretanto empezó otra ronda o
        // terminó el freezetime.
        private int haporeBrandingGeneration = 0;

        // A la demo sólo llega lo que se le manda directo al slot de GOTV
        // (PrintToCenterHtml sobre su controller, con su userid). Difundir el
        // evento a todos con FireEvent no queda grabado, ni con el userid de GOTV
        // ni sin userid como en las demos de FACEIT: se probó con demos el
        // 2026-10-20 y sólo aparecían los eventos del envío directo.
        private CCSPlayerController? haporeBrandingGotv = null;
        private string? haporeBrandingGotvLogState = null;
        private bool haporeBrandingGotvSentLogged = false;
        private bool haporeBrandingErrorLogged = false;

        /// <summary>
        /// Banner de HAPORE al empezar cada ronda en vivo, con la misma estructura
        /// que el anuncio de site de cs2-retakes-allocator: NextFrame, un delay
        /// (matchzy_hapore_branding_delay) y recién ahí la imagen por URL en el
        /// panel HTML central, reenviada en cada tick durante
        /// matchzy_hapore_branding_seconds.
        ///
        /// Si en vez de la imagen aparece el contenedor vacío (una barra gris o
        /// blanca), el cliente no pudo bajarla. No es el momento del envío: pasó
        /// con nginx sin ciphers TLS 1.2 para el certificado ECDSA, con la URL
        /// cacheada como fallida por el cliente (de ahí el ?v=) y con las
        /// comillas que dejaba el config.cfg (ver CleanCfgString).
        /// </summary>
        private void ShowHaporeBranding()
        {
            HideHaporeBranding();
            if (!haporeBrandingEnabled.Value) return;

            string html = BuildHaporeBrandingHtml();
            if (html == "") return;

            int generation = haporeBrandingGeneration;
            Server.NextFrame(() =>
            {
                if (generation != haporeBrandingGeneration) return;
                AddTimer(Math.Max(0.1f, haporeBrandingDelay.Value), () =>
                {
                    if (generation != haporeBrandingGeneration || !isMatchLive) return;
                    haporeBrandingHtml = html;
                    haporeBrandingErrorLogged = false;
                    haporeBrandingGotv = FindGotvController();
                    haporeBrandingHideAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, haporeBrandingSeconds.Value));
                }, TimerFlags.STOP_ON_MAPCHANGE);
            });
        }

        private void HideHaporeBranding()
        {
            haporeBrandingGeneration++;
            haporeBrandingHideAtUtc = null;
        }

        private string BuildHaporeBrandingHtml()
        {
            string imageUrl = CleanCfgString(haporeBrandingImageUrl.Value);
            if (imageUrl != "")
            {
                return $"<img src='{imageUrl}' class=''>";
            }

            string text = CleanCfgString(haporeBrandingText.Value);
            if (text == "") return "";
            return $"<font color='#F1EFDC'>{WebUtility.HtmlEncode(text)}</font>";
        }

        /// <summary>
        /// Mismo saneo que techPauseFlag (Utility.cs). CSS sólo le saca las
        /// comillas a una FakeConVar&lt;string&gt; si el valor termina justo en la
        /// comilla, y desde config.cfg con fines de línea CRLF llega con un \r
        /// detrás: el valor queda "https://..." con comillas y la imagen no carga.
        /// Escrita en la consola no pasa, por eso el comando sí funcionaba.
        /// </summary>
        private static string CleanCfgString(string value)
        {
            return value.Trim().Trim('"').Trim();
        }

        private void OnHaporeBrandingTick()
        {
            if (haporeBrandingHideAtUtc == null) return;
            // Fin de partida o cambio de mapa con el banner todavía en pantalla.
            if (!isMatchLive || DateTime.UtcNow >= haporeBrandingHideAtUtc)
            {
                HideHaporeBranding();
                return;
            }

            for (int slot = 0; slot < Server.MaxPlayers; slot++)
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
                // GOTV también es FL_FAKECLIENT (IsBot); va aparte, abajo.
                // Los bots no tienen listener de eventos: FireEventToClient tira.
                if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) continue;
                if (player.Connected != PlayerConnectedState.Connected) continue;

                try
                {
                    player.PrintToCenterHtml(haporeBrandingHtml, HaporeBrandingEventDuration);
                }
                catch (Exception e)
                {
                    // Una vez por ronda: el tick lo repetiría 64 veces por segundo.
                    if (haporeBrandingErrorLogged) continue;
                    haporeBrandingErrorLogged = true;
                    LogInfo("Branding", $"No se pudo mostrar el banner a {player.PlayerName} (slot {slot}): {e.Message}");
                }
            }

            SendHaporeBrandingToGotv();
        }

        private void SendHaporeBrandingToGotv()
        {
            CCSPlayerController? gotv = haporeBrandingGotv;
            if (gotv == null || !gotv.IsValid) return;

            try
            {
                gotv.PrintToCenterHtml(haporeBrandingHtml, HaporeBrandingEventDuration);
                if (!haporeBrandingGotvSentLogged)
                {
                    haporeBrandingGotvSentLogged = true;
                    LogInfo("Branding", $"Banner enviado a GOTV (slot {gotv.Slot}) para que quede en la demo.");
                }
            }
            catch (Exception e)
            {
                if (haporeBrandingErrorLogged) return;
                haporeBrandingErrorLogged = true;
                LogInfo("Branding", $"No se pudo mandar el banner a GOTV (slot {gotv.Slot}): {e.Message}");
            }
        }

        /// <summary>
        /// Busca el controller de GOTV entre todos los controllers, no sólo en
        /// los slots que recorre el tick. Loguea cuando cambia lo encontrado.
        /// </summary>
        private CCSPlayerController? FindGotvController()
        {
            CCSPlayerController? gotv = null;
            foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (controller.IsValid && controller.IsHLTV)
                {
                    gotv = controller;
                    break;
                }
            }

            string state = gotv == null
                ? $"sin controller de GOTV (IsHLTV) con MaxPlayers={Server.MaxPlayers}: el banner no va a quedar en la demo"
                : $"GOTV en slot {gotv.Slot} ('{gotv.PlayerName}', IsBot={gotv.IsBot}, Connected={gotv.Connected}) con MaxPlayers={Server.MaxPlayers}";
            if (state != haporeBrandingGotvLogState)
            {
                haporeBrandingGotvLogState = state;
                LogInfo("Branding", state);
            }
            return gotv;
        }
    }
}
