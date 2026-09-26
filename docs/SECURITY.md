# ShoreHue 海岸线 — 安全说明 / Security

> 本文档如实说明 ShoreHue 的安全模型、已实现的防线、**明确不防**的部分与已知边界。
> 目标：任何人（用户 / 研究者 / 评审）都能拿这份文档**逐条对照源码**核实，而不是靠信任。
> 最后更新：2026-09（沙箱 v2）

---

## 〇、一句话结论

ShoreHue 允许用户**在海床里写 C#/XAML 并运行时编译执行**，也允许**从在线市场安装他人写的功能包**。
我们**不假装进程内沙箱是安全边界**：它是**纵深防御**——用来把"别人写的代码"的能力压到最小、把"你已经信任的代码"标清楚。
真正的边界（独立低权限进程）在路线图上，见第六节。

---

## 一、威胁模型：防什么 / 不防什么

**防（纵深防御）**
- A. 市场/外来功能包**不得**在用户不知情的情况下：起进程、写/删文件、读注册表、反射动态执行、注入键鼠、截屏、监听网络、读出宿主保存的凭证（AI 密钥 / GitHub token）、读浏览器历史与剪贴板历史并外发。
- B. 本机数据（配置、剪贴板、会话、源码）不会在未经用户操作时被外发。

**不防（明确声明）**
1. **用户自己写的代码**：本地海床代码按设计是"用户自己的代码"，信任由用户自己给（见第三节）。
2. **用户主动粘贴给 AI 的内容**、用户主动授予的能力。
3. **资源耗尽**：插件里的死循环 / 巨量内存分配会拖住界面——进程内无法防（见第六节）。
4. **用户本人就是攻击者**：本地代码编辑器无法防住所有者自己。

---

## 二、机制总览（v2）

| 层 | 机制 | 位置 |
|---|---|---|
| 信任层 | 来源判定：**宿主来源标记（`.origin`）/ 宿主写的 `trustedSource:false` / 用户显式"改用沙箱" → 不可信**；其余本地文件 = **受信**（海床初衷：文件夹即真相源，零摩擦） | WidgetPluginStore.ReloadCore |
| 形态层 | XAML 形态**对所有来源开放**，但外来来源必须通过**受限 XAML 方言**校验（结构白名单：只允许 WPF 界面元素/布局/受限标记扩展；ObjectDataProvider / x:Code / 自定义类型命名空间 / MediaElement / Hyperlink 一律拒绝） | WidgetCompiler.CheckXamlDialect |
| 检测层 | 统一闸门 = 文本 + 符号 + 数据流三层，且**检查的必须是真正会被编译/解析的文本** | WidgetCompiler.SandboxErrors(csharp, markup) |
| 能力层 | 外来代码的特权能力只能走窄接口 HostCapabilities（经校验 + 限频 + 限额） | src/UI/Widgets/HostCapabilities.cs |
| 回归层 | 绕过语料库进 CI，规则退化即测试变红 | tests/ShoreHue.Tests/SandboxCorpusTests.cs |

