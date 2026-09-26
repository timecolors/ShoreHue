using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShoreHue.UI.Panels
{
    /// <summary>任务栏标签分组：显示整理、悬停展开、解散、持久化（成员当用户设置保存）。</summary>
    public partial class TaskbarView
    {
        /// <summary>按应用（exe 路径）记的分组定义 —— 窗口句柄跨重启会变，只有应用路径稳定。</summary>
        private readonly List<TaskbarGroupDef> _groupDefs = new();
        private readonly Dictionary<string, Popup> _groupPopups = new();
        private readonly Dictionary<string, DispatcherTimer> _groupCloseTimers = new();

        /// <summary>分组弹层展开/收起时通知宿主（true=展开）：弹层是独立窗口，展开期间面板不能自动收起。</summary>
        public static event Action<bool>? GroupOverlayVisibilityChanged;

        private static void RaiseGroupOverlay(bool visible)
        {
            try { GroupOverlayVisibilityChanged?.Invoke(visible); } catch { /* 订阅方异常不能拖垮任务栏 */ }
        }

        /// <summary>关闭所有分组弹层并解除"保持显示"（面板卸载/解散分组时调用，避免浮层留在屏幕上）。</summary>
        internal void CloseAllGroupPopups()
        {
            bool any = false;
            foreach (var p in _groupPopups.Values) { if (p.IsOpen) any = true; p.IsOpen = false; }
            _groupPopups.Clear();
            foreach (var t in _groupCloseTimers.Values) t.Stop();
            if (any) RaiseGroupOverlay(false);
        }

        private void ReloadGroupDefs()
        {
            _groupDefs.Clear();
            _groupDefs.AddRange(TaskbarGroups.Deserialize(_settings?.TaskbarGroupsJson));
            TaskbarGroups.PruneEmpty(_groupDefs);
        }

        private void SaveGroupDefs()
        {
            if (_settings == null) return;
            _settings.TaskbarGroupsJson = TaskbarGroups.Serialize(_groupDefs);
        }

        private static string AppKeyOf(TaskbarItem w) => w.Path ?? "";

        /// <summary>供测试/探针读取当前的分组显示列表（窗口标签或分组标签）。</summary>
        internal IReadOnlyList<object> WindowDisplay => _windowDisplay;

        /// <summary>供测试读取分组弹层（验证悬停展开真的建了 Popup 并打开）。</summary>
        internal IReadOnlyDictionary<string, Popup> GroupPopupsForTest => _groupPopups;

        private string _windowDisplaySig = "";

        /// <summary>把 _windows 按分组定义整理成显示列表（分组标签 + 未分组窗口）。
        /// 少于 2 个窗口的组不显示（成员回到普通标签），分组定义本身仍持久化。
        /// ★ 只在显示内容真的变化时才重建集合：否则每 5 秒的窗口刷新会把标签容器整体重建，
        ///   鼠标正悬停的分组标签被销毁 → 展开的弹层被连带关掉（"分组没法展开"的根因）。</summary>
        private void RebuildWindowDisplay()
        {
            var next = new List<object>();

            if (_groupDefs.Count == 0)
            {
                next.AddRange(_windows);
            }
            else
            {
                var byApp = new Dictionary<string, List<TaskbarItem>>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in _windows)
                {
                    string k = AppKeyOf(w);
                    if (k.Length == 0) continue;
                    if (!byApp.TryGetValue(k, out var list)) byApp[k] = list = new List<TaskbarItem>();
                    list.Add(w);
                }

                var childIds = new HashSet<string>(_groupDefs.SelectMany(d => d.Members)
                    .Where(m => m.Kind == "group").Select(m => m.Key), StringComparer.Ordinal);

                List<object> Resolve(TaskbarGroupDef def, HashSet<string> visiting, out List<TaskbarItem> preview)
                {
                    preview = new List<TaskbarItem>();
                    var members = new List<object>();
                    if (!visiting.Add(def.Id)) return members;
                    foreach (var m in def.Members)
                    {
                        if (m.Kind == "app")
                        {
                            if (byApp.TryGetValue(m.Key, out var list))
                                foreach (var w in list) { members.Add(w); preview.Add(w); }
                        }
                        else
                        {
                            var child = TaskbarGroups.Find(_groupDefs, m.Key);
                            if (child == null) continue;
                            var cm = Resolve(child, visiting, out var cp);
                            if (cm.Count > 0)
                            {
                                var sub = new TaskbarGroupItem
                                {
                                    Id = child.Id, DisplayName = child.Name, Members = cm,
                                    Preview = cp.Take(4).ToList(), IsNested = cm.Any(x => x is TaskbarGroupItem)
                                };
                                sub.WindowCount = CountWindows(cm);
                                members.Add(sub);
                            }
                            preview.AddRange(cp);
                        }
                    }
                    return members;
                }

                var grouped = new HashSet<TaskbarItem>();
                foreach (var def in _groupDefs.Where(d => !childIds.Contains(d.Id)))
                {
                    var members = Resolve(def, new HashSet<string>(StringComparer.Ordinal), out var preview);
                    int wc = CountWindows(members);
                    if (wc < 2) continue;
                    MarkWindows(members, grouped);
                    next.Add(new TaskbarGroupItem
                    {
                        Id = def.Id, DisplayName = def.Name, Members = members, WindowCount = wc,
                        Preview = preview.Take(4).ToList(), IsNested = members.Any(m => m is TaskbarGroupItem)
                    });
                }
                foreach (var w in _windows) if (!grouped.Contains(w)) next.Add(w);
            }

            string sig = BuildDisplaySignature(next);
            if (sig == _windowDisplaySig) return;   // 内容没变 → 不重建（保住悬停中的标签与弹层）
            _windowDisplaySig = sig;
            _windowDisplay.Clear();
            foreach (var it in next) _windowDisplay.Add(it);
        }

        private static string BuildDisplaySignature(List<object> items)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var it in items)
            {
                if (it is TaskbarGroupItem g)
                    sb.Append("G:").Append(g.Id).Append(':').Append(g.DisplayName).Append(':')
                      .Append(string.Join(",", g.Preview.Select(p => (p.Handle?.ToInt64() ?? 0) + "|" + p.DisplayName))).Append(';');
                else if (it is TaskbarItem t)
                    sb.Append("W:").Append(t.Handle?.ToInt64() ?? 0).Append(':').Append(t.DisplayName).Append(';');
            }
            return sb.ToString();
        }

        private static int CountWindows(List<object> members)
        {
            int n = 0;
            foreach (var m in members)
            {
                if (m is TaskbarItem) n++;
                else if (m is TaskbarGroupItem g) n += CountWindows(g.Members);
            }
            return n;
        }

        private static void MarkWindows(List<object> members, HashSet<TaskbarItem> into)
        {
            foreach (var m in members)
            {
                if (m is TaskbarItem t) into.Add(t);
                else if (m is TaskbarGroupItem g) MarkWindows(g.Members, into);
            }
        }

        // ================= 悬停展开 / 移走收起 =================

        private void Group_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not TaskbarGroupItem g) return;
            if (_groupCloseTimers.TryGetValue(g.Id, out var t)) t.Stop();
            GetOrCreateGroupPopup(g, fe).IsOpen = true;
            RaiseGroupOverlay(true);   // ★ 展开期间别让面板自动收
        }

        private void Group_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not TaskbarGroupItem g) return;
            StartGroupCloseTimer(g.Id);
        }

        private void StartGroupCloseTimer(string id)
        {
            if (!_groupCloseTimers.TryGetValue(id, out var t))
            {
                t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    if (_groupPopups.TryGetValue(id, out var p) && p.IsOpen) { p.IsOpen = false; RaiseGroupOverlay(false); }
                };
                _groupCloseTimers[id] = t;
            }
            t.Stop();
            t.Start();
        }

        private Popup GetOrCreateGroupPopup(TaskbarGroupItem g, FrameworkElement anchor)
        {
            if (_groupPopups.TryGetValue(g.Id, out var cached)) { cached.PlacementTarget = anchor; return cached; }

            var panel = new StackPanel { Margin = new Thickness(2) };
            if (g.IsNested)
                panel.Children.Add(new TextBlock
                {
                    Text = "我预感你要做一件不得了的事，不可以哦~",
                    FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                    Margin = new Thickness(2, 0, 2, 6)
                });
            AddGroupMemberRows(panel, g);

            var ungroup = new Button
            {
                Content = "解散分组", FontSize = 11, Margin = new Thickness(2, 6, 2, 0),
                Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand,
                Style = TryFindResource("FlatButton") as Style
            };
            ungroup.Click += (_, _) => UngroupGroup(g.Id);
            panel.Children.Add(ungroup);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6), Child = panel, MaxWidth = 320
            };
            border.MouseEnter += (_, _) => { if (_groupCloseTimers.TryGetValue(g.Id, out var t)) t.Stop(); };
            border.MouseLeave += (_, _) => StartGroupCloseTimer(g.Id);

            var popup = new Popup
            {
                Child = border, AllowsTransparency = true, StaysOpen = true,
                // ★ 跟随鼠标：任务栏可能在任意边，写死 Top/Bottom 会跑到屏幕外看不见
                Placement = PlacementMode.Mouse, PlacementTarget = anchor,
                HorizontalOffset = 10, VerticalOffset = 10, PopupAnimation = PopupAnimation.Fade
            };
            _groupPopups[g.Id] = popup;
            return popup;
        }

        private void AddGroupMemberRows(Panel panel, TaskbarGroupItem g)
        {
            foreach (var m in g.Members)
            {
                if (m is TaskbarItem w) AddWindowRow(panel, w);
                else if (m is TaskbarGroupItem child)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "▸ " + (string.IsNullOrEmpty(child.DisplayName) ? "分组" : child.DisplayName),
                        FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 2, 2),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x88, 0xD8))
                    });
                    AddGroupMemberRows(panel, child);
                }
            }
        }

        private void AddWindowRow(Panel panel, TaskbarItem w)
        {
            var row = new Grid { Margin = new Thickness(2, 1, 2, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Image { Source = w.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = w.DisplayName, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 200 };
            var close = new Button { Content = "✕", Width = 18, Height = 18, FontSize = 9, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), Cursor = Cursors.Hand, ToolTip = "关闭这个窗口" };
            close.Click += (_, _) => { if (w.Handle.HasValue) ShoreHue.Infrastructure.WinApi.WindowAction.Close(w.Handle.Value); Dispatcher.BeginInvoke(new Action(RefreshWindows)); };
            row.Children.Add(icon);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            Grid.SetColumn(close, 2); row.Children.Add(close);
            var hit = new Border { Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 1, 2, 1), CornerRadius = new CornerRadius(4) };
            // ★ 与普通窗口标签一致：点已在前台的那个 → 最小化（收起）；点是收起的 → 还原并前置。
            hit.MouseLeftButtonUp += (_, _) => { if (w.Handle.HasValue) ShoreHue.Infrastructure.WinApi.WindowAction.ToggleMinimize(w.Handle.Value); };
            panel.Children.Add(hit);
        }

        /// <summary>解散分组：删定义 → 落盘 → 重排。</summary>
        private void UngroupGroup(string groupId)
        {
            if (_groupPopups.TryGetValue(groupId, out var p)) { p.IsOpen = false; _groupPopups.Remove(groupId); RaiseGroupOverlay(false); }
            TaskbarGroups.Ungroup(_groupDefs, groupId);
            SaveGroupDefs();
            RebuildWindowDisplay();
            UpdateLayout();
        }

        private void Group_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not TaskbarGroupItem g) return;
            UngroupGroup(g.Id);
            e.Handled = true;
        }
    }
}