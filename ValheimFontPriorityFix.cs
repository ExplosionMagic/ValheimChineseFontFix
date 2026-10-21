using BepInEx;
using System;

//using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Resources;
using TMPro;
using UnityEngine;

namespace ValheimFontPriorityFix
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public sealed class FontPriorityFixPlugin : BaseUnityPlugin
    {
        public const string PluginId = "dev.valheim-cn.font-priority-fix";
        public const string PluginName = "Valheim Chinese Font Priority Fix";
        public const string PluginVersion = "0.1.0";

        private void Awake()
        {
            // 使用协程等待游戏资源加载完毕
            StartCoroutine(AdjustFallbackPriority());
        }

        private IEnumerator AdjustFallbackPriority()
        {
            yield return new WaitForSecondsRealtime(5f); // 确保字体加载

            var allAssets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

            // 按家族分组查找字体（忽略大小写和空格）
            TMP_FontAsset FindAsset(string keyword) => allAssets
                .FirstOrDefault(f => f.name.Replace(" ", "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

            // 查找所有相关的JP和SC字体
            var jpSans = FindAsset("NotoSansJP-Regular");
            var scSans = FindAsset("NotoSansSC-Regular");
            var jpSansThin = FindAsset("NotoSansJP-Thin");
            var scSansThin = FindAsset("NotoSansSC-Thin");
            var jpSerif = FindAsset("NotoSerifJP-Regular");
            var scSerif = FindAsset("NotoSerifSC-Regular");

            if (jpSans == null || scSans == null || jpSerif == null || scSerif == null)
            {
                Logger.LogError("未能找到核心的JP/SC字体对，修复中止。");
                yield break;
            }
            Logger.LogInfo($"找到字体对: Sans(JP={jpSans.name}, SC={scSans.name}), Serif(JP={jpSerif.name}, SC={scSerif.name})");

            // ========== 1. 处理全局回退列表 ==========
            var globalList = TMP_Settings.fallbackFontAssets;
            if (globalList != null)
            {
                // 将SC字体移到全局列表最前面
                globalList.Remove(scSans);
                globalList.Insert(0, scSans);
                globalList.Remove(scSerif);
                globalList.Insert(0, scSerif);
                Logger.LogInfo($"已确保 {scSans.name} 和 {scSerif.name} 位于全局回退列表前端。");
            }

            // ========== 2. 处理所有字体资产的本地回退列表 ==========
            int fixedCount = 0;
            foreach (var asset in allAssets)
            {
                if (asset?.fallbackFontAssetTable == null) continue;
                var localList = asset.fallbackFontAssetTable;
                bool changed = false;

                // 为Sans家族调整顺序
                if (localList.Contains(jpSans) && !localList.Contains(scSans))
                {
                    localList.Insert(localList.IndexOf(jpSans), scSans); // 插到JP前面
                    changed = true;
                }
                // 如果JP和SC都在列表里，确保SC排在JP前面
                else if (localList.Contains(jpSans) && localList.Contains(scSans) &&
                         localList.IndexOf(scSans) > localList.IndexOf(jpSans))
                {
                    localList.Remove(scSans);
                    localList.Insert(localList.IndexOf(jpSans), scSans);
                    changed = true;
                }

                // 为Serif家族调整顺序
                if (localList.Contains(jpSerif) && !localList.Contains(scSerif))
                {
                    localList.Insert(localList.IndexOf(jpSerif), scSerif);
                    changed = true;
                }
                else if (localList.Contains(jpSerif) && localList.Contains(scSerif) &&
                         localList.IndexOf(scSerif) > localList.IndexOf(jpSerif))
                {
                    localList.Remove(scSerif);
                    localList.Insert(localList.IndexOf(jpSerif), scSerif);
                    changed = true;
                }

                if (changed)
                {
                    fixedCount++;
                    Logger.LogInfo($"  修正本地回退: {asset.name} 的SC字体已调整至JP字体之前。");
                }
            }
            Logger.LogInfo($"共调整了 {fixedCount} 个字体资产的本地回退列表。");

            // ========== 3. 清理缓存并强制刷新 ==========
            TMP_ResourceManager.ClearFontAssetGlyphCache();
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null) continue;
                text.SetAllDirty();
                text.ForceMeshUpdate();
            }
            Logger.LogInfo("已清除字形缓存并强制刷新所有文本组件。");
        }

        /// <summary>
        /// 清除TMP字形缓存，并强制所有TMP_Text组件重新生成网格。
        /// </summary>
        private void ForceUpdateAllText()
        {
            // 清除TMP内部资源管理器的字形缓存
            TMP_ResourceManager.ClearFontAssetGlyphCache();

            // 强制场景中所有TMP_Text组件立即刷新
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text != null)
                {
                    text.SetAllDirty();
                    text.ForceMeshUpdate();
                }
            }
            Logger.LogInfo("已清除TMP字形缓存并强制刷新所有文本组件。");
        }
    }
}