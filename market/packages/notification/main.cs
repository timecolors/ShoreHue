using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    public class NotificationDockPanel : UserControl, IWidget
    {
        private readonly StackPanel _items = new();
        private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };

        public NotificationDockPanel()
        {
            var clear = new Button { Content = "清空", FontSize = 11, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0) };
            clear.Click += (_, _) => ToastMonitor.ClearAll();

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
            ToastMonitor.Changed += OnChanged;
            Unloaded += (_, _) => ToastMonitor.Changed -= OnChanged;
        }

        private void OnChanged() => Dispatcher.BeginInvoke(new Action(Refresh));

        private void Refresh()
        {
            _items.Children.Clear();
            int count = ToastMonitor.Notifications.Count;
            _title.Text = count > 0 ? "通知坞（" + count + "）" : "通知坞（空）";
            foreach (var n in ToastMonitor.Notifications)
            {
                var app = new TextBlock { Text = n.AppName, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(43, 136, 216)), TextTrimming = TextTrimming.CharacterEllipsis };
                var msg = new TextBlock { Text = n.Message, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextWrapping = TextWrapping.Wrap, MaxHeight = 60, Margin = new Thickness(0, 2, 0, 0) };
                var time = new TextBlock { Text = n.TimeText, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)), VerticalAlignment = VerticalAlignment.Top };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                left.Children.Add(app);
                left.Children.Add(msg);
                grid.Children.Add(left);
                Grid.SetColumn(time, 1);
                grid.Children.Add(time);

                var row = new Border { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(0, 0, 0, 4) };
                row.MouseLeftButtonUp += (_, _) => ToastMonitor.OpenApp(n);
                _items.Children.Add(row);
            }
        }

        public string Name => "通知坞";
        public UserControl CreateView() => this;
        public void OnActivated() { Refresh(); }
        public void OnDeactivated() { }
    }
}