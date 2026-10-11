using BepInEx.Configuration;
using System;

namespace QuickMeds
{
#pragma warning disable 0169, 0414, 0649
    // Read by BepInEx ConfigurationManager through reflection. Keep only the fields QuickMeds uses
    // so this stays a tiny optional UI bridge rather than a hard dependency.
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase> CustomDrawer;
        public int? Order;
        public bool? HideDefaultButton;
        public string DispName;
    }
#pragma warning restore 0169, 0414, 0649
}
