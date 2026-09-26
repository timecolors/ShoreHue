using System;

namespace ShoreHue.Core.Models
{
    /// <summary>
    /// 用户自定义面板定义（海床/编程模式创建）。
    /// 复制原面板为副本、或新建面板，保存后注册到"区域→面板"下拉。
    /// </summary>
    public class CustomPanelDefinition
    {
        /// <summary>唯一标识（如 "panel_xxx"）。</summary>
        public string Id { get; set; } = "";

        /// <summary>显示名。</summary>
        public string Name { get; set; } = "";

        /// <summary>所属一级分类（面板设计/动画/外观/交互/状态栏）。</summary>
        public string Category { get; set; } = "面板设计";

        /// <summary>内容类型基线（Taskbar/Widget/AI/Notification…，用于实例化内容）。</summary>
        public string BaseType { get; set; } = "Widget";

        /// <summary>种类：空/"Panel"=自定义面板（编译后进区域面板下拉）；"Config"=配置代码项（仅海床内编辑，不进下拉）。</summary>
        public string Kind { get; set; } = "";

        /// <summary>所属分组节点 Key（在树的哪个分组下；如 "panel-widgets"=小组件下）。</summary>
        public string ParentKey { get; set; } = "";

        /// <summary>该面板的配置（JSON 片段，随设置应用）。</summary>
        public string ConfigJson { get; set; } = "{}";

        /// <summary>面板源码（C#，实现 ShoreHue.UI.Widgets.IWidget 接口，动态编译运行）。</summary>
        public string Source { get; set; } = "";
        /// <summary>完全编程：XAML 界面源码（<名字>.xaml）。与 XamlCs 配套，存在时编译走 CompileXaml。</summary>
        public string Xaml { get; set; } = "";
        /// <summary>完全编程：XAML 代码后置（<名字>.xaml.cs，partial class + 事件处理器）。</summary>
        public string XamlCs { get; set; } = "";

        /// <summary>来源节点 Key（保存当前节点时记录：该变体替代/覆盖的内置节点）。</summary>
        public string SourceKey { get; set; } = "";

        /// <summary>创建时间。</summary>
        public string CreatedAt { get; set; } = "";

        /// <summary>
        /// 本地面板对应**市场里**的那个包 ID（形如 `登录名/短名`，发布成功后回写；未发布为空）。
        /// 用途：更新同一个包时自动填对 ID，用户不必手打（手打很容易变成"又发了一个新包"或撞别人的 ID）。
        /// </summary>
        public string MarketId { get; set; } = "";

        /// <summary>
        /// 来源是否可信（本地自写）：true = 完全权限（本地自用模型）；false = 来自「其他海床」市场，
        /// 编译时走沙箱（WidgetCompiler.CheckSandbox 拦截 Process/反射/注册表/窗口/屏幕/剪贴板/文件写等危险 API）。
        /// 旧数据缺省 true（本地创建）。
        /// </summary>
        public bool TrustedSource { get; set; } = true;
    }
}
