using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Localization;
using ShoreHue.UI.Widgets;

namespace ShoreHue.UI.Widgets.Timer
{
    public partial class TimerWidget : UserControl, IWidget, IWidgetFooter
    {
        private enum TimerMode { CountUp, CountDown, Alarm }

        /// <summary>
        /// 每个模式的独立计时状态：切换面板不重置、后台继续走。
        /// 计时一律基于绝对时间点计算，避免 tick 累积误差。
        /// </summary>
        private sealed class TimerState
        {
            // 正计时
            public DateTime? StartUtc;
            public double ElapsedSeconds;

            // 倒计时（总时长 + 结束时刻）
            public double TotalSeconds;
            public DateTime? EndUtc;
            public double PausedRemaining;

            // 闹钟（目标时刻，本地时间）
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

        public TimerWidget()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += OnTick;
            // ★ 不在构造里启动：小组件实例会被缓存（面板切走后仍存活），
            //   常驻 Start 就是每秒醒 4 次、永不停止。改由 OnActivated/OnDeactivated 管生命周期。
            ApplyMode();
            UpdateDisplay();
        }

        public new string Name => LocalizationManager.Instance["WidgetTabs_Timer"];

        public UserControl CreateView() => this;

        public void OnActivated() { StartTicking(); }

        /// <summary>★ 面板切走就停表：实例仍在缓存里，但不再空转。
        /// 计时靠 EndUtc/StartUtc 绝对时刻，重挂载后 OnActivated 续上即可，进度不会丢。</summary>
        public void OnDeactivated() { _timer.Stop(); }

        /// <summary>有正在跑的计时/闹钟才需要 tick；否则停掉（空闲零唤醒）。</summary>
        private void StartTicking()
        {
            bool anyRunning = _countUp.Running || _countDown.Running || _alarm.Running;
            if (anyRunning) { if (!_timer.IsEnabled) _timer.Start(); }
            else _timer.Stop();
        }

        public FrameworkElement GetFooterControl()
        {
            // ★ 用户要求：去掉底部提示行（"正计时/倒计时/闹钟：各模式独立计时…"），保持面板简洁。
            return new StackPanel();
        }

        private TimerState Current => _mode switch
        {
            TimerMode.CountUp => _countUp,
            TimerMode.CountDown => _countDown,
            _ => _alarm
        };

        // ================= 模式切换（不重置状态） =================

        private void ModeUp_Click(object sender, RoutedEventArgs e) => SetMode(TimerMode.CountUp);
        private void ModeDown_Click(object sender, RoutedEventArgs e) => SetMode(TimerMode.CountDown);
        private void ModeAlarm_Click(object sender, RoutedEventArgs e) => SetMode(TimerMode.Alarm);

        private void SetMode(TimerMode mode)
        {
            if (_mode == mode) return;

            // 保存当前模式输入
            SaveInput(Current);
            _mode = mode;
            // 加载目标模式输入
            LoadInput(Current);
            ApplyMode();
            UpdateDisplay();
        }

