using System;
using System.IO;
using System.Linq;
using ShoreHue.UI.Widgets.Dynamic;

namespace MarketValidator
{
    /// <summary>
    /// CI 用：编译验证 market/packages/**/main.cs（复用 WidgetCompiler 的 Roslyn 编译 + 沙箱检查）。
    /// 任一包编译失败 → exit 1（挂掉 PR），保证市场包质量。
    /// </summary>
    public static class Program
    {
        [System.STAThread]
        public static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "market");
            // 定位仓库根：从 exe 目录向上找 market/（CI 里 cwd 为仓库根）
            string cwd = Directory.GetCurrentDirectory();
            string marketDir = Directory.Exists(Path.Combine(cwd, "market")) ? Path.Combine(cwd, "market") : root;
            if (!Directory.Exists(marketDir))
            {
                Console.WriteLine("market 目录不存在: " + marketDir);
                return 1;
            }

            string packagesDir = Path.Combine(marketDir, "packages");
            var files = Directory.GetFiles(packagesDir, "main.cs", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.WriteLine("未发现 market/packages/**/main.cs（空市场，视为通过）");
                return 0;
            }

            // ★ v2：外来包一律按"客户端真实口径"校验（不再有 official 自证豁免）。
            //   先检查"只有 XAML、没有 main.cs"的包 —— 上架规则要求市场包必须带 main.cs（便于 CI 与客户端同判）；
            //   注意：客户端**并不禁用**外来来源的 XAML（走受限方言校验），这里是"上架约定"，不是安全边界。
            foreach (var pkgDir in Directory.GetDirectories(packagesDir))
            {
                bool hasMain = File.Exists(Path.Combine(pkgDir, "main.cs"));
                bool hasXaml = Directory.GetFiles(pkgDir, "*.xaml").Length > 0;
                if (!hasMain && hasXaml)
                {
                    Console.WriteLine("FAIL  " + pkgDir.Replace(cwd + Path.DirectorySeparatorChar, "") +
                        " [形态] 外来包必须提供 main.cs（XAML 形态仅限本地/可信来源）");
                    return 1;
                }
            }

