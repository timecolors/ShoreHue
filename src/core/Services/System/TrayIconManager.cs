using System;
using System.Reflection;
using System.Windows;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Infrastructure.Utils;
using Microsoft.Win32;

namespace ShoreHue.Core.Services
{
    public class TrayIconManager : IService, IDisposable
    {
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private readonly Window _owner;
        private readonly Action _onOpenSettings;
        private readonly Action _onToggleWindow;
        private readonly Action _onExit;
        private bool _disposed = false;

        public string Name => "TrayIconManager";
        public bool IsInitialized { get; private set; } = false;

        public TrayIconManager(Window owner, Action onOpenSettings, Action onToggleWindow, Action onExit)
        {
            _owner = owner;
            _onOpenSettings = onOpenSettings;
            _onToggleWindow = onToggleWindow;
            _onExit = onExit;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            Create();
            IsInitialized = true;
            LogManager.Debug("TrayIconManager 初始化完成");
        }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            Dispose();
            IsInitialized = false;
            LogManager.Debug("TrayIconManager 已关闭");
        }

        public void Create()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "ShoreHue";

            try
            {
                // 托盘图标 = exe 的关联图标（assets\icon.ico 已作为 <ApplicationIcon> 嵌进 exe）。
                // ★ 这里原本还有一支 File.Exists("Resources/icon.ico")（相对路径）：**永远不成立** ——
                //   仓库与输出目录都没有 Resources/ 目录（图标在 assets/），留着只会误导后来人。
                //   2026-09 实测确认后删除。
                var entryLocation = Environment.ProcessPath;
                _notifyIcon.Icon = !string.IsNullOrEmpty(entryLocation)
                    ? System.Drawing.Icon.ExtractAssociatedIcon(entryLocation)
                    : System.Drawing.SystemIcons.Application;
            }
            catch { _notifyIcon.Icon = System.Drawing.SystemIcons.Application; }

            _notifyIcon.Visible = true;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_Settings"], null, (s, e) => _onOpenSettings());
            menu.Items.Add("-");
            menu.Items.Add(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_Toggle"], null, (s, e) => _onToggleWindow());
            menu.Items.Add("-");

            var autoStartItem = new System.Windows.Forms.ToolStripMenuItem(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_AutoStart"])
            {
                CheckOnClick = true,
                Checked = IsAutoStartEnabled()
            };
            autoStartItem.Click += (s, e) => ToggleAutoStart(autoStartItem.Checked);
            menu.Items.Add(autoStartItem);
            menu.Items.Add("-");
            menu.Items.Add(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_Exit"], null, (s, e) => _onExit());

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (s, e) => _onToggleWindow();
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        public static bool IsAutoStartEnabled()
        {
            if (AppPaths.IsPackaged) return IsStartupTaskEnabled();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                return key?.GetValue("ShoreHue") != null;
            }
            catch { return false; }
        }

        public static void ToggleAutoStart(bool enable)
        {
            try
            {
                if (AppPaths.IsPackaged)
                {
                    ToggleStartupTask(enable);
                    return;
                }
                using var key = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run");
                if (enable)
                    key?.SetValue("ShoreHue", "\"" + (Environment.ProcessPath ?? "") + "\"");
                else
                    key?.DeleteValue("ShoreHue", false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_AutoStartFail"], ex.Message), "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>商店版开机自启：使用 MSIX 启动任务（清单中需声明 desktop:StartupTask）。</summary>
        private const string StartupTaskId = "ShoreHueStartupTask";

        /// <summary>
        /// 在**线程池**上跑同步等待的 WinRT 调用。
        /// ★ 不能直接在调用线程上 `.GetAwaiter().GetResult()`：这两个方法是从托盘初始化（UI 线程）
        ///   调过来的，而 WinRT 的 `GetAsync/RequestEnableAsync` 续体默认回到调用线程的
        ///   SynchronizationContext —— UI 线程正卡在 GetResult 上等它，形成**经典死锁**
        ///   （打包版启动时表现为整个应用卡住不出现）。丢到线程池后没有 UI 同步上下文可用，
        ///   await 在线程池续体上完成，等待自然结束。
        /// </summary>
        private static T RunBlocking<T>(Func<System.Threading.Tasks.Task<T>> call)
            => System.Threading.Tasks.Task.Run(call).GetAwaiter().GetResult();

        private static bool IsStartupTaskEnabled()
        {
            try
            {
                var task = RunBlocking(() => Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask());
                return task.State == Windows.ApplicationModel.StartupTaskState.Enabled ||
                       task.State == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            catch { return false; }
        }

        private static void ToggleStartupTask(bool enable)
        {
            try
            {
                var task = RunBlocking(() => Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId).AsTask());
                if (enable)
                    RunBlocking(() => task.RequestEnableAsync().AsTask());
                else
                    task.Disable();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_AutoStartFailStore"], ex.Message), "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
