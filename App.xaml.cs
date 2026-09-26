using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ★ 旧版本数据迁移（安装目录/Data -> %LOCALAPPDATA%\ShoreHue），必须在日志初始化前执行
            AppPaths.MigrateLegacyData();

            // ★ 安全：收紧数据目录 ACL（仅当前用户 + SYSTEM + 管理员），防止同机其他账户读取
            //   github_token.dat / ai.json / clipboard_history.json 等敏感数据（旧版残留宽权限时自动修复）
            try
            {
                ShoreHue.Infrastructure.Utils.DataDirSecurity.TightenAcl();
            }
            catch { }

            // ★ Jump List 命令：带动作参数启动时优先转发给已运行实例
            //   （无实例 → 返回 true，动作由 MainWindow 初始化后执行）
            bool startupActions = false;
            try
            {
                startupActions = ShoreHue.Infrastructure.WinApi.JumpListCommand.ForwardOrExecute(e.Args);
            }
            catch { }

            // ★ 单实例保护：已有实例运行时直接退出，避免托盘出现多个进程/图标
            _singleInstanceMutex = new Mutex(true, "ShoreHue_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                // ★ 已转发动作时静默退出（不打扰）；无动作仍提示
                if (!startupActions && HasJumpListAction(e.Args))
                {
                    // 动作已通过命名事件转发给已有实例，本进程静默退出
                    Current.Shutdown();
                    return;
                }
                MessageBox.Show("ShoreHue已在运行", "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Information);
                Current.Shutdown();
                return;
            }

            // ★ 本实例为唯一实例：注册 Jump List 动作监听（任务栏点击转发进来）
            try
            {
                ShoreHue.Infrastructure.WinApi.JumpListCommand.Listen(ExecuteJumpListAction);
            }
            catch { }

            // ★ 配置任务栏 Jump List（关联开始菜单快捷方式 AUMID）
            try
            {
                ShoreHue.Infrastructure.WinApi.JumpListManager.Configure();
            }
            catch { }

            // 初始化日志系统（最先执行）
            LogManager.Initialize(LogLevel.Debug);

            // ★ 本地化：按配置语言初始化（zh-CN / en-US，空=跟随系统）
            try
            {
                var lang = ShoreHue.Core.Services.SettingsFileManager.Load().Language;
                ShoreHue.UI.Localization.LocalizationManager.Instance.SetCulture(lang);
            }
            catch { }

            // ★ 插件运行时守卫：安全模式 + 未正常退出检测（必须在任何插件加载之前）
            try { BootstrapPluginGuard(e.Args); }
            catch (Exception ex) { LogManager.Warning($"插件运行时守卫启动配置失败（按普通模式启动）：{ex.Message}"); }
            // ★ 自定义动画的错误也要进熔断（ShapeAnimator 只依赖动画命名空间，这里把两处接起来）
            try { ShoreHue.Animation.AnimationRegistry.CustomAnimationError += ex => ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(ex, "自定义动画"); }
            catch { }

            // ★ 清理更新残留（.new.exe/.ps1）；上次更新失败则提示"仍为旧版本"
            try
            {
                if (ShoreHue.Infrastructure.WinApi.UpdateService.CleanupStaleFiles())
                {
                    MessageBox.Show("上次更新未能完成，当前仍为旧版本。请稍后重试更新。",
                        "ShoreHue", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch { }

            // ★ 后台注册 AppUserModelID（创建开始菜单快捷方式），保证系统 Toast 可显示
            try
            {
                System.Threading.Tasks.Task.Run(
                    ShoreHue.Infrastructure.WinApi.SystemToast.EnsureRegistered);
            }
            catch { }

            // 全局异常捕获
            this.DispatcherUnhandledException += (s, args) =>
            {
                // ★ 插件异常：按插件熔断计数，并且**不弹模态框** —— 插件的错不该反复打断用户，
                //   更不该把宿主一起拖垮（Windhawk 的"缩小爆炸半径"）。识别不出插件才按宿主异常处理。
                string? plugin = ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(args.Exception, "UI 线程");
                if (plugin != null) { args.Handled = true; return; }

                LogManager.Error("Dispatcher未处理异常", args.Exception);
                MessageBox.Show(
                    $"发生未处理异常:\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "ShoreHue错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            // ★ 后台任务里的插件异常同样计数（否则 Task 里抛的会被静默吞掉）
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                if (ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(args.Exception, "后台任务") != null)
                    args.SetObserved();
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(ex, "AppDomain");
                LogManager.Fatal("AppDomain未处理异常", ex);
                MessageBox.Show(
                    $"发生未处理异常:\n{ex?.Message}\n\n{ex?.StackTrace}",
                    "ShoreHue错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            };

            try
            {
                LogManager.Info("应用程序启动");
            }
            catch (Exception ex)
            {
                LogManager.Fatal("应用程序启动失败", ex);
                MessageBox.Show(
                    $"应用程序启动失败:\n{ex.Message}\n\n详细信息已写入日志文件",
                    "ShoreHue启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Current.Shutdown();
            }
        }

        /// <summary>运行标记文件：它还在 = 上次没走到 OnExit（崩溃/被强杀）。</summary>
        private static string RunningMarkerPath =>
            System.IO.Path.Combine(ShoreHue.Infrastructure.Utils.AppPaths.DataRoot, "running.marker");

        private static bool HasSafeModeArg(string[] args)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/safe-mode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-safe-mode", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 启动前引导插件守卫：
        ///   · 命令行 `--safe-mode` → 本次安全模式；
        ///   · 配置里 SafeModeRequested（托盘菜单写入）→ 本次安全模式，用后清除；
        ///   · 连续 3 次未正常退出 → 自动安全模式（插件可能一启动就把进程搞崩）。
        /// 安全模式 = 不加载任何海床插件/覆盖，只跑 exe 内置实现，用户据此把坏插件关掉。
        /// </summary>
        /// <summary>安全模式判定（纯函数，可单测）：命令行 / 用户请求 / 连续未正常退出 → 是否安全模式 + 本次的新计数。</summary>
        /// <summary>安全模式判定 + 原因串 —— **一次算出，调用方无法把两者弄反**。
        /// ★ 为什么合并成一个函数：原因串必须用「清零前」的真实次数，而「要落盘的新计数」
        ///   在命中安全模式时被清零。以前这两个值由调用方各拿一个去拼，于是日志写成
        ///   「连续 0 次未正常退出」—— 2026-09-13 与 2026-09-22 的真机日志都实测到这句，
        ///   看起来像守卫在乱判。合成一个返回值后，这种错配在结构上不可能再发生。</summary>
        internal static (bool Safe, int NewCount, string Reason) EvaluateSafeMode(
            bool argSafe, bool requested, bool markerExists, int storedCount)
        {
            int streak = storedCount + (markerExists ? 1 : 0);
            bool safe = DecideSafeMode(argSafe, requested, markerExists, storedCount, out int newCount);
            string reason = argSafe ? "命令行 --safe-mode"
                          : requested ? "用户请求（托盘：以安全模式重启）"
                          : safe ? $"连续 {streak} 次未正常退出"
                          : "";
            return (safe, newCount, reason);
        }

        internal static bool DecideSafeMode(bool argSafe, bool requested, bool markerExists, int storedCount, out int newCount)
        {
            int count = storedCount + (markerExists ? 1 : 0);
            bool safe = argSafe || requested || count >= 3;
            newCount = safe ? 0 : count;
            return safe;
        }

        private static void BootstrapPluginGuard(string[] args)
        {
            var boot = ShoreHue.Core.Services.SettingsFileManager.Load();
            bool argSafe = HasSafeModeArg(args);
            bool requested = boot.SafeModeRequested;
            bool unclean = System.IO.File.Exists(RunningMarkerPath);
            var (safe, count, safeModeReason) = EvaluateSafeMode(argSafe, requested, unclean, boot.UncleanExitCount);

            ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.Configure(
                safe, boot.CircuitBrokenPlugins, safeModeReason);

            // ★ 只在真的有变化时写盘：否则每次启动都会重写 config.json（哈希无端变化，违反数据纪律）
            bool dirty = false;
            if (safe || requested)
            {
                if (boot.SafeModeRequested) { boot.SafeModeRequested = false; dirty = true; }
                if (boot.UncleanExitCount != 0) { boot.UncleanExitCount = 0; dirty = true; }
            }
            else if (boot.UncleanExitCount != count) { boot.UncleanExitCount = count; dirty = true; }
            if (dirty) ShoreHue.Core.Services.SettingsFileManager.Save(boot);

            try { System.IO.File.WriteAllText(RunningMarkerPath, DateTime.Now.ToString("o")); } catch { }
            if (safe) LogManager.Warning("[守卫] 本次以安全模式启动（不加载任何海床插件）");
        }

        /// <summary>命令行参数是否含 Jump List 动作（决定单实例冲突时是否静默退出）。</summary>
        private static bool HasJumpListAction(string[] args)
        {
            return ShoreHue.Infrastructure.WinApi.JumpListManager.ParseActions(args).Count > 0;
        }

        /// <summary>执行 Jump List 动作（UI 线程，由命令监听/启动 pending 触发；MainWindow 启动动作也调用）。</summary>
        internal static void ExecuteJumpListAction(IReadOnlyList<string> actions)
        {
            try
            {
                if (Current?.MainWindow is not ShoreHue.UI.Main.MainWindow main) return;
                foreach (var action in actions)
                {
                    switch (action)
                    {
                        case ShoreHue.Infrastructure.WinApi.JumpListManager.ArgOpenSettings:
                            main.InvokeJumpListOpenSettings();
                            break;
                        case ShoreHue.Infrastructure.WinApi.JumpListManager.ArgToggleDnd:
                            main.InvokeJumpListToggleDnd();
                            break;
                        case ShoreHue.Infrastructure.WinApi.JumpListManager.ArgTogglePanel:
                            main.InvokeJumpListTogglePanel();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("执行 Jump List 动作失败", ex);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            LogManager.Info("应用程序退出");
            // ★ 正常退出：清掉运行标记 + 复位"未正常退出"计数（下次不会再自动进安全模式）
            try { System.IO.File.Delete(RunningMarkerPath); } catch { }
            try
            {
                var sm = ShoreHue.Core.Infrastructure.Service.ServiceManager.Instance
                    .GetService<ShoreHue.Core.Services.Configuration.SettingsManager>();
                if (sm is ShoreHue.Core.Services.Configuration.ISettingsService s && s.UncleanExitCount != 0)
                    s.UncleanExitCount = 0;
            }
            catch { }
            LogManager.Shutdown();

            // ★★★ 强制结束当前进程（确保所有线程终止） ★★★
            // 这解决 CompositionTarget.Rendering 事件未完全释放导致的进程残留
            try
            {
                Environment.Exit(0);
            }
            catch { }

            base.OnExit(e);
        }
    }
}
