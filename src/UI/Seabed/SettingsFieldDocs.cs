using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// SettingsData 字段 → 中文说明字典（供海床"配置代码"生成逐字段注释）。
    /// 区域类字段（Region_*/RegionPanel_*/UserWidth_*/UserHeight_*/EdgeMode_*/FixedShape_*/FixedOffset_*/Edge_*/Corner_*）
    /// 由模式规则生成说明，其余为精确字典。
    /// ★ 防漂移：ConfigTreeCoverageTests / SettingsFieldDocsTests 保证树里每个叶子字段都有说明。
    /// </summary>
    public static class SettingsFieldDocs
    {
        // ========== 精确字段说明（特例优先） ==========
        private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
        {
            // ---- 面板设计 · 自适应与固定位置 ----
            { "AutoFitOnTrigger", "触发时自适应面板尺寸" },
            { "UseAutoSize", "使用自动尺寸（按内容自适应）" },
            { "HorizontalLayoutThreshold", "水平布局阈值（宽高比）" },
            { "TagWidth", "标签宽度（px）" },
            // ---- 面板设计 · 任务栏 ----
            { "TaskbarIconSize", "任务栏图标大小（px）" },
            { "DividerOffset", "分隔线位置比例（0-1）" },
            // ---- 面板设计 · 勿扰模式 ----
            { "DndModeEnabled", "勿扰模式启用（隐藏通知等打扰）" },
            { "RememberDndMode", "记住勿扰模式开关状态" },
            // ---- 剪贴板 ----
            { "ClipboardMaxCount", "剪贴板历史最大条数" },
            { "ClipboardDisplayLength", "剪贴板单条最多显示行数（1–20）" },
            { "ClipboardImageMaxWidth", "剪贴板图片最长边（px，0=不缩放）" },
            { "ClipboardImageCacheLimitMB", "剪贴板图片缓存总大小上限（MB）" },
            // ---- 便签 ----
            { "DefaultNoteColor", "便签默认颜色（#RRGGBB）" },
            { "NoteShowTitleByDefault", "便签默认显示标题" },
            { "LastWidgetTab", "上次选中的小组件页签" },
            // ---- 动画（旧版兼容 + 总开关） ----
            { "ShowHideEasingType", "旧版呼出/隐藏缓动（兼容字段）" },
            { "ShowHideDurationMs", "旧版呼出/隐藏时长 ms（兼容字段）" },
            { "AnimationsEnabled", "动画总开关" },
            // ---- 触发动画 ----
            { "ShowAnimationType", "触发动画类型（Fade/Slide/Zoom/Elastic/Custom）" },
            { "ShowAnimationDurationMs", "触发动画时长（ms）" },
            { "ShowAnimationZoomFrom", "触发缩放起始比例（Zoom 动画）" },
            { "ShowAnimationOscillations", "触发弹性振荡次数（Elastic 动画）" },
            { "ShowAnimationSpringiness", "触发弹性强度（Elastic 动画）" },
            // ---- 隐藏动画 ----
            { "HideAnimationType", "隐藏动画类型（Fade/Slide/Zoom/Elastic/Custom）" },
            { "HideAnimationDurationMs", "隐藏动画时长（ms）" },
            { "HideAnimationZoomTo", "隐藏缩放目标比例（Zoom 动画）" },
            { "HideAnimationOscillations", "隐藏弹性振荡次数（Elastic 动画）" },
            { "HideAnimationSpringiness", "隐藏弹性强度（Elastic 动画）" },
            // ---- 尺寸形变 / 跟随飞行 / 隐藏延迟 ----
            { "TransformEasingType", "尺寸形变缓动（CubicEase 等）" },
            { "TransformDurationMs", "尺寸形变时长（ms）" },
            { "FlyDurationMs", "跟随/飞行动画时长（ms）" },
            { "HideDelayMs", "隐藏延迟（ms）" },
            // ---- 引潮 ----
            { "ClingModeEnabled", "引潮模式启用" },
            { "SnapRangePx", "贴边吸附范围 px（0=关闭）" },
            { "ContentStabilizeMs", "内容切换稳定防抖（ms）" },
            // ---- 穿透 / 防抖 / 性能 ----
            { "PassthroughModifier", "点击穿透修饰键（None/Ctrl/Alt/Shift）" },
            { "RegionDebounceMs", "区域切换防抖延迟（ms）" },
            { "PerformanceMode", "性能模式（Smooth/Normal/PowerSaver）" },
            { "PanelFrameRate", "面板运行帧率（fps，0=自动满帧；30/60/90/120 手动）" },
            // ---- 触发距离与延时 ----
            { "TriggerDistancePx", "边缘触发距离 px（越小越难误触）" },
            { "TriggerDelayMs", "触发延时 ms（0=立即）" },
            // ---- 外观 ----
            { "BackgroundColor", "面板背景色（#RRGGBB）" },
            { "TextColor", "文本颜色（#RRGGBB）" },
            { "Opacity", "面板不透明度（0-1）" },
            { "CornerRadius", "面板圆角半径（px）" },
              { "UiFontScale", "全局界面字号缩放系数（0.75~1.5，1.0=默认）" },
            // ---- 形状参数 ----
            { "StripLengthRatio", "条状面板长度占比（0-1）" },
            { "StripWidthMultiplier", "条状面板宽度倍数" },
            { "SquareShortSideMultiplier", "方形面板短边倍数" },
            { "GoldenRatio", "黄金比例（1.618）" },
            { "TriggerRegionRatio", "触发区占边缘比例（0-1）" },
            // ---- 状态栏显示项 ----
            { "ShowSystemStatus", "显示系统状态栏" },
            { "StatusShowTime", "状态栏显示时间" },
            { "StatusShowCpu", "状态栏显示 CPU" },
            { "StatusShowMemory", "状态栏显示内存" },
            { "StatusShowFps", "状态栏显示 FPS" },
            { "StatusShowVolume", "状态栏显示音量" },
            { "StatusShowNetwork", "状态栏显示网络" },
            { "StatusShowBattery", "状态栏显示电池" },
            { "StatusShowWeather", "状态栏显示天气" },
            // ---- 天气 ----
            { "WeatherEnabled", "天气总开关" },
            { "WeatherCity", "天气城市（空=按 IP 自动定位）" },
            // ---- 划词翻译 ----
            { "TextAiHotkey", "划词翻译热键（如 Ctrl+Alt+Q，空=未设置）" },
            { "TextAiTargetLanguage", "划词翻译：目标语言（空=自动判中英）" },
            { "TextAiHistoryLimit", "划词翻译：历史记录条数（0=不记录）" },
            // ---- 小组件开关 ----
            { "WidgetEnabled_Clipboard", "剪贴板小组件启用" },
            { "ClipboardKeyboardNav", "剪贴板：键盘操作（↑↓/Enter/数字键/Del）" },
            { "ClipboardShowSourceApp", "剪贴板：显示来源应用" },
            { "WidgetEnabled_Note", "便签小组件启用" },
            { "NoteHotkeyNew", "便签快捷键：新建（面板内生效）" },
            { "NoteHotkeyDelete", "便签快捷键：删除当前（面板内生效）" },
            { "NoteHotkeyNext", "便签快捷键：切到下一个（面板内生效）" },
            { "WidgetEnabled_Timer", "计时器小组件启用" },
            { "WidgetEnabled_Calculator", "计算器小组件启用" },
            { "CalculatorHistoryLimit", "计算器：历史记录条数（0=不记录）" },
            { "WidgetEnabled_TextAi", "划词翻译小组件启用" },
            { "WidgetEnabled_Web", "网页小组件启用（开箱不联网，需用户主动开启）" },
            { "WebWidgetUrl", "网页小组件默认地址（WebView2）" },
        };

        // ========== 区域名 → 中文 ==========
        private static readonly Dictionary<string, string> RegionNames = new(StringComparer.Ordinal)
        {
            { "Top_Left", "上边缘·左区" }, { "Top_Center", "上边缘·中区" }, { "Top_Right", "上边缘·右区" },
            { "Bottom_Left", "下边缘·左区" }, { "Bottom_Center", "下边缘·中区" }, { "Bottom_Right", "下边缘·右区" },
            { "Left_Top", "左边缘·上区" }, { "Left_Center", "左边缘·中区" }, { "Left_Bottom", "左边缘·下区" },
            { "Right_Top", "右边缘·上区" }, { "Right_Center", "右边缘·中区" }, { "Right_Bottom", "右边缘·下区" },
            { "TopLeft", "左上角" }, { "TopRight", "右上角" }, { "BottomLeft", "左下角" }, { "BottomRight", "右下角" },
        };

        // ========== 边缘名 → 中文 ==========
        private static readonly Dictionary<string, string> SideNames = new(StringComparer.Ordinal)
        {
            { "Top", "上" }, { "Bottom", "下" }, { "Left", "左" }, { "Right", "右" },
        };

        // ========== 角落名 → 中文 ==========
        private static readonly Dictionary<string, string> CornerNames = new(StringComparer.Ordinal)
        {
            { "TopLeft", "左上" }, { "TopRight", "右上" }, { "BottomLeft", "左下" }, { "BottomRight", "右下" },
        };

        // ========== 模式规则 ==========
        private static readonly (Regex Pattern, Func<Match, string> Build)[] Patterns =
        {
            // Region_Top_Left → 区域形状
            (new Regex(@"^Region_(Top_Left|Top_Center|Top_Right|Bottom_Left|Bottom_Center|Bottom_Right|Left_Top|Left_Center|Left_Bottom|Right_Top|Right_Center|Right_Bottom)$", RegexOptions.Compiled),
             m => $"{RegionNames[m.Groups[1].Value]} 形状类型（Default/条/方）"),
            // RegionPanel_Top_Left / RegionPanel_TopLeft → 区域面板类型
            (new Regex(@"^RegionPanel_(Top_Left|Top_Center|Top_Right|Bottom_Left|Bottom_Center|Bottom_Right|Left_Top|Left_Center|Left_Bottom|Right_Top|Right_Center|Right_Bottom|TopLeft|TopRight|BottomLeft|BottomRight)$", RegexOptions.Compiled),
             m => $"{RegionNames[m.Groups[1].Value]} 面板类型（Default=跟随默认布局）"),
            // UserWidth_* / UserHeight_* → 区域面板尺寸
            (new Regex(@"^User(Width|Height)_(Top_Left|Top_Center|Top_Right|Bottom_Left|Bottom_Center|Bottom_Right|Left_Top|Left_Center|Left_Bottom|Right_Top|Right_Center|Right_Bottom|Corner_TopLeft|Corner_TopRight|Corner_BottomLeft|Corner_BottomRight)$", RegexOptions.Compiled),
             m => $"{RegionName(m.Groups[2].Value)} 面板{(m.Groups[1].Value == "Width" ? "宽度" : "高度")}（px，0=自动）"),
            // EdgeMode_Top → 边缘模式
            (new Regex(@"^EdgeMode_(Top|Bottom|Left|Right)$", RegexOptions.Compiled),
             m => $"{SideNames[m.Groups[1].Value]}边缘模式（Follow=跟随鼠标/Fixed=固定贴边）"),
            // FixedShape_Top → 固定形状
            (new Regex(@"^FixedShape_(Top|Bottom|Left|Right)$", RegexOptions.Compiled),
             m => $"{SideNames[m.Groups[1].Value]}边缘固定形状（Square/Rect/Strip…）"),
            // FixedOffset_Top → 固定偏移
            (new Regex(@"^FixedOffset_(Top|Bottom|Left|Right)$", RegexOptions.Compiled),
             m => $"{SideNames[m.Groups[1].Value]}边缘固定位置偏移（px）"),
            // Edge_Top → 边缘开关
            (new Regex(@"^Edge_(Top|Bottom|Left|Right)$", RegexOptions.Compiled),
             m => $"{SideNames[m.Groups[1].Value]}边缘启用（滑过呼出）"),
            // Corner_TopLeft → 角落开关
            (new Regex(@"^Corner_(TopLeft|TopRight|BottomLeft|BottomRight)$", RegexOptions.Compiled),
             m => $"{CornerNames[m.Groups[1].Value]}角落启用（滑过呼出）"),
        };

        /// <summary>取字段中文说明；无说明返回 null（防漂移测试会兜底）。</summary>
        public static string? TryGet(string fieldName)
        {
            if (Exact.TryGetValue(fieldName, out var doc)) return doc;
            foreach (var (pattern, build) in Patterns)
            {
                var m = pattern.Match(fieldName);
                if (m.Success) return build(m);
            }
            return null;
        }

        /// <summary>取字段中文说明；无说明时回退为字段名本身（生成代码不中断）。</summary>
        public static string DocOrName(string fieldName) => TryGet(fieldName) ?? fieldName;

        /// <summary>区域名（含 Corner_ 前缀兼容）。</summary>
        private static string RegionName(string region)
        {
            string key = region.StartsWith("Corner_", StringComparison.Ordinal) ? region.Substring(7) : region;
            return RegionNames.TryGetValue(key, out var cn) ? cn : region;
        }
    }
}
