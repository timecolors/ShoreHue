using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ShoreHue.Core.Services.Configuration;

namespace ShoreHue.Animation
{
    /// <summary>
    /// 面板形变动画器（WPF 原生动画 + 渲染帧驱动）：
    /// - 切换（区域切换/飞行落地）：位置+尺寸同步动画（贴边锚定，无内容等比缩放），
    ///   内容随窗口真实布局（★ 不做 ScaleTransform 缩放）；
    /// - 滑入/滑出：位置 + 透明度 WPF 动画（尺寸不变，本就丝滑）；
    /// - 连续跟随（贴边滑动/引潮）：CompositionTarget.Rendering 渲染帧驱动（帧率可配）。
    /// 设置中 TransformDurationMs/TransformEasingType、ShowHideDurationMs/ShowHideEasingType 真实映射为动画参数。
    /// </summary>
    public class ShapeAnimator : IDisposable
    {
        private readonly Window _window;
        private readonly FrameworkElement _panel;
        private ISettingsService? _settings;
        private bool _animationsEnabled = true;
        private bool _disposed;

        // ===== 设置映射 =====
        private int _transformDurationMs = 250;
        private int _showHideDurationMs = 150;
        private IEasingFunction? _transformEasing;
        private IEasingFunction? _showHideEasing;

        // ===== 飞行 =====
        private int _flyDurationMs = 500;

        // ===== 渲染帧循环（跟随 + 引潮）=====
        private bool _renderingActive;
        private bool _followActive;            // 贴边跟随：每帧调用 provider 实时跟手（Windows 拖拽式）
        private bool _clingMode;               // 引潮：每帧 lerp 平滑趋近
        private double _clingTargetLeft;
        private double _clingTargetTop;

        // ★ 节能降帧（PowerSaver）：渲染帧每 N 帧才实际处理一次（跳帧）。
        //   CompositionTarget.Rendering 无法直接调帧率，用计数跳帧等效降频：
        //   0=每帧（60fps 满帧），1=每 2 帧（~30fps），2=每 3 帧（~20fps）。
        private int _frameSkip = 0;
        private int _frameCounter = 0;

        // ★ 跟随松紧（由设置 FlyDurationMs 映射）：拉满=1.0 绝对跟手（实时），调小=缓慢飞追
        private double _followLerp = 1.0;
        // ★ 中置状态（乱逛/切换期间）覆盖：强制绝对跟手（图标跟着鼠标逛）
        private double? _followLerpOverride;
        // ★ 滑入/滑出动画保护期（TickCount64 截止时刻）：SetShowHideTarget 启动动画后，
        //   OnRendering 的跟随分支在保护期内让位，避免"渲染帧直达目标"把动画瞬间化。
        //   根因：ShowAt(SetShowHideTarget) 与 StartFollowContext 同一 tick 连发，
        //   动画首帧 HasAnimatedProperties 尚为 false → 跟随分支提前写目标位置 → 动画被掐断。
        private long _followSuppressUntilTick;

        /// <summary>跟随目标提供者：渲染帧每帧调用，返回面板应处的 (left, top)。由业务层设置。</summary>
        public Func<(double left, double top)>? FollowPositionProvider { get; set; }

        /// <summary>引潮实时目标源：渲染帧每帧调用，返回 (目标位置, 鼠标是否在面板上)。
        /// 业务层设置为"实时读鼠标 → 钳制目标"，使目标更新频率 = 渲染帧，
        /// 消除"tick 30ms 才更新一次目标"的滞后卡顿。null = 用 SetClingTarget 设定的目标。
        /// ★ onPanel：鼠标在面板上（面板内+边）——T≤0 直接设置分支用它判定"追到即停"，
        ///   追逐中（T>0）只认"中心追到鼠标"，不受 onPanel 影响。</summary>
        public Func<(double left, double top, bool onPanel)>? ClingTargetProvider { get; set; }

        /// <summary>引潮"追到目标/面板内停止"事件：渲染循环到达目标（含面板内原地停）时触发。
        /// 业务层据此复位跟随状态（_isClinging=false）——否则渲染循环停后 tick 检测到
        /// 鼠标在面板内一动（中心偏差>2px）会再次 SetClingTarget 重启追赶（面板内绕圈也追）。</summary>
        public event Action? ClingArrived;

        // ===== 引潮（匀速飞行，由 FlyDurationMs 管控） =====
        // 设计（用户确认）：
        //  - 追的目标 = 面板中心 → 鼠标位置（钳制屏内，由业务层算好传 SetClingTarget）
        //  - 速度 = 飞行时间 FlyDurationMs 管控：0 = 直接设置（拖动窗口式最跟手）；
        //    调大 = 匀速慢速飞追（线性进度，无 lerp 指数衰减的"顿一下"）
        //  - 匀速实现：记录本次飞行起点 + 开始时间，每帧 pos = start + (target-start) × (elapsed/duration)
        //  - 帧率无关：用真实 elapsed（时间），不受跳帧影响
        private double _clingFlyDurationMs;      // 引潮飞行时长（来自设置 FlyDurationMs；0=直接设置）
        private bool _clingMouseOnPanel;         // 渲染帧最近一次 provider 判定：鼠标是否在面板上（T≤0 直接设置分支用）
        private long _lastFlyTick = Environment.TickCount64; // 跟随/引潮匀速飞行的上帧时刻（算 dt；TickCount64 单调 1ms 精度）
        private const double ClingStopDist = 0.5;          // 到达阈值（px）

