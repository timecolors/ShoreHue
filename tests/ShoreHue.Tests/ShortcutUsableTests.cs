// ==================== 快捷方式「可用性」判据 ====================
//
// 钉住一个**真机实测出来的 bug**（2026-09）：SystemToast.EnsureRegistered 原先只查
// 「快捷方式文件在不在 + AUMID 对不对」，**不查它指向的 exe 还在不在**。
// 于是用户移动或删除安装目录（**卸载就是删安装目录**；手动清理 bin 也一样）之后，
// 开始菜单快捷方式变成死链 —— 点它没反应，而应用每次启动都判定"已存在且 AUMID 正确"
// 直接返回，**永不自愈**。（实测：目标被删后连启动两次都没修复。）

using ShoreHue.Infrastructure.WinApi;
using Xunit;

namespace ShoreHue.Tests;

public class ShortcutUsableTests
{
    private const string Exe = @"D:\app\ShoreHue.exe";
    private const string OtherExe = @"E:\other\ShoreHue.exe";

    [Fact]
    public void 一切正常时判定可用()
    {
        Assert.True(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: SystemToast.Aumid, resolvedTarget: Exe,
            expectedExe: Exe, targetExists: true));
    }

    /// <summary>★ 这条就是那个 bug：目标 exe 已经不在了（用户移动/删除了安装目录）。</summary>
    [Fact]
    public void 目标exe已不存在时判定不可用_这就是那个bug()
    {
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: SystemToast.Aumid, resolvedTarget: Exe,
            expectedExe: Exe, targetExists: false));
    }

    [Fact]
    public void 目标指向别的exe时判定不可用()
    {
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: SystemToast.Aumid, resolvedTarget: OtherExe,
            expectedExe: Exe, targetExists: true));
    }

    [Fact]
    public void AUMID不是我们的时判定不可用()
    {
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: "SomeoneElse.App", resolvedTarget: Exe,
            expectedExe: Exe, targetExists: true));
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: null, resolvedTarget: Exe,
            expectedExe: Exe, targetExists: true));
    }

    [Fact]
    public void 快捷方式文件不存在时判定不可用()
    {
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: false, lnkAumid: null, resolvedTarget: "",
            expectedExe: Exe, targetExists: false));
    }

    [Fact]
    public void 当前exe未知时判定不可用()
    {
        Assert.False(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: SystemToast.Aumid, resolvedTarget: Exe,
            expectedExe: null, targetExists: true));
    }

    [Fact]
    public void 路径大小写不敏感()
    {
        Assert.True(SystemToast.ShortcutUsable(
            lnkExists: true, lnkAumid: SystemToast.Aumid,
            resolvedTarget: @"d:\APP\shorehue.EXE", expectedExe: Exe, targetExists: true));
    }
}
