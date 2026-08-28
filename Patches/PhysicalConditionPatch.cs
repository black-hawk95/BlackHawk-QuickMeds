using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Controls what you can do while healing, by refusing the conditions that stop you moving.
    ///
    /// Two conditions matter:
    ///   UsingMeds   - blocks sprinting, set for every heal
    ///   HealingLegs - blocks walking, set for splints and surgery kits
    ///
    /// The decision is made from the ITEM IN HANDS, not from the conditions themselves. That is the
    /// important design point, and it took three attempts to arrive at.
    ///
    /// The obvious approach is to watch the conditions: see HealingLegs, remember it, and treat the
    /// following UsingMeds as a leg heal. Logging showed why that can never work. The real order is:
    ///
    ///     UsingMeds=True      &lt;- the sprint decision happens HERE
    ///     HealingLegs=True    &lt;- and the game only says it is a leg AFTERWARDS
    ///
    /// UsingMeds always arrives first, so at the moment sprinting is decided there is nothing yet
    /// to say this is a leg treatment. The conditions are also set and cleared repeatedly through a
    /// heal, so any remembered state is stale by the next one.
    ///
    /// The item in hands, by contrast, is known before the heal even starts. Reading it gives the
    /// right answer at exactly the moment it is needed, with no state to keep and nothing to reset.
    ///
    /// Item types are identified by their COMPONENT DATA rather than by a list of ids, using the
    /// same test the game uses on itself - DoMedEffect checks AffectsAny(DestroyedPart) to decide
    /// whether the surgery skill applies. So a modded or backported splint works with no changes,
    /// because it declares that it treats fractures in the same way a vanilla one does.
    ///
    /// One accepted limitation: a surgery kit used on an ARM counts as a surgery item here, since
    /// the item is what is checked rather than the body part. Sensible enough - you are still
    /// holding a surgery kit - and it keeps the whole thing stateless.
    /// </summary>
    internal class PhysicalConditionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(MovementContext), nameof(MovementContext.SetPhysicalCondition));
        }

        [PatchPrefix]
        private static bool Prefix(EPhysicalCondition c, bool val, ref bool __result, Player ____player)
        {
            // Neither condition is one this mod touches.
            if (c != EPhysicalCondition.UsingMeds && c != EPhysicalCondition.HealingLegs) return true;

            // Only ever the local player. In a Fika raid other players' conditions are replicated
            // from their own client, and overriding them here would fight that.
            if (!ModEnvironment.IsLocalLivingPlayer(____player)) return true;

            var usingSurgeryItem = IsSurgeryOrSplint(____player);

            // Clearing always goes through, or a condition could never be removed.
            //
            // 0.9.6 tried refusing the clear so the sprint block would hold through the gaps
            // between body parts. It broke healing outright - the player reached for the item and
            // got stuck with their hands off screen, unable to act at all. Refusing a clear here
            // evidently interferes with the operation itself, not just with movement.
            //
            // Reverted deliberately. Being able to sprint in the brief gaps between limbs is a
            // small cosmetic inconsistency; being unable to heal is not.
            if (!val) return true;

            if (Plugin.LogHealSpeed)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuickMeds: cond {c} surgeryItem={usingSurgeryItem} {DescribeItem(____player)} " +
                    $"(sprintMeds={Plugin.SprintWhileUsingMeds} " +
                    $"walkSurgery={Plugin.WalkWhileUsingSurgery} " +
                    $"sprintSurgery={Plugin.SprintWhileUsingSurgery})");
            }

            if (c == EPhysicalCondition.HealingLegs)
            {
                // Only ever set by splints and surgery kits, so one setting covers it.
                if (Plugin.WalkWhileUsingSurgery)
                {
                    __result = false;
                    return false;
                }

                return true;
            }

            // UsingMeds - which setting applies depends on what is in hands.
            if (AllowSprint(usingSurgeryItem))
            {
                __result = false;
                return false;
            }

            return true;
        }

        /// <summary>Which sprint setting applies, given what is in hands.</summary>
        private static bool AllowSprint(bool usingSurgeryItem)
        {
            return usingSurgeryItem
                ? Plugin.SprintWhileUsingSurgery
                : Plugin.SprintWhileUsingMeds;
        }

        /// <summary>
        /// True when the item in hands is a splint or a surgery kit.
        ///
        /// Uses HealthEffectsComponent.AffectsAny, which is how the game itself classifies these -
        /// DoMedEffect calls AffectsAny(DestroyedPart) to decide whether the surgery skill applies.
        /// Matching on the same data rather than on item ids is what keeps this working with modded
        /// and backported items.
        /// </summary>
        private static bool IsSurgeryOrSplint(Player player)
        {
            if (player.HandsController is not Player.MedsController meds) return false;
            if (meds.Item is not Meds med) return false;

            var effects = med.HealthEffectsComponent;
            if (effects == null) return false;

            // A surgery kit repairs destroyed parts. Nothing else does, so this alone identifies
            // them - and it is the same check the game makes in DoMedEffect when deciding whether
            // the surgery skill applies.
            if (effects.AffectsAny(SurgeryEffect)) return true;

            // A splint treats fractures and does nothing else. Testing for fractures ALONE is not
            // enough: a Grizzly also treats them, and was being classified as a surgery item
            // because of it. The difference is that a Grizzly carries a pool of health to restore
            // and a splint does not, so an item that treats fractures while restoring no health is
            // a splint.
            if (!effects.AffectsAny(FractureEffect)) return false;

            var medKit = med.MedKitComponent;
            return medKit == null || medKit.MaxHpResource <= 0;
        }

        /// <summary>Diagnostic only - the values the classification above is based on.</summary>
        private static string DescribeItem(Player player)
        {
            if (player.HandsController is not Player.MedsController meds) return "[no meds]";
            if (meds.Item is not Meds med) return "[not meds item]";

            var effects = med.HealthEffectsComponent;
            var medKit = med.MedKitComponent;

            return $"[destroyed={effects?.AffectsAny(SurgeryEffect)} " +
                   $"fracture={effects?.AffectsAny(FractureEffect)} " +
                   $"maxHp={(medKit == null ? -1 : medKit.MaxHpResource)}]";
        }

        private static readonly EDamageEffectType[] SurgeryEffect = { EDamageEffectType.DestroyedPart };
        private static readonly EDamageEffectType[] FractureEffect = { EDamageEffectType.Fracture };
    }
}
