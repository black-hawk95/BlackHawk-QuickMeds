using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using QuickMeds.Patches;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace QuickMeds
{
    [BepInPlugin("com.blackhawk.quickmeds", "BlackHawk-QuickMeds", "1.2.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("HealingAutoCancel", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.lacyway.ch", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        private const string SectionMovement = "1. Movement";
        private const string SectionHealing = "2. Healing";
        private const string SectionHealSpeed = "3. Healing speed";
        private const string SectionMedicines = "4. Per-medicine QuickMeds";
        private const string SectionDebug = "5. Debug";

        public static ManualLogSource LogSource;

        private static bool _movementPatchApplied;
        private static bool _healSessionPatchApplied;
        private static bool _effectCapturePatchApplied;
        private static bool _effectStartedPatchApplied;
        private static bool _movementRetargetPatchApplied;
        private static bool _teardownPatchesApplied;
        private static bool _adaptiveHealSpeedRuntimeDisabled;

        private static ConfigEntry<bool> _sprintWhileUsingMeds;
        private static ConfigEntry<bool> _walkWhileUsingSurgery;
        private static ConfigEntry<bool> _sprintWhileUsingSurgery;

        private static ConfigEntry<bool> _adaptiveHealSpeed;
        private static ConfigEntry<bool> _autoCancelWhenDone;
        private static ConfigEntry<bool> _logHealSpeed;
        private static ConfigEntry<bool> _allPillsEnabled;
        private static ConfigEntry<bool> _allInjectorsEnabled;
        private static ConfigEntry<float> _stillHealSpeed;
        private static ConfigEntry<float> _walkHealSpeed;
        private static ConfigEntry<float> _sprintHealSpeed;

        private static readonly Dictionary<string, ConfigEntry<bool>> MedicineEnabledSettings =
            new Dictionary<string, ConfigEntry<bool>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, MedicineGroup> MedicineGroupCache =
            new Dictionary<string, MedicineGroup>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] InjectorUseTokens =
        {
            "injector", "injection", "syringe", "stimulator", "med_stimulator",
            "\\stim\\", "/stim/", "\\syringe", "/syringe"
        };

        private static readonly string[] PillUseTokens =
        {
            "pill", "pills", "tablet", "tablets", "capsule", "capsules"
        };

        private const string MorphineTemplateId = "544fb3f34bdc2d03748b456a";

        private static readonly HashSet<string> VanillaPillTemplateIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "544fb37f4bdc2dee738b4567", // Analgin
                "590c695186f7741e566b64a2", // Augmentin
                "5af0548586f7743a532b7e99"  // Ibuprofen
            };

        private static bool _medicineCatalogBound;

        public static bool SprintWhileUsingMeds =>
            _movementPatchApplied && (_sprintWhileUsingMeds?.Value ?? true);

        public static bool WalkWhileUsingSurgery =>
            _movementPatchApplied && (_walkWhileUsingSurgery?.Value ?? true);

        public static bool SprintWhileUsingSurgery =>
            WalkWhileUsingSurgery &&
            _movementPatchApplied &&
            (_sprintWhileUsingSurgery?.Value ?? true);

        public static bool AdaptiveHealSpeed =>
            !_adaptiveHealSpeedRuntimeDisabled && (_adaptiveHealSpeed?.Value ?? true);
        public static bool AutoCancelWhenDone => _autoCancelWhenDone?.Value ?? true;
        public static bool LogHealSpeed => _logHealSpeed?.Value ?? false;
        public static float StillHealSpeed => _stillHealSpeed?.Value ?? 1.3f;
        public static float WalkHealSpeed => _walkHealSpeed?.Value ?? 1.0f;
        public static float SprintHealSpeed => _sprintHealSpeed?.Value ?? 0.8f;

        // Adaptive timing and animation are treated as one feature. No generic health-effect
        // factory is patched: the multiplier is applied at MedEffect.Started through EFT's own
        // network-aware AddWorkTime path, then movement changes retarget that exact effect.
        public static bool AdaptiveHealSpeedAvailable =>
            _healSessionPatchApplied &&
            _effectCapturePatchApplied &&
            _effectStartedPatchApplied &&
            _movementRetargetPatchApplied &&
            _teardownPatchesApplied;

        /// <summary>
        /// QuickMeds medicine gate. Pills share one category switch, injectors share one category
        /// switch, while medkits/medical/custom medicine retain exact per-template switches.
        /// The catalog is built from EFT's real ItemFactory after it is initialized, so custom
        /// medicine templates can still receive their own F12 toggle where individual control matters.
        /// </summary>
        public static bool IsMedicineEnabled(Item item)
        {
            if (item is not Meds || HealSpeed.IsFoodOrDrink(item))
                return false;

            // EFT has a legacy taxonomy quirk: Morphine is a Drugs item even though it is an
            // injector. Do not equate the runtime Drugs class with "pill". Classify by the
            // actual medicine/use template so Morphine and compatible syringe-style custom drugs
            // follow the injector master switch, while balms stay individually selectable.
            MedicineGroup group = ClassifyMedicineTemplate(item.Template);
            if (group == MedicineGroup.Injectors)
                return _allInjectorsEnabled?.Value ?? false;
            if (group == MedicineGroup.Pills)
                return _allPillsEnabled?.Value ?? false;

            string templateId = item.TemplateId.ToString();
            if (!string.IsNullOrEmpty(templateId) &&
                MedicineEnabledSettings.TryGetValue(templateId, out var entry) &&
                entry != null)
            {
                return entry.Value;
            }

            // Other medicine stays enabled by default if a custom template appears before/without
            // catalog binding. Food/drink were already rejected above.
            return true;
        }

        public static void DisableAdaptiveHealSpeed()
        {
            _adaptiveHealSpeedRuntimeDisabled = true;

            if (_adaptiveHealSpeed != null && _adaptiveHealSpeed.Value)
                _adaptiveHealSpeed.Value = false;
        }

        private void Awake()
        {
            LogSource = Logger;

            if (ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuickMeds: Fika headless client detected, plugin disabled.");
                return;
            }

            try
            {
                BindSettings();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "QuickMeds: settings could not be created. Safe defaults will be used where possible; " +
                    $"the F12 menu may be incomplete.\n{ex}");
            }

            try
            {
                EnablePatches();
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuickMeds: startup failed, plugin is inactive.\n{ex}");
                return;
            }

            // ItemFactory is created later than BepInEx plugins. Bind exact medicine-template
            // switches as soon as EFT has loaded the real template database.
            StartCoroutine(BindMedicineSettingsWhenReady());

            if (ModEnvironment.IsAutoCancelModInstalled)
            {
                Logger.LogInfo(
                    $"QuickMeds: COMPAT HealingAutoCancel detected guid={ModEnvironment.AutoCancelGuid} " +
                    $"version={ModEnvironment.AutoCancelVersion}; internal auto-cancel disabled.");
            }

            if (ModEnvironment.IsContinuousHealingInstalled)
            {
                Logger.LogInfo(
                    $"QuickMeds: COMPAT ContinuousHealing detected guid={ModEnvironment.ContinuousHealingGuid} " +
                    $"version={ModEnvironment.ContinuousHealingVersion}; chained MedEffect compatibility enabled.");
            }

            Logger.LogInfo(
                $"QuickMeds: startup complete. movement={_movementPatchApplied} " +
                $"session={_healSessionPatchApplied} capture={_effectCapturePatchApplied} started={_effectStartedPatchApplied} " +
                $"retarget={_movementRetargetPatchApplied} teardown={_teardownPatchesApplied} " +
                $"adaptiveAvailable={AdaptiveHealSpeedAvailable}");
        }

        private void BindSettings()
        {
            _sprintWhileUsingMeds = Config.Bind(SectionMovement, "Sprint while using meds", true,
                new ConfigDescription(
                    "Run while using medicines covered by the normal med movement rule.\n\n" +
                    "Splints and surgery kits have their own settings below.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _walkWhileUsingSurgery = Config.Bind(SectionMovement, "Walk while using splints or surgery kits", true,
                new ConfigDescription(
                    "Move while splinting or doing surgery, instead of standing still the whole time.",
                    null, new ConfigurationManagerAttributes { Order = 99 }));

            _sprintWhileUsingSurgery = Config.Bind(SectionMovement,
                // Keep the old config key so existing users do not lose their saved value. F12
                // displays the cleaner name through DispName.
                "Sprint while using splints or surgery kits (needs walk on)", true,
                new ConfigDescription(
                    "Run while splinting or doing surgery. This option is available only while " +
                    "walking with splints/surgery is enabled.",
                    null, new ConfigurationManagerAttributes
                    {
                        Order = 98,
                        DispName = "Sprint while using splints or surgery kits",
                        HideDefaultButton = true,
                        CustomDrawer = DrawSurgerySprintToggle
                    }));

            // Sprint logically depends on walk. Keep the persisted value honest as well as the UI:
            // if walking is disabled, sprint is forced OFF and the F12 control is grey/unclickable.
            NormalizeSurgerySprintDependency();
            _walkWhileUsingSurgery.SettingChanged += (_, __) => NormalizeSurgerySprintDependency();
            _sprintWhileUsingSurgery.SettingChanged += (_, __) => NormalizeSurgerySprintDependency();

            _autoCancelWhenDone = Config.Bind(SectionHealing, "Heal auto cancel", true,
                new ConfigDescription(
                    "Stop healing the moment the body part is full, instead of finishing the animation for nothing. " +
                    "Also stops when the item runs out.\n\n" +
                    "Keeps going if the part is still bleeding. Broken bones always take the full time.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _adaptiveHealSpeed = Config.Bind(SectionHealSpeed, "Adaptive healing speed", true,
                new ConfigDescription(
                    "Change medicine timing and its animation together based on movement.\n\n" +
                    "Turn off for normal medicine times. Use the sliders below to set the speeds.\n\n" +
                    "The per-medicine section lets you disable QuickMeds completely for individual medicine items. " +
                    "Food and drink are never affected.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _stillHealSpeed = Config.Bind(SectionHealSpeed, "Speed - standing still", 1.3f,
                new ConfigDescription(
                    "Medicine speed while not moving. 1.00 is normal, 2.00 is twice as fast, 0.25 is four times slower.",
                    new AcceptableValueRange<float>(0.25f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 99 }));

            _walkHealSpeed = Config.Bind(SectionHealSpeed, "Speed - walking", 1.0f,
                new ConfigDescription(
                    "Medicine speed while walking. 1.00 is normal speed.",
                    new AcceptableValueRange<float>(0.25f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 98 }));

            _sprintHealSpeed = Config.Bind(SectionHealSpeed, "Speed - sprinting", 0.8f,
                new ConfigDescription(
                    "Medicine speed while running. Below 1.00 makes the application take longer.\n\n" +
                    "Only matters if you can sprint while using that medicine.",
                    new AcceptableValueRange<float>(0.25f, 2.5f),
                    new ConfigurationManagerAttributes { Order = 97 }));

            // The config key intentionally changed in v1.1.7 because this setting no longer means
            // "every Drugs item". That also gives the corrected category its requested clean OFF
            // default even if an older test config saved the previous All pills switch as ON.
            _allPillsEnabled = Config.Bind(SectionMedicines, "Pills category enabled", false,
                new ConfigDescription(
                    "Master QuickMeds switch for medicines that actually use pill/tablet/capsule-style application. " +
                    "OFF leaves all pills fully vanilla. Balms and injectors are not included here.",
                    null, new ConfigurationManagerAttributes { Order = 100000, DispName = "All pills" }));

            _allInjectorsEnabled = Config.Bind(SectionMedicines, "All injectors", false,
                new ConfigDescription(
                    "Master QuickMeds switch for every injector/stimulator, including compatible modded injectors. " +
                    "OFF leaves all injectors fully vanilla. Default is OFF as the conservative compatibility choice.",
                    null, new ConfigurationManagerAttributes { Order = 99999 }));

            // Bind Debug after the medicine section so ConfigurationManager always renders Debug
            // as the last QuickMeds section. Use a fresh persisted key in v1.1.9 so old test configs
            // that had verbose logging enabled do not carry that state forward: debug is OFF by default.
            _logHealSpeed = Config.Bind(SectionDebug, "Debug logging v1.1.9", false,
                new ConfigDescription(
                    "Detailed QuickMeds diagnostics for bug reports: ownership, medicine classification, item/effect timing, " +
                    "movement retargets, animator writes/readback, compatibility and teardown reasons.",
                    null, new ConfigurationManagerAttributes { Order = 100, DispName = "Debug logging" }));

            // F12 changes are live. If a speed/adaptive setting changes during a medicine use,
            // recalculate the current local session instead of carrying the old multiplier.
            _adaptiveHealSpeed.SettingChanged += (_, __) =>
            {
                if (_adaptiveHealSpeedRuntimeDisabled)
                {
                    if (_adaptiveHealSpeed.Value)
                        _adaptiveHealSpeed.Value = false;
                    return;
                }

                HealSpeed.RefreshFromSettings("adaptive-setting");
            };
            _stillHealSpeed.SettingChanged += (_, __) => HealSpeed.RefreshFromSettings("still-setting");
            _walkHealSpeed.SettingChanged += (_, __) => HealSpeed.RefreshFromSettings("walk-setting");
            _sprintHealSpeed.SettingChanged += (_, __) => HealSpeed.RefreshFromSettings("sprint-setting");
            _allPillsEnabled.SettingChanged += (_, __) =>
                HealSpeed.RefreshFromSettingsIfCurrentItem(
                    "pills-master-toggle",
                    item => item != null && ClassifyMedicineTemplate(item.Template) == MedicineGroup.Pills);
            _allInjectorsEnabled.SettingChanged += (_, __) =>
                HealSpeed.RefreshFromSettingsIfCurrentItem(
                    "injectors-master-toggle",
                    item => item != null && ClassifyMedicineTemplate(item.Template) == MedicineGroup.Injectors);
        }

        private IEnumerator BindMedicineSettingsWhenReady()
        {
            // ItemFactory is not available when BepInEx loads QuickMeds. Wait on the main thread
            // without touching player/raid state. This also makes the list include client-side
            // custom medicine templates loaded into the real ItemFactory.
            while (!Singleton<ItemFactory>.Instantiated)
                yield return new WaitForSecondsRealtime(0.5f);

            // Instantiated can become true before templates are fully populated.
            while (Singleton<ItemFactory>.Instance?.ItemTemplates == null ||
                   Singleton<ItemFactory>.Instance.ItemTemplates.Count == 0)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }

            try
            {
                BindMedicineTemplateSettings();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    "QuickMeds: could not build per-medicine F12 settings. Runtime type fallbacks remain active.\n" + ex);
            }
        }

        private void BindMedicineTemplateSettings()
        {
            if (_medicineCatalogBound)
                return;

            var templates = new List<MedicineTemplateInfo>();
            int pillTemplateCount = 0;
            int injectorTemplateCount = 0;

            foreach (var template in Singleton<ItemFactory>.Instance.ItemTemplates.Values)
            {
                // ItemFactory also exposes category/base nodes such as "Medical" and "MedKit".
                // They are not usable medicine items and should never become F12 rows.
                if (template == null || template._type != NodeType.Item)
                    continue;

                string kind;

                // Do not treat every DrugsTemplate as a pill. Morphine is a vanilla injector
                // stored under Drugs, and balm-type drugs are neither pills nor injectors.
                MedicineGroup group = ClassifyMedicineTemplate(template);
                if (group == MedicineGroup.Injectors)
                {
                    injectorTemplateCount++;
                    LogMedicineClassification(template, group);
                    continue;
                }
                if (group == MedicineGroup.Pills)
                {
                    pillTemplateCount++;
                    LogMedicineClassification(template, group);
                    continue;
                }

                if (template is DrugsTemplate)
                {
                    // Balm/ointment or an unknown custom Drugs template: keep it individually
                    // selectable rather than guessing that it is a pill.
                    kind = "Drug";
                    LogMedicineClassification(template, MedicineGroup.Individual);
                }
                else if (template is MedKitTemplate)
                {
                    kind = "MedKit";
                }
                else if (template is MedicalTemplate)
                {
                    kind = "Medical";
                }
                else if (template is MedsTemplate)
                {
                    // Custom medicine can use the generic Meds template directly.
                    kind = "Medicine";
                }
                else
                {
                    continue;
                }

                string id = template.StringId;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string name = GetMedicineDisplayName(template, id);
                templates.Add(new MedicineTemplateInfo(id, name, kind));
            }

            templates.Sort((a, b) =>
            {
                int kindCompare = string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase);
                return kindCompare != 0
                    ? kindCompare
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            int order = 90000;
            foreach (var info in templates)
            {
                if (MedicineEnabledSettings.ContainsKey(info.Id))
                    continue;

                // Keep the persisted config key stable and machine-safe by using the template ID,
                // while ConfigurationManager shows the readable medicine name in F12.
                string key = "Medicine " + info.Id;
                string displayName = $"{info.Kind} - {SanitizeConfigName(info.Name)}";
                var entry = Config.Bind(
                    SectionMedicines,
                    key,
                    true,
                    new ConfigDescription(
                        $"Master QuickMeds switch for this exact {info.Kind.ToLowerInvariant()} template ({info.Id}). " +
                        "OFF leaves this item fully vanilla: no QuickMeds movement override, adaptive timing/animation, or auto-cancel. " +
                        "ON uses the global Movement/Healing speed settings; auto-cancel still only applies where appropriate.",
                        null,
                        new ConfigurationManagerAttributes { Order = order--, DispName = displayName }));

                string capturedId = info.Id;
                entry.SettingChanged += (_, __) =>
                    HealSpeed.RefreshFromSettingsIfCurrentItem(
                        "medicine-master-toggle:" + capturedId,
                        item => string.Equals(item?.TemplateId.ToString(), capturedId, StringComparison.OrdinalIgnoreCase));

                MedicineEnabledSettings.Add(info.Id, entry);
            }

            _medicineCatalogBound = true;

            Logger.LogInfo(
                $"QuickMeds: medicine F12 list ready. individualTemplates={MedicineEnabledSettings.Count}, " +
                $"pillsGrouped={pillTemplateCount}, injectorsGrouped={injectorTemplateCount}. " +
                "Reopen F12 if it was already open while the list was created.");
        }

        private static void NormalizeSurgerySprintDependency()
        {
            if (_walkWhileUsingSurgery == null || _sprintWhileUsingSurgery == null)
                return;

            if (!_walkWhileUsingSurgery.Value && _sprintWhileUsingSurgery.Value)
                _sprintWhileUsingSurgery.Value = false;
        }

        private static void DrawSurgerySprintToggle(ConfigEntryBase entry)
        {
            bool dependencyEnabled = _walkWhileUsingSurgery?.Value ?? true;
            bool previousGuiEnabled = GUI.enabled;
            GUI.enabled = previousGuiEnabled && dependencyEnabled;

            bool current = entry?.BoxedValue is bool value && value;
            bool updated = GUILayout.Toggle(current, current ? "Enabled" : "Disabled", GUILayout.ExpandWidth(true));

            if (dependencyEnabled && updated != current && entry != null)
                entry.BoxedValue = updated;

            GUI.enabled = previousGuiEnabled;
        }

        private enum MedicineGroup
        {
            Individual,
            Pills,
            Injectors
        }

        private static MedicineGroup ClassifyMedicineTemplate(ItemTemplate template)
        {
            if (template == null)
                return MedicineGroup.Individual;

            string templateId = template.StringId;
            if (!string.IsNullOrEmpty(templateId) &&
                MedicineGroupCache.TryGetValue(templateId, out var cached))
            {
                return cached;
            }

            MedicineGroup group = ClassifyMedicineTemplateCore(template);
            if (!string.IsNullOrEmpty(templateId))
                MedicineGroupCache[templateId] = group;

            return group;
        }

        private static MedicineGroup ClassifyMedicineTemplateCore(ItemTemplate template)
        {
            if (template is StimulatorTemplate)
                return MedicineGroup.Injectors;

            if (template is not DrugsTemplate)
                return MedicineGroup.Individual;

            // Morphine is a vanilla legacy injector stored under Drugs instead of Stimulator.
            // Keep its exact ID as a stable fallback, then use use-form signals for compatible
            // custom Drugs templates. Unknown Drugs fail safely to an individual F12 switch.
            if (string.Equals(template.StringId, MorphineTemplateId, StringComparison.OrdinalIgnoreCase))
                return MedicineGroup.Injectors;

            string signature = BuildMedicineUseSignature(template);
            if (ContainsAny(signature, InjectorUseTokens))
                return MedicineGroup.Injectors;

            if (VanillaPillTemplateIds.Contains(template.StringId) || ContainsAny(signature, PillUseTokens))
                return MedicineGroup.Pills;

            return MedicineGroup.Individual;
        }

        private static string BuildMedicineUseSignature(ItemTemplate template)
        {
            if (template == null)
                return string.Empty;

            // Some valid EFT/custom medicine templates intentionally omit Prefab or UsePrefab.
            // Never let a cosmetic grouping heuristic abort the whole per-medicine F12 catalog.
            return string.Concat(
                template.Prefab?.path ?? string.Empty, "|",
                template.UsePrefab?.path ?? string.Empty, "|",
                template.ItemSound ?? string.Empty, "|",
                template._name ?? string.Empty);
        }

        private static bool ContainsAny(string value, string[] tokens)
        {
            if (string.IsNullOrEmpty(value) || tokens == null)
                return false;

            foreach (string token in tokens)
            {
                if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static void LogMedicineClassification(ItemTemplate template, MedicineGroup group)
        {
            if (!LogHealSpeed || template == null)
                return;

            LogSource?.LogInfo(
                $"QuickMedsDBG: MEDCLASS id={template.StringId} name={template._name ?? "?"} group={group} " +
                $"sound={template.ItemSound ?? "-"} usePrefab={template.UsePrefab?.path ?? "-"}");
        }

        private static string GetMedicineDisplayName(ItemTemplate template, string fallbackId)
        {
            try
            {
                string localized = template?.NameLocalizationKey.Localized(null);
                if (!string.IsNullOrWhiteSpace(localized) &&
                    !string.Equals(localized, template.NameLocalizationKey, StringComparison.OrdinalIgnoreCase))
                {
                    return localized;
                }
            }
            catch
            {
                // Localization is cosmetic. Fall back to the internal name if locale services are unavailable.
            }

            if (!string.IsNullOrWhiteSpace(template?._name))
                return template._name;

            return string.IsNullOrWhiteSpace(fallbackId) ? "Medicine" : fallbackId;
        }

        private static string SanitizeConfigName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Medicine";

            return value
                .Replace('=', '-')
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();
        }

        private void EnablePatches()
        {
            // Always install the movement hook when MMGA is absent. The prefix is cheap and reads
            // settings live, so enabling a movement option in F12 no longer requires a restart.
            if (ModEnvironment.IsMmgaInstalled)
            {
                Logger.LogInfo(
                    $"QuickMeds: Make Meds Great Again detected ({ModEnvironment.MmgaGuid}). " +
                    "Leaving movement features to it; healing features remain independent.");
            }
            else
            {
                _movementPatchApplied = TryEnable("movement", new PhysicalConditionPatch());
            }

            bool finishPatch = TryEnable("healing finished", new HealingFinishedPatch());
            bool resetPatch = TryEnable("healing reset", new HealingResetPatch());
            bool removePatch = TryEnable("healing removed", new HealingRemovedPatch());
            _teardownPatchesApplied = finishPatch && resetPatch && removePatch;

            _healSessionPatchApplied = TryEnable("heal session tracking", new HealSpeedPatch());
            _effectCapturePatchApplied = TryEnable("heal effect capture", new DoMedEffectPatch());
            _effectStartedPatchApplied = TryEnable("heal effect started timing/animation", new MedEffectStartedPatch());

            // Diagnostic-only observer. QuickMeds does not multiply EFT's own use-time parameter.
            TryEnable("heal animation diagnostics", new UseTimeMultiplierPatch());
            _movementRetargetPatchApplied = TryEnable("heal speed follows movement", new MovementStateChangedPatch());

            if (!AdaptiveHealSpeedAvailable)
            {
                DisableAdaptiveHealSpeed();
                Logger.LogError(
                    "QuickMeds: adaptive heal speed safety lock engaged because a critical hook failed. " +
                    "The F12 adaptive toggle has been forced OFF; healing speed will remain vanilla 1.0x. " +
                    "Movement and auto-cancel can continue where available.");
            }
            else
            {
                _adaptiveHealSpeedRuntimeDisabled = false;
            }
        }

        private bool TryEnable(string description, SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
                Logger.LogInfo($"QuickMeds: {description} enabled.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    $"QuickMeds: could not enable {description}; only that dependent feature is disabled.\n{ex}");
                return false;
            }
        }

        private sealed class MedicineTemplateInfo
        {
            public MedicineTemplateInfo(string id, string name, string kind)
            {
                Id = id;
                Name = name;
                Kind = kind;
            }

            public string Id { get; }
            public string Name { get; }
            public string Kind { get; }
        }
    }
}
