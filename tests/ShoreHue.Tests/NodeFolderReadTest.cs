using ShoreHue.UI.Settings.Pages;
using ShoreHue.Core.Models;
using ShoreHue.Infrastructure.Utils;
using Xunit;
using System;
using System.IO;
using System.Text.Json;

namespace ShoreHue.Tests
{
    /// <summary>验证：海床节点 → seabed 文件夹的内容读取（文件夹即真相源，新布局：面板/小组件、面板/面板功能、面板/状态栏）。</summary>
    [Collection("WidgetStore")]
    public class NodeFolderReadTest : IDisposable
    {
        private string _tmp = "";

        private string Root => ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.RootDir;

        private void Setup(params (string Rel, string Content)[] files)
        {
            _tmp = Path.Combine(Path.GetTempPath(), "shorehue-nfrt-" + Guid.NewGuid().ToString("N"));
            AppPaths.TestDataRoot = _tmp;
            foreach (var f in files)
            {
                string p = Path.Combine(_tmp, "seabed", f.Rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, f.Content);
            }
        }

        public void Dispose()
        {
            AppPaths.TestDataRoot = null;
            try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, true); } catch { }
        }

        [Fact]
        public void WidgetNode_ReadsRealXaml()
        {
            string xaml = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel/></UserControl>";
            string cs = "public partial class TimerWidget : UserControl, IWidget { public TimerWidget(){InitializeComponent();} public string Name => \"计时器\"; public UserControl CreateView() => this; public void OnActivated(){} public void OnDeactivated(){} }";
            Setup(
                ("面板/timer/timer.xaml", xaml),
                ("面板/timer/timer.xaml.cs", cs),
                ("面板/timer/manifest.json", "{\"kind\":\"Widget\",\"system\":true}")
            );
            var node = new ConfigNode { Key = "widget-timer", Name = "计时器", Category = "小组件" };
            var (src, x, xc, cfg) = SeabedPageAccess.LoadNodeFromFolder(node, null);
            Assert.True(x.Contains("<UserControl"), "应读到真实 XAML，实际: " + x);
            Assert.True(xc.Contains("partial class"), "应读到 xaml.cs");
        }

        [Fact]
        public void PanelNode_ReadsMainCs()
        {
            Setup(("面板/panel-notification/main.cs", "public class Panel { }"));
            var node = new ConfigNode { Key = "panel-notification", Name = "通知坞", Category = "面板功能" };
            var (src, x, xc, cfg) = SeabedPageAccess.LoadNodeFromFolder(node, null);
            Assert.False(string.IsNullOrEmpty(src), "应读到 main.cs");
        }

        [Fact]
        public void StatusNode_ReadsConfig()
        {
            Setup(("面板/状态栏/天气/config.json", "{\"WeatherCity\":\"北京\"}"));
            var node = new ConfigNode { Key = "status-weather", Name = "天气", Category = "面板" };
            var (src, x, xc, cfg) = SeabedPageAccess.LoadNodeFromFolder(node, null);
            Assert.False(string.IsNullOrEmpty(cfg), "应读到 config.json");
        }
    }
}
