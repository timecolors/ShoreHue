using System;
using System.Collections.Generic;
using System.Linq;

namespace ShoreHue.Core.Services.Configuration
{
    /// <summary>
    /// 区域元数据表（★ **唯一真相源**）：16 个屏幕区域（12 边缘 + 4 角）的规范键 → 形状 / 尺寸 / 面板 的读写。
    ///
    /// 为什么要有这张表：这块原本散着**四套字符串 switch** —— 形状一套、尺寸一套、面板一套，
    /// 设置页里还藏着第四份"直接写 RegionPanel_ 字段"的副本；而且键有两套命名（边缘 `Top_Left`、
    /// 四角 `TopLeft`，尺寸字段又是 `UserWidth_Corner_TopLeft`）。任何一处漏改，
    /// 用户看到的就是"某个区域的设置改了不生效"，且没有任何报错。收敛到这里之后，**加区域/改字段只改一处**。
    ///
    /// ★ 不改 `SettingsData` 的字段名：那是落盘格式（存量 config.json 直接反序列化），
    ///   且被配置树覆盖测试 / 字段文档测试守着。本表只做"键 → 字段"的映射，不动存储格式。
    /// </summary>
    public static class RegionTable
    {
        /// <summary>面板未配置时的缺省值（与存量 config.json 的默认一致）。</summary>
        public const string DefaultPanel = "Default";

        /// <summary>形状未配置时的缺省值。</summary>
        public const string DefaultShape = "Default";

        /// <summary>一个区域的元数据与读写入口（委托直接读写 `SettingsData`，不走反射）。</summary>
        public sealed class Region
        {
            /// <summary>规范键：边缘 `Top_Left`，四角 `TopLeft`。</summary>
            public string Key { get; init; } = "";
            /// <summary>是否四角区域（四角没有独立形状字段，见 `Region_Top_Left` 之类只覆盖 12 个边缘）。</summary>
            public bool IsCorner { get; init; }
            /// <summary>兼容别名（例如按字段名写成 `Corner_TopLeft`）。</summary>
            public string[] Aliases { get; init; } = Array.Empty<string>();

            /// <summary>读形状；四角没有形状字段 → 返回 null（调用方按 DefaultShape 处理）。</summary>
            public Func<SettingsData, string?> GetShape { get; init; } = _ => null;
            public Action<SettingsData, string> SetShape { get; init; } = (_, _) => { };
            /// <summary>读用户自定义尺寸（宽, 高）；0 = 未自定义。</summary>
            public Func<SettingsData, (double Width, double Height)> GetSize { get; init; } = _ => (0, 0);
            public Action<SettingsData, double, double> SetSize { get; init; } = (_, _, _) => { };
            /// <summary>读该区域用哪个面板（Default/Taskbar/Widget/...）。</summary>
            public Func<SettingsData, string?> GetPanel { get; init; } = _ => null;
            public Action<SettingsData, string> SetPanel { get; init; } = (_, _) => { };
        }

