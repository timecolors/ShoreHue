# ShoreHue 海床小组件开发指南

动态小组件/面板用 **C# 代码**编写（完整自由度：任意 WPF UI 与逻辑）。应用内置 Roslyn 编译器，保存/安装时把源码编译成可运行的小组件。

> 本文档面向「海床（编程模式）」用户：既能自己写，也能把本文档（尤其是第 9 节）整段喂给任意 AI 生成代码。

---

## 0. 入口与工作流（海床 / 编程模式）

1. 打开 设置 → 常规 → 勾选「编程模式（海床）」→ 设置窗口出现「海床」页签。
2. 海床页左侧是**配置树**：所有设置以树形呈现；自定义面板/小组件/配置项在树中编辑源码。
3. 在树中选中节点 → 右侧编程框写代码 →「编译」校验（失败会标红并显示错误）→「保存」。
4. 「复制 AI 提示词」：把当前节点连同本文档第 9 节的完整规范复制给 AI，让 AI 生成代码后粘回来编译。
5. **保存当前节点**：标准功能节点保存为「变体」（同名 + 数字后缀）；自定义项直接保存。
   - 小组件变体（Kind=Widget）→ 出现在小组件面板标签
   - 面板变体（Kind=Panel）→ 出现在 设置→区域 的面板下拉，可分配到任意边缘/角落
   - 配置变体（Kind=Config）→ 仅海床内，保存为单预设可「应用」
6. **本地自用**：你在海床里**新建/编辑并保存**的代码是可信的（应用会写 trustedSource: true），不设限；
   **市场来源**（在线市场「拾贝」/ 导入 .shpkg 旧 .dbp）与**手动丢进文件夹的代码**默认走沙箱（见第 5 节）。

---

## 1. 接口契约（IWidget）

小组件/面板就是实现 **`ShoreHue.UI.Widgets.IWidget`** 的公开类，并继承 `UserControl`：

```csharp
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

public class MyWidget : UserControl, IWidget
{
    public MyWidget()
    {
        // 构造函数里搭建 UI（任意 WPF 控件），赋给 Content
        Content = new System.Windows.Controls.TextBlock { Text = "你好" };
    }

    public string Name => "我的小组件";      // 面板/标签显示名
    public UserControl CreateView() => this; // 固定写法：返回自身
    public void OnActivated() { }            // 面板激活时回调（可选：启动定时器）
    public void OnDeactivated() { }          // 面板切走/隐藏时回调（可选：停定时器、清理）
}
```

### 接口要求速查
- 类必须是**公开**（`public class`），命名空间随意（不能重名冲突即可）；
- 必须**继承 `UserControl`** 并**实现 `IWidget`**（四个成员全实现）；
- `Name` 是标签显示名，不是文件名；
- `CreateView()` 固定返回 `this`；
- 定时器/事件/动画用 WPF 标准方式，无需额外注册。

---

## 2. 界面构建约定（代码构建，无 XAML）

- 全部用 C# 代码搭 UI（`new StackPanel/Grid/TextBlock/Button/TextBox/ListBox/ScrollViewer...`），不用写 XAML；
- 面板背景是**深色 Mica**：文字用**白色/浅色**，背景卡片用半透明白或深灰，保持与系统一致；
- 控件尺寸建议：按钮高 26–28，字体 12–13，圆角 6；
- **可用项目统一样式资源**（`FindResource(...)` 或 StaticResource 直接拿）：

| 资源名 | 用途 |
|---|---|
| `CardStyle` | Border 卡片（深色圆角） |
| `FlatButton` | 透明圆角按钮（hover 高亮，适合工具条） |
| `AccentButton` | 主题色强调按钮（主操作，蓝底白字） |
| `IconButton` | 28×24 方形小图标按钮 |
| `Win11Button` | Win11 浅色按钮（浅色窗口/设置页用） |
| `Win11AccentButton` | Win11 主题色按钮（设置页主操作） |
| `DarkTextBox` | 深色输入框（面板内输入） |
| `LineIcon` + `Icon*` | 线性图标（`FindResource("IconLogo")` 等，AppIcons.xaml） |
| `InfoTipStyle` | 问号提示（ToolTip 挂 InfoTip） |

