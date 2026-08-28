using EFT;
using EFT.HealthSystem;
using EFT.Ballistics;
using EFT.InventoryLogic;
using System;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Ends a body part's heal the moment it is actually finished, instead of playing out the rest
    /// of the animation for nothing.
    ///
    /// Vanilla runs the full duration even when the limb filled up early, so you stand there
    /// finishing an animation that is no longer doing anything. This watches the healing ticks and
    /// stops as soon as the work is done.
    ///
    /// Cancelling a finished limb moves on to the next one rather than ending the whole operation,
    /// so a multi-limb heal still completes - it just stops wasting time on parts that are done.
    ///
    /// Listening starts when a heal begins and stops when it ends, rather than running for the
    /// whole raid. It costs nothing while you are not healing, and there is no subscription left
    /// behind to leak into the next raid.
    ///
    /// Approach borrowed from minihazel's Healing Auto Cancel. If that mod is installed, QuickMeds
    /// leaves this to it - see Plugin.
    /// </summary>
    internal static class HealAutoCancel
    {
        private static ActiveHealthController _subscribed;
        private static Item _item;

        /// <summary>Starts watching for this heal. Safe to call repeatedly.</summary>
        public static void Watch(Player player, Item item)
        {
            if (!Plugin.AutoCancelWhenDone) return;
            if (ModEnvironment.IsAutoCancelModInstalled) return;
            if (!ModEnvironment.IsLocalLivingPlayer(player)) return;

            var health = player.ActiveHealthController;
            if (health == null) return;

            _item = item;

            // Already listening to this controller; nothing to do.
            if (ReferenceEquals(_subscribed, health)) return;

            Unwatch();

            health.HealthChangedEvent += OnHealthChanged;
            _subscribed = health;
        }

        /// <summary>
        /// Stops watching.
        ///
        /// Called from every path out of a heal. An event subscription that outlives its heal would
        /// keep firing for the rest of the raid, and would still be attached to a stale controller
        /// after the next one.
        /// </summary>
        public static void Unwatch()
        {
            if (_subscribed == null) return;

            _subscribed.HealthChangedEvent -= OnHealthChanged;
            _subscribed = null;
            _item = null;
        }

        private static void OnHealthChanged(EBodyPart bodyPart, float amount, DamageInfo damageInfo)
        {
            try
            {
                // Only healing from a medical item. Regeneration and stims raise this too.
                if (damageInfo.DamageType != EDamageType.Medicine) return;

                var health = _subscribed;
                if (health == null) return;

                // A broken limb needs the full application to be splinted, so it is never cut short.
                if (health.IsBodyPartBroken(bodyPart)) return;

                if (!ShouldStop(health, bodyPart)) return;

                health.RemoveMedEffect();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuickMeds: auto-cancel check failed: {ex.Message}");
                Unwatch();
            }
        }

        private static bool ShouldStop(ActiveHealthController health, EBodyPart bodyPart)
        {
            // Out of resource - nothing more the item can do.
            if (_item != null
                && _item.TryGetItemComponent<MedKitComponent>(out var medKit)
                && medKit.HpResource <= 0f)
            {
                return true;
            }

            var partHealth = health.GetBodyPartHealth(bodyPart);
            if (!partHealth.AtMaximum) return false;

            // A bleed drains health as fast as the kit restores it, so a limb can read "full" for
            // an instant while still bleeding. Stopping there would put the item away and leave the
            // bleed running, so the heal continues until the bleed is gone.
            return !IsBleeding(health, bodyPart);
        }

        private static bool IsBleeding(ActiveHealthController health, EBodyPart bodyPart)
        {
            var effects = health.BodyPartEffects?.Effects;
            if (effects == null || !effects.TryGetValue(bodyPart, out var partEffects) || partEffects == null)
            {
                return false;
            }

            return partEffects.ContainsKey("LightBleeding") || partEffects.ContainsKey("HeavyBleeding");
        }
    }
}
