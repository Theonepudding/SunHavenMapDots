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
    // ── Shared state used by all three patch classes ───────────────────────────
    internal static class MapDotState
    {
        // Reflection cache
        internal static FieldInfo  FMapContent;
        internal static FieldInfo  FPlayerImages;
        internal static MethodInfo MGetPlayerPos;
        internal static MethodInfo MSetImagePos;
        internal static FieldInfo  FNGPPlayer;
        internal static FieldInfo  FNGPPlayerName;
        internal static FieldInfo  FNGPFullyInit;
        internal static PropertyInfo PNGPSameScene;
        internal static FieldInfo  FPlayerScene;
        internal static PropertyInfo PPlayerExact;

        // key = connection id, value = dot data
        internal static readonly Dictionary<int, PlayerDot> Dots = new Dictionary<int, PlayerDot>();
        internal static Map CurrentMap;

        internal static void EnsureReflection()
        {
            if (FMapContent != null) return;

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            var mapType    = typeof(Map);
            FMapContent    = mapType.GetField("mapContent",  F) ?? throw new MissingFieldException(nameof(Map), "mapContent");
            FPlayerImages  = mapType.GetField("playerImages", F) ?? throw new MissingFieldException(nameof(Map), "playerImages");
            MGetPlayerPos  = mapType.GetMethod("GetPlayerPosition",
                                F, null, new[] { typeof(Image), typeof(Vector3), typeof(string) }, null)
                             ?? throw new MissingMethodException(nameof(Map), "GetPlayerPosition");
            MSetImagePos   = mapType.GetMethod("SetImagePosition",
                                F, null, new[] { typeof(Image), typeof(Vector2), typeof(bool) }, null)
                             ?? throw new MissingMethodException(nameof(Map), "SetImagePosition");

            var ngpType    = typeof(NetworkGamePlayer);
            FNGPPlayer     = ngpType.GetField("player",             F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "player");
            FNGPPlayerName = ngpType.GetField("playerName",         F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "playerName");
            FNGPFullyInit  = ngpType.GetField("isFullyInitialized", F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "isFullyInitialized");
            PNGPSameScene  = ngpType.GetProperty("SameScene", F); // optional

            var playerType = typeof(Player);
            FPlayerScene   = playerType.GetField("currentScene", F) ?? throw new MissingFieldException(nameof(Player), "currentScene");
            PPlayerExact   = playerType.GetProperty("ExactPosition", F)
                          ?? playerType.GetProperty("ExactPosition", BindingFlags.Instance | BindingFlags.Public)
                          ?? throw new MissingMemberException(nameof(Player), "ExactPosition");

            Plugin.Log.LogInfo("[MapDots] Reflection cache ready.");
        }

        internal static void DestroyAllDots()
        {
            foreach (var dot in Dots.Values)
                if (dot.Root != null) UnityEngine.Object.Destroy(dot.Root);
            Dots.Clear();
        }
    }

    // ── Data for one player dot ────────────────────────────────────────────────
    internal sealed class PlayerDot
    {
        internal readonly GameObject       Root;
        internal readonly Image            Img;
        internal readonly TextMeshProUGUI  Label;

        internal PlayerDot(GameObject root, Image img, TextMeshProUGUI label)
        {
            Root  = root;
            Img   = img;
            Label = label;
        }
    }

    // ── Patch 1: UpdatePlayerImagePosition ────────────────────────────────────
    [HarmonyPatch(typeof(Map), "UpdatePlayerImagePosition")]
    internal static class Patch_UpdatePlayerImagePosition
    {
        [HarmonyPostfix]
        static void Postfix(Map __instance, bool immediate)
        {
            try
            {
                MapDotState.EnsureReflection();
                HandleLocalPlayerFix(__instance, immediate);
                if (Plugin.ShowRemotePlayers.Value)
                    HandleRemotePlayers(__instance, immediate);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MapDots] Exception in UpdatePlayerImagePosition: {ex}");
            }
        }

        static void HandleLocalPlayerFix(Map map, bool immediate)
        {
            if (!Plugin.ShowLocalPlayer.Value) return;

            var images = MapDotState.FPlayerImages.GetValue(map) as List<Image>;
            if (images == null || images.Count == 0) return;

            var localImg = images[0];
            if (localImg == null) return;

            if (!localImg.gameObject.activeSelf)
                localImg.gameObject.SetActive(true);

            localImg.color = Plugin.PlayerColors[0];

            float d  = Plugin.DotSize.Value * 2f;
            var   rt = localImg.rectTransform;
            if (rt != null) rt.sizeDelta = new Vector2(d, d);
        }

        static void HandleRemotePlayers(Map map, bool immediate)
        {
            var lobby = NetworkLobbyManager.Instance;
            if (lobby == null) return;

            var mapContent = MapDotState.FMapContent.GetValue(map) as RectTransform;
            if (mapContent == null) return;

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

                activeCids.Add(cid);

                if (!MapDotState.Dots.TryGetValue(cid, out var dot) || dot.Root == null)
                {
                    var pName = MapDotState.FNGPPlayerName.GetValue(ngp) as string ?? "Player";
                    var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                    dot = CreateDot(mapContent, pName, color);
                    MapDotState.Dots[cid] = dot;
                }

                colorIndex++;

                var exactPos  = (Vector2)MapDotState.PPlayerExact.GetValue(player);
                var sceneName = MapDotState.FPlayerScene.GetValue(player) as string ?? string.Empty;
                var worldPos3 = new Vector3(exactPos.x, exactPos.y, 0f);

                try
                {
                    var mapPos = (Vector2)MapDotState.MGetPlayerPos.Invoke(map, new object[] { dot.Img, worldPos3, sceneName });
                    MapDotState.MSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, immediate });
                    dot.Root.SetActive(true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[MapDots] Position error for cid {cid}: {ex.Message}");
                }
            }

            // Deactivate dots for players who left or changed scene
            var toRemove = new List<int>();
            foreach (var kvp in MapDotState.Dots)
            {
                if (activeCids.Contains(kvp.Key)) continue;
                if (kvp.Value.Root == null) toRemove.Add(kvp.Key);
                else kvp.Value.Root.SetActive(false);
            }
            foreach (var k in toRemove) MapDotState.Dots.Remove(k);
        }

        static PlayerDot CreateDot(RectTransform parent, string playerName, Color color)
        {
            float size = Plugin.DotSize.Value * 2f;

            var root   = new GameObject($"MapDot_{playerName}", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(size, size);

            // Coloured outer circle
            var ringGo = new GameObject("Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            ringGo.transform.SetParent(root.transform, false);
            var ringRt  = ringGo.GetComponent<RectTransform>();
            ringRt.anchorMin = ringRt.anchorMax = ringRt.pivot = new Vector2(0.5f, 0.5f);
            ringRt.sizeDelta = new Vector2(size, size);
            var ringImg = ringGo.GetComponent<Image>();
            ringImg.color = color;
            ringImg.raycastTarget = false;

            // White centre dot
            var coreGo = new GameObject("Core", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            coreGo.transform.SetParent(root.transform, false);
            var coreRt  = coreGo.GetComponent<RectTransform>();
            coreRt.anchorMin = coreRt.anchorMax = coreRt.pivot = new Vector2(0.5f, 0.5f);
            coreRt.sizeDelta = new Vector2(size * 0.45f, size * 0.45f);
            var coreImg = coreGo.GetComponent<Image>();
            coreImg.color = Color.white;
            coreImg.raycastTarget = false;

            // Optional name label
            TextMeshProUGUI label = null;
            if (Plugin.ShowPlayerNames.Value)
            {
                var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
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

            return new PlayerDot(root, ringImg, label);
        }
    }

    // ── Patch 2: Map.OnEnable ─────────────────────────────────────────────────
    [HarmonyPatch(typeof(Map), "OnEnable")]
    internal static class Patch_MapOnEnable
    {
        [HarmonyPostfix]
        static void Postfix(Map __instance)
        {
            if (MapDotState.CurrentMap != __instance)
            {
                MapDotState.DestroyAllDots();
                MapDotState.CurrentMap = __instance;
            }
        }
    }

    // ── Patch 3: Map.OnDisable ────────────────────────────────────────────────
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
