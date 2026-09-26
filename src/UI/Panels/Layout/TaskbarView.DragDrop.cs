using ShoreHue.Core.Services;
using ShoreHue.Infrastructure.WinApi;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShoreHue.UI.Panels
{
    public partial class TaskbarView
    {
        // ★★★ 在 TaskbarView.xaml.cs 的 OnLoaded 中调用此方法 ★★★
        private void InitializeDragDropEvents()
        {
            MainGrid.PreviewMouseLeftButtonDown += OnMainGridMouseDown;
            MainGrid.PreviewMouseMove += OnMainGridMouseMove;
            MainGrid.PreviewMouseLeftButtonUp += OnMainGridMouseUp;
            MainGrid.DragOver += OnMainGridDragOver;
            MainGrid.Drop += OnMainGridDrop;
        }

        // 状态变量（object：窗口标签 TaskbarItem / 分组标签 TaskbarGroupItem）
        private object? _pendingItem = null;
        private bool _isDragging = false;
        private Point _dragStartPoint;
        private object? _pendingClickItem = null;

        /// <summary>分组标签拖拽用的数据格式（与 TaskbarItem 的 Format 分开，避免影响拖到左侧竖条固定应用）。</summary>
        private const string GroupDataFormat = "ShoreHueTaskbarGroup";
        /// <summary>当前拖拽落在哪个标签上（用于两侧排序 / 中间成组的分区判定）。</summary>
        private object? _dragHoverTarget = null;
        /// <summary>落点是否在目标标签的**中间三分之一**（= 成组；两侧三分之一 = 排序）。</summary>
        private bool _dragOverGroupZone = false;

        private void OnMainGridMouseDown(object sender, MouseButtonEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest(MainGrid, e.GetPosition(MainGrid));
            if (hit == null) return;

            // 点按发生在按钮内部（如窗口关闭按钮）时不启动拖拽
            if (IsInsideButton(hit.VisualHit)) return;

            var dep = hit.VisualHit;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && (fe.DataContext is TaskbarItem || fe.DataContext is TaskbarGroupItem))
                {
                    _pendingItem = fe.DataContext;
                    _dragStartPoint = e.GetPosition(MainGrid);
                    _isDragging = false;
                    _pendingClickItem = fe.DataContext;
                    MainGrid.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }
        }

        private void OnMainGridMouseMove(object sender, MouseEventArgs e)
        {
            if (_pendingItem == null) return;

            var currentPos = e.GetPosition(MainGrid);
            double dx = currentPos.X - _dragStartPoint.X;
            double dy = currentPos.Y - _dragStartPoint.Y;

            if (!_isDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
            {
                _isDragging = true;
                _pendingClickItem = null;

                // ★★★ 启动系统拖拽流程 ★★★
                // 这样 ShoreHue 图标的 DragEnter/DragLeave/Drop 事件会被触发
                var data = _pendingItem is TaskbarItem ti
                    ? new DataObject(typeof(TaskbarItem), ti)
                    : new DataObject(GroupDataFormat, _pendingItem);
                // ★ 允许 Copy|Move：任务栏内排序用 Move，拖到左侧图标固定应用用 Copy
                DragDrop.DoDragDrop(MainGrid, data, DragDropEffects.Copy | DragDropEffects.Move);

                // 拖拽结束后，清理状态
                EndDrag();
                e.Handled = true;
                return;
            }

            // ★★★ 移除直接删除逻辑，删除由 MainWindow.DragDrop.cs 的 IconText_Drop 处理 ★★★
        }

        private void OnMainGridMouseUp(object sender, MouseButtonEventArgs e)
        {
            bool handled = false;
            if (_pendingClickItem != null && !_isDragging)
            {
                ExecuteClick(_pendingClickItem);
                handled = true;
            }
            EndDrag();
            // 只有确实处理了点击/拖拽时才吞掉事件，避免挡住关闭按钮等内部控件
            if (handled) e.Handled = true;
        }

        private void OnMainGridDragOver(object sender, DragEventArgs e)
        {
            bool carrying = e.Data.GetDataPresent(typeof(TaskbarItem)) || e.Data.GetDataPresent(GroupDataFormat);
            e.Effects = carrying ? DragDropEffects.Move : DragDropEffects.None;
            if (carrying)
            {
                // ★ 落点分区：目标标签的**中间三分之一** = 成组；两侧三分之一 = 排序（插到两者之间）
                var (data, element) = FindTaskbarElementAt(e.GetPosition(MainGrid));
                if (element == null) (data, element) = FindNearestTaskbarElement(e.GetPosition(MainGrid));   // 缝隙 → 最近标签（排序）
                _dragHoverTarget = data;
                _dragOverGroupZone = element != null && IsInMiddleThird(element, e.GetPosition(MainGrid));
            }
            e.Handled = true;
        }

        private void OnMainGridDrop(object sender, DragEventArgs e)
        {
            try
            {
                bool isItem = e.Data.GetDataPresent(typeof(TaskbarItem));
                bool isGroup = e.Data.GetDataPresent(GroupDataFormat);
                if (!isItem && !isGroup) return;
                object? dragged = isItem ? e.Data.GetData(typeof(TaskbarItem)) : e.Data.GetData(GroupDataFormat);
                if (dragged == null) return;

                object? target = FindTaskbarElementAt(e.GetPosition(MainGrid)).Data
                    ?? FindNearestTaskbarElement(e.GetPosition(MainGrid)).Data;
                if (target == null || ReferenceEquals(target, dragged)) return;

                // ★ 中间三分之一 = 成组；两侧三分之一 = 排序
                bool wantGroup = _dragOverGroupZone && ReferenceEquals(target, _dragHoverTarget);
                if (wantGroup && TryGroupByDrag(dragged, target)) { e.Handled = true; return; }

                if (dragged is not TaskbarItem d || target is not TaskbarItem t) { e.Handled = true; return; }
                if (d.Type == TaskbarItemType.Window && t.Type == TaskbarItemType.Window)
                {
                    int from = _windows.IndexOf(d);
                    int to = _windows.IndexOf(t);
                    if (from >= 0 && to >= 0 && from != to)
                    {
                        _windows.Move(from, to);
                        // ★ 列表现在绑的是 _windowDisplay（可能含分组标签），改 _windows 不会自动反映 ——
                        //   不重建就是「拖了没反应，过几秒才动」（用户报的排序延迟）。
                        RebuildWindowDisplay();
                        UpdateLayout();
                    }
                }
                else if (d.Type == TaskbarItemType.Shortcut)
                {
                    var shortcutItems = _shortcuts.Where(i => i.Type == TaskbarItemType.Shortcut).ToList();
                    int from = shortcutItems.IndexOf(d);
                    int to = t.Type == TaskbarItemType.Shortcut
                        ? shortcutItems.IndexOf(t)
                        : ComputeShortcutInsertIndex(t);
                    if (from >= 0 && to >= 0 && from != to)
                    {
                        // 排序由 ShortcutManager 持久化，并通过 ShortcutsChanged 自动刷新视图
                        _shortcutManager.MoveShortcut(from, to);
                    }
                }
                e.Handled = true;
            }
            finally
            {
                _dragHoverTarget = null;
                _dragOverGroupZone = false;
            }
        }

        /// <summary>拖拽成组：把两个标签并进一个分组（潮池式嵌套规则见 TaskbarGroups）。</summary>
        private bool TryGroupByDrag(object dragged, object target)
        {
            var a = DragKeyOf(dragged);
            var b = DragKeyOf(target);
            if (a == null || b == null) return false;
            if (a.Value.Key.Length == 0 || b.Value.Key.Length == 0) return false;
            string? err = TaskbarGroups.Group(_groupDefs, a.Value.Kind, a.Value.Key, b.Value.Kind, b.Value.Key, out _);
            if (err != null)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[任务栏] 成组被拒：{err}");
                return false;
            }
            SaveGroupDefs();
            RebuildWindowDisplay();
            UpdateLayout();
            ShoreHue.Core.Infrastructure.Logging.LogManager.Info("[任务栏] 已创建/更新分组（当用户设置保存）");
            return true;
        }

        private static (string Kind, string Key)? DragKeyOf(object o) => o switch
        {
            TaskbarGroupItem g => ("group", g.Id),
            TaskbarItem t when t.Type == TaskbarItemType.Window => ("app", t.Path ?? ""),
            _ => null,
        };

        /// <summary>
        /// 目标不是快捷方式时（如窗口项），计算“插入到该位置之前”的快捷方式索引。
        /// </summary>
        private int ComputeShortcutInsertIndex(TaskbarItem target)
        {
            int allIndex = _shortcuts.IndexOf(target);
            if (allIndex < 0) return -1;

            int shortcutIndex = 0;
            for (int i = 0; i < allIndex && i < _shortcuts.Count; i++)
            {
                if (_shortcuts[i].Type == TaskbarItemType.Shortcut) shortcutIndex++;
            }
            return shortcutIndex;
        }

        private void ExecuteClick(object? data)
        {
            if (data is TaskbarGroupItem) return;   // 分组：悬停即展开；解散走右键
            if (data is not TaskbarItem item) return;

            if (item.Type == TaskbarItemType.Window && item.Handle.HasValue)
            {
                WindowAction.ToggleMinimize(item.Handle.Value);
                return;
            }

            if (item.Type == TaskbarItemType.Shortcut)
            {
                if (!string.IsNullOrEmpty(item.Path))
                {
                    RecentAppTracker.RecordLaunch(item.Path);
                }
                if (item.IsRunning && item.Handle.HasValue)
                {
                    WindowAction.SwitchTo(item.Handle.Value);
                    return;
                }
                if (string.IsNullOrEmpty(item.Path)) return;

                try
                {
                    string path = item.Path;
                    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        string target = ShortcutLinkResolver.Resolve(path);
                        if (!string.IsNullOrEmpty(target)) path = target;
                    }
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.Path,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { /* 尽力而为：拖进来的东西打不开（已失效的快捷方式/无关联程序）—— 上面"打开拖入项"那个分支会记日志 */ }
                }
            }
        }

        /// <summary>命中测试：返回指针下的标签数据 + 它的可视元素（用于判断落在左/中/右哪一段）。</summary>
        private (object? Data, FrameworkElement? Element) FindTaskbarElementAt(Point position)
        {
            var hitResult = VisualTreeHelper.HitTest(MainGrid, position);
            if (hitResult == null) return (null, null);

            object? data = null;
            FrameworkElement? root = null;
            var dep = hitResult.VisualHit;
            while (dep != null)
            {
                if (dep is FrameworkElement fe && (fe.DataContext is TaskbarItem || fe.DataContext is TaskbarGroupItem))
                {
                    if (data == null) data = fe.DataContext;
                    // ★ 继续上溯取**最外层**（整个标签槽），否则中间三分之一是按图标算的，落点会偏
                    if (ReferenceEquals(fe.DataContext, data)) root = fe;
                }
                dep = VisualTreeHelper.GetParent(dep);
            }
            return (data, root);
        }

        /// <summary>指针落在标签之间的缝隙里时，取最近的标签作为排序目标。</summary>
        private (object? Data, FrameworkElement? Element) FindNearestTaskbarElement(Point position)
        {
            (object? Data, FrameworkElement? Element) best = (null, null);
            double bestD = double.MaxValue;
            void Walk(DependencyObject node)
            {
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    var child = VisualTreeHelper.GetChild(node, i);
                    if (child is FrameworkElement fe && (fe.DataContext is TaskbarItem || fe.DataContext is TaskbarGroupItem))
                    {
                        try
                        {
                            var rect = fe.TransformToAncestor(MainGrid).TransformBounds(new Rect(fe.RenderSize));
                            double dx = Math.Max(rect.X - position.X, Math.Max(0, position.X - (rect.X + rect.Width)));
                            double dy = Math.Max(rect.Y - position.Y, Math.Max(0, position.Y - (rect.Y + rect.Height)));
                            double d = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = (fe.DataContext, fe); }
                        }
                        catch { /* 未布局的元素取不到坐标，跳过 */ }
                    }
                    Walk(child);
                }
            }
            Walk(MainGrid);
            return best;
        }

        /// <summary>指针是否落在该标签的中间三分之一（水平布局看 X，垂直布局看 Y）。</summary>
        private bool IsInMiddleThird(FrameworkElement element, Point positionInMainGrid)
        {
            try
            {
                var rect = element.TransformToAncestor(MainGrid).TransformBounds(new Rect(element.RenderSize));
                if (rect.Width <= 0 || rect.Height <= 0) return false;
                double rel = IsHorizontalLayout(_currentLayoutMode)
                    ? (positionInMainGrid.X - rect.X) / rect.Width
                    : (positionInMainGrid.Y - rect.Y) / rect.Height;
                return rel >= 1.0 / 3 && rel <= 2.0 / 3;
            }
            catch { return false; }
        }

        internal static bool IsInsideButton(DependencyObject? visual)
        {
            var dep = visual;
            while (dep != null)
            {
                if (dep is Button) return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        private void EndDrag()
        {
            _pendingItem = null;
            _isDragging = false;
            _pendingClickItem = null;
            _dragHoverTarget = null;
            _dragOverGroupZone = false;

            try
            {
                if (MainGrid.IsMouseCaptured)
                    MainGrid.ReleaseMouseCapture();
                if (Mouse.Captured != null)
                    Mouse.Capture(null);
            }
            catch { /* 尽力而为：捕获没释放最多影响当前这一次拖拽（随后 Mouse.OverrideCursor 会复位） */ }
            Mouse.OverrideCursor = null;
        }
    }
}
