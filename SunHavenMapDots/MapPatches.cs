using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Wish;

namespace SunHavenMapDots
{
    internal static class MapDotState
    {
        internal static MethodInfo   MGetPlayerPos;
        internal static MethodInfo   MSetImagePos;

        // NetworkGamePlayer fields
        internal static FieldInfo    FNGPPlayer;
        internal static FieldInfo    FNGPPlayerName;
        internal static FieldInfo    FNGPFullyInit;
        internal static FieldInfo    FNGPScene;       // Int16 scene ID

        // Scene lookup
        internal static PropertyInfo PSceneSettingsMgrInst; // SceneSettingsManager.Instance
        internal static FieldInfo    FSceneDict;             // .sceneDictionary Dictionary<int,SceneSettings>
        internal static FieldInfo    FSceneSettingsName;     // SceneSettings.sceneName

        // Local player
        internal static FieldInfo    FPlayerInstance;    // Player.Instance static field
        internal static PropertyInfo PActiveSceneName;   // ScenePortalManager.ActiveSceneName static

        // connection id -1 = local player, 0+ = remote
        internal static readonly Dictionary<int, PlayerDot> Dots = new Dictionary<int, PlayerDot>();
        internal static Transform NpcImages;

        internal static void EnsureReflection()
        {
            if (MGetPlayerPos != null) return;

            const BindingFlags F  = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags SF = BindingFlags.Static   | BindingFlags.NonPublic | BindingFlags.Public;

            var mapType = typeof(Map);
            MGetPlayerPos = mapType.GetMethod("GetPlayerPosition",
                               F, null, new[] { typeof(Image), typeof(Vector3), typeof(string) }, null)
                         ?? throw new MissingMethodException(nameof(Map), "GetPlayerPosition");
            MSetImagePos = mapType.GetMethod("SetImagePosition",
                               F, null, new[] { typeof(Image), typeof(Vector2), typeof(bool) }, null)
                         ?? throw new MissingMethodException(nameof(Map), "SetImagePosition");

            var ngpType = typeof(NetworkGamePlayer);
            FNGPPlayer     = ngpType.GetField("player",             F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "player");
            FNGPPlayerName = ngpType.GetField("playerName",         F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "playerName");
            FNGPFullyInit  = ngpType.GetField("isFullyInitialized", F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "isFullyInitialized");
            FNGPScene      = ngpType.GetField("scene",              F); // Int16 — optional

            // SceneSettingsManager.Instance + .sceneDictionary
            var ssmType = Type.GetType("Wish.SceneSettingsManager, SunHaven.Core")
                       ?? Type.GetType("Wish.SceneSettingsManager, Assembly-CSharp");
            if (ssmType != null)
            {
                PSceneSettingsMgrInst = ssmType.GetProperty("Instance", SF);
                FSceneDict            = ssmType.GetField("sceneDictionary", F);
            }

            // SceneSettings.sceneName
            var ssType = Type.GetType("Wish.SceneSettings, SunHaven.Core")
                      ?? Type.GetType("Wish.SceneSettings, Assembly-CSharp");
            if (ssType != null)
                FSceneSettingsName = ssType.GetField("sceneName", F);

            // Player.Instance (static)
            FPlayerInstance = typeof(Player).GetField("Instance", SF);

            // ScenePortalManager.ActiveSceneName (static property)
            var spmType = Type.GetType("Wish.ScenePortalManager, SunHaven.Core")
                       ?? Type.GetType("Wish.ScenePortalManager, Assembly-CSharp");
            if (spmType != null)
                PActiveSceneName = spmType.GetProperty("ActiveSceneName", SF);

            Plugin.Log.LogInfo($"[MapDots] Reflection ready. PlayerInstance={FPlayerInstance != null} ActiveSceneName={PActiveSceneName != null} FNGPScene={FNGPScene != null} SceneDict={FSceneDict != null}");
        }

        internal static Transform FindNpcImages()
        {
            var go = GameObject.Find("Player(Clone)");
            if (go == null) return null;

            var mapImageTr = go.transform.Find(
                "UI_Inventory/Inventory/Map/Background/Scroll View/Viewport/Content/MapImage");
            if (mapImageTr == null) return null;

            return FindChildByName(mapImageTr, "SunHavenNPCImages");
        }

        static Transform FindChildByName(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        internal static string GetRemotePlayerScene(NetworkGamePlayer ngp)
        {
            if (FNGPScene == null || PSceneSettingsMgrInst == null ||
                FSceneDict == null || FSceneSettingsName == null)
                return GetActiveSceneName(); // fallback

            var sceneId = Convert.ToInt32(FNGPScene.GetValue(ngp));
            var mgrInst = PSceneSettingsMgrInst.GetValue(null);
            if (mgrInst == null) return GetActiveSceneName();

            var dict = FSceneDict.GetValue(mgrInst) as IDictionary;
            if (dict == null) return GetActiveSceneName();

            var settings = dict[sceneId];
            if (settings == null) return GetActiveSceneName();

            return FSceneSettingsName.GetValue(settings) as string ?? GetActiveSceneName();
        }

        internal static string GetActiveSceneName()
        {
            if (PActiveSceneName == null) return string.Empty;
            return PActiveSceneName.GetValue(null) as string ?? string.Empty;
        }

        internal static void DestroyAllDots()
        {
            foreach (var dot in Dots.Values)
                if (dot.Root != null) UnityEngine.Object.Destroy(dot.Root);
            Dots.Clear();
            NpcImages = null;
        }
    }

    internal sealed class PlayerDot
    {
        internal readonly GameObject      Root;
        internal readonly Image           Img;
        internal readonly TextMeshProUGUI Label;

