/* ============================================================
   海面参数面板
   ------------------------------------------------------------
   页面右上角「参数」按钮展开。所有改动直接写入 window.__seaTune，
   下一帧就生效，不需要刷新。
   调好后点「复制参数」，把内容发我，我固化进代码。
   ============================================================ */
(function () {
  'use strict';

  var T = window.__seaTune;
  if (!T) return;

  /* 参数清单：分组 → [键, 显示名, 最小, 最大, 步长]
     命名按"用户看得懂"来，不用 layes/amp/k 这类实现词。
     这本身也是产品理念的一部分：连宣传页的背景都交给你调。 */
  var SPECS = [
    ['水面层次', [
      ['layers',    '水层数量',   2,    30,   1],
      ['spread',    '层间疏密',   0,    0.30, 0.005],
      ['waveBlur',  '柔和程度',   0,    30,   0.5],
      ['waveAlphaMin', '远处浓淡', 0,   0.25, 0.005],
      ['waveAlphaMax', '近处浓淡', 0,   0.30, 0.005]
    ]],
    ['波浪形态', [
      ['amp1', '起伏幅度 A', 0, 12, 0.1],
      ['amp2', '起伏幅度 B', 0, 12, 0.1],
      ['amp3', '细纹幅度 C', 0, 12, 0.1],
      ['k1',   '波长 A', 0.0005, 0.02, 0.0001],
      ['k2',   '波长 B', 0.0005, 0.03, 0.0001],
      ['k3',   '细纹波长', 0.0005, 0.05, 0.0001]
    ]],
    ['流动', [
      ['s1', '流速 A', 0, 3, 0.01],
      ['s2', '流速 B', 0, 3, 0.01],
      ['s3', '细纹流速', 0, 4, 0.01],
      ['phase', '层间错位', 0, 3, 0.01],
      ['layerInertia', '分层惯性', 0, 1, 0.02]
    ]],
    ['潮位与手感', [
      ['top',        '水位高低',   0.20, 0.85, 0.005],
      ['gradeSpan',  '深浅过渡',   0.20, 1.20, 0.01],
      ['pointerDip', '指针压水',   0,    24,   0.5],
      ['dpr',        '渲染精度',   0.6,  2,    0.05]
    ]]
  ];

  var COLORS = [
    ['c1', '近岸最浅'],
    ['c2', ''],
    ['c3', ''],
    ['c4', ''],
    ['c5', ''],
    ['c6', '深海最深']
  ];

  /* ---------------- 样式 ---------------- */
  var css = document.createElement('style');
  css.textContent = [
    '#tuner{position:fixed;right:22px;bottom:22px;z-index:200;display:flex;flex-direction:column-reverse;align-items:flex-end;font:12px/1.5 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif;color:#eaf4fb;}',
    '#tunerBtn{display:flex;align-items:center;gap:6px;padding:7px 14px;border-radius:999px;',
    'background:rgba(6,26,44,.72);border:1px solid rgba(255,255,255,.18);color:#eaf4fb;',
    'cursor:pointer;backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);font-size:12px;}',
    '#tunerBtn:hover{background:rgba(76,194,255,.22);}',
    '#tunerBody{display:none;width:312px;max-height:calc(100vh - 140px);overflow-y:auto;margin-bottom:10px;padding:0;',
    'border-radius:20px;background:rgba(6,26,44,.86);border:1px solid rgba(255,255,255,.18);',
    'box-shadow:0 30px 70px -40px rgba(0,0,0,.95);',
    'backdrop-filter:blur(22px) saturate(1.6);-webkit-backdrop-filter:blur(22px) saturate(1.6);}',
    '.tn-head{padding:14px 16px 12px;border-bottom:1px solid rgba(255,255,255,.12);}',
    '.tn-head b{display:block;font-size:14px;font-weight:600;color:#f2f7fb;margin-bottom:4px;}',
    '.tn-head span{display:block;font-size:11.5px;line-height:1.6;color:rgba(242,247,251,.55);}',
    '.tn-body-in{padding:12px 16px 14px;}',
    '#tunerBody.open{display:block;}',
    '#tunerBody::-webkit-scrollbar{width:6px;}',
    '#tunerBody::-webkit-scrollbar-thumb{background:rgba(76,194,255,.35);border-radius:3px;}',
    '.tn-grp{margin:0 0 10px;padding:0 0 8px;border-bottom:1px solid rgba(255,255,255,.09);}',
    '.tn-grp:last-child{border-bottom:0;}',
    '.tn-grp>b{display:block;margin-bottom:6px;font-size:11px;letter-spacing:.12em;color:#8ce0d0;font-weight:600;}',
    '.tn-row{display:flex;align-items:center;gap:8px;margin:5px 0;}',
    '.tn-row>span:first-child{flex:0 0 84px;color:rgba(234,244,251,.82);}',
    '.tn-row input[type=range]{flex:1;height:3px;-webkit-appearance:none;appearance:none;',
    'background:rgba(255,255,255,.2);border-radius:2px;outline:none;}',
    '.tn-row input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:12px;',
    'border-radius:50%;background:#4cc2ff;cursor:pointer;box-shadow:0 0 8px rgba(76,194,255,.7);}',
    '.tn-row input[type=range]::-moz-range-thumb{width:12px;height:12px;border:0;border-radius:50%;background:#4cc2ff;cursor:pointer;}',
    '.tn-val{flex:0 0 54px;text-align:right;font-variant-numeric:tabular-nums;color:#9fd8f5;}',
    '.tn-col{display:flex;align-items:center;gap:8px;margin:6px 0;}',
    '.tn-col>span{flex:1;color:rgba(234,244,251,.82);}',
    '.tn-col input[type=color]{width:38px;height:22px;padding:0;border:1px solid rgba(255,255,255,.25);',
    'border-radius:5px;background:transparent;cursor:pointer;}',
    '.tn-foot{display:flex;gap:8px;margin-top:10px;}',
    '.tn-foot button{flex:1;padding:7px 8px;border-radius:9px;cursor:pointer;font-size:11.5px;',
    'background:rgba(76,194,255,.18);border:1px solid rgba(76,194,255,.4);color:#dff2ff;}',
    '.tn-foot button:hover{background:rgba(76,194,255,.3);}',
    '#tnFps{margin-left:auto;color:#8ce0d0;font-variant-numeric:tabular-nums;}'
  ].join('');
  document.head.appendChild(css);

  /* ---------------- DOM ---------------- */
  var root = document.createElement('div');
  root.id = 'tuner';

  var btn = document.createElement('button');
  btn.id = 'tunerBtn';
  btn.innerHTML = '✦ 自定义背景 <span id="tnFps">--</span>';
  root.appendChild(btn);

  var body = document.createElement('div');
  body.id = 'tunerBody';

  // 面板抬头：说清楚这是什么、为什么可以调
  var head = document.createElement('div');
  head.className = 'tn-head';
  head.innerHTML = '<b>自定义背景</b><span>这段海岸线由 Canvas 逐帧绘制，下面每个参数都实时生效。</span>';
  body.appendChild(head);

  root.appendChild(body);

  function fmt(v) {
    if (Math.abs(v) >= 100) return v.toFixed(0);
    if (Math.abs(v) >= 1) return v.toFixed(2);
    return v.toFixed(4);
  }

  var readouts = [];

  function addSlider(parent, key, label, min, max, step) {
    var row = document.createElement('label');
    row.className = 'tn-row';

    var name = document.createElement('span');
    name.textContent = label;

    var input = document.createElement('input');
    input.type = 'range';
    input.min = min; input.max = max; input.step = step;
    input.value = T[key];

    var val = document.createElement('span');
    val.className = 'tn-val';
    val.textContent = fmt(T[key]);

    input.addEventListener('input', function () {
      T[key] = parseFloat(input.value);
      val.textContent = fmt(T[key]);
    });

    row.appendChild(name); row.appendChild(input); row.appendChild(val);
    parent.appendChild(row);

    readouts.push(function () {
      input.value = T[key];
      val.textContent = fmt(T[key]);
    });
  }

  function addColor(parent, key, label) {
    var row = document.createElement('label');
    row.className = 'tn-col';

    var name = document.createElement('span');
    name.textContent = label;

    var input = document.createElement('input');
    input.type = 'color';
    input.value = T[key];
    input.addEventListener('input', function () { T[key] = input.value; });

    row.appendChild(name); row.appendChild(input);
    parent.appendChild(row);
    readouts.push(function () { input.value = T[key]; });
  }

  var inner = document.createElement('div');
  inner.className = 'tn-body-in';
  body.appendChild(inner);

  SPECS.forEach(function (grp) {
    var box = document.createElement('div');
    box.className = 'tn-grp';
    var title = document.createElement('b');
    title.textContent = grp[0];
    box.appendChild(title);
    grp[1].forEach(function (it) { addSlider(box, it[0], it[1], it[2], it[3], it[4]); });
    inner.appendChild(box);
  });

  var cbox = document.createElement('div');
  cbox.className = 'tn-grp';
  var ctitle = document.createElement('b');
  ctitle.textContent = '水色（由浅到深）';
  cbox.appendChild(ctitle);
  COLORS.forEach(function (c) { addColor(cbox, c[0], c[1]); });
  inner.appendChild(cbox);

  /* DPR 改完需要重建 canvas 尺寸 */
  var dprInput = body.querySelector('input[type=range][max="2"]');
  if (dprInput) {
    dprInput.addEventListener('input', function () {
      if (window.__resizeSea) window.__resizeSea();
    });
  }

  var foot = document.createElement('div');
  foot.className = 'tn-foot';

  var copyBtn = document.createElement('button');
  copyBtn.textContent = '复制参数';
  copyBtn.addEventListener('click', function () {
    var out = {};
    Object.keys(T).forEach(function (k) { out[k] = T[k]; });
    var text = JSON.stringify(out, null, 2);
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(function () {
        copyBtn.textContent = '已复制 ✓';
        setTimeout(function () { copyBtn.textContent = '复制参数'; }, 1400);
      }, function () { window.prompt('复制下面的参数：', text); });
    } else {
      window.prompt('复制下面的参数：', text);
    }
  });

  var resetBtn = document.createElement('button');
  resetBtn.textContent = '恢复默认';
  resetBtn.addEventListener('click', function () {
    if (window.__seaTuneDefaults) {
      Object.keys(window.__seaTuneDefaults).forEach(function (k) {
        T[k] = window.__seaTuneDefaults[k];
      });
      readouts.forEach(function (fn) { fn(); });
      if (window.__resizeSea) window.__resizeSea();
    }
  });

  foot.appendChild(copyBtn);
  foot.appendChild(resetBtn);
  inner.appendChild(foot);

  document.body.appendChild(root);

  btn.addEventListener('click', function () {
    body.classList.toggle('open');
  });

  /* 按 T 键快速收起面板（方便看效果） */
  document.addEventListener('keydown', function (e) {
    if (e.key === 't' || e.key === 'T') {
      if (document.activeElement && document.activeElement.tagName === 'INPUT') return;
      body.classList.toggle('open');
    }
  });

  /* FPS 显示 */
  var fpsEl = document.getElementById('tnFps');
  setInterval(function () {
    if (!window.__bgState) return;
    var f = window.__bgState.fps;
    fpsEl.textContent = f + ' fps';
    fpsEl.style.color = f >= 55 ? '#8ce0d0' : (f >= 35 ? '#f0d090' : '#ff9a8a');
  }, 500);
})();
