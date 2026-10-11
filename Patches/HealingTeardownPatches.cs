using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Reflection;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Teardown is owner-aware. Each hook captures the operation's controller BEFORE EFT runs its
    /// cleanup, then compares that captured controller to the exact local controller QuickMeds owns.
    /// A Fika observed player's operation therefore cannot clear local state.
    /// </summary>
    internal class HealingFinishedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Finish));

        [PatchPrefix]
        private static void Prefix(Player.MedsController.MedsInHandsOperation __instance, out Player.MedsController __state)
        {
            __state = HealSpeed.GetOperationController(__instance);
        }

        [PatchPostfix]
        private static void Postfix(Player.MedsController __state)
        {
            try { HealSpeed.ClearIfOwner(__state, "finish"); }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QuickMeds: finish teardown failed.\n{ex}"); }
        }
    }

    internal class HealingResetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Reset));

        [PatchPrefix]
        private static void Prefix(Player.MedsController.MedsInHandsOperation __instance, out Player.MedsController __state)
        {
            __state = HealSpeed.GetOperationController(__instance);
        }

        [PatchPostfix]
        private static void Postfix(Player.MedsController __state)
        {
            try { HealSpeed.ClearIfOwner(__state, "reset"); }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QuickMeds: reset teardown failed.\n{ex}"); }
        }
    }

    internal class HealingRemovedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Remove));

        [PatchPrefix]
        private static void Prefix(Player.MedsController.MedsInHandsOperation __instance, out Player.MedsController __state)
        {
            __state = HealSpeed.GetOperationController(__instance);
        }

        [PatchPostfix]
        private static void Postfix(Player.MedsController __state)
        {
            try { HealSpeed.ClearIfOwner(__state, "remove"); }
            catch (Exception ex) { Plugin.LogSource?.LogError($"QuickMeds: remove teardown failed.\n{ex}"); }
        }
    }
}
