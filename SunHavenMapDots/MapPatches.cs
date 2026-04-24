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
        internal static FieldInfo    FMapContent;

        // NetworkGamePlayer
        internal static FieldInfo    FNGPPlayer;
        internal static FieldInfo    FNGPPlayerName;
        internal static FieldInfo    FNGPFullyInit;
        internal static FieldInfo    FNGPScene;

        // Scene lookup
        internal static PropertyInfo PSceneSettingsMgrInst;
        internal static FieldInfo    FSceneDict;
        internal static FieldInfo    FSceneSettingsName;

        // Local player
        internal static FieldInfo    FPlayerInstance;
        internal static PropertyInfo PActiveSceneName;

        internal static readonly Dictionary<int, PlayerDot> Dots = new Dictionary<int, PlayerDot>();

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
            FMapContent = mapType.GetField("mapContent", F)
                         ?? throw new MissingFieldException(nameof(Map), "mapContent");

            var ngpType = typeof(NetworkGamePlayer);
            FNGPPlayer     = ngpType.GetField("player",             F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "player");
            FNGPPlayerName = ngpType.GetField("playerName",         F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "playerName");
            FNGPFullyInit  = ngpType.GetField("isFullyInitialized", F) ?? throw new MissingFieldException(nameof(NetworkGamePlayer), "isFullyInitialized");
            FNGPScene      = ngpType.GetField("scene",              F);

            var ssmType = Type.GetType("Wish.SceneSettingsManager, SunHaven.Core")
                       ?? Type.GetType("Wish.SceneSettingsManager, Assembly-CSharp");
            if (ssmType != null)
            {
                PSceneSettingsMgrInst = ssmType.GetProperty("Instance", SF);
                FSceneDict            = ssmType.GetField("sceneDictionary", F);
            }
            var ssType = Type.GetType("Wish.SceneSettings, SunHaven.Core")
                      ?? Type.GetType("Wish.SceneSettings, Assembly-CSharp");
            if (ssType != null)
                FSceneSettingsName = ssType.GetField("sceneName", F);

            FPlayerInstance  = typeof(Player).GetField("Instance", SF);
            var spmType = Type.GetType("Wish.ScenePortalManager, SunHaven.Core")
                       ?? Type.GetType("Wish.ScenePortalManager, Assembly-CSharp");
            if (spmType != null)
                PActiveSceneName = spmType.GetProperty("ActiveSceneName", SF);

            Plugin.Log.LogInfo($"[MapDots] Reflection ready. PlayerInst={FPlayerInstance != null} ActiveScene={PActiveSceneName != null} MapContent={FMapContent != null}");
        }

        internal static string GetRemotePlayerScene(NetworkGamePlayer ngp)
        {
            if (FNGPScene == null || PSceneSettingsMgrInst == null || FSceneDict == null || FSceneSettingsName == null)
                return GetActiveSceneName();

            var sceneId = Convert.ToInt32(FNGPScene.GetValue(ngp));
            var mgrInst = PSceneSettingsMgrInst.GetValue(null);
            if (mgrInst == null) return GetActiveSceneName();

            var dict = FSceneDict.GetValue(mgrInst) as IDictionary;
            if (dict == null) return GetActiveSceneName();

            var settings = dict[sceneId];
            if (settings == null) return GetActiveSceneName();

            return FSceneSettingsName.GetValue(settings) as string ?? GetActiveSceneName();
        }

        internal static string GetActiveSceneName() =>
            PActiveSceneName?.GetValue(null) as string ?? string.Empty;

        internal static void DestroyAllDots()
        {
            foreach (var dot in Dots.Values)
                if (dot.Root != null) UnityEngine.Object.Destroy(dot.Root);
            Dots.Clear();
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
        // cached circle sprite shared by all dots
        static Sprite _dotSprite;

        [HarmonyPostfix]
        static void Postfix(Map __instance, bool immediate)
        {
            try
            {
                MapDotState.EnsureReflection();
                var mapContent = MapDotState.FMapContent.GetValue(__instance) as RectTransform;
                if (mapContent == null) return;

                if (Plugin.ShowLocalPlayer.Value)
                    HandleLocalPlayer(__instance, immediate, mapContent);
                if (Plugin.ShowRemotePlayers.Value)
                    HandleRemotePlayers(__instance, immediate, mapContent);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[MapDots] Exception: {ex}");
            }
        }

        static void HandleLocalPlayer(Map map, bool immediate, RectTransform mapContent)
        {
            if (MapDotState.FPlayerInstance == null) return;
            var player = MapDotState.FPlayerInstance.GetValue(null) as Player;
            if (player == null) return;

            var sceneName = MapDotState.GetActiveSceneName();
            PositionDot(map, immediate, -1, player.transform.position, sceneName, 0, mapContent, "");
        }

        static void HandleRemotePlayers(Map map, bool immediate, RectTransform mapContent)
        {
            var lobby = NetworkLobbyManager.Instance;
            if (lobby == null) return;

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

                activeCids.Add(cid);
                var pName    = MapDotState.FNGPPlayerName.GetValue(ngp) as string ?? "Player";
                var sceneName = MapDotState.GetRemotePlayerScene(ngp);
                PositionDot(map, immediate, cid, remotePlayer.transform.position, sceneName, colorIndex, mapContent, pName);
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

        static float _logTimer = 0f;

        static void PositionDot(Map map, bool immediate, int id, Vector3 worldPos,
                                string scene, int colorIndex, RectTransform mapContent, string playerName)
        {
            if (!MapDotState.Dots.TryGetValue(id, out var dot) || dot.Root == null)
            {
                var color = Plugin.PlayerColors[colorIndex % Plugin.PlayerColors.Length];
                dot = CreateDot(mapContent, playerName, color);
                MapDotState.Dots[id] = dot;
            }

            try
            {
                var mapPos = (Vector2)MapDotState.MGetPlayerPos.Invoke(
                    map, new object[] { dot.Img, worldPos, scene });
                mapPos.x += Plugin.DotOffsetX.Value;
                mapPos.y += Plugin.DotOffsetY.Value;
                MapDotState.MSetImagePos.Invoke(map, new object[] { dot.Img, mapPos, true });
                dot.Root.SetActive(true);

                _logTimer += Time.unscaledDeltaTime;
                if (_logTimer >= 2f && id == -1)
                {
                    _logTimer = 0f;
                    Plugin.Log.LogInfo($"[MapDots] scene='{scene}' world=({worldPos.x:F1},{worldPos.y:F1}) mapPos=({mapPos.x:F1},{mapPos.y:F1}) localPos={dot.Img.transform.localPosition}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[MapDots] Position error id={id}: {ex.Message}");
            }
        }

        static PlayerDot CreateDot(RectTransform parent, string playerName, Color color)
        {
            float size = Plugin.DotSize.Value;
            var sprite = GetOrCreateDotSprite();

            // ── Root: the Image used for positioning ──────────────────────────
            var root = new GameObject(
                string.IsNullOrEmpty(playerName) ? "MapDot_Local" : $"MapDot_{playerName}",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();

            var rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.sizeDelta = new Vector2(size, size);

            var img = root.GetComponent<Image>();
            img.sprite = sprite;
            img.color  = color;
            img.raycastTarget = false;

            // ── White centre highlight ────────────────────────────────────────
            var hlGo = new GameObject("Highlight", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hlGo.transform.SetParent(root.transform, false);
            var hlRt  = hlGo.GetComponent<RectTransform>();
            hlRt.anchorMin = hlRt.anchorMax = hlRt.pivot = new Vector2(0.5f, 0.5f);
            hlRt.sizeDelta = new Vector2(size * 0.38f, size * 0.38f);
            var hlImg = hlGo.GetComponent<Image>();
            hlImg.sprite = sprite;
            hlImg.color  = new Color(1f, 1f, 1f, 0.75f);
            hlImg.raycastTarget = false;

            // ── Optional name label ───────────────────────────────────────────
            TextMeshProUGUI label = null;
            if (Plugin.ShowPlayerNames.Value && !string.IsNullOrEmpty(playerName))
            {
                var labelGo = new GameObject("Label",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                labelGo.transform.SetParent(root.transform, false);
                var labelRt = labelGo.GetComponent<RectTransform>();
                labelRt.anchorMin = labelRt.anchorMax = labelRt.pivot = new Vector2(0.5f, 0f);
                labelRt.anchoredPosition = new Vector2(0f, size * 0.7f + 2f);
                labelRt.sizeDelta = new Vector2(120f, 18f);
                label = labelGo.GetComponent<TextMeshProUGUI>();
                label.text = playerName;
                label.fontSize = 9f;
                label.color = Color.white;
                label.alignment = TextAlignmentOptions.Center;
                label.raycastTarget = false;
            }

            return new PlayerDot(root, img, label);
        }

        static Sprite GetOrCreateDotSprite()
        {
            if (_dotSprite != null) return _dotSprite;

            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            float c = (S - 1) * 0.5f;
            float r = S * 0.46f;
            const float feather = 2.5f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01((r - d + feather * 0.5f) / feather);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            _dotSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            return _dotSprite;
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