### 检测层三层各管什么
1. **文本层**：把源码**去空白归一**后匹配危险形状（换行/别名骗不过），带 `(?<![a-z0-9_])` 前缀守卫防误伤；含 XAML 专用规则（ObjectDataProvider / x:Code / MethodName= / 危险 clr-namespace）。
2. **符号层（Roslyn 语义）**：看代码**真正引用了哪个类型/成员**，不受书写方式影响。
   - 类型黑名单：进程/反射/Interop/WMI/注册表/反序列化/**Activator**/**Unsafe**/**表达式树**/**MemoryMappedFiles**/**ProtectedData**/**HttpListener**/**XamlReader** 等；
   - **白名单化（fail-closed）**：File / Directory / Environment / Type 只放行只读成员，其余全拦（旧版是黑名单，File.CreateText / File.OpenWrite 可任意写文件）；
   - **宿主特权 API 黑名单**：AiSettingsStore（明文密钥）、UninstallHelper、UpdateService、WindowAction、RecentAppTracker、WebFavoriteManager、SelectedTextCapture、CursorOutputService、WindowCaptureService、SystemToast、SystemLauncher、ServiceManager 等。
3. **数据流层（Roslyn 语义 + 过程内污点）**：判定**真实通路**——"读取类来源（本地文件 / 剪贴板 / 宿主私有数据）的值是否流向网络出口"。
   这层解决的是"单独读文件是权限、单独联网是权限，**读到的内容发出去才是外泄**"，同时避免旧版"看到读 + 看到网就拦"的误报。
4. **判定顺序**：源码能编译才做语义/数据流判定；编译不过的插件根本不可能被执行（正式编译会拒绝），此时把错误交给编译器报告，而不是误报成"疑似绕过"。

### 能力层（HostCapabilities）
外来代码**不直接调用**宿主内部能力，只能通过经过校验的窄接口：

- `OpenExternally(target)`：只放行 http/https、**`ms-settings:` 白名单前缀**，或**宿主"最近使用"清单里出现过的真实路径**（防任意程序执行）。
  ★ 只放行 `ms-settings:`，**不放行整个 `ms-*` 协议族**——族里还有 `ms-appinstaller:` / `search-ms:` / `ms-officecmd:` 这类能拉起外部处理器的入口；
- `ShowToast`：**限频每分钟 3 条**；`AskAiAsync`：单次输入上限 8000 字符、**密钥不出宿主**；`GetRecentItems / GetWebFavorites`：条目数上限；
- `CopyToClipboard(text)` / `CaptureSelectedTextAsync()`：剪贴板写入与划词捕获。
  ★ 这两个入口存在的理由不只是方便：宿主剪贴板接口的写入方法、以及划词捕获类，它们的**类型名在符号层黑名单里**
  —— 插件直接引用会被判违规、整包被拦。窄接口让外来代码用得上能力，又不暴露宿主内部类型。
  （实测教训：连代码**注释**里写出那个类型名都会触发文本层拦截。）
- `RecordLaunch(target)`：**只刷新清单里已有的条目，不新增**（清单同时是上面那条路径白名单——能新增就等于"自己给自己发白名单"）；
- 服务以接口形式暴露（Settings / ClipboardHistory / Notes / Shortcuts），不暴露服务容器。
  ★ 对外暴露的 `ISettingsService` **不含信任读写**：信任的读写只在宿主 internal 接口 `IPluginTrustStore` 上（否则插件能给自己登记信任 → 下次加载跳过沙箱）。

---

## 三、信任与授权

| 来源 | 信任 | 说明 |
|---|---|---|
| 用户在海床里新建/编辑并保存 | **可信** | 应用写 manifest 时显式写 trustedSource: true |
| 在线市场「拾贝」安装 | **不可信（走沙箱）** | 安装时置 TrustedSource=false，并重新检测权限、弹窗确认 |
| 导入 .shpkg（旧 .dbp）包 / 市场解包出来的目录 | **不可信（走沙箱）** | 宿主在安装/解包后写来源标记（`.origin`）或 `trustedSource:false`，**包内容伪造不了"我是本地"** |
| 你或 AI 直接写进 seabed 的源码（目录 / 散文件） | **受信（不设限）** | ★ 海床初衷：文件夹即真相源，本地编程不设限、零摩擦 |
| 包内自述 official: true / author: timecolors | **不再构成信任依据** | 旧版的"免死金牌"已移除 |

**信任如何给**：
- 在海床里**新建/编辑并保存** → 应用自动登记信任（"设置 → 小工具 → 选中该功能 → 受信任"）；
- 手动放入或从市场安装的 → 默认沙箱运行；确认可信时在**同一处点「默认沙箱 · 点此信任」**（弹二次确认）；
- 信任与**内容指纹**绑定：代码一改，哈希不匹配 → 信任自动失效，需要重新确认；
- ★ 预设文件**不能替代码授信**：预设里新引入的面板一律降级为不可信。

---

## 四、为什么"只做静态检测"不够（实测结论）

对**旧的文本级实现**做绕过测试，9 个样本全部得手：

| 绕过 | 手法 |
|---|---|
| 任意写文件 | `TextWriter w = File.CreateText(p); w.Write(...)`（CreateText 不在黑名单） |
| 任意写文件 | `Stream s = File.OpenWrite(p)`（用基类声明，符号层只看到 System.IO.Stream） |
| 偷文件外传 | `File.OpenText(p)`（读关键词表里没有它）→ 直接 HttpClient 发出 |
| 偷密钥 | `AiSettingsStore.Load()` + HttpClient——**每个符号都合法**，静态分析原理上判不出恶意 |
| 卸载删数据 | `UninstallHelper.LaunchUninstall(true)` |
| 完全跳过检查 | 只扫 main.cs，实际执行的是 .xaml.cs（**检查 A、运行 B**） |

结论：**恶意往往来自"能力组合"，不是"引用了被禁 API"**，所以三层缺一不可；而"检查目标与执行目标不一致"这类问题只能靠流程修正。

---

## 五、已知边界（诚实声明）

1. **进程内不是安全边界**：.NET 已废弃 CAS/AppDomain 隔离，托管层静态防线无法证明"任意代码运行期一定不越权"。
2. **资源耗尽**：死循环、内存爆炸、UI 线程阻塞防不住。**缓解**：插件运行时守卫 —— 连续异常/失败的插件自动熔断停用
   （`PluginRuntimeGuard`），并提供 `--safe-mode` 启动（不加载任何插件）与「连续 3 次未正常退出自动安全模式」的恢复通道。
3. **宿主 UI 视图可被包装读取**：外来代码可以实例化宿主的面板视图（如通知坞），理论上能读取其展示内容——这是"UI 层允许"的残余风险。
4. **沙箱是"能力压小"而非"证明安全"**：黑名单永远不完备，这也是我们把重心放到"窄接口 + 默认不可信 + 语料库回归"的原因。
5. **剪贴板/文件读/联网属于"权限类"**：单独允许（安装时确认），**组合外发**由数据流层拦截。
6. **数据流层是"过程内"分析**：把"读"与"发"拆进两个方法（`Read()` / `Send()`）当前测不到 —— 这是检测层最大的一处盲区；
   要真正覆盖需要过程间分析（方法摘要/调用图），代价大，**目前的定位是"抬高出货门槛"而不是"证明安全"**。
7. **插件能改宿主设置**：`HostCapabilities.Settings` 给的是完整 `ISettingsService`（读+写，如区域面板、动画、触发距离）。
   这是"服务以接口形式暴露"的有意取舍，但**信任写已从中移出**（见第三节）；`CustomPanels` 整表可写仍属残余风险。
8. **"最近使用"白名单是弱白名单**：语义是"用户最近打开过 = 可以再打开"。已堵住"插件自行新增条目"这条自我授权路径（见变更记录），
   但它终究不等于"用户此刻批准执行这个程序"。

---

## 六、路线图

- **短期**：市场包与内置模板迁移到 HostCapabilities（进行中）；界面"信任开关"；内容指纹级信任；CI 与客户端共用同一闸门函数。
- **中期（唯一真正的边界）**：外来插件放到**独立低权限子进程**执行，宿主与插件之间只走 IPC，HostCapabilities 直接升级为 IPC 契约；届时可加超时/内存配额。
- **长期**：WASM/WASI 形态的插件沙箱（成熟方案：默认零 I/O、能力靠导入、带 fuel/内存上限）。

---

## 七、报告漏洞

请通过 GitHub Issues（标签 security）描述问题，附：复现步骤、受影响版本、影响面。修复后会在发布说明中致谢（可匿名）。

---

## 附：变更记录

- **沙箱 v2.2（2026-09 第二轮对抗审计）** —— 按"威胁建模 → 攻击面枚举 → 对抗语料"重跑一遍，
  新增 `tests/ShoreHue.Tests/SandboxAuditTests.cs`（13 条：9 条绕过 + 2 条回归 + 2 条良性）。
  **实测确认并修复 9 条数据外泄绕过**（修复前全部得手，修复后全部拦截）：
  1. **网络出口清单偏窄**：`SmtpClient` / `NetworkInformation.Ping` / `WebSockets.ClientWebSocket` 都不是
     HttpClient，数据流层原本判不出"本地数据 → 网络出口"，可原样把文件内容发出去；
  2. **WPF 媒体类也是出口**：`new BitmapImage(new Uri("https://…" + 数据))`、`MediaPlayer.Open(Uri)`、
     `WebBrowser.Navigate` 传 http(s) Uri 就会发起 GET（内部走 WebRequest，符号层只看得到 WPF 类型）；
     因此数据流出口扫描**补上了构造函数**这一路（原先只扫方法调用）；
  3. **宿主能力出口**：`HostCapabilities.OpenExternally(url)` 会把任何 http(s) URL 交给系统浏览器打开、
     `AskAiAsync(prompt)` 会把 prompt 发给用户配置的 AI 服务商 —— 两者都能外带数据；
  4. **集合/foreach 污点传播**：`foreach (var n in HostCapabilities.Notifications) 发送(n.Message)`
     这类写法旧实现不给迭代变量传播污点，通知正文 / 剪贴板历史逐条可外传；
  ★ 为修 (3) 引入**两级污点**：原始网络出口（HttpClient/Socket/…）对任意污点拦截；
    宿主导航出口（OpenExternally / AskAiAsync）只对**敏感数据**（文件内容/剪贴板/环境变量/宿主私有数据）拦截 ——
    否则"最近使用"包把 `GetRecentItems` 的条目交给 `OpenExternally` 打开会被误拦（已实测为误报）。
  另：出口扫描新增**构造函数**检查；`SandboxVersion` v2 → v3（结果缓存失效）。
  ★ 静态审查确认**没有**问题的：`.shpkg` 解包用 `Path.GetFileName` 扁平化（无 zip slip）+ 来源标记写不上就回滚、
  更新 SHA256 默认 fail-closed（缺失即拒绝、不匹配永不放行）、AI Key DPAPI 加密、数据目录 ACL 收口。
  ★ 仍是**已知边界**（本轮未扩大）：过程间污点只到"字段级"，把读与发拆进两个类仍测不到；
  资源耗尽（死循环/巨量内存）进程内防不住。
- **沙箱 v2.1（2026-09 安全复查）** —— 由一次外部代码评审 + 本地复核发现并修复；每项都补了回归测试
  （`tests/ShoreHue.Tests/HostCapabilityBoundaryTests.cs` 结构守卫 + `SandboxCorpusTests` 语料）：
  1. **信任自助授权（严重，已修）**：`HostCapabilities.Settings` 返回的 `ISettingsService` 当时含 `SetPluginTrusted`，
     而插件能自行算出内容哈希（`ComputeHash` 用的 SHA256/Convert/Encoding 都不在黑名单里）→
     可给自己（乃至任意包）登记信任，**下次加载整个沙箱被跳过且落盘持久化**。
     修法：信任读写移入 **internal** 的 `IPluginTrustStore`，`ISettingsService` 不再声明；`SettingsManager` 改**显式接口实现**
     （公开面上不存在）；并把 `SettingsManager` 本身加进宿主特权黑名单（防"自建一个改 config.json"）。
  2. **记录即授权（已修）**：`RecordLaunch` 可把任意路径写进"最近使用"清单，而该清单同时是 `OpenExternally` 的路径白名单 →
     插件可"先记录 cmd.exe、等缓存刷新、再打开"。修法：插件路径 `addIfMissing:false`（只刷新已有条目），宿主内部仍可新增。
  3. **`ms-*` 协议整族放行（已修）**：收窄为 `ms-settings:` 白名单前缀；
     原实现里的 `char.IsWhiteSpace` 检查也挡不住无空格参数（如 `...:/id PCWDiagnostic`）。
  4. **污点传播迭代上限 4（已修）**：改为止于收敛 + 硬上限 50；原上限对循环内重赋值/顺序倒置这类非线性流可能传播不完全
     （直线流本来一趟就够，所以此前未被发现）。
  ★ 同批确认**不是问题**的一条：`SandboxErrors` "编译成功才做语义判定" 是有意且安全的（编译不过的插件根本不会被执行）。
  ★ **评估后决定不动**的一条：信任哈希只取 SHA256 前 16 个 hex（64 位）。要定向撞上**某个已受信插件**的哈希
  需要 2^64 量级的原像搜索，不可行；而改哈希算法会让用户**已建立的信任记录全部失效**（升级后要重新点信任），
  收益不抵代价。若将来把信任令牌用在更敏感的地方，再一并换成完整哈希。
- **沙箱 v2（2026-09）**
  - 闸门改为检查"真正会编译/解析的文本"（修复"检查 main.cs、执行 .xaml.cs"的完全绕过）；
  - 外来来源的 XAML 改为**受限方言校验**（结构白名单，保留"完全编程"这一海床核心能力，砍掉"凭空造对象/按名调方法"）；
  - 信任模型修正：**无来源标记 = 本地受信**（海床初衷），只有宿主装的包走沙箱；包内自述依旧不算数；
  - 文本层去空白归一 + 前缀守卫；补 Activator / Unsafe / 表达式树 / MemoryMappedFiles / ProtectedData / HttpListener 等通道；
  - 符号层 File / Directory / Environment / Type 改白名单（fail-closed），补宿主特权 API 黑名单；
  - 新增数据流（污点）层，替代"读+网"粗筛；
  - 宿主安装/解包的外来包一律不可信（fail-closed）；本地手写文件按"文件夹即真相源"受信（不是"默认不可信"）；
  - 信任库（id + 内容哈希）+ 界面「信任」开关 + 预设防洗白（预设不能给代码授信）；
  - 文本层补宿主特权 API 兜底规则（即使源码编译不过也拦得住）；
  - 语料：仓库内 `SandboxCorpusTests`（35 条）+ `SandboxCorpusMatrixTests`（能力矩阵 62 条 + **真实市场包误报基线**）；
  - 全量单测 **324 项通过**（v1.1.1 起为 428 项）；CI 校验器与客户端同判（无免检豁免，11 个市场包全过）。
- 包扩展名 .dbp（DynamicBird 时代缩写）→ **.shpkg**，旧扩展名仍可导入。
