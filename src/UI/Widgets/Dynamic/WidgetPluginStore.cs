using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShoreHue.Animation;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Status;

namespace ShoreHue.UI.Widgets.Dynamic
{
    /// <summary>已安装的 C# 插件小组件（manifest + 源码）。</summary>
    public class WidgetPlugin
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Permissions { get; set; } = new();
        /// <summary>分组名（对应 widgets/ 下的子文件夹，如 小组件/面板功能）。</summary>
        public string Group { get; set; } = "小组件";
        [JsonIgnore]
        public string Source { get; set; } = "";
        /// <summary>XAML 形态（可选）：<id>.xaml 界面源码；与 XamlCs 配套，存在时编译走 CompileXaml。</summary>
        [JsonIgnore]
        public string Xaml { get; set; } = "";
        /// <summary>XAML 代码后置（可选）：<id>.xaml.cs（partial class + 事件处理器）。</summary>
        [JsonIgnore]
        public string XamlCs { get; set; } = "";
        /// <summary>是否信任来源（跳过沙箱）。默认 true = 本地代码（本地编程不检测，见 HANDOFF）；
        /// 市场安装/系统内置副本按 manifest 标记。false = 每次加载前过沙箱。</summary>
        [JsonIgnore]
        public bool TrustedSource { get; set; } = true;
        /// <summary>类型（Widget/Panel/Config/Category；无 manifest 的旧文件夹项为空串）。
        /// WidgetSwitcher 只把 Widget 类当作小组件标签，Panel 类走区域面板下拉。</summary>
        [JsonIgnore]
        public string Kind { get; set; } = "";

