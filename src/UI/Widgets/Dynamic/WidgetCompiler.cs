using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.UI.Widgets.Dynamic
{
    /// <summary>
    /// C# 插件编译器：用 Roslyn 把用户编写的源码动态编译为内存程序集，
    /// 反射创建 IWidget 实例。用户拥有完整自由度（任意 WPF UI 与逻辑）。
    /// 注意：这是"本地自用"模型——用户编译运行的代码即用户自己的代码；
    /// 未来市场分发含代码插件时需引入沙箱/风险标记。
    /// </summary>
    public static class WidgetCompiler
    {
        // ★ 编译缓存：id → (源码签名, 实例)。源码未变时复用实例，避免重复编译同一程序集名导致
        //   "Assembly with same name is already loaded"（Default ALC 不允许同名程序集二次加载）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, IWidget Widget)> _cache = new();

        // ★ 泛型编译缓存（IStatusProvider / IAnimation 等非 IWidget 插件）：同 _cache 语义，隔离命名空间，
        //   与小组件缓存互不干扰（同一 id 不会同时编译两种接口）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Hash, object Instance)> _genericCache = new();

        /// <summary>源码签名（供 WidgetSwitcher 判断是否需要重建）。</summary>
        public static string SourceHash(string source) => ComputeHash(source ?? "");

        /// <summary>卸载缓存条目（插件删除/海床项删除时调用，释放实例与程序集引用）。</summary>
        public static void Evict(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            _cache.TryRemove(id, out _);
            _genericCache.TryRemove(id, out _);
            // ★ 修复：CompileXaml 的缓存键 "xaml:"+id 存在 _cache（IWidget 缓存）里，
            //   原实现从 _genericCache 删除（删错字典）→ XAML 形态插件删除后缓存残留
            _cache.TryRemove("xaml:" + id, out _);

            // ★ 带前缀的编译 id 也必须清：同一次"删除插件"在各调用点用的是不同的 id
            //   （状态栏 "status_"+id、动画 "anim_"+id/XAML 形态 "xaml:anim_"+id、
            //    自定义面板 "panel_"+id/XAML 形态 "xaml:panel_"+id）。
            //   只删裸 id 会让这些实例与它们的程序集一直留到进程退出 ——
            //   用户"删了插件又装同名插件"时会拿到上一份旧实现。
            foreach (var prefix in PrefixedCompileIdPrefixes)
            {
                _cache.TryRemove(prefix + id, out _);
                _cache.TryRemove("xaml:" + prefix + id, out _);
                _genericCache.TryRemove(prefix + id, out _);
                _genericCache.TryRemove("xaml:" + prefix + id, out _);
            }
        }

        /// <summary>各调用点为同一插件加的编译 id 前缀（见 WidgetPluginStore / PanelContentController）。</summary>
        private static readonly string[] PrefixedCompileIdPrefixes = { "status_", "anim_", "panel_", "builtin_" };


        /// <summary>
        /// 把源码里 IWidget 的 Name 属性替换为指定名字（变体名注入）。
        /// 编译前调用：让动态编译的变体标签显示变体自己的名字（模板里 Name 写死）。
        /// </summary>
        /// <summary>
        /// 把源码里 IWidget 的 Name 属性替换为指定名字（变体名注入）。
        /// 编译前调用：让动态编译的变体标签显示变体自己的名字（模板里 Name 写死）。
        /// </summary>
        /// <summary>
        /// 把源码里 IWidget 的 Name 属性替换为指定名字（变体名注入）。
        /// 编译前调用：让动态编译的变体标签显示变体自己的名字（模板里 Name 写死）。
        /// </summary>
        public static string InjectWidgetName(string source, string name)
        {
            if (string.IsNullOrEmpty(source)) return source;
            int marker = source.IndexOf("Name => ");
            if (marker < 0) return source;
            // 用字符重载找真正的引号（空字符串 IndexOf 总是返回起始位，会错位）
            int quote = source.IndexOf('"', marker);
            if (quote < 0) return source;
            int end = source.IndexOf('"', quote + 1);
            if (end < 0) return source;
            // ★ 名称合法化：转义引号/反斜杠/换行，防止注入破坏源码（变体名来自用户配置）
            string safe = (name ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ");
            return source.Substring(0, quote + 1) + safe + source.Substring(end);
        }

        public static (IWidget? widget, string error) Compile(string id, string source)
        {
            try
            {
                string src = source ?? "";
                string hash = ComputeHash(src);
                if (_cache.TryGetValue(id, out var entry) && entry.Hash == hash)
                    return (entry.Widget, "");

                if (!TryGetAssembly(id, src, out var asm, out string errors) || asm == null)
                {
                    return (null, errors);
                }

                var type = asm.GetTypes()
                    .FirstOrDefault(t => typeof(IWidget).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
                if (type == null)
                {
                    return (null, "未找到 public 且实现 IWidget 的类。请定义一个公开类实现 ShoreHue.UI.Widgets.IWidget 接口。");
                }

                var widget = Activator.CreateInstance(type) as IWidget;
                if (widget == null) return (null, "实例化失败");
                _cache[id] = (hash, widget);
                return (widget, "");
            }
            catch (TargetInvocationException tie)
            {
                return (null, "构造异常：" + (tie.InnerException?.Message ?? tie.Message));
            }
            catch (Exception ex)
            {
                return (null, "编译异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 泛型编译：把源码编译为任意接口（T）的实现（IStatusProvider / IAnimation 等）。
        /// 与 Compile(id, source) 同构：id 作缓存键（建议 status_/anim_ 前缀隔离），
        /// 未变化源码复用实例；找不到实现类时返回错误文本。
        /// </summary>
        public static (T? instance, string error) Compile<T>(string id, string source) where T : class
        {
            try
            {
                string src = source ?? "";
                string hash = ComputeHash(src);
                if (_genericCache.TryGetValue(id, out var entry) && entry.Hash == hash)
                    return (entry.Instance as T, "");

                if (!TryGetAssembly(id, src, out var asm, out string errors) || asm == null)
                {
                    return (null, errors);
                }
                var type = asm.GetTypes()
                    .FirstOrDefault(t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
                if (type == null)
                {
                    return (null, "未找到 public 且实现 " + typeof(T).Name + " 的类。请定义一个公开类实现该接口。");
                }

                var instance = Activator.CreateInstance(type) as T;
                if (instance == null) return (null, "实例化失败");
                _genericCache[id] = (hash, instance);
                return (instance, "");
            }
            catch (TargetInvocationException tie)
            {
                return (null, "构造异常：" + (tie.InnerException?.Message ?? tie.Message));
            }
            catch (Exception ex)
            {
                return (null, "编译异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 完全编程：编译 XAML + 代码后置 为 IWidget。
        /// xamlCs = .xaml.cs 源码（partial class + 事件处理器 + InitializeComponent() 调用）；
        /// xaml = .xaml 源码。由 XamlCodeGenerator 生成补全代码后合并编译。
        /// </summary>
        public static (IWidget? widget, string error) CompileXaml(string id, string xaml, string xamlCs)
        {
            try
            {
                string key = "xaml:" + id;
                string hash = ComputeHash((xaml ?? "") + "\u0001" + (xamlCs ?? ""));
                if (_cache.TryGetValue(key, out var entry) && entry.Hash == hash)
                    return (entry.Widget, "");

                // 1) 生成补全代码
                string? generated = XamlCodeGenerator.Generate(xaml ?? "", xamlCs ?? "");
                if (generated == null) return (null, "XAML/代码后置格式不正确：需 partial class + 有效 XAML");

                // 2) 合并编译（带落盘缓存：命中缓存直接加载上次编好的 DLL，跳过 Roslyn）
                if (!TryGetAssembly("xaml_" + id, generated, out var asm, out string errors) || asm == null)
                    return (null, errors);

                // 3) 实例化
                var type = asm.GetTypes()
                    .FirstOrDefault(t => typeof(IWidget).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
                if (type == null)
                    return (null, "未找到 public 且实现 IWidget 的类（完全编程的 xaml.cs 需实现 IWidget 接口）");

                var widget = Activator.CreateInstance(type) as IWidget;
                if (widget == null) return (null, "实例化失败（检查 xaml.cs 构造函数）");
                _cache[key] = (hash, widget);
                return (widget, "");
            }
            catch (TargetInvocationException tie)
            {
                return (null, "构造异常：" + (tie.InnerException?.Message ?? tie.Message));
            }
            catch (Exception ex)
            {
                return (null, "编译异常：" + ex.Message);
            }
        }

        // ==================== 编译产物缓存（内存 + 落盘）====================
        //
        // ★ 为什么需要：面板内容构建是**同步跑在 UI 线程上**的，而插件编译走 Roslyn。
        //   实测第一次激活小组件面板：整体 1639ms，其中内置件文件编译 1590ms、挂载只有 2ms ——
        //   也就是说"面板要 1.6 秒后才开始显示/起帧"。显卡再好也没用，这是单线程 CPU 在挡路。
        //   分段量过：引用表只占 104ms，大头是 Roslyn 本身的冷启动 + 编译。
        // ★ 做法（成熟做法：编译产物缓存，等同 JIT 的 ngen/ReadyToRun 思路）：
        //   程序集名由 **id + 源码哈希** 决定（不再用随机 GUID）→ 同名即同内容 →
        //   ① 进程内已有同名程序集：直接复用，连加载都省了；
        //   ② 磁盘上有这个哈希的 DLL：直接加载，完全跳过 Roslyn（实测 1.6s → 十几 ms）；
        //   ③ 都没有：现编，编完把 DLL 落盘，下次就快了。

        /// <summary>编译产物目录（按内容哈希存 DLL）。</summary>
        private static string CompileCacheDir => Path.Combine(AppPaths.DataRoot, "compiled");

        /// <summary>最多保留的缓存 DLL 数（超出后按最后写入时间删最旧的）。</summary>
        private const int CompileCacheLimit = 300;

        /// <summary>由 id + 内容哈希确定的程序集名：同名 == 同内容，可安全复用已加载的程序集。</summary>
        private static string AssemblyNameFor(string id, string hash) => "ShoreHue.Widget." + id + "_" + hash;

        /// <summary>取（或现编）插件程序集：内存缓存 → 落盘缓存 → Roslyn 编译。</summary>
        private static bool TryGetAssembly(string id, string source, out Assembly? asm, out string errors)
        {
            errors = "";
            string hash = ComputeHash(source);
            string asmName = AssemblyNameFor(id, hash);

            // ① 同名程序集已经加载过（同 id + 同内容）→ 直接复用：Default ALC 不允许同名二次加载
            try
            {
                var loaded = AssemblyLoadContext.Default.Assemblies
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, asmName, StringComparison.Ordinal));
                if (loaded != null) { asm = loaded; return true; }
            }
            catch (Exception ex)
            {
                // 枚举已加载程序集失败不致命，继续走缓存/现编；但必须留痕（否则会误以为是"缓存没生效"）
                LogManager.Debug($"[编译] 枚举已加载程序集失败（继续走缓存/现编）：{ex.Message}");
            }

            // ② 落盘缓存命中 → 直接加载，跳过 Roslyn
            string cachePath = Path.Combine(CompileCacheDir, asmName + ".dll");
            if (File.Exists(cachePath))
            {
                try
                {
                    var bytes = File.ReadAllBytes(cachePath);
                    asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(bytes));
                    LogManager.Debug($"[编译] 命中落盘缓存（跳过 Roslyn）：{id}");
                    return true;
                }
                catch (Exception ex)
                {
                    // 缓存坏了（写入中断/版本不兼容）→ 删掉重编，不能带着坏缓存一直失败
                    LogManager.Warning($"[编译] 落盘缓存加载失败，改为重新编译：{id} — {ex.Message}");
                    try { File.Delete(cachePath); } catch (Exception dex) { LogManager.Debug($"[编译] 删除坏缓存失败：{dex.Message}"); }
                }
            }

            // ③ 现编 + 落盘
            using var ms = new MemoryStream();
            if (!TryEmit(id, source, ms, asmName, out errors)) { asm = null; return false; }
            byte[] emitted = ms.ToArray();
            SaveCompileCache(asmName, emitted);
            asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(emitted));
            return true;
        }

        /// <summary>把编好的程序集写进缓存目录（失败只影响下次速度，不影响本次功能）。</summary>
        private static void SaveCompileCache(string asmName, byte[] bytes)
        {
            try
            {
                Directory.CreateDirectory(CompileCacheDir);
                string path = Path.Combine(CompileCacheDir, asmName + ".dll");
                File.WriteAllBytes(path, bytes);
                PruneCompileCache();
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[编译] 写入落盘缓存失败（本次功能正常，只是下次还要现编）：{ex.Message}");
            }
        }

        /// <summary>缓存目录清理：只按"最后写入时间"保留最近 N 个（用户反复改代码会堆很多版本）。</summary>
        private static void PruneCompileCache()
        {
            try
            {
                var files = new DirectoryInfo(CompileCacheDir).GetFiles("*.dll");
                if (files.Length <= CompileCacheLimit) return;
                foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(CompileCacheLimit))
                {
                    try { f.Delete(); } catch (Exception ex) { LogManager.Debug($"[编译] 清理缓存文件失败 {f.Name}：{ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[编译] 清理编译缓存失败（缓存会继续变大）：{ex.Message}");
            }
        }

        private static string ComputeHash(string source)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(source);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).Substring(0, 16);
        }

        /// <summary>仅编译校验（不实例化，避免副作用）。成功返回空串。</summary>
        public static string Validate(string id, string source)
        {
            try
            {
                using var ms = new MemoryStream();
                return TryEmit(id, source, ms, out string errors) ? "" : errors;
            }
            catch (Exception ex)
            {
                return "编译异常：" + ex.Message;
            }
        }

        /// <summary>
        /// 沙箱校验（市场来源代码，TrustedSource=false）：扫描源码中危险 API 并返回被拦截项列表（空 = 通过）。
        /// 参考成熟平台（Chrome MV3 禁远程代码 / Wallpaper Engine 移除 EXE）的"限制能力"思路：
        /// 硬拦截攻击类 API——进程执行/反射动态调用/P-Invoke/注册表/WMI/窗口与输入钩子/屏幕捕获/文件写/剪贴板；
        /// 网络与文件读属于"权限声明类"（导入时风险标签已提示），v1 不硬拦。
        /// 注意：静态扫描有理论绕过空间（混淆+反射），故同时硬拦反射/动态加载 API 把门槛抬高。
        /// </summary>
        public static List<string> CheckSandbox(string source)
        {
            var blocked = new List<string>();
            if (string.IsNullOrEmpty(source)) return blocked;

            // ★ 必须先去掉所有空白再匹配（实测过一次绕过）：直接匹配原文时，把 System.Activator
            //   换行写成两段 .CreateInstance 即可绕过（"\r" 恰好被模式里正则的 "." 命中，纯 LF 换行就漏）；
            //   而且 file.write / type.gettype / marshal. 这些模式里的 "." 本身就是通配符。
            //   现在统一「去空白归一」后匹配稳定字面形状，换行/制表/注释换行都无法规避。
            string tight = System.Text.RegularExpressions.Regex.Replace(source.ToLowerInvariant(), @"\s+", "");

            // (?<![a-z0-9_]) 前置守卫：避免 profile.writetext 这类标识符尾巴误命中 file.write
            (string Pattern, string Label)[] blockedApis =
            {
                // ★ process 变量名不拦（误伤），只拦 Process.Start/Process.StartInfo 调用形式
                (@"(?<![a-z0-9_])process\.start", "进程执行（Process.Start）"),
                ("processstartinfo", "进程启动（ProcessStartInfo）"),
                (@"\.getmethod\(", "反射（GetMethod）"),
                (@"\.getproperty\(", "反射（GetProperty）"),
                (@"\.invokemember\(", "反射（InvokeMember）"),
                (@"(?<![a-z0-9_])activator\.", "动态创建（Activator）"),
                ("dynamicmethod", "动态方法（DynamicMethod）"),
                (@"(?<![a-z0-9_])type\.gettype", "动态类型（Type.GetType）"),
                ("dllimport", "原生调用（DllImport）"),
                (@"(?<![a-z0-9_])marshal\.", "原生内存（Marshal）"),
                // ★ 裸 "registry" 已删（2026-09-10）：会把变量名 registryPath、注释、文案一起拦掉；
                //   注册表访问由符号层精确拦截（Microsoft.Win32.Registry / RegistryKey 类型前缀）。
                //   见 SandboxPrecisionTests：注册表由符号层拦截不靠裸关键字 / 字符串字面量里出现这些词不再被拦。
                ("managementobject", "WMI"),
                // ★ v2 新增：XAML 动态调用构造 + 绕开既有黑名单的写文件/解密/监听通道
                ("objectdataprovider", "XAML 动态调用（ObjectDataProvider）"),
                ("x:code", "XAML 内联代码（x:Code）"),
                ("memorymappedfile", "内存映射文件（MemoryMappedFile）"),
                ("protecteddata", "DPAPI 解密（ProtectedData）"),
                ("httplistener", "网络监听（HttpListener）"),
                ("isolatedstorage", "独立存储（IsolatedStorage）"),
                // ★ 2026-09-10 安全复查：这里原先有 11 条裸子串规则（findwindow / enumwindows /
                //   setwindowshookex / sendinput / setforegroundwindow / postmessage / sendmessage /
                //   keybd_event / mouse_event / printwindow / bitblt）。
                //   它们全是 P/Invoke 函数名 —— 裸子串匹配会把字符串字面量、注释、UI 文案一起拦掉（纯误报），
                //   而"声明 P/Invoke"这件事本身已由符号层的 InteropServices 类型黑名单拦住
                //   （这条当时其实是坏的：特性名在语义模型里绑定到构造函数而不是类型 → P/Invoke 漏检，
                //     全靠这些文本规则兜着。已修 CheckSandboxSymbols 的 AttributeSyntax 分支，并加测试锁住）。
                //   证据：tests/ShoreHue.Tests/SandboxPrecisionTests.cs（11 个原生函数用例逐个验证符号层拦得住）。
                //   ★ copyfromscreen 保留：它走 System.Drawing.Graphics.CopyFromScreen，不是 P/Invoke，符号层拦不到。
                ("copyfromscreen", "屏幕捕获"),
                (@"(?<![a-z0-9_])file\.write", "文件写入"),
                (@"(?<![a-z0-9_])file\.append", "文件写入"),
                (@"(?<![a-z0-9_])file\.delete", "文件删除"),
                (@"(?<![a-z0-9_])file\.move", "文件移动"),
                (@"(?<![a-z0-9_])file\.copy", "文件复制"),
                (@"(?<![a-z0-9_])file\.create", "文件创建"),
                (@"(?<![a-z0-9_])file\.openwrite", "文件写入"),
                (@"(?<![a-z0-9_])file\.setattributes", "文件属性"),
                ("filestream", "文件流"),
                ("streamwriter", "文件写入流"),
                ("binarywriter", "二进制写入流"),
                (@"(?<![a-z0-9_])directory\.create", "目录创建"),
                (@"(?<![a-z0-9_])directory\.delete", "目录删除"),
                (@"(?<![a-z0-9_])directory\.move", "目录移动"),
                (@"(?<![a-z0-9_])zipfile\.(extract|create)", "压缩包解包/打包（ZipFile）"),
                // ★ 宿主特权 API 的文本层兜底：这些命名空间/类型在外来代码里出现即拦截
                //   （即使源码编译不过、符号层跳过，文本层也要挡住 —— 纵深防御）
                //   （用具体类名而非整个命名空间：ToastMonitor 等无害类要给正常包留路）
                ("uninstallhelper", "宿主卸载 API"),
                ("updateservice.", "宿主更新 API"),
                ("defenderscanner", "宿主扫描器 API"),
                ("mediakeyhelper", "宿主媒体键注入"),
                ("selectedtextcapture", "宿主划词捕获"),
                ("cursoroutputservice", "宿主光标输出/按键注入"),
                ("windowcaptureservice", "宿主窗口捕获"),
                ("recentapptracker", "宿主最近使用追踪"),
                ("webfavoritemanager", "宿主浏览器历史"),
                ("notificationcenterreader", "宿主通知库读取"),
                (@"(?<![a-z0-9_])windowaction\.", "宿主窗口控制"),
                ("systemlauncher.", "宿主系统启动器"),
                ("jumplistmanager", "宿主跳转列表"),
                ("mousehookservice", "宿主鼠标钩子"),
                ("windoweventhook", "宿主窗口事件钩子"),
                ("runtime.compilerservices.unsafe", "无类型安全内存操作（Unsafe）"),
                ("shorehue.core.services.ai.aisettings", "宿主 AI 凭证 API"),
                ("shorehue.core.services.ai.aisessionstore", "宿主 AI 会话存储"),
                ("shorehue.core.infrastructure.service.servicemanager", "宿主服务容器（ServiceManager）"),
                // ★ 具体设置管理器也拦：插件该走 HostCapabilities.Settings（窄接口），
                //   拿到 SettingsManager 就能整表改写宿主配置（含信任库所在的 config.json）。
                ("shorehue.core.services.configuration.settingsmanager", "宿主设置管理器（SettingsManager）"),
                // ★ 配置落盘封装：Save(data, path) 收任意路径 → 不拦就是一条任意写文件通道
                ("shorehue.core.services.settingsfilemanager", "宿主配置落盘 API（SettingsFileManager）"),
                ("shorehue.core.services.configuration.presetmanager", "宿主预设读写 API（PresetManager）"),
                // ★ 通知监听整体拦掉：它能开关监听、遍历并清空**用户收到的全部通知内容**，
                //   属于隐私面而不是"低危能力"。插件要通知能力请走 HostCapabilities.Notifications
                //   （已提供同样完整的读/开/删/清空 + 变更事件，且带条目数上限）。
                ("shorehue.infrastructure.winapi.toastmonitor", "宿主通知监听 API（ToastMonitor）"),
                // ★ 窗口枚举 + 句柄→进程路径：属决策表 B8 的"窗口/输入"能力面
                //   （插件可据此枚举用户开着什么、读进程路径做信息收集），不该零门槛开放。
                ("shorehue.infrastructure.winapi.windowlistprovider", "宿主窗口枚举 API（WindowListProvider）"),
                // ★ 宿主 AI 传输实现：插件要用 AI 只能走 HostCapabilities.AskAiAsync（密钥不出宿主、限长）。
                //   直接 new 它「碰巧」调不通——因为它每个有用的方法都要一个已被拦的 AiSettings，
                //   但这属于"靠参数类型间接安全"，脆得很（将来给它加个无参重载就漏了）。直接拦掉。
                ("shorehue.core.services.ai.aichatclient", "宿主 AI 传输实现（AiChatClient）"),
                ("shorehue.core.services.system.trayiconmanager", "宿主托盘 API"),
                ("shorehue.core.services.clipboardmanager", "宿主剪贴板管理器"),
                ("shorehue.ui.seabed.", "海床/市场内部 API"),
                ("shorehue.ui.settings.", "设置窗口内部 API"),
                ("shorehue.ui.main.", "主窗口内部 API"),
                ("shorehue.ui.widgets.dynamic.widget", "动态编译/插件仓库 API"),
                // ★ 剪贴板不硬拦：剪贴板读写是常见低危能力，
                //   归入"权限声明类"——安装时弹窗提示"使用剪贴板"，用户确认后放行（与网络/文件读一致）
            };

            foreach (var (pattern, label) in blockedApis)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(tight, pattern) && !blocked.Contains(label))
                {
                    blocked.Add(label);
                }
            }
            // ★ 组合 = 数据外泄（文本层粗筛；符号级 CheckSandboxSymbols 做权威语义判定）
            bool hasFileRead = System.Text.RegularExpressions.Regex.IsMatch(tight,
                @"(?<![a-z0-9_])(file\.read|readalltext|readallbytes|readlines|readalllines|opentext|openread|streamreader|fileinfo|filesysteminfo)");
            bool hasNetwork = System.Text.RegularExpressions.Regex.IsMatch(tight,
                @"(?<![a-z0-9_])(httpclient|httprequestmessage|webclient|httpwebrequest|socket|tcpclient|udpclient|webrequest|system\.net\.dns)");
            if (hasFileRead && hasNetwork)
            {
                blocked.Add("文件读取 + 网络（组合可窃取本地文件外传）");
            }
            return blocked;
        }

        // ==================== 受限 XAML 方言（结构白名单）====================

        /// <summary>允许的界面元素（起步集合，按需扩充；不在其中一律拒绝）。</summary>
        private static readonly HashSet<string> AllowedXamlElements = new(StringComparer.Ordinal)
        {
            "UserControl", "Grid", "StackPanel", "WrapPanel", "DockPanel", "Canvas", "Border", "ScrollViewer",
            "Viewbox", "UniformGrid", "TextBlock", "TextBox", "RichTextBox", "Button", "ToggleButton",
            "CheckBox", "RadioButton", "ComboBox", "ComboBoxItem", "ListBox", "ListBoxItem", "ListView",
            "ListViewItem", "ItemsControl", "ContentControl", "ContentPresenter", "Label", "Image", "Path",
            "Rectangle", "Ellipse", "Line", "Polygon", "Polyline", "ProgressBar", "Slider", "Separator",
            "Expander", "GroupBox", "TabControl", "TabItem", "ToolTip", "Popup",
            "Style", "Setter", "Trigger", "DataTrigger", "MultiTrigger", "EventTrigger", "ControlTemplate",
            "DataTemplate", "ItemsPanelTemplate", "ResourceDictionary", "RowDefinition", "ColumnDefinition",
            "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "GradientStop",
            "RotateTransform", "TranslateTransform", "ScaleTransform", "SkewTransform", "TransformGroup",
            "DropShadowEffect", "BlurEffect",
            // 常用补充（市场小组件真实用到的控件）
            "ItemsPanel", "ItemsPresenter", "VirtualizingStackPanel", "ScrollContentPresenter",
            "GridSplitter", "RepeatButton", "Thumb", "Menu", "MenuItem", "ContextMenu", "StatusBar",
            "TreeView", "TreeViewItem", "HeaderedContentControl", "HeaderedItemsControl", "AccessText",
            "Decorator", "AdornerDecorator", "DrawingBrush", "ImageBrush", "VisualBrush", "DoubleCollection",
            "Int32Collection", "PointCollection", "FontFamily", "Duration", "Thickness", "CornerRadius",
        };

        /// <summary>常见事件名（用于把"事件属性"与"普通属性"区分开；不在白名单内的事件一律拒绝）。</summary>
        private static bool IsEventLikeName(string name)
        {
            foreach (var suffix in new[] { "Click", "Changed", "Enter", "Leave", "Down", "Up", "Loaded", "Unloaded",
                                           "Focused", "Opened", "Closed", "Completed", "Tick", "Scroll", "Drop", "Drag" })
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>允许的标记扩展（其余一律拒绝：x:Static / x:Type / ObjectDataProvider 之类不在其中）。</summary>
        private static readonly HashSet<string> AllowedMarkupExtensions = new(StringComparer.Ordinal)
        {
            "Binding", "StaticResource", "DynamicResource", "TemplateBinding",
        };

        /// <summary>允许的事件（与 XamlCodeGenerator.EventNames 一致；事件由生成的 C# 代码挂接，走符号级检查）。</summary>
        private static readonly HashSet<string> AllowedXamlEvents = new(StringComparer.Ordinal)
        {
            "Click", "MouseDown", "MouseUp", "KeyDown", "KeyUp", "TextChanged", "SelectionChanged",
            "MouseLeftButtonDown", "MouseLeftButtonUp", "LostFocus", "GotFocus", "PreviewMouseDown",
            "PreviewMouseUp", "Checked", "Unchecked", "ValueChanged", "DragOver", "Drop",
        };

        /// <summary>允许的属性（界面外观/布局/内容类；不含任何"能触发行为"的属性）。</summary>
        private static readonly HashSet<string> AllowedXamlProperties = new(StringComparer.Ordinal)
        {
            "Name", "Key", "Class", "Content", "Text", "Tag", "ToolTip", "Header",
            "Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius", "Padding", "Margin",
            "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
            "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment",
            "FontSize", "FontWeight", "FontFamily", "FontStyle", "TextAlignment", "TextWrapping", "TextTrimming",
            "Orientation", "Row", "Column", "RowSpan", "ColumnSpan", "ZIndex",
            "Opacity", "Visibility", "Cursor", "IsEnabled", "IsChecked", "IsReadOnly", "AcceptsReturn",
            "IsCancel", "IsDefault", "SelectionMode", "SelectedIndex", "Stretch", "Spacing",
            "Fill", "Stroke", "StrokeThickness", "RadiusX", "RadiusY", "Points", "Data", "Geometry",
            "Style", "ItemTemplate", "ContentTemplate", "Template", "ItemsSource", "Resources",
            "TargetType", "Property", "Value", "Offset", "Angle", "CenterX", "CenterY", "ScaleX", "ScaleY",
            "StartPoint", "EndPoint", "Color", "OpacityMask", "Effect", "Transform", "LayoutTransform",
            "RenderTransform", "RenderTransformOrigin", "MinLines", "MaxLines", "LineHeight", "LineStackingStrategy",
        };

        /// <summary>
        /// ★ 受限 XAML 方言校验（替代"外来代码一律禁 XAML"这一刀切）：
        ///   保留"用 XAML 描述界面"的能力，砍掉"用 XAML 凭空构造对象 / 按名调方法"的能力。
        ///   做法是**结构化白名单**（走 XML 树，不是字符串黑名单）：
        ///     ① 元素必须属于界面子集（ObjectDataProvider / x:Code / MediaElement / WebBrowser / Hyperlink 等都不在）；
        ///     ② 命名空间只认 WPF 官方程序集，自定义 clr-namespace 一律拒绝；
        ///     ③ 属性必须在允许集合内（字面量），标记扩展只放行 Binding/StaticResource/DynamicResource/TemplateBinding；
        ///     ④ 事件只放行既有白名单（由生成的 C# 挂接，受符号级检查约束）。
        ///   代码逻辑仍在 .xaml.cs 里，走三层 C# 沙箱 —— 完全编程形态对市场包同样可用。
        /// </summary>
        public static List<string> CheckXamlDialect(string xaml)
        {
            var blocked = new List<string>();
            if (string.IsNullOrWhiteSpace(xaml)) return blocked;
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xaml);
                foreach (var el in doc.Descendants())
                {
                    string ns = el.Name.NamespaceName;
                    if (ns.StartsWith("clr-namespace:", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(blocked, "XAML 自定义类型命名空间（仅允许 WPF 界面元素）: " + ns);
                        continue;
                    }
                    // ★ XAML 里 <Grid.RowDefinitions> / <Border.Background> 这类"带点元素名"要归一化：
                    //   点号属于 XML 局部名，不是类型名前缀（类型名不会含点）
                    string local = el.Name.LocalName;
                    int dot = local.LastIndexOf('.');
                    if (dot > 0) local = local.Substring(dot + 1);
                    if (local == "Code") { Add(blocked, "XAML 内联代码（x:Code）"); continue; }
                    // 属性元素（Grid.RowDefinitions / Border.Background / StackPanel.Resources ...）：
                    // 它们是"属性"，不是类型实例，按属性名放行（其子元素照常逐个校验）
                    if (AllowedXamlProperties.Contains(local) || local is "RowDefinitions" or "ColumnDefinitions"
                        or "Resources" or "Triggers" or "Children")
                    {
                        continue;
                    }
                    if (!AllowedXamlElements.Contains(local))
                    {
                        Add(blocked, "XAML 元素不在界面子集内: " + local);
                        continue;
                    }
                    // ★ 模板（DataTemplate/ControlTemplate/ItemsPanelTemplate）里的元素在**独立命名域**，
                    //   而生成器是用 `root.FindName(...)` 挂处理器的 —— 挂不上，而且是**静默**的：
                    //   XAML 编译通过、界面里点了没反应（内置便签的芯片点击与剪贴板列表的
                    //   复制/收藏/单条删除都因此失效过）。这里直接拒绝，逼作者改用委托：
                    //   把处理器挂到模板**外面**的容器上，用 e.OriginalSource 沿可视树找回条目。
                    bool insideTemplate = el.Ancestors().Any(a =>
                        a.Name.LocalName is "DataTemplate" or "ControlTemplate" or "ItemsPanelTemplate");
                    foreach (var attr in el.Attributes())
                    {
                        // ① xmlns / xmlns:x 等命名空间声明：跳过（不是属性赋值）
                        if (attr.IsNamespaceDeclaration ||
                            attr.Name.NamespaceName == "http://www.w3.org/2000/xmlns/") continue;

                        string an = attr.Name.LocalName;
                        if (an == "Class" || an == "Name" || an == "Key") continue;   // x:Class / x:Name / x:Key

                        // ② 事件：只放行既有白名单（其余事件属性一律拒绝）
                        if (an.StartsWith("On", StringComparison.Ordinal) || IsEventLikeName(an))
                        {
                            if (!AllowedXamlEvents.Contains(an)) Add(blocked, "XAML 事件不允许: " + an);
                            else if (el.Parent == null || el.Parent.Name.LocalName == "UserControl" && el == el.Document?.Root)
                                Add(blocked, "根元素上的事件不会被接线（生成器按控件名查找宿主）: " + an
                                    + " —— 请移到一个带 x:Name 的内层容器上（如 <Grid x:Name=\"RootGrid\" KeyDown=\"…\">）");
                            else if (insideTemplate)
                                Add(blocked, "模板里的事件挂不上（处理器按根命名域查找）: " + an
                                    + " —— 请把处理器挂到模板外的容器上，用 e.OriginalSource 做委托");
                            continue;
                        }

                        // ③ 值检查：标记扩展白名单 + 外部资源引用
                        string v = attr.Value ?? "";
                        if (v.TrimStart().StartsWith("{"))
                        {
                            string ext = v.TrimStart().Substring(1).Split(new[] { ' ', '}', ',' })[0];
                            if (!AllowedMarkupExtensions.Contains(ext)) { Add(blocked, "XAML 标记扩展不允许: " + ext); continue; }
                        }
                        CheckAttributeValue(blocked, an, v);
                    }
                }
            }
            catch (Exception ex) { Add(blocked, "XAML 解析失败：" + ex.Message); }
            return blocked;
        }

        private static void Add(List<string> list, string item)
        {
            if (!list.Contains(item)) list.Add(item);
        }

        /// <summary>属性值检查：拒绝跨协议/文件访问型字面量（Image Source 之类）。</summary>
        private static void CheckAttributeValue(List<string> blocked, string attr, string value)
        {
            string v = value.Trim();
            if (v.Length == 0) return;
            bool uriLike = v.Contains("://", StringComparison.Ordinal) ||
                           v.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                           v.StartsWith("\\\\", StringComparison.Ordinal);
            if (uriLike) Add(blocked, "XAML 属性引用了外部资源（" + attr + "=" + v + "）");
        }

        /// <summary>
        /// XAML 标记文本（将被 XamlReader.Parse 解析）的专用检查。
        /// ★ 只做文本级：XAML 不是 C#，丢进符号级检查会把标记误判成「无法解析的调用」而误杀正常包；
        ///   但 XAML 里的动态调用构造（ObjectDataProvider / x:Code）与危险命名空间必须在这里拦。
        /// </summary>
        public static List<string> CheckSandboxMarkup(string xaml)
        {
            var blocked = new List<string>();
            if (string.IsNullOrWhiteSpace(xaml)) return blocked;
            string tight = System.Text.RegularExpressions.Regex.Replace(xaml.ToLowerInvariant(), @"\s+", "");

            (string Pattern, string Label)[] rules =
            {
                ("objectdataprovider", "XAML 动态调用（ObjectDataProvider）"),
                ("x:code", "XAML 内联代码（x:Code）"),
                ("methodname=", "XAML 方法调用（ObjectDataProvider.MethodName）"),
                ("clr-namespace:system.diagnostics", "XAML 引用 System.Diagnostics"),
                ("clr-namespace:system.reflection", "XAML 引用 System.Reflection"),
                ("clr-namespace:system.runtime.interopservices", "XAML 引用 InteropServices"),
                ("clr-namespace:system.runtime.loader", "XAML 引用程序集加载"),
                ("clr-namespace:system.management", "XAML 引用 WMI"),
                ("clr-namespace:microsoft.win32", "XAML 引用注册表/系统"),
                ("clr-namespace:system.io", "XAML 引用 System.IO"),
                ("clr-namespace:system.net", "XAML 引用 System.Net"),
                ("clr-namespace:system.security.cryptography", "XAML 引用加密/DPAPI"),
                ("clr-namespace:system.activator", "XAML 引用 Activator"),
                ("clr-namespace:shorehue.infrastructure", "XAML 引用宿主底层 API"),
                ("clr-namespace:shorehue.core.services.ai", "XAML 引用宿主 AI 凭证"),
                ("clr-namespace:shorehue.ui.seabed", "XAML 引用海床/市场"),
                ("clr-namespace:shorehue.ui.settings", "XAML 引用设置窗口"),
            };
            foreach (var (pattern, label) in rules)
            {
                if (tight.Contains(pattern) && !blocked.Contains(label)) blocked.Add(label);
            }
            return blocked;
        }

        // ==================== 编译符号级沙箱检查（补文本扫描的绕过洞） ====================

        /// <summary>
        /// 编译符号级检查：解析源码每个成员访问/对象创建/类型引用的真实符号（编译器解析，与书写方式无关），
        /// 命中类型级/成员级黑名单即拦截。文本扫描可被换皮绕过（如 File.Open 写文件、Assembly.GetType 反射），
        /// 符号级不可绕过——只要引用了危险类型/成员，无论怎么写都会命中。
        /// </summary>
        public static List<string> CheckSandboxSymbols(string source)
        {
            var blocked = new List<string>();
            if (string.IsNullOrWhiteSpace(source)) return blocked;
            // ★ 组合规则标志（语义级）：单独允许的能力组合起来就是外泄通道
            bool hasNetwork = false, hasFileRead = false, hasClipboardRead = false;
            try
            {
                var tree = CSharpSyntaxTree.ParseText(source);
                var compilation = CSharpCompilation.Create(
                    "sandbox_check_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    new[] { tree },
                    BuildReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                var model = compilation.GetSemanticModel(tree);

                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    try
                    {
                        ISymbol? sym = null;
                        bool resolutionFailed = false;
                        if (node is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma)
                        {
                            var info = model.GetSymbolInfo(ma);
                            sym = info.Symbol;
                            // ★ 解析失败（dynamic 延迟绑定/未知类型）→ 视为可疑，直接拦截
                            if (sym == null && info.CandidateSymbols.Length > 0)
                                sym = info.CandidateSymbols[0];
                            else if (sym == null)
                                resolutionFailed = true;
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax inv)
                        {
                            var info = model.GetSymbolInfo(inv);
                            sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                            // ★ 不再因解析失败硬拦：方法组/委托构造/重载决议等合法代码也会解析失败，
                            //   实测误杀内置模板（new Action(Refresh) → "疑似 dynamic 绕过"）。
                            //   dynamic 绕过只能通过成员访问发生（(dynamic)x.M()），由上方 MemberAccess 分支兜底。
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax oc)
                        {
                            var info = model.GetSymbolInfo(oc);
                            sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax at)
                        {
                            // ★★ 特性（[DllImport] 等）必须单独处理：特性名在语义模型里绑定到**构造函数**，
                            //   不是类型 —— 于是 IdentifierName 分支的 `is ITypeSymbol` 判定失败、
                            //   又因为该分支末尾 continue，连"包含类型是否在黑名单"都没机会查。
                            //   后果是 **P/Invoke 声明长期漏检**，全靠文本规则 `dllimport` 兜着（实测：11 个原生函数用例
                            //   没有一个被符号层拦住）。这里对特性的类型显式查一次。
                            ITypeSymbol? ats = model.GetTypeInfo(at).Type as ITypeSymbol;
                            if (ats == null)
                            {
                                var asy = model.GetSymbolInfo(at.Name).Symbol;
                                ats = (asy as IMethodSymbol)?.ContainingType ?? asy?.ContainingType;
                            }
                            if (ats != null && IsBlockedTypeSymbol(ats))
                            {
                                string tn = ats.ToString() ?? "";
                                if (!blocked.Contains("禁止类型: " + tn)) blocked.Add("禁止类型: " + tn);
                            }
                            continue;
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax idn)
                        {
                            var ids = model.GetSymbolInfo(idn).Symbol;
                            // 标识符指向被禁类型本身，或指向被禁类型的成员 → 都算命中
                            ITypeSymbol? ts = ids as ITypeSymbol;
                            if (ts == null && ids?.ContainingType != null && IsBlockedTypeName(ids.ContainingType.ToString() ?? ""))
                            {
                                string ct = ids.ContainingType.ToString() ?? "";
                                if (!blocked.Contains("禁止类型: " + ct)) blocked.Add("禁止类型: " + ct);
                                continue;
                            }
                            if (ts != null && IsBlockedTypeSymbol(ts))
                            {
                                string tn = ts.ToString() ?? "";
                                if (!blocked.Contains("禁止类型: " + tn)) blocked.Add("禁止类型: " + tn);
                            }
                            continue;
                        }
                        // ★ dynamic/未知符号：编译器都解析不出来，无法确认安全 → 拦截（防延迟绑定绕过）
                        if (resolutionFailed)
                        {
                            string label = "无法解析的调用（疑似 dynamic 绕过）: " + node.ToString();
                            if (label.Length > 120) label = label.Substring(0, 120) + "…";
                            if (!blocked.Contains(label)) blocked.Add(label);
                            continue;
                        }
                        if (sym == null) continue;
                        var containing = sym.ContainingType;
                        if (containing == null) continue;
                        string typeName = containing.ToString() ?? "";
                        // ★ 语义级能力标记（组合规则用；与书写方式无关）
                        if (IsNetworkTypeName(typeName)) hasNetwork = true;
                        if (IsFileReadMember(typeName, sym.Name)) hasFileRead = true;
                        if (IsClipboardReadMember(typeName, sym.Name)) hasClipboardRead = true;
                        if (IsBlockedTypeName(typeName))
                        {
                            if (!blocked.Contains("禁止类型: " + typeName)) blocked.Add("禁止类型: " + typeName);
                        }
                        else if (IsBlockedMember(typeName, sym.Name))
                        {
                            string label = "禁止成员: " + typeName + "." + sym.Name;
                            if (!blocked.Contains(label)) blocked.Add(label);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ★ fail-closed（与下面 632 的外层 catch 同一口径）：单个节点检查不出结果 = 无法确认它安全，
                        //   旧版静默跳过等于"这个节点不做符号检查"，属静默降级。
                        string label = "符号检查失败（按不安全处理）: " + node.ToString();
                        if (label.Length > 120) label = label.Substring(0, 120) + "…";
                        if (!blocked.Contains(label)) blocked.Add(label);
                        LogManager.Warning($"[沙箱] 符号检查单节点失败（按不安全处理）：{ex.Message}");
                    }
                }
            }
            catch
            {
                // ★ fail-closed：符号检查自身失败**不能静默跳过** —— 跳过一个符号就等于只剩文本层（防线降级）
                blocked.Add("沙箱符号检查失败（源码无法解析），按不安全处理");
            }
            // ★★ 组合规则（权威判定）：单独是权限，组合是外泄
            if (hasNetwork && hasFileRead)
                blocked.Add("文件读取 + 网络（组合可窃取本地文件外传）");
            if (hasNetwork && hasClipboardRead)
                blocked.Add("剪贴板读取 + 网络（组合可窃取剪贴板内容外传）");
            return blocked;
        }

        // ==================== 数据流（污点）分析：能力组合的真实判定 ====================

        /// <summary>
        /// ★ S1（研究驱动）：用 Roslyn 的语义模型做**过程内污点分析**，替代"看到读文件 + 看到联网就拦"的
        /// 写死规则。判定的是一条真实的数据通路：**读取类来源 → 网络出口**（本地文件 / 剪贴板 / 宿主私有数据）。
        /// 为什么需要它：单独读文件是权限（允许），单独联网是权限（允许），只有"读到的内容流到网络"才是外泄；
        /// 单纯罗列符号无法区分两者，会同时造成漏拦与误拦。
        /// 实现：对每个方法体做定点迭代，标记"来自来源的局部变量/字段"，再看网络出口的实参是否被污染。
        /// 分析不可用（源码无法编译）时返回 inconclusive，由调用方回退到粗筛。
        /// </summary>
        public static List<string> CheckDataFlow(string source, out bool conclusive)
        {
            var blocked = new List<string>();
            conclusive = false;
            if (string.IsNullOrWhiteSpace(source)) { conclusive = true; return blocked; }
            try
            {
                var tree = CSharpSyntaxTree.ParseText(source);
                var compilation = CSharpCompilation.Create(
                    "dataflow_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    new[] { tree },
                    BuildReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                // 编译有错 → 语义不完整，不做结论（交给粗筛）
                if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return blocked;
                var model = compilation.GetSemanticModel(tree);
                conclusive = true;

                // ★★ 跨方法（字段级过程间）：污染集合放在**所有方法之外**共享。
                //   旧实现每个方法各有一个 tainted 集合，于是
                //     void A() { _cache = File.ReadAllText(p); }
                //     void B() { new HttpClient().PostAsync(u, _cache); }
                //   完全不拦（实测确认：这是当时最大的一个洞）。字段符号跨方法唯一，故共享集合即可把它们串起来。
                //   代价：类里任何"曾被污染的字段"在别处出现都算脏 —— 比精确过程间分析保守，方向只会更严不会更松。
                var methods = tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().ToList();
                var tainted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                // ★ 敏感污点（子集）：文件内容/剪贴板/环境变量/宿主私有数据。
                //   宿主能力 OpenExternally/AskAiAsync 有正当用法（把"最近使用"里的条目交给系统打开），
                //   所以它们只对**敏感数据**拦截；原始网络出口（HttpClient/Socket/…）仍对任意污点拦截。
                var sensitive = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                var bodyNodes = methods.SelectMany(m => m.DescendantNodes()).ToList();

                // ① 定点迭代：把"来源"赋值给局部变量/字段的符号标为污染
                //   上限给足（正常直线流一趟就传播完，循环/回边才需要多趟）；靠下面的 !changed 提前收敛。
                //   原来写死 4 趟：非线性流（循环内重赋值、顺序倒置）可能传播不完全 → 分析结论不可靠。
                for (int pass = 0; pass < 50; pass++)
                {
                    bool changed = false;
                    foreach (var node in bodyNodes)
                    {
                        ISymbol? target = null;
                        ExpressionSyntax? value = null;
                        if (node is Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax vd && vd.Initializer != null)
                        {
                            target = model.GetDeclaredSymbol(vd);
                            value = vd.Initializer.Value;
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax ae)
                        {
                            target = model.GetSymbolInfo(ae.Left).Symbol;
                            value = ae.Right;
                        }
                        else if (node is Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax fe)
                        {
                            // ★ 2026-09 审计补充：`foreach (var n in 宿主数据源) { 发送(n.X) }`
                            //   旧实现不给迭代变量传播污点 → 集合里的每一条数据（通知正文 / 剪贴板历史）都能外传。
                            var loopVar = model.GetDeclaredSymbol(fe);
                            if (loopVar != null)
                            {
                                if (IsSensitiveExpression(fe.Expression, model, sensitive))
                                {
                                    if (sensitive.Add(loopVar)) changed = true;
                                    tainted.Add(loopVar);
                                }
                                else if (IsTaintedExpression(fe.Expression, model, tainted) && tainted.Add(loopVar)) changed = true;
                            }
                            continue;
                        }
                        if (target == null || value == null) continue;
                        if (IsSensitiveExpression(value, model, sensitive))
                        {
                            if (sensitive.Add(target)) changed = true;
                            tainted.Add(target);
                        }
                        else if (IsTaintedExpression(value, model, tainted) && tainted.Add(target)) changed = true;
                    }
                    if (!changed) break;
                }

                // ② 出口：网络调用的实参若被污染 → 数据外泄
                foreach (var inv in bodyNodes.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    var sym = model.GetSymbolInfo(inv).Symbol;
                    if (sym == null || !IsNetworkSink(sym)) continue;
                    bool nav = IsNavigationSink(sym);
                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        bool hit = nav
                            ? IsSensitiveExpression(arg.Expression, model, sensitive)
                            : IsTaintedExpression(arg.Expression, model, tainted);
                        if (!hit) continue;
                        string label = "数据流外泄：读取的内容流向网络出口（" + sym.ContainingType?.Name + "." + sym.Name + "）";
                        if (!blocked.Contains(label)) blocked.Add(label);
                        break;
                    }
                }
                // ③ 出口（构造函数）：new BitmapImage(new Uri("https://…" + 数据)) 之类会发起 HTTP GET
                foreach (var oc in bodyNodes.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>())
                {
                    var sym = model.GetSymbolInfo(oc).Symbol;
                    if (sym == null || !IsNetworkSink(sym)) continue;
                    if (oc.ArgumentList == null) continue;
                    foreach (var arg in oc.ArgumentList.Arguments)
                    {
                        if (!IsTaintedExpression(arg.Expression, model, tainted)) continue;
                        string label = "数据流外泄：读取的内容流向网络出口（" + sym.ContainingType?.Name + " 构造函数）";
                        if (!blocked.Contains(label)) blocked.Add(label);
                        break;
                    }
                }
            }
            catch
            {
                conclusive = false;   // 分析异常 → 交给粗筛兜底
            }
            return blocked;
        }

        /// <summary>表达式是否"含来源或已被污染的值"。</summary>
        private static bool IsTaintedExpression(SyntaxNode expr, SemanticModel model, HashSet<ISymbol> tainted)
        {
            foreach (var node in expr.DescendantNodesAndSelf())
            {
                ISymbol? sym = null;
                if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax)
                    sym = model.GetSymbolInfo(node).Symbol;
                if (sym != null)
                {
                    if (tainted.Contains(sym)) return true;
                    if (IsTaintSource(sym)) return true;
                }
            }
            return false;
        }

        /// <summary>读取类来源（本地文件 / 剪贴板 / 环境变量 / 宿主私有数据）。
        /// ★ 来源清单必须"覆盖常见私有数据"，否则组合规则就是摆设：实测旧清单漏掉了环境变量
        ///   （Environment.GetEnvironmentVariable 常放 API key）与宿主服务读取，
        ///   于是 PostAsync(url, new StringContent(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) 原样通过。</summary>
        private static bool IsTaintSource(ISymbol sym)
        {
            string type = sym.ContainingType?.ToString() ?? "";
            string name = sym.Name;
            if (type == "System.IO.File")
                return name.StartsWith("Read", StringComparison.Ordinal) || name is "OpenRead" or "OpenText";
            if (type is "System.IO.StreamReader" or "System.IO.TextReader")
                return name.StartsWith("Read", StringComparison.Ordinal);
            if (type == "System.Windows.Clipboard")
                return name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Contains", StringComparison.Ordinal);
            if (type.EndsWith("ClipboardManager", StringComparison.Ordinal) || type.EndsWith("IClipboardService", StringComparison.Ordinal))
                return name is "History" or "CaptureClipboardNow";
            if (type == "System.Environment")
                return name is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "GetCommandLineArgs";
            // 宿主服务读取（便签正文 / 用户设置 / 快捷方式）——这些同样是"用户的私有数据"，外传一样是外泄
            if (type.EndsWith("INoteService", StringComparison.Ordinal) || type.EndsWith("NoteManager", StringComparison.Ordinal) ||
                type.EndsWith("IShortcutService", StringComparison.Ordinal) ||
                type.EndsWith("ISettingsService", StringComparison.Ordinal))
                return true;
            if (type.StartsWith("ShoreHue.UI.Widgets.HostCapabilities", StringComparison.Ordinal))
                return name is "GetRecentItems" or "GetWebFavorites" or "CaptureSelectedTextAsync"
                            or "Notes" or "Settings" or "Shortcuts" or "Notifications" or "ClipboardHistory";
            return false;
        }

        /// <summary>表达式是否含"敏感来源或已被标记为敏感的值"。</summary>
        private static bool IsSensitiveExpression(SyntaxNode expr, SemanticModel model, HashSet<ISymbol> sensitive)
        {
            foreach (var node in expr.DescendantNodesAndSelf())
            {
                ISymbol? sym = null;
                if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax)
                    sym = model.GetSymbolInfo(node).Symbol;
                if (sym == null) continue;
                if (sensitive.Contains(sym)) return true;
                if (IsSensitiveTaintSource(sym)) return true;
            }
            return false;
        }

        /// <summary>敏感来源：IsTaintSource 去掉"导航目标类"来源（最近使用 / 网页收藏 —— 它们本来就是拿来打开的）。</summary>
        private static bool IsSensitiveTaintSource(ISymbol sym)
        {
            string type = sym.ContainingType?.ToString() ?? "";
            if (type.StartsWith("ShoreHue.UI.Widgets.HostCapabilities", StringComparison.Ordinal))
                return sym.Name is not ("GetRecentItems" or "GetWebFavorites");
            return IsTaintSource(sym);
        }

        /// <summary>导航出口（宿主能力里"把目标交给系统打开 / 交给 AI"的成员）：只对敏感数据判定外泄。</summary>
        private static bool IsNavigationSink(ISymbol sym)
        {
            string type = sym.ContainingType?.ToString() ?? "";
            return type.StartsWith("ShoreHue.UI.Widgets.HostCapabilities", StringComparison.Ordinal)
                && sym.Name is "OpenExternally" or "AskAiAsync";
        }

        /// <summary>网络出口（外传通道）。
        /// ★ 2026-09 审计补充：出口清单必须覆盖**所有**真实出站通道，漏一个就是一条数据外泄路径：
        ///   · SmtpClient / Ping / ClientWebSocket —— 不是 HttpClient 但同样把数据发出去；
        ///   · WPF 的 BitmapImage / BitmapFrame / MediaPlayer / WebBrowser —— 传给它一个 http(s) Uri
        ///     就会发起 GET（内部走 WebRequest，符号层只看到 WPF 类型）；
        ///   · 宿主窄接口里的 OpenExternally（用系统浏览器打开任意 http(s) URL）与 AskAiAsync（把 prompt 发给 AI 服务商）。</summary>
        private static bool IsNetworkSink(ISymbol sym)
        {
            string type = sym.ContainingType?.ToString() ?? "";
            string n = sym.Name;
            // ★ 2026-09 修补：NetworkStream.Write / WriteAsync 是**真实出站通道**，
            //   而下面那份动词清单只认 Send/Post/Get/Put/Patch/Delete/Upload*，漏掉了它 ——
            //   于是 `new TcpClient(...).GetStream().Write(敏感数据)` 能绕过数据外泄判定。
            //   本该按注释自己写的"漏一个就是一条数据外泄路径"来兜住，这里补上（fail-closed 方向）。
            if (type.StartsWith("System.Net.Sockets.NetworkStream", StringComparison.Ordinal) &&
                n.StartsWith("Write", StringComparison.Ordinal))
                return true;
            // ★ DNS 查询即外带出口：成员名以 Get 开头就算出口
            if (type.StartsWith("System.Net.Dns", StringComparison.Ordinal))
                return n.StartsWith("Get", StringComparison.Ordinal);
            if (type.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.WebClient", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.HttpWebRequest", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.WebRequest", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.Sockets.Socket", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.Sockets.TcpClient", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.Sockets.UdpClient", StringComparison.Ordinal) ||
                type.StartsWith("System.Net.Sockets.NetworkStream", StringComparison.Ordinal))
            {
                // 只认"发送/请求"类成员，避免把 Dispose/属性读取当出口
                return n.StartsWith("Send", StringComparison.Ordinal) ||
                       n.StartsWith("Post", StringComparison.Ordinal) ||
                       n.StartsWith("Get", StringComparison.Ordinal) ||
                       n.StartsWith("Put", StringComparison.Ordinal) ||
                       n.StartsWith("Patch", StringComparison.Ordinal) ||
                       n.StartsWith("Delete", StringComparison.Ordinal) ||
                       n == "UploadString" || n == "UploadData" || n == "UploadFile" || n == "DownloadString";
            }
            // ★ 审计补充：其它真实出站通道
            if (type.StartsWith("System.Net.Mail.SmtpClient", StringComparison.Ordinal))
                return n.StartsWith("Send", StringComparison.Ordinal);
            if (type.StartsWith("System.Net.NetworkInformation.Ping", StringComparison.Ordinal))
                return n.StartsWith("Send", StringComparison.Ordinal);
            if (type.StartsWith("System.Net.WebSockets.ClientWebSocket", StringComparison.Ordinal))
                return n.StartsWith("Send", StringComparison.Ordinal) || n == "ConnectAsync";
            // ★ WPF/媒体：构造或 Open/Navigate 会发起 HTTP GET
            if (type.StartsWith("System.Windows.Media.Imaging.BitmapImage", StringComparison.Ordinal) ||
                type.StartsWith("System.Windows.Media.Imaging.BitmapFrame", StringComparison.Ordinal))
                return n is ".ctor" or "Create" or "CreateFromUri";
            if (type.StartsWith("System.Windows.Media.MediaPlayer", StringComparison.Ordinal))
                return n == "Open";
            if (type.StartsWith("System.Windows.Controls.WebBrowser", StringComparison.Ordinal) ||
                type.StartsWith("System.Windows.Forms.WebBrowser", StringComparison.Ordinal))
                return n == "Navigate";
            // ★ 宿主窄接口里的出站能力
            if (type.StartsWith("ShoreHue.UI.Widgets.HostCapabilities", StringComparison.Ordinal))
                return n is "OpenExternally" or "AskAiAsync";
            return false;
        }

        // ★ 编译引用静态缓存：TPA 程序集列表进程内不变，MetadataReference 只建一次。
        //   原实现每次调用都对全部信任程序集 CreateFromFile（沙箱检查单次 150-350ms 的主因），
        //   高频重建（面板激活/设置变更）时反复重建数百个引用 → UI 线程冻结。
        private static readonly Lazy<List<MetadataReference>> _references =
            new(BuildReferencesCore, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        private static List<MetadataReference> BuildReferencesCore()
        {
            var swRef = System.Diagnostics.Stopwatch.StartNew();
            var refs = new List<MetadataReference>();
            var trusted = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
                .Split(Path.PathSeparator);
            foreach (var p in trusted)
            {
                // 尽力而为：个别程序集读不出元数据不影响其余引用；真缺了引用，Roslyn 会以编译诊断报给调用方
                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch (Exception ex) { LogManager.Debug($"[编译] 跳过无法读取的程序集引用 {p}：{ex.Message}"); }
            }
            try { refs.Add(MetadataReference.CreateFromFile(typeof(IWidget).Assembly.Location)); }
            catch (Exception ex)
            {
                // 宿主程序集引用缺失 = 之后所有插件都编译不过（用户会看到编译错误，这里给出原因）
                LogManager.Warning($"[编译] 添加宿主程序集引用失败（插件将无法编译）：{ex.Message}");
            }
            // ★ 构建引用表（约 200 个程序集的元数据）是纯 I/O + 纯数据，第一次编译里它占大头。
            //   量出来才知道该不该挪到后台预热（见 PrewarmAsync）。
            LogManager.Debug($"[编译] 构建程序集引用耗时 {swRef.ElapsedMilliseconds}ms（{refs.Count} 个，线程 {System.Environment.CurrentManagedThreadId}）");
            return refs;
        }

        private static List<MetadataReference> BuildReferences() => _references.Value;

        /// <summary>
        /// 后台预热编译引用表（TPA 程序集元数据，约 200 个文件）。
        /// ★ 为什么可以放后台：这里只做 MetadataReference.CreateFromFile（纯 I/O + Roslyn 数据），
        ///   **不创建任何 WPF 对象**，没有 UI 线程亲和性。
        /// ★ 为什么要放后台：面板内容构建是同步跑在 UI 线程上的，第一次编译会把这 1 秒多的引用构建
        ///   一起算进去 —— 实测首次激活小组件面板整体 1639ms，动画要等它做完才起帧。
        ///   提前在后台把引用表建好，UI 线程第一次编译直接命中缓存。
        /// </summary>
        public static void PrewarmAsync()
        {
            try
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var refs = BuildReferences();
                        LogManager.Debug($"[编译] 引用预热完成（{refs.Count} 个程序集，后台）");
                    }
                    catch (Exception ex)
                    {
                        // 预热失败不影响正确性，只是首次编译会慢一点
                        LogManager.Warning($"[编译] 引用预热失败（首次编译会慢一些）：{ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[编译] 无法启动引用预热：{ex.Message}");
            }
        }

        /// <summary>
        /// 预热一个内置件的编译产物：**只编译并落盘，不实例化**。
        ///
        /// ★ 必须与 Compile/CompileXaml 走**同一条**取程序集路径、同一个 id 口径
        ///   （调用方同样传 "builtin_" + id），产出的程序集名才与真正加载时完全一致 ——
        ///   否则预热会**静默变成空操作**：编了另一份、真要用时照样现编，看起来"预热已生效"。
        ///   这也是本方法刻意复用 TryGetAssembly 而不是自己拼名字的原因。
        ///
        /// ★ 为什么不能在这里调 CompileXaml：它会 Activator.CreateInstance 出 WPF 控件，
        ///   后台线程 new 出来的控件会绑到后台 Dispatcher 上，UI 线程再用就抛"另一个线程拥有该对象"。
        ///   本方法只做「生成 C# 字符串 → Roslyn 编译 → 写 DLL → 载入 ALC」，全是纯 CPU / 纯 IO。
        ///
        /// ★ 为什么值得做：内置件「文件优先」是在面板显示时**同步跑在 UI 线程**上的，
        ///   真机实测 5 件 = 3310ms（1.7s 一次性冷启动 + 5×350~500ms），面板要 3.3 秒后才开始显示。
        ///   提前在后台落盘后，首次唤出面板直接命中落盘缓存（实测 3370ms → 约 65ms）。
        /// </summary>
        public static bool WarmAssembly(string id, string? xaml, string? xamlCs, string? source)
        {
            try
            {
                if (!string.IsNullOrEmpty(xaml) && !string.IsNullOrEmpty(xamlCs))
                {
                    string? generated = XamlCodeGenerator.Generate(xaml!, xamlCs!);
                    if (generated == null) return false;
                    return TryGetAssembly("xaml_" + id, generated, out _, out _);
                }
                if (!string.IsNullOrEmpty(source))
                    return TryGetAssembly(id, source!, out _, out _);
                return false;
            }
            catch (Exception ex)
            {
                // 预热失败绝不影响正确性：真正要用时还会再走一遍这条路（只是慢）
                LogManager.Debug($"[编译] 预热失败（不影响功能，只是首次唤出会现编）：{id} — {ex.Message}");
                return false;
            }
        }

        /// <summary>类型级黑名单：命中即整类型拦截（进程/反射/Interop/WMI/注册表/AD/剪贴板/写流/输入注入）。</summary>
        /// <summary>公开判定：某宿主类型是否已被沙箱拦截（供"API 面快照守卫"测试复用）。</summary>
        public static bool IsHostTypeBlocked(string typeName) => IsBlockedTypeName(typeName ?? "");

        private static bool IsBlockedTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            string[] prefixes =
            {
                // ===== BCL：进程 / 反射 / 原生 / WMI / 注册表 / 动态加载 / 反序列化 =====
                "System.Diagnostics.Process", "System.Reflection", "System.Runtime.InteropServices",
                "System.Runtime.Loader", "System.Management", "System.DirectoryServices",
                "System.Runtime.Serialization",
                // ★ 精确拦截注册表（不用 Microsoft.Win32 前缀：会误伤 WPF 的 OpenFileDialog/SaveFileDialog）
                "Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey", "Microsoft.Win32.SafeHandles",
                // ===== 动态执行 / 进程终止 / 动态类型构造（Activator 可凭字符串类型名造任意对象）=====
                "System.AppDomain", "System.Activator",
                // ★ Clipboard 不硬拦（权限声明类，见 CheckSandbox 注释）
                // ===== 文件写 / 文件系统信息 / 内存映射 / 独立存储 / 解包落盘 =====
                "System.IO.FileStream", "System.IO.StreamWriter", "System.IO.BinaryWriter",
                "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileSystemInfo",
                "System.IO.RandomAccess", "System.IO.MemoryMappedFiles", "System.IO.IsolatedStorage",
                "System.IO.Compression.ZipFile", "System.IO.Compression.ZipFileExtensions",
                // ===== 无类型安全 / 表达式树按名调用 / 运行时编译（研究补充：绕过反射限制的替代通道）=====
                //   · Unsafe 类可做无边界内存读写（CLR 不做检查）→ 绕过一切托管层黑名单；
                //   · Expression.Call(Type, string methodName, ...) 能按"方法名"调用，无需 GetMethod；
                //   · ObjectDataProvider 是公开的 .NET gadget 类（XAML 反序列化链常用）；
                //   · CodeDom/CSharpCodeProvider、Microsoft.CSharp 运行时绑定器属动态执行通道。
                "System.Runtime.CompilerServices.Unsafe",
                "System.Linq.Expressions",
                "System.Windows.Data.ObjectDataProvider",
                "System.CodeDom",
                "Microsoft.CSharp",
                // ===== 凭证 / 输入注入 / 网络监听 / XAML 运行时解析（gadget 入口）=====
                "System.Security.Cryptography.ProtectedData",
                "System.Windows.Forms.SendKeys",
                "System.Net.HttpListener",
                "System.Windows.Markup.XamlReader", "System.Xaml",
                "System.Diagnostics.TextWriterTraceListener", "System.Diagnostics.TraceListener",
                "System.Diagnostics.EventLog", "System.Diagnostics.PerformanceCounter",
                // ===== 宿主特权 API（第二道防线）=====
                // 市场代码要用这些能力 → 走窄接口 ShoreHue.UI.Widgets.HostCapabilities（经校验）
                "ShoreHue.Infrastructure.WinApi.UninstallHelper",
                "ShoreHue.Infrastructure.WinApi.UpdateService",
                "ShoreHue.Infrastructure.WinApi.DefenderScanner",
                "ShoreHue.Infrastructure.WinApi.MediaKeyHelper",
                "ShoreHue.Infrastructure.WinApi.SelectedTextCapture",
                "ShoreHue.Infrastructure.WinApi.CursorOutputService",
                "ShoreHue.Infrastructure.WinApi.WindowCaptureService",
                "ShoreHue.Infrastructure.WinApi.RecentAppTracker",
                "ShoreHue.Infrastructure.WinApi.WebFavoriteManager",
                "ShoreHue.Infrastructure.WinApi.NotificationCenterReader",
                "ShoreHue.Infrastructure.WinApi.WindowAction",
                "ShoreHue.Infrastructure.WinApi.SystemLauncher",
                "ShoreHue.Infrastructure.WinApi.SystemToast",
                "ShoreHue.Infrastructure.WinApi.JumpListManager",
                "ShoreHue.Infrastructure.WinApi.JumpListCommand",
                "ShoreHue.Infrastructure.WinApi.MouseHookService",
                "ShoreHue.Infrastructure.WinApi.WindowEventHook",
                "ShoreHue.Core.Services.Ai.AiSettingsStore",
                "ShoreHue.Core.Services.Ai.AiSettings",
                "ShoreHue.Core.Services.Ai.AiSessionStore",
                "ShoreHue.Core.Services.System.TrayIconManager",
                "ShoreHue.Core.Infrastructure.Service.ServiceManager",
                "ShoreHue.Core.Services.Configuration.SettingsManager",
                // ★ 配置文件读写封装：`SettingsFileManager.Save(data, path)` 是**公开静态**且收任意路径
                //   —— 不拦就等于给了一条"绕过 File.Write* 白名单"的任意写文件通道。
                "ShoreHue.Core.Services.SettingsFileManager",
                // ★ 预设读写：SaveFull/SavePartial 会往 Presets 目录写文件，ApplyPreset 会改写配置。
                "ShoreHue.Core.Services.Configuration.PresetManager",
                // ★ 通知监听：暴露用户全部通知内容 + 监听开关（能力层 HostCapabilities.Notifications 已有替代）
                "ShoreHue.Infrastructure.WinApi.ToastMonitor",
                // ★ 窗口枚举 + 句柄→进程路径（能力面）
                "ShoreHue.Infrastructure.WinApi.WindowListProvider",
                // ★ 宿主 AI 传输实现（插件走 HostCapabilities.AskAiAsync）
                "ShoreHue.Core.Services.Ai.AiChatClient",
                "ShoreHue.UI.Seabed.",
                "ShoreHue.UI.Settings.",
                "ShoreHue.UI.Main.",
                "ShoreHue.UI.Widgets.Dynamic.WidgetCompiler",
                "ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore",
                // ★ 插件运行时守卫（安全模式/熔断）：绝不允许插件解除自己的熔断或陷害别的插件
                "ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard"
            };
            foreach (var p in prefixes)
            {
                if (typeName.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsBlockedTypeSymbol(ITypeSymbol ts)
        {
            var t = ts;
            while (t != null)
            {
                if (IsBlockedTypeName(t.ToString() ?? "")) return true;
                t = t.BaseType;
            }
            return false;
        }

        /// <summary>
        /// 成员级判定：对「可读类型」改为**白名单放行、其余全部拦截**（fail-closed）。
        /// ★ 为什么用白名单而不是黑名单：黑名单必漏 —— File.CreateText / File.OpenWrite /
        ///   File.Open(..., FileMode.Create) 这些写通道都不在名单里 → 实测可任意写文件
        ///   （再用 Stream/TextWriter 基类声明一下，连类型级拦截都能绕过）。
        /// </summary>
        private static bool IsBlockedMember(string typeName, string member)
        {
            if (typeName == "System.IO.File") return !FileAllowedMembers.Contains(member);
            if (typeName == "System.IO.Directory") return !DirectoryAllowedMembers.Contains(member);
            if (typeName == "System.Environment") return !EnvironmentAllowedMembers.Contains(member);
            if (typeName == "System.Type") return !TypeAllowedMembers.Contains(member);
            if (typeName == "System.Reflection.Assembly")
            {
                // ★ 动态加载/实例化入口
                return member is "CreateInstance" or "GetType" or "GetTypes" or "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom";
            }
            if (typeName == "System.Delegate") return member is "CreateDelegate";
            return false;
        }

        // ===== 只读/无副作用白名单（其余成员一律视为危险）=====
        private static readonly HashSet<string> FileAllowedMembers = new(StringComparer.Ordinal)
        {
            "Exists", "ReadAllText", "ReadAllBytes", "ReadAllLines", "ReadLines",
            "ReadAllTextAsync", "ReadAllBytesAsync", "ReadAllLinesAsync",
            "OpenRead", "OpenText",
            "GetAttributes", "GetCreationTime", "GetCreationTimeUtc",
            "GetLastAccessTime", "GetLastAccessTimeUtc", "GetLastWriteTime", "GetLastWriteTimeUtc",
        };

        private static readonly HashSet<string> DirectoryAllowedMembers = new(StringComparer.Ordinal)
        {
            "Exists", "GetFiles", "GetDirectories", "GetFileSystemEntries",
            "EnumerateFiles", "EnumerateDirectories", "EnumerateFileSystemEntries",
            "GetParent", "GetCurrentDirectory", "GetLogicalDrives",
            "GetCreationTime", "GetLastWriteTime", "GetLastAccessTime",
        };

        private static readonly HashSet<string> EnvironmentAllowedMembers = new(StringComparer.Ordinal)
        {
            "GetEnvironmentVariable", "GetEnvironmentVariables", "GetFolderPath", "GetLogicalDrives",
            "SpecialFolder",   // 嵌套枚举（Environment.SpecialFolder.Xxx）是类型引用，非危险成员
            "ExpandEnvironmentVariables", "NewLine", "ProcessId", "MachineName", "UserName",
            "OSVersion", "Is64BitOperatingSystem", "TickCount", "TickCount64",
            "CurrentDirectory", "SystemDirectory", "ProcessorCount",
        };

        private static readonly HashSet<string> TypeAllowedMembers = new(StringComparer.Ordinal)
        {
            "Name", "FullName", "Namespace", "ToString", "BaseType", "DeclaringType",
            "IsEnum", "IsValueType", "IsInterface", "IsAbstract", "IsPublic", "IsArray",
            "IsGenericType", "IsAssignableFrom", "IsInstanceOfType", "AssemblyQualifiedName",
            "GetElementType", "GetGenericArguments", "GetGenericTypeDefinition", "GetArrayRank",
            "GetHashCode", "Equals",
        };

        // ===== 组合规则用的能力识别（语义级，与书写方式无关）=====
        private static readonly string[] NetworkTypePrefixes =
        {
            "System.Net.Http.HttpClient", "System.Net.Http.HttpMessageInvoker",
            "System.Net.Http.HttpRequestMessage", "System.Net.WebClient", "System.Net.WebRequest",
            "System.Net.HttpWebRequest", "System.Net.Sockets.Socket", "System.Net.Sockets.TcpClient",
            "System.Net.Sockets.UdpClient", "System.Net.Sockets.NetworkStream",
            // ★ DNS 也是一条真实的外带通道：`Dns.GetHostAddresses(File.ReadAllText(p))` 会把内容
            //   编码进查询域名发出去，而它既不是 Socket 也不是 HttpClient —— 不列进来，
            //   数据流层就判不出"本地数据 → 网络出口"，整个净白/漏拦都成立。
            "System.Net.Dns",
        };

        private static bool IsNetworkTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            foreach (var p in NetworkTypePrefixes)
                if (typeName.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>文件读取能力（与网络组合 = 外泄）。</summary>
        private static bool IsFileReadMember(string typeName, string member)
        {
            if (typeName == "System.IO.File")
                return member.StartsWith("Read", StringComparison.Ordinal) || member is "OpenRead" or "OpenText";
            if (typeName is "System.IO.StreamReader" or "System.IO.TextReader") return true;
            if (typeName == "System.IO.Directory")
                return member.StartsWith("Get", StringComparison.Ordinal) || member.StartsWith("Enumerate", StringComparison.Ordinal);
            return false;
        }

        /// <summary>剪贴板**读取**能力（写入不算；与网络组合 = 剪贴板内容外泄）。</summary>
        private static bool IsClipboardReadMember(string typeName, string member)
        {
            if (typeName == "System.Windows.Clipboard")
                return member.StartsWith("Get", StringComparison.Ordinal) || member.StartsWith("Contains", StringComparison.Ordinal);
            if (typeName.EndsWith("ClipboardManager", StringComparison.Ordinal) ||
                typeName.EndsWith("IClipboardService", StringComparison.Ordinal))
                return member is "History" or "CaptureClipboardNow";
            return false;
        }

        // ★ 沙箱结果缓存：源码哈希 → 拦截错误文本。沙箱检查是纯函数（同样源码结果不变），
        //   WidgetSwitcher 在面板激活/设置变更时对同一批插件反复调用——无缓存时每次全量
        //   Roslyn 编译 + 语义遍历（实测 150-350ms/个，12 个插件一次重建 = 2-4s UI 线程冻结）。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sandboxCache = new();

        /// <summary>沙箱规则版本：规则变更时 +1，旧缓存自动失效（避免升级后仍用旧结论放行）。</summary>
        private const string SandboxVersion = "v3";   // v3：补出站通道（Mail/Ping/WebSocket/WPF 媒体/宿主能力）+ foreach 污点传播

        /// <summary>沙箱缓存上限（防止海量不同源码把缓存撑爆）。</summary>
        private const int SandboxCacheLimit = 256;

        /// <summary>沙箱校验并汇总为错误文本（非空 = 有被拦截项，编译前必须先拒绝）。单文本重载（兼容旧调用）。</summary>
        public static string SandboxErrors(string source) => SandboxErrors(source, "");

        /// <summary>
        /// ★ 统一沙箱闸门：csharp = **将要被编译/执行的 C# 全部文本**；markup = **将要被 XamlReader.Parse 的 XAML**。
        /// ★★ 调用方必须传"真正会跑的那份文本"：v2 之前 WidgetSwitcher 只扫 plugin.Source，
        ///    而 XAML 形态实际编译执行的是 .xaml.cs（甚至完全没有 main.cs）——检查 A 运行 B 就是绕过。
        /// 返回非空 = 拒绝编译（文本级 + 符号级 + XAML 级三重合并）。
        /// </summary>
        public static string SandboxErrors(string csharp, string markup)
        {
            string cs = csharp ?? "";
            string xaml = markup ?? "";
            string hash = ComputeHash(SandboxVersion + "\u0001" + cs + "\u0002" + xaml);
            if (_sandboxCache.TryGetValue(hash, out var cached)) return cached;

            // 文本层：始终跑（廉价粗筛，能挡住明显危险串）
            var blocked = CheckSandbox(cs);

            // ★ 语义层只在"源码本身能编译"时参与：
            //   编译不过的插件根本不会被加载执行（正式编译会拒绝它），此时符号解析必然失败，
            //   旧行为会把一个拼错的变量名报成"疑似 dynamic 绕过"，既误导用户又毫无安全收益。
            var flow = CheckDataFlow(cs, out bool compiles);
            if (compiles)
            {
                blocked.AddRange(CheckSandboxSymbols(cs));   // 类型/成员黑名单 + 粗筛组合规则
                // ★ S1：数据流可用 → 用精确结论替换粗筛组合规则
                //   （粗筛只看"读和发是否都出现"，正常插件"读本地配置 + 查天气"会被误拦；
                //     数据流看的是"读到的值是否真的流向网络"，这才是有意义的判据）
                blocked.RemoveAll(b => b.Contains("文件读取 + 网络") || b.Contains("剪贴板读取 + 网络"));
                blocked.AddRange(flow);
            }

            blocked.AddRange(CheckXamlDialect(xaml));   // ★ 受限 XAML 方言（结构白名单）
            blocked.AddRange(CheckSandboxMarkup(xaml)); // 旧文本规则（纵深防御）
            blocked.AddRange(CheckSandbox(xaml));       // 标记文本里的危险字面串

            string result = blocked.Count == 0
                ? ""
                : "市场来源代码被沙箱拦截，禁止以下能力：" + System.Environment.NewLine +
                  "  - " + string.Join(System.Environment.NewLine + "  - ", blocked.Distinct());

            if (_sandboxCache.Count > SandboxCacheLimit) _sandboxCache.Clear();
            _sandboxCache[hash] = result;
            return result;
        }

        /// <summary>
        /// 编译配置代码（赋值语句版）并返回可调用的 Apply 委托。
        /// 海床里保存的单预设（Kind=Config）源码形如：
        ///   public static class ConfigCode { public static void Apply(SettingsData data) { data.X = 值; ... } }
        /// 编译后反射找到静态 ConfigCode.Apply(SettingsData) 并包装为委托。
        /// 调用方传入一个 SettingsData 实例，Apply 委托会就地修改其字段值（写回由调用方负责）。
        /// 编译失败返回 null，error 含诊断信息。
        /// </summary>
        public static Action<ShoreHue.Core.Services.Configuration.SettingsData>? CompileConfigApply(string source, out string error)
        {
            error = "";
            try
            {
                string wrapped = "using ShoreHue.Core.Services.Configuration;" + System.Environment.NewLine + (source ?? "");
                using var ms = new MemoryStream();
                if (!TryEmit("config_" + Guid.NewGuid().ToString("N").Substring(0, 8), wrapped, ms, out error))
                {
                    return null;
                }

                ms.Position = 0;
                var asm = AssemblyLoadContext.Default.LoadFromStream(ms);
                var type = asm.GetTypes()
                    .FirstOrDefault(t => t.Name == "ConfigCode" && t.IsAbstract && t.IsSealed && t.IsPublic);
                if (type == null)
                {
                    error = "未找到 public static class ConfigCode。请保留模板中的 ConfigCode 类结构。";
                    return null;
                }

                var method = type.GetMethod("Apply",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null,
                    new[] { typeof(ShoreHue.Core.Services.Configuration.SettingsData) },
                    null);
                if (method == null)
                {
                    error = "未找到 public static void Apply(SettingsData data) 方法。";
                    return null;
                }

                return data => method.Invoke(null, new object[] { data });
            }
            catch (Exception ex)
            {
                error = "编译异常：" + ex.Message;
                return null;
            }
        }

        /// <summary>Roslyn 编译到内存流。返回是否成功，失败时 errors 含诊断信息。</summary>
        private static bool TryEmit(string id, string source, MemoryStream ms, out string errors)
            => TryEmit(id, source, ms, null, out errors);

        /// <summary>Roslyn 编译到内存流。asmName 非空时用它作程序集名（缓存路径要求"同名即同内容"）。</summary>
        private static bool TryEmit(string id, string source, MemoryStream ms, string? asmName, out string errors)
        {
            errors = "";
            var tree = CSharpSyntaxTree.ParseText(source);

            var refs = new List<MetadataReference>();
            var trusted = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
                .Split(Path.PathSeparator);
            foreach (var p in trusted)
            {
                // 尽力而为（每次编译都会走）：个别程序集读不出元数据不影响其余；缺引用会变成 Roslyn 编译诊断交给调用方
                try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { /* 见上：失败会由编译诊断暴露 */ }
            }
            try { refs.Add(MetadataReference.CreateFromFile(typeof(IWidget).Assembly.Location)); }
            catch (Exception ex)
            {
                LogManager.Warning($"[编译] 添加宿主程序集引用失败（本次编译将失败）：{ex.Message}");
            }

            // ★ 程序集名：调用方给了就用（id + 内容哈希，同名即同内容，可复用/可缓存）；
            //   没给（如配置代码编译）就退回随机名，避免 Default ALC 报"同名程序集已加载"。
            string finalName = asmName ?? ("ShoreHue.Widget." + id + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var compilation = CSharpCompilation.Create(
                finalName,
                new[] { tree },
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = compilation.Emit(ms);
            if (!result.Success)
            {
                errors = string.Join(Environment.NewLine, result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Take(10)
                    .Select(d => d.ToString()));
                return false;
            }
            return true;
        }
    }
}