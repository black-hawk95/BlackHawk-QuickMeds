using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Owns exactly one LOCAL player's medicine session.
    ///
    /// Important invariants:
    ///  - remote/Fika observed players are rejected before any shared state is touched;
    ///  - QuickMeds never patches EFT's generic AddEffect<T> factory (it is used during player spawn);
    ///  - the vanilla start-delay portion stays at 1.0x, then work timing + animation switch together at MedEffect.Started;
    ///  - only the exact controller/item/effect owned by the current local session can be changed;
    ///  - animator writes are absolute (never multiplied on top of a previous QuickMeds value);
    ///  - every medicine uses the same F12 selection gate across start, retarget and animation;
    ///  - pills/injectors use category switches; other medicine can still be controlled per template;
    ///  - food/drink remain vanilla.
    /// </summary>
    internal static class HealSpeed
    {
        private const float Epsilon = 0.001f;
        private const float MinMultiplier = 0.25f;
        private const float MaxMultiplier = 2.5f;

        private static readonly FieldInfo OperationControllerField =
            AccessTools.Field(typeof(Player.MedsController.MedsInHandsOperation), "_controller");

        private static Player _ownerPlayer;
        private static Player.MedsController _controller;
        private static Item _item;
        private static ActiveHealthController.MedEffect _currentEffect;
        private static bool _awaitingEffect;
        private static float _effectMultiplier = 1f;

        private static bool _animatorSpeedApplied;
        private static float _animatorBaseline = 1f;
        private static float _lastAnimatorWrite = 1f;

        public static float Current { get; private set; } = 1f;
        public static bool IsTracking => _ownerPlayer != null && _controller != null;

        public static float Evaluate(Player player, Item item = null)
        {
            item ??= _item;

            if (!Plugin.AdaptiveHealSpeed || !Plugin.AdaptiveHealSpeedAvailable)
                return 1f;

            if (!Plugin.IsMedicineEnabled(item))
                return 1f;

            if (!ModEnvironment.IsLocalLivingPlayer(player))
                return 1f;

            var state = player.MovementContext?.CurrentState?.Name;
            if (state == null)
                return 1f;

            float value = state.Value switch
            {
                EPlayerState.None or
                EPlayerState.Idle or
                EPlayerState.ProneIdle or
                EPlayerState.Transition => Plugin.StillHealSpeed,

                EPlayerState.Sprint => Plugin.SprintHealSpeed,
                _ => Plugin.WalkHealSpeed,
            };

            // Config ranges normally clamp this already. Clamp again because users can hand-edit
            // cfg files and because timing must never receive zero/negative/non-finite values.
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 1f;

            return Mathf.Clamp(value, MinMultiplier, MaxMultiplier);
        }

        public static bool IsFoodOrDrink(Item item)
        {
            return item != null && item.TryGetItemComponent<FoodDrinkComponent>(out _);
        }

        /// <summary>
        /// Adaptive-speed ownership accepts EFT medicine items, including Drugs (pills) and
        /// Stimulator (injectors). Whether the current medicine is scaled is decided by
        /// Plugin.IsMedicineEnabled so every path uses the same F12 gate.
        /// </summary>
        public static bool IsMedicineItem(Item item)
        {
            return item is Meds && !IsFoodOrDrink(item);
        }

        /// <summary>
        /// Auto-cancel intentionally stays limited to repair/heal items. Pills/injectors can have
        /// timed effects that are not equivalent to filling a body part, so they must never inherit
        /// the medkit stop-when-full heuristic just because adaptive speed supports them.
        /// </summary>
        public static bool IsAutoCancelItem(Item item)
        {
            return IsMedicineItem(item) && item is not Drugs && item is not Stimulator;
        }

        public static void Begin(Player player, Player.MedsController controller)
        {
            try
            {
                // Critical Fika rule: reject remote players BEFORE touching any static state.
                if (!ModEnvironment.IsLocalLivingPlayer(player) || controller == null)
                {
                    Debug($"BEGIN ignored remote/invalid player controller={Id(controller)}");
                    return;
                }

                // A genuinely new controller means the previous local session did not tear down
                // cleanly. Restore only what QuickMeds itself still owns before taking new ownership.
                if (_controller != null && !ReferenceEquals(_controller, controller))
                {
                    Debug($"BEGIN owner switch old={Id(_controller)} new={Id(controller)}; cleaning old session");
                    ClearInternal("owner-switch");
                }

                _ownerPlayer = player;
                _controller = controller;
                _item = controller.Item;
                _currentEffect = null;
                _awaitingEffect = true;
                _effectMultiplier = 1f;

                // Every new body-part application begins with EFT's vanilla start-delay timing.
                // If a previous limb left QuickMeds owning Animator.speed on the same controller,
                // release it now so the delay and animation remain aligned at 1.0x.
                RestoreAnimatorIfOwned("begin-delay");

                if (!IsMedicineItem(_item))
                {
                    Current = 1f;
                    RestoreAnimatorIfOwned("begin-excluded");
                    _awaitingEffect = false;
                    Debug($"BEGIN excluded item={ItemName()} type={_item?.GetType().Name ?? "null"}");
                    return;
                }

                Current = Evaluate(player, _item);

                // Do not speed the animation during MedEffect.Added. EFT's start delay is still
                // vanilla, so changing Animator.speed here would make the visible action outrun
                // the health effect. We switch both at MedEffect.Started instead.
                Debug($"ANIM deferred until effect-started target={Current:0.00} reads={ReadAnimatorSpeed(controller):0.000}");

                // Auto-cancel is independent from adaptive speed and still works at 1.0x/off.
                HealAutoCancel.Watch(player, _item);

                Debug(
                    $"BEGIN ownerPlayer=true controller={Id(controller)} item={ItemName()} " +
                    $"type={_item?.GetType().Name ?? "null"} move={MovementName(player)} mult={Current:0.00} " +
                    $"selected={Plugin.IsMedicineEnabled(_item)} adaptive={Plugin.AdaptiveHealSpeed} " +
                    $"timingAvailable={Plugin.AdaptiveHealSpeedAvailable}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: Begin failed; session cleared safely.\n{ex}");
                ClearInternal("begin-exception");
            }
        }

        public static void CaptureEffect(
            ActiveHealthController health,
            Item item,
            ActiveHealthController.MedEffect effect)
        {
            if (effect == null)
                return;

            if (!OwnsHealthController(health) || !MatchesItem(item) || !IsMedicineItem(item))
            {
                Debug(
                    $"CAPTURE ignored healthMatch={OwnsHealthController(health)} itemMatch={MatchesItem(item)} " +
                    $"item={item?.TemplateId.ToString() ?? "null"} effect={EffectSummary(effect)}");
                return;
            }

            // The effect is initialized with its MedItem before DoMedEffect returns. Verify it is the
            // exact item we own; this prevents an unrelated MedEffect from replacing local state.
            if (effect.MedItem != null && !ReferenceEquals(effect.MedItem, _item) && !SameItemId(effect.MedItem, _item))
            {
                Debug($"CAPTURE rejected mismatched MedItem effectItem={effect.MedItem.TemplateId} ownerItem={ItemName()}");
                return;
            }

            AdoptEffect(effect, "capture");
            _awaitingEffect = false;
            Debug($"CAPTURE {EffectSummary(effect)} target={Current:0.00} applied={_effectMultiplier:0.00}");

            // Zero-delay medicines can already be Started before DoMedEffect returns. The dedicated
            // Started postfix normally handles them, but this is a safe idempotent fallback.
            if (effect.State == EEffectState.Started)
                ApplyStartedTarget(effect, "capture-started");
        }

        public static bool OwnsHealthController(ActiveHealthController health)
        {
            return IsTracking && health != null && ReferenceEquals(_ownerPlayer.ActiveHealthController, health);
        }

        public static bool MatchesItem(Item item)
        {
            if (_item == null || item == null)
                return false;

            return ReferenceEquals(_item, item) || SameItemId(_item, item);
        }

        private static bool SameItemId(Item left, Item right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.Id.ToString(), right.Id.ToString(), StringComparison.Ordinal);
        }

        public static bool OwnsAnimator(FirearmsAnimator animator)
        {
            return animator != null && _controller?.FirearmsAnimator != null &&
                   ReferenceEquals(_controller.FirearmsAnimator, animator);
        }

        public static void Retarget(Player player, string source = "movement")
        {
            try
            {
                if (!ModEnvironment.IsLocalLivingPlayer(player))
                    return;

                if (!ReferenceEquals(player, _ownerPlayer))
                {
                    Debug($"RETARGET ignored non-owner local reference source={source}");
                    return;
                }

                if (player.HandsController is not Player.MedsController controller ||
                    !ReferenceEquals(controller, _controller))
                {
                    Debug($"RETARGET skipped controller mismatch source={source} live={Id(player.HandsController)} owner={Id(_controller)}");
                    return;
                }

                if (!IsMedicineItem(controller.Item) || !MatchesItem(controller.Item))
                {
                    Debug($"RETARGET skipped excluded/mismatched item source={source}");
                    return;
                }

                var effect = FindOwnedMedEffect(player, controller.Item) ?? _currentEffect;
                if (effect != null && (effect.State == EEffectState.Residued || effect.State == EEffectState.Removed))
                {
                    Debug($"RETARGET skipped teardown state={effect.State} source={source}");
                    return;
                }

                float updated = Evaluate(player, controller.Item);

                // During the vanilla Added/start-delay phase we only remember the newest target.
                // Animator.speed stays at its baseline so the visible delay stays synchronized.
                if ((effect != null && effect.State == EEffectState.Added) || (effect == null && _awaitingEffect))
                {
                    float deferredFrom = Current;
                    Current = updated;
                    Debug(
                        $"RETARGET deferred until Started source={source} move={MovementName(player)} " +
                        $"target={deferredFrom:0.00}->{updated:0.00} animator={ReadAnimatorSpeed(controller):0.000}");
                    return;
                }

                if (effect == null)
                {
                    Debug($"RETARGET skipped: no owned effect source={source}");
                    return;
                }

                if (effect.State != EEffectState.Started)
                {
                    Debug($"RETARGET skipped unsupported state={effect.State} source={source}");
                    return;
                }

                float previousTarget = Current;
                float previousApplied = _effectMultiplier;

                if (Math.Abs(updated - _effectMultiplier) > Epsilon)
                {
                    Rescale(effect, _effectMultiplier, updated);
                    _effectMultiplier = updated;
                }

                Current = updated;
                ApplyAnimatorSpeed(controller, updated, $"retarget:{source}");

                if (Math.Abs(updated - previousTarget) <= Epsilon &&
                    Math.Abs(updated - previousApplied) <= Epsilon)
                {
                    Debug($"RETARGET unchanged source={source} move={MovementName(player)} mult={updated:0.00}");
                    return;
                }

                Debug(
                    $"RETARGET applied source={source} move={MovementName(player)} " +
                    $"target={previousTarget:0.00}->{updated:0.00} effect={previousApplied:0.00}->{_effectMultiplier:0.00} " +
                    $"state={EffectSummary(effect)}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: Retarget failed; adaptive speed disabled for safety.\n{ex}");
                Plugin.DisableAdaptiveHealSpeed();
                RestoreAnimatorIfOwned("retarget-exception");
                Current = 1f;
                _effectMultiplier = 1f;
            }
        }

        public static void RefreshFromSettings(string source)
        {
            var player = _ownerPlayer;
            if (player == null)
                return;

            Retarget(player, source);
        }

        /// <summary>
        /// Refreshes a live medicine session only when the setting that changed actually applies
        /// to the item currently in the player's hands. This keeps category toggles (all pills /
        /// all injectors) from generating unrelated retargets for an active medkit or surgery kit.
        /// </summary>
        public static void RefreshFromSettingsIfCurrentItem(string source, Func<Item, bool> predicate)
        {
            var item = _item;
            if (item == null || predicate == null || !predicate(item))
                return;

            RefreshFromSettings(source);
        }

        public static void OnEffectStarted(ActiveHealthController.MedEffect effect)
        {
            try
            {
                if (effect == null || !IsTracking)
                    return;

                if (effect.MedItem == null || !MatchesItem(effect.MedItem))
                    return;

                if (!IsExactOwnedEffectLive(effect))
                {
                    Debug($"EFFECT started ignored: ownership mismatch effect={EffectSummary(effect)}");
                    return;
                }

                AdoptEffect(effect, "started");
                _awaitingEffect = false;
                Debug($"EFFECT started {EffectSummary(effect)} target={Current:0.00} applied={_effectMultiplier:0.00}");
                ApplyStartedTarget(effect, "effect-started");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: effect-started retarget failed; adaptive speed disabled.\n{ex}");
                Plugin.DisableAdaptiveHealSpeed();
                RestoreAnimatorIfOwned("effect-started-exception");
                Current = 1f;
                _effectMultiplier = 1f;
            }
        }

        private static void ApplyStartedTarget(ActiveHealthController.MedEffect effect, string source)
        {
            if (effect == null || effect.State != EEffectState.Started || _controller == null)
                return;

            float target = Evaluate(_ownerPlayer, _item);
            float previousApplied = _effectMultiplier;

            if (Math.Abs(target - previousApplied) > Epsilon)
            {
                Rescale(effect, previousApplied, target);
                _effectMultiplier = target;
            }

            Current = target;
            ApplyAnimatorSpeed(_controller, target, source);

            Debug(
                $"STARTED target source={source} applied={previousApplied:0.00}->{_effectMultiplier:0.00} " +
                $"move={MovementName(_ownerPlayer)} animator={ReadAnimatorSpeed(_controller):0.000} " +
                $"effect={EffectSummary(effect)}");
        }

        /// <summary>
        /// Takes ownership of a newly-created MedEffect without assuming GoNextBodyPart ran first.
        /// ContinuousHealing starts the next limb directly with DoMedEffect() from OnEffectRemoved,
        /// so the MedsController/item can stay the same while the effect object changes underneath it.
        ///
        /// A new EFT MedEffect always begins from vanilla 1.0x timing. Resetting the per-effect applied
        /// multiplier here prevents the next limb from inheriting the previous limb's multiplier. The
        /// animator is released to its saved baseline during any Added/start-delay phase; Started then
        /// applies the current movement target to both timing and animation together.
        /// </summary>
        private static void AdoptEffect(ActiveHealthController.MedEffect effect, string source)
        {
            if (effect == null)
                return;

            if (_currentEffect != null && !ReferenceEquals(_currentEffect, effect))
            {
                var previous = _currentEffect;
                float previousApplied = _effectMultiplier;

                RestoreAnimatorIfOwned($"effect-boundary:{source}");
                _effectMultiplier = 1f;

                Debug(
                    $"EFFECT boundary source={source} oldId={previous.Id} oldState={previous.State} " +
                    $"newId={effect.Id} newState={effect.State} applied={previousApplied:0.00}->1.00 " +
                    $"continuousHealing={ModEnvironment.IsContinuousHealingInstalled}");

                if (ModEnvironment.IsContinuousHealingInstalled)
                {
                    Debug(
                        $"COMPAT ContinuousHealing chained effect old={previous.Id} new={effect.Id}; " +
                        "new effect starts from vanilla timing");
                }
            }

            _currentEffect = effect;
        }

        private static bool IsExactOwnedEffectLive(ActiveHealthController.MedEffect effect)
        {
            var health = _ownerPlayer?.ActiveHealthController;
            if (health == null || effect == null)
                return false;

            foreach (var candidate in health.GetAllActiveEffects())
            {
                if (ReferenceEquals(candidate, effect))
                    return true;
            }

            return false;
        }

        private static ActiveHealthController.MedEffect FindOwnedMedEffect(Player player, Item item)
        {
            var health = player?.ActiveHealthController;
            if (health == null || item == null)
                return null;

            foreach (var candidate in health.GetAllActiveEffects())
            {
                if (candidate is not ActiveHealthController.MedEffect med || !med.Existing)
                    continue;

                if (med.MedItem == null)
                    continue;

                if (ReferenceEquals(med.MedItem, item) || SameItemId(med.MedItem, item))
                    return med;
            }

            return null;
        }

        private static void Rescale(
            ActiveHealthController.MedEffect effect,
            float oldMultiplier,
            float newMultiplier)
        {
            if (effect == null || effect.State != EEffectState.Started)
            {
                Debug($"RESCALE skipped state={effect?.State.ToString() ?? "null"}");
                return;
            }

            if (oldMultiplier <= 0f || newMultiplier <= 0f)
                return;

            float oldWork = effect.WorkStateTime;
            if (oldWork <= Epsilon || float.IsInfinity(oldWork) || float.IsNaN(oldWork))
                return;

            float elapsed = Mathf.Clamp(effect.StateCurrentTime, 0f, oldWork);
            float progress = Mathf.Clamp01(elapsed / oldWork);
            float ratio = oldMultiplier / newMultiplier;
            float newWork = Math.Max(0f, oldWork * ratio);
            float newElapsed = progress * newWork;
            float newRemaining = Math.Max(0f, newWork - newElapsed);

            // Preserve treatment progress, then use EFT's own AddWorkTime(reset:true) path to set
            // the new state total. Unlike writing _stateTime directly, AddWorkTime emits the
            // EffectStateTime network packet, so Fika peers receive the new remaining duration.
            effect.StateCurrentTime = newElapsed;
            effect.AddWorkTime(newRemaining, true);

            Debug(
                $"RESCALE Started ratio={ratio:0.000} progress={progress:0.000} " +
                $"work={oldWork:0.000}->{effect.WorkStateTime:0.000} elapsed={elapsed:0.000}->{effect.StateCurrentTime:0.000} " +
                $"remaining={newRemaining:0.000}");
        }

        private static void ApplyAnimatorSpeed(Player.MedsController controller, float value, string source)
        {
            var animator = controller?.FirearmsAnimator;
            if (animator == null)
            {
                Debug($"ANIM skipped null animator source={source}");
                return;
            }

            value = Mathf.Clamp(value, MinMultiplier, MaxMultiplier);

            // Returning to vanilla multiplier means QuickMeds should release animator ownership,
            // restoring whatever value existed before we touched it. Do not assume that baseline
            // was 1.0: another compatible mod may legitimately own a different value.
            if (Math.Abs(value - 1f) <= Epsilon)
            {
                if (_animatorSpeedApplied)
                    RestoreAnimatorIfOwned($"target-vanilla:{source}");
                else
                    Debug($"ANIM no-write vanilla source={source} reads={ReadAnimatorSpeed(controller):0.000}");
                return;
            }

            if (!_animatorSpeedApplied)
            {
                float current = ReadAnimatorSpeed(controller);
                _animatorBaseline = current > 0f && !float.IsNaN(current) && !float.IsInfinity(current) ? current : 1f;
            }
            else if (Math.Abs(_lastAnimatorWrite - value) <= Epsilon)
            {
                Debug($"ANIM no-write unchanged source={source} value={value:0.000}");
                return;
            }

            animator.SetAnimationSpeed(value);
            _lastAnimatorWrite = value;
            _animatorSpeedApplied = true;

            Debug(
                $"ANIM write source={source} set={value:0.000} reads={ReadAnimatorSpeed(controller):0.000} " +
                $"baseline={_animatorBaseline:0.000} controller={Id(controller)} item={ItemName()}");
        }

        private static void RestoreAnimatorIfOwned(string source)
        {
            if (!_animatorSpeedApplied)
                return;

            var controller = _controller;
            var animator = controller?.FirearmsAnimator;
            if (animator == null)
            {
                _animatorSpeedApplied = false;
                Debug($"ANIM restore skipped null animator source={source}");
                return;
            }

            float current = ReadAnimatorSpeed(controller);

            // If another mod changed Animator.speed after our last write, do not stomp its value.
            // We only restore when the animator still contains the value QuickMeds last owned.
            if (Math.Abs(current - _lastAnimatorWrite) <= 0.01f)
            {
                animator.SetAnimationSpeed(_animatorBaseline);
                Debug(
                    $"ANIM restore source={source} {_lastAnimatorWrite:0.000}->{_animatorBaseline:0.000} " +
                    $"reads={ReadAnimatorSpeed(controller):0.000}");
            }
            else
            {
                Debug(
                    $"ANIM restore ownership lost source={source} current={current:0.000} " +
                    $"lastQuickMeds={_lastAnimatorWrite:0.000}; no write performed");
            }

            _animatorSpeedApplied = false;
            _animatorBaseline = 1f;
            _lastAnimatorWrite = 1f;
        }

        public static Player.MedsController GetOperationController(
            Player.MedsController.MedsInHandsOperation operation)
        {
            return operation == null
                ? null
                : OperationControllerField?.GetValue(operation) as Player.MedsController;
        }

        public static void ClearIfOwner(Player.MedsController controller, string reason)
        {
            try
            {
                if (controller == null)
                {
                    Debug($"CLEAR ignored reason={reason}: captured controller unavailable");
                    return;
                }

                if (!ReferenceEquals(controller, _controller))
                {
                    Debug($"CLEAR ignored foreign controller reason={reason} got={Id(controller)} owner={Id(_controller)}");
                    return;
                }

                ClearInternal(reason);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: owner-aware teardown failed ({reason}).\n{ex}");
            }
        }

        private static void ClearInternal(string reason)
        {
            Debug(
                $"CLEAR reason={reason} controller={Id(_controller)} item={ItemName()} " +
                $"effect={(_currentEffect == null ? "null" : EffectSummary(_currentEffect))} mult={Current:0.00}");

            RestoreAnimatorIfOwned($"clear:{reason}");
            HealAutoCancel.Unwatch(reason);

            Current = 1f;
            _effectMultiplier = 1f;
            _currentEffect = null;
            _awaitingEffect = false;
            _item = null;
            _controller = null;
            _ownerPlayer = null;
        }

        private static float ReadAnimatorSpeed(Player.MedsController controller)
        {
            return controller?.FirearmsAnimator?.Animator?.speed ?? -1f;
        }

        private static string ItemName()
        {
            return _item?.TemplateId.ToString() ?? "none";
        }

        private static string MovementName(Player player)
        {
            return player?.MovementContext?.CurrentState?.Name.ToString() ?? "?";
        }

        private static string EffectSummary(ActiveHealthController.MedEffect effect)
        {
            if (effect == null)
                return "null";

            return $"id={effect.Id} state={effect.State} body={effect.BodyPart} " +
                   $"delay={effect.DelayTime:0.000} work={effect.WorkStateTime:0.000} " +
                   $"stateTime={effect.StateTime:0.000} elapsed={effect.StateCurrentTime:0.000} " +
                   $"item={effect.MedItem?.TemplateId.ToString() ?? "null"}";
        }

        private static string Id(object value)
        {
            return value == null ? "null" : RuntimeHelpers.GetHashCode(value).ToString("X8");
        }

        private static void Debug(string message)
        {
            if (Plugin.LogHealSpeed)
                Plugin.LogSource?.LogInfo("QuickMedsDBG: " + message);
        }
    }

    /// <summary>
    /// Samples local ownership and movement before EFT starts the next body-part application.
    /// </summary>
    internal class HealSpeedPatch : ModulePatch
    {
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
            try
            {
                var controller = HealSpeed.GetOperationController(__instance);
                var player = controller == null ? null : PlayerField?.GetValue(controller) as Player;
                HealSpeed.Begin(player, controller);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: heal-session prefix failed.\n{ex}");
            }
        }
    }

    /// <summary>
    /// Applies the requested multiplier at MedEffect.Started. EFT's initial Added/start-delay phase
    /// remains vanilla 1.0x; then work timing and animation change together through public/network-aware APIs.
    /// </summary>
    internal class MedEffectStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.DeclaredMethod(typeof(ActiveHealthController.MedEffect), "Started");
        }

        [PatchPostfix]
        private static void Postfix(ActiveHealthController.MedEffect __instance)
        {
            HealSpeed.OnEffectStarted(__instance);
        }
    }

    /// <summary>
    /// Captures only the exact local MedEffect returned by DoMedEffect. Timing is applied safely at
    /// MedEffect.Started; this hook keeps exact item/effect ownership and provides a zero-delay fallback.
    /// </summary>
    internal class DoMedEffectPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect));
        }

        [PatchPostfix]
        private static void Postfix(
            ActiveHealthController __instance,
            Item item,
            IHealthEffect __result)
        {
            try
            {
                if (__result is ActiveHealthController.MedEffect effect)
                    HealSpeed.CaptureEffect(__instance, item, effect);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: MedEffect capture failed.\n{ex}");
            }
        }
    }

    /// <summary>
    /// Diagnostic-only observer for EFT's own SetUseTimeMultiplier. QuickMeds does not change this
    /// value, avoiding the old double-multiplication (e.g. 2.5 x 2.5 = 6.25) failure.
    /// </summary>
    internal class UseTimeMultiplierPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetUseTimeMultiplier));
        }

        [PatchPrefix]
        private static void Prefix(FirearmsAnimator __instance, float speed)
        {
            if (!Plugin.LogHealSpeed || !HealSpeed.OwnsAnimator(__instance))
                return;

            Plugin.LogSource?.LogInfo($"QuickMedsDBG: USETIME game={speed:0.000} passed-through");
        }
    }

    /// <summary>Event-driven movement retargeting; local player only.</summary>
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
                if (ModEnvironment.IsLocalLivingPlayer(__instance))
                    HealSpeed.Retarget(__instance, "movement");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuickMeds: movement retarget hook failed.\n{ex}");
            }
        }
    }
}