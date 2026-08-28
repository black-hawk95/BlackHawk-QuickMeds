using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Reflection;
using UnityEngine;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Healing speed that follows how still you are.
    ///
    /// The game drives both halves of a heal from a single factor: GoNextBodyPart hands it to the
    /// animator as SetUseTimeMultiplier(X), and DoMedEffect sets the effect's work time to
    /// baseTime / X. So the multiplier is folded into that same factor - the animation multiplied
    /// by M, the duration divided by M.
    ///
    /// Which items are affected, and why they differ:
    ///
    ///   bandages, medkits, splints, surgery kits   animation + duration
    ///   pills, injectors                           animation only
    ///   food, drink                                neither
    ///
    /// Pills and injectors get the animation but not the duration because a stimulator's
    /// WorkStateTime is how long its BUFF lasts, not how long you spend injecting it. Scaling that
    /// would shorten your painkillers rather than speed up the injection.
    ///
    /// Food and drink are excluded outright - eating faster because you stood still is not what
    /// this mod is for.
    ///
    /// Item types are told apart by their COMPONENTS, never by a list of ids. Meds added by other
    /// mods - including content backports - therefore work with no changes, because a backported
    /// medkit still carries a MedKitComponent.
    /// </summary>
    internal static class HealSpeed
    {
        /// <summary>Multiplier for the body part being healed. Above 1 is faster.</summary>
        public static float Current { get; private set; } = 1f;

        /// <summary>True while a heal this mod is influencing is in progress.</summary>
        public static bool Active { get; private set; }

        /// <summary>
        /// The effect for the body part currently being healed.
        ///
        /// Captured from DoMedEffect's RETURN VALUE rather than by searching the health
        /// controller's active-effect list. The search approach worked only for surgery kits: every
        /// other med starts with a delay and is not in that list yet at the moment this runs, so
        /// the search came back empty and the speed was never applied. The return value is the
        /// effect, immediately, for every item type.
        /// </summary>
        public static ActiveHealthController.MedEffect CurrentEffect { get; private set; }

        private static Player.MedsController _controller;

        public static void CaptureEffect(ActiveHealthController.MedEffect effect) => CurrentEffect = effect;

        /// <summary>Works out the multiplier for the current movement state.</summary>
        public static float Evaluate(Player player)
        {
            if (!Plugin.AdaptiveHealSpeed) return 1f;
            if (!ModEnvironment.IsLocalLivingPlayer(player)) return 1f;

            // The animation state is how the game itself describes movement; there is no single
            // "current speed" value to read.
            var state = player.MovementContext?.CurrentState?.Name;
            if (state == null) return 1f;

            return state.Value switch
            {
                EPlayerState.None or
                EPlayerState.Idle or
                EPlayerState.ProneIdle or
                EPlayerState.Transition => Plugin.StillHealSpeed,

                EPlayerState.Sprint => Plugin.SprintHealSpeed,

                _ => Plugin.WalkHealSpeed,
            };
        }

        /// <summary>Food and drink are left entirely alone.</summary>
        public static bool IsFoodOrDrink(Item item)
        {
            return item != null && item.TryGetItemComponent<FoodDrinkComponent>(out _);
        }

        /// <summary>
        /// True for items whose work time is genuinely "how long you spend applying this" -
        /// bandages, medkits, splints, surgery kits. For a stimulator the work time is the buff
        /// duration instead, so its duration must not be scaled.
        /// </summary>
        public static bool HasApplicationDuration(Item item)
        {
            return item != null && item.TryGetItemComponent<MedKitComponent>(out _);
        }

        public static void Begin(Player player, Player.MedsController controller)
        {
            CurrentEffect = null;
            _controller = controller;

            var item = controller?.Item;

            // Reset FIRST, before any decision below can return early.
            //
            // Animator.speed persists until something changes it, so every path into this method
            // has to start from a known state. Without this, food and drink inherited whatever
            // speed the previous heal left set, and a multi-limb sequence carried its speed from
            // one body part into the next - which is where the runaway animation came from.
            ResetAnimatorSpeed();

            // Food and drink are never touched.
            if (IsFoodOrDrink(item))
            {
                Current = 1f;
                Active = false;
                return;
            }

            Current = Evaluate(player);
            Active = Math.Abs(Current - 1f) > 0.001f;

            // Pills, injectors, food and drink are left completely alone - no duration change and
            // no animation change.
            //
            // Their work time is the BUFF duration rather than how long the action takes, so it
            // must not be scaled, and scaling only the animation would show a fast animation over
            // an action whose length never changed. Touching them also gained nothing and was a
            // suspect in the put-away animation glitch.
            //
            // One rule throughout: animation and duration move together, or neither moves.
            if (!HasApplicationDuration(item))
            {
                Current = 1f;
                Active = false;
                return;
            }

            ApplyAnimatorSpeed();

            HealAutoCancel.Watch(player, item);
        }

        public static void Clear()
        {
            // The animator speed MUST be restored before the controller reference is dropped.
            //
            // SetAnimationSpeed sets Animator.speed, which scales the whole animator rather than
            // one clip. If it were left raised, every later animation on that object - drawing a
            // weapon, reloading - would play fast too. This is why Clear is called from every path
            // out of a heal, completed or interrupted.
            ResetAnimatorSpeed();
            HealAutoCancel.Unwatch();


            Active = false;
            Current = 1f;
            CurrentEffect = null;
            _controller = null;
        }

        /// <summary>
        /// Scales the animator's playback speed to match the healing speed.
        ///
        /// This is separate from SetUseTimeMultiplier, and does a different job. That method sets
        /// an animator FLOAT PARAMETER (FLOAT_USETIMEMULTIPLIER) which the animation controller
        /// only uses if its state machine was authored to read it - which is why passing 2.5 to it
        /// arrived correctly and changed nothing visible. SetAnimationSpeed sets Animator.speed
        /// directly, which scales clip playback regardless of how the controller was built.
        ///
        /// Both are applied: the float parameter for anything the controller does read it for, and
        /// the playback speed for the visible result.
        /// </summary>
        /// <summary>
        /// Every animation-speed change goes through here, tagged with where it came from.
        ///
        /// The put-away animation replaying has now been blamed on two different calls and neither
        /// fix worked, so instead of guessing a third time this records every call: which one, what
        /// value, what the animator actually reads back, and how far through the effect it landed.
        /// The call immediately before the glitch is the culprit.
        /// </summary>
        private static void SetSpeed(string source, float value)
        {
            var animator = _controller?.FirearmsAnimator;
            if (animator == null) return;

            animator.SetAnimationSpeed(value);

            if (!Plugin.LogHealSpeed) return;

            var effect = CurrentEffect;
            var elapsed = effect?.StateCurrentTime ?? -1f;
            var total = effect?.WorkStateTime ?? -1f;
            var state = effect == null ? "-" : effect.State.ToString();

            Plugin.LogSource?.LogInfo(
                $"QuickMeds: anim-speed [{source}] set={value:0.00} reads={ReadAnimatorSpeed():0.00} " +
                $"item={ItemName()} state={state} elapsed={elapsed:0.00}/{total:0.00}");
        }

        private static string ItemName()
        {
            var id = _controller?.Item?.TemplateId.ToString();
            return string.IsNullOrEmpty(id) ? "none" : id;
        }

        public static void ApplyAnimatorSpeed()
        {
            var animator = _controller?.FirearmsAnimator;
            if (animator == null) return;

            // Reset to 1 FIRST, then apply.
            //
            // Animator.speed persists until something changes it, and this runs once per body
            // part. Applying the multiplier to whatever was already there means a multi-limb heal
            // stacks it: 2.5, then 6.25, then 15.6. That is the runaway animation on a surgery kit
            // treating several parts - and it also explains the animation finishing seconds before
            // the healing, since it was running roughly four times too fast.
            //
            // Resetting first makes every body part start from a known state.
            SetSpeed("reset", 1f);
            SetSpeed("apply", Current);
            _animatorSpeedApplied = true;
        }

        /// <summary>
        /// The animator's ACTUAL playback speed, rather than what this mod believes it set.
        ///
        /// Worth logging separately: the two disagreeing is exactly how the stacking above went
        /// unnoticed.
        /// </summary>
        private static float ReadAnimatorSpeed()
        {
            var animator = _controller?.FirearmsAnimator?.Animator;
            return animator?.speed ?? -1f;
        }

        /// <summary>Returns the animator to normal speed.</summary>
        public static void ResetAnimatorSpeed()
        {
            // Only reset if a speed was actually applied.
            //
            // This used to run for EVERY item, including pills, stims, food and drink - which the
            // mod otherwise leaves completely alone. Writing to the animator as the put-away
            // animation began restarted that clip, which is the item being visibly put away twice.
            // Nothing was ever changed for those items, so there was nothing to undo.
            if (!_animatorSpeedApplied) return;

            _animatorSpeedApplied = false;
            SetSpeed("teardown", 1f);
        }

        /// <summary>True once this mod has changed the animator speed for the current heal.</summary>
        private static bool _animatorSpeedApplied;

        /// <summary>
        /// Applies a new multiplier to a heal already in progress. Called when the player starts or
        /// stops moving; does nothing unless the multiplier actually changed.
        /// </summary>
        public static void Retarget(Player player)
        {
            // Logged FIRST, before any early return.
            //
            // Four versions were built on the assumption that this method runs when the player
            // changes stance, and none of them worked - but nothing ever logged from inside here,
            // so there was no proof it runs at all. If these lines never appear, the movement hook
            // is the problem and everything downstream of it was fine.
            if (Plugin.LogHealSpeed)
            {
                var handsType = player?.HandsController?.GetType().Name ?? "null";
                Plugin.LogSource?.LogInfo(
                    $"QuickMeds: RETARGET called - hands={handsType} " +
                    $"state={player?.MovementContext?.CurrentState?.Name.ToString() ?? "?"} " +
                    $"current={Current:0.00} adaptive={Plugin.AdaptiveHealSpeed}");
            }

            if (!Plugin.AdaptiveHealSpeed) return;

            // Everything below is read LIVE from the player rather than from stored fields.
            //
            // The stored controller and effect were being wiped mid-heal: logging showed Clear()
            // running between body parts, not just at the end, so by the time a stance change
            // arrived those fields were already null and this returned immediately. That is why
            // mid-heal speed changes stopped working for every item.
            //
            // Reading live removes the dependency altogether. It no longer matters when Clear()
            // runs, or which of the teardown hooks fires per limb - the state cannot be stale
            // because it is not kept.
            if (player.HandsController is not Player.MedsController controller)
            {
                if (Plugin.LogHealSpeed) Plugin.LogSource?.LogInfo("QuickMeds: retarget skipped - not holding meds");
                return;
            }

            var item = controller.Item;
            if (IsFoodOrDrink(item))
            {
                if (Plugin.LogHealSpeed) Plugin.LogSource?.LogInfo("QuickMeds: retarget skipped - food or drink");
                return;
            }

            // Nothing here applies to an item whose duration is not scaled.
            //
            // Pills and injectors keep their timing, because their work time is the buff duration
            // rather than how long the injection takes. Touching the animator for them was pure
            // downside: no benefit, and re-applying an animation speed as the put-away animation
            // began made that animation restart - the "pill put away twice" bug.
            //
            // The rule is the same one Begin follows: animation and duration move together, or
            // neither moves.
            if (!HasApplicationDuration(item))
            {
                if (Plugin.LogHealSpeed) Plugin.LogSource?.LogInfo("QuickMeds: retarget skipped - item timing not scaled");
                return;
            }

            var updated = Evaluate(player);
            if (Math.Abs(updated - Current) < 0.001f)
            {
                if (Plugin.LogHealSpeed)
                    Plugin.LogSource?.LogInfo($"QuickMeds: retarget skipped - speed unchanged ({Current:0.00})");
                return;
            }

            // Live lookup first, with the effect captured from DoMedEffect as a fallback.
            //
            // The live search returns null for most meds, because at that moment the effect is not
            // yet in the health controller's active list - the same reason an earlier version only
            // worked for surgery kits. The captured value is the reliable one; the live search is
            // kept because it stays correct if the captured effect is stale.
            var effect = FindActiveMedEffect(player) ?? CurrentEffect;

            if (Plugin.LogHealSpeed)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuickMeds: RETARGET applying {Current:0.00} -> {updated:0.00} " +
                    $"effect={(effect == null ? "NULL" : "found")}");
            }

            if (effect != null)
            {
                Rescale(effect, Current, updated);
            }

            Current = updated;
            Active = Math.Abs(Current - 1f) > 0.001f;

            // The animation follows the new speed as well.
            //
            // This was removed in an attempt to fix the put-away animation replaying, on the theory
            // that changing the speed mid-heal restarted the clip. The glitch carried on happening
            // without it, so the removal fixed nothing and cost mid-heal animation response.
            // Restored on that basis.
            ApplyAnimatorSpeed();
        }

        /// <summary>
        /// The med effect currently being applied, found by asking the health controller.
        ///
        /// Used by Retarget only. The per-body-part path takes the effect straight from
        /// DoMedEffect's return value, which is more reliable, but that value is not available
        /// here - a stance change happens outside that call.
        /// </summary>
        private static ActiveHealthController.MedEffect FindActiveMedEffect(Player player)
        {
            var health = player?.ActiveHealthController;
            if (health == null) return null;

            foreach (var effect in health.GetAllActiveEffects())
            {
                if (effect is ActiveHealthController.MedEffect med && med.Active) return med;
            }

            return null;
        }

        private static readonly FieldInfo StateTimeField =
            AccessTools.Field(typeof(ActiveHealthController.Effect), "_stateTime");

        /// <summary>
        /// Changes a running effect's speed without changing how much it heals in total.
        ///
        /// The game's own IL shows why all three values must move together:
        ///
        ///   - NextState copies WorkStateTime into _stateTime ONCE, when the work state begins.
        ///     After that _stateTime alone decides when the effect ends.
        ///   - RegularUpdate computes each tick's healing as points * loopTime / WorkStateTime.
        ///
        /// So WorkStateTime is the RATE and _stateTime is the DURATION. Writing only WorkStateTime
        /// mid-effect speeds the healing up without shortening the effect, and the item ends up
        /// delivering the wrong amount of health.
        ///
        /// Total healed is points * (_stateTime / WorkStateTime). Keep those equal and the total is
        /// exact at any speed - so both are set to the new duration, and the elapsed time is scaled
        /// to preserve how far the heal had got.
        /// </summary>
        private static void Rescale(ActiveHealthController.Effect effect, float oldMultiplier, float newMultiplier)
        {
            if (StateTimeField == null) return;

            var workTime = effect.WorkStateTime;
            if (workTime <= 0f || newMultiplier <= 0f) return;

            var progress = Mathf.Clamp01(effect.StateCurrentTime / workTime);

            var rescaled = workTime * (oldMultiplier / newMultiplier);
            if (rescaled <= 0f) return;

            effect.WorkStateTime = rescaled;
            StateTimeField.SetValue(effect, rescaled);
            effect.StateCurrentTime = progress * rescaled;
        }
    }

    /// <summary>
    /// Captures the effect DoMedEffect just created, and scales its duration.
    ///
    /// Hooking the return value here rather than searching for the effect afterwards is what makes
    /// this work for every med rather than only surgery kits.
    /// </summary>
    internal class DoMedEffectPatch : ModulePatch
    {
        private static bool _reported;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(
                typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect));
        }

        [PatchPostfix]
        private static void Postfix(Item item, IHealthEffect __result)
        {
            try
            {
                if (__result is not ActiveHealthController.MedEffect effect) return;

                HealSpeed.CaptureEffect(effect);

                if (Plugin.LogHealSpeed)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuickMeds: effect created for {item?.TemplateId.ToString() ?? "?"} " +
                        $"mult={HealSpeed.Current:0.00} work={effect.WorkStateTime:0.00} " +
                        $"scalable={HealSpeed.HasApplicationDuration(item)}");
                }

                if (!HealSpeed.Active) return;

                // Pills and injectors keep their timing: their work time is the buff duration.
                // Food and drink never reach here, having been excluded in Begin.
                if (!HasApplicationDuration(item)) return;

                var before = effect.WorkStateTime;
                var beforeDelay = effect.DelayTime;

                // Safe to set WorkStateTime alone at this point: NextState has not yet copied it
                // into _stateTime, so the duration picks the new value up on its own. Only
                // mid-heal changes need all three set together - see HealSpeed.Rescale.
                effect.WorkStateTime /= HealSpeed.Current;

                // The start delay is scaled too, or the action is inconsistent with itself.
                //
                // Before healing begins the game waits out a delay that varies by item type - a
                // medkit's is noticeably long. Scaling only the healing left the animation running
                // fast while the delay still took its full time, so the item moved quickly, then
                // paused, then finally started healing. Worst on a Grizzly, which has the longest
                // wait.
                //
                // DelayTime is per-effect rather than a global setting, so this affects only the
                // heal in progress. Safe here because the effect has not started ticking yet -
                // elapsed time is still zero, so there is no risk of skipping past a delay that is
                // already part-way through.
                effect.DelayTime /= HealSpeed.Current;

                if (Plugin.LogHealSpeed)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuickMeds: work {before:0.00} -> {effect.WorkStateTime:0.00} " +
                        $"delay {beforeDelay:0.00} -> {effect.DelayTime:0.00}");
                }
            }
            catch (Exception ex)
            {
                if (!_reported)
                {
                    _reported = true;
                    Plugin.LogSource?.LogError(
                        $"QuickMeds: adjusting heal duration failed, adaptive speed is off: {ex.Message}");
                }

                Plugin.DisableAdaptiveHealSpeed();
                HealSpeed.Clear();
            }
        }

        private static bool HasApplicationDuration(Item item) => HealSpeed.HasApplicationDuration(item);
    }

    /// <summary>Samples the speed at the start of each body part.</summary>
    internal class HealSpeedPatch : ModulePatch
    {
        private static readonly FieldInfo ControllerField =
            AccessTools.Field(typeof(Player.MedsController.MedsInHandsOperation), "_controller");

        private static readonly FieldInfo PlayerField =
            AccessTools.Field(typeof(Player.ItemHandsController), "_player");

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(Player.MedsController.MedsInHandsOperation),
                nameof(Player.MedsController.MedsInHandsOperation.GoNextBodyPart));
        }

        [PatchPrefix]
        private static void Prefix(Player.MedsController.MedsInHandsOperation __instance)
        {
            var controller = ControllerField?.GetValue(__instance) as Player.MedsController;
            var player = controller == null ? null : PlayerField?.GetValue(controller) as Player;

            // Sampled BEFORE the original runs, so DoMedEffect's postfix already has the right
            // multiplier when the effect is created.
            HealSpeed.Begin(player, controller);
        }
    }

    /// <summary>
    /// Scales the animation, and records the game's own factor for later re-application.
    ///
    /// The parameter MUST be named "speed" - Harmony binds prefix parameters by name against the
    /// target signature, SetUseTimeMultiplier(float speed).
    /// </summary>
    internal class UseTimeMultiplierPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetUseTimeMultiplier));
        }

        [PatchPrefix]
        private static void Prefix(ref float speed)
        {
            if (!HealSpeed.Active) return;

            var before = speed;

            // The multiplier is applied through ONE mechanism, not two.
            //
            // There are two ways to speed up the animation, and this mod was using both:
            //
            //   SetUseTimeMultiplier - sets an animator float parameter. Only has an effect if the
            //                          animation controller was authored to read it, which appears
            //                          to be true for surgery animations and not for others.
            //   SetAnimationSpeed    - sets Animator.speed directly. Works on everything.
            //
            // Where both are honoured they multiply together: 2.5 x 2.5 = 6.25. That is the surgery
            // kit animation running several times too fast, finishing early, and leaving the hands
            // off screen waiting for the healing to catch up. Other items were unaffected only
            // because the parameter did nothing for them.
            //
            // The value is deliberately passed through UNCHANGED. Only the playback-speed path
            // scales the animation now; scaling here as well is what produced the runaway surgery
            // animation. Logged anyway, because seeing what the game itself passes is useful when
            // something looks wrong.
            if (Plugin.LogHealSpeed)
            {
                Plugin.LogSource?.LogInfo($"QuickMeds: anim-mult game={before:0.00} (passed through)");
            }
        }
    }

    /// <summary>
    /// Re-evaluates speed when the player starts or stops moving. Event-driven, so a whole heal
    /// costs a few calls rather than one per frame.
    /// </summary>
    internal class MovementStateChangedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.MovementContextOnStateChanged));
        }

        [PatchPostfix]
        private static void Postfix(Player __instance)
        {
            try
            {
                if (!ModEnvironment.IsLocalLivingPlayer(__instance)) return;

                HealSpeed.Retarget(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuickMeds: could not retarget heal speed: {ex.Message}");
            }
        }
    }
}
