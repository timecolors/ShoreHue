using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using Microsoft.Win32;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.Panels
{
    public enum RecentItemType { File, App, Web }

    public class RecentItem : INotifyPropertyChanged
    {
        public RecentItemType Type { get; set; }
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Path { get; set; } = "";
        public IntPtr? Handle { get; set; }
        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }
        public bool IsFavorite { get; set; }
        public bool ShowRemove { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// 左下角“最近使用”：最近打开的文件 / 最近打开的应用 / 常用网页与最近网页。
    /// </summary>
    public partial class RecentItemsView : UserControl
    {
        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private static ImageSource? _browserIcon;

        private RecentItemType _tab = RecentItemType.File;
        private readonly List<RecentItem> _files = new();
        private readonly List<RecentItem> _apps = new();
        private readonly List<RecentItem> _webs = new();

        public RecentItemsView()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshAll();
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

                var entries = new DirectoryInfo(recentDir)
                    .GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(30);

                foreach (var entry in entries)
                {
                    try
                    {
                        string target = ShortcutLinkResolver.Resolve(entry.FullName);
                        if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                        _files.Add(new RecentItem
                        {
                            Type = RecentItemType.File,
                            Name = Path.GetFileName(target),
                            Detail = target,
                            Path = target,
                            Icon = GetFileIcon(target)
                        });
                    }
                    catch { /* 尽力而为：某一条最近文件读不出来就跳过（其余照常列出） */ }
                }
            }
            catch { /* 整个"最近文件"分组取不到 → 该分组为空（其它分组照常） */ }
        }

        private void LoadApps()
        {
            _apps.Clear();
            try
            {
                // ★ 最近打开的应用（由 RecentAppTracker 记录），而不是正在运行的程序
                var recent = RecentAppTracker.GetRecentApps(30);
                if (recent.Count == 0) return;

                // 单次枚举窗口，批量建立 exe → 主窗口句柄映射（避免逐项全量枚举阻塞动画）
                var exeToHandle = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                WindowListProvider.EnumerateWindowExeHandles(exeToHandle);

                foreach (var app in recent)
                {
                    _apps.Add(new RecentItem
                    {
                        Type = RecentItemType.App,
                        Name = app.Name,
                        Detail = app.Path,
                        Path = app.Path,
                        Handle = exeToHandle.TryGetValue(app.Path, out var h) ? h : (IntPtr?)null,
                        Icon = GetFileIcon(app.Path)
                    });
                }
            }
            catch { /* 整个"最近应用"分组取不到 → 该分组为空 */ }
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
            catch { /* 尽力而为（UI 装饰）：取不到图标就显示占位（结果按路径缓存，不会反复失败） */ return null; }
        }

        private async void LoadWebs()
        {
            _webs.Clear();
            try
            {
                // ★ 浏览器历史读取/文件复制放到后台线程，避免阻塞面板滑入动画
                var entries = await Task.Run(() => WebFavoriteManager.GetCombined(40));
                foreach (var entry in entries)
                {
                    var item = new RecentItem
                    {
                        Type = RecentItemType.Web,
                        Name = string.IsNullOrEmpty(entry.Title)
                            ? WebFavoriteManager.GetDomain(entry.Url)
                            : entry.Title,
                        Detail = entry.Url,
                        Path = entry.Url,
                        IsFavorite = entry.IsFavorite,
                        ShowRemove = entry.IsFavorite,
                        Icon = GetBrowserIcon()
                    };
                    _webs.Add(item);
                }
                if (_tab == RecentItemType.Web)
                {
                    ShowTab(_tab);
                }
            }
            catch { /* 尽力而为：这次切标签没刷新出来，下次切换/重新打开会重建 */ }
        }

        /// <summary>
        /// 网页条目统一使用默认浏览器的应用图标（与 Windows“默认应用”一致）。
        /// </summary>
        private static ImageSource? GetBrowserIcon()
        {
            if (_browserIcon != null) return _browserIcon;

            string? browserPath = GetDefaultBrowserPath();
            if (!string.IsNullOrEmpty(browserPath))
            {
                _browserIcon = GetFileIcon(browserPath);
            }
            return _browserIcon;
        }

        /// <summary>通过注册表读取系统默认浏览器（http）的 exe 路径。</summary>
        private static string? GetDefaultBrowserPath()
        {
            try
            {
                using var userChoice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                string? progId = userChoice?.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) return null;

                using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                string? cmd = cmdKey?.GetValue(null) as string;
                if (string.IsNullOrEmpty(cmd)) return null;

                cmd = cmd.Trim();
                if (cmd.StartsWith("\"", StringComparison.Ordinal))
                {
                    int end = cmd.IndexOf('"', 1);
                    if (end < 0) return null;
                    cmd = cmd.Substring(1, end - 1);
                }
                else
                {
                    cmd = cmd.Split(' ')[0];
                }

                return File.Exists(cmd) ? cmd : null;
            }
            catch { /* 取不到命令行可执行文件 → 返回 null，调用方按"没有该能力"处理 */ return null; }
        }

        private void ShowTab(RecentItemType tab)
        {
            _tab = tab;
            BtnFiles.Style = (Style)FindResource(tab == RecentItemType.File ? "AccentButton" : "FlatButton");
            BtnApps.Style = (Style)FindResource(tab == RecentItemType.App ? "AccentButton" : "FlatButton");
            BtnWebs.Style = (Style)FindResource(tab == RecentItemType.Web ? "AccentButton" : "FlatButton");

            var source = tab switch
            {
                RecentItemType.File => _files,
                RecentItemType.App => _apps,
                _ => _webs
            };
            ItemList.ItemsSource = source;
            HintText.Text = tab switch
            {
                RecentItemType.File => string.Format(LocalizationManager.Instance["Recent_Files"], _files.Count),
                RecentItemType.App => string.Format(LocalizationManager.Instance["Recent_Apps"], _apps.Count),
                _ => string.Format(LocalizationManager.Instance["Recent_Webs"], _webs.Count)
            };

            WebInputPanel.Visibility = tab == RecentItemType.Web
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Item_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is RecentItem item)
            {
                OpenItem(item);
                e.Handled = true;
            }
        }

        private static void OpenItem(RecentItem item)
        {
            try
            {
                switch (item.Type)
                {
                    case RecentItemType.App when item.Handle.HasValue:
                        WindowAction.SwitchTo(item.Handle.Value);
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    case RecentItemType.App:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    case RecentItemType.File:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    default:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        WebFavoriteManager.RecordOpen(item.Path, item.Name);
                        break;
                }
            }
            catch { /* 尽力而为：这次"最近打开"没记上（只影响排序，不影响这次打开本身） */ }
        }

        private void AddWeb_Click(object sender, RoutedEventArgs e)
        {
            string input = WebUrlInput.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(input)) return;

            if (WebFavoriteManager.AddFavorite(input))
            {
                WebUrlInput.Text = "";
                RefreshAll();
            }
        }

        private void WebUrlInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddWeb_Click(sender, e);
                e.Handled = true;
            }
        }

        private void RemoveWeb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is RecentItem item &&
                item.Type == RecentItemType.Web)
            {
                WebFavoriteManager.RemoveFavorite(item.Path);
                RefreshAll();
                e.Handled = true;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();
        private void FilesTab_Click(object sender, RoutedEventArgs e) => ShowTab(RecentItemType.File);
        private void AppsTab_Click(object sender, RoutedEventArgs e) => ShowTab(RecentItemType.App);
        private void WebsTab_Click(object sender, RoutedEventArgs e) => ShowTab(RecentItemType.Web);
    }
}
