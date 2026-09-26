using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// ★ 宿主 API 面快照守卫（防"新增 API 忘记分类"的漂移）：
///   沙箱目前靠"名单"拦截宿主特权 API。真正的风险不是今天漏了什么，而是**以后新增的宿主能力**
///   没人记得加名单 —— 那就出现静默的洞。本测试把"能力命名空间里的公开类型"做成快照：
///   · 首次运行生成快照（baseline）；
///   · 之后任何新增/删除/改动都会让测试失败，并提示你决定：这个新 API 该被沙箱拦截，还是对插件开放。
///   （这是"可见面收敛（独立接口程序集）"的廉价替代：同样防漂移，不动 ABI。）
/// </summary>
public class HostApiSurfaceTests
{
    /// <summary>视为"能力命名空间"的前缀：这些命名空间里的公开类型必须被显式分类。</summary>
    private static readonly string[] CapabilityNamespaces =
    {
        "ShoreHue.Infrastructure.WinApi",
        "ShoreHue.Core.Services.Ai",
        "ShoreHue.Core.Services.System",
        "ShoreHue.Core.Infrastructure.Service",
        "ShoreHue.UI.Seabed",
        "ShoreHue.UI.Settings",
        "ShoreHue.UI.Main",
        "ShoreHue.UI.Widgets.Dynamic",
    };

    [Fact]
    public void 能力命名空间的公开类型_必须分类_快照守卫()
    {
        string? root = FindRepoRoot();
        Assert.NotNull(root);
        string snapshotPath = Path.Combine(root!, "tests", "ShoreHue.Tests", "host-api-surface.snapshot.txt");

        var actual = CurrentSurface();
        if (!File.Exists(snapshotPath))
        {
            // ★ 必须**失败**，不能自我生成基线后通过：
            //   快照缺失/被改名/未随仓库分发时，"守卫"会悄悄把自己重新武装成"当前状态"，
            //   于是"新增宿主 API 却忘了分类"这条唯一防线直接失效。
            //   也不应该在测试运行期间往仓库里写文件（测试必须只读）。
            Assert.Fail(
                "宿主 API 面快照缺失：" + snapshotPath + "\n" +
                "这表示漂移守卫当前是失效的。请审阅下面的当前 API 面，确认每项都已被正确分类\n" +
                "（该拦的进 WidgetCompiler 宿主黑名单，该开放的走 HostCapabilities），再手工写入快照文件。\n" +
                "当前 API 面（" + actual.Count + " 项）：\n  " + string.Join("\n  ", actual));
            return;
        }

        var expected = File.ReadAllLines(snapshotPath).Where(l => l.Trim().Length > 0).ToList();
        var added = actual.Except(expected).ToList();
        var removed = expected.Except(actual).ToList();

        Assert.True(added.Count == 0 && removed.Count == 0,
            "宿主 API 面发生变化（沙箱分类可能已漏）：\n" +
            (added.Count > 0 ? "  新增：\n    " + string.Join("\n    ", added) + "\n" : "") +
            (removed.Count > 0 ? "  移除：\n    " + string.Join("\n    ", removed) + "\n" : "") +
            "\n处置：① 若这些能力不该给外来插件用 → 加进 WidgetCompiler 的宿主类型黑名单；" +
            "② 若可以开放 → 用 HostCapabilities 暴露窄接口；③ 然后更新快照文件。");
    }

