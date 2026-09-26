using ShoreHue.Animation;
using ShoreHue.Core.Detection;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;
using System;
using System.Windows;

namespace ShoreHue.Core.Controllers
{
    public class EdgeTriggerController : IEdgeTriggerController
    {
        private readonly Window _window;
        private readonly ShapeAnimator _shapeAnimator;
        private WindowSizeController? _sizeController;
        private readonly PanelVisibilityController _visibilityController;
        private readonly ISettingsService _settings;

        // ★★★ 底部边界（任务栏顶部，随任务栏状态动态更新） ★★★
        private double _bottomBoundary;

        // 任务栏高度（DIP，用于统一各模式的条状尺寸）
        private readonly double _taskbarHeightDips;

        // ========== 状态追踪 ==========
        private string _currentEdge = "";
        private string _lastRegionType = "";
        private EdgeRegion _lastProcessedRegion = EdgeRegion.Unknown;
        private string _currentRegionKey = "";

        // ★ 时序状态机（防抖/触发延时/快速切换）：委托 EdgeTimingState，纯逻辑可单测
        private readonly EdgeTimingState _timing;

        // ========== 拖拽状态 ==========
        private bool _isDragging = false;
        private DateTime _dragEndCooldownUntil = DateTime.MinValue;
        public bool IsDragging
        {
            get => _isDragging;
            set => _isDragging = value;
        }
        /// <summary>
        /// 拖拽/调整大小刚结束（冷却期内），用于抑制面板隐藏。
        /// </summary>
        public bool IsRecentlyDragged => DateTime.Now < _dragEndCooldownUntil;

        // ========== 尺寸缓存 ==========
        private double _cachedWidth = 0;
        private double _cachedHeight = 0;

        // ========== 飞行 ==========
        private bool _isFlying = false;
        private bool _flyCompletedTriggered = false;
        private DateTime _flyTimeoutAt = DateTime.MinValue; // ★ 飞行保险：FlyCompleted 丢失时超时复位
        private string _flyingTargetRegionType = "";
        private string _flyingTargetRegionKey = "";
        private string _flyingTargetEdge = "";
        // ★ 飞行目标屏幕边界（DIP）：StartFlying 时用鼠标所在屏记录，完成时用它重锚，
        //   避免"飞行中窗口中心还在旧屏 → 完成时重锚到旧屏"的跨边错位。
        private double _flyingTargetScreenW;
        private double _flyingTargetScreenH;
        private double _flyingTargetW;
        private double _flyingTargetH;
        private double _lastTargetW;
        private double _lastTargetH;
        // ★ 直接加载流程进行中：切换分支先同步换内容、随后统一测量并驱动尺寸。
        //   期间 OnPanelContentChanged 跳过原子 ApplyAutoSize（避免"动画形变 + 原子 SetWindowPos"打架闪烁）。
        private bool _directLoadInProgress = false;
        private string _pendingSwitchType = "";
        private string _pendingSwitchKey = "";
        // ========== 引潮 ==========
        private bool _isClinging = false;

        // ========== IEdgeTriggerController 接口实现 ==========
        public EdgeRegion CurrentRegion => _lastProcessedRegion;

        /// <summary>当前生效的区域键（供隐藏延时按区域读取）。</summary>
        public string CurrentRegionKey => _currentRegionKey;

        /// <summary>直接加载流程进行中（内容已就位，尺寸动画由切换分支统一驱动）。</summary>
        public bool IsDirectLoadInProgress => _directLoadInProgress;

        /// <summary>触发延时进行中（主窗口据此跳过立即显示，避免延时被绕过）。</summary>
        public bool IsTriggerDelaying => _timing.IsTriggerDelaying;

        /// <summary>鼠标离开边缘区域时重置触发延时计时（重新进入需重新停留）。</summary>
        public void ResetTriggerDelay() => _timing.ResetTriggerDelay();
        public string CurrentEdge => _currentEdge;

        /// <summary>触发距离（px，DIP）。供 SizeDragHandler 判定"屏幕边缘触发带"以让位拖拽手柄。</summary>
        public int TriggerDistancePx => _settings.TriggerDistancePx;

        public bool IsFlying => _isFlying;

        /// <summary>最近一次切换的目标尺寸（防抖稳定后 MainWindow 用它做尺寸形变动画）。</summary>
        public (double Width, double Height) LastTargetSize =>
            (_lastTargetW > 0 ? _lastTargetW : _window.Width, _lastTargetH > 0 ? _lastTargetH : _window.Height);
        public bool IsClinging => _isClinging;
        public bool IsSticking => false;
        public bool IsPanelVisible { get; set; }

        public event Action? StartClingingRequested;
        public event Action? StopClingingRequested;
        public event Action? StickToMouseRequested;
        public event Action? FlyCompleted;
        public event Action<string, string>? RegionChanged;

        public EdgeTriggerController(
            Window window,
            ShapeAnimator shapeAnimator,
            WindowSizeController? sizeController,
            PanelVisibilityController visibilityController,
            ISettingsService settings,
            double bottomBoundary,
            double taskbarHeightDips)
        {
            _window = window;
            _shapeAnimator = shapeAnimator;
            _sizeController = sizeController;
            _visibilityController = visibilityController;
            _settings = settings;
            _bottomBoundary = bottomBoundary;
            _taskbarHeightDips = taskbarHeightDips;

            // ★ 时序状态机：触发延时按区域实时读取设置
            _timing = new EdgeTimingState(getTriggerDelay: key => _settings.GetTriggerDelay(key));

            _shapeAnimator.FlyCompleted += OnFlyCompleted;
            // ★ 渲染帧"追到目标/面板内停止"回调：复位 _isClinging，杜绝"停后鼠标一动又重启追"
            _shapeAnimator.ClingArrived += OnClingArrived;

            // ★ 跟随目标提供者：渲染帧每帧调用——每帧自算 region/屏幕（无防抖滞后），
            //   位置实时跟随鼠标（Windows 拖拽式跟手）。切换防抖只影响内容，不影响位置跟手。
            _shapeAnimator.FollowPositionProvider = () =>
            {
                try
                {
                    var p = System.Windows.Forms.Cursor.Position;
                    double dpi = _followDpi > 0 ? _followDpi : GetDpiScale();
                    double mx = p.X / dpi;
                    double my = p.Y / dpi;
                    var wa = ShoreHue.Infrastructure.Utils.ScreenMetrics.GetCachedScreenForPoint(mx, my);
                    double sw = wa.Width;
                    double sh = wa.Height;
                    bool allowTopRight = _settings.GetRegionPanel("TopRight") == "WindowControl";
                    var region = ShoreHue.Core.Detection.EdgeStateDetector.DetectRegion(
                        mx, my, sw, sh, _settings.TriggerDistancePx, allowTopRight);
                    if (region == EdgeRegion.Unknown)
                    {
                        return (_window.Left, _window.Top);   // 不在边缘：保持当前位置
                    }
                    // ★ 用稳定目标尺寸（_cached，切换时已更新为目标值）而非动画中的
                    //   _window.Width/Height：尺寸形变期间 ch 持续变化会导致
                    //   "top = my - ch/2" 中心跳变（快速切换方向反转时面板乱跳）
                    double cw = _cachedWidth > 0 ? _cachedWidth : _window.Width;
                    double ch = _cachedHeight > 0 ? _cachedHeight : _window.Height;
                    // ★ 贴边模式：纯贴边锚定（无吸附——吸附是引潮的设计，贴边本就贴边）
                    return CalculatePosition(region, mx, my, sw, sh, cw, ch);
                }
                catch
                {
                    // 尽力而为：坐标系算不出来就保持当前位置（跟随位置每帧都会算，记日志会刷屏）
                    return (_window.Left, _window.Top);
                }
            };

            _cachedWidth = _window.Width;
            _cachedHeight = _window.Height;
        }

