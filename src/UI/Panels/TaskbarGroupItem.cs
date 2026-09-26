using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ShoreHue.UI.Panels
{
    /// <summary>任务栏上的「分组标签」：组内图标拼版，鼠标放上展开、移走收起。</summary>
    public class TaskbarGroupItem
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>直接成员（窗口标签 / 嵌套分组）。</summary>
        public List<object> Members { get; set; } = new();
        /// <summary>用于图标拼版的窗口（最多 4 个）。</summary>
        public List<TaskbarItem> Preview { get; set; } = new();
        /// <summary>组里套了组（第 2 层）—— 打开时给彩蛋提示。</summary>
        public bool IsNested { get; set; }
        /// <summary>组内窗口总数（含嵌套组的）—— 角标显示用。</summary>
        public int WindowCount { get; set; }
    }

    /// <summary>窗口标签与分组标签用不同模板渲染。</summary>
    public sealed class TaskbarTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? WindowTemplate { get; set; }
        public DataTemplate? GroupTemplate { get; set; }
        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
            => item is TaskbarGroupItem ? GroupTemplate : WindowTemplate;
    }
}