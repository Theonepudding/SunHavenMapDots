using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SunHavenMapDots
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        public static ConfigEntry<float> DotSize;
        public static ConfigEntry<bool> ShowLocalPlayer;
        public static ConfigEntry<bool> ShowRemotePlayers;
        public static ConfigEntry<bool> ShowPlayerNames;

        // Player colors: index 0 = local, 1..5 = remote (cycles if more than 5 remotes)
        public static readonly UnityEngine.Color[] PlayerColors =
        {
            new UnityEngine.Color(1.0f, 0.9f, 0.0f, 1f), // Yellow  – local
            new UnityEngine.Color(0.0f, 0.7f, 1.0f, 1f), // Cyan
            new UnityEngine.Color(1.0f, 0.4f, 0.1f, 1f), // Orange
            new UnityEngine.Color(0.4f, 1.0f, 0.2f, 1f), // Lime
            new UnityEngine.Color(0.8f, 0.1f, 1.0f, 1f), // Purple
            new UnityEngine.Color(1.0f, 0.2f, 0.5f, 1f), // Pink
        };

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            DotSize = Config.Bind("Display", "DotSize", 6f,
                new ConfigDescription("Diameter (map units) of each player dot.", new AcceptableValueRange<float>(2f, 20f)));

            ShowLocalPlayer = Config.Bind("Display", "ShowLocalPlayer", true,
                "Show the local player's dot (yellow) on the map.");

            ShowRemotePlayers = Config.Bind("Display", "ShowRemotePlayers", true,
                "Show colour-coded dots for other players when in multiplayer.");

            ShowPlayerNames = Config.Bind("Display", "ShowPlayerNames", true,
                "Show a small name label above each remote-player dot.");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Patch_UpdatePlayerImagePosition));
            harmony.PatchAll(typeof(Patch_MapOnEnable));
            harmony.PatchAll(typeof(Patch_MapOnDisable));
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded.");
        }
    }
}
