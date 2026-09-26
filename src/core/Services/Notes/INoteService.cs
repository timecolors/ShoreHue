using ShoreHue.Core.Services;
using System;
using System.Collections.ObjectModel;

namespace ShoreHue.src.core.Services.Notes
{
    /// <summary>
    /// 便签服务接口
    /// </summary>
    public interface INoteService
    {
        /// <summary>
        /// 便签列表
        /// </summary>
        ObservableCollection<NoteItem> Notes { get; }

        /// <summary>
        /// 当前便签
        /// </summary>
        NoteItem? CurrentNote { get; }

        /// <summary>
        /// 载入时数据文件损坏（已留档、未删除）时为一句可显示的话，否则 null。
        /// ★ 需要有这个属性，是因为"解析失败"以前只能写日志 —— 用户看到的是"便签全没了"却不知原因。
        /// </summary>
        string? LoadWarning { get; }

        /// <summary>
        /// 便签变化事件
        /// </summary>
        event EventHandler? NotesChanged;

        /// <summary>
        /// 设置当前便签
        /// </summary>
        void SetCurrentNote(NoteItem? note);

        /// <summary>
        /// 创建便签
        /// </summary>
        NoteItem CreateNote(string? title = null, string? color = null);

        /// <summary>
        /// 删除便签
        /// </summary>
        void DeleteNote(NoteItem note);

        /// <summary>
        /// 更新便签内容
        /// </summary>
        void UpdateNoteContent(NoteItem note, string content);

        /// <summary>
        /// 更新便签标题
        /// </summary>
        void UpdateNoteTitle(NoteItem note, string title);

        /// <summary>
        /// 更新便签颜色
        /// </summary>
        void UpdateNoteColor(NoteItem note, string color);

        /// <summary>
        /// 更新便签标题显示
        /// </summary>
        void UpdateNoteShowTitle(NoteItem note, bool showTitle);

        /// <summary>
        /// 保存便签
        /// </summary>
        void Save();
    }
}