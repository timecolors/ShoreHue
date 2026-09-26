using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Localization;
using ShoreHue.UI.Theme;

namespace ShoreHue.UI.Onboarding
{
    /// <summary>
    /// 新手引导（完整教程）：10 页结构化教学——
    /// 欢迎概念 → 屏幕地图（16 区域）→ 任务栏实操 → 应用辅助/画中画 → 小组件 →
    /// 快捷开关与状态栏 → AI 助手配置 → 通知与最近使用 → 个性化 → 完成。
    /// 覆盖全部功能（含多显示器、右上角窗口操作中心等新特性）。
    /// </summary>
    public partial class OnboardingWindow : Window
    {
        private readonly Action<bool>? _onCompleted;
        private readonly ISettingsService? _settings;
        private int _currentPage;
        private const int PageCount = 10;

        private readonly FrameworkElement[] _pages;
        private readonly Border[] _dots = new Border[PageCount];
        private readonly TextBlock[] _nums = new TextBlock[PageCount];
        private readonly TextBlock[] _labels = new TextBlock[PageCount];

        // 步骤名称（本地化键）
        private static readonly string[] StepKeys =
        {
            "Ob_Step_1", "Ob_Step_2", "Ob_Step_3", "Ob_Step_4", "Ob_Step_5",
            "Ob_Step_6", "Ob_Step_7", "Ob_Step_8", "Ob_Step_9", "Ob_Step_10"
        };

        public OnboardingWindow(Action<bool>? onCompleted = null, ISettingsService? settings = null)
        {
            Icon = AppIconHelper.LoadAppIcon();
            InitializeComponent();
            _onCompleted = onCompleted;
            _settings = settings;

            // ★ 划词翻译 快捷键：回显已有设置
            TxtOnboardingTextAiHotkey.Text = _settings?.TextAiHotkey ?? "";
            UpdateOnboardingHotkeyHint();

            // 无论点完成/跳过/直接关闭，都视为完成引导
            Closed += (_, _) =>
            {
                try { _onCompleted?.Invoke(chkNoMore.IsChecked ?? true); } catch { /* 引导只是说明页：回调失败不影响窗口关闭与后续使用 */ }
            };

            _pages = new FrameworkElement[]
            {
                Page1, Page2, Page3, Page4, Page5,
                Page6, Page7, Page8, Page9, Page10
            };

            BuildStepList();
            UpdateNavigation();
        }

        /// <summary>动态构建左侧步骤列表（代码生成，避免 XAML 冗余）。</summary>
        private void BuildStepList()
        {
            StepList.Items.Clear();
            for (int i = 0; i < PageCount; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                var dot = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var num = new TextBlock
                {
                    Text = (i + 1).ToString(), Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dot.Child = num;
                var label = new TextBlock
                {
                    Text = LocalizationManager.Instance[StepKeys[i]],
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    TextWrapping = TextWrapping.Wrap
                };
                row.Children.Add(dot);
                row.Children.Add(label);
                _dots[i] = dot;
                _nums[i] = num;
                _labels[i] = label;
                StepList.Items.Add(row);
            }
        }

        // ========== 划词翻译 快捷键（引导页直接设置） ==========

        private void TxtOnboardingHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // 只读框不参与输入

            if (e.Key == Key.Escape || (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None))
            {
                TxtOnboardingTextAiHotkey.Text = "";
                SaveOnboardingHotkey();
                return;
            }

            string combo = HotkeyParser.Format(e.Key, Keyboard.Modifiers);
            if (combo.Length == 0) return; // 纯修饰键 / 不支持的键：继续等待组合完成

            if (combo == "Ctrl+Alt+B")
            {
                TxtOnboardingHotkeyHint.Text = LocalizationManager.Instance["Set_HotkeyConflict"];
                TxtOnboardingHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
                return; // 冲突时不保存
            }

            TxtOnboardingTextAiHotkey.Text = combo;
            SaveOnboardingHotkey();
        }

        private void BtnOnboardingClearHotkey_Click(object sender, RoutedEventArgs e)
        {
            TxtOnboardingTextAiHotkey.Text = "";
            SaveOnboardingHotkey();
        }

