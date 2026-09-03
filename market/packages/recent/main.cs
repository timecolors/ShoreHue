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
            _btnFiles.Click += (_, _) => ShowTab(Kind.File);
            _btnApps.Click += (_, _) => ShowTab(Kind.App);
            _btnWebs.Click += (_, _) => ShowTab(Kind.Web);

            var refresh = new Button { Content = "⟳", FontSize = 12, Width = 30, Height = 26, ToolTip = "刷新" };
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
            var addBtn = new Button { Content = "收藏", FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
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
            if (WebFavoriteManager.AddFavorite(url)) RefreshAll();
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
                string recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return;
                foreach (var entry in new DirectoryInfo(recentDir).GetFiles("*.lnk").OrderByDescending(x => x.LastWriteTime).Take(30))
                {
                    try
                    {
                        string target = ShortcutLinkResolver.Resolve(entry.FullName);
                        if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                        _files.Add(new Entry { Kind = Kind.File, Name = Path.GetFileName(target), Detail = target, Path = target, Icon = GetFileIcon(target) });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void LoadApps()
        {
            _apps.Clear();
            try
            {
                var recent = RecentAppTracker.GetRecentApps(30);
                if (recent.Count == 0) return;
                var exeToHandle = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                WindowListProvider.EnumerateWindowExeHandles(exeToHandle);
                foreach (var app in recent)
                {
                    _apps.Add(new Entry
                    {
                        Kind = Kind.App,
                        Name = app.Name,
                        Detail = app.Path,
                        Path = app.Path,
                        Handle = exeToHandle.TryGetValue(app.Path, out var h) ? h : (IntPtr?)null,
                        Icon = GetFileIcon(app.Path)
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
                var entries = await Task.Run(() => WebFavoriteManager.GetCombined(40));
                foreach (var entry in entries)
                {
                    _webs.Add(new Entry
                    {
                        Kind = Kind.Web,
                        Name = string.IsNullOrEmpty(entry.Title) ? WebFavoriteManager.GetDomain(entry.Url) : entry.Title,
                        Detail = entry.Url,
                        Path = entry.Url,
                        IsFavorite = entry.IsFavorite
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
            var name = new TextBlock { Text = item.Name, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = item.Detail, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)), TextTrimming = TextTrimming.CharacterEllipsis };
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
                remove.Click += (_, _) => { if (item.Kind == Kind.Web) { WebFavoriteManager.RemoveFavorite(item.Path); RefreshAll(); } };
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
                    case Kind.App when item.Handle.HasValue:
                        WindowAction.SwitchTo(item.Handle.Value);
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    case Kind.App:
                    case Kind.File:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    default:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        WebFavoriteManager.RecordOpen(item.Path, item.Name);
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