示例（面板内用统一风格）：
```csharp
var btn = new Button { Content = "开始", Height = 28, FontSize = 12 };
btn.Style = (Style)FindResource("AccentButton");
```

---

## 3. 线程与注意事项（必读）

- **UI 更新必须在 UI 线程**：构造函数、事件回调天然在 UI 线程；`async/await` 后回 UI 线程用 `Dispatcher.BeginInvoke` 或 `await Dispatcher.InvokeAsync`；
- **定时器用 `DispatcherTimer`**（自动回 UI 线程），**不要**用 `System.Threading.Timer`（回调在后台线程，碰 UI 会崩）；
- **剪贴板**：`Clipboard.SetText` 需要 STA/UI 线程（小组件运行在 UI 线程，直接调用即可）；
- **资源清理**：`DispatcherTimer` 记得 `Stop()`——放在 `OnDeactivated()` 里（面板切走时调用）；
- **编译失败**：海床编程框会显示错误行号与信息，修好后重新「编译」；
- **网络**：`HttpClient` 可用（本地自用不拦截）；请求超时要设 `Timeout`，失败要 catch 并给 UI 提示，别让面板卡住；
- **不要在构造函数里做耗时/网络同步操作**（会卡面板），用异步加载 + “加载中…”占位。

---

## 4. 常用功能示例（可直接复制修改）

### 时钟（定时刷新）
```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShoreHue.UI.Widgets;

public class ClockWidget : UserControl, IWidget
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _time = new() { FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray };

    public ClockWidget()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_time);
        panel.Children.Add(_date);
        Content = panel;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        _time.Text = DateTime.Now.ToString("HH:mm:ss");
        _date.Text = DateTime.Now.ToString("yyyy-MM-dd dddd");
    }

    public string Name => "时钟";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { _timer.Stop(); }
}
```

### 计数器（按钮交互）
```csharp
using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

public class CounterWidget : UserControl, IWidget
{
    private int _count;
    private readonly TextBlock _value = new() { FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center };

    public CounterWidget()
    {
        var up = new Button { Content = "＋", Width = 48, Height = 26 };
        up.Click += (_, _) => { _count++; Refresh(); };
        var down = new Button { Content = "－", Width = 48, Height = 26, Margin = new Thickness(6, 0, 6, 0) };
        down.Click += (_, _) => { _count--; Refresh(); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(up);
        row.Children.Add(down);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_value);
        panel.Children.Add(row);
        Content = panel;
        Refresh();
    }

    private void Refresh() => _value.Text = _count.ToString();
    public string Name => "计数器";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
}
```

### 快捷打开网址（本地自用；市场来源会拦截 Process）
```csharp
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

public class ShortcutWidget : UserControl, IWidget
{
    public ShortcutWidget()
    {
        var btn = new Button { Content = "打开网站", FontSize = 14, Padding = new Thickness(14, 6, 14, 6) };
        btn.Click += (_, _) => Process.Start(new ProcessStartInfo("https://www.bing.com") { UseShellExecute = true });
        Content = new StackPanel { Children = { btn }, VerticalAlignment = VerticalAlignment.Center };
    }
    public string Name => "快捷打开";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
}
```

### 天气（异步联网 + 失败兜底）
```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;

public class WeatherWidget : UserControl, IWidget
{
    private readonly TextBlock _text = new() { FontSize = 16, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.White };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public WeatherWidget()
    {
        Content = _text;
        _text.Text = "加载中…";
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            string city = "北京";
            string geo = await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city) + "&count=1&language=zh");
            var loc = JsonDocument.Parse(geo).RootElement.GetProperty("results")[0];
            double lat = loc.GetProperty("latitude").GetDouble();
            double lon = loc.GetProperty("longitude").GetDouble();
            string fc = await Http.GetStringAsync($"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,weather_code&timezone=auto");
            double temp = JsonDocument.Parse(fc).RootElement.GetProperty("current").GetProperty("temperature_2m").GetDouble();
            _text.Text = $"{city} · {temp:F0}°C";
        }
        catch { _text.Text = "获取失败"; }
    }
    public string Name => "天气";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
}
```

---

## 5. 沙箱限制（仅市场来源；本地自用不限制）

