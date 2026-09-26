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
        public static event Action? OpenSettingsRequested;

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
            btnSettings.Click += (_, _) => OpenSettingsRequested?.Invoke();
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
            // ★ 安全 v2：外来来源通过宿主窄接口读取选中文本（不再直接访问宿主内部能力与 AI 密钥）
            ShowState("读取选中文本…", false);
            string captured = await ShoreHue.UI.Widgets.HostCapabilities.CaptureSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(captured)) { ShowState("未选中文字（或未配置 AI）", true); return; }
            await TranslateAsync(captured);
        }

        private IntPtr GetOwnHwnd()
        {
            try { return new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle; }
            catch { return IntPtr.Zero; }
        }

        private async Task TranslateAsync(string text)
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
                // ★ 安全 v2：由宿主发起 AI 请求（密钥不出宿主），插件只拿流式增量
                string full = await ShoreHue.UI.Widgets.HostCapabilities.AskAiAsync(prompt, delta =>
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