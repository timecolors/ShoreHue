using ShoreHue.Core.Models;
using System.Linq;
using System.Text;

namespace ShoreHue.UI.Settings.Pages
{
    /// <summary>
    /// AI 提示词生成器：按配置树节点动态生成完整提示词（含可定制字段清单、当前配置、
    /// 格式要求与报错处理说明），用户复制后粘贴到任意 AI（如 DeepSeek 网页版）即可生成配置。
    /// 节点种类（ConfigNode.Kind）分发：
    ///   - StatusProvider → IStatusProvider 完整 .cs 模板提示词（状态栏插件）
    ///   - Animation     → IAnimation 完整 .cs 模板提示词（自定义动画）
    ///   - 其余           → 现有逻辑（配置代码 / 自定义面板源码）
    /// </summary>
    public static class PromptGenerator
    {
        /// <summary>编程模式：Simple=简单编程（纯 C#，默认）；Xaml=完全编程（.xaml + .xaml.cs）。</summary>
        public enum ProgrammingMode { Simple, Xaml }

        public static string Generate(ConfigNode node, string currentJson, ProgrammingMode mode = ProgrammingMode.Simple)
        {
            // 自定义面板：生成源码（动态编译），而非 JSON 配置
            if (!string.IsNullOrEmpty(node.CustomId))
            {
                // ★ 按节点种类分发：状态栏/动画插件走接口模板（仅简单编程）；小组件/面板按模式分发
                switch (node.Kind)
                {
                    case "StatusProvider": return GenerateStatusProviderSource(node.Name, currentJson);
                    case "Animation": return GenerateAnimationSource(node.Name, currentJson);
                    default: return mode == ProgrammingMode.Xaml
                        ? GeneratePanelXamlSource(node.Name, currentJson)
                        : GeneratePanelSource(node.Name, currentJson);
                }
            }
            return GenerateConfigJson(node, currentJson);
        }

        /// <summary>状态栏显示项：生成实现 IStatusProvider 的完整 .cs 源码提示词
        /// （放入 <c>面板\状态栏\&lt;id&gt;\</c> 生效 —— 扁平化后状态栏是 面板/ 下的一个分组目录）。</summary>
        private static string GenerateStatusProviderSource(string providerName, string currentSource)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【角色】你是 ShoreHue 海岸线 的状态栏显示项开发专家。请严格按下面的规范，为状态栏显示项「" + providerName + "」生成完整可编译的 C# 源码（实现 IStatusProvider，放入 seabed\\面板\\状态栏\\<id>\\ 后由 watcher 自动编译挂载到状态栏）。");
            sb.AppendLine();
            sb.AppendLine("【必须遵守的接口契约】");
            sb.AppendLine("1. 类必须公开（public class），命名空间随意但避免与内置冲突；");
            sb.AppendLine("2. 必须实现 ShoreHue.UI.Status.IStatusProvider 接口；");
            sb.AppendLine("3. 必须实现六个成员：string Name（显示名，中文）；string IconText（图标，文本符号或文字，如 ☀）；string GetText()（当前文本，每秒被调用一次，UI 线程，禁止耗时操作）；void OnActivated()（挂载时调用，可订阅资源/启动 DispatcherTimer）；void OnDeactivated()（卸载时调用，必须停止定时器、释放资源）；bool IsEnabled(ShoreHue.Core.Services.Configuration.ISettingsService settings)（是否显示，默认返回 true 即可）；");
            sb.AppendLine("4. 不要创建任何 UI 控件——状态栏项由系统自动生成「图标+文本」布局，插件只提供数据和生命周期。");
            sb.AppendLine();
            sb.AppendLine("【可靠性要求】");
            sb.AppendLine("1. 定时器必须用 System.Windows.Threading.DispatcherTimer（自动回 UI 线程），禁止 System.Threading.Timer；");
            sb.AppendLine("2. GetText() 必须在几毫秒内返回——传感器/WMI/网络查询等耗时操作放后台线程并缓存结果，禁止每次调用实时阻塞；");
            sb.AppendLine("3. 所有异常 catch，返回友好降级文本（如 \"--\"），禁止崩溃；");
            sb.AppendLine("4. 不要引用被沙箱拦截的危险 API（见下）。");
            sb.AppendLine();
            sb.AppendLine("【沙箱注意（若将发布到市场需避免；本地自用可忽略）】");
            sb.AppendLine("禁止：System.Diagnostics（Process）、System.Reflection（GetMethod/Activator/Assembly.Load/Type.GetType）、System.Runtime.InteropServices（DllImport/Marshal）、System.Management（WMI）、Microsoft.Win32（注册表）；以及 FindWindow/EnumWindows/SetWindowsHookEx/SendInput/SetForegroundWindow/PostMessage/SendMessage/keybd_event/mouse_event/CopyFromScreen/PrintWindow/BitBlt/File.Write*/File.Delete/FileStream/StreamWriter/Directory.Create 等。");
            sb.AppendLine();
            sb.AppendLine("【当前源码】现有源码如下（可整体替换或基于它修改）：");
            sb.AppendLine(currentSource);
            sb.AppendLine();
            sb.AppendLine("【输出格式要求】");
            sb.AppendLine("1. 只输出一个完整 C# 源码文件（所有 using 齐全），不要 Markdown 代码块标记（三个反引号不要），不要任何解释文字；");
            sb.AppendLine("2. 代码必须能直接编译：类名、继承、接口、using 完整；");
            sb.AppendLine("3. 不要写占位注释；变量名清晰。");
            sb.AppendLine();
            sb.AppendLine("【自检清单（输出前逐项核对）】");
            sb.AppendLine("1. 类是否 public 且实现 IStatusProvider？2. Name/IconText/GetText/OnActivated/OnDeactivated/IsEnabled 是否都实现？3. 定时器是否 DispatcherTimer、OnDeactivated 是否 Stop？4. GetText 是否轻量（秒级调用不卡 UI）？5. using 是否完整？6. 是否误用沙箱禁止 API（面向市场时）？7. 是否无占位符、可直接编译？");
            sb.AppendLine();
            sb.AppendLine("【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置（文件/行/列）和原因，并给出修正后的完整代码（仍遵守以上全部规范）。");
            sb.AppendLine();
            sb.AppendLine("【用户需求】");
            sb.AppendLine("（在这里描述你想在状态栏显示什么：数据来源、刷新频率、显示格式）");
            return sb.ToString();
        }

