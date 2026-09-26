using ShoreHue.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ShoreHue.src.core.Services.Clipboard
{
    /// <summary>
    /// 剪贴板服务接口
    /// </summary>
    public interface IClipboardService
    {
        /// <summary>
        /// 剪贴板历史列表
        /// </summary>
        ObservableCollection<ClipboardManager.ClipboardItem> History { get; }

        /// <summary>
        /// 历史变化事件
        /// </summary>
        event EventHandler? HistoryChanged;

        /// <summary>
        /// 开始监听剪贴板
        /// </summary>
        void StartListening();

        /// <summary>
        /// 停止监听剪贴板
        /// </summary>
        void StopListening();

        /// <summary>
        /// 删除单条记录
        /// </summary>
        void RemoveItem(ClipboardManager.ClipboardItem item);

        /// <summary>
        /// 删除多条记录
        /// </summary>
        void RemoveItems(IEnumerable<ClipboardManager.ClipboardItem> items);

        /// <summary>
        /// 清空全部
        /// </summary>
        void ClearAll();

        /// <summary>
        /// 复制到剪贴板
        /// </summary>
        void CopyToClipboard(ClipboardManager.ClipboardItem item);

        /// <summary>只把纯文本回填到剪贴板（借鉴 Win+V 的"粘贴为纯文本"：网页/编辑器复制来的内容常带一堆格式）。</summary>
        void CopyToClipboardPlainText(ClipboardManager.ClipboardItem item);

        /// <summary>
        /// 收藏/取消收藏（收藏条目不被自动清理）
        /// </summary>
        void SetPinned(ClipboardManager.ClipboardItem item, bool pinned);

        /// <summary>
        /// 保存拖入的文件
        /// </summary>
        bool SaveDroppedFile(string sourcePath, string targetFolder);
    }
}