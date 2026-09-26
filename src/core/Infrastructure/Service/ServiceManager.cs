using System;
using System.Collections.Generic;
using System.Linq;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.Core.Infrastructure.Service
{
    /// <summary>
    /// 服务管理器（管理所有服务的生命周期）
    /// </summary>
    public class ServiceManager : IDisposable
    {
        private static ServiceManager? _instance;
        private readonly List<IService> _services = new();
        private readonly List<IService> _initializedServices = new();
        private readonly List<(IService Service, Exception Error)> _failedServices = new();
        private bool _disposed = false;
        private readonly object _lock = new object();

        public static ServiceManager Instance => _instance ??= new ServiceManager();

        private ServiceManager() { }

        /// <summary>
        /// 注册服务（按顺序注册，按逆序关闭）
        /// </summary>
        /// <summary>已注册的服务（只读）。★ 能力层（HostCapabilities）用它做**接口优先**的回退查找：
        /// 这样"注册一个 IClipboardService 实现"也能被小组件拿到，而不是必须注册那个具体类型。</summary>
        internal System.Collections.Generic.IEnumerable<IService> All
            => _services;

        public ServiceManager Register(IService service)
        {
            lock (_lock)
            {
                if (service == null)
                    throw new ArgumentNullException(nameof(service));

                if (_services.Any(s => s.Name == service.Name))
                {
                    LogManager.Warning($"服务已注册: {service.Name}");
                    return this;
                }

                _services.Add(service);
                LogManager.Debug($"服务已注册: {service.Name}");
            }
            return this;
        }

        /// <summary>
        /// 注册多个服务
        /// </summary>
        public ServiceManager Register(params IService[] services)
        {
            foreach (var service in services)
            {
                Register(service);
            }
            return this;
        }

        /// <summary>
        /// 初始化所有已注册的服务（按注册顺序）
        /// </summary>
        public void InitializeAll()
        {
            lock (_lock)
            {
                LogManager.Info($"开始初始化 {_services.Count} 个服务");
                _failedServices.Clear();

                foreach (var service in _services)
                {
                    try
                    {
                        if (!service.IsInitialized)
                        {
                            service.Initialize();
                            _initializedServices.Add(service);
                            LogManager.Debug($"服务初始化成功: {service.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"服务初始化失败: {service.Name}", ex);
                        _failedServices.Add((service, ex));
                        // 不中断初始化流程，继续初始化其他服务
                    }
                }

                LogManager.Info($"服务初始化完成，成功 {_initializedServices.Count}/{_services.Count}");
                if (_failedServices.Count > 0)
                {
                    LogManager.Warning($"有 {_failedServices.Count} 个服务初始化失败");
                }
            }
        }

        /// <summary>
        /// 获取初始化失败的服务列表
        /// </summary>
        public IReadOnlyList<(IService Service, Exception Error)> GetFailedServices()
        {
            lock (_lock)
            {
                return _failedServices.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// 检查是否有任何服务初始化失败
        /// </summary>
        public bool HasFailedServices()
        {
            lock (_lock)
            {
                return _failedServices.Count > 0;
            }
        }

        /// <summary>
        /// 获取已初始化成功的服务数量
        /// </summary>
        public int SuccessCount
        {
            get
            {
                lock (_lock)
                {
                    return _initializedServices.Count;
                }
            }
        }

        /// <summary>
        /// 获取已注册的服务总数
        /// </summary>
        public int TotalCount
        {
            get
            {
                lock (_lock)
                {
                    return _services.Count;
                }
            }
        }

        /// <summary>
        /// 关闭所有服务（按逆序释放）
        /// </summary>
        public void ShutdownAll()
        {
            lock (_lock)
            {
                if (_disposed) return;

                LogManager.Info($"开始关闭 {_initializedServices.Count} 个服务");

                // 逆序释放
                for (int i = _initializedServices.Count - 1; i >= 0; i--)
                {
                    var service = _initializedServices[i];
                    try
                    {
                        service.Shutdown();
                        LogManager.Debug($"服务关闭成功: {service.Name}");
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"服务关闭失败: {service.Name}", ex);
                    }
                }

                _initializedServices.Clear();
                LogManager.Info("所有服务已关闭");
            }
        }

        /// <summary>
        /// 获取已注册的服务
        /// </summary>
        public T? GetService<T>() where T : class, IService
        {
            lock (_lock)
            {
                return _services.FirstOrDefault(s => s is T) as T;
            }
        }

        /// <summary>
        /// 检查服务是否已注册
        /// </summary>
        public bool IsRegistered<T>() where T : class, IService
        {
            lock (_lock)
            {
                return _services.Any(s => s is T);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            ShutdownAll();
            _disposed = true;
            _instance = null;
            GC.SuppressFinalize(this);
        }
    }
}