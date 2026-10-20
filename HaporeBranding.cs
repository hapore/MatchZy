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

        // A la demo sólo llega lo que recibe el cliente de GOTV, y PrintToCenterHtml
        // le manda el evento a un único jugador. Al slot de GOTV se le manda
        // aparte; si el motor no le da un listener propio, se cae a difundir el
        // evento a todos (con el userid de GOTV) para que igual quede grabado.
        private bool haporeBrandingGotvBroadcast = false;
        private bool haporeBrandingGotvPathLogged = false;
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
                // Los bots no tienen listener de eventos: FireEventToClient tira.
                if (player == null || !player.IsValid || player.IsBot) continue;

                try
                {
                    if (player.IsHLTV)
                    {
                        SendHaporeBrandingToGotv(player);
                    }
                    else if (player.Connected == PlayerConnectedState.Connected)
                    {
                        player.PrintToCenterHtml(haporeBrandingHtml, HaporeBrandingEventDuration);
                    }
                }
                catch (Exception e)
                {
                    // Una vez por ronda: el tick lo repetiría 64 veces por segundo.
                    if (haporeBrandingErrorLogged) continue;
                    haporeBrandingErrorLogged = true;
                    LogInfo("Branding", $"No se pudo mostrar el banner a {player.PlayerName} (slot {slot}): {e.Message}");
                }
            }
        }

        private void SendHaporeBrandingToGotv(CCSPlayerController gotv)
        {
            var @event = new EventShowSurvivalRespawnStatus(true)
            {
                LocToken = haporeBrandingHtml,
                Duration = HaporeBrandingEventDuration,
                Userid = gotv
            };

            if (!haporeBrandingGotvBroadcast)
            {
                try
                {
                    @event.FireEventToClient(gotv);
                    @event.Free();
                    if (!haporeBrandingGotvPathLogged)
                    {
                        haporeBrandingGotvPathLogged = true;
                        LogInfo("Branding", $"Banner enviado directo a GOTV (slot {gotv.Slot}).");
                    }
                    return;
                }
                catch (Exception e)
                {
                    haporeBrandingGotvBroadcast = true;
                    LogInfo("Branding", $"GOTV (slot {gotv.Slot}) no acepta eventos directos ({e.Message}); se difunde el evento a todos.");
                }
            }

            // FireEvent libera el evento.
            @event.FireEvent(false);
        }
    }
}
