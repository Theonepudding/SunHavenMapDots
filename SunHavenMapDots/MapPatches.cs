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
    // ──────────────────────────────────────────────────────────────────────────
    // Holds the UI objects we create for one remote player.
    // ──────────────────────────────────────────────────────────────────────────
    internal sealed class PlayerDot
    {
        internal readonly GameObject Root;
        internal readonly Image     Img;
        internal readonly TextMeshProUGUI Label; // may be null if TMP unavailable

        internal PlayerDot(GameObject root, Image img, TextMeshProUGUI label)
        {
            Root  = root;
            Img   = img;
            Label = label;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // All Harmony patches live here.
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch]
    internal static class MapPatches
    {
        // ── Reflection cache ──────────────────────────────────────────────────
        private static FieldInfo  _fMapContent;      // Map.mapContent        (RectTransform)
        private static FieldInfo  _fPlayerImages;    // Map.playerImages      (List<Image>)
        private static MethodInfo _mGetPlayerPos;    // Map.GetPlayerPosition (Image,Vector3,string)->Vector2
        private static MethodInfo _mSetImagePos;     // Map.SetImagePosition  (Image,Vector2,bool)->void

        private static FieldInfo  _fNGPPlayer;       // NetworkGamePlayer.player  (Player)
        private static FieldInfo  _fNGPPlayerName;   // NetworkGamePlayer.playerName (string)
        private static FieldInfo  _fNGPFullyInit;    // NetworkGamePlayer.isFullyInitialized (bool)
        private static FieldInfo  _fPlayerScene;     // Player.currentScene   (string)
        private static PropertyInfo _pPlayerExact;  // Player.ExactPosition  (Vector2)

        // ── Per-session state ─────────────────────────────────────────────────
        // key = connection id (int), value = our dot data
        private static readonly Dictionary<int, PlayerDot> _dots = new();
        private static Map _currentMap;

        // ── Patch target ──────────────────────────────────────────────────────
        [HarmonyTargetMethod]
        static MethodBase TargetMethod()
            => AccessTools.Method(typeof(Map), "UpdatePlayerImagePosition", new[] { typeof(bool) });

        [HarmonyPostfix]
        static void UpdatePlayerImagePosition_Postfix(Map __instance, bool immediate)
        {
            try
            {
                EnsureReflection();
                HandleLocalPlayerFix(__instance, immediate);
                if (Plugin.ShowRemotePlayers.Value)
                    HandleRemotePlayers(__instance, immediate);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MapDots] Unhandled exception: {ex}");
            }
        }

        // ── Map opened / closed ───────────────────────────────────────────────
        [HarmonyPatch(typeof(Map), "OnEnable")]
        [HarmonyPostfix]
        static void OnEnable_Postfix(Map __instance)
        {
            if (_currentMap != __instance)
            {
                DestroyAllDots();
                _currentMap = __instance;
            }
        }

        [HarmonyPatch(typeof(Map), "OnDisable")]
        [HarmonyPostfix]
        static void OnDisable_Postfix()
        {
            // Hide dots while map is closed (positions may be stale).
            foreach (var dot in _dots.Values)
                if (dot.Root != null) dot.Root.SetActive(false);
        }

        // ── Local-player fix ──────────────────────────────────────────────────
        // Restores the yellow local-player dot (re-implements the original fix).
        static void HandleLocalPlayerFix(Map map, bool immediate)
        {
            if (!Plugin.ShowLocalPlayer.Value) return;

            var images = _fPlayerImages.GetValue(map) as List<Image>;
            if (images == null || images.Count == 0) return;

            // The game populates playerImages[0] for the local player.
            // If it got destroyed/deactivated, reactivate it.
            var localImg = images[0];
            if (localImg == null) return;

            // Ensure it's visible and properly sized.
            if (!localImg.gameObject.activeSelf)
                localImg.gameObject.SetActive(true);

            // Tint yellow so it's obviously "you".
            localImg.color = Plugin.PlayerColors[0];

            float d = Plugin.DotSize.Value * 2f;
            var rt = localImg.rectTransform;
            if (rt != null) rt.sizeDelta = new Vector2(d, d);
        }

        // ── Remote players ────────────────────────────────────────────────────
        static void HandleRemotePlayers(Map map, bool immediate)
        {
            var lobby = NetworkLobbyManager.Instance;
            if (lobby == null) return;

            var mapContent = _fMapContent.GetValue(map) as RectTransform;
            if (mapContent == null) return;

            var activeCids = new HashSet<int>();
            int colorIndex = 1;

            foreach (var kvp in lobby.GamePlayers)
            {
                var cid = kvp.Key;
                var ngp = kvp.Value;

                if (ngp == null || ngp.gameObject == null)              continue;
                if (ngp.isLocalPlayer)                                  continue;
                if (!(bool)_fNGPFullyInit.GetValue(ngp))               continue;
                if (!ngp.SameScene)                                     continue;

                var player = _fNGPPlayer.GetValue(ngp) as Player;
                if (player == null)                                      continue;

                activeCids.Add(cid);

                // Create dot on first encounter.
                if (!_dots.TryGetValue(cid, out var dot) || dot.Root == null)
                {
                    var name  = _fNGPPlayerName.GetValue(ngp) as string ?? "Player";
                    var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                    dot = CreateDot(mapContent, name, color);
                    _dots[cid] = dot;
                }

                colorIndex++;

                // Compute world position and scene name.
                var exactPos  = (Vector2)_pPlayerExact.GetValue(player);
                var sceneName = _fPlayerScene.GetValue(player) as string ?? string.Empty;
                var worldPos3 = new Vector3(exactPos.x, exactPos.y, 0f);

                try
                {
                    var mapPos = (Vector2)_mGetPlayerPos.Invoke(map, new object[] { dot.Img, worldPos3, sceneName });
                    _mSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, immediate });
                    dot.Root.SetActive(true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[MapDots] Position error for connection {cid}: {ex.Message}");
                }
            }

            // Hide or clean up dots for players who are no longer active/same-scene.
            var toRemove = new List<int>();
            foreach (var kvp in _dots)
            {
                if (!activeCids.Contains(kvp.Key))
                {
                    if (kvp.Value.Root == null)
                        toRemove.Add(kvp.Key);
                    else
                        kvp.Value.Root.SetActive(false);
                }
            }
            foreach (var k in toRemove) _dots.Remove(k);
        }

        // ── Dot factory ───────────────────────────────────────────────────────
        static PlayerDot CreateDot(RectTransform parent, string playerName, Color color)
        {
            float size = Plugin.DotSize.Value * 2f;

            // Root GameObject parented to the map content layer.
            var root = new GameObject($"MapDot_{playerName}", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling(); // render on top of NPC dots

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(size, size);

            // Coloured circle (outer ring).
            var ringGo = new GameObject("Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            ringGo.transform.SetParent(root.transform, false);
            var ringRt = ringGo.GetComponent<RectTransform>();
            ringRt.anchorMin = ringRt.anchorMax = ringRt.pivot = new Vector2(0.5f, 0.5f);
            ringRt.sizeDelta = new Vector2(size, size);
            var ringImg = ringGo.GetComponent<Image>();
            ringImg.color = color;
            ringImg.raycastTarget = false;

            // White dot in the centre so the marker is readable on any background.
            var coreGo = new GameObject("Core", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            coreGo.transform.SetParent(root.transform, false);
            var coreRt = coreGo.GetComponent<RectTransform>();
            coreRt.anchorMin = coreRt.anchorMax = coreRt.pivot = new Vector2(0.5f, 0.5f);
            coreRt.sizeDelta = new Vector2(size * 0.45f, size * 0.45f);
            var coreImg = coreGo.GetComponent<Image>();
            coreImg.color = Color.white;
            coreImg.raycastTarget = false;

            // Optional name label above the dot.
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

        // ── Cleanup ───────────────────────────────────────────────────────────
        static void DestroyAllDots()
        {
            foreach (var dot in _dots.Values)
                if (dot.Root != null) UnityEngine.Object.Destroy(dot.Root);
            _dots.Clear();
        }

        // ── Reflection bootstrap ─────────────────────────────────────────────
        static void EnsureReflection()
        {
            if (_fMapContent != null) return; // already initialised

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags S = BindingFlags.Static   | BindingFlags.NonPublic | BindingFlags.Public;

            var mapType = typeof(Map);
            _fMapContent   = mapType.GetField("mapContent",   F) ?? throw new MissingFieldException(nameof(Map), "mapContent");
            _fPlayerImages = mapType.GetField("playerImages",  F) ?? throw new MissingFieldException(nameof(Map), "playerImages");
            _mGetPlayerPos = mapType.GetMethod("GetPlayerPosition",
                F, null, new[] { typeof(Image), typeof(Vector3), typeof(string) }, null)
                ?? throw new MissingMethodException(nameof(Map), "GetPlayerPosition");
            _mSetImagePos  = mapType.GetMethod("SetImagePosition",
                F, null, new[] { typeof(Image), typeof(Vector2), typeof(bool) }, null)
                ?? throw new MissingMethodException(nameof(Map), "SetImagePosition");

            var ngpType = typeof(NetworkGamePlayer);
            _fNGPPlayer    = ngpType.GetField("player",            F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "player");
            _fNGPPlayerName= ngpType.GetField("playerName",        F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "playerName");
            _fNGPFullyInit = ngpType.GetField("isFullyInitialized",F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "isFullyInitialized");

            var playerType = typeof(Player);
            _fPlayerScene  = playerType.GetField("currentScene",   F) ?? throw new MissingFieldException(nameof(Player), "currentScene");
            _pPlayerExact  = playerType.GetProperty("ExactPosition", F)
                          ?? playerType.GetProperty("ExactPosition", BindingFlags.Instance | BindingFlags.Public)
                          ?? throw new MissingMemberException(nameof(Player), "ExactPosition");

            Plugin.Log.LogInfo("[MapDots] Reflection cache ready.");
        }
    }
}