        /// <summary>是否内置件（manifest 带 system:true）。
        /// ★ 内置件迁移中：默认只作文件夹展示（跳过加载），但列在 `MigratedBuiltinIds` 里的
        ///   会真正**从文件夹加载**并在界面上标注——它们才是"文件夹即真相源"里那份在跑的代码。</summary>
        [JsonIgnore]
        public bool IsBuiltin { get; set; }
    }

    /// <summary>
    /// 本地插件仓库：每个小组件一个目录
    /// %LOCALAPPDATA%\ShoreHue\widgets\<id>\（main.cs 源码 + manifest.json 元信息）。
    /// </summary>
    public static class WidgetPluginStore
    {
        /// <summary>校验小组件 id（仅英文/数字/下划线/连字符）。</summary>
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2 || id.Length > 32) return false;
            foreach (char c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>
        /// 「已迁移到从 seabed 文件夹加载」的内置件 id（过渡期清单：迁一个加一个）。
        ///
        /// 背景：内置小组件历史上是**编译进 exe、由宿主直接 new** 的，文件夹里那份带 system:true 只作展示 ——
        /// 于是"用户在文件夹里改内置小组件"根本不生效，与"文件夹即真相源"相反。
        /// 迁移做法：把 id 加进这里 → 加载器不再跳过它（真正从文件夹编译）→ WidgetSwitcher 优先用它；
        ///   文件坏了/没了则**回退 exe 内置实现**（不丢功能），并在日志里说明。
        /// 六个都迁完并稳定后，删掉 WidgetSwitcher 里的内置实例字段与兜底分支。
        /// </summary>
        public static readonly HashSet<string> MigratedBuiltinIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "timer",       // 试点：无参构造，且宿主没有别处引用它的具体类型
            "calculator",  // 无参构造；宿主内部只在 WidgetSwitcher 的 _tabs 里引用
            "clipboard",   // 已加无参构造（服务经 HostCapabilities 取）；宿主无外部引用
            "note",        // 已加无参构造（服务经 HostCapabilities 取）；宿主无外部引用
            "web",         // 已加无参构造（设置经 HostCapabilities 取）；宿主无外部引用
            "textai",      // 已收敛：静态事件换成 HostCapabilities.OpenSettingsPage；热键走 ITextAiWidget 接口拿当前实例
        };

        /// <summary>
        /// 「已迁移到从 seabed 文件夹加载」的内置面板功能 id（过渡期清单：迁一个加一个）。
        ///
        /// 背景与小组件同：内置面板历史上编译进 exe、由 PanelContentController 直接 new 出来，
        /// 文件夹里那份带 system:true 只作展示 —— 于是"改文件夹里的 main.cs"不生效。
        /// 迁移做法：把 id 加进这里 → 加载器不再跳过它（真正从文件夹编译）→ PanelContentController
        /// 优先用它；文件坏了/编译失败则回退宿主视图（不丢功能），日志里说明。
        /// </summary>
        public static readonly HashSet<string> MigratedPanelIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "panel-notification",   // 通知坞：main.cs 为完整纯代码实现（分组 + 单条关闭）
            "panel-recent",         // 最近使用：main.cs 为完整纯代码实现（三标签）
            "panel-quicksettings",  // 快捷设置：main.cs 为完整纯代码实现（音量/亮度/蓝牙/WiFi/热点）
            "panel-windowcontrol",  // 窗口控制：main.cs 为薄封装（复用宿主 WindowControlView）
            "panel-taskbar-feature",// 任务栏：薄封装（宿主按 TaskbarView 类型取内容 → 已改为向下查找）
            "panel-ai",             // AI 助手：薄封装（设置刷新改到 OnActivated）
            "panel-apphelper",      // 应用辅助：薄封装（宿主按 AppHelperView 类型取内容 → 已改为向下查找）
        };

        /// <summary>ShoreHue 内置小组件 id（官方随附，删除可能导致运行异常）。</summary>
        private static readonly HashSet<string> _builtinIds = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "timer", "calculator", "clipboard", "note", "textai", "web"
        };

        // ==================== 信任（id + 内容哈希）====================

        /// <summary>宿主写的来源标记文件名（市场拾贝 / 宿主解包 .shpkg 时写入；包内容无法伪造"我是本地"）。</summary>
        public const string OriginMarkerFile = ".origin";

        /// <summary>该目录是否带宿主来源标记（=宿主明确知道这是"外来包"）。</summary>
        public static bool HasOriginMarker(string dir) => File.Exists(Path.Combine(dir, OriginMarkerFile));

        /// <summary>写入来源标记（宿主在安装/解包后调用）。返回是否写入成功——调用方必须处理失败：
        /// 没有标记的目录会被信任判定当成"本地代码"直接放行，等于给外来包发了免检牌。</summary>
        public static bool WriteOriginMarker(string dir, string origin = "package")
        {
            string marker = Path.Combine(dir, OriginMarkerFile);
            try { File.WriteAllText(marker, origin); return true; }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 无法写入来源标记 {marker}：解包结果按外来代码回滚", ex);
                return false;
            }
        }

        // ★ 信任的读写走 IPluginTrustStore（internal，宿主专用）——它对插件不可见，
        //   所以"给插件登记信任"这件事只有宿主能做。见 docs/SECURITY.md。
        /// <summary>用户是否对该 id 显式选择"改用沙箱"。</summary>
        public static bool IsExplicitlySandboxed(string id)
        {
            try
            {
                var s = ServiceManager.Instance.GetService<SettingsManager>() as IPluginTrustStore;
                return s != null && s.IsPluginTrusted(id, DenySentinel);
            }
            catch (Exception ex)
            {
                // 读不到信任表 → 按"未显式改用沙箱"处理（fail-safe 方向：宿主来源标记仍会拦住外来包）
                LogManager.Warning($"[插件] 读取插件信任表失败（{id}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>"显式改用沙箱"的哨兵值（放在同一张信任表里，便于随设置落盘）。</summary>
        public const string DenySentinel = "!sandboxed";

        /// <summary>
        /// 纯函数信任判定（可单测）：
        ///   ① 用户显式"改用沙箱" → 不可信；
        ///   ② 用户显式"信任"且哈希匹配 → 可信；
        ///   ③ 宿主来源标记 / 宿主写的 trustedSource:false → 不可信（外来包）；
        ///   ④ 其余 → 可信（海床初衷：文件夹即真相源，本地代码不设限）。
        /// </summary>
        public static bool ComputeTrust(bool hostOriginMarker, bool? manifestTrusted,
                                        bool explicitlySandboxed, bool trustedByHash)
        {
            if (explicitlySandboxed) return false;
            if (trustedByHash) return true;
            if (hostOriginMarker) return false;
            if (manifestTrusted == false) return false;
            return true;
        }

        /// <summary>插件内容哈希（源码 + XAML + 代码后置；任一变化即视为不同内容）。</summary>
        public static string ContentHash(string? source, string? xaml, string? xamlCs)
            => WidgetCompiler.SourceHash((source ?? "") + "\u0001" + (xaml ?? "") + "\u0002" + (xamlCs ?? ""));

        /// <summary>该 id + 内容是否已被用户显式信任。</summary>
        public static bool IsTrustedById(string id, string contentHash)
        {
            try
            {
                var s = ServiceManager.Instance.GetService<SettingsManager>() as IPluginTrustStore;
                return s != null && s.IsPluginTrusted(id, contentHash);
            }
            catch (Exception ex)
            {
                // 查不到信任记录 → 按"未受信"处理（内容哈希信任失效的保守方向：仍受来源标记约束）
                LogManager.Warning($"[插件] 查询插件信任记录失败（{id}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>界面「信任」开关：把当前内容记为受信（写盘后 Reload 生效）。</summary>
        public static bool SetTrusted(string id, bool trusted)
        {
            try
            {
                var plg = GetById(id);
                if (plg == null) return false;
                var s = ServiceManager.Instance.GetService<SettingsManager>() as IPluginTrustStore;
                if (s == null) return false;
                if (trusted) s.SetPluginTrusted(id, ContentHash(plg.Source, plg.Xaml, plg.XamlCs));
                else { s.RevokePluginTrust(id); s.SetPluginTrusted(id, DenySentinel); }   // 显式"改用沙箱"
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                // 用户点了"信任/改用沙箱"却没生效 → 调用方据此弹提示，日志留原因
                LogManager.Error($"[插件] 修改 [{id}] 信任状态失败：{ex.Message}", ex);
                return false;
            }
        }

        /// <summary>该插件当前是否受信（供界面显示）。TrustedSource 字段在加载时已按 ComputeTrust 解析为"有效信任"。</summary>
        public static bool IsTrusted(WidgetPlugin plugin) => plugin != null && plugin.TrustedSource;

        /// <summary>是否为 ShoreHue 内置文件（官方 author 或内置 id 清单）。</summary>
        public static bool IsBuiltin(WidgetPlugin plugin)
        {
            if (plugin == null) return false;
            if (_builtinIds.Contains(plugin.Id)) return true;
            return string.Equals(plugin.Author, "timecolors", StringComparison.OrdinalIgnoreCase);
        }

        // ★ 权限标签只有一处实现：WidgetPermissions.PermissionLabel（7 类）。
        //   这里曾另有一份 3 类版本 —— 两套真相会随新增权限静默漂移，已删除。

        /// <summary>列表变化（安装/删除）时触发，供 WidgetSwitcher 重建标签。</summary>
        public static event Action? Changed;

        /// <summary>宿主在"改动了 seabed 文件"之后主动通知一次（与 Save/Delete/watcher 同一条链路）。
        /// ★ 用于海床文件编辑器：它保存时把 watcher 挂起了（防自触发），若不主动通知，
        ///   改了内置件/插件文件要等下次重启或别的事件才生效。</summary>
        public static void NotifyChanged() => Changed?.Invoke();

        // ==================== 加载期问题（让"它为什么没出现"看得见）====================

        /// <summary>本次加载没能成功的原因（插件 id → 一句话）。
        /// ★ 这些**以前只写日志**：小组件在标签栏直接消失、区域面板悄悄退化成通知坞，
        ///   用户只看到"东西没了"，不知道是编译失败还是被沙箱拦了 —— 与"让用户知道风险/知道发生了什么"直接冲突。
        ///   界面（海床页状态行、设置·小组件列表）读这里把原因说出来。</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LoadIssues
            = new(StringComparer.OrdinalIgnoreCase);

        public static void SetLoadIssue(string? id, string? reason)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            try
            {
                string line = OneLine(reason);
                LoadIssues[id!] = line;
                // Debug 级留痕：界面上的"⚠ 原因"是从这里来的，排查"我看到的是哪一次失败"时需要
                LogManager.Debug($"[插件] 记录加载问题：{id} = {line}");
            }
            catch (Exception ex) { LogManager.Warning($"[插件] 记录加载问题失败（{id}）：{ex.Message}"); }
        }

        public static void ClearLoadIssue(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            LoadIssues.TryRemove(id!, out _);
        }

        public static string? GetLoadIssue(string? id)
            => string.IsNullOrWhiteSpace(id) ? null : (LoadIssues.TryGetValue(id!, out var r) ? r : null);

        /// <summary>一行摘要（没有问题时返回空串），供状态行直接显示。</summary>
        public static string DescribeLoadIssues()
        {
            try
            {
                if (LoadIssues.IsEmpty) return "";
                return "⚠ " + string.Join("；", LoadIssues
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => kv.Key + "：" + kv.Value));
            }
            catch (Exception ex)
            {
                // 汇总失败不能把界面搞崩（这里被 UI 直接调用），只留痕
                LogManager.Warning($"[插件] 汇总加载问题失败：{ex.Message}");
                return "";
            }
        }

        /// <summary>编译器错误是一大坨多行文本，状态行放不下 → 压成一行并截断。</summary>
        private static string OneLine(string? text)
        {
            string t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length <= 120 ? t : t[..120] + "…";
        }

        // ★ 文件夹变化监听：用户在系统文件夹增删文件时，自动刷新小组件列表（双向同步）
        private static FileSystemWatcher? _watcher;
        private static readonly object _watcherLock = new object();

        // ★ 防抖窗口：一次保存常产生多个事件（临时文件 + 改名覆盖 + 写入），窗口内的事件合并为一次 Reload。
        private const int WatchDebounceMs = 300;
        private static System.Threading.Timer? _watchDebounceTimer;
        private static readonly object _watchDebounceLock = new object();
        private static string? _lastTreeSignature;   // 最近一次已处理的海床目录状态签名（内容没变就不刷新）

        /// <summary>开始监听小组件文件夹（应用启动时调用一次；Watcher 生命周期随进程）。
        /// ★ 职责边界：**增删改名 + 内容写入都检测** —— 海床是文件资源管理器，在 Windows 文件夹里改内容
        ///   和在应用内改必须等效（记事本原地保存只产生 LastWrite 事件，实测不监听就完全没反应）。
        /// ★ 防死循环：宿主自己写盘一律走 WithWatcherSuspended（暂停监听），自触发链从源头切断；
        ///   再加"目录状态签名"短路——事件来了但内容没变就不 Reload、不通知（写盘被暂停期间的漏网事件、
        ///   属性变更等都不会引起界面抖动）。历史上曾因"应用写盘→事件→Reload→又写盘"实测 CPU 78% 卡死。
        /// ★ 事件只触发"扫描更新缓存"（ReloadCore 轻量，不编译），编译在使用时按需进行。</summary>
        public static void StartWatching()
        {
            lock (_watcherLock)
            {
                if (_watcher != null) return;
                try
                {
                    EnsureSkeleton();
                    _watcher = new FileSystemWatcher(RootDir)
                    {
                        IncludeSubdirectories = true,
                        // ★ 增删改名 + 内容写入都监听：缺 LastWrite 会导致"在文件夹里改文件内容"永远不生效
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    // 事件 → 防抖窗口（WatchDebounceMs，每来一个事件窗口往后推）→ 到点统一 Reload
                    FileSystemEventHandler handler = (_, _) => ScheduleWatchReload();
                    _watcher.Created += handler;
                    _watcher.Deleted += handler;
                    _watcher.Changed += handler;
                    _watcher.Renamed += (_, _) => ScheduleWatchReload();
                    _watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    // 监听不可用不致命（启动/手动 Reload 仍会扫描），但"往文件夹里丢文件"不再自动出现
                    _watcher?.Dispose();
                    _watcher = null;   // 置空以便下次调用重试（否则一次失败会永久卡在"看起来已启动"）
                    LogManager.Warning($"[插件] 无法监听海床文件夹（文件增删不再自动刷新）：{ex.Message}");
                }
            }
        }

        /// <summary>聚合 watcher 事件：每个事件把防抖窗口往后推，窗口静默 WatchDebounceMs 后统一 Reload。
        /// ★ 真防抖（旧实现是"首个事件起算的固定 120ms 窗口"：后续事件直接丢弃，注释却写"只重置定时器"，
        ///   注释与实现不一致）——一次保存常产生 3 个连续事件，固定窗口容易在写盘中途就 Reload 到半成品。</summary>
        private static void ScheduleWatchReload()
        {
            lock (_watchDebounceLock)
            {
                if (_watchDebounceTimer != null)
                {
                    // 已挂起的窗口 → 往后推
                    try { _watchDebounceTimer.Change(WatchDebounceMs, System.Threading.Timeout.Infinite); return; }
                    catch (ObjectDisposedException) { /* 上一轮回调已释放，下面重建 */ }
                }
                _watchDebounceTimer = new System.Threading.Timer(
                    _ => WatchReloadCallback(), null, WatchDebounceMs, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>防抖到点：内容确实变了才 Reload + 通知（内容没变就什么都不做）。</summary>
        private static void WatchReloadCallback()
        {
            lock (_watchDebounceLock)
            {
                _watchDebounceTimer?.Dispose();
                _watchDebounceTimer = null;
            }
            try
            {
                // ★ 内容短路：事件来了不代表内容变了（暂停监听的漏网写、属性/时间戳变更、宿主重写同名文件）。
                //   签名相同 → 不 Reload、不通知，避免无谓的界面重建与 Roslyn 编译。
                string sig = ComputeTreeSignature();
                if (sig == _lastTreeSignature)
                {
                    LogManager.Debug("[插件] 海床目录有文件事件但内容未变，跳过刷新");
                    return;
                }
                _lastTreeSignature = sig;

                // ★ 暂停 watcher：Reload 的目录扫描（含单文件归一化、包解包落盘）会再产生事件，防"事件→Reload→事件"
                if (_watcher != null) _watcher.EnableRaisingEvents = false;
                try
                {
                    Reload();
                    Changed?.Invoke();
                }
                finally
                {
                    if (_watcher != null) _watcher.EnableRaisingEvents = true;
                }
                // Reload 可能自己改写了目录（归一化/解包）→ 重算签名，免得把宿主自己的写盘当成用户改动
                _lastTreeSignature = ComputeTreeSignature();
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 文件夹变更后的刷新失败（列表可能不是最新）：{ex.Message}", ex);
            }
        }

        /// <summary>海床目录状态签名（相对路径 + 长度 + 最后写入时间）。算不出来时返回随机值 =
        /// 按"已变化"处理（宁可多刷新一次，不可漏刷新）。</summary>
        private static string ComputeTreeSignature()
        {
            try
            {
                if (!Directory.Exists(RootDir)) return "";
                var sb = new System.Text.StringBuilder();
                foreach (var f in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories)
                                           .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    // 逐文件取信息：文件可能正被写入/删除（事件到得快）→ 跳过这一条，不影响整体判断
                    try
                    {
                        var fi = new FileInfo(f);
                        sb.Append(fi.FullName).Append('|').Append(fi.Length).Append('|')
                          .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                    }
                    catch (Exception ex) { LogManager.Debug($"[插件] 读取文件状态失败（跳过该条）{f}：{ex.Message}"); }
                }
                return WidgetCompiler.SourceHash(sb.ToString());
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[插件] 计算海床目录签名失败（本次按已变化处理）：{ex.Message}");
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>
        /// 应用自身写盘路径的统一包装：写盘期间暂停 watcher，避免应用的新建/删除/写文件
        /// 触发 watcher 事件链。应用内保存已显式 Reload+Changed，watcher 只需响应**用户**的增删。
        /// watcher 未启动（如 Seeder 在 StartWatching 之前运行）时无副作用。
        /// </summary>
        public static void WithWatcherSuspended(Action action)
        {
            if (action == null) return;
            var watcher = _watcher;
            if (watcher == null) { action(); return; }
            try { watcher.EnableRaisingEvents = false; }
            catch (Exception ex)
            {
                // 暂停失败：应用自身写盘可能触发 watcher 事件链（曾实测 CPU 卡死），必须留痕
                LogManager.Warning($"[插件] 暂停海床文件夹监听失败（写盘可能触发额外刷新）：{ex.Message}");
            }
            try { action(); }
            finally
            {
                try { watcher.EnableRaisingEvents = true; }
                catch (Exception ex)
                {
                    // 恢复失败 = 之后文件增删不再自动刷新；不能抛出（会盖掉 action 的真实异常），只记日志
                    LogManager.Warning($"[插件] 恢复海床文件夹监听失败（后续文件增删不再自动刷新）：{ex.Message}");
                }
            }
        }

        /// <summary>海床项目文件夹根（= 海床树的物理投影）。</summary>
        public static string RootDir => Path.Combine(AppPaths.DataRoot, "seabed");

        /// <summary>小组件代码目录 = 面板/小组件（面板页签「小组件」分区：widget 源码即真相）。</summary>
        public static string WidgetsCodeDir => Path.Combine(RootDir, "面板", "小组件");
        /// <summary>面板功能代码目录 = 面板/面板功能（面板内容代码，用于区域面板下拉）。</summary>
        public static string PanelsCodeDir => Path.Combine(RootDir, "面板", "面板功能");

        /// <summary>
        /// 面板所在的一级目录（<b>扁平化后的唯一位置</b>）：顶层 = 设置页签，面板在 <c>面板/</c> 下平铺、同级。
        /// ★ 上面两个 <c>*CodeDir</c> 是扁平化之前的旧位置，仅用于①启动迁移②过渡期兜底扫描，不再写入。
        /// </summary>
        public static string PanelsDir => Path.Combine(RootDir, "面板");

        /// <summary>旧版本 widgets/ 目录 → seabed/ 迁移（首次运行执行一次）。</summary>
        private static void MigrateLegacyWidgetsDir()
        {
            try
            {
                string old = Path.Combine(AppPaths.DataRoot, "widgets");
                if (Directory.Exists(old) && !Directory.Exists(RootDir))
                {
                    Directory.Move(old, RootDir);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[插件] 旧 widgets/ 目录迁移失败（旧小组件本轮不可见）：{ex.Message}");
            }
        }

        private static List<WidgetPlugin>? _cache;

        public static List<WidgetPlugin> Installed
        {
            get
            {
                if (_cache == null) Reload();
                return _cache ?? new List<WidgetPlugin>();
            }
        }

        // ★ Reload 串行化：watcher 恢复后会在线程池线程触发 Reload，与 UI 线程的
        //   Save/Delete/Installed 并发——文件移动/解包/目录扫描不能并行（会互相踩文件）。
        private static readonly object _reloadLock = new object();

        public static void Reload()
        {
            lock (_reloadLock)
            {
                // ★ 分段计时：启动卡顿（实测"第二阶段→主窗口初始化完成"之间有约 5 秒空白）需要先看清时间花在哪，
                //   只在这段明显慢时记一行，避免高频 Reload 刷屏。
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ReloadCore();
                long tCore = sw.ElapsedMilliseconds;
                // ★ 状态栏/动画插件缓存随 Reload 一起刷新（watcher 增删文件、应用保存后都会走到这里）
                ReloadStatusProviders();
                long tStatus = sw.ElapsedMilliseconds;
                ReloadAnimations();
                long tAnim = sw.ElapsedMilliseconds;
                if (tAnim > 300)
                    LogManager.Debug($"[插件] Reload 耗时 {tAnim}ms：扫描 {tCore}ms / 状态栏 {tStatus - tCore}ms / 动画 {tAnim - tStatus}ms");
            }
        }

        /// <summary>
        /// 让下次访问重新扫描海床目录。Seeder 升级模板文件后必须调 —— 否则本轮仍会用升级前的缓存内容，
        /// 表现为「改了仓库模板要启动两次才生效」（第一次 Seeder 升级文件，插件商店却在升级前就扫过并缓存了）。
        /// </summary>
        public static void InvalidateCache()
        {
            _cache = null;
            _statusProviders = null;
            _animations = null;
        }

        private static void ReloadCore()
        {
            var list = new List<WidgetPlugin>();
            try
            {
                EnsureSkeleton();   // 不存在时创建分组骨架
                // ★ 分组发现：设置页签目录（常规/区域/面板/动画）+ 面板/小组件 + 面板/面板功能（代码目录），
                //   兼容历史顶层代码目录（迁移后为空）；同一目录只扫一次。
                var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var groupPairs = new List<(string Group, string Dir)>();
                foreach (var g in Directory.GetDirectories(RootDir))
                    groupPairs.Add((Path.GetFileName(g), g));
                foreach (var sub in new[] { (Group: "小组件", Dir: WidgetsCodeDir), (Group: "面板功能", Dir: PanelsCodeDir) })
                    if (Directory.Exists(sub.Dir)) groupPairs.Add(sub);
                foreach (var pair in groupPairs)
                {
                    string groupDir = pair.Dir;
                    if (!seenDirs.Add(groupDir)) continue;
                    string group = pair.Group;
                    // ① 分组目录下的 .cs 单文件 → 归一化为 <id>/main.cs（自动包裹）
                    foreach (var csFile in Directory.GetFiles(groupDir, "*.cs"))
                    {
                        try { NormalizeSingleCs(csFile, groupDir); }
                        catch (Exception ex)
                        {
                            // 单个文件归一化失败只影响这一个小组件，但"我放了文件却没出现"必须能从日志查
                            LogManager.Warning($"[插件] 归一化单文件失败 {csFile}：{ex.Message}");
                        }
                    }
                    // ② 分组目录下的预设包（.shpkg，兼容旧版 .dbp）→ 自动解包为 <id>/ 目录
                    foreach (var archive in Directory.GetFiles(groupDir, "*.shpkg")
                                 .Concat(Directory.GetFiles(groupDir, "*.dbp")))
                    {
                        try { NormalizePackageArchive(archive, groupDir); }
                        catch (Exception ex)
                        {
                            LogManager.Error($"[插件] 解包预设包失败（包未安装）{archive}：{ex.Message}", ex);
                        }
                    }
                    // ③ 标准目录（main.cs + manifest.json；或 XAML 形态 <id>.xaml + <id>.xaml.cs）
                    foreach (var dir in Directory.GetDirectories(groupDir))
                    {
                        try
                        {
                            string id = Path.GetFileName(dir);
                            string dirName = Path.GetFileName(dir);
                            string main = Path.Combine(dir, "main.cs");
                            bool hasMain = File.Exists(main);
                            // ★ XAML 形态：<id>.xaml + <id>.xaml.cs（无 main.cs 时也可运行，走 CompileXaml）
                            string xamlFile = Path.Combine(dir, dirName + ".xaml");
                            string xamlCsFile = Path.Combine(dir, dirName + ".xaml.cs");
                            bool hasXaml = File.Exists(xamlFile) && File.Exists(xamlCsFile);
                            if (!hasMain && !hasXaml) continue;   // 目录里既无 main.cs 也无 XAML → 跳过
                            string source = hasMain ? File.ReadAllText(main) : "";
                            string xaml = hasXaml ? File.ReadAllText(xamlFile) : "";
                            string xamlCs = hasXaml ? File.ReadAllText(xamlCsFile) : "";
                            string name = id, author = "", desc = "";
                            var perms = new List<string>();
                            string mf = Path.Combine(dir, "manifest.json");
                            bool isSystem = false;
                            bool? trustedFromManifest = null;
                            string kind = "";
                            if (File.Exists(mf))
                            {
                                // ★★★ 必须大小写不敏感：manifest 有两个写盘端、键风格不同 ——
                                //   BuiltinTemplateSeeder / SaveNodeToFolder 用小写 Dictionary 键（"name"/"system"/"kind"…），
                                //   WidgetPluginStore.Save 序列化 WidgetManifest 则是 PascalCase（"Name"/"System"…）。
                                //   默认反序列化大小写敏感 → 小写键全部匹配失败 → system/kind 恒空 →
                                //   内置模板不被跳过、面板功能被当小组件编译（13 标签布局循环卡死 + 激活 2.9s 冷编译）。
                                var m = JsonSerializer.Deserialize<WidgetManifest>(File.ReadAllText(mf),
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (m != null)
                                {
                                    if (!string.IsNullOrEmpty(m.Name)) name = m.Name;
                                    author = m.Author ?? "";
                                    desc = m.Description ?? "";
                                    perms = m.Permissions ?? new List<string>();
                                    isSystem = m.System;
                                    trustedFromManifest = m.TrustedSource;
                                    kind = m.Kind ?? "";
                                }
                            }
                            // ★ 内置副本（system 标记）默认只作文件夹展示、不当可安装/可删除的小组件；
                            //   但**已迁移**的 id（小组件 MigratedBuiltinIds / 面板 MigratedPanelIds）要真正加载 ——
                            //   那才是"文件夹里那份在跑"。
                            if (isSystem && !MigratedBuiltinIds.Contains(id) && !MigratedPanelIds.Contains(id)) continue;
                            // ★ 信任判定：manifest 显式标记优先；内置副本/无标记的本地文件默认信任
                            //   （本地编程不检测；内置模板构建时已验证，且面板功能合理使用 Process.Start/FileInfo 等
                            //   被黑名单覆盖的 API——无条件过沙箱会把内置功能全拦掉，已实测误杀过一次）
                            // ★★ v2 信任模型：**只认用户在宿主里的显式信任记录**（id + 内容哈希）。
                            //   · manifest 自述（trustedSource / official / author）不再构成信任依据 —— 那是"包自己发免检牌"；
                            //   · 内容一变，哈希对不上 → 信任自动失效（防"先信任干净版、再偷换恶意版"）；
                            //   · 应用自己新建/保存的海床项会在保存时写入信任记录（SaveNodeToFolder）；
                            //   · 其余（市场拾贝、导入包、手动丢文件夹）默认不可信，走沙箱；用户可在界面点"信任"。
                            // ★ 信任判定（v2 修正版，回归海床初衷）：
                            //   · 用户显式点过"改用沙箱" → 不可信；
                            //   · 宿主写的来源标记 / 宿主写的 trustedSource:false / 用户点过"信任"的哈希 → 见 ComputeTrust；
                            //   · 其余（你自己或 AI 写进文件夹的代码）→ **受信，零摩擦**（文件夹即真相源）。
                            string contentHash = ContentHash(source, xaml, xamlCs);
                            bool trusted = ComputeTrust(HasOriginMarker(dir), trustedFromManifest,
                                                        IsExplicitlySandboxed(id), IsTrustedById(id, contentHash));
                            list.Add(new WidgetPlugin
                            {
                                Id = id, Name = name, Author = author, Description = desc,
                                Permissions = perms, Group = group, Source = source,
                                Xaml = xaml, XamlCs = xamlCs,
                                TrustedSource = trusted, Kind = kind, IsBuiltin = isSystem
                            });
                        }
                        catch (Exception ex)
                        {
                            // 单目录读取失败 → 该小组件本轮不出现；不留痕的话用户只看到"我的小组件不见了"
                            LogManager.Warning($"[插件] 读取小组件目录失败（已跳过）{dir}：{ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 扫描海床目录失败（本次列表可能不完整）：{ex.Message}", ex);
            }
            _cache = list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            LogManager.Debug($"[插件] Installed {_cache.Count} 个: " + string.Join(",", _cache.Select(p => p.Id + "(" + (p.Kind ?? "?") + ")")));
        }

        public static WidgetPlugin? GetById(string id) => Installed.FirstOrDefault(p => p.Id == id);

        // ============================================================
        //  自定义状态栏显示项（seabed/状态栏/，IStatusProvider）
        // ============================================================

        private static Dictionary<string, IStatusProvider>? _statusProviders;
        private static readonly Dictionary<string, IStatusProvider> EmptyStatusProviders = new();

        /// <summary>已编译的自定义状态栏显示项：key = "status_&lt;id&gt;"，value = IStatusProvider 实例。
        /// 复用 Installed（ReloadCore 已扫描全部分组并解析 manifest），只过滤「状态栏」分组。</summary>
        public static IReadOnlyDictionary<string, IStatusProvider> StatusProviders
        {
            get
            {
                if (_statusProviders == null) ReloadStatusProviders();
                return _statusProviders ?? EmptyStatusProviders;
            }
        }

        /// <summary>重新扫描「状态栏」分组并编译全部 IStatusProvider（失败项跳过并记日志）。</summary>
        public static void ReloadStatusProviders()
        {
            var map = new Dictionary<string, IStatusProvider>();
            // ★ 安全模式：不加载任何状态栏插件
            if (PluginRuntimeGuard.SafeMode) { _statusProviders = map; LogManager.Debug("[守卫] 安全模式：跳过状态栏插件"); return; }
            try
            {
                foreach (var plugin in Installed)
                {
                    // 类型判定以 manifest kind 为准（不依赖顶层目录名）；kind 缺省时按目录分组名兼容
                    string kind = plugin.Kind ?? "";
                    if (kind == "StatusProvider") { }
                    else if (kind.Length > 0) continue;
                    else if (plugin.Group != "状态栏") continue;
                    // ★ 沙箱只对市场来源（TrustedSource=false）执行（与小组件一致）
                    //   状态栏插件只有 main.cs 一种形态：闸门文本 == 编译文本
                    if (!plugin.TrustedSource)
                    {
                        string sandboxErr = WidgetCompiler.SandboxErrors(plugin.Source, "");
                        if (sandboxErr.Length > 0)
                        {
                            LogManager.Warning(
                                $"状态栏插件 [{plugin.Id}] 被沙箱拦截: {sandboxErr}");
                            continue;
                        }
                    }
                    // ★ id 前缀 status_ 隔离编译缓存，避免与小组件 Widget_ 同名程序集冲突
                    string cacheId = "status_" + plugin.Id;
                    // ★ 运行时守卫：已熔断的插件不再加载
                    if (PluginRuntimeGuard.IsDisabled(cacheId)) { LogManager.Debug($"[守卫] 状态栏插件 {plugin.Id} 已停用，跳过"); continue; }
                    var (provider, err) = WidgetCompiler.Compile<IStatusProvider>(cacheId, plugin.Source);
                    if (provider != null) { map[cacheId] = provider; PluginRuntimeGuard.RegisterAssembly(provider.GetType().Assembly, cacheId); }
                    else LogManager.Warning(
                        $"状态栏插件 [{plugin.Id}] 编译失败: {err}");
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 扫描状态栏插件失败（本次仅 {map.Count} 个可用）：{ex.Message}", ex);
            }
            _statusProviders = map;
            LogManager.Debug($"[插件] 状态栏编译成功 {map.Count} 个: " + string.Join(",", map.Keys));
        }

        // ============================================================
        //  自定义动画（seabed/动画/，IAnimation）
        // ============================================================

        private static Dictionary<string, IAnimation>? _animations;
        private static readonly Dictionary<string, IAnimation> EmptyAnimations = new();

        /// <summary>已编译的自定义动画：key = 动画 Id（实例 Id，缺省用插件 id），value = IAnimation 实例。
        /// 同时注册进 AnimationRegistry（ShapeAnimator 运行时查表用）。</summary>
        public static IReadOnlyDictionary<string, IAnimation> Animations
        {
            get
            {
                if (_animations == null) ReloadAnimations();
                return _animations ?? EmptyAnimations;
            }
        }

        /// <summary>重新扫描「动画」分组并编译全部 IAnimation；注册表同步重建（清空后重新注册）。</summary>
        public static void ReloadAnimations()
        {
            var map = new Dictionary<string, IAnimation>();
            // ★ 安全模式：不加载任何自定义动画（内置动画照常）
            if (PluginRuntimeGuard.SafeMode) { _animations = map; ShoreHue.Animation.AnimationRegistry.ReplaceAll(map); LogManager.Debug("[守卫] 安全模式：跳过自定义动画"); return; }
            try
            {
                foreach (var plugin in Installed)
                {
                    string kind = plugin.Kind ?? "";
                    if (kind == "Animation") { }
                    else if (kind.Length > 0) continue;
                    else if (plugin.Group != "动画") continue;
                    if (PluginRuntimeGuard.IsDisabled(plugin.Id)) continue;   // ★ 运行时守卫：已熔断的动画不再加载
                    // 动画插件只有 main.cs 一种形态：闸门文本 == 编译文本
                    if (!plugin.TrustedSource)
                    {
                        string sandboxErr = WidgetCompiler.SandboxErrors(plugin.Source, "");
                        if (sandboxErr.Length > 0)
                        {
                            LogManager.Warning(
                                $"动画插件 [{plugin.Id}] 被沙箱拦截: {sandboxErr}");
                            continue;
                        }
                    }
                    string cacheId = "anim_" + plugin.Id;
                    var (anim, err) = WidgetCompiler.Compile<IAnimation>(cacheId, plugin.Source);
                    if (anim == null)
                    {
                        LogManager.Warning(
                            $"动画插件 [{plugin.Id}] 编译失败: {err}");
                        continue;
                    }
                    // ★ 注册表/设置存储都用动画实例的 Id（缺省回退文件夹 id）——GetResolvedShowAnimationType
                    //   返回的就是这个 Id，ShapeAnimator 据此查表；Name 用于设置页 ComboBox 展示。
                    string key = string.IsNullOrEmpty(anim.Id) ? plugin.Id : anim.Id;
                    if (!map.ContainsKey(key)) { map[key] = anim; PluginRuntimeGuard.RegisterAssembly(anim.GetType().Assembly, plugin.Id); }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 扫描动画插件失败（本次仅 {map.Count} 个可用）：{ex.Message}", ex);
            }
            _animations = map;
            LogManager.Debug($"[插件] 动画编译成功 {map.Count} 个: " + string.Join(",", map.Keys));
            // ★ 注册表与缓存同源：ShapeAnimator 只依赖 AnimationRegistry（动画命名空间），
            //   不反向依赖 UI 层，避免分层耦合。
            ShoreHue.Animation.AnimationRegistry.ReplaceAll(map);
        }

        /// <summary>分组目录集合：设置页签顶层 + 代码目录（面板/小组件、面板/面板功能）。</summary>
        private static IEnumerable<string> LeafGroupDirs()
        {
            foreach (var g in Directory.GetDirectories(RootDir)) yield return g;
            if (Directory.Exists(WidgetsCodeDir)) yield return WidgetsCodeDir;
            if (Directory.Exists(PanelsCodeDir)) yield return PanelsCodeDir;
        }

        /// <summary>叶子功能目录（分组下一层的功能文件夹，去重）。</summary>
        private static IEnumerable<string> LeafFeatureDirs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in LeafGroupDirs())
            {
                foreach (var d in Directory.GetDirectories(g))
                {
                    if (seen.Add(d)) yield return d;
                }
            }
        }

        /// <summary>保存（新建或覆盖）。返回错误信息，成功为空串。</summary>
        public static string Save(WidgetPlugin plugin)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || !IsValidId(plugin.Id))
                return "Id 无效：仅允许英文/数字/下划线/连字符（2-32 字符）";
            if (string.IsNullOrWhiteSpace(plugin.Source))
                return "源码为空";
            try
            {
                // ★ 应用自身写盘：暂停 watcher，避免写文件触发事件链（watcher 只响应**用户**的增删）
                WithWatcherSuspended(() =>
                {
                    // ★ 写入面板目录（扁平化后只有一层：小组件/面板功能 → 面板/<id>；其余按分组名）
                    string group = string.IsNullOrEmpty(plugin.Group) ? "小组件" : plugin.Group;
                    string groupPath = group is "小组件" or "面板功能" ? PanelsDir
                        : Path.Combine(RootDir, group);
                    Directory.CreateDirectory(groupPath);
                    string dir = Path.Combine(groupPath, plugin.Id);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "main.cs"), plugin.Source);
                    var m = new WidgetManifest
                    {
                        Name = plugin.Name, Author = plugin.Author,
                        Description = plugin.Description, Permissions = plugin.Permissions,
                        TrustedSource = plugin.TrustedSource
                    };
                    File.WriteAllText(Path.Combine(dir, "manifest.json"),
                        JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
                });
                Reload();
                Changed?.Invoke();
                return "";
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 保存 [{plugin.Id}] 失败：{ex.Message}", ex);
                return "保存失败：" + ex.Message;
            }
        }

        public static bool Delete(string id)
        {
            try
            {
                // ★ 应用自身删除：暂停 watcher，避免删除目录触发事件链（watcher 只响应**用户**的增删）
                bool deleted = false;
                WithWatcherSuspended(() =>
                {
                    // ★ 支持分组路径：从所有叶子功能目录中查找（含 面板/小组件、面板/面板功能）
                    if (!Directory.Exists(RootDir)) return;
                    foreach (var dir in LeafFeatureDirs())
                    {
                        if (string.Equals(Path.GetFileName(dir), id, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(dir, true);
                            deleted = true;
                            return;
                        }
                    }
                });
                if (deleted)
                {
                    // ★ 清编译缓存，释放 widget 实例与程序集引用
                    WidgetCompiler.Evict(id);
                    ClearLoadIssue(id);   // 插件没了，它上一次的加载问题也别再挂着
                    Reload();
                    Changed?.Invoke();
                    return true;
                }
            }
            catch (Exception ex)
            {
                // 返回 false 由调用方在界面上提示"删除失败"（文件被占用时最常见）
                LogManager.Error($"[插件] 删除 [{id}] 失败：{ex.Message}", ex);
            }
            return false;
        }

        /// <summary>创建分组骨架（不存在时）。根目录 = 设置页签（常规/区域/面板/动画）；
        /// 面板内容代码归 面板/小组件 与 面板/面板功能（与设置面板「面板」页签同构）。</summary>
        private static void EnsureSkeleton()
        {
            MigrateLegacyWidgetsDir();
            MigrateGroupLayout();   // 旧布局：顶层 小组件/面板功能 → 面板/ 下
            if (!Directory.Exists(RootDir)) Directory.CreateDirectory(RootDir);
            foreach (var g in new[] { "常规", "区域", "面板", "动画" })
            {
                string d = Path.Combine(RootDir, g);
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            }
            if (!Directory.Exists(PanelsDir)) Directory.CreateDirectory(PanelsDir);
            // ★ 扁平化（一次性、幂等）：把 面板/小组件/<id> 与 面板/面板功能/<id> 上提到 面板/<id>。
            //   只移动不删除、目标已存在则跳过、失败不阻塞启动（旧路径仍会被扫描）。
            //   见 docs\方案-海床目录扁平化.md 与 SeabedPanelLayout。
            ShoreHue.UI.Seabed.SeabedPanelLayout.Flatten(RootDir);
        }

        /// <summary>布局迁移（一次）：把历史顶层代码目录 小组件/、面板功能/ 整体挪到 面板/ 下（目标存在则不重复移动）。</summary>
        private static void MigrateGroupLayout()
        {
            try
            {
                MoveDirInto(RootDir, "小组件", WidgetsCodeDir);
                MoveDirInto(RootDir, "面板功能", PanelsCodeDir);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[插件] 旧分组布局迁移失败（部分插件可能仍落在旧目录）：{ex.Message}");
            }
        }

        private static void MoveDirInto(string root, string name, string targetDir)
        {
            string src = Path.Combine(root, name);
            if (!Directory.Exists(src)) return;
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                Directory.Move(src, targetDir);
                return;
            }
            // 目标已存在（如 Seeder 先跑）：逐项并入
            foreach (var item in Directory.EnumerateFileSystemEntries(src))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(item));
                if (Directory.Exists(dest) || File.Exists(dest)) continue;
                try
                {
                    if (Directory.Exists(item)) Directory.Move(item, dest);
                    else File.Move(item, dest);
                }
                catch (Exception ex)
                {
                    // 单项失败只影响这一个条目（其余继续并入）；冲突条目留在原目录，下次启动重试
                    LogManager.Warning($"[插件] 迁移旧布局条目失败 {item}：{ex.Message}");
                }
            }
            // 源里所有条目都已在目标存在（旧布局残留副本）→ 整体删除；否则保留真正冲突的条目
            bool allMoved = Directory.EnumerateFileSystemEntries(src)
                .All(item => Directory.Exists(Path.Combine(targetDir, Path.GetFileName(item)))
                          || File.Exists(Path.Combine(targetDir, Path.GetFileName(item))));
            if (allMoved)
            {
                // 尽力而为：残留副本删不掉不影响功能（条目已并入目标目录，下次启动会再试一次）
                try { Directory.Delete(src, true); }
                catch (Exception ex) { LogManager.Debug($"[插件] 清理旧布局残留目录失败（无害）{src}：{ex.Message}"); }
            }
        }

        /// <summary>把分组目录下的 .cs 单文件归一化为 &lt;name&gt;/main.cs 目录（id = 文件名）。</summary>
        private static void NormalizeSingleCs(string csFile, string groupDir)
        {
            string fileName = Path.GetFileNameWithoutExtension(csFile);
            if (string.IsNullOrEmpty(fileName)) return;
            string id = SanitizeId(fileName);
            if (id.Length < 2) return;
            string targetDir = Path.Combine(groupDir, id);
            if (Directory.Exists(targetDir))
            {
                // ★ 不要删用户的文件：以前这里直接 File.Delete(csFile)（注释写"清理重复文件"），
                //   而本方法由 ReloadCore 对 seabed 下每个分组目录、**每次 Reload/watcher 事件**都会跑 ——
                //   用户在 `foo/` 旁边放了一个 `foo.cs`（很自然：想试单个文件形态），
                //   文件会在下次刷新时被**无提示、不进回收站**地删掉。
                //   改为保留文件并把冲突写进加载问题（界面可见），由用户自己决定怎么处理。
                SetLoadIssue(id, $"同名冲突：目录 {id}/ 与文件 {Path.GetFileName(csFile)} 同时存在，已保留文件未处理");
                LogManager.Warning($"[插件] 同名冲突（已保留文件，未自动删除）：目录 {targetDir} 与 {csFile}");
                return;
            }
            Directory.CreateDirectory(targetDir);
            File.Move(csFile, Path.Combine(targetDir, "main.cs"));
        }

        /// <summary>把预设包（.shpkg，兼容旧 .dbp）解包为 &lt;id&gt;/ 目录（manifest + main.cs + config），然后删除包文件。</summary>
        private static void NormalizePackageArchive(string archiveFile, string groupDir)
        {
            string fileName = Path.GetFileNameWithoutExtension(archiveFile);
            string id = SanitizeId(fileName);
            if (id.Length < 2) return;
            string targetDir = Path.Combine(groupDir, id);
            if (Directory.Exists(targetDir)) { File.Delete(archiveFile); return; }   // 已解包过 → 清理
            Directory.CreateDirectory(targetDir);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(archiveFile))
            {
                foreach (var entry in zip.Entries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    string dest = Path.Combine(targetDir, name);
                    using var src = entry.Open();
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                }
            }
            // ★ 来源标记：宿主解包出来的包 = 外来代码 → 走沙箱（包内自述无法把自己变成"本地"）
            //   fail-closed：标记写不上就**不能**留下解包结果 —— 没有标记的目录会被信任判定当成
            //   "本地代码"直接放行；此时保留原包文件（用户看得见包还在），下次扫描重试，原因在日志里。
            if (!WriteOriginMarker(targetDir, "package"))
            {
                try { Directory.Delete(targetDir, true); }
                catch (Exception ex) { LogManager.Error($"[插件] 回滚未标记的解包目录失败 {targetDir}：{ex.Message}", ex); }
                return;
            }
            File.Delete(archiveFile);
        }

        /// <summary>文件名 → 合法 id（英文/数字/下划线/连字符）。</summary>
        private static string SanitizeId(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (name ?? ""))
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                if (sb.Length >= 32) break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把海床树的节点落盘到文件夹（用户保存/创建时调用）：
        /// 路径 = seabed/&lt;树路径链&gt;/&lt;节点名&gt;/（与海床树一级/二级/三级结构一致），内含 manifest.json + 内容文件。
        /// manifest.json 是树↔文件夹的桥梁：文件夹里的文件被 ShoreHue 扫描时靠它还原节点。
        /// 返回错误信息（空串 = 成功）：调用方紧接着会显示"已保存/已更新"，吞掉失败等于界面谎报成功。
        /// </summary>
        public static string SaveNodeToFolder(ShoreHue.Core.Models.CustomPanelDefinition cp)
        {
            string err = "";
            try
            {
                // ★ 应用自身写盘：暂停 watcher（应用保存已显式 Reload+Changed，watcher 只响应**用户**的增删）
                WithWatcherSuspended(() => err = SaveNodeToFolderCore(cp));
            }
            catch (Exception ex)
            {
                // 兜底：写盘包装本身失败（内核自带 try，正常走不到这里），同样按对外契约返回错误
                LogManager.Error($"[插件] 保存节点到文件夹失败（{cp?.Name}）：{ex.Message}", ex);
                return "写入文件夹失败：" + ex.Message;
            }
            return err;
        }

        /// <summary>节点在 seabed 里的真实目录 —— **唯一真相源**。
        /// ★ 与主落盘（SaveNodeToFolderCore 的树路径链）共用同一套算法。此前 WriteExtraFiles / LoadNodeXaml
        ///   各自用「分类 + 显示名」另算一份路径，于是同一个市场包被拆到两个目录：
        ///   main.cs/manifest.json → 面板/小组件/&lt;名字&gt;/；.xaml/.xaml.cs → 面板/&lt;名字&gt;/。
        ///   而后者没有 manifest.json，加载端又要求「文件名 = 目录名」——
        ///   于是 XAML 形态要么被静默跳过（装包是残的：只剩那份从不加载的 main.cs），
        ///   要么（文件名恰好等于目录名时）被当成「用户手写放进文件夹的代码」而**免沙箱**加载
        ///   （ComputeTrust 对"无来源标记 + manifest 未写 trustedSource:false"返回 true）。
        /// </summary>
        private static string NodeDirFor(ShoreHue.Core.Models.CustomPanelDefinition cp)
        {
            // ★ 树路径 = 文件夹路径：按 ParentKey 找到父节点在树里的完整路径链（一级/二级/三级）
            var pathChain = ShoreHue.UI.Seabed.ConfigTreeBuilder.FindPathNames(cp.ParentKey ?? "");
            var parts = new System.Collections.Generic.List<string>();
            if (pathChain.Count > 0)
            {
                // 树路径链直接作为文件夹层级（如 面板/小组件/节点名）
                foreach (var seg in pathChain) parts.Add(SanitizeId(seg));
            }
            else
            {
                // 兜底：按一级分类映射（节点无内置父链时）
                parts.Add(MapCategoryToFolder(cp.Category));
            }
            parts.Add(SanitizeId(cp.Name));
            string current = RootDir;
            foreach (var seg in parts) current = Path.Combine(current, seg);
            return current;
        }

        /// <summary>落盘的实现体：成功返回空串，失败返回界面可直接显示的错误信息。</summary>
        private static string SaveNodeToFolderCore(ShoreHue.Core.Models.CustomPanelDefinition cp)
        {
            try
            {
                EnsureSkeleton();
                string safeName = SanitizeId(cp.Name);
                // ★ 必须返回**错误串**而不是空串：所有调用方都把 "" 当成功
                //   （界面会提示"已保存单预设/文件已写入"），于是名字过短或全是符号时
                //   用户以为存上了，实际什么都没落盘（只剩 CustomPanels 里那条内存记录）。
                if (safeName.Length < 2)
                    return $"名称「{cp.Name}」至少需要 2 个有效字符（字母/数字/_/-），请改名后重试";
                string nodeDir = NodeDirFor(cp);
                Directory.CreateDirectory(nodeDir);

                // manifest.json：完整记录节点元信息（树↔文件夹还原依据）
                var manifest = new Dictionary<string, object?>
                {
                    ["id"] = cp.Id,
                    ["name"] = cp.Name,
                    ["category"] = cp.Category,
                    ["kind"] = cp.Kind ?? "",
                    ["baseType"] = cp.BaseType ?? "",
                    ["parentKey"] = cp.ParentKey ?? "",
                    ["sourceKey"] = cp.SourceKey ?? "",
                    ["createdAt"] = cp.CreatedAt ?? "",
                    ["permissions"] = WidgetPermissions.Detect(cp.Source ?? ""),
                    ["trustedSource"] = cp.TrustedSource,
                    // ★ 这个本地面板对应市场里的哪个包（发布成功后回写）。没有它，更新同一个包时
                    //   用户得手打「登录名/短名」，很容易打错成新包或撞上别人的 ID。
                    ["marketId"] = cp.MarketId ?? ""
                };
                File.WriteAllText(Path.Combine(nodeDir, "manifest.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

                // 内容文件：小组件/面板 → main.cs（+ 完全编程 .xaml/.xaml.cs）；配置 → config.json
                if (!string.IsNullOrEmpty(cp.Source))
                    File.WriteAllText(Path.Combine(nodeDir, "main.cs"), cp.Source);
                if (!string.IsNullOrEmpty(cp.Xaml))
                    File.WriteAllText(Path.Combine(nodeDir, safeName + ".xaml"), cp.Xaml);
                if (!string.IsNullOrEmpty(cp.XamlCs))
                    File.WriteAllText(Path.Combine(nodeDir, safeName + ".xaml.cs"), cp.XamlCs);
                if (!string.IsNullOrEmpty(cp.ConfigJson) && cp.ConfigJson != "{}")
                    File.WriteAllText(Path.Combine(nodeDir, "config.json"), cp.ConfigJson);

                // ★ 安全 v2：只有"可信"的节点（用户在海床里新建/编辑保存的）才登记信任记录；
                //   市场拾贝/导入的节点（TrustedSource=false）不登记 → 加载时走沙箱。
                if (cp.TrustedSource) RegisterNodeTrust(cp);
                return "";
            }
            catch (Exception ex)
            {
                // 用户点了"保存"却没落盘 → 必须把错误带回界面（附带回退：内存里的节点仍在，只是文件没写）
                LogManager.Error($"[插件] 保存节点到文件夹失败（{cp?.Name}）：{ex.Message}", ex);
                return "写入文件夹失败：" + ex.Message;
            }
        }

        /// <summary>登记节点信任（id + 内容哈希）。失败只降级、不丢数据：节点文件已落盘，
        /// 只是下次加载会被当成外来代码过沙箱 —— 必须留痕，否则用户以为"我保存过就该受信"。</summary>
        private static void RegisterNodeTrust(ShoreHue.Core.Models.CustomPanelDefinition cp)
        {
            try
            {
                var s = ServiceManager.Instance.GetService<SettingsManager>() as IPluginTrustStore;
                if (s == null)
                {
                    LogManager.Warning($"[插件] 设置服务不可用，节点 [{cp.Id}] 未登记信任（下次加载按外来代码过沙箱）");
                    return;
                }
                s.SetPluginTrusted(cp.Id, ContentHash(cp.Source, cp.Xaml, cp.XamlCs));
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 登记节点 [{cp.Id}] 信任失败（下次加载按外来代码过沙箱）：{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 向已保存节点的海床文件夹写入附加文件（多形态：.xaml / .xaml.cs 等）。
        /// 目录按 分类/节点名 定位；返回错误信息（空串 = 成功）。
        /// 附加文件缺失会让 XAML 形态的包退化成"只有 main.cs"，所以调用方必须把失败说出来。
        /// </summary>
        public static string WriteExtraFiles(ShoreHue.Core.Models.CustomPanelDefinition cp,
            System.Collections.Generic.List<ShoreHue.UI.Seabed.GitHubMarketService.PackageFile> files)
        {
            try
            {
                if (files == null || files.Count == 0) return "";
                // ★ 与主落盘共用同一个「节点目录」算法（NodeDirFor）——以前这里另算一份路径，
                //   导致 main.cs/manifest.json 与 .xaml/.xaml.cs 被拆到两个目录（见 NodeDirFor 注释）。
                string dir = NodeDirFor(cp);
                if (!Directory.Exists(dir))
                {
                    // ★ 节点还没落盘 → 附加文件无处可写（调用方应先 SaveNodeToFolder）。留痕但不上报界面：
                    //   该包会退化成纯 main.cs 形态，用户看不到差异。
                    LogManager.Warning($"[插件] 附加文件未写入：节点文件夹不存在 {dir}");
                    return "";
                }
                foreach (var f in files)
                {
                    if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Content)) continue;
                    string fname = Path.GetFileName(f.Name);
                    if (string.IsNullOrEmpty(fname)) continue;
                    // ★ 附加文件不得改写 manifest.json：它是信任判定的输入（system / trustedSource），
                    //   外来包借它就能把自己洗成内置件、或洗掉"外来"标记。manifest 只由宿主自己写。
                    if (string.Equals(fname, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        LogManager.Warning($"[插件] 附加文件试图覆盖 manifest.json，已忽略（{cp.Name}）");
                        continue;
                    }
                    WithWatcherSuspended(() => File.WriteAllText(Path.Combine(dir, fname), f.Content));
                }
                return "";
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 写入附加文件失败（{cp?.Name}）：{ex.Message}", ex);
                return "写入附加文件失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 读取节点海床文件夹里的 XAML 形态文件（<名字>.xaml + <名字>.xaml.cs）。
        /// 按 分类/节点名 定位；找不到返回空串。用于选中节点时补充加载完全编程代码。
        /// </summary>
        public static (string Xaml, string XamlCs) LoadNodeXaml(ShoreHue.Core.Models.CustomPanelDefinition cp)
        {
            try
            {
                // ★ 与主落盘共用同一个「节点目录」算法（NodeDirFor）。此前这里也另算一份路径，
                //   与 SaveNodeToFolderCore 不一致 → 读不到刚写进另一个目录的 XAML 形态。
                string dir = NodeDirFor(cp);
                if (!Directory.Exists(dir)) return ("", "");
                string safeName = SanitizeId(cp.Name);
                string x = "", xc = "";
                string xf = Path.Combine(dir, safeName + ".xaml");
                string xcf = Path.Combine(dir, safeName + ".xaml.cs");
                if (File.Exists(xf)) x = File.ReadAllText(xf);
                if (File.Exists(xcf)) xc = File.ReadAllText(xcf);
                return (x, xc);
            }
            catch (Exception ex)
            {
                // 读不到 XAML 形态 → 节点可能被当成"纯 main.cs"编译（形态决定走哪条编译闸门），必须留痕
                LogManager.Warning($"[插件] 读取节点 XAML 形态失败（{cp?.Name}）：{ex.Message}");
                return ("", "");
            }
        }

        /// <summary>按 manifest.json 的 id 查找节点文件夹（跨分组）。找不到返回 null。</summary>
        public static string? FindNodeDirById(string customId)
        {
            try
            {
                if (string.IsNullOrEmpty(customId) || !Directory.Exists(RootDir)) return null;
                foreach (var dir in LeafFeatureDirs())
                {
                    string mf = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(mf)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                        if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                            idEl.ValueKind == JsonValueKind.String &&
                            idEl.GetString() == customId)
                            return dir;
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning($"[插件] 解析 manifest 失败（该目录已跳过）{mf}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 按 id 查找节点目录失败（{customId}）：{ex.Message}", ex);
            }
            return null;
        }

        /// <summary>删除海床节点的文件夹（按 manifest.json 里的 id 匹配，跨分组查找）。</summary>
        public static void DeleteNodeFolder(string customId)
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (var dir in LeafFeatureDirs())
                {
                    string mf = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(mf)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                        if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                            idEl.ValueKind == JsonValueKind.String &&
                            idEl.GetString() == customId)
                        {
                            Directory.Delete(dir, true);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning($"[插件] 解析 manifest 失败（该目录已跳过）{mf}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[插件] 删除节点文件夹失败（{customId}）：{ex.Message}", ex);
            }
        }

        /// <summary>一级分类 → 分组文件夹名（设置页签分类；保留旧分类名映射以兼容存量数据）。</summary>
        public static string MapCategoryToFolder(string category)
        {
            switch (category)
            {
                // ★ 扁平化后：面板全部落在 面板/ 这一层，不再分「小组件 / 面板功能」
                case "小组件": return "面板";
                case "面板功能": return "面板";
                // 设置页签分组
                case "常规": return "常规";
                case "区域": return "区域";
                case "面板": return "面板";
                case "动画": return "动画";
                // 旧分类名（v1.1.0 之前/过渡期存量 CustomPanel 仍带这些值）：映射到就近目录，避免落"其他"
                case "面板设计": return "面板";
                case "外观": return "面板";
                case "交互": return "区域";
                case "状态栏": return "面板";
                default: return "其他";
            }
        }

        /// <summary>在系统文件管理器中打开指定节点的文件夹（按 manifest.id 匹配，跨分组查找；找不到回退根目录）。</summary>
        public static void OpenNodeFolder(string customId, string name, string category)
        {
            var log = LogManager.Debug;
            log($"[OpenFolder] 调用 customId={customId} name={name} category={category} RootDir={RootDir}");
            try
            {
                if (!Directory.Exists(RootDir)) { log("[OpenFolder] RootDir 不存在"); return; }
                // ① 按 manifest.id 精确定位（候选叶子目录，含 面板/小组件、面板/面板功能）
                if (!string.IsNullOrEmpty(customId))
                {
                    foreach (var dir in LeafFeatureDirs())
                    {
                        string mf = Path.Combine(dir, "manifest.json");
                        if (!File.Exists(mf)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                            if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                                idEl.ValueKind == JsonValueKind.String &&
                                idEl.GetString() == customId)
                            {
                                log($"[OpenFolder] ① manifest.id 命中 dir={dir}");
                                OpenFolderInExplorer(dir);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogManager.Warning($"[插件] 解析 manifest 失败（该目录已跳过）{mf}：{ex.Message}");
                        }
                    }
                }
                // ② 回退：按 分组/节点名 定位
                string group = MapCategoryToFolder(category);
                string safeName = SanitizeId(name);
                string path = Path.Combine(RootDir, group, safeName);
                log($"[OpenFolder] ①未命中，尝试② path={path} exists={Directory.Exists(path)}");
                if (Directory.Exists(path))
                {
                    OpenFolderInExplorer(path);
                    return;
                }
                // ③ 兜底：先尝试分组目录（内置节点无独立文件夹 → 打开所属分组，用户能看到该分组所有文件）
                string groupPath = Path.Combine(RootDir, group);
                log($"[OpenFolder] ②未命中，尝试③ groupPath={groupPath} exists={Directory.Exists(groupPath)}");
                if (Directory.Exists(groupPath))
                {
                    OpenFolderInExplorer(groupPath);
                    return;
                }
                // ④ 最终兜底：打开根目录
                log("[OpenFolder] ③未命中，兜底打开根目录");
                OpenFolder();
            }
            catch (Exception ex)
            {
                // 用户点了"打开文件夹"却没反应：Debug 级追踪不够，按关键路径留 Warning
                LogManager.Warning($"[插件] 打开节点文件夹失败：{ex}");
            }
        }

        /// <summary>在系统文件管理器中打开小组件根目录（用户可直接增删/拖文件）。</summary>
        public static void OpenFolder()
        {
            try
            {
                EnsureSkeleton();
                OpenFolderInExplorer(RootDir);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[插件] 打开海床根目录失败：{ex.Message}");
            }
        }

        /// <summary>用 explorer.exe 显式打开文件夹（比 UseShellExecute 直接给目录更可靠，点击必生效）。</summary>
        private static void OpenFolderInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // 打不开 = 用户点了没反应，日志是唯一线索
                LogManager.Warning($"[插件] 无法用资源管理器打开 {path}：{ex.Message}");
            }
        }

        private class WidgetManifest
        {
            public string? Name { get; set; }
            public string? Author { get; set; }
            public string? Description { get; set; }
            public List<string>? Permissions { get; set; }
            /// <summary>ShoreHue 内置副本标记（只展示，不可当作用户小组件安装/删除）。</summary>
            public bool System { get; set; }
            /// <summary>信任来源标记：false = 市场来源，加载前过沙箱；缺省/true = 本地代码直接加载。</summary>
            public bool? TrustedSource { get; set; }
            /// <summary>类型（Widget/Panel/Config/Category）。</summary>
            public string? Kind { get; set; }
        }
    }
}
