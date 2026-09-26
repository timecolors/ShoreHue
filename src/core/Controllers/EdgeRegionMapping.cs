using System;

namespace ShoreHue.Core.Controllers
{
    /// <summary>
    /// 边缘区域映射与位置计算（纯函数，无状态、无 UI 依赖，可单元测试）。
    /// 从 EdgeTriggerController 提取：区域键/边缘名/面板类型推导 + 面板锚定位置计算。
    /// </summary>
    public static class EdgeRegionMapping
    {
        /// <summary>区域 → 边缘名（Top/Bottom/Left/Right）。
        /// ★ 角落（TopLeft/TopRight/BottomLeft/BottomRight）返回**空串** —— 角落不属于任何单一边，
        ///   而 GetRegionKey 正是靠这个空串把角落当作独立区域（返回枚举名）处理。
        ///   所以日志里出现 `edge=` 为空只表示"这次触发来自角落"，不是异常。
        ///   （旧注释写成"角落返回 Top/Bottom"，与实现和测试都不符，曾据此误判过一个 bug。）
        /// 未知值同样返回空。</summary>
        public static string GetEdgeName(EdgeRegion r) => r switch
        {
            EdgeRegion.Top_Left or EdgeRegion.Top_Center or EdgeRegion.Top_Right => "Top",
            EdgeRegion.Bottom_Left or EdgeRegion.Bottom_Center or EdgeRegion.Bottom_Right => "Bottom",
            EdgeRegion.Left_Top or EdgeRegion.Left_Center or EdgeRegion.Left_Bottom => "Left",
            EdgeRegion.Right_Top or EdgeRegion.Right_Center or EdgeRegion.Right_Bottom => "Right",
            _ => ""
        };

        /// <summary>区域 → 区域键（如 "Top_Left"；角落返回枚举名）。</summary>
        public static string GetRegionKey(EdgeRegion r)
        {
            string edge = GetEdgeName(r);
            if (string.IsNullOrEmpty(edge)) return r.ToString();

            string sub = r switch
            {
                EdgeRegion.Top_Left or EdgeRegion.Bottom_Left => "Left",
                EdgeRegion.Top_Center or EdgeRegion.Bottom_Center or EdgeRegion.Left_Center or EdgeRegion.Right_Center => "Center",
                EdgeRegion.Top_Right or EdgeRegion.Bottom_Right => "Right",
                EdgeRegion.Left_Top or EdgeRegion.Right_Top => "Top",
                EdgeRegion.Left_Bottom or EdgeRegion.Right_Bottom => "Bottom",
                _ => r.ToString()
            };
            return edge + "_" + sub;
        }

        /// <summary>区域键 → 边缘名（用于固定形状查询；不含 "_" 返回空）。</summary>
        public static string GetEdgeFromKey(string key)
        {
            return key.Contains('_') ? key.Split('_')[0] : "";
        }

        /// <summary>
        /// 区域 → 面板类型（默认布局 + 用户自定义覆盖）。
        /// getRegionPanel：区域键 → 设置中的面板名（"Default" = 跟随默认布局）。
        /// isCorner 由调用方判定（右上角默认不呼出，需额外判断）。
        /// </summary>
        public static string GetRegionTypeFromEnum(EdgeRegion r, Func<string, string> getRegionPanel, Func<string, bool> isValidPanelType)
        {
            // 区域面板自定义：设置里非 Default 时覆盖默认布局
            string custom = getRegionPanel(GetRegionKey(r));
            if (custom != "Default" && isValidPanelType(custom))
            {
                return custom;
            }

            bool isHorizontal = r == EdgeRegion.Top_Left || r == EdgeRegion.Top_Center || r == EdgeRegion.Top_Right ||
                                 r == EdgeRegion.Bottom_Left || r == EdgeRegion.Bottom_Center || r == EdgeRegion.Bottom_Right;
            bool isCenter = r == EdgeRegion.Top_Center || r == EdgeRegion.Bottom_Center ||
                            r == EdgeRegion.Left_Center || r == EdgeRegion.Right_Center;
            if (isCenter)
            {
                // ★ 左边缘中间默认 AI 助手，其余中心默认应用辅助
                return r == EdgeRegion.Left_Center ? "AI" : "AppHelper";
            }
            bool isVertical = r == EdgeRegion.Left_Top || r == EdgeRegion.Left_Center || r == EdgeRegion.Left_Bottom ||
                              r == EdgeRegion.Right_Top || r == EdgeRegion.Right_Center || r == EdgeRegion.Right_Bottom;
            if (isVertical) return "Widget";
            if (isHorizontal) return "Taskbar";
            return "Placeholder";
        }

        /// <summary>面板类型合法性：内置类型 + 自定义面板（Custom:前缀）。</summary>
        public static bool IsValidPanelType(string type)
        {
            // 自定义面板：Custom:面板Id（编译注册后有效）
            if (type.StartsWith("Custom:", StringComparison.Ordinal)) return true;
            return type is "Taskbar" or "Widget" or "AppHelper" or "Notification" or "Recent" or "QuickSettings" or "AI" or "WindowControl";
        }

        /// <summary>
        /// 面板锚定位置计算（区域/边 + 鼠标位置 + 面板尺寸 → 左上角坐标，钳制在屏幕内）。
        /// getEdgeMode：边缘 → "Follow"/"Fixed"；getFixedOffset：边缘 → 固定偏移（px）。
        /// </summary>
        public static (double left, double top) CalculatePosition(EdgeRegion region, double mx, double my,
            double sw, double sh, double w, double h, double bottomBoundary,
            Func<string, string> getEdgeMode, Func<string, double> getFixedOffset)
        {
            string edge = GetEdgeName(region);
            double left = 0, top = 0;

            // ★ 固定位置模式：面板不跟随鼠标，按保存的偏移量定位（由拖动面板时保存）
            if (!string.IsNullOrEmpty(edge) && getEdgeMode(edge) == "Fixed")
            {
                double offset = getFixedOffset(edge);
                switch (edge)
                {
                    case "Top":
                        left = Math.Max(0, Math.Min(sw / 2 - w / 2 + offset, sw - w));
                        return (left, 0);
                    case "Bottom":
                        left = Math.Max(0, Math.Min(sw / 2 - w / 2 + offset, sw - w));
                        return (left, bottomBoundary - h);
                    case "Left":
                        top = Math.Max(0, Math.Min(sh / 2 - h / 2 + offset, sh - h));
                        return (0, top);
                    case "Right":
                        top = Math.Max(0, Math.Min(sh / 2 - h / 2 + offset, sh - h));
                        return (sw - w, top);
                }
            }

            switch (edge)
            {
                case "Top":
                    left = mx - w / 2;
                    top = 0;
                    break;
                case "Bottom":
                    left = mx - w / 2;
                    top = bottomBoundary - h;
                    break;
                case "Left":
                    left = 0;
                    top = my - h / 2;
                    break;
                case "Right":
                    left = sw - w;
                    top = my - h / 2;
                    break;
                default:
                    return (0, 0);
            }

            left = Math.Max(0, Math.Min(left, sw - w));
            top = Math.Max(0, Math.Min(top, sh - h));
            return (left, top);
        }
    }
}
