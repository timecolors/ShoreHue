using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Windows.Input;
using ShoreHue.UI.Localization;
using ShoreHue.UI.Widgets;
using ShoreHue.Core.Infrastructure.Logging;   // LogManager（进制解析失败留痕）

namespace ShoreHue.UI.Widgets.Calculator
{
    public partial class CalculatorWidget : UserControl, IWidget, IWidgetFooter
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

        public CalculatorWidget()
        {
            
            LoadHistory();   // 历史记录借设置落盘（小组件不能碰文件系统）
InitializeComponent();
            Focusable = true;
            PreviewKeyDown += CalculatorWidget_PreviewKeyDown;
        }

        public new string Name => LocalizationManager.Instance["WidgetTabs_Calculator"];

        public UserControl CreateView() => this;

        public void OnActivated() { }

        public void OnDeactivated() { }

        public FrameworkElement GetFooterControl()
        {
            var status = new TextBlock
            {
                Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Calculator_FooterHint"],
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102)),
                VerticalAlignment = VerticalAlignment.Center
            };
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { status } };
        }

        // ================= 输入 =================

        // ==================== 借鉴 Windows 计算器：键盘输入 / 复制结果 / 历史记录 ====================

        /// <summary>
        /// 键盘输入（面板聚焦时）。
        /// ★ 直接调 InputDigit/InputOperator——它们本来就吃字符串，不用伪造按钮。
        /// 键位都避开 Windows 全局快捷键；Ctrl+H（历史）与 Ctrl+C（复制）是应用内约定，不注册系统热键。
        /// </summary>
        private void Root_KeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.C: CopyResult(); e.Handled = true; return;
                    case Key.H: ShowHistoryMenu(); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.D0: case Key.NumPad0: InputDigit("0"); break;
                case Key.D1: case Key.NumPad1: InputDigit("1"); break;
                case Key.D2: case Key.NumPad2: InputDigit("2"); break;
                case Key.D3: case Key.NumPad3: InputDigit("3"); break;
                case Key.D4: case Key.NumPad4: InputDigit("4"); break;
                case Key.D5: case Key.NumPad5: InputDigit("5"); break;
                case Key.D6: case Key.NumPad6: InputDigit("6"); break;
                case Key.D7: case Key.NumPad7: InputDigit("7"); break;
                case Key.D8: case Key.NumPad8: InputDigit("8"); break;
                case Key.D9: case Key.NumPad9: InputDigit("9"); break;
                case Key.OemPeriod: case Key.Decimal: InputDot(); break;
                case Key.Add: case Key.OemPlus: InputOperator("+"); break;
                case Key.Subtract: case Key.OemMinus: InputOperator("-"); break;
                case Key.Multiply: InputOperator("*"); break;
                case Key.Divide: case Key.OemQuestion: InputOperator("/"); break;
                case Key.Enter: Equals_Click(this, new RoutedEventArgs()); break;   // "=" 与 "+" 是同一个物理键，只能给 +（Windows 计算器把 = 留给 Enter）

                case Key.Back: Backspace_Click(this, new RoutedEventArgs()); break;
                case Key.Escape: Clear_Click(this, new RoutedEventArgs()); break;
                case Key.Delete: Clear_Click(this, new RoutedEventArgs()); break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>复制当前显示值到剪贴板（走宿主的剪贴板服务，顺带进历史；没有服务就退回系统剪贴板）。</summary>
        private void CopyResult()
        {
            string text = DisplayText?.Text ?? "";
            if (string.IsNullOrEmpty(text)) return;
            // ★ 必须走 HostCapabilities.CopyToClipboard(string)：宿主剪贴板服务的写入方法，其参数类型是
            //   「宿主剪贴板管理器」里的条目类型，而那个类型在符号层黑名单里 —— 插件只要**写出它的类型名**
            //   就会被判违规、整包被拦（本文件同步到市场包后 MarketValidator 实测 FAIL）。
            //   注意：连注释里都不能写那个类型名 —— 沙箱的文本层会连注释一起扫（本项目实测踩过）。
            if (!ShoreHue.UI.Widgets.HostCapabilities.CopyToClipboard(text))
                System.Windows.Clipboard.SetText(text);
        }

        // ---- 历史记录（借设置落盘：小组件沙箱不允许碰文件系统）----

        private List<string> _history = new();

        private int HistoryLimit
            => ShoreHue.UI.Widgets.HostCapabilities.Settings?.CalculatorHistoryLimit ?? 20;

        private void LoadHistory()
        {
            try
            {
                string json = ShoreHue.UI.Widgets.HostCapabilities.Settings?.CalculatorHistoryJson ?? "";
                _history = string.IsNullOrWhiteSpace(json)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (Exception ex)
            {
                _history = new List<string>();
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[计算器] 历史记录读取失败（按空处理）：{ex.Message}");
            }
        }

        private void SaveHistory()
        {
            try
            {
                var s = ShoreHue.UI.Widgets.HostCapabilities.Settings;
                if (s == null) return;
                int limit = HistoryLimit;
                if (limit <= 0) { s.CalculatorHistoryJson = ""; return; }
                while (_history.Count > limit) _history.RemoveAt(_history.Count - 1);
                s.CalculatorHistoryJson = System.Text.Json.JsonSerializer.Serialize(_history);
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[计算器] 历史记录保存失败：{ex.Message}");
            }
        }

        /// <summary>记一条历史（等号算完时调用）。上限 0 = 不记录（设置里可关，符合"高度自定义"）。</summary>
        private void RecordHistory(string expr, string result)
        {
            if (HistoryLimit <= 0)
            {
                // ★ 同上：关掉就清空（已设置 0 的用户下次计算时，旧历史被抹掉）
                if (_history.Count > 0) { _history.Clear(); SaveHistory(); }
                return;
            }
            if (string.IsNullOrWhiteSpace(result)) return;
            if (result == LocalizationManager.Instance["Calc_Error"]) return;
            string line = string.IsNullOrWhiteSpace(expr) ? result : expr.Trim() + "  =  " + result;
            _history.Insert(0, line);
            SaveHistory();
        }

        /// <summary>Ctrl+H：把历史列成菜单，点一条回填显示区（Windows 计算器同款用法）。</summary>
        private void ShowHistoryMenu()
        {
            var menu = new ContextMenu();
            if (_history.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = LocalizationManager.Instance["Calc_HistoryEmpty"], IsEnabled = false });
            }
            else
            {
                foreach (var line in _history)
                {
                    var item = new MenuItem { Header = line };
                    item.Click += (_, _) =>
                    {
                        int idx = line.LastIndexOf("=  ", StringComparison.Ordinal);
                        string value = idx >= 0 ? line.Substring(idx + 3).Trim() : line;
                        DisplayText.Text = value;
                        ExprText.Text = "";
                        _enteringNewNumber = true;
                        _error = false;
                    };
                    menu.Items.Add(item);
                }
            }
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        private void Digit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string digit) InputDigit(digit);
        }

        private void Dot_Click(object sender, RoutedEventArgs e) => InputDot();

        private void Op_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string op) InputOperator(op);
        }

        private void Equals_Click(object sender, RoutedEventArgs e)
        {
            Calculate();
            RecordHistory(ExprText?.Text ?? "", DisplayText?.Text ?? "");
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _left = 0; _right = 0; _pendingOp = "";
            _enteringNewNumber = true; _error = false;
            ExprText.Text = "";
            DisplayText.Text = "0";
        }

        private void Negate_Click(object sender, RoutedEventArgs e)
        {
            if (_error) return;
            if (TryReadDisplay(out double v))
            {
                DisplayText.Text = Format(-v);
            }
        }

        private void Percent_Click(object sender, RoutedEventArgs e)
        {
            if (_error) return;
            if (TryReadDisplay(out double v))
            {
                DisplayText.Text = Format(v / 100);
            }
        }

        // ================= 模式切换 =================

        private void ModeStd_Click(object sender, RoutedEventArgs e) => SetMode(CalcMode.Standard);
        private void ModeSci_Click(object sender, RoutedEventArgs e) => SetMode(CalcMode.Scientific);
        private void ModeProg_Click(object sender, RoutedEventArgs e) => SetMode(CalcMode.Programmer);

        private void SetMode(CalcMode mode)
        {
            _mode = mode;
            BtnStd.Style = (Style)FindResource(mode == CalcMode.Standard ? "AccentButton" : "FlatButton");
            BtnSci.Style = (Style)FindResource(mode == CalcMode.Scientific ? "AccentButton" : "FlatButton");
            BtnProg.Style = (Style)FindResource(mode == CalcMode.Programmer ? "AccentButton" : "FlatButton");
            SciPanel.Visibility = mode == CalcMode.Scientific ? Visibility.Visible : Visibility.Collapsed;
            ProgPanel.Visibility = mode == CalcMode.Programmer ? Visibility.Visible : Visibility.Collapsed;
            RadixText.Text = mode == CalcMode.Programmer ? RadixName(_radix) : "";
            UpdateDisplayRadix();
        }

        private void SciFn_Click(object sender, RoutedEventArgs e)
        {
            if (_error || sender is not Button btn || btn.Tag is not string fn) return;
            if (!TryReadDisplay(out double v))
            {
                v = 0;
            }

            double ToRad(double d) => _useDegrees ? d * Math.PI / 180.0 : d;
            double FromRad(double r) => _useDegrees ? r * 180.0 / Math.PI : r;

            double result = fn switch
            {
                "sin" => Math.Sin(ToRad(v)),
                "cos" => Math.Cos(ToRad(v)),
                "tan" => Math.Tan(ToRad(v)),
                "asin" => FromRad(Math.Asin(v)),
                "acos" => FromRad(Math.Acos(v)),
                "atan" => FromRad(Math.Atan(v)),
                "sinh" => Math.Sinh(v),
                "cosh" => Math.Cosh(v),
                "tanh" => Math.Tanh(v),
                "sqrt" => v < 0 ? double.NaN : Math.Sqrt(v),
                "sqr" => v * v,
                "inv" => Math.Abs(v) < 1e-12 ? double.NaN : 1.0 / v,
                "pi" => Math.PI,
                "e" => Math.E,
                "exp" => Math.Exp(v),
                "pow10" => Math.Pow(10, v),
                "fact" => Factorial((long)Math.Round(v)),
                "ln" => v <= 0 ? double.NaN : Math.Log(v),
                "log" => v <= 0 ? double.NaN : Math.Log10(v),
                _ => v
            };

            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                _error = true;
                DisplayText.Text = LocalizationManager.Instance["Calc_Error"];
                ExprText.Text = "";
                return;
            }

            DisplayText.Text = Format(result);
            _enteringNewNumber = true;
        }

        private static double Factorial(long n)
        {
            if (n < 0 || n > 170) return double.NaN;
            double r = 1;
            for (long i = 2; i <= n; i++) r *= i;
            return r;
        }

        private void DegRad_Click(object sender, RoutedEventArgs e)
        {
            _useDegrees = !_useDegrees;
            BtnDeg.Content = _useDegrees ? "DEG" : "RAD";
        }

        // ================= 程序员模式 =================

        private void Radix_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string s && int.TryParse(s, out int radix))
            {
                _radix = radix;
                RadixText.Text = RadixName(radix);
                UpdateDisplayRadix();
            }
        }

        private void BitOp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string op) InputOperator(op);
        }

        private void BitNot_Click(object sender, RoutedEventArgs e)
        {
            if (_error) return;
            if (TryReadDisplay(out double v))
            {
                DisplayText.Text = Format(~(long)Math.Round(v));
                _enteringNewNumber = true;
            }
        }

        private void BitShift_Click(object sender, RoutedEventArgs e)
        {
            if (_error || sender is not Button btn || btn.Tag is not string dir) return;
            if (!TryReadDisplay(out double v)) return;
            long lv = (long)Math.Round(v);
            // ★ 位移要用真正的移位：旧实现是 `lv * 2` / `lv / 2` ——
            //   乘以 2 对负数/溢出行为与 `<<` 不同，而 `/2` 是**截断除法**（-3/2 = -1，而 -3>>1 = -2），
            //   在程序员模式里这就是错的。
            DisplayText.Text = Format(dir == "l" ? lv << 1 : lv >> 1);
            _enteringNewNumber = true;
        }

        /// <summary>
        /// 读取"当前显示的数字"。
        /// ★ 程序员模式下显示的是 `0x1F` / `0o17` / `0b101`（见 <see cref="FormatRadix"/>），
        ///   而各处原来一律用 `double.TryParse(..., InvariantCulture)` 去读 —— 带前缀的文本**必然解析失败**，
        ///   于是按下 ±、%、NOT、位移、= 这些键时要么静默不生效，要么拿 `_right` 里的旧值算错
        ///   （例如 0x1F + 2 被算成 0 + 2）。这里统一按前缀识别进制。
        /// </summary>
        private bool TryReadDisplay(out double value)
        {
            value = 0;
            string text = DisplayText.Text ?? "";
            if (text.Length == 0) return false;

            // 程序员的 16/8/2 进制显示（FormatRadix 产出的小写前缀）
            if (text.Length > 2 && text[0] == '0')
            {
                char p = char.ToLowerInvariant(text[1]);
                int radix = p == 'x' ? 16 : p == 'o' ? 8 : p == 'b' ? 2 : 0;
                if (radix != 0)
                {
                    try
                    {
                        // 用 ulong 承接再转 double：程序员模式下的位运算结果可能超出 long 的正区间，
                        // Convert.ToInt64 对超大无符号值会抛异常。
                        ulong u = Convert.ToUInt64(text.Substring(2), radix);
                        value = u;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // 非法数字（用户可能手输/退格造成 0x 后面为空）→ 按"读不出"处理
                        LogManager.Debug($"[计算器] 解析进制数值失败（按无值处理）：{ex.Message}");
                        return false;
                    }
                }
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string RadixName(int radix) => radix switch
        {
            16 => "HEX",
            8 => "OCT",
            2 => "BIN",
            _ => "DEC"
        };

        private void UpdateDisplayRadix()
        {
            if (_mode == CalcMode.Programmer &&
                TryReadDisplay(out double v))
            {
                DisplayText.Text = Format(v);
            }
        }

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            if (_error || _enteringNewNumber) return;
            if (DisplayText.Text.Length > 1)
            {
                DisplayText.Text = DisplayText.Text[..^1];
            }
            else
            {
                DisplayText.Text = "0";
                _enteringNewNumber = true;
            }
        }

        private void InputDigit(string digit)
        {
            if (_error) return;
            if (_enteringNewNumber)
            {
                DisplayText.Text = digit;
                _enteringNewNumber = false;
            }
            else
            {
                if (DisplayText.Text == "0") DisplayText.Text = digit;
                else if (DisplayText.Text.Length < 16) DisplayText.Text += digit;
            }
        }

        private void InputDot()
        {
            if (_error) return;
            if (_enteringNewNumber)
            {
                DisplayText.Text = "0.";
                _enteringNewNumber = false;
            }
            else if (!DisplayText.Text.Contains('.'))
            {
                DisplayText.Text += ".";
            }
        }

        private void InputOperator(string op)
        {
            if (_error) return;

            if (!_enteringNewNumber && !string.IsNullOrEmpty(_pendingOp))
            {
                Calculate();
            }

            if (TryReadDisplay(out double v))
            {
                _left = v;
            }
            _pendingOp = op;
            _enteringNewNumber = true;
            string displayOp = op switch { "pow" => "^", _ => op };
            ExprText.Text = $"{Format(_left)} {displayOp}";
        }

        private void Calculate()
        {
            if (string.IsNullOrEmpty(_pendingOp) || _error) return;
            if (!TryReadDisplay(out double right))
            {
                right = _right;
            }

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

            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                _error = true;
                DisplayText.Text = LocalizationManager.Instance["Calc_Error"];
                ExprText.Text = "";
                _pendingOp = "";
                return;
            }

            string displayOp = _pendingOp switch { "pow" => "^", _ => _pendingOp };
            ExprText.Text = $"{Format(_left)} {displayOp} {Format(right)} =";
            _left = result;
            DisplayText.Text = Format(result);
            _pendingOp = "";
            _enteringNewNumber = true;
        }

        private string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return LocalizationManager.Instance["Calc_Error"];
            bool isInteger = Math.Abs(v - Math.Round(v)) < 1e-12 && Math.Abs(v) < 9.2e18;
            return _mode == CalcMode.Programmer && isInteger
                ? FormatRadix((long)Math.Round(v))
                : isInteger && Math.Abs(v) < 1e15
                    ? v.ToString("0", CultureInfo.InvariantCulture)
                    : v.ToString("G12", CultureInfo.InvariantCulture);
        }

        private string FormatRadix(long v)
        {
            return _radix switch
            {
                16 => "0x" + Convert.ToString(v, 16).ToUpperInvariant(),
                8 => "0o" + Convert.ToString(v, 8),
                2 => "0b" + Convert.ToString(v, 2),
                _ => v.ToString(CultureInfo.InvariantCulture)
            };
        }

        // ================= 键盘输入 =================

        private void CalculatorWidget_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.D0:
                case Key.NumPad0:
                    InputDigit("0"); e.Handled = true; break;
                case Key.D1:
                case Key.NumPad1:
                    InputDigit("1"); e.Handled = true; break;
                case Key.D2:
                case Key.NumPad2:
                    InputDigit("2"); e.Handled = true; break;
                case Key.D3:
                case Key.NumPad3:
                    InputDigit("3"); e.Handled = true; break;
                case Key.D4:
                case Key.NumPad4:
                    InputDigit("4"); e.Handled = true; break;
                case Key.D5:
                    if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        Percent_Click(sender, e);
                        e.Handled = true;
                        break;
                    }
                    InputDigit("5"); e.Handled = true; break;
                case Key.NumPad5:
                    InputDigit("5"); e.Handled = true; break;
                case Key.D6:
                case Key.NumPad6:
                    InputDigit("6"); e.Handled = true; break;
                case Key.D7:
                case Key.NumPad7:
                    InputDigit("7"); e.Handled = true; break;
                case Key.D8:
                case Key.NumPad8:
                    InputDigit("8"); e.Handled = true; break;
                case Key.D9:
                case Key.NumPad9:
                    InputDigit("9"); e.Handled = true; break;
                case Key.Decimal:
                case Key.OemPeriod:
                    InputDot(); e.Handled = true; break;
                case Key.Add:
                    InputOperator("+"); e.Handled = true; break;
                case Key.OemPlus:
                    // 主键盘 +/-= 键：无 Shift 是 "="，Shift 是 "+"
                    if (e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        InputOperator("+");
                    }
                    else
                    {
                        Calculate();
                    }
                    e.Handled = true; break;
                case Key.Subtract:
                case Key.OemMinus:
                    InputOperator("-"); e.Handled = true; break;
                case Key.Multiply:
                    InputOperator("*"); e.Handled = true; break;
                case Key.Divide:
                case Key.OemQuestion:
                    InputOperator("/"); e.Handled = true; break;
                case Key.Enter:
                    Calculate(); e.Handled = true; break;
                case Key.Back:
                    Backspace_Click(sender, e); e.Handled = true; break;
                case Key.Escape:
                    Clear_Click(sender, e); e.Handled = true; break;
            }
        }
    }
}
