using System;
using System.Collections.Generic;
using System.Threading;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Services.Configuration
{
    public class SettingsManager : ISettingsService, IPluginTrustStore, ISettingsHost, IService
    {
        private SettingsData _data;
        private readonly object _lock = new object();
        private bool _applyingPreset; // 应用性能预设期间不触发"自定义"检测

        // ★ 防抖落盘：拖动滑块等高频 set 时，内存即时更新，但落盘与 SettingsChanged
        //   合并为 300ms 一次，避免写盘风暴与 UI 全量刷新风暴。
        private Timer? _saveTimer;
        private bool _saveDirty;
        private const int SaveDebounceMs = 300;

        public event Action? SettingsChanged;

        public string Name => "SettingsManager";
        public bool IsInitialized { get; private set; } = false;

        /// <summary>
        /// ★ 本实例锁定的配置文件路径（构造时确定，之后不再变）。
        /// 为什么必须锁：落盘是 300ms 防抖的，定时器在**稍后**才跑；若那时才去解析
        /// `AppPaths.DataRoot`，一旦数据根变了（单元测试隔离目录在 Dispose 里被清空就是这样），
        /// 这次落盘就会写到**另一个文件**上去（实测踩过：测试把用户真实 config.json 覆盖成默认值）。
        /// 一个 SettingsManager 实例只应管一个配置文件。
        /// </summary>
        private readonly string _configPath = AppPaths.ConfigPath;

        public SettingsManager()
        {
            _data = SettingsFileManager.Load(_configPath);
            NormalizePanelKinds();
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            Reload();
            IsInitialized = true;
            LogManager.Debug("SettingsManager 初始化完成");
        }

        public void Shutdown()
        {
            if (!IsInitialized) return;
            FlushSaveNow(); // 关闭前强制落盘（防抖未触发时兜底）
            IsInitialized = false;
            LogManager.Debug("SettingsManager 已关闭");
        }

        /// <summary>宿主面：从磁盘重载（见 ISettingsHost）。</summary>
        public void Reload()
        {
            // ★ 刷新前先强制落盘内存中的待保存改动（防抖 300ms 内点刷新会丢改动：
            //   例如刚关掉引潮未落盘，Reload 读到旧值又恢复开启）。
            FlushSaveNow();
            lock (_lock)
            {
                _data = SettingsFileManager.Load(_configPath);
                NormalizePanelKinds();
                SettingsChanged?.Invoke();
            }
        }

        /// <summary>
        /// 统一自定义面板种类：BaseType=Widget → Kind=Widget（小组件变体，进小组件标签）；
        /// BaseType 为面板类型 → Kind=Panel（区域面板，进区域面板下拉）；其余不变。
        /// 修复旧版本（Kind 为空/Panel 混存）导致的位置错乱。
        /// </summary>
        private void NormalizePanelKinds()
        {
            if (_data.CustomPanels == null || _data.CustomPanels.Count == 0) return;
            bool changed = false;
            foreach (var p in _data.CustomPanels)
            {
                if (p.Kind == "Config" || p.Kind == "Category") continue;   // 配置代码项/新分类保持
                string bt = p.BaseType ?? "";
                if (bt == "Widget")
                {
                    if (p.Kind != "Widget") { p.Kind = "Widget"; changed = true; }
                }
                else if (!string.IsNullOrEmpty(bt) && bt != "Category")
                {
                    if (p.Kind != "Panel") { p.Kind = "Panel"; changed = true; }
                }
            }
            if (changed)
            {
                // 修好的 Kind 没落盘 → 下次启动又要重修（且界面分类可能不对）
                try { SettingsFileManager.Save(_data, _configPath); }
                catch (Exception ex) { LogManager.Error($"修复面板 Kind 后落盘失败：{ex.Message}", ex); }
            }
        }

        /// <summary>立即写入磁盘并通知设置变化（设置页实时保存入口）。</summary>
        public void SaveSettings()
        {
            Save();
        }

        /// <summary>
        /// 用一份完整的 SettingsData 替换内部数据并落盘。
        /// 设置窗口的 ApplyControlsToData 把控件值写入本地副本后调用此方法，
        /// 把副本整体同步进 SettingsManager（否则设置改动只改副本、不落盘，
        /// 刷新/重启后全部还原——曾导致"关掉引潮刷新又开"）。
        /// </summary>
        public void Apply(SettingsData data)
        {
            // ★ 设置窗口保存入口：整体替换数据并立即落盘 + 立即通知。
            //   调用方（SaveSettingsNow）已自带防抖，这里不再叠加 300ms 延迟，
            //   保证"保存即生效"且 SettingsChanged 同步触发。
            lock (_lock)
            {
                _data = data;
                _saveDirty = true;
                _saveTimer?.Dispose();
                _saveTimer = null;
            }
            try
            {
                bool saved;
                lock (_lock)
                {
                    saved = SettingsFileManager.Save(_data, _configPath);
                    // ★ 落盘成功后必须清脏标记。
                    //   否则 `_saveDirty` 会一直停在 true（Apply 不排定防抖定时器），
                    //   下一次 `Reload()` 里的 `FlushSaveNow()` 就会把**这份内存快照再写一遍**，
                    //   覆盖掉其间别的组件刚写进 config.json 的内容 ——
                    //   「应用整套预设 / 一键恢复 / 云端恢复」全是"先写文件、再 Reload"这一形态，
                    //   于是它们会静默失效（界面照样提示成功）。
                    //   落盘失败时保持脏，交给下一次防抖或 Shutdown 重试。
                    _saveDirty = !saved;
                }
                // ★ 这里**同步**通知（不调 NotifySettingsChanged 的 Dispatcher 封送）：
                //   ① 本方法的调用点全是 UI 线程（设置窗口保存、海床"应用预设/应用配置目录"），
                //      它自己的文档契约就是"保存即生效 + 同步触发"；
                //   ② 走封送的话，只要进程里**恰好存在别的 WPF Application**（单元测试进程里
                //      XAML 编译测试创建过 Application），通知就变成异步投递 → 调用方以为没通知，
                //      测试随执行顺序时绿时红（历史"偶发假红"的真正来源）。
                //   将来若真有人从后台线程调 Apply，跨线程异常会照常抛给订阅者并写进日志，不会静默。
                try { SettingsChanged?.Invoke(); }
                catch (Exception ex) { LogManager.Error("设置变更通知失败", ex); }
            }
            catch (Exception ex)
            {
                LogManager.Error("设置落盘失败", ex);
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                _saveDirty = true;
                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(_ => FlushSaveNow(), null, SaveDebounceMs, Timeout.Infinite);
                }
                else
                {
                    _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
                }
            }
        }

        // ========== 属性样板瘦身辅助 ==========

        /// <summary>简单赋值 + 自动保存（替代 4 行样板 setter）。</summary>
        private void SetField<T>(Action<T> setter, T value)
        {
            setter(value);
            Save();
        }

        /// <summary>钳制赋值 + 自动保存（替代"Math.Max/Min + Save"样板）。</summary>
        private void SetField(Action<int> setter, int value, int min, int max)
        {
            setter(Math.Max(min, Math.Min(max, value)));
            Save();
        }

        /// <summary>钳制赋值 + 自动保存（double 版）。</summary>
        private void SetField(Action<double> setter, double value, double min, double max)
        {
            setter(Math.Max(min, Math.Min(max, value)));
            Save();
        }

        /// <summary>防抖到期/关闭时：落盘一次并触发一次 SettingsChanged。</summary>
        private void FlushSaveNow()
        {
            bool shouldSave;
            lock (_lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
                shouldSave = _saveDirty;
                _saveDirty = false;
            }
            if (!shouldSave) return;
            try
            {
                bool saved;
                lock (_lock)
                {
                    saved = SettingsFileManager.Save(_data, _configPath);
                    // 落盘失败 → 重新置脏，下一次防抖/关闭时再试；否则这次改动就此丢失且无迹可查
                    if (!saved) _saveDirty = true;
                }
                NotifySettingsChanged();
            }
            catch (Exception ex)
            {
                lock (_lock) { _saveDirty = true; }
                LogManager.Error("设置落盘失败", ex);
            }
        }

        /// <summary>
        /// 触发 SettingsChanged（订阅者含 UI 刷新逻辑，必须在 UI 线程执行）。
        /// Timer 线程调用时封送回 WPF Dispatcher；无 Dispatcher 环境（单元测试）直接调用。
        /// </summary>
        private void NotifySettingsChanged()
        {
            var app = System.Windows.Application.Current;
            if (app != null &&
                app.Dispatcher != null &&
                !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { SettingsChanged?.Invoke(); } catch (Exception ex) { LogManager.Error("设置变更通知失败", ex); }
                }));
                return;
            }
            try { SettingsChanged?.Invoke(); } catch (Exception ex) { LogManager.Error("设置变更通知失败", ex); }
        }

        public bool IsEdgeEnabled(string edge)
        {
            return edge switch
            {
                "Top" => _data.Edge_Top,
                "Bottom" => _data.Edge_Bottom,
                "Left" => _data.Edge_Left,
                "Right" => _data.Edge_Right,
                _ => true
            };
        }

        public void SetEdgeEnabled(string edge, bool enabled)
        {
            switch (edge)
            {
                case "Top": _data.Edge_Top = enabled; break;
                case "Bottom": _data.Edge_Bottom = enabled; break;
                case "Left": _data.Edge_Left = enabled; break;
                case "Right": _data.Edge_Right = enabled; break;
                default: return;
            }
            Save();
        }

        public bool IsCornerEnabled(string corner)
        {
            return corner switch
            {
                "TopLeft" => _data.Corner_TopLeft,
                "TopRight" => _data.Corner_TopRight,
                "BottomLeft" => _data.Corner_BottomLeft,
                "BottomRight" => _data.Corner_BottomRight,
                _ => true
            };
        }

        public void SetCornerEnabled(string corner, bool enabled)
        {
            switch (corner)
            {
                case "TopLeft": _data.Corner_TopLeft = enabled; break;
                case "TopRight": _data.Corner_TopRight = enabled; break;
                case "BottomLeft": _data.Corner_BottomLeft = enabled; break;
                case "BottomRight": _data.Corner_BottomRight = enabled; break;
                default: return;
            }
            Save();
        }

        // ========== 边行为模式 ==========
        public string GetEdgeMode(string edge)
        {
            return edge switch
            {
                "Top" => _data.EdgeMode_Top ?? "Follow",
                "Bottom" => _data.EdgeMode_Bottom ?? "Follow",
                "Left" => _data.EdgeMode_Left ?? "Follow",
                "Right" => _data.EdgeMode_Right ?? "Follow",
                _ => "Follow"
            };
        }

        public void SetEdgeMode(string edge, string mode)
        {
            switch (edge)
            {
                case "Top": _data.EdgeMode_Top = mode; break;
                case "Bottom": _data.EdgeMode_Bottom = mode; break;
                case "Left": _data.EdgeMode_Left = mode; break;
                case "Right": _data.EdgeMode_Right = mode; break;
                default: return;
            }
            Save();
        }

        // ========== 外观 ==========
        public string BackgroundColor
        {
            get => _data.BackgroundColor ?? "#2D2D2D";
            set => SetField(v => _data.BackgroundColor = v, value);
        }



        public string TextColor
        {
            get => _data.TextColor ?? "#FFFFFF";
            set => SetField(v => _data.TextColor = v, value);
        }

        public double Opacity
        {
            get => _data.Opacity;
            set => SetField(v => _data.Opacity = v, value, 0, 1);
        }

        public int CornerRadius
        {
            get => _data.CornerRadius;
            set => SetField(v => _data.CornerRadius = v, value, 0, 50);
        }

        public bool ShowSystemStatus
        {
            get => _data.ShowSystemStatus;
            set => SetField(v => _data.ShowSystemStatus = v, value);
        }

        public string WebWidgetUrl
        {
            get => _data.WebWidgetUrl;
            set => SetField(v => _data.WebWidgetUrl = v, value);
        }

        public System.Collections.Generic.List<ShoreHue.Core.Services.Configuration.WebBookmark> WebBookmarks
        {
            get => _data.WebBookmarks;
            set => SetField(v => _data.WebBookmarks = v, value);
        }




        // ========== 形状参数 ==========
        public double StripLengthRatio
        {
            get => _data.StripLengthRatio;
            set => SetField(v => _data.StripLengthRatio = v, value, 0.1, 1.0);
        }

        public double StripWidthMultiplier
        {
            get => _data.StripWidthMultiplier;
            set => SetField(v => _data.StripWidthMultiplier = v, value, 0.5, 3.0);
        }

        public double SquareShortSideMultiplier
        {
            get => _data.SquareShortSideMultiplier;
            set => SetField(v => _data.SquareShortSideMultiplier = v, value, 1.0, 4.0);
        }

        public double GoldenRatio
        {
            get => _data.GoldenRatio;
            set => SetField(v => _data.GoldenRatio = v, value, 1.0, 3.0);
        }

        public double TriggerRegionRatio
        {
            get => _data.TriggerRegionRatio;
            set => SetField(v => _data.TriggerRegionRatio = v, value, 0.1, 0.5);
        }

        public double HorizontalLayoutThreshold
        {
            get => _data.HorizontalLayoutThreshold;
            set => SetField(v => _data.HorizontalLayoutThreshold = v, value, 0.1, 1.0);
        }

        public double TagWidth
        {
            get => _data.TagWidth;
            set => SetField(v => _data.TagWidth = v, value, 40, 400);
        }

        // ========== 自适应行为 ==========
        public bool AutoFitOnTrigger
        {
            get => _data.AutoFitOnTrigger;
            set => SetField(v => _data.AutoFitOnTrigger = v, value);
        }

        // ========== 固定位置 ==========
        public string GetFixedShape(string edge)
        {
            return edge switch
            {
                "Top" => _data.FixedShape_Top ?? "Square",
                "Bottom" => _data.FixedShape_Bottom ?? "Square",
                "Left" => _data.FixedShape_Left ?? "Square",
                "Right" => _data.FixedShape_Right ?? "Square",
                _ => "Square"
            };
        }

        public void SetFixedShape(string edge, string shape)
        {
            switch (edge)
            {
                case "Top": _data.FixedShape_Top = shape; break;
                case "Bottom": _data.FixedShape_Bottom = shape; break;
                case "Left": _data.FixedShape_Left = shape; break;
                case "Right": _data.FixedShape_Right = shape; break;
                default: return;
            }
            Save();
        }

        public double GetFixedOffset(string edge)
        {
            return edge switch
            {
                "Top" => _data.FixedOffset_Top,
                "Bottom" => _data.FixedOffset_Bottom,
                "Left" => _data.FixedOffset_Left,
                "Right" => _data.FixedOffset_Right,
                _ => 0
            };
        }

        public void SetFixedOffset(string edge, double offset)
        {
            switch (edge)
            {
                case "Top": _data.FixedOffset_Top = Math.Max(0, offset); break;
                case "Bottom": _data.FixedOffset_Bottom = Math.Max(0, offset); break;
                case "Left": _data.FixedOffset_Left = Math.Max(0, offset); break;
                case "Right": _data.FixedOffset_Right = Math.Max(0, offset); break;
                default: return;
            }
            Save();
        }

        // ========== 区域形状 ==========
        // ★ 键 → 字段 的映射只有一处：RegionTable（边缘拆成 edge+region 的旧调用形态在这里拼回规范键）。
        /// <summary>区域形状；四角没有形状字段 → 返回 Default。含未知键的所有情况都回落 Default。</summary>
        public string GetRegionShape(string edge, string region)
            => RegionTable.Find(RegionTable.KeyOf(edge, region))?.GetShape(_data) ?? RegionTable.DefaultShape;

        public void SetRegionShape(string edge, string region, string shape)
        {
            var r = RegionTable.Find(RegionTable.KeyOf(edge, region));
            if (r == null) return;   // 未知区域：与原实现一致 —— 不写、不落盘
            r.SetShape(_data, shape);
            Save();
        }

        // ========== 剪贴板与便签 ==========
        public int ClipboardMaxCount
        {
            get => _data.ClipboardMaxCount;
            set => SetField(v => _data.ClipboardMaxCount = v, value, 1, 50);
        }

        /// <summary>
        /// 剪贴板**单条最多显示几行**（1–20，默认 4）。
        ///
        /// ★ 语义变更（2026-09-13）：旧语义是"最多显示多少字符"（值域 10–500，默认 100），
        ///   而且查遍全仓库**没有任何消费者** —— 界面有滑块、文档有文案，代码里没人读它，
        ///   是个纯粹的摆设；真正把文字砍掉的是 ClipboardManager 里硬编码的 500 字 + 界面 CharacterEllipsis。
        ///   现在改为"行数"并由剪贴板小组件真正读取（`MaxHeight = 行数 × 行高`）。
        ///
        /// ★ 旧值迁移：老配置里存的是 100/500 这类字符数，在新语义下等于"100 行"（＝不限），
        ///   阈值会形同失效。所以 getter 把超出新值域的值一律归一到默认 4。
        /// </summary>
        public int ClipboardDisplayLength
        {
            get => _data.ClipboardDisplayLength is >= 1 and <= 20 ? _data.ClipboardDisplayLength : 4;
            set => SetField(v => _data.ClipboardDisplayLength = v, value, 1, 20);
        }

        public int ClipboardImageMaxWidth
        {
            get => _data.ClipboardImageMaxWidth;
            set => SetField(v => _data.ClipboardImageMaxWidth = v, value, 0, 4096);
        }

        public int ClipboardImageCacheLimitMB
        {
            get => _data.ClipboardImageCacheLimitMB;
            set => SetField(v => _data.ClipboardImageCacheLimitMB = v, value, 5, 1024);
        }

        public string LastWidgetTab
        {
            get => _data.LastWidgetTab ?? "Clipboard";
            set => SetField(v => _data.LastWidgetTab = v, value);
        }

        public string DefaultNoteColor
        {
            get => _data.DefaultNoteColor ?? "#00000000";
            set => SetField(v => _data.DefaultNoteColor = v, value);
        }

        public bool NoteShowTitleByDefault
        {
            get => _data.NoteShowTitleByDefault;
            set => SetField(v => _data.NoteShowTitleByDefault = v, value);
        }

        // ========== 划词翻译面板（在「设置 → 面板 → 划词翻译」里配） ==========

        public int TextAiHistoryLimit
        {
            get => _data.TextAiHistoryLimit;
            set => SetField(v => _data.TextAiHistoryLimit = Math.Max(0, value), value);
        }

        public string TextAiHistoryJson
        {
            get => _data.TextAiHistoryJson ?? "";
            set => SetField(v => _data.TextAiHistoryJson = v, value);
        }

        public string TextAiTargetLanguage
        {
            get => _data.TextAiTargetLanguage ?? "";
            set => SetField(v => _data.TextAiTargetLanguage = value, value);
        }

        // ========== 计算器面板（在「设置 → 面板 → 计算器」里配） ==========

        public int CalculatorHistoryLimit
        {
            get => _data.CalculatorHistoryLimit;
            set => SetField(v => _data.CalculatorHistoryLimit = Math.Max(0, value), value);
        }

        public string CalculatorHistoryJson
        {
            get => _data.CalculatorHistoryJson ?? "";
            set => SetField(v => _data.CalculatorHistoryJson = v, value);
        }

        // ========== 任务栏标签分组（当用户设置持久化） ==========

        public string TaskbarGroupsJson
        {
            get => _data.TaskbarGroupsJson ?? "";
            set => SetField(v => _data.TaskbarGroupsJson = v, value);
        }

        // ========== 插件运行时守卫（安全模式 / 异常熔断） ==========

        public List<string> CircuitBrokenPlugins
        {
            get => _data.CircuitBrokenPlugins ?? new List<string>();
            set => SetField(v => _data.CircuitBrokenPlugins = v ?? new List<string>(), value);
        }

        public bool SafeModeRequested
        {
            get => _data.SafeModeRequested;
            set => SetField(v => _data.SafeModeRequested = v, value);
        }

        public int UncleanExitCount
        {
            get => _data.UncleanExitCount;
            set => SetField(v => _data.UncleanExitCount = Math.Max(0, value), value);
        }

        // ========== 剪贴板面板（在「设置 → 面板 → 剪贴板」里开关） ==========

        public bool ClipboardKeyboardNav
        {
            get => _data.ClipboardKeyboardNav;
            set => SetField(v => _data.ClipboardKeyboardNav = v, value);
        }

        public bool ClipboardShowSourceApp
        {
            get => _data.ClipboardShowSourceApp;
            set => SetField(v => _data.ClipboardShowSourceApp = v, value);
        }

        // ========== 便签快捷键（在「设置 → 面板 → 便签」里录入；在便签面板内生效） ==========

        public string NoteHotkeyNew
        {
            get => string.IsNullOrWhiteSpace(_data.NoteHotkeyNew) ? "Ctrl+Alt+N" : _data.NoteHotkeyNew!;
            set => SetField(v => _data.NoteHotkeyNew = v, value);
        }

        public string NoteHotkeyDelete
        {
            get => string.IsNullOrWhiteSpace(_data.NoteHotkeyDelete) ? "Ctrl+Alt+D" : _data.NoteHotkeyDelete!;
            set => SetField(v => _data.NoteHotkeyDelete = v, value);
        }

        public string NoteHotkeyNext
        {
            get => string.IsNullOrWhiteSpace(_data.NoteHotkeyNext) ? "Ctrl+Alt+Right" : _data.NoteHotkeyNext!;
            set => SetField(v => _data.NoteHotkeyNext = v, value);
        }

        public bool UseAutoSize
        {
            get => _data.UseAutoSize;
            set => SetField(v => _data.UseAutoSize = v, value);
        }

        // ========== 自动更新（GitHub Releases） ==========
        public bool AutoCheckUpdate
        {
            get => _data.AutoCheckUpdate;
            set => SetField(v => _data.AutoCheckUpdate = v, value);
        }

        public bool OnboardingCompleted
        {
            get => _data.OnboardingCompleted;
            set => SetField(v => _data.OnboardingCompleted = v, value);
        }

        // ========== 状态栏显示项 ==========
        public bool StatusShowTime { get => _data.StatusShowTime; set => SetField(v => _data.StatusShowTime = v, value); }
        public bool StatusShowCpu { get => _data.StatusShowCpu; set => SetField(v => _data.StatusShowCpu = v, value); }
        public bool StatusShowMemory { get => _data.StatusShowMemory; set => SetField(v => _data.StatusShowMemory = v, value); }
        public bool StatusShowFps { get => _data.StatusShowFps; set => SetField(v => _data.StatusShowFps = v, value); }
        public bool StatusShowVolume { get => _data.StatusShowVolume; set => SetField(v => _data.StatusShowVolume = v, value); }
        public bool StatusShowNetwork { get => _data.StatusShowNetwork; set => SetField(v => _data.StatusShowNetwork = v, value); }
        public bool StatusShowBattery { get => _data.StatusShowBattery; set => SetField(v => _data.StatusShowBattery = v, value); }
        public bool StatusShowWeather { get => _data.StatusShowWeather; set => SetField(v => _data.StatusShowWeather = v, value); }

        // ========== 天气 ==========
        public bool WeatherEnabled { get => _data.WeatherEnabled; set => SetField(v => _data.WeatherEnabled = v, value); }
        public string? WeatherCity { get => _data.WeatherCity; set => SetField(v => _data.WeatherCity = v, value); }

        // ========== ShoreHue 性能模式 ==========
        public string PerformanceMode
        {
            get => _data.PerformanceMode ?? "Normal";
            set => SetField(v => _data.PerformanceMode = v, value);
        }

        // ========== 面板运行帧率（fps，0=自动满帧） ==========
        public int PanelFrameRate
        {
            get => _data.PanelFrameRate;
            set => SetField(v => _data.PanelFrameRate = v, value);
        }

        // ========== 全局界面字号缩放（0.75~1.5） ==========
        public double UiFontScale
        {
            get => _data.UiFontScale;
            set => SetField(v => _data.UiFontScale = Math.Max(0.75, Math.Min(1.5, value)), value);
        }

        /// <summary>应用性能预设（内部标志保护：不触发自定义检测）。</summary>
        public void SetPerformanceMode(string mode)
        {
            _applyingPreset = true;
            try
            {
                PerformancePresets.Apply(this, mode);
                _data.PerformanceMode = mode;
            }
            finally
            {
                _applyingPreset = false;
            }
            Save();
        }

        /// <summary>非预设应用路径修改相关参数 → 自动进入自定义模式。</summary>
        private void MarkCustomIfPreset()
        {
            if (_applyingPreset) return;
            if (_data.PerformanceMode != PerformancePresets.Custom)
            {
                _data.PerformanceMode = PerformancePresets.Custom;
            }
        }

        // ========== 边缘触发距离与延时 ==========
        public int TriggerDistancePx
        {
            get => _data.TriggerDistancePx;
            set { _data.TriggerDistancePx = Math.Max(2, Math.Min(20, value)); MarkCustomIfPreset(); Save(); }
        }

        public int TriggerDelayMs
        {
            get => _data.TriggerDelayMs;
            set { _data.TriggerDelayMs = Math.Max(0, Math.Min(1000, value)); MarkCustomIfPreset(); Save(); }
        }

        public int GetTriggerDelay(string regionKey)
        {
            if (_data.RegionTriggerDelay != null &&
                _data.RegionTriggerDelay.TryGetValue(regionKey, out int v))
                return Math.Max(0, Math.Min(1000, v));
            return TriggerDelayMs;
        }

        public void SetTriggerDelay(string regionKey, int ms)
        {
            _data.RegionTriggerDelay ??= new System.Collections.Generic.Dictionary<string, int>();
            _data.RegionTriggerDelay[regionKey] = Math.Max(0, Math.Min(1000, ms));
            Save();
        }

        public int GetHideDelay(string regionKey)
        {
            if (_data.RegionHideDelay != null &&
                _data.RegionHideDelay.TryGetValue(regionKey, out int v))
                return Math.Max(0, Math.Min(1000, v));
            return HideDelayMs;
        }

        public void SetHideDelay(string regionKey, int ms)
        {
            _data.RegionHideDelay ??= new System.Collections.Generic.Dictionary<string, int>();
            _data.RegionHideDelay[regionKey] = Math.Max(0, Math.Min(1000, ms));
            Save();
        }

        // ========== 小组件显示开关 ==========
        public bool IsWidgetEnabled(string widgetKey)
        {
            // ★ 用户 C# 插件小组件：启用状态存 WidgetPluginOverrides（缺省启用）
            if (widgetKey.StartsWith("Widget_", StringComparison.Ordinal))
                return _data.WidgetPluginOverrides.TryGetValue(widgetKey, out var v) ? v : true;

            return widgetKey switch
            {
                "Clipboard" => _data.WidgetEnabled_Clipboard,
                "Note" => _data.WidgetEnabled_Note,
                "Timer" => _data.WidgetEnabled_Timer,
                "Calculator" => _data.WidgetEnabled_Calculator,
                "TextAi" => _data.WidgetEnabled_TextAi,
                "Web" => _data.WidgetEnabled_Web,
                _ => true
            };
        }

        /// <summary>
        /// 清掉某个插件小组件的启用状态覆盖记录（删除插件时调用）。
        /// ★ 必须清：覆盖记录是"用户显式开关"的持久化，不会随插件删除自动消失 ——
        ///   留着它，同 id 的包以后重新装回来会**默认禁用**，而界面上没有任何线索说明原因。
        /// </summary>
        public void ClearWidgetEnabledOverride(string widgetKey)
        {
            if (string.IsNullOrEmpty(widgetKey)) return;
            if (_data.WidgetPluginOverrides.Remove(widgetKey)) Save();
        }

        public void SetWidgetEnabled(string widgetKey, bool enabled)
        {
            // ★ 用户 C# 插件小组件
            if (widgetKey.StartsWith("Widget_", StringComparison.Ordinal))
            {
                _data.WidgetPluginOverrides[widgetKey] = enabled;
                Save();
                return;
            }

            switch (widgetKey)
            {
                case "Clipboard": _data.WidgetEnabled_Clipboard = enabled; break;
                case "Note": _data.WidgetEnabled_Note = enabled; break;
                case "Timer": _data.WidgetEnabled_Timer = enabled; break;
                case "Calculator": _data.WidgetEnabled_Calculator = enabled; break;
                case "TextAi": _data.WidgetEnabled_TextAi = enabled; break;
                case "Web": _data.WidgetEnabled_Web = enabled; break;
                default: return;
            }
            Save();
        }

        // ========== 自定义状态栏显示项开关 ==========
        /// <summary>自定义状态栏插件（status_&lt;id&gt;）是否启用；缺省视为启用。</summary>
        public bool IsStatusProviderEnabled(string providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return false;
            return _data.StatusProviderEnabled.TryGetValue(providerId, out var v) ? v : true;
        }

        /// <summary>设置自定义状态栏插件开关（true=显示）。</summary>
        public void SetStatusProviderEnabled(string providerId, bool enabled)
        {
            if (string.IsNullOrEmpty(providerId)) return;
            _data.StatusProviderEnabled[providerId] = enabled;
            Save();
        }

        // ========== 插件信任（安全 v2）：id + 内容哈希 ==========
        // ★ 三个方法都是**显式接口实现**（IPluginTrustStore 是 internal）：
        //   于是"信任的读写"在 SettingsManager 的公开面上根本不存在，插件即使拿到本实例也调不到。
        //   别改回 public —— 那等于把信任的写权限递给外来代码（见 IPluginTrustStore 注释）。
        /// <summary>该插件当前内容是否被用户显式信任（内容变化 → 哈希不匹配 → 自动失效）。</summary>
        bool IPluginTrustStore.IsPluginTrusted(string pluginId, string contentHash)
        {
            if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(contentHash)) return false;
            var map = _data.TrustedPlugins;
            if (map == null) return false;
            return map.TryGetValue(pluginId, out var h) && string.Equals(h, contentHash, StringComparison.Ordinal);
        }

        /// <summary>记录信任（界面点"信任"或海床保存可信项时调用）。</summary>
        void IPluginTrustStore.SetPluginTrusted(string pluginId, string contentHash)
        {
            if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(contentHash)) return;
            _data.TrustedPlugins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _data.TrustedPlugins[pluginId] = contentHash;
            Save();
        }

        /// <summary>撤销信任。</summary>
        void IPluginTrustStore.RevokePluginTrust(string pluginId)
        {
            if (string.IsNullOrEmpty(pluginId)) return;
            if (_data.TrustedPlugins?.Remove(pluginId) == true) Save();
        }

        // ========== 划词翻译 热键 ==========
        public string TextAiHotkey
        {
            get => _data.TextAiHotkey ?? "";
            set => SetField(v => _data.TextAiHotkey = v, value);
        }

        // ========== 键盘呼出区域面板（Ctrl+数字环） ==========
        public bool RegionHotkeysEnabled
        {
            get => _data.RegionHotkeysEnabled;
            set => SetField(v => _data.RegionHotkeysEnabled = v, value);
        }

        public string RegionHotkeyModifier
        {
            get => _data.RegionHotkeyModifier ?? "Ctrl";
            set => SetField(v => _data.RegionHotkeyModifier = v, value);
        }

        // ========== 勿扰模式 ==========
        public bool RememberDndMode
        {
            get => _data.RememberDndMode;
            set => SetField(v => _data.RememberDndMode = v, value);
        }

        public bool DndModeEnabled
        {
            get => _data.DndModeEnabled;
            set => SetField(v => _data.DndModeEnabled = v, value);
        }

        // ========== 任务栏 ==========
        public double TaskbarIconSize
        {
            get => _data.TaskbarIconSize;
            set
            {
                _data.TaskbarIconSize = Math.Max(16, Math.Min(48, value));
                Save();
            }
        }

        public double DividerOffset
        {
            get => _data.DividerOffset;
            set
            {
                _data.DividerOffset = Math.Max(0.1, Math.Min(0.9, value));
                Save();
            }
        }

        // ========== 16个独立区域尺寸（含四角）==========
        // ★ 键 → 字段 的映射只有一处：RegionTable（四角的尺寸字段多一层 Corner_，这种例外也收在表里）。
        /// <summary>该区域的用户自定义尺寸（宽,高）；0 = 未自定义，未知键返回 (0,0)。</summary>
        public (double width, double height) GetUserSize(string regionKey)
            => RegionTable.Find(regionKey)?.GetSize(_data) ?? (0, 0);

        public void SetUserSize(string regionKey, double width, double height)
        {
            var r = RegionTable.Find(regionKey);
            if (r == null) return;   // 未知区域：与原实现一致 —— 不写、不落盘
            r.SetSize(_data, width, height);
            Save();
        }

        // ========== 动画设置 ==========
        public bool AnimationsEnabled
        {
            get => _data.AnimationsEnabled;
            set { _data.AnimationsEnabled = value; MarkCustomIfPreset(); Save(); }
        }

        public string ShowHideEasingType
        {
            get => _data.ShowHideEasingType ?? "CubicEase";
            set => SetField(v => _data.ShowHideEasingType = v, value);
        }

        public int ShowHideDurationMs
        {
            get => _data.ShowHideDurationMs;
            set { _data.ShowHideDurationMs = Math.Max(100, Math.Min(800, value)); MarkCustomIfPreset(); Save(); }
        }

        public string TransformEasingType
        {
            get => _data.TransformEasingType ?? "CubicEase";
            set => SetField(v => _data.TransformEasingType = v, value);
        }

        // ========== 触发/隐藏动画（类型 + 时长 + 特化参数） ==========
        public string ShowAnimationType
        {
            get => string.IsNullOrEmpty(_data.ShowAnimationType) ? "Slide" : _data.ShowAnimationType;
            set { _data.ShowAnimationType = value; MarkCustomIfPreset(); Save(); }
        }

        public int ShowAnimationDurationMs
        {
            get => _data.ShowAnimationDurationMs > 0 ? _data.ShowAnimationDurationMs : _data.ShowHideDurationMs;
            set { _data.ShowAnimationDurationMs = Math.Max(30, Math.Min(2000, value)); MarkCustomIfPreset(); Save(); }
        }

        public double ShowAnimationZoomFrom
        {
            get => _data.ShowAnimationZoomFrom;
            set { _data.ShowAnimationZoomFrom = Math.Max(0.05, Math.Min(0.95, value)); MarkCustomIfPreset(); Save(); }
        }

        public int ShowAnimationOscillations
        {
            get => _data.ShowAnimationOscillations;
            set { _data.ShowAnimationOscillations = Math.Max(1, Math.Min(10, value)); MarkCustomIfPreset(); Save(); }
        }

        public double ShowAnimationSpringiness
        {
            get => _data.ShowAnimationSpringiness;
            set { _data.ShowAnimationSpringiness = Math.Max(1, Math.Min(10, value)); MarkCustomIfPreset(); Save(); }
        }

        public string HideAnimationType
        {
            get => string.IsNullOrEmpty(_data.HideAnimationType) ? _data.ShowAnimationType : _data.HideAnimationType;
            set { _data.HideAnimationType = value; MarkCustomIfPreset(); Save(); }
        }

        public int HideAnimationDurationMs
        {
            get => _data.HideAnimationDurationMs > 0 ? _data.HideAnimationDurationMs : _data.ShowAnimationDurationMs;
            set { _data.HideAnimationDurationMs = Math.Max(30, Math.Min(2000, value)); MarkCustomIfPreset(); Save(); }
        }

        public double HideAnimationZoomTo
        {
            get => _data.HideAnimationZoomTo;
            set { _data.HideAnimationZoomTo = Math.Max(0.05, Math.Min(0.95, value)); MarkCustomIfPreset(); Save(); }
        }

        public int HideAnimationOscillations
        {
            get => _data.HideAnimationOscillations;
            set { _data.HideAnimationOscillations = Math.Max(1, Math.Min(10, value)); MarkCustomIfPreset(); Save(); }
        }

        public double HideAnimationSpringiness
        {
            get => _data.HideAnimationSpringiness;
            set { _data.HideAnimationSpringiness = Math.Max(1, Math.Min(10, value)); MarkCustomIfPreset(); Save(); }
        }

        public int TransformDurationMs
        {
            get => _data.TransformDurationMs;
            set { _data.TransformDurationMs = Math.Max(100, Math.Min(600, value)); MarkCustomIfPreset(); Save(); }
        }

        public int HideDelayMs
        {
            get => _data.HideDelayMs;
            // ★ 0 = 取消延时隐藏（鼠标一离开立即隐藏）
            set { _data.HideDelayMs = Math.Max(0, Math.Min(1000, value)); MarkCustomIfPreset(); Save(); }
        }

        public int FlyDurationMs
        {
            get => _data.FlyDurationMs;
            set { _data.FlyDurationMs = Math.Max(0, Math.Min(2000, value)); MarkCustomIfPreset(); Save(); }
        }

        // ========== 逐区域动画覆盖（动画页签「动画应用于」） ==========
        public ShoreHue.Core.Models.RegionAnimationOverride? GetRegionAnimation(string regionKey)
        {
            if (_data.RegionAnimationOverrides != null &&
                _data.RegionAnimationOverrides.TryGetValue(regionKey, out var ov))
                return ov;
            return null;
        }

        public void SetRegionAnimation(string regionKey, ShoreHue.Core.Models.RegionAnimationOverride? ov)
        {
            _data.RegionAnimationOverrides ??= new System.Collections.Generic.Dictionary<string, ShoreHue.Core.Models.RegionAnimationOverride>();
            if (ov == null || (string.IsNullOrEmpty(ov.ShowAnimationType) && !ov.ShowAnimationDurationMs.HasValue &&
                               string.IsNullOrEmpty(ov.HideAnimationType) && !ov.HideAnimationDurationMs.HasValue))
            {
                _data.RegionAnimationOverrides.Remove(regionKey);
            }
            else
            {
                _data.RegionAnimationOverrides[regionKey] = ov;
            }
            Save();
        }

        public string GetResolvedShowAnimationType(string regionKey)
        {
            var ov = GetRegionAnimation(regionKey);
            return !string.IsNullOrEmpty(ov?.ShowAnimationType) ? ov!.ShowAnimationType! : ShowAnimationType;
        }

        public int GetResolvedShowAnimationDurationMs(string regionKey)
        {
            var ov = GetRegionAnimation(regionKey);
            return ov?.ShowAnimationDurationMs.HasValue == true ? ov.ShowAnimationDurationMs.Value : ShowAnimationDurationMs;
        }

        public string GetResolvedHideAnimationType(string regionKey)
        {
            var ov = GetRegionAnimation(regionKey);
            return !string.IsNullOrEmpty(ov?.HideAnimationType) ? ov!.HideAnimationType! : HideAnimationType;
        }

        public int GetResolvedHideAnimationDurationMs(string regionKey)
        {
            var ov = GetRegionAnimation(regionKey);
            return ov?.HideAnimationDurationMs.HasValue == true ? ov.HideAnimationDurationMs.Value : HideAnimationDurationMs;
        }

        // ========== 编程模式（海床） ==========
        public bool ProgrammingModeEnabled
        {
            get => _data.ProgrammingModeEnabled;
            set { _data.ProgrammingModeEnabled = value; Save(); }
        }

        public System.Collections.Generic.List<ShoreHue.Core.Models.CustomPanelDefinition> CustomPanels
        {
            get => _data.CustomPanels ??= new System.Collections.Generic.List<ShoreHue.Core.Models.CustomPanelDefinition>();
            // ★ setter 降为 internal（宿主面经由 ISettingsHost.SetCustomPanels）：
            //   公开面上不再存在"整表替换面板列表"的写入口。
            internal set { _data.CustomPanels = value; Save(); }
        }

        // ISettingsHost：宿主面的整表替换入口
        void ISettingsHost.SetCustomPanels(System.Collections.Generic.List<ShoreHue.Core.Models.CustomPanelDefinition>? panels)
            => CustomPanels = panels ?? new System.Collections.Generic.List<ShoreHue.Core.Models.CustomPanelDefinition>();

        public System.Collections.Generic.Dictionary<string, string> AppliedPresets
        {
            get => _data.AppliedPresets ??= new System.Collections.Generic.Dictionary<string, string>();
            set { _data.AppliedPresets = value; Save(); }
        }

        // ========== 引潮模式 ==========
        public bool ClingModeEnabled
        {
            get => _data.ClingModeEnabled;
            set => SetField(v => _data.ClingModeEnabled = v, value);
        }

        public int SnapRangePx
        {
            get => _data.SnapRangePx;
            set => SetField(v => _data.SnapRangePx = Math.Max(0, Math.Min(100, value)), value);
        }

        public int ContentStabilizeMs
        {
            get => _data.ContentStabilizeMs;
            set => SetField(v => _data.ContentStabilizeMs = Math.Max(200, Math.Min(800, value)), value);
        }

        public string? PassthroughModifier
        {
            get => _data.PassthroughModifier ?? "Ctrl";
            set => SetField(v => _data.PassthroughModifier = v, string.IsNullOrWhiteSpace(value) ? "Ctrl" : value);
        }

        // ★★★ 新增：区域防抖延迟 ★★★
        public int RegionDebounceMs
        {
            get => _data.RegionDebounceMs;
            set { _data.RegionDebounceMs = Math.Max(30, Math.Min(300, value)); MarkCustomIfPreset(); Save(); }
        }

        // ★ 键 → 字段 的映射只有一处：RegionTable。
        /// <summary>该区域用哪个面板（Default/Taskbar/Widget/…）；未知键返回 Default。</summary>
        public string GetRegionPanel(string regionKey)
            => RegionTable.Find(regionKey)?.GetPanel(_data) ?? RegionTable.DefaultPanel;

        public void SetRegionPanel(string regionKey, string panelType)
        {
            var r = RegionTable.Find(regionKey);
            if (r == null) return;   // 未知区域：与原实现一致 —— 不写、不落盘
            r.SetPanel(_data, panelType);
            Save();
        }
    }
}