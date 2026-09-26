// ==================== 剪贴板面板：交互自测（不用人的鼠标） ====================
//
// 盯三件事，全是"只有真的操作一次才知道"的：
//   ① 键盘操作真的接上了（↑↓ 移动光标、Enter 复制）—— 上一轮的 bug 就是"处理器没挂上"；
//   ② 复制走的是宿主服务（不是自己乱设剪贴板），且复制的是**光标那一条**；
//   ③ "只复制纯文本"确实走的是另一条通道（IClipboardService.CopyToClipboardPlainText）。
//
// 用假服务而不是真 ClipboardManager：真实现会**覆盖用户当前剪贴板**，测试不该动用户的东西。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>★ 与另一个交互测试类**串行**跑：WPF 一个进程只能有一个 Application。</summary>
[Collection("WidgetUI")]
public class ClipboardInteractionTests
{
    /// <summary>只记账、不动系统剪贴板的假实现（同时实现 IService 以便注册进 ServiceManager）。</summary>
    private sealed class FakeClipboard : IClipboardService, IService
    {
        public ObservableCollection<ClipboardManager.ClipboardItem> History { get; } = new();
        public event EventHandler? HistoryChanged;
        public string Name => "FakeClipboard";
        public bool IsInitialized => true;
        public void Initialize() { }
        public void Shutdown() { }
        public void StartListening() { }
        public void StopListening() { }
        public void RemoveItem(ClipboardManager.ClipboardItem item) { History.Remove(item); HistoryChanged?.Invoke(this, EventArgs.Empty); }
        public void RemoveItems(IEnumerable<ClipboardManager.ClipboardItem> items) { foreach (var i in items.ToList()) History.Remove(i); }
        public void ClearAll() => History.Clear();
        public bool SaveDroppedFile(string sourcePath, string targetFolder) => false;

        public readonly List<ClipboardManager.ClipboardItem> Copied = new();
        public readonly List<ClipboardManager.ClipboardItem> CopiedPlain = new();
        public void CopyToClipboard(ClipboardManager.ClipboardItem item) => Copied.Add(item);
        public void CopyToClipboardPlainText(ClipboardManager.ClipboardItem item) => CopiedPlain.Add(item);
        public void SetPinned(ClipboardManager.ClipboardItem item, bool pinned) => item.IsPinned = pinned;
    }

    /// <summary>
    /// 把 ServiceManager 单例清掉。
    /// ★ 交互测试是**进程内**跑的，而服务定位器是静态单例：上一个测试注册的 SettingsManager/便签服务
    ///   会留在里面，下一个测试再注册同名服务就被拒 → 拿到的是**已删临时目录**的旧服务。
    ///   这一类"单独跑绿、一起跑红"的假红必须从根上掐掉。
    /// </summary>
    private static void ResetServiceLocator()
    {
        var f = typeof(ServiceManager).GetField("_instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        f?.SetValue(null, null);
    }

    [Fact]
    public void 剪贴板_键盘能选能复制_纯文本走另一条通道_不需要人的鼠标()
    {
        string outcome = UiTestHost.Run(() =>
        {

            string dir = Path.Combine(Path.GetTempPath(), "sh_itest_clip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();          // 键盘开关默认开
                var fake = new FakeClipboard();
                for (int i = 1; i <= 3; i++)
                    fake.History.Add(ClipboardManager.ClipboardItem.FromText("第 " + i + " 条"));
                ServiceManager.Instance.Register(settings, fake);   // HostCapabilities 从服务定位器取

                var (xaml, cs) = UiTestHost.WidgetFiles("clipboard");

                var (widget, err) = WidgetCompiler.CompileXaml("itest_clip", xaml, cs);
                if (widget == null) return "编译失败：" + err;

                var view = widget.CreateView();
                var host = UiTestHost.Realize(view);

                widget.OnActivated();
                view.UpdateLayout();

                var list = UiTestHost.FindVisual<ItemsControl>(view, c => c.Name == "HistoryList");
                if (list == null) return "找不到历史列表";
                // ★ 键盘事件要靠焦点路由：真实使用里点一下列表就把焦点给了它（Focusable=True），
                //   测试里必须显式给焦点，否则事件根本到不了列表（数字键还会被搜索框吃掉）。
                list.Focus();
                Keyboard.Focus(list);
                if (!list.IsKeyboardFocusWithin) FocusManager.SetFocusedElement(view, list);   // 屏幕外窗口可能拿不到键盘焦点

                var src = PresentationSource.FromVisual(view);
                if (src == null) return "控件不在可视树里（拿不到 PresentationSource）";

                void Press(Key k)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.KeyDownEvent };
                    list.RaiseEvent(args);
                }

                // ★ 断言必须按**控件自己的列表顺序**（它会置顶优先、新建在前），不能假设等于 History 顺序
                var shown = (List<ClipboardManager.ClipboardItem>)widget.GetType()
                    .GetField("_currentItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(widget)!;
                if (shown.Count != 3) return "控件列表条数不对：" + shown.Count;

                // ① ↓ 两次 → 光标到列表第 2 条；Enter → 复制的必须就是那一条
                Press(Key.Down);
                Press(Key.Down);
                Press(Key.Enter);
                if (fake.Copied.Count != 1) return "Enter 没有触发复制（键盘处理器没挂上？）";
                if (!ReferenceEquals(fake.Copied[0], shown[1])) return "复制的不是光标那一条";

                // ② 数字键 3 → 直接复制列表第 3 条
                Press(Key.D3);
                if (fake.Copied.Count != 2 || !ReferenceEquals(fake.Copied[1], shown[2]))
                    return "数字键快选没有复制第 3 条";

                // ③ 纯文本通道：直接走控件自己的 Copy(item, plainTextOnly: true)（真实方法，假服务）
                var copyMethod = widget.GetType().GetMethod("Copy",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (copyMethod == null) return "找不到 Copy 方法";
                copyMethod.Invoke(widget, new object?[] { shown[0], true });
                if (fake.CopiedPlain.Count != 1) return "纯文本复制没有走 CopyToClipboardPlainText";

                // ④ 开关关掉后，键盘不再响应（高度自定义：用户能真的关掉）
                settings.ClipboardKeyboardNav = false;
                int before = fake.Copied.Count;
                Press(Key.Down);
                Press(Key.Enter);
                if (fake.Copied.Count != before) return "关掉键盘操作开关后仍然响应了按键";

                host.Close();
                return "OK";
            }
            finally
            {
                UiTestHost.ResetServices();   // 服务定位器是进程级单例：不清会污染下一个交互测试
                AppPaths.TestDataRoot = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Assert.Equal("OK", outcome);
    }
}
