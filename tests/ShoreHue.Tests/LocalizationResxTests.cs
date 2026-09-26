using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 本地化资源的一致性守卫。
///
/// 背景（真实发生过）：两个 resx 的键集合曾经漂移 —— 中文加了键、英文没加（或反过来），
/// 界面在对应语言下会直接显示**资源键名本身**（`LocalizationManager` 的索引器找不到键时返回键名），
/// 而 CI 完全没有信号。同类问题还有"占位符个数不一致"（`{0}` 只在一侧存在 → string.Format 抛异常或漏值）。
///
/// ★ 明确不做的两件事（避免把误报当问题）：
///   ① **不**断言"每个键都被引用"：仓库里现有一批历史代际键（被重设计取代的
///      `UI_OnboardingWindow_58…107`、`Ob_Map_*`、`WidgetMkt_*` 等）已经没有引用，
///      但删除它们需要逐条人工确认（可能有运行时拼接或文档引用），属独立清理项，不适合当守卫；
///   ② **不**断言"没有硬编码中文"：AI 的 SystemPrompt、模型提示词等是发给模型的行为指令，
///      本地化反而会改变模型行为，属于有意保留。
/// </summary>
public class LocalizationResxTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShoreHue.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（ShoreHue.csproj）");
    }

    private static Dictionary<string, string> ReadResx(string name)
    {
        string path = Path.Combine(RepoRoot(), "src", "UI", "Localization", name);
        Assert.True(File.Exists(path), "找不到本地化文件：" + path);
        string xml = File.ReadAllText(path);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xml,
            @"<data\s+name=""(?<k>[^""]+)""[^>]*>\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline))
        {
            result[m.Groups["k"].Value] = m.Groups["v"].Value;
        }
        return result;
    }

    [Fact]
    public void 中英键集合_必须完全一致()
    {
        var zh = ReadResx("Strings.resx");
        var en = ReadResx("Strings.en-US.resx");

        var zhOnly = zh.Keys.Except(en.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var enOnly = en.Keys.Except(zh.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(zhOnly.Count == 0 && enOnly.Count == 0,
            "两个 resx 的键集合不一致（界面会在缺键的语言下显示资源键名）：\n" +
            (zhOnly.Count > 0 ? "  只存在于中文：" + string.Join(", ", zhOnly.Take(30)) + "\n" : "") +
            (enOnly.Count > 0 ? "  只存在于英文：" + string.Join(", ", enOnly.Take(30)) + "\n" : ""));
    }

    [Fact]
    public void 中英占位符_必须一一对应()
    {
        var zh = ReadResx("Strings.resx");
        var en = ReadResx("Strings.en-US.resx");

        var bad = new List<string>();
        foreach (var (key, zhValue) in zh)
        {
            if (!en.TryGetValue(key, out var enValue)) continue;
            var zhSlots = Slots(zhValue);
            var enSlots = Slots(enValue);
            if (!zhSlots.SetEquals(enSlots))
            {
                bad.Add($"{key}: zh=[{string.Join(",", zhSlots.OrderBy(x => x))}] en=[{string.Join(",", enSlots.OrderBy(x => x))}]");
            }
        }

        Assert.True(bad.Count == 0,
            "格式化占位符在两个语言里不一致（string.Format 会漏值或抛异常）：\n  " + string.Join("\n  ", bad.Take(30)));
    }

    [Fact]
    public void 键名不得重复定义()
    {
        foreach (var name in new[] { "Strings.resx", "Strings.en-US.resx" })
        {
            string path = Path.Combine(RepoRoot(), "src", "UI", "Localization", name);
            var keys = Regex.Matches(File.ReadAllText(path), @"<data\s+name=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToList();
            var dup = keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, name + " 里有重复键（后一个会覆盖前一个）：" + string.Join(", ", dup));
        }
    }

    /// <summary>
    /// ★ 界面引用的每个键都必须在字典里存在。
    ///
    /// 为什么需要这条：`LocalizationManager` 的索引器在找不到键时**返回键名本身**（便于发现漏译），
    /// 于是"引用了不存在的键"在界面上表现为**直接显示英文键名**——看起来像普通文字，很难被注意到。
    /// 真实案例：`SettingsWindow.xaml` 里两处写的是 `Item[Set_TriggerDelay]`，而字典里从来没有这个键
    /// （真正该用的是早已存在的 `Set_TriggerDelayGlobal`），于是一整块设置项的标题长期显示为
    /// "Set_TriggerDelay" 这个英文标识符，没有任何测试或日志会报警。
    ///
    /// 只检查**字面量键**（`Item[Xxx]` / `Instance["Xxx"]`）；变量取键（`Instance[locKey]`）静态判不了，
    /// 那类需要靠键名以字面量形式出现在数组/元组里、从而被下面这轮扫描覆盖到。
    /// </summary>
    [Fact]
    public void 界面引用的键_必须都在字典里存在()
    {
        var keys = new HashSet<string>(ReadResxKeys("Strings.resx"), StringComparer.Ordinal);
        Assert.True(keys.Count > 500, "字典读取异常，只拿到 " + keys.Count + " 个键");

        var root = RepoRoot();
        var missing = new List<string>();

        foreach (var dir in new[] { "src", "seabed", "market" })
        {
            string full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;

            foreach (var f in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".xaml" && ext != ".cs") continue;

                string text;
                try { text = File.ReadAllText(f); } catch { continue; }

                // XAML: {Binding Item[Key], ...}
                foreach (Match m in Regex.Matches(text, @"Item\[([A-Za-z_][A-Za-z0-9_]*)\]"))
                    if (!keys.Contains(m.Groups[1].Value))
                        missing.Add($"{Path.GetFileName(f)}: Item[{m.Groups[1].Value}]");

                // 代码: LocalizationManager.Instance["Key"]（仅字面量）
                foreach (Match m in Regex.Matches(text, @"Instance\[\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\]"))
                    if (!keys.Contains(m.Groups[1].Value))
                        missing.Add($"{Path.GetFileName(f)}: Instance[\"{m.Groups[1].Value}\"]");
            }
        }

        Assert.True(missing.Count == 0,
            "以下界面引用的本地化键在字典里不存在 —— 界面会**直接显示键名本身**：\n  " +
            string.Join("\n  ", missing.Distinct().Take(40)) +
            "\n处置：补上该键（中英各一条），或把引用改成正确的键名。");
    }

    private static IEnumerable<string> ReadResxKeys(string name)
    {
        string path = Path.Combine(RepoRoot(), "src", "UI", "Localization", name);
        return Regex.Matches(File.ReadAllText(path), @"<data\s+name=""([^""]+)""")
            .Select(m => m.Groups[1].Value);
    }

    private static HashSet<string> Slots(string value)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(value, @"\{(\d+)")) set.Add(m.Groups[1].Value);
        return set;
    }
}
