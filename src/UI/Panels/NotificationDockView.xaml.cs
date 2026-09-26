using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShoreHue.Builtin.Notifications;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.Panels
{
    /// <summary>
    /// 右下角通知坞：按应用分组展示捕获到的消息弹窗/系统通知，点击打开对应应用，每条可单独关闭。
    /// 分组与「单条关闭」的决策在纯类 <see cref="NotificationGrouping"/> 里（海床面板模板共用同一份）。
    /// </summary>
    public partial class NotificationDockView : UserControl
    {
        /// <summary>已关闭的条目标记（按宿主稳定 Id）。移除失败/事件延迟时，界面也立刻正确。</summary>
        private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
        private List<NotificationGroup> _groups = new();

        public NotificationDockView()
        {
            InitializeComponent();
            // ★ 必须退订：ToastMonitor.Changed 是 **static** 事件，而 PanelContentController
            //   每次切到通知坞/四角面板都会 new 一个新的 NotificationDockView ——
            //   不退订就等于每切一次面板泄漏一整个视图，且它还会继续被回调。
            ToastMonitor.Changed += OnNotificationsChanged;
            Unloaded += (_, _) => ToastMonitor.Changed -= OnNotificationsChanged;
            RefreshList();
            UpdateHeader();
        }

        private void OnNotificationsChanged()
        {
            Dispatcher.BeginInvoke(new Action(() => { RefreshList(); UpdateHeader(); }));
        }

        private void UpdateHeader()
        {
            int count = _groups.Sum(g => g.Rows.Count);
            TitleText.Text = count > 0
                    ? string.Format(LocalizationManager.Instance["Notify_Title"], count)
                    : LocalizationManager.Instance["Notify_TitleEmpty"];
        }

        /// <summary>把宿主通知投影成展示行 → 纯类分组 → 绑定到列表。</summary>
        private void RefreshList()
        {
            var rows = ToastMonitor.Notifications
                .Select(n => new NotificationRow(
                    n.Id,
                    string.IsNullOrWhiteSpace(n.AppName) ? "系统" : n.AppName,
                    n.Message ?? "",
                    n.TimeText ?? ""))
                .ToList();

            NotificationGrouping.PruneDismissed(_dismissed, rows);
            _groups = NotificationGrouping.Group(rows, _dismissed);
            NotificationList.ItemsSource = _groups;
        }

        private void Item_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NotificationRow row)
            {
                OpenOne(row.Key);
                e.Handled = true;
            }
        }

        private void ItemClose_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;   // 别让这一击冒泡到条目的"打开"处理
            if (sender is FrameworkElement fe && fe.DataContext is NotificationRow row) CloseOne(row.Key);
        }

        /// <summary>关掉一条：先记本地（界面立刻正确），再从宿主移除（重开面板也不会回来）。</summary>
        private void CloseOne(string key)
        {
            if (NotificationGrouping.Dismiss(_dismissed, new NotificationRow(key, "", "", "")))
            {
                var item = ToastMonitor.Notifications.FirstOrDefault(n => n.Id == key);
                if (item != null) ToastMonitor.RemoveItem(item);
            }
            RefreshList();
            UpdateHeader();
        }

        private void OpenOne(string key)
        {
            var item = ToastMonitor.Notifications.FirstOrDefault(n => n.Id == key);
            if (item != null) ToastMonitor.OpenApp(item);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _dismissed.Clear();
            ToastMonitor.ClearAll();
            RefreshList();
            UpdateHeader();
        }
    }
}
