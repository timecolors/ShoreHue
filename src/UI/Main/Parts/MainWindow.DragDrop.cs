using System;
using System.Windows;
using ShoreHue.Core.Services;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.AppHelper;
using ShoreHue.UI.Panels;
using System.Windows.Controls;

namespace ShoreHue.UI.Main
{
    public partial class MainWindow
    {
        private enum IconState
        {
            Default,
            AddMode,
            DeleteMode
        }

        private IconState _currentIconState = IconState.Default;
        private bool _isHovering = false;
        private AppHelperView? _appHelperView;
        private ShoreHue.UI.AI.AiChatView? _aiChatView;

        private void UpdateIconTextInternal()
        {
            // ★ 左侧竖条的反馈由状态机驱动（不用图标）：
            //   拖放态显示大字字标：AddMode=＋（固定窗口/文件）、DeleteMode=🗑（移除快捷方式），
            //   否则仅按悬停点亮淡色反馈条。
            switch (_currentIconState)
            {
                case IconState.AddMode:
                    IconHoverBar.Opacity = 1.0;
                    if (IconModeGlyph != null)
                    {
                        IconModeGlyph.Text = "＋";
                        IconModeGlyph.Opacity = 1.0;
                    }
                    return;
                case IconState.DeleteMode:
                    IconHoverBar.Opacity = 1.0;
                    if (IconModeGlyph != null)
                    {
                        IconModeGlyph.Text = "🗑";
                        IconModeGlyph.Opacity = 1.0;
                    }
                    return;
            }

            if (IconModeGlyph != null) IconModeGlyph.Opacity = 0.0;
            IconHoverBar.Opacity = (_isHovering && !_modeService.IsDoNotDisturb) ? 1.0 : 0.0;
        }

        private void SetIcon(string resourceKey, bool accent)
        {
            // 图标已移除；保留调用点兼容（AppHelper 循环页等），仅点亮反馈条
            IconHoverBar.Opacity = 1.0;
        }

        private void ResetIconToDefault()
        {
            _currentIconState = IconState.Default;
            UpdateIconTextInternal();
        }

        private void IconText_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isHovering = true;
            UpdateIconTextInternal();
            e.Handled = true;
        }

