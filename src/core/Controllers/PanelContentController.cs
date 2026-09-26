using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.src.core.Services.System;
using ShoreHue.UI.AppHelper;
using ShoreHue.UI.Panels;
using ShoreHue.UI.Widgets;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ShoreHue.Core.Controllers
{
    public class PanelContentController
    {
        private readonly ContentControl _contentContainer;
        private readonly ISettingsService _settings;
        private readonly IShortcutService _shortcutService;
        private readonly INoteService _noteService;
        private readonly IClipboardService _clipboardService;
        private readonly IModeService _modeService;

        private IWidget? _currentWidget;
        private string _currentRegionType = "Taskbar";
        // ★ 四个角共用 regionType=Placeholder，内容由 regionKey 决定 —— 复用判断必须带上它，
        //   否则「哪个角先出来，另外三个角都显示它」（对角快速移动/直接跳过去时必现）。
        private string _currentRegionKey = "";
        // ★ 画中画实例缓存：呼出面板时复用，避免镜像/播放状态被重置
        private ShoreHue.UI.AppHelper.AppHelperView? _cachedAppHelper;
        private ShoreHue.UI.Widgets.WidgetSwitcher? _cachedWidgetSwitcher;
        private ShoreHue.UI.AI.AiChatView? _cachedAiChat;
        // ★ 任务栏视图缓存：贴边切换频繁触发 LoadContent，重建快捷方式布局是卡顿主因
        private ShoreHue.UI.Panels.TaskbarView? _cachedTaskbarView;
        // ★ 自定义面板实例缓存：id → 视图，编译一次复用（源码变化时由设置重载重建）
        private readonly System.Collections.Generic.Dictionary<string, FrameworkElement> _customPanelCache = new();
        private string _customPanelsSignature = "";

        // ★★★ 新增事件 ★★★
        public event Action? LoadingStarted;
        public event Action? LoadingCompleted;

        public event Action? ContentChanged;

        public string CurrentRegionType => _currentRegionType;

        /// <summary>当前缓存的小组件切换器（可能为 null，尚未创建）。</summary>
        public ShoreHue.UI.Widgets.WidgetSwitcher? WidgetSwitcher => _cachedWidgetSwitcher;

        /// <summary>
        /// 显示小组件面板并切换到指定标签（如划词热键跳转到 TextAi）。
        /// 面板当前是其他内容时强制切到小组件，标签不存在时保持当前标签。
        /// </summary>
        public void ShowWidgetTab(string tab)
        {
            LoadContentForRegion("Widget");
            _cachedWidgetSwitcher?.SelectTab(tab);
        }

        public PanelContentController(
            ContentControl contentContainer,
            ISettingsService settings,
            IShortcutService shortcutService,
            INoteService noteService,
            IClipboardService clipboardService,
            IModeService modeService)
        {
            _contentContainer = contentContainer;
            _settings = settings;
            _shortcutService = shortcutService;
            _noteService = noteService;
            _clipboardService = clipboardService;
            _modeService = modeService;
        }

        public void LoadContentForRegion(string regionType, string regionKey = "")
        {
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"LoadContent type={regionType} key={regionKey}");

            // ★ 同类型不重建：同一边内滑动（如 Left_Top → Left_Center）内容相同，
            //   直接复用当前实例，避免每次 new TaskbarView 导致的贴边切换卡顿。
            // ★ 但 Placeholder 是例外：四个角共用同一个 regionType、内容却由 regionKey 决定
            //   （左上快捷设置 / 左下最近使用 / 右下通知坞）—— 只比类型会让先出现的那个角占住全部四个角。
            bool sameContent = regionType == _currentRegionType
                && (regionType != "Placeholder" || regionKey == _currentRegionKey);
            if (sameContent && _currentWidget != null)
            {
                // ★ 隐藏时 OnPanelHidden 会把 ContentContainer.Content 清空（滑出动画后释放视觉树），
                //   同类型复用（隐藏后回到同一边）必须重新挂载缓存实例，否则面板空白
                if (_contentContainer.Content == null && _currentWidget is System.Windows.FrameworkElement cached)
                {
                    _contentContainer.Content = cached;
                    _currentWidget.OnActivated();
                    ContentChanged?.Invoke();
                }
                return;
            }

            _currentRegionType = regionType;
            _currentRegionKey = regionKey;

            // ★ 切换耗时定位：内容构建是同步跑在 UI 线程上的，构多慢动画就晚多久起帧
            //   （实测内置件文件化之后，Widget 首次激活有约 1.6 秒卡在 Roslyn 编译上）
            var swLoad = System.Diagnostics.Stopwatch.StartNew();

            string cacheNote = "";

            // ★★★ 通知开始加载 ★★★
            LoadingStarted?.Invoke();

            if (_currentWidget != null)
            {
                _currentWidget.OnDeactivated();
                _currentWidget = null;
            }

            FrameworkElement newContent;

            switch (regionType)
            {
                case "Taskbar":
                    // ★ 文件夹优先（panel-taskbar-feature）；宿主视图回退。切走再切回不重建（布局/窗口列表保持）
                    cacheNote = _cachedTaskbarView == null ? "构造" : "缓存";
                    newContent = PanelOrHost("panel-taskbar-feature", () =>
                    {
                        _cachedTaskbarView ??= new ShoreHue.UI.Panels.TaskbarView(_shortcutService, _settings);
                        return _cachedTaskbarView;
                    });
                    break;

                case "Widget":
                    // ★ 小组件实例缓存：呼出面板时保留计时/便签/剪贴板状态
                    cacheNote = _cachedWidgetSwitcher == null ? "构造(含Roslyn)" : "缓存";
                    if (_cachedWidgetSwitcher == null)
                    {
                        _cachedWidgetSwitcher = new WidgetSwitcher(_settings, _clipboardService, _noteService);
                        // ★ 小组件内部切标签 → 面板按新内容重新自适应尺寸（延迟到布局完成，测量更准确）
                        _cachedWidgetSwitcher.ContentSizeChanged += () =>
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                                () => ContentChanged?.Invoke(),
                                System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    var widgetSwitcher = _cachedWidgetSwitcher;
                    newContent = widgetSwitcher;
                    break;

                case "AppHelper":
                    cacheNote = _cachedAppHelper == null ? "构造" : "缓存";
                    newContent = PanelOrHost("panel-apphelper", () =>
                    {
                        _cachedAppHelper ??= new AppHelperView();
                        return _cachedAppHelper;
                    });
                    break;

                case "AI":
                    // ★ AI 助手面板缓存：保持对话状态。文件夹优先（panel-ai，薄封装在 OnActivated 刷设置）
                    cacheNote = _cachedAiChat == null ? "构造" : "缓存";
                    newContent = PanelOrHost("panel-ai", () =>
                    {
                        _cachedAiChat ??= new ShoreHue.UI.AI.AiChatView();
                        _cachedAiChat.RefreshSettings();
                        return _cachedAiChat;
                    });
                    break;

                case "WindowControl":
                    // ★ 右上角窗口操作中心（文件夹优先：panel-windowcontrol；不缓存：每次显示都刷新前台窗口信息）
                    newContent = PanelOrHost("panel-windowcontrol", () => new ShoreHue.UI.Widgets.WindowControlView());
                    break;

                case "Notification":
                    newContent = PanelOrHost("panel-notification", () => new NotificationDockView());
                    break;

                case "Recent":
                    newContent = PanelOrHost("panel-recent", () => new RecentItemsView());
                    break;

                case "QuickSettings":
                    newContent = PanelOrHost("panel-quicksettings", () => new QuickSettingsView(_settings));
                    break;

                case "Placeholder":
                    // ★ 四角分工：右下通知坞 / 左下最近使用 / 左上系统开关（都文件夹优先）
                    newContent = regionKey switch
                    {
                        "BottomLeft" => PanelOrHost("panel-recent", () => new RecentItemsView()),
                        "TopLeft" => PanelOrHost("panel-quicksettings", () => new QuickSettingsView(_settings)),
                        _ => PanelOrHost("panel-notification", () => new NotificationDockView())
                    };
                    break;

                default:
                    if (regionType.StartsWith("Custom:", StringComparison.Ordinal))
                    {
                        newContent = LoadCustomPanel(regionType);
                        break;
                    }
                    newContent = new NotificationDockView();
                    break;
            }

            long tBuild = swLoad.ElapsedMilliseconds;

            // ★★★ 应用内容 ★★★
            _contentContainer.Content = newContent;
            ContentChanged?.Invoke();

            // ★★★ 记录当前组件并激活（WidgetSwitcher 内部再管理自己的标签页） ★★★
            _currentWidget = newContent as IWidget;
            _currentWidget?.OnActivated();

            // ★★★ 通知加载完成 ★★★
            LoadingCompleted?.Invoke();

            // ★ 只在肉眼能感觉到的量级（≥50ms）才打：这段是同步阻塞 UI 线程的，直接等于动画晚起帧的时间
            long tAll = swLoad.ElapsedMilliseconds;
            if (tAll >= 50)
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                    $"[面板] 内容构建 {regionType}/{regionKey} 耗时 {tAll}ms（构建 {tBuild}ms / 挂载+激活 {tAll - tBuild}ms，{cacheNote}）");
        }

        /// <summary>
        /// 加载用户自定义面板：按 "Custom:面板Id" 从 CustomPanels 取源码，
        /// 用 WidgetCompiler 动态编译（实现 IWidget → CreateView()），实例缓存复用。
        /// 编译失败回退默认通知坞并写日志。
        /// </summary>
        private FrameworkElement LoadCustomPanel(string regionType)
        {
            string panelId = regionType.Substring("Custom:".Length);
            try
            {
                // 源码变化时清缓存（签名对比；★ 用哈希而非长度——同长不同内容会漏判导致陈旧缓存）
                //   ★ v2：签名必须覆盖 XAML 形态的全部文本，否则改 .xaml/.xaml.cs 不重建
                string sig = string.Join("|",
                    _settings.CustomPanels.Select(p =>
                        p.Id + ":" + ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(p.Source ?? "") +
                        ":" + ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(p.Xaml ?? "") +
                        ":" + ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(p.XamlCs ?? "")));
                if (sig != _customPanelsSignature)
                {
                    _customPanelsSignature = sig;
                    _customPanelCache.Clear();
                }

                if (_customPanelCache.TryGetValue(panelId, out var cached)) return cached;

                var cp = _settings.CustomPanels.FirstOrDefault(p => p.Id == panelId);
                if (cp == null) return new NotificationDockView();

                // ★ 形态分流：完全编程（.xaml + .xaml.cs）走 CompileXaml；否则纯代码
                bool xamlForm = !string.IsNullOrWhiteSpace(cp.Xaml) && !string.IsNullOrWhiteSpace(cp.XamlCs);
                // ★★ v2 策略（已修正）：XAML 形态对外来来源同样开放，但必须通过"受限 XAML 方言"校验
                //   （结构白名单，见 WidgetCompiler.CheckXamlDialect）—— 安全靠结构，不靠禁用。
                if (!xamlForm && string.IsNullOrWhiteSpace(cp.Source)) return new NotificationDockView();

                // ★ 沙箱：市场来源（TrustedSource=false）先拦截危险 API。
                //   ★★ v2 修复：必须扫「真正会被编译/解析的文本」——XAML 形态是 .xaml.cs + 标记文本，
                //   旧实现只扫 Source（XAML-only 包直接空串放行）。
                if (!cp.TrustedSource)
                {
                    string sandboxErr = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SandboxErrors(
                        xamlForm ? (cp.XamlCs ?? "") : (cp.Source ?? ""),
                        xamlForm ? (cp.Xaml ?? "") : "");
                    if (sandboxErr.Length > 0)
                    {
                        ShoreHue.Core.Infrastructure.Logging.LogManager.Error(
                            $"自定义面板 [{cp.Name}] 市场来源被沙箱拦截: {sandboxErr}");
                        // ★ 面板被拦时界面只是"退化成通知坞"，用户完全看不出原因 —— 记进加载问题，海床页会显示
                        ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(cp.Id, "被沙箱拦截 · " + sandboxErr);
                        return new NotificationDockView();
                    }
                }
                var (widget, err) = xamlForm
                    ? ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.CompileXaml("panel_" + cp.Id, cp.Xaml ?? "", cp.XamlCs ?? "")
                    : ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.Compile("panel_" + cp.Id, cp.Source ?? "");
                if (widget == null)
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Error(
                        $"自定义面板 [{cp.Name}] 编译失败: {err}");
                    ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(cp.Id, "编译失败 · " + err);
                    return new NotificationDockView();
                }
                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ClearLoadIssue(cp.Id);

                var view = widget.CreateView();
                _customPanelCache[panelId] = view;
                return view;
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error(
                    "自定义面板加载异常", ex);
                return new NotificationDockView();
            }
        }

        // ============================================================
        //  内置面板：seabed 文件夹优先（"文件夹即真相源"）
        // ============================================================

        /// <summary>文件夹里有可用实现就用它，否则回退宿主视图。两个分支都返回可挂载的控件。</summary>
        private static FrameworkElement PanelOrHost(string panelId, Func<FrameworkElement> host)
            => TryLoadFolderPanel(panelId) ?? host();

        /// <summary>同一个面板只有内容变了才打 Info，避免每次呼出都刷一行日志。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> _panelLoadedHash = new();

        /// <summary>
        /// 从 seabed 文件夹加载内置面板：编译失败 / 被沙箱拦 / 没有可加载形态 → 返回 null，由调用方回退宿主视图
        /// （不丢功能，并把原因记进 WidgetPluginStore.LoadIssues，海床页可见）。
        ///
        /// ★ 复用 WidgetCompiler 的编译产物缓存（同内容不重复 Roslyn 编译）；面板实例由编译缓存复用，
        ///   所以文件夹里的面板必须在 Loaded/Unloaded 或 OnActivated/OnDeactivated 里订阅/退订事件，
        ///   不能只在构造函数里订阅（那时 Unloaded 退订后重挂不再订阅 → 面板"活着但不再更新"）。
        /// </summary>
        internal static FrameworkElement? TryLoadFolderPanel(string panelId)
        {
            try
            {
                // ★ 安全模式：不加载任何文件夹面板（回退宿主视图）
                if (ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.SafeMode) return null;
                var plugin = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.GetById(panelId);
                if (plugin == null) return null;   // 没有这个文件夹项（或未迁移）→ 宿主视图
                // ★ 运行时守卫：已熔断的面板不再加载
                if (ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.IsDisabled(panelId))
                {
                    ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(panelId, "已被安全熔断停用（可在托盘菜单解除）");
                    return null;
                }

                bool xamlForm = !string.IsNullOrEmpty(plugin.Xaml) && !string.IsNullOrEmpty(plugin.XamlCs);
                bool csForm = !string.IsNullOrEmpty(plugin.Source);
                if (!xamlForm && !csForm)
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                        $"[面板] {panelId}：seabed 文件夹里找不到可加载的形态，回退宿主视图");
                    ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(panelId, "文件夹里没有可加载的形态，正在用宿主实现");
                    return null;
                }

                // 与小组件/自定义面板同口径：只有外来来源才过沙箱（内置副本是本地受信代码）
                if (!plugin.TrustedSource)
                {
                    string sandboxErr = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SandboxErrors(
                        xamlForm ? plugin.XamlCs : plugin.Source,
                        xamlForm ? plugin.Xaml : "");
                    if (sandboxErr.Length > 0)
                    {
                        ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                            $"[面板] {panelId}：文件夹版本未过沙箱，回退宿主视图 —— {sandboxErr}");
                        ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(panelId, "被沙箱拦截 · 正在用宿主实现 · " + sandboxErr);
                        return null;
                    }
                }

                var (widget, err) = xamlForm
                    ? ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.CompileXaml("panel_" + panelId, plugin.Xaml, plugin.XamlCs)
                    : ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.Compile("panel_" + panelId, plugin.Source);
                if (widget == null)
                {
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                        $"[面板] {panelId}：从文件夹编译失败，回退宿主视图 —— {err}");
                    ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(panelId, "编译失败 · 正在用宿主实现 · " + err);
                    return null;
                }

                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ClearLoadIssue(panelId);

                // 内容变了才打 Info（"改了文件夹没反应"排查时先看这一行）
                string hash = ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SourceHash(
                    (plugin.Source ?? "") + "\u0001" + (plugin.Xaml ?? "") + "\u0002" + (plugin.XamlCs ?? ""));
                bool changed = !_panelLoadedHash.TryGetValue(panelId, out var last) || last != hash;
                _panelLoadedHash[panelId] = hash;
                if (changed)
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Info(
                        $"[面板] {panelId} 已从 seabed 文件夹加载（改文件夹里的文件即生效）");

                ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.RegisterAssembly(widget.GetType().Assembly, panelId);
                return widget.CreateView();
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error($"[面板] {panelId}：从文件夹加载异常，回退宿主视图", ex);
                ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetLoadIssue(panelId, "加载异常 · 正在用宿主实现 · " + ex.Message);
                return null;
            }
        }
    }
}
