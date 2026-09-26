using System.Collections.Generic;
using System.Linq;

namespace ShoreHue.UI.Widgets.Dynamic
{
    /// <summary>
    /// 小组件/单预设源码权限检测：扫描源码用到的能力并自动标注。
    /// 仅在「其他海床」市场上传/导出时调用 Detect(源码) 做检测并随包下发风险标签，
    /// 导入方用 PermissionLabel/Describe 展示提醒用户；本地编程不做检测。
    /// 检测是保守的（宁可多标不可漏标）：命中关键字即标注，供风险提示用，不代表一定执行。
    /// </summary>
    public static class WidgetPermissions
    {
        /// <summary>检测源码所需权限（可多个，按 network/clipboard/file/process/system/window/screen 顺序）。</summary>
        public static List<string> Detect(string source)
        {
            var perms = new List<string>();
            if (string.IsNullOrEmpty(source)) return perms;
            string lower = source.ToLower();

            // 联网：HTTP/网络请求
            if (lower.Contains("httpclient") || lower.Contains("webclient") ||
                lower.Contains("httprequest") || lower.Contains("webrequest") ||
                lower.Contains("httplistener") || lower.Contains("system.net") ||
                lower.Contains("tcpclient") || lower.Contains("udpclient") ||
                lower.Contains("websocket") || lower.Contains("downloadstring") ||
                lower.Contains("getstringasync") || lower.Contains("postasync") ||
                lower.Contains("sendasync") || lower.Contains("uri(") ||
                // ★ 宿主的天气服务也走互联网（固定端点）：插件调它同样是"把请求发出去"，
                //   不标注就等于静默联网。固定端点只降低外带能力，不改变"这是联网"这一事实。
                lower.Contains("weatherservice"))
            {
                perms.Add("network");
            }

            // 剪贴板
            if (lower.Contains("clipboard") || lower.Contains("idataobject"))
            {
                perms.Add("clipboard");
            }

            // 本地文件
            if (lower.Contains("system.io") || lower.Contains("file.") ||
                lower.Contains("directory.") || lower.Contains("filestream") ||
                lower.Contains("streamwriter") || lower.Contains("streamreader") ||
                lower.Contains("path.combine") || lower.Contains("savedialog") ||
                lower.Contains("opendialog") || lower.Contains("fileinfo") ||
                // ★ 快捷方式解析要读磁盘上的 .lnk（宿主 COM 实现，符号层看不到文件访问）。
                //   信息量很小，但按本类"宁可多标不可漏标"的口径应如实标注为"读文件"。
                lower.Contains("shortcutlinkresolver"))
            {
                perms.Add("file");
            }

            // ⚙进程与命令执行
            if (lower.Contains("process.start") || lower.Contains("processstartinfo") ||
                lower.Contains("new process(") || lower.Contains("cmd.exe") ||
                lower.Contains("powershell") || lower.Contains("useshellexecute") ||
                lower.Contains("shell.execute"))
            {
                perms.Add("process");
            }

            // 系统信息 / 注册表 / 原生调用
            if (lower.Contains("system.management") || lower.Contains("managementobject") ||
                lower.Contains("registry") || lower.Contains("performancecounter") ||
                lower.Contains("dllimport") || lower.Contains("environment.getenvironmentvariable") ||
                lower.Contains("wmi"))
            {
                perms.Add("system");
            }

            // 窗口与输入（可能影响其他应用或监听输入）
            if (lower.Contains("findwindow") || lower.Contains("enumwindows") ||
                lower.Contains("setforegroundwindow") || lower.Contains("getforegroundwindow") ||
                lower.Contains("windowfrompoint") || lower.Contains("sendmessage") ||
                lower.Contains("sendinput") || lower.Contains("setwindowshookex") ||
                lower.Contains("keybd_event") || lower.Contains("mouse_event") ||
                lower.Contains("getcursorpos") || lower.Contains("setcursorpos"))
            {
                perms.Add("window");
            }

            // 屏幕捕获
            if (lower.Contains("copyfromscreen") || lower.Contains("printwindow") ||
                lower.Contains("bitblt") || lower.Contains("dwmthumbnail") ||
                lower.Contains("screencapture") || lower.Contains("getwindowrect") ||
                lower.Contains("graphics.copy"))
            {
                perms.Add("screen");
            }

            // 硬件 / 系统开关：屏幕亮度、蓝牙与 Wi-Fi 无线电、移动热点。
            // ★ 这三个宿主 API（DisplayBrightness / SystemRadios / HotspotControl）在沙箱面里是**开放**的
            //   （它们不在宿主特权黑名单里，因为宿主与市场包都要用，且属于"合法但有后果"而非攻击能力）。
            //   既然不硬拦，就必须**让用户在安装时看见**：按决策表 B 类的处理方式归入"权限 + 告知"，
            //   与剪贴板/联网同一档。以前它们既不被拦、也不出现在安装提示里 —— 等于静默给硬件控制权。
            if (lower.Contains("displaybrightness") || lower.Contains("setbrightness") ||
                lower.Contains("systemradios") || lower.Contains("radiokind") ||
                lower.Contains("hotspotcontrol") || lower.Contains("setstateasync"))
            {
                perms.Add("device");
            }

            return perms;
        }

