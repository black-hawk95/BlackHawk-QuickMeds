using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using System.Reflection;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Cleans up when a heal ends, however it ends.
    ///
    /// Two things must not outlive a heal:
    ///
    ///   - the animator's playback speed, which is global to the animator rather than to one clip.
    ///     Left raised, the next weapon draw or reload would play fast.
    ///   - the health-changed subscription used by auto-cancel, which would otherwise keep firing
    ///     for the rest of the raid and stay attached to a stale controller after the next one.
    ///
    /// HealSpeed.Clear handles both. It is hooked to all three exits - completed, reset, and
    /// removed - because a heal can end by any of them, and only one of those is the tidy case.
    /// </summary>
    internal class HealingFinishedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Finish));
        }

        [PatchPostfix]
        private static void Postfix() => HealSpeed.Clear();
    }

    /// <summary>Cleans up when the operation is reset.</summary>
    internal class HealingResetPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Reset));
        }

        [PatchPostfix]
        private static void Postfix() => HealSpeed.Clear();
    }

    /// <summary>Cleans up when the operation is removed or interrupted.</summary>
    internal class HealingRemovedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.Remove));
        }

        [PatchPostfix]
        private static void Postfix() => HealSpeed.Clear();
    }
}
