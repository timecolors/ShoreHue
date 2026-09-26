using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.UI.Localization;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets.Dynamic;
using NAudio.CoreAudioApi;

namespace ShoreHue.UI.Status
{
    public partial class SystemStatusView : UserControl
    {
        private DispatcherTimer _timer;
        private DispatcherTimer? _weatherTimer;
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _memoryCounter;
        private int _frameCount = 0;
        private DateTime _fpsStartTime = DateTime.Now;
        private int _currentFps = 0;
        private bool _fpsSubscribed;   // 是否已订阅 CompositionTarget.Rendering（仅在显示 FPS 时为 true）

        private MMDevice? _audioDevice;
        private ISettingsService? _settings;
        private bool _weatherEnabled;
        private string _weatherCity = "";
        private DateTime _weatherLastClick = DateTime.MinValue;   // 双击判定：与系统双击时间比较，不依赖 ClickCount
        private DispatcherTimer? _weatherClickTimer;              // 单击延迟刷新（给双击留判定窗口）

        // ===== 自定义状态栏显示项（IStatusProvider 动态挂载） =====
        private sealed class CustomStatusItem
        {
            public string Key = "";
            public IStatusProvider Provider = null!;
            public TextBlock Text = null!;
            public StackPanel Panel = null!;
        }

        private readonly List<CustomStatusItem> _customItems = new();
        private readonly System.Action _pluginChangedHandler;   // 保存引用以便 Unloaded 解绑（防视图重建累积订阅）

        public SystemStatusView()
        {
            InitializeComponent();

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            }
            catch { /* 性能计数器不可用（个别系统/权限）→ 该项不显示，其余状态项照常 */ }

            try
            {
                _memoryCounter = new PerformanceCounter("Memory", "Available MBytes");
            }
            catch { /* 同上：取不到就不显示内存项 */ }

            InitAudioDevice();

            // ★ 不在这里无条件订阅 CompositionTarget.Rendering。
            //   Rendering 是**全局每帧**回调：只要存在订阅者，WPF 组合管线就会持续出帧，
            //   即使面板已经移出屏幕、即使 FPS 项根本没开 —— 实测这会让进程长期停在
            //   单核 7~9% 的空闲占用（本项目自己的铁律也写着"禁止常驻订阅 CompositionTarget.Rendering"，
            //   见 docs/评估-跨平台与定位.md / docs/SEABED-SPEC.md）。
            //   现在只在「状态栏显示 FPS」真的打开时才挂上，关掉即摘掉。

            UpdateStatus();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => UpdateStatus();
            _timer.Start();

            // 网络状态改为事件驱动（见 IsNetworkAvailableCached 注释）
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;

            // ★ 海床文件夹增删（用户放/删状态栏插件）：自动重新挂载自定义项
            _pluginChangedHandler = () =>
            {
                // ★ 本回调可能来自 watcher 的**后台线程**：IsLoaded 是 UI 线程亲和成员，只能在 Dispatcher 里读
                //   （在外面读会抛跨线程异常，并中断 Changed 的整个多播 → 后面的订阅者这轮全都不刷新）
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_settings == null || !IsLoaded) return;
                    try { ApplySettings(_settings); } catch { /* 尽力而为：状态栏项没重挂上，下次设置变化/文件变化会再试 */ }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            WidgetPluginStore.Changed += _pluginChangedHandler;

            Unloaded += (s, e) =>
            {
                _timer?.Stop();
                _weatherTimer?.Stop();
                SetFpsRenderingSubscription(false);
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                _audioDevice?.Dispose();
                DeactivateCustomItems();
                WidgetPluginStore.Changed -= _pluginChangedHandler;
            };
        }