        private void SaveOnboardingHotkey()
        {
            string hotkey = TxtOnboardingTextAiHotkey.Text.Trim();
            UpdateOnboardingHotkeyHint();
            if (_settings != null)
            {
                // setter 会立即保存并触发 SettingsChanged → 主窗口重新注册热键
                _settings.TextAiHotkey = hotkey;
            }
        }

        private void UpdateOnboardingHotkeyHint()
        {
            if (TxtOnboardingHotkeyHint == null) return;
            string hotkey = TxtOnboardingTextAiHotkey.Text.Trim();
            if (hotkey.Length == 0)
            {
                TxtOnboardingHotkeyHint.Text = LocalizationManager.Instance["Ob_HotkeyNotSet"];
                TxtOnboardingHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            }
            else
            {
                TxtOnboardingHotkeyHint.Text = string.Format(LocalizationManager.Instance["Ob_HotkeyEnabled"], hotkey);
                TxtOnboardingHotkeyHint.Foreground = new SolidColorBrush(Color.FromRgb(60, 170, 90));
            }
        }

        // ================================================================
        // ========== 导航 ==========

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < PageCount - 1)
            {
                _currentPage++;
                UpdateNavigation();
            }
            else
            {
                Finish();
            }
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 0)
            {
                _currentPage--;
                UpdateNavigation();
            }
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => Finish();

        private void Finish()
        {
            Close();
        }

        private void UpdateNavigation()
        {
            for (int i = 0; i < PageCount; i++)
            {
                _pages[i].Visibility = i == _currentPage ? Visibility.Visible : Visibility.Collapsed;
                bool active = i == _currentPage;
                bool done = i < _currentPage;

                var brush = active
                    ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))
                    : done
                        ? new SolidColorBrush(Color.FromRgb(0x9A, 0xC8, 0xEB))
                        : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                _dots[i].Background = brush;
                _nums[i].Foreground = active || done
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                _labels[i].Foreground = active
                    ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
                    : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }

            BtnPrev.Visibility = _currentPage > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnNext.Content = _currentPage == PageCount - 1
                ? LocalizationManager.Instance["Ob_Done_Btn"]
                : LocalizationManager.Instance["UI_OnboardingWindow_110"]; // 下一步

            // 完成页：动态显示已配置的划词翻译快捷键
            if (_currentPage == PageCount - 1)
            {
                RefreshDoneHotkeys();
            }
        }

        /// <summary>完成页展示快捷键：面板呼出固定 + 划词翻译 + 数字环（按设置态展示）。</summary>
        private void RefreshDoneHotkeys()
        {
            string hotkey = _settings?.TextAiHotkey?.Trim() ?? "";
            if (string.IsNullOrEmpty(hotkey))
            {
                DoneHotkey2Key.Text = LocalizationManager.Instance["Ob_Done_Hotkey2UnsetKey"];
                DoneHotkey2Desc.Text = LocalizationManager.Instance["Ob_Done_Hotkey2UnsetDesc"];
            }
            else
            {
                DoneHotkey2Key.Text = hotkey;
                DoneHotkey2Desc.Text = LocalizationManager.Instance["Ob_Done_Hotkey2SetDesc"];
            }

            // ★ 数字环：开启时显示「修饰键 + 1-9」，未开启给出开启入口提示
            bool ringEnabled = _settings?.RegionHotkeysEnabled ?? false;
            if (ringEnabled)
            {
                string mod = string.IsNullOrEmpty(_settings?.RegionHotkeyModifier)
                    ? "Ctrl"
                    : _settings!.RegionHotkeyModifier;
                DoneHotkey3Key.Text = mod + " + 1–9";
                DoneHotkey3Desc.Text = LocalizationManager.Instance["Ob_Done_Hotkey3DescSet"];
            }
            else
            {
                DoneHotkey3Key.Text = LocalizationManager.Instance["Ob_Done_Hotkey3KeyUnset"];
                DoneHotkey3Desc.Text = LocalizationManager.Instance["Ob_Done_Hotkey3DescUnset"];
            }
        }
    }
}
