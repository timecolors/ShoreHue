using System;
using ShoreHue.Core.Infrastructure.Logging;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// WH_MOUSE_LL 全局鼠标钩子（事件驱动边缘检测）。
    /// 钩子回调在专用消息循环线程执行，只缓存最新鼠标位置/边缘区域并置"有事件"标志——
    /// 不在钩子线程做任何 UI/窗口操作（WPF 对象必须 UI 线程访问）。
    /// MainWindow 轮询 tick 读取缓存：有事件立即处理，无事件时保持低频。
    /// 钩子安装失败自动降级为纯轮询（现有行为不变）。
    /// </summary>
    public sealed class MouseHookService : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProc? _procRef;      // 防 GC
        private Thread? _messageThread;
        private uint _messageThreadId;
        private volatile bool _running;
        private volatile bool _hasEvent;          // 有新鼠标事件待处理
        private volatile bool _everReported;      // 是否至少收到过一次鼠标事件（见 HasEverReported）
        private int _lastX, _lastY;
        private readonly object _lock = new();

        /// <summary>是否成功安装钩子（false = 降级轮询）。</summary>
        public bool IsActive { get; private set; }

        /// <summary>有新的鼠标移动事件待处理（MainWindow tick 读取后调用 Consume）。</summary>
        public bool HasEvent => _hasEvent;

        /// <summary>读取并清除事件标志（tick 处理后调用）。</summary>
        public void ConsumeEvent() => _hasEvent = false;

        /// <summary>最新鼠标位置（物理像素）。
        /// ★ 只有收到过鼠标事件之后才有意义：在此之前 _lastX/_lastY 还是初始值 (0,0)，
        ///   把它当坐标会得到"鼠标在屏幕左上角"这个假位置（启动后左上角面板自己弹出来就是这个原因）。</summary>
        public (int X, int Y) LastPosition => (_lastX, _lastY);

        /// <summary>钩子是否至少派发过一次鼠标事件。false = LastPosition 不可信，必须另取真实光标位置。
        /// 装完钩子到第一次鼠标移动之间可能隔很久（用户不动鼠标），这段时间不能拿 LastPosition 用。</summary>
        public bool HasEverReported => _everReported;

        public MouseHookService()
        {
            try
            {
                _procRef = HookProc;
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _procRef, GetModuleHandle(null), 0);
                if (_hookId == IntPtr.Zero) return;
                IsActive = true;

                _running = true;
                uint tid = GetCurrentThreadId();
                _messageThread = new Thread(() =>
                {
                    _messageThreadId = GetCurrentThreadId();
                    // ★ 钩子回调由本线程的消息循环派发（GetMessage）
                    while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                        if (msg.message == 0x0400) break;   // 唤醒消息
                    }
                });
                _messageThread.IsBackground = true;
                _messageThread.Start();
            }
            catch
            {
                IsActive = false;
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                lock (_lock)
                {
                    _lastX = data.pt.X;
                    _lastY = data.pt.Y;
                    _hasEvent = true;
                    _everReported = true;
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            _running = false;
            if (_hookId != IntPtr.Zero)
            {
                // 尽力而为：卸载钩子失败不影响进程退出（钩子随进程结束自动失效）
                try { UnhookWindowsHookEx(_hookId); }
                catch (Exception ex) { LogManager.Debug($"[鼠标钩子] 卸载钩子失败（无害）：{ex.Message}"); }
                _hookId = IntPtr.Zero;
            }
            // 唤醒消息循环线程（ManagedThreadId 可能非 Win32 线程 id，改用 GetCurrentThreadId 捕获）
            // 尽力而为：唤醒消息循环失败也没关系（消息线程是 IsBackground，不阻止进程退出）
            try { PostThreadMessage(_messageThreadId, 0x0400, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex) { LogManager.Debug($"[鼠标钩子] 唤醒消息线程失败（无害）：{ex.Message}"); }
            _messageThread = null;
            _procRef = null;
            IsActive = false;
        }
    }
}
