// ==================== 海床 = 内置文件资源管理器（文件夹=真相） ====================
// 树 = seabed 目录的真实扫描结果；点文件读真文件、保存写回、删除按选中项（文件/目录）走回收站；
// 编译/AI 提示词/应用/变体等海床能力全部作用在「目录 + manifest」上，能力不减。
// 外部改动（磁盘内容变化）保存前提示覆盖；IO 失败（占用/只读/杀软锁）重试并给出错误，不静默吞。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Services.Configuration;   // SettingsHostExtensions.Host()（宿主面取用）
using ShoreHue.UI.Seabed;                     // FlatNode / SeabedTreeScanner（树模型与扫描器）
using static ShoreHue.UI.Seabed.SeabedTreeScanner;   // 让 ReadManifestField / FsKindOf 的调用点零改动

namespace ShoreHue.UI.Settings.Pages
{
    public partial class SeabedPage
    {
        // ===== 资源管理器状态 =====
        private readonly System.Collections.Generic.HashSet<string> _expandedDirs = new(StringComparer.OrdinalIgnoreCase);
        private string? _fsPath;                 // 当前选中（文件或目录，绝对路径）
        private bool _fsIsDir;
        private string? _fsXamlPath;             // 完全编程：.xaml 文件路径（可空）
        private string? _fsXamlCsPath;           // 完全编程：.xaml.cs 文件路径（可空）
        private readonly Dictionary<string, string> _fsSnapshot = new(StringComparer.OrdinalIgnoreCase);   // 打开时磁盘内容快照（外部改动检测）

        private static string ExplorerRoot => ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.RootDir;

        private bool _treeRefreshQueued;   // 防抖：watcher 连续事件合并为一次重建
        private bool _openingFile;         // 防重入：打开文件 → SetProgMode → 模式切换回调 → 又回来

        /// <summary>widget 插件仓库变化（用户增删文件/目录）→ 树刷新（合并、回 UI 线程）。</summary>
        private void OnWidgetStoreChanged()
        {
            if (_treeRefreshQueued) return;
            _treeRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // ★ 本回调可能来自 watcher 的**后台线程**：IsLoaded 是 UI 线程亲和成员，只能在 Dispatcher 里读
                _treeRefreshQueued = false;
                if (!IsLoaded) return;
                try { LoadExplorerTree(); }
                catch (Exception ex)
                {
                    // 用户往 seabed 里放/删了文件，树却没刷新（界面上看不到变化）
                    LogManager.Warning($"[海床] 文件夹变化后刷新树失败（界面不更新）：{ex.Message}");
                }
                // ★ 编辑器跟随磁盘：内容改动现在也会走到这里（watcher 已监听 LastWrite），
                //   打开着的文件必须跟着变 —— 否则"在 Windows 文件夹里改"要和"在海床里改"等效就不成立。
                SyncOpenEditorsWithDisk();
                ShowLoadIssues();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnSeabedPageUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.Changed -= OnWidgetStoreChanged;
        }

        /// <summary>海床树：加载（文件资源管理器视图，LoadTree 统一入口）。</summary>
        private void LoadTree() => LoadExplorerTree();

        /// <summary>树 = seabed 目录扫描（展开的目录显示子目录/文件），与资源管理器一致。</summary>
        private void LoadExplorerTree()
        {
            ResetArm();
            EnsureInitialExpansion();   // 首次默认全展开：旧树一次列全三级，别让用户以为"分类有问题"
            // ★ 扫描与筛选已抽到 SeabedTreeScanner（**无 UI 依赖、可单测**）：
            //   页面只负责"拿结果绑列表"。抽出去的直接收益是"哪个目录算面板/算配置组"能被断言钉住，
            //   而不是靠人盯着树看 —— 目录扁平化正需要这套测试台。
            _flatNodes = SeabedTreeScanner.Scan(ExplorerRoot, _expandedDirs, _treeFilter);
            lstConfigTree.ItemsSource = _flatNodes;
        }

