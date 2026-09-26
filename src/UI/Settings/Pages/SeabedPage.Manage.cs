// ==================== 海床 · 文件管理的「界面胶水」 ====================
//
// 这一层只做四件事：**弹窗问名字 → 调 SeabedFs → 写状态栏 → 刷新树**。
// 文件系统逻辑在 `SeabedFs`（纯 IO、可单测），模板字符串在 `SeabedFileTemplates`（纯字符串、可单测），
// 这里不再出现任何 File/Directory 操作 —— 免得 SeabedPage（2500+ 行）继续膨胀成上帝类。
//
// 范围（2026-09-13 与用户逐条确认）：只做普通用户天天会用到的 ——
//   新建文件（带能直接编译/加载的骨架）、从资源管理器拖入、快捷键 F2/Del/Ctrl+S/Ctrl+N/Ctrl+Shift+N/F5、
//   按名字筛选（全深度、显示相对路径）。
// 有意不做：行号与语法高亮（真想写好代码的人会用 VSCode，这是工作量与包体最大的一项）、
//   多标签编辑（`.xaml`/`.xaml.cs` 那一对已有固定两页）、剪切/复制/粘贴（已有"另存为变体"）、
//   多选批量删除、跨文件内容搜索。
// 工作区范围：**只管理 ShoreHue 自己的 seabed 目录**，不引入"打开任意文件夹" ——
//   保存/删除/编译/重命名全依赖目录里的 manifest 语义，换成任意目录会拿自己家的规矩去动用户的真实文件。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.UI.Seabed;

namespace ShoreHue.UI.Settings.Pages
{
    public partial class SeabedPage
    {
        private string _treeFilter = "";

        private bool _treeExpandInitialized;

        /// <summary>
        /// 首次加载时把各级目录**默认展开**：旧版配置树是一次列全三级的，资源管理器的"逐级点开"
        /// 在三级这种小规模下没必要（实测整棵只有 4 组 / 20 个二级 / 14 个三级），
        /// 用户看不到全貌反而会以为"分类有问题"。之后用户的折叠状态照常被尊重。
        /// </summary>
        private void EnsureInitialExpansion()
        {
            if (_treeExpandInitialized) return;
            _treeExpandInitialized = true;
            try
            {
                foreach (var g in Directory.GetDirectories(ExplorerRoot))
                {
                    _expandedDirs.Add(g);
                    foreach (var s in Directory.GetDirectories(g))
                    {
                        _expandedDirs.Add(s);
                        foreach (var t in Directory.GetDirectories(s)) _expandedDirs.Add(t);
                    }
                }
            }
            catch (Exception ex)
            {
                // 展开状态初始化失败不影响正确性：树照样能点开
                LogManager.Debug($"[海床] 初始化展开状态失败（树仍可用）：{ex.Message}");
            }
        }

        /// <summary>新建/拖放时的目标目录：选中项是目录则用它、是文件则用其所在目录；都没有则用海床根。</summary>
        private string CurrentDirForNew()
        {
            var fn = lstConfigTree?.SelectedItem as FlatNode;
            string? dir = fn != null ? CtxDirOf(fn) : null;
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : ExplorerRoot;
        }

        // ==================== 快捷键 ====================

        /// <summary>树上键盘快捷键（沿用 VSCode 习惯；只覆盖文件管理那几个）。</summary>
        private void Tree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            var fn = lstConfigTree?.SelectedItem as FlatNode;

            switch (e.Key)
            {
                case System.Windows.Input.Key.F2:
                    if (fn?.FsPath == null) return;
                    RenameFsNode(fn);
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.Delete:
                    if (fn?.FsPath == null) return;
                    TryArmOrDelete(fn, null);   // 与行末 ✕ / 右键删除一致：两击确认，走回收站
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.F5:
                    LoadExplorerTree();
                    txtJsonStatus.Text = "已刷新";
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.N when ctrl && shift:
                    NewFolderIn(CurrentDirForNew());
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.N when ctrl:
                    NewFileIn(CurrentDirForNew());
                    e.Handled = true;
                    break;

                case System.Windows.Input.Key.S when ctrl:
                    BtnSaveFile_Click(this, new System.Windows.RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }

        // ==================== 新建（菜单与快捷键共用） ====================

        /// <summary>右键 → 新建文件。</summary>
        private void Fs_NewFile_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var fn = CtxNode(sender);
            NewFileIn(fn != null ? CtxDirOf(fn) : null);
        }