        /// <summary>
        /// 向目标以**固定距离**移动（匀速，不减速）：每帧走 moveDist 像素，
        /// 到达目标（≤ moveDist 内）直接落定。moveDist = 速度 × dt（速度恒定 → 匀速）。
        /// 修复：原"每帧移动剩余距离比例"是指数衰减（临近目标变慢），用户要求匀速。
        /// </summary>
        private void MoveTowardFixedSpeed(double targetLeft, double targetTop, double moveDist)
        {
            double dx = targetLeft - _window.Left;
            double dy = targetTop - _window.Top;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= moveDist || dist < 0.5)
            {
                // ★ 落定写整数值：WPF 窗口位置底层是整数像素，写小数（如 508.5）会被取整，
                //   下一帧又差 0.5px → 永不收敛 → 每帧 SetWindowPos → 渲染自激循环（100% CPU）。
                //   取整 + 仅值不同才写：落定后不重复 SetWindowPos（WPF 对相同值也可能发原生消息，
                //   必须显式跳过，否则渲染帧每帧写相同值仍产生消息风暴）。
                double rl = Math.Round(targetLeft);
                double rt = Math.Round(targetTop);
                if (Math.Abs(_window.Left - rl) > 0.5 || Math.Abs(_window.Top - rt) > 0.5)
                {
                    _window.Left = rl;
                    _window.Top = rt;
                }
                return;
            }
            // 匀速：移动固定距离（方向向量 × moveDist）
            _window.Left += dx / dist * moveDist;
            _window.Top += dy / dist * moveDist;
        }

        // ===== 切换动画状态 =====
        private bool _switching;

        // ===== Mica backdrop 临时控制（尺寸动画期间禁用，避免 DWM 重采样闪烁）=====
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_MAINWINDOW = 2;
        private bool _micaMode;         // Win11 Mica 模式（MainWindow 告知）
        private bool _micaSuspended;    // 尺寸动画期间是否已临时禁用

        /// <summary>设置窗口是否为 Win11 Mica 模式（MainWindow 在启用 Fluent 材质后调用）。</summary>
        public void SetMicaBackdropEnabled(bool enabled) => _micaMode = enabled;

        private void SuspendMicaBackdrop()
        {
            if (!_micaMode || _micaSuspended) return;
            try
            {
                // ★ 只切不透明背景，不动 backdrop：
                //   - 不透明背景盖住 Mica → DWM 重采样不可见（无闪烁感知）；
                //   - backdrop 保持开启 → 无材质切换/重新生成延迟（开关 backdrop 才是闪烁源）；
                //   动画结束恢复半透明，毛玻璃直接透出。
                _window.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
                _micaSuspended = true;
            }
            catch { /* 只影响毛玻璃观感（动画照常跑） */ }
        }

