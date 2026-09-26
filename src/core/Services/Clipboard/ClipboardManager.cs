using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Clipboard;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ShoreHue.Infrastructure.Utils;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ShoreHue.Core.Services
{
    /// <summary>
    /// 剪贴板管理器（实例类，实现 IClipboardService + IService）
    /// </summary>
    public class ClipboardManager : IClipboardService, IService, IDisposable
    {
        private readonly ISettingsService _settings;
        private string? _lastContentHash;
        private bool _isListening = false;
        private bool _isRestoring = false;
        private readonly object _lock = new object();
        private bool _disposed = false;
        private HwndSource? _messageWindow;

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public ObservableCollection<ClipboardItem> History { get; } = new ObservableCollection<ClipboardItem>();

        public event EventHandler? HistoryChanged;

        // ========== IService 实现 ==========
        public string Name => "ClipboardManager";
        public bool IsInitialized { get; private set; } = false;

        public ClipboardManager(ISettingsService settings)
        {
            _settings = settings;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            LoadHistory();
            IsInitialized = true;
            LogManager.Debug($"ClipboardManager 初始化完成，已加载 {History.Count} 条记录");
        }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            StopListening();
            SaveHistory();
            IsInitialized = false;
            LogManager.Debug("ClipboardManager 已关闭");
        }

        // ============ 公开方法 ============

        public void StartListening()
        {
            if (_isListening) return;
            _isListening = true;
            CreateClipboardListenerWindow();
            LogManager.Debug("剪贴板监听已启动（事件驱动）");
        }

        public void StopListening()
        {
            if (!_isListening) return;
            _isListening = false;
            DestroyClipboardListenerWindow();
            LogManager.Debug("剪贴板监听已停止");
        }

        /// <summary>创建隐藏消息窗口并注册 WM_CLIPBOARDUPDATE 监听（替代轮询）。</summary>
        private void CreateClipboardListenerWindow()
        {
            try
            {
                var p = new HwndSourceParameters("ShoreHueClipboardListener")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                    ExtendedWindowStyle = 0x80 // WS_EX_TOOLWINDOW
                };
                _messageWindow = new HwndSource(p);
                _messageWindow.AddHook(WndProc);
                if (!AddClipboardFormatListener(_messageWindow.Handle))
                {
                    LogManager.Warning("AddClipboardFormatListener 失败");
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("创建剪贴板监听窗口失败", ex);
                _messageWindow = null;
            }
        }

        private void DestroyClipboardListenerWindow()
        {
            try
            {
                if (_messageWindow != null)
                {
                    if (_messageWindow.Handle != IntPtr.Zero)
                        RemoveClipboardFormatListener(_messageWindow.Handle);
                    _messageWindow.Dispose();
                }
            }
            catch (Exception ex)
            {
                // 正在销毁窗口：监听随之失效，清理失败无害
                LogManager.Debug($"[剪贴板] 注销剪贴板监听窗口失败（无害）：{ex.Message}");
            }
            _messageWindow = null;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                // 剪贴板变化事件：延迟一拍再读，避免与写入方抢占剪贴板
                Application.Current?.Dispatcher.BeginInvoke(new Action(CaptureClipboardNow),
                    System.Windows.Threading.DispatcherPriority.Background);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void CaptureClipboardNow()
        {
            if (!_isListening || _isRestoring) return;

            try
            {
                if (!System.Windows.Clipboard.ContainsText() &&
                    !System.Windows.Clipboard.ContainsImage() &&
                    !System.Windows.Clipboard.ContainsFileDropList())
                    return;

                var item = CaptureClipboard();
                if (item == null) return;

                lock (_lock)
                {
                    string hash = item.GetHashString();
                    if (_lastContentHash == hash) return;
                    _lastContentHash = hash;

                    // ★ 去重必须查**整张历史**并把命中的那条移到最前（"最近复制优先"），
                    //   不能只看 History[0]：复制 A → B → A 会得到 [A, B, A] 这种重复项，
                    //   而用户看到的是同一条内容出现两次、且旧的那条抢不到置顶。
                    int dup = -1;
                    for (int i = 0; i < History.Count; i++)
                    {
                        if (History[i].GetHashString() == hash) { dup = i; break; }
                    }
                    if (dup >= 0)
                    {
                        if (dup == 0) return;
                        var existing = History[dup];
                        History.RemoveAt(dup);
                        existing.Timestamp = DateTime.Now;   // 复用同一对象（图片缓存文件也随之复用）
                        History.Insert(0, existing);
                        SaveHistory();
                        return;
                    }

                    item.SourceApp ??= TryGetSourceApp();   // 借鉴 Win+V/Ditto：记下"从哪复制来的"
                    History.Insert(0, item);

                    int maxCount = _settings.ClipboardMaxCount;
                    // ★ 记忆库：收藏（IsPinned）的条目不被自动清理淘汰
                    while (History.Count > maxCount && History.Any(i => !i.IsPinned))
                    {
                        int last = History.Count - 1;
                        while (last >= 0 && History[last].IsPinned) last--;
                        if (last < 0) break;
                        var removed = History[last];
                        History.RemoveAt(last);
                        removed.CleanupCache();
                    }

                    SaveHistory();
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("剪贴板轮询异常", ex);
            }
        }

        private ClipboardItem? CaptureClipboard()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                        return ClipboardItem.FromText(text);
                }

                if (System.Windows.Clipboard.ContainsImage())
                {
                    try
                    {
                        var img = System.Windows.Clipboard.GetImage();
                        if (img != null)
                            return ClipboardItem.FromImage(img, _settings.ClipboardImageMaxWidth);
                    }
                    catch (Exception ex)
                    {
                        // 剪贴板被别的程序占着是常态，后面还有 HTML/文件/文本兜底 → 只留 Debug
                        LogManager.Debug($"[剪贴板] 读取图片失败（继续尝试其它格式）：{ex.Message}");
                    }
                }

                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Clipboard.GetFileDropList();
                    if (files.Count > 0)
                        return ClipboardItem.FromFiles(files.Cast<string>().ToList());
                }

                if (System.Windows.Clipboard.ContainsData(DataFormats.Html))
                {
                    try
                    {
                        var html = System.Windows.Clipboard.GetData(DataFormats.Html) as string;
                        if (!string.IsNullOrWhiteSpace(html))
                            return ClipboardItem.FromHtml(html);
                    }
                    catch (Exception ex)
                    {
                        // 同上：读不到 HTML 还有文件/文本兜底
                        LogManager.Debug($"[剪贴板] 读取 HTML 格式失败（继续尝试其它格式）：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("捕获剪贴板内容失败", ex);
            }
            return null;
        }

        public void RemoveItem(ClipboardItem item)
        {
            lock (_lock)
            {
                if (History.Remove(item))
                {
                    item.CleanupCache();
                    SaveHistory();
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void RemoveItems(IEnumerable<ClipboardItem> items)
        {
            lock (_lock)
            {
                foreach (var item in items.ToList())
                {
                    if (History.Remove(item))
                        item.CleanupCache();
                }
                SaveHistory();
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                bool wasListening = _isListening;
                if (wasListening) StopListening();

                foreach (var item in History)
                    item.CleanupCache();
                History.Clear();
                _lastContentHash = GetCurrentClipboardHash();

                SaveHistory();
                HistoryChanged?.Invoke(this, EventArgs.Empty);

                if (wasListening) StartListening();
            }
        }

        /// <summary>复制回剪贴板。plainTextOnly=true 时只回填纯文本（借鉴 Win+V 的"粘贴为纯文本"：
        /// 从网页复制来的内容常带一堆字体/颜色，粘进编辑器就花掉）。</summary>
        public void CopyToClipboard(ClipboardItem item, bool plainTextOnly = false)
        {
            try
            {
                _isRestoring = true;
                if (plainTextOnly) RestoreAsPlainText(item);
                else item.RestoreToClipboard();
                _lastContentHash = item.GetHashString();
            }
            catch (Exception ex)
            {
                LogManager.Error("复制到剪贴板失败", ex);
            }
            finally
            {
                _isRestoring = false;
            }
        }

        /// <summary>接口成员（可选参数不满足接口签名）：保持对外契约不变，转发到带纯文本开关的实现。</summary>
        void IClipboardService.CopyToClipboard(ClipboardItem item) => CopyToClipboard(item, false);
        void IClipboardService.CopyToClipboardPlainText(ClipboardItem item) => CopyToClipboard(item, true);

        /// <summary>只回填纯文本（HTML 条目取其纯文本投影；图片/文件原样回填）。</summary>
        private static void RestoreAsPlainText(ClipboardItem item)
        {
            switch (item.Type)
            {
                case "Html":
                    System.Windows.Clipboard.SetText(item.FullText ?? item.DisplayText ?? "");
                    break;
                case "Text":
                    System.Windows.Clipboard.SetText(item.FullText ?? item.DisplayText ?? "");
                    break;
                default:
                    item.RestoreToClipboard();
                    break;
            }
        }

        // ★ 就地声明（本项目惯例：哪个服务用哪个服务自己声明，避免为一个 API 抽公共类）
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        /// <summary>取当前前台窗口的进程名作为"来源应用"。拿不到就返回 null（不猜）。</summary>
        private static string? TryGetSourceApp()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return null;
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                string name = p.ProcessName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"[剪贴板] 读取来源应用失败（留空）：{ex.Message}");
                return null;
            }
        }
        /// <summary>收藏/取消收藏（收藏条目不被自动清理，记忆库核心）。</summary>
        public void SetPinned(ClipboardItem item, bool pinned)
        {
            lock (_lock)
            {
                if (item.IsPinned == pinned) return;
                item.IsPinned = pinned;
                SaveHistory();
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private string? GetCurrentClipboardHash()
        {
            try
            {
                var item = CaptureClipboard();
                return item?.GetHashString();
            }
            catch (Exception ex)
            {
                // 返回 null = "这次读不出当前内容"，调用方按"变了"处理（最坏是重复记一条）
                LogManager.Debug($"[剪贴板] 读取当前剪贴板内容失败（按未知处理）：{ex.Message}");
                return null;
            }
        }

        private void LoadHistory()
        {
            try
            {
                string filePath = GetHistoryFilePath();
                if (!File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                var list = System.Text.Json.JsonSerializer.Deserialize<List<ClipboardItemData>>(json);
                if (list == null) return;

                foreach (var data in list)
                {
                    var item = ClipboardItem.FromData(data);
                    if (item != null)
                        History.Add(item);
                }
            }
            catch (Exception ex)
            {
                // ★ 加载失败**不能**静默继续用空列表：下一次 SaveHistory 会把"空历史"写回文件，
                //   把用户的历史彻底抹掉。这里把坏文件改名隔离，保证它不会被随后的保存覆盖。
                LogManager.Error("加载剪贴板历史失败（已隔离坏文件，避免被空历史覆盖）", ex);
                try
                {
                    string bad = GetHistoryFilePath();
                    if (File.Exists(bad))
                    {
                        string quarantine = bad + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Move(bad, quarantine);
                        LogManager.Error("已把无法解析的剪贴板历史移动到：" + quarantine);
                    }
                }
                catch (Exception qex)
                {
                    // 连隔离都失败：至少已记 Error，且此时不会再有"空写回"（文件仍存在但下次 Save 会覆盖）
                    LogManager.Error("隔离损坏的剪贴板历史失败：" + qex.Message, qex);
                }
            }
        }

        private void SaveHistory()
        {
            try
            {
                var list = History.Select(item => item.ToData()).ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                // ★ 原子写：剪贴板历史是会被高频重写的文件，直接 WriteAllText 遇到崩溃/断电会留下截断的 JSON，
                //   而加载端解析失败后返回空列表，下一次保存就把"空"写回去 → 历史**整个丢失**。
                string path = GetHistoryFilePath();
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                LogManager.Error("保存剪贴板历史失败", ex);
                try { if (File.Exists(GetHistoryFilePath() + ".tmp")) File.Delete(GetHistoryFilePath() + ".tmp"); }
                catch { /* 清理失败无害：下次写入会覆盖同名临时文件 */ }
            }

            // ★ 图片缓存总量上限：超限时清理"未被收藏引用且最旧"的缓存文件
            try { EnforceImageCacheLimit(); }
            catch (Exception ex)
            {
                // 限额清理失败 → 图片缓存会继续涨（占磁盘），用户可见的后果，留 Warning
                LogManager.Warning($"[剪贴板] 清理超限图片缓存失败（缓存会继续增长）：{ex.Message}");
            }
        }

        /// <summary>
        /// 图片缓存总量控制：缓存目录总大小超过 ClipboardImageCacheLimitMB 时，
        /// 按文件修改时间从旧到新删除"不在收藏条目中"的图片缓存，直到低于上限。
        /// 收藏（IsPinned）条目的图片永不自动删除。
        /// </summary>
        private void EnforceImageCacheLimit()
        {
            int limitMB = _settings.ClipboardImageCacheLimitMB;
            if (limitMB <= 0) return;

            string dir = AppPaths.ClipboardCacheDir;
            if (!Directory.Exists(dir)) return;

            // 收藏条目引用的缓存文件（保护集）
            var pinnedCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in History)
            {
                if (item.IsPinned && !string.IsNullOrEmpty(item.CachePath))
                    pinnedCache.Add(item.CachePath);
            }

            var files = Directory.GetFiles(dir, "*.png")
                .Select(f => new FileInfo(f))
                .Where(fi => !pinnedCache.Contains(fi.FullName))
                .OrderBy(fi => fi.LastWriteTime)
                .ToList();

            long limitBytes = (long)limitMB * 1024 * 1024;
            long total = files.Sum(fi => fi.Length);
            if (total <= limitBytes) return;

            foreach (var fi in files)
            {
                if (total <= limitBytes) break;
                try
                {
                    long len = fi.Length;
                    fi.Delete();
                    total -= len;
                    LogManager.Debug($"剪贴板图片缓存清理: {fi.Name} (-{len / 1024}KB)");
                }
                catch (Exception ex)
                {
                    // 尽力而为：单个缓存文件删不掉就跳过，下一轮再试
                    LogManager.Debug($"[剪贴板] 删除缓存文件失败（跳过该文件）：{ex.Message}");
                }
            }
        }

        private string GetHistoryFilePath()
        {
            if (!Directory.Exists(AppPaths.DataRoot)) Directory.CreateDirectory(AppPaths.DataRoot);
            return AppPaths.ClipboardHistoryPath;
        }

        public bool SaveDroppedFile(string sourcePath, string targetFolder)
        {
            try
            {
                if (!Directory.Exists(targetFolder))
                    Directory.CreateDirectory(targetFolder);

                string fileName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(targetFolder, fileName);
                int counter = 1;
                while (File.Exists(destPath))
                {
                    string name = Path.GetFileNameWithoutExtension(fileName);
                    string ext = Path.GetExtension(fileName);
                    destPath = Path.Combine(targetFolder, $"{name}_{counter}{ext}");
                    counter++;
                }

                File.Copy(sourcePath, destPath);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Error($"保存拖放文件失败", ex);
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Shutdown();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        // ----- 内部类：ClipboardItem 和 ClipboardItemData（保持不变） -----

        public class ClipboardItem
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Type { get; set; } = "Text";
            public string DisplayText { get; set; } = "";
            public string? FullText { get; set; }
            public string? CachePath { get; set; }
            public List<string>? FilePaths { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public string? HtmlContent { get; set; }

            /// <summary>来源应用（进程名，如 "chrome"）。★ 借鉴 Win+V / Ditto：一眼看出这条是从哪复制来的。
            /// 取不到就留空（不影响功能）。</summary>
            public string? SourceApp { get; set; }

            /// <summary>收藏到常用（不被自动清理上限淘汰）。</summary>
            public bool IsPinned { get; set; }

            public string GetHashString()
            {
                if (Type == "Text") return HashText(FullText ?? DisplayText);

                if (Type == "Image")
                {
                    if (!string.IsNullOrEmpty(CachePath) && File.Exists(CachePath))
                    {
                        try
                        {
                            using var fs = File.OpenRead(CachePath);
                            using var sha = SHA256.Create();
                            var hash = sha.ComputeHash(fs);
                            return Convert.ToBase64String(hash);
                        }
                        catch (Exception ex)
                        {
                            // 降级：读不到缓存文件就退回按文本算哈希（去重精度略降，功能不受影响）
                            LogManager.Debug($"[剪贴板] 读取缓存文件算哈希失败（退回按文本算）：{ex.Message}");
                        }
                    }
                    return HashText(DisplayText);
                }

                if (Type == "File") return HashText(string.Join("|", FilePaths ?? new List<string>()));
                if (Type == "Html") return HashText(HtmlContent ?? FullText ?? DisplayText);

                return HashText(DisplayText);
            }

            private static string HashText(string text)
            {
                using var sha = SHA256.Create();
                var bytes = Encoding.UTF8.GetBytes(text ?? "");
                var hash = sha.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }

            /// <summary>
            /// 显示文本的**安全上限（字符）** —— 只是防线，不是显示策略。
            /// 界面显示多长由「单条最多显示几行」决定：换行显示，到行数上限为止。
            /// ★ 旧值 500 太小：一条稍长的文本就被砍成"前 500 字 + …"，而用户要的是
            ///   "完整显示内容而不是省略号"（2026-09-13）。这里放宽到 4000，只防住病态巨串。
            /// </summary>
            private const int DisplayTextSafetyCap = 4000;

            public static ClipboardItem FromText(string text)
            {
                text ??= "";
                return new ClipboardItem
                {
                    Type = "Text",
                    FullText = text,
                    DisplayText = text.Length > DisplayTextSafetyCap
                        ? text.Substring(0, DisplayTextSafetyCap) + "..."
                        : text
                };
            }

            public static ClipboardItem FromImage(System.Windows.Media.Imaging.BitmapSource image, int maxWidth = 0)
            {
                try
                {
                    if (!Directory.Exists(AppPaths.ClipboardCacheDir)) Directory.CreateDirectory(AppPaths.ClipboardCacheDir);
                    string fileName = $"img_{Guid.NewGuid():N}.png";
                    string filePath = Path.Combine(AppPaths.ClipboardCacheDir, fileName);

                    // ★ 缩略化：最长边超过 maxWidth 时等比缩放后再保存（默认 1280px），
                    //   大幅降低磁盘占用与历史加载开销；恢复时粘贴的也是缩略图（清晰度足够）。
                    var toSave = image;
                    int saveWidth = image.PixelWidth;
                    int saveHeight = image.PixelHeight;
                    if (maxWidth > 0)
                    {
                        int longSide = Math.Max(image.PixelWidth, image.PixelHeight);
                        if (longSide > maxWidth)
                        {
                            double scale = (double)maxWidth / longSide;
                            saveWidth = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
                            saveHeight = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));
                            try
                            {
                                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                                    image, new System.Windows.Media.ScaleTransform(scale, scale));
                                toSave = System.Windows.Media.Imaging.BitmapFrame.Create(scaled);
                            }
                            catch { /* 缩放失败则保存原图 */ }
                        }
                    }

                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(toSave));
                    using (var stream = File.OpenWrite(filePath))
                    {
                        encoder.Save(stream);
                    }

                    return new ClipboardItem
                    {
                        Type = "Image",
                        CachePath = filePath,
                        DisplayText = string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Clip_ImageSize"], saveWidth, saveHeight)
                    };
                }
                catch
                {
                    return new ClipboardItem
                    {
                        Type = "Image",
                        DisplayText = ShoreHue.UI.Localization.LocalizationManager.Instance["Clip_ImageSaveFailed"]
                    };
                }
            }

            public static ClipboardItem FromFiles(List<string> files)
            {
                var names = files.Select(f => Path.GetFileName(f)).ToList();
                return new ClipboardItem
                {
                    Type = "File",
                    FilePaths = files,
                    DisplayText = $"{string.Join(", ", names.Take(3))}" + (names.Count > 3 ? $" (+{names.Count - 3})" : "")
                };
            }

            public static ClipboardItem FromHtml(string html)
            {
                // ★ 先让"分段/换行"标签变成真正的换行，再清理其余标签、压缩空白。
                //   旧实现把 \s+ 一律压成空格 → 多段内容被压成**一长行**，用户看到的就是"全糊在一起"
                //   （2026-09-13 用户提问"复制了多段内容怎么处理的"就是这么来的）。
                string withBreaks = System.Text.RegularExpressions.Regex.Replace(
                    html, @"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?\s*>", "\n",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var plainText = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<.*?>", " ");
                plainText = System.Text.RegularExpressions.Regex.Replace(plainText, "[ \t]+", " ");
                plainText = System.Text.RegularExpressions.Regex.Replace(plainText, @"[ \t]*\n[ \t]*", "\n").Trim();
                return new ClipboardItem
                {
                    Type = "Html",
                    HtmlContent = html,
                    FullText = plainText,
                    // ★ 与纯文本同口径：只留"病态巨串"安全上限，不再按 200 字砍成 "HTML: …"。
                    //   富文本粘贴（网页/编辑器）走的正是这条，"HTML: 前200字..." 既显示不全、
                    //   前缀又占掉了第一行。
                    DisplayText = plainText.Length > DisplayTextSafetyCap
                        ? plainText.Substring(0, DisplayTextSafetyCap) + "..."
                        : plainText
                };
            }

            public static ClipboardItem? FromData(ClipboardItemData data)
            {
                try
                {
                    var item = new ClipboardItem
                    {
                        Id = data.Id,
                        Type = data.Type,
                        DisplayText = data.DisplayText,
                        FullText = data.FullText,
                        CachePath = data.CachePath,
                        FilePaths = data.FilePaths,
                        Timestamp = data.Timestamp,
                        HtmlContent = data.HtmlContent,
                        SourceApp = data.SourceApp,
                        IsPinned = data.IsPinned
                    };

                    if (item.Type == "Image" && !string.IsNullOrEmpty(item.CachePath) && !File.Exists(item.CachePath))
                    {
                        item.DisplayText = ShoreHue.UI.Localization.LocalizationManager.Instance["Clip_ImageMissing"];
                    }
                    return item;
                }
                catch { return null; }
            }

            public ClipboardItemData ToData()
            {
                return new ClipboardItemData
                {
                    Id = Id,
                    Type = Type,
                    DisplayText = DisplayText,
                    FullText = FullText,
                    CachePath = CachePath,
                    FilePaths = FilePaths,
                    Timestamp = Timestamp,
                    HtmlContent = HtmlContent,
                    SourceApp = SourceApp,
                    IsPinned = IsPinned
                };
            }

            public void RestoreToClipboard()
            {
                switch (Type)
                {
                    case "Text":
                        System.Windows.Clipboard.SetText(FullText ?? DisplayText);
                        break;
                    case "Image":
                        if (!string.IsNullOrEmpty(CachePath) && File.Exists(CachePath))
                        {
                            var image = new System.Windows.Media.Imaging.BitmapImage();
                            image.BeginInit();
                            image.UriSource = new Uri(CachePath, UriKind.Absolute);
                            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            image.EndInit();
                            System.Windows.Clipboard.SetImage(image);
                        }
                        break;
                    case "File":
                        if (FilePaths != null && FilePaths.Count > 0)
                        {
                            var collection = new System.Collections.Specialized.StringCollection();
                            collection.AddRange(FilePaths.ToArray());
                            System.Windows.Clipboard.SetFileDropList(collection);
                        }
                        break;
                    case "Html":
                        if (!string.IsNullOrEmpty(HtmlContent))
                        {
                            System.Windows.Clipboard.SetData(DataFormats.Html, HtmlContent);
                        }
                        break;
                }
            }

            public void CleanupCache()
            {
                if (!string.IsNullOrEmpty(CachePath) && File.Exists(CachePath))
                {
                    // 尽力而为：删不掉会留个孤儿缓存文件（下次限额清理还会再扫到）
                    try { File.Delete(CachePath); }
                    catch (Exception ex) { LogManager.Debug($"[剪贴板] 删除孤儿缓存失败（无害）：{ex.Message}"); }
                }
            }
        }

        public class ClipboardItemData
        {
            public string Id { get; set; } = "";
            public string Type { get; set; } = "Text";
            public string DisplayText { get; set; } = "";
            public string? FullText { get; set; }
            public string? CachePath { get; set; }
            public List<string>? FilePaths { get; set; }
            public DateTime Timestamp { get; set; }
            public string? HtmlContent { get; set; }

            /// <summary>来源应用（进程名）。</summary>
            public string? SourceApp { get; set; }

            /// <summary>收藏（不被自动清理）。</summary>
            public bool IsPinned { get; set; }
        }
    }
}
