using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShoreHue.UI.Widgets.Notes
{
    /// <summary>
    /// 便签面板（对照成熟方案重做：Windows 便笺 / Google Keep）。
    ///
    /// 借鉴的四条结构，以及为什么：
    ///   ① **没有独立标题字段**：标题 = 正文第一行（便笺与 Keep 都是这样，"改名"就是改第一行）。
    ///      原来的"独立标题框 + 「标」显示开关"带来两套真相：改标题不刷新标签、不落盘、隐藏后改不了名。
    ///   ② **列表 + 搜索**（便笺有便笺列表窗口、Keep 有搜索）：笔记一多没有搜索就没法用。
    ///   ③ **置顶**（Keep）：重要的排最前，用 📌 标记。
    ///   ④ **删除立即生效 + 撤销**（Keep 的撤销条）：不再用模态确认框打断人。
    /// 颜色用固定色板（便笺 7 色 / Keep 12 色），系统取色器退到「更多颜色」。
    /// </summary>
    public partial class NoteWidget : UserControl, IWidget, IWidgetFooter
    {
        private readonly INoteService _noteService;
        private readonly ISettingsService _settings;
        private bool _isUpdating;

        private Button? _btnNewNote;
        private Button? _btnDeleteNote;
        private Button? _btnPin;
        private Button? _btnUndo;
        private TextBlock? _statusText;

        private (NoteItem Note, int Index)? _lastDeleted;   // 删除后 8 秒内可撤销
        private System.Windows.Threading.DispatcherTimer? _undoTimer;

        public NoteWidgetViewModel ViewModel { get; } = new NoteWidgetViewModel();

        public NoteWidget()
            : this(ShoreHue.UI.Widgets.HostCapabilities.Notes
                   ?? throw new InvalidOperationException("宿主未提供便签服务（HostCapabilities.Notes 为空）"),
                   ShoreHue.UI.Widgets.HostCapabilities.Settings
                   ?? throw new InvalidOperationException("宿主未提供设置服务（HostCapabilities.Settings 为空）"))
        {
        }

        public NoteWidget(INoteService noteService, ISettingsService settings)
        {
            _noteService = noteService;
            _settings = settings;
            InitializeComponent();
            DataContext = ViewModel;

            Subscribe();
            RefreshList(resetSelection: true);
            UpdateUI();
            UpdateStatus();
        }

        public new string Name => LocalizationManager.Instance["WidgetTabs_Notes"];

        public UserControl CreateView() => this;

        private bool _subscribed;

        private void Subscribe()
        {
            if (_subscribed) return;
            _noteService.NotesChanged += OnNotesChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _noteService.NotesChanged -= OnNotesChanged;
            _subscribed = false;
        }

        public void OnActivated()
        {
            Subscribe();
            SyncStoredTitles();
            RefreshList(resetSelection: false);
            UpdateUI();
        }

        /// <summary>
        /// 一次性把老的 `Title` 字段对齐到"第一行"（旧版本的标题是独立字段，数据里还留着「便签 1」这种）。
        /// ★ 不迁移的话就又是两套真相：界面显示第一行、文件里存着另一个标题。
        /// </summary>
        private void SyncStoredTitles()
        {
            bool dirty = false;
            foreach (var n in _noteService.Notes)
            {
                string title = n.FirstLine;
                if (title.Length > 0 && !string.Equals(n.Title, title, StringComparison.Ordinal))
                {
                    n.Title = title;
                    dirty = true;
                }
            }
            if (dirty) _noteService.Save();
        }

        /// <summary>切走前：丢弃空便签（Keep 的做法）+ 把去抖里攒着的那次写入落盘。</summary>
        public void OnDeactivated()
        {
            DiscardIfEmpty(_noteService.CurrentNote);
            _noteService.Save();
            Unsubscribe();
        }

        public FrameworkElement GetFooterControl()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

            _btnNewNote = FooterButton(LocalizationManager.Instance["Note_New"], 80, Color.FromRgb(64, 64, 64), () => NewNote());
            _btnDeleteNote = FooterButton(LocalizationManager.Instance["Note_Delete"], 70, Color.FromRgb(85, 51, 51), () => DeleteCurrent());
            _btnDeleteNote.Margin = new Thickness(8, 0, 0, 0);
            _btnPin = FooterButton(LocalizationManager.Instance["Note_Pin"], 70, Color.FromRgb(64, 72, 88), () => TogglePin());
            _btnPin.Margin = new Thickness(8, 0, 0, 0);
            _btnUndo = FooterButton(LocalizationManager.Instance["Note_Undo"], 70, Color.FromRgb(72, 72, 72), () => UndoDelete());
            _btnUndo.Margin = new Thickness(8, 0, 0, 0);
            _btnUndo.Visibility = Visibility.Collapsed;

            _statusText = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(_btnNewNote);
            panel.Children.Add(_btnDeleteNote);
            panel.Children.Add(_btnPin);
            panel.Children.Add(_btnUndo);
            panel.Children.Add(_statusText);

            if (!string.IsNullOrEmpty(_noteService.LoadWarning)) _statusText.Text = _noteService.LoadWarning;
            return panel;
        }

        private static Button FooterButton(string text, double width, Color bg, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                Width = width,
                Height = 26,
                FontSize = 11,
                Background = new SolidColorBrush(bg),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        // ==================== 快捷键（面板内生效；键位在「设置 → 面板 → 便签」里配） ====================

        private void Root_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            HandleHotkey(Keyboard.Modifiers, e.Key, e);
        }

        /// <summary>
        /// 快捷键决策（**从事件处理器里抽出来**，这样"按了某个组合键会干什么"能被测试直接驱动；
        /// 事件处理器只负责把当前修饰键与按键转交进来）。
        /// 用 KeyDown 冒泡而不是 PreviewKeyDown：后者不在受限 XAML 方言允许的事件名单里。
        /// </summary>
        public void HandleHotkey(ModifierKeys mods, Key key, KeyEventArgs? e = null)
        {

            bool handled = false;

            // Ctrl+F 搜索 / Esc 退出搜索：成熟方案的通用键位，写死不算"可配置快捷键"
            if (key == Key.F && mods == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                handled = true;
            }
            else if (key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
            {
                SearchBox.Text = "";
                ContentEditor.Focus();
                handled = true;
            }
            else if (HotkeyParser.Matches(_settings.NoteHotkeyNew, mods, key)) { NewNote(); handled = true; }
            else if (HotkeyParser.Matches(_settings.NoteHotkeyDelete, mods, key)) { DeleteCurrent(); handled = true; }
            else if (HotkeyParser.Matches(_settings.NoteHotkeyNext, mods, key)) { CycleNote(1); handled = true; }

            if (handled && e != null) e.Handled = true;
        }

        private void CycleNote(int delta)
        {
            var list = CurrentList();
            if (list.Count < 2) return;
            int idx = _noteService.CurrentNote == null ? -1 : list.IndexOf(_noteService.CurrentNote);
            int next = ((idx + delta) % list.Count + list.Count) % list.Count;
            SelectNote(list[next]);
        }

        // ==================== 列表（排序 + 搜索过滤） ====================

        private string SearchText => (SearchBox?.Text ?? "").Trim();

        /// <summary>搜索框为空时给全部；否则按第一行/正文匹配（大小写不敏感）。</summary>
        private List<NoteItem> CurrentList()
        {
            var q = SearchText;
            var all = _noteService.Notes.AsEnumerable();
            if (q.Length > 0)
                all = all.Where(n => (n.FirstLine + "\n" + n.Content).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            // ★ 排序对照 Keep：置顶优先，其次新建在前。**不按修改时间排** —— 一边打字一边把芯片
            //   挪到最前会让人点错（列表可以，手指要点的芯片不行）。
            return all.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.CreateTime).ToList();
        }

        private void RefreshList(bool resetSelection)
        {
            var list = CurrentList();
            NoteTabs.ItemsSource = list;

            if (resetSelection && _noteService.CurrentNote == null && list.Count > 0)
                _noteService.SetCurrentNote(list[0]);

            // 当前便签被搜索过滤掉了 → 继续编辑它也没错（成熟方案也是"列表筛掉、编辑区不动"），
            // 但要让状态说清楚"当前这条不在筛选结果里"，否则用户以为搜索坏了。
            ViewModel.EmptyHintVisibility = _noteService.Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshList(resetSelection: false);

        // ==================== 状态同步 ====================

        private void LoadEditorFrom(NoteItem? note)
        {
            _isUpdating = true;
            try { ContentEditor.Text = note?.Content ?? ""; }
            finally { _isUpdating = false; }
        }

        private void SelectNote(NoteItem note)
        {
            DiscardIfEmpty(_noteService.CurrentNote);   // 空便签不留（Keep 行为）
            _noteService.Save();
            _noteService.SetCurrentNote(note);
            ViewModel.CurrentNote = note;
            LoadEditorFrom(note);
            UpdateUI();
            UpdateStatus();
        }

        private void UpdateUI()
        {
            var current = _noteService.CurrentNote;
            ViewModel.CurrentNote = current;
            ViewModel.ShowTitle = false;                    // 已无独立标题框（保留属性仅为兼容绑定）
            ViewModel.EmptyHintVisibility = _noteService.Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ViewModel.RefreshColorBrush();

            foreach (var note in _noteService.Notes) note.IsCurrent = note == current;

            if (_btnDeleteNote != null) _btnDeleteNote.IsEnabled = current != null;
            if (_btnPin != null) _btnPin.Content = LocalizationManager.Instance[current?.Pinned == true ? "Note_Unpin" : "Note_Pin"];
        }

        private void UpdateStatus()
        {
            var current = _noteService.CurrentNote;
            string body;
            if (current == null)
                body = LocalizationManager.Instance["Note_NoNote"];
            else
            {
                var list = CurrentList();
                int pos = list.IndexOf(current);
                body = string.Format(LocalizationManager.Instance[pos >= 0 ? "Note_Status" : "Note_StatusFiltered"],
                                     pos >= 0 ? pos + 1 : 0, list.Count);
                body += " · " + string.Format(LocalizationManager.Instance["Note_EditedAt"], current.UpdateTime.ToString("HH:mm"));
            }
            ViewModel.StatusText = string.IsNullOrEmpty(_noteService.LoadWarning) ? body : _noteService.LoadWarning + "；" + body;
            if (_statusText != null) _statusText.Text = ViewModel.StatusText;
        }

        private void OnNotesChanged(object? sender, EventArgs e)
        {
            RefreshList(resetSelection: false);
            UpdateUI();
            UpdateStatus();
        }

        // ==================== 增删改 ====================

        private void NewNote()
        {
            _noteService.Save();
            var note = _noteService.CreateNote("");
            ViewModel.CurrentNote = note;
            LoadEditorFrom(note);
            SearchBox.Text = "";             // 新建的便签若被搜索过滤掉会让人以为没建成，先清搜索
            RefreshList(resetSelection: false);
            UpdateUI();
            UpdateStatus();
            ContentEditor.Focus();
        }

        private void DeleteCurrent()
        {
            if (_noteService.CurrentNote == null) return;
            Delete(_noteService.CurrentNote);
        }

        /// <summary>删除：**不弹确认框**（成熟方案都是删了给撤销），8 秒内可撤销。</summary>
        private void Delete(NoteItem note)
        {
            int index = _noteService.Notes.IndexOf(note);
            _lastDeleted = (note, index);
            _noteService.DeleteNote(note);

            ViewModel.CurrentNote = _noteService.CurrentNote;
            LoadEditorFrom(_noteService.CurrentNote);
            RefreshList(resetSelection: true);
            UpdateUI();
            UpdateStatus();
            ArmUndo();
        }

        private void ArmUndo()
        {
            if (_btnUndo != null) _btnUndo.Visibility = Visibility.Visible;
            _undoTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _undoTimer.Tick -= OnUndoTimerTick;
            _undoTimer.Tick += OnUndoTimerTick;
            _undoTimer.Stop();
            _undoTimer.Start();

            if (_statusText != null && _lastDeleted != null)
                _statusText.Text = string.Format(LocalizationManager.Instance["Note_Deleted"], TitleOf(_lastDeleted.Value.Note));
        }

        private void OnUndoTimerTick(object? sender, EventArgs e)
        {
            _undoTimer?.Stop();
            _lastDeleted = null;
            if (_btnUndo != null) _btnUndo.Visibility = Visibility.Collapsed;
            UpdateStatus();
        }

        private void UndoDelete()
        {
            if (_lastDeleted == null) return;
            var (note, index) = _lastDeleted.Value;
            _lastDeleted = null;
            _undoTimer?.Stop();
            if (_btnUndo != null) _btnUndo.Visibility = Visibility.Collapsed;

            int at = Math.Clamp(index, 0, _noteService.Notes.Count);
            _noteService.Notes.Insert(at, note);
            _noteService.SetCurrentNote(note);
            _noteService.Save();
            ViewModel.CurrentNote = note;
            LoadEditorFrom(note);
            RefreshList(resetSelection: false);
            UpdateUI();
            UpdateStatus();
        }

        private void DuplicateCurrent()
        {
            var current = _noteService.CurrentNote;
            if (current == null) return;
            var copy = _noteService.CreateNote(current.Title, current.Color);
            _noteService.UpdateNoteContent(copy, current.Content);
            copy.Pinned = current.Pinned;
            _noteService.Save();
            LoadEditorFrom(copy);
            RefreshList(resetSelection: false);
            UpdateUI();
            UpdateStatus();
        }

        private void TogglePin()
        {
            var current = _noteService.CurrentNote;
            if (current == null) return;
            current.Pinned = !current.Pinned;
            _noteService.Save();
            RefreshList(resetSelection: false);
            UpdateUI();
            UpdateStatus();
        }

        /// <summary>按成熟色板换色（便笺 7 色 / Keep 12 色 → 这里 8 色 + 更多）。</summary>
        private void SetColor(string hex)
        {
            var current = _noteService.CurrentNote;
            if (current == null) return;
            _noteService.UpdateNoteColor(current, hex);
            UpdateUI();
        }

        private void PickCustomColor()
        {
            var current = _noteService.CurrentNote;
            if (current == null) return;
            using var dialog = new System.Windows.Forms.ColorDialog();
            dialog.Color = HexToDrawingColor(current.Color);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                SetColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }

        /// <summary>空便签（一个字都没有）在离开它时丢弃 —— Keep 同行为，避免攒出一堆"空便签 1/2/3"。</summary>
        private void DiscardIfEmpty(NoteItem? note)
        {
            if (note == null) return;
            if (!string.IsNullOrWhiteSpace(note.Content)) return;
            if (_noteService.Notes.Count <= 1) return;     // 只剩这一条就留着，否则编辑区没东西可编
            _noteService.DeleteNote(note);
        }

        private string TitleOf(NoteItem note)
            => string.IsNullOrEmpty(note.FirstLine) ? LocalizationManager.Instance["Note_Untitled"] : note.FirstLine;

        // ==================== 芯片交互（事件委托：处理器在根命名域，模板里挂不上） ====================

        private NoteItem? _pressedNote;   // 按下时命中的芯片（松开时必须是同一张，免得拖拽/滚动误选）

        private void Tabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _pressedNote = NoteAt(e.OriginalSource, out _);
        }

        /// <summary>
        /// 芯片上的左键（切换/关闭）与右键（菜单）都在这里判定。
        /// ★ 为什么不用模板内事件：`XamlCodeGenerator` 用 `root.FindName(...)` 挂处理器，而
        ///   DataTemplate 里的元素在**独立命名域** → FindName 返回 null → 处理器**静默不挂**
        ///   （真机现象：芯片点不动、✕ 没反应）。挂在 ItemsControl 上则在其根命名域内，稳。
        /// </summary>
        private void Tabs_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            var note = NoteAt(e.OriginalSource, out bool onCloseButton);
            if (note == null) return;

            if (e.ChangedButton == MouseButton.Right)
            {
                if (note != _noteService.CurrentNote) SelectNote(note);
                ShowChipMenu(note);
                e.Handled = true;
                return;
            }

            if (e.ChangedButton != MouseButton.Left) return;
            if (!ReferenceEquals(_pressedNote, note)) return;   // 按下与松开不在同一张 → 当拖拽，忽略
            _pressedNote = null;

            if (onCloseButton) Delete(note);
            else if (note != _noteService.CurrentNote) SelectNote(note);
            e.Handled = true;
        }

        /// <summary>命中的元素 → 它所属的便签芯片；onCloseButton 表示这一击落在 ✕ 上。</summary>
        private static NoteItem? NoteAt(object? source, out bool onCloseButton)
        {
            onCloseButton = false;
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is Button) onCloseButton = true;
                if (d is Border { DataContext: NoteItem note } && (d as FrameworkElement)?.TemplatedParent is ContentPresenter)
                    return note;
                d = NextParent(d);
            }
            return null;
        }

        private static DependencyObject? NextParent(DependencyObject d)
        {
            // TextBlock 的命中源可能是 Run（不是 Visual）→ 可视树取不到父级，退回逻辑树
            if (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        /// <summary>右键菜单（代码构造）：受限 XAML 方言里没有能表达"菜单项点击"的写法。</summary>
        private void ShowChipMenu(NoteItem note)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeMenuItem(LocalizationManager.Instance[note.Pinned ? "Note_Unpin" : "Note_Pin"], TogglePin));

            var colors = new MenuItem { Header = LocalizationManager.Instance["Note_Recolor"] };
            colors.Items.Add(MakeMenuItem(
                LocalizationManager.Instance["Note_FollowPanel"] + (NoteWidgetViewModel.FollowsPanel(note) ? "  ✓" : ""),
                () => SetColor("#00000000")));
            foreach (var c in NotePalette.Skip(1)) colors.Items.Add(ColorMenuItem(c));
            var more = new MenuItem { Header = LocalizationManager.Instance["Note_MoreColors"] };
            more.Click += (_, _) => PickCustomColor();
            colors.Items.Add(new Separator());
            colors.Items.Add(more);
            menu.Items.Add(colors);

            menu.Items.Add(MakeMenuItem(LocalizationManager.Instance["Note_Duplicate"], DuplicateCurrent));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(LocalizationManager.Instance["Note_Delete"], () => Delete(note)));

            menu.PlacementTarget = NoteTabs;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>固定色板：与 Windows 便笺/Keep 一样的浅色系（正文用自动对比色，深色也不会看不清）。</summary>
        /// <summary>
        /// 色板：**首项是全透明 = 跟随面板背景**（面板底色本身可配置，写死一个颜色早晚对不上），
        /// 其余是常用便签色。深色在前，方便暗色界面直接用。
        /// </summary>
        private static readonly string[] NotePalette =
        {
            "#00000000", "#1E1E1E", "#000000", "#FFFF99", "#FFD08A",
            "#FFB3B3", "#F5B7F5", "#B7C9FF", "#B7F0C2", "#D9D9D9",
        };

        private MenuItem ColorMenuItem(string hex)
        {
            var item = new MenuItem { Header = "  ", Background = (Brush)new BrushConverter().ConvertFromString(hex)! };
            item.Click += (_, _) => SetColor(hex);
            return item;
        }

        private static MenuItem MakeMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        private void NewNote_Click(object sender, RoutedEventArgs e) => NewNote();

        /// <summary>
        /// 正文变了：① 落盘（服务去抖）；② **把标题同步成第一行**（标题=第一行的数据面）。
        /// ★ 这就是"没有独立标题框"的实现：改第一行即改名，不再有两套真相。
        /// </summary>
        private void Content_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            var note = ViewModel.CurrentNote;
            if (note == null) return;
            if (ContentEditor.Text == note.Content) return;

            _noteService.UpdateNoteContent(note, ContentEditor.Text);

            string title = note.FirstLine;
            if (!string.Equals(note.Title, title, StringComparison.Ordinal))
                _noteService.UpdateNoteTitle(note, title);

            // 搜索框非空时，正文改动可能让它进出结果集 → 只在结果集变化时才重排（避免打字时列表抖动）
            if (SearchText.Length > 0) RefreshList(resetSelection: false);
            UpdateStatus();
        }

        private System.Drawing.Color HexToDrawingColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return System.Drawing.Color.FromArgb(255, 0, 0, 0);
                if (hex.StartsWith("#")) hex = hex.Substring(1);
                byte a = 255, r = 0, g = 0, b = 0;
                if (hex.Length == 6)
                {
                    r = Convert.ToByte(hex.Substring(0, 2), 16);
                    g = Convert.ToByte(hex.Substring(2, 2), 16);
                    b = Convert.ToByte(hex.Substring(4, 2), 16);
                }
                else if (hex.Length == 8)
                {
                    a = Convert.ToByte(hex.Substring(0, 2), 16);
                    r = Convert.ToByte(hex.Substring(2, 2), 16);
                    g = Convert.ToByte(hex.Substring(4, 2), 16);
                    b = Convert.ToByte(hex.Substring(6, 2), 16);
                }
                else return System.Drawing.Color.FromArgb(255, 0, 0, 0);
                return System.Drawing.Color.FromArgb(a, r, g, b);
            }
            catch { return System.Drawing.Color.FromArgb(255, 0, 0, 0); }
        }
    }

    /// <summary>便签视图模型：整块底色 / 文字对比色 / 空状态提示。</summary>
    public class NoteWidgetViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private NoteItem? _currentNote;
        private bool _showTitle;
        private string _statusText = "";
        private Visibility _emptyHintVisibility = Visibility.Collapsed;
        private SolidColorBrush _colorBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));
        private SolidColorBrush _bodyForeground = new SolidColorBrush(Color.FromRgb(238, 238, 238));

        public NoteItem? CurrentNote
        {
            get => _currentNote;
            set
            {
                _currentNote = value;
                OnPropertyChanged(nameof(CurrentNote));
                RefreshColorBrush();
                OnPropertyChanged(nameof(ShowTitleVisibility));
            }
        }

        /// <summary>兼容保留：早期版本有「显示标题」开关，现在标题框已取消（标题=第一行）。</summary>
        public bool ShowTitle
        {
            get => _showTitle;
            set
            {
                _showTitle = value;
                OnPropertyChanged(nameof(ShowTitle));
                OnPropertyChanged(nameof(ShowTitleVisibility));
            }
        }

        public Visibility ShowTitleVisibility => ShowTitle ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyHintVisibility
        {
            get => _emptyHintVisibility;
            set { _emptyHintVisibility = value; OnPropertyChanged(nameof(EmptyHintVisibility)); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public SolidColorBrush ColorBrush
        {
            get => _colorBrush;
            set { _colorBrush = value; OnPropertyChanged(nameof(ColorBrush)); }
        }

        /// <summary>正文颜色：按底色亮度自动选深色或浅色（用户选了深色底也不会有看不清的字）。</summary>
        public SolidColorBrush BodyForeground
        {
            get => _bodyForeground;
            set { _bodyForeground = value; OnPropertyChanged(nameof(BodyForeground)); }
        }

        private static readonly SolidColorBrush BrushEmpty = Frozen(Color.FromArgb(0, 0, 0, 0));   // 透明：跟随面板
        private static readonly SolidColorBrush BrushFallback = Frozen(Color.FromRgb(0, 0, 0));
        private static readonly SolidColorBrush InkDark = Frozen(Color.FromRgb(30, 30, 30));
        private static readonly SolidColorBrush InkLight = Frozen(Color.FromRgb(242, 242, 242));
        private static readonly Dictionary<Color, SolidColorBrush> _brushCache = new();

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static SolidColorBrush BrushFor(Color c)
        {
            lock (_brushCache)
            {
                if (_brushCache.TryGetValue(c, out var cached)) return cached;
                var b = Frozen(c);
                if (_brushCache.Count < 64) _brushCache[c] = b;
                return b;
            }
        }

        /// <summary>应用的主题前景色（跟随面板时用）；查不到就退回浅字。</summary>
        private static SolidColorBrush? _panelInk;
        private static SolidColorBrush PanelInk()
        {
            if (_panelInk != null) return _panelInk;
            var app = System.Windows.Application.Current;
            foreach (var key in new[] { "TextPrimaryBrush", "SettingsWindowFg" })
                if (app?.TryFindResource(key) is SolidColorBrush b) { _panelInk = b; return b; }
            _panelInk = InkLight;
            return _panelInk;
        }

        /// <summary>这张便签是否"跟随面板"（颜色为全透明）。</summary>
        public static bool FollowsPanel(NoteItem note)
        {
            try { return ((Color)ColorConverter.ConvertFromString(note.Color)!).A == 0; }
            catch { return false; }
        }
        /// <summary>感知亮度（sRGB 加权）：> 0.62 认为底色偏亮 → 用深字。</summary>
        private static bool IsLight(Color c)
            => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 > 0.62;

        public void RefreshColorBrush()
        {
            if (CurrentNote == null)
            {
                if (!ReferenceEquals(ColorBrush, BrushEmpty)) ColorBrush = BrushEmpty;
                if (!ReferenceEquals(BodyForeground, InkLight)) BodyForeground = InkLight;
                return;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(CurrentNote.Color)!;
                // α=0（透明）= 跟随面板：底色交回面板，文字用应用的主题前景色
                bool followsPanel = color.A == 0;
                var brush = followsPanel ? BrushEmpty : BrushFor(color);
                if (!ReferenceEquals(ColorBrush, brush)) ColorBrush = brush;
                var ink = followsPanel ? PanelInk() : (IsLight(color) ? InkDark : InkLight);
                if (!ReferenceEquals(BodyForeground, ink)) BodyForeground = ink;
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                    $"[便签] 颜色值无法解析（改用默认色）：{ex.Message}");
                if (!ReferenceEquals(ColorBrush, BrushFallback)) ColorBrush = BrushFallback;
                if (!ReferenceEquals(BodyForeground, InkDark)) BodyForeground = InkDark;
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
