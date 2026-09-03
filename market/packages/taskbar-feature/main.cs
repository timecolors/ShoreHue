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
            var shortcuts = ServiceManager.Instance.GetService<ShortcutManager>() as IShortcutService;
            var settings = ServiceManager.Instance.GetService<SettingsManager>() as ISettingsService;
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

            // 示例：外层加一行自定义标题（不需要可删掉）
            var header = new TextBlock
            {
                Text = "我的任务栏",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(138, 138, 138)),
                Margin = new Thickness(4, 2, 4, 4)
            };

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(inner);
            Content = root;
        }

        public string Name => "任务栏";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}