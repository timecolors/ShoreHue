# ShoreHue「海床」一级分类代码化规范（SEABED-SPEC）

> 面向：AI 与用户共同编写ShoreHue的**新功能代码**。所有一级分类（小组件/面板功能/面板设计/动画/外观/交互/状态栏）
> 均已代码化：把符合规范的 .cs 文件放入对应文件夹，watcher 自动识别 → 编译 → 生效，与内置功能同格式、同管理。
>
> 配套文档：小组件/面板的详细界面规范见 [WIDGET-SPEC.md](./WIDGET-SPEC.md)；实施交接记录见 [HANDOFF-代码化.md](./HANDOFF-代码化.md)。

---

## 〇、文件夹即生态：目录结构总览

数据目录（`%LOCALAPPDATA%/ShoreHue/seabed/`）下的每个**一级分类文件夹**对应一类可编程功能：

```
seabed/
├── 小组件/     → IWidget（标签式小组件，如 计算器/便签）    → 小组件面板标签
├── 面板功能/   → IWidget（区域面板功能，如 最近文件/通知）   → 设置→区域 面板下拉
├── 面板设计/   → 配置代码（config.json / 赋值代码）          → 树节点编辑
├── 动画/       → IAnimation（呼出/隐藏动画）                 → 设置页动画类型可选
├── 外观/       → 配置代码                                    → 树节点编辑
├── 交互/       → 配置代码                                    → 树节点编辑
└── 状态栏/     → IStatusProvider（状态栏显示项）             → 状态栏动态挂载
```

### 每个功能的文件夹格式（三类通用）

```
seabed/<一级分类>/<名字>/
├── main.cs          # 插件源码（必须，public 类实现对应接口）
└── manifest.json    # 元信息（可选但推荐；缺失时按所在分组推断类型）
```

### manifest.json 约定

```json
{
  "name": "CPU 温度",
  "author": "",
  "description": "",
  "kind": "StatusProvider",   // Widget | Panel | Config | StatusProvider | Animation
  "trustedSource": false         // 只有 false 有效（= 外来包，编译前强制过沙箱）；true/缺省都**不构成信任依据**
  "system": false                // true = 内置镜像副本（只展示不加载）
  "permissions": ["network"]   // 可选权限声明
}
```

> ★ `kind` 区分功能类型；同一分组下放错 kind 会被跳过（如 动画/ 里 kind=Widget 不加载）。
> ★ 文件夹里放单个 `.cs` 文件也会被自动归一化为 `<名字>/main.cs`；`.shpkg` 包（旧版 `.dbp`）自动解包。
> ★ 安全（v2）：**完全编程（XAML + 代码后置）对所有来源开放**，但外来来源编译前必须过**受限 XAML 方言**校验
>   （结构白名单，见 `WidgetCompiler.CheckXamlDialect`）——保留"完全编程"这一海床核心能力，砍掉"凭空造对象/按名调方法"。
>   外来代码的特权能力（打开外部/通知/划词/AI/最近使用）只能走窄接口 `HostCapabilities`，宿主内部 API 已被沙箱拦截。
>   **信任只看两件事**：宿主写的来源标记（`.origin` / `trustedSource:false`）或用户在界面上的显式信任记录（id + 内容哈希）；
>   包内自述 `official`/`author`/`trustedSource:true` 一概不算数。你自己或 AI **直接写进 seabed 的代码默认受信**
>   （海床初衷：文件夹即真相源，本地编程不设限、零摩擦）。

---

## 一、小组件 / 面板功能（IWidget）

接口契约、界面风格、沙箱限制全部见 **[WIDGET-SPEC.md](./WIDGET-SPEC.md)**。

速查：

```csharp
public class MyWidget : UserControl, IWidget  // 必须 public
{
    public string Name => "我的小组件";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
    public MyWidget() { Content = ...; }       // 构造函数里搭 UI
}
```

---

## 二、状态栏显示项（IStatusProvider）

**目录**：`seabed/状态栏/<名字>/main.cs`（manifest `kind` = `"StatusProvider"`）

**接口**：`ShoreHue.UI.Status.IStatusProvider`

