using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>编译期沙箱：市场来源代码拦截危险 API（进程/反射/注册表/窗口/屏幕/剪贴板/文件写），合法 UI 代码放行。</summary>
public class WidgetCompilerSandboxTests
{
    [Fact]
    public void BenignUiWidget_Passes()
    {
        var blocked = WidgetCompiler.CheckSandbox(
            "using System.Windows; using System.Windows.Controls; using System.Windows.Threading; using ShoreHue.UI.Widgets;\n" +
            "public class W : UserControl, IWidget { public W() { var t = new TextBlock { Text = \"hi\" }; Content = t; } public string Name => \"w\"; public UserControl CreateView() => this; public void OnActivated() { } public void OnDeactivated() { } }");
        Assert.Empty(blocked);
    }

    [Fact]
    public void ProcessStart_Blocked()
    {
        var blocked = WidgetCompiler.CheckSandbox("Process.Start(\"cmd.exe\");");
        Assert.Contains(blocked, b => b.Contains("Process"));
    }

    [Fact]
    public void Reflection_Blocked()
    {
        var blocked = WidgetCompiler.CheckSandbox("typeof(X).GetMethod(\"M\").Invoke(null, null);");
        Assert.Contains(blocked, b => b.Contains("GetMethod"));
        Assert.Contains(blocked, b => b.Contains("反射"));
    }

    [Fact]
    public void Activator_Blocked()
    {
        Assert.Contains(WidgetCompiler.CheckSandbox("Activator.CreateInstance(typeof(X));"), b => b.Contains("Activator"));
    }

    [Fact]
    public void DllImport_Blocked()
    {
        Assert.Contains(WidgetCompiler.CheckSandbox("[DllImport(\"user32.dll\")] static extern int X();"), b => b.Contains("DllImport"));
    }

    [Fact]
    public void Registry_Blocked()
    {
        // ★ 2026-09-10：裸 "registry" 文本规则已删（会把 registryPath 变量名/注释一起拦掉），
        //   改由符号层按类型拦 —— 所以这条测试必须用**能编译**的源码走总闸门（SandboxErrors），
        //   而不是拿一句编译不过的片段去测文本层。
        const string src = "using Microsoft.Win32;\npublic class A { public object M() => Registry.CurrentUser; }";
        Assert.Contains("Microsoft.Win32.Registry", WidgetCompiler.SandboxErrors(src));
    }

    [Fact]
    public void FileWrite_Blocked()
    {
        var blocked = WidgetCompiler.CheckSandbox("File.WriteAllText(\"a.txt\", \"x\");");
        Assert.Contains(blocked, b => b.Contains("文件写入"));
    }

    [Fact]
    public void Clipboard_Allowed_PermissionDeclared()
    {
        // ★ 剪贴板降为权限声明类（2026-08 用户修正）：不硬拦，安装时弹窗提示
        Assert.Empty(WidgetCompiler.CheckSandbox("Clipboard.SetText(\"x\");"));
    }

    [Fact]
    public void WindowHook_Blocked()
    {
        // ★ 2026-09-10：11 条"窗口/输入函数名"裸子串规则已删（纯误报源），
        //   P/Invoke 由符号层拦（同时修好了特性名绑定构造函数导致的 P/Invoke 漏检）。
        const string src = "using System;\nusing System.Runtime.InteropServices;\n" +
                           "public class A { [DllImport(\"user32.dll\")] static extern IntPtr SetWindowsHookEx(int id, IntPtr f, IntPtr m, uint t); " +
                           "void M() { SetWindowsHookEx(14, IntPtr.Zero, IntPtr.Zero, 0); } }";
        Assert.Contains("System.Runtime.InteropServices", WidgetCompiler.SandboxErrors(src));
    }

    [Fact]
    public void ScreenCapture_Blocked()
    {
        Assert.Contains(WidgetCompiler.CheckSandbox("Graphics.CopyFromScreen(0, 0, 0, 0, s);"), b => b.Contains("屏幕捕获"));
    }

    [Fact]
    public void DangerousUsing_Blocked()
    {
        // ★ 整命名空间拦截已移除（Stopwatch 等无害类不误伤）：纯 using 不拦；
        //   危险类型由编译符号级（SandboxErrors → CheckSandboxSymbols）精确拦截
        var blocked = WidgetCompiler.CheckSandbox("using System.Diagnostics; using System.Reflection; using System.IO;");
        Assert.Empty(blocked);
        var err = WidgetCompiler.SandboxErrors("using System.Diagnostics;\npublic class A { void M() { Process.Start(\"x\"); } }");
        Assert.Contains("Process", err);
    }

    [Fact]
    public void DispatcherInvoke_NotBlocked()
    {
        // 常见合法代码里的 Invoke（Dispatcher）不应被误伤
        var blocked = WidgetCompiler.CheckSandbox("Dispatcher.BeginInvoke(new System.Action(() => { }));");
        Assert.Empty(blocked);
    }

    [Fact]
    public void SandboxErrors_Empty_WhenPass()
    {
        Assert.Equal("", WidgetCompiler.SandboxErrors("var x = 1;"));
    }

    [Fact]
    public void SandboxErrors_NonEmpty_WhenBlocked()
    {
        Assert.NotEqual("", WidgetCompiler.SandboxErrors("Process.Start(\"x\");"));
    }
}
