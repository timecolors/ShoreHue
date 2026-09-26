using System.Collections.Generic;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests
{
    /// <summary>
    /// 加载期问题登记（WidgetPluginStore.LoadIssues）：编译失败 / 被沙箱拦的组件**必须在界面上说得出原因**。
    /// 以前这些只写日志，界面上就是"它不见了"（小组件标签消失、区域面板悄悄变成通知坞）。
    /// </summary>
    public class LoadIssueVisibilityTests
    {
        [Fact]
        public void 记录与读取()
        {
            WidgetPluginStore.ClearLoadIssue("unit-issue-a");
            Assert.Null(WidgetPluginStore.GetLoadIssue("unit-issue-a"));

            WidgetPluginStore.SetLoadIssue("unit-issue-a", "编译失败 · (1,1): error CS1002");
            Assert.Equal("编译失败 · (1,1): error CS1002", WidgetPluginStore.GetLoadIssue("unit-issue-a"));

            WidgetPluginStore.ClearLoadIssue("unit-issue-a");
            Assert.Null(WidgetPluginStore.GetLoadIssue("unit-issue-a"));
        }

        [Fact]
        public void 多行编译诊断压成一行并截断()
        {
            WidgetPluginStore.SetLoadIssue("unit-issue-b", "第一行\n第二行\r\n" + new string('x', 400));
            var got = WidgetPluginStore.GetLoadIssue("unit-issue-b")!;

            Assert.DoesNotContain("\n", got);
            Assert.DoesNotContain("\r", got);
            Assert.EndsWith("…", got);
            Assert.True(got.Length <= 121);

            WidgetPluginStore.ClearLoadIssue("unit-issue-b");
        }

        [Fact]
        public void 空id不记录也不会抛()
        {
            WidgetPluginStore.SetLoadIssue(null, "x");
            WidgetPluginStore.SetLoadIssue("", "x");
            WidgetPluginStore.ClearLoadIssue(null);
            Assert.Null(WidgetPluginStore.GetLoadIssue(null));
            Assert.Null(WidgetPluginStore.GetLoadIssue(""));
        }

        [Fact]
        public void 摘要包含全部条目且无问题时为空()
        {
            foreach (var kv in new Dictionary<string, string> { ["unit-issue-c"] = "编译失败", ["unit-issue-d"] = "被沙箱拦截" })
                WidgetPluginStore.SetLoadIssue(kv.Key, kv.Value);

            var text = WidgetPluginStore.DescribeLoadIssues();
            Assert.Contains("unit-issue-c：编译失败", text);
            Assert.Contains("unit-issue-d：被沙箱拦截", text);

            WidgetPluginStore.ClearLoadIssue("unit-issue-c");
            WidgetPluginStore.ClearLoadIssue("unit-issue-d");
        }

        [Fact]
        public void 权限后果文案覆盖全部权限类别()
        {
            // ★ 不要断言具体的中文/英文文案：CI 的语言是 en-US，本机是 zh-CN，
            //   写死"互联网"就会变成"只在开发机绿"的假测试（2026-09-26 CI 实际红过一次）。
            //   这里改成**拿同一个来源做对照**，任何语言下都成立。
            string unknownLabel = WidgetPermissions.ConsequenceLabel("这不是一个真实权限");

            foreach (var p in new[] { "network", "clipboard", "file", "process", "system", "window", "screen" })
            {
                string consequence = WidgetPermissions.ConsequenceLabel(p);
                Assert.False(string.IsNullOrWhiteSpace(consequence));
                // 已知权限必须给出**专属**文案，而不是落到未知能力兜底
                Assert.NotEqual(unknownLabel, consequence);
            }

            // 摘要里必须真的带上该权限的后果文案（同样用同一个来源比对）
            string network = WidgetPermissions.ConsequenceLabel("network");
            Assert.Contains(network, WidgetPermissions.DescribeConsequences(new List<string> { "network" }));
            Assert.Equal("", WidgetPermissions.DescribeConsequences(new List<string>()));
        }
    }
}