        private void IconText_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isHovering = false;
            UpdateIconTextInternal();
            e.Handled = true;
        }

        private void IconText_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                bool isAiPanel = _contentController?.CurrentRegionType == "AI";
                bool hasFile = files is { Length: > 0 };
                bool isImage = hasFile && IsImageFile(files![0]);

                if (isAiPanel)
                {
                    // ★ AI 面板：接受文件（图片/文本/代码/docx 上传给 AI，不支持的给提示）
                    e.Effects = DragDropEffects.Copy;
                    _currentIconState = IconState.AddMode;
                    UpdateIconTextInternal();
                }
                else
                {
                    e.Effects = DragDropEffects.Copy;
                    _currentIconState = IconState.AddMode;
                    UpdateIconTextInternal();
                }
            }
            else if (e.Data.GetDataPresent(typeof(TaskbarItem)))
            {
                var item = e.Data.GetData(typeof(TaskbarItem)) as TaskbarItem;
                if (item != null)
                {
                    if (item.Type == TaskbarItemType.Shortcut)
                    {
                        e.Effects = DragDropEffects.Move;
                        _currentIconState = IconState.DeleteMode;
                        UpdateIconTextInternal();
                    }
                    else if (item.Type == TaskbarItemType.Window)
                    {
                        e.Effects = DragDropEffects.Copy;
                        _currentIconState = IconState.AddMode;
                        UpdateIconTextInternal();
                    }
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private static bool IsImageFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tiff" or ".tif";
        }

        private void IconText_DragLeave(object sender, DragEventArgs e)
        {
            ResetIconToDefault();
            e.Handled = true;
        }

        private void IconText_DragOver(object sender, DragEventArgs e)
        {
            // ★ 悬停期间持续刷新拖放效果：否则光标会退回“禁止”
            IconText_DragEnter(sender, e);
        }

        private void IconText_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        string path = files[0];

                        // ★ AI 面板：拖入文件 = 上传给 AI（图片/文本/代码/docx，内部按类型分发）
                        if (_contentController?.CurrentRegionType == "AI" && _aiChatView != null)
                        {
                            _ = _aiChatView.SendFileAsync(path);
                            return;
                        }

                        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            path = ShortcutLinkResolver.Resolve(path);
                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        {
                            string name = System.IO.Path.GetFileNameWithoutExtension(path);
                            if (_shortcutService.AddShortcut(path, name))
                                RefreshTaskbarView();
                        }
                    }
                }
                else if (e.Data.GetDataPresent(typeof(TaskbarItem)))
                {
                    var item = e.Data.GetData(typeof(TaskbarItem)) as TaskbarItem;
                    if (item != null)
                    {
                        if (item.Type == TaskbarItemType.Shortcut)
                        {
                            if (!string.IsNullOrEmpty(item.Id) && _shortcutService.RemoveShortcut(item.Id))
                                RefreshTaskbarView();
                        }
                        else if (item.Type == TaskbarItemType.Window)
                        {
                            // ★ 固定正在运行的窗口应用：窗口标题会变，用 exe 文件名做快捷方式名
                            string? exe = item.Path;
                            if (string.IsNullOrEmpty(exe) && item.Handle.HasValue)
                                exe = WindowListProvider.GetProcessPathByHandle(item.Handle.Value);
                            if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                            {
                                string name = System.IO.Path.GetFileNameWithoutExtension(exe);
                                if (_shortcutService.AddShortcut(exe, name))
                                    RefreshTaskbarView();
                            }
                            else if (!string.IsNullOrEmpty(item.Path) && _shortcutService.AddShortcut(item.Path, item.DisplayName))
                            {
                                RefreshTaskbarView();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"拖放处理失败: {ex.Message}");
            }
            finally
            {
                ResetIconToDefault();
            }
            e.Handled = true;
        }

        private void IconText_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                // 右键只用于弹出快捷菜单
                if (e.ChangedButton == System.Windows.Input.MouseButton.Right) return;

                // ★ 应用辅助模式：点击 ShoreHue 循环切换辅助功能页 ★
                if (_contentController?.CurrentRegionType == "AppHelper" && _appHelperView != null)
                {
                    _appHelperView.CyclePage();
                    _isHovering = false;
                    UpdateIconTextInternal();
                    e.Handled = true;
                    return;
                }

                // ★ AI 面板：点击图标不触发勿扰模式（避免面板内容被误隐藏）
                if (_contentController?.CurrentRegionType == "AI")
                {
                    e.Handled = true;
                    return;
                }

                bool newState = !_modeService.IsDoNotDisturb;
                _modeService.IsDoNotDisturb = newState;

                if (newState)
                    _visibilityController.ForceHide();

                _isHovering = false;
                UpdateIconTextInternal();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"勿扰切换失败: {ex.Message}");
            }
        }

        private void IconText_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();

            var dnd = new MenuItem
            {
                Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_Dnd"],
                IsCheckable = true,
                IsChecked = _modeService.IsDoNotDisturb
            };
            dnd.Click += (_, _) => ToggleWindow();
            menu.Items.Add(dnd);

            // ===== 面板专属区（只放面板内做不到的实用操作，不与面板已有按钮重复） =====
            string? regionType = _contentController?.CurrentRegionType;
            if (regionType != null && !string.IsNullOrEmpty(regionType))
            {
                switch (regionType)
                {
                    case "Taskbar":
                    {
                        var showDesktop = new MenuItem
                        {
                            Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Menu_ShowDesktop"]
                        };
                        showDesktop.Click += (_, _) => ShoreHue.Infrastructure.WinApi.WindowAction.ShowDesktop();
                        menu.Items.Add(showDesktop);

                        var closeWindows = new MenuItem
                        {
                            Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Menu_CloseRunning"]
                        };
                        closeWindows.Click += (_, _) =>
                        {
                            var tv = FindContentDescendant<ShoreHue.UI.Panels.TaskbarView>(ContentContainer.Content);
                            if (tv != null) tv.CloseAllWindows();
                            else RefreshTaskbarView();
                        };
                        menu.Items.Add(closeWindows);

                        var refresh = new MenuItem { Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_Refresh"] };
                        refresh.Click += (_, _) => RefreshTaskbarView();
                        menu.Items.Add(refresh);
                        break;
                    }
                    case "Widget":
                    {
                        var editWidget = new MenuItem
                        {
                            Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Menu_EditWidget"]
                        };
                        editWidget.Click += (_, _) => OpenSettings("tabSeabed");
                        menu.Items.Add(editWidget);
                        break;
                    }
                    case "AI":
                    {
                        var aiSettings = new MenuItem
                        {
                            Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Menu_AiSettings"]
                        };
                        aiSettings.Click += (_, _) => OpenSettings("tabAI");
                        menu.Items.Add(aiSettings);
                        break;
                    }
                }
                menu.Items.Add(new Separator());
            }

            // ★ 插件守卫的恢复入口：安全模式重启 + 解除熔断
            var safeMode = new MenuItem { Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_SafeMode"] };
            safeMode.IsEnabled = !ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.SafeMode;
            safeMode.Click += (_, _) => RequestSafeModeRestart();
            menu.Items.Add(safeMode);

            var clearBreaker = new MenuItem { Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_ClearBreaker"] };
            clearBreaker.Click += (_, _) =>
            {
                int n = ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.ClearAll();
                ShoreHue.Core.Infrastructure.Logging.LogManager.Info($"[守卫] 用户解除了 {n} 个插件的熔断");
            };
            menu.Items.Add(clearBreaker);

            var settings = new MenuItem { Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_Settings"] };
            settings.Click += (_, _) => OpenSettings();
            menu.Items.Add(settings);

            var exit = new MenuItem { Header = ShoreHue.UI.Localization.LocalizationManager.Instance["Tray_Exit"] };
            exit.Click += (_, _) => ExitApp();
            menu.Items.Add(exit);

            menu.PlacementTarget = IconContainer;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }

        internal void OnPanelContentChanged()
        {
            // ★ 全局字号缩放：面板内容切换（新控件进视觉树）后补应用一次
            ShoreHue.UI.Theme.FontScaleManager.ApplyFontScale(this, _settingsService.UiFontScale);
            _appHelperView = FindContentDescendant<AppHelperView>(ContentContainer.Content);
            _aiChatView = FindContentDescendant<ShoreHue.UI.AI.AiChatView>(ContentContainer.Content);
            UpdateIconTooltip();

            // ★ 小组件内容（含内部切标签）变化后重新测量并自适应面板尺寸：
            //   内容尽量显示全，剪贴板/便签已内部限高
            //   （切换动画/图标中置期间跳过 AutoSize：保持"触发的尺寸"不变，
            //     尺寸由稳定后的形变动画（目标尺寸）统一更新）
            if (_contentController.CurrentRegionType == "Widget")
            {
                // 内容变了 → 目标尺寸缓存始终失效（下次切换重新测量）
                _edgeController.InvalidateTargetSizeCache("Widget");
                // ★ 直接加载流程中（内容就位 → 尺寸由切换分支统一测量并形变）跳过
                //   原子跳变，避免"动画形变 + 原子 SetWindowPos"打架闪烁
                if (!_shapeAnimator.IsTransformAnimating && !_iconCentered &&
                    !_edgeController.IsDirectLoadInProgress)
                {
                    // ★ 面板隐藏时不跑自适应：此时内容不在可视树、布局未就绪，量到的是**上一份内容**的尺寸。
                    //   真机实测（2026-09-13）：面板停在 web 标签 → 隐藏 → 在设置里取消勾选 web →
                    //   隐藏期间仍跑了一次 AutoSize（量到 448x434 → 把面板设成 488x554），
                    //   再唤出时又量到 1765x333（任务栏的形状）→ 面板被设成 683x453 → 内容区是空的。
                    //   尺寸在下次**显示**时由显示路径统一测量即可，隐藏期间算它没有意义。
                    if (_edgeController.IsPanelVisible)
                        _sizeController.ApplySizeStrategyForWidget();
                }
            }
        }

        /// <summary>
        /// 请求"下次以安全模式启动"：落盘标记 → 退出。
        /// ★ 不做"本进程内重启"：单实例互斥体在新进程启动时还没释放，新实例会直接退出；
        ///   落盘标记 + 下次手动/自启进入，是可靠且不会和单实例打架的做法。
        /// </summary>
        private void RequestSafeModeRestart()
        {
            try
            {
                _settingsService.SafeModeRequested = true;
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning("[守卫] 用户请求：下次以安全模式启动");
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("设置安全模式请求失败", ex);
            }
            ExitApp();
        }

        private void UpdateIconTooltip()
        {
            if (_contentController == null) return;

            string tooltip = _contentController.CurrentRegionType switch
            {
                "AppHelper" =>
                    ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_TipHelper"],

                "AI" =>
                    ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_TipAi"],

                "Taskbar" =>
                    ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_TipTaskbar"],

                _ =>
                    ShoreHue.UI.Localization.LocalizationManager.Instance["Dnd_TipDnd"]
            };

            if (ShoreHue.UI.Widgets.Dynamic.PluginRuntimeGuard.SafeMode)
                tooltip += "（安全模式：未加载任何插件）";
            IconContainer.ToolTip = tooltip;
        }

        /// <summary>
        /// 面板内容可能是**文件夹加载的薄封装**（如 TaskbarPanel 内部才是 TaskbarView），
        /// 所以按具体类型取当前内容必须**向下找**，不能只看 ContentContainer.Content 本身。
        /// </summary>
        private static T? FindContentDescendant<T>(object? root) where T : DependencyObject
        {
            if (root is not DependencyObject node) return null;
            if (node is T hit) return hit;
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d)
                {
                    var found = FindContentDescendant<T>(d);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void RefreshTaskbarView()
        {
            // 刷新任务栏视图（文件夹版时它包在薄封装里 → 向下找）
            var taskbarView = FindContentDescendant<TaskbarView>(ContentContainer.Content);
            taskbarView?.RefreshData();
        }
    }
}
