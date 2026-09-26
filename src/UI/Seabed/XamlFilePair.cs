// ==================== 完全编程的「成对文件」判定：<名字>.xaml + <名字>.xaml.cs ====================
//
// 为什么值得单独一个纯类（真机反馈查出来的 bug）：
//   这段判据原来在页面里写了两遍（`.xaml` 一支、`.xaml.cs` 一支），其中 `.xaml.cs` 那支写成
//   `Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".xaml")` —— 而对
//   `calculator.xaml.cs`，`GetFileNameWithoutExtension` 返回的是 **`calculator.xaml`**（只剥掉最后
//   一个扩展名 `.cs`），拼出来是 `calculator.xaml.xaml`：文件当然不存在。
//   结果就是"点左侧 .xaml.cs → 右上 XAML 框空着；点 .xaml → 两个都加载"。
//
//   判据抽成纯函数后，这条**能被断言钉住**，不必等人去点界面才发现。
//   （同样的坑在项目里并不罕见：凡是对 `.xaml.cs` 用 `GetFileNameWithoutExtension` 的地方都错。）

using System;
using System.IO;

namespace ShoreHue.UI.Seabed
{
    internal static class XamlFilePair
    {
        internal const string ViewExt = ".xaml";
        internal const string CodeExt = ".xaml.cs";

        /// <summary>是不是界面文件（`.xaml`，**且不是** `.xaml.cs`）。</summary>
        internal static bool IsView(string fileName)
            => fileName.EndsWith(ViewExt, StringComparison.OrdinalIgnoreCase)
               && !fileName.EndsWith(CodeExt, StringComparison.OrdinalIgnoreCase);

        /// <summary>是不是代码后置（`.xaml.cs`）。</summary>
        internal static bool IsCodeBehind(string fileName)
            => fileName.EndsWith(CodeExt, StringComparison.OrdinalIgnoreCase);

        /// <summary>成对的共同名字：`calculator.xaml` 与 `calculator.xaml.cs` 都得到 `calculator`。</summary>
        internal static string Stem(string filePath)
        {
            string name = Path.GetFileName(filePath);
            if (IsCodeBehind(name)) return name.Substring(0, name.Length - CodeExt.Length);
            return Path.GetFileNameWithoutExtension(name);
        }

        /// <summary>
        /// 由**任意一半**推出成对的两个路径（不检查是否存在 —— 存在性由调用方按需判断，
        /// 因为"只写了 .xaml 还没写 .xaml.cs"是合法的编辑中间态）。
        /// 不认识的扩展名返回 (null, null)。
        /// </summary>
        internal static (string? View, string? Code) Resolve(string filePath)
        {
            string name = Path.GetFileName(filePath);
            if (!IsView(name) && !IsCodeBehind(name)) return (null, null);
            string dir = Path.GetDirectoryName(filePath) ?? "";
            string stem = Stem(filePath);
            return (Path.Combine(dir, stem + ViewExt), Path.Combine(dir, stem + CodeExt));
        }
    }
}