        // ===== 跟随上下文（渲染帧 provider 读取）=====
        private EdgeRegion _followRegion = EdgeRegion.Unknown;
        private double _followScreenW = 1920;
        private double _followScreenH = 1080;
        private double _followDpi = 1.0;

        /// <summary>切换触发（内容待加载）：移动期间只中置跟随，内容延迟到稳定后由 CompletePendingSwitch 加载。</summary>
        public event Action<string, string>? SwitchStarted;

        /// <summary>鼠标稳定（防抖通过）后调用：加载待切换的内容（同 type 复用缓存，回到出发边省加载）。</summary>
        public void CompletePendingSwitch()
        {
            // ★ 一轮快速切换结束（稳定/隐藏）：重置切换计数，下次重新分级
            _timing.ResetSwitchCount();
            if (string.IsNullOrEmpty(_pendingSwitchType)) return;
            string type = _pendingSwitchType;
            string key = _pendingSwitchKey;
            _pendingSwitchType = "";
            _pendingSwitchKey = "";
            RegionChanged?.Invoke(type, key);

            // ★ 屏幕边缘限制最高优先级：内容就位后立即把目标尺寸同步到 _cached/_lastTarget。
            //   图标模态期间 _cached 保持"进入前旧尺寸"，渲染帧 provider 用旧宽度算位置 →
            //   窗口已变宽（如任务栏 2/3 屏宽）时右缘被旧宽度 clamp 挤到屏幕外。
            //   此处对所有类型生效（原仅 Widget）；尺寸与形变目标有差异则平滑接续校正。
            var (w, h) = GetTargetSizeForRegion(type, key);
            _cachedWidth = w;
            _cachedHeight = h;
            if (Math.Abs(w - _lastTargetW) > 2 || Math.Abs(h - _lastTargetH) > 2)
            {
                _lastTargetW = w;
                _lastTargetH = h;
                _shapeAnimator.AnimateSizeTo(w, h, null);
            }
        }

        /// <summary>切换计数：两次切换间隔 ≤ 1s 视为快速连续切换（累计），超过则重新起算。
        /// 返回是否已累计 ≥3 次（进入图标模态，延迟加载最终面板）。委托 EdgeTimingState。</summary>
        private bool IsRapidSwitching() => _timing.IsRapidSwitching();

        /// <summary>更新跟随上下文（region/屏幕）并激活渲染帧跟随（幂等）。
        /// DPI 从窗口实时取（鼠标 DIP 转换必须用真实缩放，传参易错）。</summary>
        public void StartFollowContext(EdgeRegion region, double screenW, double screenH, double dpiScale)
        {
            if (region == EdgeRegion.Unknown) return;
            _followRegion = region;
            _followScreenW = screenW > 0 ? screenW : _followScreenW;
            _followScreenH = screenH > 0 ? screenH : _followScreenH;
            _followDpi = GetDpiScale();
            _shapeAnimator.StartFollowPosition();
        }

        private double GetDpiScale()
        {
            try
            {
                var ps = System.Windows.PresentationSource.FromVisual(_window);
                double? m = ps?.CompositionTarget?.TransformToDevice.M11;
                return (m.HasValue && m.Value > 0) ? m.Value : 1.0;
            }
            catch (Exception ex)
            {
                // 取不到 DPI 就按 100% 算（定位会偏一点，但不至于不显示）
                LogManager.Debug($"[区域] 读取 DPI 缩放失败（按 100% 处理）：{ex.Message}");
                return 1.0;
            }
        }

        /// <summary>停止贴边跟随（渲染帧循环在无 cling 时停止）。</summary>
        public void StopFollowPosition() => _shapeAnimator.StopFollowPosition();

        /// <summary>
        /// 附加尺寸控制器（解除 MainWindow 初始化时的循环依赖，替代反射注入）。
        /// </summary>
        public void SetSizeController(WindowSizeController controller)
        {
            _sizeController = controller;
        }

        /// <summary>
        /// 更新底部贴边边界（任务栏自动隐藏/升起时由主窗口定时刷新）。
        /// </summary>
        public void UpdateBottomBoundary(double value)
        {
            if (value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                _bottomBoundary = value;
            }
        }

        /// <summary>
        /// 当任务栏边界变化（升起/隐藏）时，若面板正贴着底边，立即按新边界重新锚定。
        /// 由主窗口定时器在边界变化时调用，保证面板始终贴住任务栏顶而不遮挡。
        /// </summary>
        public void ReanchorBottomPanel()
        {
            if (!_visibilityController.IsVisible) return;
            if (_isDragging) return;

            bool bottomAnchored =
                _currentEdge == "Bottom" ||
                _lastProcessedRegion == EdgeRegion.Bottom_Left ||
                _lastProcessedRegion == EdgeRegion.Bottom_Center ||
                _lastProcessedRegion == EdgeRegion.Bottom_Right ||
                _lastProcessedRegion == EdgeRegion.BottomLeft ||
                _lastProcessedRegion == EdgeRegion.BottomRight;

            if (!bottomAnchored) return;
            // ★ 联动切换动画进行中跳过（避免直接设置被动画循环覆盖/打架），动画完成后后续刷新会补锚定
            if (_shapeAnimator.IsTransformAnimating) return;
            // ★ 渲染帧跟随激活时跳过：跟随 provider 每帧用最新边界重算目标，无需手动重锚
            if (_shapeAnimator.IsFollowActive) return;

            var wa = GetPanelWorkArea();
            double screenW = wa.Width;
            double screenH = wa.Height;
            double w = _window.Width;
            double h = _window.Height;
            double left = Math.Max(0, Math.Min(_window.Left, screenW - w));
            double top = Math.Max(0, Math.Min(_bottomBoundary - h, screenH - h));

            // ★ 任务栏跟随使用平滑物理收敛，而非瞬移（动画关闭时由 ShapeAnimator 自动跳转）
            _shapeAnimator.SetPositionTargetWithoutReset(left, top);
        }

        /// <summary>
        /// 当前区域/边缘对应的锚定位置（面板应贴住的点）。
        /// </summary>
        public (double left, double top) GetCurrentAnchor()
        {
            var wa = GetPanelWorkArea();
            double sw = wa.Width;
            double sh = wa.Height;
            double w = _cachedWidth > 0 ? _cachedWidth : _window.Width;
            double h = _cachedHeight > 0 ? _cachedHeight : _window.Height;

            switch (_currentEdge)
            {
                case "Top":
                    return (Math.Max(0, Math.Min(_window.Left, sw - w)), 0);
                case "Bottom":
                    return (Math.Max(0, Math.Min(_window.Left, sw - w)), _bottomBoundary - h);
                case "Left":
                    return (0, Math.Max(0, Math.Min(_window.Top, sh - h)));
                case "Right":
                    return (sw - w, Math.Max(0, Math.Min(_window.Top, sh - h)));
                default:
                    return (Math.Max(0, Math.Min(_window.Left, sw - w)),
                            Math.Max(0, Math.Min(_window.Top, sh - h)));
            }
        }

