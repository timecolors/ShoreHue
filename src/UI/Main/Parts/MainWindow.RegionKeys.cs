using System;
using ShoreHue.Core;
using ShoreHue.Core.Detection;
using ShoreHue.Infrastructure.WinApi;

namespace ShoreHue.UI.Main
{
    // ==================== Ctrl+数字环：键盘呼出 16 区域面板 ====================
    // 规则（用户确认）：
    //   主键盘 1-9 与小键盘 Numpad1-9 均支持（按键码不同，两套都注册）；
    //   单键：7/9/1/3 = 左上/右上/左下/右下角面板；2/4/6/8 = 上/左/右/下 边中段面板
    //   精修组合（Ctrl 按住连按两键，任意顺序，300ms 内）：边键 + 邻角键 = 该边端段
    //     (4,7)→左边缘上段(小组件)  (4,1)→左边缘下段  (6,9)→右边缘上段  (6,3)→右边缘下段
    //     (8,7)→上边缘左段  (8,9)→上边缘右段  (2,1)→下边缘左段  (2,3)→下边缘右段
    // 行为：键盘呼出的面板热键钉住、不自动隐藏；再按当前所在区域的键 → 收起。
    // ★ 右上角（Ctrl+9）默认无面板：安全区内不呼出（EdgeTriggerController.SummonRegion 内部保护）。
    public partial class MainWindow
    {
        // 主键盘 '1'-'9' (VK 0x31-0x39) 与小键盘 Numpad1-9 (VK 0x61-0x69)
        // 是两个不同的按键码——必须都注册，否则按小键盘数字永远不触发。
        private const int RegionKeyBase = 0x6200;              // 主键盘数字 id 基址
        private const int RegionKeyNumpadBase = 0x6200 + 0x40; // 小键盘数字 id 基址
        private const int RegionKeyMaxDigit = 9;
        private const long RegionSeqMs = 300;   // 精修组合窗口

        private int _regionPendingDigit = 0;
        private long _regionPendingTick = 0;

        private bool IsRegionHotkey(int id) =>
            (id >= RegionKeyBase && id <= RegionKeyBase + RegionKeyMaxDigit) ||
            (id >= RegionKeyNumpadBase && id <= RegionKeyNumpadBase + RegionKeyMaxDigit);

        /// <summary>按设置应用数字环热键：默认关闭（避免与浏览器 Ctrl+数字 切标签冲突）；
        /// 开启后按所选修饰键注册。设置变化（OnSettingsChanged）时重新应用。</summary>
        private void ApplyRegionHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            UnregisterRegionHotkeys(_hwnd);
            bool enabled = _settingsService?.RegionHotkeysEnabled ?? false;
            if (!enabled) return;
            uint mods = ModsFromName(_settingsService?.RegionHotkeyModifier);
            for (int d = 1; d <= RegionKeyMaxDigit; d++)
            {
                // 主键盘 '1'-'9'：VK 0x31-0x39
                try { RegisterHotKey(_hwnd, RegionKeyBase + d, mods, (uint)('0' + d)); } catch { /* 注册不上通常是该组合键被别的程序占了；其余数字键照常注册 */ }
                // 小键盘 Numpad1-9：VK 0x61-0x69（同一 id 区间高位偏移，分发时归一到同一 digit）
                try { RegisterHotKey(_hwnd, RegionKeyNumpadBase + d, mods, (uint)(0x60 + d)); } catch { /* 同上：单个键注册失败不影响其它键 */ }
            }
        }

        private static uint ModsFromName(string? name) => name switch
        {
            "Ctrl+Shift" => HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_SHIFT,
            "Alt" => HotkeyParser.MOD_ALT,
            "Ctrl+Alt" => HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT,
            _ => HotkeyParser.MOD_CONTROL   // 默认 Ctrl
        };

        private void UnregisterRegionHotkeys(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            for (int d = 1; d <= RegionKeyMaxDigit; d++)
            {
                try { UnregisterHotKey(hwnd, RegionKeyBase + d); } catch { /* 注销失败无害：窗口销毁时系统会一并回收热键 */ }
                try { UnregisterHotKey(hwnd, RegionKeyNumpadBase + d); } catch { /* 同上 */ }
            }
        }

