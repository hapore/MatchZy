using CounterStrikeSharp.API.Core;

namespace MatchZy
{
    public partial class MatchZy
    {
        // Unicos tipos de daño que siguen afectando a un compañero cuando el friendly
        // fire "logico" (matchConfig.FriendlyFire) esta apagado. Es una whitelist a
        // proposito: todo lo que no sea explosion o fuego (balas, cuchillo, zeus,
        // impacto directo de una granada) se anula sin tener que enumerarlo.
        private static readonly int FriendlyFireAllowedDamageBits =
            (int)DamageTypes_t.DMG_BLAST | (int)DamageTypes_t.DMG_BURN;

        // Se pone en true si el listener quedo registrado. Si CSS cambia la API y el
        // registro falla, el modo friendly_fire=false no filtra nada y hay que saberlo.
        public bool friendlyFireListenerActive = false;

        public void RegisterFriendlyFireListener()
        {
            try
            {
                RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnPlayerTakeDamagePreHandler);
                friendlyFireListenerActive = true;
                Log("[FriendlyFire] Listener OnPlayerTakeDamagePre registrado.");
            }
            catch (Exception ex)
            {
                friendlyFireListenerActive = false;
                Log($"[FriendlyFire FATAL] No se pudo registrar OnPlayerTakeDamagePre: {ex.Message}. " +
                    "Las partidas con friendly_fire=false van a correr con daño competitivo completo!");
            }
        }

        private HookResult OnPlayerTakeDamagePreHandler(CCSPlayerPawn victim, CTakeDamageInfo info)
        {
            // Friendly fire competitivo: comportamiento nativo, no tocamos nada.
            if (matchConfig.FriendlyFire) return HookResult.Continue;

            // En practica mp_teammates_are_enemies esta en 1: los "compañeros" son
            // rivales y filtrarles el daño romperia el modo.
            if (isPractice) return HookResult.Continue;

            CBaseEntity? attackerEntity = info.Attacker.Value;
            // Daño del mundo (caida, agua, trigger_hurt): no es friendly fire.
            if (attackerEntity == null || attackerEntity.DesignerName != "player") return HookResult.Continue;

            CCSPlayerPawn attacker = attackerEntity.As<CCSPlayerPawn>();

            // Autodaño: lo gobierna ff_damage_reduction_grenade_self, no nosotros.
            if (attacker.Handle == victim.Handle) return HookResult.Continue;
            if (attacker.TeamNum != victim.TeamNum) return HookResult.Continue;

            if (((int)info.BitsDamageType & FriendlyFireAllowedDamageBits) != 0) return HookResult.Continue;

            info.Damage = 0;
            return HookResult.Continue;
        }
    }
}
