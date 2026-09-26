using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    public class RecentItemsPanel : UserControl, IWidget
    {
        private enum Kind { File, App, Web }

        private sealed class Entry
        {
            public Kind Kind;
            public string Name = "";
            public string Detail = "";
            public string Path = "";
            public IntPtr? Handle;
            public ImageSource? Icon;
            public bool IsFavorite;
        }

        private readonly List<Entry> _files = new();
        private readonly List<Entry> _apps = new();
        private readonly List<Entry> _webs = new();
        private Kind _tab = Kind.File;

        private readonly TextBlock _hint = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), Margin = new Thickness(0, 2, 0, 0) };
        private readonly Button _btnFiles = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "文件" };
        private readonly Button _btnApps = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "应用", Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _btnWebs = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "网页", Margin = new Thickness(6, 0, 0, 0) };
        private readonly Border _webRow = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        private readonly StackPanel _list = new();

        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        public RecentItemsPanel()
        {
            _btnFiles.Style = TryFindResource("FlatButton") as Style;
            _btnApps.Style = TryFindResource("FlatButton") as Style;
            _btnWebs.Style = TryFindResource("FlatButton") as Style;
            _btnFiles.Click += (_, _) => ShowTab(Kind.File);
            _btnApps.Click += (_, _) => ShowTab(Kind.App);
            _btnWebs.Click += (_, _) => ShowTab(Kind.Web);

            var refresh = new Button { Content = "⟳", FontSize = 12, Width = 30, Height = 26, ToolTip = "刷新", Style = TryFindResource("IconButton") as Style };
            refresh.Click += (_, _) => RefreshAll();

            var title = new TextBlock { Text = "最近使用", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headLeft = new StackPanel();
            headLeft.Children.Add(title);
            headLeft.Children.Add(_hint);
            head.Children.Add(headLeft);
            Grid.SetColumn(refresh, 1);
            head.Children.Add(refresh);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tabs.Children.Add(_btnFiles);
            tabs.Children.Add(_btnApps);
            tabs.Children.Add(_btnWebs);

            var input = new TextBox { FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "输入网址，回车收藏" };
            var addBtn = new Button { Content = "收藏", FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("FlatButton") as Style };
            addBtn.Click += (_, _) => AddWeb(input.Text);
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddWeb(input.Text); e.Handled = true; } };
            var webGrid = new Grid();
            webGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            webGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            webGrid.Children.Add(input);
            Grid.SetColumn(addBtn, 1);
            webGrid.Children.Add(addBtn);
            _webRow.Child = webGrid;

            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(head);
            root.Children.Add(tabs);
            root.Children.Add(_webRow);
            root.Children.Add(scroll);
            Content = root;

            Loaded += (_, _) => RefreshAll();
        }

        private void AddWeb(string input)
        {
            string url = (input ?? "").Trim();
            if (url.Length == 0) return;
            if (ShoreHue.UI.Widgets.HostCapabilities.AddWebFavorite(url)) RefreshAll();
        }

        public void RefreshAll()
        {
            LoadFiles();
            LoadApps();
            LoadWebs();
            ShowTab(_tab);
        }

        private void LoadFiles()
        {
            _files.Clear();
            try
            {
                // ★ 安全 v2：最近文件由宿主窄接口提供（不再直接枚举 Recent 目录 / 解析 .lnk）
                foreach (var it in ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(40))
                {
                    if (it.Kind != "File") continue;
                    _files.Add(new Entry { Kind = Kind.File, Name = it.Name, Detail = it.Target, Path = it.Target, Icon = GetFileIcon(it.Target) });
                }
            }
            catch { }
        }

        private void LoadApps()
        {
            _apps.Clear();
            try
            {
                // ★ 安全 v2：最近应用由宿主窄接口提供（不再读注册表 UserAssist / 枚举窗口）
                foreach (var it in ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(40))
                {
                    if (it.Kind != "App") continue;
                    _apps.Add(new Entry
                    {
                        Kind = Kind.App,
                        Name = string.IsNullOrEmpty(it.Name) ? it.Target : it.Name,
                        Detail = it.Target,
                        Path = it.Target,
                        Icon = GetFileIcon(it.Target)
                    });
                }
            }
            catch { }
        }

        private static ImageSource? GetFileIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_iconCache.TryGetValue(path, out var cached)) return cached;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    new Int32Rect(0, 0, icon.Width, icon.Height),
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                _iconCache[path] = source;
                return source;
            }
            catch { return null; }
        }

        private async void LoadWebs()
        {
            _webs.Clear();
            try
            {
                // ★ 安全 v2：网页收藏由宿主窄接口提供（不再读浏览器 History）
                var entries = await Task.Run(() => ShoreHue.UI.Widgets.HostCapabilities.GetWebFavorites(40));
                foreach (var entry in entries)
                {
                    _webs.Add(new Entry
                    {
                        Kind = Kind.Web,
                        Name = entry.Name,
                        Detail = entry.Target,
                        Path = entry.Target,
                        IsFavorite = true
                    });
                }
                if (_tab == Kind.Web) ShowTab(_tab);
            }
            catch { }
        }

        private void ShowTab(Kind tab)
        {
            _tab = tab;
            var source = tab switch { Kind.File => _files, Kind.App => _apps, _ => _webs };
            _hint.Text = "共 " + source.Count + " 条";
            _webRow.Visibility = tab == Kind.Web ? Visibility.Visible : Visibility.Collapsed;

            _list.Children.Clear();
            foreach (var item in source)
            {
                _list.Children.Add(BuildRow(item));
            }
        }

        private Border BuildRow(Entry item)
        {
            // ★ 去掉省略号改换行（应用名/详情常是长路径，能看全）；高度取行高整数倍（4 行），
            //   超出时裁剪落在行间而不是切半个字。与内置面板 RecentItemsView 同口径。
            var name = new TextBlock { Text = item.Name, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextWrapping = TextWrapping.Wrap, LineHeight = 16, MaxHeight = 64 };
            var detail = new TextBlock { Text = item.Detail, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)), TextWrapping = TextWrapping.Wrap, LineHeight = 14, MaxHeight = 56 };
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(detail);

            var iconCol = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (item.Icon != null)
            {
                iconCol.Children.Add(new Image { Source = item.Icon, Width = 18, Height = 18 });
            }
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(iconCol);
            left.Children.Add(text);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);

            if (item.IsFavorite)
            {
                var remove = new Button
                {
                    Content = "✕",
                    Width = 18, Height = 18,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                remove.Click += (_, _) => { if (item.Kind == Kind.Web) { ShoreHue.UI.Widgets.HostCapabilities.RemoveWebFavorite(item.Path); RefreshAll(); } };
                Grid.SetColumn(remove, 1);
                grid.Children.Add(remove);
            }

            var row = new Border { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 2, 2, 2), Margin = new Thickness(0, 0, 0, 4) };
            row.MouseLeftButtonUp += (_, _) => OpenItem(item);
            return row;
        }

        private static void OpenItem(Entry item)
        {
            try
            {
                switch (item.Kind)
                {
                    // ★ 安全 v2：打开动作走宿主窄接口（白名单协议 / 最近使用清单内的路径）
                    case Kind.App:
                    case Kind.File:
                        ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(item.Path);
                        ShoreHue.UI.Widgets.HostCapabilities.RecordLaunch(item.Path);
                        break;
                    default:
                        ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(item.Path);
                        ShoreHue.UI.Widgets.HostCapabilities.RecordWebOpen(item.Path, item.Name);
                        break;
                }
            }
            catch { }
        }

        public string Name => "最近使用";
        public UserControl CreateView() => this;
        public void OnActivated() { RefreshAll(); }
        public void OnDeactivated() { }
    }
}