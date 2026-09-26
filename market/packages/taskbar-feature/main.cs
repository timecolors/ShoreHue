using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 任务栏（薄封装）：直接复用 ShoreHue 内置任务栏（快捷方式/运行中窗口/拖拽排序）。
    // 想自定义：在 root 里叠加你自己的头部/装饰/按钮即可；想完全重写，把 inner 换成你的代码。
    public class TaskbarPanel : UserControl, IWidget
    {
        public TaskbarPanel()
        {
            // 从服务容器取内置服务（运行在 ShoreHue 内时已注册；独立验证环境无服务时显示占位而非崩溃）
            // ★ 安全 v2：外来来源只能用窄接口
            var shortcuts = ShoreHue.UI.Widgets.HostCapabilities.Shortcuts;
            var settings = ShoreHue.UI.Widgets.HostCapabilities.Settings;
            if (shortcuts == null || settings == null)
            {
                Content = new TextBlock
                {
                    Text = "任务栏需在 ShoreHue 应用内运行（服务未就绪）",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
            var inner = new TaskbarView(shortcuts, settings);
            // ★ 不加自定义标题行：它挤占标签空间，属多余装饰
            Content = inner;
        }

        public string Name => "任务栏";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}