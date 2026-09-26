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
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.Widgets.TextAi
{
    /// <summary>
    /// 划词翻译 小组件：在任何应用中选中文字，按全局快捷键（引导页 / 设置中配置），
    /// 自动读取选中文本并调用 AI 翻译，结果流式显示在本面板内。
    /// 纯文本请求，所有 OpenAI 兼容模型都支持。
    /// </summary>
    public partial class TextAiWidget : UserControl, IWidget, IWidgetFooter, ITextAiWidget
    {
        private readonly AiChatClient _client = new();
        private CancellationTokenSource? _cts;

        public TextAiWidget()
        {
            InitializeComponent();
        }

        public new string Name => LocalizationManager.Instance["WidgetTabs_TextAi"];

        public UserControl CreateView() => this;

        public void OnActivated()
        {
        }

        public void OnDeactivated()
        {
            // 保持流式输出继续：面板切走时翻译继续，切回可见结果
        }

        public FrameworkElement GetFooterControl()
        {
            return new StackPanel();
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            // ★ 走宿主能力而不是静态事件：文件夹版的类型与 exe 版不同，静态事件收不到。
            HostCapabilities.OpenSettingsPage("tabAI");
        }

        /// <summary>一键复制译文（STA/UI 线程直接 SetText）。</summary>
        private void CopyResult_Click(object sender, RoutedEventArgs e)
        {
            string text = ResultText.Text.Trim();
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
                BtnCopyResult.Content = LocalizationManager.Instance["TextAi_Copied"];
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    BtnCopyResult.Content = LocalizationManager.Instance["TextAi_Copy"];
                };
                timer.Start();
            }
            catch { }
        }

        // ============ 划词翻译 ============

        /// <summary>由全局热键触发：捕获前台窗口选中文本并翻译。</summary>
        public async Task CaptureAndTranslateAsync()
        {
            // 1. AI 是否已配置
            var ai = AiSettingsStore.Load();
            if (!ai.Enabled || string.IsNullOrWhiteSpace(ai.ApiKey))
            {
                ShowState(LocalizationManager.Instance["TextAi_NotConfigured"], true);
                return;
            }

            // 2. 捕获选中文本（必须在 STA/UI 线程，内部已处理剪贴板恢复）
            ShowState(LocalizationManager.Instance["TextAi_Reading"], false);
            // ★ 走窄接口 HostCapabilities：宿主划词捕获类在符号层黑名单里，外来包直接引用它会被判违规、
            //   整包被拦。代价：这个入口只返回文本、拿不到失败原因，失败一律按「未选中」提示。
            //   注意：连注释里都不能写那个类型名 —— 沙箱文本层会连注释一起扫。
            string selected = await ShoreHue.UI.Widgets.HostCapabilities.CaptureSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(selected))
            {
                ShowState(LocalizationManager.Instance["TextAi_NoSelection"], true);
                return;
            }

            // 3. 翻译
            await TranslateAsync(selected, ai);
        }


        // ==================== 最近翻译历史（借鉴沉浸式翻译/翻译插件：翻过的东西能找回来） ====================

        private List<string> _history = new();
        private Button? _btnHistory;

        /// <summary>把「历史」按钮加在"复制结果"旁边（代码构造，不动 XAML：受限方言里塞事件风险高）。</summary>
        private void EnsureHistoryButton()
        {
            if (_btnHistory != null || BtnCopyResult?.Parent is not Panel panel) return;
            _btnHistory = new Button
            {
                Content = LocalizationManager.Instance["TextAi_History"],
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = LocalizationManager.Instance["TextAi_HistoryTip"]
            };
            _btnHistory.Click += (_, _) => ShowHistory();
            panel.Children.Add(_btnHistory);
            LoadHistory();
        }

        private int HistoryLimit
            => ShoreHue.UI.Widgets.HostCapabilities.Settings?.TextAiHistoryLimit ?? 20;

        private void LoadHistory()
        {
            try
            {
                string json = ShoreHue.UI.Widgets.HostCapabilities.Settings?.TextAiHistoryJson ?? "";
                _history = string.IsNullOrWhiteSpace(json)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (Exception ex)
            {
                _history = new List<string>();
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[划词翻译] 历史读取失败（按空处理）：{ex.Message}");
            }
        }

        private void SaveHistory()
        {
            try
            {
                var s = ShoreHue.UI.Widgets.HostCapabilities.Settings;
                if (s == null) return;
                int limit = HistoryLimit;
                if (limit <= 0) { s.TextAiHistoryJson = ""; return; }
                while (_history.Count > limit) _history.RemoveAt(_history.Count - 1);
                s.TextAiHistoryJson = System.Text.Json.JsonSerializer.Serialize(_history);
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[划词翻译] 历史保存失败：{ex.Message}");
            }
        }

        /// <summary>记一条翻译（翻译成功时调用）。条数上限 0 = 不记录（设置里可关）。</summary>
        public void RecordTranslation(string source, string result)
        {
            if (HistoryLimit <= 0)
            {
                // ★ 关掉就**清空**：否则用户以为"不记录了"，磁盘上还留着历史（隐私预期不符）
                if (_history.Count > 0) { _history.Clear(); SaveHistory(); }
                return;
            }
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(result)) return;
            string one = source.Replace("\r", " ").Replace("\n", " ").Trim();
            if (one.Length > 40) one = one.Substring(0, 40) + "…";
            string line = one + "  →  " + result.Trim();
            _history.RemoveAll(x => x == line);
            _history.Insert(0, line);
            SaveHistory();
        }

        /// <summary>历史菜单（代码构造）：点一条即把译文放回结果区并复制。</summary>
        public void ShowHistory()
        {
            var menu = new ContextMenu();
            if (_history.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = LocalizationManager.Instance["TextAi_HistoryEmpty"], IsEnabled = false });
            }
            else
            {
                foreach (var line in _history)
                {
                    var item = new MenuItem { Header = string.IsNullOrEmpty(line) ? " " : line };
                    item.Click += (_, _) =>
                    {
                        int idx = line.IndexOf("→", StringComparison.Ordinal);
                        string result = idx >= 0 ? line.Substring(idx + 1).Trim() : line;
                        ResultText.Text = result;
                        CopyText(result);
                    };
                    menu.Items.Add(item);
                }
            }
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void CopyText(string text)
        {
            // 同 calculator：不引用「宿主剪贴板管理器」里的条目类型（符号层黑名单）
            if (!ShoreHue.UI.Widgets.HostCapabilities.CopyToClipboard(text))
                System.Windows.Clipboard.SetText(text);
        }
        private async Task TranslateAsync(string text, AiSettings ai)
        {
            EnsureHistoryButton();
            Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            SourceText.Text = text.Length > 1000 ? text[..1000] + "…" : text;
            ResultText.Text = "";
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238));
            ShowState(LocalizationManager.Instance["TextAi_Translating"], false);
            try
            {
                // 判断语言方向：含较多 CJK 字符 → 译为英文；否则译为中文
                bool chinese = CountCjk(text) >= Math.Max(3, text.Length / 6);
                // ★ 目标语言可在「设置 → 面板 → 划词翻译」里指定；留空才用"自动判中英"
                string target = ShoreHue.UI.Widgets.HostCapabilities.Settings?.TextAiTargetLanguage?.Trim() ?? "";
                string targetClause = target.Length > 0
                    ? "请将以下内容翻译成" + target + "。"
                    : (chinese ? "请将以下内容翻译成英文。" : "请将以下内容翻译成中文。");
                string prompt = targetClause + "只输出译文，不要任何解释、引号或多余文字：\n\n" + text;

                // 翻译用独立 SystemPrompt，避免默认助手提示词污染译文
                var translateSettings = new AiSettings
                {
                    Enabled = ai.Enabled,
                    BaseUrl = ai.BaseUrl,
                    ApiKey = ai.ApiKey,
                    Model = ai.Model,
                    Temperature = Math.Min(ai.Temperature, 0.5),
                    ContextWindowTokens = ai.ContextWindowTokens,
                    SystemPrompt = "你是翻译引擎，只输出译文。"
                };

                var history = new List<ChatMessage>();
                string full = await _client.StreamChatAsync(translateSettings, history, prompt, delta =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            ResultText.Text += delta;
                        }
                    });
                }, ct);

                if (!ct.IsCancellationRequested)
                {
                    ShowState(full.Length > 0 ? "" : LocalizationManager.Instance["TextAi_EmptyResult"],
                        full.Length == 0);
                    if (full.Length > 0) RecordTranslation(text, full);   // 最近翻译（可在设置里关：条数 0）
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowState(LocalizationManager.Instance["TextAi_Failed"] + ex.Message, true);
            }
        }

        /// <summary>粗略统计 CJK（中日韩）字符数，用于判断翻译方向。</summary>
        private static int CountCjk(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) count++;   // CJK 统一表意文字
                else if (c >= 0x3040 && c <= 0x30FF) count++; // 日文假名
                else if (c >= 0xAC00 && c <= 0xD7AF) count++; // 韩文
            }
            return count;
        }

        private void ShowState(string? text, bool isError)
        {
            StateText.Text = text ?? "";
            StateText.Foreground = new SolidColorBrush(isError
                ? Color.FromRgb(255, 130, 120)
                : Color.FromRgb(138, 138, 138));
        }

        private void Cancel()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }
            _cts = null;
        }
    }
}
