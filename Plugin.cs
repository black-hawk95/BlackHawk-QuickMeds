using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using QuickMeds.Patches;
using UnityEngine;

namespace QuickMeds
{
    [BepInPlugin("com.blackhawk.quickmeds", "BlackHawk-QuickMeds", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private const string SectionMovement = "1. Movement";
        private const string SectionHealing = "2. Healing";

        // A real BepInEx section, which is what the F12 menu groups by. 1.0.2 tried to separate
        // these with a config entry keyed on whitespace; BepInEx rejected the key, Awake threw,
        // and the entire mod silently failed to load. Sections cost nothing and cannot do that.
        private const string SectionHealSpeed = "3. Healing speed";

        public static ManualLogSource LogSource;

        // False when the movement patch was never applied - either Make Meds Great Again is
        // handling it, or the patch failed. Either way the settings must read as off, or the
        // F12 menu would claim a feature is enabled when nothing is enforcing it.
        private static bool _movementPatchApplied;

        // ---- movement
        private static ConfigEntry<bool> _sprintWhileUsingMeds;
        private static ConfigEntry<bool> _walkWhileUsingSurgery;
        private static ConfigEntry<bool> _sprintWhileUsingSurgery;

        // ---- healing
        private static ConfigEntry<bool> _adaptiveHealSpeed;
        private static ConfigEntry<bool> _autoCancelWhenDone;
        private static ConfigEntry<bool> _logHealSpeed;
        private static ConfigEntry<float> _stillHealSpeed;
        private static ConfigEntry<float> _walkHealSpeed;
        private static ConfigEntry<float> _sprintHealSpeed;

        public static bool SprintWhileUsingMeds => _movementPatchApplied && (_sprintWhileUsingMeds?.Value ?? true);
        public static bool WalkWhileUsingSurgery => _movementPatchApplied && (_walkWhileUsingSurgery?.Value ?? true);
        /// <summary>
        /// Sprinting during leg treatment. Requires walking during leg treatment to be on as well.
        ///
        /// The dependency is enforced HERE rather than only greyed out in the menu, so the
        /// behaviour matches what the menu shows even if the setting was edited in the config file
        /// directly. If you cannot walk while splinting, you certainly cannot run.
        /// </summary>
        public static bool SprintWhileUsingSurgery =>
            WalkWhileUsingSurgery && _movementPatchApplied && (_sprintWhileUsingSurgery?.Value ?? true);

        public static bool AdaptiveHealSpeed => _adaptiveHealSpeed?.Value ?? true;
        public static bool AutoCancelWhenDone => _autoCancelWhenDone?.Value ?? true;
        public static bool LogHealSpeed => _logHealSpeed?.Value ?? false;
        public static float StillHealSpeed => _stillHealSpeed?.Value ?? 1.3f;
        public static float WalkHealSpeed => _walkHealSpeed?.Value ?? 1.0f;
        public static float SprintHealSpeed => _sprintHealSpeed?.Value ?? 0.8f;

        /// <summary>
        /// Turns adaptive speed off from inside the patch when it faults, so a broken feature
        /// cannot keep running every frame.
        /// </summary>
        public static void DisableAdaptiveHealSpeed()
        {
            if (_adaptiveHealSpeed != null) _adaptiveHealSpeed.Value = false;
        }

        private void Awake()
        {
            LogSource = Logger;

            // A Fika headless client has no player to heal.
            if (ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuickMeds: headless client detected, plugin disabled.");
                return;
            }

            // Both are guarded, and separately.
            //
            // In 1.0.2 a single bad config key threw inside BindSettings, Awake died, and NO
            // patches were applied at all - with nothing in the log to say so. Features that had
            // worked for weeks appeared to break. Guarding only the patching, as the previous
            // version did, was not enough: the fault happened one step earlier.
            //
            // Settings failing is survivable, since every setting falls back to a default, so
            // patching is still attempted afterwards.
            try
            {
                BindSettings();
            }
            catch (System.Exception ex)
            {
                Logger.LogError(
                    $"QuickMeds: settings could not be created, so defaults are in use and the F12 " +
                    $"menu will be incomplete. The mod itself still works. Reason: {ex.Message}");
            }

            try
            {
                EnablePatches();
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"QuickMeds: startup failed, the mod is inactive. Reason: {ex}");
                return;
            }

            // A definite end-of-startup marker. Its absence in the log means Awake did not finish,
            // which is otherwise invisible.
            if (ModEnvironment.IsAutoCancelModInstalled)
            {
                Logger.LogInfo(
                    $"QuickMeds: Healing Auto Cancel detected ({ModEnvironment.AutoCancelGuid}). " +
                    "Leaving the stop-when-full behaviour to it.");
            }

            Logger.LogInfo("QuickMeds: startup complete.");
        }

        private void BindSettings()
        {
            _sprintWhileUsingMeds = Config.Bind(SectionMovement, "Sprint while using meds", true,
                new ConfigDescription(
                    "Run while using bandages, medkits and pills.\n\n" +
                    "Splints and surgery kits have their own settings below.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _walkWhileUsingSurgery = Config.Bind(SectionMovement, "Walk while using splints or surgery kits", true,
                new ConfigDescription(
                    "Move while splinting or doing surgery, instead of standing still the whole time.",
                    null, new ConfigurationManagerAttributes { Order = 99 }));

            _sprintWhileUsingSurgery = Config.Bind(SectionMovement, "Sprint while using splints or surgery kits (needs walk on)", true,
                new ConfigDescription(
                    "Run while splinting or doing surgery.\n\n" +
                    "Turn this off and leave walking on if you want to walk but not run.\n\n" +
                    "Does nothing unless the walk setting above is on.",
                    null, new ConfigurationManagerAttributes { Order = 98 }));

            _autoCancelWhenDone = Config.Bind(SectionHealing, "Heal auto cancel", true,
                new ConfigDescription(
                    "Stop healing the moment the body part is full, instead of finishing the " +
                    "animation for nothing. Also stops when the item runs out.\n\n" +
                    "Keeps going if the part is still bleeding. Broken bones always take the " +
                    "full time.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _adaptiveHealSpeed = Config.Bind(SectionHealSpeed, "Adaptive healing speed", true,
                new ConfigDescription(
                    "Heal faster when standing still, slower when running.\n\n" +
                    "Turn off for normal healing times. Use the sliders below to set the speeds.\n\n" +
                    "Does not affect pills, injectors, food or drink.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _stillHealSpeed = Config.Bind(SectionHealSpeed, "Speed - standing still", 1.3f,
                new ConfigDescription(
                    "Healing speed while not moving. 1.00 is normal, 2.00 is twice as fast.",
                    new AcceptableValueRange<float>(0.7f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 99 }));

            _walkHealSpeed = Config.Bind(SectionHealSpeed, "Speed - walking", 1.0f,
                new ConfigDescription(
                    "Healing speed while walking. 1.00 means walking costs you nothing.",
                    new AcceptableValueRange<float>(0.7f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 98 }));

            _sprintHealSpeed = Config.Bind(SectionHealSpeed, "Speed - sprinting", 0.8f,
                new ConfigDescription(
                    "Healing speed while running. Below 1.00 means running makes it take longer.\n\n" +
                    "Only matters if you can sprint while healing.",
                    new AcceptableValueRange<float>(0.7f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 97 }));

            _logHealSpeed = Config.Bind(SectionHealSpeed, "Log details to console", false,
                new ConfigDescription(
                    "Write what the mod is doing to the BepInEx log. Only needed for reporting a bug.",
                    null, new ConfigurationManagerAttributes { Order = 90 }));

        }


        private void EnablePatches()
        {
            // Movement is handled by a single prefix on SetPhysicalCondition. Make Meds Great
            // Again patches the same method for the same purpose, so when it is present QuickMeds
            // stands down rather than stacking a second prefix on the same call.
            if (ModEnvironment.IsMmgaInstalled)
            {
                Logger.LogInfo(
                    $"QuickMeds: Make Meds Great Again detected ({ModEnvironment.MmgaGuid}). " +
                    "Leaving the movement features to it; healing features are unaffected.");
            }
            else if (_sprintWhileUsingMeds.Value || _walkWhileUsingSurgery.Value || _sprintWhileUsingSurgery.Value)
            {
                _movementPatchApplied = TryEnable("movement", new PhysicalConditionPatch());
            }

            // Each patch is enabled independently.
            //
            // This matters more than it looks: an earlier version enabled them in a bare sequence,
            // and when one threw during patching the exception escaped and every patch after it
            // was silently skipped. Three separate features appeared broken because of one fault
            // in a fourth. A failure here now costs only the feature that failed.
            TryEnable("healing finished", new HealingFinishedPatch());
            TryEnable("healing reset", new HealingResetPatch());
            TryEnable("healing removed", new HealingRemovedPatch());
            TryEnable("adaptive heal speed", new HealSpeedPatch());
            TryEnable("heal effect timing", new DoMedEffectPatch());
            TryEnable("heal animation speed", new UseTimeMultiplierPatch());
            TryEnable("heal speed follows movement", new MovementStateChangedPatch());
        }

        /// <summary>
        /// Applies one patch, reporting success or failure without letting it affect the others.
        ///
        /// Note the message is logged AFTER Enable() returns. Harmony validates the patched method
        /// during Enable, so anything logged before that can claim success for a patch that is
        /// about to be rejected - which is exactly what happened during development, and made a
        /// broken patch look like a working one.
        /// </summary>
        private bool TryEnable(string description, SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
                Logger.LogInfo($"QuickMeds: {description} enabled.");
                return true;
            }
            catch (System.Exception ex)
            {
                Logger.LogError(
                    $"QuickMeds: could not enable {description}, so that feature is off. " +
                    $"Everything else still works. Reason: {ex.Message}");
                return false;
            }
        }
    }
}