        /// <summary>应用设置：控制各状态项显隐与天气开关。</summary>
        public void ApplySettings(ISettingsService settings)
        {
            _settings = settings;
            // ★ 天气启用 = "状态栏显示天气" 勾选（WeatherEnabled 字段无独立入口，不再作为门槛）
            _weatherEnabled = settings.StatusShowWeather;
            _weatherCity = settings.WeatherCity ?? "";

            SetVisible(TimePanel, settings.StatusShowTime);
            SetVisible(CpuPanel, settings.StatusShowCpu);
            SetVisible(MemoryPanel, settings.StatusShowMemory);
            SetVisible(FpsPanel, settings.StatusShowFps);
            SetFpsRenderingSubscription(settings.StatusShowFps);
            SetVisible(VolumePanel, settings.StatusShowVolume);
            SetVisible(NetworkPanel, settings.StatusShowNetwork);
            SetVisible(BatteryPanel, settings.StatusShowBattery);
            SetVisible(WeatherPanel, settings.StatusShowWeather && _weatherEnabled);

            // ★ 自定义状态栏显示项：先卸载旧项再按当前启用状态重新挂载（内置项之后）
            RebuildCustomItems(settings);

            if (_weatherEnabled && settings.StatusShowWeather)
            {
                WeatherText.Text = LocalizationManager.Instance["Status_WeatherLoading"];
                _ = RefreshWeatherAsync();
                _weatherTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
                // ★ 修复：每次 ApplySettings 都 += 会累积多个 Tick 订阅（重复刷新天气）；
                //   先 -= 再 += 保证只挂一个
                _weatherTimer.Tick -= OnWeatherTimerTick;
                _weatherTimer.Tick += OnWeatherTimerTick;
                _weatherTimer.Start();
            }
            else
            {
                _weatherTimer?.Stop();
            }
        }

        /// <summary>重新挂载自定义状态栏显示项：卸载旧项 → 编译缓存中取启用项 → Children.Add 到容器尾。</summary>
        private void RebuildCustomItems(ISettingsService settings)
        {
            DeactivateCustomItems();

            foreach (var kvp in WidgetPluginStore.StatusProviders)
            {
                try
                {
                    var provider = kvp.Value;
                    // ★ 启用判定：设置开关（StatusProviderEnabled，缺省启用）+ 插件自决 IsEnabled
                    if (!settings.IsStatusProviderEnabled(kvp.Key) || !provider.IsEnabled(settings)) continue;

                    var icon = new TextBlock
                    {
                        Text = provider.IconText ?? "",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var text = new TextBlock
                    {
                        Text = "",
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 15, 0)
                    };
                    panel.Children.Add(icon);
                    panel.Children.Add(text);
                    // ★ 顺序很关键：必须**先登记再激活**。
                    //   以前是先 StatusContainer.Children.Add(panel)、后才 provider.OnActivated()，
                    //   而 OnActivated 抛异常会被下面 catch 吞掉 —— 此时 panel 已经在可视树里、
                    //   `_customItems` 里却没有它，于是 DeactivateCustomItems() 永远移不掉它；
                    //   下一次 ApplySettings 再加一块 → 状态栏每次刷新都多一个孤儿面板。
                    var item = new CustomStatusItem
                    {
                        Key = kvp.Key,
                        Provider = provider,
                        Text = text,
                        Panel = panel
                    };
                    _customItems.Add(item);
                    try
                    {
                        StatusContainer.Children.Add(panel);
                        provider.OnActivated();
                    }
                    catch
                    {
                        // 挂载/激活失败 → 就地回滚，避免留下登记了但没进树的半成品
                        _customItems.Remove(item);
                        StatusContainer.Children.Remove(panel);
                        throw;
                    }
                }
                catch (Exception ex) { ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(ex, "状态栏挂载"); }
            }

            UpdateCustomItems();
        }

        /// <summary>每秒刷新自定义状态栏项的文本（provider.GetText()）。</summary>
        private void UpdateCustomItems()
        {
            foreach (var item in _customItems)
            {
                try { SetTextIfChanged(item.Text, item.Provider.GetText() ?? ""); }
                // ★ 守卫：连续失败的插件会被熔断停用（只记一次/秒，窗口内计满阈值才动作）
                catch (Exception ex) { ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(ex, "状态栏 GetText"); }
            }
        }

        /// <summary>卸载全部自定义项：OnDeactivated + 从容器移除。</summary>
        private void DeactivateCustomItems()
        {
            foreach (var item in _customItems)
            {
                try { item.Provider.OnDeactivated(); } catch (Exception ex) { ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ReportException(ex, "状态栏卸载"); }
                StatusContainer.Children.Remove(item.Panel);
            }
            _customItems.Clear();
        }

