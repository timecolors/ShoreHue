using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 设置云同步弹窗（替代系统 MessageBox）：显示登录账号与云端备份状态，
    /// 「上传当前设置」/「从云端恢复」两个操作按钮，内置进度与结果提示。
    /// 主题跟随系统（浅色白底 / 深色黑底，与共享平台一致）。
    /// </summary>
    public sealed class ConfigSyncWindow : Window
    {
        private readonly string _user;
        private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
        private readonly Button _uploadBtn = new();
        private readonly Button _downloadBtn = new();
        private bool _busy;

        /// <summary>null = 未操作；"upload" = 已上传；"download" = 已下载。</summary>
        public string? Result { get; private set; }

        public ConfigSyncWindow(string user, bool? hasCloud)
        {
            _user = user;

            bool light = ShoreHue.Infrastructure.Utils.SystemTheme.IsLightTheme();
            var cBg = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x1E, 0x1E, 0x1E);
            var cCard = light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x2A);
            var cText = light ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
            var cSub = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0xBB, 0xBB, 0xBB);
            var cBorder = light ? Color.FromRgb(0xE0, 0xE0, 0xE0) : Color.FromRgb(0x40, 0x40, 0x40);
            var cOk = Color.FromRgb(0x3C, 0xA8, 0x5C);
            var cErr = Color.FromRgb(0xD4, 0x50, 0x45);

            Title = "同步设置";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(cBg);
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

            // 标题
            root.Children.Add(new TextBlock
            {
                Text = "同步设置",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(cText)
            });

            // 账号 + 云端状态卡片
            var card = new Border
            {
                Background = new SolidColorBrush(cCard),
                BorderBrush = new SolidColorBrush(cBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 12, 0, 0)
            };
            var cardStack = new StackPanel();
            cardStack.Children.Add(new TextBlock
            {
                Text = "登录账号：" + _user,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(cText)
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = hasCloud == true
                    ? "云端已有你的设置备份 （可下载恢复，也可重新上传覆盖）"
                    : "云端暂无备份（首次使用请点「上传当前设置」）",
                FontSize = 11,
                Foreground = new SolidColorBrush(hasCloud == true ? cOk : cSub),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = cardStack;
            root.Children.Add(card);

            // 说明
            root.Children.Add(new TextBlock
            {
                Text = "上传：把当前电脑的设置保存到 GitHub（换电脑 / 重装后可从云端恢复）；下载：用云端备份覆盖本机当前设置（覆盖前会再次确认）。",
                FontSize = 11,
                Foreground = new SolidColorBrush(cSub),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

            // 状态
            _status.Text = "就绪";
            _status.Foreground = new SolidColorBrush(cSub);
            _status.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(_status);

            // 按钮行
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            _uploadBtn.Content = "⬆ 上传当前设置";
            _uploadBtn.Width = 130; _uploadBtn.Height = 30; _uploadBtn.FontSize = 12; _uploadBtn.Margin = new Thickness(0, 0, 8, 0);
            _uploadBtn.Style = (Style)FindResource("Win11Button");
            _uploadBtn.Click += async (_, _) => await UploadAsync();
            bottom.Children.Add(_uploadBtn);
            _downloadBtn.Content = "⬇ 从云端恢复";
            _downloadBtn.Width = 130; _downloadBtn.Height = 30; _downloadBtn.FontSize = 12;
            _downloadBtn.Style = (Style)FindResource("Win11Button");
            _downloadBtn.Click += async (_, _) => await DownloadAsync();
            bottom.Children.Add(_downloadBtn);
            var cancel = new Button { Content = "关闭", Width = 80, Height = 30, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
            cancel.Style = (Style)FindResource("Win11Button");
            cancel.Click += (_, _) => Close();
            bottom.Children.Add(cancel);
            root.Children.Add(bottom);

            Content = root;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _uploadBtn.IsEnabled = !busy;
            _downloadBtn.IsEnabled = !busy;
        }

        private void ShowStatus(string text, Color color)
        {
            _status.Text = text;
            _status.Foreground = new SolidColorBrush(color);
        }

        private async Task UploadAsync()
        {
            if (_busy) return;
            try
            {
                SetBusy(true);
                ShowStatus("正在上传…", Colors.Gray);
                string cfgPath = ShoreHue.Infrastructure.Utils.AppPaths.ConfigPath;
                if (!File.Exists(cfgPath))
                {
                    ShowStatus("未找到本机配置文件", Color.FromRgb(0xD4, 0x50, 0x45));
                    return;
                }
                string json = File.ReadAllText(cfgPath);
                string? err = await GitHubMarketService.UploadConfigAsync(_user, json);
                if (err != null)
                {
                    ShowStatus(err, Color.FromRgb(0xD4, 0x50, 0x45));
                    return;
                }
                Result = "upload";
                ShowStatus("已上传到云端（configs/" + _user + ".json），换电脑/重装后可下载恢复", Color.FromRgb(0x3C, 0xA8, 0x5C));
            }
            catch (Exception ex)
            {
                ShowStatus("上传失败：" + ex.Message, Color.FromRgb(0xD4, 0x50, 0x45));
            }
            finally { SetBusy(false); }
        }

        private async Task DownloadAsync()
        {
            if (_busy) return;
            try
            {
                SetBusy(true);
                ShowStatus("正在下载…", Colors.Gray);
                var (json, err) = await GitHubMarketService.DownloadConfigAsync(_user);
                if (err != null)
                {
                    ShowStatus(err, Color.FromRgb(0xD4, 0x50, 0x45));
                    return;
                }
                // 覆盖前二次确认（自定义确认弹窗，非 MessageBox）
                var confirm = new ConfirmDialog(
                    "从云端恢复",
                    "将用云端设置覆盖本机当前设置，确定？本机设置会被替换，建议先点「上传当前设置」做备份。",
                    "确定恢复", "取消");
                confirm.Owner = this;
                if (confirm.ShowDialog() != true)
                {
                    ShowStatus("已取消恢复", Colors.Gray);
                    return;
                }

                // ★ 落盘前必须校验：云端那份是**外部数据**，可能因为版本差异/传输损坏/仓库里被改过
                //   而根本不是合法配置。以前是 `File.WriteAllText(config, json)` 直接覆盖 ——
                //   一份 HTML 错误页或截断的 JSON 就能把用户全部设置一次性毁掉，而且是非原子的：
                //   连 `SettingsFileManager` 的 .bak 也会基于这个坏文件生成，回不去上一份好配置。
                ShoreHue.Core.Services.Configuration.SettingsData? parsed;
                try
                {
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        ShowStatus("云端内容为空，已中止恢复（本机设置未改动）", Color.FromRgb(0xD4, 0x50, 0x45));
                        return;
                    }
                    parsed = System.Text.Json.JsonSerializer
                        .Deserialize<ShoreHue.Core.Services.Configuration.SettingsData>(json);
                }
                catch (Exception pex)
                {
                    ShowStatus("云端内容不是合法配置，已中止恢复（本机设置未改动）：" + pex.Message,
                        Color.FromRgb(0xD4, 0x50, 0x45));
                    return;
                }
                if (parsed == null)
                {
                    ShowStatus("云端内容不是合法配置，已中止恢复（本机设置未改动）", Color.FromRgb(0xD4, 0x50, 0x45));
                    return;
                }

                // ★ 信任表不能由云端配置注入（否则别人仓库里一条记录就能让你机器上某个外来包永久免检）
                ShoreHue.Core.Services.Configuration.PluginTrustSanitizer.StripExternalTrust(
                    parsed,
                    ShoreHue.Core.Services.Configuration.PluginTrustSanitizer.ReadLocalTrust(),
                    "云端配置");

                // 恢复前留一份「恢复前」备份，出问题可以手工回退
                try
                {
                    if (File.Exists(ShoreHue.Infrastructure.Utils.AppPaths.ConfigPath))
                        File.Copy(ShoreHue.Infrastructure.Utils.AppPaths.ConfigPath,
                                  ShoreHue.Infrastructure.Utils.AppPaths.ConfigPath + ".preRestore.bak", true);
                }
                catch (Exception bex)
                {
                    // 备份失败不阻塞恢复，但要留痕（用户事后想回退时会找这份文件）
                    ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                        $"[云同步] 恢复前备份失败（不影响本次恢复）：{bex.Message}");
                }

                // ★ 走统一的原子写（临时文件 + File.Replace），不再裸 WriteAllText
                ShoreHue.Core.Services.SettingsFileManager.Save(
                    parsed, ShoreHue.Infrastructure.Utils.AppPaths.ConfigPath);

                // 通知设置服务重新加载（由调用方执行：_page.SettingsService.Reload()）
                Result = "download";
                ShowStatus("已从云端恢复设置（关闭窗口后到设置页查看效果）", Color.FromRgb(0x3C, 0xA8, 0x5C));
            }
            catch (Exception ex)
            {
                ShowStatus("下载失败：" + ex.Message, Color.FromRgb(0xD4, 0x50, 0x45));
            }
            finally { SetBusy(false); }
        }
    }

    /// <summary>通用确认弹窗（替代 MessageBox OK/Cancel）：标题 + 消息 + 确认/取消按钮，主题跟随系统。</summary>
    public sealed class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, string okText = "确定", string cancelText = "取消")
        {
            bool light = ShoreHue.Infrastructure.Utils.SystemTheme.IsLightTheme();
            var cBg = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x1E, 0x1E, 0x1E);
            var cText = light ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White;
            var cSub = light ? Color.FromRgb(0x66, 0x66, 0x66) : Color.FromRgb(0xBB, 0xBB, 0xBB);

            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(cBg);
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(cText)
            });
            root.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush(cSub),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var ok = new Button { Content = okText, Width = 90, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
            ok.Style = (Style)FindResource("Win11Button");
            ok.Click += (_, _) => { DialogResult = true; Close(); };
            bottom.Children.Add(ok);
            var cancel = new Button { Content = cancelText, Width = 80, Height = 28, FontSize = 12 };
            cancel.Style = (Style)FindResource("Win11Button");
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            bottom.Children.Add(cancel);
            root.Children.Add(bottom);
            Content = root;
        }
    }
}
