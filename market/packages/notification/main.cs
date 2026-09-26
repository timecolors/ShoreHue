using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Builtin.Notifications;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    public class NotificationDockPanel : UserControl, IWidget
    {
        private readonly StackPanel _items = new();
        private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };
        private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

        public NotificationDockPanel()
        {
            var clear = new Button { Content = "清空", FontSize = 11, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0), Style = TryFindResource("FlatButton") as Style };
            clear.Click += (_, _) => { _dismissed.Clear(); HostCapabilities.ClearNotifications(); Refresh(); };

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 2, 8) };
            header.Children.Add(_title);
            header.Children.Add(clear);

            var scroll = new ScrollViewer
            {
                Content = _items,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(scroll);
            Content = root;

            Refresh();
            // ★ 订阅走 Loaded/Unloaded：面板实例会被编译缓存复用，只在构造函数里订阅会在首次
            //   Unloaded 退订后再也不订阅（面板还在，但不再跟着新通知更新）。
            Loaded += (_, _) => { HostCapabilities.NotificationsChanged -= OnChanged; HostCapabilities.NotificationsChanged += OnChanged; Refresh(); };
            Unloaded += (_, _) => HostCapabilities.NotificationsChanged -= OnChanged;
        }

        private void OnChanged() => Dispatcher.BeginInvoke(new Action(Refresh));

        // ★ 分组与「单条关闭」的决策全在纯类 NotificationGrouping 里，这里只负责画出来 —— 逻辑才可单测。
        private void Refresh()
        {
            // Key 用宿主稳定 Id：同内容的新通知不会被旧的关闭标记误吞
            var rows = HostCapabilities.Notifications
                .Select(n => new NotificationRow(n.Id, string.IsNullOrWhiteSpace(n.AppName) ? "系统" : n.AppName, n.Message ?? "", n.TimeText ?? ""))
                .ToList();

            NotificationGrouping.PruneDismissed(_dismissed, rows);
            var groups = NotificationGrouping.Group(rows, _dismissed);

            _items.Children.Clear();
            int visible = groups.Sum(g => g.Rows.Count);
            _title.Text = visible > 0 ? "通知坞（" + visible + "）" : "通知坞（空）";

            foreach (var group in groups)
            {
                _items.Children.Add(new TextBlock
                {
                    Text = group.AppLabel,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(43, 136, 216)),
                    Margin = new Thickness(2, 6, 2, 2)
                });
                foreach (var row in group.Rows) _items.Children.Add(BuildRow(row));
            }
        }

        private FrameworkElement BuildRow(NotificationRow row)
        {
            var msg = new TextBlock { Text = row.Message, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextWrapping = TextWrapping.Wrap, LineHeight = 16, MaxHeight = 64 };
            var time = new TextBlock { Text = row.Time, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 4, 0) };
            var close = new Button
            {
                Content = "✕", Tag = row.Key, FontSize = 10, Width = 20, Height = 20, Padding = new Thickness(0),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                Cursor = Cursors.Hand, ToolTip = "关闭这一条", Style = TryFindResource("IconButton") as Style
            };
            close.Click += (_, e) => { e.Handled = true; CloseOne(row.Key); };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(msg);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            Grid.SetColumn(close, 2);
            grid.Children.Add(close);

            var border = new Border { Tag = row.Key, Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(0, 0, 0, 4) };
            border.MouseLeftButtonUp += (_, _) => OpenOne(row.Key);
            return border;
        }

        /// <summary>关掉一条：先记本地（界面立刻正确），再从宿主移除（重开面板也不会回来）。</summary>
        private void CloseOne(string key)
        {
            if (NotificationGrouping.Dismiss(_dismissed, new NotificationRow(key, "", "", "")))
            {
                var item = HostCapabilities.Notifications.FirstOrDefault(n => n.Id == key);
                if (item != null) HostCapabilities.RemoveNotification(item);
            }
            Refresh();
        }

        private void OpenOne(string key)
        {
            var item = HostCapabilities.Notifications.FirstOrDefault(n => n.Id == key);
            if (item != null) HostCapabilities.OpenNotification(item);
        }

        public string Name => "通知坞";
        public UserControl CreateView() => this;
        public void OnActivated() { Refresh(); }
        public void OnDeactivated() { }
    }
}
