using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// Jump List 命令的单实例转发/执行：
    /// - 启动带动作参数时：若已有实例在运行（单实例 Mutex 被占用）→ 通过命名事件通知其执行动作；
    /// - 无已有实例 → 当前进程执行动作（启动后由 MainWindow 消费）。
    /// 已运行实例启动时注册监听命名事件，收到动作立即执行（打开设置/切换勿扰/呼出面板）。
    /// </summary>
    public static class JumpListCommand
    {
        // ★ 命名事件：已运行实例监听；新实例发信号（传参数用共享内存文件，见 ForwardActions）
        private const string EventName = @"Global\ShoreHue_JumpList_Command";
        // 共享内存文件：写入待执行动作（新实例 → 已运行实例）
        private static readonly string MemFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ShoreHue_jumplist_cmd.txt");

        private static EventWaitHandle? _incomingEvent;

        /// <summary>已在运行的实例调用：注册监听，收到 Jump List 动作立即执行。</summary>
        public static void Listen(Action<IReadOnlyList<string>> onActions)
        {
            try
            {
                _incomingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out _);
                // 后台线程轮询事件（非阻塞等待信号）
                Task.Run(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _incomingEvent.WaitOne();
                            var actions = ReadActions();
                            if (actions.Count > 0)
                            {
                                // 必须在 UI 线程执行（操作窗口/模式服务）
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                                {
                                    try { onActions(actions); }
                                    catch (Exception ex) { ShoreHue.Core.Infrastructure.Logging.LogManager.Error("执行 Jump List 动作失败", ex); }
                                });
                            }
                        }
                        catch { /* 事件句柄释放等异常忽略 */ }
                    }
                });
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("注册 Jump List 监听失败", ex);
            }
        }

        /// <summary>
        /// 启动带动作参数时调用：优先通知已运行实例；无实例（单实例 Mutex 未占用）→ 返回 true 表示当前进程执行。
        /// </summary>
        public static bool ForwardOrExecute(string[] args)
        {
            var actions = JumpListManager.ParseActions(args);
            if (actions.Count == 0) return false;   // 无动作：正常启动

            try
            {
                // ★ 尝试通知已有实例（与 App 单实例 Mutex 同名判断：能拿到事件 = 已有实例在监听）
                bool created;
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out created);
                if (!created)
                {
                    // 已有实例在监听：写共享内存 + 发信号
                    WriteActions(actions);
                    ev.Set();
                    return false;   // 动作已转发，当前进程正常退出（App 单实例逻辑会处理）
                }
            }
            catch { /* 无监听者/事件创建失败 → 当前进程执行 */ }

            // 无已有实例：返回 true，启动完成后由 MainWindow 执行动作
            _pendingStartupActions = actions;
            return true;
        }

        private static List<string> _pendingStartupActions = new();

        /// <summary>无已有实例时：App 启动后取回待执行动作（MainWindow 初始化后调用）。</summary>
        public static IReadOnlyList<string> TakePendingStartupActions()
        {
            var copy = _pendingStartupActions;
            _pendingStartupActions = new List<string>();
            return copy;
        }

        private static void WriteActions(List<string> actions)
        {
            try
            {
                System.IO.File.WriteAllText(MemFile, string.Join("\n", actions));
            }
            catch (Exception ex)
            {
                // 跨进程转发动作失败 → 已有实例收不到这次 Jump List 动作（用户点了没反应）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[跳转列表] 写入待执行动作失败（动作会丢失）：{ex.Message}");
            }
        }

        private static List<string> ReadActions()
        {
            var result = new List<string>();
            try
            {
                if (System.IO.File.Exists(MemFile))
                {
                    string text = System.IO.File.ReadAllText(MemFile);
                    foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!result.Contains(line)) result.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                // 读不到 = 启动时的 Jump List 动作丢失（返回空列表继续启动）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[跳转列表] 读取待执行动作失败（本次动作丢失）：{ex.Message}");
            }
            return result;
        }
    }
}
