using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.AppHelper;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Widgets;

namespace ScreenshotGen
{
    /// <summary>
    /// 商店截图生成器：离屏渲染真实面板控件（不读取任何真实用户数据），
    /// 输出 4K PNG，供 Microsoft Store 一览使用。
    /// </summary>
    internal static class Program
    {
        private const double CanvasW = 1920;
        private const double CanvasH = 1080;
        private const double RenderScale = 2.0;

        [STAThread]
        private static void Main()
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(LoadResource("src/UI/Theme/Theme.xaml"));
            app.Resources.MergedDictionaries.Add(LoadResource("src/UI/Theme/AppIcons.xaml"));

            string outDir = Path.Combine(FindRepoRoot(), "assets", "screenshots");
            Directory.CreateDirectory(outDir);
            foreach (var old in Directory.EnumerateFiles(outDir, "*.png")) File.Delete(old);

            // 1) 边缘任务栏：通用示例快捷方式 + 虚拟窗口
            var taskbarSettings = new FakeSettings();
              var taskbar = new TaskbarView(new FakeShortcutService(), taskbarSettings, SampleWindows);
            Render("01-Taskbar.png", taskbar, 1280, 100, new Point(320, 940), outDir);

            // 2) 应用辅助：画中画（镜像窗口 / 嵌入窗口 / 播放视频）
            Render("02-AppHelper.png", new AppHelperView(), 420, 340, new Point(1476, 370), outDir);

            // 3) 小组件：计算器
            var calcSettings = new FakeSettings { LastWidgetTab = "Calculator" };
            Render("03-Widget-Calculator.png",
                new WidgetSwitcher(calcSettings, new FakeClipboard(), new FakeNotes()),
                360, 260, new Point(24, 410), outDir);

            // 4) 小组件：计时器
            var timerSettings = new FakeSettings { LastWidgetTab = "Timer" };
            Render("04-Widget-Timer.png",
                new WidgetSwitcher(timerSettings, new FakeClipboard(), new FakeNotes()),
                360, 260, new Point(24, 700), outDir);

            // 5) AI 助手面板（截图模式：不读真实会话数据）
            ShoreHue.UI.AI.AiChatView.UseEmptyForScreenshot = true;
            Render("05-AI.png", new ShoreHue.UI.AI.AiChatView(), 420, 400, new Point(730, 240), outDir);

            // 6) 海床（AI 编程）界面
            var fakeSettings = new FakeSettings();
            Render("06-Seabed.png", new ShoreHue.UI.Settings.Pages.SeabedPage(birdSettings), 880, 600, new Point(520, 150), outDir);
        }


        private static ResourceDictionary LoadResource(string relativePath)
        {
            var uri = new Uri($"pack://application:,,,/ShoreHue;component/{relativePath.Replace('\\', '/')}");
            return new ResourceDictionary { Source = uri };
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ShoreHue.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? AppContext.BaseDirectory;
        }

        private static void Render(string file, FrameworkElement content, double w, double h, Point pos, string outDir)
        {
            var canvas = new Canvas
            {
                Width = CanvasW,
                Height = CanvasH,
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
            };

            var panel = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                CornerRadius = new CornerRadius(16),
                Opacity = 0.9,
                Padding = new Thickness(4),
                Child = content
            };
            Canvas.SetLeft(panel, pos.X);
            Canvas.SetTop(panel, pos.Y);
            canvas.Children.Add(panel);

