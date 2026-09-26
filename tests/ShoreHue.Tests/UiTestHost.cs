// ==================== 交互测试的共享 UI 宿主 ====================
//
// 为什么需要它（踩出来的）：两个交互测试各自 `new Thread(STA)` + `new Application()`，
// 结果"单独跑都绿、一起跑就红" —— 因为 WPF 的 Application 属于**创建它的那条线程**，
// 前一个测试的线程退出后，Application 还挂在一条死线程上，后一个测试的窗口就再也排版不出来
// （表现是"找不到某个控件"，看起来像控件坏了，其实是测试宿主坏了）。
//
// 正确做法：**一条长期存活的 STA UI 线程**，Application 与主题只在这条线程上建一次，
// 所有交互测试都 marshal 到它上面跑。这样测试之间不再互相污染，也就不会再有这种假红。

using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

// 交互/控件测试共享一个 Application 与服务定位器 —— 进程级资源，必须串行跑。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace ShoreHue.Tests;

internal static class UiTestHost
{
    private static Dispatcher? _dispatcher;
    private static readonly object _gate = new();

    /// <summary>共享 UI 线程（首次使用时启动，进程结束前一直活着）。</summary>
    private static Dispatcher Dispatcher
    {
        get
        {
            lock (_gate)
            {
                if (_dispatcher != null) return _dispatcher;

                using var ready = new ManualResetEventSlim(false);
                Dispatcher? created = null;
                var t = new Thread(() =>
                {
                    // 主题必须合并：控件 XAML 里的 {StaticResource} 靠它解析
                    var app = Application.Current ?? new Application();
                    // ★ 测试进程里绝不能"最后一个窗口关闭 = 应用退出"：否则先跑完的测试会把 Application
                    //   关掉，后面的 WPF 测试全部撞上"应用程序对象正在关闭"（偶发假红就是这么来的）。
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    // ★ 主题 + 图标字典都要合并：宿主视图（如 NotificationDockView）会用到 AppIcons.xaml
                    //   里的 {StaticResource IconTrash} 等图标，只有 Theme.xaml 时实例化会抛"找不到资源"。
                    if (!app.Resources.MergedDictionaries.Any(d => (d.Source?.OriginalString ?? "").Contains("Theme.xaml")))
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml") });
                    if (!app.Resources.MergedDictionaries.Any(d => (d.Source?.OriginalString ?? "").Contains("AppIcons.xaml")))
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/AppIcons.xaml") });

                    created = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "UiTestHost"
                };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ready.Wait(TimeSpan.FromSeconds(20));
                _dispatcher = created ?? throw new InvalidOperationException("UI 测试线程启动失败");
                return _dispatcher;
            }
        }
    }

    private static readonly System.Collections.Generic.List<Window> _windows = new();

    /// <summary>在共享 UI 线程上跑一段"会真的建控件"的代码；异常转成文本返回，便于断言里看到真因。</summary>
    internal static string Run(Func<string> body)
    {
        string result = "";
        Dispatcher.Invoke(() =>
        {
            try { result = body(); }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                for (var e = ex; e != null; e = e.InnerException) sb.AppendLine(e.GetType().Name + ": " + e.Message);
                result = "ERROR: " + sb;
            }
            finally
            {
                // ★ 关闭本测试建过的窗口并清队列：残留窗口会让**下一个测试**排不出布局
                //   （表现成"找不到某个控件"，看着像控件坏了，其实是上一个测试留下的现场）。
                foreach (var w in _windows.ToArray()) { try { w.Close(); } catch { } }
                _windows.Clear();
                try { Dispatcher.Invoke(() => { }, DispatcherPriority.Background); } catch { }
            }
        });
        return result;
    }

    /// <summary>同上，但返回任意结果（给需要拿到控件对象的测试用）。</summary>
    internal static T Run<T>(Func<T> body)
    {
        T result = default!;
        Dispatcher.Invoke(() =>
        {
            try { result = body(); }
            finally
            {
                foreach (var w in _windows.ToArray()) { try { w.Close(); } catch { } }
                _windows.Clear();
            }
        });
        return result;
    }

    /// <summary>清掉服务定位器（它是进程级单例，跨测试会残留"指向已删临时目录"的旧服务）。</summary>
    internal static void ResetServices()
    {
        var f = typeof(ShoreHue.Core.Infrastructure.Service.ServiceManager).GetField("_instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        f?.SetValue(null, null);
    }

    /// <summary>把控件摆进一个**屏幕外**窗口并跑一次布局（容器才会生成，又不打扰人、不占鼠标）。</summary>
    internal static Window Realize(FrameworkElement view)
    {
        var host = new Window
        {
            Width = 420,
            Height = 360,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Left = -4000,
            Top = -4000,
            Content = view
        };
        host.Show();
        // ★ 显式跑一次测量/排布：共享 UI 线程上"第二个窗口"有时不会自动排版，
        //   容器就不会生成（表现成"芯片容器没生成"）。手动 Measure/Arrange 与窗口无关，稳定。
        view.Measure(new Size(420, 360));
        view.Arrange(new Rect(0, 0, 420, 360));
        view.UpdateLayout();
        host.UpdateLayout();
        _windows.Add(host);
        return host;
    }

    /// <summary>找控件：先走可视树；可视树还没铺出来时退回**逻辑树**（跨测试复用 UI 线程时踩过）。</summary>
    internal static T? FindVisual<T>(DependencyObject root, Func<T, bool>? where = null) where T : DependencyObject
    {
        var hit = FindInVisualTree(root, where);
        if (hit != null) return hit;
        return FindInLogicalTree(root, where);
    }

    private static T? FindInVisualTree<T>(DependencyObject root, Func<T, bool>? where) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && (where == null || where(hit))) return hit;
            var deep = FindInVisualTree(child, where);
            if (deep != null) return deep;
        }
        return null;
    }

    private static T? FindInLogicalTree<T>(DependencyObject root, Func<T, bool>? where) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is T hit && (where == null || where(hit))) return hit;
            var deep = FindInLogicalTree(d, where);
            if (deep != null) return deep;
        }
        return null;
    }

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ShoreHue.csproj"))) return dir;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到项目根");
    }

    internal static (string Xaml, string Cs) WidgetFiles(string id)
    {
        string dir = System.IO.Path.Combine(RepoRoot(), "seabed", "小组件", id);
        string xaml = "";
        string cs = "";
        foreach (var f in System.IO.Directory.GetFiles(dir))
        {
            if (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                xaml = System.IO.File.ReadAllText(f);
            else if (f.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
                cs = System.IO.File.ReadAllText(f);
        }
        return (xaml, cs);
    }
}
