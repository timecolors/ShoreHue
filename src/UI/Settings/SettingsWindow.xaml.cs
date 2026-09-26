using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.UI.Settings.Pages;
using ShoreHue.UI.Theme;
using ShoreHue.UI.Localization;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ShoreHue.UI.Settings
{
    public partial class SettingsWindow : Window
    {
        private readonly ISettingsService _settings;
        private readonly IShortcutService _shortcutService;
        private SettingsData _settingsData = null!;
        // ★ 静态事件订阅句柄：关闭时注销，避免每次打开窗口都累积强引用（泄漏 + 主题切换重复执行）
        private Microsoft.Win32.UserPreferenceChangedEventHandler? _themeHandler;
        private Action? _pluginChangedHandler;

        public SettingsWindow(ISettingsService settings, IShortcutService shortcutService)
        {
            _settings = settings;
            _shortcutService = shortcutService;
            Icon = AppIconHelper.LoadAppIcon();
            InitializeComponent();
            // ★ 自适应窗口尺寸：默认 960x720；小屏/高 DPI 时按工作区钳制，避免超出屏幕
            //   （SystemParameters.WorkArea 为 DIP，与 WPF 坐标一致；留 40px 边距）
            try
            {
                Width = Math.Min(960, SystemParameters.WorkArea.Width - 40);
                Height = Math.Min(720, SystemParameters.WorkArea.Height - 40);
            }
            catch (Exception ex)
            {
                // 尽力而为：算不出工作区就用 XAML 里的默认宽高
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[设置] 计算窗口尺寸失败（用默认尺寸）：{ex.Message}");
            }

            // ★ 跟随系统浅/深色主题（DynamicResource 即时生效；系统切换时自动刷新）
            ApplySystemTheme();
            try
            {
                _themeHandler = (_, e) =>
                {
                    if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                    {
                        Dispatcher.BeginInvoke(new Action(ApplySystemTheme));
                    }
                };
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += _themeHandler;
            }
            catch (Exception ex)
            {
                // 订阅不上 = 系统切换浅/深色时设置页不跟着变（要重开设置页）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[设置] 订阅系统主题变化失败（主题不会实时跟随）：{ex.Message}");
            }
            // ★ 不启用 Mica：Mica 跟随系统主题，深色主题下会把设置页背景变成黑色。
            //   固定用 XAML 浅色背景（#F9F9F9），Win10/Win11 观感一致。
            LoadSettings();
              // ★ 打开窗口：视觉树就绪后应用全局字号缩放（滑块 ValueChanged 在 LoadSettings 之后才订阅，
              //   此处显式应用一次，保证打开即按配置字号显示）
              Loaded += (_, _) => ShoreHue.UI.Theme.FontScaleManager.ApplyFontScale(this, _settingsData.UiFontScale);
            LoadShortcutPage();
            // ★ 实时保存：所有设置控件变化自动保存（400ms 防抖）。
            //   注意：构造函数时窗口视觉树尚未建立（Show 之前），FindVisualChildren 找不到控件，
            //   必须在 Loaded（视觉树就绪）后再挂；惰性页签（动画等）内容首次选中才进视觉树，
            //   选中后以 Loaded 优先级补挂。三者都用 _autoSaveHooked 去重，重复调用安全。
            // ★ 自动保存钩子自愈：不赌 WPF 视觉树在哪个时机完整——
            //   窗口打开期间每 500ms 补挂一次（_autoSaveHooked 去重，幂等）。
            //   解耦了"控件何时呈现"与"钩子何时挂上"的耦合（原实现依赖 Loaded 时机，脆弱）。
            HookAutoSave();
            var autoSaveMaintenance = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            autoSaveMaintenance.Tick += (_, _) => HookAutoSave();
            autoSaveMaintenance.Start();
            Closed += (_, _) =>
            {
                autoSaveMaintenance.Stop();
                // ★ 注销静态事件订阅（防累积泄漏）
                try
                {
                    if (_themeHandler != null) Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _themeHandler;
                    if (_pluginChangedHandler != null) ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.Changed -= _pluginChangedHandler;
                }
                catch (Exception ex)
                {
                    // 尽力而为：窗口正在关闭，退订失败只是留了个已死的订阅
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[设置] 退订事件失败（无害）：{ex.Message}");
                }
            };

            // ★ 插件安装/删除时实时刷新（本窗口非模态常驻，可能在别处保存插件）
            _pluginChangedHandler = () => Dispatcher.Invoke(() =>
            {
                RefreshWidgetMarket();
            });
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.Changed += _pluginChangedHandler;

            // AI 高级参数滑块
            sldAiTemperature.ValueChanged += (s, e) => txtAiTemperature.Text = sldAiTemperature.Value.ToString("F1");

            // 滑块事件绑定
              // ★ 全局字号缩放：滑块变化 → 更新显示 + 应用缩放（保存时落盘）
              sldUiFontScale.ValueChanged += (s, e) =>
              {
                  txtUiFontScale.Text = sldUiFontScale.Value.ToString("P0");
                  ShoreHue.UI.Theme.FontScaleManager.ApplyFontScale(this, sldUiFontScale.Value);
              };
            sldOpacity.ValueChanged += (s, e) => txtOpacityValue.Text = sldOpacity.Value.ToString("F2");
            sldCornerRadius.ValueChanged += (s, e) => txtCornerRadiusValue.Text = sldCornerRadius.Value.ToString("F0");
            sldHorizontalThreshold.ValueChanged += (s, e) => txtHorizontalThreshold.Text = (sldHorizontalThreshold.Value * 100).ToString("F0") + "%";
            sldTagWidth.ValueChanged += (s, e) => txtTagWidth.Text = sldTagWidth.Value.ToString("F0");

            sldIconSize.ValueChanged += (s, e) =>
            {
                double val = Math.Round(sldIconSize.Value);
                sldIconSize.Value = val;
                txtIconSize.Text = $"{val:F0}px";
            };

            sldClipboardMax.ValueChanged += (s, e) => txtClipboardMax.Text = sldClipboardMax.Value.ToString("F0");
            sldClipboardDisplay.ValueChanged += (s, e) => txtClipboardDisplay.Text = sldClipboardDisplay.Value.ToString("F0");
            sldClipImageMax.ValueChanged += (s, e) => txtClipImageMax.Text = ((int)sldClipImageMax.Value) + "px";
            sldClipImageCacheLimit.ValueChanged += (s, e) => txtClipImageCacheLimit.Text = ((int)sldClipImageCacheLimit.Value) + "MB";

            // 动画设置滑块（触发/隐藏分设）
            sldShowDuration.ValueChanged += (s, e) => txtShowDuration.Text = sldShowDuration.Value.ToString("F0") + "ms";
            sldHideDuration.ValueChanged += (s, e) => txtHideDuration.Text = sldHideDuration.Value.ToString("F0") + "ms";
            sldShowZoomFrom.ValueChanged += (s, e) => txtShowZoomFrom.Text = sldShowZoomFrom.Value.ToString("0.0#");
            sldHideZoomTo.ValueChanged += (s, e) => txtHideZoomTo.Text = sldHideZoomTo.Value.ToString("0.0#");
            sldShowOsc.ValueChanged += (s, e) => txtShowOsc.Text = sldShowOsc.Value.ToString("F0");
            sldHideOsc.ValueChanged += (s, e) => txtHideOsc.Text = sldHideOsc.Value.ToString("F0");
            sldShowSpring.ValueChanged += (s, e) => txtShowSpring.Text = sldShowSpring.Value.ToString("0.0#");
            sldHideSpring.ValueChanged += (s, e) => txtHideSpring.Text = sldHideSpring.Value.ToString("0.0#");
            sldTransformDuration.ValueChanged += (s, e) => txtTransformDuration.Text = sldTransformDuration.Value.ToString("F0") + "ms";
            sldHideDelay.ValueChanged += (s, e) => txtHideDelay.Text =
                sldHideDelay.Value <= 0 ? ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Immediate"] : sldHideDelay.Value.ToString("F0") + "ms";
            sldFlyDuration.ValueChanged += (s, e) => txtFlyDuration.Text = sldFlyDuration.Value.ToString("F0") + "ms";
            sldContentStabilize.ValueChanged += (s, e) => txtContentStabilize.Text = sldContentStabilize.Value.ToString("F0") + "ms";
            sldSnapRange.ValueChanged += (s, e) => txtSnapRange.Text = ((int)sldSnapRange.Value) + "px";

            // ★★★ 区域防抖滑块 ★★★
            sldRegionDebounce.ValueChanged += (s, e) => txtRegionDebounce.Text = sldRegionDebounce.Value.ToString("F0") + "ms";

            // ★★★ 触发距离与延时滑块 ★★★
            sldTrigDist.ValueChanged += (s, e) => txtTrigDist.Text = ((int)sldTrigDist.Value) + "px";
            sldGlbTrig.ValueChanged += (s, e) => txtGlbTrig.Text = ((int)sldGlbTrig.Value) + "ms";
            // 全局隐藏延时与"动画"标签页的延时隐藏滑块双向联动（同一字段，改哪边都同步）
            sldGlbHide.ValueChanged += (s, e) =>
            {
                txtGlbHide.Text = ((int)sldGlbHide.Value) + "ms";
                if (Math.Abs(sldHideDelay.Value - sldGlbHide.Value) > 0.01)
                    sldHideDelay.Value = sldGlbHide.Value;
            };
            sldHideDelay.ValueChanged += (s, e) =>
            {
                if (Math.Abs(sldGlbHide.Value - sldHideDelay.Value) > 0.01)
                    sldGlbHide.Value = sldHideDelay.Value;
            };

            // ★★★ 面板运行帧率滑块（0=自动满帧；30/60/90/120 手动档） ★★★
            sldPanelFrameRate.ValueChanged += (s, e) =>
            {
                int fps = (int)sldPanelFrameRate.Value;
                txtPanelFrameRate.Text = fps <= 0
                    ? ShoreHue.UI.Localization.LocalizationManager.Instance["Set_FrameRateAuto"]
                    : fps + "fps";
            };
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // （曾尝试 Mica 背景，已决定不启用；无调用点的死代码已删除）
        private void LoadShortcutPage()
        {
            var page = new ShortcutManagementPage(_shortcutService);
            ShortcutManagementFrame.Navigate(page);
        }

        /// <summary>按系统浅/深色主题切换设置窗口配色（浅色默认，深色用 Win11 风格暗色变体）。</summary>
        private void ApplySystemTheme()
        {
            try
            {
                bool dark = !ShoreHue.Infrastructure.Utils.SystemTheme.IsLightTheme();
                var bg = dark ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) : new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
                var fg = dark ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                var card = dark ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) : new SolidColorBrush(Colors.White);
                var border = dark ? new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x3F)) : new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                // 窗口背景：海岸线静态渐变（右上沙滩 → 左下海；深色 = 夜海）
                var coast = new System.Windows.Media.LinearGradientBrush(
                    dark ? Color.FromRgb(0x33, 0x2C, 0x22) : Color.FromRgb(0xFB, 0xF3, 0xDF),
                    dark ? Color.FromRgb(0x14, 0x1E, 0x29) : Color.FromRgb(0xB4, 0xD3, 0xE0),
                    45);
                coast.GradientStops.Add(new System.Windows.Media.GradientStop(
                    dark ? Color.FromRgb(0x24, 0x28, 0x2B) : Color.FromRgb(0xDC, 0xE9, 0xE6), 0.50));
                coast.Freeze();
                Resources["SettingsWindowBg"] = bg;
                Resources["SettingsWindowFg"] = fg;
                Resources["SettingsCardBg"] = card;
                Resources["SettingsBorder"] = border;
                Resources["CoastFlat"] = coast;
            }
            catch (Exception ex)
            {
                // 主题资源换不上 → 设置页颜色仍是旧的（切主题时看得见）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[设置] 应用系统主题失败（界面颜色不更新）：{ex.Message}");
            }
        }

        private void LoadSettings()
        {
            _settingsData = SettingsDataManager.Load();

            // 触发位置
            chkTop.IsChecked = _settingsData.Edge_Top;
            chkBottom.IsChecked = _settingsData.Edge_Bottom;
            chkLeft.IsChecked = _settingsData.Edge_Left;
            chkRight.IsChecked = _settingsData.Edge_Right;
            chkTopLeft.IsChecked = _settingsData.Corner_TopLeft;
            chkTopRight.IsChecked = _settingsData.Corner_TopRight;
            chkBottomLeft.IsChecked = _settingsData.Corner_BottomLeft;
            chkBottomRight.IsChecked = _settingsData.Corner_BottomRight;

            // 边行为模式
            SettingsUIHelper.SetComboSelected(cmbTopMode, _settingsData.EdgeMode_Top ?? "Follow");
            SettingsUIHelper.SetComboSelected(cmbBottomMode, _settingsData.EdgeMode_Bottom ?? "Follow");
            SettingsUIHelper.SetComboSelected(cmbLeftMode, _settingsData.EdgeMode_Left ?? "Follow");
            SettingsUIHelper.SetComboSelected(cmbRightMode, _settingsData.EdgeMode_Right ?? "Follow");

            // 固定形状
            SettingsUIHelper.SetShapeComboSelected(cmbFixedShapeTop, _settingsData.FixedShape_Top ?? "Square");
            SettingsUIHelper.SetShapeComboSelected(cmbFixedShapeBottom, _settingsData.FixedShape_Bottom ?? "Square");
            SettingsUIHelper.SetShapeComboSelected(cmbFixedShapeLeft, _settingsData.FixedShape_Left ?? "Square");
            SettingsUIHelper.SetShapeComboSelected(cmbFixedShapeRight, _settingsData.FixedShape_Right ?? "Square");

            // 区域形状
            SettingsUIHelper.SetShapeComboSelected(cmbTopLeft, _settingsData.Region_Top_Left ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbTopCenter, _settingsData.Region_Top_Center ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbTopRight, _settingsData.Region_Top_Right ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbBottomLeft, _settingsData.Region_Bottom_Left ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbBottomCenter, _settingsData.Region_Bottom_Center ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbBottomRight, _settingsData.Region_Bottom_Right ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbLeftTop, _settingsData.Region_Left_Top ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbLeftCenter, _settingsData.Region_Left_Center ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbLeftBottom, _settingsData.Region_Left_Bottom ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbRightTop, _settingsData.Region_Right_Top ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbRightCenter, _settingsData.Region_Right_Center ?? "Default");
            SettingsUIHelper.SetShapeComboSelected(cmbRightBottom, _settingsData.Region_Right_Bottom ?? "Default");

            // 外观
            txtBgColor.Text = _settingsData.BackgroundColor ?? "#2D2D2D";
            txtTextColor.Text = _settingsData.TextColor ?? "#FFFFFF";
            sldOpacity.Value = _settingsData.Opacity;
            sldCornerRadius.Value = _settingsData.CornerRadius;
            chkShowSystemStatus.IsChecked = _settingsData.ShowSystemStatus;
              // ★ 修复：先读持久化值再回填滑块（原实现先把滑块默认值 1.0 写进数据，
              //   持久化的字号缩放从未被读取，打开设置即被冲掉）
              sldUiFontScale.Value = _settingsData.UiFontScale;
              txtUiFontScale.Text = $"{_settingsData.UiFontScale:P0}";

            // 网页工具（预置下拉 + 自定义地址）
            LoadWebToolSettings();

            // 动画与布局
            sldHorizontalThreshold.Value = _settingsData.HorizontalLayoutThreshold;
            sldTagWidth.Value = _settingsData.TagWidth;

            // 任务栏
            sldIconSize.Value = _settingsData.TaskbarIconSize;
            txtIconSize.Text = $"{_settingsData.TaskbarIconSize:F0}px";

            // 剪贴板与便签
            sldClipboardMax.Value = _settingsData.ClipboardMaxCount;
            // ★ 用**归一后的**值（SettingsManager 的 getter 会把旧的"字符数"老配置归一到默认 4）；
            //   直接用 _settingsData 里的原始值，老配置的 100 会被 Slider 静默钳成上限 20，反而写坏。
            sldClipboardDisplay.Value = _settings.ClipboardDisplayLength;
            chkClipKeyboardNav.IsChecked = _settingsData.ClipboardKeyboardNav;
            chkClipShowSourceApp.IsChecked = _settingsData.ClipboardShowSourceApp;
            sldClipImageMax.Value = _settingsData.ClipboardImageMaxWidth;
            txtClipImageMax.Text = _settingsData.ClipboardImageMaxWidth + "px";
            sldClipImageCacheLimit.Value = _settingsData.ClipboardImageCacheLimitMB;
            txtClipImageCacheLimit.Text = _settingsData.ClipboardImageCacheLimitMB + "MB";
            txtDefaultNoteColor.Text = _settingsData.DefaultNoteColor ?? "#00000000";
            chkNoteShowTitle.IsChecked = _settingsData.NoteShowTitleByDefault;
            txtNoteHotkeyNew.Text = _settingsData.NoteHotkeyNew ?? "";
            txtNoteHotkeyDelete.Text = _settingsData.NoteHotkeyDelete ?? "";
            txtNoteHotkeyNext.Text = _settingsData.NoteHotkeyNext ?? "";
            UpdateNoteHotkeyHint();

            // 自适应
            chkAutoFitOnTrigger.IsChecked = _settingsData.AutoFitOnTrigger;

            // 勿扰
            chkRememberDndMode.IsChecked = _settingsData.RememberDndMode;

            // ★★★ 动画设置 ★★★
            chkAnimationsEnabled.IsChecked = _settingsData.AnimationsEnabled;

            // ★★★ 编程模式（海床） ★★★
            chkProgrammingMode.IsChecked = _settingsData.ProgrammingModeEnabled;
            UpdateSeabedTabVisibility();

            cmbTransformEasing.SelectedItem = GetComboBoxItemByContent(cmbTransformEasing, SettingsUIHelper.GetEasingDisplayName(_settingsData.TransformEasingType ?? "CubicEase"));

            // ★ 触发/隐藏动画（类型 + 时长 + 特化参数）
            // 自定义动画（海床「动画」分组）：先确保注册表已加载，再把它加入四个类型下拉
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ReloadAnimations();
            RefreshCustomAnimItems(cmbShowAnimType);
            RefreshCustomAnimItems(cmbHideAnimType);
            RefreshCustomAnimItems(cmbRegionShowType);
            RefreshCustomAnimItems(cmbRegionHideType);
            SelectComboByAnimType(cmbShowAnimType, _settingsData.ShowAnimationType, isHide: false);
            SelectComboByAnimType(cmbHideAnimType, _settingsData.HideAnimationType, isHide: true);

            // ★★★ 逐区域动画（动画应用于：全局默认 / 16 区域） ★★★
            PopulateAnimRegionCombo();
            cmbAnimRegion.SelectedIndex = 0;
            // ★ 0 = "尚未迁移"的哨兵值（老配置没有新字段时由 SettingsFileManager 迁移，迁移不到就靠这里的回退）。
            //   滑块以前 Minimum=50，加载时会把 0 直接夹成 50 并在首次保存时把 50 写死，
            //   于是"回退到 ShowHideDurationMs(150)"这条逻辑被永久破坏。现在滑块允许 0，
            //   但**显示/保存时仍然解析成有效时长**，不给用户看到 0ms。
            int showMs = _settingsData.ShowAnimationDurationMs > 0
                ? _settingsData.ShowAnimationDurationMs
                : (_settingsData.ShowHideDurationMs > 0 ? _settingsData.ShowHideDurationMs : 150);
            int hideMs = _settingsData.HideAnimationDurationMs > 0
                ? _settingsData.HideAnimationDurationMs
                : (_settingsData.ShowHideDurationMs > 0 ? _settingsData.ShowHideDurationMs : 150);
            sldShowDuration.Value = showMs;
            txtShowDuration.Text = showMs + "ms";
            sldHideDuration.Value = hideMs;
            txtHideDuration.Text = hideMs + "ms";
            sldShowZoomFrom.Value = _settingsData.ShowAnimationZoomFrom;
            txtShowZoomFrom.Text = _settingsData.ShowAnimationZoomFrom.ToString("0.0#");
            sldHideZoomTo.Value = _settingsData.HideAnimationZoomTo;
            txtHideZoomTo.Text = _settingsData.HideAnimationZoomTo.ToString("0.0#");
            sldShowOsc.Value = _settingsData.ShowAnimationOscillations;
            txtShowOsc.Text = _settingsData.ShowAnimationOscillations.ToString();
            sldHideOsc.Value = _settingsData.HideAnimationOscillations;
            txtHideOsc.Text = _settingsData.HideAnimationOscillations.ToString();
            sldShowSpring.Value = _settingsData.ShowAnimationSpringiness;
            txtShowSpring.Text = _settingsData.ShowAnimationSpringiness.ToString("0.0#");
            sldHideSpring.Value = _settingsData.HideAnimationSpringiness;
            txtHideSpring.Text = _settingsData.HideAnimationSpringiness.ToString("0.0#");
            UpdateShowAnimRows();
            UpdateHideAnimRows();

            sldTransformDuration.Value = _settingsData.TransformDurationMs;
            txtTransformDuration.Text = _settingsData.TransformDurationMs + "ms";
            sldContentStabilize.Value = _settingsData.ContentStabilizeMs;
            txtContentStabilize.Text = _settingsData.ContentStabilizeMs + "ms";
            sldSnapRange.Value = _settingsData.SnapRangePx;
            txtSnapRange.Text = _settingsData.SnapRangePx + "px";

            sldHideDelay.Value = _settingsData.HideDelayMs;
            txtHideDelay.Text = _settingsData.HideDelayMs <= 0
                ? ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Immediate"]
                : _settingsData.HideDelayMs + "ms";

            sldFlyDuration.Value = _settingsData.FlyDurationMs;
            txtFlyDuration.Text = _settingsData.FlyDurationMs + "ms";

            // ★★★ 引潮模式 ★★★
            chkClingMode.IsChecked = _settingsData.ClingModeEnabled;

            // ★★★ 键盘呼出区域面板（Ctrl+数字环） ★★★
            chkRegionHotkeys.IsChecked = _settingsData.RegionHotkeysEnabled;
            string rhMod = _settingsData.RegionHotkeyModifier ?? "Ctrl";
            foreach (var it in cmbRegionHotkeyMod.Items)
            {
                if (it is ComboBoxItem cbi && (cbi.Tag?.ToString() ?? "") == rhMod) { cmbRegionHotkeyMod.SelectedItem = it; break; }
            }

            // ★ 面板点击穿透修饰键
            string passthrough = _settingsData.PassthroughModifier ?? "Ctrl";
            foreach (var item in cmbPassthrough.Items)
            {
                if (item is System.Windows.Controls.ComboBoxItem cbi && (cbi.Tag?.ToString() ?? "") == passthrough)
                {
                    cmbPassthrough.SelectedItem = item;
                    break;
                }
            }

            // ★★★ 区域防抖 ★★★
            sldRegionDebounce.Value = _settingsData.RegionDebounceMs;
            txtRegionDebounce.Text = _settingsData.RegionDebounceMs + "ms";

            // ★★★ 小工具市场列表 ★★★
            RefreshWidgetMarket();

            // ★★★ 触发距离与延时（全局默认 + 逐区域） ★★★
            sldTrigDist.Value = _settingsData.TriggerDistancePx;
            txtTrigDist.Text = _settingsData.TriggerDistancePx + "px";
            sldGlbTrig.Value = _settingsData.TriggerDelayMs;
            txtGlbTrig.Text = _settingsData.TriggerDelayMs + "ms";
            sldGlbHide.Value = _settingsData.HideDelayMs;
            txtGlbHide.Text = _settingsData.HideDelayMs + "ms";
            BuildRegionDelayRows();

            // ★★★ 区域面板自定义 ★★★
            foreach (string key in RegionPanelKeys)
            {
                var combo = (ComboBox?)FindName("cmbPanel_" + key);
                if (combo == null) continue;
                FillPanelCombo(combo);
                SelectPanelValue(combo, _settings.GetRegionPanel(key));
            }

            // ★★★ 语言（通用设置） ★★★
            SelectLanguage(_settingsData.Language ?? "");
            UpdateCurrentLanguageText();

            // ★★★ 面板小组件与划词翻译 快捷键 ★★★
            // ★ 小组件启停由小工具页签左侧勾选框即时生效（此处无需加载）
            txtTextAiHotkey.Text = _settingsData.TextAiHotkey ?? "";
            UpdateTextAiHotkeyHint();

            // ★★★ 状态栏显示项与天气 ★★★
            chkStatusTime.IsChecked = _settingsData.StatusShowTime;
            chkStatusCpu.IsChecked = _settingsData.StatusShowCpu;
            chkStatusMemory.IsChecked = _settingsData.StatusShowMemory;
            chkStatusFps.IsChecked = _settingsData.StatusShowFps;
            chkStatusVolume.IsChecked = _settingsData.StatusShowVolume;
            chkStatusNetwork.IsChecked = _settingsData.StatusShowNetwork;
            chkStatusBattery.IsChecked = _settingsData.StatusShowBattery;
            chkStatusWeather.IsChecked = _settingsData.StatusShowWeather;
            txtWeatherCity.Text = _settingsData.WeatherCity ?? "";

            // ★★★ 自动更新 ★★★
            chkAutoCheckUpdate.IsChecked = _settingsData.AutoCheckUpdate;
            UpdateUpdateStatus();

            // ★★★ 商店版（MSIX）不提供 GitHub 自更新 ★★★
            UpdateGroup.Visibility = AppPaths.IsPackaged ? Visibility.Collapsed : Visibility.Visible;

            // ★★★ 关于与隐私 ★★★
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            txtAbout.Text = $"ShoreHue 海岸线 v{ver?.ToString(3) ?? "1.0.0"}";

            // ★★★ AI 助手设置 ★★★
            LoadAiSettings();

            // ★★★ 面板运行帧率（0=自动满帧，30/60/90/120 手动） ★★★
            sldPanelFrameRate.Value = Math.Clamp(_settingsData.PanelFrameRate, 0, 120);
            txtPanelFrameRate.Text = _settingsData.PanelFrameRate <= 0
                ? ShoreHue.UI.Localization.LocalizationManager.Instance["Set_FrameRateAuto"]
                : _settingsData.PanelFrameRate + "fps";

            // ★★★ 预设覆盖：冲突的内置设置分组变灰（应用预设后刷新生效） ★★★
            ApplyOverrideDimming();
        }


        private void UpdateUpdateStatus()
        {
            string owner = ShoreHue.Infrastructure.WinApi.UpdateService.GitHubOwner;
            string repo = ShoreHue.Infrastructure.WinApi.UpdateService.GitHubRepo;
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            {
                txtUpdateStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_UpdateSourceMissing"];
            }
            else
            {
                txtUpdateStatus.Text = string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Set_UpdateSource"], owner, repo);
            }
        }

        // ==================== 网页工具 ====================
        private bool _loadingWebTool;

        private void LoadWebToolSettings()
        {
            try
            {
                _loadingWebTool = true;
                cmbWebTool.Items.Clear();
                foreach (var t in ShoreHue.UI.Widgets.WebToolPresets.Presets)
                {
                    cmbWebTool.Items.Add(new ComboBoxItem { Tag = t, Content = t.Name });
                }
                cmbWebTool.Items.Add(new ComboBoxItem
                {
                    Tag = null,
                    Content = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_WebTool_CustomItem"]
                });

                string url = _settingsData.WebWidgetUrl ?? "";
                var match = ShoreHue.UI.Widgets.WebToolPresets.Presets.FirstOrDefault(t =>
                    string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase));
                txtWebToolUrl.Text = url;
                RefreshWebBookmarkList();
                if (match != null)
                {
                    cmbWebTool.SelectedIndex = ShoreHue.UI.Widgets.WebToolPresets.Presets.ToList().IndexOf(match);
                }
                else
                {
                    cmbWebTool.SelectedIndex = cmbWebTool.Items.Count - 1; // 自定义…
                }
            }
            finally { _loadingWebTool = false; }
        }

        private void CmbWebTool_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingWebTool || cmbWebTool.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is ShoreHue.UI.Widgets.WebToolPresets.Tool t)
            {
                txtWebToolUrl.Text = t.Url;
                _settingsData.WebWidgetUrl = t.Url;
                ScheduleSave(); // ★ 改了数据就要排一次落盘（以前调的是 HookAutoSave —— 它只挂事件，不保存）
            }
        }

        private void TxtWebToolUrl_LostFocus(object sender, RoutedEventArgs e)
            => SaveWebToolUrlFromBox();

        private void TxtWebToolUrl_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                SaveWebToolUrlFromBox();
                Keyboard.ClearFocus();
            }
        }

        private void SaveWebToolUrlFromBox()
        {
            string url = txtWebToolUrl.Text.Trim();
            if (url.Length == 0) return;
            if (!string.Equals(_settingsData.WebWidgetUrl, url, StringComparison.Ordinal))
            {
                _settingsData.WebWidgetUrl = url;
                ScheduleSave();
            }
        }

        private void RefreshWebBookmarkList()
        {
            lstWebBookmarks.ItemsSource = null;
            lstWebBookmarks.ItemsSource = _settingsData.WebBookmarks;
        }

        private void BtnAddWebBookmark_Click(object sender, RoutedEventArgs e)
        {
            string url = txtWebToolUrl.Text.Trim();
            if (url.Length == 0) return;
            if (!url.Contains("://")) url = "https://" + url;
            if (_settingsData.WebBookmarks.Any(b =>
                string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "该网址已在收藏中", "网页工具", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string name = GetBookmarkName(url);
            _settingsData.WebBookmarks.Add(new ShoreHue.Core.Services.Configuration.WebBookmark { Name = name, Url = url });
            RefreshWebBookmarkList();
            ScheduleSave();
            MessageBox.Show(this, "已收藏：" + name, "网页工具", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDelWebBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (lstWebBookmarks.SelectedItem is ShoreHue.Core.Services.Configuration.WebBookmark b)
            {
                _settingsData.WebBookmarks.Remove(b);
                RefreshWebBookmarkList();
                ScheduleSave();
            }
        }

        private static string GetBookmarkName(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host.TrimStart("www.".ToCharArray());
            }
            catch (Exception ex)
            {
                // 尽力而为：URL 解析不出来就显示原串
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[设置] 解析网址主机失败（显示原始地址）：{ex.Message}");
                return url;
            }
        }

        /// <summary>卸载 ShoreHue（非商店版）：二次确认后启动卸载脚本（删除应用/可选删除数据）。</summary>
        private void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ShoreHue.Infrastructure.Utils.AppPaths.IsPackaged)
                {
                    System.Windows.MessageBox.Show(this,
                        ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_397"],
                        "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // ★ 二次确认 1：是否卸载
                var c1 = System.Windows.MessageBox.Show(this,
                    ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_397"],
                    "ShoreHue", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (c1 != MessageBoxResult.OK) return;

                // ★ 二次确认 2：是否删除本地数据
                var c2 = System.Windows.MessageBox.Show(this,
                    ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_398"],
                    "ShoreHue", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (c2 == MessageBoxResult.Cancel) return;

                bool ok = ShoreHue.Infrastructure.WinApi.UninstallHelper.LaunchUninstall(c2 == MessageBoxResult.Yes);
                if (!ok)
                {
                    System.Windows.MessageBox.Show(this, "启动卸载脚本失败", "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                // 卸载脚本随后会停止并删除应用；这里正常退出
                Close();
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("卸载失败", ex);
                System.Windows.MessageBox.Show(this, "卸载失败：" + ex.Message, "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>切换设置窗口到指定页签（x:Name；找不到时忽略）。供右键菜单直达（如 AI 设置 / 海床）。</summary>
        public void ActivateTab(string tabName)
        {
            if (FindName(tabName) is TabItem tab)
            {
                tab.IsSelected = true;
            }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ShoreHue.Infrastructure.WinApi.UpdateService.GitHubOwner) ||
                string.IsNullOrWhiteSpace(ShoreHue.Infrastructure.WinApi.UpdateService.GitHubRepo))
            {
                txtUpdateStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_UpdateSourceMissing2"];
                return;
            }

            btnCheckUpdate.IsEnabled = false;
            txtUpdateStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Checking"];
            try
            {
                var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                              ?? new Version(1, 0, 0);
                var info = await ShoreHue.Infrastructure.WinApi.UpdateService
                    .CheckForUpdateAsync(current);
                txtUpdateStatus.Text = info == null
                    ? ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Latest"]
                    : string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Set_NewVersion"], info.Version, info.Tag);
            }
            catch
            {
                txtUpdateStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_CheckFailed"];
            }
            finally
            {
                btnCheckUpdate.IsEnabled = true;
            }
        }

        // ========== 天气城市选择 ==========

        private void BtnPickWeatherCity_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new WeatherCityPickerWindow(txtWeatherCity.Text);
                picker.Owner = this;
                if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedCity))
                {
                    txtWeatherCity.Text = picker.SelectedCity;
                }
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("打开城市选择器失败", ex);
            }
        }

        // ========== 划词翻译 快捷键捕获 ==========

        private const string PanelToggleHotkey = "Ctrl+Alt+B";

        /// <summary>热键捕获框：点击后按下组合键即显示；Backspace/Esc 清除。</summary>
        private void TxtTextAiHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // 只读框不参与输入

            if (e.Key == Key.Escape || (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None))
            {
                txtTextAiHotkey.Text = "";
                UpdateTextAiHotkeyHint();
                return;
            }

            string combo = HotkeyParser.Format(e.Key, Keyboard.Modifiers);
            if (combo.Length == 0) return; // 纯修饰键 / 不支持的键：继续等待组合完成

            txtTextAiHotkey.Text = combo;
            UpdateTextAiHotkeyHint();
        }

        private void BtnClearTextAiHotkey_Click(object sender, RoutedEventArgs e)
        {
            txtTextAiHotkey.Text = "";
            UpdateTextAiHotkeyHint();
        }

        // ========== 便签快捷键（面板内生效；三个框共用同一套捕获/清除/提示逻辑） ==========

        /// <summary>便签快捷键捕获框：按下组合键即显示；Backspace/Esc 清除。与划词翻译同一套范式。</summary>
        private void NoteHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;
            e.Handled = true; // 只读框不参与输入

            if (e.Key == Key.Escape || (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None))
            {
                box.Text = "";
                UpdateNoteHotkeyHint();
                return;
            }

            string combo = ShoreHue.Infrastructure.WinApi.HotkeyParser.Format(e.Key, Keyboard.Modifiers);
            if (combo.Length == 0) return; // 纯修饰键 / 不支持的键：继续等待组合完成

            box.Text = combo;
            UpdateNoteHotkeyHint();
        }

        private void BtnClearNoteHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name } && FindName(name) is TextBox box) box.Text = "";
            UpdateNoteHotkeyHint();
        }

        /// <summary>提示：未设置 / 三个里重复 / 正常（并说明"只在便签面板内生效"）。</summary>
        private void UpdateNoteHotkeyHint()
        {
            if (txtNoteHotkeyHint == null) return;
            var boxes = new[] { txtNoteHotkeyNew, txtNoteHotkeyDelete, txtNoteHotkeyNext };
            var set = boxes.Select(b => (b.Text ?? "").Trim()).Where(s => s.Length > 0).ToList();

            if (set.Count == 0)
            {
                txtNoteHotkeyHint.Text = LocalizationManager.Instance["Set_NoteHotkey_NotSet"];
                txtNoteHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
            else if (set.Count != set.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                txtNoteHotkeyHint.Text = LocalizationManager.Instance["Set_NoteHotkey_Conflict"];
                txtNoteHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
            }
            else
            {
                txtNoteHotkeyHint.Text = LocalizationManager.Instance["Set_NoteHotkey_Hint"];
                txtNoteHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(90, 120, 90));
            }
        }

        private void UpdateTextAiHotkeyHint()
        {
            if (txtTextAiHotkey == null) return;
            string hotkey = txtTextAiHotkey.Text.Trim();
            if (hotkey.Length == 0)
            {
                txtTextAiHotkeyHint.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_HotkeyHintNotSet"];
                txtTextAiHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
            else if (hotkey == PanelToggleHotkey)
            {
                txtTextAiHotkeyHint.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_HotkeyConflict"];
                txtTextAiHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
            }
            else
            {
                txtTextAiHotkeyHint.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_HotkeySet"];
                txtTextAiHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(60, 170, 90));
            }
        }

        // ========== 语言（通用设置） ==========

        private void SelectLanguage(string value)
        {
            foreach (ComboBoxItem item in cmbLanguage.Items)
            {
                if (item.Tag?.ToString() == value)
                {
                    cmbLanguage.SelectedItem = item;
                    return;
                }
            }
            cmbLanguage.SelectedIndex = 0; // 跟随系统
        }

        private void UpdateCurrentLanguageText()
        {
            if (txtCurrentLanguage == null) return;
            var lm = ShoreHue.UI.Localization.LocalizationManager.Instance;
            string suffix = string.IsNullOrEmpty(_settingsData.Language)
                ? "（" + lm["Set_LangFollowSystem"] + "）"
                : "";
            txtCurrentLanguage.Text = lm["Set_LangPrefix"] + lm.CurrentCultureName + suffix;
        }

        // ========== 编程模式（海床） ==========

        private void ChkProgrammingMode_Changed(object sender, RoutedEventArgs e)
        {
            _settingsData.ProgrammingModeEnabled = chkProgrammingMode.IsChecked ?? false;
            UpdateSeabedTabVisibility();
        }

        private void UpdateSeabedTabVisibility()
        {
            if (tabSeabed == null) return;
            bool on = chkProgrammingMode.IsChecked == true;
            tabSeabed.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && tabSeabed.Content == null)
            {
                tabSeabed.Content = new ShoreHue.UI.Settings.Pages.SeabedPage(_settings);
            }
        }

        // ========== 颜色选择器 ==========

        private void BtnBgColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.ColorDialog();
            dialog.Color = SettingsUIHelper.HexToDrawingColor(txtBgColor.Text ?? "#2D2D2D");
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                txtBgColor.Text = SettingsUIHelper.DrawingColorToHex(dialog.Color);
            }
        }

        private void BtnTextColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.ColorDialog();
            dialog.Color = SettingsUIHelper.HexToDrawingColor(txtTextColor.Text ?? "#FFFFFF");
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                txtTextColor.Text = SettingsUIHelper.DrawingColorToHex(dialog.Color);
            }
        }

        private void BtnNoteColorPicker_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.ColorDialog();
            dialog.Color = SettingsUIHelper.HexToDrawingColor(txtDefaultNoteColor.Text ?? "#00000000");
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                txtDefaultNoteColor.Text = SettingsUIHelper.DrawingColorToHex(dialog.Color);
            }
        }

        // ========== 自动保存（实时生效）/ 刷新 ==========

        /// <summary>把所有控件值写入设置数据（不含保存/应用，供实时保存与刷新调用）。</summary>
        private void ApplyControlsToData()
        {
            // ★ 自动检查更新：以前这个复选框只在加载时被**读**，从来没有写回 ——
            //   取消勾选关掉窗口再打开又变回勾选状态，MainWindow 也照旧检查更新。
            _settingsData.AutoCheckUpdate = chkAutoCheckUpdate.IsChecked ?? true;

            // 触发位置
            _settingsData.Edge_Top = chkTop.IsChecked ?? true;
            _settingsData.Edge_Bottom = chkBottom.IsChecked ?? true;
            _settingsData.Edge_Left = chkLeft.IsChecked ?? true;
            _settingsData.Edge_Right = chkRight.IsChecked ?? true;
            _settingsData.Corner_TopLeft = chkTopLeft.IsChecked ?? true;
            _settingsData.Corner_TopRight = chkTopRight.IsChecked ?? true;
            _settingsData.Corner_BottomLeft = chkBottomLeft.IsChecked ?? true;
            _settingsData.Corner_BottomRight = chkBottomRight.IsChecked ?? true;

            // 边行为模式
            _settingsData.EdgeMode_Top = SettingsUIHelper.GetComboMode(cmbTopMode);
            _settingsData.EdgeMode_Bottom = SettingsUIHelper.GetComboMode(cmbBottomMode);
            _settingsData.EdgeMode_Left = SettingsUIHelper.GetComboMode(cmbLeftMode);
            _settingsData.EdgeMode_Right = SettingsUIHelper.GetComboMode(cmbRightMode);

            // 固定形状
            _settingsData.FixedShape_Top = SettingsUIHelper.GetShapeValue(cmbFixedShapeTop);
            _settingsData.FixedShape_Bottom = SettingsUIHelper.GetShapeValue(cmbFixedShapeBottom);
            _settingsData.FixedShape_Left = SettingsUIHelper.GetShapeValue(cmbFixedShapeLeft);
            _settingsData.FixedShape_Right = SettingsUIHelper.GetShapeValue(cmbFixedShapeRight);

            // 区域形状
            _settingsData.Region_Top_Left = SettingsUIHelper.GetShapeValue(cmbTopLeft);
            _settingsData.Region_Top_Center = SettingsUIHelper.GetShapeValue(cmbTopCenter);
            _settingsData.Region_Top_Right = SettingsUIHelper.GetShapeValue(cmbTopRight);
            _settingsData.Region_Bottom_Left = SettingsUIHelper.GetShapeValue(cmbBottomLeft);
            _settingsData.Region_Bottom_Center = SettingsUIHelper.GetShapeValue(cmbBottomCenter);
            _settingsData.Region_Bottom_Right = SettingsUIHelper.GetShapeValue(cmbBottomRight);
            _settingsData.Region_Left_Top = SettingsUIHelper.GetShapeValue(cmbLeftTop);
            _settingsData.Region_Left_Center = SettingsUIHelper.GetShapeValue(cmbLeftCenter);
            _settingsData.Region_Left_Bottom = SettingsUIHelper.GetShapeValue(cmbLeftBottom);
            _settingsData.Region_Right_Top = SettingsUIHelper.GetShapeValue(cmbRightTop);
            _settingsData.Region_Right_Center = SettingsUIHelper.GetShapeValue(cmbRightCenter);
            _settingsData.Region_Right_Bottom = SettingsUIHelper.GetShapeValue(cmbRightBottom);

            // 外观
            _settingsData.BackgroundColor = txtBgColor.Text;
            _settingsData.TextColor = txtTextColor.Text;
            _settingsData.Opacity = sldOpacity.Value;
            _settingsData.CornerRadius = (int)sldCornerRadius.Value;
            _settingsData.ShowSystemStatus = chkShowSystemStatus.IsChecked ?? true;
            _settingsData.UiFontScale = sldUiFontScale.Value;   // ★ 修复：字号缩放写回（此前不落盘，重启还原 1.0）

            // 网页工具
            _settingsData.WebWidgetUrl = txtWebToolUrl.Text.Trim();

            // 动画与布局
            _settingsData.HorizontalLayoutThreshold = sldHorizontalThreshold.Value;
            _settingsData.TagWidth = sldTagWidth.Value;

            // 任务栏
            _settingsData.TaskbarIconSize = Math.Round(sldIconSize.Value);

            // 剪贴板与便签
            _settingsData.ClipboardMaxCount = (int)sldClipboardMax.Value;
            _settingsData.ClipboardDisplayLength = (int)sldClipboardDisplay.Value;
            _settingsData.ClipboardKeyboardNav = chkClipKeyboardNav.IsChecked ?? true;
            _settingsData.ClipboardShowSourceApp = chkClipShowSourceApp.IsChecked ?? true;
            _settingsData.ClipboardImageMaxWidth = (int)sldClipImageMax.Value;
            _settingsData.ClipboardImageCacheLimitMB = (int)sldClipImageCacheLimit.Value;
            _settingsData.DefaultNoteColor = txtDefaultNoteColor.Text;
            _settingsData.NoteShowTitleByDefault = chkNoteShowTitle.IsChecked ?? true;
            _settingsData.NoteHotkeyNew = txtNoteHotkeyNew.Text.Trim();
            _settingsData.NoteHotkeyDelete = txtNoteHotkeyDelete.Text.Trim();
            _settingsData.NoteHotkeyNext = txtNoteHotkeyNext.Text.Trim();

            // 自适应
            _settingsData.AutoFitOnTrigger = chkAutoFitOnTrigger.IsChecked ?? true;

            // 勿扰
            _settingsData.RememberDndMode = chkRememberDndMode.IsChecked ?? false;

            // ★★★ 动画设置 ★★★
            _settingsData.AnimationsEnabled = chkAnimationsEnabled.IsChecked ?? true;
            _settingsData.ProgrammingModeEnabled = chkProgrammingMode.IsChecked ?? false;

            string transformEasing = SettingsUIHelper.GetEasingValue(
                (cmbTransformEasing.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "立方缓动");
            _settingsData.TransformEasingType = transformEasing;

            // ★ 触发/隐藏动画保存
            _settingsData.ShowAnimationType = LabelToAnimType(cmbShowAnimType, isHide: false);
            _settingsData.HideAnimationType = LabelToAnimType(cmbHideAnimType, isHide: true);
            // ★ 滑块允许 0（哨兵），但真正落盘的必须是有效时长：0 会让动画瞬间完成
            _settingsData.ShowAnimationDurationMs = Math.Max(1, (int)sldShowDuration.Value);
            _settingsData.HideAnimationDurationMs = Math.Max(1, (int)sldHideDuration.Value);
            _settingsData.ShowAnimationZoomFrom = sldShowZoomFrom.Value;
            _settingsData.HideAnimationZoomTo = sldHideZoomTo.Value;
            _settingsData.ShowAnimationOscillations = (int)sldShowOsc.Value;
            _settingsData.HideAnimationOscillations = (int)sldHideOsc.Value;
            _settingsData.ShowAnimationSpringiness = sldShowSpring.Value;
            _settingsData.HideAnimationSpringiness = sldHideSpring.Value;
            _settingsData.TransformDurationMs = (int)sldTransformDuration.Value;
            _settingsData.ContentStabilizeMs = (int)sldContentStabilize.Value;
            _settingsData.SnapRangePx = (int)sldSnapRange.Value;
            _settingsData.HideDelayMs = (int)sldHideDelay.Value;
            _settingsData.FlyDurationMs = (int)sldFlyDuration.Value;

            // ★★★ 引潮模式 ★★★
            _settingsData.ClingModeEnabled = chkClingMode.IsChecked ?? false;

            // ★ 面板点击穿透修饰键（双写：本地副本 + 设置服务）
            string passthroughSel = "Ctrl";
            if (cmbPassthrough.SelectedItem is System.Windows.Controls.ComboBoxItem pasCbi)
                passthroughSel = pasCbi.Tag?.ToString() ?? "Ctrl";
            _settingsData.PassthroughModifier = passthroughSel;
            _settings.PassthroughModifier = passthroughSel;

            // ★★★ 区域防抖 ★★★
            _settingsData.RegionDebounceMs = (int)sldRegionDebounce.Value;

            // ★★★ 触发距离与延时（全局默认 + 逐区域） ★★★
            _settingsData.TriggerDistancePx = (int)sldTrigDist.Value;
            _settingsData.TriggerDelayMs = (int)sldGlbTrig.Value;
            // HideDelayMs 由"动画"标签页的 sldHideDelay 写入（sldGlbHide 已与之联动）
            _settingsData.RegionTriggerDelay = new System.Collections.Generic.Dictionary<string, int>();
            _settingsData.RegionHideDelay = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var (key, c) in _regionDelayControls)
            {
                // 仅记录与全局不同的值；与全局相同 → 不写字典 → 始终跟随全局（后续改全局仍生效）
                if ((int)c.Trig.Value != (int)sldGlbTrig.Value)
                    _settingsData.RegionTriggerDelay[key] = (int)c.Trig.Value;
                if ((int)c.Hide.Value != (int)sldGlbHide.Value)
                    _settingsData.RegionHideDelay[key] = (int)c.Hide.Value;
            }

            // ★★★ 区域面板自定义 ★★★
            foreach (string key in RegionPanelKeys)
            {
                var combo = (ComboBox?)FindName("cmbPanel_" + key);
                if (combo == null) continue;
                string panelValue = GetSelectedPanelValue(combo);
                _settings.SetRegionPanel(key, panelValue);
                // ★ 双写：同步进 _settingsData，否则后续 _settings.Apply(_settingsData) 会用旧副本
                //   把这里刚设置的区域面板覆盖丢失。
                //   ★ 键 → 字段 的映射走 RegionTable（唯一真相源）——这里原来是第四份 switch 副本。
                RegionTable.Find(key)?.SetPanel(_settingsData, panelValue);
            }

            // ★★★ 语言（通用设置） ★★★
            _settingsData.Language = (cmbLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

            // ★★★ 划词翻译 热键（静默校验：冲突/无效则不写入，提示由输入框下方文字展示） ★★★
            string textAiHotkey = txtTextAiHotkey.Text.Trim();
            if (textAiHotkey.Length == 0 ||
                (textAiHotkey != PanelToggleHotkey && HotkeyParser.TryParse(textAiHotkey, out _, out _)))
            {
                _settingsData.TextAiHotkey = textAiHotkey;
                _settingsData.TextAiTargetLanguage = txtTextAiTargetLang.Text.Trim();
            }

            // ★★★ 天气城市（选择器只改文本框，这里统一写入并即时保存） ★★★
            _settings.WeatherCity = txtWeatherCity.Text;
            _settingsData.WeatherCity = txtWeatherCity.Text; // ★ 双写，避免 Apply 覆盖

            // ★★★ 状态栏显示项（修复：此前只加载不写回，勾选后刷新/重启即还原） ★★★
            _settingsData.StatusShowTime = chkStatusTime.IsChecked ?? true;
            _settingsData.StatusShowCpu = chkStatusCpu.IsChecked ?? true;
            _settingsData.StatusShowMemory = chkStatusMemory.IsChecked ?? true;
            _settingsData.StatusShowFps = chkStatusFps.IsChecked ?? true;
            _settingsData.StatusShowVolume = chkStatusVolume.IsChecked ?? true;
            _settingsData.StatusShowNetwork = chkStatusNetwork.IsChecked ?? true;
            _settingsData.StatusShowBattery = chkStatusBattery.IsChecked ?? true;
            _settingsData.StatusShowWeather = chkStatusWeather.IsChecked ?? true;

            // ★★★ 性能模式 ★★★
            string perfMode = _settingsData.PerformanceMode ?? PerformancePresets.Normal;
            if (!PerformancePresets.Matches(_settingsData, perfMode))
            {
                _settingsData.PerformanceMode = PerformancePresets.Custom;
            }

            // ★★★ 键盘呼出区域面板（Ctrl+数字环） ★★★
            _settingsData.RegionHotkeysEnabled = chkRegionHotkeys.IsChecked ?? false;
            _settingsData.RegionHotkeyModifier =
                (cmbRegionHotkeyMod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Ctrl";

            // ★★★ 面板运行帧率（0=自动满帧） ★★★
            _settingsData.PanelFrameRate = (int)sldPanelFrameRate.Value;
            _settings.PanelFrameRate = (int)sldPanelFrameRate.Value;   // ★ 双写：立即生效（帧率即时应用）
        }

        private System.Windows.Threading.DispatcherTimer? _saveTimer;

        // ★ 自动保存钩子去重：惰性页签内容首次选中才进视觉树，页签切换时补挂（防重复挂/漏挂）
        private readonly System.Collections.Generic.HashSet<object> _autoSaveHooked = new();
        private bool _languageHooked;
        private bool _aiKeyHooked;

        /// <summary>防抖自动保存：设置控件变化后 400ms 内未再变化则写入并应用。</summary>
        private void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Threading.DispatcherTimer();
                _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    SaveSettingsNow();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>立即把所有控件值写入设置并保存应用（含语言切换与 AI 配置）。</summary>
        private void SaveSettingsNow()
        {
            try
            {
                ApplyControlsToData();
                // ★ 服务端独有的数据（海床新建的预设/冲突标记）不在窗口控件里，保存前同步进副本，
                //   避免 Apply(_settingsData) 用旧快照把这些数据从配置里抹掉（曾致"新建预设保存后丢失"）
                _settingsData.CustomPanels = _settings.CustomPanels;
                _settingsData.AppliedPresets = _settings.AppliedPresets;
                // ★ 关键修复：把设置副本整体同步进 SettingsManager 再落盘。
                //   之前 ApplyControlsToData 只写本地 _settingsData，_settings.SaveSettings()
                //   保存的是 SettingsManager 内部从未更新的旧数据 → 设置改动全部丢失，
                //   刷新/重启后还原（曾导致"关掉引潮刷新又开"、面板一直跟随鼠标）。
                _settings.Host().Apply(_settingsData);
                SaveAiSettings();
                ShoreHue.Infrastructure.WinApi.WeatherService.ClearCache();
                ShoreHue.UI.Localization.LocalizationManager.Instance.SetCulture(_settingsData.Language);
                _settings.Host().SaveSettings();
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("自动保存设置失败", ex);
            }
        }

        /// <summary>所有设置控件值变化 → 实时自动保存（滑块/勾选/下拉/文本，防抖）。
        /// ★ 海床页（tabSeabed）是编程工具而非设置控件：代码编辑器（txtJsonEditor/xaml 双框）、
        ///   编程模式/预设下拉在选中树节点时会被程序性改写，若也被挂钩会触发全量设置保存 →
        ///   SettingsChanged → 小组件面板重建/重新测量（曾致"点海床列表项 → 面板异常 + 内容变空"）。</summary>
        private void HookAutoSave()
        {
            foreach (var s in FindVisualChildren<Slider>(this))
                if (!IsInsideSeabedPage(s) && _autoSaveHooked.Add(s)) s.ValueChanged += (_, _) => ScheduleSave();
            foreach (var c in FindVisualChildren<CheckBox>(this))
            {
                if (!IsInsideSeabedPage(c) && _autoSaveHooked.Add(c))
                {
                    c.Checked += (_, _) => ScheduleSave();
                    c.Unchecked += (_, _) => ScheduleSave();
                }
            }
            foreach (var c in FindVisualChildren<ComboBox>(this))
                if (!IsInsideSeabedPage(c) && _autoSaveHooked.Add(c)) c.SelectionChanged += (_, _) => ScheduleSave();
            foreach (var t in FindVisualChildren<TextBox>(this))
                if (!IsInsideSeabedPage(t) && _autoSaveHooked.Add(t)) t.TextChanged += (_, _) => ScheduleSave();

            // 语言：选中立即切换界面语言（不等防抖）
            if (!_languageHooked)
            {
                _languageHooked = true;
                cmbLanguage.SelectionChanged += (_, _) =>
                {
                    _settingsData.Language = (cmbLanguage.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                    ShoreHue.UI.Localization.LocalizationManager.Instance.SetCulture(_settingsData.Language);
                    ScheduleSave();
                };
            }

            // ★ AI 密钥（PasswordBox 不是 TextBox，上面的通用钩子挂不到它）：
            //   以前粘贴完密钥直接关窗 = 什么都没保存（关闭时只在防抖计时器还挂着才落盘）。
            if (!_aiKeyHooked)
            {
                _aiKeyHooked = true;
                pwdAiKey.PasswordChanged += (_, _) => ScheduleSave();
            }
        }

        /// <summary>控件是否位于海床页内（沿视觉树上溯）。海床控件不参与设置自动保存。</summary>
        private static bool IsInsideSeabedPage(DependencyObject d)
        {
            DependencyObject? cur = d;
            while (cur != null)
            {
                if (cur is ShoreHue.UI.Settings.Pages.SeabedPage) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        /// <summary>刷新：重新从磁盘加载设置并刷新所有控件显示（面板已通过 SettingsChanged 同步应用）。</summary>
        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettingsNow();
                LoadSettings();
                UpdateCurrentLanguageText();
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("刷新设置失败", ex);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 关闭前立即保存挂起的变化（防抖未触发时兜底）
            if (_saveTimer != null && _saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettingsNow();
            }
            base.OnClosed(e);
        }

        private void BtnOnboarding_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var onboarding = new ShoreHue.UI.Onboarding.OnboardingWindow(
                    noMore => _settings.OnboardingCompleted = noMore,
                    _settings);
                onboarding.Owner = this;
                onboarding.ShowDialog();
            }
            catch
            {
            }
        }
    }
}