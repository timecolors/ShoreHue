using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ShoreHue.Core.Services.Ai;
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.AI
{
    /// <summary>AI 聊天面板：多会话 + 流式对话 + Markdown + 快捷指令。</summary>
    public partial class AiChatView : UserControl
    {
        public sealed class ChatItem : INotifyPropertyChanged
        {
            private FlowDocument? _doc;
            private string? _imagePath;
            public bool IsUser { get; set; }
            public bool IsAssistant { get; set; }
            public bool IsError { get; set; }
            public string PlainText { get; set; } = "";

            /// <summary>关联的会话消息（用于重新生成 / 编辑定位）。</summary>
            public ChatMessage? Message { get; set; }

            /// <summary>本地图片路径（仅用户图片消息，用于缩略图显示）。</summary>
            public string? ImagePath
            {
                get => _imagePath;
                set { _imagePath = value; OnPropertyChanged(); }
            }

            public bool HasImage => !string.IsNullOrEmpty(ImagePath);

            public FlowDocument? Document
            {
                get => _doc;
                set { _doc = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        /// <summary>截图模式：不读真实会话数据（ScreenshotGen 离屏渲染用）。</summary>
        /// <summary>测试接缝：tools/ScreenshotGen 生成商店截图时置 true，以空会话渲染（勿当死代码删除）。</summary>
        internal static bool UseEmptyForScreenshot;
        private readonly AiChatClient _client = new();
        private readonly ObservableCollection<ChatItem> _items = new();
        private readonly ObservableCollection<AiSession> _sessions = new();
        private AiSessionData _sessionData = new();
        private AiSession _current = new();
        private CancellationTokenSource? _cts;
        private bool _streaming;
        private bool _switchingSession;

        // ★ 输出到光标模式
        private bool _inputExpanded;
        private bool _needsTitleGeneration;
        private string _pendingTitleFirstText = "";
        private readonly ShoreHue.Infrastructure.WinApi.CursorOutputService _cursorOutput = new();
        private readonly System.Text.StringBuilder _cursorBuffer = new();
        private System.Windows.Threading.DispatcherTimer? _cursorFlushTimer;
        private System.Windows.Threading.DispatcherTimer? _cursorAimTimer;
        private bool _cursorOutputMode;
        private bool _cursorAiming;
        private bool _cursorAimHooked;     // Tick 只挂一次（见 EnterAim 处注释）
        private bool _cursorFlushHooked;

        /// <summary>面板内“打开设置”按钮被点击时触发（由主窗口订阅）。</summary>
        public static event Action? OpenSettingsRequested;

        public AiChatView()
        {
            InitializeComponent();
            MsgList.ItemsSource = _items;

            _sessionData = UseEmptyForScreenshot ? new AiSessionData() : AiSessionStore.Load();
            foreach (var s in _sessionData.Sessions) _sessions.Add(s);
            SessionCombo.ItemsSource = _sessions;   // DisplayMemberPath 已在 XAML 声明（先于 ItemsSource 生效，避免类型名泄漏）

            var current = _sessionData.Sessions.FirstOrDefault(s => s.Id == _sessionData.CurrentId)
                            ?? _sessionData.Sessions.FirstOrDefault();
            if (current == null)
            {
                current = new AiSession();
                _sessionData.Sessions.Add(current);
                _sessions.Add(current);
                _sessionData.CurrentId = current.Id;
            }
            _current = current;

            _switchingSession = true;
            SessionCombo.SelectedItem = current;
            _switchingSession = false;

            RenderCurrentSession();
            RefreshHeader();
            UpdateEmptyState();

            // ★ 离开可视树时收尾：AiChatView 不是 IWidget（PanelContentController 不会调 OnDeactivated），
            //   而瞄准/输出两个定时器与 CursorOutputService 的锁定在面板切走后没人会停 ——
            //   实测会留下"面板已关，仍每秒 4 次轮询并保持光标输出锁定"的常驻状态。
            Unloaded += (_, _) =>
            {
                try { StopCursorOutput(); }
                catch { /* 卸载清理尽力而为：定时器/锁没释放不影响主流程 */ }
            };
        }

        // ============ 会话管理 ============

        private void RenderCurrentSession()
        {
            _items.Clear();

            // 界面只渲染最近 300 条（避免超长会话卡顿）；完整历史已保存在本机
            const int renderLimit = 300;
            var visible = _current.Messages;
            if (_current.Messages.Count > renderLimit)
            {
                visible = _current.Messages.Skip(_current.Messages.Count - renderLimit).ToList();
                _items.Add(new ChatItem
                {
                    IsError = true,
                    PlainText = string.Format(LocalizationManager.Instance["Ai_OnlyRecent"], renderLimit)
                });
            }

            foreach (var m in visible)
            {
                if (m.Role == ChatRole.User) AddItem(true, m.Content, m);
                else if (m.Role == ChatRole.Assistant) AddItem(false, m.Content, m);
            }
            UpdateEmptyState();
        }

        private void RefreshHeader()
        {
            var settings = AiSettingsStore.Load();
            ModelText.Text = settings.Enabled && !string.IsNullOrWhiteSpace(settings.Model)
                ? string.Format(LocalizationManager.Instance["Ai_ModelText"], settings.Model)
                : LocalizationManager.Instance["Ai_ModelNotConfigured"];
        }

        private void BtnNewSession_Click(object sender, RoutedEventArgs e)
        {
            if (_streaming) return;
            var session = new AiSession();
            _sessionData.Sessions.Add(session);
            _sessions.Add(session);
            _sessionData.CurrentId = session.Id;
            _current = session;
            _switchingSession = true;
            SessionCombo.SelectedItem = session;
            _switchingSession = false;
            _items.Clear();
            AiSessionStore.Save(_sessionData);
            InputBox.Focus();
        }

        private void BtnDeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (_streaming) return;
            if (_sessions.Count <= 1)
            {
                // 只剩一个会话：清空而不是删除
                _current.Messages.Clear();
                _items.Clear();
                AiSessionStore.Save(_sessionData);
                return;
            }

            _sessionData.Sessions.Remove(_current);
            _sessions.Remove(_current);
            var next = _sessions.FirstOrDefault();
            if (next != null)
            {
                _sessionData.CurrentId = next.Id;
                _current = next;
                _switchingSession = true;
                SessionCombo.SelectedItem = next;
                _switchingSession = false;
            }
            RenderCurrentSession();
            AiSessionStore.Save(_sessionData);
        }

        private void SessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_switchingSession || SessionCombo.SelectedItem is not AiSession s) return;
            if (_streaming) return;
            _current = s;
            _sessionData.CurrentId = s.Id;
            RenderCurrentSession();
            AiSessionStore.Save(_sessionData);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_streaming) return;
            _current.Messages.Clear();
            _items.Clear();
            AiSessionStore.Save(_sessionData);
        }

        // ============ 对话 ============

        private void AddItem(bool isUser, string text, ChatMessage? message = null)
        {
            var item = new ChatItem
            {
                IsUser = isUser,
                IsAssistant = !isUser,
                PlainText = text,
                Message = message
            };
            if (!isUser)
                item.Document = MiniMarkdown.ToFlowDocument(text);
            _items.Add(item);
            UpdateEmptyState();
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(() => MsgScroll.ScrollToEnd(), DispatcherPriority.Background);
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e) => await SendAsync();

        // ★ 用 PreviewKeyDown 拦截 Enter：在输入法（IME）组合状态之前处理，
        //   避免中文输入时回车被输入法当作确认键吞掉导致无法发送。
        /// <summary>展开 / 收起输入框（默认单行，展开后多行）。</summary>
        private void BtnExpandInput_Click(object sender, RoutedEventArgs e)
        {
            _inputExpanded = !_inputExpanded;
            if (_inputExpanded)
            {
                InputBox.MinHeight = 64;
                InputBox.MaxHeight = 150;
                InputBox.Height = double.NaN; // Auto：随内容
                InputBox.VerticalContentAlignment = VerticalAlignment.Top;
            }
            else
            {
                InputBox.MinHeight = 30;
                InputBox.MaxHeight = 110;
                InputBox.Height = 30;
                InputBox.VerticalContentAlignment = VerticalAlignment.Center;
            }
            InputBox.Focus();
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        }

        private async Task SendAsync()
        {
            if (_streaming) return;
            string text = InputBox.Text.Trim();
            if (text.Length == 0) return;

            // 新会话：标记等待模型生成标题（回复完成后调用）
            if (ShoreHue.Core.Services.Ai.AiSession.IsUntitled(_current.Title))
            {
                _needsTitleGeneration = true;
                _pendingTitleFirstText = text;
            }

            var userMsg = new ChatMessage { Role = ChatRole.User, Content = text };
            _current.Messages.Add(userMsg);
            AddItem(true, text, userMsg);

            await StreamResponseAsync(userMsg, text);
        }

        /// <summary>
        /// 拖入图片上传给 AI：读取图片 → 生成用户消息（含缩略图）→ 发送给模型分析。
        /// 模型不支持识图时由 StreamResponseAsync 给出友好提示。
        /// </summary>
        public async Task SendImageAsync(string imagePath)
        {
            if (_streaming) return;

            var settings = AiSettingsStore.Load();
            if (!settings.Enabled)
            {
                var warn = new ChatItem { IsError = true, PlainText = LocalizationManager.Instance["Ai_NotEnabled"] };
                _items.Add(warn);
                ScrollToEnd();
                return;
            }

            byte[] bytes;
            try { bytes = await System.IO.File.ReadAllBytesAsync(imagePath); }
            catch { return; }

            string? mime = GetImageMime(imagePath);
            if (mime == null)
            {
                var warn = new ChatItem { IsError = true, PlainText = LocalizationManager.Instance["Ai_BadImage"] };
                _items.Add(warn);
                ScrollToEnd();
                return;
            }

            // 新会话自动命名
            if (ShoreHue.Core.Services.Ai.AiSession.IsUntitled(_current.Title))
            {
                _current.Title = LocalizationManager.Instance["Ai_ImageChatTitle"];
                SessionCombo.SelectedItem = _current;
            }

            string caption = System.IO.Path.GetFileName(imagePath);
            var userMsg = new ChatMessage
            {
                Role = ChatRole.User,
                Content = caption,
                ImageBase64 = Convert.ToBase64String(bytes),
                ImageMime = mime
            };
            _current.Messages.Add(userMsg);
            AddImageItem(imagePath, caption, userMsg);
            AiSessionStore.Save(_sessionData);

            await StreamResponseAsync(userMsg, caption);
        }

        /// <summary>
        /// 拖入文件统一入口：按类型分发。
        /// 图片 → 多模态上传；文本/代码/docx → 读文本发送；其余 → 提示。
        /// </summary>
        public async Task SendFileAsync(string path)
        {
            if (_streaming) return;
            if (!System.IO.File.Exists(path)) return;

            // 图片走多模态
            if (GetImageMime(path) != null)
            {
                await SendImageAsync(path);
                return;
            }

            var settings = AiSettingsStore.Load();
            if (!settings.Enabled)
            {
                ShowWarning(LocalizationManager.Instance["Ai_NotEnabled"]);
                return;
            }

            // 尝试读取文本（含 docx）
            string content = ReadFileAsText(path, out bool isBinary, out bool isDocx);
            if (isBinary)
            {
                ShowWarning(LocalizationManager.Instance["Ai_UnsupportedFile"]);
                return;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                ShowWarning(LocalizationManager.Instance["Ai_NoTextInFile"]);
                return;
            }

            // 截断过大的文件，避免超出模型上下文
            const int maxChars = 12000;
            string display = System.IO.Path.GetFileName(path);
            if (content.Length > maxChars)
            {
                content = content[..maxChars] + Environment.NewLine + string.Format(LocalizationManager.Instance["Ai_Truncated"], maxChars);
            }

            // 新会话自动命名
            if (ShoreHue.Core.Services.Ai.AiSession.IsUntitled(_current.Title))
            {
                _current.Title = display.Length > 20 ? display[..20] + "…" : display;
                SessionCombo.SelectedItem = _current;
            }

            string label = isDocx ? LocalizationManager.Instance["Ai_FileLabelDocx"] : LocalizationManager.Instance["Ai_FileLabelGeneric"];
            string prompt = $"{label}：{display}\n\n以下是文件内容，请分析：\n\n{content}";

            var userMsg = new ChatMessage { Role = ChatRole.User, Content = prompt };
            _current.Messages.Add(userMsg);
            var item = new ChatItem { IsUser = true, PlainText = $"{label}：{display}", Message = userMsg };
            _items.Add(item);
            UpdateEmptyState();
            ScrollToEnd();
            AiSessionStore.Save(_sessionData);

            await StreamResponseAsync(userMsg, prompt);
        }

        /// <summary>读取文件为文本；支持 UTF-8/GBK 文本与 docx。二进制返回 isBinary=true。</summary>
        public static string ReadFileAsText(string path, out bool isBinary, out bool isDocx)
        {
            isDocx = path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
            if (isDocx)
            {
                isBinary = false;
                return ReadDocxText(path);
            }

            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                if (LooksBinary(bytes))
                {
                    isBinary = true;
                    return "";
                }

                isBinary = false;
                // 优先严格 UTF-8；失败（如 GBK 中文）退回系统默认宽松解码
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes);
                }
                catch
                {
                    return Encoding.UTF8.GetString(bytes);
                }
            }
            catch
            {
                isBinary = true;
                return "";
            }
        }

        /// <summary>探测二进制：前 4KB 中 NUL 或控制字符比例过高即视为二进制。</summary>
        private static bool LooksBinary(byte[] bytes)
        {
            if (bytes.Length == 0) return false;
            int sample = Math.Min(bytes.Length, 4096);
            int bad = 0;
            for (int i = 0; i < sample; i++)
            {
                byte b = bytes[i];
                if (b == 0 || (b < 0x09) || (b > 0x0D && b < 0x20))
                    bad++;
            }
            return bad > sample / 10;
        }

        /// <summary>零依赖解析 docx：读 word/document.xml 中的 w:t 文本。</summary>
        private static string ReadDocxText(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) return "";
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string xml = reader.ReadToEnd();
                var sb = new StringBuilder();
                foreach (Match m in Regex.Matches(xml, "<w:t[^>]*>(.*?)</w:t>"))
                {
                    sb.AppendLine(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private void ShowWarning(string message)
        {
            var warn = new ChatItem { IsError = true, PlainText = message };
            _items.Add(warn);
            ScrollToEnd();
        }

        private void AddImageItem(string imagePath, string caption, ChatMessage message)
        {
            var item = new ChatItem
            {
                IsUser = true,
                PlainText = caption,
                ImagePath = imagePath,
                Message = message
            };
            _items.Add(item);
            UpdateEmptyState();
            ScrollToEnd();
        }

        /// <summary>把刚刚加入会话的最后一条用户消息（可能带图）发送给模型并流式渲染回复。</summary>
        private async Task StreamResponseAsync(ChatMessage lastUser, string fallbackText)
        {
            var settings = AiSettingsStore.Load();
            var aiItem = new ChatItem { IsAssistant = true, PlainText = "", Document = MiniMarkdown.ToFlowDocument("") };
            _items.Add(aiItem);
            UpdateEmptyState();
            ScrollToEnd();

            _streaming = true;
            BtnSend.IsEnabled = false;
            BtnStop.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();

            var sb = new System.Text.StringBuilder();
            // ★ 渲染节流：流式 delta 高频到达（每秒可达几十次），若每个 delta 都全量重建
            //   FlowDocument 会在长回复时压垮 UI 线程。这里合并 burst：delta 到达时启动
            //   80ms 定时器，Tick 时把累积文本渲染一次，渲染频率降 80%+；
            //   渲染走 Background 优先级，不阻塞打字/输入。
            var renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            renderTimer.Tick += (_, _) =>
            {
                renderTimer.Stop();
                string snapshot = sb.ToString();
                Dispatcher.BeginInvoke(() =>
                {
                    aiItem.PlainText = snapshot;
                    aiItem.Document = MiniMarkdown.ToFlowDocument(snapshot);
                    ScrollToEnd();
                }, DispatcherPriority.Background);
            };

            try
            {
                var history = _current.Messages.Take(_current.Messages.Count - 1).ToList();
                bool trimmed = TrimToContextWindow(settings, history, fallbackText);
                UpdateContextUsage(settings, history, fallbackText, trimmed);

                string full = await _client.StreamChatAsync(settings, history, fallbackText,
                    delta =>
                    {
                        sb.Append(delta);
                        OnStreamDelta(delta); // ★ 输出到光标模式：累积到缓冲
                        if (!renderTimer.IsEnabled) renderTimer.Start();
                    },
                    _cts.Token,
                    lastUser);

                // 最后一次完整渲染（流式结束，内容已完整）
                aiItem.PlainText = full;
                aiItem.Document = MiniMarkdown.ToFlowDocument(full);
                _current.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = full });
                AiSessionStore.Save(_sessionData);
            }
            catch (OperationCanceledException)
            {
                // 用户点停止：把已生成的文本做一次最终渲染并保留
                string partial = sb.ToString();
                aiItem.PlainText = partial;
                if (partial.Length > 0)
                    aiItem.Document = MiniMarkdown.ToFlowDocument(partial);
                _current.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = partial });
                AiSessionStore.Save(_sessionData);
            }
            catch (Exception ex)
            {
                aiItem.IsError = true;
                aiItem.PlainText = "⚠ " + FriendlyError(ex.Message, lastUser.HasImage);
            }
            finally
            {
                renderTimer.Stop();
                FlushCursorBuffer(); // ★ 流式结束后把剩余内容输出到光标
                _streaming = false;
                BtnSend.IsEnabled = true;
                BtnStop.Visibility = Visibility.Collapsed;
                _cts?.Dispose();
                _cts = null;
                InputBox.Focus();

                // ★ 回复结束后用模型生成会话标题（异步；失败回退原文截断）
                if (_needsTitleGeneration)
                {
                    _needsTitleGeneration = false;
                    string firstText = _pendingTitleFirstText;
                    _pendingTitleFirstText = "";
                    _ = GenerateSessionTitleAsync(settings, firstText, _current);
                }
            }
        }

        /// <summary>用模型为第一个问题生成简洁标题；失败回退原文截断。</summary>
        private async Task GenerateSessionTitleAsync(AiSettings settings, string firstUserText,
            ShoreHue.Core.Services.Ai.AiSession target)
        {
            string fallback = firstUserText.Length > 20 ? firstUserText[..20] + "…" : firstUserText;
            try
            {
                string? title = await System.Threading.Tasks.Task.Run(
                    () => _client.GenerateTitleAsync(settings, firstUserText));
                target.Title = string.IsNullOrWhiteSpace(title) ? fallback : title!;
            }
            catch
            {
                target.Title = fallback;
            }
            // ★ 必须写回**发起时捕获的那个会话**：以前用的是字段 `_current`，
            //   而这中间有 await —— 用户完全可以切到别的会话，于是标题被写到了
            //   用户后来选中的那个会话上（把它的标题覆盖掉），原会话仍是"新对话"。
            //   只有当前显示的正是它时才需要刷新下拉选中项。
            if (ReferenceEquals(_current, target)) SessionCombo.SelectedItem = _current;
            AiSessionStore.Save(_sessionData);
        }

        // ============ 输出到光标 ============

        private void BtnCursorOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_cursorOutputMode)
            {
                StopCursorOutput();
            }
            else if (_cursorAiming)
            {
                // 取消瞄准
                _cursorAiming = false;
                _cursorAimTimer?.Stop();
                BtnCursorOutput.Content = LocalizationManager.Instance["Ai_CursorOutputStart"];
                BtnCursorOutput.ClearValue(Control.BackgroundProperty);
            }
            else
            {
                // 进入瞄准模式：提示用户点击目标窗口
                _cursorAiming = true;
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[CursorOutput] aiming started");
                BtnCursorOutput.Content = LocalizationManager.Instance["Ai_CursorOutputAiming"];
                BtnCursorOutput.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(38, 79, 120));
                _cursorAimTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                // ★ Tick 只挂一次：以前每次进出瞄准都会再 += 一个同样的处理器，
                //   同一个 tick 里 CheckAimTarget 被调用 N 次。
                if (!_cursorAimHooked) { _cursorAimTimer.Tick += (_, _) => CheckAimTarget(); _cursorAimHooked = true; }
                _cursorAimTimer.Start();
            }
        }

        /// <summary>瞄准期间轮询前台窗口：用户点击了目标窗口（前台不再是本应用）即锁定。</summary>
        private void CheckAimTarget()
        {
            if (!_cursorAiming) { _cursorAimTimer?.Stop(); return; }
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[CursorOutput] aim tick");
            if (_cursorOutput.TryLockTarget(out string error))
            {
                _cursorAiming = false;
                _cursorAimTimer?.Stop();
                _cursorOutputMode = true;
                _cursorFlushTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                if (!_cursorFlushHooked) { _cursorFlushTimer.Tick += (_, _) => FlushCursorBuffer(); _cursorFlushHooked = true; }
                _cursorFlushTimer.Start();
                BtnCursorOutput.Content = LocalizationManager.Instance["Ai_CursorOutputActive"];
            }
            // 未锁定（前台还是本应用）：继续等待用户点击目标
        }

        private void StopCursorOutput()
        {
            _cursorOutputMode = false;
            _cursorAiming = false;
            _cursorAimTimer?.Stop();
            _cursorFlushTimer?.Stop();
            FlushCursorBuffer();
            _cursorOutput.Unlock();
            BtnCursorOutput.Content = LocalizationManager.Instance["Ai_CursorOutputStart"];
            BtnCursorOutput.ClearValue(Control.BackgroundProperty);
        }

        /// <summary>把累积的回复文本输出到锁定位置（流式期间每 300ms 批量写入一次）。</summary>
        private void FlushCursorBuffer()
        {
            if (_cursorBuffer.Length == 0) return;
            string chunk = _cursorBuffer.ToString();
            _cursorBuffer.Clear();
            // 必须在 UI（STA）线程调用：Clipboard.SetText 在 MTA 线程会失败
            Dispatcher.BeginInvoke(new Action(() => _cursorOutput.OutputText(chunk)), DispatcherPriority.Background);
        }

        /// <summary>流式收到增量时，若处于输出模式则累积到缓冲。</summary>
        private void OnStreamDelta(string delta)
        {
            if (_cursorOutputMode)
            {
                _cursorBuffer.Append(delta);
                _cursorFlushTimer?.Start();
            }
        }

        // ============ 上下文管理 ============

        /// <summary>按模型上下文窗口从最早的对话开始裁剪（保留最近）。返回是否发生裁剪。</summary>
        private bool TrimToContextWindow(AiSettings settings, List<ChatMessage> history, string userText)
        {
            int window = settings.ContextWindowTokens;
            if (window <= 0 || history.Count <= 1) return false;

            int used = AiChatClient.EstimateTokens(settings.SystemPrompt)
                       + AiChatClient.EstimateMessagesTokens(history)
                       + AiChatClient.EstimateTokens(userText) + 200;
            if (used <= window) return false;

            // 一次 RemoveRange 裁剪（避免逐条 RemoveAt 造成 O(n²) 卡顿）
            int budget = used - window;
            int dropCount = 0;
            for (int i = 0; i < history.Count - 1 && budget > 0; i++)
            {
                budget -= AiChatClient.EstimateTokens(history[i].Content) + 4;
                dropCount++;
            }
            if (dropCount > 0) history.RemoveRange(0, dropCount);
            return dropCount > 0;
        }

        /// <summary>在头部模型信息上显示上下文用量。</summary>
        private void UpdateContextUsage(AiSettings settings, List<ChatMessage> history, string userText, bool trimmed)
        {
            int window = settings.ContextWindowTokens;
            if (window <= 0)
            {
                ModelText.Text = string.Format(LocalizationManager.Instance["Ai_ModelText"], settings.Model);
                return;
            }
            int used = AiChatClient.EstimateTokens(settings.SystemPrompt)
                       + AiChatClient.EstimateMessagesTokens(history)
                       + AiChatClient.EstimateTokens(userText) + 200;
            int pct = Math.Clamp(used * 100 / window, 0, 100);
            ModelText.Text = trimmed
                    ? string.Format(LocalizationManager.Instance["Ai_ModelContext"], settings.Model, pct)
                    : string.Format(LocalizationManager.Instance["Ai_ModelText"], settings.Model);
        }

        // ============ 消息操作 ============

        private void AiCopy_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ChatItem item && !string.IsNullOrEmpty(item.PlainText))
            {
                // 用户点了"复制"却没进剪贴板（剪贴板被别的程序占用是常见原因）
                try { Clipboard.SetText(item.PlainText); }
                catch (Exception ex) { ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[AI] 复制回复到剪贴板失败：{ex.Message}"); }
            }
        }

        private async void AiRegen_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ChatItem item) await RegenerateAsync(item);
        }

        private void AiSave_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ChatItem item && !string.IsNullOrEmpty(item.PlainText))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = LocalizationManager.Instance["Ai_SaveDialogFilter"],
                    FileName = LocalizationManager.Instance["Ai_ReplyFileName"]
                };
                if (dlg.ShowDialog() == true)
                {
                    // 导出失败必须留痕：用户以为文件已经存下来了
                    try { System.IO.File.WriteAllText(dlg.FileName, item.PlainText); }
                    catch (Exception ex) { ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[AI] 导出 Markdown 失败 {dlg.FileName}：{ex.Message}"); }
                }
            }
        }

        private void UserEdit_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ChatItem item) EditUserMessage(item);
        }

        /// <summary>重新生成：删除该 AI 回复（及之后），用相同输入再问一次。</summary>
        private async Task RegenerateAsync(ChatItem aiItem)
        {
            if (_streaming || aiItem.Message == null) return;
            var settings = AiSettingsStore.Load();
            if (!settings.Enabled) return;

            int idx = _current.Messages.IndexOf(aiItem.Message);
            if (idx < 0) return;
            _current.Messages.RemoveRange(idx, _current.Messages.Count - idx);

            int itemIdx = _items.IndexOf(aiItem);
            if (itemIdx >= 0)
            {
                for (int i = _items.Count - 1; i >= itemIdx; i--) _items.RemoveAt(i);
            }

            var userMsg = _current.Messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (userMsg == null) { AiSessionStore.Save(_sessionData); return; }

            AiSessionStore.Save(_sessionData);
            await StreamResponseAsync(userMsg, userMsg.Content);
        }

        /// <summary>编辑用户消息：删除该消息及其后的回复，把文本放回输入框。</summary>
        private void EditUserMessage(ChatItem userItem)
        {
            if (_streaming || userItem.Message == null) return;
            int idx = _current.Messages.IndexOf(userItem.Message);
            if (idx < 0) return;
            _current.Messages.RemoveRange(idx, _current.Messages.Count - idx);

            int itemIdx = _items.IndexOf(userItem);
            if (itemIdx >= 0)
            {
                for (int i = _items.Count - 1; i >= itemIdx; i--) _items.RemoveAt(i);
            }

            InputBox.Text = userItem.Message.Content;
            InputBox.Focus();
            AiSessionStore.Save(_sessionData);
            UpdateEmptyState();
        }

        // ============ 空状态 / 示例 ============

        private void UpdateEmptyState()
        {
            bool empty = _items.Count == 0;
            if (EmptyPanel == null) return;
            EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (!empty) return;

            var settings = AiSettingsStore.Load();
            bool configured = settings.Enabled;
            EmptyTitle.Text = configured ? LocalizationManager.Instance["UI_AiChatView_16"] : LocalizationManager.Instance["Ai_EmptyTitleNotConfigured"];
            EmptyHint.Text = configured
                ? LocalizationManager.Instance["Ai_EmptyHintConfigured"]
                : LocalizationManager.Instance["Ai_EmptyHintNotConfigured"];
            ExamplePanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
            BtnEmptySettings.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Example_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string kind)
            {
                InputBox.Text = kind switch
                {
                    "translate" => "请把下面内容翻译成英文：\n\n",
                    "summarize" => "请总结下面这段内容：\n\n",
                    "explain" => "请解释这段代码的作用：\n\n",
                    _ => ""
                };
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }

        /// <summary>把模型报错转成更友好的提示，特别是图片不支持的场景。</summary>
        private static string FriendlyError(string raw, bool hasImage)
        {
            string lower = raw.ToLowerInvariant();
            if (hasImage && (lower.Contains("image") || lower.Contains("vision") ||
                             lower.Contains("multimodal") || lower.Contains("not support")))
            {
                return raw + Environment.NewLine +
                       LocalizationManager.Instance["Ai_VisionHint"];
            }
            return raw;
        }

        private static string? GetImageMime(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".tiff" or ".tif" => "image/tiff",
                _ => null
            };
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string kind)
            {
                InputBox.Text = kind switch
                {
                    "translate" => "请把下面内容翻译成中文（保留原意与语气）：\n\n",
                    "summarize" => "请用 3-5 句话总结下面内容：\n\n",
                    "explain" => "请解释下面代码的作用，并指出潜在问题：\n\n",
                    "polish" => "请润色下面这段文字，使其更流畅专业：\n\n",
                    _ => ""
                };
                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }

        /// <summary>面板显示时刷新模型信息（设置变更后）。</summary>
        public void RefreshSettings() => RefreshHeader();
    }
}
