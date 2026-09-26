using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 剪贴板 · 纯代码版（动态编译运行，风格与内置一致：深色卡片/列表）
    public class ClipboardPanel : UserControl, IWidget
    {
        private readonly IClipboardService _clipboard;
        private readonly List<ClipboardManager.ClipboardItem> _selected = new();
        private ContentControl _historyList;
        private TextBlock _searchPlaceholder, _statusText;
        private TextBox _searchBox;
        private Button _btnDeleteSelected;
        private StackPanel _filterPanel;
        private string _filterType = "All";
        private string _searchQuery = "";

        public ClipboardPanel()
        {
            // ★ 安全 v2：外来来源只能用窄接口（宿主内部服务容器已被沙箱拦截）
            _clipboard = ShoreHue.UI.Widgets.HostCapabilities.ClipboardHistory;
            BuildUi();
            if (_clipboard != null) _clipboard.HistoryChanged += (_, _) => RefreshList();
            RefreshList();
        }

        private void RefreshList()
        {
            if (_historyList == null || _clipboard == null) return;
            IEnumerable<ClipboardManager.ClipboardItem> items = _clipboard.History;
            switch (_filterType)
            {
                case "Pinned": items = items.Where(i => i.IsPinned); break;
                case "Text": items = items.Where(i => i.Type == "Text" || i.Type == "Html"); break;
                case "Link": items = items.Where(i => i.Type == "Link"); break;
                case "Image": items = items.Where(i => i.Type == "Image"); break;
                case "File": items = items.Where(i => i.Type == "File"); break;
            }
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                string q = _searchQuery.ToLower();
                items = items.Where(i =>
                    (i.DisplayText ?? "").ToLower().Contains(q) ||
                    (i.FullText ?? "").ToLower().Contains(q));
            }

            var panel = new StackPanel();
            foreach (var item in items.Take(50))
            {
                panel.Children.Add(BuildClipRow(item));
            }
            _historyList.Content = panel;
            UpdateStatus(null);
        }

        private Border BuildClipRow(ClipboardManager.ClipboardItem item)
        {
            string preview = item.Type == "File"
                ? string.Join("  ", item.FilePaths ?? new List<string>())
                : item.DisplayText ?? "";
            if (preview.Length > 120) preview = preview.Substring(0, 120) + "…";

            var text = new TextBlock
            {
                Text = preview,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 48,
                TextWrapping = TextWrapping.Wrap
            };
            var time = new TextBlock
            {
                Text = item.Timestamp.ToString("HH:mm"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
                VerticalAlignment = VerticalAlignment.Top
            };
            var pin = new TextBlock
            {
                Text = item.IsPinned ? "★" : "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 179, 1)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            Grid.SetColumn(pin, 2);
            grid.Children.Add(pin);

            var row = new Border
            {
                Child = grid,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            row.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    string copyText = item.Type == "File"
                        ? string.Join(Environment.NewLine, item.FilePaths ?? new List<string>())
                        : item.FullText ?? item.DisplayText;
                    if (!string.IsNullOrEmpty(copyText)) Clipboard.SetText(copyText);
                }
                catch { }
            };
            return row;
        }

        public string Name => "剪贴板";
        public UserControl CreateView() => this;
        public void OnActivated() => RefreshList();
        public void OnDeactivated() { }

        private void BuildUi()
        {
            // 搜索框
            _searchBox = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), BorderThickness = new Thickness(0) };
            _searchBox.TextChanged += (_, _) => { _searchQuery = _searchBox.Text; if (_searchPlaceholder != null) _searchPlaceholder.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Visible : Visibility.Collapsed; RefreshList(); };
            _searchPlaceholder = new TextBlock { Text = "搜索剪贴板…", Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)), FontSize = 11, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            searchHost.Children.Add(_searchBox); searchHost.Children.Add(_searchPlaceholder);

            // 过滤按钮
            _filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (label, tag) in new (string, string)[] { ("全部","All"), ("收藏","Pinned"), ("文本","Text"), ("链接","Link"), ("图片","Image"), ("文件","File") })
            {
                var b = new Button { Content = label, Tag = tag, FontSize = 11, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)) };
                b.Click += Filter_Click;
                _filterPanel.Children.Add(b);
            }

            // 列表
            _historyList = new ContentControl { Background = Brushes.Transparent };
            var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _historyList };

            var root = new StackPanel { Margin = new Thickness(2) };
            root.Children.Add(searchHost);
            root.Children.Add(_filterPanel);
            root.Children.Add(listScroll);
            Content = root;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string t) return;
            _filterType = t;
            foreach (var child in _filterPanel.Children)
            {
                if (child is Button b)
                {
                    bool active = (b.Tag as string) == t;
            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(204, 204, 204));
        }
    }
    RefreshList();
}

private void UpdateStatus(string? msg)
{
    if (_statusText == null) return;
    _statusText.Text = msg ?? (_clipboard != null ? "共 " + _clipboard.History.Count + " 条" : "");
}
    }
}