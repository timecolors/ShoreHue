# ShoreHue AI 编程指南（海床）

> 本文档说明：用 AI（如 DeepSeek 网页版）为 ShoreHue 编写功能时，**能做什么、不能做什么、怎么做**。
> 配套：海床界面 → 选中分类节点 → 「复制 AI 提示词」按钮获取该类的模板（提示词与本文件同源同口径）。
> 文件放入：`%LOCALAPPDATA%\ShoreHue\seabed\` 下对应位置，**保存即自动编译生效**（watcher 监听增删改名与内容写入）。

---

## 一、总览：能做什么，不能做什么

### 能做的（AI 编程生态）

海床根 = `%LOCALAPPDATA%\ShoreHue\seabed\`，顶层就是四个页签目录：`常规\`、`区域\`、`面板\`、`动画\`。

| 分类 | 放哪（相对海床根） | 文件形态 | manifest | 效果 |
|------|------|---------|---------|------|
| **小组件** | `面板\<id>\` | `main.cs`，或 `<id>.xaml` + `<id>.xaml.cs` | **必须**，`kind: "Widget"` | 出现在小组件面板的标签 |
| **面板功能** | `面板\<id>\` | 同上 | **必须**，`kind: "Panel"` | 可在 设置→区域→面板类型 里选作某区域的显示内容 |
| **状态栏显示项** | `面板\状态栏\<id>\` | `main.cs` | 建议有（`kind: "StatusProvider"`），省略时按父目录名推断 | 出现在状态栏（内置项之后） |
| **动画** | `动画\<id>\` | `main.cs` | 建议有（`kind: "Animation"`），省略时按父目录名推断 | 出现在 设置→动画 的类型下拉，可选作呼出/隐藏动画 |
| **配置调整** | `常规\`、`区域\`、`面板\`、`动画\` 下的配置节点 | `config.json` | 由系统维护 | 修改动画/外观/交互/状态栏参数 |

> ★ **小组件与面板功能是平级的**：目录扁平化之后两者都在 `面板\` 下直接铺开（没有 `面板\小组件\` 这样的中间层），
>    **靠 manifest 里的 `kind` 区分** —— 因为父目录名推不出类型。状态栏与动画则相反，所在目录名本身（`状态栏` / `动画`）就是判据。
> ★ **文件名必须与目录名一致**：完全编程形态要求目录 `timer\` 里的界面文件叫 `timer.xaml`（加载器按 `<目录名>.xaml` 查找）。
>    不一致时海床会在保存结果里给出警告。
> ★ 找不到目录时：海床界面选中对应节点 →「打开文件夹」直达真实位置。

### 两种编程模式（海床「复制 AI 提示词」旁可选）

| 模式 | 产出 | 适用 | 可用分类 |
|------|------|------|---------|
| **简单编程**（默认） | 纯 C# 单文件（`main.cs`） | 快速功能、逻辑为主、界面简单 | 全部 4 类 |
| **完全编程（完全编译）** | XAML + 代码后置（`<id>.xaml` + `<id>.xaml.cs`） | 界面复杂、需要精细布局/样式/事件 | 小组件、面板功能 |

- **简单编程**：AI 生成 `main.cs`（全 C# 构建 UI），放入 `<分类>\<id>\` 生效；
- **完全编程 = 完整 WPF 用户控件编译**：AI 生成 `<id>.xaml` + `<id>.xaml.cs` 两个**同名**文件
  （`.xaml.cs` 里 `public partial class <类名> : UserControl, IWidget`，`CreateView() => this`），
  放入 `面板\<id>\`（面板全部平铺在这一层）；
- 系统检测到同目录 `.xaml` + `.xaml.cs` 就走 **XAML 完全编译**（两者编入同一程序集，事件/绑定/样式/触发器全部可用）；
  否则回退纯代码编译；
- 界面提供**实时预览**：保存后立即重新编译并刷新，编译报错内联显示（可直接贴回 AI 修改）；
- 状态栏 / 动画只有 `main.cs` 一种形态（无 XAML），系统会自动强制简单模式。

### 安全限制（沙箱）

**本地自用不设限**（你自己写的、你自己承担）；**只有"外来来源"**（市场安装、带宿主来源标记的包）在编译前过沙箱。
沙箱是**三层**，不是一层字符串匹配：

1. **文本层粗筛**：扫源码里的危险字面串（廉价快筛，挡明显特征）；
2. **符号层（真正的闸门，无法换皮绕过）**：用 Roslyn 拿到语义符号后判定：
   - **类型黑名单**：进程/反射/Interop/WMI/注册表/活动目录/剪贴板/写流/输入注入等，命中即整类型拦截；
   - **成员白名单**：对"可读类型"采取**白名单放行、其余全部拦截**（fail-closed）——
     黑名单必漏（`File.CreateText` / `File.OpenWrite` / `File.AppendAllText`… 逐个列永远列不全）；
   - **数据流外泄判定**：读敏感来源（本地文件/剪贴板/宿主私有数据）→ 流向**网络出口**即拦截。
     出口清单覆盖 `HttpClient` / `Socket` / `TcpClient` / `UdpClient` / `NetworkStream` / `SmtpClient` /
     `Ping` / `ClientWebSocket` / DNS 查询 / WPF 的 `BitmapImage`·`MediaPlayer`·`WebBrowser`，
     以及宿主窄接口的 `OpenExternally` / `AskAiAsync`；
3. **XAML 结构白名单**（只对 XAML 形态）：走 XML 树而不是字符串——
   - 元素必须属于界面子集：`ObjectDataProvider` / `x:Code` / `MediaElement` / `WebBrowser` / `Hyperlink` 等一律拒绝；
   - 命名空间只认 WPF 官方程序集，**自定义 `clr-namespace:` 一律拒绝**，
     且显式拒绝 `System.Diagnostics` / `Reflection` / `InteropServices` / `Runtime.Loader` / `Management` /
     `Microsoft.Win32` / `IO` / `Net` / `Cryptography` / `Activator` 以及 `ShoreHue.*` 内部命名空间；
   - 事件只放行既有白名单（由生成的 C# 挂接，仍受符号级检查约束）；
   - 标记扩展只放行既有白名单（`x:Static` / `x:Type` / `ObjectDataProvider` 之类不在其中）。

**上架市场**另有硬性要求：**必须提供 `main.cs`**（哪怕同时提供 XAML 形态）——
CI 的 `MarketValidator` 会拒绝"只有 XAML、没有 main.cs"的包。详见 `docs/SECURITY.md`。

### 不能做的（边界）

1. **不能修改 ShoreHue 内核**：边缘检测、面板容器、窗口动画系统、托盘、设置框架是写死的——AI 编的插件只能"长在"内核上。
2. **不能碰危险系统 API**（市场发布时会被沙箱拦截）：进程操作、反射、注册表、WMI、全局钩子、模拟输入、
   屏幕截图、文件写入、数据外传等。本地自用可以（有提示），上架市场不行。
3. **不能做常驻后台任务**：插件只在激活期间运行（面板显示 / 状态栏挂载期间），面板隐藏后 `OnDeactivated` 会被调用，必须停掉定时器。
4. **动画不能无限自激**：动画驱动必须用 `DispatcherTimer` 且动画结束就停，禁止长期订阅渲染帧（否则 100% CPU 卡死）。

---

## 二、4 类功能教程

### 1. 小组件（最常见）

**目录**：`seabed\面板\<英文id>\` + `main.cs`（或 `<id>.xaml` + `<id>.xaml.cs`）+ `manifest.json`

**接口契约**：
```csharp
public class 你的类 : UserControl, ShoreHue.UI.Widgets.IWidget
{
    public string Name => "显示名";              // 中文
    public UserControl CreateView() => this;     // 返回 this
    public void OnActivated() { }                // 激活时（启动定时器等）
    public void OnDeactivated() { }              // 切走时（停止定时器）
}
```

**可选：页脚控件**（如计时器面板底部那条按钮栏）——额外实现 `IWidgetFooter` 即可，不实现就没有页脚：
```csharp
public class 你的类 : UserControl, ShoreHue.UI.Widgets.IWidget, ShoreHue.UI.Widgets.IWidgetFooter
{
    public FrameworkElement GetFooterControl() => 你的页脚控件;
}
```

**要点**：
- **简单模式**：全部用 C# 代码构建 UI（`new StackPanel/TextBlock/Button...`），不要 XAML
- **完全模式**：用 `.xaml` + `.xaml.cs` 写真实 WPF 布局，XAML 里可写事件/绑定/样式
- 背景建议深色 `#1E1E1E`、文字白色（浅色主题下由窗口整体负责）；也可在 XAML 里自由设计
- 定时器用 `DispatcherTimer`（自动回 UI 线程）
- 联网用 `HttpClient` + 超时，异常 catch 后显示友好提示

