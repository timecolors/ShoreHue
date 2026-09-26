using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 解析 .lnk 快捷方式的目标路径。
    /// 使用 COM Interop 直接调用 IShellLinkW，避免项目引用 Shell32 COM
    /// （.NET Core 版 MSBuild 不支持 ResolveComReference，会导致项目无法编译）。
    /// </summary>
    public static class ShortcutLinkResolver
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        private const uint SLGP_UNCPRIORITY = 0x2;
        private const uint SLR_NO_UI = 0x1;
        private const uint STGM_READ = 0x00000000;

        public static string Resolve(string shortcutPath)
        {
            try
            {
                if (string.IsNullOrEmpty(shortcutPath) ||
                    !shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    return shortcutPath;
                }

                var link = (IShellLinkW)new ShellLink();
                // ★ 必须先用 IPersistFile::Load 把 .lnk **读进来**，否则这个 IShellLink 实例里
                //   什么都没有：旧实现在未加载的情况下调用 SetPath(shortcutPath)，
                //   于是 GetPath 返回的就是那个 .lnk 路径本身 —— 解析等于没做
                //   （结果是"最近使用"列表里塞满 .lnk 条目、RecentAppTracker 的 .exe 复核永不通过）。
                ((IPersistFile)link).Load(shortcutPath, STGM_READ);
                link.Resolve(IntPtr.Zero, SLR_NO_UI);

                var buffer = new StringBuilder(1024);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SLGP_UNCPRIORITY);

                string target = buffer.ToString();
                return string.IsNullOrEmpty(target) ? shortcutPath : target;
            }
            catch
            {
                return shortcutPath;
            }
        }
    }
}