        /// <summary>WM_HOTKEY 分发：Ctrl+数字 → 单键或精修组合 → 呼出/收起区域面板。</summary>
        private void HandleRegionHotkey(int id)
        {
            try
            {
                if (_visibilityController == null || _edgeController == null) return;
                // ★ 主键盘/小键盘 id 两区归一：小键盘区在高位偏移，先归到统一 digit
                int digit = id >= RegionKeyNumpadBase ? id - RegionKeyNumpadBase
                                                      : id - RegionKeyBase;
                if (digit < 1 || digit > RegionKeyMaxDigit) return;

                // ★ Ctrl 兼作穿透修饰键：数字环按下时短时抑制穿透（覆盖 300ms 组合窗口），
                //   且若穿透已激活（窗口被 Ctrl 按住而隐藏）立即恢复——否则呼出面板不可见。
                _passthroughSuppressUntilTick = Environment.TickCount64 + 800;
                if (_passthroughActive) UpdatePassthroughState();

                long now = Environment.TickCount64;
                int first = 0;
                if (_regionPendingDigit > 0 && (now - _regionPendingTick) <= RegionSeqMs)
                {
                    first = _regionPendingDigit;   // Ctrl 按住期间的连按 → 精修组合
                }
                _regionPendingDigit = digit;
                _regionPendingTick = now;

                var region = ResolveRegion(first, digit);
                if (region == EdgeRegion.Unknown)
                {
                    // 无有效组合：仅记录首键等待第二键；单键立即生效
                    if (first == 0)
                    {
                        var single = ResolveRegion(0, digit);
                        if (single != EdgeRegion.Unknown) ToggleOrSummon(single);
                    }
                    return;
                }
                _regionPendingDigit = 0;
                ToggleOrSummon(region);
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("数字环热键处理失败", ex);
            }
        }

        private void ToggleOrSummon(EdgeRegion region)
        {
            string key = region switch
            {
                EdgeRegion.TopLeft or EdgeRegion.TopRight or EdgeRegion.BottomLeft or EdgeRegion.BottomRight =>
                    region.ToString(),
                _ => ShoreHue.Core.Controllers.EdgeRegionMapping.GetRegionKey(region)
            };

            // 再按当前所在区域键 → 收起（解除钉住）
            if (_visibilityController.IsVisible &&
                string.Equals(_edgeController.CurrentRegionKey, key, StringComparison.Ordinal))
            {
                _visibilityController.SetHotkeyPinned(false);
                _visibilityController.Hide();
                return;
            }

            if (_modeService.IsDoNotDisturb)
            {
                _modeService.IsDoNotDisturb = false;
                UpdateIconText();
            }
            _visibilityController.SetHotkeyPinned(true);   // ★ 键盘呼出不自动隐藏
            _edgeController.SummonRegion(region);
        }

        /// <summary>解析单键/组合 → EdgeRegion。first=0 表示单键。</summary>
        private static EdgeRegion ResolveRegion(int first, int second)
        {
            if (first == 0) return SingleRegion(second);
            return ComboRegion(first, second) ?? ComboRegion(second, first) ?? EdgeRegion.Unknown;
        }

        private static EdgeRegion SingleRegion(int d) => d switch
        {
            1 => EdgeRegion.BottomLeft,
            2 => EdgeRegion.Bottom_Center,
            3 => EdgeRegion.BottomRight,
            4 => EdgeRegion.Left_Center,
            6 => EdgeRegion.Right_Center,
            7 => EdgeRegion.TopLeft,
            8 => EdgeRegion.Top_Center,
            9 => EdgeRegion.TopRight,
            _ => EdgeRegion.Unknown
        };

        /// <summary>边键 + 邻角键 → 边端段（顺序不敏感，两方向都查）。</summary>
        private static EdgeRegion? ComboRegion(int a, int b)
        {
            return (a, b) switch
            {
                (4, 7) => EdgeRegion.Left_Top,
                (4, 1) => EdgeRegion.Left_Bottom,
                (6, 9) => EdgeRegion.Right_Top,
                (6, 3) => EdgeRegion.Right_Bottom,
                (8, 7) => EdgeRegion.Top_Left,
                (8, 9) => EdgeRegion.Top_Right,
                (2, 1) => EdgeRegion.Bottom_Left,
                (2, 3) => EdgeRegion.Bottom_Right,
                _ => null
            };
        }
    }
}
