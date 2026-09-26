using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.UI.Settings;
using System;
using System.Windows;
using System.Windows.Input;

namespace ShoreHue.UI.Main
{
    public partial class MainWindow
    {
        // ★ 非模态单实例：设置窗口打开时主面板仍正常工作（可实时看面板效果）
        private SettingsWindow? _settingsWindow;

        /// <summary>无参入口（方法组可转 Action，供托盘/热键/JumpList 使用）。</summary>
        private void OpenSettings() => OpenSettings(null);

        internal void OpenSettings(string? tabName)
        {
            try
            {
                if (_settingsWindow == null || !_settingsWindow.IsLoaded)
                {
                    _settingsWindow = new SettingsWindow(_settingsService, _shortcutService);
                    _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                }
                if (!_settingsWindow.IsVisible)
                {
                    _settingsWindow.Show();
                }
                _settingsWindow.Activate();
                if (!string.IsNullOrEmpty(tabName))
                {
                    _settingsWindow.ActivateTab(tabName);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("打开设置窗口失败", ex);
                MessageBox.Show($"打开设置失败:\n{ex.Message}", "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        /// <summary>隐形分隔线悬停：淡色反馈条 + 左右双箭头（可拖动调竖条宽度，双箭头是正常拖动提示）。
        /// ★ 只用局部 Cursor（XAML 已设 SizeWE），不用 Mouse.OverrideCursor——它是全局的，
        ///   鼠标移到设置窗口等其他窗口时若 Leave/Move 竞态未清除，全局残留 → 到处双箭头。</summary>
        private void IconBarSplitter_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            IconBarSplitter.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        }

        private void IconBarSplitter_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            IconBarSplitter.Background = System.Windows.Media.Brushes.Transparent;
        }

        /// <summary>鼠标在分隔线区域移动时保持局部双箭头（XAML Cursor 已覆盖，无需额外处理）。</summary>
        private void IconBarSplitter_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
        }

        private void ToggleWindow()
        {
            // ★ 切换勿扰模式（与点击图标行为一致）
            bool newState = !_modeService.IsDoNotDisturb;
            _modeService.IsDoNotDisturb = newState;

            if (newState)
            {
                _visibilityController.ForceHide();
            }
            // 退出勿扰时，面板由边缘触发自动显示，不需要手动 Show

            UpdateIconText();
        }

        /// <summary>
        /// 划词翻译 全局热键：呼出小组件面板并翻译当前前台窗口的选中文本。
        /// 热键未设置时由 TextAiWidget 内部提示去设置；捕获失败也会在面板内给出原因。
        /// </summary>
        private async void OnTextAiHotkey()
        {
            try
            {
                if (_modeService.IsDoNotDisturb)
                {
                    _modeService.IsDoNotDisturb = false;
                    UpdateIconText();
                }

                // 先切到小组件内容，再显示面板（避免先显示旧内容闪一下）
                _contentController.ShowWidgetTab("TextAi");
                var widget = _contentController.WidgetSwitcher?.TextAiWidget;
                if (widget == null) return;

                // 钉住：避免边缘定时器在鼠标不在边缘/面板上时立刻隐藏
                _visibilityController.SetHotkeyPinned(true);
                _edgeController.ShowPanelAtAnchor();

                await widget.CaptureAndTranslateAsync();
            }
            catch (Exception ex)
            {
                LogManager.Error("划词翻译 热键处理失败", ex);
            }
        }

        /// <summary>全局热键 Ctrl+Alt+B：切换面板显示/隐藏。</summary>
        private void HotkeyTogglePanel()
        {
            try
            {
                if (_visibilityController == null) return;

                if (_visibilityController.IsVisible)
                {
                    // ★ 解除钉住并隐藏；若面板处于“跟随边缘”状态则直接隐藏
                    _visibilityController.SetHotkeyPinned(false);
                    _visibilityController.Hide();
                }
                else
                {
                    // 在最后锚定的位置呼出面板（若在勿扰模式则先退出勿扰）
                    if (_modeService.IsDoNotDisturb)
                    {
                        _modeService.IsDoNotDisturb = false;
                        UpdateIconText();
                    }
                    // ★ 钉住：避免 30ms 边缘定时器因鼠标不在边缘/面板上而立刻隐藏
                    _visibilityController.SetHotkeyPinned(true);
                    _edgeController.ShowPanelAtAnchor();
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("热键切换面板失败", ex);
            }
        }

        private void ExitApp()
        {
            LogManager.Info("用户退出应用");
            _trayManager?.Dispose();
            Application.Current.Shutdown();
        }

        // ========== Jump List 动作入口（App 通过反射/内部调用） ==========

        /// <summary>Jump List「打开设置」。</summary>
        internal void InvokeJumpListOpenSettings() => OpenSettings();

        /// <summary>Jump List「切换勿扰」。</summary>
        internal void InvokeJumpListToggleDnd() => ToggleWindow();

        /// <summary>Jump List「呼出/隐藏面板」。</summary>
        internal void InvokeJumpListTogglePanel() => HotkeyTogglePanel();

        // ========== 面板鼠标事件（UI 绑定） ==========

        // 注意：MainPanel_MouseEnter 和 MainPanel_MouseLeave 在 XAML 中已绑定
        // 这些方法在 MainWindow.UI.cs 中有完整实现

        // ========== 托盘相关 ==========

        internal void UpdateIconText()
        {
            UpdateIconTextInternal();
        }
    }
}
