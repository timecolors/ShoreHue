using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 系统 Toast 通知（Windows 通知中心）。
    /// 非打包桌面应用必须先在开始菜单创建带 System.AppUserModel.ID 的快捷方式，
    /// CreateToastNotifier(Aumid) 才能正常发送通知。
    /// </summary>
    public static class SystemToast
    {
        /// <summary>
        /// Toast AUMID：商店（MSIX）版使用包身份（PFN!App），
        /// 普通版使用固定 AUMID（配合开始菜单快捷方式注册）。
        /// </summary>
        public static string Aumid
        {
            get
            {
                if (AppPaths.IsPackaged)
                {
                    try
                    {
                        return Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
                    }
                    catch (Exception ex)
                    {
                        // 正常情况：非打包运行（绿色版）取不到包标识 → 用 "ShoreHue" 兜底
                        LogManager.Debug($"[通知] 读取包标识失败（按非打包运行处理）：{ex.Message}");
                    }
                }
                return "ShoreHue";
            }
        }
        private static readonly Guid PKEY_AppUserModelID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private static readonly object LogLock = new();

        private static string LogPath
        {
            get
            {
                try
                {
                    string dataDir = AppPaths.LogDirectory;
                    Directory.CreateDirectory(dataDir);
                    return AppPaths.SystemToastLogPath;
                }
                catch
                {
                    return Path.Combine(Path.GetTempPath(), "system-toast.log");
                }
            }
        }

        private static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { /* 通知自己的调试日志写不进去就不再记（避免自我递归） */ }
        }

        /// <summary>
        /// 确保开始菜单存在带 AUMID 的快捷方式。已存在且 AUMID 正确则跳过。
        /// </summary>
        public static void EnsureRegistered()
        {
            if (AppPaths.IsPackaged) return; // 商店包由包身份提供 AUMID，无需手工快捷方式
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    Log("EnsureRegistered: 找不到可执行文件，跳过");
                    return;
                }

                string lnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "ShoreHue.lnk");

                // ★ 判据必须包含「目标还有效」：只查「快捷方式文件在不在 + AUMID 对不对」的话，
                //   用户移动或删除安装目录（**卸载就是删安装目录**）之后快捷方式会变成死链 ——
                //   点它没反应，而且应用每次启动都在这里 return、**永远不自愈**。
                //   （2026-09 实测：目标被删后连启动两次都没修复。）
                bool lnkExists = File.Exists(lnk);
                string? lnkAumid = lnkExists ? ReadAumid(lnk) : null;
                string resolved = lnkExists ? ShortcutLinkResolver.Resolve(lnk) : "";
                bool targetExists = lnkExists && File.Exists(resolved);

                if (ShortcutUsable(lnkExists, lnkAumid, resolved, exe, targetExists))
                {
                    Log("EnsureRegistered: 快捷方式已存在、AUMID 正确、目标有效");
                    return;
                }

                if (lnkExists) Log($"EnsureRegistered: 快捷方式需重建（当前目标={resolved}，应为={exe}）");
                CreateShortcut(lnk, exe);
                string? written = ReadAumid(lnk);
                Log($"EnsureRegistered: 已创建/修复快捷方式，AUMID={(written ?? "<null>")}");
            }
            catch (Exception ex)
            {
                Log($"EnsureRegistered 异常: {ex}");
            }
        }

        /// <summary>
        /// 「现有快捷方式可以直接用吗」的判据（**纯函数**，便于钉住回归）：
        /// 三件事必须同时成立 —— ① 快捷方式文件在；② 它的 AUMID 是我们的；③
        /// **它指向的就是当前这个 exe，而且那个 exe 确实存在**。
        ///
        /// ★ 第 ③ 条曾经漏掉（2026-09 实测）：只查①+②时，用户移动或删除安装目录后
        ///   （**卸载就是删安装目录**；手动清理 bin 也一样）快捷方式会变成死链 ——
        ///   点它没反应，而应用每次启动都判定"已存在且 AUMID 正确"直接返回，**永不自愈**。
        /// </summary>
        internal static bool ShortcutUsable(bool lnkExists, string? lnkAumid, string resolvedTarget,
                                            string? expectedExe, bool targetExists)
            => lnkExists
               && string.Equals(lnkAumid, Aumid, StringComparison.Ordinal)
               && targetExists
               && !string.IsNullOrEmpty(expectedExe)
               && string.Equals(resolvedTarget, expectedExe, StringComparison.OrdinalIgnoreCase);

        private static void CreateShortcut(string lnk, string exe)
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetDescription("ShoreHue 边缘面板");

            var persist = (IPersistFile)link;
            persist.Save(lnk, true);

            // 写入 System.AppUserModel.ID
            var props = (IPropertyStore)link;
            var key = new PROPERTYKEY { fmtid = PKEY_AppUserModelID, pid = 5 };
            IntPtr str = Marshal.StringToCoTaskMemUni(Aumid);
            var value = new PROPVARIANT { vt = 31, pointer = str }; // VT_LPWSTR
            try
            {
                props.SetValue(ref key, ref value);
                props.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(str);
            }

            // 属性提交后再次保存，确保持久化
            persist.Save(lnk, true);
        }

        /// <summary>读取快捷方式的 System.AppUserModel.ID，失败返回 null。</summary>
        public static string? ReadAumid(string lnk)
        {
            try
            {
                // IShellLink 的 IPropertyStore.GetValue 对 .lnk 的 AUMID 不可靠（实测返回空），
                // 改用 Shell.Application 的 ExtendedProperty 读取，与资源管理器行为一致。
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                string dir = Path.GetDirectoryName(lnk) ?? "";
                string name = Path.GetFileName(lnk);
                dynamic folder = shell.Namespace(dir);
                dynamic item = folder.ParseName(name);
                object? value = item.ExtendedProperty("System.AppUserModel.ID");
                return value as string;
            }
            catch (Exception ex)
            {
                Log($"ReadAumid 异常: {ex}");
                return null;
            }
        }

        /// <summary>发送系统通知（Windows 通知中心），返回是否成功。</summary>
        public static bool Show(string title, string message)
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
                var doc = new XmlDocument();
                string t = System.Security.SecurityElement.Escape(title);
                string m = System.Security.SecurityElement.Escape(message);
                doc.LoadXml(
                    $"<toast><visual><binding template='ToastGeneric'>" +
                    $"<text>{t}</text><text>{m}</text>" +
                    $"</binding></visual></toast>");
                notifier.Show(new ToastNotification(doc));
                Log($"Show: 已发送通知 '{title}'");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Show 失败: {ex}");
                return false;
            }
        }

        // ================= COM：快捷方式 + AppUserModelID =================

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile(out string ppszFileName);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pointer;
        }
    }
}
