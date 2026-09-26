using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.AppHelper
{
    public partial class MediaControlView : UserControl
    {
        private readonly DispatcherTimer _refreshTimer;
        private bool _refreshing;

        public MediaControlView()
        {
            InitializeComponent();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += async (_, _) => await RefreshAsync();
            Loaded += async (_, _) => await RefreshAsync();
            Unloaded += (_, _) => _refreshTimer.Stop();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var sessions = await MediaSessionController.GetSessionsAsync();
                SessionList.ItemsSource = sessions.Select(s => new MediaSessionItem(s)).ToList();
                StatusText.Text = sessions.Count > 0
                    ? string.Format(LocalizationManager.Instance["Media_Detected"], sessions.Count)
                    : LocalizationManager.Instance["Media_None"];
                if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = LocalizationManager.Instance["Media_Unavailable"];
                System.Diagnostics.Debug.WriteLine($"刷新媒体会话失败: {ex.Message}");
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshAsync();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (GetSession(sender) is { } item) await MediaSessionController.PrevAsync(item.Info);
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (GetSession(sender) is { } item) await MediaSessionController.TogglePlayPauseAsync(item.Info);
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (GetSession(sender) is { } item) await MediaSessionController.NextAsync(item.Info);
        }

        private static MediaSessionItem? GetSession(object sender)
        {
            return sender is FrameworkElement fe && fe.DataContext is MediaSessionItem item
                ? item
                : null;
        }

        private void KeyPrev_Click(object sender, RoutedEventArgs e) => MediaKeyHelper.Press(MediaKeyHelper.PrevTrack);
        private void KeyPlayPause_Click(object sender, RoutedEventArgs e) => MediaKeyHelper.Press(MediaKeyHelper.PlayPause);
        private void KeyNext_Click(object sender, RoutedEventArgs e) => MediaKeyHelper.Press(MediaKeyHelper.NextTrack);
        private void KeyVolDown_Click(object sender, RoutedEventArgs e) => MediaKeyHelper.Press(MediaKeyHelper.VolumeDown);
        private void KeyVolUp_Click(object sender, RoutedEventArgs e) => MediaKeyHelper.Press(MediaKeyHelper.VolumeUp);
    }

    /// <summary>
    /// 会话列表项（绑定友好显示字段，不直接暴露 WinRT 对象）。
    /// </summary>
    public class MediaSessionItem
    {
        public MediaSessionInfo Info { get; }
        public string AppName => Info.AppName;
        public string DisplayText => Info.DisplayText;
        /// <summary>
        /// 专辑封面。★ 必须在这里显式暴露：XAML 里绑的是 `{Binding Thumbnail}`，
        /// 而 DataContext 是本类（不是 `MediaSessionInfo`）—— 以前本类没有这个属性，
        /// 绑定静默失败（WPF 只写调试输出），于是封面**永远显示不出来**，
        /// 而控制器每次轮询仍然照旧解码一张 BitmapImage（白付代价）。
        /// </summary>
        public System.Windows.Media.ImageSource? Thumbnail => Info.Thumbnail;
        public Geometry PlayGlyph =>
            (Geometry)(Info.IsPlaying
                ? Application.Current.FindResource("IconPause")
                : Application.Current.FindResource("IconPlay"));

        public MediaSessionItem(MediaSessionInfo info)
        {
            Info = info;
        }

    }
}