```csharp
using ShoreHue.UI.Status;
using ShoreHue.Core.Services.Configuration;

public interface IStatusProvider
{
    string Name { get; }        // 显示名（如 "CPU 温度"）
    string IconText { get; }    // 图标（文本符号或文字，如 "☀"）
    string GetText();           // 当前文本（每秒调用一次，UI 线程）
    void OnActivated();         // 挂载时（订阅资源/启动定时器）
    void OnDeactivated();       // 卸载时（释放资源/停止定时器）
    bool IsEnabled(ISettingsService settings);  // 是否显示
}
```

**最小示例**（AI 提示词模板同款）：

```csharp
using System;
using ShoreHue.UI.Status;
using ShoreHue.Core.Services.Configuration;

public class CpuTempProvider : IStatusProvider
{
    public string Name => "CPU 温度";
    public string IconText => "☀";
    public string GetText() => "65°C";   // 实际用 WMI/传感器（后台缓存，禁止每帧阻塞）
    public void OnActivated() { }
    public void OnDeactivated() { }
    public bool IsEnabled(ISettingsService s) => true;
}
```

**生命周期与挂载**：
- 文件放入 `状态栏/` → watcher → 编译（id 前缀 `status_`）→ `SystemStatusView.ApplySettings` 动态 `Children.Add` 到状态栏容器（内置项之后）；
- 每秒 `UpdateStatus()` 调用一次 `GetText()` 更新文本；
- 视图卸载 / 插件移除时调用 `OnDeactivated()`；
- 开关：`settings.IsStatusProviderEnabled("status_<id>")`（缺省启用），插件自身也可在 `IsEnabled` 里二次判断；设置页暂未提供独立开关 UI（后续补）。

**可靠性要求**：
1. `GetText()` 必须在几毫秒内返回（秒级调用，禁止同步 WMI/网络查询）；
2. 定时器用 `DispatcherTimer`，`OnDeactivated` 必须停止；
3. 所有异常 catch，降级返回 `"--"` 之类，禁止崩溃；
4. 不要创建 UI 控件——系统自动生成「图标+文本」布局。

---

## 三、自定义动画（IAnimation）

**目录**：`seabed/动画/<名字>/main.cs`（manifest `kind` = `"Animation"`）

**接口**：`ShoreHue.Animation.IAnimation`

```csharp
using System;
using System.Windows;
using ShoreHue.Animation;

public interface IAnimation
{
    string Name { get; }   // 显示名（如 "弹跳"，设置页动画类型下拉展示）
    string Id { get; }     // 唯一标识（设置里存这个值，ShapeAnimator 据此查注册表）
    void AnimateShow(FrameworkElement panel, Window window, double ms, Action onCompleted);
    void AnimateHide(FrameworkElement panel, Window window, double ms, Action onCompleted);
}
```

**最小示例**（DispatcherTimer 驱动）：

```csharp
using System;
using System.Windows;
using System.Windows.Threading;
using ShoreHue.Animation;

public class BounceAnimation : IAnimation
{
    public string Name => "弹跳";
    public string Id => "bounce";
    private DispatcherTimer? _timer;

    public void AnimateShow(FrameworkElement panel, Window window, double ms, Action onCompleted)
    {
        double start = window.Left;
        double end = start + 40;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            double p = sw.Elapsed.TotalMilliseconds / ms;
            if (p >= 1) { _timer.Stop(); window.Left = end; onCompleted(); return; }
            window.Left = start + (end - start) * p;   // 弹跳可叠加三角函数
        };
        _timer.Start();
    }

    public void AnimateHide(FrameworkElement panel, Window window, double ms, Action onCompleted)
    {
        panel.Opacity = 0;
        onCompleted();
    }
}
```

**★ 渲染帧热路径保护（必须遵守，违反会导致 100% CPU 卡死——项目历史教训）**：

1. **禁止**直接订阅 `CompositionTarget.Rendering` 而不取消；用 `DispatcherTimer` 逐帧驱动；
2. 动画结束（或 `onCompleted`）时必须停定时器；`onCompleted` **只调一次**；
3. 时长 `ms` 毫秒，进度 = 已用/ms，到 1 落终值并回调；
4. 异常 catch 并仍回调（系统有 try-catch + 超时兜底，但正常完成不依赖兜底）；
5. 位置动画改 `window.Left/Top`，透明度动画改 `panel.Opacity`；隐藏动画要把透明度降到 0。

