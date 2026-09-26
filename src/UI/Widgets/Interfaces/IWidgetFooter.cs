using System.Windows;

namespace ShoreHue.UI.Widgets
{
    /// <summary>
    /// 可选的"小组件页脚"契约：小组件可以在面板底部挂一条自己的控件（如计时器的按钮条）。
    ///
    /// 为什么不直接加进 `IWidget`：那是**对外契约**（市场包也实现它），加成员会让所有
    /// 已发布的插件编译不过。这里用"可选接口 + `as` 探测"：不实现就没有页脚，老插件零影响。
    ///
    /// ★ 它同时补上了一个**暗契约**：宿主过去是直接调具体类的 `GetFooterControl()` 的
    ///   （`WidgetSwitcher` 里的 5 个 case），那只在"内置件编译进 exe"时成立；
    ///   内置件改为从 seabed 文件夹加载后，宿主只能按接口认它 —— 这就是内置件模块化必须补的一环。
    /// </summary>
    public interface IWidgetFooter
    {
        /// <summary>页脚控件。</summary>
        FrameworkElement GetFooterControl();
    }
}
