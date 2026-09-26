using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.UI.Settings.Pages;
using ShoreHue.UI.Theme;
using ShoreHue.UI.Localization;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ShoreHue.UI.Settings
{
    public partial class SettingsWindow
    {
        // ========== 小工具市场（左侧竖排列表 + 右侧精调） ==========

        private static readonly Dictionary<string, string> _builtinLocKeys = new()
        {
            ["Clipboard"] = "UI_SettingsWindow_317",
            ["Note"] = "UI_SettingsWindow_318",
            ["Timer"] = "UI_SettingsWindow_319",
            ["Calculator"] = "UI_SettingsWindow_320",
            ["TextAi"] = "UI_SettingsWindow_321",
            ["Web"] = "WidgetTabs_Web",
        };

        private string _selectedWidgetKey = "";

        /// <summary>
        /// 正在重建小组件列表（Children.Clear() + 重新加行）。
        /// 重建期间**必须抑制勾选框写回**：Clear() 会把正在被点击的勾选框从可视树里拔掉，
        /// 它随后回落成"未勾选"并触发一次 Unchecked → 把用户刚勾上的启用状态立刻写回 false
        /// （实测日志里出现过相隔 100ms 的相反两次状态：先 true 后 false，最终落盘 false）。
        /// </summary>
        private bool _rebuildingWidgetRows;

        /// <summary>刷新左侧小组件列表（内置 + 用户插件），保持当前选中项。</summary>
        /// <summary>在系统文件管理器中打开小组件文件夹。</summary>
        private void BtnOpenWidgetFolder_Click(object sender, RoutedEventArgs e)
        {
            ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.OpenFolder();
        }

        private void RefreshWidgetMarket()
        {
            if (WidgetMarketList == null) return;
            // ★ 重建期间抑制勾选框写回（理由见 _rebuildingWidgetRows 注释）。
            //   用 try/finally：中途抛异常也不能把标志留在 true，否则之后所有勾选都失效。
            _rebuildingWidgetRows = true;
            try { RefreshWidgetMarketCore(); }
            finally { _rebuildingWidgetRows = false; }
        }

        private void RefreshWidgetMarketCore()
        {
            WidgetPluginStore.Reload();
            WidgetMarketList.Children.Clear();

            foreach (var kv in _builtinLocKeys)
                AddMarketItem(kv.Key, WithIssueMark(kv.Key.ToLowerInvariant(), LocalizationManager.Instance[kv.Value]));
            foreach (var plugin in WidgetPluginStore.Installed)
            {
                // ★ 内置件（如已文件化的 timer）已由上面的 _builtinLocKeys 列出，
                //   这里再列一次会出现两行同名条目（一行用本地化名、一行用 manifest 名）。
                if (plugin.IsBuiltin) continue;
                AddPluginMarketItem(plugin);
            }
            // ★ 海床保存的小组件变体（BaseType=Widget）：作为启停项列出（前缀区分）
            foreach (var cp in _settings.CustomPanels)
            {
                if (cp.Kind == "Config" || (cp.BaseType ?? "") != "Widget") continue;
                WidgetMarketList.Children.Add(BuildMarketRow("Seabed_" + cp.Id, WithIssueMark(cp.Id, cp.Name), null));
            }

            if (string.IsNullOrEmpty(_selectedWidgetKey) || !KeyExists(_selectedWidgetKey))
                _selectedWidgetKey = "Clipboard";
            SelectWidget(_selectedWidgetKey);
        }

        private bool KeyExists(string key)
        {
            if (_builtinLocKeys.ContainsKey(key)) return true;
            if (WidgetPluginStore.Installed.Any(p => "Widget_" + p.Id == key)) return true;
            if (_settings.CustomPanels.Any(p => p.Kind != "Config" && (p.BaseType ?? "") == "Widget" && "Seabed_" + p.Id == key)) return true;
            return false;
        }
        private void AddMarketItem(string key, string name)
        {
            WidgetMarketList.Children.Add(BuildMarketRow(key, name, null));
        }

        private void AddPluginMarketItem(WidgetPlugin plugin)
        {
            WidgetMarketList.Children.Add(BuildMarketRow("Widget_" + plugin.Id, WithIssueMark(plugin.Id, plugin.Name), plugin));
        }

        /// <summary>列表项名字后加个"⚠"：这个组件上一次没加载成功（编译失败 / 被沙箱拦）。
        /// ★ 以前这类失败只写日志，界面上就是"它不见了"，用户完全不知道为什么。</summary>
        private static string WithIssueMark(string id, string name)
        {
            try
            {
                return ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.GetLoadIssue(id) == null ? name : name + " ⚠";
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[小组件] 读取加载问题失败（{id}）：{ex.Message}");
                return name;
            }
        }

        /// <summary>构建左侧列表项：勾选框（启用，即时生效）+ 名称按钮（左键选中精调，右键菜单）。</summary>
        private Grid BuildMarketRow(string key, string name, WidgetPlugin? plugin)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var chk = new CheckBox
            {
                IsChecked = _settings.IsWidgetEnabled(key),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            // ★ 重建列表期间不写回：Children.Clear() 拔掉正在点击的勾选框会触发一次 Unchecked，
            //   把用户刚勾上的启用状态立刻改回 false（现象就是"勾了自己又没了 / 刷新不保存"）。
            //   重建结束后行的勾选状态本来就是从设置里重新读的，所以抑制不会丢用户操作。
            chk.Checked += (_, _) => { if (!_rebuildingWidgetRows) _settings.SetWidgetEnabled(key, true); };
            chk.Unchecked += (_, _) => { if (!_rebuildingWidgetRows) _settings.SetWidgetEnabled(key, false); };
            row.Children.Add(chk);

            var btn = new System.Windows.Controls.Button
            {
                Content = name,
                Tag = key,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 5, 6, 5),
                FontSize = 12
            };
            btn.Click += (_, _) => SelectWidget(key);
            btn.ContextMenu = BuildMarketMenu(key, plugin);
            row.Children.Add(btn);
            System.Windows.Controls.Grid.SetColumn(btn, 1);
            return row;
        }

        /// <summary>右键菜单：仅用户插件提供编辑/删除（启停已由左侧勾选框承担）。</summary>
        private ContextMenu BuildMarketMenu(string key, WidgetPlugin? plugin)
        {
            var menu = new ContextMenu();
            if (plugin != null)
            {
                var miDelete = new MenuItem { Header = LocalizationManager.Instance["WidgetMkt_Delete"] };
                miDelete.Click += (_, _) => DeletePlugin(plugin);
                menu.Items.Add(miDelete);
            }
            return menu;
        }

        /// <summary>左键选中：切换右侧精调面板 + 列表高亮。</summary>
        private void SelectWidget(string key)
        {
            _selectedWidgetKey = key;
            foreach (var row in WidgetMarketList.Children.OfType<Grid>())
            {
                var btn = row.Children.OfType<System.Windows.Controls.Button>().FirstOrDefault();
                if (btn == null) continue;
                btn.Background = (btn.Tag as string) == key
                    ? new SolidColorBrush(Color.FromRgb(229, 241, 255))
                    : System.Windows.Media.Brushes.Transparent;
            }
            DetailClipboard.Visibility = key == "Clipboard" ? Visibility.Visible : Visibility.Collapsed;
            DetailNote.Visibility = key == "Note" ? Visibility.Visible : Visibility.Collapsed;
            DetailTimer.Visibility = key == "Timer" ? Visibility.Visible : Visibility.Collapsed;
            DetailCalc.Visibility = key == "Calculator" ? Visibility.Visible : Visibility.Collapsed;
            DetailTextAi.Visibility = key == "TextAi" ? Visibility.Visible : Visibility.Collapsed;
            DetailWeb.Visibility = key == "Web" ? Visibility.Visible : Visibility.Collapsed;
            if (key.StartsWith("Widget_"))
            {
                DetailPlugin.Visibility = Visibility.Visible;
                FillPluginDetail(key.Substring("Widget_".Length));
            }
            else if (key.StartsWith("Seabed_"))
            {
                DetailPlugin.Visibility = Visibility.Visible;
                FillSeabedDetail(key.Substring("Seabed_".Length));
            }
            else
            {
                DetailPlugin.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>精调区：海床小组件变体的信息（编辑/删除请到海床页）。</summary>
        private void FillSeabedDetail(string id)
        {
            DetailPlugin.Children.Clear();
            var cp = _settings.CustomPanels.FirstOrDefault(p => p.Id == id);
            if (cp == null) return;
            DetailPlugin.Children.Add(new TextBlock
            {
                Text = cp.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });
            DetailPlugin.Children.Add(new TextBlock
            {
                Text = "海床小组件变体（动态编译）",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });
            DetailPlugin.Children.Add(new TextBlock
            {
                Text = "编辑源码、编译与删除请在「海床」页操作；此处仅控制启用/停用。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        /// <summary>精调区：用户插件的状态/权限/启用/编辑/删除。</summary>
        private void FillPluginDetail(string id)
        {
            DetailPlugin.Children.Clear();
            var plugin = WidgetPluginStore.GetById(id);
            if (plugin == null) return;
            string key = "Widget_" + plugin.Id;

            DetailPlugin.Children.Add(new TextBlock
            {
                Text = plugin.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });

            bool compileOk = string.IsNullOrEmpty(WidgetCompiler.Validate(plugin.Id, plugin.Source));
            var permText = plugin.Permissions.Count == 0
                ? LocalizationManager.Instance["WidgetMkt_None"]
                : string.Join(" · ", plugin.Permissions.Select(ShoreHue.UI.Widgets.Dynamic.WidgetPermissions.PermissionLabel));
            DetailPlugin.Children.Add(new TextBlock
            {
                Text = (compileOk ? " " : "⚠ 编译失败  ") + permText,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(compileOk
                    ? (plugin.Permissions.Count > 0 ? Color.FromRgb(255, 170, 90) : Color.FromRgb(136, 136, 136))
                    : Color.FromRgb(200, 80, 70)),
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            // ★ 安全 v2：信任开关（信任范围 = 当前这份内容；内容改动后自动失效，需要重新确认）
            bool isTrusted = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.IsTrusted(plugin);
            var btnTrust = new System.Windows.Controls.Button
            {
                Content = isTrusted ? "受信任 · 改用沙箱" : "默认沙箱 · 点此信任",
                Style = (Style)FindResource("Win11Button"),
                Height = 26,
                FontSize = 11,
                Padding = new Thickness(10, 0, 10, 0),
                ToolTip = isTrusted
                    ? "当前内容已受信任（不扫描）。内容一旦改动，信任自动失效。"
                    : "外来代码默认在沙箱中运行：限制文件写入/进程/宿主特权 API 等。仅当你确认这份代码可信时才点此。"
            };
            btnTrust.Click += (_, _) =>
            {
                if (!isTrusted && MessageBox.Show(
                        "确定要信任这份代码吗？\n\n信任后它将不再受沙箱限制（可访问文件写入、进程、宿主特权 API 等）。\n" +
                        "内容一旦改动，信任会自动失效。",
                        "信任外来代码", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                if (!ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.SetTrusted(plugin.Id, !isTrusted))
                    MessageBox.Show("修改「" + plugin.Name + "」的信任状态失败，详情见日志。",
                        "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshWidgetMarket();
            };
            btnRow.Children.Add(btnTrust);

            var btnDel = new System.Windows.Controls.Button
            {
                Content = LocalizationManager.Instance["WidgetMkt_Delete"],
                Style = (Style)FindResource("Win11Button"),
                Width = 76,
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };
            btnDel.Click += (_, _) => DeletePlugin(plugin);
            btnRow.Children.Add(btnDel);
            DetailPlugin.Children.Add(btnRow);
        }

        /// <summary>卸载已安装的插件小组件。</summary>
        private void DeletePlugin(WidgetPlugin plugin)
        {
            // ★ ShoreHue 内置文件保护：官方随附的小组件删除前警告（用户自定义/拾贝的不适用）
            if (ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.IsBuiltin(plugin))
            {
                var warn = new ShoreHue.UI.Seabed.ConfirmDialog(
                    "删除 ShoreHue 内部文件",
                    "「" + plugin.Name + "」是 ShoreHue 内部文件，删除可能导致运行异常。\n\n确定要删除吗？（删除自定义功能与卸载 ShoreHue 不受此提示影响）",
                    "确定删除", "取消")
                {
                    Owner = this
                };
                if (warn.ShowDialog() != true) return;
            }
            else if (MessageBox.Show(string.Format(LocalizationManager.Instance["WidgetMkt_DeleteConfirm"], plugin.Name),
                    LocalizationManager.Instance["WidgetMkt_Confirm"],
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            // ★ 先删文件再清开关：删失败就**不要**把启用状态置为 false ——
            //   否则条目还在列表里、却被记成"用户禁用了它"，而覆盖记录会一直留在
            //   WidgetPluginOverrides 里：同 id 的包以后重新装回来，默认是禁用状态，
            //   用户在界面上看不出任何原因（"覆盖"是设置里的显式开关，不会随删除自动消失）。
            if (!WidgetPluginStore.Delete(plugin.Id))
            {
                MessageBox.Show("删除「" + plugin.Name + "」失败：文件可能被占用，详情见日志。",
                    "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshWidgetMarket();
                return;
            }
            _settings.ClearWidgetEnabledOverride("Widget_" + plugin.Id);
            RefreshWidgetMarket();
        }
    }
}