        /// <summary>16 个区域。顺序 = 设置页里区域列表的顺序（上 → 下 → 左 → 右 → 四角），改顺序会改设置页排布。</summary>
        public static readonly IReadOnlyList<Region> All = new Region[]
        {
            // ---- 12 个边缘区域：形状/尺寸/面板都带下划线键，尺寸字段就是 UserWidth_<键> ----
            new Region { Key = "Top_Left", GetShape = d => d.Region_Top_Left, SetShape = (d, v) => d.Region_Top_Left = v,
                GetSize = d => (d.UserWidth_Top_Left, d.UserHeight_Top_Left), SetSize = (d, w, h) => { d.UserWidth_Top_Left = w; d.UserHeight_Top_Left = h; },
                GetPanel = d => d.RegionPanel_Top_Left, SetPanel = (d, v) => d.RegionPanel_Top_Left = v },
            new Region { Key = "Top_Center", GetShape = d => d.Region_Top_Center, SetShape = (d, v) => d.Region_Top_Center = v,
                GetSize = d => (d.UserWidth_Top_Center, d.UserHeight_Top_Center), SetSize = (d, w, h) => { d.UserWidth_Top_Center = w; d.UserHeight_Top_Center = h; },
                GetPanel = d => d.RegionPanel_Top_Center, SetPanel = (d, v) => d.RegionPanel_Top_Center = v },
            new Region { Key = "Top_Right", GetShape = d => d.Region_Top_Right, SetShape = (d, v) => d.Region_Top_Right = v,
                GetSize = d => (d.UserWidth_Top_Right, d.UserHeight_Top_Right), SetSize = (d, w, h) => { d.UserWidth_Top_Right = w; d.UserHeight_Top_Right = h; },
                GetPanel = d => d.RegionPanel_Top_Right, SetPanel = (d, v) => d.RegionPanel_Top_Right = v },
            new Region { Key = "Bottom_Left", GetShape = d => d.Region_Bottom_Left, SetShape = (d, v) => d.Region_Bottom_Left = v,
                GetSize = d => (d.UserWidth_Bottom_Left, d.UserHeight_Bottom_Left), SetSize = (d, w, h) => { d.UserWidth_Bottom_Left = w; d.UserHeight_Bottom_Left = h; },
                GetPanel = d => d.RegionPanel_Bottom_Left, SetPanel = (d, v) => d.RegionPanel_Bottom_Left = v },
            new Region { Key = "Bottom_Center", GetShape = d => d.Region_Bottom_Center, SetShape = (d, v) => d.Region_Bottom_Center = v,
                GetSize = d => (d.UserWidth_Bottom_Center, d.UserHeight_Bottom_Center), SetSize = (d, w, h) => { d.UserWidth_Bottom_Center = w; d.UserHeight_Bottom_Center = h; },
                GetPanel = d => d.RegionPanel_Bottom_Center, SetPanel = (d, v) => d.RegionPanel_Bottom_Center = v },
            new Region { Key = "Bottom_Right", GetShape = d => d.Region_Bottom_Right, SetShape = (d, v) => d.Region_Bottom_Right = v,
                GetSize = d => (d.UserWidth_Bottom_Right, d.UserHeight_Bottom_Right), SetSize = (d, w, h) => { d.UserWidth_Bottom_Right = w; d.UserHeight_Bottom_Right = h; },
                GetPanel = d => d.RegionPanel_Bottom_Right, SetPanel = (d, v) => d.RegionPanel_Bottom_Right = v },
            new Region { Key = "Left_Top", GetShape = d => d.Region_Left_Top, SetShape = (d, v) => d.Region_Left_Top = v,
                GetSize = d => (d.UserWidth_Left_Top, d.UserHeight_Left_Top), SetSize = (d, w, h) => { d.UserWidth_Left_Top = w; d.UserHeight_Left_Top = h; },
                GetPanel = d => d.RegionPanel_Left_Top, SetPanel = (d, v) => d.RegionPanel_Left_Top = v },
            new Region { Key = "Left_Center", GetShape = d => d.Region_Left_Center, SetShape = (d, v) => d.Region_Left_Center = v,
                GetSize = d => (d.UserWidth_Left_Center, d.UserHeight_Left_Center), SetSize = (d, w, h) => { d.UserWidth_Left_Center = w; d.UserHeight_Left_Center = h; },
                GetPanel = d => d.RegionPanel_Left_Center, SetPanel = (d, v) => d.RegionPanel_Left_Center = v },
            new Region { Key = "Left_Bottom", GetShape = d => d.Region_Left_Bottom, SetShape = (d, v) => d.Region_Left_Bottom = v,
                GetSize = d => (d.UserWidth_Left_Bottom, d.UserHeight_Left_Bottom), SetSize = (d, w, h) => { d.UserWidth_Left_Bottom = w; d.UserHeight_Left_Bottom = h; },
                GetPanel = d => d.RegionPanel_Left_Bottom, SetPanel = (d, v) => d.RegionPanel_Left_Bottom = v },
            new Region { Key = "Right_Top", GetShape = d => d.Region_Right_Top, SetShape = (d, v) => d.Region_Right_Top = v,
                GetSize = d => (d.UserWidth_Right_Top, d.UserHeight_Right_Top), SetSize = (d, w, h) => { d.UserWidth_Right_Top = w; d.UserHeight_Right_Top = h; },
                GetPanel = d => d.RegionPanel_Right_Top, SetPanel = (d, v) => d.RegionPanel_Right_Top = v },
            new Region { Key = "Right_Center", GetShape = d => d.Region_Right_Center, SetShape = (d, v) => d.Region_Right_Center = v,
                GetSize = d => (d.UserWidth_Right_Center, d.UserHeight_Right_Center), SetSize = (d, w, h) => { d.UserWidth_Right_Center = w; d.UserHeight_Right_Center = h; },
                GetPanel = d => d.RegionPanel_Right_Center, SetPanel = (d, v) => d.RegionPanel_Right_Center = v },
            new Region { Key = "Right_Bottom", GetShape = d => d.Region_Right_Bottom, SetShape = (d, v) => d.Region_Right_Bottom = v,
                GetSize = d => (d.UserWidth_Right_Bottom, d.UserHeight_Right_Bottom), SetSize = (d, w, h) => { d.UserWidth_Right_Bottom = w; d.UserHeight_Right_Bottom = h; },
                GetPanel = d => d.RegionPanel_Right_Bottom, SetPanel = (d, v) => d.RegionPanel_Right_Bottom = v },

            // ---- 4 个角落区域：键无下划线；**没有形状字段**；尺寸字段多一层 Corner_ ----
            new Region { Key = "TopLeft", IsCorner = true, Aliases = new[] { "Corner_TopLeft" },
                GetSize = d => (d.UserWidth_Corner_TopLeft, d.UserHeight_Corner_TopLeft), SetSize = (d, w, h) => { d.UserWidth_Corner_TopLeft = w; d.UserHeight_Corner_TopLeft = h; },
                GetPanel = d => d.RegionPanel_TopLeft, SetPanel = (d, v) => d.RegionPanel_TopLeft = v },
            new Region { Key = "TopRight", IsCorner = true, Aliases = new[] { "Corner_TopRight" },
                GetSize = d => (d.UserWidth_Corner_TopRight, d.UserHeight_Corner_TopRight), SetSize = (d, w, h) => { d.UserWidth_Corner_TopRight = w; d.UserHeight_Corner_TopRight = h; },
                GetPanel = d => d.RegionPanel_TopRight, SetPanel = (d, v) => d.RegionPanel_TopRight = v },
            new Region { Key = "BottomLeft", IsCorner = true, Aliases = new[] { "Corner_BottomLeft" },
                GetSize = d => (d.UserWidth_Corner_BottomLeft, d.UserHeight_Corner_BottomLeft), SetSize = (d, w, h) => { d.UserWidth_Corner_BottomLeft = w; d.UserHeight_Corner_BottomLeft = h; },
                GetPanel = d => d.RegionPanel_BottomLeft, SetPanel = (d, v) => d.RegionPanel_BottomLeft = v },
            new Region { Key = "BottomRight", IsCorner = true, Aliases = new[] { "Corner_BottomRight" },
                GetSize = d => (d.UserWidth_Corner_BottomRight, d.UserHeight_Corner_BottomRight), SetSize = (d, w, h) => { d.UserWidth_Corner_BottomRight = w; d.UserHeight_Corner_BottomRight = h; },
                GetPanel = d => d.RegionPanel_BottomRight, SetPanel = (d, v) => d.RegionPanel_BottomRight = v },
        };

