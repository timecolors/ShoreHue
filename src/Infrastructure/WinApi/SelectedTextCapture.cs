using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 捕获当前前台窗口中用户选中的文本（划词翻译 用）。
    ///
    /// 方案：
    ///  1. 记录复制前剪贴板文本作为基线
    ///  2. 向前台窗口发送 Ctrl+C（SendInput，先抬起 Alt/Win 避免热键残留干扰）
    ///  3. 轮询剪贴板，直到内容与基线不同（这才是目标应用真正写入的选中文本）
    ///  4. 恢复原剪贴板
    ///  5. 剪贴板方案失败时回退 UIA：TextPattern 直接读取选中文本
    ///
    /// 必须在 STA 线程调用（WPF UI 线程即可）。
    /// </summary>
    public static class SelectedTextCapture
    {
        /// <summary>捕获结果：Success 时 Text 为选中文本；失败时 Message 说明原因。</summary>
        public sealed class CaptureResult
        {
            public string? Text { get; init; }
            public string Message { get; init; } = "";
            public bool Success => !string.IsNullOrEmpty(Text);
        }

        // ---------- Win32 ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;   // Alt
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_C = 0x43;

        private const int MaxPollAttempts = 20;
        private const int PollIntervalMs = 80;

        /// <summary>
        /// 捕获前台窗口的选中文本；失败时 Success=false 且 Message 说明原因。
        /// </summary>
        public static async Task<CaptureResult> CaptureAsync(IntPtr ownHwnd = default)
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero)
                {
                    return new CaptureResult { Message = ShoreHue.UI.Localization.LocalizationManager.Instance["Capture_NoFg"] };
                }
                if (ownHwnd != IntPtr.Zero && fg == ownHwnd)
                {
                    return new CaptureResult { Message = ShoreHue.UI.Localization.LocalizationManager.Instance["Capture_SwitchApp"] };
                }

                // 1. 剪贴板方案
                var clipboardResult = await CaptureViaClipboardAsync();
                if (clipboardResult.Success)
                {
                    return clipboardResult;
                }

                // 2. UIA 回退：部分应用（Word/旧记事本）可直接读选中文本。
                //    放后台线程执行，避免复杂窗口（如浏览器）的 UIA 遍历卡住面板 UI
                string? uiaText = await Task.Run(() => TryReadSelectionViaUia(fg));
                if (!string.IsNullOrWhiteSpace(uiaText))
                {
                    return new CaptureResult { Text = uiaText.Trim() };
                }

                return new CaptureResult { Message = ShoreHue.UI.Localization.LocalizationManager.Instance["Capture_NoSelection"] };
            }
            catch (Exception ex)
            {
                Log("捕获异常: " + ex);
                return new CaptureResult { Message = string.Format(ShoreHue.UI.Localization.LocalizationManager.Instance["Capture_Failed"], ex.Message) };
            }
        }

        /// <summary>
        /// 剪贴板方案：基线 + 变化轮询。关键修复：必须等到剪贴板内容与复制前不同，
        /// 否则读到的是用户之前复制的旧内容。
        /// </summary>
        private static async Task<CaptureResult> CaptureViaClipboardAsync()
        {
            // 记录基线（复制前的文本）
            string baseline = "";
            try
            {
                if (Clipboard.ContainsText())
                {
                    baseline = Clipboard.GetText() ?? "";
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：读不到基线 → 基线按空串算，最坏是把"已有内容"当成选中文本（后面还有 UIA 回退）
                Log("读取剪贴板基线失败（按空基线继续）: " + ex.Message);
            }

            // 保存原剪贴板对象（恢复用）
            IDataObject? original = null;
            bool hasOriginal = false;
            try
            {
                original = Clipboard.GetDataObject();
                hasOriginal = original != null;
            }
            catch (Exception ex)
            {
                // 尽力而为：拿不到原剪贴板对象 → 捕获后无法还原（用户原内容被选中文本顶掉，仅此而已）
                Log("保存原剪贴板对象失败（本次捕获后不还原剪贴板）: " + ex.Message);
            }

            try
            {
                SendCtrlCWithModifierRelease();

                for (int i = 0; i < MaxPollAttempts; i++)
                {
                    await Task.Delay(PollIntervalMs);
                    try
                    {
                        if (!Clipboard.ContainsText()) continue;

                        string now = Clipboard.GetText() ?? "";
                        // ★ 内容与基线不同 → 目标应用已写入选中文本
                        if (now != baseline && !string.IsNullOrWhiteSpace(now))
                        {
                            return new CaptureResult { Text = now.Trim() };
                        }
                    }
                    catch
                    {
                        // 剪贴板被目标应用短暂占用（SetClipboardData 期间），继续轮询
                    }
                }

                // 轮询超时：再试一次读取（覆盖“选中文本恰好等于基线文本”的边界情况）
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string late = Clipboard.GetText()?.Trim() ?? "";
                        if (late.Length > 0)
                        {
                            return new CaptureResult { Text = late };
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 按"没抓到"处理，返回空 Message → 调用方走 UIA 回退（这条路本来就是兜底）
                    Log("最后一次读取剪贴板失败（转 UIA 回退）: " + ex.Message);
                }

                return new CaptureResult { Message = "" }; // 交给调用方走 UIA 回退
            }
            finally
            {
                await RestoreClipboardAsync(original, hasOriginal);
            }
        }

        /// <summary>
        /// 发送 Ctrl+C。若 Alt / Win 仍处于按下状态（热键触发时常见），先抬起，
        /// 避免目标应用把组合键解释为 Alt 菜单等行为。
        /// </summary>
        private static void SendCtrlCWithModifierRelease()
        {
            // 抬起 Alt / Win（若当前按下），防止干扰；Ctrl/Shift 保留即可
            ReleaseKeyIfDown(VK_MENU);
            ReleaseKeyIfDown(VK_LWIN);
            ReleaseKeyIfDown(VK_RWIN);

            var inputs = new[]
            {
                KeyInput(VK_CONTROL, 0),
                KeyInput(VK_C, 0),
                KeyInput(VK_C, KEYEVENTF_KEYUP),
                KeyInput(VK_CONTROL, KEYEVENTF_KEYUP)
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void ReleaseKeyIfDown(ushort vk)
        {
            try
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    var up = new[] { KeyInput(vk, KEYEVENTF_KEYUP) };
                    SendInput(1, up, Marshal.SizeOf<INPUT>());
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：松键失败最坏是修饰键"逻辑上仍按着"，再点一下该键即可恢复；
                //   这里不能抛（抛了会把 Ctrl+C 的捕获流程整个打断）
                Log("释放修饰键失败（最坏情况修饰键保持按下）: " + ex.Message);
            }
        }

        private static INPUT KeyInput(ushort vk, uint flags)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        /// <summary>UIA 回退：通过 TextPattern 读取可编辑控件/文档中的选中文本。</summary>
        private static string? TryReadSelectionViaUia(IntPtr hwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                // 1. 优先 TextPattern（支持 GetSelection）
                var tpCond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
                var tpEl = root.FindFirst(TreeScope.Descendants, tpCond);
                if (tpEl != null)
                {
                    try
                    {
                        var tp = (TextPattern)tpEl.GetCurrentPattern(TextPattern.Pattern);
                        var selection = tp.GetSelection();
                        if (selection != null && selection.Length > 0)
                        {
                            string? text = selection[0].GetText(-1);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 尽力而为：这个元素的 TextPattern 取不到就试下一个（下面还有 ValuePattern）
                        Log("UIA TextPattern 读取失败（继续试其它模式）: " + ex.Message);
                    }
                }

                // 2. 备用 ValuePattern（简单输入框）
                var valueCond = new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true);
                var valueEl = root.FindFirst(TreeScope.Descendants, valueCond);
                if (valueEl != null)
                {
                    try
                    {
                        var vp = (ValuePattern)valueEl.GetCurrentPattern(ValuePattern.Pattern);
                        string? v = vp.Current.Value;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            return v;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 尽力而为：ValuePattern 也取不到 → 该元素放弃，继续遍历
                        Log("UIA ValuePattern 读取失败（继续遍历）: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                // UIA 整体失败 = 这个应用的控件树读不出来（返回 null，调用方按"没选到"处理）
                Log("UIA 选区读取整体失败（按未选中处理）: " + ex.Message);
            }
            return null;
        }

        private static async Task RestoreClipboardAsync(IDataObject? original, bool hasOriginal)
        {
            const int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (hasOriginal && original != null)
                    {
                        Clipboard.SetDataObject(original, true);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                    return;
                }
                catch
                {
                    await Task.Delay(60);
                }
            }
        }

        private static void Log(string msg)
        {
            try
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[SelectedTextCapture] " + msg);
            }
            catch { /* 记日志本身失败就不再记（避免自我递归） */ }
        }
    }
}
