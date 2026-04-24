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

        internal static readonly Dictionary<int, PlayerDot> Dots = new Dictionary<int, PlayerDot>();
        internal static Transform NpcImages;

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
            var playerGo = GameObject.Find("Player(Clone)");
            if (playerGo == null) return null;

            var mapImageTr = playerGo.transform.Find(
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
        internal static bool LoggedOnce = false;

        [HarmonyPostfix]
        static void Postfix(Map __instance, bool immediate)
        {
            try
            {
                MapDotState.EnsureReflection();
                HandleLocalPlayerFix();
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

        static void HandleLocalPlayerFix()
        {
            if (!Plugin.ShowLocalPlayer.Value) return;

            var npcImages = GetNpcImages();
            if (npcImages == null) return;

            var candidates = new List<(Transform t, Image img)>();
            foreach (Transform child in npcImages)
            {
                if (!child.name.StartsWith("Player", StringComparison.OrdinalIgnoreCase)) continue;
                if (child.name.StartsWith("PlayerDot_", StringComparison.OrdinalIgnoreCase)) continue;
                var img = child.GetComponent<Image>();
                if (img == null) continue;
                candidates.Add((child, img));
            }

            if (!LoggedOnce)
            {
                LoggedOnce = true;
                Plugin.Log.LogInfo($"[MapDots] NpcImages='{npcImages.name}' children={npcImages.childCount} markers={candidates.Count}");
                foreach (var c in candidates)
                    Plugin.Log.LogInfo($"[MapDots]   '{c.t.name}' activeH={c.t.gameObject.activeInHierarchy} pos={c.img.rectTransform.anchoredPosition}");
            }

            if (candidates.Count == 0) return;

            // Sort: prefer activeInHierarchy, then activeSelf
            candidates.Sort((a, b) =>
            {
                int dh = b.t.gameObject.activeInHierarchy.CompareTo(a.t.gameObject.activeInHierarchy);
                if (dh != 0) return dh;
                return b.t.gameObject.activeSelf.CompareTo(a.t.gameObject.activeSelf);
            });

            var best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
                candidates[i].t.gameObject.SetActive(false);

            if (!best.t.gameObject.activeSelf)
                best.t.gameObject.SetActive(true);

            best.img.color = Plugin.PlayerColors[0];

            float d  = Plugin.DotSize.Value * 2f;
            var   rt = best.img.rectTransform;
            if (rt != null)
            {
                rt.sizeDelta = new Vector2(d, d);
                // Game already moved the marker this frame; bump it up by Y_OFFSET
                var pos = rt.anchoredPosition;
                rt.anchoredPosition = new Vector2(pos.x, pos.y + Y_OFFSET);
            }
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

                activeCids.Add(cid);

                if (!MapDotState.Dots.TryGetValue(cid, out var dot) || dot.Root == null)
                {
                    var pName = MapDotState.FNGPPlayerName.GetValue(ngp) as string ?? "Player";
                    var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                    dot = CreateDot(npcImages, pName, color);
                    MapDotState.Dots[cid] = dot;
                }

                colorIndex++;

                var exactPos  = (Vector2)MapDotState.PPlayerExact.GetValue(player);
                var sceneName = MapDotState.FPlayerScene.GetValue(player) as string ?? string.Empty;
                var worldPos3 = new Vector3(exactPos.x, exactPos.y, 0f);

                try
                {
                    var mapPos = (Vector2)MapDotState.MGetPlayerPos.Invoke(map, new object[] { dot.Img, worldPos3, sceneName });
                    mapPos.y += Y_OFFSET;
                    MapDotState.MSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, immediate });
                    dot.Root.SetActive(true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[MapDots] Position error for cid {cid}: {ex.Message}");
                }
            }

            var toRemove = new List<int>();
            foreach (var kvp in MapDotState.Dots)
            {
                if (activeCids.Contains(kvp.Key)) continue;
                if (kvp.Value.Root == null) toRemove.Add(kvp.Key);
                else kvp.Value.Root.SetActive(false);
            }
            foreach (var k in toRemove) MapDotState.Dots.Remove(k);
        }

        static PlayerDot CreateDot(Transform parent, string playerName, Color color)
        {
            float size = Plugin.DotSize.Value * 2f;

            var root = new GameObject($"PlayerDot_{playerName}",
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
            if (Plugin.ShowPlayerNames.Value)
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
            Patch_UpdatePlayerImagePosition.LoggedOnce = false;
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