**TrustedSource=false**（在线市场下载 / 导入 .shpkg / 手动放入且 manifest 未声明可信）的代码编译前会做三层沙箱检查
（文本级 + Roslyn 符号级 + 数据流级，见 [SECURITY.md](./SECURITY.md)），**硬拦截**以下能力（命中即拒绝编译）：

### 禁止的命名空间（整包拦截）
- `System.Diagnostics`（Process）
- `System.Reflection`（反射）
- `System.Runtime.InteropServices`（DllImport/Marshal）
- `System.Management`（WMI）
- `Microsoft.Win32`（注册表）
- `System.DirectoryServices`

### 禁止的 API（词边界/子串匹配，命中即拦截）
- **进程**：`Process`、`ProcessStartInfo`
- **反射/动态**：`GetMethod`、`GetProperty`、`Activator.`、`DynamicMethod`、`Assembly.Load`、`Type.GetType`
- **原生调用**：`DllImport`、`Marshal.`
- **注册表/WMI**：`registry`、`ManagementObject`
- **窗口/输入**：`FindWindow`、`EnumWindows`、`SetWindowsHookEx`、`SendInput`、`SetForegroundWindow`、`PostMessage`、`SendMessage`、`keybd_event`、`mouse_event`
- **屏幕/窗口捕获**：`CopyFromScreen`、`PrintWindow`、`BitBlt`
- **文件写/删/移**：`File.Write*`、`File.Append*`、`File.Delete`、`File.Move`、`File.Copy`、`File.SetAttributes`、`FileStream`、`StreamWriter`、`Directory.Create`、`Directory.Delete`
- **剪贴板**：`Clipboard`、`IDataObject`

> 网络（HttpClient）与文件**读**（File.ReadAllText 等）属于“权限声明类”——导入/安装时会做风险标签提示（联网/文件），不硬拦。静态扫描有理论绕过空间（混淆+反射），故同时硬拦反射/动态加载把门槛抬高。

---

## 6. 面板变体（Kind=Panel，分配到任意区域）

- 海床内置**面板功能模板**：通知坞、最近使用、快捷设置（完整纯代码模板）；任务栏、AI 助手、窗口控制（薄封装模板，复用内置视图）；
- 保存为面板变体后，到 设置 → 区域 → 面板类型 下拉选择该面板，即可替换默认内容；
- 面板源码同样实现 `IWidget`，但尺寸/行为由面板框架管理（内容自适应尺寸，无需自己管窗口）。

---

## 7. 在线市场发布（可选）

- 预设/功能包随主仓库 `market/` 目录发布，客户端通过 jsDelivr CDN 拉取，安装走**沙箱 + 权限确认**；
- 结构：`market/packages/<包ID>/manifest.json + main.cs`，并在 `market/index.json` 登记；
- CI 会自动编译验证每个包（`tools/MarketValidator`），编译失败/触发沙箱的包会挂掉 PR；
- 完整贡献流程见 [market/README.md](../market/README.md)。

---

## 8. 给 AI 的生成提示（完整模板，可直接整段复制）

> 把下面整段（连同你的需求）发给任意 AI（DeepSeek/Claude/ChatGPT 等），要求它输出完整可编译的 C# 代码，粘回海床编程框编译即可。