        /// <summary>目录切换展开/收起；文件则读入编辑器。</summary>
        private void OnExplorerRow(FlatNode fn)
        {
            _fsPath = fn.FsPath;
            _fsIsDir = fn.FsIsDir;
            if (fn.FsIsDir)
            {
                // 目录：切换展开 → 重建树（资源管理器语义：单击展开/收起）
                if (!_expandedDirs.Remove(fn.FsPath ?? "")) _expandedDirs.Add(fn.FsPath ?? "");
                LoadExplorerTree();
                lstConfigTree.SelectedItem = null;
                // ★ 编辑区目录状态：提示该目录能力（保留已打开文件不打断编辑）
                txtNodeTitle.Text = Path.GetFileName(fn.FsPath);
                // ★ 目录可能在"树扫描"与"用户点击"之间被删掉（watcher 刷新是防抖的）——
                //   以前这里裸调 Directory.GetFiles，目录不存在会抛 DirectoryNotFoundException
                //   一路冒到全局未处理异常弹窗。
                bool hasCode = false;
                try
                {
                    hasCode = Directory.Exists(fn.FsPath) &&
                              Directory.GetFiles(fn.FsPath).Any(f =>
                                  f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                                  f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    LogManager.Debug($"[海床] 读取目录内容失败（按空目录提示）：{ex.Message}");
                }
                txtNodeHint.Text = fn.FsIsConfigDir
                    ? "配置目录：点开 config.json 编辑，或用「应用」写回设置"
                    : (hasCode
                        ? "功能目录：点文件编辑；右键可新建文件夹/重命名/删除"
                        : "（空）右键新建文件夹，或放入代码文件即成为功能");
                txtJsonStatus.Text = "";
                return;
            }
            // 文件：读入编辑器
            OpenExplorerFile(fn.FsPath!);
            lstConfigTree.SelectedItem = null;
        }

        // ==================== VS Code 式文件操作（右键菜单） ====================

        private static FlatNode? CtxNode(object sender)
        {
            return (sender as System.Windows.FrameworkElement)?.DataContext as FlatNode;
        }

        /// <summary>右键所在目录：目录行=自身；文件行=所在目录。</summary>
        private static string? CtxDirOf(FlatNode fn) => fn.FsIsDir ? fn.FsPath : Path.GetDirectoryName(fn.FsPath);

        /// <summary>右键 → 新建文件夹（实现见 SeabedPage.Manage.cs，与 Ctrl+Shift+N 共用）。</summary>
        private void Fs_NewFolder_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var fn = CtxNode(sender);
            NewFolderIn(fn != null ? CtxDirOf(fn) : null);
        }

        /// <summary>右键 → 重命名（文件/文件夹）。</summary>
        private void Fs_Rename_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var fn = CtxNode(sender);
            if (fn != null) RenameFsNode(fn);
        }

