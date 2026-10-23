using BepInEx;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace ValheimFontPriorityFix
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class FontPriorityFixPlugin : BaseUnityPlugin
    {
        public const string PluginId = "dev.valheim-cn.font-priority-fix";
        public const string PluginName = "Valheim Chinese Font Priority Fix";
        public const string PluginVersion = "0.1.4";

        private static FontPriorityFixPlugin _instance;
        private Harmony _harmony;
        private bool _jotunnPatched;

        private void Awake()
        {
            _instance = this;
            _harmony = new Harmony(PluginId);
            StartCoroutine(InitialFix());
            TryPatchJotunn();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
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

        // ============ Jotunn 时序补丁 ============

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
            if (awakeMethod == null)
            {
                Logger.LogWarning("未找到 Jotunn.Main.Awake 方法，跳过时序补丁。");
                return;
            }

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

        // ============ 核心修复逻辑 ============

        private void ApplyFallbackFixes()
        {
            var allAssets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

            // 收集所有 SC 字体（全局池）
            var allScFonts = new List<TMP_FontAsset>();
            foreach (var f in allAssets)
            {
                if (f != null && IsScFont(f.name) && !allScFonts.Contains(f))
                    allScFonts.Add(f);
            }

            if (allScFonts.Count == 0)
            {
                Logger.LogWarning("未找到任何 SC 字体资产。");
                return;
            }

            Logger.LogInfo($"SC 字体池: {string.Join(", ", allScFonts.Select(f => f.name))}");

            // ---- 全局回退列表 ----
            var globalList = TMP_Settings.fallbackFontAssets;
            if (globalList != null)
            {
                bool changed = ReorderList(globalList, allScFonts);
                Logger.LogInfo(
                    $"全局回退列表{(changed ? "（已重排）" : "（无变化）")}: " +
                    $"{string.Join(" -> ", globalList.Select(f => f?.name ?? "null"))}"
                );
            }

            // ---- 所有资产的本地回退列表 ----
            int fixedCount = 0;
            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                var localList = asset.fallbackFontAssetTable;
                if (localList == null || localList.Count == 0) continue;

                if (ReorderList(localList, allScFonts))
                {
                    fixedCount++;
                    Logger.LogInfo(
                        $"  修正本地回退: {asset.name} -> " +
                        $"[{string.Join(", ", localList.Select(f => f?.name ?? "null"))}]"
                    );
                }
            }

            Logger.LogInfo($"共调整 {fixedCount} 个资产的本地回退列表。");
        }

        /// <summary>
        /// 对回退列表做"SC 插到同字重 JP 之前"的重排，保留其余所有相对顺序。
        ///
        /// 规则示例：
        ///   [SansJP-Regular]           → [SansSC-Regular, SansJP-Regular]
        ///   [SansJP-Thin]              → [SansSC-Thin, SansJP-Thin]
        ///   [SansJP-Regular, SansJP-Thin]
        ///                             → [SansSC-Regular, SansJP-Regular, SansSC-Thin, SansJP-Thin]
        ///   [SansSC-Regular, SansJP-Regular]
        ///                             → 不变（顺序已正确）
        ///   [SerifJP-Regular]          → [SerifSC-Regular, SerifJP-Regular]
        ///   [SomeOtherFont]            → 不变（无 JP）
        ///
        /// 返回值：是否发生了变更。
        /// </summary>
        private static bool ReorderList(List<TMP_FontAsset> list, List<TMP_FontAsset> allScFonts)
        {
            if (list == null || list.Count == 0) return false;

            var newList = new List<TMP_FontAsset>(list.Count + 4);
            var added = new HashSet<TMP_FontAsset>();
            bool changed = false;

            foreach (var f in list)
            {
                if (f == null)
                {
                    newList.Add(null);
                    continue;
                }

                // 遇到 JP：先插入对应字重/家族的 SC（若尚未加入）
                if (IsJpFont(f.name))
                {
                    var matchSc = FindMatchingSc(f.name, allScFonts);
                    if (matchSc != null && !added.Contains(matchSc))
                    {
                        newList.Add(matchSc);
                        added.Add(matchSc);
                        changed = true; // 插入了新元素
                    }
                }

                // 输出自身（已添加过则跳过 → 原列表有重复也会被清理）
                if (!added.Contains(f))
                {
                    newList.Add(f);
                    added.Add(f);
                }
                else
                {
                    changed = true; // 去掉了重复
                }
            }

            if (newList.Count != list.Count) changed = true;

            if (changed)
            {
                list.Clear();
                list.AddRange(newList);
            }
            return changed;
        }

        /// <summary>
        /// 根据 JP 字体的名字，从 SC 池里找到同家族、同字重的对应字体。
        ///
        /// 家族匹配：Sans ↔ Sans，Serif ↔ Serif
        /// 字重匹配：Regular ↔ Regular，Thin ↔ Thin，Bold ↔ Bold
        /// </summary>
        private static TMP_FontAsset FindMatchingSc(string jpName, List<TMP_FontAsset> scFonts)
        {
            if (string.IsNullOrEmpty(jpName)) return null;
            var jp = jpName.Replace(" ", "");

            bool isSans = jp.IndexOf("Sans", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isSerif = jp.IndexOf("Serif", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isSans && !isSerif) return null;

            // 提取字重关键词
            string weightKey = null;
            if (jp.IndexOf("Thin", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Thin";
            else if (jp.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Regular";
            else if (jp.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Bold";
            else if (jp.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) weightKey = "Light";

            // 家族关键词（对应 SC 的家族名）
            string familyKey = isSans ? "SansSC" : "SerifSC";

            foreach (var sc in scFonts)
            {
                if (sc == null) continue;
                var n = sc.name.Replace(" ", "");

                // 家族匹配
                if (n.IndexOf(familyKey, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // 字重匹配
                if (weightKey != null && n.IndexOf(weightKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sc;
            }

            return null;
        }

        // ============ 字体判定辅助 ============

        private static bool IsScFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Replace(" ", "");
            return n.IndexOf("NotoSansSC", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("NotoSerifSC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsJpFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.Replace(" ", "");
            return n.IndexOf("NotoSansJP", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("NotoSerifJP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ============ 缓存清理 + 强制刷新 ============

        private void ForceUpdateAllText()
        {
            try
            {
                TMP_ResourceManager.ClearFontAssetGlyphCache();
            }
            catch (Exception e)
            {
                Logger.LogWarning($"清除字形缓存失败：{e.Message}");
            }

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
    }
}