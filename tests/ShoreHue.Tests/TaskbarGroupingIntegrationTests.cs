using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Panels;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>任务栏分组：注入假窗口 + 分组定义 → 真的渲染成一个分组标签。</summary>
public class TaskbarGroupingIntegrationTests
{
    private sealed class FakeShortcuts : ShoreHue.src.core.Services.Shortcuts.IShortcutService
    {
        public System.Collections.ObjectModel.ObservableCollection<ShortcutData> Shortcuts { get; } = new();
        public event EventHandler? ShortcutsChanged { add { } remove { } }
        public bool AddShortcut(string path, string? name = null, string? arguments = null) => false;
        public bool RemoveShortcut(string id) => false;
        public bool RemoveShortcutByPath(string path) => false;
        public void MoveShortcut(int fromIndex, int toIndex) { }
        public void SaveShortcutsOrder() { }
        public void UpdateShortcutName(string id, string name) { }
        public void Reload() { }
        public System.Windows.Media.ImageSource? GetIcon(string path) => null;
    }

    [Fact]
    public void 分组定义_让同组窗口渲染成一个分组标签()
    {
        string outcome = UiTestHost.Run(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sh_tbgroup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();
                settings.TaskbarGroupsJson = TaskbarGroups.Serialize(new List<TaskbarGroupDef>
                {
                    new TaskbarGroupDef
                    {
                        Id = "g1",
                        Members = { new TaskbarGroupMember("app", @"C:\fake\a.exe"), new TaskbarGroupMember("app", @"C:\fake\b.exe") }
                    }
                });

                var view = new TaskbarView(new FakeShortcuts(), settings, () => new[]
                {
                    new WindowListProvider.WindowItem { Handle = new IntPtr(1), Title = "甲", ProcessPath = @"C:\fake\a.exe" },
                    new WindowListProvider.WindowItem { Handle = new IntPtr(2), Title = "乙", ProcessPath = @"C:\fake\b.exe" },
                    new WindowListProvider.WindowItem { Handle = new IntPtr(3), Title = "丙", ProcessPath = @"C:\fake\c.exe" },
                });
                // ★ 必须先布局出真实尺寸：TaskbarView.UpdateLayout 在 ActualWidth/Height<10 时会
                //   Dispatcher.BeginInvoke 重试 —— 不 Realize 就会把 UI 线程转成死循环（测试进程挂住）。
                UiTestHost.Realize(view);
                view.RefreshData();

                var display = view.WindowDisplay;
                if (display.Count != 2) return "期望 1 分组 + 1 普通窗口，实际 " + display.Count;
                if (display[0] is not TaskbarGroupItem g) return "第一项不是分组标签";
                if (g.Preview.Count != 2) return "拼版图标数量不对：" + g.Preview.Count;
                if (g.WindowCount != 2) return "成员数角标不对：" + g.WindowCount;
                if (display[1] is not TaskbarItem) return "第二项不是普通窗口标签";

                // ★ 内容没变时不得重建显示列表：否则每 5 秒的窗口刷新会把鼠标正悬停的分组标签销毁，
                //   连带把展开的弹层关掉（用户报的"分组没法展开"就是这个）。
                var first = view.WindowDisplay[0];
                view.RefreshData();
                if (!ReferenceEquals(first, view.WindowDisplay[0])) return "内容没变却重建了分组标签（悬停会被打断）";
                return "OK";
            }
            finally
            {
                AppPaths.TestDataRoot = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        });
        Assert.Equal("OK", outcome);
    }

    [Fact]
    public void 分组标签_真的渲染出角标_且悬停能展开()
    {
        string outcome = UiTestHost.Run(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sh_tbgroup2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();
                settings.TaskbarGroupsJson = TaskbarGroups.Serialize(new List<TaskbarGroupDef>
                {
                    new TaskbarGroupDef
                    {
                        Id = "g1",
                        Members = { new TaskbarGroupMember("app", @"C:\fake\a.exe"), new TaskbarGroupMember("app", @"C:\fake\b.exe") }
                    }
                });
                var view = new TaskbarView(new FakeShortcuts(), settings, () => new[]
                {
                    new WindowListProvider.WindowItem { Handle = new IntPtr(1), Title = "甲", ProcessPath = @"C:\fake\a.exe" },
                    new WindowListProvider.WindowItem { Handle = new IntPtr(2), Title = "乙", ProcessPath = @"C:\fake\b.exe" },
                });
                UiTestHost.Realize(view);
                view.RefreshData();
                view.UpdateLayout();

                var g = view.WindowDisplay.OfType<TaskbarGroupItem>().FirstOrDefault();
                if (g == null) return "没有分组标签";
                if (!view.Resources.Contains("GroupTemplate")) return "资源字典里没有 GroupTemplate";
                var ic = UiTestHost.FindVisual<ItemsControl>(view, c => c.ItemTemplateSelector is TaskbarTemplateSelector);
                if (ic == null) return "找不到带选择器的窗口 ItemsControl";
                var sel = (TaskbarTemplateSelector)ic.ItemTemplateSelector!;
                if (sel.GroupTemplate == null) return "选择器的 GroupTemplate 为 null（资源没取到）";
                if (sel.WindowTemplate == null) return "选择器的 WindowTemplate 为 null";
                var badge = UiTestHost.FindVisual<TextBlock>(view, t => t.Text == g.WindowCount.ToString());
                if (badge == null)
                {
                    var cp = UiTestHost.FindVisual<ContentPresenter>(view, c => ReferenceEquals(c.Content, g));
                    if (cp == null) return "找不到分组的 ContentPresenter";
                    string tplName = ReferenceEquals(cp.ContentTemplate, sel.GroupTemplate) ? "GroupTemplate"
                        : ReferenceEquals(cp.ContentTemplate, sel.WindowTemplate) ? "WindowTemplate" : (cp.ContentTemplate?.GetHashCode().ToString() ?? "null");
                    var texts = new List<string>();
                    void Walk(DependencyObject n)
                    {
                        if (n is TextBlock tb) texts.Add(tb.Text ?? "");
                        int cnt = System.Windows.Media.VisualTreeHelper.GetChildrenCount(n);
                        for (int i = 0; i < cnt; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(n, i));
                    }
                    Walk(cp);
                    return "角标未渲染；容器模板=" + tplName + "；容器下文本=[" + string.Join(",", texts) + "]";
                }
                var border = UiTestHost.FindVisual<Border>(view, b => ReferenceEquals(b.DataContext, g));
                if (border == null) return "找不到分组标签根元素";

                // ③ 展开/收起必须通知宿主"保持显示"（弹层是独立窗口，否则鼠标移上去面板会自己收起）
                bool overlay = false;
                Action<bool> onOverlay = v => overlay = v;
                TaskbarView.GroupOverlayVisibilityChanged += onOverlay;
                try
                {
                    border.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                    if (!view.GroupPopupsForTest.TryGetValue(g.Id, out var popup) || popup == null) return "悬停没有建立弹层";
                    if (!popup.IsOpen) return "悬停没有打开弹层";
                    if (!overlay) return "展开时没有通知宿主保持显示（面板会自己收起）";
                    view.CloseAllGroupPopups();
                    if (overlay) return "收起后没有解除保持显示";
                    return "OK";
                }
                finally { TaskbarView.GroupOverlayVisibilityChanged -= onOverlay; }
            }
            finally
            {
                AppPaths.TestDataRoot = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        });
        Assert.Equal("OK", outcome);
    }
}