**系统侧保护（ShapeAnimator 内置，无需插件关心）**：
- 异常隔离：自定义动画抛异常 → 回退内置 Slide（滑入/滑出）；
- 超时保护：时长 ×2 后未回调 → 强制完成（停动画 + 落终值）；
- 完成回调幂等：终值只落一次；
- 市场来源编译前过沙箱（`WidgetCompiler.SandboxErrors`）。

**设置联动**：
- 编译后自动注册进 `AnimationRegistry`（key = 动画 `Id`）；
- 设置页「动画」的 触发/隐藏动画类型下拉自动出现自定义动画项（显示 `Name`，存 `Id`）；
- 区域动画覆盖（动画应用于）同样可选自定义动画；
- `GetResolvedShowAnimationType/HideAnimationType` 返回自定义 `Id` 时，ShapeAnimator 查注册表分发；删除插件后配置里的 Id 自动回退内置 Slide。

---

## 四、配置代码节点（Kind=Config）

配置类功能（面板设计/外观/交互 的叶子节点）保持 **config.json / 赋值代码** 模式：

```csharp
public static class ConfigCode
{
    public static void Apply(SettingsData data)
    {
        data.ShowAnimationType = "Slide";   // 每行一个赋值
        data.ShowAnimationDurationMs = 150;
    }
}
```

- 编译校验后保存为「单预设」，可在海床内「应用」并标记冲突变灰；
- 新增设置字段必须同步三处：`SettingsData` + `ConfigTreeBuilder` + `SettingsFieldDocs`（两个测试强制：ConfigTreeCoverageTests / SettingsFieldDocsTests）。

---

## 五、AI 提示词（PromptGenerator 分发）

海床「复制 AI 提示词」按节点 `Kind` 分发生成不同模板：

| 节点 Kind | 生成模板 | 产出文件去向 |
|-----------|----------|--------------|
| `StatusProvider` | IStatusProvider 完整 .cs 模板 | `seabed/状态栏/<名字>/main.cs` |
| `Animation`     | IAnimation 完整 .cs 模板     | `seabed/动画/<名字>/main.cs`   |
| `Widget`/`Panel` | IWidget 面板源码模板（见 WIDGET-SPEC） | `seabed/小组件|面板功能/<名字>/main.cs` |
| 标准节点 | ConfigCode 配置代码模板 | 树内保存为单预设 |

模板内置：接口契约、可靠性要求、沙箱禁用清单、自检清单、报错处理约定。

---

## 六、沙箱（市场来源代码）

`trustedSource=false`（市场安装/导入）的代码编译前强制过沙箱：
- **文本扫描**（`CheckSandbox`）：Process.Start / 反射 / Activator / DllImport / Marshal / 注册表 / WMI / 窗口钩子 / 输入注入 / 屏幕捕获 / 文件写删移 / 目录创建；
- **编译符号级检查**（`CheckSandboxSymbols`）：类型黑名单 + 成员黑名单，换皮写法也拦得住；
- **组合检测**：文件读 + 网络 = 数据外泄，直接拦截；
- 本地自用代码（`trustedSource` 缺省/true）不检测（HANDOFF 设计：本地编程不设限）。

---

## 七、测试与回归

- `ConfigTreeCoverageTests`：SettingsData 每个属性必须挂进配置树或进白名单（运行时字典类字段加白名单）；
- `SettingsFieldDocsTests`：树每个叶子字段必须有中文说明；
- `WidgetCompilerTests`：编译链路（含 `Compile<T>` 泛型重载）；
- `WidgetPluginStoreFolderTests`：文件夹增删/归一化；
- `ShapeAnimatorSmokeTests / FrameSkipTests`：动画器公开 API 与跳帧；
- 端到端探针 `tools/CodecifyProbe`：隔离目录验证 状态栏插件 + 自定义动画 的编译→扫描→挂载/分发→开关→保护全链路。