        /// <summary>
        /// 以当前锚点滑入显示面板（由主窗口在边缘触发时调用）。
        /// </summary>
        public void ShowPanelAtAnchor()
        {
            var (left, top) = GetCurrentAnchor();
            _visibilityController.ShowAt(left, top, _currentEdge);
        }

        /// <summary>
        /// 键盘呼出指定区域面板（Ctrl+数字环）：绕过触发延时/防抖，直接加载内容并锚定显示。
        /// 角部默认规则与鼠标触发一致：右上角未配置「窗口控制」时不呼出（安全区，无副作用）。
        /// 显示后由 MainWindow 置热键钉住（不自动隐藏；再按同键收起）。
        /// </summary>
        public void SummonRegion(EdgeRegion region)
        {
            try
            {
                if (region == EdgeRegion.Unknown) return;
                // ★ 右上角安全区：默认不设面板 → 不呼出（Ctrl+9 无副作用）
                if (region == EdgeRegion.TopRight &&
                    _settings.GetRegionPanel("TopRight") != "WindowControl")
                {
                    return;
                }
                if (_isDragging) return;

                var wa = GetPanelWorkArea();
                double sw = wa.Width;
                double sh = wa.Height;
                bool corner = region == EdgeRegion.TopLeft || region == EdgeRegion.TopRight ||
                              region == EdgeRegion.BottomLeft || region == EdgeRegion.BottomRight;

                string type, key;
                if (corner)
                {
                    key = region.ToString();
                    string custom = _settings.GetRegionPanel(key);
                    type = custom != "Default" && IsValidPanelType(custom) ? custom : "Placeholder";
                    _currentEdge = region is EdgeRegion.BottomLeft or EdgeRegion.BottomRight ? "Bottom" : "Top";
                }
                else
                {
                    type = GetRegionTypeFromEnum(region);
                    key = GetRegionKey(region);
                    _currentEdge = GetEdgeName(region);
                }

                _lastRegionType = type;
                _currentRegionKey = key;
                _lastProcessedRegion = region;
                RegionChanged?.Invoke(type, key);   // 加载内容（同 type 复用缓存实例）
                SizeController.SetMode(type, key);

                double w, h, left = 0, top = 0;
                if (corner)
                {
                    // 与 ProcessCorner 一致：内容自适应（AutoFit）或用户尺寸
                    if (_settings.AutoFitOnTrigger)
                    {
                        (w, h) = SizeController.MeasurePlaceholderTargetSize();
                    }
                    else
                    {
                        var (userW, userH) = _settings.GetUserSize(key);
                        w = Math.Max(100, Math.Min(userW >= 100 ? userW : sw * 2.0 / 7.0, sw * 0.8));
                        h = Math.Max(100, Math.Min(userH >= 100 ? userH : sw * 2.0 / 7.0, sh * 0.8));
                    }
                    _cachedWidth = w; _cachedHeight = h; _lastTargetW = w; _lastTargetH = h;
                    switch (region)
                    {
                        case EdgeRegion.TopLeft: break;
                        case EdgeRegion.TopRight: left = sw - w; break;
                        case EdgeRegion.BottomLeft: top = _bottomBoundary - h; break;
                        case EdgeRegion.BottomRight: left = sw - w; top = _bottomBoundary - h; break;
                    }
                }
                else
                {
                    (w, h) = GetTargetSizeForRegion(type, key);
                    _cachedWidth = w; _cachedHeight = h; _lastTargetW = w; _lastTargetH = h;
                    _shapeAnimator.SetSizeKeepPositionDirect(w, h);
                    // ★ 键盘呼出瞄准：中段取该边中点、端段贴向对应角，使面板落在目标边段。
                    //   不沿用窗口当前位置作锚点——窗口隐藏（屏幕外）时旧锚点会被钳到边角，
                    //   导致「上/下/左/右边中段」错误地落在边缘端部。
                    double mx = sw / 2, my = sh / 2;
                    switch (region)
                    {
                        case EdgeRegion.Top_Left: mx = 0; break;
                        case EdgeRegion.Top_Right: mx = sw; break;
                        case EdgeRegion.Bottom_Left: mx = 0; break;
                        case EdgeRegion.Bottom_Right: mx = sw; break;
                        case EdgeRegion.Left_Top: my = 0; break;
                        case EdgeRegion.Left_Bottom: my = sh; break;
                        case EdgeRegion.Right_Top: my = 0; break;
                        case EdgeRegion.Right_Bottom: my = sh; break;
                        // Top_Center/Bottom_Center/Left_Center/Right_Center 保持 0.5 → 居中
                    }
                    (left, top) = CalculatePosition(region, mx, my, sw, sh, w, h);
                }

                LogManager.Debug(
                    $"键盘呼出 region={region} type={type} key={key} → 目标({left:0},{top:0}) 屏幕{sw:0}x{sh:0}");
                _visibilityController.ShowAt(left, top, _currentEdge);
                _visibilityController.CurrentRegionKey = key;
                _timing.ResetTriggerDelay();
                _timing.ResetDebounce();
            }
            catch (Exception ex)
            {
                LogManager.Error("键盘呼出区域面板异常: " + region, ex);
            }
        }

        /// <summary>
        /// 鼠标在面板内时：仅跟随边缘滑动更新位置，不切换区域/内容。
        /// 解决“面板内移动鼠标位置不实时跟手”的问题。
        /// </summary>
        public void FollowMouseInPanel(EdgeRegion region, double mouseX, double mouseY,
            double screenWidth, double screenHeight)
        {
            if (region == EdgeRegion.Unknown || _isFlying || _isDragging ||
                DateTime.Now < _dragEndCooldownUntil) return;

            string edge = GetEdgeName(region);
            if (string.IsNullOrEmpty(edge) || edge != _currentEdge) return;

            // 角落不在此处理（角落面板不跟随鼠标滑动）
            if (region == EdgeRegion.TopLeft || region == EdgeRegion.TopRight ||
                region == EdgeRegion.BottomLeft || region == EdgeRegion.BottomRight) return;

            // 同一边内滑动到不同区域（如 Bottom_Left → Bottom_Center）：及时切换模态
            string regionKey = GetRegionKey(region);
            string regionType = GetRegionTypeFromEnum(region);

            // ★ 仅内容类型变化才切换：同边滑动（同 type）只位置跟随，避免频繁切换动画抖动
            if (regionType != _lastRegionType)
            {
                // 防抖：避免在区域边界来回抖动导致内容反复重建（FollowMouseInPanel 语义：每次变化刷新计时）
                if (_timing.ShouldDebounceAndRefresh(region, _settings.RegionDebounceMs)) return;

                _lastRegionType = regionType;
                _currentRegionKey = regionKey;
                SizeController.SetMode(regionType, regionKey);

                var (w, h) = GetTargetSizeForRegion(regionType, regionKey);
                var (left, top) = CalculatePosition(region, mouseX, mouseY, screenWidth, screenHeight, w, h);
                // ★ 跟手原则（同拖窗口）：位置走渲染帧实时跟随；尺寸保持，稳定后形变；
                //   内容不立即加载（移动期间静默），稳定后 CompletePendingSwitch 加载
                _lastTargetW = w;
                _lastTargetH = h;
                // ★ 快速切换 ≥3 次（时间窗口内，不限面板种类）→ 图标模态延迟加载；
                //   ≤2 次 → 直接加载（渲染帧间立即，不等稳定——延迟反而显得慢）
                if (IsRapidSwitching())
                {
                    _pendingSwitchType = regionType;
                    _pendingSwitchKey = regionKey;
                    SwitchStarted?.Invoke(regionType, regionKey);
                    // ★ 图标模态期间不更新 _cachedWidth/Height：窗口保持进入图标模态前的实际尺寸
                }
                else
                {
                    // ★ 直接加载（内容先同步就位，尺寸在内容就位后测量）：
                    //   首次进入时容器里还是上一面板内容，先测会量到错误内容（窄/方/长条）。
                    //   测量前 WidgetSwitcher 强制一次布局（UpdateLayout），结果确定；
                    //   目标尺寸缓存命中时零测量开销。尺寸动画统一在此驱动，
                    //   OnPanelContentChanged 经 _directLoadInProgress 跳过原子跳变（不打架闪烁）。
                    _directLoadInProgress = true;
                    RegionChanged?.Invoke(regionType, regionKey);
                    var (w2, h2) = GetTargetSizeForRegion(regionType, regionKey);
                    _cachedWidth = w2;
                    _cachedHeight = h2;
                    _lastTargetW = w2;
                    _lastTargetH = h2;
                    // ★ 尺寸形变 + 位置同步：用新尺寸的贴边锚点，动画中间帧即保持贴边
                    var (aLeft, aTop) = CalculatePosition(region, mouseX, mouseY, screenWidth, screenHeight, w2, h2);
                    _shapeAnimator.AnimateSizeToKeepAnchor(aLeft, aTop, w2, h2, () => _directLoadInProgress = false);
                }
                StartFollowContext(region, screenWidth, screenHeight, 1.0);
                return;
            }

            // 同区域：激活渲染帧实时跟随（Windows 拖拽式跟手）。
            // ★ 切换动画进行中跳过（等动画完成再恢复跟随）
            if (_shapeAnimator.IsTransformAnimating) return;
            StartFollowContext(region, screenWidth, screenHeight, 1.0);
        }

