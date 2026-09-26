// ==================== 通知坞面板：交互自测（不用人的鼠标） ====================
//
// 盯三件事，全是"只有真的点一下才知道"的：
//   ① 按应用分组真的画出来了（组头 + 组内条数）；
//   ② 点某条的 ✕ 只关那一条（不误伤同应用的其它条，也不误伤别的应用）；
//   ③ 关掉 / 清空之后，标题里的计数跟着变。
//
// ★ 面板的 Refresh() 读的是宿主静态集合（HostCapabilities.Notifications），本来"不可注入" ——
//   但 ToastMonitor.Notifications 是公开的，测试直接喂假数据即可，不需要人的鼠标。

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShoreHue.Builtin.Notifications;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>★ 与其它交互测试**串行**跑：WPF 一个进程只能有一个 Application。</summary>
[Collection("WidgetUI")]
public class NotificationDockInteractionTests
{
    private static ToastNotificationItem Item(string app, string msg)
        => new() { Id = Guid.NewGuid().ToString("N"), AppName = app, Message = msg, Time = DateTime.Now };

    [Fact]
    public void 通知坞_按应用分组_点叉只关那一条_清空后计数归零_不需要人的鼠标()
    {
        string outcome = UiTestHost.Run(() =>
        {
            ToastMonitor.Notifications.Clear();
            try
            {
                string src = BuiltinFeatureSources.Sources["panel-notification"];
                var (widget, err) = WidgetCompiler.Compile("itest_notification", src);
                if (widget == null) return "编译失败：" + err;

                var wechat1 = Item("微信", "张三：在吗");
                var wechat2 = Item("微信", "李四：收到");
                var system1 = Item("系统", "有可用更新");
                // 宿主顺序：新的在前（与 ToastMonitor 的插入方式一致）
                ToastMonitor.Notifications.Add(wechat2);
                ToastMonitor.Notifications.Add(wechat1);
                ToastMonitor.Notifications.Add(system1);

                var view = widget.CreateView();
                UiTestHost.Realize(view);
                widget.OnActivated();
                view.UpdateLayout();

                // ① 分组画出来了
                if (UiTestHost.FindVisual<TextBlock>(view, t => t.Text == "微信（2）") == null)
                    return "没有『微信（2）』分组头（分组没接线？）";
                if (UiTestHost.FindVisual<TextBlock>(view, t => t.Text == "系统（1）") == null)
                    return "没有『系统（1）』分组头";
                var title = UiTestHost.FindVisual<TextBlock>(view, t => t.Text != null && t.Text.StartsWith("通知坞（", StringComparison.Ordinal));
                if (title == null || title.Text != "通知坞（3）") return "标题计数不对：" + (title?.Text ?? "找不到标题");

                // ② ✕ 只关那一条
                var close = UiTestHost.FindVisual<Button>(view, b => (b.Tag as string) == wechat1.Id);
                if (close == null) return "找不到 wechat1 的 ✕（处理器没挂上？）";
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                if (ToastMonitor.Notifications.Contains(wechat1)) return "点 ✕ 没有关掉那一条";
                if (!ToastMonitor.Notifications.Contains(wechat2)) return "点 ✕ 误删了同应用的其它条";
                if (!ToastMonitor.Notifications.Contains(system1)) return "点 ✕ 误删了别的应用";
                if (ToastMonitor.Notifications.Count != 2) return "关闭后宿主数量不对：" + ToastMonitor.Notifications.Count;

                view.UpdateLayout();
                if (UiTestHost.FindVisual<TextBlock>(view, t => t.Text == "微信（1）") == null)
                    return "关闭后分组计数没更新";
                if (title.Text != "通知坞（2）") return "关闭后标题计数不对：" + title.Text;

                // ③ 清空 → 全部消失、标题空态
                var clear = UiTestHost.FindVisual<Button>(view, b => (b.Content as string) == "清空");
                if (clear == null) return "找不到『清空』按钮";
                clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (ToastMonitor.Notifications.Count != 0) return "清空后宿主还有：" + ToastMonitor.Notifications.Count;
                if (title.Text != "通知坞（空）") return "清空后标题不对：" + title.Text;

                return "OK";
            }
            finally
            {
                ToastMonitor.Notifications.Clear();
            }
        });

        Assert.Equal("OK", outcome);
    }

    [Fact]
    public void 通知坞宿主视图_同样按应用分组_点叉只关那一条()
    {
        string outcome = UiTestHost.Run(() =>
        {
            ToastMonitor.Notifications.Clear();
            try
            {
                var wechat1 = Item("微信", "张三：在吗");
                var wechat2 = Item("微信", "李四：收到");
                var system1 = Item("系统", "有可用更新");
                ToastMonitor.Notifications.Add(wechat2);
                ToastMonitor.Notifications.Add(wechat1);
                ToastMonitor.Notifications.Add(system1);

                var view = new NotificationDockView();
                UiTestHost.Realize(view);
                view.UpdateLayout();

                if (UiTestHost.FindVisual<TextBlock>(view, t => t.Text == "微信（2）") == null)
                    return "宿主视图没有『微信（2）』分组头";
                if (UiTestHost.FindVisual<TextBlock>(view, t => t.Text == "系统（1）") == null)
                    return "宿主视图没有『系统（1）』分组头";

                var close = UiTestHost.FindVisual<Button>(view, b => b.DataContext is NotificationRow r && r.Key == wechat1.Id);
                if (close == null) return "宿主视图找不到 wechat1 的 ✕";
                close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                if (ToastMonitor.Notifications.Contains(wechat1)) return "宿主视图点 ✕ 没关掉那一条";
                if (!ToastMonitor.Notifications.Contains(wechat2)) return "宿主视图点 ✕ 误删了同应用其它条";
                if (!ToastMonitor.Notifications.Contains(system1)) return "宿主视图点 ✕ 误删了别的应用";
                if (ToastMonitor.Notifications.Count != 2) return "宿主视图关闭后数量不对：" + ToastMonitor.Notifications.Count;
                return "OK";
            }
            finally
            {
                ToastMonitor.Notifications.Clear();
            }
        });

        Assert.Equal("OK", outcome);
    }
}
