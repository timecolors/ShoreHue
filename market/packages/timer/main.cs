using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    public class TimerPanel : UserControl, IWidget
    {
        private enum TimerMode { CountUp, CountDown, Alarm }

        private sealed class TimerState
        {
            public DateTime? StartUtc;
            public double ElapsedSeconds;
            public double TotalSeconds;
            public DateTime? EndUtc;
            public double PausedRemaining;
            public DateTime? TargetTime;
            public bool AlarmTriggered;
            public bool Running;
            public int InputHour = 8;
            public int InputMin = 0;
        }

        private readonly DispatcherTimer _timer;
        private TimerMode _mode = TimerMode.CountDown;
        private readonly TimerState _countUp = new();
        private readonly TimerState _countDown = new() { TotalSeconds = 1500, PausedRemaining = 1500, InputHour = 0, InputMin = 25 };
        private readonly TimerState _alarm = new() { InputHour = 8, InputMin = 0 };

        private readonly TextBlock _timeText = new() { FontSize = 32, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };
        private readonly TextBlock _stateText = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)) };
        private readonly TextBox _txtHour = new() { Width = 56, FontSize = 12, TextAlignment = TextAlignment.Center, Text = "0" };
        private readonly TextBox _txtMin = new() { Width = 56, FontSize = 12, TextAlignment = TextAlignment.Center, Text = "5" };
        private readonly TextBlock _inputUnit = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        private readonly StackPanel _inputPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        private readonly StackPanel _presetPanel = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _progressArea = new() { Visibility = Visibility.Collapsed };
        private readonly Border _progressFill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Width = 0, Height = 8 };
        private readonly Button _btnStart = new() { Width = 76, Height = 30, Content = "开始" };
        private readonly Button _btnModeUp = new(), _btnModeDown = new(), _btnModeAlarm = new();

        public TimerPanel()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += OnTick;
            _timer.Start();
            BuildUi();
            ApplyMode();
            UpdateDisplay();
        }

        public string Name => "计时器";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }

        private void BuildUi()
        {
            // ★ 与内置计时器一致：全部使用主题资源样式（AccentButton/FlatButton/DarkTextBox/CardStyle/AccentBrush）
            var accentBtn = (Style)FindResource("AccentButton");
            var flatBtn = (Style)FindResource("FlatButton");
            var darkTb = (Style)FindResource("DarkTextBox");
            var cardStyle = (Style)FindResource("CardStyle");

            _btnModeUp.Content = "正计时"; _btnModeUp.Style = flatBtn; _btnModeUp.Height = 26; _btnModeUp.Padding = new Thickness(10, 0, 10, 0); _btnModeUp.Click += (_, _) => SetMode(TimerMode.CountUp);
            _btnModeDown.Content = "倒计时"; _btnModeDown.Style = flatBtn; _btnModeDown.Height = 26; _btnModeDown.Padding = new Thickness(10, 0, 10, 0); _btnModeDown.Margin = new Thickness(6, 0, 0, 0); _btnModeDown.Click += (_, _) => SetMode(TimerMode.CountDown);
            _btnModeAlarm.Content = "闹钟"; _btnModeAlarm.Style = flatBtn; _btnModeAlarm.Height = 26; _btnModeAlarm.Padding = new Thickness(10, 0, 10, 0); _btnModeAlarm.Margin = new Thickness(6, 0, 0, 0); _btnModeAlarm.Click += (_, _) => SetMode(TimerMode.Alarm);
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            modeRow.Children.Add(_btnModeUp); modeRow.Children.Add(_btnModeDown); modeRow.Children.Add(_btnModeAlarm);

            _inputUnit.Text = "分";
            _txtHour.Style = darkTb; _txtMin.Style = darkTb;
            var colon = new TextBlock { Text = ":", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _inputPanel.Orientation = Orientation.Horizontal; _inputPanel.HorizontalAlignment = HorizontalAlignment.Center;
            _inputPanel.Children.Add(_txtHour); _inputPanel.Children.Add(colon); _inputPanel.Children.Add(_txtMin); _inputPanel.Children.Add(_inputUnit);

            _progressFill.Background = (Brush)FindResource("AccentBrush");
            var progressTrack = new Border { Height = 8, CornerRadius = new CornerRadius(4), Style = cardStyle, Padding = new Thickness(0) };
            progressTrack.Child = _progressFill;
            _progressArea.Children.Add(progressTrack);

            foreach (var (label, sec) in new[] { ("25:00", "1500"), ("5:00", "300"), ("10:00", "600"), ("1:00", "60") })
            {
                var b = new Button { Content = label, Style = flatBtn, Height = 26, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 4, 0), Tag = sec };
                b.Click += Preset_Click;
                _presetPanel.Children.Add(b);
            }

            _btnStart.Style = accentBtn; _btnStart.Click += StartPause_Click;
            var btnReset = new Button { Content = "重置", Style = flatBtn, Width = 72, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
            btnReset.Click += (_, _) => { ResetState(Current); UpdateDisplay(); };
            var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            ctrlRow.Children.Add(_btnStart); ctrlRow.Children.Add(btnReset);

            var root = new StackPanel { Margin = new Thickness(2) };
            root.Children.Add(modeRow);
            var timeCol = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
            timeCol.Children.Add(_timeText); timeCol.Children.Add(_stateText);
            root.Children.Add(timeCol);
            root.Children.Add(_inputPanel);
            root.Children.Add(_progressArea);
            root.Children.Add(_presetPanel);
            root.Children.Add(ctrlRow);
            Content = root;
        }

        private TimerState Current => _mode switch
        {
            TimerMode.CountUp => _countUp,
            TimerMode.CountDown => _countDown,
            _ => _alarm
        };

        private void SetMode(TimerMode mode)
        {
            if (_mode == mode) return;
            SaveInput(Current);
            _mode = mode;
            LoadInput(Current);
            ApplyMode();
            UpdateDisplay();
        }

        private void SaveInput(TimerState s)
        {
            int.TryParse(_txtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(_txtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            s.InputHour = h; s.InputMin = m;
        }

        private void LoadInput(TimerState s)
        {
            _txtHour.Text = s.InputHour.ToString(CultureInfo.InvariantCulture);
            _txtMin.Text = s.InputMin.ToString(CultureInfo.InvariantCulture);
        }

        private void ApplyMode()
        {
            // ★ 与内置一致：当前模式按钮用 AccentButton，其余 FlatButton
            var accentBtn = (Style)FindResource("AccentButton");
            var flatBtn = (Style)FindResource("FlatButton");
            _btnModeUp.Style = _mode == TimerMode.CountUp ? accentBtn : flatBtn;
            _btnModeDown.Style = _mode == TimerMode.CountDown ? accentBtn : flatBtn;
            _btnModeAlarm.Style = _mode == TimerMode.Alarm ? accentBtn : flatBtn;

            bool needInput = _mode == TimerMode.CountDown || _mode == TimerMode.Alarm;
            _inputPanel.Visibility = needInput ? Visibility.Visible : Visibility.Collapsed;
            _presetPanel.Visibility = _mode == TimerMode.CountDown ? Visibility.Visible : Visibility.Collapsed;
            _progressArea.Visibility = _mode == TimerMode.CountDown ? Visibility.Visible : Visibility.Collapsed;
            _inputUnit.Text = _mode == TimerMode.Alarm ? "时:分(24h)" : "分";
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (_mode != TimerMode.CountDown || sender is not Button btn || btn.Tag is not string seconds) return;
            var s = _countDown;
            s.TotalSeconds = double.Parse(seconds, CultureInfo.InvariantCulture);
            s.PausedRemaining = s.TotalSeconds;
            s.Running = false; s.AlarmTriggered = false; s.EndUtc = null;
            int total = (int)s.TotalSeconds;
            _txtHour.Text = (total / 3600).ToString(CultureInfo.InvariantCulture);
            _txtMin.Text = ((total % 3600) / 60).ToString(CultureInfo.InvariantCulture);
            UpdateDisplay();
        }

        private double GetCustomSeconds()
        {
            int.TryParse(_txtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(_txtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            return Math.Max(1, h * 3600 + m * 60);
        }

        private void StartPause_Click(object sender, RoutedEventArgs e)
        {
            var s = Current;
            if (s.AlarmTriggered) { ResetState(s); UpdateDisplay(); return; }
            switch (_mode)
            {
                case TimerMode.Alarm: ToggleAlarm(); break;
                case TimerMode.CountDown: ToggleCountDown(s); break;
                default: ToggleCountUp(s); break;
            }
            UpdateDisplay();
        }

        private void ToggleAlarm()
        {
            var s = _alarm;
            if (s.Running) { s.Running = false; s.TargetTime = null; s.AlarmTriggered = false; return; }
            if (!int.TryParse(_txtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
                !int.TryParse(_txtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ||
                h < 0 || h > 23 || m < 0 || m > 59)
            {
                _stateText.Text = "时间无效（时0-23 分0-59）";
                _stateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 90));
                return;
            }
            SaveInput(s);
            var now = DateTime.Now;
            var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
            if (target <= now) target = target.AddDays(1);
            s.TargetTime = target; s.Running = true; s.AlarmTriggered = false;
        }

        private void ToggleCountDown(TimerState s)
        {
            if (!s.Running)
            {
                double custom = GetCustomSeconds();
                if (Math.Abs(custom - s.TotalSeconds) > 0.5) { s.TotalSeconds = custom; s.PausedRemaining = custom; }
                if (s.PausedRemaining <= 0) s.PausedRemaining = s.TotalSeconds;
                s.EndUtc = DateTime.UtcNow.AddSeconds(s.PausedRemaining);
                s.Running = true;
            }
            else
            {
                s.PausedRemaining = GetCountDownRemaining(s);
                s.EndUtc = null; s.Running = false;
            }
        }

        private void ToggleCountUp(TimerState s)
        {
            if (!s.Running) { s.StartUtc = DateTime.UtcNow; s.Running = true; }
            else { s.ElapsedSeconds += (DateTime.UtcNow - s.StartUtc!.Value).TotalSeconds; s.StartUtc = null; s.Running = false; }
        }

        private void ResetState(TimerState s)
        {
            s.Running = false; s.AlarmTriggered = false; s.StartUtc = null; s.EndUtc = null; s.ElapsedSeconds = 0;
            if (s == _alarm) s.TargetTime = null;
            else s.PausedRemaining = s.TotalSeconds;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            TickAlarm();
            bool needRefresh = _mode switch
            {
                TimerMode.CountUp => _countUp.Running,
                TimerMode.CountDown => _countDown.Running,
                TimerMode.Alarm => _alarm.Running || _alarm.AlarmTriggered,
                _ => false
            };
            if (needRefresh) UpdateDisplay();
        }

        private void TickAlarm()
        {
            if (!_alarm.Running || !_alarm.TargetTime.HasValue) return;
            if (DateTime.Now < _alarm.TargetTime.Value) return;
            _alarm.Running = false; _alarm.AlarmTriggered = true;
            try
            {
                // ★ 安全 v2：外来来源用窄接口发通知（带限频保护）
            ShoreHue.UI.Widgets.HostCapabilities.ShowToast("ShoreHue", "闹钟时间到：" + _alarm.TargetTime.Value.ToString("HH:mm"));
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }
            if (_mode == TimerMode.Alarm) UpdateDisplay();
        }

        private double GetCountDownRemaining(TimerState s)
        {
            if (s.Running && s.EndUtc.HasValue)
                return Math.Max(0, (s.EndUtc.Value - DateTime.UtcNow).TotalSeconds);
            return Math.Max(0, s.PausedRemaining);
        }

        private double GetCountUpElapsed(TimerState s)
        {
            double e = s.ElapsedSeconds;
            if (s.Running && s.StartUtc.HasValue) e += (DateTime.UtcNow - s.StartUtc.Value).TotalSeconds;
            return e;
        }

        private void UpdateDisplay()
        {
            var s = Current;
            if (_mode == TimerMode.Alarm) { UpdateAlarmDisplay(); return; }

            if (s.AlarmTriggered)
            {
                _stateText.Text = "时间到！";
                _stateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                _timeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                _btnStart.Content = "停止提醒";
            }
            else
            {
                _stateText.Text = s.Running ? "计时中" : "就绪";
                _stateText.Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138));
                _timeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
                _btnStart.Content = s.Running ? "暂停" : "开始";
            }

            if (_mode == TimerMode.CountUp)
            {
                int total = (int)Math.Floor(GetCountUpElapsed(s));
                _timeText.Text = string.Format("{0:00}:{1:00}:{2:00}", total / 3600, total % 3600 / 60, total % 60);
                return;
            }

            int remain = (int)Math.Ceiling(GetCountDownRemaining(s));
            _timeText.Text = remain >= 3600
                ? string.Format("{0:00}:{1:00}:{2:00}", remain / 3600, remain % 3600 / 60, remain % 60)
                : string.Format("{0:00}:{1:00}", remain / 60, remain % 60);

            if (s.TotalSeconds > 0 && _progressArea.ActualWidth > 0)
            {
                double ratio = Math.Clamp(1 - remain / s.TotalSeconds, 0, 1);
                _progressFill.Width = _progressArea.ActualWidth * ratio;
            }
        }

        private void UpdateAlarmDisplay()
        {
            var s = _alarm;
            if (s.AlarmTriggered)
            {
                _timeText.Text = s.TargetTime?.ToString("HH:mm") ?? "00:00";
                _stateText.Text = "时间到！";
                _stateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                _timeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                _btnStart.Content = "停止提醒";
                return;
            }
            if (s.Running && s.TargetTime.HasValue)
            {
                var target = s.TargetTime.Value;
                _timeText.Text = target.ToString("HH:mm");
                var remain = target - DateTime.Now;
                if (remain.TotalSeconds < 0) remain = TimeSpan.Zero;
                _stateText.Text = string.Format("剩余 {0} 时 {1} 分 {2} 秒", remain.Hours, remain.Minutes, remain.Seconds);
                _stateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 90));
                _timeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
                _btnStart.Content = "取消闹钟";
                return;
            }
            int.TryParse(_txtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(_txtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            h = Math.Clamp(h, 0, 23); m = Math.Clamp(m, 0, 59);
            _timeText.Text = string.Format("{0:00}:{1:00}", h, m);
            _stateText.Text = "设置闹钟时间，点开始";
            _stateText.Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138));
            _timeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
            _btnStart.Content = "设置闹钟";
        }
    }
}