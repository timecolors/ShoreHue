/* 打包成单个自包含 HTML
   ------------------------------------------------------------
   把 styles.css / 3 个 js / 两个 logo 全部内联进 index.html，
   并把指向 ../docs 的死链换成 GitHub 地址。
   产出两份（内容完全一致，同一份 html 变量写出）：
     1) web/ShoreHue-官网.html   —— 双击即可打开，可单独发给别人
     2) docs/index.html          —— GitHub Pages 从 master/docs 发布，
                                    这份让 https://timecolors.github.io/ShoreHue/ 直接能看
   为什么要有第 2 份：Pages 只会发布仓库里的文件，不能指向 web/；
   一份源 → 一次打包 → 两处落地，避免两边各存一份而慢慢不一致。
   用法：node web/tools/pack_single.js
*/
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');      // D:\海岸线
const WEB = path.join(ROOT, 'web');
const OUT = path.join(WEB, 'ShoreHue-官网.html');
const OUT_PAGES = path.join(ROOT, 'docs', 'index.html');

let html = fs.readFileSync(path.join(WEB, 'index.html'), 'utf8');
const report = [];
const fail = [];

function must(cond, msg) { if (!cond) fail.push(msg); }

/* 1) 内联 CSS */
const cssTag = '<link rel="stylesheet" href="styles.css">';
must(html.includes(cssTag), '找不到 styles.css 的 link 标签');
const css = fs.readFileSync(path.join(WEB, 'styles.css'), 'utf8');
must(!/<\/style>/i.test(css), 'styles.css 里含 </style>，内联会破坏文档');
html = html.replace(cssTag, '<style>\n' + css + '\n</style>');
report.push('内联 CSS  ' + (css.length / 1024).toFixed(1) + ' KB');

/* 2) 内联 JS（保持原有顺序） */
const jsFiles = ['js/scroll-physics.js', 'js/glass-bg.js', 'js/tuner.js'];
jsFiles.forEach(function (f) {
  const tag = '<script src="' + f + '"></script>';
  must(html.includes(tag), '找不到 script 标签：' + f);
  const js = fs.readFileSync(path.join(WEB, f), 'utf8');
  must(!/<\/script>/i.test(js), f + ' 里含 </script>，内联会破坏文档');
  html = html.replace(tag, '<script>\n' + js + '\n</script>');
  report.push('内联 ' + f);
});

/* 3) logo → data URI */
['Square150x150Logo.png', 'Square44x44Logo.png'].forEach(function (f) {
  const rel = '../packaging/Assets/' + f;
  must(html.includes(rel), '找不到 logo 引用：' + f);
  const p = path.join(ROOT, 'packaging', 'Assets', f);
  must(fs.existsSync(p), 'logo 文件不存在：' + p);
  const b64 = fs.readFileSync(p).toString('base64');
  const uri = 'data:image/png;base64,' + b64;
  html = html.split(rel).join(uri);
  report.push('内联 logo ' + f + '  ' + (b64.length / 1024).toFixed(1) + ' KB');
});

/* 4) ../docs 死链 → GitHub */
const docsMap = {
  '../docs/PRIVACY.md': 'https://github.com/timecolors/ShoreHue/blob/master/docs/PRIVACY.md',
  '../docs/SECURITY.md': 'https://github.com/timecolors/ShoreHue/blob/master/docs/SECURITY.md'
};
Object.keys(docsMap).forEach(function (k) {
  const n = html.split(k).length - 1;
  if (n > 0) { html = html.split(k).join(docsMap[k]); report.push('死链替换 ' + k + ' ×' + n); }
});

/* 5) 产物自检：不允许再残留任何本地相对引用 */
const leftovers = [];
const re = /(?:src|href)="([^"]+)"/g;
let m;
while ((m = re.exec(html)) !== null) {
  const v = m[1];
  if (/^(https?:|data:|#|mailto:)/.test(v)) continue;
  leftovers.push(v);
}
if (leftovers.length) fail.push('仍有本地引用：' + leftovers.join(', '));

const hasInlineStyle = html.includes('<style>');
const hasThreeScripts = (html.match(/<script>/g) || []).length >= 4;   // 3 个库 + 1 段内联
must(hasInlineStyle, '未找到内联 style');
must(hasThreeScripts, 'script 数量不对（应为 3 个库 + 1 段内联）');

if (fail.length) {
  console.error('打包失败：');
  fail.forEach(function (x) { console.error('  ✗ ' + x); });
  process.exit(1);
}

fs.writeFileSync(OUT, html, 'utf8');
fs.writeFileSync(OUT_PAGES, html, 'utf8');
console.log('打包完成：' + OUT);
console.log('          ' + OUT_PAGES + '  （GitHub Pages 用）');
report.forEach(function (x) { console.log('  · ' + x); });
console.log('  · 产物大小 ' + (Buffer.byteLength(html, 'utf8') / 1024).toFixed(1) + ' KB');
console.log('  · 残留本地引用 0 个 ✓');
