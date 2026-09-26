/* ============================================================
   滚动 → 惯性 → 阻尼 → 自动回中
   ------------------------------------------------------------
   实现依据（调研结论）：
   · 弹簧阻尼 ODE  a + 2ζωv + ω²x = 0，ω=√(k/m)，ζ=β/(2√(mk))
   · 用「闭式解系数」而不是 lerp：对任意 dt 精确、无条件稳定，
     不需要固定步长 accumulator。临界阻尼 ζ=1 最快且不过冲。
   · 绝不写成 lerp(a,b,0.1)（每帧固定比例 → 144Hz 比 60Hz 快 2.4 倍）；
     正确形式是 lerp(a,b,1-exp(-λdt))（每单位时间固定比例）。
   · 滚轮只注入冲量，绝不直接赋值位置，否则滚动与视觉耦合会抖动/过冲。
   · deltaY 量级：deltaMode 0=px / 1=行(×16) / 2=页(×视高)；
     触控板 3px 与鼠标 120px 差 30 倍，必须归一化。ctrlKey 是捏合缩放，丢弃。
   ============================================================ */

(function (global) {
  'use strict';

  // ---- 可调参数 ----
  /* 手感标定：ζ>1 为过阻尼（黏、回弹慢），ω 越小整体越"重"。
     之前 ω=4.2 / ζ=1.0 / GAIN=0.000125 —— 稍微一滚就涨满、松手又很快落回去，
     显得"太容易涨落潮"。现在：更慢的频率 + 过阻尼 + 更低的触发增益，
     需要持续滚动才推得动，停下后也是缓慢地退回去。 */
  const OMEGA   = 2.6;    // 自然角频率（越小越沉）
  const ZETA    = 1.25;   // 过阻尼：不振荡，且回落明显更黏
  const WHEEL_GAIN   = 0.000055; // 滚轮 → 冲量/像素（调低，不易被单次滚动点燃）
  const TOUCH_GAIN   = 0.0011;   // 触摸拖动 → 冲量
  const SCROLL_GAIN  = 0.000022; // 原生 scroll 增量 → 冲量
  const MAX_STEP     = 120;      // 单次 deltaY 上限
  const VMAX   = 900;     // 冲量上限，防一次性甩飞
  const X_REST = 0.006;   // 位置休眠双阈值
  const V_REST = 0.020;   // 速度休眠双阈值

  function clamp(v, a, b) { return v < a ? a : (v > b ? b : v); }

  function ScrollPhysics(opts) {
    opts = opts || {};
    this.pos = 0;              // 归一化偏移：+ 涨潮 / - 退潮
    this.vel = 0;
    this.prevScrollY = window.scrollY || 0;
    this.scrollVel = 0;        // 原生滚动的低通速度
    this.touchLast = 0;
    this.lastWheelAt = -1e9;
    this.touchAcc = 0;
    this.sleeping = false;
    this.enabled = !global.matchMedia ||
                   !global.matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.onState = opts.onState || null;
    this._bind();
  }

  ScrollPhysics.prototype._bind = function () {
    const self = this;

    // ---------- 滚轮：passive 监听，只注入冲量 ----------
    window.addEventListener('wheel', function (e) {
      if (!self.enabled) return;
      if (e.ctrlKey) return;                       // 触控板捏合缩放，不是滚动
      let d = e.deltaY;
      const mode = e.deltaMode;
      if (mode === 1) d *= 16;                     // 行
      else if (mode === 2) d *= window.innerHeight; // 页
      d = clamp(d, -MAX_STEP, MAX_STEP);
      // 方向必须与「拖滚动条」一致：两者都 += 。
      // 之前滚轮用 -= 、scroll 用 += ，同一方向的两个操作会让水往相反方向跑。
      // 统一为：向下滚(d>0) → 涨潮。
      self.lastWheelAt = performance.now();
      self.vel += d * WHEEL_GAIN * 1000;
      self.vel = clamp(self.vel, -VMAX, VMAX);
      self.sleeping = false;
    }, { passive: true });

    // ---------- 触摸：采样位移当冲量 ----------
    window.addEventListener('touchstart', function (e) {
      if (!self.enabled || !e.touches.length) return;
      self.touchLast = e.touches[0].clientY;
      self.touchAcc = 0;
    }, { passive: true });

    window.addEventListener('touchmove', function (e) {
      if (!self.enabled || !e.touches.length) return;
      const y = e.touches[0].clientY;
      const dy = y - self.touchLast;
      self.touchLast = y;
      self.touchAcc += dy;
      // 与滚轮/滚动条同向
      self.vel += dy * TOUCH_GAIN * 1000;
      self.vel = clamp(self.vel, -VMAX, VMAX);
      self.sleeping = false;
    }, { passive: true });

    window.addEventListener('touchend', function () {
      self.touchAcc = 0;
      self.touchLast = 0;
    }, { passive: true });

    // ---------- 切后台回来：时间基准必须重置，否则 dt 巨大会炸飞 ----------
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) self._last = 0;
    });
  };

  /**
   * 每帧推进。
   * @param {number} dt 秒（调用方需已 clamp）
   */
  ScrollPhysics.prototype.update = function (dt) {
    if (!this.enabled) {
      // 减少动效：直接贴目标，无惯性无回弹
      this.pos = 0; this.vel = 0;
      if (this.onState) this.onState(0, 0);
      return;
    }

    // 原生滚动（拖滚动条、键盘、触摸惯性）也作为冲量来源
    const sy = window.scrollY || 0;
    const dScroll = sy - this.prevScrollY;
    this.prevScrollY = sy;
    // scrollY 只用于「知道页面在滚动」，不再作为冲量来源。
    // 位置差分当冲量是错的：滚动期间每帧都在变 → 每帧都在推 →
    // 弹簧刚往回收又被推出去，回落途中就抽搐。冲量只来自 wheel / touch。
    if (dScroll !== 0) {
      this.scrolling = true;
      this.scrollIdleAt = (typeof performance !== 'undefined' ? performance.now() : Date.now()) + 140;
    } else {
      const t = (typeof performance !== 'undefined' ? performance.now() : Date.now());
      this.scrolling = t < (this.scrollIdleAt || 0);
    }

    if (this.sleeping) {
      this.pos = 0; this.vel = 0;
      if (this.onState) this.onState(0, 0);
      return;
    }

    // ---------- 闭式解：欠阻尼弹簧 ----------
    // 精确解对任意 dt 稳定；ζ<1 时带阻尼振荡
    const w = OMEGA, z = ZETA;
    const x = this.pos, v = this.vel;
    let nx, nv;

    if (z < 1) {
      const wd = w * Math.sqrt(1 - z * z);          // 阻尼角频率
      const e = Math.exp(-z * w * dt);
      const c = Math.cos(wd * dt);
      const s = Math.sin(wd * dt);
      nx = e * (x * c + (v + z * w * x) / wd * s);
      nv = e * (v * c - (w * w * x + 2 * z * w * v) / wd * s);
    } else {
      // 临界阻尼（ζ=1）的稳定形式
      const e = Math.exp(-w * dt);
      nx = e * (x + (v + w * x) * dt);
      nv = e * (v - w * (v + w * x) * dt);
    }

    // 数值兜底
    if (!isFinite(nx) || !isFinite(nv)) { nx = 0; nv = 0; }

    this.pos = nx;
    this.vel = nv;

    // 双阈值休眠
    if (Math.abs(this.pos) < X_REST && Math.abs(this.vel) < V_REST) {
      this.pos = 0; this.vel = 0; this.sleeping = true;
    }

    if (this.onState) this.onState(this.pos, this.vel);
  };

  global.ScrollPhysics = ScrollPhysics;
})(window);
