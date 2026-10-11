using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using System;

namespace QuickMeds.Patches
{
    internal static class HealAutoCancel
    {
        private static ActiveHealthController _subscribed;
        private static Item _item;

        public static void Watch(Player player, Item item)
        {
            if (!Plugin.AutoCancelWhenDone) return;
            if (ModEnvironment.IsAutoCancelModInstalled) return;
            if (!ModEnvironment.IsLocalLivingPlayer(player)) return;
            if (!Plugin.IsMedicineEnabled(item)) return;
            if (!HealSpeed.IsAutoCancelItem(item)) return;

            var health = player.ActiveHealthController;
            if (health == null) return;

            _item = item;

            if (ReferenceEquals(_subscribed, health))
            {
                Debug($"AUTOCANCEL watch refresh item={ItemName(item)}");
                return;
            }

            Unwatch("watch-switch");
            health.HealthChangedEvent += OnHealthChanged;
            _subscribed = health;
            Debug($"AUTOCANCEL watch start item={ItemName(item)}");
        }

        public static void Unwatch(string reason = "teardown")
        {
            if (_subscribed == null)
            {
                _item = null;
                return;
            }

            _subscribed.HealthChangedEvent -= OnHealthChanged;
            Debug($"AUTOCANCEL watch stop reason={reason} item={ItemName(_item)}");
            _subscribed = null;
            _item = null;
        }

        private static void OnHealthChanged(EBodyPart bodyPart, float amount, DamageInfo damageInfo)
        {
            try
            {
                if (damageInfo.DamageType != EDamageType.Medicine)
                    return;

                var health = _subscribed;
                if (health == null)
                    return;

                // Per-medicine master OFF must stop QuickMeds auto-cancel from acting even if the
                // setting is changed while this application is already in progress.
                if (!Plugin.IsMedicineEnabled(_item))
                {
                    Unwatch("medicine-disabled");
                    return;
                }

                // Fracture treatment is finalized at the end of the application; never cut it short.
                if (health.IsBodyPartBroken(bodyPart))
                {
                    Debug($"AUTOCANCEL keep body={bodyPart} reason=fracture");
                    return;
                }

                string reason;
                if (!ShouldStop(health, bodyPart, out reason))
                    return;

                Debug($"AUTOCANCEL stop body={bodyPart} reason={reason} amount={amount:0.000} item={ItemName(_item)}");
                health.RemoveMedEffect();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: auto-cancel check failed; watcher removed safely.\n{ex}");
                Unwatch("exception");
            }
        }

        private static bool ShouldStop(ActiveHealthController health, EBodyPart bodyPart, out string reason)
        {
            if (_item != null &&
                _item.TryGetItemComponent<MedKitComponent>(out var medKit) &&
                medKit.HpResource <= 0f)
            {
                reason = "resource-empty";
                return true;
            }

            var partHealth = health.GetBodyPartHealth(bodyPart);
            if (!partHealth.AtMaximum)
            {
                reason = "not-full";
                return false;
            }

            if (IsBleeding(health, bodyPart))
            {
                reason = "still-bleeding";
                return false;
            }

            reason = "full";
            return true;
        }

        private static bool IsBleeding(ActiveHealthController health, EBodyPart bodyPart)
        {
            var effects = health.BodyPartEffects?.Effects;
            if (effects == null || !effects.TryGetValue(bodyPart, out var partEffects) || partEffects == null)
                return false;

            return partEffects.ContainsKey("LightBleeding") || partEffects.ContainsKey("HeavyBleeding");
        }

        private static string ItemName(Item item) => item?.TemplateId.ToString() ?? "none";

        private static void Debug(string message)
        {
            if (Plugin.LogHealSpeed)
                Plugin.LogSource?.LogInfo("QuickMedsDBG: " + message);
        }
    }
}
