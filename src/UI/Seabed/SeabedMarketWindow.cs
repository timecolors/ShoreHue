using ShoreHue.Core.Models;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.UI.Widgets.Dynamic;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 其他海床：按分类（对齐海床树）浏览在线市场，Win11 资源管理器式查看（大图标/小图标/列表/详细信息）；
    /// 本地 .dbp 导出/导入；安装走权限确认 + Defender + 沙箱编译。
    /// </summary>
    public sealed class SeabedMarketWindow : Window
    {
        /// <summary>在线市场 CDN 根。★ jsDelivr 生产建议用 @版本号 而非 @master（防 CDN 跟随分支滚动更新）；
        /// 当前随 master 便于开发期即时验证，正式发布时随版本号切换为 @vX.Y.Z（见 docs/HANDOFF.md）。</summary>
        public const string MarketBase = "https://cdn.jsdelivr.net/gh/timecolors/ShoreHue@master/market";

        /// <summary>本客户端支持的市场包协议版本（manifest.apiVersion）。未来包格式破坏性变更时 bump，旧客户端据此拒绝新包。</summary>
        public const int SupportedApiVersion = 1;

        /// <summary>查看模式（Win11 资源管理器式）。</summary>
        private enum ViewMode { LargeIcon, SmallIcon, List, Details }

        /// <summary>市场分类（对齐海床树一级分类；小组件/面板功能单列便于浏览）。</summary>
        private static readonly string[] Categories =
        {
            "全部", "小组件", "面板功能", "面板设计", "动画", "外观", "交互", "状态栏"
        };

        private readonly ShoreHue.UI.Settings.Pages.SeabedPage _page;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly List<MarketItem> _allPackages = new();
        private readonly ListBox _categoryList = new() { FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly ListBox _content = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly TextBlock _empty = new() { Text = "暂无内容", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };
        private readonly ScrollViewer _contentScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _status = new()
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        private ViewMode _viewMode = ViewMode.LargeIcon;
        private string _category = "全部";

        // ★ 跟随系统主题（与设置窗口一致）：浅色白底 / 深色黑底
        private readonly Color _cBg, _cCard, _cText, _cSub, _cBorder;

        // ★ 官方验证徽章色：暖金，浅色(#B8860B)与深色(#F5C542)主题下均清晰
        private static readonly Color OfficialBadgeColor = Color.FromRgb(0xF5, 0xC5, 0x42);

        // ===== 详情页 =====
        private readonly Grid _detail = new() { Visibility = Visibility.Collapsed };
        private readonly TextBox _codeBox = new()
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 8)
        };
        private readonly TextBlock _detailInfo = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private string _detailSource = "";      // 当前详情源码（main.cs）
        private string _detailManifest = "";    // 当前详情 manifest（供下载）
        private readonly System.Collections.Generic.Dictionary<string, string> _detailFileSizes = new();  // 文件名→可读大小
        private MarketItem? _currentItem;       // 当前详情包（删除时校验作者）
        private int _detailSeq;                 // ★ 详情加载序号：防快速连点竞态（旧请求结果丢弃）
        private readonly Button _loginBtn = new();   // 登录 GitHub 按钮（文本随登录状态更新）
        private readonly Button _syncBtn = new();    // 同步设置按钮（上传/下载个人配置，登录后可用）

        /// <summary>在线市场条目。</summary>
        public sealed class MarketItem
        {
            // ★ 用属性（DataTemplate Binding 只能绑定属性，不能绑定字段）
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Kind { get; set; } = "Widget";
            public string Category { get; set; } = "小组件";
            public string Version { get; set; } = "";
            /// <summary>包协议版本（manifest.apiVersion；缺省 1）。</summary>
            public int ApiVersion { get; set; } = 1;
            public string Author { get; set; } = "";
            public string Description { get; set; } = "";
            public List<string> Permissions { get; set; } = new();
            /// <summary>发布者 GitHub 数字 ID（删除时身份校验；老包可能为 null）。</summary>
            public long? PublisherId { get; set; }
            /// <summary>官方验证（仓库维护者背书；index.json 中 official:true）。</summary>
            public bool IsOfficial { get; set; }
            /// <summary>包内文件清单（manifest.files，下载端按此拉取）。</summary>
            public List<string> Files { get; set; } = new();

            /// <summary>包形态标注（预览用）：XAML 形态 / 纯代码 / 整套预设 / 配置代码。</summary>
            public string FormatLabel
            {
                get
                {
                    bool hasXaml = Files.Any(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        && !f.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase));
                    if (hasXaml) return "XAML 形态";
                    if (Kind == "Full") return "整套预设";
                    if (Kind == "Config") return "配置代码";
                    return "纯代码";
                }
            }

            public string MetaLine =>
                (IsOfficial ? "★官方 · " : "") + Id + " · " + Author +
                (string.IsNullOrEmpty(Version) ? "" : " · v" + Version) +
                (Permissions.Count > 0 ? " · ⚠ " + string.Join(",", Permissions) : "");

            public string DetailLine =>
                ((IsOfficial ? "★官方 · " : "") + Id + " · " + Author + (string.IsNullOrEmpty(Version) ? "" : " · v" + Version) +
                 (Permissions.Count > 0 ? " · ⚠ " + string.Join(",", Permissions) : "")) +
                (string.IsNullOrEmpty(Description) ? "" : System.Environment.NewLine + Description);

            /// <summary>建议安装目录（海床 seabed 文件夹内相对路径；按 kind/分类映射）。</summary>
            public string InstallDirLabel
            {
                get
                {
                    string folder = Kind == "Widget" ? "面板/小组件"
                        : Kind == "Panel" ? "面板/面板功能"
                        : Kind == "Full" ? "整套预设（设置 → 预设）"
                        : "海床对应分类";
                    return folder;
                }
            }

            /// <summary>文件类型清单（按扩展名分组合并，如 XAML + 代码后置 / 纯 C#）。</summary>
            public string FileTypesLabel
            {
                get
                {
                    var exts = Files.Select(f => System.IO.Path.GetExtension(f).ToLowerInvariant())
                        .Where(x => x.Length > 1).Distinct().ToList();
                    if (exts.Contains(".xaml") && exts.Contains(".cs")) return "XAML + 代码后置 (.xaml / .xaml.cs)";
                    if (exts.Contains(".xaml")) return "XAML 界面 (.xaml)";
                    if (exts.Contains(".json")) return "配置 / 预设 (.json)";
                    if (exts.Count == 0) return Kind == "Full" ? "整套预设" : "纯 C# (.cs)";
                    return string.Join(" / ", exts.Select(e2 => "." + e2.TrimStart('.')));
                }
            }
        }

        public SeabedMarketWindow(ShoreHue.UI.Settings.Pages.SeabedPage page)
        {
            _page = page;
            bool light = ShoreHue.Infrastructure.Utils.SystemTheme.IsLightTheme();
            _cBg = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x1E, 0x1E, 0x1E);
            _cCard = light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x2A);
            _cText = light ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
            _cSub = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0xBB, 0xBB, 0xBB);
            _cBorder = light ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x40, 0x40, 0x40);

            Title = "其他海床";
            Width = 760;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;   // ★ 可调整大小（内容多时拖大窗口看全）
            MinWidth = 640;
            MinHeight = 480;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(_cBg);
            Closed += (_, _) => { try { _http.Dispose(); } catch { } };

            // ===== 顶部工具栏（Win11Button 与设置页一致） =====
            var export = new Button { Content = "导出当前预设", Width = 110, Height = 28, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            export.Style = (Style)FindResource("Win11Button");
            export.Click += Export_Click;
            var import = new Button { Content = "导入 .dbp…", Width = 100, Height = 28, FontSize = 11, Margin = new Thickness(0, 0, 10, 0) };
            import.Style = (Style)FindResource("Win11Button");
            import.Click += Import_Click;
            var refresh = new Button { Content = "刷新列表", Width = 80, Height = 28, FontSize = 11, Margin = new Thickness(0, 0, 10, 0) };
            refresh.Style = (Style)FindResource("Win11Button");
            refresh.Click += RefreshOnline_Click;
            var publish = new Button { Content = "放流", Width = 90, Height = 28, FontSize = 11, Margin = new Thickness(0, 0, 10, 0) };
            publish.Style = (Style)FindResource("Win11Button");
            publish.ToolTip = "把当前选中的海床项发布到市场，其他用户即可拾贝";
            publish.Click += Publish_Click;
            _loginBtn.Content = "登录 GitHub";
            _loginBtn.Width = 100; _loginBtn.Height = 28; _loginBtn.FontSize = 11; _loginBtn.Margin = new Thickness(0, 0, 10, 0);
            _loginBtn.Style = (Style)FindResource("Win11Button");
            _loginBtn.Click += Login_Click;
            _syncBtn.Content = "☁ 同步设置";
            _syncBtn.Width = 100; _syncBtn.Height = 28; _syncBtn.FontSize = 11; _syncBtn.Margin = new Thickness(0, 0, 10, 0);
            _syncBtn.Style = (Style)FindResource("Win11Button");
            _syncBtn.IsEnabled = false;   // 登录后启用
            _syncBtn.ToolTip = "把当前电脑的设置上传到 GitHub，或从云端下载恢复（换电脑/重装后使用）";
            _syncBtn.Click += SyncConfig_Click;

            // 查看切换（Win11 资源管理器式）
            var viewLabel = new TextBlock { Text = "查看:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            var btnLarge = MakeViewButton("大图标");
            var btnSmall = MakeViewButton("小图标");
            var btnList = MakeViewButton("列表");
            var btnDetails = MakeViewButton("详细信息");
            btnLarge.Click += (_, _) => SetView(ViewMode.LargeIcon);
            btnSmall.Click += (_, _) => SetView(ViewMode.SmallIcon);
            btnList.Click += (_, _) => SetView(ViewMode.List);
            btnDetails.Click += (_, _) => SetView(ViewMode.Details);
            var views = new StackPanel { Orientation = Orientation.Horizontal };
            views.Children.Add(btnLarge); views.Children.Add(btnSmall); views.Children.Add(btnList); views.Children.Add(btnDetails);

            // ★ 工具栏两行：行1 = 本地操作（导出/导入/刷新）；行2 = 账号 + 查看方式（防溢出 760px 窗口）
            var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(export); row1.Children.Add(import); row1.Children.Add(publish); row1.Children.Add(refresh);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row2.Children.Add(_loginBtn);
            row2.Children.Add(_syncBtn);
            row2.Children.Add(viewLabel); row2.Children.Add(views);
            toolbar.Children.Add(row1);
            toolbar.Children.Add(row2);

            // ===== 左侧分类导航 =====
            foreach (var c in Categories)
            {
                _categoryList.Items.Add(new ListBoxItem { Content = c, Tag = c, Padding = new Thickness(8, 4, 8, 4), FontSize = 12 });
            }
            _categoryList.SelectedIndex = 0;
            _categoryList.SelectionChanged += (_, _) =>
            {
                if (_categoryList.SelectedItem is ListBoxItem li && li.Tag is string c)
                {
                    _category = c;
                    RefreshContent();
                }
            };
            var categoryPanel = new Border
            {
                Child = _categoryList,
                Width = 150,
                VerticalAlignment = VerticalAlignment.Top,   // ★ 高度按内容自适应，不填满（避免下方空白）
                Background = new SolidColorBrush(_cCard),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 8, 6, 8),
                Margin = new Thickness(0, 0, 10, 0),
                BorderBrush = new SolidColorBrush(_cBorder),
                BorderThickness = new Thickness(1)
            };

            // ===== 右侧内容区 =====
            // ★ 禁水平滚动：WrapPanel 按视口宽换行（否则排一行被截断）
            ScrollViewer.SetHorizontalScrollBarVisibility(_content, ScrollBarVisibility.Disabled);
            // ★ 视图项点击 → 打开详情页（不自动下载；详情页可下载/复制）
            _content.SelectionChanged += (_, _) =>
            {
                if (_content.SelectedItem is ListBoxItem lbi && lbi.Tag is MarketItem mi)
                {
                    _content.SelectedItem = null;
                    // ★ 安全：包 id 仅允许 英文/数字/下划线/连字符（防 index.json 被注入 ../../ 路径）
                    if (IsValidMarketId(mi.Id))
                    {
                        _ = ShowDetailAsync(mi);
                    }
                    else
                    {
                        _status.Text = "包 id 非法，已忽略（" + mi.Id + "）";
                    }
                }
            };
            _contentScroll.Content = _content;   // ★ ListBox 挂进 ScrollViewer（此前丢失导致不渲染）
            var contentPanel = new Border
            {
                Child = new Grid
                {
                    Children = { _contentScroll, _empty }
                },
                Background = new SolidColorBrush(_cCard),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                BorderBrush = new SolidColorBrush(_cBorder),
                BorderThickness = new Thickness(1)
            };

            // ===== 详情页（点包后切换显示；可下载/复制代码） =====
            var back = new Button { Content = "← 返回列表", Width = 90, Height = 26, FontSize = 11 };
            back.Style = (Style)FindResource("Win11Button");
            back.Click += (_, _) => { _detail.Visibility = Visibility.Collapsed; _contentScroll.Visibility = Visibility.Visible; _empty.Visibility = _allPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed; };
            var download = new Button { Content = "拾贝", Width = 90, Height = 28, FontSize = 11, Margin = new Thickness(0, 0, 8, 0) };
            download.Style = (Style)FindResource("Win11Button");
            download.Click += (_, _) => { if (!string.IsNullOrEmpty(_detailManifest)) _ = InstallFromDetailAsync(); };
            var copy = new Button { Content = "复制代码", Width = 100, Height = 28, FontSize = 11 };
            copy.Style = (Style)FindResource("Win11Button");
            copy.Click += (_, _) =>
            {
                try { if (!string.IsNullOrEmpty(_detailSource)) { System.Windows.Clipboard.SetText(_detailSource); _status.Text = "代码已复制到剪贴板"; } }
                catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
            };
            // ★ 删除：生成删除指引（删除是仓库操作，需仓库写权限；无账号体系不做假身份验证）
            var del = new Button { Content = "删除此包", Width = 100, Height = 28, FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
            del.Style = (Style)FindResource("Win11Button");
            del.Foreground = System.Windows.Media.Brushes.Firebrick;
            del.Click += (_, _) => ShowDeleteGuide();
            var detailBtns = new StackPanel { Orientation = Orientation.Horizontal };
            detailBtns.Children.Add(download);
            detailBtns.Children.Add(copy);
            detailBtns.Children.Add(del);
            var detailHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            detailHead.Children.Add(back);
            var detailTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            detailHead.Children.Add(detailTitle);
            _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _detail.Children.Add(detailHead);
            _detail.Children.Add(_detailInfo);
            _detail.Children.Add(_codeBox);
            _detail.Children.Add(detailBtns);
            Grid.SetRow(detailHead, 0);
            Grid.SetRow(_detailInfo, 1);
            Grid.SetRow(_codeBox, 2);
            Grid.SetRow(detailBtns, 3);

            var contentHost = new Grid();
            contentHost.Children.Add(contentPanel);
            contentHost.Children.Add(_detail);

            var body = new Grid { Margin = new Thickness(0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(categoryPanel, 0);
            Grid.SetColumn(contentHost, 1);
            body.Children.Add(categoryPanel);
            body.Children.Add(contentHost);
            var terms = new TextBlock
            {
                Text = "上传即代表你保证所上传内容拥有全部权利、不侵犯任何第三方版权，并同意 ShoreHue 在开源许可范围内免费分发。",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(_cSub),
                Margin = new Thickness(0, 8, 0, 0)
            };

            var panel = new Grid { Margin = new Thickness(16) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // ★ body 占满剩余高度
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(toolbar, 0);
            Grid.SetRow(body, 1);
            Grid.SetRow(_status, 2);
            Grid.SetRow(terms, 3);
            panel.Children.Add(toolbar);
            panel.Children.Add(body);
            panel.Children.Add(_status);
            panel.Children.Add(terms);
            Content = panel;

            // ★ 前景色在配色初始化后设置
            _status.Foreground = new SolidColorBrush(_cSub);
            _empty.Foreground = new SolidColorBrush(_cSub);
            _categoryList.Foreground = new SolidColorBrush(_cText);

            Loaded += async (_, _) =>
            {
                await GitHubMarketService.TryLoadTokenAsync();
                UpdateLoginButton();
                await RefreshOnlineAsync();
            };
        }

        private Button MakeViewButton(string text)
        {
            var b = new Button { Content = text, Width = 60, Height = 26, FontSize = 11, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(4, 0, 4, 0) };
            b.Style = (Style)FindResource("Win11Button");   // ★ 与设置页按钮一致（浅色底深色字，浅色主题下清晰可见）
            return b;
        }

        private void SetView(ViewMode mode)
        {
            _viewMode = mode;
            RefreshContent();
        }

        /// <summary>按当前分类 + 视图模式重建内容区。</summary>
        private void RefreshContent()
        {
            IEnumerable<MarketItem> items = _allPackages;
            if (_category != "全部")
            {
                items = _allPackages.Where(p => p.Category == _category);
            }
            var list = items.ToList();
            _empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _contentScroll.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            // ★ ItemsPanel 不能设为 null（ListBox 默认 StackPanel 面板会被破坏 → 不渲染）；只重置 ItemsSource
            _content.ItemsSource = null;
            _content.ItemsSource = BuildViewItems(list);
            _content.SelectedIndex = -1;
        }

        private System.Collections.IEnumerable BuildViewItems(List<MarketItem> items)
        {
            // ★ 视图的 ItemsPanel：大图标/小图标 → WrapPanel 横向排布；列表/详细 → StackPanel 垂直
            bool wrap = _viewMode == ViewMode.LargeIcon || _viewMode == ViewMode.SmallIcon;
            _content.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(
                wrap ? typeof(WrapPanel) : typeof(StackPanel)));

            var result = new List<ListBoxItem>();
            foreach (var item in items)
            {
                result.Add(new ListBoxItem
                {
                    Content = BuildItemView(item),
                    Tag = item,          // ★ 用 Tag 携带 MarketItem（SelectedItem 是 ListBoxItem，取 Tag 还原数据）
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, wrap ? 0 : 0, wrap ? 4 : 4),
                    // WrapPanel 里不拉伸（按卡片宽排）；列表/详细占满行
                    HorizontalContentAlignment = wrap ? HorizontalAlignment.Left : HorizontalAlignment.Stretch
                });
            }
            return result;
        }

        /// <summary>构建包名称文本（官方验证包在名称后追加高亮「★官方」标记）。</summary>
        private TextBlock BuildNameText(string name, bool isOfficial, double fontSize, FontWeight weight, Color color, bool wrap = false)
        {
            var tb = new TextBlock
            {
                FontSize = fontSize,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tb.Inlines.Add(new System.Windows.Documents.Run(name));
            if (isOfficial)
            {
                var badge = new System.Windows.Documents.Run("  ★官方")
                {
                    FontSize = Math.Max(9, fontSize - 1.5),
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(OfficialBadgeColor)
                };
                tb.Inlines.Add(badge);
            }
            return tb;
        }

        /// <summary>手动构建包卡片/行（按视图模式）。</summary>
        private FrameworkElement BuildItemView(MarketItem item)
        {
            if (_viewMode == ViewMode.LargeIcon)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x12, _cText.R, _cText.G, _cText.B)),
                    CornerRadius = new CornerRadius(8),
                    Width = 132,
                    Margin = new Thickness(0, 0, 8, 8),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(8)
                };
                var sp = new StackPanel();
                sp.Children.Add(BuildNameText(item.Name, item.IsOfficial, 12, FontWeights.SemiBold, _cText, wrap: true));
                sp.Children.Add(new TextBlock { Text = item.MetaLine, FontSize = 10, Foreground = new SolidColorBrush(_cSub), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
                card.Child = sp;
                return card;
            }
            if (_viewMode == ViewMode.SmallIcon)
            {
                var b = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x12, _cText.R, _cText.G, _cText.B)),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(6, 4, 6, 4),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                b.Child = new TextBlock { Text = item.Name, FontSize = 11, Foreground = new SolidColorBrush(_cText) };
                return b;
            }
            if (_viewMode == ViewMode.List)
            {
                // ★ 列表视图（Win11 资源管理器式紧凑行）：名称 + 类型 · 作者 · 版本
                var row = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 8, 4),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var rp = new Grid();
                rp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nameTb = BuildNameText(item.Name, item.IsOfficial, 12, FontWeights.SemiBold, _cText);
                nameTb.VerticalAlignment = VerticalAlignment.Center;
                rp.Children.Add(nameTb);
                rp.Children.Add(new TextBlock
                {
                    Text = (item.Kind ?? "Widget") + " · " + item.Author + (string.IsNullOrEmpty(item.Version) ? "" : " · v" + item.Version),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(_cSub),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                });
                Grid.SetColumn(rp.Children[1], 1);
                row.Child = rp;
                return row;
            }
            // ★ 详细信息视图（Win11 资源管理器式多列）：名称 + 作者/版本/分类/权限/描述
            var detailRow = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var dp = new StackPanel();
            dp.Children.Add(BuildNameText(item.Name, item.IsOfficial, 12, FontWeights.SemiBold, _cText));
            dp.Children.Add(new TextBlock
            {
                Text = item.Author + (string.IsNullOrEmpty(item.Version) ? "" : "  ·  v" + item.Version) +
                       "  ·  " + (item.Category ?? "小组件") +
                       (item.Permissions.Count > 0 ? "  ·  ⚠ " + string.Join(",", item.Permissions) : ""),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(_cSub),
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrEmpty(item.Description))
            {
                dp.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(_cSub),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 36
                });
            }
            detailRow.Child = dp;
            return detailRow;
        }
        // ==================== 本地：导出/导入 ====================

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cp = _page.CurrentSelectedCustom();
                if (cp != null)
                {
                    var dlg = new SaveFileDialog
                    {
                        Filter = "ShoreHue 预设包 (*.dbp)|*.dbp",
                        FileName = cp.Name + SeabedPackage.Extension,
                        Title = "导出单预设"
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    string? err = SeabedPackage.ExportCustom(cp, dlg.FileName);
                    _status.Text = err ?? "已导出「" + cp.Name + "」 · 权限：" +
                        WidgetPermissions.Describe(WidgetPermissions.Detect(cp.Source ?? ""));
                    return;
                }

                string? presetName = _page.CurrentSelectedPresetName;
                if (!string.IsNullOrEmpty(presetName))
                {
                    var data = PresetManager.LoadPreset(presetName);
                    if (data == null) { _status.Text = "读取预设失败"; return; }
                    var dlg = new SaveFileDialog
                    {
                        Filter = "ShoreHue 预设包 (*.dbp)|*.dbp",
                        FileName = presetName + SeabedPackage.Extension,
                        Title = "导出整套预设"
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    string? err = SeabedPackage.ExportFullPreset(presetName, data, dlg.FileName);
                    _status.Text = err ?? "已导出整套预设「" + presetName + "」";
                    return;
                }

                _status.Text = "请先在海床树选中一个单预设，或在下拉框选整套预设";
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "ShoreHue 预设包 (*.dbp)|*.dbp|所有文件|*.*",
                    Title = "导入预设包"
                };
                if (dlg.ShowDialog(this) != true) return;

                _status.Text = "正在使用 Windows Defender 扫描…";
                var scan = await ShoreHue.Infrastructure.WinApi.DefenderScanner.ScanFileAsync(dlg.FileName);
                if (scan.Result == ShoreHue.Infrastructure.WinApi.DefenderScanner.ScanResult.ThreatFound)
                {
                    _status.Text = scan.Detail + "，已阻止导入";
                    return;
                }
                string scanNote = scan.Result == ShoreHue.Infrastructure.WinApi.DefenderScanner.ScanResult.Clean
                    ? "Defender 扫描：未发现已知威胁"
                    : "⚠ Defender 扫描不可用（仍按风险提示导入）";

                var result = SeabedPackage.Import(dlg.FileName, out string? err);
                if (result == null) { _status.Text = err; return; }
                if (!ConfirmPermissions(result.Name, result.Permissions)) return;
                InstallResult(result, scanNote);
            }
            catch (Exception ex) { _status.Text = ex.Message; }
        }

        /// <summary>放流：把当前选中的海床自定义项发布到市场（需登录 + 沙箱通过）。</summary>
        private void Publish_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!GitHubMarketService.IsLoggedIn)
                {
                    var needLogin = new ConfirmDialog("放流", "请先登录 GitHub 才能发布", "去登录", "取消") { Owner = this };
                    if (needLogin.ShowDialog() == true) { _loginBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                    return;
                }
                var cp = _page.CurrentSelectedCustom();
                if (cp == null || string.IsNullOrWhiteSpace(cp.Source))
                {
                    _status.Text = "请先在海床页选中一个有源码的自定义项（小组件/面板/配置代码）";
                    return;
                }
                // ★ 安全：发布前沙箱校验（防发布恶意代码到市场祸害他人）
                string sandboxErr = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SandboxErrors(cp.Source);
                if (sandboxErr.Length > 0)
                {
                    MessageBox.Show(this, "该代码被沙箱拦截，不能发布：\n\n" + sandboxErr,
                        "放流 · 安全检查", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var permissions = ShoreHue.UI.Widgets.Dynamic.WidgetPermissions.Detect(cp.Source);
                // ★ 多形态上传：收集节点海床文件夹里的附加文件（.xaml/.xaml.cs 等，与 .dbp 导出一致）
                var extra = new System.Collections.Generic.List<GitHubMarketService.PackageFile>();
                try
                {
                    string group = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.MapCategoryToFolder(cp.Category);
                    string safeName = SanitizeForFolder(cp.Name);
                    string nodeDir = System.IO.Path.Combine(
                        ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.RootDir, group, safeName);
                    if (System.IO.Directory.Exists(nodeDir))
                    {
                        foreach (var f in System.IO.Directory.EnumerateFiles(nodeDir))
                        {
                            string fn = System.IO.Path.GetFileName(f);
                            if (fn == "main.cs" || fn == "config.json" || fn == "manifest.json") continue;
                            if (fn.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            {
                                extra.Add(new GitHubMarketService.PackageFile(fn, System.IO.File.ReadAllText(f)));
                            }
                        }
                    }
                }
                catch { }
                var win = new PublishWindow(cp.Source, cp.Name, cp.Kind ?? "Widget",
                    cp.BaseType ?? "Widget", cp.ParentKey ?? "", cp.SourceKey ?? "", cp.Category, permissions, extra) { Owner = this };
                win.ShowDialog();
                if (win.Published)
                {
                    _status.Text = "已放流「" + (string.IsNullOrWhiteSpace(win.NameResult) ? cp.Name : win.NameResult) + "」";
                    _ = RefreshOnlineAsync();   // 刷新市场列表
                }
            }
            catch (Exception ex)
            {
                _status.Text = "放流失败：" + ex.Message;
            }
        }

        private bool ConfirmPermissions(string name, List<string> permissions)
        {
            string perms = WidgetPermissions.Describe(permissions);
            if (permissions.Count > 0)
            {
                var confirm = MessageBox.Show(this,
                    "「" + name + "」声明了以下权限：\n" + perms +
                    "\n\n该代码由他人编写，将运行在你的电脑上。仅从可信来源安装，确定继续吗？",
                    "其他海床 · 权限提示",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.OK) { _status.Text = "已取消"; return false; }
            }
            return true;
        }

        private void InstallResult(SeabedPackage.ImportResult result, string scanNote)
        {
            if (result.Kind == "Full")
            {
                if (result.FullData == null) { _status.Text = "整套预设数据无效"; return; }
                PresetManager.SaveFull(result.Name, result.FullData);
                _page.RefreshAll();
                _status.Text = "已导入整套预设「" + result.Name + "」";
                return;
            }

            var settings = _page.SettingsService;
            var list = settings.CustomPanels ?? new System.Collections.Generic.List<CustomPanelDefinition>();
            string defaultParent = result.Kind == "Widget" ? "panel-widgets"
                : result.Kind == "Panel" ? "panel-features"
                : result.Kind == "Category" ? "root" : "";
            var cp = new CustomPanelDefinition
            {
                Id = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = result.Name,
                // ★ 按包分类落到对应海床一级分类（文件夹即真相源：seabed/<分类>/<id>/）
                Category = string.IsNullOrEmpty(result.Category) ? MapKindToCategory(result.Kind) : result.Category,
                ParentKey = string.IsNullOrEmpty(result.ParentKey) ? defaultParent : result.ParentKey,
                BaseType = string.IsNullOrEmpty(result.BaseType)
                    ? (result.Kind == "Widget" ? "Widget" : result.Kind == "Panel" ? "Panel" : "Config")
                    : result.BaseType,
                Kind = result.Kind,
                ConfigJson = result.ConfigJson,
                Source = result.Source,
                SourceKey = result.SourceKey ?? "",
                TrustedSource = false,   // ★ 市场来源统一走沙箱（剪贴板已降为权限声明，安装时提示）
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            list.Add(cp);
            settings.CustomPanels = list;
            // ★ 落盘到海床文件夹（文件夹即真相源）：watcher/Reload 检测 → 编译挂载 → UI 自动刷新
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SaveNodeToFolder(cp);
            // ★ 多形态：把 .xaml/.xaml.cs 等附加文件写入节点目录（XAML 形态走 CompileXaml）
            if (result.ExtraFiles.Count > 0)
                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.WriteExtraFiles(cp, result.ExtraFiles);
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.Reload();
            _page.RefreshAll();
            _status.Text = "已拾贝「" + result.Name + "」 · " + scanNote +
                " · 权限：" + WidgetPermissions.Describe(result.Permissions);
        }

        // ==================== 详情页 ====================

        /// <summary>点包打开详情：拉取 manifest + main.cs 展示，可下载或复制。</summary>
        private async Task ShowDetailAsync(MarketItem item)
        {
            int seq = ++_detailSeq;
            try
            {
                _currentItem = item;
                _status.Text = "正在加载「" + item.Name + "」…";
                _detailManifest = await _http.GetStringAsync(
                    MarketBase + "/packages/" + item.Id + "/manifest.json");
                _detailSource = await _http.GetStringAsync(
                    MarketBase + "/packages/" + item.Id + "/main.cs");
                // ★ 读取 manifest 的 files 清单（多形态包；老包无 files 字段时回退 main.cs）
                item.Files = new List<string> { "main.cs" };
                try
                {
                    using var doc = JsonDocument.Parse(_detailManifest);
                    if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var f in files.EnumerateArray()) { var s = f.GetString(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s); }
                        if (list.Count > 0) item.Files = list;
                    }
                }
                catch { }
                // ★ 按 files 清单拉取各文件并记录大小（main.cs 已拉，跳过）
                _detailFileSizes.Clear();
                if (item.Files.Count > 0)
                {
                    _detailFileSizes["main.cs"] = (_detailSource.Length / 1024.0).ToString("F1") + "KB";
                    foreach (var f in item.Files)
                    {
                        if (f == "main.cs") continue;
                        try
                        {
                            string content = await _http.GetStringAsync(MarketBase + "/packages/" + item.Id + "/" + f);
                            if (seq != _detailSeq) return;
                            _detailFileSizes[f] = (content.Length / 1024.0).ToString("F1") + "KB";
                        }
                        catch { _detailFileSizes[f] = "?"; }
                    }
                }
                // ★ 加载期间用户点了别的包：丢弃过期结果，避免显示内容与 _currentItem 不一致（误装/误删）
                if (seq != _detailSeq) return;

                _detailInfo.Text = (item.IsOfficial ? "★官方  " : "") + item.Name + "  ·  " + item.Id +
                    System.Environment.NewLine + "文件类型：" + item.FileTypesLabel +
                    System.Environment.NewLine + "建议安装到：" + item.InstallDirLabel +
                    System.Environment.NewLine + "形态：" + item.FormatLabel +
                    System.Environment.NewLine + "文件：" + string.Join(" + ", item.Files.Select(f => f + "(" + (_detailFileSizes.TryGetValue(f, out var sz) ? sz : "?") + ")")) +
                    System.Environment.NewLine + "上传者：" + item.Author +
                    (string.IsNullOrEmpty(item.Version) ? "" : "  ·  v" + item.Version) +
                    "  ·  分类：" + item.Category +
                    (item.Permissions.Count > 0 ? System.Environment.NewLine + "⚠ 权限：" + string.Join(", ", item.Permissions) : "") +
                    (string.IsNullOrEmpty(item.Description) ? "" : System.Environment.NewLine + item.Description);
                _detailInfo.Foreground = new SolidColorBrush(_cSub);
                _codeBox.Text = _detailSource;
                _codeBox.Background = new SolidColorBrush(Color.FromArgb(0x08, _cText.R, _cText.G, _cText.B));
                _codeBox.Foreground = new SolidColorBrush(_cText);
                _codeBox.BorderBrush = new SolidColorBrush(_cBorder);

                _detail.Visibility = Visibility.Visible;
                _contentScroll.Visibility = Visibility.Collapsed;
                _empty.Visibility = Visibility.Collapsed;
                _status.Text = "已加载，可拾贝或复制代码";
            }
            catch (Exception ex)
            {
                if (_detailSeq == seq)
                    _status.Text = "加载详情失败：" + ex.Message;
            }
        }

        /// <summary>从详情页下载安装（复用权限确认 + 沙箱流程）。</summary>
        private async Task InstallFromDetailAsync()
        {
            try
            {
                if (_currentItem == null) return;
                if (_currentItem.ApiVersion > SupportedApiVersion)
                {
                    _status.Text = "此包需要更新版本的 ShoreHue（包协议 v" + _currentItem.ApiVersion + "，当前支持 v" + SupportedApiVersion + "）。请升级后重试。";
                    return;
                }
                var result = new SeabedPackage.ImportResult
                {
                    Name = !string.IsNullOrEmpty(_detailInfo.Text) ? _detailManifest : "未命名",
                    Kind = "Widget",
                    Source = _detailSource,
                    ConfigJson = "{}"
                };
                using (var doc = JsonDocument.Parse(_detailManifest))
                {
                    var root = doc.RootElement;
                    result.Name = GetStr(root, "name") ?? "未命名";
                    result.Kind = GetStr(root, "kind") ?? "Widget";
                    result.Category = GetStr(root, "category");
                    result.BaseType = GetStr(root, "baseType");
                    result.ParentKey = GetStr(root, "parentKey");
                    result.SourceKey = GetStr(root, "sourceKey");
                    if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            string? fname = f.GetString();
                            if (string.IsNullOrWhiteSpace(fname) || fname == "main.cs") continue;
                            try
                            {
                                string fcontent = await _http.GetStringAsync(MarketBase + "/packages/" + _currentItem!.Id + "/" + fname);
                                result.ExtraFiles.Add(new GitHubMarketService.PackageFile(fname, fcontent));
                            }
                            catch { }
                        }
                    }
                }
                result.Permissions = WidgetPermissions.Detect(_detailSource);

                if (!ConfirmPermissions(result.Name, result.Permissions)) return;
                InstallResult(result, "在线来源（源码直接解析，沙箱编译拦截危险 API）");
            }
            catch (Exception ex)
            {
                _status.Text = "拾贝失败：" + ex.Message;
            }
        }

        // ==================== 在线市场 ====================

        private async void RefreshOnline_Click(object sender, RoutedEventArgs e)
            => await RefreshOnlineAsync();

        private async Task RefreshOnlineAsync()
        {
            try
            {
                _status.Text = "正在连接在线市场…";
                string json = await _http.GetStringAsync(MarketBase + "/index.json");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                _allPackages.Clear();
                if (root.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pkgs.EnumerateArray())
                    {
                        var item = new MarketItem
                        {
                            Id = GetStr(p, "id") ?? "",
                            Name = GetStr(p, "name") ?? "未命名",
                            Kind = GetStr(p, "kind") ?? "Widget",
                            Version = GetStr(p, "version") ?? "",
                            ApiVersion = p.TryGetProperty("apiVersion", out var av) && av.ValueKind == JsonValueKind.Number ? av.GetInt32() : 1,
                            Author = GetStr(p, "author") ?? "",
                            Description = GetStr(p, "description") ?? ""
                        };
                        string? cat = GetStr(p, "category");
                        item.Category = string.IsNullOrEmpty(cat)
                            ? MapKindToCategory(item.Kind)
                            : cat;
                        if (p.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var perm in perms.EnumerateArray())
                            {
                                string? s = perm.GetString();
                                if (!string.IsNullOrEmpty(s) && !item.Permissions.Contains(s)) item.Permissions.Add(s);
                            }
                        }
                        if (p.TryGetProperty("publisherId", out var pid) && pid.ValueKind == JsonValueKind.Number)
                        {
                            item.PublisherId = pid.GetInt64();
                        }
                        if (p.TryGetProperty("official", out var off) && off.ValueKind == JsonValueKind.True)
                        {
                            item.IsOfficial = true;
                        }
                        if (!string.IsNullOrEmpty(item.Id)) _allPackages.Add(item);
                    }
                }
                _status.Text = _allPackages.Count > 0
                    ? "在线市场 " + _allPackages.Count + " 个包"
                    : "⚠ 在线市场暂无包（market/packages/ 为空）";
                RefreshContent();
            }
            catch (Exception ex)
            {
                _status.Text = "连接在线市场失败：" + ex.Message + "（需联网；如网络受限可稍后重试）";
            }
        }

        /// <summary>kind → 默认分类（与海床树一致）。</summary>
        private static string SanitizeForFolder(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (name ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                if (sb.Length >= 32) break;
            }
            return sb.Length >= 2 ? sb.ToString() : "unnamed";
        }

        private static string MapKindToCategory(string kind) => kind switch
        {
            "Widget" => "小组件",
            "Panel" => "面板功能",
            "Config" => "面板设计",
            _ => "面板设计"
        };

        private async Task DownloadInstallAsync(MarketItem item)
        {
            if (!IsValidMarketId(item.Id)) { _status.Text = "包 id 非法，已拒绝下载"; return; }
            if (item.ApiVersion > SupportedApiVersion)
            {
                _status.Text = "此包需要更新版本的 ShoreHue（包协议 v" + item.ApiVersion + "，当前支持 v" + SupportedApiVersion + "）。请升级后重试。";
                return;
            }
            try
            {
                _status.Text = "正在拾贝「" + item.Name + "」…";
                string manifestJson = await _http.GetStringAsync(
                    MarketBase + "/packages/" + item.Id + "/manifest.json");
                string source = await _http.GetStringAsync(
                    MarketBase + "/packages/" + item.Id + "/main.cs");

                var result = new SeabedPackage.ImportResult
                {
                    Name = item.Name,
                    Kind = item.Kind,
                    Source = source,
                    ConfigJson = "{}"
                };
                using (var doc = JsonDocument.Parse(manifestJson))
                {
                    var root = doc.RootElement;
                    result.Category = GetStr(root, "category");
                    result.BaseType = GetStr(root, "baseType");
                    result.ParentKey = GetStr(root, "parentKey");
                    result.SourceKey = GetStr(root, "sourceKey");
                    // ★ 多形态：按 files 清单拉取附加文件（.xaml / .xaml.cs / config.json）
                    if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            string? fname = f.GetString();
                            if (string.IsNullOrWhiteSpace(fname) || fname == "main.cs") continue;
                            try
                            {
                                string fcontent = await _http.GetStringAsync(MarketBase + "/packages/" + item.Id + "/" + fname);
                                result.ExtraFiles.Add(new GitHubMarketService.PackageFile(fname, fcontent));
                            }
                            catch { }
                        }
                    }
                }
                result.Permissions = WidgetPermissions.Detect(source);

                if (!ConfirmPermissions(result.Name, result.Permissions)) return;
                InstallResult(result, "在线来源（源码直接解析，沙箱编译拦截危险 API）");
            }
            catch (Exception ex)
            {
                _status.Text = "拾贝失败：" + ex.Message;
            }
        }

        /// <summary>预览用：文件大小标注（显示 KB）。</summary>
        private static string FileSizeLabel(string name)
        {
            // 源码在内存里，无真实大小——按文件名给个可读标注
            return name;
        }

        // ==================== GitHub 登录 / 删除 ====================

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GitHubMarketService.IsLoggedIn)
                {
                    var c = MessageBox.Show(this,
                        "已登录：" + GitHubMarketService.CurrentUser + "，点击确定退出登录？\n\n" +
                        "（仅移除本机登录状态；GitHub 授权仍有效，可在 GitHub 设置 → 应用程序 中撤销）",
                        "GitHub", MessageBoxButton.OKCancel);
                    if (c == MessageBoxResult.OK)
                    {
                        GitHubMarketService.Logout();
                        UpdateLoginButton();
                        _status.Text = "已退出登录（如不再使用可在 GitHub 设置中撤销授权）";
                    }
                    return;
                }
                _status.Text = "正在获取 GitHub 授权…";
                var (code, uri, deviceCode) = await GitHubMarketService.StartDeviceFlowAsync();
                var win = new DeviceLoginWindow(uri, code, deviceCode) { Owner = this };
                win.ShowDialog();
                if (GitHubMarketService.IsLoggedIn)
                {
                    UpdateLoginButton();
                    _status.Text = "已登录：" + GitHubMarketService.CurrentUser;
                }
                else _status.Text = "未登录（已取消或超时）";
            }
            catch (Exception ex)
            {
                GitHubMarketService.Log("登录失败（UI捕获）: " + ex.GetType().Name + " " + ex.Message + (ex.InnerException != null ? " / inner: " + ex.InnerException.Message : ""));
                _status.Text = "登录失败：" + ex.Message + "（详情见 %LOCALAPPDATA%\\ShoreHue\\github_login.log）";
            }
        }

        private void UpdateLoginButton()
        {
            bool loggedIn = GitHubMarketService.IsLoggedIn;
            _loginBtn.Content = loggedIn ? "已登录：" + GitHubMarketService.CurrentUser : "登录 GitHub";
            _syncBtn.IsEnabled = loggedIn;
        }

        /// <summary>同步个人设置：自定义弹窗（上传/下载/确认均非系统 MessageBox）。</summary>
        private async void SyncConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!GitHubMarketService.IsLoggedIn || string.IsNullOrEmpty(GitHubMarketService.CurrentUser))
                {
                    var needLogin = new ConfirmDialog("同步设置", "请先登录 GitHub", "去登录", "取消") { Owner = this };
                    if (needLogin.ShowDialog() == true) { _loginBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                    return;
                }
                string user = GitHubMarketService.CurrentUser!;
                bool? hasCloud = await GitHubMarketService.HasConfigAsync(user);

                var win = new ConfigSyncWindow(user, hasCloud) { Owner = this };
                win.ShowDialog();

                // 下载完成后刷新设置页
                if (win.Result == "download")
                {
                    try { _page.SettingsService.Reload(); } catch { }
                    _page.RefreshAll();
                    _status.Text = "已从云端恢复设置（设置页已刷新）";
                }
                else if (win.Result == "upload")
                {
                    _status.Text = "设置已上传到云端";
                }
            }
            catch (Exception ex)
            {
                GitHubMarketService.Log("同步设置异常: " + ex.GetType().Name + " " + ex.Message);
                _status.Text = "同步失败：" + ex.Message;
            }
        }

        private async void ShowDeleteGuide()
        {
            try
            {
                if (_currentItem == null) return;
                if (!GitHubMarketService.IsLoggedIn)
                {
                    var needLogin = new ConfirmDialog("删除此包", "请先登录 GitHub，登录后即可删除自己上传的包。", "去登录", "取消") { Owner = this };
                    if (needLogin.ShowDialog() == true) { _loginBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                    return;
                }
                // ★ 身份校验：优先比 GitHub 数字 ID（publisherId 不可伪造、不可改名）；
                //   老包无 publisherId 时回退比 author 字符串（至少挡住不同名用户）
                bool isOwner = false;
                if (_currentItem.PublisherId.HasValue && GitHubMarketService.CurrentUserId.HasValue)
                {
                    isOwner = _currentItem.PublisherId.Value == GitHubMarketService.CurrentUserId.Value;
                }
                else
                {
                    isOwner = !string.IsNullOrEmpty(_currentItem.Author) &&
                              string.Equals(GitHubMarketService.CurrentUser, _currentItem.Author, StringComparison.OrdinalIgnoreCase);
                }
                if (!isOwner)
                {
                    var notOwner = new ConfirmDialog("删除此包",
                        "仅上传者可删除此包。\n当前登录：" + GitHubMarketService.CurrentUser + "\n包作者：" + _currentItem.Author,
                        "知道了", "关闭") { Owner = this };
                    notOwner.ShowDialog();
                    return;
                }
                var c = new ConfirmDialog("删除此包",
                    "确定删除市场包「" + _currentItem.Name + "」？\n删除后所有用户无法再安装，此操作不可恢复。",
                    "确定删除", "取消") { Owner = this };
                if (c.ShowDialog() != true) return;
                _status.Text = "正在删除…";
                string? err = await GitHubMarketService.DeletePackageAsync(_currentItem.Id);
                if (err == null)
                {
                    _status.Text = "已删除「" + _currentItem.Name + "」（仓库已更新，稍后市场同步）";
                    _allPackages.Remove(_currentItem);
                    RefreshContent();
                }
                else _status.Text = err;
            }
            catch (Exception ex)
            {
                _status.Text = "删除失败：" + ex.Message;
            }
        }

        private static string? GetStr(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        /// <summary>市场包 id 校验：仅英文/数字/下划线/连字符（防路径穿越注入）。</summary>
        private static bool IsValidMarketId(string? id)
        {
            return !string.IsNullOrEmpty(id) &&
                   System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{2,64}$");
        }
    }
}
