using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.Infrastructure.WinApi;
using Microsoft.Win32;

namespace ShoreHue.UI.Media
{
    /// <summary>
    /// 面板窗口镜像/嵌入：
    ///  - 镜像：DWM 缩略图（GPU 合成，流畅）优先，PrintWindow 抓帧兜底，原窗口不动、可点击操作
    ///  - 嵌入：真实窗口 SetParent 进面板（第二块显示屏），可操作
    ///  - 本地视频：用系统默认播放器打开后自动嵌入面板（播放交给系统，硬件加速）
    /// </summary>
    public partial class PanelMediaPlayer : UserControl
    {
        private readonly DispatcherTimer _embedCheckTimer;
        private readonly WindowThumbnailHost _thumbnailHost = new();
        private IntPtr _captureHwnd;
        private IntPtr _returnedHwnd;
        private string _returnedTitle = "";
        private string _currentTitle = "";
        private bool _mirroring;
        private volatile bool _captureRunning;
        private System.Threading.Thread? _captureThread;
        private Size _mirrorFrameSize;
        private double _mirrorZoom = 1.0;
        private bool _mirrorButtonDown;
        private int _embedGeneration;
        private Window? _hookedWindow;

        public PanelMediaPlayer()
        {
            InitializeComponent();

            // ★ 嵌入/镜像状态检查：窗口被关闭/最小化时及时提示或归还任务栏
            _embedCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _embedCheckTimer.Tick += (_, _) => CheckEmbedState();

            // ★ 离开可视树时必须停掉所有后台资源（DWM 缩略图注册、抓帧线程、500ms 定时器、窗口事件钩子）。
            //   AppHelperView.ShowPage 会整体替换 PageContent.Content，面板切走后本视图再无 UI 可停它 ——
            //   不在这里收尾就是一个"没人能关掉"的常驻定时器 + 未释放的 DWM 注册。
            Unloaded += (_, _) => StopMirror();
        }

        // ================= 来源入口 =================

        private void MirrorPick_Click(object sender, RoutedEventArgs e)
        {
            PickWindowAndStart(embed: false);
        }

        private void EmbedPick_Click(object sender, RoutedEventArgs e)
        {
            PickWindowAndStart(embed: true);
        }

        private void PickWindowAndStart(bool embed)
        {
            var owner = Window.GetWindow(this);
            if (owner == null) return;

            var hwnd = WindowPickerWindow.Pick(owner, embed);
            if (hwnd.HasValue)
            {
                var title = WindowTitleProvider.GetWindowTitle(hwnd.Value);
                StartMirror(hwnd.Value, string.IsNullOrEmpty(title) ? ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Window"] : title, embed);
            }
        }

        // ================= 镜像 / 嵌入 =================

        public void StartMirror(IntPtr hwnd, string title, bool embed = true)
        {
            StopCaptureLoop();
            _thumbnailHost.Detach();
            UnhookWindowEvents();

            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                $"[Embed] StartMirror: hwnd={hwnd} title={title} embed={embed}");

            _captureHwnd = hwnd;
            _currentTitle = title;
            _mirroring = true;
            _mirrorZoom = 1.0;
            PlaceholderText.Visibility = Visibility.Collapsed;
            MirrorTitleText.Text = embed ? string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_EmbedTitle"], title) : string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_MirrorTitle"], title);
            MirrorStatusBar.Visibility = Visibility.Collapsed;
            MirrorImage.Visibility = Visibility.Collapsed;

            if (embed)
            {
                // ★ 嵌入真实窗口（第二块显示屏），失败则回退镜像
                EmbedHost.Visibility = Visibility.Visible;
                int gen = ++_embedGeneration;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _embedGeneration) return;
                    if (!_mirroring || _captureHwnd == IntPtr.Zero) return;