        private void RestoreMicaBackdrop()
        {
            if (!_micaMode || !_micaSuspended) return;
            try
            {
                // 恢复半透明深色背景 → Mica 毛玻璃透出（backdrop 一直在，无重建延迟）
                _window.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2D, 0x2D, 0x2D));
                _micaSuspended = false;
            }
            catch { /* 同上：背景换不上只影响观感 */ }
        }

        /// <summary>
        /// 恢复面板内容布局：解除动画期间的"固定尺寸 + 缩放跟随"，让内容按窗口实际尺寸真实布局。
        /// 任何中断/完成路径都必须调用，避免内容层残留固定尺寸导致布局错误。
        /// </summary>
        private void RestorePanelContentLayout()
        {
            try
            {
                _panel.RenderTransform = null;
                _panel.Width = double.NaN;
                _panel.Height = double.NaN;
                _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                _panel.VerticalAlignment = VerticalAlignment.Stretch;
            }
            catch { /* 归位失败最多留一点尺寸/变换残留；动画回调里不能抛（抛了会中断收尾） */ }
            RestoreMicaBackdrop();
        }

        public event Action? FlyCompleted;

        public double CurrentLeft => _window.Left;
        public double CurrentTop => _window.Top;
        public double CurrentWidth => _window.Width;
        public double CurrentHeight => _window.Height;

        public ShapeAnimator(Window window, FrameworkElement panel)
        {
            _window = window;
            _panel = panel;
        }

        // ============================================================
        //  缓动映射
        // ============================================================

        private static IEasingFunction? CreateEasing(string? type) => type switch
        {
            "Linear" => null,
            "QuadraticEase" => new QuadraticEase { EasingMode = EasingMode.EaseOut },
            "QuarticEase" => new QuarticEase { EasingMode = EasingMode.EaseOut },
            "QuinticEase" => new QuinticEase { EasingMode = EasingMode.EaseOut },
            "ElasticEase" => new ElasticEase { EasingMode = EasingMode.EaseOut },
            "BackEase" => new BackEase { EasingMode = EasingMode.EaseOut },
            "BounceEase" => new BounceEase { EasingMode = EasingMode.EaseOut },
            _ => new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        // ============================================================
        //  动画辅助
        // ============================================================

        private void StopPositionAnimations()
        {
            _window.BeginAnimation(Window.LeftProperty, null);
            _window.BeginAnimation(Window.TopProperty, null);
        }

        private void StopSizeAnimations()
        {
            _window.BeginAnimation(Window.WidthProperty, null);
            _window.BeginAnimation(Window.HeightProperty, null);
        }

        private void StopOpacityAnimation()
        {
            _panel.BeginAnimation(UIElement.OpacityProperty, null);
        }

        private void StopAllAnimations()
        {
            StopPositionAnimations();
            StopSizeAnimations();
            StopOpacityAnimation();
        }

        /// <summary>WPF 动画到目标，结束时锁定终值并移除动画时钟。</summary>
        private static void Animate(IAnimatable target, DependencyProperty prop, double to, int ms,
                                    IEasingFunction? easing, EventHandler? completed = null)
        {
            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            anim.Completed += (_, _) =>
            {
                target.BeginAnimation(prop, null);
                ((DependencyObject)target).SetValue(prop, to);
            };
            if (completed != null) anim.Completed += completed;
            target.BeginAnimation(prop, anim);
        }

        private void SetDirect(double? left = null, double? top = null, double? width = null, double? height = null, double? opacity = null)
        {
            StopAllAnimations();
            StopRenderingLoop();
            RestorePanelContentLayout();
            _followActive = false;
            _switching = false;
            if (left.HasValue) _window.Left = left.Value;
            if (top.HasValue) _window.Top = top.Value;
            if (width.HasValue) _window.Width = width.Value;
            if (height.HasValue) _window.Height = height.Value;
            if (opacity.HasValue) _panel.Opacity = opacity.Value;
        }

        // ============================================================
        //  渲染帧循环（跟随 / 引潮）
        // ============================================================

        private void EnsureRenderingLoop()
        {
            if (_renderingActive) return;
            _renderingActive = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRenderingLoop()
        {
            _renderingActive = false;
            _clingMode = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            // ★ 节能跳帧：PowerSaver 下每 N 帧才处理一次（等效降帧降 CPU）。
            //   跟随/引潮为匀速飞行（时间驱动），跳帧只降低刷新率，不改变追赶速度。
            if (_frameSkip > 0)
            {
                _frameCounter++;
                if (_frameCounter % (_frameSkip + 1) != 0) return;
            }

            // ★ 位置/尺寸动画进行中：渲染循环让位，不写窗口位置——
            //   每帧写本地值会干扰 Window.Left/Top 的动画时钟（滑入/滑出动画被"瞬间化"）。
            //   动画完成后（HasAnimatedProperties=false）再接管位置。
            if (_followActive && _window.HasAnimatedProperties) return;

            // ★ 滑入/滑出动画保护期：SetShowHideTarget 启动的动画（含首帧时钟未就绪）
            //   在保护期内跟随让位，避免"渲染帧直达目标"把动画掐断成瞬间到位
            if (_followActive && Environment.TickCount64 < _followSuppressUntilTick) return;

            if (_followActive && FollowPositionProvider != null)
            {
                var (l, t) = FollowPositionProvider();
                double k = _followLerpOverride ?? _followLerp;
                if (k >= 0.999 || _followLerpOverride != null)
                {
                    // ★ 绝对跟手（FlyDurationMs 拉满 / 中置强制）：每帧直接设置——拖窗口手感
                    //   目标取整到像素再比较/写入：小数目标（mx-w/2）与整数窗口位置差 0.5px
                    //   恒 > 0.01 → 每帧写 → 渲染自激循环（100% CPU）。取整后相同值不触发 SetWindowPos。
                    double rl = Math.Round(l);
                    double rt = Math.Round(t);
                    if (Math.Abs(_window.Left - rl) > 0.5 || Math.Abs(_window.Top - rt) > 0.5)
                    {
                        _window.Left = rl;
                        _window.Top = rt;
                    }
                }
                else
                {
                    // ★ 匀速跟随（由 FlyDurationMs 管控）：每帧移动**固定距离**（速度恒定，不减速）。
                    //   速度 = 基准 / FlyDurationMs：FlyDurationMs=0 → 直接设置（最跟手）；
                    //   调大 → 慢速匀速。原"每帧移动剩余距离比例"是指数衰减（临近变慢），已弃用。
                    double T = _clingFlyDurationMs;
                    if (T <= 0)
                    {
                        // ★ 同上：写整数值 + 仅值不同才写，避免渲染帧每帧 SetWindowPos 消息风暴
                        double rl = Math.Round(l);
                        double rt = Math.Round(t);
                        if (Math.Abs(_window.Left - rl) > 0.5 || Math.Abs(_window.Top - rt) > 0.5)
                        {
                            _window.Left = rl;
                            _window.Top = rt;
                        }
                    }
                    else
                    {
                        // ★ 高精度单调计时（TickCount64，1ms）：DateTime.Now 分辨率 ~15.6ms ≈ 60Hz 帧间隔，
                        //   dt 被严重量化（0/15/30ms 跳变）→ 每帧移动距离忽大忽小 → "卡卡的但有些顺滑"
                        long now2 = Environment.TickCount64;
                        double dt2 = Math.Min(now2 - _lastFlyTick, 50);   // 钳制：首帧/卡顿后不跳一大步
                        _lastFlyTick = now2;
                        // 固定速度（px/ms）：FlyDurationMs 越大速度越慢；基准 1000px 全程飞行
                        double speed2 = 1000.0 / Math.Max(1, T);
                        MoveTowardFixedSpeed(l, t, speed2 * dt2);
                    }
                }
            }
            else if (_clingMode)
            {
                // ★ 引潮：匀速飞行，由 FlyDurationMs 管控（用户确认）
                //   - FlyDurationMs <= 0 → 直接设置（拖动窗口式最跟手）
                //   - > 0 → 每帧移动**固定距离**（匀速，不减速，与跟随分支一致）
                //   - 到达目标（≤ moveDist / ClingStopDist）→ 停
                // ★ 每帧刷新目标（实时跟手）：目标源 = 业务层读实时鼠标（与跟随分支同频）。
                //   消除"tick 30ms 才更新一次目标"的滞后 → 面板连续匀速追最新鼠标位置
                if (ClingTargetProvider != null)
                {
                    var (cl, ct, onPanel) = ClingTargetProvider();
                    _clingTargetLeft = cl;
                    _clingTargetTop = ct;
                    _clingMouseOnPanel = onPanel;
                }
                double T = _clingFlyDurationMs;
                if (T <= 0)
                {
                    // ★ 直接设置（拖动窗口式）：
                    //   - 鼠标在面板上（中心追到后鼠标在面板中心 / 面板内绕圈）→ 停并通知业务层复位
                    //   - 面板外移动 → 每帧瞬移跟手（连续，不触发停）
                    if (_clingMouseOnPanel)
                    {
                        _clingMode = false;
                        StopRenderingLoop();
                        ClingArrived?.Invoke();
                        return;
                    }
                    // ★ 写整数值 + 仅值不同才写：cling 目标 = 鼠标 - 半宽（小数），
                    //   取整避免小数振荡，跳过相同值避免每帧 SetWindowPos 消息风暴
                    double crl = Math.Round(_clingTargetLeft);
                    double crt = Math.Round(_clingTargetTop);
                    if (Math.Abs(_window.Left - crl) > 0.5 || Math.Abs(_window.Top - crt) > 0.5)
                    {
                        _window.Left = crl;
                        _window.Top = crt;
                    }
                    return;
                }
                // ★ 高精度单调计时（同跟随分支）：消除 DateTime.Now 分辨率量化导致的步长跳变
                long now = Environment.TickCount64;
                double dt = Math.Min(now - _lastFlyTick, 50);
                _lastFlyTick = now;
                double speed = 1000.0 / Math.Max(1, T);   // 固定速度（px/ms）
                double dx = _clingTargetLeft - _window.Left;
                double dy = _clingTargetTop - _window.Top;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double moveDist = speed * dt;
                if (dist <= moveDist || dist < ClingStopDist)
                {
                    // ★ 写整数值（同跟随分支）：小数目标 + 整数窗口位置 → 落定判定不收敛 → 每帧移动自激
                    _window.Left = Math.Round(_clingTargetLeft);
                    _window.Top = Math.Round(_clingTargetTop);
                    _clingMode = false;
                    StopRenderingLoop();
                    ClingArrived?.Invoke();
                    return;
                }
                _window.Left += dx / dist * moveDist;   // 匀速移动固定距离
                _window.Top += dy / dist * moveDist;
            }
        }

        // ============================================================
        //  设置
        // ============================================================

        public void SetSettings(ISettingsService settings)
        {
            _settings = settings;
            UpdateParametersFromSettings();
        }

        private void UpdateParametersFromSettings()
        {
            if (_settings == null) return;
            _transformDurationMs = Math.Max(30, _settings.TransformDurationMs);
            _showHideDurationMs = Math.Max(30, _settings.ShowHideDurationMs);
            // ★ 跟随松紧：FlyDurationMs=0（不飞）→ 1.0 绝对跟手（立即到达）；调大 → 缓慢飞追
            //   语义：飞行时长越短越跟手（0 时长 = 立即跟随），越长追得越慢
            _followLerp = Math.Max(0.05, Math.Min(1.0, 1.0 - _settings.FlyDurationMs / 2000.0));
            // ★ 引潮飞行时长 = 设置 FlyDurationMs（0 = 直接设置/拖动窗口式最跟手）
            _clingFlyDurationMs = Math.Max(0, _settings.FlyDurationMs);
            _transformEasing = CreateEasing(_settings.TransformEasingType);
            _showHideEasing = CreateEasing(_settings.ShowHideEasingType);
        }

        public void SetAnimationsEnabled(bool enabled) => _animationsEnabled = enabled;

        /// <summary>
        /// 设置渲染帧跳帧（节能降帧）：0=每帧（60fps），1=每 2 帧（~30fps），2=每 3 帧（~20fps）。
        /// PowerSaver 模式由 MainWindow 调用；Normal/Smooth 恢复 0。
        /// </summary>
        public void SetFrameSkip(int skip)
        {
            _frameSkip = Math.Max(0, Math.Min(3, skip));
            _frameCounter = 0;
        }

        /// <summary>
        /// 按目标帧率设置渲染帧跳帧（fps：0=自动满帧；30/60/120 手动）。
        /// CompositionTarget.Rendering 以显示器刷新率触发（多为 60Hz，高刷屏 120/144Hz），
        /// 无法超刷新率；跳帧 = 降帧。映射：目标帧率 ≥ 刷新率 → 不跳；否则每 (刷新率/目标) 取一帧。
        /// </summary>
        public void SetTargetFrameRate(int fps)
        {
            int skip;
            if (fps <= 0)
            {
                skip = 0;   // 自动满帧
            }
            else
            {
                // 估算刷新率（Rendering 触发频率接近显示器刷新率；60 为基准）
                int refresh = 60;
                try
                {
                    var presentationSource = System.Windows.PresentationSource.FromVisual(_window);
                    double? rate = presentationSource?.CompositionTarget?.TransformToDevice.M11;
                    if (rate.HasValue && rate.Value > 0) refresh = Math.Max(60, (int)Math.Round(rate.Value * 60));
                }
                catch { /* 取不到刷新率就按 60Hz 基准算跳帧（refresh 已初始化为 60） */ }
                skip = fps >= refresh ? 0 : Math.Max(0, (int)Math.Ceiling((double)refresh / Math.Max(1, fps)) - 1);
            }
            SetFrameSkip(skip);
        }

        // ============================================================
        //  飞行
        // ============================================================

        public void SetFlyParameters(int durationMs) => _flyDurationMs = Math.Max(50, durationMs);

        public void StartFly(double targetLeft, double targetTop)
        {
            if (!_animationsEnabled)
            {
                _window.Left = targetLeft;
                _window.Top = targetTop;
                FlyCompleted?.Invoke();
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            int ms = _flyDurationMs;
            Animate(_window, Window.LeftProperty, targetLeft, ms, ease);
            Animate(_window, Window.TopProperty, targetTop, ms, ease, (_, _) => FlyCompleted?.Invoke());
        }

        // ============================================================
        //  位置（连续跟随：渲染帧驱动）
        // ============================================================

        /// <summary>激活贴边跟随（渲染帧实时跟手）。调用方设置 FollowPositionProvider 后调用。</summary>
        public bool IsFollowActive => _followActive;

        /// <summary>跟随紧度覆盖：中置状态（乱逛/切换）强制绝对跟手；false 恢复设置映射。</summary>
        public void SetFollowAbsolute(bool absolute)
        {
            _followLerpOverride = absolute ? 1.0 : (double?)null;
        }

        public void StartFollowPosition()
        {
            if (_followActive) return;   // ★ 幂等：跟随已激活不重复停动画/启动循环
            // ★ 不打断位置动画：滑入/滑出动画完整跑完（呼出/隐藏的 800ms 时长真实生效），
            //   动画完成后 provider 自然接管位置。StopPositionAnimations 会把
            //   滑入动画掐断 → 面板瞬间到位（动画时长失效）；也会掐断滑出 → 卡半路黑框。
            RestorePanelContentLayout();
            _clingMode = false;
            _followActive = true;
            EnsureRenderingLoop();
        }

        /// <summary>停止贴边跟随。</summary>
        public void StopFollowPosition()
        {
            _followActive = false;
            if (!_clingMode) StopRenderingLoop();
        }

        public void SetPositionTargetWithoutReset(double left, double top)
        {
            // ★ 贴边跟随：短时长（30ms）WPF 动画重定向——渲染帧 60fps 插值，
            //   目标由 tick（30ms）更新，动画在两次目标间连续插值 → 位置丝滑跟手（无 33fps 步进感）。
            //   每次调用替换旧动画（从当前位置开始），保持连续性。
            StopRenderingLoop();
            StopPositionAnimations();
            // 线性（null easing）→ 30ms 内匀速插值，跟手
            Animate(_window, Window.LeftProperty, left, 30, null);
            Animate(_window, Window.TopProperty, top, 30, null);
        }

        public void SetPositionTarget(double left, double top, bool resetVelocity = false)
        {
            SetPositionTargetWithoutReset(left, top);
        }

        public void JumpToPosition(double left, double top)
        {
            SetDirect(left: left, top: top);
        }

        public void FollowWithInertia(double targetLeft, double targetTop)
        {
            if (!_animationsEnabled)
            {
                _window.Left = targetLeft;
                _window.Top = targetTop;
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Animate(_window, Window.LeftProperty, targetLeft, 180, ease);
            Animate(_window, Window.TopProperty, targetTop, 180, ease);
        }

        // ============================================================
        //  引潮（渲染帧 lerp）
        // ============================================================

        public void SetClingParameters()
        {
            StopAllAnimations();
            RestorePanelContentLayout();
            _followActive = false;
            _switching = false;
            _clingMode = true;
            EnsureRenderingLoop();
        }

        public void ExitClingParameters()
        {
            _clingMode = false;
            StopRenderingLoop();
        }

        public void SetClingTarget(double left, double top)
        {
            // ★ 目标更新：直接替换目标（匀速由渲染循环的固定速度实现，无需起点/时间）
            //   鼠标持续移动时业务层（UpdateClinging）每 tick 更新目标 → 面板持续匀速追最新位置
            _lastFlyTick = Environment.TickCount64;   // 重置计时：进入/更新目标时首帧不跳一大步
            _clingTargetLeft = left;
            _clingTargetTop = top;
            // ★ 跟随让位给 cling：隐藏滑出/引潮由渲染帧循环接管位置（不可被打断）
            _followActive = false;
            _clingMode = true;
            EnsureRenderingLoop();
        }

        // ============================================================
        //  尺寸
        // ============================================================

        public void SetSizeDirect(double width, double height)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            width = Math.Max(10, Math.Min(width, screenWidth));
            height = Math.Max(10, Math.Min(height, screenHeight));
            SetDirect(width: width, height: height);
        }

        public void SetSizeKeepPositionDirect(double width, double height)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            width = Math.Max(10, Math.Min(width, screenWidth));
            height = Math.Max(10, Math.Min(height, screenHeight));
            StopSizeAnimations();
            RestorePanelContentLayout();
            _window.Width = width;
            _window.Height = height;
        }

        // ============================================================
        //  透明度
        // ============================================================

        public void SetOpacityTarget(double opacity, bool resetVelocity = false)
        {
            opacity = Math.Max(0, Math.Min(1, opacity));
            if (!_animationsEnabled)
            {
                _panel.Opacity = opacity;
                return;
            }
            Animate(_panel, UIElement.OpacityProperty, opacity, _showHideDurationMs, _showHideEasing);
        }

        public void SetOpacityDirect(double opacity)
        {
            _panel.Opacity = Math.Max(0, Math.Min(1, opacity));
            StopOpacityAnimation();
        }

        // ============================================================
        //  滑入 / 滑出（尺寸不变 → WPF 动画本就丝滑）
        // ============================================================

        /// <summary>
        /// 呼出/隐藏动画（触发/隐藏类型）：Slide=滑入滑出+淡入淡出；Fade=仅透明度（位置直接到位）；
        /// Zoom=缩放（zoom→1 或 1→zoom）+淡入淡出；Elastic=滑入滑出带弹性；Custom=占位（暂按 Slide）。
        /// </summary>
        public void SetShowHideTarget(double left, double top, double opacity, bool allowOffscreen = false,
            string animType = "Slide", int durationMs = 0,
            double zoom = 0.5, int oscillations = 3, double springiness = 3)
        {
            _panel.RenderTransform = null;
            StopRenderingLoop();
            RestorePanelContentLayout();
            _followActive = false;
            _switching = false;
            opacity = Math.Max(0, Math.Min(1, opacity));
            if (!_animationsEnabled)
            {
                _window.Left = left;
                _window.Top = top;
                _panel.Opacity = opacity;
                return;
            }
            int ms = Math.Max(30, durationMs > 0 ? durationMs : _showHideDurationMs);
            // ★ 动画保护期：动画期间（含首帧时钟未就绪）渲染帧跟随不写位置，
            //   否则 ShowAt→StartFollowContext 连发会让跟随分支提前直达目标 → 动画瞬间化
            _followSuppressUntilTick = Environment.TickCount64 + ms + 100;

            // ★ 自定义动画（海床「动画」分组）：按类型 Id 查注册表，命中则走
            //   沙箱/超时/异常隔离保护的执行路径（见 RunCustomShowHide）
            if (AnimationRegistry.TryGet(animType, out var customAnim) && customAnim != null)
            {
                RunCustomShowHide(customAnim, left, top, opacity, ms);
                return;
            }

            // ★ 平滑缓动（滑入/淡入/缩放用；不再用旧 ShowHideEasingType——那可能是 ElasticEase，
            //   起步极快导致位置瞬间到位，动画时长观感失效）
            var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
            switch (animType)
            {
                case "Fade":
                    // 淡入/淡出：位置直接到位，仅透明度动画
                    _window.Left = left;
                    _window.Top = top;
                    Animate(_panel, UIElement.OpacityProperty, opacity, ms, smooth);
                    break;

                case "Zoom":
                    // 缩放：位置到位 + 面板缩放（呼出 zoom→1，隐藏 1→zoom）+ 透明度
                    _window.Left = left;
                    _window.Top = top;
                    var st = new ScaleTransform(zoom, zoom, _panel.ActualWidth / 2, _panel.ActualHeight / 2);
                    _panel.RenderTransform = st;
                    Animate(st, ScaleTransform.ScaleXProperty, opacity <= 0 ? zoom : 1.0, ms, smooth);
                    Animate(st, ScaleTransform.ScaleYProperty, opacity <= 0 ? zoom : 1.0, ms, smooth);
                    Animate(_panel, UIElement.OpacityProperty, opacity, ms, smooth);
                    break;

                case "Elastic":
                    // 弹性：滑入滑出带弹性回弹（振荡/弹性强度由特化参数决定）
                    var elastic = new ElasticEase
                    {
                        EasingMode = EasingMode.EaseOut,
                        Oscillations = Math.Max(1, oscillations),
                        Springiness = Math.Max(1, springiness)
                    };
                    Animate(_window, Window.LeftProperty, left, ms, elastic);
                    Animate(_window, Window.TopProperty, top, ms, elastic);
                    Animate(_panel, UIElement.OpacityProperty, opacity, ms, elastic);
                    break;

                case "Slide":
                case "Custom":
                default:
                    // ★ 滑入滑出：纯位置动画，不带透明度渐变（淡入淡出是"淡入/淡出"类型的特性）。
                    //   呼出：透明度直接设为不透明（无渐变），滑入过程完整可见；
                    //   隐藏：透明度保持不变（滑出过程可见），窗口滑出屏幕后自然不可见。
                    FallbackSlideShowHide(left, top, opacity, ms);
                    break;
            }
        }

        // ============================================================
        //  自定义动画执行（★ 沙箱 + 超时 + 异常隔离，渲染帧热路径保护）
        // ============================================================

        /// <summary>进行中的自定义动画超时计时器（StopAll/Dispose 时统一停止，防悬挂回调）。</summary>
        private readonly List<DispatcherTimer> _customAnimTimers = new();

        /// <summary>
        /// 执行自定义动画并包裹三道保护：
        /// 1) 异常隔离：AnimateShow/Hide 抛异常 → 回退内置 Slide（滑入/滑出），不崩溃、不卡半路；
        /// 2) 超时保护：动画时长 ×2 后仍未回调 onCompleted → 强制完成（停动画 + 落终值），
        ///    防止自定义动画不回调导致面板卡在中间态（历史教训：100% CPU 卡死）；
        /// 3) 完成回调幂等：onCompleted 只执行一次（落终值后再重复调用无副作用）。
        /// </summary>
        private void RunCustomShowHide(IAnimation custom, double left, double top, double opacity, int ms)
        {
            bool completed = false;
            Action onCompleted = () =>
            {
                if (completed) return;
                completed = true;
                StopAllAnimations();
                RestorePanelContentLayout();
                // 锁定终值：位置到位 + 透明度到位（隐藏=0）
                _window.Left = left;
                _window.Top = top;
                _panel.Opacity = opacity;
            };
            try
            {
                if (opacity <= 0.01)
                    custom.AnimateHide(_panel, _window, ms, onCompleted);
                else
                    custom.AnimateShow(_panel, _window, ms, onCompleted);
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                    "自定义动画异常，回退内置动画: " + ex.Message);
                AnimationRegistry.ReportCustomAnimationError(ex);   // ★ 交给插件守卫熔断
                FallbackSlideShowHide(left, top, opacity, ms);
                return;
            }
            // ★ 超时兜底：自定义动画不回调也不至于卡死（ms×2 后强制完成）
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms * 2L))
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _customAnimTimers.Remove(timer);
                onCompleted();
            };
            timer.Start();
            _customAnimTimers.Add(timer);
        }

        /// <summary>内置 Slide 回退（滑入/滑出：纯位置动画，不带透明度渐变）。</summary>
        private void FallbackSlideShowHide(double left, double top, double opacity, int ms)
        {
            var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (opacity > 0.5) _panel.Opacity = opacity;
            Animate(_window, Window.LeftProperty, left, ms, smooth);
            Animate(_window, Window.TopProperty, top, ms, smooth);
        }

        /// <summary>停止所有自定义动画超时计时器（中断/释放时调用）。</summary>
        private void StopCustomAnimTimers()
        {
            foreach (var t in new List<DispatcherTimer>(_customAnimTimers))
            {
                try { t.Stop(); } catch { /* 停不掉最多多跑一次 Tick（Tick 内有幂等保护） */ }
            }
            _customAnimTimers.Clear();
        }

        public void SetShowHideDirect(double left, double top, double opacity)
        {
            SetDirect(left: left, top: top, opacity: opacity);
        }

        // ============================================================
        //  切换（区域切换 / 飞行落地）：
        //  位置+尺寸同步动画（贴边锚定），内容随窗口真实布局——
        //  ★ 不做内容 ScaleTransform 等比缩放（用户要求）
        // ============================================================

        public bool IsTransformAnimating => _switching;

        /// <summary>
        /// 位置动画 + 尺寸直接到位（图标中置方案：切换期间不做尺寸形变动画，减少卡顿）。
        /// 尺寸一次 resize（Mica 背景暂切不透明，无重采样闪烁），位置平滑移动。
        /// </summary>
        public void SetPositionAnimateSizeDirect(double left, double top, double width, double height)
        {
            if (!_animationsEnabled)
            {
                SetDirect(left: left, top: top, width: width, height: height);
                return;
            }
            StopRenderingLoop();
            StopAllAnimations();
            _followActive = false;
            RestorePanelContentLayout();

            var (screenWidth, screenHeight) = ScreenSize;
            double cw = Math.Max(10, Math.Min(width, screenWidth));
            double ch = Math.Max(10, Math.Min(height, screenHeight));

            // 尺寸直接到位（一次 resize）：背景切不透明避免 Mica 重采样闪烁
            SuspendMicaBackdrop();
            _window.Width = cw;
            _window.Height = ch;

            int ms = Math.Max(1, _transformDurationMs);
            var ease = _transformEasing;
            _switching = true;
            Animate(_window, Window.LeftProperty, left, ms, ease);
            Animate(_window, Window.TopProperty, top, ms, ease, (_, _) =>
            {
                RestoreMicaBackdrop();
                _switching = false;
            });
        }

        /// <summary>仅位置动画（尺寸不动）：快速切换期间不形变，跟手；尺寸由稳定后的形变动画负责。</summary>
        public void SetPositionAnimate(double left, double top)
        {
            if (!_animationsEnabled)
            {
                _window.Left = left;
                _window.Top = top;
                return;
            }
            StopRenderingLoop();
            StopAllAnimations();
            _followActive = false;
            RestorePanelContentLayout();

            int ms = Math.Max(1, _transformDurationMs);
            var ease = _transformEasing;
            _switching = true;
            Animate(_window, Window.LeftProperty, left, ms, ease);
            Animate(_window, Window.TopProperty, top, ms, ease, (_, _) => _switching = false);
        }

        /// <summary>仅尺寸形变动画（防抖稳定后调用）：平滑形变到目标尺寸，完成后回调（用于内容变实）。</summary>
        public void AnimateSizeTo(double width, double height, Action? completed = null)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            double cw = Math.Max(10, Math.Min(width, screenWidth));
            double ch = Math.Max(10, Math.Min(height, screenHeight));
            if (!_animationsEnabled)
            {
                _window.Width = cw;
                _window.Height = ch;
                completed?.Invoke();
                return;
            }
            // ★ 形变期间置 _switching：IsTransformAnimating=true，
            //   业务层（如 OnPanelContentChanged 的 ApplyAutoSize）据此跳过
            //   原子尺寸跳变——避免"动画形变 + 原子 SetWindowPos"同时执行导致尺寸横跳闪烁
            SuspendMicaBackdrop();
            _switching = true;
            int ms = Math.Max(1, _transformDurationMs);
            var ease = _transformEasing;
            Animate(_window, Window.WidthProperty, cw, ms, ease);
            Animate(_window, Window.HeightProperty, ch, ms, ease, (_, _) =>
            {
                _switching = false;
                RestoreMicaBackdrop();
                completed?.Invoke();
            });
        }

        /// <summary>
        /// 尺寸形变 + 位置同步（贴边锚定）：区域切换（任务栏→小组件等）时，
        /// 位置与尺寸同时动画到目标——贴边边缘（Top 边 top=0 / Left 边 left=0 / 角落角点）保持贴边，
        /// 另一侧收缩/扩展。★ 修复：原 AnimateSizeTo 只动尺寸、位置由渲染帧在动画完成后跳变，
        /// 导致"固定左上角缩放→完成后才贴边"的横跳。此方法用 CalculatePosition 的结果作位置目标，
        /// 动画中间帧即保持贴边。
        /// </summary>
        public void AnimateSizeToKeepAnchor(double left, double top, double width, double height, Action? completed = null)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            double cw = Math.Max(10, Math.Min(width, screenWidth));
            double ch = Math.Max(10, Math.Min(height, screenHeight));
            if (!_animationsEnabled)
            {
                SetDirect(left: left, top: top, width: cw, height: ch);
                completed?.Invoke();
                return;
            }
            // ★ 形变期间置 _switching（同 AnimateSizeTo 语义）
            SuspendMicaBackdrop();
            StopRenderingLoop();
            StopAllAnimations();
            _followActive = false;
            RestorePanelContentLayout();
            _switching = true;
            int ms = Math.Max(1, _transformDurationMs);
            var ease = _transformEasing;
            // ★ 位置与尺寸同缓动同时长 → 动画中间帧贴边边缘不脱边（滑向新锚点的同时缩放）
            Animate(_window, Window.LeftProperty, left, ms, ease);
            Animate(_window, Window.TopProperty, top, ms, ease);
            Animate(_window, Window.WidthProperty, cw, ms, ease);
            Animate(_window, Window.HeightProperty, ch, ms, ease, (_, _) =>
            {
                _switching = false;
                RestoreMicaBackdrop();
                completed?.Invoke();
            });
        }

        public void SetPositionAndSizeTarget(double left, double top, double width, double height)
        {
            if (!_animationsEnabled)
            {
                SetDirect(left: left, top: top, width: width, height: height);
                return;
            }
            StopRenderingLoop();
            StopAllAnimations();
            _followActive = false;
            RestorePanelContentLayout();

            // ★ 面板与内容同步动画（★ 无内容等比缩放）：
            //   窗口真实尺寸+位置 WPF 动画（渲染帧 60fps 插值），内容随窗口真实布局——
            //   不再用 ScaleTransform 缩放内容（用户要求：内容切出不做等比放大动画）。
            //   内容层保持真实布局（不固定尺寸/不缩放），动画期间内容随窗口 resize 真实重排。
            var (screenWidth, screenHeight) = ScreenSize;
            double cw = Math.Max(10, Math.Min(width, screenWidth));
            double ch = Math.Max(10, Math.Min(height, screenHeight));

            SuspendMicaBackdrop();

            int ms = Math.Max(1, _transformDurationMs);
            var ease = _transformEasing;
            _switching = true;

            // ★ 完成判定绑定尺寸动画（Width）：位置动画可能 0 距离瞬间完成（如仅横向切换），
            //   不能作为"切换完成"依据（否则 switching 提前复位、跟随提前恢复、Mica 提前恢复）
            Animate(_window, Window.WidthProperty, cw, ms, ease, (_, _) =>
            {
                RestoreMicaBackdrop();
                _switching = false;
            });
            Animate(_window, Window.HeightProperty, ch, ms, ease);
            Animate(_window, Window.LeftProperty, left, ms, ease);
            Animate(_window, Window.TopProperty, top, ms, ease);
        }

        public void SetSizeTarget(double width, double height)
        {
            if (!_animationsEnabled)
            {
                var (screenWidth, screenHeight) = ScreenSize;
                _window.Width = Math.Max(10, Math.Min(width, screenWidth));
                _window.Height = Math.Max(10, Math.Min(height, screenHeight));
                return;
            }
            SetPositionAndSizeTarget(_window.Left, _window.Top, width, height);
        }

        public void SetPositionAndSizeDirect(double left, double top, double width, double height)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            width = Math.Max(10, Math.Min(width, screenWidth));
            height = Math.Max(10, Math.Min(height, screenHeight));
            SetDirect(left: left, top: top, width: width, height: height);
        }

        public void SetPositionAndSizeWithoutReset(double left, double top, double width, double height)
        {
            SetPositionAndSizeDirect(left, top, width, height);
        }

        public void JumpTo(double left, double top, double width, double height)
        {
            var (screenWidth, screenHeight) = ScreenSize;
            width = Math.Max(10, Math.Min(width, screenWidth));
            height = Math.Max(10, Math.Min(height, screenHeight));
            SetDirect(left: left, top: top, width: width, height: height);
        }

        // ============================================================
        //  停止 / 兼容入口
        // ============================================================

        public void StopAll()
        {
            StopAllAnimations();
            StopRenderingLoop();
            StopCustomAnimTimers();
            RestorePanelContentLayout();
            _followActive = false;
            _switching = false;
        }

        public void SetTarget(double left, double top, double width, double height, bool resetVelocity = true)
        {
            SetPositionAndSizeTarget(left, top, width, height);
        }

        public void SetTargetPositionAndSizeWithoutReset(double left, double top, double width, double height)
        {
            SetPositionAndSizeTarget(left, top, width, height);
        }

        public void SetTargetSizeWithoutReset(double width, double height)
        {
            SetSizeTarget(width, height);
        }

        public void SetImmediate(double width, double height, double left, double top)
        {
            SetPositionAndSizeDirect(left, top, width, height);
        }

        public void AnimateTo(double width, double height, double left, double top, int durationMs)
        {
            SetFlyParameters(durationMs);
            StartFly(left, top);
        }

        // ============================================================
        //  屏幕尺寸
        // ============================================================

        private (double width, double height) ScreenSize
        {
            get
            {
                try
                {
                    var wa = ShoreHue.Infrastructure.Utils.ScreenMetrics.GetCachedScreenForWindow(
                        _window.Left, _window.Top, _window.Width, _window.Height);
                    return (wa.Width, wa.Height);
                }
                catch
                {
                    return (SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CompositionTarget.Rendering -= OnRendering;
            StopAllAnimations();
            StopCustomAnimTimers();
        }
    }
}