        /// <summary>右键 → 删除（文件/目录，走回收站，与行末 ✕ 一致）。</summary>
        private void Fs_Delete_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var fn = CtxNode(sender);
            if (fn?.FsPath != null) TryArmOrDelete(fn, null);   // 两击确认（无弹窗）
        }

        /// <summary>右键 → 在资源管理器中打开（目录=自身；文件=所在目录）。</summary>
        private void Fs_OpenDir_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var fn = CtxNode(sender);
            string? target = fn != null ? CtxDirOf(fn) : null;
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target)) { txtJsonStatus.Text = "目标目录不存在"; return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { txtJsonStatus.Text = "打开失败：" + ex.Message; }
        }

        /// <summary>把"某个小组件/面板没加载成功"的原因显示到状态行。
        /// ★ 这些以前只写日志：编译失败或被沙箱拦的组件在界面上直接消失，用户不知道发生了什么。</summary>
        private void ShowLoadIssues()
        {
            try
            {
                string issues = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.DescribeLoadIssues();
                if (issues.Length > 0) txtJsonStatus.Text = issues;
            }
            catch (Exception ex)
            {
                // 显示失败不影响别的功能，但不能静默
                LogManager.Warning($"[海床] 显示加载问题失败：{ex.Message}");
            }
        }

        /// <summary>打开着的编辑器内容跟随磁盘（VS Code 语义）：
        ///   · 没有未保存改动 → 直接重载成磁盘版本（这就是"在 Windows 文件夹里改 = 在海床里改"）；
        ///   · 有未保存改动、磁盘也变了 → 只在状态栏提示冲突，不弹模态框（每次文件事件都弹框会打断人），
        ///     真正覆盖前仍由「保存文件」再确认一次。
        /// 由 WidgetPluginStore.Changed 驱动（watcher 现在监听内容写入）。</summary>
        private void SyncOpenEditorsWithDisk()
        {
            if (_fsPath == null || _fsIsDir) return;
            try
            {
                var files = new List<string>();
                if (_fsXamlPath != null) files.Add(_fsXamlPath);
                if (_fsXamlCsPath != null) files.Add(_fsXamlCsPath);
                if (files.Count == 0) files.Add(_fsPath);

                bool reloaded = false, conflict = false;
                foreach (var path in files)
                {
                    if (!_fsSnapshot.TryGetValue(path, out var snap)) continue;
                    if (!File.Exists(path)) { conflict = true; continue; }   // 文件被外部删掉
                    string disk = File.ReadAllText(path);
                    if (disk == snap) continue;                              // 磁盘没变

                    string editor = EditorTextOf(path) ?? "";
                    if (editor == snap)
                    {
                        SetEditorText(path, disk);                           // 无未保存改动 → 跟随磁盘
                        _fsSnapshot[path] = disk;
                        reloaded = true;
                    }
                    else
                    {
                        // ★ 刻意**不**把快照推进到磁盘版本：快照的语义是"编辑器里这份内容是基于哪一版磁盘的"。
                        //   以前这里推进了快照，导致保存时的冲突判定 `disk != snap` 恒为假 ——
                        //   下面那句"已在磁盘上被修改，是否仍要覆盖？"的确认框**永远弹不出来**，
                        //   外部改动被静默覆盖。保持快照不变，冲突状态就会一直可见、保存前必然确认一次。
                        conflict = true;
                    }
                }
                if (reloaded)
                    txtJsonStatus.Text = "已从磁盘重新加载：" + string.Join("、", files.Select(Path.GetFileName));
                else if (conflict)
                    txtJsonStatus.Text = "⚠ 磁盘上的内容已变化（你还有未保存的改动），保存会覆盖磁盘版本";
            }
            catch (Exception ex)
            {
                // 同步失败不能静默：编辑器里显示的可能是旧内容
                LogManager.Warning($"[海床] 外部改动同步失败（编辑器可能仍是旧内容）：{ex.Message}");
            }
        }

        /// <summary>路径 → 对应编辑器当前文本（不在编辑器里的路径返回 null）。</summary>
        private string? EditorTextOf(string path)
        {
            if (path.Equals(_fsXamlPath, StringComparison.OrdinalIgnoreCase)) return txtXamlEditor.Text;
            if (path.Equals(_fsXamlCsPath, StringComparison.OrdinalIgnoreCase)) return txtXamlCsEditor.Text;
            if (path.Equals(_fsPath, StringComparison.OrdinalIgnoreCase)) return txtJsonEditor.Text;
            return null;
        }

        private void SetEditorText(string path, string text)
        {
            if (path.Equals(_fsXamlPath, StringComparison.OrdinalIgnoreCase)) { txtXamlEditor.Text = text; return; }
            if (path.Equals(_fsXamlCsPath, StringComparison.OrdinalIgnoreCase)) { txtXamlCsEditor.Text = text; return; }
            if (path.Equals(_fsPath, StringComparison.OrdinalIgnoreCase)) txtJsonEditor.Text = text;
        }

        /// <summary>
        /// 完全编程有一条"看代码猜不到"的硬规则：文件名的名字部分必须**与所在目录名一致**
        /// （加载器按 &lt;目录名&gt;.xaml 找界面）。用户把文件改名、或从别处拖进来时最容易踩，
        /// 所以在编辑区提示里直接点出来，而不是让他去翻文档。
        /// </summary>
        private static string NamingHint(string path)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                string dirName = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);
                string stem = ShoreHue.UI.Seabed.XamlFilePair.Stem(path);
                if (string.IsNullOrEmpty(dirName)
                    || string.Equals(stem, dirName, StringComparison.OrdinalIgnoreCase)) return "";
                return "　⚠ 文件名与目录名不一致（加载器要求 " + dirName + ".xaml），当前不会作为该功能的界面生效";
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[海床] 命名一致性提示失败：" + ex.Message);
                return "";
            }
        }
        /// <summary>按扩展名把文件读入对应编辑器（完全编程双框 / 简单单框 / config.json JSON）。</summary>
        private void OpenExplorerFile(string path)
        {
            // ★ 防重入：本方法内部会 SetProgMode → 触发 CmbProgMode_SelectionChanged → 又回到本方法。
            //   守卫放在这里而不是靠调用方小心，免得以后多几条路径就漏。
            if (_openingFile) return;
            _openingFile = true;
            try
            {
                string name = Path.GetFileName(path);
                _fsPath = path;
                _fsIsDir = false;
                txtNodeTitle.Text = name;
                _fsXamlPath = null;
                _fsXamlCsPath = null;
                _fsSnapshot.Clear();

                // ★ 完全编程：`.xaml` 与 `.xaml.cs` 是**成对的同一件事** —— 点哪一半都该把两个框都填上。
                //   判据与"由任一半推出另一半"抽在 XamlFilePair（纯函数）：以前这里是两支几乎相同的代码，
                //   而 `.xaml.cs` 那支用 GetFileNameWithoutExtension 拼出 `calculator.xaml.xaml`，
                //   导致点 .xaml.cs 时 XAML 框永远是空的（真机反馈）。
                if (ShoreHue.UI.Seabed.XamlFilePair.IsView(name) || ShoreHue.UI.Seabed.XamlFilePair.IsCodeBehind(name))
                {
                    SetProgMode(1);
                    var (xamlPath, csPath) = ShoreHue.UI.Seabed.XamlFilePair.Resolve(path);
                    string xaml = xamlPath != null && File.Exists(xamlPath) ? File.ReadAllText(xamlPath) : "";
                    string cs = csPath != null && File.Exists(csPath) ? File.ReadAllText(csPath) : "";
                    txtXamlEditor.Text = xaml;
                    txtXamlCsEditor.Text = cs;
                    _fsXamlPath = xamlPath != null && File.Exists(xamlPath) ? xamlPath : null;
                    _fsXamlCsPath = csPath != null && File.Exists(csPath) ? csPath : null;
                    if (_fsXamlPath != null) _fsSnapshot[_fsXamlPath] = xaml;
                    if (_fsXamlCsPath != null) _fsSnapshot[_fsXamlCsPath] = cs;
                    // 两个框都填好之后，**把显示切到用户点的那一半** —— 点 .xaml.cs 却停在 .xaml 页
                    // 会让人以为"点错了/没加载"（两个框其实都有内容）。
                    if (xamlTab != null)
                        xamlTab.SelectedIndex = ShoreHue.UI.Seabed.XamlFilePair.IsCodeBehind(name) ? 1 : 0;
                    txtNodeHint.Text = "完全编程（XAML + 代码后置），保存 = 写回目录中的真实文件" + NamingHint(path);
                    // ★ 显式刷一次预览：万一编辑器里那份内容与上次**完全相同**，TextChanged 不会触发，
                    //   预览会停在上一状态（例如被别的路径清空过就一直是空的）—— 真机反馈过这一条。
                    UpdateXamlPreview();
                }
                else
                {
                    // 简单模式：.cs / main.cs / config.json / manifest.json（manifest 只读展示）
                    SetProgMode(0);
                    string content = File.ReadAllText(path);
                    txtJsonEditor.Text = content;
                    _fsSnapshot[path] = content;
                    // ★ 说清"为什么这个面板连 .xaml 都没有"：两种编程形态按**文件存在与否**判定，
                    //   简单编程（main.cs）的界面完全由 C# 构建 → 没有 .xaml、也就没有 XAML 预览。
                    //   以前只写"简单编程"，用户看到别处有预览、这里没有，会以为坏了。
                    //   ★ 判断依据必须是"**目录名**.xaml"（成对规则按目录名，不是按这个 .cs 的文件名），
                    //     否则 `calculator/main.cs` 会被当成有配对（其实要找的是 calculator.xaml）。
                    string? dirPath = Path.GetDirectoryName(path);
                    string dirName = string.IsNullOrEmpty(dirPath) ? "" : Path.GetFileName(dirPath);
                    bool dirIsXamlForm = !string.IsNullOrEmpty(dirName)
                        && File.Exists(Path.Combine(dirPath!, dirName + ShoreHue.UI.Seabed.XamlFilePair.ViewExt));
                    txtNodeHint.Text = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? (name == "manifest.json" ? "元信息（只读展示）" : "配置 JSON，保存 = 写回该文件")
                        : dirIsXamlForm
                            ? "C# 源码（本目录是完全编程形态，界面在同目录 " + dirName + ".xaml）"
                            : "简单编程：界面由本文件里的 C# 代码直接构建，没有 .xaml，所以没有 XAML 预览（想换成完全编程：在本目录新建 " + dirName + ".xaml）";
                }
                txtJsonStatus.Text = "";
                txtJsonEditor.Visibility = System.Windows.Visibility.Visible;
                xamlEditorPanel.Visibility = System.Windows.Visibility.Collapsed;
                UpdateEditorVisibility();   // 按模式恢复显示
            }
            catch (Exception ex)
            {
                txtJsonStatus.Text = "打开失败：" + ex.Message;
            }
            finally
            {
                _openingFile = false;
            }
        }

        /// <summary>切换编程模式（0=简单，1=完全），不触发任何保存（海床控件已排除自动保存）。</summary>
        private void SetProgMode(int idx)
        {
            if (cmbProgMode != null && cmbProgMode.SelectedIndex != idx) cmbProgMode.SelectedIndex = idx;
        }

        // ==================== 保存：写回真实文件 ====================

        /// <summary>「保存文件」：把当前编辑器内容写回选中文件（或 XAML 双文件）；外部改动先提示；IO 失败重试并报错。</summary>
        private void BtnSaveFile_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_fsPath == null || _fsIsDir) { txtJsonStatus.Text = "请先在左侧选中一个文件"; return; }
            try
            {
                var targets = new List<(string Path, string Content)>();
                if (_fsXamlPath != null || _fsXamlCsPath != null)
                {
                    if (_fsXamlPath != null) targets.Add((_fsXamlPath, txtXamlEditor.Text ?? ""));
                    if (_fsXamlCsPath != null) targets.Add((_fsXamlCsPath, txtXamlCsEditor.Text ?? ""));
                }
                else
                {
                    targets.Add((_fsPath, txtJsonEditor.Text ?? ""));
                }

                // ★ 情况一（VS Code：干净缓冲区跟随磁盘）：我**没编辑过**，但磁盘被外部改了 → 不写盘，重新载入。
                //   为什么保存时还要判：watcher 事件可能漏（同秒覆盖、外部程序、刚启动未订阅），
                //   漏掉时下面的"要不要覆盖"会因为"我没改过"而放行，把旧内容写回去、外部改动静默消失。
                var reloaded = new List<string>();
                foreach (var t in targets)
                {
                    if (!_fsSnapshot.TryGetValue(t.Path, out var s)) continue;
                    string diskNow = File.ReadAllText(t.Path);
                    if (!SeabedFs.ShouldReloadFromDisk(s, t.Content, diskNow)) continue;
                    SetEditorText(t.Path, diskNow);
                    _fsSnapshot[t.Path] = diskNow;
                    reloaded.Add(Path.GetFileName(t.Path));
                }
                if (reloaded.Count > 0)
                {
                    txtJsonStatus.Text = "磁盘上已有新版本，已重新载入（未写入）：" + string.Join("、", reloaded);
                    return;
                }

                // ★ 情况二（VS Code：dirty write prevention）：磁盘内容既不是我打开时的快照、也不是我要写的内容
                //   → 说明外部改过且我有自己的改动，覆盖会丢东西，先问一句。判据抽在 SeabedFs（纯函数、可单测）。
                foreach (var t in targets)
                {
                    if (_fsSnapshot.TryGetValue(t.Path, out var snap)
                        && SeabedFs.ShouldWarnBeforeOverwrite(snap, File.ReadAllText(t.Path), t.Content))
                    {
                        var r = System.Windows.MessageBox.Show(
                            Path.GetFileName(t.Path) + " 在磁盘上已被外部修改（磁盘版本较新）。\n\n"
                            + "是 = 用编辑器里的内容覆盖磁盘\n"
                            + "否 = 取消保存，保留磁盘上的新版本",
                            "海床", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                        if (r != System.Windows.MessageBoxResult.Yes) { txtJsonStatus.Text = "已取消保存（保留磁盘上的新版本）"; return; }
                    }
                }

                foreach (var t in targets)
                {
                    WriteFileWithRetry(t.Path, t.Content);
                    _fsSnapshot[t.Path] = t.Content;
                }
                // ★ 保存即生效：写盘时 watcher 是挂起的（防自触发），所以这里主动刷新插件仓库并通知。
                //   否则"在海床里改了内置件/插件源码"要等下一次无关事件或重启才生效（与"文件夹即真相源"不符）。
                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.Reload();
                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.NotifyChanged();
                txtJsonStatus.Text = "已保存：" + string.Join("、", targets.Select(t => Path.GetFileName(t.Path)));
            }
            catch (Exception ex)
            {
                txtJsonStatus.Text = "保存失败：" + ex.Message;
                System.Windows.MessageBox.Show("保存失败：\n" + ex.Message + "\n\n若文件被占用（如已在其他程序打开）或为只读，请关闭占用后重试。",
                    "海床", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>写文件（应用自身写盘走 watcher 挂起防事件链；IO 占用重试 3 次）。</summary>
        private void WriteFileWithRetry(string path, string content)
        {
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.WithWatcherSuspended(() =>
            {
                Exception? last = null;
                for (int i = 0; i < 3; i++)
                {
                    try { File.WriteAllText(path, content, new System.Text.UTF8Encoding(false)); return; }
                    catch (IOException ex) { last = ex; System.Threading.Thread.Sleep(300); }
                    catch (UnauthorizedAccessException ex) { last = ex; System.Threading.Thread.Sleep(300); }
                }
                if (last != null) throw last;
            });
        }

        // ==================== 删除：两击确认（与全项目「再点一次删除」一致，无弹窗），走回收站 ====================

        /// <summary>两击删除入口：第一击进入 3 秒确认态（行末 ✕ 变「再点一次删除」，右键删除以状态提示）；
        /// 3 秒内再次点击同一项 → 真正删除（回收站，可恢复）。内置项在状态里提示。</summary>
        private void TryArmOrDelete(FlatNode fn, System.Windows.Controls.Button? btn)
        {
            if (fn.FsPath == null) return;
            if (string.Equals(fn.FsPath, ExplorerRoot, StringComparison.OrdinalIgnoreCase))
            {
                txtJsonStatus.Text = "不能删除海床根目录";
                return;
            }
            if (IsArmed(fn.FsPath))
            {
                ExplorerDeleteCore(fn);   // 第二击：执行
                return;
            }
            if (btn != null) ArmDelete(btn, fn.FsPath);   // 行末 ✕：按钮进入「再点一次删除」
            else ArmPathNoButton(fn.FsPath);              // 右键删除：状态栏进入确认态
            bool system = fn.FsIsSystem || IsUnderSystemDir(fn.FsPath, ExplorerRoot);
            txtJsonStatus.Text = "已选择删除「" + Path.GetFileName(fn.FsPath) + "」，3 秒内再点一次确认（进入回收站）"
                + (system ? "；注意：这是 ShoreHue 内置项" : "");
        }

        /// <summary>无行末按钮的确认态（右键删除）：记录 + 3 秒自动还原。</summary>
        private void ArmPathNoButton(string id)
        {
            ResetArm();
            _armDeleteId = id;
            _armDeleteAt = DateTime.Now;
            var t = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
                IsEnabled = true
            };
            t.Tick += (_, _) => ResetArm();
            _armDeleteTimer = t;
        }

        /// <summary>执行删除：文件操作交给 <see cref="SeabedFs"/>（含"不能删根目录"的硬约束），这里只管界面善后。</summary>
        private void ExplorerDeleteCore(FlatNode fn)
        {
            if (fn.FsPath == null) return;

            var result = SeabedFs.DeleteToRecycleBin(fn.FsPath, fn.FsIsDir, ExplorerRoot);
            if (!result.Ok) { txtJsonStatus.Text = result.Message; return; }

            // 同步派生索引：若该目录 manifest.id 是海床自定义项（custom_*），从 CustomPanels 移除
            string? mid = fn.FsIsDir ? ReadManifestField(fn.FsPath, "id") : null;
            if (!string.IsNullOrEmpty(mid) && mid.StartsWith("custom_", StringComparison.Ordinal))
            {
                var list = _settings.CustomPanels;
                list.RemoveAll(p => p.Id == mid);
                _settings.Host().SetCustomPanels(list);
            }
            _expandedDirs.Remove(fn.FsPath);
            if (_fsPath == fn.FsPath) { _fsPath = null; txtJsonEditor.Text = ""; txtXamlEditor.Text = ""; txtXamlCsEditor.Text = ""; txtNodeTitle.Text = ""; }
            LoadExplorerTree();
            txtJsonStatus.Text = result.Message;
        }

        // ==================== 编译（作用于当前编辑内容） ====================

        private void CompileFsFile()
        {
            try
            {
                if (_fsPath == null) { txtJsonStatus.Text = "请先选中文件"; return; }
                string name = Path.GetFileName(_fsPath);
                string err;
                if (_fsXamlPath != null || _fsXamlCsPath != null)
                {
                    // 完全编程：XAML + 代码后置 联合编译（文件夹内新 id，防缓存冲突）
                    var (w, cerr) = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.CompileXaml(
                        "fs_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        txtXamlEditor.Text ?? "", txtXamlCsEditor.Text ?? "");
                    err = w == null ? cerr : "";
                }
                else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try { _ = JsonDocument.Parse(txtJsonEditor.Text ?? ""); err = ""; }
                    catch (Exception jex) { err = "JSON 语法错误：" + jex.Message; }
                }
                else
                {
                    err = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.Validate(
                        "fs_" + Guid.NewGuid().ToString("N").Substring(0, 8), txtJsonEditor.Text ?? "");
                }
                txtJsonStatus.Text = err.Length == 0 ? "编译通过（未保存，点「保存文件」写回）" : "编译失败：" + err;
            }
            catch (Exception ex) { txtJsonStatus.Text = "编译异常：" + ex.Message; }
        }

        // ==================== 复制 AI 提示词（按目录 manifest kind） ====================

        private void CopyFsAiPrompt()
        {
            try
            {
                if (_fsPath == null) { txtJsonStatus.Text = "请先选中文件或目录"; return; }
                string dir = _fsIsDir ? _fsPath : Path.GetDirectoryName(_fsPath)!;
                string name = Path.GetFileName(_fsIsDir ? dir : _fsPath);
                string kind = ReadManifestField(dir, "kind") ?? FsKindOf(dir) ?? "";
                string currentSrc = string.Join("\n/* --- */\n",
                    Directory.GetFiles(dir)
                        .Where(f => !f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                        .Select(File.ReadAllText));
                var node = new ShoreHue.Core.Models.ConfigNode
                {
                    Key = "fs_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = name,
                    Category = Path.GetFileName(Path.GetDirectoryName(dir) ?? "") ?? "",
                    Kind = kind,
                    CustomId = "fs:" + name
                };
                var mode = cmbProgMode?.SelectedIndex == 1
                    ? ShoreHue.UI.Settings.Pages.PromptGenerator.ProgrammingMode.Xaml
                    : ShoreHue.UI.Settings.Pages.PromptGenerator.ProgrammingMode.Simple;
                string prompt = ShoreHue.UI.Settings.Pages.PromptGenerator.Generate(node, currentSrc, mode);
                System.Windows.Clipboard.SetText(prompt);
                txtJsonStatus.Text = "已复制 AI 提示词（" + (kind == "" ? name : kind) + "，产出文件放回 " + dir + " 即生效）";
            }
            catch (Exception ex) { txtJsonStatus.Text = "复制失败：" + ex.Message; }
        }

        // ==================== 应用：配置目录（config.json）→ 写回设置 + 冲突标记 ====================

        private void ApplyFsConfigDir(string dir)
        {
            try
            {
                string cfgPath = Path.Combine(dir, "config.json");
                if (!File.Exists(cfgPath)) { txtJsonStatus.Text = "该目录没有 config.json，无法应用"; return; }
                var data = ShoreHue.Core.Services.SettingsFileManager.Load();
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                var overrides = data.AppliedPresets ?? new Dictionary<string, string>();
                string presetName = Path.GetFileName(dir);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    SetPropertyFromJson(data, prop.Name, prop.Value);
                    foreach (var n in ShoreHue.UI.Seabed.ConfigTreeBuilder.FindNodeChain(prop.Name))
                        overrides[n.Key] = presetName;
                }
                data.AppliedPresets = overrides;
                _settings.Host().Apply(data);
                LoadTree();
                txtJsonStatus.Text = "已应用配置目录「" + presetName + "」（冲突项已置灰，可在设置页两击解除）";
            }
            catch (Exception ex) { txtJsonStatus.Text = "应用失败：" + ex.Message; }
        }

        private static void SetPropertyFromJson(object data, string name, JsonElement v)
        {
            var p = data.GetType().GetProperty(name);
            if (p == null || !p.CanWrite) return;
            try
            {
                object? val;
                if (v.ValueKind == JsonValueKind.String) val = v.GetString();
                else if (v.ValueKind == JsonValueKind.Number)
                {
                    var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                    val = t == typeof(double) ? v.GetDouble()
                        : t == typeof(float) ? v.GetSingle()
                        : t == typeof(int) ? v.GetInt32()
                        : t == typeof(long) ? v.GetInt64()
                        : v.GetRawText();
                }
                else if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) val = v.GetBoolean();
                else val = null;
                p.SetValue(data, val);
            }
            catch (Exception ex)
            {
                // 应用预设时某个字段没设上 → 用户以为生效了其实没有
                LogManager.Warning($"[海床] 写入配置字段失败（该字段未生效）{name}：{ex.Message}");
            }
        }

        // ==================== 变体：当前目录另存为副本 ====================

        private void CopyFsAsVariant(FlatNode fn)
        {
            if (fn?.FsPath == null) return;
            CopyFsAsVariantPath(fn.FsPath);
        }

        /// <summary>变体：把 path（目录或目录内文件）所在功能目录整体复制为新目录（名称N）。</summary>
        private void CopyFsAsVariantPath(string path)
        {
            try
            {
                string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { txtJsonStatus.Text = "变体作用于功能目录（请选中目录或目录内文件）"; return; }
                if (string.Equals(dir, ExplorerRoot, StringComparison.OrdinalIgnoreCase)) { txtJsonStatus.Text = "不能把海床根目录另存为变体"; return; }
                string parent = Path.GetDirectoryName(dir)!;
                string name = Path.GetFileName(dir);
                string newName = NextSiblingName(name,
                    Directory.GetDirectories(parent).Select(p => Path.GetFileName(p)));
                string newDir = Path.Combine(parent, newName);
                var dlg = new InputDialog("海床 · 另存为变体", "复制目录为变体，输入新目录名：", newName);
                dlg.Owner = System.Windows.Window.GetWindow(this);
                if (dlg.ShowDialog() != true) { txtJsonStatus.Text = "已取消"; return; }
                string input = dlg.ResultText.Trim();
                if (string.IsNullOrEmpty(input)) { txtJsonStatus.Text = "名称不能为空"; return; }
                newDir = Path.Combine(parent, SanitizeFsName(input));
                if (Directory.Exists(newDir)) { txtJsonStatus.Text = "同名目录已存在"; return; }
                Directory.CreateDirectory(newDir);
                foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(newDir, Path.GetFileName(f)), true);
                // 更新副本 manifest：新 id/name
                string nmf = Path.Combine(newDir, "manifest.json");
                if (File.Exists(nmf))
                {
                    string json = File.ReadAllText(nmf);
                    using var doc = JsonDocument.Parse(json);
                    var mf = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                    mf["id"] = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    mf["name"] = input;
                    mf["system"] = false;
                    File.WriteAllText(nmf, JsonSerializer.Serialize(mf, new JsonSerializerOptions { WriteIndented = true }));
                }
                LoadExplorerTree();
                txtJsonStatus.Text = "已创建变体目录：" + Path.GetFileName(newDir) + "（watcher 将自动识别）";
            }
            catch (Exception ex) { txtJsonStatus.Text = "创建变体失败：" + ex.Message; }
        }

        private static string SanitizeFsName(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            }
            return sb.Length >= 2 ? sb.ToString() : "variant";
        }
    }
}