        private static void SetVisible(FrameworkElement el, bool visible)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private async System.Threading.Tasks.Task RefreshWeatherAsync()
        {
            var w = await WeatherService.GetWeatherWithCityAsync(_weatherCity);

            // 确保回到 UI 线程更新（await 可能在无 SynchronizationContext 时落到线程池）
            // ★ 显式丢弃返回值：这里是"投递后不等"的封送（DispatcherOperation 可 await，
            //   不丢弃会触发 CS4014）。
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (w.HasValue)
                {
                    // 显示生效城市名 + 天气，如 保定 · ☀25° 晴；IP 定位无城市名时只显示天气
                    WeatherText.Text = string.IsNullOrEmpty(w.Value.City)
                        ? w.Value.Text
                        : w.Value.City + " · " + w.Value.Text;
                }
                else
                {
                    WeatherText.Text = LocalizationManager.Instance["Status_WeatherUnavailable"];
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>天气交互：单击 → 刷新；双击（系统双击时间内两次点击）→ 默认浏览器搜索"城市 + 天气"。
        /// ★ 不用 WPF ClickCount（曾出现双击判不出/单击被双击第一击打断），用时间差自判：
        ///   单击只延迟执行刷新（给双击留判定窗口），双击到达即取消刷新、直接开浏览器。</summary>
        private async void WeatherPanel_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var now = DateTime.Now;
                bool isDouble = (now - _weatherLastClick).TotalMilliseconds
                    <= System.Windows.Forms.SystemInformation.DoubleClickTime;
                _weatherLastClick = now;

                if (isDouble)
                {
                    // 双击：取消待执行的单击刷新 → 开浏览器搜索天气
                    if (_weatherClickTimer != null) { _weatherClickTimer.Stop(); _weatherClickTimer = null; }
                    _weatherLastClick = DateTime.MinValue;   // 防三连击再次触发
                    await WeatherService.OpenForecastPageAsync(_weatherCity);
                    return;
                }

                // 单击：延迟 300ms 刷新（若紧随其后有第二击 → isDouble 分支接管并取消）
                if (_weatherClickTimer == null)
                {
                    _weatherClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _weatherClickTimer.Tick += async (_, _) =>
                    {
                        _weatherClickTimer?.Stop();
                        _weatherClickTimer = null;
                        try
                        {
                            WeatherText.Text = LocalizationManager.Instance["Status_WeatherLoading"];
                            await RefreshWeatherAsync();
                        }
                        catch { /* 尽力而为：本次刷新失败，界面保留上次天气（下面计时器到点会再刷） */ }
                    };
                }
                _weatherClickTimer.Stop();
                _weatherClickTimer.Start();
            }
            catch { /* 计时器起不来只影响"点一下立刻刷新"，定时刷新照常 */ }
        }

