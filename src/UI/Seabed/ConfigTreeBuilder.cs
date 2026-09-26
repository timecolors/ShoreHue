using System.Collections.Generic;
using ShoreHue.Core.Models;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 构建海床配置树：一级分类 = 设置窗口页签（常规/区域/面板/动画），与文件夹目录同构——
    /// 文件夹里放什么，树里就看到什么；树的配置节点归属严格按设置面板页签内容分类。
    /// 叶子节点 Key 与 SettingsData 字段名保持稳定（不随分组移动变化，预设/模板/变灰映射依赖它）。
    /// </summary>
    public static class ConfigTreeBuilder
    {
        public static ConfigNode Build()
        {
            var root = new ConfigNode { Key = "root", Name = "海床" };

            root.Children.Add(BuildGeneral());   // 设置页签：常规
            root.Children.Add(BuildRegion());    // 设置页签：区域
            root.Children.Add(BuildPanel());     // 设置页签：面板
            root.Children.Add(BuildAnimation()); // 设置页签：动画

            return root;
        }

        /// <summary>按 Key 查找节点（含子级递归）；未找到返回 null。用于预设覆盖 → 字段映射。</summary>
        public static ConfigNode? FindNodeByKey(string key)
        {
            var root = Build();
            return FindRecursive(root, key);
        }

        /// <summary>按 Key 返回节点路径的名称链（如 [区域, 触发与位置]）；未找到返回空。用于树↔文件夹映射。
        /// ★ **不含根节点名**：根「海床」= seabed 目录本身那一层（资源管理器的根），不是一层文件夹。
        ///   含根会让落盘路径多一级（seabed/海床/区域/…），扫描与读取都按 seabed/&lt;分类&gt;/… 找 → 节点存了却读不回来。</summary>
        public static System.Collections.Generic.List<string> FindPathNames(string key)
        {
            var result = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(key)) return result;
            var root = Build();
            if (!FindPathRecursive(root, key, result)) return new System.Collections.Generic.List<string>();
            result.RemoveAt(0);   // 去掉根：树根对应 seabed 目录本身（key="root" → 返回空，调用方走分类目录兜底）
            return result;
        }

        private static bool FindPathRecursive(ConfigNode n, string key, System.Collections.Generic.List<string> path)
        {
            path.Add(n.Name);
            if (n.Key == key) return true;
            foreach (var c in n.Children)
            {
                if (FindPathRecursive(c, key, path)) return true;
            }
            path.RemoveAt(path.Count - 1);
            return false;
        }

        private static ConfigNode? FindRecursive(ConfigNode n, string key)
        {
            if (n.Key == key) return n;
            foreach (var c in n.Children)
            {
                var r = FindRecursive(c, key);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>收集节点自身 + 所有子级的字段名（用于预设覆盖 → 设置控件变灰）。</summary>
        public static void CollectFields(ConfigNode node, System.Collections.Generic.HashSet<string> into)
        {
            if (node.FieldNames != null)
            {
                foreach (var f in node.FieldNames) into.Add(f);
            }
            foreach (var c in node.Children) CollectFields(c, into);
        }

        /// <summary>根据字段名找到其所属节点链（叶子 → 二级 → 一级）。用于预设应用时定位冲突来源。</summary>
        public static System.Collections.Generic.List<ConfigNode> FindNodeChain(string fieldName)
        {
            var result = new System.Collections.Generic.List<ConfigNode>();
            var root = Build();
            foreach (var c1 in root.Children)
            {
                foreach (var c2 in c1.Children)
                {
                    if (c2.FieldNames.Contains(fieldName))
                    {
                        result.Add(c1);
                        result.Add(c2);
                        return result;
                    }
                    foreach (var c3 in c2.Children)
                    {
                        if (c3.FieldNames.Contains(fieldName))
                        {
                            result.Add(c1);
                            result.Add(c2);
                            result.Add(c3);
                            return result;
                        }
                    }
                }
            }
            return result;
        }

        private static ConfigNode Leaf(string key, string name, string category, params string[] fields)
        {
            var n = new ConfigNode { Key = key, Name = name, Category = category };
            n.FieldNames.AddRange(fields);
            return n;
        }

        // ========== 设置页签：常规（语言/更新/关于无配置叶子；含勿扰与性能帧率） ==========
        private static ConfigNode BuildGeneral()
        {
            var c = new ConfigNode { Key = "general", Name = "常规", Category = "常规" };
            c.Children.Add(Leaf("panel-dnd", "勿扰模式", "常规",
                "DndModeEnabled", "RememberDndMode"));
            c.Children.Add(Leaf("inter-perf", "性能模式与帧率", "常规",
                "PerformanceMode", "PanelFrameRate"));
            return c;
        }

        // ========== 设置页签：区域（触发与位置 / 区域面板 / 触发行为 / 高级） ==========
        private static ConfigNode BuildRegion()
        {
            var c = new ConfigNode { Key = "region", Name = "区域", Category = "区域" };

            // 页签分区「触发与位置」：边/角开关 + 边行为模式 + 固定形状/偏移 + 区域形状
            c.Children.Add(Leaf("panel-edges", "触发与位置", "区域",
                "Edge_Top", "Edge_Bottom", "Edge_Left", "Edge_Right",
                "Corner_TopLeft", "Corner_TopRight", "Corner_BottomLeft", "Corner_BottomRight",
                "EdgeMode_Top", "EdgeMode_Bottom", "EdgeMode_Left", "EdgeMode_Right",
                "FixedShape_Top", "FixedShape_Bottom", "FixedShape_Left", "FixedShape_Right",
                "FixedOffset_Top", "FixedOffset_Bottom", "FixedOffset_Left", "FixedOffset_Right",
                "Region_Top_Left", "Region_Top_Center", "Region_Top_Right",
                "Region_Bottom_Left", "Region_Bottom_Center", "Region_Bottom_Right",
                "Region_Left_Top", "Region_Left_Center", "Region_Left_Bottom",
                "Region_Right_Top", "Region_Right_Center", "Region_Right_Bottom"));

            // 页签分区「区域面板」：16 区域面板类型 + 16 区域尺寸
            c.Children.Add(Leaf("panel-regions", "区域面板类型", "区域",
                "RegionPanel_Top_Left", "RegionPanel_Top_Center", "RegionPanel_Top_Right",
                "RegionPanel_Bottom_Left", "RegionPanel_Bottom_Center", "RegionPanel_Bottom_Right",
                "RegionPanel_Left_Top", "RegionPanel_Left_Center", "RegionPanel_Left_Bottom",
                "RegionPanel_Right_Top", "RegionPanel_Right_Center", "RegionPanel_Right_Bottom",
                "RegionPanel_TopLeft", "RegionPanel_TopRight", "RegionPanel_BottomLeft", "RegionPanel_BottomRight"));
            c.Children.Add(Leaf("panel-sizes", "区域尺寸（各区域）", "区域",
                "UserWidth_Top_Left", "UserHeight_Top_Left", "UserWidth_Top_Center", "UserHeight_Top_Center",
                "UserWidth_Top_Right", "UserHeight_Top_Right", "UserWidth_Bottom_Left", "UserHeight_Bottom_Left",
                "UserWidth_Bottom_Center", "UserHeight_Bottom_Center", "UserWidth_Bottom_Right", "UserHeight_Bottom_Right",
                "UserWidth_Left_Top", "UserHeight_Left_Top", "UserWidth_Left_Center", "UserHeight_Left_Center",
                "UserWidth_Left_Bottom", "UserHeight_Left_Bottom", "UserWidth_Right_Top", "UserHeight_Right_Top",
                "UserWidth_Right_Center", "UserHeight_Right_Center", "UserWidth_Right_Bottom", "UserHeight_Right_Bottom",
                "UserWidth_Corner_TopLeft", "UserHeight_Corner_TopLeft", "UserWidth_Corner_TopRight", "UserHeight_Corner_TopRight",
                "UserWidth_Corner_BottomLeft", "UserHeight_Corner_BottomLeft", "UserWidth_Corner_BottomRight", "UserHeight_Corner_BottomRight"));

            // 页签分区「触发行为」：触发距离/延时/防抖（RegionTriggerDelay/HideDelay 是运行时字典，白名单）
            c.Children.Add(Leaf("inter-trigger", "触发行为", "区域",
                "TriggerDistancePx", "TriggerDelayMs", "RegionDebounceMs"));

            // 页签分区「高级」：自适应 / 引潮（吸附）/ 穿透
            c.Children.Add(Leaf("region-advanced", "高级设置", "区域",
                "AutoFitOnTrigger", "UseAutoSize",
                "ClingModeEnabled", "SnapRangePx",
                "PassthroughModifier"));
            return c;
        }

        // ========== 设置页签：面板（外观 / 任务栏 / 小组件 / 面板功能 / 状态栏） ==========
        private static ConfigNode BuildPanel()
        {
            var c = new ConfigNode { Key = "panel", Name = "面板", Category = "面板" };

            // 页签分区「外观」：颜色主题 + 形状参数
            c.Children.Add(Leaf("appr-theme", "外观", "面板",
                "BackgroundColor", "TextColor", "Opacity", "CornerRadius", "UiFontScale"));
            c.Children.Add(Leaf("appr-shape", "形状参数", "面板",
                "StripLengthRatio", "StripWidthMultiplier", "SquareShortSideMultiplier",
                "GoldenRatio", "TriggerRegionRatio"));

            // 页签分区「任务栏」
            c.Children.Add(Leaf("panel-taskbar", "任务栏", "面板",
                "TaskbarIconSize", "DividerOffset", "TagWidth", "HorizontalLayoutThreshold"));

            // 页签分区「小组件」：启停开关 + 各小组件参数（剪贴板/便签参数在小组件详情里编辑）
            var widget = new ConfigNode { Key = "panel-widgets", Name = "小组件", Category = "面板" };
            widget.Children.Add(Leaf("widget-clipboard", "剪贴板", "面板",
                "WidgetEnabled_Clipboard", "ClipboardMaxCount", "ClipboardDisplayLength",
                "ClipboardImageMaxWidth", "ClipboardImageCacheLimitMB", "ClipboardKeyboardNav", "ClipboardShowSourceApp", "LastWidgetTab"));
            widget.Children.Add(Leaf("widget-note", "便签", "面板",
                "WidgetEnabled_Note", "DefaultNoteColor", "NoteShowTitleByDefault",
                "NoteHotkeyNew", "NoteHotkeyDelete", "NoteHotkeyNext"));
            widget.Children.Add(Leaf("widget-timer", "计时器", "面板", "WidgetEnabled_Timer"));
            widget.Children.Add(Leaf("widget-calculator", "计算器", "面板", "WidgetEnabled_Calculator", "CalculatorHistoryLimit"));
            widget.Children.Add(Leaf("widget-textai", "划词翻译", "面板",
                "WidgetEnabled_TextAi", "TextAiHotkey", "TextAiTargetLanguage", "TextAiHistoryLimit"));
            widget.Children.Add(Leaf("widget-web", "网页工具", "面板", "WidgetEnabled_Web", "WebWidgetUrl"));
            c.Children.Add(widget);

            // 页签分区「面板功能」：可源码化面板（保存为 Panel 变体进区域面板下拉）
            var features = new ConfigNode { Key = "panel-features", Name = "面板功能", Category = "面板" };
            features.Children.Add(Leaf("panel-notification", "通知坞", "面板"));
            features.Children.Add(Leaf("panel-recent", "最近使用", "面板"));
            features.Children.Add(Leaf("panel-quicksettings", "快捷设置", "面板"));
            features.Children.Add(Leaf("panel-taskbar-feature", "任务栏", "面板"));
            features.Children.Add(Leaf("panel-ai", "AI 助手", "面板"));
            features.Children.Add(Leaf("panel-windowcontrol", "窗口控制", "面板"));
            c.Children.Add(features);

            // 页签分区「状态栏」（状态栏显示项/天气在面板页签内编辑）
            var status = new ConfigNode { Key = "status", Name = "状态栏", Category = "面板" };
            status.Children.Add(Leaf("status-items", "显示项", "面板",
                "ShowSystemStatus", "StatusShowTime", "StatusShowCpu", "StatusShowMemory",
                "StatusShowFps", "StatusShowVolume", "StatusShowNetwork", "StatusShowBattery", "StatusShowWeather"));
            status.Children.Add(Leaf("status-weather", "天气", "面板",
                "WeatherEnabled", "WeatherCity"));
            c.Children.Add(status);
            return c;
        }

        // ========== 设置页签：动画（触发/隐藏/形变/飞行/稳定/延时隐藏/总开关） ==========
        private static ConfigNode BuildAnimation()
        {
            var c = new ConfigNode { Key = "anim", Name = "动画", Category = "动画" };
            c.Children.Add(Leaf("anim-show", "触发动画", "动画",
                "ShowAnimationType", "ShowAnimationDurationMs", "ShowAnimationZoomFrom",
                "ShowAnimationOscillations", "ShowAnimationSpringiness"));
            c.Children.Add(Leaf("anim-hide", "隐藏动画", "动画",
                "HideAnimationType", "HideAnimationDurationMs", "HideAnimationZoomTo",
                "HideAnimationOscillations", "HideAnimationSpringiness"));
            c.Children.Add(Leaf("anim-transform", "尺寸形变", "动画",
                "TransformEasingType", "TransformDurationMs"));
            c.Children.Add(Leaf("anim-fly", "跟随 / 飞行", "动画", "FlyDurationMs"));
            c.Children.Add(Leaf("anim-stabilize", "内容稳定", "动画", "ContentStabilizeMs"));
            c.Children.Add(Leaf("anim-hidedelay", "延时隐藏", "动画", "HideDelayMs"));
            c.Children.Add(Leaf("anim-master", "总开关与旧兼容", "动画",
                "AnimationsEnabled", "ShowHideEasingType", "ShowHideDurationMs"));
            return c;
        }
    }
}