        private void NewFileIn(string? parent)
        {
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                txtJsonStatus.Text = "请先选中一个目录（或目录里的文件）再新建";
                return;
            }

            var dlg = new InputDialog("海床 · 新建文件",
                "在「" + Path.GetFileName(parent) + "」下新建文件，输入文件名（含扩展名，如 main.cs / 我的功能.xaml）：", "main.cs");
            dlg.Owner = System.Windows.Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;

            var r = SeabedFs.CreateFile(parent, dlg.ResultText.Trim());
            if (r.Ok) _expandedDirs.Add(parent);
            LoadExplorerTree();
            txtJsonStatus.Text = r.Message + (r.Warn == null ? "" : "　" + r.Warn);
        }

        /// <summary>新建文件夹的实现（右键菜单与 Ctrl+Shift+N 共用，避免两处逻辑漂移）。</summary>
        private void NewFolderIn(string? parent)
        {
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                txtJsonStatus.Text = "请先选中一个目录（或目录里的文件）再新建文件夹";
                return;
            }

            var dlg = new InputDialog("海床 · 新建文件夹",
                "在「" + Path.GetFileName(parent) + "」下新建文件夹，输入名称：", "新功能");
            dlg.Owner = System.Windows.Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;

            var r = SeabedFs.CreateFolder(parent, dlg.ResultText.Trim());
            if (r.Ok) _expandedDirs.Add(parent);
            LoadExplorerTree();
            txtJsonStatus.Text = r.Message;
        }

        // ==================== 重命名（右键与 F2 共用） ====================

        /// <summary>重命名的实现：问名字 → 交给 SeabedFs 做移动与 manifest 同步 → 收拾界面状态。</summary>
        private void RenameFsNode(FlatNode fn)
        {
            if (fn?.FsPath == null) return;
            string src = fn.FsPath;
            string parent = Path.GetDirectoryName(src) ?? "";
            if (string.IsNullOrEmpty(parent)) return;

            var dlg = new InputDialog("海床 · 重命名", "输入新名称：", Path.GetFileName(src));
            dlg.Owner = System.Windows.Window.GetWindow(this);
            if (dlg.ShowDialog() != true) return;

            string newName = dlg.ResultText.Trim();
            var r = SeabedFs.Rename(src, fn.FsIsDir, newName);
            if (r.Ok && r.Message.Length == 0) return;          // 名字没变：什么都不做
            if (r.Ok)
            {
                // 选中的正是这个文件 → 它已经不在原路径，清掉右侧编辑器标题免得指向不存在的文件
                if (_fsPath == src) { _fsPath = null; txtNodeTitle.Text = ""; }
                _expandedDirs.Add(parent);
                LoadExplorerTree();
            }
            txtJsonStatus.Text = r.Message;
        }

        // ==================== 拖放：从资源管理器放入 ====================

        private void Tree_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>把从 Windows 资源管理器拖来的文件/文件夹复制进海床（不动源文件，同名跳过不覆盖）。</summary>
        private void Tree_Drop(object sender, System.Windows.DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

                string dest = TargetDirForDrop(e);
                var r = SeabedFs.CopyInto(paths, dest);
                if (r.Ok) _expandedDirs.Add(dest);
                LoadExplorerTree();
                txtJsonStatus.Text = r.Message;
            }
            catch (Exception ex)
            {
                txtJsonStatus.Text = "拖入失败：" + ex.Message;
            }
            finally
            {
                e.Handled = true;
            }
        }

        /// <summary>拖放目标：命中的那一行（目录→自身、文件→所在目录）；拖到空白处→海床根。</summary>
        private string TargetDirForDrop(System.Windows.DragEventArgs e)
        {
            var item = (e.OriginalSource as System.Windows.FrameworkElement)?.DataContext as FlatNode;
            string? dir = item != null ? CtxDirOf(item) : null;
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : ExplorerRoot;
        }

        // ==================== 按名字筛选 ====================
        //
        // ★ 这一块**留在页面里**是有意的：它产出的是 FlatNode（页面的行模型），
        //   要真正搬出去得先把 FlatNode 与扫描逻辑一起抽成独立的树模型类 —— 那是下一步的事，
        //   现在硬搬只会把"页面 → 行模型"的耦合换个地方藏起来。

        private void Tree_Filter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string q = (sender as System.Windows.Controls.TextBox)?.Text ?? "";
            if (q == _treeFilter) return;
            _treeFilter = q;
            LoadExplorerTree();
        }

    }
}
