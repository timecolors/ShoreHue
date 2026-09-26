# 第三方素材与组件声明（官网部分）

本页面（`docs/` 下的官网）**不加载任何第三方运行时**：没有 CDN、没有 web font、没有分析脚本、没有 Cookie。
唯一的外部资源是页面里指向 GitHub 的链接。

## 背景贴图（docs/beach/tex/）

海岸线背景用了 6 张 PBR 贴图，来自 [ambientCG](https://ambientcg.com)，采用 **CC0 1.0（公有领域）** 许可
（可商用、免署名；这里仍如实注明来源）：

| 文件 | 来源 | 用途 |
|---|---|---|
| `sand-dry-color.jpg` / `sand-dry-normal.jpg` / `sand-dry-rough.jpg` | ambientCG `Ground080`（细干沙） | 干沙 albedo / 法线 / 粗糙度 |
| `sand-pebble-color.jpg` / `sand-pebble-normal.jpg` | ambientCG `Ground035`（含贝壳碎石的海滩沙） | 打散"同质感"的卵石层 |
| `water-normal.jpg` | 由 `tools/make-water-normal.cjs` 用 **Phillips 谱 + 二维 FFT** 生成（周期可平铺） | 水面法线 |

> 两张 ambientCG 压缩包（约 14MB）保留在 `tools/_tex/` 作为素材来源存档，不参与网站发布。

## 代码

- 海岸线着色器与物理（`docs/beach/*.js`）：本项目原创，随主项目 MIT 许可。
- 做法参考了以下**公开实现**（只参考思路与参数，未复制代码）：
  - three.js `examples/jsm/objects/Water.js`（MIT）—— 多层法线采样的组织方式
  - Roystan《Toon Water Shader》与其 Unity 实现 `IronWarrior/ToonWaterShader`（Unlicense）—— 深度驱动白浪、二值化 + AA
  - Godot 移植版 `hgouveia/godot-toon-water-shader-port`（MIT）—— `beer_factor` 等取值
  - RToon 核心光照文档 —— cel 色阶的 `smoothstep` 阈值切割写法