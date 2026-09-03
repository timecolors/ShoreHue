using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;

// 一次性工具：把内置小组件模板导出为市场包（market/packages/<id>/）
var names = new Dictionary<string, (string Name, string Desc)>
{
    ["widget-timer"] = ("计时器", "ShoreHue内置计时器：倒计时 / 正计时 / 闹钟。"),
    ["widget-calculator"] = ("计算器", "ShoreHue内置计算器：标准 / 科学 / 程序员。"),
    ["widget-textai"] = ("划词翻译", "划词翻译：选中文本翻译 / 总结 / 解释。"),
    ["widget-clipboard"] = ("剪贴板", "ShoreHue内置剪贴板历史：复制记录 / 搜索 / 固定。"),
    ["widget-note"] = ("便签", "ShoreHue内置便签：多色 / 编辑 / 置顶。"),
};

string market = Path.Combine(Environment.CurrentDirectory, "market", "packages");
var packages = new List<object>();
foreach (var kv in BuiltinFeatureSources.Sources)
{
    if (!kv.Key.StartsWith("widget-")) continue;
    if (!names.TryGetValue(kv.Key, out var meta)) continue;
    string id = kv.Key.Substring("widget-".Length);
    string dir = Path.Combine(market, id);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "main.cs"), kv.Value, new System.Text.UTF8Encoding(false));
    var manifest = new Dictionary<string, object?>
    {
        ["id"] = id,
        ["name"] = meta.Name,
        ["kind"] = "Widget",
        ["category"] = "小组件",
        ["version"] = "1.0.0",
        ["author"] = "timecolors",
        ["description"] = meta.Desc,
        ["baseType"] = "Widget",
        ["parentKey"] = "panel-widgets",
        ["sourceKey"] = kv.Key,
        ["permissions"] = WidgetPermissions.Detect(kv.Value),
        ["apiVersion"] = 1,
        ["official"] = true
    };
    File.WriteAllText(Path.Combine(dir, "manifest.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
        new System.Text.UTF8Encoding(false));
    packages.Add(manifest);
    Console.WriteLine("EXPORTED " + id + " (" + kv.Value.Length + " chars)");
}

// 生成 index.json
// ★ 警告：本工具仅为初始批量导出设计；重跑会覆盖 index.json 丢弃已发布第三方包与 official 标记。
//   后续维护请用应用内放流（保留字段）或手工编辑 index.json。
var index = new Dictionary<string, object?>
{
    ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
    ["apiVersion"] = 1,
    ["marketBase"] = "https://cdn.jsdelivr.net/gh/timecolors/ShoreHue@master/market",
    ["packages"] = packages
};
File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "market", "index.json"),
    JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }),
    new System.Text.UTF8Encoding(false));
Console.WriteLine("INDEX updated: " + packages.Count + " packages");