                    bool ok = EmbedHost.Attach(_captureHwnd);
                    if (!ok)
                    {
                        EmbedHost.Visibility = Visibility.Collapsed;
                        StartThumbnailMirror(title);
                        return;
                    }
                    _embedCheckTimer.Start();
                }), DispatcherPriority.Loaded);
            }
            else
            {
                // ★ 镜像：优先 DWM 缩略图（GPU 合成），失败回退 PrintWindow 抓帧
                if (WindowCaptureService.IsMinimized(_captureHwnd))
                {
                    _mirroring = false;
                    _captureHwnd = IntPtr.Zero;
                    MirrorTitleText.Text = "";
                    MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Minimized"];
                    MirrorStatusBar.Visibility = Visibility.Visible;
                    return;
                }

                EmbedHost.Visibility = Visibility.Collapsed;
                StartThumbnailMirror(title);
            }
        }

        /// <summary>DWM 缩略图镜像（GPU 合成），失败回退后台抓帧。</summary>
        private void StartThumbnailMirror(string title)
        {
            MirrorTitleText.Text = string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_MirrorTitle"], title);
            MirrorImage.Visibility = Visibility.Collapsed;
            int gen = ++_embedGeneration;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != _embedGeneration || !_mirroring || _captureHwnd == IntPtr.Zero) return;

                var window = Window.GetWindow(this);
                var targetHwnd = window == null
                    ? IntPtr.Zero
                    : new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (targetHwnd != IntPtr.Zero && _thumbnailHost.Attach(targetHwnd, _captureHwnd))
                {
                    UpdateMirrorFrameSize();
                    _mirrorZoom = 1.0;
                    HookWindowEvents(window!);
                    Dispatcher.BeginInvoke(new Action(UpdateThumbnailDestination), DispatcherPriority.Loaded);
                    _embedCheckTimer.Start();
                }
                else
                {
                    // 回退：后台抓帧
                    MirrorImage.Visibility = Visibility.Visible;
                    StartCaptureLoop();
                }
            }), DispatcherPriority.Loaded);
        }

        public void StopAll()
        {
            StopMirror();
            PlaceholderText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["UI_PanelMediaPlayer_50"].Replace("\n", Environment.NewLine);
            PlaceholderText.Visibility = Visibility.Visible;
        }

        private void StopMirror()
        {
            _embedGeneration++; // 使挂起的嵌入回调失效
            StopCaptureLoop();
            _embedCheckTimer.Stop();
            EmbedHost.Detach();
            EmbedHost.Visibility = Visibility.Collapsed;
            _thumbnailHost.Detach();
            UnhookWindowEvents();
            _captureHwnd = IntPtr.Zero;
            _returnedHwnd = IntPtr.Zero;
            _returnedTitle = "";
            _mirroring = false;
            _mirrorButtonDown = false;
            MirrorImage.Source = null;
            MirrorTitleText.Text = "";
            MirrorStatusBar.Visibility = Visibility.Collapsed;
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => StopAll();

        // ================= 状态检查 =================

        private void CheckEmbedState()
        {
            if (!_mirroring || _captureHwnd == IntPtr.Zero) return;

            // 镜像缩略图模式：窗口关闭则解除；最小化时缩略图空白，恢复后自动继续
            if (_thumbnailHost.IsActive)
            {
                UpdateMirrorFrameSize();
                if (!WindowCaptureService.IsWindowAlive(_captureHwnd))
                {
                    _embedCheckTimer.Stop();
                    _thumbnailHost.Detach();
                    UnhookWindowEvents();
                    _captureHwnd = IntPtr.Zero;
                    _mirroring = false;
                    MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Closed"];
                    MirrorStatusBar.Visibility = Visibility.Visible;
                }
                else if (WindowCaptureService.IsMinimized(_captureHwnd))
                {
                    // 缩略图对最小化窗口空白：解除并提示，恢复后点击提示条继续
                    _thumbnailHost.Detach();
                    UnhookWindowEvents();
                    MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_MinimizedClick"];
                    MirrorStatusBar.Visibility = Visibility.Visible;
                }
                else
                {
                    MirrorStatusBar.Visibility = Visibility.Collapsed;
                }
                return;
            }

            if (!EmbedHost.IsEmbedded) return;

            if (!WindowCaptureService.IsWindowAlive(_captureHwnd))
            {
                _embedCheckTimer.Stop();
                EmbedHost.Detach();
                _returnedHwnd = IntPtr.Zero;
                _returnedTitle = "";
                MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Closed"];
                MirrorStatusBar.Visibility = Visibility.Visible;
                return;
            }

            if (WindowCaptureService.IsMinimized(_captureHwnd))
            {
                // ★ 最小化 = 解除嵌入并归还 Windows 任务栏（用户可从任务栏恢复）
                _embedCheckTimer.Stop();
                EmbedHost.Detach(showWindow: false);
                _returnedHwnd = _captureHwnd;
                _returnedTitle = _currentTitle;
                _captureHwnd = IntPtr.Zero;
                _mirroring = false;
                MirrorTitleText.Text = "";
                MirrorStatusText.Text = string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Returned"], _returnedTitle);
                MirrorStatusBar.Visibility = Visibility.Visible;
            }
            else
            {
                MirrorStatusBar.Visibility = Visibility.Collapsed;
            }
        }

        // ================= 后台抓帧（镜像回退） =================

        private void StartCaptureLoop()
        {
            if (_captureRunning) return;
            _captureRunning = true;
            _captureThread = new System.Threading.Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "MirrorCapture"
            };
            _captureThread.Start();
        }

        private void StopCaptureLoop()
        {
            _captureRunning = false;
            var thread = _captureThread;
            _captureThread = null;
            try
            {
                thread?.Join(300);
            }
            catch { /* 尽力而为：等不到线程结束也不阻塞（后面还有超时/退出兜底） */ }
        }

        private void CaptureLoop()
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (_captureRunning)
            {
                sw.Restart();
                try
                {
                    IntPtr hwnd = _captureHwnd;
                    if (!_mirroring || hwnd == IntPtr.Zero) break;

                    if (!WindowCaptureService.IsWindowAlive(hwnd))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MirrorImage.Source = null;
                            MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Closed"];
                            MirrorStatusBar.Visibility = Visibility.Visible;
                        }));
                        break;
                    }

                    if (WindowCaptureService.IsMinimized(hwnd))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MirrorImage.Source = null;
                            MirrorStatusText.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_MinimizedContinue"];
                            MirrorStatusBar.Visibility = Visibility.Visible;
                        }));
                        break;
                    }

                    var frame = WindowCaptureService.Capture(hwnd);
                    if (frame != null)
                    {
                        _mirrorFrameSize = new Size(frame.PixelWidth, frame.PixelHeight);
                        var captured = frame;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_mirroring) return;
                            MirrorImage.Source = captured;
                            MirrorStatusBar.Visibility = Visibility.Collapsed;
                        }), DispatcherPriority.Render);
                    }
                }
                catch { /* 只影响"镜像状态条"的显隐（画面本身照常） */ }

                while (sw.ElapsedMilliseconds < 15)
                {
                    System.Threading.Thread.Sleep(1);
                }
            }
            _captureRunning = false;
        }

        // ================= 鼠标交互 =================

        private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 嵌入模式是真实窗口，鼠标由窗口自身接收，无需转发
            if (_mirroring && !EmbedHost.IsEmbedded && _captureHwnd != IntPtr.Zero)
            {
                _mirrorButtonDown = true;
                ForwardMirrorMouse(e.GetPosition(VideoArea), down: true);
                VideoArea.CaptureMouse();
                e.Handled = true;
            }
        }

        private void VideoArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mirroring && _mirrorButtonDown)
            {
                _mirrorButtonDown = false;
                ForwardMirrorMouse(e.GetPosition(VideoArea), down: false);
                VideoArea.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void VideoArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mirroring && !EmbedHost.IsEmbedded && _mirrorButtonDown && _captureHwnd != IntPtr.Zero)
            {
                ForwardMirrorMouse(e.GetPosition(VideoArea), down: true);
                e.Handled = true;
            }
        }

        private void MirrorStatusBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_captureHwnd != IntPtr.Zero)
            {
                WindowAction.Restore(_captureHwnd);
                // 缩略图镜像：窗口恢复后重新挂载
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_mirroring || _captureHwnd == IntPtr.Zero) return;
                    if (_thumbnailHost.IsActive) return;

                    var window = Window.GetWindow(this);
                    var targetHwnd = window == null
                        ? IntPtr.Zero
                        : new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (targetHwnd != IntPtr.Zero && _thumbnailHost.Attach(targetHwnd, _captureHwnd))
                    {
                        HookWindowEvents(window!);
                        UpdateThumbnailDestination();
                        MirrorStatusBar.Visibility = Visibility.Collapsed;
                        _embedCheckTimer.Start();
                    }
                }), DispatcherPriority.Loaded);
            }
            else if (_returnedHwnd != IntPtr.Zero)
            {
                // ★ 点击提示条 = 把最近归还的窗口重新嵌入面板（而不是弹回桌面）
                IntPtr hwnd = _returnedHwnd;
                string title = _returnedTitle;
                _returnedHwnd = IntPtr.Zero;
                _returnedTitle = "";

                if (!WindowCaptureService.IsWindowAlive(hwnd))
                {
                    MirrorStatusBar.Visibility = Visibility.Collapsed;
                    return;
                }

                StartMirror(hwnd, string.IsNullOrEmpty(title) ? ShoreHue.UI.Localization.LocalizationManager.Instance["Pip_Window"] : title, embed: true);
                MirrorStatusBar.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 把面板内的鼠标坐标映射到源窗口客户端坐标并转发，
        /// 使镜像可以像操作原窗口一样点击/拖拽（左键与右键）。
        /// </summary>
        private void ForwardMirrorMouse(Point pos, bool down, bool right = false)
        {
            try
            {
                bool mapped = _thumbnailHost.IsActive
                    ? MapThumbnailToClient(pos, out int clientX, out int clientY)
                    : WindowCaptureService.MapToClient(_captureHwnd, pos,
                        VideoArea.ActualWidth, VideoArea.ActualHeight,
                        _mirrorFrameSize.Width, _mirrorFrameSize.Height,
                        out clientX, out clientY);
                if (!mapped)
                {
                    return;
                }

                if (right)
                {
                    if (down)
                    {
                        WindowCaptureService.SetForeground(_captureHwnd);
                        WindowCaptureService.SendMouseEvent(_captureHwnd, WindowCaptureService.MouseMessage.RightDown, clientX, clientY);
                    }
                    else
                    {
                        WindowCaptureService.SendMouseEvent(_captureHwnd, WindowCaptureService.MouseMessage.RightUp, clientX, clientY);
                    }
                    return;
                }

                if (down)
                {
                    WindowCaptureService.SetForeground(_captureHwnd);
                    WindowCaptureService.SendMouseEvent(_captureHwnd, WindowCaptureService.MouseMessage.LeftDown, clientX, clientY);
                }
                else
                {
                    WindowCaptureService.SendMouseEvent(_captureHwnd, WindowCaptureService.MouseMessage.LeftUp, clientX, clientY);
                }
            }
            catch { /* 尽力而为：这次点击没转发到被镜像的窗口（源窗口可能已关闭/提权），不影响播放 */ }
        }

        /// <summary>
        /// 右键按下/抬起：镜像模式且非嵌入时转发给源窗口。
        /// 用 Preview 事件在 WPF 弹出系统上下文菜单之前拦截。
        /// </summary>
        private void VideoArea_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mirroring && !EmbedHost.IsEmbedded && _captureHwnd != IntPtr.Zero)
            {
                ForwardMirrorMouse(e.GetPosition(VideoArea), down: true, right: true);
                VideoArea.CaptureMouse();
                e.Handled = true;
            }
        }

        private void VideoArea_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mirroring && !EmbedHost.IsEmbedded && _captureHwnd != IntPtr.Zero)
            {
                ForwardMirrorMouse(e.GetPosition(VideoArea), down: false, right: true);
                VideoArea.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        // ================= 缩放 =================

        private void VideoArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_mirroring && EmbedHost.IsEmbedded) return;

            // ★ Ctrl+滚轮 = 转发滚轮给源窗口（镜像可滚动页面/缩放网页等），
            //   普通滚轮保持原有缩放画面行为。
            if (_mirroring && !EmbedHost.IsEmbedded && _captureHwnd != IntPtr.Zero &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                try
                {
                    bool mapped = _thumbnailHost.IsActive
                        ? MapThumbnailToClient(e.GetPosition(VideoArea), out int clientX, out int clientY)
                        : WindowCaptureService.MapToClient(_captureHwnd, e.GetPosition(VideoArea),
                            VideoArea.ActualWidth, VideoArea.ActualHeight,
                            _mirrorFrameSize.Width, _mirrorFrameSize.Height,
                            out clientX, out clientY);
                    if (mapped)
                    {
                        WindowCaptureService.SetForeground(_captureHwnd);
                        // WM_MOUSEWHEEL 的 lParam 需要屏幕坐标
                        var screenPt = VideoArea.PointToScreen(e.GetPosition(VideoArea));
                        WindowCaptureService.SendMouseWheel(_captureHwnd, e.Delta, (int)screenPt.X, (int)screenPt.Y);
                    }
                }
                catch { /* 尽力而为：滚轮没转发过去（源窗口已关闭/提权），事件照常吞掉不冒泡 */ }
                e.Handled = true;
                return;
            }

            // ★ DWM 缩略图镜像：滚轮缩放画面（等比，中心不变）
            if (_mirroring && _thumbnailHost.IsActive)
            {
                double zoomStep = e.Delta > 0 ? 0.15 : -0.15;
                _mirrorZoom = Math.Clamp(_mirrorZoom + zoomStep, 1.0, 4.0);
                UpdateThumbnailDestination();
                e.Handled = true;
                return;
            }

            // 截图抓帧回退：用 RenderTransform 缩放
            if (_mirroring)
            {
                double step = e.Delta > 0 ? 0.15 : -0.15;
                double next = Math.Clamp(MirrorZoom.ScaleX + step, 1.0, 4.0);
                MirrorZoom.ScaleX = next;
                MirrorZoom.ScaleY = next;
                e.Handled = true;
            }
        }

        // ================= DWM 缩略图区域跟踪 =================

        private void HookWindowEvents(Window window)
        {
            if (_hookedWindow == window) return;
            UnhookWindowEvents();
            _hookedWindow = window;
            window.LocationChanged += OnHostWindowChanged;
            window.SizeChanged += OnHostWindowChanged;
            VideoArea.SizeChanged += OnVideoAreaChanged;
        }

        private void UnhookWindowEvents()
        {
            if (_hookedWindow != null)
            {
                _hookedWindow.LocationChanged -= OnHostWindowChanged;
                _hookedWindow.SizeChanged -= OnHostWindowChanged;
                _hookedWindow = null;
            }
            VideoArea.SizeChanged -= OnVideoAreaChanged;
        }

        private void OnHostWindowChanged(object? sender, EventArgs e) => UpdateThumbnailDestination();

        private void OnVideoAreaChanged(object? sender, SizeChangedEventArgs e) => UpdateThumbnailDestination();

        private void UpdateThumbnailDestination()
        {
            if (!_thumbnailHost.IsActive) return;
            try
            {
                var window = Window.GetWindow(this);
                if (window == null || VideoArea.ActualWidth < 2 || VideoArea.ActualHeight < 2) return;

                double dpi = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpi = source.CompositionTarget.TransformToDevice.M11;
                }
                if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;

                double areaW = VideoArea.ActualWidth;
                double areaH = VideoArea.ActualHeight;

                // ★ 等比缩放 + 滚轮缩放：按源窗口客户区比例居中显示，不拉伸变形
                double fw = _mirrorFrameSize.Width;
                double fh = _mirrorFrameSize.Height;
                double w = areaW, h = areaH;
                if (fw > 1 && fh > 1)
                {
                    double baseScale = Math.Min(areaW / fw, areaH / fh) * _mirrorZoom;
                    w = fw * baseScale;
                    h = fh * baseScale;
                }

                var origin = VideoArea.TransformToAncestor(window).Transform(new Point(0, 0));
                var dest = new Rect(
                    origin.X + (areaW - w) / 2,
                    origin.Y + (areaH - h) / 2,
                    w,
                    h);
                _thumbnailHost.UpdateDestination(dest, dpi);
            }
            catch { /* 尽力而为：DWM 缩略图没跟上（画面尺寸短暂不一致，下次布局变化会再对齐） */ }
        }

        /// <summary>从源窗口刷新镜像帧尺寸（用于等比计算与鼠标映射）。</summary>
        private void UpdateMirrorFrameSize()
        {
            if (_captureHwnd == IntPtr.Zero) return;
            if (WindowCaptureService.GetClientSize(_captureHwnd, out int cw, out int ch) && cw > 0 && ch > 0)
            {
                _mirrorFrameSize = new Size(cw, ch);
            }
        }

        /// <summary>
        /// 缩略图模式下的鼠标坐标映射：与 UpdateThumbnailDestination 的等比+缩放矩形一致。
        /// </summary>
        private bool MapThumbnailToClient(Point pos, out int clientX, out int clientY)
        {
            clientX = clientY = 0;
            double areaW = VideoArea.ActualWidth;
            double areaH = VideoArea.ActualHeight;
            double fw = _mirrorFrameSize.Width;
            double fh = _mirrorFrameSize.Height;
            if (areaW <= 1 || areaH <= 1 || fw <= 1 || fh <= 1) return false;

            double scale = Math.Min(areaW / fw, areaH / fh) * _mirrorZoom;
            double renderW = fw * scale;
            double renderH = fh * scale;
            double offX = (areaW - renderW) / 2;
            double offY = (areaH - renderH) / 2;

            if (pos.X < offX || pos.X > offX + renderW ||
                pos.Y < offY || pos.Y > offY + renderH)
            {
                return false;
            }

            if (!WindowCaptureService.GetClientSize(_captureHwnd, out int cw, out int ch) ||
                cw <= 0 || ch <= 0)
            {
                return false;
            }

            clientX = (int)Math.Round((pos.X - offX) / renderW * cw);
            clientY = (int)Math.Round((pos.Y - offY) / renderH * ch);
            return true;
        }
    }

    /// <summary>
    /// 读取窗口标题（供镜像模式显示来源窗口名称）。
    /// </summary>
    internal static class WindowTitleProvider
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        public static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
    }
}
