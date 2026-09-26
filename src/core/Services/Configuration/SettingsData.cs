using System;
using System.Collections.Generic;

namespace ShoreHue.Core.Services.Configuration
{
    /// <summary>网页工具收藏项（设置页添加/删除，小组件地址栏下拉展示）。</summary>
    public sealed class WebBookmark
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Display => Name + " · " + Url;
    }

    public class SettingsData
    {
        // ========== 语言（zh-CN / en-US，空=跟随系统） ==========
        public string? Language { get; set; } = "";

        public bool Edge_Top { get; set; } = true;
        public bool Edge_Bottom { get; set; } = true;
        public bool Edge_Left { get; set; } = true;
        public bool Edge_Right { get; set; } = true;
        public bool Corner_TopLeft { get; set; } = true;
        public bool Corner_TopRight { get; set; } = true;
        public bool Corner_BottomLeft { get; set; } = true;
        public bool Corner_BottomRight { get; set; } = true;

        // ========== 形状参数 ==========
        public double StripLengthRatio { get; set; } = 0.6;
        public double StripWidthMultiplier { get; set; } = 1.5;
        public double SquareShortSideMultiplier { get; set; } = 1.8;
        public double GoldenRatio { get; set; } = 1.618;
        public double TriggerRegionRatio { get; set; } = 1.0 / 3.0;

        // ========== 边行为模式 ==========
        public string? EdgeMode_Top { get; set; } = "Follow";
        public string? EdgeMode_Bottom { get; set; } = "Follow";
        public string? EdgeMode_Left { get; set; } = "Follow";
        public string? EdgeMode_Right { get; set; } = "Follow";

        // ========== 外观 ==========
        public string? BackgroundColor { get; set; } = "#2D2D2D";
        public string? TextColor { get; set; } = "#FFFFFF";
        public double Opacity { get; set; } = 0.85;
        public int CornerRadius { get; set; } = 16;
        public bool ShowSystemStatus { get; set; } = true;
        /// <summary>网页小组件默认地址（WebView2，可随时在小组件地址栏换）。</summary>
        public string WebWidgetUrl { get; set; } = "https://www.bing.com";
        /// <summary>网页工具收藏列表（地址栏下拉项，设置页管理）。</summary>
        public List<WebBookmark> WebBookmarks { get; set; } = new();

        // ========== 动画与布局 ==========
        public double HorizontalLayoutThreshold { get; set; } = 3.0 / 7.0;
        public double TagWidth { get; set; } = 120;

        // ========== 自适应行为 ==========
        public bool AutoFitOnTrigger { get; set; } = true;

        // ========== 固定位置 ==========
        public string? FixedShape_Top { get; set; } = "Square";
        public string? FixedShape_Bottom { get; set; } = "Square";
        public string? FixedShape_Left { get; set; } = "Square";
        public string? FixedShape_Right { get; set; } = "Square";
        public double FixedOffset_Top { get; set; } = 0;
        public double FixedOffset_Bottom { get; set; } = 0;
        public double FixedOffset_Left { get; set; } = 0;
        public double FixedOffset_Right { get; set; } = 0;

        // ========== 区域形状 ==========
        public string? Region_Top_Left { get; set; } = "Default";
        public string? Region_Top_Center { get; set; } = "Default";
        public string? Region_Top_Right { get; set; } = "Default";
        public string? Region_Bottom_Left { get; set; } = "Default";
        public string? Region_Bottom_Center { get; set; } = "Default";
        public string? Region_Bottom_Right { get; set; } = "Default";
        public string? Region_Left_Top { get; set; } = "Default";
        public string? Region_Left_Center { get; set; } = "Default";
        public string? Region_Left_Bottom { get; set; } = "Default";
        public string? Region_Right_Top { get; set; } = "Default";
        public string? Region_Right_Center { get; set; } = "Default";
        public string? Region_Right_Bottom { get; set; } = "Default";

        // ========== 剪贴板与便签 ==========
        public int ClipboardMaxCount { get; set; } = 10;
        // 剪贴板单条最多显示几行（1–20）。★ 旧语义是"最多显示多少字符"（默认 100），已废弃，见 SettingsManager
        public int ClipboardDisplayLength { get; set; } = 4;
        // 图片缩略化：最长边超过该值（px）时缩放后保存；0 = 不缩放
        public int ClipboardImageMaxWidth { get; set; } = 1280;
        // 图片缓存总大小上限（MB）：超限时按"未收藏且最旧"优先清理缓存文件
        public int ClipboardImageCacheLimitMB { get; set; } = 50;
        public string? LastWidgetTab { get; set; } = "Clipboard";
        public string? DefaultNoteColor { get; set; } = "#00000000";  // ★ 默认全透明 = 跟随面板背景（面板底色本身可配置，写死颜色早晚对不上）
        public bool NoteShowTitleByDefault { get; set; } = true;

        public bool UseAutoSize { get; set; } = true;

        // ========== 勿扰模式 ==========
        public bool RememberDndMode { get; set; } = false;
        public bool DndModeEnabled { get; set; } = false;

        // ========== 任务栏 ==========
        public double TaskbarIconSize { get; set; } = 28.0;
        public double DividerOffset { get; set; } = 0.4;

        // ========== 12个独立区域尺寸 ==========
        public double UserWidth_Top_Left { get; set; } = 0;
        public double UserHeight_Top_Left { get; set; } = 0;
        public double UserWidth_Top_Center { get; set; } = 0;
        public double UserHeight_Top_Center { get; set; } = 0;
        public double UserWidth_Top_Right { get; set; } = 0;
        public double UserHeight_Top_Right { get; set; } = 0;
        public double UserWidth_Bottom_Left { get; set; } = 0;
        public double UserHeight_Bottom_Left { get; set; } = 0;
        public double UserWidth_Bottom_Center { get; set; } = 0;
        public double UserHeight_Bottom_Center { get; set; } = 0;
        public double UserWidth_Bottom_Right { get; set; } = 0;
        public double UserHeight_Bottom_Right { get; set; } = 0;
        public double UserWidth_Left_Top { get; set; } = 0;
        public double UserHeight_Left_Top { get; set; } = 0;
        public double UserWidth_Left_Center { get; set; } = 0;
        public double UserHeight_Left_Center { get; set; } = 0;
        public double UserWidth_Left_Bottom { get; set; } = 0;
        public double UserHeight_Left_Bottom { get; set; } = 0;
        public double UserWidth_Right_Top { get; set; } = 0;
        public double UserHeight_Right_Top { get; set; } = 0;
        public double UserWidth_Right_Center { get; set; } = 0;
        public double UserHeight_Right_Center { get; set; } = 0;
        public double UserWidth_Right_Bottom { get; set; } = 0;
        public double UserHeight_Right_Bottom { get; set; } = 0;

        // ========== 四角区域尺寸 ==========
        public double UserWidth_Corner_TopLeft { get; set; } = 0;
        public double UserHeight_Corner_TopLeft { get; set; } = 0;
        public double UserWidth_Corner_TopRight { get; set; } = 0;
        public double UserHeight_Corner_TopRight { get; set; } = 0;
        public double UserWidth_Corner_BottomLeft { get; set; } = 0;
        public double UserHeight_Corner_BottomLeft { get; set; } = 0;
        public double UserWidth_Corner_BottomRight { get; set; } = 0;
        public double UserHeight_Corner_BottomRight { get; set; } = 0;

        // ========== 界面字号缩放 ==========
        /// <summary>全局界面字号缩放系数（0.75~1.5，1.0=默认）。</summary>
        public double UiFontScale { get; set; } = 1.0;

        // ========== 动画设置 ==========
        public bool AnimationsEnabled { get; set; } = true;
        // ★ 旧版"呼出/隐藏共用"字段（保留兼容；新字段为空/0 时迁移用）
        public string ShowHideEasingType { get; set; } = "CubicEase";
        public int ShowHideDurationMs { get; set; } = 150;
        // ★ 触发（呼出）动画：类型(Fade/Slide/Zoom/Elastic/Custom) + 时长 + 特化参数
        public string ShowAnimationType { get; set; } = "";    // 空=待迁移
        public int ShowAnimationDurationMs { get; set; } = 0;  // 0=待迁移
        public double ShowAnimationZoomFrom { get; set; } = 0.5;      // Zoom：起始比例
        public int ShowAnimationOscillations { get; set; } = 3;       // Elastic：振荡次数
        public double ShowAnimationSpringiness { get; set; } = 3;     // Elastic：弹性强度
        // ★ 隐藏动画：类型 + 时长 + 特化参数
        public string HideAnimationType { get; set; } = "";    // 空=待迁移
        public int HideAnimationDurationMs { get; set; } = 0;  // 0=待迁移
        public double HideAnimationZoomTo { get; set; } = 0.5;        // Zoom：目标比例
        public int HideAnimationOscillations { get; set; } = 3;
        public double HideAnimationSpringiness { get; set; } = 3;
        public string TransformEasingType { get; set; } = "CubicEase";
        public int TransformDurationMs { get; set; } = 250;
        public int HideDelayMs { get; set; } = 200;            // ★ 从 300 改为 200
        public int FlyDurationMs { get; set; } = 500;

        // ========== 引潮模式 ==========
        public bool ClingModeEnabled { get; set; } = false;

        // ========== 贴边吸附范围（px）：面板边缘距屏幕边小于该值 → 磁铁吸附贴边（0=关闭） ==========
        public int SnapRangePx { get; set; } = 30;

        // ========== 内容切换稳定防抖（ms）：切换后图标中置，稳定该时长后归位并显示内容 ==========
        public int ContentStabilizeMs { get; set; } = 400;

        // ========== 面板点击穿透修饰键（None / Ctrl / Alt / Shift）==========
        // 按住该键 + 点击可穿透面板，点击面板覆盖区域下方的屏幕内容
        public string? PassthroughModifier { get; set; } = "Ctrl";

        // ========== 区域防抖延迟 ==========
        public int RegionDebounceMs { get; set; } = 80;

        // ========== ShoreHue 性能模式（Smooth / Normal / PowerSaver） ==========
        // 一键预设：Smooth=面板动画全开更柔滑；Normal=平衡；PowerSaver=关动画最省电
        public string? PerformanceMode { get; set; } = "Normal";

        // ========== 面板运行帧率（fps：0=自动满帧，30/60/120 可选手动） ==========
        // 渲染帧跟随/引潮的目标帧率；0 = 跟随显示器刷新率（CompositionTarget.Rendering 满帧）。
        // 值越低越省 CPU（配合 PowerSaver 降帧），越高越顺滑（Smooth 建议 60/120）。
        public int PanelFrameRate { get; set; } = 0;

        // ========== 编程模式（海床） ==========
        // 勾选后出现"海床"页签：设置以代码（JSON）编辑，可创建面板副本、保存预设、AI 提示词生成
        public bool ProgrammingModeEnabled { get; set; } = false;
        // 用户自定义面板定义（编辑副本/新建面板，注册后可被区域选择）
        public System.Collections.Generic.List<ShoreHue.Core.Models.CustomPanelDefinition>? CustomPanels { get; set; }
        // 预设覆盖记录：sourceKey（内置节点 Key）→ 当前生效的预设名。应用预设时记录，恢复/应用内置时清除。
        // 存在覆盖 → 海床左侧对应节点高亮（未启用）、设置页对应分组变灰。
        public System.Collections.Generic.Dictionary<string, string>? AppliedPresets { get; set; }

        // ========== 逐区域动画覆盖（动画页签「动画应用于」） ==========
        // regionKey → 覆盖项（只覆盖 触发/隐藏动画 类型+时长）；空值字段 = 继承全局；无条目 = 完全跟随全局。
        public System.Collections.Generic.Dictionary<string, ShoreHue.Core.Models.RegionAnimationOverride>? RegionAnimationOverrides { get; set; }

        // ========== 边缘触发距离与延时（防误触） ==========
        // 触发距离（DIP）：鼠标距屏幕边缘多远判定为贴边；越小越难误触（默认 6，原 12 偏宽）
        public int TriggerDistancePx { get; set; } = 6;

        // 全局触发延时（ms）：鼠标进入边缘区域后停留多久才呼出面板；0 = 立即
        public int TriggerDelayMs { get; set; } = 150;

        // 逐区域触发延时 / 隐藏延时覆盖（regionKey → ms）；缺省用全局值
        public System.Collections.Generic.Dictionary<string, int>? RegionTriggerDelay { get; set; }
        public System.Collections.Generic.Dictionary<string, int>? RegionHideDelay { get; set; }

        // ========== 各区域自定义面板（Default = 跟随默认布局） ==========
        public string? RegionPanel_Top_Left { get; set; } = "Default";
        public string? RegionPanel_Top_Center { get; set; } = "Default";
        public string? RegionPanel_Top_Right { get; set; } = "Default";
        public string? RegionPanel_Bottom_Left { get; set; } = "Default";
        public string? RegionPanel_Bottom_Center { get; set; } = "Default";
        public string? RegionPanel_Bottom_Right { get; set; } = "Default";
        public string? RegionPanel_Left_Top { get; set; } = "Default";
        public string? RegionPanel_Left_Center { get; set; } = "Default";
        public string? RegionPanel_Left_Bottom { get; set; } = "Default";
        public string? RegionPanel_Right_Top { get; set; } = "Default";
        public string? RegionPanel_Right_Center { get; set; } = "Default";
        public string? RegionPanel_Right_Bottom { get; set; } = "Default";
        public string? RegionPanel_TopLeft { get; set; } = "Default";
        public string? RegionPanel_TopRight { get; set; } = "Default";
        public string? RegionPanel_BottomLeft { get; set; } = "Default";
        public string? RegionPanel_BottomRight { get; set; } = "Default";

        // ========== 自动更新（GitHub Releases） ==========
        public bool AutoCheckUpdate { get; set; } = true;

        // ========== 首次引导 ==========
        public bool OnboardingCompleted { get; set; } = false;

        // ========== 状态栏显示项 ==========
        public bool StatusShowTime { get; set; } = true;
        public bool StatusShowCpu { get; set; } = true;
        public bool StatusShowMemory { get; set; } = true;
        public bool StatusShowFps { get; set; } = true;
        public bool StatusShowVolume { get; set; } = true;
        public bool StatusShowNetwork { get; set; } = true;
        public bool StatusShowBattery { get; set; } = true;
        public bool StatusShowWeather { get; set; } = false;

        // ========== 天气（Open-Meteo，免费无 Key） ==========
        public bool WeatherEnabled { get; set; } = false;
        public string? WeatherCity { get; set; } = "";   // 空 = 按 IP 自动定位

        // 天气城市选择器里的"最近使用"（最多保留 8 个）
        public System.Collections.Generic.List<string>? WeatherRecentCities { get; set; }

        // ========== 小组件显示开关（用户选择面板中保留哪些功能） ==========
        public bool WidgetEnabled_Clipboard { get; set; } = true;
        public bool WidgetEnabled_Note { get; set; } = true;
        public bool WidgetEnabled_Timer { get; set; } = true;
        public bool WidgetEnabled_Calculator { get; set; } = true;
        public bool WidgetEnabled_TextAi { get; set; } = true;
        // ★ 网页工具（WebView2 联网）默认关闭：保持"开箱不联网"，用户主动启用后才会加载网页
        public bool WidgetEnabled_Web { get; set; } = false;

        // ========== 插件信任记录（安全 v2）==========
        /// <summary>
        /// 插件 id → 内容哈希：**只有哈希与当前内容一致**的记录才算受信（内容一变即自动失效）。
        /// ★ 信任只由用户在本机的显式操作产生（海床保存 / 界面点"信任"），不再由包内 manifest 自述决定
        ///   —— official / trustedSource 自述就是旧版的"免死金牌"。
        /// </summary>
        public Dictionary<string, string>? TrustedPlugins { get; set; }

        /// <summary>用户插件小组件（Widget_&lt;id&gt;）的启用覆盖；缺省视为启用。</summary>
        public Dictionary<string, bool> WidgetPluginOverrides { get; set; } = new();

        // ========== 自定义状态栏显示项开关 ==========
        /// <summary>自定义状态栏插件（status_&lt;id&gt;，IStatusProvider）的启用覆盖；缺省视为启用。</summary>
        public Dictionary<string, bool> StatusProviderEnabled { get; set; } = new();

        // ========== 划词翻译 ==========
        // 全局热键字符串（如 "Ctrl+Alt+Q"）；空 = 未设置（面板内提示去设置）
        public string? TextAiHotkey { get; set; } = "";

        // ========== 键盘呼出区域面板（Ctrl+数字环；与浏览器切标签冲突 → 默认关，可改修饰键） ==========
        public bool RegionHotkeysEnabled { get; set; } = false;
        public string RegionHotkeyModifier { get; set; } = "Ctrl";   // Ctrl / Ctrl+Shift / Alt / Ctrl+Alt

        // ========== 便签快捷键（在便签面板内生效；空 = 用默认值，见 SettingsManager） ==========
        // ========== 划词翻译面板（设置 → 面板 → 划词翻译）==========
        /// <summary>最近翻译保留几条（0 = 不记录）。</summary>
        public int TextAiHistoryLimit { get; set; } = 20;

        /// <summary>最近翻译（JSON）。★ 借设置落盘：小组件沙箱不允许碰文件系统。</summary>
        public string? TextAiHistoryJson { get; set; } = "";

        /// <summary>目标语言（如"英文"）；空 = 保持默认的"自动判中英"。</summary>
        public string? TextAiTargetLanguage { get; set; } = "";

        // ========== 计算器面板（设置 → 面板 → 计算器）==========
        /// <summary>计算历史最多保留几条（0 = 不记录）。借鉴 Windows 计算器的"历史记录"。</summary>
        public int CalculatorHistoryLimit { get; set; } = 20;

        /// <summary>计算历史（JSON 字符串）。★ 小组件沙箱不允许碰文件系统，所以历史借设置落盘。</summary>
        public string? CalculatorHistoryJson { get; set; } = "";

        /// <summary>任务栏「窗口标签分组」（JSON：组 → 成员应用路径）。★ 当用户设置持久化，跨重启恢复分组。</summary>
        public string? TaskbarGroupsJson { get; set; } = "";

        // ========== 插件运行时守卫（安全模式 / 异常熔断；系统数据，不是用户配置）==========
        /// <summary>已安全熔断停用的插件 key。连续失败达阈值后由守卫写入，用户可手动解除。</summary>
        public List<string> CircuitBrokenPlugins { get; set; } = new();
        /// <summary>用户请求"下次以安全模式启动"（托盘菜单写入；启动时读取并清除）。</summary>
        public bool SafeModeRequested { get; set; }
        /// <summary>连续"未正常退出"的次数；达到阈值启动时自动进安全模式。</summary>
        public int UncleanExitCount { get; set; }

        // ========== 剪贴板面板（设置 → 面板 → 剪贴板）==========
        /// <summary>历史列表是否支持键盘操作（↑↓ 选择 / Enter 复制 / 数字键 1-9 快选 / Del 删除）。</summary>
        public bool ClipboardKeyboardNav { get; set; } = true;

        /// <summary>是否显示"来源应用"（这条是从哪个程序复制来的）。</summary>
        public bool ClipboardShowSourceApp { get; set; } = true;

        public string? NoteHotkeyNew { get; set; } = "";
        public string? NoteHotkeyDelete { get; set; } = "";
        public string? NoteHotkeyNext { get; set; } = "";
    }
}