using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 市场包的身份闸门（2026-09-13）。
///
/// 背景：上传时"同 id 已存在就写"= **直接覆盖**，而归属校验只在删除时做、上传时没有，
/// 且包 ID 由用户手填、默认从**显示名**推导（中文名经 SanitizeId 会退化成同样的英文串）
/// → 撞名是常态，后发布者会**静默覆盖**前者的包。
///
/// 为什么要把这些写成断言：这是**安全闸门**。原来的实现埋在 `GitHubMarketService` 的网络流程里，
/// 只能靠"人工点一遍"验证；抽成纯函数（`MarketPackageRules`）之后，下面每条都是可复现的。
/// </summary>
public class MarketPackageRulesTests
{
    private const long MyId = 12345;
    private const string MyLogin = "timecolors";

    private static string Manifest(long publisherId, string author)
        => "{ \"id\": \"x\", \"author\": \"" + author + "\", \"publisherId\": " + publisherId + " }";

    // ==================== 形态 ====================

    [Theory]
    [InlineData("timecolors/tide-note", true)]
    [InlineData("tide-note", true)]                  // 一段也合法（官方保留命名空间用）
    [InlineData("a/b/c", false)]                     // 最多一个斜杠
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("../evil", false)]                   // 路径穿越：段里有点
    [InlineData("a/", false)]                        // 空段
    [InlineData("/b", false)]
    [InlineData("a b", false)]
    [InlineData("x", false)]                         // 单段至少 2 字符
    public void 包ID形态校验(string id, bool ok)
        => Assert.Equal(ok, MarketPackageRules.ValidateIdFormat(id) == null);

    // ==================== 新包：必须占自己的命名空间 ====================

    [Fact]
    public void 新包_带自己命名空间_允许()
        => Assert.Null(MarketPackageRules.CheckCanPublish("timecolors/tide-note", null, MyId, MyLogin));

    [Fact]
    public void 新包_名字大小写不敏感_允许()
        => Assert.Null(MarketPackageRules.CheckCanPublish("TimeColors/tide-note", null, MyId, MyLogin));

    [Fact]
    public void 新包_裸ID_被拒_因为那是官方保留命名空间()
    {
        string? err = MarketPackageRules.CheckCanPublish("tide-note", null, MyId, MyLogin);
        Assert.NotNull(err);
        Assert.Contains("官方保留", err);
    }

    [Fact]
    public void 新包_占用别人的命名空间_被拒()
    {
        string? err = MarketPackageRules.CheckCanPublish("alice/tide-note", null, MyId, MyLogin);
        Assert.NotNull(err);
        Assert.Contains("登录名", err);
    }

    // ==================== 已有包：只有原作者能更新（核心漏洞） ====================

    [Fact]
    public void 已有包_作者是别人_必须被拒()
    {
        string? err = MarketPackageRules.CheckCanPublish("alice/tide-note", Manifest(999, "alice"), MyId, MyLogin);
        Assert.NotNull(err);
        Assert.Contains("alice", err);
        Assert.Contains("只有原作者", err);
    }

    [Fact]
    public void 已有包_是自己的_允许更新()
        => Assert.Null(MarketPackageRules.CheckCanPublish("timecolors/tide-note", Manifest(MyId, MyLogin), MyId, MyLogin));

    [Fact]
    public void 已有包_缺归属信息_一律拒绝而不是猜作者名()
    {
        // 早期数据没有 publisherId。**不能**退化去比 author 字符串 —— 那可以随便伪造。
        // fail-closed：拒绝覆盖，并告诉用户走 PR。
        string? err = MarketPackageRules.CheckCanPublish("official-pack", "{ \"id\": \"official-pack\", \"author\": \"timecolors\" }", MyId, MyLogin);
        Assert.NotNull(err);
        Assert.Contains("归属信息", err);
        Assert.Contains("PR", err);
    }

    [Fact]
    public void 已有包_manifest坏掉_也拒绝()
        => Assert.NotNull(MarketPackageRules.CheckCanPublish("x/y", "{ 这不是 JSON", MyId, MyLogin));

    [Fact]
    public void 伪造author冒充原作者_无效()
    {
        // 有人把 author 写成我的登录名，但 publisherId 不是我的 → 仍然拒绝
        string? err = MarketPackageRules.CheckCanPublish("timecolors/stolen", Manifest(777, MyLogin), MyId, MyLogin);
        Assert.NotNull(err);
    }

    // ==================== 归属解析 ====================

    [Theory]
    [InlineData("{ \"publisherId\": 42, \"author\": \"bob\" }", 42L, "bob")]
    [InlineData("{ \"publisherId\": \"42\", \"author\": \"bob\" }", 42L, "bob")]   // 字符串数字也认
    [InlineData("{ \"author\": \"bob\" }", 0L, "bob")]                            // 缺 publisherId
    [InlineData("", 0L, "")]
    public void 归属解析_容错(string json, long expectedId, string expectedAuthor)
    {
        var (id, author) = MarketPackageRules.ParseOwner(json);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedAuthor, author);
    }
}
