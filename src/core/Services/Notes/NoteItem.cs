using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShoreHue.Core.Services
{
    /// <summary>
    /// 便签数据模型。
    /// ★ 必须实现 INotifyPropertyChanged：界面上的标签名（`{Binding Title}`）、当前高亮
    ///   （`{Binding IsCurrent}`）、颜色（`{Binding ColorBrush}` 的来源）全靠绑定这个对象。
    ///   以前它是纯 POCO —— 属性改了没人通知，于是"重命名后标签名一直是旧的""切换便签高亮不动"，
    ///   只有增删便签导致列表容器重建时才偶然对一次（真机反馈）。
    /// ★ 序列化不受影响：仍是同名公开属性（System.Text.Json 只认公开属性，与 INPC 无关）。
    /// </summary>
    public class NoteItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _title = "";
        private string _content = "";
        private string _color = "#00000000";   // 透明 = 跟随面板
        private DateTime _createTime = DateTime.Now;
        private DateTime _updateTime = DateTime.Now;
        private bool _showTitle = true;
        private bool _isCurrent;
        private bool _pinned;

        public string Id
        {
            get => _id;
            set => Set(ref _id, value);
        }

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public string Content
        {
            get => _content;
            set
            {
                if (!Set(ref _content, value)) return;
                // 第一行/预览都是正文的投影 → 正文一变必须一起通知，否则芯片上的标题不刷新
                Raise(nameof(FirstLine));
                Raise(nameof(Preview));
            }
        }

        public string Color
        {
            get => _color;
            set => Set(ref _color, value);
        }

        public DateTime CreateTime
        {
            get => _createTime;
            set => Set(ref _createTime, value);
        }

        public DateTime UpdateTime
        {
            get => _updateTime;
            set => Set(ref _updateTime, value);
        }

        public bool ShowTitle
        {
            get => _showTitle;
            set => Set(ref _showTitle, value);
        }

        public bool IsCurrent
        {
            get => _isCurrent;
            set => Set(ref _isCurrent, value);
        }

        /// <summary>置顶（便笺列表里排在最前；与 Google Keep 的"置顶"一致）。</summary>
        public bool Pinned
        {
            get => _pinned;
            set => Set(ref _pinned, value);
        }

        /// <summary>
        /// 便签的"标题" = **正文的第一行非空文本**（Windows 便笺 / Google Keep 的成熟做法：
        /// 不单独设标题字段，改标题就是改第一行）。列表、标签都显示它。
        /// ★ 不参与序列化（是正文的投影，存两份必然不一致）。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string FirstLine
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_content)) return "";
                foreach (var raw in _content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length > 0) return line.Length > 60 ? line.Substring(0, 60) : line;
                }
                return "";
            }
        }

        /// <summary>悬停预览：正文压成一行、截断到 200 字（芯片 ToolTip 用）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Preview
        {
            get
            {
                string flat = _content.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
                while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");
                return flat.Length > 200 ? flat.Substring(0, 200) + "…" : flat;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
