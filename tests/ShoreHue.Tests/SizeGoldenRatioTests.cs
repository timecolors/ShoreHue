using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ShoreHue.Core.Calculators;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 小组件面板的**黄金长宽比**（2026-09-13 定稿，规则见 docs\设计-自适应尺寸与换行.md 规则 3）。
///
/// 规则：**长边 = 短边 × φ**，即只把"短边补长"，逼近 φ；**绝不压短长边** ——
/// 压短长边就会重新引入"内容显示不全"，而那正是这一轮要修的东西。
/// "竖向还是横向长条"沿用既有的 `HorizontalLayoutThreshold`（宽高比阈值），不另立口径。
///
/// 为什么值得写成断言：φ 是否达成完全可以用两个数算出来，不该靠截图目测；
/// 而"没有为了凑比例把宽度压到内容所需之下"这条更容易在后续改动里被无声破坏。
/// </summary>
public class SizeGoldenRatioTests
{
    private const double Phi = 1.618;
    private const double Threshold = 0.43;

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = action(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }

    /// <summary>测试注入的工作区尺寸：**必须大到不会盖过被断言的目标尺寸**。
    /// ★ 不注入就会红（2026-09-26 实际发生）：CI 的虚拟桌面很小，
    ///   `CalculateTargetSize` 会把目标钳到工作区的 2/5 宽、2/3 高之内，
    ///   于是黄金比断言算出来是 1.5 / 1.17，本机（大屏）却全绿。</summary>
    private const double TestScreenW = 3840;
    private const double TestScreenH = 2160;

    private static (double w, double h) Target(double cw, double ch) => RunSta(() =>
    {
        // 不 Show 的窗口即可：CalculateTargetSize 只读它的 Left/Top/Width/Height 用来找屏幕
        var calc = new SizeCalculator(new Window(), new ContentControl());
        return calc.CalculateTargetSize(cw, ch, "Widget", Phi, Threshold, TestScreenW, TestScreenH);
    });

    /// <summary>黄金比的正确不变量：**长边 / 短边 ≈ φ**。
    /// 竖向长条面板的 宽/高 是 1/φ（≈0.618），横向长条才是 φ —— 只断言 宽/高==1.618 会把竖条判错。</summary>
    private static double Goldenness(double w, double h) => Math.Max(w, h) / Math.Min(w, h);

    [Fact]
    public void 横向长条_由宽度推出高度_长边比短边接近黄金比()
    {
        // 宽 800 高 200 → 宽高比 4 > 0.43 → 横向长条 → 高补到 宽/φ
        var (w, h) = Target(800, 200);
        Assert.True(w > h, "横向内容的宽应当是长边");
        Assert.Equal(Phi, Goldenness(w, h), 1);   // 精度 1 位小数（≈±0.05）
    }

    [Fact]
    public void 竖向长条_由高度推出宽度_长边比短边接近黄金比()
    {
        // 宽 100 高 500 → 宽高比 0.2 < 0.43 → 竖向长条 → 宽补到 高/φ
        var (w, h) = Target(100, 500);
        Assert.True(h > w, "竖向内容的高应当是长边");
        Assert.Equal(Phi, Goldenness(w, h), 1);
    }

    [Fact]
    public void 补比例时绝不会把宽度压到低于内容所需()
    {
        // ★ 这条是防回归的关键：为凑 φ 而压短长边 = 重新引入"内容显示不全"。
        //   内容 200 宽 → 加内边距后 240，再受最小宽度 340 约束 → 目标宽至少 340。
        var (w, _) = Target(200, 500);
        Assert.True(w >= 340, $"目标宽 {w:F0} 被压到内容所需之下（应 ≥ 340）");

        // 横向内容同理：高度不得低于内容所需 + 固定开销
        var (_, h2) = Target(800, 200);
        Assert.True(h2 >= 200 + 30, $"目标高 {h2:F0} 低于内容所需（应 ≥ 230）");
    }

    [Fact]
    public void 内容本来就比黄金比更修长的面板_不会被压矮()
    {
        // 宽 400 高 600：宽高比 0.67 > 0.43 → 判为横向长条 → 高补到 400/φ≈247。
        // 但内容需要 600+90=690 高，所以只可能维持 690，绝不能压到 247。
        var (_, h) = Target(400, 600);
        Assert.True(h >= 600, $"目标高 {h:F0} 被压到内容所需之下（应 ≥ 600）");
    }

    [Fact]
    public void 屏幕小时被工作区钳制_这不是黄金比失效()
    {
        // 与上面几条互补：小屏上"没达成 φ"是**正确行为**（不能超出工作区），
        // 把这条也钉住，免得以后有人为了让黄金比断言通过而把限幅删掉。
        var (w, h) = RunSta(() =>
        {
            var calc = new SizeCalculator(new Window(), new ContentControl());
            return calc.CalculateTargetSize(800, 200, "Widget", Phi, Threshold, 1024, 768);
        });
        Assert.True(w <= 1024 * 2.0 / 5.0 + 0.01, $"宽度 {w:F0} 超出了小屏工作区的 2/5（应 ≤ 410）");
        Assert.True(h <= 768 * 2.0 / 3.0 + 0.01, $"高度 {h:F0} 超出了小屏工作区的 2/3（应 ≤ 512）");
    }
}
