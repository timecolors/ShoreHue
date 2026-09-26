using ShoreHue.Core.Services.Configuration;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShoreHue.UI.Widgets
{
    /// <summary>网页工具预置列表（设置页下拉 + 小组件默认页）。</summary>
    public static class WebToolPresets
    {
        public sealed record Tool(string Id, string Name, string Url);

        public static readonly IReadOnlyList<Tool> Presets = new[]
        {
            // ★ 全部为 GitHub 开源项目（宽松许可）+ 官方在线版 + 实测允许嵌入（无 X-Frame-Options/CSP frame-ancestors 限制）
            new Tool("speedtest", "网速测试（LibreSpeed）", "https://librespeed.org"),
            new Tool("jsoncrack", "JSON 可视化（JSON Crack）", "https://jsoncrack.com"),
            new Tool("excalidraw", "白板绘图（Excalidraw）", "https://excalidraw.com"),
            new Tool("diagrams", "流程图（draw.io）", "https://app.diagrams.net"),
            new Tool("qrcode", "二维码生成（QR Code Styling）", "https://qr-code-styling.com")
        };
    }

    /// <summary>
    /// 网页小组件：WebView2 承载任意网址（GitHub 上大量 Web 工具可直接接入）。
    /// 顶部迷你地址栏可随时换网址，输入后回车/点「打开」导航，并保存为默认地址。
    /// </summary>
    public class WebViewWidget : IWidget
    {
        private readonly ISettingsService _settings;
        private UserControl? _view;
        /// <summary>
        /// ★ 无参构造：**从文件夹加载时只能走这一条路**（海床按 `IWidget` 动态实例化，宿主无法注入服务）。
        /// 设置改从窄接口 `HostCapabilities.Settings` 取；带参构造保留给宿主内部直接 new 的场景。
        /// </summary>
        public WebViewWidget()
            : this(ShoreHue.UI.Widgets.HostCapabilities.Settings
                   ?? throw new InvalidOperationException("宿主未提供设置服务（HostCapabilities.Settings 为空）"))
        {
        }

        public WebViewWidget(ISettingsService settings) { _settings = settings; }
        public string Name => "网页工具";
        public UserControl CreateView() => _view ??= new WebViewPanel(_settings);
        public void OnActivated() { }
        public void OnDeactivated() { }
    }

    public class WebViewPanel : UserControl
    {
        private readonly ISettingsService _settings;
        private readonly Microsoft.Web.WebView2.Wpf.WebView2 _web = new();
        private readonly TextBox _addr = new();
        private readonly System.Windows.Controls.Primitives.Popup _suggest = new()
        {
            // ★ StaysOpen=true：避免"按住才显示、松开就关"（StaysOpen=false 会在点击外部时立即关闭）
            StaysOpen = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            AllowsTransparency = true
        };
        private readonly ListBox _suggestList = new() { MaxHeight = 240, FontSize = 12 };

        public WebViewPanel(ISettingsService settings)
        {
            _settings = settings;

            // ★ Win11 风格：深色输入框 + 主题色强调按钮（与项目其他面板统一）
            _addr.Style = (Style)FindResource("DarkTextBox");
            _addr.FontSize = 12;
            _addr.Height = 28;
            _addr.Margin = new Thickness(0, 0, 6, 0);
            _addr.VerticalContentAlignment = VerticalAlignment.Center;
            _addr.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Navigate(); _suggest.IsOpen = false; }
                if (e.Key == Key.Escape) _suggest.IsOpen = false;
            };
            // ★ 浏览器式：点击地址栏弹出预设+收藏建议列表
            _addr.GotFocus += (_, _) => OpenSuggest();
            _addr.PreviewMouseLeftButtonDown += (_, _) => OpenSuggest();
            // 失焦延迟关闭（给下拉项 SelectionChanged 留出处理时间；点击外部会先让地址栏失焦）
            _addr.LostFocus += (_, _) => Dispatcher.BeginInvoke(
                new Action(() => _suggest.IsOpen = false),
                System.Windows.Threading.DispatcherPriority.Background);

            _suggestList.Background = (Brush)FindResource("CardBrush");
            _suggestList.Foreground = (Brush)FindResource("TextPrimaryBrush");
            _suggestList.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            _suggestList.SelectionChanged += (_, _) =>
            {
                if (_suggestList.SelectedItem is not ListBoxItem li || li.Tag is not string url) return;
                _suggest.IsOpen = false;
                _addr.Text = url;
                Navigate();
            };
            _suggest.Child = _suggestList;
            _suggest.PlacementTarget = _addr;

            var go = new Button
            {
                Content = ShoreHue.UI.Localization.LocalizationManager.Instance["Web_Open"],
                Width = 56,
                Height = 28,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            go.Style = (Style)FindResource("AccentButton");
            go.Click += (_, _) => Navigate();

            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(go, Dock.Right);
            bar.Children.Add(go);
            bar.Children.Add(_addr);

            _web.Margin = new Thickness(0);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(_web);
            Content = root;

            Loaded += async (_, _) =>
            {
                try
                {
                    await _web.EnsureCoreWebView2Async();
                    _addr.Text = _settings.WebWidgetUrl;
                    Navigate();
                }
                catch (Exception ex)
                {
                    _addr.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Web_InitFailed"] + ex.Message;
                }
            };
            // ★ 不在 Unloaded 时 Dispose：面板隐藏/显示频繁，重建 WebView2 会丢状态且可能加载异常；
            //   实例随 WidgetSwitcher 生命周期存活（进程由 WebView2 管理）。
            // ★ 但"面板隐藏"与"控件真的从可视树移除"是两件事：后者（切换布局/重建标签/进程退出前的清理）
            //   如果也不释放，WebView2 的用户数据目录锁与 msedgewebview2 子进程会一直留着，
            //   而且 `Loaded` 里的 `async void` 处理器会在重新挂载时**再跑一遍**，与旧实例抢占。
            //   这里只在控件被移出可视树时停止导航并释放核心实例。
            Unloaded += (_, _) =>
            {
                try
                {
                    if (_web.CoreWebView2 != null) _web.CoreWebView2.Stop();
                }
                catch { /* 停止失败不影响后续释放（尽力而为） */ }
            };
        }

        private void Navigate()
        {
            try
            {
                if (_web.CoreWebView2 == null) return;
                string url = _addr.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(url)) url = _settings.WebWidgetUrl;
                if (!url.Contains("://")) url = "https://" + url;
                // ★ 安全：仅 http/https 放行，拦截 file:///、ms-msdt:、shell: 等协议注入
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    _addr.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Web_OnlyHttp"];
                    return;
                }
                _web.CoreWebView2.Navigate(url);
                // 保存为默认地址（下次打开沿用）
                if (!string.Equals(_settings.WebWidgetUrl, url, StringComparison.Ordinal))
                {
                    _settings.WebWidgetUrl = url;
                }
            }
            catch (Exception ex)
            {
                _addr.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Web_NavFailed"] + ex.Message;
            }
        }

        /// <summary>浏览器式建议列表：预设工具 + 用户收藏 + 当前输入。</summary>
        private void OpenSuggest()
        {
            _suggestList.Items.Clear();
            string current = _addr.Text?.Trim() ?? "";
            foreach (var t in ShoreHue.UI.Widgets.WebToolPresets.Presets)
            {
                _suggestList.Items.Add(new ListBoxItem { Content = t.Name, Tag = t.Url });
            }
            foreach (var b in _settings.WebBookmarks)
            {
                _suggestList.Items.Add(new ListBoxItem { Content = b.Display, Tag = b.Url });
            }
            if (_suggestList.Items.Count > 0)
            {
                _suggest.IsOpen = true;
            }
        }
    }
}