            // ★ 初始化一个**隔离的**宿主服务环境：XAML 形态包在客户端是由宿主
            //   `Activator` 实例化并在构造函数里经 HostCapabilities 取服务的，
            //   所以"能不能编译"之外还要"能不能构造出来"。
            //   数据根必须指到临时目录 —— 否则 SettingsManager 初始化会把开发者的真实 config.json 卷进来。
            string sandboxRoot = Path.Combine(Path.GetTempPath(), "sh_marketval_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxRoot);
            ShoreHue.Infrastructure.Utils.AppPaths.TestDataRoot = sandboxRoot;
            var hostSettings = new ShoreHue.Core.Services.Configuration.SettingsManager();
            var services = ShoreHue.Core.Infrastructure.Service.ServiceManager.Instance
                .Register(hostSettings,
                          new ShoreHue.Core.Services.NoteManager(hostSettings),
                          new ShoreHue.Core.Services.ClipboardManager(hostSettings));
            services.InitializeAll();

            // ★ 模拟真实运行环境：加载主题资源（模板构造函数会 FindResource 按钮样式）
            var app = new System.Windows.Application();
            try { app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml") }); } catch { }
            try { app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/AppIcons.xaml") }); } catch { }

            int pass = 0, fail = 0;

            // ★ 借鉴 Windhawk 的市场流程纪律（见 docs\对比-Windhawk市场机制.md 第五节 A 组；
            //   只借规则、不抄代码）。四项里的一次性覆盖：
            //     ① manifest schema（必填/类型/枚举/长度/版本/apiVersion）
            //     ② 权限名必须真实存在 —— 对应他们"用第三方索引校验你钩的 DLL 真存在"；
            //        AI 生成的包常写出宿主不认识的权限名，后果是能力被**静默忽略**
            //     ③ 隐形字符与编码（零宽空格/双向控制符/NBSP/BOM）—— 他们逐行查，我们也该查：
            //        我们的用户大量用 AI 生成代码，而这类字符**人眼完全看不出来**
            //     ④ license 合法性（未声明按 MIT，只提示不拦，与 Windhawk 一致）
            foreach (var pkgDir in Directory.GetDirectories(packagesDir))
            {
                string relId = Path.GetRelativePath(packagesDir, pkgDir).Replace('\\', '/');
                string mfPath = Path.Combine(pkgDir, "manifest.json");
                string? mfJson = File.Exists(mfPath) ? File.ReadAllText(mfPath) : null;

                var check = ShoreHue.UI.Seabed.MarketManifestRules.ValidateManifest(mfJson, relId);
                foreach (string note in check.Notes)
                    Console.WriteLine("NOTE  market/packages/" + relId + " — " + note);
                foreach (string e in check.Errors)
                {
                    Console.WriteLine("FAIL  market/packages/" + relId + " [清单] " + e);
                    fail++;
                }

                foreach (string tf in ShoreHue.UI.Seabed.MarketManifestRules.TextFilesOf(pkgDir))
                {
                    foreach (string e in ShoreHue.UI.Seabed.MarketManifestRules.ValidateText(
                                 Path.GetFileName(tf), File.ReadAllText(tf)))
                    {
                        Console.WriteLine("FAIL  " + tf.Replace(cwd + Path.DirectorySeparatorChar, "") + " [编码] " + e);
                        fail++;
                    }
                }
            }

            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                string source = File.ReadAllText(f);
                string id = Path.GetFileName(Path.GetDirectoryName(f) ?? "pkg");

                // ★ 沙箱：**所有**市场包都按客户端同一闸门校验（文本 + 符号 + 数据流 + XAML 标记）。
                //   旧版按包内自述的 official:true 跳过检查 —— 那等于"包自己给自己发免检牌"。
                //
                //   ★★ 必须覆盖**包内全部会真正编译/解析的文本**，且与客户端同参数形态。
                //   客户端闸门（WidgetSwitcher / PanelContentController）是按**实际编译形态**取文本的：
                //     · XAML 形态（同时有 .xaml 与 .xaml.cs）→ SandboxErrors(xamlCs, xaml)
                //     · 纯代码形态                          → SandboxErrors(source, "")
                //   而旧版校验器只取 `*.xaml` 的第一份当 markup、**从不看 .xaml.cs** ——
                //   于是 XAML 形态包把恶意代码放进代码后置（或第二份 .xaml）就能让 CI 变绿，
                //   "CI 与客户端同判、无免检豁免"这条承诺实际上不成立。这里按同一口径补齐。
                string pkgDir = Path.GetDirectoryName(f) ?? "";
                var xamlFiles = SafeList(pkgDir, "*.xaml");
                var xamlCsFiles = SafeList(pkgDir, "*.xaml.cs");
                string xamlAll = ReadAllText(xamlFiles);
                string xamlCsAll = ReadAllText(xamlCsFiles);
                bool xamlForm = xamlFiles.Count > 0 && xamlCsFiles.Count > 0;

                string sandboxErr = xamlForm
                    ? WidgetCompiler.SandboxErrors(xamlCsAll, xamlAll)
                    : WidgetCompiler.SandboxErrors(source, "");
                if (!string.IsNullOrEmpty(sandboxErr))
                {
                    Console.WriteLine("FAIL  " + f.Replace(cwd + Path.DirectorySeparatorChar, "") + " [沙箱拦截] " + sandboxErr.Replace(Environment.NewLine, " "));
                    fail++;
                    continue;
                }

                var (_, err) = WidgetCompiler.Compile(id, source);

                // ★ XAML 形态还要**真正编译一遍**：沙箱/方言只保证"结构上是界面"，
                //   而事件绑定、控件类型推断、标记扩展这些只有过一遍编译器才知道对不对。
                //   以前 CI 完全没编译 XAML，于是"main.cs 绿、XAML 形态装上去是坏的"可以一路合进来
                //   （真实案例：calculator/clipboard 包的事件元素没有 x:Name，
                //    生成的事件绑定挂到了无关元素上 → CS1061 或点了没反应）。
                if (xamlForm)
                {
                    var (widget, xerr) = WidgetCompiler.CompileXaml("market_" + id, xamlAll, xamlCsAll);
                    if (widget == null)
                    {
                        Console.WriteLine("FAIL  " + f.Replace(cwd + Path.DirectorySeparatorChar, "") + " [XAML 编译] " + xerr.Split('\n')[0]);
                        fail++;
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(err))
                {
                    Console.WriteLine("PASS  " + f.Replace(cwd + Path.DirectorySeparatorChar, ""));
                    pass++;
                }
                else
                {
                    Console.WriteLine("FAIL  " + f.Replace(cwd + Path.DirectorySeparatorChar, "") + " [编译] " + err.Split('\n')[0]);
                    fail++;
                }
            }
            Console.WriteLine(fail == 0
                ? "MARKET OK (" + pass + " 包可编译)"
                : "MARKET FAILED: " + fail + " 包编译失败");
            return fail == 0 ? 0 : 1;
        }

        // ★ v2 已移除 IsOfficial：不再有任何"包内自述即免检"的豁免路径。

        /// <summary>把给定文件列表的内容拼起来（与客户端"检查真正会被编译的文本"同口径）。</summary>
        private static string ReadAllText(List<string> files)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in files)
            {
                try { sb.AppendLine(File.ReadAllText(f)); }
                catch { /* 单个文件读不出不影响其余（缺失会在后续编译/校验阶段暴露） */ }
            }
            return sb.ToString();
        }

        /// <summary>枚举目录下匹配的文件（目录不存在/无权限 → 空列表，不抛）。</summary>
        private static List<string> SafeList(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
            catch { return new List<string>(); }
        }
    }
}
