using ShoreHue.Infrastructure.WinApi;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShoreHue.UI.Panels
{
    public partial class TaskbarView
    {
        // ============================================================
        //  数据加载
        // ============================================================

        private void LoadItems()
        {
            _shortcuts.Clear();
            _windows.Clear();

            // 从快捷方式管理器加载
            foreach (var item in _shortcutManager.Items)
            {
                if (item.IsVisible) _shortcuts.Add(item);
            }

            // 获取正在运行的窗口（测试/截图时可注入虚拟窗口源）
            var windows = _windowSource != null
                ? _windowSource()
                : WindowListProvider.GetOpenWindows(WindowListProvider.WindowFilterMode.UserAppsOnly);
            foreach (var w in windows)
            {
                if (!_windows.Any(i => i.Handle == w.Handle))
                {
                    _windows.Add(TaskbarItem.FromWindow(w.Handle, w.Title, w.Icon, w.ProcessPath));
                }
            }

            UpdateShortcutRunningStates();
            ReloadGroupDefs();
            RebuildWindowDisplay();
            UpdateLayout();
        }

        private void RefreshWindows()
        {
            if ((DateTime.Now - _lastRefresh).TotalMilliseconds < 500) return;
            _lastRefresh = DateTime.Now;

            var windows = _windowSource != null
                ? _windowSource()
                : WindowListProvider.GetOpenWindows(WindowListProvider.WindowFilterMode.UserAppsOnly);
            var windowHandles = windows.Select(w => w.Handle).ToHashSet();

            // 移除已关闭的窗口
            var toRemove = _windows.Where(i => i.Handle.HasValue && !windowHandles.Contains(i.Handle.Value)).ToList();
            foreach (var item in toRemove) _windows.Remove(item);

            // 添加新窗口
            foreach (var w in windows)
            {
                if (!_windows.Any(i => i.Handle == w.Handle))
                {
                    _windows.Add(TaskbarItem.FromWindow(w.Handle, w.Title, w.Icon, w.ProcessPath));
                }
                else
                {
                    // 更新标题
                    var existing = _windows.FirstOrDefault(i => i.Handle == w.Handle);
                    if (existing != null && existing.DisplayName != w.Title)
                        existing.DisplayName = w.Title;
                }
            }

            UpdateShortcutRunningStates();
            ReloadGroupDefs();
            RebuildWindowDisplay();
            UpdateLayout();
        }

        private void UpdateShortcutRunningStates()
        {
            foreach (var item in _shortcuts)
            {
                if (string.IsNullOrEmpty(item.Path)) continue;
                try
                {
                    string exeName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                    item.IsRunning = System.Diagnostics.Process.GetProcessesByName(exeName).Length > 0;
                }
                catch { item.IsRunning = false; }
            }
        }
    }
}