        /// <summary>自定义动画：生成实现 IAnimation 的完整 .cs 源码提示词（放入 动画/ 文件夹后在设置页可选）。</summary>
        private static string GenerateAnimationSource(string animName, string currentSource)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【角色】你是 ShoreHue 海岸线 的面板动画开发专家。请严格按下面的规范，为动画「" + animName + "」生成完整可编译的 C# 源码（实现 IAnimation，放入 动画/ 文件夹后自动出现在设置页动画类型下拉，可被选为呼出/隐藏动画）。");
            sb.AppendLine();
            sb.AppendLine("【必须遵守的接口契约】");
            sb.AppendLine("1. 类必须公开（public class），命名空间随意但避免与内置冲突；");
            sb.AppendLine("2. 必须实现 ShoreHue.Animation.IAnimation 接口；");
            sb.AppendLine("3. 必须实现四个成员：string Name（显示名，中文，设置页下拉展示）；string Id（唯一标识，英文/数字/下划线，设置里存这个值）；void AnimateShow(System.Windows.FrameworkElement panel, System.Windows.Window window, double ms, System.Action onCompleted)（呼出动画，完成后调 onCompleted）；void AnimateHide(System.Windows.FrameworkElement panel, System.Windows.Window window, double ms, System.Action onCompleted)（隐藏动画，把 panel 透明度降到 0，完成后调 onCompleted）；");
            sb.AppendLine();
            sb.AppendLine("【动画实现要点（★ 渲染帧热路径，必须遵守）】");
            sb.AppendLine("1. 用 System.Windows.Threading.DispatcherTimer 逐帧驱动（每帧约 16ms，Tick 里推进进度并更新 panel/window 属性）；");
            sb.AppendLine("2. ★ 绝对禁止直接订阅 CompositionTarget.Rendering 而不取消——会导致无限自激循环 100% CPU 卡死（本项目历史教训）；如需渲染帧请务必在 onCompleted/动画结束时取消订阅；");
            sb.AppendLine("3. 位置动画改 window.Left/Top（窗口位置），透明度动画改 panel.Opacity；");
            sb.AppendLine("4. 时长 ms 为毫秒（double），进度 = elapsed/ms，到 1 时必须调用 onCompleted（只调一次）并停掉定时器；");
            sb.AppendLine("5. 所有异常 catch 并仍调用 onCompleted（系统还有超时兜底，但正常完成不依赖它）；");
            sb.AppendLine("6. 不要引用被沙箱拦截的危险 API（见下）。");
            sb.AppendLine();
            sb.AppendLine("【沙箱注意（若将发布到市场需避免；本地自用可忽略）】");
            sb.AppendLine("禁止：System.Diagnostics（Process）、System.Reflection（GetMethod/Activator/Assembly.Load/Type.GetType）、System.Runtime.InteropServices（DllImport/Marshal）、System.Management（WMI）、Microsoft.Win32（注册表）；以及 FindWindow/EnumWindows/SetWindowsHookEx/SendInput/SetForegroundWindow/PostMessage/SendMessage/keybd_event/mouse_event/CopyFromScreen/PrintWindow/BitBlt/File.Write*/File.Delete/FileStream/StreamWriter/Directory.Create 等。");
            sb.AppendLine();
            sb.AppendLine("【当前源码】现有源码如下（可整体替换或基于它修改）：");
            sb.AppendLine(currentSource);
            sb.AppendLine();
            sb.AppendLine("【输出格式要求】");
            sb.AppendLine("1. 只输出一个完整 C# 源码文件（所有 using 齐全），不要 Markdown 代码块标记（三个反引号不要），不要任何解释文字；");
            sb.AppendLine("2. 代码必须能直接编译：类名、继承、接口、using 完整；");
            sb.AppendLine("3. 不要写占位注释；变量名清晰。");
            sb.AppendLine();
            sb.AppendLine("【自检清单（输出前逐项核对）】");
            sb.AppendLine("1. 类是否 public 且实现 IAnimation？2. Name/Id/AnimateShow/AnimateHide 是否都实现？3. 是否 DispatcherTimer 驱动、动画结束停定时器并调 onCompleted（且只调一次）？4. 是否避免 CompositionTarget.Rendering 常驻订阅？5. 隐藏动画是否把 panel.Opacity 降到 0？6. using 是否完整？7. 是否误用沙箱禁止 API（面向市场时）？8. 是否无占位符、可直接编译？");
            sb.AppendLine();
            sb.AppendLine("【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置（文件/行/列）和原因，并给出修正后的完整代码（仍遵守以上全部规范）。");
            sb.AppendLine();
            sb.AppendLine("【用户需求】");
            sb.AppendLine("（在这里描述你想要的面板动画效果：如弹跳/抖动/翻转，呼出与隐藏各自的形态）");
            return sb.ToString();
        }

        /// <summary>标准节点：生成 C# 配置代码提示词（编译校验，保存小预设时在同级创建新项）。</summary>
        private static string GenerateConfigJson(ConfigNode node, string currentJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【角色】你是 ShoreHue 海岸线 的「" + node.Name + "」配置代码专家。");
            sb.AppendLine("【目标】根据下面的需求，生成一段 C# 配置代码（赋值语句，编译校验后保存）。");
            sb.AppendLine();
            sb.AppendLine("【可编辑字段】只允许出现以下字段（赋值给 data.字段）：");
            foreach (var f in node.FieldNames)
            {
                sb.AppendLine("  - " + f);
            }
            sb.AppendLine();
            sb.AppendLine("【代码模板】输出如下结构的完整代码（Apply 方法内每行一个赋值）：");
            sb.AppendLine("public static class ConfigCode");
            sb.AppendLine("{");
            sb.AppendLine("    public static void Apply(ShoreHue.Core.Services.Configuration.SettingsData data)");
            sb.AppendLine("    {");
            sb.AppendLine("        // 每行一个赋值，例如：");
            sb.AppendLine("        // data.ShowAnimationType = \"Slide\";");
            sb.AppendLine("        // data.ShowAnimationDurationMs = 150;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("【当前配置】当前值如下（JSON），请基于它修改：");
            sb.AppendLine(currentJson);
            sb.AppendLine();
            sb.AppendLine("【输出要求】");
            sb.AppendLine("1. 只输出完整 C# 代码（含 public static class ConfigCode 与 Apply 方法），不要 Markdown 代码块标记；");
            sb.AppendLine("2. 字段名必须与【可编辑字段】一致，属性名完全匹配 SettingsData；");
            sb.AppendLine("3. 类型必须正确（布尔 true/false、数字不带引号、字符串带双引号）；");
            sb.AppendLine("4. 未提到的字段不要出现在代码里。");
            sb.AppendLine();
            sb.AppendLine("【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置并给出修正后的完整代码。");
            sb.AppendLine();
            sb.AppendLine("【用户需求】");
            sb.AppendLine("（在这里描述你想要的效果）");
            return sb.ToString();
        }

        /// <summary>自定义面板：生成 C# 面板源码提示词（动态编译为真实可运行面板，详细规范见 docs/WIDGET-SPEC.md）。</summary>
        private static string GeneratePanelSource(string panelName, string currentSource)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【角色】你是 ShoreHue 海岸线 的 WPF 小组件/面板开发专家。请严格按下面的规范，为自定义面板「" + panelName + "」生成完整可编译的 C# 源码（动态编译为真实可运行的面板）。");
            sb.AppendLine();
            sb.AppendLine("【必须遵守的接口契约】");
            sb.AppendLine("1. 类必须公开（public class），命名空间随意但避免与内置冲突；");
            sb.AppendLine("2. 必须继承 System.Windows.Controls.UserControl 并实现 ShoreHue.UI.Widgets.IWidget 接口；");
            sb.AppendLine("3. 必须实现四个成员：string Name（面板显示名，中文）；UserControl CreateView()（固定返回 this）；void OnActivated()（激活回调）；void OnDeactivated()（切走回调，这里停止定时器等资源）；");
            sb.AppendLine("4. 全部用 C# 代码构建界面（new StackPanel/Grid/TextBlock/Button/TextBox/ListBox/ScrollViewer...），不要使用 XAML；");
            sb.AppendLine("5. 构造函数里把根控件赋给 Content。");
            sb.AppendLine();
            sb.AppendLine("【界面风格要求】");
            sb.AppendLine("1. 面板背景深色（近似 #1E1E1E / 半透明白），文字白色或浅灰（#FFFFFF / #EEEEEE / #BBBBBB），与系统一致；");
            sb.AppendLine("2. 布局简洁留白合理，适合卡片式面板（宽 300-500、高 200-400，内容自适应）；");
            sb.AppendLine("3. 按钮高 26-28、字号 12、圆角 6；可用统一样式：FindResource(\"AccentButton\") 主按钮、FlatButton 工具按钮、CardStyle 卡片、DarkTextBox 深色输入框；");
            sb.AppendLine("4. 需要图标用内置线性图标：FindResource(\"IconLogo\") 等。");
            sb.AppendLine();
            sb.AppendLine("【线程与可靠性要求】");
            sb.AppendLine("1. 定时器必须用 System.Windows.Threading.DispatcherTimer（自动回 UI 线程），禁止 System.Threading.Timer；");
            sb.AppendLine("2. UI 更新必须在 UI 线程：async/await 后更新界面先 await Dispatcher.InvokeAsync(...)；");
            sb.AppendLine("3. 联网（HttpClient）设 Timeout（如 8 秒），所有异常 catch 并显示友好提示，禁止崩溃；");
            sb.AppendLine("4. 不要在构造函数里做同步耗时/网络操作——用异步 + 加载中占位；");
            sb.AppendLine("5. 剪贴板操作在 UI 线程直接调用。");
            sb.AppendLine();
            sb.AppendLine("【沙箱注意（若将发布到市场需避免；本地自用可忽略）】");
            sb.AppendLine("禁止：System.Diagnostics（Process）、System.Reflection（GetMethod/Activator/Assembly.Load/Type.GetType）、System.Runtime.InteropServices（DllImport/Marshal）、System.Management（WMI）、Microsoft.Win32（注册表）、System.DirectoryServices；以及 FindWindow/EnumWindows/SetWindowsHookEx/SendInput/SetForegroundWindow/PostMessage/SendMessage/keybd_event/mouse_event/CopyFromScreen/PrintWindow/BitBlt/File.Write*/File.Delete/FileStream/StreamWriter/Directory.Create/Clipboard/IDataObject 等；如需打开网址用 Process.Start(new ProcessStartInfo(url){UseShellExecute=true}) 并注明仅本地自用。");
            sb.AppendLine();
            sb.AppendLine("【当前源码】现有源码如下（可整体替换或基于它修改）：");
            sb.AppendLine(currentSource);
            sb.AppendLine();
            sb.AppendLine("【输出格式要求】");
            sb.AppendLine("1. 只输出一个完整 C# 源码文件（所有 using 齐全），不要 Markdown 代码块标记（三个反引号不要），不要任何解释文字；");
            sb.AppendLine("2. 代码必须能直接编译：类名、继承、接口、using 完整；");
            sb.AppendLine("3. 不要写占位注释；变量名清晰；");
            sb.AppendLine("4. 若需要用户数据/设置，说明假设的数据来源（如字段默认值）。");
            sb.AppendLine();
            sb.AppendLine("【自检清单（输出前逐项核对）】");
            sb.AppendLine("1. 类是否 public 且 : UserControl, IWidget？2. Name/CreateView/OnActivated/OnDeactivated 是否都实现、CreateView 是否返回 this？3. 是否全代码构建 UI 并赋给 Content？4. using 是否完整？5. 定时器是否 DispatcherTimer、OnDeactivated 是否 Stop？6. 网络/异步是否有 Timeout、catch、UI 提示？7. 是否误用沙箱禁止 API（面向市场时）？8. 是否无占位符、可直接编译？");
            sb.AppendLine();
            sb.AppendLine("【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置（文件/行/列）和原因，并给出修正后的完整代码（仍遵守以上全部规范）。");
            sb.AppendLine();
            sb.AppendLine("【用户需求】");
            sb.AppendLine("（在这里描述你想要的面板效果：显示什么、有哪些交互）");
            return sb.ToString();
        }

        /// <summary>完全编程：生成 XAML + 代码后置（.xaml + .xaml.cs，放入海床文件夹即可动态编译运行）。</summary>
        private static string GeneratePanelXamlSource(string panelName, string currentSource)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【角色】你是 ShoreHue 海岸线 的 WPF 小组件/面板开发专家。请严格按下面的规范，为「" + panelName + "」生成【完全编程】形态的 XAML + 代码后置（两个文件：.xaml 和 .xaml.cs），放入海床文件夹后由系统自动编译挂载为真实可运行的面板。");
            sb.AppendLine();
            sb.AppendLine("【完全编程说明】完全编程 = XAML 声明界面 + C# 代码后置（事件/逻辑）。与【简单编程】（纯 C# 构建 UI）二选一，本模式产出两个文件：");
            sb.AppendLine("  - <名字>.xaml       ：界面声明（布局、样式、事件挂接）");
            sb.AppendLine("  - <名字>.xaml.cs    ：代码后置（partial class + 事件处理器 + 逻辑）");
            sb.AppendLine();
            sb.AppendLine("【必须遵守的接口契约】");
            sb.AppendLine("1. .xaml 根元素必须是 <UserControl>，且 xmlns 声明完整（见下）；");
            sb.AppendLine("2. .xaml.cs 必须是 public partial class <名字> : System.Windows.Controls.UserControl, ShoreHue.UI.Widgets.IWidget；");
            sb.AppendLine("3. .xaml.cs 必须实现四个成员：string Name（面板显示名，中文）；UserControl CreateView()（固定返回 this）；void OnActivated()（激活回调）；void OnDeactivated()（切走回调，这里停止定时器等资源）；");
            sb.AppendLine("4. 构造函数必须调用 InitializeComponent()（由系统生成的加载方法，不要自己写 XamlReader）；");
            sb.AppendLine("5. 事件处理器签名：private void Xxx_Click(object sender, RoutedEventArgs e)。");
            sb.AppendLine();
            sb.AppendLine("【.xaml 文件模板】（复制此结构，替换内容）");
            sb.AppendLine("<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
            sb.AppendLine("             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
            sb.AppendLine("             xmlns:loc=\"clr-namespace:ShoreHue.UI.Localization;assembly=ShoreHue\">");
            sb.AppendLine("    <StackPanel Margin=\"2\">");
            sb.AppendLine("        <!-- 在这里写界面：TextBlock/Button/TextBox/ListBox/ScrollViewer... -->");
            sb.AppendLine("    </StackPanel>");
            sb.AppendLine("</UserControl>");
            sb.AppendLine();
            sb.AppendLine("【界面风格要求】");
            sb.AppendLine("1. 背景深色（#1E1E1E 或半透明白），文字白色/浅灰（#FFFFFF/#EEEEEE/#BBBBBB），与系统一致；");
            sb.AppendLine("2. 按钮样式用 {StaticResource AccentButton}（主按钮）/ {StaticResource FlatButton}（工具按钮）；卡片用 CardStyle；");
            sb.AppendLine("3. 中英文案用 {Binding Item[<键名>], Source={x:Static loc:LocalizationManager.Instance}}（键名可自造，运行时缺省显示键名）——不要写死中文到 XAML，除非是临时演示；");
            sb.AppendLine("4. 需要图标用内置线性图标：{StaticResource IconLogo} 等。");
            sb.AppendLine();
            sb.AppendLine("【.xaml.cs 文件模板】（复制此结构，替换类名与逻辑）");
            sb.AppendLine("using System.Windows;");
            sb.AppendLine("using System.Windows.Controls;");
            sb.AppendLine("using ShoreHue.UI.Widgets;");
            sb.AppendLine("using ShoreHue.UI.Localization;");
            sb.AppendLine("");
            sb.AppendLine("public partial class <名字> : UserControl, IWidget");
            sb.AppendLine("{");
            sb.AppendLine("    public <名字>() { InitializeComponent(); }");
            sb.AppendLine("    public string Name => \"<中文名>\";");
            sb.AppendLine("    public UserControl CreateView() => this;");
            sb.AppendLine("    public void OnActivated() { }");
            sb.AppendLine("    public void OnDeactivated() { }");
            sb.AppendLine("    // 事件处理器：private void Xxx_Click(object sender, RoutedEventArgs e) { ... }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("【线程与可靠性要求】");
            sb.AppendLine("1. 定时器必须用 System.Windows.Threading.DispatcherTimer（自动回 UI 线程），禁止 System.Threading.Timer；");
            sb.AppendLine("2. UI 更新必须在 UI 线程：async/await 后更新界面先 await Dispatcher.InvokeAsync(...)；");
            sb.AppendLine("3. 联网（HttpClient）设 Timeout（如 8 秒），所有异常 catch 并显示友好提示，禁止崩溃；");
            sb.AppendLine("4. 不要在构造函数里做同步耗时/网络操作——用异步 + 加载中占位；");
            sb.AppendLine("5. 剪贴板操作在 UI 线程直接调用。");
            sb.AppendLine();
            sb.AppendLine("【沙箱注意（若将发布到市场需避免；本地自用可忽略）】");
            sb.AppendLine("禁止：System.Diagnostics（Process）、System.Reflection（GetMethod/Activator/Assembly.Load/Type.GetType）、System.Runtime.InteropServices（DllImport/Marshal）、System.Management（WMI）、Microsoft.Win32（注册表）、System.DirectoryServices；以及 FindWindow/EnumWindows/SetWindowsHookEx/SendInput/SetForegroundWindow/PostMessage/SendMessage/keybd_event/mouse_event/CopyFromScreen/PrintWindow/BitBlt/File.Write*/File.Delete/FileStream/StreamWriter/Directory.Create/Clipboard/IDataObject 等。");
            sb.AppendLine();
            sb.AppendLine("【现有源码】当前节点源码如下（可参考，完全编程将替换为 .xaml + .xaml.cs 两文件）：");
            sb.AppendLine(currentSource);
            sb.AppendLine();
            sb.AppendLine("【输出格式要求】");
            sb.AppendLine("1. 输出两个文件，用以下标记分隔：");
            sb.AppendLine("   --- FILE: <名字>.xaml ---");
            sb.AppendLine("   （.xaml 内容）");
            sb.AppendLine("   --- FILE: <名字>.xaml.cs ---");
            sb.AppendLine("   （.xaml.cs 内容）");
            sb.AppendLine("2. 不要 Markdown 代码块标记（三个反引号不要），不要解释文字；");
            sb.AppendLine("3. 代码必须能直接编译：using 完整、partial class、事件处理器齐全；");
            sb.AppendLine("4. 不要写占位注释；变量名清晰。");
            sb.AppendLine();
            sb.AppendLine("【自检清单（输出前逐项核对）】");
            sb.AppendLine("1. .xaml 根是 UserControl、xmlns 声明完整（含 loc）？2. .xaml.cs 是 public partial class : UserControl, IWidget？3. Name/CreateView/OnActivated/OnDeactivated 都实现、CreateView 返回 this？4. 构造函数调 InitializeComponent？5. 事件处理器与 .xaml 里 Click= 对应？6. using 完整？7. 定时器 DispatcherTimer、OnDeactivated Stop？8. 网络/异步有 Timeout、catch、UI 提示？9. 是否误用沙箱禁止 API（面向市场时）？10. 无占位符、可直接编译？");
            sb.AppendLine();
            sb.AppendLine("【报错处理】如果用户把编译报错信息粘贴回来，请指出错误位置（文件/行/列）和原因，并给出修正后的完整两文件（仍遵守以上全部规范）。");
            sb.AppendLine();
            sb.AppendLine("【用户需求】");
            sb.AppendLine("（在这里描述你想要的面板效果：显示什么、有哪些交互）");
            return sb.ToString();
        }
    }
}