            canvas.Measure(new Size(CanvasW, CanvasH));
            canvas.Arrange(new Rect(0, 0, CanvasW, CanvasH));
            canvas.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)(CanvasW * RenderScale), (int)(CanvasH * RenderScale),
                96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
            bitmap.Render(canvas);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outDir, file));
            encoder.Save(stream);
            Console.WriteLine($"SAVED {Path.Combine(outDir, file)}");
        }

        private static IEnumerable<WindowListProvider.WindowItem> SampleWindows()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return new List<WindowListProvider.WindowItem>
            {
                new()
                {
                    Handle = new IntPtr(0x1001),
                    Title = "文档 - 2026 年度计划.docx",
                    ProcessPath = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE",
                    Icon = ExtractIcon(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")
                },
                new()
                {
                    Handle = new IntPtr(0x1002),
                    Title = "网页 - Microsoft Edge",
                    ProcessPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                    Icon = ExtractIcon(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
                },
                new()
                {
                    Handle = new IntPtr(0x1003),
                    Title = "终端 - Windows PowerShell",
                    ProcessPath = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Icon = ExtractIcon(Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"))
                }
            };
        }

        private static ImageSource? ExtractIcon(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                using var bmp = icon.ToBitmap();
                IntPtr hBitmap = bmp.GetHbitmap();
                try
                {
                    return Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>测试用设置：全部使用默认值，不读取真实配置。</summary>

    /// <summary>测试用快捷方式服务：通用 Windows 应用。</summary>
    /// <summary>测试用设置：继承真实 SettingsManager（自动实现全部接口成员），仅覆盖截图所需字段。</summary>
    internal sealed class FakeSettings : ShoreHue.Core.Services.Configuration.SettingsManager
    {
        public FakeSettings()
        {
            BackgroundColor = "#2D2D2D";
            TextColor = "#FFFFFF";
            Opacity = 0.85;
            CornerRadius = 16;
            ShowSystemStatus = true;
            LastWidgetTab = "Calculator";
            TaskbarIconSize = 28;
            DividerOffset = 0.4;
            AnimationsEnabled = true;
            ShowHideEasingType = "CubicEase";
            ShowHideDurationMs = 150;
            TransformEasingType = "CubicEase";
            TransformDurationMs = 250;
            HideDelayMs = 200;
            FlyDurationMs = 500;
            RegionDebounceMs = 80;
            AutoCheckUpdate = true;
            OnboardingCompleted = true;
        }
    }

    internal sealed class FakeShortcutService : IShortcutService
    {
        public ObservableCollection<ShortcutData> Shortcuts { get; } = new();

        public event EventHandler? ShortcutsChanged;

        public FakeShortcutService()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Add("记事本", Path.Combine(sys, "notepad.exe"));
            Add("计算器", Path.Combine(sys, "calc.exe"));
            Add("画图", Path.Combine(sys, "mspaint.exe"));
            Add("文件资源管理器", Path.Combine(win, "explorer.exe"));
            Add("终端", Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"));
        }

        private void Add(string name, string path)
        {
            Shortcuts.Add(new ShortcutData
            {
                Id = Guid.NewGuid().ToString(),
                Path = path,
                Name = name,
                Order = Shortcuts.Count,
                CreateTime = DateTime.Now,
                IsVisible = true
            });
        }

        public bool AddShortcut(string path, string? name = null, string? arguments = null) => true;
        public bool RemoveShortcut(string id) => false;
        public bool RemoveShortcutByPath(string path) => false;
        public void MoveShortcut(int fromIndex, int toIndex) { }
        public void UpdateShortcutName(string id, string newName) { }
        public void SaveShortcutsOrder() { }
        public void Reload() { }

        public ImageSource? GetIcon(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                using var bmp = icon.ToBitmap();
                IntPtr hBitmap = bmp.GetHbitmap();
                try
                {
                    return Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>测试用剪贴板服务：仅示例数据。</summary>
    internal sealed class FakeClipboard : IClipboardService
    {
        public ObservableCollection<ClipboardManager.ClipboardItem> History { get; } = new()
        {
            new ClipboardManager.ClipboardItem { Type = "Text", DisplayText = "示例文本：欢迎使用ShoreHue", Timestamp = DateTime.Now },
            new ClipboardManager.ClipboardItem { Type = "Text", DisplayText = "https://github.com/timecolors/ShoreHue", Timestamp = DateTime.Now }
        };

        public event EventHandler? HistoryChanged;

        public void StartListening() { }
        public void StopListening() { }
        public void RemoveItem(ClipboardManager.ClipboardItem item) { }
        public void RemoveItems(IEnumerable<ClipboardManager.ClipboardItem> items) { }
        public void ClearAll() { }
        public void CopyToClipboard(ClipboardManager.ClipboardItem item) { }
        /// <summary>截图工具用不到"粘贴为纯文本"，但接口成员必须实现（CI 会编译本工具）。</summary>
        public void CopyToClipboardPlainText(ClipboardManager.ClipboardItem item) { }
        public bool SaveDroppedFile(string sourcePath, string targetFolder) => false;
        public void SetPinned(ClipboardManager.ClipboardItem item, bool pinned) { }
    }

    /// <summary>测试用便签服务：空列表。</summary>
    internal sealed class FakeNotes : INoteService
    {
        public ObservableCollection<NoteItem> Notes { get; } = new();
        public NoteItem? CurrentNote { get; private set; }
        public string? LoadWarning => null;

        public event EventHandler? NotesChanged;

        public void SetCurrentNote(NoteItem? note) => CurrentNote = note;
        public NoteItem CreateNote(string? title = null, string? color = null)
            => new NoteItem { Title = title ?? "便签", Color = color ?? "#FFFF99" };
        public void DeleteNote(NoteItem note) { }
        public void UpdateNoteContent(NoteItem note, string content) { }
        public void UpdateNoteTitle(NoteItem note, string title) { }
        public void UpdateNoteColor(NoteItem note, string color) { }
        public void UpdateNoteShowTitle(NoteItem note, bool showTitle) { }
        public void Save() { }
    }
}
