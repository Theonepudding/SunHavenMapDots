using HarmonyLib;
using Mirror;
using System;
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
        internal static FieldInfo    FNGPPlayer;
        internal static FieldInfo    FNGPPlayerName;
        internal static FieldInfo    FNGPFullyInit;
        internal static PropertyInfo PNGPSameScene;
        internal static FieldInfo    FPlayerScene;
        internal static PropertyInfo PPlayerExact;

        // connection id -1 = local player, 0+ = remote players
        internal static readonly Dictionary<int, PlayerDot> Dots = new Dictionary<int, PlayerDot>();
        internal static Transform NpcImages;
        internal static GameObject LocalPlayerGo;

        internal static void EnsureReflection()
        {
            if (MGetPlayerPos != null) return;

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

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
            PNGPSameScene  = ngpType.GetProperty("SameScene", F);

            var playerType = typeof(Player);
            FPlayerScene = playerType.GetField("currentScene", F) ?? throw new MissingFieldException(nameof(Player), "currentScene");
            PPlayerExact = playerType.GetProperty("ExactPosition", F)
                      ?? playerType.GetProperty("ExactPosition", BindingFlags.Instance | BindingFlags.Public)
                      ?? throw new MissingMemberException(nameof(Player), "ExactPosition");

            Plugin.Log.LogInfo("[MapDots] Reflection cache ready.");
        }

        internal static Transform FindNpcImages()
        {
            var go = GameObject.Find("Player(Clone)");
            if (go == null) return null;
            LocalPlayerGo = go;

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
                    HandlePlayer(__instance, immediate, local: true);
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

        // Positions our own dot for the local player using their world-space ExactPosition.
        // We do NOT touch the game's built-in 'Player' marker so we don't fight its sprite/alpha.
        static void HandlePlayer(Map map, bool immediate, bool local)
        {
            var npcImages = GetNpcImages();
            if (npcImages == null) return;

            var localGo = MapDotState.LocalPlayerGo;
            if (localGo == null) return;

            var player = localGo.GetComponent<Player>();
            if (player == null) return;

            var scene = MapDotState.FPlayerScene.GetValue(player) as string ?? string.Empty;
            PositionDot(map, immediate, -1, player, scene, 0, npcImages, "");
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
                if (MapDotState.PNGPSameScene != null &&
                    !(bool)MapDotState.PNGPSameScene.GetValue(ngp)) continue;

                var player = MapDotState.FNGPPlayer.GetValue(ngp) as Player;
                if (player == null) continue;

                var scene = MapDotState.FPlayerScene.GetValue(player) as string ?? string.Empty;
                var pName = MapDotState.FNGPPlayerName.GetValue(ngp) as string ?? "Player";

                activeCids.Add(cid);
                PositionDot(map, immediate, cid, player, scene, colorIndex, npcImages, pName);
                colorIndex++;
            }

            // Hide dots for players who left or changed scene
            var toRemove = new List<int>();
            foreach (var kvp in MapDotState.Dots)
            {
                if (kvp.Key == -1) continue; // local player managed separately
                if (activeCids.Contains(kvp.Key)) continue;
                if (kvp.Value.Root == null) toRemove.Add(kvp.Key);
                else kvp.Value.Root.SetActive(false);
            }
            foreach (var k in toRemove) MapDotState.Dots.Remove(k);
        }

        static void PositionDot(Map map, bool immediate, int id, Player player,
                                string scene, int colorIndex, Transform parent, string playerName)
        {
            if (!MapDotState.Dots.TryGetValue(id, out var dot) || dot.Root == null)
            {
                var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                dot = CreateDot(parent, playerName, color);
                MapDotState.Dots[id] = dot;
                Plugin.Log.LogInfo($"[MapDots] Created dot for id={id} name='{playerName}'");
            }

            var exactPos  = (Vector2)MapDotState.PPlayerExact.GetValue(player);
            var worldPos3 = new Vector3(exactPos.x, exactPos.y, 0f);

            try
            {
                var mapPos = (Vector2)MapDotState.MGetPlayerPos.Invoke(
                    map, new object[] { dot.Img, worldPos3, scene });
                mapPos.y += Y_OFFSET;
                MapDotState.MSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, immediate });
                dot.Root.SetActive(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[MapDots] Position error for id={id}: {ex.Message}");
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
        static void Postfix()
        {
            MapDotState.DestroyAllDots();
        }
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
