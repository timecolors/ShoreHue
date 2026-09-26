// ==================== 通知坞的"分组 / 单条关闭"纯逻辑 ====================
//
// 为什么单独一个纯类：通知数据来自宿主（`ToastMonitor.Notifications`，静态、不可注入），
// 如果逻辑写在面板的绘制代码里，**测试就拿不到它**（本会话的规矩：只有"能自测"的功能才算做完）。
// 抽成纯函数后，可以喂假数据断言分组与关闭行为，面板只负责把结果画出来。
//
// 借鉴 Windows 通知中心：**按应用分组**（同一应用的几条摞在一起）、**单条关闭**（✕ 只关这一条）。
// 关闭记忆留在面板实例内：宿主其实提供了单条移除（HostCapabilities.RemoveNotification），面板会再调它
// 让"重开面板也不再出现"；本地这份记忆保证"移除失败/宿主事件延迟"时界面立刻正确，也让纯逻辑可单测。

using System;
using System.Collections.Generic;
using System.Linq;

namespace ShoreHue.Builtin.Notifications
{
    /// <summary>一条通知的展示数据（面板把宿主模型投影成它，纯逻辑只认这个）。</summary>
    public readonly struct NotificationRow
    {
        public NotificationRow(string key, string app, string message, string time, bool isRead = false)
        {
            Key = key;
            App = app;
            Message = message;
            Time = time;
            IsRead = isRead;
        }

        /// <summary>稳定标识（用于"关闭"记忆；宿主模型没有 id 时由面板用 应用+内容+时间 合成）。</summary>
        public string Key { get; }
        public string App { get; }
        public string Message { get; }
        public string Time { get; }
        public bool IsRead { get; }
    }

    public sealed class NotificationGroup
    {
        public NotificationGroup(string app, IReadOnlyList<NotificationRow> rows)
        {
            App = app;
            Rows = rows;
        }

        public string App { get; }
        public IReadOnlyList<NotificationRow> Rows { get; }

        /// <summary>组头文案（应用名 + 条数）。宿主视图与海床模板共用，避免两处各拼一份。</summary>
        public string AppLabel => App + "（" + Rows.Count + "）";
    }

    public static class NotificationGrouping
    {
        /// <summary>按应用分组：组内按原顺序（宿主给的通常是新的在前），组间按"最近一条"排序。</summary>
        public static List<NotificationGroup> Group(IEnumerable<NotificationRow> rows, ISet<string>? dismissed = null)
        {
            var visible = (rows ?? Enumerable.Empty<NotificationRow>())
                .Where(r => r.Key.Length > 0 && (dismissed == null || !dismissed.Contains(r.Key)));

            return visible
                .GroupBy(r => string.IsNullOrWhiteSpace(r.App) ? "其他" : r.App)
                .Select(g => new NotificationGroup(g.Key, g.ToList()))
                .ToList();
        }

        /// <summary>关闭一条（返回是否真的新增了关闭标记；重复关闭返回 false，避免无意义重绘）。</summary>
        public static bool Dismiss(ISet<string> dismissed, NotificationRow row)
        {
            if (dismissed == null || row.Key.Length == 0) return false;
            return dismissed.Add(row.Key);
        }

        /// <summary>把宿主模型投影成展示行；缺字段时给出稳定兜底，保证 Key 唯一且可复现。</summary>
        public static NotificationRow Row(string? app, string? message, string? time)
        {
            string a = string.IsNullOrWhiteSpace(app) ? "系统" : app!.Trim();
            string m = (message ?? "").Trim();
            string t = (time ?? "").Trim();
            return new NotificationRow(a + "\u0001" + m + "\u0001" + t, a, m, t);
        }

        /// <summary>清空/刷新后，已关闭的记忆要跟着淘汰（否则同一条新通知会被旧标记误吞）。</summary>
        public static void PruneDismissed(ISet<string> dismissed, IEnumerable<NotificationRow> current)
        {
            if (dismissed == null) return;
            var live = new HashSet<string>((current ?? Enumerable.Empty<NotificationRow>()).Select(r => r.Key), StringComparer.Ordinal);
            foreach (var k in dismissed.Where(k => !live.Contains(k)).ToList()) dismissed.Remove(k);
        }
    }
}
