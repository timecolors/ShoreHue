using System.Threading.Tasks;

namespace ShoreHue.UI.Widgets
{
    /// <summary>
    /// 划词翻译小组件的宿主契约（可选接口）。
    /// ★ 为什么用接口而不是具体类型：内置件改为从 seabed 文件夹加载后，Roslyn 编译出来的类型
    ///   与 exe 里那个同名类型**不是同一个**；宿主按具体类型拿会拿到"另一个实例"
    ///   （热键翻译的不是屏幕上那个，界面也不会更新）。
    /// </summary>
    public interface ITextAiWidget
    {
        /// <summary>读取当前选中文本并翻译（宿主热键触发）。</summary>
        Task CaptureAndTranslateAsync();
    }
}