        /// <summary>16 个区域的规范键（顺序同 `All`）。给"要遍历全部区域"的地方用，避免各处再抄一份键列表。</summary>
        public static readonly string[] Keys = All.Select(r => r.Key).ToArray();

        private static readonly Dictionary<string, Region> _byKey = BuildIndex();

        private static Dictionary<string, Region> BuildIndex()
        {
            var map = new Dictionary<string, Region>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in All)
            {
                map[r.Key] = r;
                foreach (var a in r.Aliases) map[a] = r;
            }
            return map;
        }

        /// <summary>按键找区域（大小写不敏感，别名也认）；未知键返回 null。</summary>
        public static Region? Find(string? key)
            => string.IsNullOrEmpty(key) ? null : (_byKey.TryGetValue(key!, out var r) ? r : null);

        /// <summary>别名 → 规范键；认不出来就原样返回（调用方按未知处理）。</summary>
        public static string Normalize(string? key) => Find(key)?.Key ?? (key ?? "");

        /// <summary>边缘拆分的 (edge, region) → 规范键，如 ("Top","Left") → "Top_Left"；拼不出已知键则返回空串。</summary>
        public static string KeyOf(string? edge, string? region)
        {
            if (string.IsNullOrEmpty(edge) || string.IsNullOrEmpty(region)) return "";
            string key = edge + "_" + region;
            return Find(key) != null ? Normalize(key) : "";
        }
    }
}
