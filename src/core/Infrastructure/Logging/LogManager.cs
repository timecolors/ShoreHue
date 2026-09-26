using System;
using System.IO;
using ShoreHue.Infrastructure.Utils;
using Serilog;
using Serilog.Events;

namespace ShoreHue.Core.Infrastructure.Logging
{
    /// <summary>
    /// 日志管理器（全局入口），后端基于 Serilog：
    ///  - 按天滚动文件（log-YYYYMMDD.log），保留最近 30 天
    ///  - 多进程共享写安全（shared: true）
    ///  - 目录不可写时静默降级，绝不影响应用启动
    /// 公共 API（Initialize / Shutdown / Debug / Info / Warning / Error / Fatal）保持不变。
    /// </summary>
    public static class LogManager
    {
        private static Serilog.Core.Logger? _logger;
        private static readonly object _lock = new object();
        private static bool _initialized = false;

        /// <summary>
        /// 初始化日志系统
        /// </summary>
        public static void Initialize(LogLevel minLevel = LogLevel.Debug, string logDirectory = "")
        {
            lock (_lock)
            {
                if (_initialized) return;

                string dir = string.IsNullOrWhiteSpace(logDirectory) ? AppPaths.LogDirectory : logDirectory;
                // 刻意不记日志：日志系统本身还没初始化（这里失败会走内存/空操作降级，见类注释"绝不影响应用启动"）
                try { Directory.CreateDirectory(dir); } catch { }

                var config = new LoggerConfiguration()
                    .MinimumLevel.Is(ToSerilogLevel(minLevel));

                try
                {
                    config = config.WriteTo.File(
                        path: Path.Combine(dir, "log-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        shared: true,
                        encoding: System.Text.Encoding.UTF8,
                        outputTemplate:
                            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
                }
                catch
                {
                    // 文件日志不可用时继续（内存日志 / 降级为空操作）
                }

                _logger = config.CreateLogger();
                _initialized = true;

                Info("========================================");
                Info("日志系统初始化完成 (Serilog)");
                Info($"日志级别: {minLevel}");
                Info($"日志目录: {dir}");
                Info($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Info("========================================");
            }
        }

        /// <summary>获取底层 Serilog 记录器（高级用途）。</summary>
        public static Serilog.Core.Logger? SerilogLogger => _logger;

        public static ILogger Logger
        {
            get
            {
                if (!_initialized)
                {
                    throw new InvalidOperationException("日志系统未初始化，请先调用 LogManager.Initialize()");
                }
                return new SerilogLoggerAdapter(_logger ?? Serilog.Log.Logger);
            }
        }

        public static bool IsInitialized => _initialized;

        // ========== 便捷方法 ==========

        public static void Debug(string message) => Write(LogLevel.Debug, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warning(string message) => Write(LogLevel.Warning, message);
        public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);
        public static void Fatal(string message, Exception? exception = null) => Write(LogLevel.Fatal, message, exception);
        public static void Log(LogLevel level, string message, Exception? exception = null) => Write(level, message, exception);

        private static void Write(LogLevel level, string message, Exception? exception = null)
        {
            var logger = _logger;
            if (logger == null) return;

            switch (level)
            {
                case LogLevel.Trace: logger.Verbose(exception, "{Message}", message); break;
                case LogLevel.Debug: logger.Debug(exception, "{Message}", message); break;
                case LogLevel.Info: logger.Information(exception, "{Message}", message); break;
                case LogLevel.Warning: logger.Warning(exception, "{Message}", message); break;
                case LogLevel.Error: logger.Error(exception, "{Message}", message); break;
                case LogLevel.Fatal: logger.Fatal(exception, "{Message}", message); break;
            }
        }

        private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Info => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Fatal => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };

        /// <summary>释放日志资源。</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_logger != null)
                {
                    // 刻意不记日志：正在关闭日志系统，且记日志本身就会再走一遍这里
                    try { Info("日志系统关闭"); } catch { }
                    try { _logger.Dispose(); } catch { }
                    _logger = null;
                }
                _initialized = false;
            }
        }

        /// <summary>
        /// 适配器：把 Serilog 记录器包装成项目原有的 ILogger 接口，
        /// 兼容少量直接使用 LogManager.Logger 的调用方。
        /// </summary>
        private sealed class SerilogLoggerAdapter : ILogger
        {
            private readonly Serilog.ILogger _inner;
            public SerilogLoggerAdapter(Serilog.ILogger inner) => _inner = inner;

            public void Log(LogLevel level, string message, Exception? exception = null)
                => LogManager.Log(level, message, exception);

            public bool IsEnabled(LogLevel level) => _inner.IsEnabled(ToSerilogLevel(level));

            public void Debug(string message) => LogManager.Debug(message);
            public void Info(string message) => LogManager.Info(message);
            public void Warning(string message) => LogManager.Warning(message);
            public void Error(string message, Exception? exception = null) => LogManager.Error(message, exception);
            public void Fatal(string message, Exception? exception = null) => LogManager.Fatal(message, exception);
        }
    }
}
