using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// GitHub 设备流登录弹窗（替代系统 MessageBox）：
    /// 大号等宽验证码、一键复制、打开即自动复制到剪贴板、一键打开授权页、内置轮询。
    /// 主题跟随系统（浅色白底 / 深色黑底，与共享平台一致）。
    /// </summary>
    public sealed class DeviceLoginWindow : Window
    {
        private readonly string _uri, _userCode, _deviceCode;
        private readonly System.Threading.CancellationTokenSource _cts = new();
        private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly Button _copyBtn = new();
        private readonly Button _browserBtn = new();

        public DeviceLoginWindow(string uri, string userCode, string deviceCode)
        {
            _uri = uri; _userCode = userCode; _deviceCode = deviceCode;

            bool light = ShoreHue.Infrastructure.Utils.SystemTheme.IsLightTheme();
            var cBg = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x1E, 0x1E, 0x1E);
            var cCard = light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x2A);
            var cText = light ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
            var cSub = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0xBB, 0xBB, 0xBB);
            var cBorder = light ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x40, 0x40, 0x40);
            var cAccent = light ? Color.FromRgb(0x0A, 0x66, 0xC2) : Color.FromRgb(0x5C, 0xA8, 0xF5);

            Title = "使用 GitHub 登录";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(cBg);
            FontFamily = new FontFamily("Microsoft YaHei UI");

            // ===== 布局 =====
            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

            // 标题
            var title = new TextBlock
            {
                Text = "使用 GitHub 登录",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(cText)
            };
            root.Children.Add(title);

            // 说明
            root.Children.Add(new TextBlock
            {
                Text = "在浏览器打开下面的 GitHub 授权页面，登录后在网页里输入验证码。授权完成会自动登录。",
                FontSize = 12,
                Foreground = new SolidColorBrush(cSub),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

            // 授权地址（可点击）
            var uriText = new TextBlock
            {
                Text = _uri,
                FontSize = 12,
                Foreground = new SolidColorBrush(cAccent),
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            };
            uriText.MouseLeftButtonUp += (s, e) => OpenBrowser();
            root.Children.Add(uriText);

            // 打开浏览器按钮
            _browserBtn.Content = "打开 GitHub 授权页面";
            _browserBtn.Height = 30; _browserBtn.FontSize = 12; _browserBtn.Margin = new Thickness(0, 10, 0, 0);
            _browserBtn.Style = (Style)FindResource("Win11Button");
            _browserBtn.Click += (s, e) => OpenBrowser();
            root.Children.Add(_browserBtn);

            // 验证码卡片
            var card = new Border
            {
                Background = new SolidColorBrush(cCard),
                BorderBrush = new SolidColorBrush(cBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var codeBox = new TextBox
            {
                Text = _userCode,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(cText),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.IBeam
            };
            codeBox.Focus();
            codeBox.SelectAll();
            Grid.SetColumn(codeBox, 0);
            cardGrid.Children.Add(codeBox);

            _copyBtn.Content = "复制验证码";
            _copyBtn.Height = 26; _copyBtn.FontSize = 11; _copyBtn.Margin = new Thickness(10, 0, 0, 0);
            _copyBtn.Style = (Style)FindResource("Win11Button");
            _copyBtn.Click += (s, e) => CopyCode();
            Grid.SetColumn(_copyBtn, 1);
            cardGrid.Children.Add(_copyBtn);

            card.Child = cardGrid;
            root.Children.Add(card);

            // 复制提示
            root.Children.Add(new TextBlock
            {
                Text = "验证码已自动复制到剪贴板，可直接粘贴到授权页面",
                FontSize = 11,
                Foreground = new SolidColorBrush(cSub),
                Margin = new Thickness(0, 8, 0, 0)
            });

            // 状态
            _status.Text = "正在获取授权…";
            _status.Foreground = new SolidColorBrush(cSub);
            _status.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(_status);

            // 底部按钮
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "取消", Width = 80, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            cancel.Style = (Style)FindResource("Win11Button");
            cancel.Click += (s, e) => Close();
            bottom.Children.Add(cancel);
            var done = new Button { Content = "已完成授权", Width = 100, Height = 28, FontSize = 12 };
            done.Style = (Style)FindResource("Win11Button");
            done.Click += (s, e) => { if (_finished) Close(); };
            bottom.Children.Add(done);
            root.Children.Add(bottom);

            Content = root;
            Loaded += OnLoaded;
        }

        private bool _finished;

        private void OpenBrowser()
        {
            try { Process.Start(new ProcessStartInfo(_uri) { UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = "打开浏览器失败：" + ex.Message; }
        }

        private async void CopyCode()
        {
            try
            {
                await SetClipboardAsync(_userCode);
                _copyBtn.Content = "✓ 已复制";
                await Task.Delay(1200);
                _copyBtn.Content = "复制验证码";
            }
            catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
        }

        /// <summary>写剪贴板（WPF 剪贴板忙时重试，避免弹错误）。</summary>
        private static async Task SetClipboardAsync(string text)
        {
            Exception? last = null;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return; }
                catch (Exception ex) { last = ex; }
                await Task.Delay(120);
            }
            throw last ?? new InvalidOperationException("剪贴板不可用");
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 自动复制验证码：复制不上也不影响授权（验证码就显示在界面上，用户可手抄）
            try { await SetClipboardAsync(_userCode); }
            catch (Exception ex) { ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[登录] 自动复制验证码失败（可手动复制）：{ex.Message}"); }
            _status.Text = "等待授权…（请在浏览器输入验证码 " + _userCode + "）";

            for (int i = 0; i < 60 && !_cts.IsCancellationRequested; i++)
            {
                bool ok;
                try { ok = await GitHubMarketService.PollForTokenAsync(_deviceCode, 5); }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested) return;
                    _status.Text = "轮询失败：" + ex.Message;
                    return;
                }
                if (_cts.IsCancellationRequested) return;
                if (ok)
                {
                    _finished = true;
                    _status.Text = "已登录：" + GitHubMarketService.CurrentUser;
                    _status.Foreground = new SolidColorBrush(Colors.SeaGreen);
                    await Task.Delay(1200);
                    if (!_cts.IsCancellationRequested) { DialogResult = true; Close(); }
                    return;
                }
                if (i % 12 == 11) _status.Text = "仍在等待授权…（" + (60 - i) + " 秒内请在浏览器完成）";
            }
            if (!_cts.IsCancellationRequested)
                _status.Text = "授权超时，请重新点击登录重试";
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            _cts.Dispose();
            base.OnClosed(e);
        }
    }
}
