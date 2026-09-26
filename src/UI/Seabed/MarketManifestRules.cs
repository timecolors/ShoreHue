// ==================== 市场包的「清单与文本」规则（纯函数，可单测） ====================
//
// 借鉴自 Windhawk 的市场流程（见 docs\对比-Windhawk市场机制.md 第五节 A 组），只借规则、不抄代码：
//   · manifest schema 校验（必填/类型/枚举/长度）—— 他们校验 README/Settings 块的结构
//   · 引用的能力名必须真实存在 —— 他们用第三方索引校验"你钩的那个 DLL 真的存在"，
//     我们对应的是**权限名必须在宿主白名单里**（AI 生成的包常写出不存在的权限名 → 运行时静默失效）
//   · 隐形字符与编码 —— 他们逐行查 Cf/异常空白/控制字符；我们的用户大量用 AI 生成代码，更容易中招
//   · license 字段（未声明按 MIT，与 Windhawk 一致；写了但不合法则报错）
//
// 全部是纯函数，因此 MarketValidator（CI 与本地都跑）与测试共用同一份实现。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ShoreHue.UI.Seabed
{
    internal static class MarketManifestRules
    {
        /// <summary>合法的包种类（与客户端按 kind 分流的口径一致）。</summary>
        internal static readonly string[] KnownKinds = { "Widget", "Panel", "Config", "Category", "StatusProvider", "Animation" };

        /// <summary>客户端当前支持的包协议版本；高于它的包客户端会拒绝（防格式演进破坏旧客户端）。</summary>
        internal const int SupportedApiVersion = 1;

        internal const int MinNameLength = 2;
        internal const int MaxNameLength = 40;
        internal const int MaxDescriptionLength = 200;

        /// <summary>未声明许可证时的默认（与 Windhawk 一致：不声明即 MIT）。</summary>
        internal const string DefaultLicense = "MIT";

        /// <summary>常见 SPDX 标识符。★ 离线可校验的白名单 —— CI 不该依赖联网查 spdx.org。</summary>
        internal static readonly HashSet<string> KnownLicenses = new(StringComparer.OrdinalIgnoreCase)
        {
            "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MPL-2.0",
            "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later",
            "LGPL-3.0-only", "AGPL-3.0-only", "Unlicense", "CC0-1.0"
        };

        internal readonly record struct ManifestCheck(List<string> Errors, List<string> Notes)
        {
            internal bool Ok => Errors.Count == 0;
        }

        /// <summary>
        /// 校验一个市场包的 manifest。
        /// <paramref name="expectedId"/> = 包所在目录相对于 `market/packages/` 的路径（含命名空间），
        /// 用于抓"清单里的 id 与它实际放在哪不一致"——那会让客户端安装到一个不存在的位置。
        /// </summary>
        internal static ManifestCheck ValidateManifest(string? json, string expectedId)
        {
            var errors = new List<string>();
            var notes = new List<string>();

            if (string.IsNullOrWhiteSpace(json))
                return new ManifestCheck(new List<string> { "缺少 manifest.json" }, notes);

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(json);
                root = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                return new ManifestCheck(new List<string> { "manifest.json 不是合法 JSON：" + ex.Message }, notes);
            }

            if (root.ValueKind != JsonValueKind.Object)
                return new ManifestCheck(new List<string> { "manifest.json 顶层必须是对象" }, notes);

            string? GetString(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            // ---- id：必填、形态合法、且必须与所在目录一致 ----
            string? id = GetString("id");
            if (string.IsNullOrWhiteSpace(id)) errors.Add("缺少 id");
            else
            {
                if (MarketPackageRules.ValidateIdFormat(id) is string idErr) errors.Add("id 不合法：" + idErr);
                if (!string.Equals(id, expectedId, StringComparison.Ordinal))
                    errors.Add($"id（{id}）与包所在路径（{expectedId}）不一致 —— 客户端会按路径安装，两者必须相同");
            }

            // ---- name：必填、长度、不得含制表符/换行（列表页会错行） ----
            string? name = GetString("name");
            if (string.IsNullOrWhiteSpace(name)) errors.Add("缺少 name");
            else
            {
                if (name.Length < MinNameLength || name.Length > MaxNameLength)
                    errors.Add($"name 长度必须在 {MinNameLength}–{MaxNameLength} 之间（当前 {name.Length}）");
                if (name.Contains('\t') || name.Contains('\n') || name.Contains('\r'))
                    errors.Add("name 不能含制表符或换行");
            }

            // ---- kind：必填、枚举 ----
            string? kind = GetString("kind");
            if (string.IsNullOrWhiteSpace(kind)) errors.Add("缺少 kind");
            else if (!KnownKinds.Contains(kind, StringComparer.Ordinal))
                errors.Add($"kind「{kind}」不在允许集合内：{string.Join(" / ", KnownKinds)}");

            // ---- version：必填、数字与点 ----
            string? version = GetString("version");
            if (string.IsNullOrWhiteSpace(version)) errors.Add("缺少 version");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+(\\.[0-9]+)*$"))
                errors.Add($"version「{version}」只能是数字与点（如 1.0.0）");

            // ---- author：必填（发布时由登录态带出，缺了说明包不是从客户端发的） ----
            if (string.IsNullOrWhiteSpace(GetString("author"))) errors.Add("缺少 author");

            // ---- apiVersion：不得高于客户端支持 ----
            if (root.TryGetProperty("apiVersion", out var api))
            {
                if (api.ValueKind != JsonValueKind.Number || !api.TryGetInt32(out int apiVer))
                    errors.Add("apiVersion 必须是整数");
                else if (apiVer > SupportedApiVersion)
                    errors.Add($"apiVersion={apiVer} 高于客户端支持的 {SupportedApiVersion}（旧客户端会拒绝这个包）");
            }

            // ---- permissions：数组 + **每个名字必须真实存在**（对应 Windhawk 的"目标必须真实存在"） ----
            if (root.TryGetProperty("permissions", out var perms))
            {
                if (perms.ValueKind != JsonValueKind.Array) errors.Add("permissions 必须是数组");
                else
                {
                    foreach (var p in perms.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.String) { errors.Add("permissions 里只能放字符串"); continue; }
                        string? pv = p.GetString();
                        if (string.IsNullOrWhiteSpace(pv) || !ShoreHue.UI.Widgets.Dynamic.WidgetPermissions.IsKnown(pv))
                            errors.Add($"权限名「{pv}」在宿主白名单里不存在（客户端不会识别它，能力会被静默忽略）");
                    }
                }
            }

            // ---- files：必须是非空字符串数组，且不能带路径分隔符（防路径穿越的清单层防线） ----
            if (root.TryGetProperty("files", out var files))
            {
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                    errors.Add("files 必须是非空数组");
                else
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        string? fv = f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                        if (string.IsNullOrWhiteSpace(fv)) { errors.Add("files 里只能放非空字符串"); continue; }
                        if (fv.Contains('/') || fv.Contains('\\') || fv.Contains(".."))
                            errors.Add($"files 里不能出现路径分隔符或 ..（「{fv}」）");
                    }
                }
            }
            else errors.Add("缺少 files（客户端按它逐个拉取文件）");

            // ---- license：可选；写了必须合法；没写按 MIT（与 Windhawk 一致，给个提示不拦） ----
            string? license = GetString("license");
            if (string.IsNullOrWhiteSpace(license))
                notes.Add($"未声明 license，按 {DefaultLicense} 处理（建议在 manifest 里写明）");
            else if (!KnownLicenses.Contains(license))
                errors.Add($"license「{license}」不在已知 SPDX 标识符白名单里");

            // ---- description：可选，长度限制 ----
            string? desc = GetString("description");
            if (desc != null && desc.Length > MaxDescriptionLength)
                errors.Add($"description 过长（{desc.Length} > {MaxDescriptionLength}）");

            return new ManifestCheck(errors, notes);
        }

        /// <summary>
        /// 文本的编码与隐形字符检查（Windhawk 同款，逐行报出码点）。
        /// ★ 为什么值得做：用户大量用 AI 生成代码，AI 输出里混入不可见字符（零宽空格、双向控制符、
        ///   NBSP 等）时人眼**完全看不出来**，但编译器/沙箱匹配会因此行为诡异。
        /// </summary>
        internal static List<string> ValidateText(string displayName, string? text)
        {
            var errors = new List<string>();
            if (text == null) return errors;

            if (text.Length > 0 && text[0] == '\uFEFF')
                errors.Add(displayName + "：文件不能以 UTF-8 BOM 开头");

            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var bad = new List<char>();
                foreach (char c in lines[i])
                {
                    var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                    bool isCf = cat == System.Globalization.UnicodeCategory.Format;                 // 零宽/双向控制
                    bool isOddSpace = cat == System.Globalization.UnicodeCategory.SpaceSeparator && c != ' ';
                    bool isControl = cat == System.Globalization.UnicodeCategory.Control && c != '\t' && c != '\r';
                    if (isCf || isOddSpace || isControl) bad.Add(c);
                }
                if (bad.Count > 0)
                {
                    string codes = string.Join(", ", bad.Distinct().Select(c => $"U+{(int)c:X4}").OrderBy(x => x));
                    errors.Add($"{displayName}:{i + 1} 含 {bad.Count} 个隐形/异常字符（{codes}）—— 要求人工确认");
                }
            }
            return errors;
        }

        /// <summary>包目录里所有应当被检查的文本文件（相对的，排序稳定）。</summary>
        internal static IEnumerable<string> TextFilesOf(string packageDir)
        {
            if (!System.IO.Directory.Exists(packageDir)) yield break;
            foreach (string f in System.IO.Directory.GetFiles(packageDir)
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                string n = System.IO.Path.GetFileName(f);
                if (n.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
        }
    }
}
