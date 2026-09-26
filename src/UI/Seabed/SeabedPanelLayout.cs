// ==================== 海床面板布局：扁平化迁移（纯逻辑，可单测） ====================
//
// 按 docs\方案-海床目录扁平化.md 执行：顶层目录 = 设置页签（常规/区域/面板/动画），
// **面板在 面板/ 下平铺、同级**，不再埋在 面板/小组件/ 与 面板/面板功能/ 两层里。
//
// 为什么单独成类：这是**会移动用户真实文件**的操作，必须能被测试钉住
// （只移动不删除、目标已存在则跳过、失败不阻塞启动且旧路径仍可用）。
//
// 判定规则（"哪个目录算面板"）不是新加的：现有加载器本来就有
// `if (!hasMain && !hasXaml) continue;` —— 只有配置子目录的组（如 面板/状态栏）因此天然被跳过。

using System;
using System.IO;
using System.Linq;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.UI.Seabed
{
    internal static class SeabedPanelLayout
    {
        /// <summary>面板所在的一级目录（与设置页签同名）。</summary>
        internal const string PanelsFolder = "面板";

        /// <summary>扁平化之前的两个两层分组目录（相对 <c>面板/</c>）。</summary>
        internal static readonly string[] LegacyGroups = { "小组件", "面板功能" };

        internal readonly record struct FlattenResult(int Moved, int Skipped, int Failed)
        {
            internal bool ChangedAnything => Moved > 0;
        }

        /// <summary>这个目录里有没有"会被编译的代码"（main.cs / *.cs / *.xaml）。</summary>
        internal static bool ContainsCode(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                return Directory.GetFiles(dir).Any(f =>
                    f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;   // 读不动就当没有（调用方据此跳过，不会误判成面板）
            }
        }

        /// <summary>
        /// 扁平化：把 <c>面板/&lt;旧分组&gt;/&lt;id&gt;</c> 上提到 <c>面板/&lt;id&gt;</c>。
        ///
        /// ★ 三条自我保护（照方案文档）：
        ///   ① 只**移动**，绝不删除、绝不覆盖；
        ///   ② **目标已存在则跳过**（上次搬过，或用户手工整理过）；
        ///   ③ 任一项失败**不抛**：记 Warning、保留原处 —— 加载器仍会扫旧路径，搬到一半也不会让面板消失。
        /// 旧分组目录在搬空后会被删掉（只在确实为空时），免得树里多出一层空目录。
        /// </summary>
        internal static FlattenResult Flatten(string root)
        {
            int moved = 0, skipped = 0, failed = 0;
            string panels = Path.Combine(root, PanelsFolder);
            if (!Directory.Exists(panels)) return new FlattenResult(0, 0, 0);

            foreach (string legacyGroup in LegacyGroups)
            {
                string legacyDir = Path.Combine(panels, legacyGroup);
                if (!Directory.Exists(legacyDir)) continue;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(legacyDir);
                }
                catch (Exception ex)
                {
                    failed++;
                    LogManager.Warning($"[海床] 面板扁平化：读不动旧分组目录（保留原处）：{legacyGroup} — {ex.Message}");
                    continue;
                }

                foreach (string child in children)
                {
                    string name = Path.GetFileName(child);
                    string target = Path.Combine(panels, name);
                    if (Directory.Exists(target) || File.Exists(target)) { skipped++; continue; }

                    try
                    {
                        Directory.Move(child, target);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LogManager.Warning($"[海床] 面板扁平化：搬不动「{legacyGroup}/{name}」（保留原处，仍会被扫描）：{ex.Message}");
                    }
                }

                // 搬空了就把旧分组目录删掉（只在确实为空时；删不掉也无害，不动它）
                try
                {
                    if (Directory.Exists(legacyDir) && !Directory.EnumerateFileSystemEntries(legacyDir).Any())
                        Directory.Delete(legacyDir);
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"[海床] 面板扁平化：旧的空分组目录删不掉（无害）：{legacyGroup} — {ex.Message}");
                }
            }

            if (moved > 0 || failed > 0)
                LogManager.Info($"[海床] 面板扁平化：上提 {moved} 个面板到 {PanelsFolder}/，跳过 {skipped} 个（目标已存在），失败 {failed} 个");

            return new FlattenResult(moved, skipped, failed);
        }
    }
}
