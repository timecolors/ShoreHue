// ==================== 快捷键字符串匹配 ====================
//
// 便签快捷键（可在 设置 → 面板 → 便签 里配）靠 HotkeyParser.Matches 判定。
// 这里钉住"设置里存什么、控件就认什么"这条口径：修饰键必须完全一致、主键按虚拟键码比。

using ShoreHue.Infrastructure.WinApi;
using System.Windows.Input;
using Xunit;

namespace ShoreHue.Tests;

public class HotkeyParserMatchTests
{
    [Fact]
    public void 命中同一个组合()
        => Assert.True(HotkeyParser.Matches("Ctrl+N", ModifierKeys.Control, Key.N));

    [Fact]
    public void 修饰键不一致不算命中()
    {
        Assert.False(HotkeyParser.Matches("Ctrl+N", ModifierKeys.None, Key.N));
        Assert.False(HotkeyParser.Matches("Ctrl+N", ModifierKeys.Control | ModifierKeys.Shift, Key.N));
        Assert.False(HotkeyParser.Matches("Ctrl+Shift+N", ModifierKeys.Control, Key.N));
    }

    [Fact]
    public void 主键不一致不算命中()
        => Assert.False(HotkeyParser.Matches("Ctrl+N", ModifierKeys.Control, Key.M));

    [Fact]
    public void 大小写与写法宽容()
    {
        Assert.True(HotkeyParser.Matches("ctrl+n", ModifierKeys.Control, Key.N));
        Assert.True(HotkeyParser.Matches("Control+N", ModifierKeys.Control, Key.N));
        Assert.True(HotkeyParser.Matches("Ctrl + N", ModifierKeys.Control, Key.N));
    }

    [Fact]
    public void 三个默认便签快捷键都可用()
    {
        Assert.True(HotkeyParser.Matches("Ctrl+N", ModifierKeys.Control, Key.N));       // 新建
        Assert.True(HotkeyParser.Matches("Ctrl+W", ModifierKeys.Control, Key.W));       // 删除
        Assert.True(HotkeyParser.Matches("Ctrl+Tab", ModifierKeys.Control, Key.Tab));   // 下一个
    }

    [Fact]
    public void 空值与非法值一律不命中_不误触发()
    {
        Assert.False(HotkeyParser.Matches("", ModifierKeys.Control, Key.N));
        Assert.False(HotkeyParser.Matches(null, ModifierKeys.Control, Key.N));
        Assert.False(HotkeyParser.Matches("Ctrl", ModifierKeys.Control, Key.N));        // 只有修饰键
        Assert.False(HotkeyParser.Matches("Ctrl+不存在的键", ModifierKeys.Control, Key.N));
    }

    [Fact]
    public void 功能键与方向键也认()
    {
        Assert.True(HotkeyParser.Matches("Ctrl+F5", ModifierKeys.Control, Key.F5));
        Assert.True(HotkeyParser.Matches("Alt+Left", ModifierKeys.Alt, Key.Left));
    }
}
