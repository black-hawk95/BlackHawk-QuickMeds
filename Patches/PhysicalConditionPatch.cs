using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Reflection;

namespace QuickMeds.Patches
{
    internal class PhysicalConditionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(MovementContext), nameof(MovementContext.SetPhysicalCondition));
        }

        [PatchPrefix]
        private static bool Prefix(EPhysicalCondition c, bool val, ref bool __result, Player ____player)
        {
            try
            {
                if (c != EPhysicalCondition.UsingMeds && c != EPhysicalCondition.HealingLegs)
                    return true;

                if (!ModEnvironment.IsLocalLivingPlayer(____player))
                    return true;

                // Clears must always pass through. Blocking them can leave the player stuck in the
                // medicine hands state between limbs/after interruption.
                if (!val)
                    return true;

                // QuickMeds is scoped to actual medicine items. Food/drink and any exact medicine
                // disabled in the per-medicine F12 section must keep vanilla physical conditions.
                if (____player.HandsController is not Player.MedsController liveMeds ||
                    !HealSpeed.IsMedicineItem(liveMeds.Item) ||
                    !Plugin.IsMedicineEnabled(liveMeds.Item))
                {
                    return true;
                }

                var usingSurgeryItem = IsSurgeryOrSplint(____player);

                if (Plugin.LogHealSpeed)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuickMedsDBG: CONDITION {c} surgeryOrSplint={usingSurgeryItem} {DescribeItem(____player)} " +
                        $"sprintMeds={Plugin.SprintWhileUsingMeds} walkSurgery={Plugin.WalkWhileUsingSurgery} " +
                        $"sprintSurgery={Plugin.SprintWhileUsingSurgery}");
                }

                if (c == EPhysicalCondition.HealingLegs)
                {
                    if (Plugin.WalkWhileUsingSurgery)
                    {
                        __result = false;
                        return false;
                    }

                    return true;
                }

                bool allowSprint = usingSurgeryItem
                    ? Plugin.SprintWhileUsingSurgery
                    : Plugin.SprintWhileUsingMeds;

                if (allowSprint)
                {
                    __result = false;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Movement is fail-open: if classification ever breaks, let vanilla handle the
                // physical condition rather than risking a stuck hands/player state.
                Plugin.LogSource?.LogError($"QuickMeds: movement-condition check failed; vanilla condition preserved.\n{ex}");
                return true;
            }
        }

        private static bool IsSurgeryOrSplint(Player player)
        {
            if (player.HandsController is not Player.MedsController meds) return false;
            if (meds.Item is not Meds med) return false;

            var effects = med.HealthEffectsComponent;
            if (effects == null) return false;

            // Surgery repairs destroyed parts.
            if (effects.AffectsAny(SurgeryEffect)) return true;

            // Splints are Medical items that treat fractures. Do NOT use MaxHpResource as the
            // discriminator: in SPT 4.1.6 the Aluminum splint is a Medical item with
            // MedKitComponent.MaxHpResource == 3, while Grizzly is a MedKit that can also treat
            // fractures. The runtime class + fracture capability cleanly separates those cases.
            if (!effects.AffectsAny(FractureEffect)) return false;

            return med is Medical;
        }

        private static string DescribeItem(Player player)
        {
            if (player.HandsController is not Player.MedsController meds) return "[no-meds-controller]";
            if (meds.Item is not Meds med) return "[not-meds-item]";

            var effects = med.HealthEffectsComponent;
            var medKit = med.MedKitComponent;

            return $"[item={med.TemplateId} type={med.GetType().Name} medicalClass={med is Medical} " +
                   $"destroyed={effects?.AffectsAny(SurgeryEffect)} fracture={effects?.AffectsAny(FractureEffect)} " +
                   $"maxHp={(medKit == null ? -1 : medKit.MaxHpResource)}]";
        }

        private static readonly EDamageEffectType[] SurgeryEffect = { EDamageEffectType.DestroyedPart };
        private static readonly EDamageEffectType[] FractureEffect = { EDamageEffectType.Fracture };
    }
}