```text
你是ShoreHue 海岸线 的 WPF 小组件/面板开发专家。请严格按下面的规范，为我的需求生成完整可编译的 C# 源码。

【目标】生成一个ShoreHue小组件（或面板）的完整 C# 类源码，粘回海床编程框后可直接编译运行。

【需求描述】
（在这里用自然语言描述你想要的小组件：显示什么、有哪些交互、什么风格。例如：“一个每秒更新的时钟，显示大号时间和日期，深色背景适配面板”；“一个按钮，点击用系统浏览器打开 https://github.com”；“一个待办清单，能添加和勾选删除”。）

【必须遵守的接口契约】
1. 类必须是公开类（public class），命名空间随意但避免与内置冲突；
2. 必须继承 System.Windows.Controls.UserControl 并实现 ShoreHue.UI.Widgets.IWidget；
3. 必须实现四个成员：
   - string Name（面板标签显示名，用中文）；
   - UserControl CreateView() → 固定返回 this；
   - void OnActivated()（面板激活时回调）；
   - void OnDeactivated()（面板切走时回调，在这里停止定时器等资源）；
4. 全部用 C# 代码构建界面（new StackPanel/Grid/TextBlock/Button/TextBox...），不要使用 XAML；
5. 构造函数里把根控件赋给 Content。

【界面风格要求】
1. 面板背景是深色（近似 #1E1E1E / 半透明白），文字用白色或浅灰（#FFFFFF / #EEEEEE / #BBBBBB），保持与系统一致；
2. 布局简洁、留白合理，适合窄条/卡片式面板（一般宽 300-500，高 200-400，内容自适应）；
3. 按钮建议：高度 26-28、字号 12、圆角 6；
4. 若需要统一风格控件，可用（通过 FindResource 或 StaticResource 获取）：AccentButton（主题色主按钮）、FlatButton（透明工具按钮）、IconButton（方形小图标按钮）、CardStyle（Border 卡片）、DarkTextBox（深色输入框）；
5. 需要图标可用项目内置线性图标：FindResource("IconLogo") 等（AppIcons.xaml 定义）。

【线程与可靠性要求】
1. 定时器必须用 System.Windows.Threading.DispatcherTimer（自动回 UI 线程），禁止用 System.Threading.Timer；
2. UI 更新必须在 UI 线程：async/await 后如需要更新界面，先 await Dispatcher.InvokeAsync(...) 或用 Dispatcher.BeginInvoke；
3. 需要联网（HttpClient）时：设 Timeout（如 8 秒），所有异常要 catch 并显示友好提示（如“获取失败”），禁止崩溃；
4. 不要在构造函数里做同步的耗时/网络操作——用异步 + “加载中…”占位；
5. 剪贴板操作（Clipboard.SetText）在 UI 线程直接调用即可。

【沙箱注意（如果代码将发布到市场，需避免以下能力；本地自用可忽略但建议养成习惯）】
禁止使用：System.Diagnostics（Process）、System.Reflection（反射/GetMethod/Activator/Assembly.Load/Type.GetType）、System.Runtime.InteropServices（DllImport/Marshal）、System.Management（WMI）、Microsoft.Win32（注册表）、System.DirectoryServices；以及 FindWindow/EnumWindows/SetWindowsHookEx/SendInput/SetForegroundWindow/PostMessage/SendMessage/keybd_event/mouse_event/CopyFromScreen/PrintWindow/BitBlt/File.Write*/File.Delete/FileStream/StreamWriter/Directory.Create/Clipboard/IDataObject 等；
如需“打开网址”请用 System.Diagnostics.Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })，并注明该代码仅限本地自用（市场来源会拦截 Process）。

【输出格式要求】
1. 只输出一个完整 C# 源码文件（所有 using 齐全），不要 Markdown 代码块标记（不要三个反引号），不要任何解释文字；
2. 代码必须能直接编译：类名、继承、接口实现、using 都完整；
3. 不要写“// 这里填…”之类的占位注释；变量名清晰，中文注释可有；
4. 若需要用户数据/设置，请说明你假设的数据来源（如字段默认值）。

【自检清单（输出前逐项核对）】
1. 类是否 public 且 : UserControl, IWidget？
2. Name/CreateView/OnActivated/OnDeactivated 是否都实现了？CreateView 是否返回 this？
3. 是否全部用代码构建 UI 且赋值给 Content？
4. using 是否完整（System、System.Windows、System.Windows.Controls、System.Windows.Threading、ShoreHue.UI.Widgets，用到什么补什么）？
5. 定时器是否用 DispatcherTimer？OnDeactivated 是否 Stop？
6. 网络/异步是否有 Timeout、catch 和 UI 提示？
7. 是否误用了沙箱禁止的 API（如果面向市场）？
8. 代码是否无占位符、可直接编译？

【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置（文件/行/列）和原因，并给出修正后的完整代码（仍遵守以上全部规范）。
```

---

## 9. 其他

- 本文档会持续补充常见功能示例（欢迎贡献）；
- 预设/配置代码（非小组件）的 AI 提示词由海床「复制 AI 提示词」按节点动态生成。