        private WindowSizeController SizeController =>
            _sizeController ?? throw new InvalidOperationException("WindowSizeController 尚未附加");

        // ========================================
        //  IEdgeTriggerController 接口方法
        // ========================================

        public void OnMouseMove(EdgeRegion region, double mouseX, double mouseY, bool isInsidePanel)
        {
        }

        public void Reset()
        {
            ClearEdge();
            _isFlying = false;
            _isClinging = false;
        }

        public bool ShouldPreventAutoHide()
        {
            return _isClinging || _isFlying || _isDragging;
        }

        /// <summary>
        /// 引潮开关变化：关闭时若正在跟随，立即停止（面板停住，回普通模式）。
        /// MainWindow 在设置变更时调用。
        /// </summary>
        public void SetClingModeEnabled(bool enabled)
        {
            if (!enabled && _isClinging)
            {
                StopClinging();
            }
        }

        public void OnFlyCompleted()
        {
            if (!_isFlying || _flyCompletedTriggered) return;

            _flyCompletedTriggered = true;
            _isFlying = false;

            // ★ 内容切换 + 落位（正常飞行完成）
            CompleteFlyContent();

            _flyingTargetRegionType = "";
            _flyingTargetRegionKey = "";
            _flyingTargetEdge = "";
            FlyCompleted?.Invoke();
        }

        /// <summary>
        /// 飞行目标落位：切换目标区域内容并重新锚定到目标边。
        /// 正常完成（OnFlyCompleted）与异常兜底（超时强制复位）共用，
        /// 保证飞行一定切换到正确的面板（修复"飞完还是原面板"）。
        /// </summary>
        private void CompleteFlyContent()
        {
            if (string.IsNullOrEmpty(_flyingTargetRegionType)) return;

            _lastRegionType = _flyingTargetRegionType;
            _currentRegionKey = _flyingTargetRegionKey;
            // ★ 内容与尺寸已在 StartFlying 就位（RegionChanged + _flyingTargetW/H 预计算），
            //   这里不再重新加载内容/测量，直接使用目标尺寸锚定——到达即切换完成
            double w = _flyingTargetW > 0 ? _flyingTargetW : _window.Width;
            double h = _flyingTargetH > 0 ? _flyingTargetH : _window.Height;
            _cachedWidth = w;
            _cachedHeight = h;

            // ★ 用飞行目标屏（StartFlying 时鼠标所在屏）重锚，而非窗口当前屏——
            //   飞行中窗口中心可能仍在旧屏，用旧屏边界会把面板锚到错误的显示器。
            double screenW = _flyingTargetScreenW > 0 ? _flyingTargetScreenW : SystemParameters.PrimaryScreenWidth;
            double screenH = _flyingTargetScreenH > 0 ? _flyingTargetScreenH : SystemParameters.PrimaryScreenHeight;

            // ★ 按目标边缘重新锚定后原子应用完整矩形，
            //   避免"位置按旧尺寸飞行、到站后尺寸突变导致贴边错位"
            double left = _shapeAnimator.CurrentLeft;
            double top = _shapeAnimator.CurrentTop;
            switch (_flyingTargetEdge)
            {
                case "Top": top = 0; break;
                case "Bottom": top = _bottomBoundary - h; break;
                case "Left": left = 0; break;
                case "Right": left = screenW - w; break;
            }
            left = Math.Max(0, Math.Min(left, screenW - w));
            top = Math.Max(0, Math.Min(top, screenH - h));
            // ★ 飞行到达后位置+尺寸平滑收尾（物理收敛），替代原子跳转——落地不"顿"一下
            _shapeAnimator.SetPositionAndSizeTarget(left, top, w, h);


            // 以实际生效尺寸回填缓存（WPF 最小尺寸等可能钳制目标值）
            _cachedWidth = _window.Width;
            _cachedHeight = _window.Height;
        }

        public void OnStickToMouseSuccess() { }

        // ========================================
        //  核心业务方法
        // ========================================