    /// <summary>
    /// ★ 每个"对外敞开"（sandbox=open）的宿主类型都必须在下面登记**分类理由**。
    ///
    /// 为什么需要这条，而不只是快照：快照只约束"形状没变"——它无法阻止
    /// "某个 open 类型其实有操作系统/读隐私的能力，但从没人逐个看过"。
    /// 实际情况正是如此：本仓库曾有 18 个 open 类型，其中
    /// `HotspotControl`（开关移动热点）、`SystemRadios`（开关蓝牙/Wi-Fi）、`DisplayBrightness`（改屏幕亮度）、
    /// `WindowListProvider`（枚举窗口 + 句柄→进程路径）、`ToastMonitor`（读用户全部通知 + 开关监听）、
    /// `AiChatClient`（宿主 AI 传输）全都在名单里躺着，既没人拦、也没人告知用户。
    ///
    /// 现在：新增一个 open 类型 → 本测试红 → 作者必须写明"为什么它可以敞开"或把它拦掉。
    /// </summary>
    private static readonly Dictionary<string, string> ReviewedOpenTypes = new(StringComparer.Ordinal)
    {
        // 纯数据模型 / DTO（无副作用、无能力）
        ["ShoreHue.Core.Services.Ai.AiSession"] = "会话数据模型（AiSessionStore 已拦，插件拿不到实例）",
        ["ShoreHue.Core.Services.Ai.AiSessionData"] = "会话集合数据模型（同上）",
        ["ShoreHue.Core.Services.Ai.ChatMessage"] = "AI 消息 DTO（纯数据）",
        ["ShoreHue.Core.Services.Ai.ChatRole"] = "AI 角色枚举（纯数据）",
        ["ShoreHue.Infrastructure.WinApi.ToastNotificationItem"] = "通知 DTO：HostCapabilities.Notifications 的集合元素类型，必须可见",
        ["ShoreHue.Infrastructure.WinApi.WeatherService+CitySuggestion"] = "天气城市建议 DTO（纯数据）",
        ["ShoreHue.UI.Widgets.Dynamic.WidgetPlugin"] = "插件仓库元数据 DTO（纯数据）",
        // 纯工具 / 无副作用
        ["ShoreHue.Core.Infrastructure.Service.IService"] = "服务生命周期契约（实现它拿不到任何服务实例）",
        ["ShoreHue.Infrastructure.WinApi.HotkeyParser"] = "热键字符串 ↔ 修饰键/虚拟键码解析（纯函数）",
        ["ShoreHue.UI.Widgets.Dynamic.WidgetPermissions"] = "权限检测工具（纯函数，插件可自检）",
        ["ShoreHue.UI.Widgets.Dynamic.WidgetSamples"] = "示例源码字符串常量（不执行任何东西）",
        ["ShoreHue.UI.Widgets.Dynamic.XamlCodeGenerator"] = "XAML→C# 文本生成（只产出字符串；执行它需要已被拦的 WidgetCompiler）",
        // 有后果但属"权限 + 安装时告知"档（见 WidgetPermissions.Detect 的对应关键字）
        ["ShoreHue.Infrastructure.WinApi.DisplayBrightness"] = "改屏幕亮度 → 已在 Detect 标 device，安装弹窗告知",
        ["ShoreHue.Infrastructure.WinApi.HotspotControl"] = "开关移动热点 → 已在 Detect 标 device，安装弹窗告知",
        ["ShoreHue.Infrastructure.WinApi.SystemRadios"] = "开关蓝牙/Wi-Fi → 已在 Detect 标 device，安装弹窗告知",
        ["ShoreHue.Infrastructure.WinApi.WeatherService"] = "固定端点联网 → 已在 Detect 标 network，安装弹窗告知",
        ["ShoreHue.Infrastructure.WinApi.ShortcutLinkResolver"] = "读磁盘 .lnk 目标 → 已在 Detect 标 file，安装弹窗告知",
    };

    [Fact]
    public void 每个对外开放的宿主类型_都必须登记分类理由()
    {
        var open = CurrentSurface()
            .Where(l => l.EndsWith("[sandbox=open]", StringComparison.Ordinal))
            .Select(l => l[..l.IndexOf("  [sandbox=", StringComparison.Ordinal)])
            .ToList();

        var undocumented = open.Where(t => !ReviewedOpenTypes.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.True(undocumented.Count == 0,
            "以下宿主类型对外来插件是**敞开**的，但没有人登记过分类理由：\n  " +
            string.Join("\n  ", undocumented) +
            "\n处置：① 若它带能力（操作系统/读隐私/执行）→ 加进 WidgetCompiler 黑名单，或经 HostCapabilities 开放窄接口；" +
            "② 若确实无副作用 → 在 HostApiSurfaceTests.ReviewedOpenTypes 里写明理由，并更新快照。");

        // 反向：已拦掉的类型不该继续留在理由表里（避免陈旧的"已审阅"假象）
        var stale = ReviewedOpenTypes.Keys.Where(t => !open.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "这些类型已经不在 open 名单里了（多半已被拦掉），请从 ReviewedOpenTypes 移除：" + string.Join(", ", stale));
    }

    private static List<string> CurrentSurface()
    {
        var asm = typeof(WidgetCompiler).Assembly;
        var list = new List<string>();
        foreach (var t in asm.GetExportedTypes())
        {
            string ns = t.Namespace ?? "";
            if (!CapabilityNamespaces.Any(p => ns.StartsWith(p, StringComparison.Ordinal))) continue;
            string fullName = t.FullName ?? t.Name;
            list.Add(fullName + "  [sandbox=" + (WidgetCompiler.IsHostTypeBlocked(fullName) ? "blocked" : "open") + "]");
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "market", "packages"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }
}
