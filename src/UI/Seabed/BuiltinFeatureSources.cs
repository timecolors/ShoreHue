using System.Collections.Generic;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 内置功能的纯代码版源码模板（可动态编译）。
    /// 海床里功能节点（计时器/剪贴板等）显示这些模板，用户可在原代码基础上修改，
    /// 点「保存当前节点」→ 同级创建「名称N」新项（编译注册后可选用），原功能不动。
    /// </summary>
    public static class BuiltinFeatureSources
    {
        /// <summary>功能节点 Key → 可编译源码模板。Key 与 ConfigTreeBuilder 中叶子 Key 对应。</summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> Sources = new()
        {
            ["widget-timer"] = TimerSource,
            ["widget-calculator"] = CalculatorSource,
            ["widget-textai"] = TextAiSource,
            ["widget-clipboard"] = ClipboardSource,
            ["widget-note"] = NoteSource,
            ["panel-notification"] = NotificationDockSource,
            ["panel-recent"] = RecentItemsSource,
            ["panel-quicksettings"] = QuickSettingsSource,
            ["panel-taskbar-feature"] = TaskbarPanelSource,
            ["panel-ai"] = AiPanelSource,
            ["panel-apphelper"] = AppHelperPanelSource,
            ["panel-windowcontrol"] = WindowControlPanelSource
        };

        /// <summary>
        /// 面板功能节点 Key：这些节点的模板实现 IWidget（编译后作为自定义面板注册到区域面板下拉），
        /// 「保存当前预设」时 Kind=Panel / BaseType=Panel（区别于小组件变体 Kind=Widget）。
        /// 尚未提供模板的面板节点（任务栏/AI/窗口控制）用户可从零编写，同样按 Panel 归类。
        /// </summary>
        public static readonly System.Collections.Generic.HashSet<string> PanelKeys = new()
        {
            "panel-notification", "panel-recent", "panel-quicksettings",
            "panel-taskbar-feature", "panel-ai", "panel-apphelper", "panel-windowcontrol"
        };

        /// <summary>计时器（纯代码版，功能等价：正计时/倒计时/闹钟、预设、进度、系统提醒）。</summary>
        private const string TimerSource = """
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
                ShoreHue.Infrastructure.WinApi.SystemToast.Show("ShoreHue", "闹钟时间到：" + _alarm.TargetTime.Value.ToString("HH:mm"));
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
""";

        /// <summary>计算器（纯代码版，与内置风格一致：AccentButton/FlatButton/CardStyle）。</summary>
        private const string CalculatorSource = """
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 计算器 · 纯代码版（动态编译运行，与内置风格一致：AccentButton/FlatButton/CardStyle）
    public class CalcPanel : UserControl, IWidget
    {
        private double _left;
        private double _right;
        private string _pendingOp = "";
        private bool _enteringNewNumber = true;
        private bool _error;

        private enum CalcMode { Standard, Scientific, Programmer }
        private CalcMode _mode = CalcMode.Standard;
        private int _radix = 10;
        private bool _useDegrees = true;

        private TextBlock _exprText;
        private TextBlock _displayText;
        private TextBlock _radixText;
        private Button _btnStd, _btnSci, _btnProg, _btnDeg;
        private WrapPanel _sciPanel;
        private StackPanel _progPanel;

        public CalcPanel()
        {
            BuildUi();
            Focusable = true;
            PreviewKeyDown += OnPreviewKeyDown;
            SetMode(CalcMode.Standard);
        }

        public string Name => "计算器";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }

        private Style AccentBtn => (Style)FindResource("AccentButton");
        private Style FlatBtn => (Style)FindResource("FlatButton");

        private void BuildUi()
        {
            // 模式切换
            _btnStd = MakeButton("标准", FlatBtn, 24, 8); _btnStd.Click += (_, _) => SetMode(CalcMode.Standard);
            _btnSci = MakeButton("科学", FlatBtn, 24, 8); _btnSci.Margin = new Thickness(6, 0, 0, 0); _btnSci.Click += (_, _) => SetMode(CalcMode.Scientific);
            _btnProg = MakeButton("程序员", FlatBtn, 24, 8); _btnProg.Margin = new Thickness(6, 0, 0, 0); _btnProg.Click += (_, _) => SetMode(CalcMode.Programmer);
            _radixText = new TextBlock { FontSize = 10, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 136, 216)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            modeRow.Children.Add(_btnStd); modeRow.Children.Add(_btnSci); modeRow.Children.Add(_btnProg); modeRow.Children.Add(_radixText);

            // 显示区
            _exprText = new TextBlock { FontSize = 11, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(138, 138, 138)), TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            _displayText = new TextBlock { Text = "0", FontSize = 26, FontWeight = FontWeights.Light, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 238, 238)), TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var displayCol = new StackPanel { Children = { _exprText, _displayText } };
            var displayBorder = new Border { Style = (Style)FindResource("CardStyle"), Margin = new Thickness(0, 0, 0, 6), Child = displayCol };

            // 科学函数行
            _sciPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            foreach (var (label, tag, handler) in new (string, string, Action<string>)[]
            {
                ("sin","sin",SciFn), ("cos","cos",SciFn), ("tan","tan",SciFn),
                ("asin","asin",SciFn), ("acos","acos",SciFn), ("atan","atan",SciFn),
                ("sinh","sinh",SciFn), ("cosh","cosh",SciFn), ("tanh","tanh",SciFn),
                ("√","sqrt",SciFn), ("x²","sqr",SciFn), ("xʸ","pow",Op),
                ("1/x","inv",SciFn), ("n!","fact",SciFn), ("π","pi",SciFn), ("e","e",SciFn),
                ("eˣ","exp",SciFn), ("10ˣ","pow10",SciFn), ("ln","ln",SciFn), ("log","log",SciFn)
            })
            {
                var b = new Button { Content = label, Style = FlatBtn, FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 3, 0), Tag = tag };
                b.Click += (s, _) => { var bt = (Button)s; handler((string)bt.Tag); };
                _sciPanel.Children.Add(b);
            }
            _btnDeg = new Button { Content = "DEG", Style = FlatBtn, FontSize = 10, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(3, 0, 0, 0), Tag = "deg" };
            _btnDeg.Click += (_, _) => { _useDegrees = !_useDegrees; _btnDeg.Content = _useDegrees ? "DEG" : "RAD"; };
            _sciPanel.Children.Add(_btnDeg);

            // 程序员行
            _progPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            var radixRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, r) in new (string, string)[] { ("HEX","16"), ("DEC","10"), ("OCT","8"), ("BIN","2") })
            {
                var b = new Button { Content = label, Style = FlatBtn, FontSize = 10, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0), Tag = r };
                b.Click += (s, _) => { var bt = (Button)s; if (int.TryParse((string)bt.Tag, out int rr)) { _radix = rr; _radixText.Text = RadixName(rr); UpdateDisplayRadix(); } };
                radixRow.Children.Add(b);
            }
            var bitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            foreach (var (label, tag) in new (string, string)[] { ("AND","&"), ("OR","|"), ("XOR","^") })
            {
                var b = new Button { Content = label, Style = FlatBtn, FontSize = 10, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0), Tag = tag };
                b.Click += (s, _) => { var bt = (Button)s; InputOperator((string)bt.Tag); };
                bitRow.Children.Add(b);
            }
            var btnNot = new Button { Content = "NOT", Style = FlatBtn, FontSize = 10, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0) };
            btnNot.Click += (_, _) => BitNot();
            var btnShl = new Button { Content = "<<", Style = FlatBtn, FontSize = 10, Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 3, 0), Tag = "l" };
            btnShl.Click += (s, _) => { var bt = (Button)s; BitShift((string)bt.Tag); };
            var btnShr = new Button { Content = ">>", Style = FlatBtn, FontSize = 10, Padding = new Thickness(5, 1, 5, 1), Tag = "r" };
            btnShr.Click += (s, _) => { var bt = (Button)s; BitShift((string)bt.Tag); };
            bitRow.Children.Add(btnNot); bitRow.Children.Add(btnShl); bitRow.Children.Add(btnShr);
            _progPanel.Children.Add(radixRow); _progPanel.Children.Add(bitRow);

            // 键盘 4x5
            var keys = new Grid { Margin = new Thickness(0) };
            for (int i = 0; i < 5; i++) keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Put(int r, int c, string content, Action handler, bool accent = false, double fontSize = 15, string? opTag = null)
            {
                var b = new Button { Content = content, Style = accent ? AccentBtn : FlatBtn, FontSize = fontSize, Margin = new Thickness(1), Foreground = opTag != null ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 136, 216)) : null };
                b.Click += (_, _) => handler();
                Grid.SetRow(b, r); Grid.SetColumn(b, c);
                keys.Children.Add(b);
            }
            Put(0,0,"C", Clear); Put(0,1,"±", Negate); Put(0,2,"%", Percent);
            Put(0,3,"÷", () => InputOperator("/"), opTag:"/");
            Put(1,0,"7", () => InputDigit("7"), fontSize:16); Put(1,1,"8", () => InputDigit("8"), fontSize:16); Put(1,2,"9", () => InputDigit("9"), fontSize:16);
            Put(1,3,"×", () => InputOperator("*"), opTag:"*");
            Put(2,0,"4", () => InputDigit("4"), fontSize:16); Put(2,1,"5", () => InputDigit("5"), fontSize:16); Put(2,2,"6", () => InputDigit("6"), fontSize:16);
            Put(2,3,"−", () => InputOperator("-"), opTag:"-");
            Put(3,0,"1", () => InputDigit("1"), fontSize:16); Put(3,1,"2", () => InputDigit("2"), fontSize:16); Put(3,2,"3", () => InputDigit("3"), fontSize:16);
            Put(3,3,"+", () => InputOperator("+"), opTag:"+");
            Put(4,0,"0", () => InputDigit("0"), fontSize:16); Put(4,1,".", InputDot, fontSize:16); Put(4,2,"⌫", Backspace, fontSize:14);
            Put(4,3,"=", Calculate, accent:true, fontSize:16);

            var root = new Grid { Margin = new Thickness(2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(modeRow, 0); Grid.SetRow(displayBorder, 1); Grid.SetRow(_sciPanel, 2); Grid.SetRow(_progPanel, 2); Grid.SetRow(keys, 3);
            root.Children.Add(modeRow); root.Children.Add(displayBorder); root.Children.Add(_sciPanel); root.Children.Add(_progPanel); root.Children.Add(keys);
            Content = root;
        }

        private Button MakeButton(string content, Style style, double height, double pad)
        {
            return new Button { Content = content, Style = style, Height = height, Padding = new Thickness(pad, 0, pad, 0) };
        }

        private void SetMode(CalcMode mode)
        {
            _mode = mode;
            _btnStd.Style = mode == CalcMode.Standard ? AccentBtn : FlatBtn;
            _btnSci.Style = mode == CalcMode.Scientific ? AccentBtn : FlatBtn;
            _btnProg.Style = mode == CalcMode.Programmer ? AccentBtn : FlatBtn;
            _sciPanel.Visibility = mode == CalcMode.Scientific ? Visibility.Visible : Visibility.Collapsed;
            _progPanel.Visibility = mode == CalcMode.Programmer ? Visibility.Visible : Visibility.Collapsed;
            _radixText.Text = mode == CalcMode.Programmer ? RadixName(_radix) : "";
        }

        // ============ 逻辑（与内置一致） ============
        private void InputDigit(string digit)
        {
            if (_error) return;
            if (_enteringNewNumber) { _displayText.Text = digit; _enteringNewNumber = false; }
            else if (_displayText.Text == "0") _displayText.Text = digit;
            else if (_displayText.Text.Length < 16) _displayText.Text += digit;
        }
        private void InputDot()
        {
            if (_error) return;
            if (_enteringNewNumber) { _displayText.Text = "0."; _enteringNewNumber = false; }
            else if (!_displayText.Text.Contains('.')) _displayText.Text += ".";
        }
        private void InputOperator(string op)
        {
            if (_error) return;
            if (!_enteringNewNumber && !string.IsNullOrEmpty(_pendingOp)) Calculate();
            if (double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) _left = v;
            _pendingOp = op; _enteringNewNumber = true;
            string displayOp = op == "pow" ? "^" : op;
            _exprText.Text = Format(_left) + " " + displayOp;
        }
        private void Calculate()
        {
            if (string.IsNullOrEmpty(_pendingOp) || _error) return;
            if (!double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double right)) right = _right;
            _right = right;
            double result = _pendingOp switch
            {
                "+" => _left + right,
                "-" => _left - right,
                "*" => _left * right,
                "/" => Math.Abs(right) < 1e-12 ? double.NaN : _left / right,
                "&" => (double)((long)Math.Round(_left) & (long)Math.Round(right)),
                "|" => (double)((long)Math.Round(_left) | (long)Math.Round(right)),
                "^" => (double)((long)Math.Round(_left) ^ (long)Math.Round(right)),
                "pow" => Math.Pow(_left, right),
                _ => right
            };
            if (double.IsNaN(result) || double.IsInfinity(result)) { _error = true; _displayText.Text = "错误"; _exprText.Text = ""; _pendingOp = ""; return; }
            string displayOp = _pendingOp == "pow" ? "^" : _pendingOp;
            _exprText.Text = Format(_left) + " " + displayOp + " " + Format(right) + " =";
            _left = result; _displayText.Text = Format(result); _pendingOp = ""; _enteringNewNumber = true;
        }
        private void Clear() { _left = 0; _right = 0; _pendingOp = ""; _enteringNewNumber = true; _error = false; _exprText.Text = ""; _displayText.Text = "0"; }
        private void Negate() { if (_error) return; if (double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) _displayText.Text = Format(-v); }
        private void Percent() { if (_error) return; if (double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) _displayText.Text = Format(v / 100); }
        private void Backspace() { if (_error || _enteringNewNumber) return; if (_displayText.Text.Length > 1) _displayText.Text = _displayText.Text[..^1]; else { _displayText.Text = "0"; _enteringNewNumber = true; } }
        private void SciFn(string fn)
        {
            if (_error) return;
            if (!double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) v = 0;
            double ToRad(double d) => _useDegrees ? d * Math.PI / 180.0 : d;
            double FromRad(double r) => _useDegrees ? r * 180.0 / Math.PI : r;
            double result = fn switch
            {
                "sin" => Math.Sin(ToRad(v)), "cos" => Math.Cos(ToRad(v)), "tan" => Math.Tan(ToRad(v)),
                "asin" => FromRad(Math.Asin(v)), "acos" => FromRad(Math.Acos(v)), "atan" => FromRad(Math.Atan(v)),
                "sinh" => Math.Sinh(v), "cosh" => Math.Cosh(v), "tanh" => Math.Tanh(v),
                "sqrt" => v < 0 ? double.NaN : Math.Sqrt(v), "sqr" => v * v,
                "inv" => Math.Abs(v) < 1e-12 ? double.NaN : 1.0 / v,
                "pi" => Math.PI, "e" => Math.E, "exp" => Math.Exp(v), "pow10" => Math.Pow(10, v),
                "fact" => Factorial((long)Math.Round(v)),
                "ln" => v <= 0 ? double.NaN : Math.Log(v), "log" => v <= 0 ? double.NaN : Math.Log10(v),
                _ => v
            };
            if (double.IsNaN(result) || double.IsInfinity(result)) { _error = true; _displayText.Text = "错误"; _exprText.Text = ""; return; }
            _displayText.Text = Format(result); _enteringNewNumber = true;
        }
        private static double Factorial(long n) { if (n < 0 || n > 170) return double.NaN; double r = 1; for (long i = 2; i <= n; i++) r *= i; return r; }
        private void BitNot() { if (_error) return; if (double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { _displayText.Text = Format(~(long)Math.Round(v)); _enteringNewNumber = true; } }
        private void BitShift(string dir) { if (_error) return; if (!double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return; long lv = (long)Math.Round(v); _displayText.Text = Format(dir == "l" ? lv * 2 : lv / 2); _enteringNewNumber = true; }
        private void Op(string op) => InputOperator(op);
        private static string RadixName(int radix) => radix switch { 16 => "HEX", 8 => "OCT", 2 => "BIN", _ => "DEC" };
        private void UpdateDisplayRadix() { if (_mode == CalcMode.Programmer && double.TryParse(_displayText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) _displayText.Text = Format(v); }
        private string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "错误";
            bool isInteger = Math.Abs(v - Math.Round(v)) < 1e-12 && Math.Abs(v) < 9.2e18;
            return _mode == CalcMode.Programmer && isInteger ? FormatRadix((long)Math.Round(v))
                : isInteger && Math.Abs(v) < 1e15 ? v.ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("G12", CultureInfo.InvariantCulture);
        }
        private string FormatRadix(long v) => _radix switch
        {
            16 => "0x" + Convert.ToString(v, 16).ToUpperInvariant(),
            8 => "0o" + Convert.ToString(v, 8),
            2 => "0b" + Convert.ToString(v, 2),
            _ => v.ToString(CultureInfo.InvariantCulture)
        };

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            void D(string d) { InputDigit(d); e.Handled = true; }
            switch (e.Key)
            {
                case Key.D0: case Key.NumPad0: D("0"); break;
                case Key.D1: case Key.NumPad1: D("1"); break;
                case Key.D2: case Key.NumPad2: D("2"); break;
                case Key.D3: case Key.NumPad3: D("3"); break;
                case Key.D4: case Key.NumPad4: D("4"); break;
                case Key.D5: if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)) { Percent(); e.Handled = true; break; } D("5"); break;
                case Key.NumPad5: D("5"); break;
                case Key.D6: case Key.NumPad6: D("6"); break;
                case Key.D7: case Key.NumPad7: D("7"); break;
                case Key.D8: case Key.NumPad8: D("8"); break;
                case Key.D9: case Key.NumPad9: D("9"); break;
                case Key.Decimal: case Key.OemPeriod: InputDot(); e.Handled = true; break;
                case Key.Add: InputOperator("+"); e.Handled = true; break;
                case Key.OemPlus: if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)) InputOperator("+"); else Calculate(); e.Handled = true; break;
                case Key.Subtract: case Key.OemMinus: InputOperator("-"); e.Handled = true; break;
                case Key.Multiply: InputOperator("*"); e.Handled = true; break;
                case Key.Divide: case Key.OemQuestion: InputOperator("/"); e.Handled = true; break;
                case Key.Enter: Calculate(); e.Handled = true; break;
                case Key.Back: Backspace(); e.Handled = true; break;
                case Key.Escape: Clear(); e.Handled = true; break;
            }
        }
    }
}
""";

        /// <summary>划词翻译（纯代码版，与内置风格一致：FlatButton/CardStyle）。</summary>
        private const string TextAiSource = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 划词翻译 · 纯代码版（动态编译运行，与内置风格一致：FlatButton/CardStyle）
    public class TextAiPanel : UserControl, IWidget
    {
        private readonly AiChatClient _client = new();
        private CancellationTokenSource? _cts;

        private TextBlock _sourceText, _resultText, _stateText;
        private Button _btnCopy;

        public TextAiPanel()
        {
            BuildUi();
        }

        public string Name => "划词翻译";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }

        private void BuildUi()
        {
            var flatBtn = (Style)FindResource("FlatButton");
            var cardStyle = (Style)FindResource("CardStyle");

            var title = new TextBlock { Text = "划词翻译", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var btnSettings = new Button { Content = "设置", Style = flatBtn, Padding = new Thickness(8, 2, 8, 2), Height = 24 };
            btnSettings.Click += (_, _) => ShoreHue.UI.Widgets.HostCapabilities.OpenSettingsPage("tabAI");
            var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(btnSettings, 2);
            head.Children.Add(title); head.Children.Add(btnSettings);

            _sourceText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)), TextWrapping = TextWrapping.Wrap, MaxHeight = 140, TextTrimming = TextTrimming.CharacterEllipsis };
            var srcCard = new Border { Style = cardStyle, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 6, 8, 6), Child = new StackPanel { Children = { new TextBlock { Text = "选中文本", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), Margin = new Thickness(0, 0, 0, 4) }, _sourceText } } };

            _stateText = new TextBlock { Margin = new Thickness(10, 0, 0, 0), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _btnCopy = new Button { Content = "复制", Style = flatBtn, Padding = new Thickness(8, 2, 8, 2), Height = 22, FontSize = 11 };
            _btnCopy.Click += (_, _) => CopyResult();
            var resultHead = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            resultHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            resultHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            resultHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_stateText, 1); Grid.SetColumn(_btnCopy, 2);
            resultHead.Children.Add(new TextBlock { Text = "译文", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), VerticalAlignment = VerticalAlignment.Center });
            resultHead.Children.Add(_stateText); resultHead.Children.Add(_btnCopy);

            _resultText = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextWrapping = TextWrapping.Wrap };
            var resultCard = new Border { Style = cardStyle, Padding = new Thickness(8, 6, 8, 6), Child = new StackPanel { Children = { resultHead, _resultText } } };

            var content = new StackPanel { Children = { srcCard, resultCard } };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content };

            var root = new Grid { Margin = new Thickness(2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(head, 0); Grid.SetRow(scroll, 1);
            root.Children.Add(head); root.Children.Add(scroll);
            Content = root;
        }

        private void CopyResult()
        {
            string text = _resultText.Text.Trim();
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
                _btnCopy.Content = "已复制";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) => { timer.Stop(); _btnCopy.Content = "复制"; };
                timer.Start();
            }
            catch { }
        }

        public async Task CaptureAndTranslateAsync()
        {
            var ai = AiSettingsStore.Load();
            if (!ai.Enabled || string.IsNullOrWhiteSpace(ai.ApiKey)) { ShowState("未配置 AI（请在设置中填写）", true); return; }
            ShowState("读取选中文本…", false);
            var capture = await SelectedTextCapture.CaptureAsync(ownHwnd: GetOwnHwnd());
            if (!capture.Success) { ShowState(capture.Message.Length > 0 ? capture.Message : "未选中文字", true); return; }
            await TranslateAsync(capture.Text!, ai);
        }

        private IntPtr GetOwnHwnd()
        {
            try { return new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle; }
            catch { return IntPtr.Zero; }
        }

        private async Task TranslateAsync(string text, AiSettings ai)
        {
            Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _sourceText.Text = text.Length > 1000 ? text[..1000] + "…" : text;
            _resultText.Text = "";
            _resultText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
            ShowState("翻译中…", false);
            try
            {
                bool chinese = CountCjk(text) >= Math.Max(3, text.Length / 6);
                string prompt = (chinese
                    ? "请将以下内容翻译成英文。只输出译文，不要任何解释、引号或多余文字：\n\n"
                    : "请将以下内容翻译成中文。只输出译文，不要任何解释、引号或多余文字：\n\n") + text;
                var translateSettings = new AiSettings
                {
                    Enabled = ai.Enabled, BaseUrl = ai.BaseUrl, ApiKey = ai.ApiKey, Model = ai.Model,
                    Temperature = Math.Min(ai.Temperature, 0.5), ContextWindowTokens = ai.ContextWindowTokens,
                    SystemPrompt = "你是翻译引擎，只输出译文。"
                };
                var history = new List<ChatMessage>();
                string full = await _client.StreamChatAsync(translateSettings, history, prompt, delta =>
                {
                    Dispatcher.Invoke(() => { if (!ct.IsCancellationRequested) _resultText.Text += delta; });
                }, ct);
                if (!ct.IsCancellationRequested) ShowState(full.Length > 0 ? "" : "无结果", full.Length == 0);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowState("翻译失败：" + ex.Message, true); }
        }

        private static int CountCjk(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) count++;
                else if (c >= 0x3040 && c <= 0x30FF) count++;
                else if (c >= 0xAC00 && c <= 0xD7AF) count++;
            }
            return count;
        }

        private void ShowState(string? text, bool isError)
        {
            _stateText.Text = text ?? "";
            _stateText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(255, 130, 120) : Color.FromRgb(138, 138, 138));
        }

        private void Cancel() { try { _cts?.Cancel(); } catch { } _cts = null; }
    }
}
""";

        /// <summary>便签（纯代码版，风格与内置一致：便签条+颜色+标题/内容编辑）。</summary>
        private const string NoteSource = """
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.Core.Services;
using ShoreHue.UI.Widgets;
using WinForms = System.Windows.Forms;