        internal PlayerDot(GameObject root, Image img, TextMeshProUGUI label)
        {
            Root  = root;
            Img   = img;
            Label = label;
        }
    }

    [HarmonyPatch(typeof(Map), "UpdatePlayerImagePosition")]
    internal static class Patch_UpdatePlayerImagePosition
    {
        private const float Y_OFFSET = 24f;

        [HarmonyPostfix]
        static void Postfix(Map __instance, bool immediate)
        {
            try
            {
                MapDotState.EnsureReflection();
                if (Plugin.ShowLocalPlayer.Value)
                    HandleLocalPlayer(__instance, immediate);
                if (Plugin.ShowRemotePlayers.Value)
                    HandleRemotePlayers(__instance, immediate);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MapDots] Exception in UpdatePlayerImagePosition: {ex}");
            }
        }

        static Transform GetNpcImages()
        {
            if (MapDotState.NpcImages != null && MapDotState.NpcImages.gameObject != null)
                return MapDotState.NpcImages;
            MapDotState.NpcImages = MapDotState.FindNpcImages();
            return MapDotState.NpcImages;
        }

        static void HandleLocalPlayer(Map map, bool immediate)
        {
            if (MapDotState.FPlayerInstance == null) return;

            var player = MapDotState.FPlayerInstance.GetValue(null) as Player;
            if (player == null) return;

            var npcImages = GetNpcImages();
            if (npcImages == null) return;

            var sceneName = MapDotState.GetActiveSceneName();
            var worldPos  = player.transform.position;

            PositionDot(map, immediate, -1, worldPos, sceneName, 0, npcImages, "");
        }

        static void HandleRemotePlayers(Map map, bool immediate)
        {
            var lobby = NetworkLobbyManager.Instance;
            if (lobby == null) return;

            var npcImages = GetNpcImages();
            if (npcImages == null) return;

            var activeCids = new HashSet<int>();
            int colorIndex = 1;

            foreach (var kvp in lobby.GamePlayers)
            {
                var cid = kvp.Key;
                var ngp = kvp.Value;

                if (ngp == null || ngp.gameObject == null)   continue;
                if (ngp.isLocalPlayer)                       continue;
                if (!(bool)MapDotState.FNGPFullyInit.GetValue(ngp)) continue;

                var remotePlayer = MapDotState.FNGPPlayer.GetValue(ngp) as Player;
                if (remotePlayer == null) continue;

                var sceneName = MapDotState.GetRemotePlayerScene(ngp);
                var pName     = MapDotState.FNGPPlayerName.GetValue(ngp) as string ?? "Player";
                var worldPos  = remotePlayer.transform.position;

                activeCids.Add(cid);
                PositionDot(map, immediate, cid, worldPos, sceneName, colorIndex, npcImages, pName);
                colorIndex++;
            }

            var toRemove = new List<int>();
            foreach (var kvp in MapDotState.Dots)
            {
                if (kvp.Key == -1) continue;
                if (activeCids.Contains(kvp.Key)) continue;
                if (kvp.Value.Root == null) toRemove.Add(kvp.Key);
                else kvp.Value.Root.SetActive(false);
            }
            foreach (var k in toRemove) MapDotState.Dots.Remove(k);
        }

        static void PositionDot(Map map, bool immediate, int id, Vector3 worldPos,
                                string scene, int colorIndex, Transform parent, string playerName)
        {
            if (!MapDotState.Dots.TryGetValue(id, out var dot) || dot.Root == null)
            {
                var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                dot = CreateDot(parent, playerName, color);
                MapDotState.Dots[id] = dot;
                Plugin.Log.LogInfo($"[MapDots] Created dot id={id} name='{playerName}' scene='{scene}' world={worldPos}");
            }

            try
            {
                var mapPos = (Vector2)MapDotState.MGetPlayerPos.Invoke(
                    map, new object[] { dot.Img, worldPos, scene });
                mapPos.y += Y_OFFSET;
                MapDotState.MSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, true });
                dot.Root.SetActive(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[MapDots] Position error id={id}: {ex.Message}");
            }
        }

        static PlayerDot CreateDot(Transform parent, string playerName, Color color)
        {
            float size = Plugin.DotSize.Value * 2f;

            var root = new GameObject(
                string.IsNullOrEmpty(playerName) ? "MapDot_Local" : $"MapDot_{playerName}",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(size, size);

            var img = root.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;

            TextMeshProUGUI label = null;
            if (Plugin.ShowPlayerNames.Value && !string.IsNullOrEmpty(playerName))
            {
                var labelGo = new GameObject("Label",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(root.transform, false);
                var labelRt = labelGo.GetComponent<RectTransform>();
                labelRt.anchorMin = labelRt.anchorMax = labelRt.pivot = new Vector2(0.5f, 0f);
                labelRt.anchoredPosition = new Vector2(0f, size * 0.6f + 2f);
                labelRt.sizeDelta = new Vector2(120f, 18f);
                label = labelGo.GetComponent<TextMeshProUGUI>();
                label.text = playerName;
                label.fontSize = 9f;
                label.color = color;
                label.alignment = TextAlignmentOptions.Center;
                label.raycastTarget = false;
            }

            return new PlayerDot(root, img, label);
        }
    }

    [HarmonyPatch(typeof(Map), "OnEnable")]
    internal static class Patch_MapOnEnable
    {
        [HarmonyPostfix]
        static void Postfix() => MapDotState.DestroyAllDots();
    }

    [HarmonyPatch(typeof(Map), "OnDisable")]
    internal static class Patch_MapOnDisable
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            foreach (var dot in MapDotState.Dots.Values)
                if (dot.Root != null) dot.Root.SetActive(false);
        }
    }
}
