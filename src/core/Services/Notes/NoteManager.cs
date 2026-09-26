using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.Infrastructure.Utils;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ShoreHue.Core.Services
{
    /// <summary>
    /// 便签管理器（实例类，实现 INoteService + IService）
    ///
    /// ★ 写盘安全（真机反馈后重做）：
    ///   · **原子写**：先写 `.tmp`，再用 `File.Replace` 覆盖 —— 断电/崩溃不会留下半个 JSON；
    ///   · **自带备份**：`File.Replace` 把旧文件挪到 `notes.json.bak`，误删/写坏还有一份；
    ///   · **损坏留档**：载入解析失败时把坏文件**改名留档**（不删、不覆盖），不再"当作没有便签"——
    ///     以前解析失败只记日志、内存里是空列表，随后任何一次自动保存都会把用户的便签**覆盖成空**；
    ///   · **去抖保存**：打字时 800ms 内只落盘一次（原来每按一个键就整份重写一遍）。
    ///     注意：**序列化仍在调用线程**（通常是 UI 线程）完成，后台线程只负责磁盘 I/O，
    ///     否则后台枚举 ObservableCollection 会和界面增删打架。
    /// </summary>
    public class NoteManager : INoteService, IService
    {
        /// <summary>打字时的落盘去抖窗口（毫秒）。</summary>
        internal const int SaveDebounceMs = 800;

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly ObservableCollection<NoteItem> _notes = new();
        private readonly string _dataFilePath;
        private readonly ISettingsService _settings;

        private readonly object _saveLock = new();
        private Timer? _saveTimer;
        private string? _pendingJson;        // 已经序列化好、等着落盘的内容（null = 无待写）

        public event EventHandler? NotesChanged;

        // ========== IService 实现 ==========
        public string Name => "NoteManager";
        public bool IsInitialized { get; private set; } = false;

        public ObservableCollection<NoteItem> Notes => _notes;
        public NoteItem? CurrentNote { get; private set; }

        /// <summary>载入时发现数据文件损坏（已留档）时为一句可显示的话，否则 null。界面拿它提示用户。</summary>
        public string? LoadWarning { get; private set; }

        public NoteManager(ISettingsService settings)
            : this(settings, null)
        {
        }

        /// <summary>可注入数据文件路径的构造（测试用：在临时目录里验证原子写/损坏留档/去抖）。</summary>
        internal NoteManager(ISettingsService settings, string? dataFilePathOverride)
        {
            _settings = settings;
            if (string.IsNullOrWhiteSpace(dataFilePathOverride) && !Directory.Exists(AppPaths.DataRoot))
                Directory.CreateDirectory(AppPaths.DataRoot);
            _dataFilePath = string.IsNullOrWhiteSpace(dataFilePathOverride) ? AppPaths.NotesPath : dataFilePathOverride!;
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            Load();
            IsInitialized = true;
            LogManager.Debug($"NoteManager 初始化完成，已加载 {_notes.Count} 个便签");
        }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            Flush();          // 先把去抖里攒着的那次写完，再收尾
            Save();
            _saveTimer?.Dispose();
            _saveTimer = null;
            IsInitialized = false;
            LogManager.Debug("NoteManager 已关闭");
        }

        // ============ 公开方法 ============

        public void SetCurrentNote(NoteItem? note)
        {
            CurrentNote = note;
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        public NoteItem CreateNote(string? title = null, string? color = null)
        {
            var note = new NoteItem
            {
                Title = title ?? "",
                Color = color ?? _settings.DefaultNoteColor,
                ShowTitle = _settings.NoteShowTitleByDefault,
                CreateTime = DateTime.Now,
                UpdateTime = DateTime.Now
            };
            _notes.Insert(0, note);
            CurrentNote = note;
            Save();
            NotesChanged?.Invoke(this, EventArgs.Empty);
            return note;
        }

        public void DeleteNote(NoteItem note)
        {
            if (_notes.Remove(note))
            {
                if (CurrentNote == note) CurrentNote = _notes.FirstOrDefault();
                Save();
                NotesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>正文字内容：走**去抖**保存（打字很密，不必每个键都整份写盘）。</summary>
        public void UpdateNoteContent(NoteItem note, string content)
        {
            note.Content = content;
            note.UpdateTime = DateTime.Now;
            SaveDebounced();
        }

        public void UpdateNoteTitle(NoteItem note, string title)
        {
            note.Title = title;
            note.UpdateTime = DateTime.Now;
            SaveDebounced();          // 改名也是连续输入（逐字），同样去抖
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateNoteColor(NoteItem note, string color)
        {
            note.Color = color;
            note.UpdateTime = DateTime.Now;
            Save();
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateNoteShowTitle(NoteItem note, bool showTitle)
        {
            note.ShowTitle = showTitle;
            note.UpdateTime = DateTime.Now;
            Save();
            NotesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>立即落盘（原子 + 备份），并取消尚未触发的去抖写入。</summary>
        public void Save()
        {
            string json = JsonSerializer.Serialize(_notes, JsonOpts);
            lock (_saveLock)
            {
                _pendingJson = null;
                WriteAtomic(json);
            }
        }

        /// <summary>去抖落盘：内容先在本线程序列化好，磁盘 I/O 交给定时器线程。</summary>
        public void SaveDebounced()
        {
            string json = JsonSerializer.Serialize(_notes, JsonOpts);
            lock (_saveLock)
            {
                _pendingJson = json;
                _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
            }
        }

        /// <summary>把待写内容立刻落盘（退出、切换便签、关闭面板时用）。</summary>
        public void Flush()
        {
            string? json;
            lock (_saveLock)
            {
                json = _pendingJson;
                _pendingJson = null;
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (json != null) WriteAtomic(json);
        }

        /// <summary>原子写：`.tmp` → `File.Replace`（旧文件自动进 `.bak`）。失败时清理 `.tmp`，绝不破坏旧文件。</summary>
        private void WriteAtomic(string json)
        {
            string tmp = _dataFilePath + ".tmp";
            try
            {
                string? dir = Path.GetDirectoryName(_dataFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_dataFilePath))
                    File.Replace(tmp, _dataFilePath, _dataFilePath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tmp, _dataFilePath);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败无所谓，下次覆盖 */ }
                LogManager.Error("保存便签失败（旧文件未被破坏）", ex);
            }
        }

        private void Load()
        {
            if (!File.Exists(_dataFilePath)) return;
            try
            {
                string json = File.ReadAllText(_dataFilePath);
                var list = JsonSerializer.Deserialize<ObservableCollection<NoteItem>>(json);
                if (list != null)
                {
                    foreach (var item in list) _notes.Add(item);
                    // 恢复上次选中的那一条（IsCurrent 记在文件里），否则用第一条
                    var marked = _notes.FirstOrDefault(n => n.IsCurrent) ?? _notes.FirstOrDefault();
                    foreach (var n in _notes) n.IsCurrent = n == marked;
                    CurrentNote = marked;
                }
            }
            catch (Exception ex)
            {
                // ★ 解析失败**绝不等于"用户没有便签"**：以前这里只记日志、内存空列表，
                //   随后任何一次自动保存都会把损坏文件覆盖成 `[]` —— 数据真的没了。
                //   现在把坏文件改名留档（保留原字节），再以空列表继续（用户可重新开始，数据仍可抢救）。
                string kept = MoveAsideCorrupt();
                LoadWarning = "便签数据文件损坏，已留档为 " + Path.GetFileName(kept) + "（原文件未删除，未丢失）";
                LogManager.Error($"加载便签失败，已留档为 {kept}（未删除，可人工抢救）", ex);
            }
        }

        /// <summary>把损坏的数据文件改名留档（绝不删除）；返回留档后的路径。</summary>
        private string MoveAsideCorrupt()
        {
            try
            {
                string kept = _dataFilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(_dataFilePath, kept, overwrite: false);
                return kept;
            }
            catch (Exception ex)
            {
                LogManager.Warning($"便签损坏文件留档失败（原文件仍在原处，请勿手动覆盖）：{ex.Message}");
                return _dataFilePath;
            }
        }
    }
}
