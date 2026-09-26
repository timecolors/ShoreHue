using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services.Configuration;

namespace ShoreHue.UI.Widgets.Dynamic
{
    /// <summary>
    /// 插件运行时守卫 = **安全模式 + 异常熔断**。
    /// 借鉴 Windhawk 的"缩小爆炸半径 + 恢复通道"：插件跑在**进程内**，出问题时宿主必须能自愈 ——
    /// 否则一个连续异常的插件会把整个界面拖死，用户连"关掉它"的开关都点不到。
    ///   · 安全模式：本次运行不加载任何海床插件/覆盖，只跑 exe 内置实现（唯一可靠的救命通道）；
    ///   · 异常熔断：窗口内累计失败达阈值 → 自动停用该插件、落盘、并让各界面重建摘掉它。
    /// </summary>
    internal static class PluginRuntimeGuard
    {
        /// <summary>安全模式：本次运行不加载任何海床插件/覆盖。</summary>
        public static bool SafeMode { get; private set; }

        /// <summary>本次进入安全模式的原因（给界面显示用；未进入时为空串）。
        /// ★ 以前只把原因写进日志，界面只弹一句「安全模式」，用户不知道**为什么**进来的、
        ///   也就无从判断该不该担心（真机实测过：日志写成「连续 0 次未正常退出」，看起来像误判）。</summary>
        public static string SafeModeReason { get; private set; } = "";

        /// <summary>熔断阈值：窗口内累计失败达到这个数 → 停用该插件。</summary>
        public const int TripThreshold = 3;
        /// <summary>熔断窗口（秒）：超窗则重新计数。</summary>
        public const int WindowSeconds = 60;

        private sealed class Fail
        {
            public int Count;
            public DateTime LastUtc;
        }

        private static readonly object _gate = new();
        private static readonly Dictionary<string, Fail> _failures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _trippedThisRun = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _persisted = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>插件程序集 → 插件 key（编译成功时登记；异常时按调用栈反查是哪个插件）。</summary>
        private static readonly Dictionary<Assembly, string> _pluginAssemblies = new();

        /// <summary>某个插件刚被熔断（参数 = 插件 key）；订阅方负责提示/重建。</summary>
        public static event Action<string>? Tripped;

        public static void EnterSafeMode(string reason)
        {
            SafeMode = true;
            SafeModeReason = reason ?? "";
            LogManager.Warning($"[守卫] 已进入安全模式：本次不加载任何海床插件/覆盖（只跑 exe 内置实现）。原因：{reason}");
        }

        /// <summary>启动时配置：安全模式 + 持久化的熔断名单。</summary>
        public static void Configure(bool safeMode, IEnumerable<string>? persisted, string reason)
        {
            lock (_gate)
            {
                _persisted.Clear();
                if (persisted != null)
                    foreach (var id in persisted) if (!string.IsNullOrWhiteSpace(id)) _persisted.Add(id);
            }
            if (safeMode) EnterSafeMode(reason);
        }

        /// <summary>编译成功后登记"程序集 → 插件 key"。</summary>
        public static void RegisterAssembly(Assembly? assembly, string pluginKey)
        {
            if (assembly == null || string.IsNullOrWhiteSpace(pluginKey)) return;
            lock (_gate) _pluginAssemblies[assembly] = pluginKey;
        }

        /// <summary>该插件是否应被跳过（安全模式 / 持久熔断 / 本进程已熔断）。</summary>
        public static bool IsDisabled(string? pluginKey)
        {
            if (string.IsNullOrWhiteSpace(pluginKey)) return false;
            if (SafeMode) return true;
            lock (_gate) return _persisted.Contains(pluginKey!) || _trippedThisRun.Contains(pluginKey!);
        }

        public static bool IsTripped(string? pluginKey)
        {
            if (string.IsNullOrWhiteSpace(pluginKey)) return false;
            lock (_gate) return _persisted.Contains(pluginKey!) || _trippedThisRun.Contains(pluginKey!);
        }

