using System;
using System.Linq;
using ShoreHue.Core.Infrastructure.Logging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Management;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 显示器亮度控制：优先 WMI（笔记本内屏），其次 Dxva2（部分外接显示器）。
    /// 注意：System.Management 8.0 的 lib 资产是“非桌面平台占位程序”，
    /// 运行时必须解析 runtimes/win 下的真实程序集，因此本项目目标框架需带 -windows 后缀。
    /// </summary>
    public static class DisplayBrightness
    {
        private const uint MC_CAPS_BRIGHTNESS = 0x00000004;
        private static IntPtr _monitor = IntPtr.Zero;
        private static bool _wmiAvailable;
        private static ManagementObject? _wmiMethodInstance;
        private static byte[] _wmiLevels = Array.Empty<byte>();

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        // ★ EnumDisplayMonitors 属于 user32.dll，不是 dxva2.dll
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorCapabilities(IntPtr hMonitor, out uint pdwMonitorCapabilities, out uint pdwSupportedColorTemperatures);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        public static bool TryGetState(out int min, out int current, out int max)
        {
            min = current = max = 0;

            if (TryGetWmiState(ref min, ref current, ref max)) return true;
            return TryGetDxva2State(ref min, ref current, ref max);
        }

        public static void Set(int value)
        {
            if (_wmiAvailable && _wmiMethodInstance != null)
            {
                try
                {
                    // WMI 只接受 Levels 数组中的档位，直接设任意值会抛异常
                    byte target = _wmiLevels.Length > 0
                        ? SnapToLevel(value, _wmiLevels)
                        : (byte)Math.Clamp(value, 0, 100);
                    _wmiMethodInstance.InvokeMethod("WmiSetBrightness",
                        new object[] { uint.MaxValue, target });
                    return;
                }
                catch (Exception ex)
                {
                    // 设计如此：WMI 不行就继续往下走 DDC/CI（下一条路），所以这里只是"换条路"
                    LogManager.Debug($"[系统控制] WMI 调亮度失败（改用 DDC/CI）：{ex.Message}");
                }
            }

            if (_monitor == IntPtr.Zero) return;
            try { SetMonitorBrightness(_monitor, (uint)Math.Max(0, value)); }
            catch (Exception ex)
            {
                // 两条路都失败 = 亮度没变（用户会看到滑块动了但屏幕没反应）
                LogManager.Warning($"[系统控制] DDC/CI 调亮度也失败（屏幕亮度未改变）：{ex.Message}");
            }
        }

        // ================= WMI（笔记本内屏） =================

        private static bool TryGetWmiState(ref int min, ref int current, ref int max)
        {
            try
            {
                var options = new ConnectionOptions
                {
                    Impersonation = ImpersonationLevel.Impersonate,
                    EnablePrivileges = true
                };
                var scope = new ManagementScope(@"\\.\root\wmi", options);
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    scope, new SelectQuery("SELECT * FROM WmiMonitorBrightness"));
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        current = Convert.ToInt32(obj["CurrentBrightness"]);
                        if (obj["Levels"] is byte[] levels && levels.Length > 1)
                        {
                            min = levels[0];
                            max = levels[^1];
                            _wmiLevels = levels;
                        }
                        else
                        {
                            // 部分驱动把 Levels 暴露为 UInt32（档位数量，如 101 表示 0..100）
                            min = 0;
                            max = 100;
                            _wmiLevels = Enumerable.Range(0, 101).Select(i => (byte)i).ToArray();
                        }
                    }

                    using var methodSearcher = new ManagementObjectSearcher(
                        scope, new SelectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
                    foreach (ManagementObject m in methodSearcher.Get())
                    {
                        _wmiMethodInstance = m;
                        break;
                    }

                    _wmiAvailable = max > min;
                    return _wmiAvailable;
                }
            }
            catch (Exception ex)
            {
                // 探测失败 = 按"这台机器不支持 WMI 调亮度"处理（下面回落到 DDC/CI）
                LogManager.Debug($"[系统控制] 探测 WMI 亮度支持失败（按不支持处理）：{ex.Message}");
            }

            _wmiAvailable = false;
            return false;
        }

        // ================= Dxva2（部分外接显示器） =================

        private static bool TryGetDxva2State(ref int min, ref int current, ref int max)
        {
            try
            {
                _monitor = IntPtr.Zero;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
                {
                    if (GetMonitorCapabilities(hMonitor, out uint caps, out _) &&
                        (caps & MC_CAPS_BRIGHTNESS) != 0)
                    {
                        _monitor = hMonitor;
                        return false; // 找到支持亮度的显示器后停止
                    }
                    return true;
                }, IntPtr.Zero);

                if (_monitor == IntPtr.Zero) return false;
                if (!GetMonitorBrightness(_monitor, out uint mn, out uint cur, out uint mx)) return false;
                min = (int)mn;
                current = (int)cur;
                max = (int)mx;
                return max > min;
            }
            catch (Exception ex)
            {
                LogManager.Debug($"[系统控制] 读取 WMI 亮度失败（按不支持处理）：{ex.Message}");
                return false;
            }
        }

        private static byte SnapToLevel(int value, byte[] levels)
        {
            byte best = levels[0];
            int bestDist = int.MaxValue;
            foreach (byte lv in levels)
            {
                int d = Math.Abs(lv - value);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = lv;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// 无线开关（蓝牙 / Wi-Fi），基于 Windows.Devices.Radios。
    /// </summary>
    public static class SystemRadios
    {
        public static async Task<Radio?> GetRadioAsync(RadioKind kind)
        {
            try
            {
                var radios = await Radio.GetRadiosAsync().AsTask();
                return radios.FirstOrDefault(r => r.Kind == kind);
            }
            catch (Exception ex)
            {
                // 尽力而为：拿不到无线电设备（无该硬件/权限）→ 开关显示为不可用
                LogManager.Debug($"[系统控制] 获取无线电设备失败 kind={kind}：{ex.Message}");
                return null;
            }
        }

        public static async Task<RadioState?> GetStateAsync(RadioKind kind)
        {
            var radio = await GetRadioAsync(kind);
            return radio?.State;
        }

        public static async Task<bool> SetStateAsync(RadioKind kind, bool on)
        {
            try
            {
                var radio = await GetRadioAsync(kind);
                if (radio == null) return false;
                var status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off).AsTask();
                return status == RadioAccessStatus.Allowed;
            }
            catch (Exception ex)
            {
                // 用户点了开关但没生效（QuickSettings 会按 false 显示未切换）
                LogManager.Warning($"[系统控制] 切换无线电失败 kind={kind} on={on}：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 移动热点开关（Windows.Networking.NetworkOperators，台式机/无热点网卡时不可用）。
    /// </summary>
    public static class HotspotControl
    {
        private static NetworkOperatorTetheringManager? _manager;

        public static async Task<(bool Supported, bool Enabled)> GetStateAsync()
        {
            try
            {
                _manager = null;
                var profile = NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(p => p.IsWlanConnectionProfile || p.IsWwanConnectionProfile);
                if (profile == null) return (false, false);

                _manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile) != TetheringCapability.Enabled)
                {
                    return (false, false);
                }

                return (true, _manager.TetheringOperationalState == TetheringOperationalState.On);
            }
            catch (Exception ex)
            {
                LogManager.Debug($"[系统控制] 读取热点状态失败（按不可用处理）：{ex.Message}");
                return (false, false);
            }
        }

        public static async Task<bool> SetAsync(bool on)
        {
            try
            {
                if (_manager == null) return false;
                var result = on
                    ? await _manager.StartTetheringAsync().AsTask()
                    : await _manager.StopTetheringAsync().AsTask();
                return result.Status == TetheringOperationStatus.Success;
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[系统控制] 切换热点失败 on={on}：{ex.Message}");
                return false;
            }
        }
    }
}