        /// <summary>
        /// 已知权限名的**单一真相源**：标签、后果说明、市场校验都从这里取，别再各写一份 switch。
        /// ★ 市场校验用它拦"不存在的权限名"（AI 生成的包常写出宿主不认识的权限 → 能力被静默忽略）。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _known = new(System.StringComparer.Ordinal)
        { "network", "clipboard", "file", "process", "system", "window", "screen", "device" };

        /// <summary>这个权限名宿主是否认识。</summary>
        internal static bool IsKnown(string? permission)
            => !string.IsNullOrEmpty(permission) && _known.Contains(permission);

        /// <summary>权限 → 显示标签。★ 文案走 resx：以前是硬编码中文，英文界面下安装弹窗显示中文。</summary>
        public static string PermissionLabel(string p) => ShoreHue.UI.Localization.LocalizationManager.Instance[
            p switch
            {
                "network" => "Perm_Label_network",
                "clipboard" => "Perm_Label_clipboard",
                "file" => "Perm_Label_file",
                "process" => "Perm_Label_process",
                "system" => "Perm_Label_system",
                "window" => "Perm_Label_window",
                "screen" => "Perm_Label_screen",
                "device" => "Perm_Label_device",
                _ => "Perm_Label_None"
            }];

        /// <summary>权限 → **一句话后果**。安装弹窗给用户看的不该是权限代号，而是"这意味着什么"
        /// （Chrome 扩展的安装提示就是这个路数：列能力 + 说影响，把决定权交给用户）。
        /// ★ 文案走 resx（同上：以前硬编码中文，英文界面下弹窗是中文）。</summary>
        public static string ConsequenceLabel(string p)
        {
            var loc = ShoreHue.UI.Localization.LocalizationManager.Instance;
            return p switch
            {
                "network" => loc["Perm_Consequence_network"],
                "clipboard" => loc["Perm_Consequence_clipboard"],
                "file" => loc["Perm_Consequence_file"],
                "process" => loc["Perm_Consequence_process"],
                "system" => loc["Perm_Consequence_system"],
                "window" => loc["Perm_Consequence_window"],
                "screen" => loc["Perm_Consequence_screen"],
                "device" => loc["Perm_Consequence_device"],
                _ => string.Format(loc["Perm_Unknown"], p)
            };
        }

        /// <summary>把权限列表渲染成"· 联网：可以把数据发送到互联网"形式的多行文本。</summary>
        public static string DescribeConsequences(IEnumerable<string>? perms)
        {
            if (perms == null) return "";
            var list = perms.ToList();
            if (list.Count == 0) return "";
            return string.Join("\n", list.Select(p => "  · " + PermissionLabel(p) + "：" + ConsequenceLabel(p)));
        }

        /// <summary>把权限列表渲染为紧凑可读文本（空/未知 → "无权限"）。</summary>
        public static string Describe(IEnumerable<string>? perms)
        {
            if (perms == null) return "无权限";
            var list = perms.ToList();
            if (list.Count == 0) return "无权限";
            return string.Join(" ", list.Select(PermissionLabel));
        }
    }
}
