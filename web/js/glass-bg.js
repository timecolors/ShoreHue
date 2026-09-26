/* ============================================================
   ShoreHue 官网背景
   ------------------------------------------------------------
   组成：
     · 色块背景（.blob + #bg 渐变）—— 纯 CSS，沙滩
     · 毛玻璃内容卡片 —— 纯 CSS
     · 水面 —— Canvas 逐帧程序化绘制（多层水波）
   交互：
     · 鼠标移动 → 色块按不同幅度平移，形成纵深
     · 滚动      → 潮水涨落（临界阻尼弹簧，来自 scroll-physics.js）

   所有视觉参数集中在 window.__seaTune，页面右上角的参数面板
   （js/tuner.js）会直接改它，改完立刻生效，不需要刷新。
   ============================================================ */
(function () {
  'use strict';

  /* ============================================================
     可调参数（参数面板直接改这里）
     ============================================================ */
  var T = window.__seaTune = {
    /* --- 分层 --- */
    layers: 6,           // 层数
    spread: 0.055,       // 层间距（相对屏幕高度）。太挤就调大
    step: 17,            // 横向采样步长(px)：越大越快、越粗糙

    /* --- 每层透明度 --- */
    waveBlur: 5,         // 波纹层模糊半径(px)：用来化开层的分界线
    waveAlphaMin: 0.007,
    waveAlphaMax: 0.021,

    /* --- 波形：三个正弦叠加 --- */
    amp1: 3.2,  amp2: 1.9,  amp3: 0.9,      // 振幅
    k1: 0.0031, k2: 0.0071, k3: 0.0165,     // 空间频率
    s1: 0.38,   s2: 0.55,   s3: 0.90,       // 时间速度
    phase: 0.61,                            // 层间相位差
    layerInertia: 1.0,                      // 分层惯性强度：0=整块同步移动，1=近层跟手/远层迟缓

    /* --- 水体底色（决定水色，不透明） --- */
    /* 水色：近岸透亮，越深越冷。
       深段参考《鸣潮》「漂泊的终点 / 守岸人」那套深水 ——
       青蓝 → 深蓝 → 带紫的夜蓝，整体比原来压深一档，
       让"深"在画面中段就出现，而不是只留最底下一条。 */
    c1: '#8fdcec', c2: '#45b6d4', c3: '#1786b0',
    c4: '#0c5182', c5: '#093358', c6: '#0a1a3c',
    top: 0.53,           // 中性水位线（屏幕高度比例）
    gradeSpan: 0.47,     // 水色阶跨度（相对屏幕高）：固定值，不随潮汐伸缩
    dpr: 1.0,            // canvas 分辨率倍率（调低更流畅）

    /* --- 交互 --- */
    pointerDip: 6.0      // 指针压水深度(px)
  };

  // 默认值快照（面板「恢复默认」用）
  window.__seaTuneDefaults = JSON.parse(JSON.stringify(T));

  var reduceMotion = window.matchMedia &&
                     window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ============================================================
     一、水面（Canvas 程序化绘制）
     ============================================================ */
  var canvas = document.getElementById('seaCanvas');
  var sctx = canvas ? canvas.getContext('2d') : null;
  var seaW = 0, seaH = 0, seaDpr = 1;

  function sizeSea() {
    if (!canvas) return;
    seaDpr = Math.min(window.devicePixelRatio || 1, T.dpr);
    seaW = window.innerWidth;
    seaH = window.innerHeight;
    canvas.width = Math.round(seaW * seaDpr);
    canvas.height = Math.round(seaH * seaDpr);
    canvas.style.width = seaW + 'px';
    canvas.style.height = seaH + 'px';
  }
  window.__resizeSea = sizeSea;      // 供参数面板在改 dpr 后调用

  /* 逐帧绘制，分两部分：
     ① 底层：一整块【不透明】的水色渐变 —— 它决定水的颜色，所以纯净不发灰。
     ② 上层：若干【半透明】波纹，只负责流动与层次，不参与决定颜色。

     涨潮时还要让"最深的水"也能铺满屏幕：
     如果色阶永远从水面起算，满潮时屏幕上方仍是最浅的 c1，看着不像深水。
     这里让色阶跨度随潮汐【收窄】——水越深，颜色越快走到深海色，
     于是铺满屏幕时整片都是深水，而不是上浅下深。 */
  function drawSea(t, tidePx, mousePx, mouseStrength, layerOffsets) {
    if (!sctx) return;
    sctx.setTransform(seaDpr, 0, 0, seaDpr, 0, 0);
    sctx.clearRect(0, 0, seaW, seaH);

    var baseY = seaH * T.top + tidePx;

    // 0 = 中性，1 = 满潮（tidePx 为负表示涨潮）
    var flood = Math.max(0, Math.min(1, -tidePx / (seaH * 0.60)));
    /* 色阶跨度随潮汐收窄，且用高次幂。
       之前线性收到 38%，满潮时顶部仍有 ~11% 是过渡色 —— 那不算"铺满"。
       改成 (1-flood)^2.2：中性 0.47H、半潮约 0.10H、满潮趋近 0（整屏深海色）。 */
    var gradeSpan = seaH * T.gradeSpan * Math.pow(1 - flood, 2.2);
    if (gradeSpan < 1) gradeSpan = 1;   // 保底：gradient 起终点不能重合

    var g = sctx.createLinearGradient(0, baseY - 10, 0, baseY + gradeSpan);
    g.addColorStop(0,    T.c1);
    g.addColorStop(0.10, T.c2);
    g.addColorStop(0.26, T.c3);
    g.addColorStop(0.52, T.c4);
    g.addColorStop(0.78, T.c5);
    g.addColorStop(1,    T.c6);
    sctx.fillStyle = g;

    var step = T.step;
    sctx.beginPath();
    sctx.moveTo(-14, seaH + 14);
    for (var bx = -14; bx <= seaW + 14; bx += step) {
      var by = baseY
        + Math.sin(bx * T.k1 + t * T.s1) * (T.amp1 * 0.85)
        + Math.sin(bx * T.k2 - t * T.s2) * (T.amp2 * 0.85);
      if (mousePx !== null) {
        var bdx = (bx - mousePx) / (seaW * 0.10);
        by -= Math.exp(-bdx * bdx) * T.pointerDip * 1.2 * mouseStrength;
      }
      sctx.lineTo(bx, by);
    }
    sctx.lineTo(seaW + 14, seaH + 14);
    sctx.closePath();
    sctx.fill();

    /* ---- ② 上层波纹 ----
       每层都是"从波浪线填到屏幕底部"的形状，所以它的上边缘会与下层重叠；
       而各层透明度又逐层递增，于是在边缘处透明度发生突变 → 看得见一条条分界线。
       解法：整组波纹统一加模糊，把边缘化开；层次感靠叠加自然形成。 */
    var N = Math.max(2, Math.round(T.layers));
    var DEN = N - 1;

    sctx.save();
    if (T.waveBlur > 0) sctx.filter = 'blur(' + T.waveBlur + 'px)';

    for (var i = 0; i < N; i++) {
      var f = i / DEN;
      /* 每层带自己的惯性偏移（layerOffsets）：
         近层跟手、远层迟缓，于是同一片水面会被"拉开"，
         而不是整块一起平移 —— 这是分层阻尼的观感来源。 */
      var ly = baseY + f * seaH * T.spread + (layerOffsets ? layerOffsets[i] : 0);

      var ca = T.waveAlphaMin + (T.waveAlphaMax - T.waveAlphaMin) * f;
      /* 不要做成亮/暗交替！层间距只有几像素，交替必然形成规则横条纹
         （看起来"一段深一段浅"）。全部统一成极淡的亮色。 */
      sctx.fillStyle = 'rgba(146,222,248,' + ca.toFixed(3) + ')';

      sctx.beginPath();
      sctx.moveTo(-14, seaH + 14);
      for (var x = -14; x <= seaW + 14; x += step) {
        var y = ly
          + Math.sin(x * T.k1 + t * T.s1 + i * T.phase) * (T.amp1 * 0.62 + f * 3.0)
          + Math.sin(x * T.k2 - t * T.s2 + i * T.phase * 2.2) * (T.amp2 * 0.62 + f * 1.9)
          + Math.sin(x * T.k3 + t * T.s3 + i * T.phase * 3.4) * (T.amp3 * 0.55 + f * 0.9);
        if (mousePx !== null) {
          var dxm = (x - mousePx) / (seaW * 0.10);
          y -= Math.exp(-dxm * dxm) * T.pointerDip * (0.4 + 0.6 * f) * mouseStrength;
        }
        sctx.lineTo(x, y);
      }
      sctx.lineTo(seaW + 14, seaH + 14);
      sctx.closePath();
      sctx.fill();
    }
    sctx.restore();
  }


  /* ---------------- 海床：打开与退出 ---------------- */
  var seabedCooldownUntil = 0;   // 浮出水面后的冷却截止时间（防抖）

  function openSeabed() {
    if (seabedOpen) return;
    seabedOpen = true;
    document.documentElement.classList.add('at-seabed');
    if (seabedEl) seabedEl.scrollTop = 0;
  }

  function closeSeabed() {
    if (!seabedOpen) return;
    seabedOpen = false;
    document.documentElement.classList.remove('at-seabed');
    /* 关键：把主页面往上挪一点，脱离"最底部"。
       否则退出瞬间 atBottom 仍为真、水位也还满着，会立刻被重新判定为下潜。 */
    var leaveY = Math.max(0, document.documentElement.scrollHeight - window.innerHeight - 160);
    /* 必须用 instant：全局有 scroll-behavior:smooth，
       用平滑滚动的话，往上爬的过程中主循环仍会读到 atBottom=true，
       于是刚退出就被重新判定为下潜 —— 表现就是"从海床滚不回去"。
       同时加一个冷却窗口兜底，防止这一帧的 curSeaY 还是满的。 */
    window.scrollTo({ top: leaveY, behavior: 'instant' });
    seabedCooldownUntil = (window.performance && performance.now ? performance.now() : Date.now()) + 700;
    // 清掉冲量，让水开始回落
    tidePos = 0; tideVel = 0;
    if (physics) { physics.pos = 0; physics.vel = 0; physics.sleeping = false; }
  }

  /* 退出方式：① 海床内滚到顶后继续上滚  ② Esc  ③ 点击右下角"浮出水面"按钮 */
  function bindSeabedExit() {
    if (!seabedEl || seabedBound) return;
    seabedBound = true;

    seabedEl.addEventListener('wheel', function (e) {
      if (!seabedOpen) return;
      // 已经在顶部还继续往上滚 → 浮出水面
      if (seabedEl.scrollTop <= 0 && e.deltaY < 0) {
        e.preventDefault();
        closeSeabed();
      }
    }, { passive: false });

    document.addEventListener('keydown', function (e) {
      if (seabedOpen && (e.key === 'Escape' || e.key === 'Esc')) closeSeabed();
    });

    // 浮出水面按钮。
    // 必须挂在 body 上，不能挂在 .seabed 里 —— .seabed 有 transform + overflow:auto，
    // 会让内部 position:fixed 变成"相对该容器"定位，按钮就跟着内容滚出视口了。
    var btn = document.createElement('button');
    btn.className = 'seabed-exit';
    btn.type = 'button';
    btn.innerHTML = '<span>↑</span> 浮出水面';
    btn.addEventListener('click', closeSeabed);
    document.body.appendChild(btn);
  }
  /* ============================================================
     二、色块视差
     ============================================================ */
  var blobEls = [
    document.querySelector('.blob-a'),
    document.querySelector('.blob-b'),
    document.querySelector('.blob-c'),
    document.querySelector('.blob-d')
  ].filter(Boolean);

  var target = { x: 0, y: 0 };
  var current = { x: 0, y: 0 };
  var pointerScreen = { x: null, y: null };
  var mouseStrength = 0;

  if (!reduceMotion) {
    window.addEventListener('pointermove', function (e) {
      target.x = (e.clientX / window.innerWidth) - 0.5;
      target.y = (e.clientY / window.innerHeight) - 0.5;
      pointerScreen.x = e.clientX;
      mouseStrength = Math.min(1, mouseStrength + 0.04);
    }, { passive: true });
    window.addEventListener('pointerleave', function () {
      target.x = 0; target.y = 0;
      pointerScreen.x = null;
      mouseStrength = 0;
    }, { passive: true });
  }

  /* ============================================================
     三、滚动 → 潮水涨落
     ============================================================ */
  var tidePos = 0;
  var tideVel = 0;
  var physics = null;
  if (window.ScrollPhysics && !reduceMotion) {
    physics = new window.ScrollPhysics({
      onState: function (p, v) { tidePos = p; tideVel = v; }
    });
  }

  /* ============================================================
     四、主循环
     ============================================================ */
  var last = 0;
  var physicsLast = 0;
  var curSeaY = 0;              // 单位 vh：+ 退潮（水退走）、- 涨潮（水铺满）
  var clock = 0;
  var fpsSmooth = 60;
  var seabedEl = null;      // 海床覆盖层
  var seabedOpen = false;   // 是否已潜入海床
  var seabedBound = false;  // 退出事件只绑一次
  var layerY = [];          // 每层自身的位移（带惯性）
  var layerOffsets = [];    // 每层相对整体的滞后量，交给 drawSea
  window.__bgErrors = [];

  function frame(now) {
    try {
      frameBody(now);
    } catch (err) {
      var msg = (err && err.message) ? err.message : String(err);
      if (window.__bgErrors.indexOf(msg) < 0) window.__bgErrors.push(msg);
      console.error('[glass-bg] frame error:', err);
      requestAnimationFrame(frame);
    }
  }

  function frameBody(now) {
    if (!last) { last = now; physicsLast = now; }
    var dt = (now - last) / 1000;
    last = now;
    if (dt > 1 / 30) dt = 1 / 30;
    if (dt <= 0) dt = 1 / 60;

    if (dt > 0) fpsSmooth += (1 / dt - fpsSmooth) * 0.08;

    if (physics) {
      var pdt = (now - physicsLast) / 1000;
      physicsLast = now;
      if (pdt > 1 / 30) pdt = 1 / 30;
      physics.update(pdt);
    }

    // 色块视差（低通平滑，避免抖动）
    var lerp = 1 - Math.exp(-dt * 5.0);
    current.x += (target.x - current.x) * lerp;
    current.y += (target.y - current.y) * lerp;
    for (var i = 0; i < blobEls.length; i++) {
      var k = 30 + i * 30;
      blobEls[i].style.transform =
        'translate3d(' + (-current.x * k).toFixed(2) + 'px,' +
                         (-current.y * k).toFixed(2) + 'px,0)';
    }

    /* 潮水位移 —— 由滚动的【速度】驱动，而不是滚动的位置。
       曾经改成"跟随滚动进度"（targetSea = -progress*60），那样只能从 0 单调涨到 -60，
       松手停住后水也不会回落，等于把"退潮"这个行为弄丢了。
       现在恢复为冲量模型：
         · 滚动时把速度灌进 scroll-physics 的弹簧 → 涨潮
         · 停止滚动后弹簧自然泄力 → 水位回落到中性
       量程：-60vh = 铺满全屏，+52vh = 完全褪去。 */
    var targetSea = -tidePos * 9.5;
    if (targetSea < -60) targetSea = -60;
    if (targetSea > 52) targetSea = 52;

    /* 「潜到海床」—— 状态机，两个条件必须【同时】满足：
         ① 已经划到页面最底部（再滚也滚不动了）
         ② 此刻深水已铺满屏幕
       之前只判"海床区块进入视野 + 水位满"，滚到一半就会触发，是错的。
       进入后海床是一层 fixed 覆盖层，在里面滚动不会改变 pageYOffset，
       所以不能靠"离开底部"退出 —— 必须给明确的退出动作（见 bindSeabedExit）。 */
    var doc = document.documentElement;
    if (!seabedEl) { seabedEl = document.getElementById('seabed'); bindSeabedExit(); }

    if (seabedOpen) {
      targetSea = -60;                       // 停在水下
    } else {
      /* 刚浮出水面的 0.7s 内不再判定下潜：
         退出那一帧水位仍是满的，没有这个冷却会被立刻重新吸进去。 */
      var nowMs = (window.performance && performance.now) ? performance.now() : Date.now();
      if (nowMs >= seabedCooldownUntil) {
        var atBottom = (window.pageYOffset + window.innerHeight) >= (doc.scrollHeight - 4);
        if (atBottom && curSeaY < -45) openSeabed();
      }
    }

    curSeaY += (targetSea - curSeaY) * (1 - Math.exp(-dt * 9.0));

    /* 逐层惯性：每层用【不同的响应速度】追赶同一个目标位移。
       远层（f 小）响应慢 = 阻尼大、惯性大；近层（f 大）跟手。
       于是涨落过程中各层彼此错开，水是"层层涌动"而不是整块滑动。 */
    var N = Math.max(2, Math.round(T.layers));
    var DEN = N - 1;
    // 层数变化时补/截数组
    while (layerY.length < N) layerY.push(curSeaY / 100 * seaH);
    if (layerY.length > N) layerY.length = N;

    var targetPx = curSeaY / 100 * seaH;
    for (var li = 0; li < N; li++) {
      var lf = li / DEN;
      // 惯性系数：远层 2.5，近层 2.5 + inertia*8
      var resp = 2.5 + lf * (T.layerInertia * 8);
      layerY[li] += (targetPx - layerY[li]) * (1 - Math.exp(-dt * resp));
      layerOffsets[li] = layerY[li] - targetPx;   // 相对整体的滞后量
    }

    if (pointerScreen.x === null) mouseStrength *= Math.max(0, 1 - dt * 1.6);

    clock += dt;
    drawSea(clock, targetPx, pointerScreen.x, mouseStrength, layerOffsets);

    requestAnimationFrame(frame);
  }

  sizeSea();
  window.addEventListener('resize', function () { sizeSea(); });

  /* ============================================================
     五、诊断 / 面板接口
     ============================================================ */
  window.__bgState = {
    get tide() { return tidePos; },
    get seaY() { return curSeaY; },
    get mouse() { return { x: current.x, y: current.y }; },
    get hasPhysics() { return !!physics; },
    get seabedOpen() { return seabedOpen; },
    openSeabed: openSeabed,
    closeSeabed: closeSeabed,
    get physicsPos() { return physics ? physics.pos : null; },
    get layers() { return T.layers; },
    get fps() { return Math.round(fpsSmooth); },
    get tune() { return T; },
    setTide: function (p, v) {
      tidePos = p; tideVel = v;
      if (physics) { physics.pos = p; physics.vel = v || 0; physics.sleeping = false; }
    }
  };

  // 主循环延后一帧启动，留出时间给 tuner 覆盖默认参数
  requestAnimationFrame(frame);
})();
