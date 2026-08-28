using Comfort.Common;
using EFT;
using EFT.Hideout;
using System;
using System.Linq;

namespace QuickMeds.Patches
{
    /// <summary>
    /// Shared guards. Every patch in this mod starts with one of these.
    ///
    /// Two rules drive the whole design:
    ///
    ///  1. Only ever act on the local player. In a Fika raid other players are represented by
    ///     separate observed-player classes whose healing is replicated from THEIR client. Acting
    ///     on anyone else would fight that replication and desync their animations.
    ///
    ///  2. Do nothing on a headless client. It has no player to heal.
    /// </summary>
    internal static class ModEnvironment
    {
        /// <summary>True on a Fika headless client - a server-side client with no player or UI.</summary>
        public static bool IsHeadlessClient
        {
            get
            {
                if (_isHeadless.HasValue) return _isHeadless.Value;

                // Detected by looking for Fika's headless plugin rather than referencing Fika
                // directly, so this builds and runs fine without Fika installed.
                _isHeadless = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys
                    .Any(guid => guid.IndexOf("headless", StringComparison.OrdinalIgnoreCase) >= 0);

                return _isHeadless.Value;
            }
        }

        private static bool? _isHeadless;

        /// <summary>
        /// True when Make Meds Great Again is installed.
        ///
        /// MMGA provides the same two movement features by prefixing the same method, so running
        /// both would mean two prefixes competing over one call. The outcome would be the same
        /// either way - both refuse the condition - but there is no reason to stack them, so
        /// QuickMeds stands down and lets MMGA own that part. The healing features are unrelated
        /// and keep working.
        /// </summary>
        public static bool IsMmgaInstalled => MmgaGuid != null;

        /// <summary>
        /// The GUID Make Meds Great Again was found under, or null.
        ///
        /// Matched on the name rather than an exact GUID. The published 4.0 source uses
        /// "com.vinihns.makeMedsGreatAgain", but a later build could change it and there is no way
        /// to verify a GUID that does not exist yet - so anything containing "makemedsgreatagain"
        /// counts. The matched value is returned so it can be logged, which makes a wrong match
        /// visible instead of silent.
        /// </summary>
        public static string MmgaGuid
        {
            get
            {
                if (_mmgaChecked) return _mmgaGuid;
                _mmgaChecked = true;

                _mmgaGuid = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys
                    .FirstOrDefault(guid =>
                        guid.Replace(".", string.Empty)
                            .IndexOf("makemedsgreatagain", StringComparison.OrdinalIgnoreCase) >= 0);

                return _mmgaGuid;
            }
        }

        private static string _mmgaGuid;
        private static bool _mmgaChecked;

        /// <summary>True when Healing Auto Cancel is installed.</summary>
        public static bool IsAutoCancelModInstalled => AutoCancelGuid != null;

        /// <summary>
        /// The GUID Healing Auto Cancel was found under, or null.
        ///
        /// Matched on the name for the same reason as MMGA above: a GUID can change between
        /// versions, and there is no 4.1 build of it yet to check against. If the match ever fails,
        /// the worst case is that both mods end a heal at the same moment - the second call finds
        /// the effect already gone and does nothing.
        /// </summary>
        public static string AutoCancelGuid
        {
            get
            {
                if (_autoCancelChecked) return _autoCancelGuid;
                _autoCancelChecked = true;

                _autoCancelGuid = BepInEx.Bootstrap.Chainloader.PluginInfos.Keys
                    .FirstOrDefault(guid =>
                        guid.Replace(".", string.Empty)
                            .IndexOf("healingautocancel", StringComparison.OrdinalIgnoreCase) >= 0);

                return _autoCancelGuid;
            }
        }

        private static string _autoCancelGuid;
        private static bool _autoCancelChecked;

        /// <summary>True when the player is in a raid rather than the hideout.</summary>
        public static bool IsInRaid =>
            Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance is not HideoutGameWorld;

        /// <summary>
        /// True only for the local, living player. This is THE guard for multiplayer safety, and
        /// every gameplay patch calls it before touching anything.
        /// </summary>
        public static bool IsLocalLivingPlayer(Player player)
        {
            if (player == null) return false;
            if (!player.IsYourPlayer) return false;

            var health = player.ActiveHealthController;
            return health != null && health.IsAlive;
        }
    }
}