        private void SaveInput(TimerState s)
        {
            int.TryParse(TxtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(TxtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            s.InputHour = h;
            s.InputMin = m;
        }

        private void LoadInput(TimerState s)
        {
            TxtHour.Text = s.InputHour.ToString(CultureInfo.InvariantCulture);
            TxtMin.Text = s.InputMin.ToString(CultureInfo.InvariantCulture);
        }

        private void ApplyMode()
        {
            BtnModeUp.Style = (Style)FindResource(_mode == TimerMode.CountUp ? "AccentButton" : "FlatButton");
            BtnModeDown.Style = (Style)FindResource(_mode == TimerMode.CountDown ? "AccentButton" : "FlatButton");
            BtnModeAlarm.Style = (Style)FindResource(_mode == TimerMode.Alarm ? "AccentButton" : "FlatButton");

            bool needInput = _mode == TimerMode.CountDown || _mode == TimerMode.Alarm;
            InputPanel.Visibility = needInput ? Visibility.Visible : Visibility.Collapsed;
            PresetPanel.Visibility = _mode == TimerMode.CountDown ? Visibility.Visible : Visibility.Collapsed;
            ProgressArea.Visibility = _mode == TimerMode.CountDown ? Visibility.Visible : Visibility.Collapsed;

            // ★ 必须用"重新绑定"而不是直接赋值 `.Text = ...`：
            //   XAML 里这几处是 `{Binding Item[键], Source=LocalizationManager.Instance}` 的**活绑定**，
            //   直接赋值会把绑定整个替换掉（BindingExpression 被清空）—— 于是用户切换界面语言后，
            //   这几处文字/提示**永远不再跟随**（而同一个界面里其它文字都会变）。
            BindLoc(TxtInputUnit, TextBlock.TextProperty,
                _mode == TimerMode.Alarm ? "Timer_InputUnit" : "UI_TimerWidget_419");
            BindLoc(TxtHour, FrameworkElement.ToolTipProperty,
                _mode == TimerMode.Alarm ? "Timer_TipTargetHours" : "Timer_TipHours");
            BindLoc(TxtMin, FrameworkElement.ToolTipProperty,
                _mode == TimerMode.Alarm ? "Timer_TipTargetMinutes" : "Timer_TipMinutes");
        }

        /// <summary>把控件属性重新绑到本地化索引器（保留"跟随语言切换"的语义）。</summary>
        private static void BindLoc(System.Windows.DependencyObject target,
            System.Windows.DependencyProperty prop, string key)
        {
            System.Windows.Data.BindingOperations.SetBinding(target, prop,
                new System.Windows.Data.Binding("Item[" + key + "]")
                {
                    Source = LocalizationManager.Instance,
                    Mode = System.Windows.Data.BindingMode.OneWay
                });
        }

        // ================= 预设（仅倒计时） =================

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (_mode != TimerMode.CountDown || sender is not Button btn || btn.Tag is not string seconds)
                return;

            var s = _countDown;
            s.TotalSeconds = double.Parse(seconds, CultureInfo.InvariantCulture);
            s.PausedRemaining = s.TotalSeconds;
            s.Running = false;
            s.AlarmTriggered = false;
            s.EndUtc = null;

            // ★ 修复：预设点击后同步输入框，避免“开始”时 ToggleCountDown
            //   读取旧的 时/分 输入值覆盖预设时长（如点 1:00 实际从 5:00 开始）。
            int totalSeconds = (int)s.TotalSeconds;
            TxtHour.Text = (totalSeconds / 3600).ToString(CultureInfo.InvariantCulture);
            TxtMin.Text = ((totalSeconds % 3600) / 60).ToString(CultureInfo.InvariantCulture);

            UpdateDisplay();
        }

        private double GetCustomSeconds()
        {
            int.TryParse(TxtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(TxtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            return Math.Max(1, h * 3600 + m * 60);
        }

        // ================= 开始 / 暂停 / 取消 / 重置 =================

        private void StartPause_Click(object sender, RoutedEventArgs e)
        {
            var s = Current;

            // 到点后点击：停止提醒
            if (s.AlarmTriggered)
            {
                ResetState(s);
                UpdateDisplay();
                return;
            }

            switch (_mode)
            {
                case TimerMode.Alarm:
                    ToggleAlarm();
                    break;
                case TimerMode.CountDown:
                    ToggleCountDown(s);
                    break;
                default:
                    ToggleCountUp(s);
                    break;
            }
            UpdateDisplay();
            StartTicking();   // 起停计时器（空闲时不空转，见 StartTicking 注释）
        }

        private void ToggleAlarm()
        {
            var s = _alarm;

            // 已设定：取消闹钟
            if (s.Running)
            {
                s.Running = false;
                s.TargetTime = null;
                s.AlarmTriggered = false;
                return;
            }

            // 解析目标时刻（本地时间）
            if (!int.TryParse(TxtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
                !int.TryParse(TxtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ||
                h < 0 || h > 23 || m < 0 || m > 59)
            {
                StateText.Text = LocalizationManager.Instance["Timer_InvalidTime"];
                StateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 90));
                return;
            }

            SaveInput(s);
            var now = DateTime.Now;
            var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
            if (target <= now) target = target.AddDays(1); // 时间已过则明天

            s.TargetTime = target;
            s.Running = true;
            s.AlarmTriggered = false;
        }

        private void ToggleCountDown(TimerState s)
        {
            if (!s.Running)
            {
                // 未运行：读取输入作为新一轮时长
                double custom = GetCustomSeconds();
                if (Math.Abs(custom - s.TotalSeconds) > 0.5)
                {
                    s.TotalSeconds = custom;
                    s.PausedRemaining = custom;
                }
                if (s.PausedRemaining <= 0) s.PausedRemaining = s.TotalSeconds;
                s.EndUtc = DateTime.UtcNow.AddSeconds(s.PausedRemaining);
                s.Running = true;
            }
            else
            {
                // 暂停：记录剩余
                s.PausedRemaining = GetCountDownRemaining(s);
                s.EndUtc = null;
                s.Running = false;
            }
        }

        private void ToggleCountUp(TimerState s)
        {
            if (!s.Running)
            {
                s.StartUtc = DateTime.UtcNow;
                s.Running = true;
            }
            else
            {
                s.ElapsedSeconds += (DateTime.UtcNow - s.StartUtc!.Value).TotalSeconds;
                s.StartUtc = null;
                s.Running = false;
            }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ResetState(Current);
            UpdateDisplay();
            StartTicking();
        }

        private void ResetState(TimerState s)
        {
            s.Running = false;
            s.AlarmTriggered = false;
            s.StartUtc = null;
            s.EndUtc = null;
            s.ElapsedSeconds = 0;
            if (s == _alarm)
            {
                s.TargetTime = null;
            }
            else
            {
                s.PausedRemaining = s.TotalSeconds;
            }
        }

        // ================= 全局计时 =================

        private void OnTick(object? sender, EventArgs e)
        {
            TickAlarm();
            TickCountDown();

            bool needRefresh = _mode switch
            {
                TimerMode.CountUp => _countUp.Running,
                TimerMode.CountDown => _countDown.Running,
                TimerMode.Alarm => _alarm.Running || _alarm.AlarmTriggered,
                _ => false
            };
            if (needRefresh) UpdateDisplay();
            StartTicking();   // 没有在跑的计时就把表停掉（倒计时/闹钟到点后不再空转）
        }

        /// <summary>
        /// 倒计时归零：弹通知 + 提示音 + 进入"时间到"状态。
        /// ★ 以前只有闹钟会触发提醒，倒计时结束时这一段（红色 TimeUp + 「停止提醒」按钮）**永远不可达** ——
        ///   界面会停在 "计时中… 00:00"，既不响也不弹，用户根本不知道时间到了。
        /// </summary>
        private void TickCountDown()
        {
            var s = _countDown;
            if (!s.Running || !s.EndUtc.HasValue) return;
            if (DateTime.UtcNow < s.EndUtc.Value) return;

            s.Running = false;
            s.EndUtc = null;
            s.PausedRemaining = 0;
            s.AlarmTriggered = true;

            SystemToast.Show("ShoreHue", LocalizationManager.Instance["Timer_TimeUp"]);
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { /* 提示音失败不影响提醒（Toast 已出） */ }

            if (_mode == TimerMode.CountDown) UpdateDisplay();
        }

        /// <summary>
        /// 闹钟：系统时间到达目标时刻即触发，弹系统通知 + 提示音。
        /// 无论当前显示哪个模式，到点都会提醒。
        /// </summary>
        private void TickAlarm()
        {
            if (!_alarm.Running || !_alarm.TargetTime.HasValue) return;
            if (DateTime.Now < _alarm.TargetTime.Value) return;

            _alarm.Running = false;
            _alarm.AlarmTriggered = true;

            SystemToast.Show("ShoreHue", string.Format(LocalizationManager.Instance["Timer_AlarmToast"], _alarm.TargetTime.Value.ToString("HH:mm")));
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }

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
            if (s.Running && s.StartUtc.HasValue)
                e += (DateTime.UtcNow - s.StartUtc.Value).TotalSeconds;
            return e;
        }

        private void UpdateDisplay()
        {
            var s = Current;

            if (_mode == TimerMode.Alarm)
            {
                UpdateAlarmDisplay();
                return;
            }

            if (s.AlarmTriggered)
            {
                StateText.Text = LocalizationManager.Instance["Timer_TimeUp"];
                StateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TimeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                BtnStart.Content = LocalizationManager.Instance["Timer_StopAlarm"];
            }
            else
            {
                StateText.Text = s.Running ? LocalizationManager.Instance["Timer_Running"] : LocalizationManager.Instance["UI_TimerWidget_416"];
                StateText.Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138));
                TimeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
                BtnStart.Content = s.Running ? LocalizationManager.Instance["Timer_Pause"] : LocalizationManager.Instance["UI_TimerWidget_424"];
            }

            if (_mode == TimerMode.CountUp)
            {
                int total = (int)Math.Floor(GetCountUpElapsed(s));
                TimeText.Text = $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}";
                return;
            }

