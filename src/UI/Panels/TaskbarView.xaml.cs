using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Shortcuts;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShoreHue.Infrastructure.WinApi;
using System.Collections.Generic;
using System.Linq;

namespace ShoreHue.UI.Panels
{
    public partial class TaskbarView : UserControl
    {
        private readonly TaskbarShortcutManager _shortcutManager;
        private readonly ISettingsService _settings;
        private readonly Func<IEnumerable<WindowListProvider.WindowItem>>? _windowSource;
        private DispatcherTimer? _refreshTimer;
        private DateTime _lastRefresh = DateTime.MinValue;

        // 数据集合
        private readonly ObservableCollection<TaskbarItem> _shortcuts = new();
        private readonly ObservableCollection<TaskbarItem> _windows = new();
        /// <summary>★ 分组显示列表：窗口标签（TaskbarItem）或分组标签（TaskbarGroupItem）。</summary>
        private readonly ObservableCollection<object> _windowDisplay = new();

        public ObservableCollection<TaskbarItem> Shortcuts => _shortcuts;
        public ObservableCollection<TaskbarItem> Windows => _windows;

        // 布局状态
        internal int _shortcutRows = 1;
        internal int _windowRows = 1;
        internal int _totalRows = 1;
        internal bool _isSingleRowLayout = true;

        // 分隔线相关
        internal Border? _dividerElement = null;
        internal FrameworkElement? _dividerContainer = null;
        internal bool _isDividerDragging = false;
        internal double _dividerStartPos;
        internal double _dividerStartSize;
        internal double _dividerTotalSize;

        // 滚动查看器
        private ScrollViewer? _shortcutScrollViewer;
        private ScrollViewer? _windowScrollViewer;

        // 滚动处理器
        private TaskbarScrollHandler? _shortcutScrollHandler;
        private TaskbarScrollHandler? _windowScrollHandler;

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(TaskbarView),
                new PropertyMetadata(28.0, OnIconSizeChanged));

        private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TaskbarView view) view.UpdateLayout();
        }

        public TaskbarView(IShortcutService shortcutService, ISettingsService settings)
            : this(shortcutService, settings, null)
        {
        }

        /// <summary>
        /// 测试/截图用途：可注入窗口数据源，避免读取真实运行窗口。
        /// </summary>
        public TaskbarView(
            IShortcutService shortcutService,
            ISettingsService settings,
            Func<IEnumerable<WindowListProvider.WindowItem>>? windowSource)
        {
            _settings = settings;
            _windowSource = windowSource;
            // ★ 启动卡顿定位用分段计时（面板第一次加载时这几段串在 UI 线程上，实测"第二阶段"里有约 5 秒空白）
            var swInit = System.Diagnostics.Stopwatch.StartNew();
            InitializeComponent();
            long tXaml = swInit.ElapsedMilliseconds;
            DataContext = this;

            VerticalAlignment = VerticalAlignment.Stretch;
            HorizontalAlignment = HorizontalAlignment.Stretch;

            MinHeight = 36;

            IconSize = _settings.TaskbarIconSize;
            _shortcutManager = new TaskbarShortcutManager(shortcutService);
            long tShortcuts = swInit.ElapsedMilliseconds;
            _shortcutManager.ItemsChanged += OnItemsChanged;

            LoadItems();
            long tItems = swInit.ElapsedMilliseconds;
            UpdateLayout();
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                $"[任务栏] 构建耗时 {tItems}ms：XAML {tXaml}ms / 快捷方式管理 {tShortcuts - tXaml}ms / " +
                $"加载条目+窗口图标 {tItems - tShortcuts}ms / UpdateLayout {swInit.ElapsedMilliseconds - tItems}ms");

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
        }

        public void RefreshData() => LoadItems();

        /// <summary>
        /// 菜单入口：关闭所有运行中窗口标签（面板内只能逐个关闭）。
        /// 只关 Window 类型项，不动用户快捷方式。
        /// </summary>
        public void CloseAllWindows()
        {
            var list = _windows.Where(i => i.Type == TaskbarItemType.Window && i.Handle.HasValue).ToList();
            foreach (var item in list)
            {
                WindowAction.Close(item.Handle!.Value);
            }
            Dispatcher.BeginInvoke(new Action(RefreshWindows));
        }

        /// <summary>窗口事件钩子回调（UI 线程）：窗口列表变化时刷新（节流已由钩子合并）。</summary>
        private void OnWindowEventChanged()
        {
            try { RefreshWindows(); } catch { /* 尽力而为：这次刷新失败时列表保持上一次内容，下一次窗口事件会再刷 */ }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeDragDropEvents();

            // ★ 事件驱动 + 轮询兜底：窗口创建/关闭/标题变化由 WindowEventHook 实时通知；
            //   轮询降至 5s 兜底（钩子失败或极端场景），空闲 CPU 占用大幅下降。
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(5);
            _refreshTimer.Tick += (s, args) => RefreshWindows();
            _refreshTimer.Start();

            // ★ 全局窗口事件钩子（进程内共享，只在面板显示时订阅）
            WindowEventHook.Changed += OnWindowEventChanged;
            WindowEventHook.Start();
            UpdateLayout();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_shortcutScrollViewer != null && _shortcutScrollHandler == null)
                {
                    _shortcutScrollHandler = new TaskbarScrollHandler(_shortcutScrollViewer, "快捷方式");
                }
                if (_windowScrollViewer != null && _windowScrollHandler == null)
                {
                    _windowScrollHandler = new TaskbarScrollHandler(_windowScrollViewer, "任务标签");
                }
            }), DispatcherPriority.Loaded);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CloseAllGroupPopups();   // ★ 面板卸载 → 关掉分组弹层并解除保持显示，避免浮层留在屏幕上
            _refreshTimer?.Stop();
            _refreshTimer = null;

            WindowEventHook.Changed -= OnWindowEventChanged;

            _shortcutScrollHandler?.Detach();
            _shortcutScrollHandler = null;
            _windowScrollHandler?.Detach();
            _windowScrollHandler = null;
        }

        private void OnItemsChanged(object? sender, EventArgs e) => LoadItems();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayout();
        }
    }
}