**manifest.json**：
```json
{
  "id": "你的英文id",
  "name": "中文名",
  "kind": "Widget",
  "category": "小组件",
  "permissions": []
}
```

### 2. 面板功能

**目录**：`seabed\面板\<英文id>\` + `main.cs`（或 XAML 形态）+ `manifest.json`（`kind: "Panel"`）

**与小组件区别**：同样实现 `IWidget`，但它是"整块面板"而非小组件标签。放入后需在 **设置 → 区域 → 面板类型**
里选它作为某区域的显示内容（选后显示为 `Custom:面板id`）。

> ★ 两者目录**同级**（都在 `面板\` 下），唯一的区分就是 manifest 里的 `kind`。

### 3. 状态栏显示项

**目录**：`seabed\面板\状态栏\<名字>\` + `main.cs` + `manifest.json`（`kind: "StatusProvider"`）

**接口**：
```csharp
public class 你的类 : ShoreHue.UI.Status.IStatusProvider
{
    public string Name => "CPU 温度";       // 中文名
    public string IconText => "☀";        // 图标（文本符号或文字）
    public string GetText() => "65°C";      // 每秒调用，必须毫秒级返回
    public void OnActivated() { }            // 挂载时
    public void OnDeactivated() { }          // 卸载时（停定时器）
    public bool IsEnabled(ISettingsService s) => true;
}
```

**要点**：
- **不要创建 UI 控件**——系统自动生成「图标+文本」布局，插件只提供数据
- `GetText()` 每秒调用，禁止耗时操作（WMI/网络放后台线程缓存结果）
- 定时器用 `DispatcherTimer`，`OnDeactivated` 必须 `Stop`

### 4. 动画

**目录**：`seabed\动画\<名字>\` + `main.cs` + `manifest.json`（`kind: "Animation"`）

**接口**：
```csharp
public class 你的类 : ShoreHue.Animation.IAnimation
{
    public string Name => "弹跳";                    // 中文，设置下拉展示
    public string Id => "bounce";                    // 英文唯一标识
    public void AnimateShow(FrameworkElement panel, Window window, double ms, Action onCompleted) { }
    public void AnimateHide(FrameworkElement panel, Window window, double ms, Action onCompleted) { }
}
```

**要点（★ 渲染帧热路径，必须遵守）**：
- 用 `DispatcherTimer` 逐帧驱动（每帧约 16ms），Tick 里推进进度
- **禁止直接订阅 `CompositionTarget.Rendering` 而不取消**——会导致 100% CPU 卡死（项目历史教训）
- 进度 = `elapsed/ms`，到 1 时调 `onCompleted`（**只调一次**）并停定时器
- 位置动画改 `window.Left/Top`，透明度动画改 `panel.Opacity`
- 隐藏动画必须把 `panel.Opacity` 降到 0
- 所有异常 catch 并仍调 `onCompleted`（系统有超时兜底）

---

## 三、工作流程

1. **进入海床**：设置 → 海床（编程模式）→ 选择要添加的分类节点（在 面板 / 面板-状态栏 / 动画 下）
2. **选模式**：小组件/面板功能可在「复制 AI 提示词」旁切换 **简单编程 / 完全编程**（状态栏与动画固定简单编程）
3. **复制提示词**：点「复制 AI 提示词」→ 粘贴到 AI（DeepSeek 等），描述你想要的效果
4. **AI 生成代码**：简单模式输出 `main.cs`；完全模式输出 `<id>.xaml` + `<id>.xaml.cs`（+ `manifest.json`）
5. **放入文件夹**：把文件存入对应目录（完全模式两个文件必须**同名同目录**，且名字与目录名一致）
6. **自动生效 + 实时预览**：watcher 检测 → 自动编译 → 出现在对应位置并刷新预览；完全模式的报错会内联显示
7. **有报错**：把编译报错信息粘贴回 AI，让它修正（提示词已包含报错处理指引）

**小技巧**：在海床界面选中某节点点「打开文件夹」，可直接定位到该节点目录，方便放文件。

---

## 四、常见错误与排查

| 症状 | 原因 | 解决 |
|------|------|------|
| 小组件不出现 | `main.cs` 编译失败 | 看日志「小组件 [id] 编译失败: …」；把报错贴回 AI |
| 放在 `面板\` 下却识别不出类型 | manifest 缺失或 `kind` 写错 | `面板\` 下的件**必须**有 manifest，`kind` 为 `Widget` 或 `Panel` |
| 完全编程不生效（当作不存在） | 文件名与目录名不一致 | 目录 `timer\` 里必须是 `timer.xaml` / `timer.xaml.cs` |
| 状态栏项不出现 | 接口方法没实现全 / `GetText` 抛异常 | 检查 6 个成员是否齐全；`GetText` catch 后返回 `"--"` |
| 动画在设置里没有 | `Id` 冲突或编译失败 | 换唯一 `Id`；看日志「动画编译失败」 |
| 面板显示「编译失败」 | 源码有问题 | 看日志；确保实现 `IWidget` 全部成员 |
| XAML 报错 `x:Class` 不匹配 | `.xaml` 的 `x:Class` 与 `.xaml.cs` 的 partial class 名字不一致 | 两个文件用同一类名 |
| XAML 报错根元素错误 | `.xaml` 根不是 `UserControl` | 小组件/面板必须根为 `UserControl`（不要 `Window`） |
| XAML 找不到类型/命名空间 | 代码后置少了 using | 检查 `.xaml.cs` 顶部 using；自定义样式请内联在窗口资源里 |
| 市场包被 CI 拒 | 只有 XAML、没有 `main.cs` | 上架包**必须**提供 `main.cs` |
| 面板/状态栏闪退 | 定时器没在 `OnDeactivated` 停止 | 必须 `DispatcherTimer` + `OnDeactivated` 里 `Stop` |
| CPU 100% 卡死 | 动画/更新循环自激 | 动画禁止常驻渲染帧订阅；确保动画结束停定时器 |

**日志位置**：`%LOCALAPPDATA%\ShoreHue\Logs\log-*.log`（搜索「编译失败」/「沙箱」/「跳过无法读取」）

**出问题了先看这三处**：
1. 日志里有没有「编译失败」「被沙箱拦截」「添加宿主程序集引用失败」；
2. 海床树该节点上有没有问题标记（编译报错是红色、未启用是主题色、被预设覆盖是灰色删除线）；
3. 目录结构对不对（尤其"文件名要与目录名一致""`面板\` 下必须有 manifest"这两条）。
