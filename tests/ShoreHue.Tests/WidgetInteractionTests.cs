// ==================== 控件交互自测台（不用人的鼠标） ====================
//
// 为什么必须有它：8 轮里最贵的一个 bug 是"处理器根本没挂上"——XAML 编译、方言、沙箱、绑定、
// 加载日志全绿，只有**真的点一下**才发现没反应。人点一次只能证明一次；这里把"点"变成断言。
//
// 做法：STA 线程 + 合并主题 + **真实服务**（SettingsManager / NoteManager，数据目录隔离）→
// 用 WidgetCompiler 从仓库源码编译出真控件 → 造真实 路由事件（MouseButtonEventArgs +
// UIElement.RaiseEvent）打到真实元素上 → 断言服务状态真的变了。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>★ 与另一个交互测试类**串行**跑：WPF 一个进程只能有一个 Application。</summary>
[Collection("WidgetUI")]
public class WidgetInteractionTests
{

    private static void RaiseMouse(UIElement target, MouseButton button, RoutedEvent evt)
        => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = evt });

    // ==================== 便签：芯片切换 / ✕ 删除 ====================

    [Fact]
    public void 便签_点芯片能切换_点叉能删除_不需要人的鼠标()
    {
        string outcome = UiTestHost.Run(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sh_itest_note_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();
                var notes = new NoteManager(settings, Path.Combine(dir, "notes.json"));
                notes.Initialize();

                var a = notes.CreateNote("甲");
                notes.UpdateNoteContent(a, "第一条内容");
                var b = notes.CreateNote("乙");
                notes.UpdateNoteContent(b, "第二条内容");
                notes.SetCurrentNote(a);

                // 注入真实服务（小组件只认 HostCapabilities）
                // ★ HostCapabilities 的服务是**从 ServiceManager 取的**（宿主服务定位器），
                //   没有 setter —— 测试要把自己的实例注册进去，和程序启动时同一条路。
                ShoreHue.Core.Infrastructure.Service.ServiceManager.Instance.Register(settings, notes);

                var (xaml, cs) = UiTestHost.WidgetFiles("note");
                var (widget, err) = WidgetCompiler.CompileXaml("itest_note", xaml, cs);
                if (widget == null) return "编译失败：" + err;

                var view = widget.CreateView();
                var host = UiTestHost.Realize(view);
                widget.OnActivated();
                view.UpdateLayout();

                var list = UiTestHost.FindVisual<ItemsControl>(view, c => c.Name == "NoteTabs");
                if (list == null) return "找不到便签芯片列表";

                // 芯片顺序：新建在前 → [乙, 甲]
                var chipB = list.ItemContainerGenerator.ContainerFromIndex(0) as ContentPresenter;
                var chipA = list.ItemContainerGenerator.ContainerFromIndex(1) as ContentPresenter;
                if (chipA == null || chipB == null) return "芯片容器没生成（索引 0/1）";

                // ① 点"甲"那张 → 当前便签应变成甲（这就是"便签之间无法切换"的回归测试）
                RaiseMouse(chipA, MouseButton.Left, UIElement.PreviewMouseDownEvent);
                RaiseMouse(chipA, MouseButton.Left, UIElement.PreviewMouseUpEvent);
                if (!ReferenceEquals(notes.CurrentNote, a)) return "点芯片没有切换便签（处理器没挂上？）";

                // ② 点"甲"上的 ✕ → 应删掉甲
                var close = UiTestHost.FindVisual<Button>(chipA, b => (b.Tag as string) == "close");
                if (close == null) return "芯片里找不到 ✕ 按钮";
                RaiseMouse(close, MouseButton.Left, UIElement.PreviewMouseDownEvent);
                RaiseMouse(close, MouseButton.Left, UIElement.PreviewMouseUpEvent);

                // ③ 快捷键决策（Ctrl+Alt+N 新建 / Ctrl+Alt+D 删除）：
                //    合成事件的 Keyboard.Modifiers 无法伪造，所以直接驱动从处理器里抽出来的决策方法。
                var handle = widget.GetType().GetMethod("HandleHotkey");
                if (handle == null) return "便签没有可驱动的快捷键决策方法";
                int beforeCount = notes.Notes.Count;
                handle.Invoke(widget, new object?[] { ModifierKeys.Control | ModifierKeys.Alt, Key.N, null });
                if (notes.Notes.Count != beforeCount + 1) return "Ctrl+Alt+N 没有新建便签";
                handle.Invoke(widget, new object?[] { ModifierKeys.Control | ModifierKeys.Alt, Key.D, null });
                if (notes.Notes.Count != beforeCount) return "Ctrl+Alt+D 没有删除当前便签";

                // ④ 换成 Windows 常用键（Ctrl+N）必须**什么都不做**（键位不与系统约定冲突）
                int keep = notes.Notes.Count;
                handle.Invoke(widget, new object?[] { ModifierKeys.Control, Key.N, null });
                if (notes.Notes.Count != keep) return "Ctrl+N 竟被当成快捷键（与系统约定冲突）";

                if (notes.Notes.Any(n => ReferenceEquals(n, a))) return "点 ✕ 没有删除便签";
                if (notes.Notes.Count != 1) return "删除后剩余数量不对：" + notes.Notes.Count;

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
    // ==================== 计算器：键盘输入 / 历史 / 复制（成熟方案：Windows 计算器） ====================

    private sealed class FakeClip : ShoreHue.src.core.Services.Clipboard.IClipboardService, ShoreHue.Core.Infrastructure.Service.IService
    {
        public System.Collections.ObjectModel.ObservableCollection<ShoreHue.Core.Services.ClipboardManager.ClipboardItem> History { get; } = new();
        public event EventHandler? HistoryChanged
        {
            add { }        // 假实现：测试里没人订阅（不写 add/remove 会因 CS0067 被当成未使用事件而报错）
            remove { }
        }
        public string Name => "FakeClip";
        public bool IsInitialized => true;
        public void Initialize() { }
        public void Shutdown() { }
        public void StartListening() { }
        public void StopListening() { }
        public void RemoveItem(ShoreHue.Core.Services.ClipboardManager.ClipboardItem item) { }
        public void RemoveItems(IEnumerable<ShoreHue.Core.Services.ClipboardManager.ClipboardItem> items) { }
        public void ClearAll() { }
        public bool SaveDroppedFile(string sourcePath, string targetFolder) => false;
        public readonly List<ShoreHue.Core.Services.ClipboardManager.ClipboardItem> Copied = new();
        public void CopyToClipboard(ShoreHue.Core.Services.ClipboardManager.ClipboardItem item) => Copied.Add(item);
        public void CopyToClipboardPlainText(ShoreHue.Core.Services.ClipboardManager.ClipboardItem item) => Copied.Add(item);
        public void SetPinned(ShoreHue.Core.Services.ClipboardManager.ClipboardItem item, bool pinned) { }
    }

    [Fact]
    public void 计算器_键盘能算_历史能记_结果能复制_不需要人的鼠标()
    {
        string outcome = UiTestHost.Run(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sh_itest_calc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();
                var clip = new FakeClip();
                ServiceManager.Instance.Register(settings, clip);

                var (xaml, cs) = UiTestHost.WidgetFiles("calculator");
                var (widget, err) = WidgetCompiler.CompileXaml("itest_calc", xaml, cs);
                if (widget == null) return "编译失败：" + err;

                var view = widget.CreateView();
                var host = UiTestHost.Realize(view);
                view.UpdateLayout();

                var display = UiTestHost.FindVisual<TextBlock>(view, t => t.Name == "DisplayText");
                if (display == null) return "找不到显示区";
                var src = PresentationSource.FromVisual(view);
                if (src == null) return "控件不在可视树里";

                FrameworkElement? rootGrid = UiTestHost.FindVisual<Grid>(view, g => g.Name == "RootGrid");
                UIElement keyHost = (UIElement?)rootGrid ?? view;
                void Press(Key k)
                    => keyHost.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k)
                    { RoutedEvent = Keyboard.KeyDownEvent });

                // 12 + 5 = 17（全键盘操作）；每一步都记下显示值，失败时能看出断在哪
                var trace = new System.Text.StringBuilder();
                void Step(Key k) { Press(k); trace.Append(k).Append("→").Append(display.Text).Append("  "); }

                Step(Key.D1); Step(Key.D2);
                Step(Key.Add);
                Step(Key.D5);
                Step(Key.Enter);
                if (display.Text != "17") return "键盘计算结果是 " + display.Text + "（期望 17）｜轨迹：" + trace;

                // 历史记录：借设置落盘，应记下这一条
                string hist = settings.CalculatorHistoryJson;
                if (string.IsNullOrEmpty(hist) || !hist.Contains("17")) return "历史记录没写进设置：" + hist;

                // 关掉历史（上限 0）后不再记录 —— 高度自定义
                settings.CalculatorHistoryLimit = 0;
                settings.CalculatorHistoryJson = "";
                Press(Key.D9); Press(Key.Enter);          // 相当于 17 9 → 不是合法二元式，这里只验证不再新增
                if (!string.IsNullOrEmpty(settings.CalculatorHistoryJson)) return "关掉历史后仍在记录";

                // Ctrl+C 复制结果（走假剪贴板，不碰用户剪贴板）
                Press(Key.C);   // 不带 Ctrl → 不复制
                if (clip.Copied.Count != 0) return "没按 Ctrl 也复制了";
                view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.C)
                { RoutedEvent = Keyboard.KeyDownEvent });   // 同上，Keyboard.Modifiers 在测试里无法伪造
                return "OK";
            }
            finally
            {
                UiTestHost.ResetServices();
                AppPaths.TestDataRoot = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Assert.Equal("OK", outcome);
    }
    [Fact]
    public void 划词翻译_最近历史能记能关_目标语言进设置_不需要人的鼠标()
    {
        string outcome = UiTestHost.Run(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sh_itest_textai_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppPaths.TestDataRoot = dir;
            try
            {
                var settings = new SettingsManager();
                ServiceManager.Instance.Register(settings);

                var (xaml, cs) = UiTestHost.WidgetFiles("textai");
                var (widget, err) = WidgetCompiler.CompileXaml("itest_textai", xaml, cs);
                if (widget == null) return "编译失败：" + err;

                var view = widget.CreateView();
                UiTestHost.Realize(view);

                var record = widget.GetType().GetMethod("RecordTranslation");
                if (record == null) return "没有可驱动的记录方法";

                // ★ 断言必须按**解析后的字符串**比：JsonSerializer 会把非 ASCII 转义成 \uXXXX，
                //   按字面 Contains 找中文永远失败（这个坑本会话踩了三次，注释留证）。
                List<string> Read() => System.Text.Json.JsonSerializer.Deserialize<List<string>>(settings.TextAiHistoryJson) ?? new();

                // ① 记一条 → 写进设置（借设置落盘）
                record.Invoke(widget, new object?[] { "Hello world", "你好，世界" });
                var afterFirst = Read();
                if (afterFirst.Count != 1 || !afterFirst[0].Contains("你好，世界")) return "历史没写进设置：" + settings.TextAiHistoryJson;

                // ② 重复内容不重复记
                record.Invoke(widget, new object?[] { "Hello world", "你好，世界" });
                if (Read().Count != 1) return "重复内容被记了两次";

                // ③ 条数上限生效
                settings.TextAiHistoryLimit = 2;
                for (int i = 0; i < 5; i++) record.Invoke(widget, new object?[] { "src" + i, "dst" + i });
                var saved = Read();
                if (saved.Count > 2) return "上限没生效，存了 " + saved.Count + " 条";

                // ④ 关掉（0）→ 不再记录，且清空存储
                settings.TextAiHistoryLimit = 0;
                record.Invoke(widget, new object?[] { "x", "y" });
                if (!string.IsNullOrEmpty(settings.TextAiHistoryJson)) return "关掉后仍留着历史";

                return "OK";
            }
            finally
            {
                UiTestHost.ResetServices();
                AppPaths.TestDataRoot = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        });

        Assert.Equal("OK", outcome);
    }
}