namespace ShoreHue.Builtin
{
    // 便签 · 纯代码版（动态编译运行，风格与内置一致：便签条+颜色+标题/内容编辑）
    public class NotePanel : UserControl, IWidget
    {
        private readonly INoteService _noteService;
        private bool _isUpdating;
        private StackPanel _tabPanel;
        private TextBox _titleEditor, _contentEditor;
        private Button _btnToggleTitle, _btnColor, _btnNew, _btnDelete;
        private TextBlock _statusText;

        public NotePanel()
        {
            _noteService = ServiceManager.Instance.GetService<NoteManager>() as INoteService;
            BuildUi();
            if (_noteService != null)
            {
                _noteService.NotesChanged += (_, _) => Dispatcher.Invoke(RefreshAll);
                RefreshAll();
            }
        }

        public string Name => "便签";
        public UserControl CreateView() => this;
        public void OnActivated() { if (_noteService != null) _noteService.SetCurrentNote(_noteService.CurrentNote); RefreshAll(); }
        public void OnDeactivated() { }

        private void BuildUi()
        {
            // 标签栏
            var tabScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _tabPanel = new StackPanel { Orientation = Orientation.Horizontal };
            tabScroll.Content = _tabPanel;

            _btnToggleTitle = new Button { Content = "标", Width = 24, Height = 24, FontSize = 14, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "显示/隐藏标题" };
            _btnToggleTitle.Click += (_, _) => { if (_noteService?.CurrentNote != null) { _noteService.CurrentNote.ShowTitle = !_noteService.CurrentNote.ShowTitle; RefreshAll(); } };
            _btnColor = new Button { Content = "色", Width = 24, Height = 24, FontSize = 14, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "颜色" };
            _btnColor.Click += (_, _) => ColorPicker();
            var headRight = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            headRight.Children.Add(_btnToggleTitle); headRight.Children.Add(_btnColor);

            var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(tabScroll, 0); Grid.SetColumn(headRight, 1);
            head.Children.Add(tabScroll); head.Children.Add(headRight);

            // 标题 + 内容
            _titleEditor = new TextBox { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            _titleEditor.TextChanged += (_, _) => { if (!_isUpdating && _noteService?.CurrentNote != null) _noteService.CurrentNote.Title = _titleEditor.Text; };
            _contentEditor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 120 };
            _contentEditor.TextChanged += (_, _) => { if (!_isUpdating && _noteService?.CurrentNote != null) _noteService.CurrentNote.Content = _contentEditor.Text; };
            var editCol = new StackPanel { Children = { _titleEditor, _contentEditor } };
            var contentScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300, Content = editCol };