        private void InitAudioDevice()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                _audioDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                _audioDevice = null;
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            _frameCount++;
            var now = DateTime.Now;
            if ((now - _fpsStartTime).TotalSeconds >= 1)
            {
                _currentFps = _frameCount;
                _frameCount = 0;
                _fpsStartTime = now;
                // 本回调已经在 UI 线程上（CompositionTarget.Rendering 由渲染管线在 UI 线程派发），
                // 不需要再 Dispatcher.Invoke 绕一圈。
                FpsText.Text = $"{_currentFps}fps";
            }
        }

        /// <summary>
        /// FPS 项的渲染订阅开关：**只有显示 FPS 时才订阅**（见构造函数注释）。
        /// 幂等，可被 ApplySettings 反复调用。
        /// </summary>
        private void SetFpsRenderingSubscription(bool enabled)
        {
            if (enabled == _fpsSubscribed) return;
            if (enabled)
            {
                _frameCount = 0;
                _fpsStartTime = DateTime.Now;
                CompositionTarget.Rendering += OnRendering;
                _fpsSubscribed = true;
            }
            else
            {
                CompositionTarget.Rendering -= OnRendering;
                _fpsSubscribed = false;
            }
        }

        /// <summary>天气定时器回调（15 分钟刷新一次；-= / += 防重复订阅）。</summary>
        private void OnWeatherTimerTick(object? sender, EventArgs e) => _ = RefreshWeatherAsync();

        private void UpdateStatus()
        {
            // ★ 只算真正显示出来的项：隐藏项的读取在面板上看不到任何结果，却每秒都要付一次代价。
            // ★ 写入走"值没变就不碰控件"的守卫：避免每秒重复触发资源查找 + 属性失效 + 测量/渲染。
            //
            // ★ 这里**没有**做"重要读数降到 2 秒一档"：我实测过那一版（3.44% vs 3.50%，在噪声范围内），
            //   收益为零，却让 CPU/内存百分比刷新变慢、用户可能觉得"卡了"——已回退。
            //   本视图真正的开销大头是**网络枚举**（`GetIsNetworkAvailable` 实测 78ms/次），
            //   那条已改为事件驱动 + 30s 兜底缓存，见 IsNetworkAvailableCached。
            UpdateTime();
            UpdateCustomItems();

            if (CpuPanel.Visibility == Visibility.Visible) UpdateCpu();
            if (MemoryPanel.Visibility == Visibility.Visible) UpdateMemory();
            if (VolumePanel.Visibility == Visibility.Visible) UpdateVolume();
            if (NetworkPanel.Visibility == Visibility.Visible) UpdateNetwork();
            if (BatteryPanel.Visibility == Visibility.Visible) UpdateBattery();
        }

        // ===== 变更守卫（值没变就不动 UI）=====

        private static void SetTextIfChanged(TextBlock target, string text)
        {
            if (!string.Equals(target.Text, text, StringComparison.Ordinal)) target.Text = text;
        }

        /// <summary>元素 → 当前生效的画刷资源键（弱引用，元素被回收后条目自动消失）。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, System.Runtime.CompilerServices.StrongBox<string>> _brushKeys = new();

        private static void SetBrushKeyIfChanged(FrameworkElement target, string resourceKey)
        {
            var box = _brushKeys.GetValue(target, _ => new System.Runtime.CompilerServices.StrongBox<string>(""));
            if (string.Equals(box.Value, resourceKey, StringComparison.Ordinal)) return;
            box.Value = resourceKey;
            // 仍用 SetResourceReference（动态资源引用）：主题切换时图标要能跟着变，
            // 这里只是避免"每秒重设同一个键"带来的重复资源查找与属性失效。
            target.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, resourceKey);
        }

        private void UpdateTime()
        {
            SetTextIfChanged(TimeText, DateTime.Now.ToString("HH:mm:ss"));
        }

        private void UpdateCpu()
        {
            try
            {
                if (_cpuCounter != null)
                {
                    float val = _cpuCounter.NextValue();
                    if (val > 0 && val < 100)
                        SetTextIfChanged(CpuText, $"{val:F0}%");
                }
            }
            catch { /* 读一次失败就保持上次数字（每秒都会再读） */ }
        }

        private void UpdateMemory()
        {
            try
            {
                if (_memoryCounter != null)
                {
                    float availableMB = _memoryCounter.NextValue();
                    float totalMB = GetTotalMemoryMB();
                    if (totalMB > 0)
                    {
                        float usedPercent = (1 - availableMB / totalMB) * 100;
                        SetTextIfChanged(MemoryText, $"{usedPercent:F0}%");
                    }
                }
            }
            catch { /* 同上：保持上次显示 */ }
        }

        private float _totalMemoryMb;
        private DateTime _totalMemoryAt = DateTime.MinValue;

        private float GetTotalMemoryMB()
        {
            // ★ 物理内存总量几乎不变，没必要每秒向 GC 查一次（GC.GetGCMemoryInfo 会走 GC 内部查询）
            if (_totalMemoryMb > 0 && DateTime.Now - _totalMemoryAt < TimeSpan.FromSeconds(30))
                return _totalMemoryMb;
            try
            {
                var gcMemoryInfo = GC.GetGCMemoryInfo();
                _totalMemoryMb = gcMemoryInfo.TotalAvailableMemoryBytes / 1024f / 1024f;
                _totalMemoryAt = DateTime.Now;
                return _totalMemoryMb;
            }
            catch { return _totalMemoryMb; }
        }

        private void UpdateVolume()
        {
            try
            {
                if (_audioDevice != null)
                {
                    float volume = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                    int vol = (int)(volume * 100);
                    SetTextIfChanged(VolumeText, $"{vol}%");
                    SetBrushKeyIfChanged(VolumeIcon, vol <= 0 ? "DangerBrush" : "TextSecondaryBrush");
                }
            }
            catch { /* 音量读数/图标更新失败（无音频设备）→ 保持上次显示 */ }
        }

        private void VolumePanel_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            HandleVolumeWheel(e.Delta);
        }

        public void HandleVolumeWheel(int delta)
        {
            try
            {
                if (_audioDevice == null) return;

                int step = 2;
                int deltaValue = delta > 0 ? step : -step;

                float current = _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                int newVol = (int)(current * 100) + deltaValue;
                newVol = Math.Clamp(newVol, 0, 100);

                _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = newVol / 100f;

                UpdateVolume();
            }
            catch { /* 设置系统音量失败（设备被独占/已拔出）→ 显示值不变 */ }
        }

        /// <summary>
        /// 网络是否可用。
        /// ★ 绝不能每秒调一次 `NetworkInterface.GetIsNetworkAvailable()`：它内部走
        ///   `GetAdaptersAddresses` 枚举**所有**适配器（VPN / Hyper-V / WSL / Docker / 虚拟网卡越多越慢），
        ///   实测在普通机器上是几十毫秒级的调用 —— 放在 1 秒定时器里就是**单核 7~8% 的常驻占用**
        ///   （用 dotnet-stack 抓到的热栈：UpdateStatus → UpdateNetwork → GetNetworkInterfaces → GetPerAdapterInfo）。
        ///   改为事件驱动 + 兜底 TTL 缓存：状态变化由 NetworkChange 通知，最坏 10 秒兜底刷新一次。
        /// </summary>
        private bool _netAvailable = true;
        private DateTime _netCheckedAt = DateTime.MinValue;
        private static readonly TimeSpan NetCacheTtl = TimeSpan.FromSeconds(30);

        private bool IsNetworkAvailableCached()
        {
            var now = DateTime.Now;
            if (now - _netCheckedAt < NetCacheTtl) return _netAvailable;
            try
            {
                _netAvailable = NetworkInterface.GetIsNetworkAvailable();
            }
            catch { /* 取不到就沿用上次结论（有确定性回退，不需要刷屏） */ }
            _netCheckedAt = now;
            return _netAvailable;
        }

        /// <summary>网络拓扑变化（插拔网线 / 连上 Wi-Fi / VPN 起落）时让缓存立刻失效。</summary>
        private void OnNetworkChanged(object? sender, EventArgs e) => _netCheckedAt = DateTime.MinValue;

        private void UpdateNetwork()
        {
            try
            {
                if (!IsNetworkAvailableCached())
                {
                    SetTextIfChanged(NetworkText, LocalizationManager.Instance["Status_NetDisconnected"]);
                    SetBrushKeyIfChanged(NetworkIcon, "DangerBrush");
                    return;
                }
                SetTextIfChanged(NetworkText, LocalizationManager.Instance["UI_SystemStatusView_401"]);
                SetBrushKeyIfChanged(NetworkIcon, "TextSecondaryBrush");
            }
            catch
            {
                SetTextIfChanged(NetworkText, LocalizationManager.Instance["Status_NetUnknown"]);
                SetBrushKeyIfChanged(NetworkIcon, "TextSecondaryBrush");
            }
        }

        private void UpdateBattery()
        {
            try
            {
                var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;
                if (powerStatus.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                {
                    SetTextIfChanged(BatteryText, LocalizationManager.Instance["Status_NoBattery"]);
                    SetBrushKeyIfChanged(BatteryIcon, "TextSecondaryBrush");
                    return;
                }
                int percent = (int)(powerStatus.BatteryLifePercent * 100);
                SetTextIfChanged(BatteryText, $"{percent}%");
                bool charging = powerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                SetBrushKeyIfChanged(BatteryIcon, charging ? "AccentBrush" : "TextSecondaryBrush");
            }
            catch
            {
                SetTextIfChanged(BatteryText, "--");
                SetBrushKeyIfChanged(BatteryIcon, "TextSecondaryBrush");
            }
        }
    }
}