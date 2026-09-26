using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ShoreHue.Infrastructure.Utils
{
    /// <summary>
    /// 数据目录安全：把 %LOCALAPPDATA%\ShoreHue 的 ACL 收紧为
    /// 仅当前用户 + SYSTEM + Administrators（防同机其他账户读取 token/密钥/剪贴板历史）。
    /// </summary>
    public static class DataDirSecurity
    {
        public static void TightenAcl()
        {
            string dir = AppPaths.DataRoot;
            if (!Directory.Exists(dir)) return;

            // 不覆盖显式设置：仅当目录继承权限（存在其他用户可读风险）时才收紧
            var acl = new DirectoryInfo(dir).GetAccessControl();
            bool hasForeign = false;
            try
            {
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(NTAccount)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow) continue;
                    if (rule.IdentityReference.Value.StartsWith("Everyone", StringComparison.OrdinalIgnoreCase) ||
                        rule.IdentityReference.Value.StartsWith("Users", StringComparison.OrdinalIgnoreCase))
                    {
                        hasForeign = true;
                        break;
                    }
                }
            }
            catch { /* 读不出 ACL 就**不擅自改权限**：盲改可能把用户自己的访问权弄坏；%LOCALAPPDATA% 默认本就仅本人可读 */ }

            if (!hasForeign) return;   // 已安全

            var newAcl = new DirectorySecurity();
            var current = WindowsIdentity.GetCurrent().User;
            if (current != null)
                newAcl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            newAcl.AddAccessRule(new FileSystemAccessRule(@"NT AUTHORITY\SYSTEM", FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            newAcl.AddAccessRule(new FileSystemAccessRule(@"BUILTIN\Administrators", FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(newAcl);
        }
    }
}