        public void ProcessRegion(EdgeRegion region, double mouseX, double mouseY, double screenWidth, double screenHeight)
        {
            // ========== 0. 前置守卫 ==========
            if (region == EdgeRegion.Unknown || _isDragging ||
                DateTime.Now < _dragEndCooldownUntil) return;

            // 飞行状态：正常完成由 OnFlyCompleted 复位；动画被打断（JumpTo/StopAll）时超时兜底复位
            if (_isFlying)
            {
                if (DateTime.Now > _flyTimeoutAt)
                {
                    _isFlying = false;
                    _flyCompletedTriggered = true; // 防止迟到的 OnFlyCompleted 重复切换
                    // ★ 超时兜底也要完成内容切换与落位，避免"飞完还是原面板"
                    CompleteFlyContent();
                    _flyingTargetRegionType = "";
                    _flyingTargetRegionKey = "";
                    _flyingTargetEdge = "";
                }
                else
                {
                    return;
                }
            }

            if (_isClinging) StopClinging();

            // ★ 右上角默认不呼出面板（避免影响关闭窗口的体验），如已显示则隐藏。
            //   仅当显式配置为"窗口操作中心"（WindowControl）时正常进入面板逻辑。
            if (region == EdgeRegion.TopRight &&
                _settings.GetRegionPanel("TopRight") != "WindowControl")
            {
                if (_visibilityController.IsVisible) _visibilityController.Hide();
                return;
            }

            // ★ TopRight 默认不是角落（安全区不呼出）；配置"窗口操作中心"后按角落面板处理
            bool isCorner = region is EdgeRegion.TopLeft or EdgeRegion.BottomLeft or EdgeRegion.BottomRight ||
                (region == EdgeRegion.TopRight &&
                 _settings.GetRegionPanel("TopRight") == "WindowControl");

            // ========== 1. 触发延时（停留才触发，防误触）：面板隐藏时生效 ==========
            // 放在所有区域（含角落）处理之前，保证"所有边角都可单独设置触发延时"
            if (!_visibilityController.IsVisible)
            {
                if (!TriggerDelayPassed(region)) return;
            }

            // ========== 2. 角落 ==========
            if (isCorner)
            {
                ProcessCorner(region, screenWidth, screenHeight);
                return;
            }

            // ========== 3. 边缘通用状态 ==========
            string currentEdgeName = GetEdgeName(region);
            _currentEdge = currentEdgeName;
            SizeController.UpdateHandlePosition(_currentEdge);
            _visibilityController.UpdateEdge(_currentEdge);

            // 防抖（区域快速抖动过滤）
            if (_timing.ShouldDebounce(region, _settings.RegionDebounceMs)) return;

            string regionType = GetRegionTypeFromEnum(region);
            string regionKey = GetRegionKey(region);

            // ========== 4. 跨边移动（统一为跟随逻辑） ==========
            // ★ 与边缘滑动同一逻辑：位置由渲染帧跟随（绝对/lerp，速度由 FlyDurationMs 控制），
            //   面板大小不变（切换时保持），像拖 Windows 窗口一样顺滑；
            //   内容切换/中置/稳定后更新走第 5 步通用流程。
            //   （不再使用独立的飞行动画——那与跟随是两套逻辑，且不跟手）

            // ========== 5. 区域切换 / 显示 / 跟随 ==========
            // ★ 仅"内容类型"变化才触发切换动画：同一边内滑动（如 Bottom 左/右都是任务栏）
            //   内容相同只做位置跟随（渲染帧跟手），避免频繁切换动画导致抖动不跟手。
            bool regionChanged = regionType != _lastRegionType;

            double targetW, targetH;
            if (regionChanged)
            {
                (targetW, targetH) = GetTargetSizeForRegion(regionType, regionKey);
            }
            else
            {
                targetW = _cachedWidth > 0 ? _cachedWidth : _window.Width;
                targetH = _cachedHeight > 0 ? _cachedHeight : _window.Height;
            }
            var (targetLeft, targetTop) = CalculatePosition(region, mouseX, mouseY, screenWidth, screenHeight, targetW, targetH);

            if (regionChanged)
            {
                _lastRegionType = regionType;
                _currentRegionKey = regionKey;
                SizeController.SetMode(regionType, regionKey);

                if (_visibilityController.IsVisible)
                {
                    // ★ 跟手原则（同拖窗口）：位置走渲染帧实时跟随（无动画滞后）；
                    //   尺寸保持不形变（快速切换不卡顿），目标尺寸记录，稳定后由 MainWindow 形变；
                    //   内容不立即加载——移动期间完全静默（不替换/不布局），鼠标停止稳定后才加载
                    _lastTargetW = targetW;
                    _lastTargetH = targetH;
                    // ★ 快速切换 ≥3 次（时间窗口内，不限面板种类）→ 图标模态延迟加载：
                    //   移动期间内容完全静默，停稳后只加载最终面板；≤2 次 → 直接加载
                    if (IsRapidSwitching())
                    {
                        _pendingSwitchType = regionType;
                        _pendingSwitchKey = regionKey;
                        SwitchStarted?.Invoke(regionType, regionKey);
                        // ★ 图标模态期间不更新 _cachedWidth/Height：窗口保持"进入图标模态前的
                        //   实际尺寸"（最后一次非图标面板的尺寸），provider 用该尺寸算位置 →
                        //   中心持续对齐鼠标，且不随目标面板尺寸变化偏移
                    }
                    else
                    {
                        // ★ 直接加载（内容先同步就位，尺寸在内容就位后测量）：
                        //   首次进入时容器里还是上一面板内容，先测会量到错误内容（窄/方/长条）。
                        //   测量前 WidgetSwitcher 强制一次布局（UpdateLayout），结果确定；
                        //   目标尺寸缓存命中时零测量开销。尺寸动画统一在此驱动，
                        //   OnPanelContentChanged 经 _directLoadInProgress 跳过原子跳变（不打架闪烁）。
                        _directLoadInProgress = true;
                        RegionChanged?.Invoke(regionType, regionKey);
                        var (w2, h2) = GetTargetSizeForRegion(regionType, regionKey);
                        _cachedWidth = w2;
                        _cachedHeight = h2;
                        _lastTargetW = w2;
                        _lastTargetH = h2;
                        // ★ 尺寸形变 + 位置同步：用新尺寸的贴边锚点，动画中间帧即保持贴边
                        //   （修复：任务栏→小组件等切换时原为"左上角固定缩放→完成才贴边"）
                        var (aLeft, aTop) = CalculatePosition(region, mouseX, mouseY, screenWidth, screenHeight, w2, h2);
                        _shapeAnimator.AnimateSizeToKeepAnchor(aLeft, aTop, w2, h2, () => _directLoadInProgress = false);
                    }
                    StartFollowContext(region, screenWidth, screenHeight, 1.0);
                }
                else
                {
                    // ★ 隐藏→显示：内容先同步就位，尺寸在内容就位后测量并就地应用
                    //   （首次进入时容器里还是旧面板内容，先测必错 → 窄/方/长条）
                    _directLoadInProgress = true;
                    RegionChanged?.Invoke(regionType, regionKey);
                    var (w2, h2) = GetTargetSizeForRegion(regionType, regionKey);
                    _cachedWidth = w2;
                    _cachedHeight = h2;
                    _lastTargetW = w2;
                    _lastTargetH = h2;
                    // 先就地改尺寸，再由滑入动画带向锚点（位置按新尺寸重算）
                    _shapeAnimator.SetSizeKeepPositionDirect(w2, h2);
                    var (l2, t2) = CalculatePosition(region, mouseX, mouseY, screenWidth, screenHeight, w2, h2);
                    _visibilityController.ShowAt(l2, t2, GetEdgeName(region));
                    _directLoadInProgress = false;
                    // ★ 激活渲染帧跟随：面板中心实时追鼠标（首次触发也必须跟手）
                    StartFollowContext(region, screenWidth, screenHeight, 1.0);
                }

                // ★ 不回填 _cachedWidth/Height：形变进行中 _window.Height 是动画中间值，
                //   回填会把"目标尺寸"污染成中间值 → provider 用错误尺寸算位置 → 中心错位。
                //   目标尺寸已由 GetTargetSizeForRegion 写入 _cachedWidth/_cachedHeight（见该方法内两处赋值）。
            }
            else
            {
                // 同类型也同步区域键（隐藏延时按当前区域读取）
                _currentRegionKey = regionKey;
                if (_visibilityController.IsVisible)
                {
                    // ★ 同区域（面板可见）：激活渲染帧实时跟随（Windows 拖拽式跟手）；
                    //   切换动画进行中跳过
                    if (_shapeAnimator.IsTransformAnimating) return;
                    StartFollowContext(region, screenWidth, screenHeight, 1.0);
                }
                else
                {
                    // 同区域但面板隐藏（鼠标离开后回到同一边）：按鼠标位置直接显示
                    _shapeAnimator.SetSizeKeepPositionDirect(targetW, targetH);
                    // ★ 同步目标尺寸到 _cached + 激活渲染帧跟随（首次触发中心追鼠标）
                    _cachedWidth = targetW;
                    _cachedHeight = targetH;
                    _visibilityController.ShowAt(targetLeft, targetTop, GetEdgeName(region));
                    StartFollowContext(region, screenWidth, screenHeight, 1.0);
                }
            }

            _visibilityController.CurrentRegionKey = _currentRegionKey;
            _lastProcessedRegion = region;
        }

