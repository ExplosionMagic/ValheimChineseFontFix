using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Text = UnityEngine.UI.Text;

namespace ValheimFontPriorityFix
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class FontPriorityFixPlugin : BaseUnityPlugin
    {
        public const string PluginId = "dev.valheim-cn.font-priority-fix";
        public const string PluginName = "Valheim Chinese Font Priority Fix";
        public const string PluginVersion = "0.2.0";

        private static FontPriorityFixPlugin _instance;
        private Harmony _harmony;
        private bool _jotunnPatched;

        // 缓存：中文的 UnityEngine.Font
        internal static Font ChineseSansUiFont;
        internal static Font ChineseSerifUiFont;

        // 缓存：中文的 TMP_FontAsset
        internal static TMP_FontAsset ChineseSansTmp;
        internal static TMP_FontAsset ChineseSerifTmp;

        private void Awake()
        {
            _instance = this;
            _harmony = new Harmony(PluginId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // 监听场景加载，每次进入新场景后重新应用优先级
            SceneManager.sceneLoaded += OnSceneLoaded;

            StartCoroutine(InitialFix());
            TryPatchJotunn();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo($"场景加载: {scene.name}，重新应用字体优先级。");
            StartCoroutine(DelayedApply(scene.name));
        }

        private IEnumerator DelayedApply(string sceneName)
        {
            yield return null;
            yield return null;
            ApplyFallbackFixes();
            ForceUpdateAllText();
        }

        // ============ 修复入口 ============

        private IEnumerator InitialFix()
        {
            yield return new WaitForSecondsRealtime(3f);
            ApplyFallbackFixes();
            ForceUpdateAllText();

            yield return new WaitForSecondsRealtime(5f);
            ApplyFallbackFixes();
            ForceUpdateAllText();
        }

        private static IEnumerator DelayedReapply()
        {
            yield return null;
            yield return null;
            _instance?.ApplyFallbackFixes();
            _instance?.ForceUpdateAllText();
            Debug.Log("[FontPriorityFix] Jotunn 初始化后已重新应用字体修复。");
        }

        private void TryPatchJotunn()
        {
            if (_jotunnPatched) return;

            Type jotunnMainType = AccessTools.TypeByName("Jotunn.Main");
            if (jotunnMainType == null)
            {
                Logger.LogInfo("未检测到 Jotunn，跳过时序补丁。");
                return;
            }

            var awakeMethod = AccessTools.Method(jotunnMainType, "Awake");
            if (awakeMethod == null) return;

            try
            {
                var postfix = new HarmonyMethod(
                    typeof(FontPriorityFixPlugin).GetMethod(
                        nameof(AfterJotunnAwake),
                        BindingFlags.Static | BindingFlags.NonPublic));

                _harmony.Patch(awakeMethod, postfix: postfix);
                _jotunnPatched = true;
                Logger.LogInfo("已 Hook Jotunn.Main.Awake（Postfix）。");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"Hook Jotunn.Main.Awake 失败：{e.Message}");
            }
        }

        private static void AfterJotunnAwake()
        {
            _instance?.StartCoroutine(DelayedReapply());
        }

        // ============ TMP 回退优先级修复 ============

        private void ApplyFallbackFixes()
        {
            var allAssets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

            // 找 SC 字体池和缓存 ChineseSerifTmp / ChineseSansTmp
            var allScFonts = new List<TMP_FontAsset>();
            foreach (var f in allAssets)
            {
                if (f == null) continue;
                var n = f.name.Replace(" ", "");
                if (n.IndexOf("NotoSansSC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("NotoSerifSC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!allScFonts.Contains(f))
                        allScFonts.Add(f);
                }
            }

            if (allScFonts.Count == 0)
            {
                Logger.LogWarning("未找到任何 SC 字体资产。");
                return;
            }

            // 缓存供 XPortal Hook 使用
            ChineseSansTmp = allScFonts.FirstOrDefault(f =>
                f.name.Replace(" ", "").IndexOf("NotoSansSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0);
            ChineseSerifTmp = allScFonts.FirstOrDefault(f =>
                f.name.Replace(" ", "").IndexOf("NotoSerifSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0);

            var globalList = TMP_Settings.fallbackFontAssets;
            if (globalList != null)
            {
                bool changed = ReorderList(globalList, allScFonts);
                if (changed)
                    Logger.LogInfo(
                        $"全局回退列表（已重排）: " +
                        $"{string.Join(" -> ", globalList.Select(f => f?.name ?? "null"))}"
                    );
            }

            int fixedCount = 0;
            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                var localList = asset.fallbackFontAssetTable;
                if (localList == null || localList.Count == 0) continue;

                if (ReorderList(localList, allScFonts))
                    fixedCount++;
            }

            if (fixedCount > 0)
                Logger.LogInfo($"共调整 {fixedCount} 个资产的本地回退列表。");
        }

        private static bool ReorderList(List<TMP_FontAsset> list, List<TMP_FontAsset> allScFonts)
        {
            if (list == null || list.Count == 0) return false;

            var newList = new List<TMP_FontAsset>(list.Count + 4);
            var added = new HashSet<TMP_FontAsset>();
            bool changed = false;

            foreach (var f in list)
            {
                if (f == null) { newList.Add(null); continue; }

                if (IsJpFont(f.name))
                {
                    var matchSc = FindMatchingSc(f.name, allScFonts);
                    if (matchSc != null && !added.Contains(matchSc))
                    {
                        newList.Add(matchSc);
                        added.Add(matchSc);
                        changed = true;
                    }
                }

                if (!added.Contains(f))
                {
                    newList.Add(f);
                    added.Add(f);
                }
                else changed = true;
            }

            if (newList.Count != list.Count) changed = true;

            if (changed)
            {
                list.Clear();
                list.AddRange(newList);
            }
            return changed;
        }

        private static TMP_FontAsset FindMatchingSc(string jpName, List<TMP_FontAsset> scFonts)
        {
            if (string.IsNullOrEmpty(jpName)) return null;
            var jp = jpName.Replace(" ", "");

            bool isSans = jp.IndexOf("Sans", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isSerif = jp.IndexOf("Serif", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isSans && !isSerif) return null;

            string weightKey = null;
            if (jp.IndexOf("Thin", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Thin";
            else if (jp.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Regular";
            else if (jp.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Bold";
            else if (jp.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Light";

            string familyKey = isSans ? "SansSC" : "SerifSC";

            foreach (var sc in scFonts)
            {
                if (sc == null) continue;
                var n = sc.name.Replace(" ", "");
                if (n.IndexOf(familyKey, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (weightKey != null && n.IndexOf(weightKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sc;
            }
            return null;
        }

        private static bool IsJpFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Replace(" ", "");
            return n.IndexOf("NotoSansJP", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("NotoSerifJP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ForceUpdateAllText()
        {
            try { TMP_ResourceManager.ClearFontAssetGlyphCache(); }
            catch { }

            int refreshed = 0;
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null) continue;
                text.SetAllDirty();
                text.ForceMeshUpdate();
                refreshed++;
            }
            Logger.LogInfo($"已清除字形缓存并强制刷新 {refreshed} 个 TMP_Text。");
        }

        // ============ XPortal 的 UnityEngine.UI.Text 修复 ============

        internal static void PickChineseUiFonts()
        {
            if (ChineseSansUiFont != null && ChineseSerifUiFont != null) return;

            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            foreach (var f in fonts)
            {
                if (f == null) continue;
                var n = f.name ?? "";
                if (ChineseSansUiFont == null &&
                    n.IndexOf("NotoSansSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0)
                    ChineseSansUiFont = f;
                if (ChineseSerifUiFont == null &&
                    n.IndexOf("NotoSerifSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0)
                    ChineseSerifUiFont = f;
            }

            if (ChineseSansUiFont != null)
                _instance?.Logger.LogInfo($"[XPortal] 缓存 UI Sans 字体: {ChineseSansUiFont.name}");
            if (ChineseSerifUiFont != null)
                _instance?.Logger.LogInfo($"[XPortal] 缓存 UI Serif 字体: {ChineseSerifUiFont.name}");
        }

        /// <summary>
        /// 判断是否在 XPortal 面板内（根据根对象名判断）。
        /// </summary>
        internal static bool IsInXPortalPanel(Transform t)
        {
            if (t == null) return false;
            var root = t.root;
            if (root == null) return false;
            return root.name.IndexOf("XPortal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // ============ Harmony 补丁：只对 XPortal 面板内的 UI.Text 换字体 ============

    [HarmonyPatch(typeof(Text), "set_font")]
    internal static class XPortalTextSetFontPatch
    {
        static void Prefix(Text __instance, ref Font value)
        {
            if (value == null || __instance == null) return;

            // 首次调用时缓存中文 UI 字体
            FontPriorityFixPlugin.PickChineseUiFonts();

            // 只在 XPortal 面板内替换
            if (!FontPriorityFixPlugin.IsInXPortalPanel(__instance.transform))
                return;

            var name = value.name ?? "";

            // AveriaSerif → NotoSerifSC-Regular
            if (name.IndexOf("AveriaSerif", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (FontPriorityFixPlugin.ChineseSerifUiFont != null)
                    value = FontPriorityFixPlugin.ChineseSerifUiFont;
            }
            // AveriaSans → NotoSansSC-Regular
            else if (name.IndexOf("AveriaSans", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (FontPriorityFixPlugin.ChineseSansUiFont != null)
                    value = FontPriorityFixPlugin.ChineseSansUiFont;
            }
        }
    }
}