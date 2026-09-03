using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // 窗口控制（薄封装）：复用 ShoreHue 内置窗口操作中心（最小化/最大化/关闭/置顶）。
    public class WindowControlPanel : UserControl, IWidget
    {
        public WindowControlPanel()
        {
            Content = new WindowControlView();
        }

        public string Name => "窗口控制";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}