        /// <summary>
        /// 触发延时判定（面板隐藏时）：鼠标进入区域需停留 N ms 才放行（防误触）。
        /// 区域变化会重新计时；返回 true = 放行显示。委托 EdgeTimingState。
        /// </summary>
        private bool TriggerDelayPassed(EdgeRegion region) => _timing.TriggerDelayPassed(region);

        /// <summary>
        /// 拖拽/调整大小结束时调用：短暂抑制边缘触发，避免鼠标仍在屏幕边缘时误切换区域。
        /// </summary>
        public void NotifyDragEnded()
        {
            _dragEndCooldownUntil = DateTime.Now.AddMilliseconds(1200);
        }

        public void ClearEdge()
        {
            _currentEdge = "";
            _lastRegionType = "";
            _lastProcessedRegion = EdgeRegion.Unknown;
            _currentRegionKey = "";
            _isClinging = false;
            _timing.ResetTriggerDelay();
            _timing.ResetDebounce();
            _directLoadInProgress = false;
        }

        public void ApplySizeStrategy()
        {
            SizeController.ApplySizeForCurrentMode();
        }

        // ========================================
        //  引潮
        // ========================================

        public bool StartClinging(double mouseX, double mouseY)
        {
            if (!_settings.ClingModeEnabled) return false;
            if (_isClinging) return false;
            if (_isFlying) return false;
            if (!_visibilityController.IsVisible) return false;

            // ★ 防横跳：鼠标还在面板（严格矩形）内时不启动跟随。
            //   ★ 修复：restartMargin 从 2px 改为 0——原 2px 迟滞会让"追上停住后鼠标刚移出面板"
            //   被误判为仍在面板内 → StartClinging 返回 false → tick else 走 HideWithDelay → 面板消失。
            //   现在严格按面板矩形：出了面板就重新追（tick 的 isInsidePanel 分支已处理面板内交互）。
            const double restartMargin = 0;
            bool inside = mouseX >= _window.Left - restartMargin &&
                          mouseX <= _window.Left + _window.Width + restartMargin &&
                          mouseY >= _window.Top - restartMargin &&
                          mouseY <= _window.Top + _window.Height + restartMargin;
            if (inside) return false;

            // ★ 不再用"距屏幕边缘 80px 内拒绝启动"：边缘触发的面板就在屏幕边缘，
            //   鼠标在边缘带内时若拒绝启动，面板永远不追（用户反馈"边缘触发后不追"）。
            //   防乒乓已由 tick 的 IsInClinging→UpdateClinging 分支覆盖（cling 中不响应边缘触发），
            //   cling 目标也有屏幕钳制（UpdateClinging 的 blockedByEdge 处理贴边停止）。
            _isClinging = true;

            // ★ 启动跟随 = 取消任何隐藏意图：避免"鼠标移出面板 → HideWithDelay 已设延时 →
            //   紧接着 StartClinging 启动跟随，却被 200ms 后的延时隐藏秒杀"。
            _visibilityController.CancelHide();

            // ★ 跟随用独立快速参数（低配设备减少拖尾渲染）
            _shapeAnimator.SetClingParameters();
            double halfW = _window.Width / 2;
            double halfH = _window.Height / 2;
            // ★ 修复：启动目标同样钳制在屏幕内（否则鼠标在屏幕边缘时面板追出屏幕）
            var startWa = GetMouseWorkArea(mouseX, mouseY);
            double startLeft = Math.Max(0, Math.Min(startWa.Width - _window.Width, mouseX - halfW));
            double startTop = Math.Max(0, Math.Min(startWa.Height - _window.Height, mouseY - halfH));
            _shapeAnimator.SetClingTarget(startLeft, startTop);
            StartClingingRequested?.Invoke();
            return true;
        }

        public void UpdateClinging(double mouseX, double mouseY)
        {
            if (!_isClinging) return;
            if (_isFlying) return;

            // ★ 状态机（用户确认：停是默认，移出面板触发跟随；停止 = 面板中心点追到鼠标点）
            //   - 跟随：面板中心点匀速飞向鼠标点（FlyDurationMs 管控速度，钳制屏内）
            //   - 停止：面板中心点追到鼠标点（中心距离 ≤ 2px 视为追到）→ 停，鼠标在面板中心可操作
            //   - 无"追不上超时"：鼠标到哪面板跟到哪；隐藏仅由 tick 的延时隐藏负责。
            //   - 省电不省机制：跟随照常（省电只降帧率）。
            double halfW = _window.Width / 2;
            double halfH = _window.Height / 2;
            double centerX = _window.Left + halfW;
            double centerY = _window.Top + halfH;

            // ★ 停止条件（用户确认）：面板中心点追到鼠标点（中心距离 ≤ 2px 即追到）
            //   **且** 鼠标在面板上（面板内+边）→ 停。
            //   追逐中只认"中心追到鼠标"这一个停止条件——中心没追上（哪怕鼠标已在面板矩形内）
            //   继续追；中心追到后（鼠标在面板中心）再判定"鼠标在面板上"→ 在则停。
            //   T≤0 直接设置时面板瞬移到鼠标、中心=鼠标，但鼠标在面板外（跟手中）→ 不停，
            //   面板持续瞬移跟手；鼠标停/进入面板 → 在面板上 → 停。
            const double CatchThreshold = 2;
            bool centerCaught = Math.Abs(mouseX - centerX) <= CatchThreshold &&
                                Math.Abs(mouseY - centerY) <= CatchThreshold;
            if (centerCaught && IsMouseInsidePanelRect(mouseX, mouseY))
            {
                _visibilityController.CancelHide();
                StopClinging();
                _visibilityController.Show();
                StickToMouseRequested?.Invoke();
                OnStickToMouseSuccess();
                return;
            }

            // ★ 跟随：目标 = 鼠标位置（面板中心点对准鼠标点），钳制屏内
            var (clingLeft, clingTop) = ComputeClingTarget(mouseX, mouseY);
            _shapeAnimator.SetClingTarget(clingLeft, clingTop);
        }

        /// <summary>
        /// 计算引潮目标位置：面板中心对准鼠标点，磁铁吸附 + 屏幕钳制。
        /// 由 tick 的 UpdateClinging 和渲染帧的 ClingTargetProvider（实时跟手）共用。
        /// </summary>
        public (double left, double top) ComputeClingTarget(double mouseX, double mouseY)
        {
            double halfW = _window.Width / 2;
            double halfH = _window.Height / 2;
            double clingLeft = mouseX - halfW;
            double clingTop = mouseY - halfH;
            var clingWa = GetMouseWorkArea(mouseX, mouseY);
            double csw = clingWa.Width;
            double csh = clingWa.Height;
            // 磁铁吸附（引潮的设计）：面板边缘距屏幕边 < 吸附范围 → 吸到该边贴边
            int snap = _settings.SnapRangePx;
            if (snap > 0)
            {
                double dL = clingLeft;
                double dR = csw - (clingLeft + _window.Width);
                double dT = clingTop;
                double dB = csh - (clingTop + _window.Height);
                double minD = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
                if (minD < snap)
                {
                    if (minD == dL) clingLeft = 0;
                    else if (minD == dR) clingLeft = csw - _window.Width;
                    else if (minD == dT) clingTop = 0;
                    else clingTop = csh - _window.Height;
                    // 角落吸附
                    if (minD == dT || minD == dB)
                    {
                        if (dL < snap) clingLeft = 0;
                        else if (dR < snap) clingLeft = csw - _window.Width;
                    }
                    else
                    {
                        if (dT < snap) clingTop = 0;
                        else if (dB < snap) clingTop = csh - _window.Height;
                    }
                }
            }
            // ★ 目标始终钳制在屏幕内（吸附只吸附到边，鼠标在边缘附近/快速移动时仍可能越界）
            clingLeft = Math.Max(0, Math.Min(csw - _window.Width, clingLeft));
            clingTop = Math.Max(0, Math.Min(csh - _window.Height, clingTop));
            return (clingLeft, clingTop);
        }

