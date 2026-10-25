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
        public const string PluginVersion = "0.2.6";

        private static FontPriorityFixPlugin _instance;
        private Harmony _harmony;

        internal static Font ChineseSansUiFont;
        internal static Font ChineseSerifUiFont;

        private void Awake()
        {
            _instance = this;
            _harmony = new Harmony(PluginId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(InitialFix());
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(DelayedApply());
        }

        private IEnumerator DelayedApply()
        {
            yield return null;
            yield return null;
            ApplyFallbackFixes();
            ForceUpdateAllText();
        }

        private IEnumerator InitialFix()
        {
            yield return new WaitForSecondsRealtime(3f);
            ApplyFallbackFixes();
            ForceUpdateAllText();

            yield return new WaitForSecondsRealtime(5f);
            ApplyFallbackFixes();
            ForceUpdateAllText();
        }

        internal static void PickChineseUiFonts()
        {
            if (ChineseSansUiFont != null && ChineseSerifUiFont != null) return;

            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            foreach (var f in fonts)
            {
                if (f == null) continue;
                var n = f.name ?? "";
                if (ChineseSansUiFont == null && n.IndexOf("NotoSansSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0)
                    ChineseSansUiFont = f;
                if (ChineseSerifUiFont == null && n.IndexOf("NotoSerifSC-Regular", StringComparison.OrdinalIgnoreCase) >= 0)
                    ChineseSerifUiFont = f;
            }
        }

        // ============ TMP 回退优先级修复 ============

        private void ApplyFallbackFixes()
        {
            var allAssets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

            var allScFonts = new List<TMP_FontAsset>();
            foreach (var f in allAssets)
            {
                if (f == null) continue;
                var n = f.name.Replace(" ", "");
                if (n.IndexOf("NotoSansSC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("NotoSerifSC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!allScFonts.Contains(f)) allScFonts.Add(f);
                }
            }

            if (allScFonts.Count == 0) return;

            var globalList = TMP_Settings.fallbackFontAssets;
            if (globalList != null) ReorderList(globalList, allScFonts);

            int fixedCount = 0;
            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                var localList = asset.fallbackFontAssetTable;
                if (localList == null || localList.Count == 0) continue;
                if (ReorderList(localList, allScFonts)) fixedCount++;
            }
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
                if (!added.Contains(f)) { newList.Add(f); added.Add(f); }
                else changed = true;
            }

            if (newList.Count != list.Count) changed = true;
            if (changed) { list.Clear(); list.AddRange(newList); }
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

            string familyKey = isSans ? "SansSC" : "SerifSC";
            foreach (var sc in scFonts)
            {
                if (sc == null) continue;
                var n = sc.name.Replace(" ", "");
                if (n.IndexOf(familyKey, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (weightKey != null && n.IndexOf(weightKey, StringComparison.OrdinalIgnoreCase) >= 0) return sc;
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
            try { TMP_ResourceManager.ClearFontAssetGlyphCache(); } catch { }

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null) continue;
                text.SetAllDirty();
                text.ForceMeshUpdate();
            }
        }
    }

    // ============ Harmony Patch: 截获 Jotunn 的初始化，从源头替换字体 ============

    [HarmonyPatch]
    internal static class JotunnInitPatch
    {
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Jotunn.Managers.GUIManager");
            return type == null ? null : AccessTools.Method(type, "InitializeAssets");
        }

        static void Postfix()
        {
            FontPriorityFixPlugin.PickChineseUiFonts();
            if (FontPriorityFixPlugin.ChineseSerifUiFont == null) return;

            Type guiManagerType = AccessTools.TypeByName("Jotunn.Managers.GUIManager");
            if (guiManagerType == null) return;

            var instanceProp = guiManagerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            if (instance == null) return;

            string[] serifFields = { "<AveriaSerif>k__BackingField", "<AveriaSerifBold>k__BackingField" };
            string[] sansFields = { "<AveriaSans>k__BackingField", "<AveriaSansBold>k__BackingField" };

            foreach (var fieldName in serifFields)
            {
                var field = guiManagerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(instance, FontPriorityFixPlugin.ChineseSerifUiFont);
            }
            foreach (var fieldName in sansFields)
            {
                var field = guiManagerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null && FontPriorityFixPlugin.ChineseSansUiFont != null)
                    field.SetValue(instance, FontPriorityFixPlugin.ChineseSansUiFont);
            }
        }
    }

    // ============ Harmony Patch: 修复 InputField 排版溢出 ============

    [HarmonyPatch]
    internal static class JotunnCreateInputFieldPatch
    {
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Jotunn.Managers.GUIManager");
            return type == null ? null : AccessTools.Method(type, "CreateInputField");
        }

        static void Postfix(ref GameObject __result)
        {
            if (__result == null) return;
            FontPriorityFixPlugin.PickChineseUiFonts();
            var font = FontPriorityFixPlugin.ChineseSerifUiFont;
            if (font == null) return;

            var input = __result.GetComponent<InputField>();
            if (input != null)
            {
                if (input.textComponent != null)
                {
                    var t = input.textComponent;
                    t.font = font;
                    t.color = Color.white;
                    t.alignment = TextAnchor.MiddleLeft;
                    t.horizontalOverflow = HorizontalWrapMode.Overflow;
                    t.verticalOverflow = VerticalWrapMode.Overflow;
                    t.raycastTarget = false; // 确保不阻挡点击
                    t.SetAllDirty();

                    // 强制重置其尺寸，防止被裁剪
                    var rt = t.GetComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = new Vector2(5f, 0f);
                    rt.offsetMax = new Vector2(-5f, 0f);
                }

                if (input.placeholder is Text p)
                {
                    p.font = font;
                    p.color = new Color(0.6f, 0.6f, 0.6f, 1f); // 灰色占位符
                    p.alignment = TextAnchor.MiddleLeft;
                    p.horizontalOverflow = HorizontalWrapMode.Overflow;
                    p.verticalOverflow = VerticalWrapMode.Overflow;
                    p.SetAllDirty();

                    var rt = p.GetComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = new Vector2(5f, 0f);
                    rt.offsetMax = new Vector2(-5f, 0f);
                }

                // 强制刷新 InputField 内部状态
                input.ForceLabelUpdate();
            }
        }
    }

    // ============ Harmony Patch: UI.Text 兜底 ============

    [HarmonyPatch(typeof(Text), "set_font")]
    internal static class AveriaTextFontPatch
    {
        static void Prefix(Text __instance, ref Font value)
        {
            if (value == null || __instance == null) return;

            // 输入框内部的 Text 跳过，由 JotunnCreateInputFieldPatch 专门处理
            if (__instance.GetComponentInParent<InputField>() != null) return;

            var name = value.name ?? "";
            if (name.IndexOf("AveriaSerif", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("AveriaSans", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                FontPriorityFixPlugin.PickChineseUiFonts();

                if (name.IndexOf("AveriaSerif", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (FontPriorityFixPlugin.ChineseSerifUiFont != null)
                        value = FontPriorityFixPlugin.ChineseSerifUiFont;
                }
                else
                {
                    if (FontPriorityFixPlugin.ChineseSansUiFont != null)
                        value = FontPriorityFixPlugin.ChineseSansUiFont;
                }
            }
        }
    }
}