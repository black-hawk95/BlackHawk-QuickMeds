using BepInEx.Bootstrap;
using EFT;
using System;
using System.Linq;
using System.Reflection;

namespace QuickMeds.Patches
{
    internal static class ModEnvironment
    {
        private static bool? _isHeadless;

        private static string _mmgaGuid;
        private static bool _mmgaChecked;

        private static string _autoCancelGuid;
        private static string _autoCancelVersion;
        private static bool _autoCancelChecked;

        private static string _continuousHealingGuid;
        private static string _continuousHealingVersion;
        private static bool _continuousHealingChecked;

        /// <summary>
        /// Prefer Fika's real FikaBackendUtils.IsHeadless flag via reflection, so QuickMeds keeps no
        /// compile-time Fika dependency. Falls back to the legacy GUID heuristic only if that API is
        /// unavailable.
        /// </summary>
        public static bool IsHeadlessClient
        {
            get
            {
                if (_isHeadless.HasValue)
                    return _isHeadless.Value;

                try
                {
                    var fikaAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "Fika.Core", StringComparison.OrdinalIgnoreCase));

                    var backendType = fikaAssembly?.GetType("Fika.Core.Main.Utils.FikaBackendUtils", false);
                    var property = backendType?.GetProperty(
                        "IsHeadless",
                        BindingFlags.Public | BindingFlags.Static);

                    if (property?.PropertyType == typeof(bool))
                    {
                        _isHeadless = (bool)property.GetValue(null);
                        return _isHeadless.Value;
                    }
                }
                catch (Exception ex)
                {
                    if (Plugin.LogHealSpeed)
                        Plugin.LogSource?.LogWarning($"QuickMedsDBG: Fika headless reflection failed: {ex.Message}");
                }

                // Compatibility fallback for older/different dedicated-client packaging.
                _isHeadless = Chainloader.PluginInfos.Keys
                    .Any(guid => guid.IndexOf("headless", StringComparison.OrdinalIgnoreCase) >= 0);

                return _isHeadless.Value;
            }
        }

        public static bool IsMmgaInstalled => MmgaGuid != null;

        public static string MmgaGuid
        {
            get
            {
                if (_mmgaChecked) return _mmgaGuid;
                _mmgaChecked = true;

                _mmgaGuid = Chainloader.PluginInfos.Keys
                    .FirstOrDefault(guid =>
                        Normalize(guid).IndexOf("makemedsgreatagain", StringComparison.OrdinalIgnoreCase) >= 0);

                return _mmgaGuid;
            }
        }

        /// <summary>
        /// HealingAutoCancel currently builds with AssemblyName=HealingAutoCancel and BepInEx.PluginInfoProps.
        /// Match GUID, display name, and loaded plugin assembly name so a harmless metadata change does not
        /// cause QuickMeds' internal auto-cancel to compete with it.
        /// </summary>
        public static bool IsAutoCancelModInstalled
        {
            get
            {
                EnsureAutoCancelDetected();
                return _autoCancelGuid != null;
            }
        }

        public static string AutoCancelGuid
        {
            get
            {
                EnsureAutoCancelDetected();
                return _autoCancelGuid;
            }
        }

        public static string AutoCancelVersion
        {
            get
            {
                EnsureAutoCancelDetected();
                return _autoCancelVersion;
            }
        }

        /// <summary>
        /// Lacyway ContinuousHealing uses the stable GUID com.lacyway.ch. We also match its display/assembly
        /// identity for defensive compatibility with repacks. QuickMeds does not patch ContinuousHealing;
        /// this flag is diagnostics plus effect-boundary handling only.
        /// </summary>
        public static bool IsContinuousHealingInstalled
        {
            get
            {
                EnsureContinuousHealingDetected();
                return _continuousHealingGuid != null;
            }
        }

        public static string ContinuousHealingGuid
        {
            get
            {
                EnsureContinuousHealingDetected();
                return _continuousHealingGuid;
            }
        }

        public static string ContinuousHealingVersion
        {
            get
            {
                EnsureContinuousHealingDetected();
                return _continuousHealingVersion;
            }
        }

        private static void EnsureAutoCancelDetected()
        {
            if (_autoCancelChecked)
                return;

            _autoCancelChecked = true;
            DetectPlugin(
                exactGuid: "HealingAutoCancel",
                normalizedTokens: new[] { "healingautocancel", "improvedselfcare" },
                out _autoCancelGuid,
                out _autoCancelVersion);
        }

        private static void EnsureContinuousHealingDetected()
        {
            if (_continuousHealingChecked)
                return;

            _continuousHealingChecked = true;
            DetectPlugin(
                exactGuid: "com.lacyway.ch",
                normalizedTokens: new[] { "continuoushealing" },
                out _continuousHealingGuid,
                out _continuousHealingVersion);
        }

        private static void DetectPlugin(
            string exactGuid,
            string[] normalizedTokens,
            out string matchedGuid,
            out string matchedVersion)
        {
            matchedGuid = null;
            matchedVersion = null;

            string[] normalizedNeedles = normalizedTokens == null
                ? Array.Empty<string>()
                : normalizedTokens
                    .Select(Normalize)
                    .Where(token => !string.IsNullOrEmpty(token))
                    .ToArray();

            foreach (var pair in Chainloader.PluginInfos)
            {
                var info = pair.Value;
                string metadataGuid = info?.Metadata?.GUID;
                string metadataName = info?.Metadata?.Name;
                string assemblyName = null;

                try
                {
                    assemblyName = info?.Instance?.GetType().Assembly.GetName().Name;
                }
                catch
                {
                    // Metadata/key matching below is enough if an unusual loader proxy blocks assembly access.
                }

                bool exact =
                    string.Equals(pair.Key, exactGuid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(metadataGuid, exactGuid, StringComparison.OrdinalIgnoreCase);

                bool tokenMatch = false;
                if (!exact && normalizedNeedles.Length > 0)
                {
                    string[] candidates = { pair.Key, metadataGuid, metadataName, assemblyName };

                    foreach (string candidate in candidates)
                    {
                        string normalized = Normalize(candidate);
                        if (string.IsNullOrEmpty(normalized))
                            continue;

                        if (normalizedNeedles.Any(token =>
                            normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            tokenMatch = true;
                            break;
                        }
                    }
                }

                if (!exact && !tokenMatch)
                    continue;

                matchedGuid = !string.IsNullOrEmpty(metadataGuid) ? metadataGuid : pair.Key;
                matchedVersion = info?.Metadata?.Version?.ToString() ?? "unknown";
                return;
            }
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static bool IsLocalLivingPlayer(Player player)
        {
            if (player == null || !player.IsYourPlayer)
                return false;

            var health = player.ActiveHealthController;
            return health != null && health.IsAlive;
        }
    }
}