        /// <summary>从异常调用栈反查插件 key（按已登记的插件程序集匹配；未识别返回 null）。</summary>
        public static string? IdentifyPlugin(Exception? ex)
        {
            if (ex == null) return null;
            try
            {
                foreach (var f in new StackTrace(ex, false).GetFrames())
                {
                    var asm = f.GetMethod()?.DeclaringType?.Assembly;
                    if (asm == null) continue;
                    lock (_gate) if (_pluginAssemblies.TryGetValue(asm, out var key)) return key;
                }
                var ts = ex.TargetSite?.DeclaringType?.Assembly;
                if (ts != null) lock (_gate) if (_pluginAssemblies.TryGetValue(ts, out var key)) return key;
            }
            catch { /* 取栈失败不影响主流程 */ }
            return null;
        }

        /// <summary>报告一次插件异常；返回识别出的插件 key（未识别返回 null）。触发熔断时会停用并落盘。</summary>
        public static string? ReportException(Exception? ex, string phase)
        {
            string? key = IdentifyPlugin(ex);
            if (key == null) return null;
            ReportFailure(key, phase, ex?.Message ?? "");
            return key;
        }

        /// <summary>报告一次插件失败（异常/超时）；返回是否因此触发熔断。</summary>
        public static bool ReportFailure(string pluginKey, string phase, string message)
        {
            if (string.IsNullOrWhiteSpace(pluginKey)) return false;
            bool trippedNow = false;
            int count;
            lock (_gate)
            {
                if (_trippedThisRun.Contains(pluginKey) || _persisted.Contains(pluginKey)) return false;
                var now = DateTime.UtcNow;
                if (!_failures.TryGetValue(pluginKey, out var f) || (now - f.LastUtc).TotalSeconds > WindowSeconds)
                {
                    f = new Fail();
                    _failures[pluginKey] = f;
                }
                f.Count++;
                f.LastUtc = now;
                count = f.Count;
                if (count >= TripThreshold)
                {
                    _trippedThisRun.Add(pluginKey);
                    _persisted.Add(pluginKey);
                    trippedNow = true;
                }
            }

            if (!trippedNow)
            {
                LogManager.Warning($"[守卫] 插件 {pluginKey} 在「{phase}」失败 {count}/{TripThreshold} 次：{message}");
                return false;
            }

            LogManager.Error($"[守卫] 插件 {pluginKey} 连续 {count} 次失败，已安全熔断停用（{phase}：{message}）");
            WidgetPluginStore.SetLoadIssue(pluginKey, $"已安全熔断停用（在「{phase}」连续失败 {count} 次）· 最近：{message}");
            Persist();
            try { WidgetPluginStore.NotifyChanged(); } catch { /* 通知失败不影响已经生效的停用 */ }
            try { Tripped?.Invoke(pluginKey); } catch { /* 订阅方异常不能反过来拖垮守卫 */ }
            return true;
        }

        /// <summary>用户手动解除某个插件的熔断（并清计数）。</summary>
        public static void Clear(string pluginKey)
        {
            lock (_gate)
            {
                _persisted.Remove(pluginKey);
                _trippedThisRun.Remove(pluginKey);
                _failures.Remove(pluginKey);
            }
            WidgetPluginStore.ClearLoadIssue(pluginKey);
            Persist();
            try { WidgetPluginStore.NotifyChanged(); } catch { }
        }

        /// <summary>解除全部熔断（托盘菜单）；返回解除的数量。</summary>
        public static int ClearAll()
        {
            int n;
            lock (_gate)
            {
                n = _persisted.Count;
                _persisted.Clear();
                _trippedThisRun.Clear();
                _failures.Clear();
            }
            Persist();
            try { WidgetPluginStore.NotifyChanged(); } catch { }
            return n;
        }

        public static IReadOnlyList<string> TrippedKeys
        {
            get { lock (_gate) return new List<string>(_persisted); }
        }

        /// <summary>把熔断名单写回设置（拿不到设置服务就只保留在内存，本次停用仍然生效）。</summary>
        private static void Persist()
        {
            try
            {
                var settings = ServiceManager.Instance.GetService<SettingsManager>();
                if (settings is ISettingsService s)
                {
                    lock (_gate) s.CircuitBrokenPlugins = new List<string>(_persisted);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[守卫] 保存熔断名单失败（本次仍已停用）：{ex.Message}");
            }
        }

        /// <summary>测试用：重置全部运行时状态。</summary>
        internal static void ResetAllForTests()
        {
            lock (_gate)
            {
                SafeMode = false;
                SafeModeReason = "";
                _failures.Clear();
                _trippedThisRun.Clear();
                _persisted.Clear();
                _pluginAssemblies.Clear();
            }
        }
    }
}