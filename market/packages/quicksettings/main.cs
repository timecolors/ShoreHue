using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;
using NAudio.CoreAudioApi;

namespace ShoreHue.Builtin
{
    public class QuickSettingsPanel : UserControl, IWidget
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly MMDevice? _audio;
        private bool _brightnessChanging;

        private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true };
        private readonly TextBlock _volumeText = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _mute = new() { FontSize = 14, Margin = new Thickness(6, 0, 0, 0) };

        private readonly Border _brightnessRow = new() { Visibility = Visibility.Collapsed };
        private readonly Slider _brightness = new() { IsMoveToPointEnabled = true };
        private readonly TextBlock _brightnessText = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)), VerticalAlignment = VerticalAlignment.Center };

        private readonly TextBlock _btState = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)) };
        private readonly Button _btBtn = new() { Content = "…", Width = 56, Height = 26, FontSize = 12 };
        private readonly TextBlock _wifiState = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)) };
        private readonly Button _wifiBtn = new() { Content = "…", Width = 56, Height = 26, FontSize = 12 };
        private readonly Border _hotspotRow = new() { Visibility = Visibility.Collapsed };
        private readonly TextBlock _hotspotState = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)) };
        private readonly Button _hotspotBtn = new() { Content = "…", Width = 56, Height = 26, FontSize = 12 };

        public QuickSettingsPanel()
        {
            try { _audio = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            catch { _audio = null; }

            _volume.ValueChanged += (_, _) =>
            {
                if (_audio == null) return;
                try { _audio.AudioEndpointVolume.MasterVolumeLevelScalar = (float)_volume.Value; _volumeText.Text = (_volume.Value * 100).ToString("F0") + "%"; } catch { }
            };
            _mute.Click += (_, _) =>
            {
                if (_audio == null) return;
                try { _audio.AudioEndpointVolume.Mute = !_audio.AudioEndpointVolume.Mute; _mute.Content = _audio.AudioEndpointVolume.Mute ? "静" : "音"; } catch { }
            };
            _brightness.ValueChanged += (_, _) =>
            {
                if (_brightnessChanging) return;
                try { DisplayBrightness.Set((int)_brightness.Value); _brightnessText.Text = _brightness.Value.ToString("F0"); } catch { }
            };
            _btBtn.Click += async (_, _) =>
            {
                try
                {
                    bool cur = await SystemRadios.GetStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth) == Windows.Devices.Radios.RadioState.On;
                    await SystemRadios.SetStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth, !cur);
                    await RefreshStatesAsync();
                }
                catch { }
            };
            _wifiBtn.Click += async (_, _) =>
            {
                try
                {
                    bool cur = await SystemRadios.GetStateAsync(Windows.Devices.Radios.RadioKind.WiFi) == Windows.Devices.Radios.RadioState.On;
                    await SystemRadios.SetStateAsync(Windows.Devices.Radios.RadioKind.WiFi, !cur);
                    await RefreshStatesAsync();
                }
                catch { }
            };
            _hotspotBtn.Click += async (_, _) =>
            {
                try { var s = await HotspotControl.GetStateAsync(); await HotspotControl.SetAsync(!s.Enabled); await RefreshStatesAsync(); }
                catch { }
            };

            _brightnessRow.Child = Card(Row(Icon("☀"), _brightness, _brightnessText));

            var settings = new Button { Content = "系统设置", FontSize = 12, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
            settings.Click += (_, _) => ShoreHue.UI.Widgets.HostCapabilities.OpenWindowsSettingsPage();

            var rows = new StackPanel();
            rows.Children.Add(Card(Row(Icon("♪"), _volume, _volumeText, _mute)));
            rows.Children.Add(_brightnessRow);
            rows.Children.Add(Card(StateRow("蓝牙", _btState, _btBtn)));
            rows.Children.Add(Card(StateRow("Wi-Fi", _wifiState, _wifiBtn)));
            rows.Children.Add(_hotspotRow);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0) };
            bottom.Children.Add(settings);
            rows.Children.Add(bottom);

            var title = new TextBlock { Text = "快捷设置", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };
            var hint = new TextBlock { Text = "调节音量 / 亮度，开关蓝牙 / Wi-Fi / 热点", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), Margin = new Thickness(0, 2, 0, 0) };
            var head = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
            head.Children.Add(title);
            head.Children.Add(hint);

            var root = new StackPanel { Margin = new Thickness(4), MinHeight = 360 };
            root.Children.Add(head);
            root.Children.Add(rows);
            Content = root;

            Loaded += async (_, _) =>
            {
                await InitBrightnessAsync();
                await RefreshStatesAsync();
                RefreshVolume();
                _timer.Start();
            };
            Unloaded += (_, _) => _timer.Stop();
        }

        private void RefreshVolume()
        {
            if (_audio == null) return;
            try
            {
                _volume.Value = _audio.AudioEndpointVolume.MasterVolumeLevelScalar;
                _volumeText.Text = (_volume.Value * 100).ToString("F0") + "%";
                _mute.Content = _audio.AudioEndpointVolume.Mute ? "静" : "音";
            }
            catch { }
        }

        private async Task InitBrightnessAsync()
        {
            var state = await Task.Run(() =>
                DisplayBrightness.TryGetState(out int min, out int cur, out int max)
                    ? (Ok: true, min: min, cur: cur, max: max)
                    : (Ok: false, min: 0, cur: 0, max: 0));
            if (!state.Ok) return;
            _brightnessRow.Visibility = Visibility.Visible;
            _brightness.Minimum = state.min;
            _brightness.Maximum = state.max;
            _brightnessChanging = true;
            _brightness.Value = state.cur;
            _brightnessText.Text = state.cur.ToString();
            _brightnessChanging = false;
        }

        private async Task RefreshStatesAsync()
        {
            var bt = await SystemRadios.GetStateAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
            _btBtn.IsEnabled = bt.HasValue;
            _btBtn.Content = bt == Windows.Devices.Radios.RadioState.On ? "开" : bt == Windows.Devices.Radios.RadioState.Off ? "关" : "—";
            _btState.Text = bt == Windows.Devices.Radios.RadioState.On ? "已启用" : bt == Windows.Devices.Radios.RadioState.Off ? "已禁用" : "不可用";

            var wifi = await SystemRadios.GetStateAsync(Windows.Devices.Radios.RadioKind.WiFi);
            _wifiBtn.IsEnabled = wifi.HasValue;
            _wifiBtn.Content = wifi == Windows.Devices.Radios.RadioState.On ? "开" : wifi == Windows.Devices.Radios.RadioState.Off ? "关" : "—";
            _wifiState.Text = wifi == Windows.Devices.Radios.RadioState.On ? "已启用" : wifi == Windows.Devices.Radios.RadioState.Off ? "已禁用" : "不可用";

            var hotspot = await HotspotControl.GetStateAsync();
            if (hotspot.Supported)
            {
                _hotspotRow.Visibility = Visibility.Visible;
                _hotspotBtn.Content = hotspot.Enabled ? "开" : "关";
                _hotspotState.Text = hotspot.Enabled ? "已启用" : "已禁用";
            }
            else
            {
                _hotspotRow.Visibility = Visibility.Collapsed;
            }
        }

        private static TextBlock Icon(string s) => new() { Text = s, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        private static Border Card(UIElement child) => new()
        {
            Child = child,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        private static Grid Row(params UIElement?[] children)
        {
            var g = new Grid();
            for (int i = 0; i < children.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] == null) continue;
                Grid.SetColumn(children[i], i);
                g.Children.Add(children[i]);
            }
            return g;
        }

        private static Grid StateRow(string label, TextBlock state, Button btn)
        {
            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) });
            left.Children.Add(state);
            return Row(left, null, btn);
        }

        public string Name => "快捷设置";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { _timer.Stop(); }
    }
}