            var root = new Grid { Margin = new Thickness(2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(head, 0); Grid.SetRow(contentScroll, 1);
            root.Children.Add(head); root.Children.Add(contentScroll);
            Content = root;
        }

        private void RefreshAll()
        {
            if (_tabPanel == null || _noteService == null) return;
            _tabPanel.Children.Clear();
            foreach (var note in _noteService.Notes)
            {
                note.IsCurrent = note == _noteService.CurrentNote;
                var tab = new Border
                {
                    Background = TabBrush(note.Color),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    BorderThickness = note.IsCurrent ? new Thickness(2) : new Thickness(0),
                    BorderBrush = note.IsCurrent ? new SolidColorBrush(Color.FromRgb(0, 120, 212)) : Brushes.Transparent,
                    Tag = note
                };
                tab.MouseLeftButtonUp += Tab_Click;
                tab.Child = new TextBlock { Text = note.Title, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)), MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis };
                _tabPanel.Children.Add(tab);
            }
            var current = _noteService.CurrentNote;
            _isUpdating = true;
            _titleEditor.Text = current?.Title ?? "";
            _titleEditor.Visibility = current != null && current.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            _contentEditor.Text = current?.Content ?? "";
            _isUpdating = false;
            UpdateStatus();
        }

        private static SolidColorBrush TabBrush(string color)
        {
            try { if (!string.IsNullOrEmpty(color)) return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); } catch { }
            return new SolidColorBrush(Color.FromRgb(0, 0, 0));
        }

        private void Tab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border b || b.Tag is not NoteItem note || _noteService == null) return;
            _noteService.SetCurrentNote(note);
            RefreshAll();
        }

        private void UpdateStatus()
        {
            // 状态栏由 Footer 提供，面板内简洁即可
        }

        private void ColorPicker()
        {
            var current = _noteService?.CurrentNote;
            if (current == null) return;
            using var dialog = new WinForms.ColorDialog();
            dialog.Color = HexToDrawing(current.Color);
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                string hex = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                _noteService.UpdateNoteColor(current, hex);
                RefreshAll();
            }
        }

        private System.Drawing.Color HexToDrawing(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return System.Drawing.Color.FromArgb(255, 0, 0, 0);
                if (hex.StartsWith("#")) hex = hex.Substring(1);
                if (hex.Length == 6)
                    return System.Drawing.Color.FromArgb(255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { }
            return System.Drawing.Color.FromArgb(255, 0, 0, 0);
        }
    }
}
""";

        /// <summary>剪贴板（纯代码版，风格与内置一致：深色卡片/列表）。</summary>
        private const string ClipboardSource = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 剪贴板 · 纯代码版（动态编译运行，风格与内置一致：深色卡片/列表）
    public class ClipboardPanel : UserControl, IWidget
    {
        private readonly IClipboardService _clipboard;
        private readonly List<ClipboardManager.ClipboardItem> _selected = new();
        private ContentControl _historyList;
        private TextBlock _searchPlaceholder, _statusText;
        private TextBox _searchBox;
        private Button _btnDeleteSelected;
        private StackPanel _filterPanel;
        private string _filterType = "All";
        private string _searchQuery = "";

        public ClipboardPanel()
        {
            _clipboard = ServiceManager.Instance.GetService<ClipboardManager>() as IClipboardService;
            BuildUi();
            if (_clipboard != null) _clipboard.HistoryChanged += (_, _) => RefreshList();
            RefreshList();
        }

        private void RefreshList()
        {
            if (_historyList == null || _clipboard == null) return;
            IEnumerable<ClipboardManager.ClipboardItem> items = _clipboard.History;
            switch (_filterType)
            {
                case "Pinned": items = items.Where(i => i.IsPinned); break;
                case "Text": items = items.Where(i => i.Type == "Text" || i.Type == "Html"); break;
                case "Link": items = items.Where(i => i.Type == "Link"); break;
                case "Image": items = items.Where(i => i.Type == "Image"); break;
                case "File": items = items.Where(i => i.Type == "File"); break;
            }
            if (!string.IsNullOrEmpty(_searchQuery))
            {
                string q = _searchQuery.ToLower();
                items = items.Where(i =>
                    (i.DisplayText ?? "").ToLower().Contains(q) ||
                    (i.FullText ?? "").ToLower().Contains(q));
            }

            var panel = new StackPanel();
            foreach (var item in items.Take(50))
            {
                panel.Children.Add(BuildClipRow(item));
            }
            _historyList.Content = panel;
            UpdateStatus(null);
        }

        private Border BuildClipRow(ClipboardManager.ClipboardItem item)
        {
            string preview = item.Type == "File"
                ? string.Join("  ", item.FilePaths ?? new List<string>())
                : item.DisplayText ?? "";
            if (preview.Length > 120) preview = preview.Substring(0, 120) + "…";

            var text = new TextBlock
            {
                Text = preview,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 48,
                TextWrapping = TextWrapping.Wrap
            };
            var time = new TextBlock
            {
                Text = item.Timestamp.ToString("HH:mm"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)),
                VerticalAlignment = VerticalAlignment.Top
            };
            var pin = new TextBlock
            {
                Text = item.IsPinned ? "★" : "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 179, 1)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            Grid.SetColumn(pin, 2);
            grid.Children.Add(pin);

            var row = new Border
            {
                Child = grid,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            row.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    string copyText = item.Type == "File"
                        ? string.Join(Environment.NewLine, item.FilePaths ?? new List<string>())
                        : item.FullText ?? item.DisplayText;
                    if (!string.IsNullOrEmpty(copyText)) Clipboard.SetText(copyText);
                }
                catch { }
            };
            return row;
        }

        public string Name => "剪贴板";
        public UserControl CreateView() => this;
        public void OnActivated() => RefreshList();
        public void OnDeactivated() { }

        private void BuildUi()
        {
            // 搜索框
            _searchBox = new TextBox { FontSize = 12, Padding = new Thickness(6, 4, 6, 4), Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), BorderThickness = new Thickness(0) };
            _searchBox.TextChanged += (_, _) => { _searchQuery = _searchBox.Text; if (_searchPlaceholder != null) _searchPlaceholder.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Visible : Visibility.Collapsed; RefreshList(); };
            _searchPlaceholder = new TextBlock { Text = "搜索剪贴板…", Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)), FontSize = 11, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            searchHost.Children.Add(_searchBox); searchHost.Children.Add(_searchPlaceholder);

            // 过滤按钮
            _filterPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var (label, tag) in new (string, string)[] { ("全部","All"), ("收藏","Pinned"), ("文本","Text"), ("链接","Link"), ("图片","Image"), ("文件","File") })
            {
                var b = new Button { Content = label, Tag = tag, FontSize = 11, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)) };
                b.Click += Filter_Click;
                _filterPanel.Children.Add(b);
            }

            // 列表
            _historyList = new ContentControl { Background = Brushes.Transparent };
            var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _historyList };

            var root = new StackPanel { Margin = new Thickness(2) };
            root.Children.Add(searchHost);
            root.Children.Add(_filterPanel);
            root.Children.Add(listScroll);
            Content = root;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string t) return;
            _filterType = t;
            foreach (var child in _filterPanel.Children)
            {
                if (child is Button b)
                {
                    bool active = (b.Tag as string) == t;
            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(204, 204, 204));
        }
    }
    RefreshList();
}

private void UpdateStatus(string? msg)
{
    if (_statusText == null) return;
    _statusText.Text = msg ?? (_clipboard != null ? "共 " + _clipboard.History.Count + " 条" : "");
}
    }
}
""";

        /// <summary>通知坞（纯代码版：按应用分组、每条可单独关闭、点击打开、一键清空）。</summary>
        private const string NotificationDockSource = """
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
""";

        /// <summary>快捷设置（纯代码版，功能等价：音量/亮度/蓝牙/Wi-Fi/热点/打开系统设置）。</summary>
        private const string QuickSettingsSource = """
using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, Button> _perfButtons = new();

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

            // ★ 用主题里的扁平按钮样式（与宿主 QuickSettingsView 同款），避免文件化后变成默认灰按钮
            _mute.Style = TryFindResource("FlatButton") as Style;
            _btBtn.Style = TryFindResource("FlatButton") as Style;
            _wifiBtn.Style = TryFindResource("FlatButton") as Style;
            _hotspotBtn.Style = TryFindResource("FlatButton") as Style;
            _brightnessRow.Child = Card(Row(Icon("☀"), _brightness, _brightnessText));

            var settings = new Button { Content = "系统设置", FontSize = 12, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("FlatButton") as Style };
            settings.Click += (_, _) => SystemLauncher.OpenWindowsSettings();

            var rows = new StackPanel();
            rows.Children.Add(Card(Row(Icon("♪"), _volume, _volumeText, _mute)));
            rows.Children.Add(_brightnessRow);
            rows.Children.Add(Card(StateRow("蓝牙", _btState, _btBtn)));
            rows.Children.Add(Card(StateRow("Wi-Fi", _wifiState, _wifiBtn)));
            rows.Children.Add(_hotspotRow);
            rows.Children.Add(BuildPerfRow());
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
                RefreshPerfButtons();
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

        // ★ ShoreHue 性能模式（与宿主 QuickSettingsView 对齐）：顺滑 / 正常 / 省电 / 自定义
        private FrameworkElement BuildPerfRow()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
            foreach (var pair in new (string Label, string Tag)[] { ("顺滑", "Smooth"), ("正常", "Normal"), ("省电", "PowerSaver"), ("自定义", "Custom") })
            {
                var b = new Button { Content = pair.Label, Tag = pair.Tag, FontSize = 12, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0), Style = TryFindResource("FlatButton") as Style };
                b.Click += (_, _) => ApplyPerfMode(pair.Tag);
                _perfButtons[pair.Tag] = b;
                row.Children.Add(b);
            }
            return Card(row);
        }

        private void ApplyPerfMode(string mode)
        {
            if (mode == "Custom") return;
            var s = HostCapabilities.Settings;
            if (s == null || s.PerformanceMode == mode) return;
            s.SetPerformanceMode(mode);
            RefreshPerfButtons();
        }

        private void RefreshPerfButtons()
        {
            var s = HostCapabilities.Settings;
            string mode = s == null ? "Normal" : s.PerformanceMode;
            foreach (var kv in _perfButtons)
            {
                bool active = kv.Key == mode;
                kv.Value.Style = (TryFindResource(active ? "AccentButton" : "FlatButton") as Style) ?? kv.Value.Style;
                kv.Value.Opacity = active ? 1.0 : 0.55;
                kv.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
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
        public void OnActivated() { RefreshPerfButtons(); }
        public void OnDeactivated() { _timer.Stop(); }
    }
}
""";

        /// <summary>最近使用（纯代码版，功能等价：最近文件/最近应用/常用网页，一键打开与收藏）。</summary>
        private const string RecentItemsSource = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    public class RecentItemsPanel : UserControl, IWidget
    {
        private enum Kind { File, App, Web }

        private sealed class Entry
        {
            public Kind Kind;
            public string Name = "";
            public string Detail = "";
            public string Path = "";
            public IntPtr? Handle;
            public ImageSource? Icon;
            public bool IsFavorite;
        }

        private readonly List<Entry> _files = new();
        private readonly List<Entry> _apps = new();
        private readonly List<Entry> _webs = new();
        private Kind _tab = Kind.File;

        private readonly TextBlock _hint = new() { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)), Margin = new Thickness(0, 2, 0, 0) };
        private readonly Button _btnFiles = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "文件" };
        private readonly Button _btnApps = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "应用", Margin = new Thickness(6, 0, 0, 0) };
        private readonly Button _btnWebs = new() { Height = 26, Padding = new Thickness(8, 0, 8, 0), Content = "网页", Margin = new Thickness(6, 0, 0, 0) };
        private readonly Border _webRow = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        private readonly StackPanel _list = new();

        private static readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        public RecentItemsPanel()
        {
            _btnFiles.Style = TryFindResource("FlatButton") as Style;
            _btnApps.Style = TryFindResource("FlatButton") as Style;
            _btnWebs.Style = TryFindResource("FlatButton") as Style;
            _btnFiles.Click += (_, _) => ShowTab(Kind.File);
            _btnApps.Click += (_, _) => ShowTab(Kind.App);
            _btnWebs.Click += (_, _) => ShowTab(Kind.Web);

            var refresh = new Button { Content = "⟳", FontSize = 12, Width = 30, Height = 26, ToolTip = "刷新", Style = TryFindResource("IconButton") as Style };
            refresh.Click += (_, _) => RefreshAll();

            var title = new TextBlock { Text = "最近使用", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)) };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headLeft = new StackPanel();
            headLeft.Children.Add(title);
            headLeft.Children.Add(_hint);
            head.Children.Add(headLeft);
            Grid.SetColumn(refresh, 1);
            head.Children.Add(refresh);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            tabs.Children.Add(_btnFiles);
            tabs.Children.Add(_btnApps);
            tabs.Children.Add(_btnWebs);

            var input = new TextBox { FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "输入网址，回车收藏" };
            var addBtn = new Button { Content = "收藏", FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("FlatButton") as Style };
            addBtn.Click += (_, _) => AddWeb(input.Text);
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddWeb(input.Text); e.Handled = true; } };
            var webGrid = new Grid();
            webGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            webGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            webGrid.Children.Add(input);
            Grid.SetColumn(addBtn, 1);
            webGrid.Children.Add(addBtn);
            _webRow.Child = webGrid;

            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(head);
            root.Children.Add(tabs);
            root.Children.Add(_webRow);
            root.Children.Add(scroll);
            Content = root;

            Loaded += (_, _) => RefreshAll();
        }

        private void AddWeb(string input)
        {
            string url = (input ?? "").Trim();
            if (url.Length == 0) return;
            if (WebFavoriteManager.AddFavorite(url)) RefreshAll();
        }

        public void RefreshAll()
        {
            LoadFiles();
            LoadApps();
            LoadWebs();
            ShowTab(_tab);
        }

        private void LoadFiles()
        {
            _files.Clear();
            try
            {
                string recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return;
                foreach (var entry in new DirectoryInfo(recentDir).GetFiles("*.lnk").OrderByDescending(x => x.LastWriteTime).Take(30))
                {
                    try
                    {
                        string target = ShortcutLinkResolver.Resolve(entry.FullName);
                        if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                        _files.Add(new Entry { Kind = Kind.File, Name = Path.GetFileName(target), Detail = target, Path = target, Icon = GetFileIcon(target) });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void LoadApps()
        {
            _apps.Clear();
            try
            {
                var recent = RecentAppTracker.GetRecentApps(30);
                if (recent.Count == 0) return;
                var exeToHandle = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
                WindowListProvider.EnumerateWindowExeHandles(exeToHandle);
                foreach (var app in recent)
                {
                    _apps.Add(new Entry
                    {
                        Kind = Kind.App,
                        Name = app.Name,
                        Detail = app.Path,
                        Path = app.Path,
                        Handle = exeToHandle.TryGetValue(app.Path, out var h) ? h : (IntPtr?)null,
                        Icon = GetFileIcon(app.Path)
                    });
                }
            }
            catch { }
        }

        private static ImageSource? GetFileIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_iconCache.TryGetValue(path, out var cached)) return cached;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    new Int32Rect(0, 0, icon.Width, icon.Height),
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                _iconCache[path] = source;
                return source;
            }
            catch { return null; }
        }

        private async void LoadWebs()
        {
            _webs.Clear();
            try
            {
                var entries = await Task.Run(() => WebFavoriteManager.GetCombined(40));
                foreach (var entry in entries)
                {
                    _webs.Add(new Entry
                    {
                        Kind = Kind.Web,
                        Name = string.IsNullOrEmpty(entry.Title) ? WebFavoriteManager.GetDomain(entry.Url) : entry.Title,
                        Detail = entry.Url,
                        Path = entry.Url,
                        IsFavorite = entry.IsFavorite
                    });
                }
                if (_tab == Kind.Web) ShowTab(_tab);
            }
            catch { }
        }

        private void ShowTab(Kind tab)
        {
            _tab = tab;
            var source = tab switch { Kind.File => _files, Kind.App => _apps, _ => _webs };
            _hint.Text = "共 " + source.Count + " 条";
            _webRow.Visibility = tab == Kind.Web ? Visibility.Visible : Visibility.Collapsed;

            _list.Children.Clear();
            foreach (var item in source)
            {
                _list.Children.Add(BuildRow(item));
            }
        }

        private Border BuildRow(Entry item)
        {
            var name = new TextBlock { Text = item.Name, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = item.Detail, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(119, 119, 119)), TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(detail);

            var iconCol = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (item.Icon != null)
            {
                iconCol.Children.Add(new Image { Source = item.Icon, Width = 18, Height = 18 });
            }
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(iconCol);
            left.Children.Add(text);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);

            if (item.IsFavorite)
            {
                var remove = new Button
                {
                    Content = "✕",
                    Width = 18, Height = 18,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                remove.Click += (_, _) => { if (item.Kind == Kind.Web) { WebFavoriteManager.RemoveFavorite(item.Path); RefreshAll(); } };
                Grid.SetColumn(remove, 1);
                grid.Children.Add(remove);
            }

            var row = new Border { Child = grid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 2, 2, 2), Margin = new Thickness(0, 0, 0, 4) };
            row.MouseLeftButtonUp += (_, _) => OpenItem(item);
            return row;
        }

        private static void OpenItem(Entry item)
        {
            try
            {
                switch (item.Kind)
                {
                    case Kind.App when item.Handle.HasValue:
                        WindowAction.SwitchTo(item.Handle.Value);
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    case Kind.App:
                    case Kind.File:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        RecentAppTracker.RecordLaunch(item.Path);
                        break;
                    default:
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                        WebFavoriteManager.RecordOpen(item.Path, item.Name);
                        break;
                }
            }
            catch { }
        }

        public string Name => "最近使用";
        public UserControl CreateView() => this;
        public void OnActivated() { RefreshAll(); }
        public void OnDeactivated() { }
    }
}
""";

        /// <summary>任务栏（薄封装版：复用内置任务栏视图，可在外层叠加自定义装饰/内容）。</summary>
        private const string TaskbarPanelSource = """
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 任务栏（薄封装）：直接复用 ShoreHue 内置任务栏（快捷方式/运行中窗口/拖拽排序）。
    // 想自定义：在 root 里叠加你自己的头部/装饰/按钮即可；想完全重写，把 inner 换成你的代码。
    public class TaskbarPanel : UserControl, IWidget
    {
        public TaskbarPanel()
        {
            // 从服务容器取内置服务（运行在 ShoreHue 内时已注册；独立验证环境无服务时显示占位而非崩溃）
            var shortcuts = ServiceManager.Instance.GetService<ShortcutManager>() as IShortcutService;
            var settings = ServiceManager.Instance.GetService<SettingsManager>() as ISettingsService;
            if (shortcuts == null || settings == null)
            {
                Content = new TextBlock
                {
                    Text = "任务栏需在 ShoreHue 应用内运行（服务未就绪）",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
            var inner = new TaskbarView(shortcuts, settings);
            // ★ 不加自定义标题行：它挤占标签空间，属多余装饰
            Content = inner;
        }

        public string Name => "任务栏";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}
""";

        /// <summary>AI 助手（薄封装版：复用内置 AI 聊天视图）。</summary>
        private const string AiPanelSource = """
using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.AI;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // AI 助手（薄封装）：复用 ShoreHue 内置 AI 聊天面板（流式对话/文件/输出到光标）。
    // ★ 面板实例会被编译缓存复用，所以设置刷新放 OnActivated（每次显示都刷），不是构造函数。
    public class AiPanel : UserControl, IWidget
    {
        private readonly AiChatView _view = new();

        public AiPanel() { Content = _view; }

        public string Name => "AI 助手";
        public UserControl CreateView() => this;
        public void OnActivated() { _view.RefreshSettings(); }
        public void OnDeactivated() { }
    }
}
""";

        /// <summary>应用辅助（薄封装版：复用宿主应用辅助视图）。</summary>
        private const string AppHelperPanelSource = """
using System.Windows.Controls;
using ShoreHue.UI.AppHelper;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 应用辅助（薄封装）：媒体控制 / 窗口镜像（画中画）/ 本地视频播放。
    public class AppHelperPanel : UserControl, IWidget
    {
        private readonly AppHelperView _view = new();

        public AppHelperPanel() { Content = _view; }

        public string Name => "应用辅助";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}
""";

        /// <summary>窗口控制（薄封装版：复用内置窗口操作中心）。</summary>
        private const string WindowControlPanelSource = """
using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 窗口控制（薄封装）：复用 ShoreHue 内置窗口操作中心（最小化/最大化/关闭/置顶）。
    public class WindowControlPanel : UserControl, IWidget
    {
        public WindowControlPanel()
        {
            Content = new WindowControlView();
        }

        public string Name => "窗口控制";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}
""";

    }
}