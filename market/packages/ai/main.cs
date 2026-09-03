using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.AI;
using ShoreHue.UI.Widgets;

namespace ShoreHue.Builtin
{
    // AI 助手（薄封装）：复用 ShoreHue 内置 AI 聊天面板（流式对话/文件/输出到光标）。
    // 想自定义：把 root 替换为你自己的布局，在 ai 前后叠加内容。
    public class AiPanel : UserControl, IWidget
    {
        public AiPanel()
        {
            var ai = new AiChatView();
            Content = ai;
        }

        public string Name => "AI 助手";
        public UserControl CreateView() => this;
        public void OnActivated() { }
        public void OnDeactivated() { }
    }
}