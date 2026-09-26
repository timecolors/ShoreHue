using System;
using System.IO;
using System.Windows.Controls;
using ShoreHue.Core.Controllers;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 内置面板文件化：海床文件夹里的 main.cs 必须真的会被加载（"文件夹即真相源"），
/// 坏了要回退（返回 null → 调用方用宿主视图），改了要生效。
/// </summary>
public class PanelFileIizationTests
{
    private static string PanelSource(string marker) => string.Join("\n", new[]
    {
        "using System.Windows.Controls;",
        "using ShoreHue.UI.Widgets;",
        "namespace TestPanels",
        "{",
        "    public class TestPanel : UserControl, IWidget",
        "    {",
        "        public TestPanel() { Content = new TextBlock { Text = \"" + marker + "\" }; }",
        "        public string Name => \"测试面板\";",
        "        public UserControl CreateView() => this;",
        "        public void OnActivated() { }",
        "        public void OnDeactivated() { }",
        "    }",
        "}",
    });

    private static string MakeRoot(string panelId, string source, bool system = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "sh_panelfile_" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "seabed", "面板", panelId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.cs"), source);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"id\":\"" + panelId + "\",\"name\":\"测试\",\"kind\":\"Panel\",\"category\":\"面板功能\",\"system\":" + (system ? "true" : "false") + "}");
        return root;
    }

    /// <summary>把插件缓存清成"空目录"状态：既不残留临时数据，也不去扫真实用户数据。</summary>
    private static void ResetPluginCache(string? prevRoot)
    {
        string empty = Path.Combine(Path.GetTempPath(), "sh_panelfile_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        AppPaths.TestDataRoot = empty;
        WidgetPluginStore.Reload();
        AppPaths.TestDataRoot = prevRoot;
        try { Directory.Delete(empty, true); } catch { }
    }

    [Fact]
    public void 已迁移清单_覆盖全部内置面板与小组件()
    {
        // 六个内置面板功能 + 六个内置小组件都要从文件夹加载
        foreach (var id in new[] { "panel-notification", "panel-recent", "panel-quicksettings",
                                   "panel-windowcontrol", "panel-taskbar-feature", "panel-ai", "panel-apphelper" })
            Assert.Contains(id, WidgetPluginStore.MigratedPanelIds);
        foreach (var id in new[] { "timer", "calculator", "clipboard", "note", "web", "textai" })
            Assert.Contains(id, WidgetPluginStore.MigratedBuiltinIds);
    }

    [Fact]
    public void textai_实现宿主接口_热键能拿到当前实例()
    {
        Assert.Contains("textai", WidgetPluginStore.MigratedBuiltinIds);
        // ★ 控件实例化必须在 STA（UiTestHost 的共享 UI 线程）上跑
        string outcome = UiTestHost.Run(() =>
        {
            var (xaml, cs) = UiTestHost.WidgetFiles("textai");
            var (widget, err) = WidgetCompiler.CompileXaml("itest_textai_migrated", xaml, cs);
            if (widget == null) return "编译失败：" + err;
            return widget is ShoreHue.UI.Widgets.ITextAiWidget ? "OK" : "没有实现 ITextAiWidget";
        });
        Assert.Equal("OK", outcome);
    }

    [Fact]
    public void 已迁移面板_从文件夹加载_内容正确()
    {
        string root = MakeRoot("panel-notification", PanelSource("PANEL_V1"));
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            // ★ 控件在 UI 线程上创建，Text 也必须在 UI 线程上读（跨线程读 DependencyProperty 会抛）
            string text = UiTestHost.Run(() =>
                ((PanelContentController.TryLoadFolderPanel("panel-notification") as UserControl)?.Content as TextBlock)?.Text ?? "");
            Assert.Equal("PANEL_V1", text);
        }
        finally
        {
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 文件夹面板编译失败_回退为null()
    {
        string root = MakeRoot("panel-notification", "public class Broken { 这 不是 合法 C# }");
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            var view = UiTestHost.Run(() => PanelContentController.TryLoadFolderPanel("panel-notification"));
            Assert.Null(view);   // 调用方据此回退宿主视图
        }
        finally
        {
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 改了文件夹里的文件_重新加载后生效()
    {
        string root = MakeRoot("panel-notification", PanelSource("PANEL_V1"));
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            string v1 = UiTestHost.Run(() =>
                ((PanelContentController.TryLoadFolderPanel("panel-notification") as UserControl)?.Content as TextBlock)?.Text ?? "");
            Assert.Equal("PANEL_V1", v1);

            // 原地改文件 → 重新扫描 → 必须拿到新内容
            File.WriteAllText(Path.Combine(root, "seabed", "面板", "panel-notification", "main.cs"), PanelSource("PANEL_V2"));
            WidgetPluginStore.Reload();
            string v2 = UiTestHost.Run(() =>
                ((PanelContentController.TryLoadFolderPanel("panel-notification") as UserControl)?.Content as TextBlock)?.Text ?? "");
            Assert.Equal("PANEL_V2", v2);
        }
        finally
        {
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 未迁移的system面板_仍被跳过()
    {
        string root = MakeRoot("panel-foo", PanelSource("PANEL_FOO"), system: true);
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            Assert.Null(WidgetPluginStore.GetById("panel-foo"));   // 没进清单 → 不加载
        }
        finally
        {
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 安全模式_面板回退宿主视图()
    {
        string root = MakeRoot("panel-notification", PanelSource("SAFE"));
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            PluginRuntimeGuard.EnterSafeMode("测试");
            var view = UiTestHost.Run(() => PanelContentController.TryLoadFolderPanel("panel-notification"));
            Assert.Null(view);   // 安全模式：不加载文件夹面板 → 调用方回退宿主视图
        }
        finally
        {
            PluginRuntimeGuard.ResetAllForTests();
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void 已熔断的面板_回退宿主视图()
    {
        string root = MakeRoot("panel-notification", PanelSource("TRIPPED"));
        string? prev = AppPaths.TestDataRoot;
        AppPaths.TestDataRoot = root;
        try
        {
            WidgetPluginStore.Reload();
            for (int i = 0; i < PluginRuntimeGuard.TripThreshold; i++)
                PluginRuntimeGuard.ReportFailure("panel-notification", "测试", "boom");
            Assert.True(PluginRuntimeGuard.IsDisabled("panel-notification"));
            var view = UiTestHost.Run(() => PanelContentController.TryLoadFolderPanel("panel-notification"));
            Assert.Null(view);   // 已熔断 → 不加载，回退宿主视图
        }
        finally
        {
            PluginRuntimeGuard.ResetAllForTests();
            ResetPluginCache(prev);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
