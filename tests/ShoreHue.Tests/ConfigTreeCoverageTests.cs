using ShoreHue.Core.Models;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.UI.Seabed;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 配置树 ↔ SettingsData 双向防漂移：
/// 1) 树里的每个叶子字段名必须是 SettingsData 的真实属性（已有 BuildConfigCode 编译测试）；
/// 2) SettingsData 的每个属性必须出现在配置树里（或属于结构/运行时字段白名单）——
///    保证"所有设置都能在海床里配置/生成代码"（用户要求：所有设置都做好）。
/// 新增设置属性但忘记挂进树时，本测试会失败。
/// </summary>
public class ConfigTreeCoverageTests
{
    /// <summary>结构/运行时字段（不进配置树）：语言、编程模式数据、逐区域字典、开关状态等。</summary>
    private static readonly HashSet<string> Whitelist = new(StringComparer.Ordinal)
    {
        "Language",
        "ProgrammingModeEnabled", "CustomPanels", "AppliedPresets",
        "RegionTriggerDelay", "RegionHideDelay", "RegionAnimationOverrides",
        "WidgetPluginOverrides", "StatusProviderEnabled", "WeatherRecentCities",
        "AutoCheckUpdate", "OnboardingCompleted",
        "ShowHideEasingType", "ShowHideDurationMs",   // 旧版兼容字段（anim-master 已含，保留白名单）
        "LastWidgetTab",                               // status-note 已含；保留以防重复挂载
        "WeatherEnabled",                              // 天气总开关（status-weather 未显式列出开关字段，仅城市）
        "WebWidgetUrl",                                 // 网页小组件地址（WebView2 运行时字段）
        "RegionHotkeysEnabled", "RegionHotkeyModifier",   // 键盘呼出区域面板（运行时热键配置，不进配置树）
        "WebBookmarks",                                 // 网页工具收藏列表（设置页管理）
        "WidgetEnabled_Web",                            // 网页工具开关（联网功能默认关）
        "TrustedPlugins",                               // 插件信任库（id→内容哈希，安全 v2 运行时数据，不进配置树）
            "CalculatorHistoryJson",                       // 计算历史的存储载体（借设置落盘）；用户只配 CalculatorHistoryLimit
            "TextAiHistoryJson",                           // 划词翻译历史的存储载体（用户只配 TextAiHistoryLimit）
            "TaskbarGroupsJson",                           // 任务栏标签分组的存储载体（用户在任务栏里拖拽成组，不配字段）
            "CircuitBrokenPlugins", "SafeModeRequested", "UncleanExitCount",   // 插件运行时守卫（安全模式/熔断，系统数据）
    };

    [Fact]
    public void EverySettingsDataProperty_IsInTree_OrWhitelisted()
    {
        var root = ConfigTreeBuilder.Build();
        var treeFields = new HashSet<string>(StringComparer.Ordinal);
        void Collect(ConfigNode n)
        {
            foreach (var f in n.FieldNames) treeFields.Add(f);
            foreach (var c in n.Children) Collect(c);
        }
        Collect(root);

        var missing = new List<string>();
        foreach (var prop in typeof(SettingsData).GetProperties())
        {
            if (Whitelist.Contains(prop.Name)) continue;
            if (!treeFields.Contains(prop.Name)) missing.Add(prop.Name);
        }

        Assert.True(missing.Count == 0,
            "以下 SettingsData 属性未挂进配置树（新增设置请加入 ConfigTreeBuilder）：" + string.Join(", ", missing));
    }
}