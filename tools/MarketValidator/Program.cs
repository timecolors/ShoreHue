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

            var files = Directory.GetFiles(Path.Combine(marketDir, "packages"), "main.cs", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.WriteLine("未发现 market/packages/**/main.cs（空市场，视为通过）");
                return 0;
            }

            // ★ 模拟真实运行环境：加载主题资源（模板构造函数会 FindResource 按钮样式）
            var app = new System.Windows.Application();
            try { app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml") }); } catch { }
            try { app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ShoreHue;component/src/UI/Theme/AppIcons.xaml") }); } catch { }

            int pass = 0, fail = 0;
            foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
            {
                string source = File.ReadAllText(f);
                string id = Path.GetFileName(Path.GetDirectoryName(f) ?? "pkg");
                var (_, err) = WidgetCompiler.Compile(id, source);
                // ★ 沙箱：第三方市场包必须过沙箱；官方验证包（official:true，仓库维护者审查背书）只验证编译
                string? manifestPath = Path.Combine(Path.GetDirectoryName(f) ?? "", "manifest.json");
                bool official = IsOfficial(manifestPath);
                string sandboxErr = official ? "" : WidgetCompiler.SandboxErrors(source);
                if (!string.IsNullOrEmpty(sandboxErr))
                {
                    Console.WriteLine("FAIL  " + f.Replace(cwd + Path.DirectorySeparatorChar, "") + " [沙箱拦截] " + sandboxErr.Replace(Environment.NewLine, " "));
                    fail++;
                    continue;
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

        /// <summary>读取同目录 manifest.json 的 official 字段（官方内置包豁免沙箱，只验证编译）。</summary>
        private static bool IsOfficial(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath)) return false;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                return doc.RootElement.TryGetProperty("official", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            catch { return false; }
        }
    }
}
