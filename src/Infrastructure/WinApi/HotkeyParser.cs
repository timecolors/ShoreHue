using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 全局热键字符串解析/格式化工具。
    /// 存储格式：修饰键 + "+" + 主键，如 "Ctrl+Alt+Q"（顺序固定 Ctrl、Alt、Shift、Win）。
    /// 用于设置界面的热键捕获框与 RegisterHotKey 注册参数的相互转换。
    /// </summary>
    public static class HotkeyParser
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        /// <summary>
        /// 把设置字符串解析为 RegisterHotKey 参数。返回 false 表示格式无效（未设置/缺主键/缺修饰键）。
        /// </summary>
        public static bool TryParse(string? text, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false; // 必须至少一个修饰键 + 主键

            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win": case "windows": case "cmd": modifiers |= MOD_WIN; break;
                    default:
                        if (TryGetVk(p, out uint key))
                        {
                            vk = key;
                        }
                        else
                        {
                            return false; // 无法识别的主键
                        }
                        break;
                }
            }

            return modifiers != 0 && vk != 0;
        }

        /// <summary>把主键名（如 "Q"、"F5"、"Space"）转为虚拟键码。支持字母、数字、F1-F12 与常用功能键。</summary>
        private static bool TryGetVk(string name, out uint vk)
        {
            vk = 0;
            string n = name.ToUpperInvariant();
            if (n.Length == 1 && n[0] >= 'A' && n[0] <= 'Z') { vk = (uint)n[0]; return true; }
            if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') { vk = (uint)n[0]; return true; }
            if (n.StartsWith("F") && n.Length > 1 &&
                int.TryParse(n[1..], out int fnum) && fnum >= 1 && fnum <= 12)
            {
                vk = (uint)(0x70 + fnum - 1);
                return true;
            }
            switch (n)
            {
                case "SPACE": vk = 0x20; return true;
                case "TAB": vk = 0x09; return true;
                case "ENTER": case "RETURN": vk = 0x0D; return true;
                case "DELETE": vk = 0x2E; return true;
                case "INSERT": vk = 0x2D; return true;
                case "HOME": vk = 0x24; return true;
                case "END": vk = 0x23; return true;
                case "PAGEUP": vk = 0x21; return true;
                case "PAGEDOWN": vk = 0x22; return true;
                case "UP": vk = 0x26; return true;
                case "DOWN": vk = 0x28; return true;
                case "LEFT": vk = 0x25; return true;
                case "RIGHT": vk = 0x27; return true;
                default: return false;
            }
        }

        /// <summary>
        /// 判断一次按键是否命中某个快捷键设置（如 "Ctrl+N"）。
        /// ★ 放在这里而不是各控件自己写：格式解析（TryParse）与"当前按键转成同样的编码"必须用
        ///   同一套口径，否则设置里存了什么、控件就该认什么这件事会慢慢漂移。
        ///   便签面板的快捷键就是这么用的（KeyDown 冒泡到根 → Matches）。
        /// </summary>
        public static bool Matches(string? text, ModifierKeys modifiers, Key key)
        {
            if (!TryParse(text, out uint mods, out uint vk)) return false;

            uint cur = 0;
            if ((modifiers & ModifierKeys.Control) != 0) cur |= MOD_CONTROL;
            if ((modifiers & ModifierKeys.Alt) != 0) cur |= MOD_ALT;
            if ((modifiers & ModifierKeys.Shift) != 0) cur |= MOD_SHIFT;
            if ((modifiers & ModifierKeys.Windows) != 0) cur |= MOD_WIN;

            return cur == mods && (uint)KeyInterop.VirtualKeyFromKey(key) == vk;
        }

        /// <summary>把 WPF 按键事件转换为规范热键字符串（如 "Ctrl+Alt+Q"）。
        /// 纯修饰键或没有修饰键时返回空字符串（表示不可用）。</summary>
        public static string Format(Key key, ModifierKeys modifiers)
        {
            string main = GetKeyName(key);
            if (main.Length == 0) return "";

            var parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

            // 纯修饰键 / 无修饰键的普通键 → 无效（避免误触或与普通输入冲突）
            if (parts.Count == 0) return "";
            if (IsModifierKey(key)) return "";

            parts.Add(main);
            return string.Join("+", parts);
        }

        private static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

        /// <summary>WPF Key → 显示名（字母/数字/F键/常用功能键），不支持的返回空串。</summary>
        private static string GetKeyName(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return key.ToString();
            if (key >= Key.D0 && key <= Key.D9) return key.ToString().Substring(1); // "D0"→"0"
            if (key >= Key.F1 && key <= Key.F12) return key.ToString();
            switch (key)
            {
                case Key.Space: return "Space";
                case Key.Tab: return "Tab";
                case Key.Enter: return "Enter";
                case Key.Delete: return "Delete";
                case Key.Insert: return "Insert";
                case Key.Home: return "Home";
                case Key.End: return "End";
                case Key.PageUp: return "PageUp";
                case Key.PageDown: return "PageDown";
                case Key.Up: return "Up";
                case Key.Down: return "Down";
                case Key.Left: return "Left";
                case Key.Right: return "Right";
                default: return "";
            }
        }

        /// <summary>展示用：格式化修饰键与键码（用于冲突提示等）。</summary>
        public static string Format(uint modifiers, uint vk)
        {
            var parts = new List<string>();
            if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
            string main = VkToName(vk);
            if (main.Length > 0) parts.Add(main);
            return string.Join("+", parts);
        }

        private static string VkToName(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
            return vk switch
            {
                0x20 => "Space",
                0x09 => "Tab",
                0x0D => "Enter",
                0x2E => "Delete",
                0x2D => "Insert",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x26 => "Up",
                0x28 => "Down",
                0x25 => "Left",
                0x27 => "Right",
                _ => ""
            };
        }
    }
}
