using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.UI.Localization;
using System;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Widgets.Calculator;
using ShoreHue.UI.Widgets.ClipboardHistory;
using ShoreHue.UI.Widgets.Notes;
using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.UI.Widgets.TextAi;
using ShoreHue.UI.Widgets.Timer;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ShoreHue.UI.Widgets
{
    /// <summary>
    /// 小组件切换器：标签按设置中启用的小组件动态生成，
    /// 用户可在设置中自行选择面板保留哪些功能。
    /// </summary>
    public partial class WidgetSwitcher : UserControl, IWidget
    {
        private sealed class WidgetTab
        {
            public string Key = "";
            public string IconKey = "";
            public string LocKey = "";
            public IWidget Widget = null!;
        }

        private readonly ISettingsService _settings;
        private readonly ClipboardHistoryWidget _clipboardWidget;
        private readonly NoteWidget _noteWidget;
        private readonly TimerWidget _timerWidget;
        private readonly CalculatorWidget _calculatorWidget;
        private readonly TextAiWidget _textAiWidget;
        private readonly WebViewWidget _webWidget;

        private readonly List<WidgetTab> _tabs = new();
        private readonly TaskbarScrollHandler _tabScrollHandler;
        private string _currentTab = "";
        private bool _rebuilding;
        /// <summary>
        /// 抑制下一次由 ShowContent 排定的尺寸通知。
        /// ★ 设置变化**不该改变面板尺寸**：组件集合与当前标签都没变，内容的尺寸语义上没变。
        ///   不抑制的话链条是：RebuildTabs → ShowContent → ContentSizeChanged → 主窗口 ApplyAutoSize，
        ///   而此刻面板还处在"上一个大尺寸"的约束下，MeasureContentSize 会量出偏小的 DesiredSize →
        ///   面板变窄 → 内容重排更高 → 下次量得更窄 …… 真机实测每步宽 −32 / 高 +74、一路顶到高度上限，
        ///   用户看到的就是"改个设置面板自己就变瘦，重新唤出面板才恢复"。
        ///   只有当前标签**真的换了**（原标签被停用）才需要重新自适应。
        /// </summary>
        private bool _suppressSizeNotify;
        private string _dynamicSignature = ""; // 插件列表签名：面板激活时对比，变化则重建标签
        private string? _cachedPluginSignature; // 签名缓存：插件列表/源码变化时才重算，避免激活时重复哈希
        // ★ 内置件「启用集合」签名：只有它变了才补跑文件优先覆盖。
        //   不放进 _dynamicSignature —— 那会让"勾选一个组件"触发整条动态重建
        //   （RebuildDynamicTabs 里对全部插件重跑沙箱+编译，并且在重建期间把列表清空，
        //     实测现象是"面板直接变空"）。启用状态只影响"要不要编译这一个内置件"。
        private string _enabledSig = "";

        public WidgetSwitcher(ISettingsService settings, IClipboardService clipboardService, INoteService noteService)
        {
            _settings = settings;
            InitializeComponent();

            _clipboardWidget = new ClipboardHistoryWidget(clipboardService);
            _noteWidget = new NoteWidget(noteService, settings);
            _timerWidget = new TimerWidget();
            _calculatorWidget = new CalculatorWidget();
            _textAiWidget = new TextAiWidget();
            _webWidget = new WebViewWidget(settings);

            _tabs.Add(new WidgetTab { Key = "Clipboard", IconKey = "IconClipboard", LocKey = "WidgetTabs_Clipboard", Widget = _clipboardWidget });
            _tabs.Add(new WidgetTab { Key = "Note", IconKey = "IconNote", LocKey = "WidgetTabs_Notes", Widget = _noteWidget });
            _tabs.Add(new WidgetTab { Key = "Timer", IconKey = "IconTimer", LocKey = "WidgetTabs_Timer", Widget = _timerWidget });
            _tabs.Add(new WidgetTab { Key = "Calculator", IconKey = "IconCalc", LocKey = "WidgetTabs_Calculator", Widget = _calculatorWidget });
            _tabs.Add(new WidgetTab { Key = "TextAi", IconKey = "IconAi", LocKey = "WidgetTabs_TextAi", Widget = _textAiWidget });
            _tabs.Add(new WidgetTab { Key = "Web", IconKey = "IconWeb", LocKey = "WidgetTabs_Web", Widget = _webWidget });

            // ★ 用户安装的 C# 插件小组件：每个成为一个标签（编译失败跳过）
            // ★ 重建动态标签（末尾会跑 ApplyBuiltinFileOverrides：已迁移的内置件从文件夹加载，失败回退 exe 实现）
            RebuildDynamicTabs();
            // ★ 签名立即初始化：避免首次激活时因 _dynamicSignature 为空而重复编译（同名程序集冲突）
            _dynamicSignature = BuildPluginSignature();
            // ★ 启用集合签名也立即初始化：构造时 RebuildDynamicTabs 已跑过一次覆盖，
            //   不记下来的话第一次 SettingsChanged 会白跑一次覆盖。
            _enabledSig = BuildEnabledSignature();

            // 小组件安装/删除：重建动态标签。
            // ★ 先失效签名缓存再比签名：Installed 已 Reload 出新列表，不失效会拿到旧签名
            //   误判"未变化"而漏建；失效后重算（SHA256 全源 ≈ 几 ms）很便宜，
            //   真正的重活（Roslyn 沙箱编译 + 插件编译）被 RebuildIfSignatureChanged 按签名挡住。
            WidgetPluginStore.Changed += () => Dispatcher.Invoke(() =>
            {
                _cachedPluginSignature = null;
                if (!RebuildIfSignatureChanged()) RebuildTabs();
            });

            // 设置变化（含小组件开关/海床保存小组件变体）时重建标签栏，不丢失各小组件内部状态。
            // ★ 性能：设置窗口任何控件变化都会触发 SettingsChanged，必须先比签名——
            //   旧实现无条件全量重建 = 每次对全部插件跑 Roslyn 沙箱编译（实测 2-4s UI 冻结）。
            //   签名未变（颜色/动画/帧率等无关设置）→ 只重建标签按钮（反映启停开关），不碰编译。
            _settings.SettingsChanged += () => Dispatcher.Invoke(() =>
            {
                _cachedPluginSignature = null;
                if (RebuildIfSignatureChanged()) return;   // 插件列表/源码变了：内部已重跑覆盖 + 重建标签

                // ★ 只是启用状态变了（勾选/取消某个组件）：**不**走全量重建，只补跑文件优先覆盖。
                //   为什么要补跑：ApplyBuiltinFileOverrides 会跳过未启用标签，所以某个内置件被
                //   重新启用时它还挂着 exe 内置实例 —— 不补跑，用户就会看到 exe 版，
                //   以为自己在文件夹里的改动失效了。
                //   为什么要比签名：SettingsChanged 对颜色/动画/帧率等无关设置也会触发，
                //   无条件重跑会让拖动滑块每次多花几十毫秒（虽是缓存命中）。
                string es = BuildEnabledSignature();
                if (es != _enabledSig)
                {
                    _enabledSig = es;
                    ApplyBuiltinFileOverrides();
                }

                // ★ 设置变化不改面板尺寸（见 _suppressSizeNotify）；但当前标签若因被停用而换掉了，
                //   那必须重新测量 —— 这时手动补一次通知。
                string tabBefore = _currentTab;
                _suppressSizeNotify = true;
                try { RebuildTabs(); }
                finally { _suppressSizeNotify = false; }
                if (tabBefore != _currentTab)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        new Action(() => ContentSizeChanged?.Invoke()),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            });

            // ★ 标签栏自动滚动：鼠标移到左/右边缘自动滚动（与任务栏一致）
            _tabScrollHandler = new TaskbarScrollHandler(TabScroll, "小组件标签", isHorizontal: true);

            _currentTab = _settings.LastWidgetTab;
            if (!_tabs.Any(t => t.Key == _currentTab && IsTabEnabled(t)))
            {
                _currentTab = _tabs.FirstOrDefault(IsTabEnabled)?.Key ?? "";
            }
            RebuildTabs();
        }

        /// <summary>上一次从 seabed 文件夹编译成功的内置件实例（id → 实例）。
        /// 用于"改到一半语法错"时保留上一次可用的版本，而不是立刻退回 exe 内置实现（否则用户其它改动凭空消失）。</summary>
        private readonly Dictionary<string, IWidget> _lastGoodBuiltin = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 内置件「文件优先」：已迁移的 id（WidgetPluginStore.MigratedBuiltinIds）改为**从 seabed 文件夹**编译加载，
        /// 让"改文件夹里的文件就生效"成立（原先内置件是编译进 exe、由宿主 new 出来的，文件夹里那份只作展示）。
        /// 失败回退顺序：本次编译失败但曾经加载成功 → 保留上一份可用实例；从未成功 → 回退 exe 内置实现。
        /// </summary>
        private void ApplyBuiltinFileOverrides()
        {
            if (PluginRuntimeGuard.SafeMode) return;   // 安全模式：不覆盖内置件，直接跑 exe 内置实现
            // ★ 内置件"文件优先"要在启动时现场跑一次 Roslyn 编译 —— 这是启动卡顿的主要嫌疑，先量出来
            var swBuiltin = System.Diagnostics.Stopwatch.StartNew();
            // ★ 诊断用：文件优先是否真的跑到、哪些标签被判定为"已迁移"（排查"改了文件夹没反应"时先看这一行）
            var migrated = _tabs.Where(t => WidgetPluginStore.MigratedBuiltinIds.Contains(t.Key.ToLowerInvariant())).ToList();
            // ★ 关掉的标签不编译：每个组件是几百 ms 的 Roslyn 编译，而用户根本看不到它。
            //   真机实测：web 一直关着（WidgetEnabled_Web=false），却白算了 1.2s。
            //   当前标签必然是启用的（构造函数与 SwitchTab 都按 IsTabEnabled 选），所以跳过未启用标签
            //   不会让任何"会渲染的组件"退回 exe 实现。
            //   ★★ 但这条跳过**必须**与 BuildPluginSignature 里"启用状态也算签名"配套 ——
            //   否则之后启用时没有任何路径会补跑覆盖，用户会看到 exe 版、以为文件夹里的改动失效。
            var todo = migrated.Where(IsTabEnabled).ToList();
            LogManager.Debug($"[内置件] 文件优先检查：标签 {_tabs.Count} 个 / 已迁移 {migrated.Count} 个" +
                             (migrated.Count > 0 ? "（" + string.Join(",", migrated.Select(t => t.Key)) + "）" : "") +
                             (todo.Count != migrated.Count
                                 ? $" / 待编译 {todo.Count} 个（跳过 {migrated.Count - todo.Count} 个未启用）"
                                 : ""));
            foreach (var tab in todo)
            {
                string id = tab.Key.ToLowerInvariant();          // 内置标签 Key 是 "Timer"，文件夹 id 是 "timer"
                if (!WidgetPluginStore.MigratedBuiltinIds.Contains(id)) continue;

                var plugin = WidgetPluginStore.GetById(id);
                // ★ 形态分流必须与下面的 RebuildDynamicTabs 同口径：内置件模板既有 XAML 形态
                //   （<id>.xaml + <id>.xaml.cs），也有纯 C# 单文件形态（main.cs，如 web）。
                //   旧实现只认 XAML 形态 → 纯 C# 形态的内置件**永远回退 exe 内置实现**，
                //   "在文件夹里改它"其实是假的（2026-09 真机实测：web 一直回退，日志只有这行 WRN）。
                bool xamlForm = plugin != null && !string.IsNullOrEmpty(plugin.Xaml) && !string.IsNullOrEmpty(plugin.XamlCs);
                bool csForm = plugin != null && !string.IsNullOrEmpty(plugin.Source);
                if (!xamlForm && !csForm)
                {
                    LogManager.Warning($"[内置件] {id}：seabed 文件夹里找不到可加载的形态（既无 .xaml+.xaml.cs，也无 main.cs），回退 exe 内置实现");
                    WidgetPluginStore.SetLoadIssue(id, "文件夹里没有可加载的形态，正在用 exe 内置实现");
                    continue;
                }
                var p = plugin!;

                // ★ 与普通插件/海床变体同口径的沙箱闸门（见本文件插件与变体两处的 `!TrustedSource` 判定）。
                //   以前这条内置件分支**完全不过闸门** —— 只要 seabed 里出现一个 id 为 timer 的目录，
                //   它的 .xaml.cs 就会被直接编译执行，而 `MigratedBuiltinIds` 只按 id 匹配。
                //   外来包占据同名目录（解包/手放）即可借这条路免检。
                if (!p.TrustedSource)
                {
                    // ★ 沙箱必须检查"真正会被编译的那份文本"（与 RebuildDynamicTabs 同口径）
                    string sandboxErr = WidgetCompiler.SandboxErrors(
                        xamlForm ? p.XamlCs : p.Source,
                        xamlForm ? p.Xaml : "");
                    if (sandboxErr.Length > 0)
                    {
                        LogManager.Warning($"[内置件] {id}：文件夹版本未过沙箱，回退 exe 内置实现 —— {sandboxErr}");
                        WidgetPluginStore.SetLoadIssue(id, "被沙箱拦截 · 正在用 exe 内置实现 · " + sandboxErr);
                        continue;
                    }
                }

                // ★ 单个组件的编译耗时单独量出来：总量日志出现过"5052ms，但各组件间隔只数得出 1.3s"
                //   的矛盾 —— 不逐项计时就无法判断这 5 秒到底花在 Roslyn、还是花在别处。
                var swOne = System.Diagnostics.Stopwatch.StartNew();
                var (widget, err) = xamlForm
                    ? WidgetCompiler.CompileXaml("builtin_" + id, p.Xaml, p.XamlCs)
                    : WidgetCompiler.Compile("builtin_" + id, p.Source);
                swOne.Stop();
                if (widget == null)
                {
                    // ★ 保留上一次从文件夹加载成功的版本：编辑器里存了一个语法错，不该让组件弹回 exe 那份老实现
                    if (_lastGoodBuiltin.TryGetValue(id, out var last))
                    {
                        tab.Widget = last;
                        LogManager.Warning($"[内置件] {id}：本次编译失败，已保留上一次可用的文件夹版本（改回正确语法会自动恢复）—— {err}");
                        WidgetPluginStore.SetLoadIssue(id, "编译失败 · 正在用上一次可用的版本 · " + err);
                    }
                    else
                    {
                        LogManager.Warning($"[内置件] {id}：从文件夹编译失败，回退 exe 内置实现 —— {err}");
                        WidgetPluginStore.SetLoadIssue(id, "编译失败 · 正在用 exe 内置实现 · " + err);
                    }
                    continue;
                }
                _lastGoodBuiltin[id] = widget;
                tab.Widget = widget;
                PluginRuntimeGuard.RegisterAssembly(widget.GetType().Assembly, tab.Key);
                WidgetPluginStore.ClearLoadIssue(id);
                // ★ 页脚走可选接口 IWidgetFooter：文件夹里那份如果还是旧代码（没实现该接口），
                //   内置件的页脚会莫名其妙消失 —— 这行日志就是给这种"看起来没事但少了一块"的问题用的。
                LogManager.Info($"[内置件] {id} 已从 seabed 文件夹加载（改文件夹里的文件即生效）" +
                                $" · IWidgetFooter={(widget is IWidgetFooter ? "有" : "无")}" +
                                $" · 本件耗时 {swOne.ElapsedMilliseconds}ms");
            }
            if (todo.Count > 0 || swBuiltin.ElapsedMilliseconds > 200)
                LogManager.Debug($"[内置件] 文件优先覆盖耗时 {swBuiltin.ElapsedMilliseconds}ms（迁移 {migrated.Count} 个 / 实编译 {todo.Count} 个）");
        }

        /// <summary>新版本不可用时保留上一个可用实例（找不到就什么都不做，组件按失败处理）。</summary>
        private void KeepPreviousTab(Dictionary<string, WidgetTab> previous, string key, string reason)
        {
            if (previous.TryGetValue(key, out var old))
            {
                _tabs.Add(old);
                LogManager.Warning($"[插件] {key} {reason} —— 已保留上一次可用的版本（界面上不会消失）");
            }
        }

        /// <summary>重建用户 C# 插件小组件标签（安装/删除后调用，内置标签保留）。</summary>
        private void RebuildDynamicTabs()
        {
            // ★ 安全模式：不加载任何海床插件/变体（只留 exe 内置标签），这是"插件把界面拖死"的唯一自救通道
            if (PluginRuntimeGuard.SafeMode)
            {
                _tabs.RemoveAll(t => t.Key.StartsWith("Widget_", StringComparison.Ordinal) || t.Key.StartsWith("Seabed_", StringComparison.Ordinal));
                return;
            }
            // ★ 重建前留存上一版动态标签：新版本不可用（编译失败/被沙箱拦截）时退回上一版实例 ——
            //   否则"在编辑器里保存了一个语法错误"会让组件直接从界面上消失，只留一行日志，用户不知道发生了什么。
            var previous = _tabs.Where(t => t.Key.StartsWith("Widget_", StringComparison.Ordinal))
                                .ToDictionary(t => t.Key, t => t, StringComparer.Ordinal);
            _tabs.RemoveAll(t => t.Key.StartsWith("Widget_", StringComparison.Ordinal));
            foreach (var plugin in WidgetPluginStore.Installed)
            {
                // ★ 只把 Widget 类当作小组件标签：Panel/Config/Category 是区域面板/配置项，
                //   编译成标签既错（面板功能出现在小组件栏）又白耗 Roslyn 编译（每次激活/设置变更全量跑）。
                //   旧文件夹项（无 manifest，Kind 为空）是历史小组件，照常作为标签。
                  if (plugin.Kind is "Panel" or "Config" or "Category" or "StatusProvider" or "Animation") continue;
                // ★ 内置件（manifest system:true，且已列入 MigratedBuiltinIds 而真正加载的）
                //   由内置标签渲染（见 ApplyBuiltinFileOverrides），这里不再建同名动态标签 ——
                //   否则"计时器"会同时出现两个标签。
                if (plugin.IsBuiltin) continue;
                // ★ 运行时守卫：安全模式/已熔断的插件一律不加载
                if (PluginRuntimeGuard.IsDisabled(plugin.Id)) { WidgetPluginStore.SetLoadIssue(plugin.Id, "已被安全熔断停用（可在托盘菜单解除）"); continue; }
                // ★ 沙箱只对市场来源（TrustedSource=false）执行：本地编程不检测（HANDOFF 设计），
                //   内置模板构建时已验证且合理使用黑名单 API（如 panel-recent 的 Process.Start/FileInfo），
                //   无条件沙箱会把它们全拦掉（2026-08 误杀回归）且每次重建白跑 Roslyn 编译。
                //   结果已按源码哈希缓存（SandboxErrors），未变化源码的重复重建零成本。
                // ★ 形态分流：XAML 形态（.xaml + .xaml.cs）走 CompileXaml；否则纯代码 Compile
                //   ★★ v2 修复：沙箱必须检查「真正会被编译/解析的那份文本」——
                //   旧实现只扫 plugin.Source，XAML 形态（甚至没有 main.cs）的 .xaml.cs 完全不检查，
                //   实测可编译并执行任意代码；带诱饵 main.cs 时更是"检查 A 运行 B"。
                bool xamlForm = !string.IsNullOrEmpty(plugin.Xaml) && !string.IsNullOrEmpty(plugin.XamlCs);
                // ★★ v2 策略（已修正）：XAML 形态**对外来来源同样开放**，但必须通过"受限 XAML 方言"校验
                //   （结构化白名单：只允许 WPF 界面元素/布局属性/受限标记扩展；ObjectDataProvider、x:Code、
                //    自定义类型命名空间、MediaElement/Hyperlink 等一律拒绝）。安全靠"结构上只能拼界面"，
                //   而不是一刀切禁用 —— 完全编程是海床的核心能力，市场包同样需要它。
                if (!plugin.TrustedSource)
                {
                    string sandboxErr = WidgetCompiler.SandboxErrors(
                        xamlForm ? plugin.XamlCs : plugin.Source,
                        xamlForm ? plugin.Xaml : "");
                    if (sandboxErr.Length > 0)
                    {
                        ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                            $"小组件 [{plugin.Id}] 被沙箱拦截: {sandboxErr}");
                        WidgetPluginStore.SetLoadIssue(plugin.Id, "被沙箱拦截 · " + sandboxErr);
                        KeepPreviousTab(previous, "Widget_" + plugin.Id, "被沙箱拦截");
                        continue;
                    }
                }
                var (widget, err) = xamlForm
                    ? WidgetCompiler.CompileXaml(plugin.Id, plugin.Xaml, plugin.XamlCs)
                    : WidgetCompiler.Compile(plugin.Id, plugin.Source);
                if (widget != null)
                {
                    _tabs.Add(new WidgetTab
                    {
                        Key = "Widget_" + plugin.Id,
                        IconKey = "IconApp",
                        LocKey = "",
                        Widget = widget
                    });
                      PluginRuntimeGuard.RegisterAssembly(widget.GetType().Assembly, plugin.Id);
                      WidgetPluginStore.ClearLoadIssue(plugin.Id);
                      ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[插件] 小组件编译成功: " + plugin.Id);
                }
                else
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                        $"小组件 [{plugin.Id}] 编译失败: {err}");
                    WidgetPluginStore.SetLoadIssue(plugin.Id, "编译失败 · " + err);
                    KeepPreviousTab(previous, "Widget_" + plugin.Id, "编译失败");
                }
            }
            // ★ 海床保存的小组件变体（BaseType=Widget）：编译后作为标签加入
            var previousSeabed = _tabs.Where(t => t.Key.StartsWith("Seabed_", StringComparison.Ordinal))
                                      .ToDictionary(t => t.Key, t => t, StringComparer.Ordinal);
            _tabs.RemoveAll(t => t.Key.StartsWith("Seabed_", StringComparison.Ordinal));
            foreach (var cp in _settings.CustomPanels)
            {
                if (cp.Kind == "Config" || (cp.BaseType ?? "") != "Widget") continue;
                if (string.IsNullOrWhiteSpace(cp.Source)) continue;
                // ★ 把变体名注入源码（模板 Name 写死，编译前替换为变体实际名字）
                string src = WidgetCompiler.InjectWidgetName(cp.Source, cp.Name);
                // ★ 沙箱：市场来源（TrustedSource=false）先拦截危险 API，恶意代码编译不过
                if (!cp.TrustedSource)
                {
                    string sandboxErr = WidgetCompiler.SandboxErrors(src);
                    if (sandboxErr.Length > 0)
                    {
                        ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                            $"海床小组件 [{cp.Name}] 市场来源被沙箱拦截: {sandboxErr}");
                        WidgetPluginStore.SetLoadIssue(cp.Id, "被沙箱拦截 · " + sandboxErr);
                        KeepPreviousTab(previousSeabed, "Seabed_" + cp.Id, "被沙箱拦截");
                        continue;
                    }
                }
                var (widget, err) = WidgetCompiler.Compile("seabed_" + cp.Id, src);
                if (widget != null)
                {
                    _tabs.Add(new WidgetTab
                    {
                        Key = "Seabed_" + cp.Id,
                        IconKey = "IconApp",
                        LocKey = "",
                        Widget = widget
                    });
                    WidgetPluginStore.ClearLoadIssue(cp.Id);
                }
                else
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                        $"海床小组件 [{cp.Name}] 编译失败: {err}");
                    WidgetPluginStore.SetLoadIssue(cp.Id, "编译失败 · " + err);
                    KeepPreviousTab(previousSeabed, "Seabed_" + cp.Id, "编译失败");
                }
            }
            // ★ 每次重建都重跑内置件覆盖：用户在文件夹里改了内置件 → 签名变化 → 走到这里 → 重新编译并换掉实例
            //   （不重跑的话只有重启才会生效，与"文件夹即真相源"不符）
            ApplyBuiltinFileOverrides();
        }

        /// <summary>插件签名：id + 源码哈希（源码编辑保存后也会触发重建）。带缓存，插件变化时失效。
        /// ★ 包含海床小组件变体（CustomPanels Kind=Widget），保证保存后激活时重建标签。</summary>
        private string BuildPluginSignature()
        {
            _cachedPluginSignature ??= string.Join(",",
                WidgetPluginStore.Installed
                    // ★ 必须按**实际会被编译的形态**取文本：XAML 形态插件的 Source 是空的，
                    //   只哈希 Source 会导致"改了 .xaml/.xaml.cs 签名不变 → 不重建"（运行中改文件不生效）。
                    //   与信任哈希（WidgetPluginStore.ContentHash）同一口径。
                    .Select(p => "W:" + p.Id + ":" + ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(
                        p.Source + "\u0001" + p.Xaml + "\u0002" + p.XamlCs))
                    .Concat(
                        _settings.CustomPanels
                            .Where(cp => cp.Kind != "Config" && (cp.BaseType ?? "") == "Widget")
                            .Select(cp => "B:" + cp.Id + ":" + ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(cp.Source))
                        .Concat(new[] { "G:" + PluginRuntimeGuard.SafeMode + ":" + string.Join("|", PluginRuntimeGuard.TrippedKeys) })));
            return _cachedPluginSignature;
        }

        /// <summary>
        /// 迁移内置件的「启用集合」签名（Key:1/0）。
        /// ★ **故意不并进 BuildPluginSignature**：那会让"勾选一个组件"触发整条动态重建
        ///   （RebuildDynamicTabs 对全部插件重跑沙箱+编译，且重建期间列表被清空 ——
        ///     实测现象就是"启用某个组件后面板直接变空"）。启用状态只决定
        ///   "这一个内置件要不要编译"，所以用一条独立签名只驱动文件优先覆盖。
        /// </summary>
        private string BuildEnabledSignature() => string.Join(",",
            _tabs.Where(t => WidgetPluginStore.MigratedBuiltinIds.Contains(t.Key.ToLowerInvariant()))
                 .OrderBy(t => t.Key, StringComparer.Ordinal)
                 .Select(t => t.Key + ":" + (_settings.IsWidgetEnabled(t.Key) ? "1" : "0")));

        /// <summary>
        /// 签名变化时重建动态标签（含沙箱/编译）；未变化返回 false（跳过昂贵的全量重建）。
        /// ★ 性能关键：SettingsChanged 对无关设置（颜色/动画/帧率…）也触发，签名未变时
        ///   全量重建 = 对全部插件重复 Roslyn 编译（每次 2-4s UI 冻结），必须按签名跳过。
        /// </summary>
        private bool RebuildIfSignatureChanged()
        {
            string sig = BuildPluginSignature();
            if (sig == _dynamicSignature) return false;
            _dynamicSignature = sig;
            RebuildDynamicTabs();
            RebuildTabs();
            return true;
        }

        /// <summary>划词翻译 小组件实例（供主窗口热键处理调用）。</summary>
        /// <summary>当前「划词翻译」标签用的实例（可能是文件夹版）—— 宿主热键必须操作屏幕上这一份。</summary>
        public ITextAiWidget? TextAiWidget => _tabs.FirstOrDefault(t => t.Key == "TextAi")?.Widget as ITextAiWidget;

        /// <summary>当前小组件标签内容变化（切换 tab / 激活）：面板据此重新自适应尺寸。</summary>
        public event Action? ContentSizeChanged;

        /// <summary>当前启用的标签键列表（供外部校验）。</summary>
        public IReadOnlyList<string> EnabledKeys => _tabs.Where(IsTabEnabled).Select(t => t.Key).ToList();

        /// <summary>各标签上次的稳定测量（内容未就绪时复用，保证测量确定性）。</summary>
        private readonly Dictionary<string, (double width, double height)> _stableSizeByTab = new();

        /// <summary>
        /// 测量当前小组件内容的理想尺寸（DIP）。
        /// 直接测量 ContentContainer.Content（真正的剪贴板/便签/计时器等控件），
        /// 绕开外层 ScrollViewer 的视口限制——否则面板被量窄（ScrollViewer 对
        /// 无限空间测量返回视口宽而非内容宽）。
        /// ★ 测量前先 UpdateLayout 强制一次布局：内容刚换入时模板未应用、
        ///   DesiredSize=0 → 测出保底小尺寸（"有时窄"）；布局就绪后测量结果确定。
        ///   （与 WPF SizeToContent 同思路：布局就绪后再取 DesiredSize；
        ///   注意方向：只能"先布局后测量"——测量后绝不能再 UpdateLayout，
        ///   否则 DesiredSize 会被当前面板约束覆盖成小尺寸）
        /// </summary>
        public (double width, double height)? MeasureContentSize()
        {
            // ★ WebView2 标签：独立进程渲染，WPF Measure 无法得到有效内容尺寸（返回 0/异常值导致面板变小/消失）
            //   → 用固定面板尺寸（网页自适应，用户可手动拖面板大小）
            if (_currentTab == "Web")
            {
                return (480, 360);
            }
            try
            {
                if (ContentContainer.Content is System.Windows.FrameworkElement fe)
                {
                    fe.UpdateLayout();

                    // ★ 测量宽度用**真实可用宽度**，不用 ∞。
                    //   诊断证据（2026-09-13：加了一行日志把"量到的究竟是谁"打出来才查到）：
                    //   反复出现的 `1765x333` 既不是上一份内容、也不是任务栏 —— 就是剪贴板组件**自己**，
                    //   它的期望宽度被"历史里最长的那条剪贴板文本"撑到 1765（超过工作区宽的 1707）。
                    //   用 ∞ 宽测量列表/长文本类内容有两重错：
                    //     ① 面板宽度被最长的那条文本决定（再来一条更长的就更宽，没有上界）；
                    //     ② 高度是"单行高度"，不是换行后真正需要的高度。
                    //   改用可用宽度约束后：窄组件（计时器等）照样报自己的自然宽度
                    //   （DesiredSize 是"想要多少"，不是"给了多少"），宽内容则在可用宽度内换行/滚动。
                    //   最后再 clamp 一次是必要的：WPF 允许 DesiredSize 突破给定约束（NoWrap 长文本、固定 Width）。
                    double maxWidth = System.Windows.SystemParameters.WorkArea.Width;
                    fe.Measure(new Size(maxWidth, double.PositiveInfinity));
                    double rawWidth = fe.DesiredSize.Width;
                    double w = Math.Min(rawWidth, maxWidth);
                    double h = fe.DesiredSize.Height;

                    if (rawWidth > maxWidth)
                    {
                        LogManager.Debug($"[测量] 内容想要 {rawWidth:F0} 宽，超过可用 {maxWidth:F0}，已按可用宽度计" +
                                         $" · 标签={_currentTab} · 对象={fe.GetType().Name}");
                    }

                    if (w >= 10 && h >= 10)
                    {
                        _stableSizeByTab[_currentTab] = (w, h);
                        return (w, h);
                    }
                }
            }
            catch
            {
                // 刻意不记日志：在测量热路径上（每次切标签都走），且下面立刻有"上次稳定尺寸/null"的确定性回退
            }
            // ★ 内容未就绪：返回该标签上次的稳定测量（确定性）；无历史则 null（调用方保底）
            return _stableSizeByTab.TryGetValue(_currentTab, out var s) ? s : null;
        }

        private bool IsTabEnabled(WidgetTab tab) =>
            _settings.IsWidgetEnabled(tab.Key)
            && !PluginRuntimeGuard.IsDisabled(tab.Key)
            && !PluginRuntimeGuard.IsDisabled(GuardKeyOf(tab.Key));

        /// <summary>标签 Key（Widget_xx / Seabed_xx / 内置名）→ 守卫登记用的插件 id。</summary>
        private static string GuardKeyOf(string tabKey)
        {
            if (tabKey.StartsWith("Widget_", StringComparison.Ordinal)) return tabKey.Substring("Widget_".Length);
            if (tabKey.StartsWith("Seabed_", StringComparison.Ordinal)) return tabKey.Substring("Seabed_".Length);
            return tabKey;
        }

        /// <summary>切换到指定标签（外部调用，如划词热键跳转到 TextAi）。</summary>
        public void SelectTab(string tab)
        {
            if (!_tabs.Any(t => t.Key == tab && IsTabEnabled(t))) return;
            if (_currentTab == tab) return;

            DeactivateCurrent();
            _currentTab = tab;
            _settings.LastWidgetTab = tab;
            ApplyTabButtonStyles();
            ShowContent();
            // ★ 内容切换后通知面板重新测量自适应（内容高度可能变化）
            ContentSizeChanged?.Invoke();
        }

        private void RebuildTabs()
        {
            if (_rebuilding) return;
            _rebuilding = true;
            try
            {
                ButtonPanel.Children.Clear();

                var enabled = _tabs.Where(IsTabEnabled).ToList();
                EmptyHint.Visibility = enabled.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                foreach (var tab in enabled)
                {
                    ButtonPanel.Children.Add(CreateTabButton(tab));
                }

                if (enabled.Count == 0)
                {
                    DeactivateCurrent();
                    ContentContainer.Content = null;
                    FooterPanel.Child = null;
                    return;
                }

                // 当前标签被关闭时回退到第一个启用标签
                if (!enabled.Any(t => t.Key == _currentTab))
                {
                    _currentTab = enabled[0].Key;
                    _settings.LastWidgetTab = _currentTab;
                }

                ApplyTabButtonStyles();
                ShowContent();
            }
            finally
            {
                _rebuilding = false;
            }
        }

        private Button CreateTabButton(WidgetTab tab)
        {
            var btn = new Button
            {
                Height = 28,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = ButtonPanel.Children.Count == 0 ? new Thickness(0) : new Thickness(8, 0, 0, 0),
                Tag = tab.Key
            };
            btn.Click += (_, _) => SelectTab(tab.Key);

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Path
            {
                Style = (Style)FindResource("LineIcon"),
                Data = (Geometry)FindResource(tab.IconKey),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            if (string.IsNullOrEmpty(tab.LocKey))
            {
                label.Text = tab.Widget.Name; // 动态小组件：名称来自配置
            }
            else
            {
                label.SetBinding(TextBlock.TextProperty,
                    new Binding("Item[" + tab.LocKey + "]") { Source = LocalizationManager.Instance });
            }
            sp.Children.Add(label);

            btn.Content = sp;
            return btn;
        }

        private void ApplyTabButtonStyles()
        {
            foreach (var child in ButtonPanel.Children)
            {
                if (child is Button b && b.Tag is string key)
                {
                    b.Style = (Style)FindResource(key == _currentTab ? "AccentButton" : "FlatButton");
                }
            }
        }

        private void ShowContent()
        {
            // ★ 动态小组件标签：Widget_<id>（插件）与 Seabed_<id>（海床变体）
            if (_currentTab.StartsWith("Widget_", StringComparison.Ordinal) ||
                _currentTab.StartsWith("Seabed_", StringComparison.Ordinal))
            {
                var tab = _tabs.FirstOrDefault(t => t.Key == _currentTab);
                if (tab != null)
                {
                    ContentContainer.Content = tab.Widget;
                    tab.Widget.OnActivated();
                    FooterPanel.Child = null;
                    return;
                }
            }

            // ★ 内置标签也统一走「标签里的实例」（tab.Widget），不再直接引用具体字段：
            //   · 这样"从 seabed 文件夹加载的内置件"（见 ApplyBuiltinFileOverrides）才真正生效；
            //   · 内置件与外来插件从此走**同一条渲染路径**，去掉"宿主按名字认识内置件"的特判；
            //   · 页脚走可选接口 IWidgetFooter（不再调具体类的 GetFooterControl）。
            var cur = _tabs.FirstOrDefault(t => t.Key == _currentTab)
                      ?? _tabs.FirstOrDefault(t => t.Key == "Calculator");   // 兜底与原 default 分支一致
            if (cur != null)
            {
                ContentContainer.Content = cur.Widget.CreateView();
                cur.Widget.OnActivated();
                FooterPanel.Child = (cur.Widget as IWidgetFooter)?.GetFooterControl();
            }

            // ★ 内容切换后延迟通知面板重测尺寸：布局完成后再测量，
            //   避免"从 AI 划到小组件时用未布局的保底尺寸"导致面板过小。
            //   ★ 但设置驱动的重建会临时抑制这次通知（详见 _suppressSizeNotify 的说明）——
            //   在"排定"这一刻判断，不能靠回调执行时再判断（那时标志早已被清掉）。
            if (_suppressSizeNotify) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                new Action(() => ContentSizeChanged?.Invoke()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public new string Name => ShoreHue.UI.Localization.LocalizationManager.Instance["Widget_GroupName"];

        public UserControl CreateView() => this;

        public void OnActivated()
        {
            // ★ 插件列表/源码变化（保存时 WidgetSwitcher 可能尚未创建，订阅丢失）：
            //   每次面板激活时对比签名（含源码哈希），变化则重建动态标签，保证新增/修改的小组件一定出现
            RebuildIfSignatureChanged();

            // ★ 与内容切换同一口径：激活当前标签的实例（内置件与插件一致）
            _tabs.FirstOrDefault(t => t.Key == _currentTab)?.Widget.OnActivated();
        }

        public void OnDeactivated()
        {
            DeactivateCurrent();
        }

        private void DeactivateCurrent()
        {
            // ★ 不再区分"内置/插件"（原先插件走标签查找、内置走 5 个硬编码 case）：
            //   当前标签的实例自己负责收尾，内置件从文件夹加载后也走这条。
            _tabs.FirstOrDefault(t => t.Key == _currentTab)?.Widget.OnDeactivated();
        }
    }
}