        /// <summary>
        /// 渲染帧"追到目标/面板内停止"回调：完整停止跟随（与 tick 停止分支一致）。
        /// 渲染循环到达目标（含面板内原地停）时触发，立即复位 _isClinging——
        /// 否则停后鼠标在面板内一动（绕圈），tick 的 UpdateClinging 会重启追赶。
        /// </summary>
        private void OnClingArrived()
        {
            if (!_isClinging) return;
            _visibilityController.CancelHide();
            StopClinging();
            _visibilityController.Show();
            StickToMouseRequested?.Invoke();
            OnStickToMouseSuccess();
        }

        /// <summary>鼠标是否在面板矩形内（DIP，严格边界）——面板内严格不追的依据。</summary>
        private bool IsMouseInsidePanelRect(double mouseX, double mouseY)
        {
            return mouseX >= _window.Left && mouseX <= _window.Left + _window.Width &&
                   mouseY >= _window.Top && mouseY <= _window.Top + _window.Height;
        }

        private void StopClinging()
        {
            if (!_isClinging) return;   // ★ 幂等：渲染帧与 tick 都可能触发停止
            _isClinging = false;
            // ★ 退出跟随：恢复普通动画参数（防止后续边缘/滑入动画仍用慢速 cling 参数）
            _shapeAnimator.ExitClingParameters();
            // ★ 清除残留的边缘状态：面板跟随鼠标停住后不再属于任何触发边。
            //   否则 isInsidePanel 分支的 FollowMouseInPanel 会因 edge == _currentEdge
            //   把面板拉回触发边 → "触发边 ↔ 鼠标"来回横跳。
            _currentEdge = "";
            _lastRegionType = "";
            _lastProcessedRegion = EdgeRegion.Unknown;
            _currentRegionKey = "";
            _timing.ResetTriggerDelay();
            _timing.ResetDebounce();
            StopClingingRequested?.Invoke();
        }

        public bool IsInClinging() => _isClinging;

        // ========================================
        //  飞行
        // ========================================

        private void StartFlying(EdgeRegion target, double mx, double my, double sw, double sh, string type, string key)
        {
            _isFlying = true;
            _flyCompletedTriggered = false;
            _flyTimeoutAt = DateTime.Now.AddMilliseconds(_settings.FlyDurationMs + 1000); // 飞行时长 + 1s 余量

            // ★ 提前就位：飞行开始前切换目标内容并预计算目标尺寸。
            //   内容加载（缓存实例）+ 尺寸测量（MeasureContent）在飞行途中完成，
            //   到达后 CompleteFlyContent 只做锚定——消除"飞到后卡一下才切换"的延迟感。
            _lastRegionType = type;
            _currentRegionKey = key;
            SizeController.SetMode(type, key);
            RegionChanged?.Invoke(type, key);

            var (w, h) = GetTargetSizeForRegion(type, key);
            _flyingTargetW = w;
            _flyingTargetH = h;

            // ★ 飞行期间保持当前尺寸（内容已就位），到达后原子应用目标尺寸+锚定
            double curW = _window.Width;
            double curH = _window.Height;
            var (left, top) = CalculatePosition(target, mx, my, sw, sh, curW, curH);

            int duration = _settings.FlyDurationMs;

            _shapeAnimator.SetFlyParameters(duration);
            _shapeAnimator.SetSizeDirect(curW, curH);
            _shapeAnimator.StartFly(left, top);

            _flyingTargetRegionType = type;
            _flyingTargetRegionKey = key;
            _flyingTargetEdge = GetEdgeName(target);
            _flyingTargetScreenW = sw;
            _flyingTargetScreenH = sh;
            _lastProcessedRegion = target;
        }

        // ========================================
        //  内部辅助方法
        // ========================================

        private void ProcessCorner(EdgeRegion region, double sw, double sh)
        {
            string key = region.ToString();
            // ★ 角落面板类型：设置里可自定义，默认按现有分工（通知坞/最近使用/快捷设置）
            string custom = _settings.GetRegionPanel(key);
            string type = custom != "Default" && IsValidPanelType(custom) ? custom : "Placeholder";
            _currentEdge = region is EdgeRegion.BottomLeft or EdgeRegion.BottomRight ? "Bottom" : "Top";

            // ★ 每个角落有独立内容（通知坞 / 最近使用 / 系统开关），仅进入时切换一次
            if (type != _lastRegionType || key != _currentRegionKey)
            {
                _lastRegionType = type;
                _currentRegionKey = key;
                SizeController.SetMode(type, key);
                RegionChanged?.Invoke(type, key);
            }

            // ★ 角落面板尺寸：
            //   - AutoFitOnTrigger：内容自适应（快捷开关/通知/最近的内容比"方形"矮，
            //     固定方形在 2K/4K 屏可达 700+px 高，明显过高）
            //   - 否则用用户拖拽保存的尺寸
            double w, h;
            if (_settings.AutoFitOnTrigger)
            {
                (w, h) = SizeController.MeasurePlaceholderTargetSize();
            }
            else
            {
                var (userW, userH) = _settings.GetUserSize(key);
                w = Math.Max(100, Math.Min(userW >= 100 ? userW : sw * 2.0 / 7.0, sw * 0.8));
                h = Math.Max(100, Math.Min(userH >= 100 ? userH : sw * 2.0 / 7.0, sh * 0.8));
            }
            _cachedWidth = w;
            _cachedHeight = h;

            double left = 0, top = 0;
            switch (region)
            {
                case EdgeRegion.TopLeft: break;
                case EdgeRegion.TopRight: left = sw - w; break;
                case EdgeRegion.BottomLeft: top = _bottomBoundary - h; break;
                case EdgeRegion.BottomRight: left = sw - w; top = _bottomBoundary - h; break;
                default: return;
            }

            if (_visibilityController.IsVisible)
            {
                // ★ 已显示：角落切换同样平滑过渡（位置+尺寸物理收敛）
                //   即使类型未变（lastType==Placeholder）也应用目标尺寸：
                //   从边缘划入角落时窗口尺寸可能停留在旧面板（Widget），必须重设
                if (Math.Abs(_window.Width - w) > 2 || Math.Abs(_window.Height - h) > 2)
                {
                    _shapeAnimator.SetPositionAndSizeTarget(left, top, w, h);
                }
                else if (Math.Abs(_window.Left - left) > 2 || Math.Abs(_window.Top - top) > 2)
                {
                    _shapeAnimator.SetPositionAndSizeTarget(left, top, w, h);
                }
            }
            else
            {
                // ★ 隐藏→显示：先就地改尺寸，再由滑入动画带向角落（从对应边滑入）
                _shapeAnimator.SetSizeKeepPositionDirect(w, h);
                _visibilityController.ShowAt(left, top, _currentEdge);
            }
            _visibilityController.CurrentRegionKey = _currentRegionKey;
            _lastProcessedRegion = region;
        }

