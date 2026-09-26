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
        // ========== AI 助手设置（独立存储，随实时保存一起写入） ==========

        private bool _loadingAi;

        private void LoadAiSettings()
        {
            var ai = AiSettingsStore.Load();
            _loadingAi = true;
            chkAiEnabled.IsChecked = ai.Enabled;
            txtAiBaseUrl.Text = ai.BaseUrl;
            pwdAiKey.Password = ai.ApiKey;
            txtAiModel.Text = ai.Model;
            txtAiSystemPrompt.Text = ai.SystemPrompt;
            sldAiTemperature.Value = Math.Clamp(ai.Temperature, 0, 2);
            txtAiTemperature.Text = ai.Temperature.ToString("F1");
            txtAiContextWindow.Text = ai.ContextWindowTokens.ToString();
            chkAiWebSearch.IsChecked = ai.EnableWebSearch;
            chkAiReasoning.IsChecked = ai.EnableReasoning;

            cmbAiProvider.Items.Clear();
            foreach (var (name, display, _, _) in AiSettings.Presets)
            {
                cmbAiProvider.Items.Add(new ComboBoxItem { Content = display, Tag = name });
            }
            int idx = -1;
            for (int i = 0; i < AiSettings.Presets.Length; i++)
            {
                if (string.Equals(ai.BaseUrl.TrimEnd('/'),
                        AiSettings.Presets[i].Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            cmbAiProvider.SelectedIndex = idx;
            _loadingAi = false;
        }

        private void CmbAiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingAi) return;
            if (cmbAiProvider.SelectedItem is ComboBoxItem item && item.Tag is string name)
            {
                var preset = Array.Find(AiSettings.Presets, p => p.Name == name);
                if (preset.Name != null)
                {
                    txtAiBaseUrl.Text = preset.Url;
                    txtAiModel.Text = preset.Model;
                }
            }
        }

        private async void BtnAiTest_Click(object sender, RoutedEventArgs e)
        {
            var testSettings = new AiSettings
            {
                Enabled = true,
                BaseUrl = string.IsNullOrWhiteSpace(txtAiBaseUrl.Text) ? "https://api.deepseek.com/v1" : txtAiBaseUrl.Text.Trim(),
                ApiKey = pwdAiKey.Password ?? "",
                Model = string.IsNullOrWhiteSpace(txtAiModel.Text) ? "deepseek-chat" : txtAiModel.Text.Trim()
            };

            btnAiTest.IsEnabled = false;
            txtAiTestStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_Testing"];
            txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120));
            try
            {
                using var client = new AiChatClient();
                string? err = await client.TestConnectionAsync(testSettings);
                if (err == null)
                {
                    txtAiTestStatus.Text = ShoreHue.UI.Localization.LocalizationManager.Instance["Set_ConnOk"];
                    txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(60, 170, 90));
                }
                else
                {
                    txtAiTestStatus.Text = err;
                    txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
                }
            }
            catch (Exception ex)
            {
                txtAiTestStatus.Text = ex.Message;
                txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
            }
            finally
            {
                btnAiTest.IsEnabled = true;
            }
        }

        /// <summary>AI 助手配置（独立存储，随实时保存一起写入）。</summary>
        private void SaveAiSettings()
        {
            try
            {
                var ai = new AiSettings
                {
                    Enabled = chkAiEnabled.IsChecked ?? false,
                    BaseUrl = txtAiBaseUrl.Text.Trim(),
                    ApiKey = pwdAiKey.Password ?? "",
                    Model = txtAiModel.Text.Trim(),
                    SystemPrompt = txtAiSystemPrompt.Text,
                    Temperature = sldAiTemperature.Value,
                    ContextWindowTokens = int.TryParse(txtAiContextWindow.Text, out var cw) ? cw : 8000,
                    EnableWebSearch = chkAiWebSearch.IsChecked ?? false,
                    EnableReasoning = chkAiReasoning.IsChecked ?? false
                };
                string err = ShoreHue.Core.Services.Ai.AiSettingsStore.Save(ai);
                if (err.Length > 0)
                {
                    // 如实提示：不要把"没保存成功"显示成保存成功
                    txtAiTestStatus.Text = err;
                    txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
                }
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Error("保存 AI 设置失败", ex);
                txtAiTestStatus.Text = "保存 AI 设置失败：" + ex.Message;
                txtAiTestStatus.Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 70));
            }
        }
    }
}
