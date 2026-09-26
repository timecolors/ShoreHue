// ==================== 海床文件操作（纯文件系统 + manifest 语义） ====================
//
// 为什么单独成类：海床页（SeabedPage）已经 2500+ 行，文件操作再塞进去就是往上帝类上继续堆。
// 本类**只依赖 System.IO / System.Text.Json**：不弹窗、不碰界面、不认识 FlatNode。
// 约定：一律**返回结果**（Ok + 一句可直接显示的状态文字），由调用方决定怎么提示。
// 因此它可以被直接单测（`ShoreHue.Tests` 有 InternalsVisibleTo）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.UI.Seabed
{
    internal static class SeabedFs
    {
        /// <summary>操作结果：Ok=false 时 Message 是失败原因（已经是可以直接显示的一句话）。</summary>
        internal readonly record struct Result(bool Ok, string Message, string? Warn = null);

        /// <summary>新名字的合法性检查；返回 null 表示可用。</summary>
        internal static string? ValidateNewName(string dir, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "名称不能为空";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称含非法字符";
            string target = Path.Combine(dir, name);
            if (File.Exists(target) || Directory.Exists(target)) return "同名已存在：" + name;
            return null;
        }

        /// <summary>
        /// 保存前是否该提示"磁盘已被改动、仍要覆盖吗"（VS Code 语义）。
        /// 判据：**我自己改过**（编辑内容 ≠ 打开时的快照），且**磁盘既不是我的快照、也不是我要写的内容**
        /// —— 后者说明是别人（或别的程序）改的，覆盖会让用户的改动丢失。
        /// ★ 抽成纯函数的原因：这段语义以前只是页面里一层嵌套 if，**测不到**；
        ///   而它是"用户会不会丢改动"的关键判定。
        /// </summary>
        internal static bool ShouldWarnBeforeOverwrite(string? snapshot, string? diskContent, string? editingContent)
            => snapshot != editingContent && diskContent != snapshot && diskContent != editingContent;

        /// <summary>
        /// 保存时该不该**改成重新载入**而不是写盘（VS Code 语义：干净缓冲区跟随磁盘）。
        /// 判据：**我没编辑**（编辑内容 == 打开时的快照），但**磁盘已经不是那一版**了。
        /// ★ 为什么保存路径还要判一次（页面里 watcher 已经在同步磁盘了）：
        ///   watcher 事件**可能漏**（同秒覆盖、目录被占用、外部程序瞬间改两次、应用刚启动还没订阅）。
        ///   一旦漏了，上面那个"要不要覆盖"的判定会因为 `snapshot == editingContent` 而放行，
        ///   于是把**打开时的旧内容**写回去、外部改动无声消失 —— 这正是 VS Code 在**写盘那一刻**
        ///   还要比对文件版本（dirty write prevention）的原因。这里等价地把关。
        /// </summary>
        internal static bool ShouldReloadFromDisk(string? snapshot, string? editingContent, string? diskContent)
            => snapshot == editingContent && diskContent != snapshot;

        // ==================== 新建 ====================

        internal static Result CreateFolder(string dir, string name)
        {
            if (ValidateNewName(dir, name) is string bad) return new Result(false, bad);
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, name));
                return new Result(true, "已新建文件夹：" + name + "（放入代码文件即成为功能）");
            }
            catch (Exception ex)
            {
                return new Result(false, "新建失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 新建文件：内容按扩展名给骨架。
        /// ★ `.xaml` 会**连 `.xaml.cs` 一起建** —— 完全编程形态必须成对，单独一个 .xaml 加载不了。
        /// ★ 文件名还必须与**目录名**一致（加载器按 &lt;目录名&gt;.xaml 查找），不一致时在结果里给警告，
        ///   因为这条规则不看代码是猜不到的（今天刚在别处踩过同类坑）。
        /// </summary>
        internal static Result CreateFile(string dir, string name)
        {
            if (ValidateNewName(dir, name) is string bad) return new Result(false, bad);
            try
            {
                string target = Path.Combine(dir, name);
                string ext = Path.GetExtension(name).ToLowerInvariant();
                string stem = Path.GetFileNameWithoutExtension(name);

                File.WriteAllText(target, SeabedFileTemplates.ForExtension(ext, stem), new System.Text.UTF8Encoding(false));

                string? warn = null;
                if (ext == ".xaml")
                {
                    string csPath = target + ".cs";
                    if (!File.Exists(csPath))
                        File.WriteAllText(csPath, SeabedFileTemplates.XamlCs(stem), new System.Text.UTF8Encoding(false));

                    string dirName = Path.GetFileName(dir);
                    if (!string.Equals(stem, dirName, StringComparison.OrdinalIgnoreCase))
                        warn = "⚠ 完全编程要求文件名与目录名一致（本目录名：" + dirName + "），否则加载器不会把它当作该功能的界面";
                }

                return new Result(true, "已新建文件：" + name + (warn == null ? "" : "（已同时建 .xaml.cs）"), warn);
            }
            catch (Exception ex)
            {
                return new Result(false, "新建文件失败：" + ex.Message);
            }
        }

        // ==================== 重命名 ====================

        /// <summary>
        /// 重命名文件/目录。目录改名后**同步 manifest.name**（id 保持稳定，否则树与文件夹两份真相不一致）；
        /// manifest 更新失败只记日志、不让整个重命名失败 —— 文件已经改名成功，回滚更糟。
        /// </summary>
        internal static Result Rename(string path, bool isDir, string newName)
        {
            string oldName = Path.GetFileName(path);
            string parent = Path.GetDirectoryName(path) ?? "";
            if (string.IsNullOrEmpty(parent)) return new Result(false, "找不到所在目录：" + path);

            if (string.IsNullOrWhiteSpace(newName)) return new Result(false, "名称不能为空");
            if (newName == oldName) return new Result(true, "");            // 没改：静默成功
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return new Result(false, "名称含非法字符");

            string target = Path.Combine(parent, newName);
            if (File.Exists(target) || Directory.Exists(target)) return new Result(false, "同名已存在：" + newName);

            try
            {
                if (isDir) Directory.Move(path, target); else File.Move(path, target);
            }
            catch (Exception ex)
            {
                return new Result(false, "重命名失败：" + ex.Message);
            }

            if (isDir) SyncManifestName(target, newName);
            return new Result(true, "已重命名：" + oldName + " → " + newName);
        }

        private static void SyncManifestName(string dir, string newName)
        {
            string mf = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mf)) return;
            try
            {
                string json = File.ReadAllText(mf);
                var m = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                if (m == null) return;
                m["name"] = newName;
                File.WriteAllText(mf, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // ★ 文件夹改名了但 manifest 里的 name 没跟上 → 树↔文件夹两份真相不一致，必须留痕
                LogManager.Warning($"[海床] 重命名后更新 manifest 失败（文件夹名与 manifest 不一致）：{ex.Message}");
            }
        }

        // ==================== 拖入：把外部文件/目录复制进来 ====================

        /// <summary>
        /// 把外部路径**复制**进目标目录（不动源文件）。
        /// ★ 同名一律**跳过**、不覆盖：宁可少做一步，也不静默改掉用户已有的文件。
        /// </summary>
        internal static Result CopyInto(IReadOnlyList<string> sources, string destDir)
        {
            if (!Directory.Exists(destDir)) return new Result(false, "目标目录不存在：" + destDir);

            int ok = 0;
            var skipped = new List<string>();
            var failed = new List<string>();

            foreach (var raw in sources)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string src = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(src);
                if (string.IsNullOrEmpty(name)) continue;

                string dst = Path.Combine(destDir, name);
                if (File.Exists(dst) || Directory.Exists(dst)) { skipped.Add(name); continue; }

                try
                {
                    if (Directory.Exists(src)) CopyDirInto(src, dst);
                    else File.Copy(src, dst);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed.Add(name + "（" + ex.Message + "）");
                }
            }

            string msg = "已放入 " + ok + " 项到「" + Path.GetFileName(destDir) + "」";
            if (skipped.Count > 0)
                msg += "；跳过同名 " + skipped.Count + " 项（不覆盖）：" + string.Join("、", skipped.Take(3))
                       + (skipped.Count > 3 ? " 等" : "");
            if (failed.Count > 0)
                msg += "；失败 " + failed.Count + " 项：" + string.Join("、", failed.Take(2));

            return new Result(failed.Count == 0, msg);
        }

        private static void CopyDirInto(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
            foreach (var d in Directory.GetDirectories(src))
                CopyDirInto(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // ==================== 删除（回收站） ====================

        /// <summary>
        /// 删除文件/目录到**回收站**（可恢复）。
        /// ★ "拒绝删除海床根目录"是本方法的硬约束，之所以放在这里而不是散在页面里，是为了**能被单测钉住**
        ///   （以前它只是页面里一句 `if`，谁都测不到）。
        /// 回收站 API 失败（被占用 / 权限 / 无 shell）时回退为永久删除；永久删除也失败才如实报错。
        /// </summary>
        internal static Result DeleteToRecycleBin(string path, bool isDir, string root)
        {
            if (string.IsNullOrWhiteSpace(path)) return new Result(false, "删除失败：路径为空");

            if (string.Equals(path.TrimEnd('\\', '/'), (root ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return new Result(false, "不能删除海床根目录");

            if (!File.Exists(path) && !Directory.Exists(path))
                return new Result(false, "删除失败：找不到「" + Path.GetFileName(path) + "」");

            string name = Path.GetFileName(path);
            try
            {
                if (isDir)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                // 回退：永久删除。这里**记一条 Debug**：用户以为进了回收站、其实被永久删了，事后要能查。
                try
                {
                    if (isDir) Directory.Delete(path, true); else File.Delete(path);
                    LogManager.Debug($"[海床] 送回收站失败，已永久删除「{name}」：{ex.Message}");
                }
                catch (Exception ex2)
                {
                    return new Result(false, "删除失败：" + ex2.Message + "（若文件被其他程序占用，请关闭后重试）");
                }
            }
            return new Result(true, "已删除（回收站）：" + name);
        }
    }
}