        // ★ 目标尺寸缓存：切换时动画立即启动（不阻塞于内容测量），
        //   内容变化/屏幕变化时失效（RegionChanged / 屏幕尺寸签名）。
        private readonly System.Collections.Generic.Dictionary<string, (double w, double h, double sw, double sh)> _targetSizeCache = new();

                private (double w, double h) GetTargetSizeForRegion(string type, string key)
        {
            var wa = GetPanelWorkArea();
            double sw = wa.Width;
            double sh = wa.Height;

            // ★ 尺寸缓存：切换时动画立即启动，不阻塞于内容测量（内容变化时由调用方失效）
            string cacheKey = type + "|" + key;
            if (_targetSizeCache.TryGetValue(cacheKey, out var cached) &&
                Math.Abs(cached.sw - sw) < 1 && Math.Abs(cached.sh - sh) < 1)
            {
                return (cached.w, cached.h);
            }

            var (w, h) = ComputeTargetSize(type, key, sw, sh);
            _targetSizeCache[cacheKey] = (w, h, sw, sh);
            return (w, h);
        }

        /// <summary>尺寸缓存失效：内容/设置变化后调用，下次切换重新测量。</summary>
        public void InvalidateTargetSizeCache(string? type = null, string? key = null)
        {
            if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(key))
            {
                _targetSizeCache.Clear();
                return;
            }
            var dead = new System.Collections.Generic.List<string>();
            foreach (var kv in _targetSizeCache)
            {
                string[] parts = kv.Key.Split('|');
                if (parts.Length == 2 &&
                    (string.IsNullOrEmpty(type) || parts[0] == type) &&
                    (string.IsNullOrEmpty(key) || parts[1] == key))
                {
                    dead.Add(kv.Key);
                }
            }
            foreach (var d in dead) _targetSizeCache.Remove(d);
        }

        private (double w, double h) ComputeTargetSize(string type, string key, double sw, double sh)
        {
            // ★ 区域形状设置（Default/方形/横条/竖条），让设置真正生效
            string regionShape = GetRegionShapeSetting(key);
            if (regionShape == "Square")
            {
                double s = Math.Max(100, Math.Min(sw * 0.4, 420));
                return (s, s);
            }
            if (regionShape == "StripH")
            {
                double hh = Math.Max(60, Math.Min(120, sh * 0.12));
                return (sw * 0.6, hh);
            }
            if (regionShape == "StripV")
            {
                double ww = Math.Max(60, Math.Min(120, sw * 0.12));
                return (ww, sh * 0.6);
            }

            // ★ 固定位置模式的固定形状
            string edge = GetEdgeFromKey(key);
            if (!string.IsNullOrEmpty(edge) && _settings.GetEdgeMode(edge) == "Fixed")
            {
                string fixedShape = _settings.GetFixedShape(edge);
                if (fixedShape == "Square")
                {
                    double s = Math.Max(100, Math.Min(sw * 0.4, 420));
                    return (s, s);
                }
                if (fixedShape == "StripH")
                {
                    return (sw * 0.6, Math.Max(60, Math.Min(120, sh * 0.12)));
                }
                if (fixedShape == "StripV")
                {
                    return (Math.Max(60, Math.Min(120, sw * 0.12)), sh * 0.6);
                }
            }

            if (type == "Taskbar")
            {
                // ★ 允许任务栏面板保存用户调整后的尺寸
                var (taskUserW, taskUserH) = _settings.GetUserSize(key);
                if (!_settings.AutoFitOnTrigger && taskUserW > 0 && taskUserH > 0)
                {
                    return (taskUserW, taskUserH);
                }
                return (sw * 2.0 / 3.0, Math.Max(_window.MinHeight, Math.Max(60, _taskbarHeightDips * 1.75)));
            }

            // ★ 通知坞 / 最近使用 / 快捷设置 / 角落占位：内容自适应（固定方形在 2K/4K 屏过高）
            if (type is "Notification" or "Recent" or "QuickSettings" or "Placeholder")
            {
                return SizeController.MeasurePlaceholderTargetSize();
            }

            var (userW, userH) = _settings.GetUserSize(key);
            if (!_settings.AutoFitOnTrigger && userW > 0 && userH > 0)
                return (userW, userH);

            // ★ Widget：按当前小组件标签内容实际尺寸自适应（内容尽量显示全，剪贴板/便签已内部限高）
            if (type == "Widget") return SizeController.MeasureWidgetTargetSize();
            // ★ AppHelper（画中画/媒体控制）：固定预设方形（420×340 DIP），各边统一。
            //   不用内容自适应：画中画内容随媒体会话变化，各边测得尺寸不一致（上宽下窄）。
            if (type == "AppHelper") return (420, 340);
            if (type == "AI") return (420, 400);
            // ★ 自定义面板：固定预设尺寸（420×340），内容由用户源码自行适配
            if (type.StartsWith("Custom:", StringComparison.Ordinal)) return (420, 340);
            return (420, 340);
        }
private string GetRegionShapeSetting(string key)
        {
            try
            {
                string[] parts = key.Split('_');
                if (parts.Length == 2)
                {
                    return _settings.GetRegionShape(parts[0], parts[1]);
                }
            }
            catch (Exception ex)
            {
                // 形状读不出来 → 回落到默认形状（面板仍能显示）
                LogManager.Debug($"[区域] 读取区域形状失败（按默认形状处理）key={key}：{ex.Message}");
            }
            return "Default";
        }

        private static string GetEdgeFromKey(string key) => EdgeRegionMapping.GetEdgeFromKey(key);

        private (double left, double top) CalculatePosition(EdgeRegion region, double mx, double my,
            double sw, double sh, double w, double h) =>
            EdgeRegionMapping.CalculatePosition(region, mx, my, sw, sh, w, h, _bottomBoundary,
                edge => _settings.GetEdgeMode(edge), edge => _settings.GetFixedOffset(edge));

        private string GetEdgeName(EdgeRegion r) => EdgeRegionMapping.GetEdgeName(r);

        private string GetRegionKey(EdgeRegion r) => EdgeRegionMapping.GetRegionKey(r);

        private string GetRegionTypeFromEnum(EdgeRegion r) =>
            EdgeRegionMapping.GetRegionTypeFromEnum(r,
                key => _settings.GetRegionPanel(key), EdgeRegionMapping.IsValidPanelType);

        private static bool IsValidPanelType(string type) => EdgeRegionMapping.IsValidPanelType(type);

        // ========================================
        //  多显示器：面板所在显示器整屏边界
        // ========================================

        /// <summary>
        /// 面板当前所在显示器的整屏边界（DIP，含任务栏区域，语义与原 PrimaryScreen* 一致）。
        /// 面板被拖到副屏或跟随鼠标跨屏后，锚定/尺寸计算以面板实际所在屏幕为准。
        /// </summary>
        private System.Windows.Rect GetPanelWorkArea()
        {
            return ScreenMetrics.GetScreenForWindow(
                _window.Left, _window.Top, _window.Width, _window.Height);
        }

        /// <summary>鼠标所在显示器的整屏边界（DIP，供跟随模式定位）。</summary>
        private static System.Windows.Rect GetMouseWorkArea(double mx, double my)
        {
            return ScreenMetrics.GetScreenForPoint(mx, my);
        }
    }
}