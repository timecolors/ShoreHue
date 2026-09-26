using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShoreHue.UI.Main
{
    public partial class MainWindow
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ★ 稳定不透明模式：恒用 SetWindowRgn 圆角（不启用 DWM 材质/圆角）
        private bool _useDwmCorner;
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x5A11;
        private const int TextAiHotkeyId = 0x5A12;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_B = 0x42; // B

        // ========== 面板点击穿透（按住修饰键时鼠标点击穿透面板，操作下层窗口）==========
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_SHIFT = 0x10;


        private bool _passthroughActive;
        // ★ 数字环呼出后短时抑制穿透：Ctrl 兼作穿透修饰键时，按住 Ctrl 按数字键
        //   会被穿透逻辑误判为"点击穿透"而把面板藏掉（T+30ms tick 即隐藏）。
        private long _passthroughSuppressUntilTick;

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex)
                                    : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, value);
            else SetWindowLong32(hWnd, nIndex, value.ToInt32());
        }

        /// <summary>当前穿透修饰键是否按下（GetAsyncKeyState 轮询，焦点不在本窗口也可靠）。</summary>
        private bool IsPassthroughModifierDown()
        {
            switch (_settingsService.PassthroughModifier)
            {
                case "Alt": return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                case "Shift": return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                case "None": return false;
                default: return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            }
        }

        /// <summary>鼠标当前是否位于面板窗口矩形内（DIP 换算，与 IsMouseInsidePanel 同一语义）。</summary>
        private bool IsMouseOverPanelRect()
        {
            try
            {
                var p = System.Windows.Forms.Cursor.Position;
                double dpi = GetDpiScale();
                double mx = p.X / dpi;
                double my = p.Y / dpi;
                return mx >= Left && mx <= Left + Width &&
                       my >= Top && my <= Top + Height;
            }
            catch { /* 算不出来就当"不在窗口内"（最坏是让开穿透多做一次判断） */ return false; }
        }

        /// <summary>
        /// 按住穿透修饰键 → 窗口加 WS_EX_TRANSPARENT（鼠标命中测试跳过本窗口，
        /// 点击穿透到面板覆盖区域下方的屏幕内容）；松开 → 移除。
        /// ★ 触发条件（须避开 Ctrl+数字环热键冲突）：
        ///   仅在「鼠标位于面板上」时才进入穿透——穿透的用途是按住修饰键点击面板
        ///   穿透到下层，鼠标不在面板上时没有可穿透的点击目标。否则按住 Ctrl 按数字
        ///   环热键（修饰键同为 Ctrl）会在 30ms tick 内把面板藏掉，热键形同失效。
        ///   另设数字环呼出后的短时抑制（_passthroughSuppressUntilTick），覆盖 300ms
        ///   精修组合窗口；热键呼出若遇穿透已激活则立即恢复显示。
        /// </summary>
        private void UpdatePassthroughState()
        {
            bool keyDown = IsPassthroughModifierDown();
            bool suppressed = Environment.TickCount64 < _passthroughSuppressUntilTick;
            bool down = keyDown && !suppressed && IsMouseOverPanelRect();
            if (down == _passthroughActive) return;
            _passthroughActive = down;
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[穿透] 状态切换 down={down} modifier={_settingsService.PassthroughModifier}");
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                long nv = down ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
                if (nv != ex)
                {
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(nv));
                    // ★ 刷新窗口样式：命中测试立即生效（否则 WS_EX_TRANSPARENT 可能延迟/不生效）
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
                // ★ 穿透：隐藏面板窗口（下层内容 100% 可见可点），松开恢复。
                //   用 Visibility.Hidden（WPF 立即隐藏，无动画）而非透明度/背景 alpha——
                //   实测：MainWindow 为 Mica 用 AllowsTransparency=False（非分层窗口），
                //   Window.Opacity 对 Mica 层不透明、背景 alpha 无效（变黑）、
                //   WS_EX_TRANSPARENT 非分层不穿透命中测试（LayeredProbe2/HitTestProbe 证实）。
                //   隐藏窗口是唯一对所有模式都可靠的"让开"方式。
                if (down)
                {
                    _visibilityController.SuppressOpacityReset = true;   // 防 ShowAt 重置
                    this.Visibility = Visibility.Hidden;                 // 窗口立即隐藏
                }
                else
                {
                    _visibilityController.SuppressOpacityReset = false;
                    this.Visibility = Visibility.Visible;                // 恢复窗口显示
                    MainPanel.Opacity = _visibilityController.Opacity;   // 内容透明度还原
                }
            }
            catch { /* 只影响"让开后恢复显示"这一步；不抛是为了不打断调用方（下次呼出会重新应用可见性） */ }
        }

        private IntPtr _hwnd = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("user32.dll")]
        private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const int RGN_AND = 1;

        private const uint SPI_GETWORKAREA = 0x0030;
        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const uint ABE_BOTTOM = 3;

        private double _lastTaskbarBoundary = -1;
        private string _lastRegionSignature = "";

        // 面板贴屏幕底边时，最底部 3 物理像素让给自动隐藏任务栏的呼出条
        private const int BottomStripClickThroughPx = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }

        private double GetDpiScale()
        {
            try
            {
                return VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
            catch { /* 拿不到就试下面第二种算法，最后兜底 1.0 */ }
            try
            {
                double scale = PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice.M11 ?? 0;
                if (scale > 0) return scale;
            }
            catch { /* 两种算法都拿不到 → 按 100% 处理（见下一行） */ }
            return 1.0;
        }

        /// <summary>注册全局热键 Ctrl+Alt+B：切换面板显示/隐藏；并按设置注册划词翻译 热键。</summary>
        private void RegisterGlobalHotkey(IntPtr hwnd)
        {
            _hwnd = hwnd;
            try
            {
                RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT, VK_B);
                ApplyRegionHotkeys();   // ★ Ctrl+数字环：键盘呼出 16 区域面板（默认关，按设置修饰键）
            }
            catch { /* 注册不上通常是 Ctrl+Alt+B 被别的程序占用；鼠标边缘呼出不受影响 */ }
            // 服务可能尚未初始化（SourceInitialized 早于 InitializeCoreServices），稍后再补注册
            ReapplyTextAiHotkey();
        }

        private void UnregisterGlobalHotkey(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(hwnd, HotkeyId);
                    UnregisterHotKey(hwnd, TextAiHotkeyId);
                    UnregisterRegionHotkeys(hwnd);
                }
            }
            catch { /* 注销失败无害：窗口销毁时系统会一并回收热键 */ }
        }

        /// <summary>
        /// 按设置重新注册划词翻译 全局热键（启动完成 / 设置保存后调用）。
        /// 未设置、划词翻译 小组件被关闭时注销；注册失败（冲突）时提示用户。
        /// </summary>
        private void ReapplyTextAiHotkey()
        {
            if (_hwnd == IntPtr.Zero || _settingsService == null) return;
            try
            {
                UnregisterHotKey(_hwnd, TextAiHotkeyId);

                if (!_settingsService.IsWidgetEnabled("TextAi")) return;
                string hotkey = _settingsService.TextAiHotkey;
                if (string.IsNullOrWhiteSpace(hotkey)) return;

                if (ShoreHue.Infrastructure.WinApi.HotkeyParser.TryParse(hotkey, out uint mods, out uint vk))
                {
                    if (!RegisterHotKey(_hwnd, TextAiHotkeyId, mods, vk))
                    {
                        ShoreHue.Infrastructure.WinApi.SystemToast.Show(
                            "ShoreHue", string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Set_HotkeyOccupied"], hotkey));
                        ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"划词翻译 热键注册失败（冲突？）: {hotkey}");
                    }
                }
                else
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"划词翻译 热键格式无效: {hotkey}");
                }
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("重新注册划词热键失败", ex);
            }
        }

        private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyId)
                {
                    HotkeyTogglePanel();
                    handled = true;
                }
                else if (id == TextAiHotkeyId)
                {
                    OnTextAiHotkey();
                    handled = true;
                }
                else if (IsRegionHotkey(id))
                {
                    HandleRegionHotkey(id);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private double GetTaskbarHeight()
        {
            try
            {
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                if (taskbarHandle != IntPtr.Zero)
                {
                    RECT rect = new RECT();
                    GetWindowRect(taskbarHandle, ref rect);
                    return rect.Bottom - rect.Top;
                }
            }
            catch { /* 取不到任务栏高度就用常见值 40（只影响底边贴边的判定容差） */ }
            return 40;
        }

        /// <summary>
        /// ★★★ 获取任务栏顶部坐标（DIP 单位） ★★★
        /// 通过 SHAppBarMessage(ABM_GETTASKBARPOS) 实时查询任务栏矩形：
        ///  - 任务栏显示时 → 返回任务栏上边缘（面板贴任务栏顶）
        ///  - 自动隐藏且未呼出时 → 任务栏矩形在屏幕外 → 返回屏幕底边（面板贴屏幕底）
        /// 相比 SPI_GETWORKAREA 更精确，且不依赖 WPF 缓存的工作区。
        /// </summary>
        private double GetTaskbarTopInDips()
        {
            try
            {
                var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero &&
                    abd.uEdge == ABE_BOTTOM && abd.rc.Bottom > abd.rc.Top)
                {
                    double dpiScale = GetDpiScale();
                    if (dpiScale <= 0 || double.IsNaN(dpiScale) || double.IsInfinity(dpiScale))
                    {
                        dpiScale = 1.0;
                    }

                    double topDips = abd.rc.Top / dpiScale;
                    double screenHeightDips = SystemParameters.PrimaryScreenHeight;

                    // 自动隐藏且未呼出：任务栏顶边已超出/等于屏幕底边 → 面板贴屏幕底
                    if (topDips >= screenHeightDips - 1)
                    {
                        return screenHeightDips;
                    }
                    if (topDips > 0)
                    {
                        return topDips;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取任务栏位置失败: {ex.Message}");
            }

            // ★ 备用：Win32 实时工作区（任务栏隐藏时等于屏幕底边，升起时等于任务栏顶边）
            try
            {
                RECT workArea = new RECT();
                if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref workArea, 0))
                {
                    double dpiScale = GetDpiScale();
                    return workArea.Bottom / dpiScale;
                }
            }
            catch { /* 算不出来就用 WPF 的工作区底边兜底（见下一行；底部贴边的真值另有实测结论，见 HANDOFF 坑 1） */ }

            return SystemParameters.WorkArea.Bottom;
        }

        /// <summary>
        /// ★★★ 动态刷新底部边界并让面板跟随 ★★★
        /// 每 ~150ms 由边缘定时器调用：任务栏隐藏/升起时更新边界，
        /// 若边界变化且面板正贴着底边，立即重新锚定，避免面板停留在旧位置。
        /// </summary>
        private void RefreshTaskbarBoundary()
        {
            try
            {
                // ★ 容错上限用主屏高度（与 GetTaskbarTopInDips 内部基准一致，稳定）；
                //   窗口隐藏于屏幕外时 Screen 查询不可靠，且此处仅作钳制容错。
                double screenH = SystemParameters.PrimaryScreenHeight;
                double boundary = GetTaskbarTopInDips();

                // 容错：边界必须在屏幕范围内
                if (boundary <= 0 || double.IsNaN(boundary) || double.IsInfinity(boundary))
                {
                    boundary = screenH;
                }
                boundary = Math.Max(0, Math.Min(boundary, screenH));

                bool boundaryChanged = Math.Abs(boundary - _lastTaskbarBoundary) > 0.5;
                if (boundaryChanged)
                {
                    _lastTaskbarBoundary = boundary;
                }

                _edgeController?.UpdateBottomBoundary(boundary);
                _sizeController?.UpdateBottomBoundary(boundary);

                // 边界变化且面板可见贴底 → 立即重锚，跟随任务栏
                if (boundaryChanged)
                {
                    _edgeController?.ReanchorBottomPanel();
                }

                // 面板贴屏幕底边时挖掉底部呼出条，保证自动隐藏任务栏能正常呼出；
                // ★ 拖拽调整大小时跳过，避免区域反复切换导致抖动
                if (_edgeController != null && !_edgeController.IsDragging)
                {
                    ApplyBottomStripClickThrough();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新任务栏边界失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 面板底部贴住屏幕底边时，将最底部 3 物理像素设为点击穿透，
        /// 让 Windows 自动隐藏任务栏的呼出条可以接收鼠标；任务栏升起后恢复。
        /// </summary>
        private void ApplyBottomStripClickThrough()
        {
            bool atBottom = Math.Abs((Top + Height) - SystemParameters.PrimaryScreenHeight) < 1.0;
            ApplyWindowRegion(atBottom && Height > BottomStripClickThroughPx + 2);
        }

        /// <summary>
        /// 窗口区域：圆角 + 底部点击穿透条。
        /// 非透明窗口（AllowsTransparency=false）下 WPF 走硬件渲染，
        /// 用窗口区域实现圆角，避免透明窗口强制软件渲染导致的视频/镜像卡顿。
        /// </summary>
        private void ApplyWindowRegion(bool carveBottom)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                double scale = GetDpiScale();
                if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
                int w = Math.Max(1, (int)Math.Round(ActualWidth * scale));
                int h = Math.Max(1, (int)Math.Round(ActualHeight * scale));
                int radius = Math.Max(2, (int)(8 * scale * 2)); // 8 DIP 圆角（对齐 Win11 Fluent）→ 椭圆直径

                string sig = $"{w}x{h}|{carveBottom}";
                if (sig == _lastRegionSignature) return;

                IntPtr roundRgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, radius, radius);
                if (roundRgn == IntPtr.Zero) return;

                if (carveBottom && h > BottomStripClickThroughPx + 2)
                {
                    IntPtr bottomRgn = CreateRectRgn(0, 0, w, h - BottomStripClickThroughPx);
                    IntPtr combined = CreateRectRgn(0, 0, 0, 0);
                    if (bottomRgn != IntPtr.Zero && combined != IntPtr.Zero)
                    {
                        CombineRgn(combined, roundRgn, bottomRgn, RGN_AND);
                        DeleteObject(roundRgn);
                        DeleteObject(bottomRgn);
                        SetWindowRgn(hwnd, combined, true);
                        _lastRegionSignature = sig;
                        return;
                    }
                    if (bottomRgn != IntPtr.Zero) DeleteObject(bottomRgn);
                    if (combined != IntPtr.Zero) DeleteObject(combined);
                }

                SetWindowRgn(hwnd, roundRgn, true);
                _lastRegionSignature = sig;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置窗口区域失败: {ex.Message}");
            }
        }
    }
}