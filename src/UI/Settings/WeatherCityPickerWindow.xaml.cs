using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Theme;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ShoreHue.UI.Settings
{
    /// <summary>
    /// 天气城市选择器：
    ///  - 搜索任意城市（输入即联网联想 + 实时天气预览）
    ///  - 最近使用（记住选过的城市）
    ///  - 国内 / 国际热门城市快捷选择
    /// 选中后 SelectedCity 为城市名（天气请求时再按名 geocoding 定位）。
    /// </summary>
    public partial class WeatherCityPickerWindow : Window
    {
        /// <summary>用户选中的城市名；未选择则为空。</summary>
        public string SelectedCity { get; private set; } = "";

        private static readonly string[] DomesticCities =
        {
            "北京", "上海", "广州", "深圳", "杭州", "成都", "武汉", "西安", "重庆", "南京", "天津", "苏州"
        };

        private static readonly string[] InternationalCities =
        {
            "东京", "首尔", "新加坡", "曼谷", "伦敦", "巴黎", "柏林", "纽约", "洛杉矶", "悉尼", "迪拜", "莫斯科"
        };

        private const int MaxRecent = 8;

        private readonly DispatcherTimer _searchTimer;
        private readonly SettingsData _settingsData;

        public WeatherCityPickerWindow(string currentCity = "")
        {
            Icon = AppIconHelper.LoadAppIcon();
            InitializeComponent();

            _settingsData = SettingsFileManager.Load();

            // 最近使用
            var recent = _settingsData.WeatherRecentCities ?? new List<string>();
            if (recent.Count > 0)
            {
                RecentTitle.Visibility = Visibility.Visible;
                foreach (var city in recent)
                {
                    RecentPanel.Children.Add(CreateCityButton(city, CityButtonKind.Recent));
                }
            }

            // 国内 / 国际热门
            foreach (var city in DomesticCities)
            {
                DomesticPanel.Children.Add(CreateCityButton(city, CityButtonKind.Popular));
            }
            foreach (var city in InternationalCities)
            {
                InternationalPanel.Children.Add(CreateCityButton(city, CityButtonKind.Popular));
            }

            // 搜索防抖：输入即联想 + 预览该城市天气
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer.Stop();
                await SearchAsync(TxtSearch.Text);
            };

            if (!string.IsNullOrEmpty(currentCity))
            {
                TxtSearch.Text = currentCity;
            }
        }

        private enum CityButtonKind { Popular, Recent }

        private Button CreateCityButton(string city, CityButtonKind kind)
        {
            var btn = new Button
            {
                Content = city,
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 12,
                // ★ 修复：浅色窗口用 Win11Button（白底深字），原 FlatButton 是深色面板样式（前景 #EEEEEE），在 #F9F9F9 背景上文字几乎不可见
                Style = (Style)FindResource("Win11Button"),
                Tag = city
            };
            btn.Click += (_, _) => CityButton_Click(city, kind);
            return btn;
        }

        private async void CityButton_Click(string city, CityButtonKind kind)
        {
            SelectedCity = city;
            TxtSearch.Text = city;   // 触发搜索联想 + 天气预览
            await PreviewWeatherAsync(city);
        }

        private async Task SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ResultList.ItemsSource = null;
                return;
            }
            try
            {
                var cities = await WeatherService.SearchCitiesAsync(query.Trim());
                if (TxtSearch.Text.Trim() != query.Trim()) return; // 输入已变化，丢弃过期结果
                ResultList.ItemsSource = cities;
            }
            catch (Exception ex)
            {
                // 搜索失败 → 列表不更新（用户看到的是上一次的结果）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[天气] 搜索城市失败（列表未更新）：{ex.Message}");
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// <summary>输入/选择的城市名 → 联网获取实时天气并预览。</summary>
        private async Task PreviewWeatherAsync(string city)
        {
            if (string.IsNullOrWhiteSpace(city))
            {
                WeatherPreviewBox.Visibility = Visibility.Collapsed;
                return;
            }

            string query = city.Trim();
            WeatherPreviewBox.Visibility = Visibility.Visible;
            WeatherPreview.Text = "正在查询 " + query + " 的天气…";

            try
            {
                var w = await WeatherService.GetWeatherAsync(query);
                if (TxtSearch.Text.Trim() != query) return; // 输入已变化，丢弃过期结果
                if (w.HasValue)
                {
                    WeatherPreview.Text = query + " · " + w.Value.Text;
                }
                else
                {
                    WeatherPreview.Text = "未找到 " + query + " 的天气数据，请检查城市名或稍后重试";
                }
            }
            catch
            {
                WeatherPreview.Text = "查询天气失败，请检查网络";
            }
        }

        private async void ResultList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ResultList.SelectedItem is WeatherService.CitySuggestion city)
            {
                SelectedCity = city.Name;
                await PreviewWeatherAsync(city.Name);
                if (!IsLoaded || !IsVisible) return;   // ★ 防窗口已关闭后设 DialogResult
                SaveRecent(city.Name);
                DialogResult = true;
            }
        }

        private async void Ok_Click(object sender, RoutedEventArgs e)
        {
            string city = "";
            if (ResultList.SelectedItem is WeatherService.CitySuggestion s)
            {
                city = s.Name;
            }
            else if (!string.IsNullOrWhiteSpace(TxtSearch.Text))
            {
                city = TxtSearch.Text.Trim();
            }

            if (string.IsNullOrEmpty(city)) return;

            SelectedCity = city;
            await PreviewWeatherAsync(city); // 确认前再看一眼该城市天气
            if (!IsLoaded || !IsVisible) return;   // ★ 防窗口已关闭后设 DialogResult
            SaveRecent(city);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        /// <summary>把城市写入"最近使用"（去重、最新在前、上限 8 个），持久化到 config.json。</summary>
        private void SaveRecent(string city)
        {
            try
            {
                // ★ 修复：保存前重新加载最新数据（避免用窗口打开时的旧副本覆盖
                //   其他设置的新改动——同构于"设置窗口改动不落盘"的问题）。
                //   只更新 recent 字段再落盘，其余字段保持磁盘当前值。
                var latest = SettingsFileManager.Load();
                var recent = latest.WeatherRecentCities ?? new List<string>();
                recent.RemoveAll(c => string.Equals(c, city, StringComparison.OrdinalIgnoreCase));
                recent.Insert(0, city);
                if (recent.Count > MaxRecent)
                {
                    recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
                }
                latest.WeatherRecentCities = recent;
                SettingsFileManager.Save(latest);

                // 同步回本地副本（供本窗口后续使用）
                _settingsData.WeatherRecentCities = recent;
            }
            catch (Exception ex)
            {
                // 最近城市没落盘（下次打开选择器时少了历史）
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning($"[天气] 保存最近城市失败：{ex.Message}");
            }
        }
    }
}