            int remain = (int)Math.Ceiling(GetCountDownRemaining(s));
            TimeText.Text = remain >= 3600
                ? $"{remain / 3600:00}:{remain % 3600 / 60:00}:{remain % 60:00}"
                : $"{remain / 60:00}:{remain % 60:00}";

            if (s.TotalSeconds > 0 &&
                ProgressFill.Parent is FrameworkElement parent && parent.ActualWidth > 0)
            {
                double ratio = Math.Clamp(1 - remain / s.TotalSeconds, 0, 1);
                ProgressFill.Width = parent.ActualWidth * ratio;
            }
        }

        private void UpdateAlarmDisplay()
        {
            var s = _alarm;

            if (s.AlarmTriggered)
            {
                TimeText.Text = s.TargetTime?.ToString("HH:mm") ?? "00:00";
                StateText.Text = LocalizationManager.Instance["Timer_TimeUp"];
                StateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TimeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                BtnStart.Content = LocalizationManager.Instance["Timer_StopAlarm"];
                return;
            }

            if (s.Running && s.TargetTime.HasValue)
            {
                var target = s.TargetTime.Value;
                TimeText.Text = target.ToString("HH:mm");
                var remain = target - DateTime.Now;
                if (remain.TotalSeconds < 0) remain = TimeSpan.Zero;
                StateText.Text = string.Format(LocalizationManager.Instance["Timer_AlarmRemain"], remain.Hours, remain.Minutes, remain.Seconds);
                StateText.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 90));
                TimeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
                BtnStart.Content = LocalizationManager.Instance["Timer_CancelAlarm"];
                return;
            }

            // 就绪：显示当前输入的目标时间
            int.TryParse(TxtHour.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
            int.TryParse(TxtMin.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m);
            h = Math.Clamp(h, 0, 23);
            m = Math.Clamp(m, 0, 59);
            TimeText.Text = $"{h:00}:{m:00}";
            StateText.Text = LocalizationManager.Instance["Timer_SetAlarmHint"];
            StateText.Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138));
            TimeText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
            BtnStart.Content = LocalizationManager.Instance["Timer_SetAlarm"];
        }
    }
}
