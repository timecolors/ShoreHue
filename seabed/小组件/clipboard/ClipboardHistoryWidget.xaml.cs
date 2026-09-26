using ShoreHue.Core.Services;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShoreHue.UI.Widgets.ClipboardHistory
{
    /// <summary>
    /// 剪贴板记忆库：跨重启保留 + 收藏（不被清理）+ 实时搜索 + 分类过滤（全部/收藏/文本/链接/图片/文件）。
    /// </summary>
    public partial class ClipboardHistoryWidget : UserControl, IWidget, IWidgetFooter
    {
        private readonly IClipboardService _clipboardService;
        private readonly List<ClipboardManager.ClipboardItem> _selectedItems = new();

        /// <summary>用于读「设置 → 面板 → 剪贴板」里的开关（键盘操作 / 显示来源应用）。为空时按默认值走。</summary>
        private readonly ShoreHue.Core.Services.Configuration.ISettingsService? _settings
            = ShoreHue.UI.Widgets.HostCapabilities.Settings;

        private Button? _btnDeleteSelected;
        private TextBlock? _statusText;

        private string _filterType = "All";
        private string _searchQuery = "";

        /// <summary>
        /// ★ 无参构造：**从文件夹加载时只能走这一条路**（海床按 `IWidget` 动态实例化，宿主无法注入服务）。
        /// 服务改从窄接口 `HostCapabilities` 取 —— 插件能力层已经提供了剪贴板历史/便签/设置。
        /// 带参构造保留给宿主内部直接 new 的场景（内置实例、市场包副本）。
        /// </summary>
        public ClipboardHistoryWidget()
            : this(ShoreHue.UI.Widgets.HostCapabilities.ClipboardHistory
                   ?? throw new InvalidOperationException("宿主未提供剪贴板服务（HostCapabilities.ClipboardHistory 为空）"))
        {
        }

        public ClipboardHistoryWidget(IClipboardService clipboardService)
        {
            _clipboardService = clipboardService;
            InitializeComponent();
            Subscribe();
            RefreshList();
        }

        public new string Name => LocalizationManager.Instance["WidgetTabs_Clipboard"];

        public UserControl CreateView() => this;

        // ★ 订阅必须成对：`IClipboardService` 是**单例**，而小组件实例会被缓存 ——
        //   只订阅不退订，服务就会一直持有已卸载的视图（每次重建再叠一个回调）。
        private bool _subscribed;

        private void Subscribe()
        {
            if (_subscribed) return;
            _clipboardService.HistoryChanged += OnHistoryChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _clipboardService.HistoryChanged -= OnHistoryChanged;
            _subscribed = false;
        }

        private void OnHistoryChanged(object? sender, EventArgs e) => RefreshList();

        public void OnActivated()
        {
            // ★ 面板一显示就把焦点交给列表：否则搜索框会先拿到焦点，↑↓/数字键全部失灵（要用户先点一下才发现）。
            HistoryList?.Focus();
            // 剪贴板监听已由主窗口应用级常驻（保证 AI 面板复制等也进入历史）
            Subscribe();
            RefreshList();
        }

        public void OnDeactivated()
        {
            // 不再停用全局监听，但本视图的服务订阅要摘掉（否则单例服务 root 住已卸载的视图）
            Unsubscribe();
        }

        public FrameworkElement GetFooterControl()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };

            _btnDeleteSelected = new Button
            {
                Content = LocalizationManager.Instance["Clip_DeleteSelected"],
                Width = 110,
                Height = 26,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(85, 51, 51)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                IsEnabled = false
            };
            _btnDeleteSelected.Click += DeleteSelected_Click;

            var btnClearAll = new Button
            {
                Content = LocalizationManager.Instance["Clip_ClearAll"],
                Width = 80,
                Height = 26,
                FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(85, 51, 51)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnClearAll.Click += ClearAll_Click;

            _statusText = new TextBlock
            {
                Text = LocalizationManager.Instance["Clip_Ready"],
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            panel.Children.Add(_btnDeleteSelected);
            panel.Children.Add(btnClearAll);
            panel.Children.Add(_statusText);

            return panel;
        }

        // ========== 记忆库：过滤 / 排序 ==========

        /// <summary>剪贴板单条最多显示几行（默认 4，值域 1–20）。</summary>
        private static int CurrentMaxLines
        {
            get
            {
                try
                {
                    int lines = ShoreHue.UI.Widgets.HostCapabilities.Settings?.ClipboardDisplayLength ?? 4;
                    return lines < 1 ? 1 : (lines > 20 ? 20 : lines);
                }
                catch
                {
                    // 拿不到设置（例如插件未获授权）→ 用默认值：绝不能因为读设置失败就让列表不显示
                    return 4;
                }
            }
        }

        /// <summary>按分类 + 搜索词过滤，收藏优先置顶，重建列表。</summary>
        private void RefreshList()
        {
            if (HistoryList == null) return;
            var q = _searchQuery?.Trim() ?? "";
            var list = _clipboardService.History
                .Where(i => MatchesType(i) && MatchesQuery(i, q))
                .OrderByDescending(i => i.IsPinned)
                .ThenByDescending(i => i.Timestamp)
                .ToList();

            // ★ 收起高度通过**资源**交给 XAML 的 Style setter（见模板里的说明）：
            //   Style.Setter.Value 不支持 Binding，收起高度若写成元素上的 MaxHeight="..."，
            //   局部值优先级高于样式触发器 → 悬停展开就永远不生效。
            //   每次刷新都写一遍，改了「预览行数」设置后立刻反映到下一次挂载。
            Resources["ClipCollapsedHeight"] = CurrentMaxLines * 16.0;

            _currentItems = list;
            HistoryList.ItemsSource = list;
            UpdateUI();
        }

        private bool MatchesType(ClipboardManager.ClipboardItem item)
        {
            switch (_filterType)
            {
                case "Pinned": return item.IsPinned;
                case "Text": return item.Type == "Text" && !IsLink(item);
                case "Link": return IsLink(item);
                case "Image": return item.Type == "Image";
                case "File": return item.Type == "File";
                default: return true; // All（Html 也归入全部）
            }
        }

        private static bool IsLink(ClipboardManager.ClipboardItem item)
        {
            if (item.Type != "Text") return false;
            var t = item.FullText ?? item.DisplayText ?? "";
            return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesQuery(ClipboardManager.ClipboardItem item, string q)
        {
            if (q.Length == 0) return true;
            var hay = (item.FullText ?? "") + " " + (item.DisplayText ?? "");
            return hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchQuery = SearchBox.Text;
            if (SearchPlaceholder != null)
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Visible : Visibility.Collapsed;
            RefreshList();
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string t)
            {
                _filterType = t;
                if (FilterPanel != null)
                {
                    foreach (var child in FilterPanel.Children)
                    {
                        if (child is Button b)
                        {
                            bool active = (b.Tag as string) == t;
                            b.Background = active
                                ? new SolidColorBrush(Color.FromRgb(0, 120, 212))
                                : new SolidColorBrush(Color.FromRgb(45, 45, 45));
                            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(204, 204, 204));
                        }
                    }
                }
                RefreshList();
            }
        }


        // ========== 原有交互 ==========

        private void UpdateUI()
        {
            if (_btnDeleteSelected != null)
                _btnDeleteSelected.IsEnabled = _selectedItems.Count > 0;
            if (_statusText != null)
                _statusText.Text = string.Format(LocalizationManager.Instance["Clip_Count"], _clipboardService.History.Count);
        }

        // ==================== 列表交互（事件委托：处理器在根命名域，模板里挂不上） ====================
        //
        // ★ 为什么改成委托：`XamlCodeGenerator` 用 `root.FindName(...)` 挂处理器，而 DataTemplate 里的
        //   元素在**独立命名域** → FindName 返回 null → 处理器**静默不挂**。
        //   真机现象就是"列表点不动、☆ 和 ✕ 都没反应"（复制/收藏/单条删除/勾选全部失效）。
        //   现在处理器挂在 HistoryList（根命名域）上，靠 OriginalSource 沿可视树找回条目。

        private ClipboardManager.ClipboardItem? _pressedItem;
        private List<ClipboardManager.ClipboardItem> _currentItems = new();   // 键盘操作用（与界面同一份顺序）

        private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
            => _pressedItem = ItemAt(e.OriginalSource);

        private void List_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var item = ItemAt(e.OriginalSource);
            if (item == null || !ReferenceEquals(_pressedItem, item)) return;   // 按下与松开不同条目 → 当拖拽
            _pressedItem = null;

            // 勾选框：自己接管状态（预览事件已把默认处理挡在后面，不会双重切换）
            if (FindAncestor<CheckBox>(e.OriginalSource) is CheckBox cb)
            {
                bool nowChecked = !(cb.IsChecked ?? false);
                cb.IsChecked = nowChecked;
                if (nowChecked) { if (!_selectedItems.Contains(item)) _selectedItems.Add(item); }
                else _selectedItems.Remove(item);
                UpdateUI();
                e.Handled = true;
                return;
            }

            if (FindAncestor<Button>(e.OriginalSource) is Button btn)
            {
                switch (btn.Tag as string)
                {
                    case "pin":
                        _clipboardService.SetPinned(item, !item.IsPinned);
                        break;
                    case "del":
                        _clipboardService.RemoveItem(item);
                        _selectedItems.Remove(item);
                        UpdateUI();
                        break;
                }
                e.Handled = true;
                return;
            }

            Copy(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }

        // ==================== 键盘操作（借鉴 Win+V / Ditto：剪贴板历史没有键盘就等于只能鼠标点） ====================
        // ↑↓ 选、Enter 复制、Ctrl+Enter 只复制纯文本、数字键 1-9 直接复制第 N 条、Del 删除、Esc 取消选择。

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            // 设置 → 面板 → 剪贴板：「键盘操作」开关（高度自定义：不喜欢的用户可以直接关掉）
            if (_settings != null && !_settings.ClipboardKeyboardNav) return;
            var list = _currentItems;
            if (list.Count == 0) return;
            int idx = _pressedItem == null ? -1 : list.IndexOf(_pressedItem);

            switch (e.Key)
            {
                case Key.Down: MoveCursor(list, idx + 1); e.Handled = true; break;
                case Key.Up: MoveCursor(list, idx <= 0 ? list.Count - 1 : idx - 1); e.Handled = true; break;
                case Key.Enter:
                    if (_pressedItem != null)
                    {
                        Copy(_pressedItem, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                        e.Handled = true;
                    }
                    break;
                case Key.Delete:
                    if (_pressedItem != null)
                    {
                        _clipboardService.RemoveItem(_pressedItem);
                        _selectedItems.Remove(_pressedItem);
                        _pressedItem = null;
                        RefreshList();
                        UpdateUI();
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    _selectedItems.Clear();
                    UpdateUI();
                    e.Handled = true;
                    break;
                default:
                    // 数字键 1-9 快选（搜索框聚焦时不抢键位）
                    // ★ 不再用"搜索框是否聚焦"当守卫（那会误伤：面板刚显示时焦点可能还在搜索框上，
                    //   但事件既然路由到了列表，就说明这一击是给列表的）。改成看事件来源。
                    if (e.Key >= Key.D1 && e.Key <= Key.D9 && e.OriginalSource is not TextBox)
                    {
                        int n = e.Key - Key.D1;
                        if (n < list.Count) { Copy(list[n], Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; }
                    }
                    break;
            }
        }

        /// <summary>移动键盘光标（复用多选集合当"当前项"，界面上勾选框就是光标，看得见）。</summary>
        private void MoveCursor(List<ClipboardManager.ClipboardItem> list, int index)
        {
            index = Math.Clamp(index, 0, list.Count - 1);
            _pressedItem = list[index];
            _selectedItems.Clear();
            _selectedItems.Add(_pressedItem);
            UpdateUI();
            if (HistoryList.ItemContainerGenerator.ContainerFromItem(_pressedItem) is FrameworkElement fe)
                fe.BringIntoView();
        }
        private void Copy(ClipboardManager.ClipboardItem item, bool plainTextOnly = false)
        {
            if (plainTextOnly) _clipboardService.CopyToClipboardPlainText(item);
            else _clipboardService.CopyToClipboard(item);
            if (_statusText != null)
            {
                bool showSource = _settings == null || _settings.ClipboardShowSourceApp;
                _statusText.Text = (!showSource || string.IsNullOrEmpty(item.SourceApp))
                    ? LocalizationManager.Instance["Clip_Copied"]
                    : string.Format(LocalizationManager.Instance["Clip_CopiedFrom"], item.SourceApp);
                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(1.5);
                timer.Tick += (s, args) => { timer.Stop(); UpdateUI(); };
                timer.Start();
            }
        }

        /// <summary>命中的元素 → 它所属的剪贴板条目（条目容器是 ContentPresenter 里的 Border）。</summary>
        private static ClipboardManager.ClipboardItem? ItemAt(object? source)
        {
            for (var d = source as DependencyObject; d != null; d = NextParent(d))
                if (d is Border { DataContext: ClipboardManager.ClipboardItem item }
                    && (d as FrameworkElement)?.TemplatedParent is ContentPresenter)
                    return item;
            return null;
        }

        /// <summary>沿可视树找最近的某类祖先（TextBlock 的命中源可能是 Run → 退回逻辑树）。</summary>
        private static T? FindAncestor<T>(object? source) where T : DependencyObject
        {
            for (var d = source as DependencyObject; d != null; d = NextParent(d))
                if (d is T hit) return hit;
            return null;
        }

        private static DependencyObject? NextParent(DependencyObject d)
        {
            if (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }



        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItems.Count == 0) return;
            var items = _selectedItems.ToList();
            _clipboardService.RemoveItems(items);
            _selectedItems.Clear();
            UpdateUI();
            if (_statusText != null)
                _statusText.Text = string.Format(LocalizationManager.Instance["Clip_Deleted"], items.Count);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_clipboardService.History.Count == 0) return;
            if (MessageBox.Show(LocalizationManager.Instance["Clip_ClearConfirm"],
                    LocalizationManager.Instance["Clip_Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _clipboardService.ClearAll();
                _selectedItems.Clear();
                UpdateUI();
                if (_statusText != null)
                    _statusText.Text = LocalizationManager.Instance["Clip_Cleared"];
            }
        }
    }
}
