using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.Core.Services;
using ShoreHue.UI.Widgets;
using WinForms = System.Windows.Forms;

namespace ShoreHue.Builtin
{
    // 便签 · 纯代码版（动态编译运行，风格与内置一致：便签条+颜色+标题/内容编辑）
    public class NotePanel : UserControl, IWidget
    {
        private readonly INoteService _noteService;
        private bool _isUpdating;
        private StackPanel _tabPanel;
        private TextBox _titleEditor, _contentEditor;
        private Button _btnToggleTitle, _btnColor, _btnNew, _btnDelete;
        private TextBlock _statusText;

        public NotePanel()
        {
            // ★ 安全 v2：外来来源只能用窄接口
            _noteService = ShoreHue.UI.Widgets.HostCapabilities.Notes;
            BuildUi();
            if (_noteService != null)
            {
                _noteService.NotesChanged += (_, _) => Dispatcher.Invoke(RefreshAll);
                RefreshAll();
            }
        }

        public string Name => "便签";
        public UserControl CreateView() => this;
        public void OnActivated() { if (_noteService != null) _noteService.SetCurrentNote(_noteService.CurrentNote); RefreshAll(); }
        public void OnDeactivated() { }

        private void BuildUi()
        {
            // 标签栏
            var tabScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _tabPanel = new StackPanel { Orientation = Orientation.Horizontal };
            tabScroll.Content = _tabPanel;

            _btnToggleTitle = new Button { Content = "标", Width = 24, Height = 24, FontSize = 14, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "显示/隐藏标题" };
            _btnToggleTitle.Click += (_, _) => { if (_noteService?.CurrentNote != null) { _noteService.CurrentNote.ShowTitle = !_noteService.CurrentNote.ShowTitle; RefreshAll(); } };
            _btnColor = new Button { Content = "色", Width = 24, Height = 24, FontSize = 14, Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "颜色" };
            _btnColor.Click += (_, _) => ColorPicker();
            var headRight = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            headRight.Children.Add(_btnToggleTitle); headRight.Children.Add(_btnColor);

            var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(tabScroll, 0); Grid.SetColumn(headRight, 1);
            head.Children.Add(tabScroll); head.Children.Add(headRight);

            // 标题 + 内容
            _titleEditor = new TextBox { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            _titleEditor.TextChanged += (_, _) => { if (!_isUpdating && _noteService?.CurrentNote != null) _noteService.CurrentNote.Title = _titleEditor.Text; };
            _contentEditor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(238, 238, 238)), Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinHeight = 120 };
            _contentEditor.TextChanged += (_, _) => { if (!_isUpdating && _noteService?.CurrentNote != null) _noteService.CurrentNote.Content = _contentEditor.Text; };
            var editCol = new StackPanel { Children = { _titleEditor, _contentEditor } };
            var contentScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 300, Content = editCol };

            var root = new Grid { Margin = new Thickness(2) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(head, 0); Grid.SetRow(contentScroll, 1);
            root.Children.Add(head); root.Children.Add(contentScroll);
            Content = root;
        }

        private void RefreshAll()
        {
            if (_tabPanel == null || _noteService == null) return;
            _tabPanel.Children.Clear();
            foreach (var note in _noteService.Notes)
            {
                note.IsCurrent = note == _noteService.CurrentNote;
                var tab = new Border
                {
                    Background = TabBrush(note.Color),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    BorderThickness = note.IsCurrent ? new Thickness(2) : new Thickness(0),
                    BorderBrush = note.IsCurrent ? new SolidColorBrush(Color.FromRgb(0, 120, 212)) : Brushes.Transparent,
                    Tag = note
                };
                tab.MouseLeftButtonUp += Tab_Click;
                tab.Child = new TextBlock { Text = note.Title, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)), MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis };
                _tabPanel.Children.Add(tab);
            }
            var current = _noteService.CurrentNote;
            _isUpdating = true;
            _titleEditor.Text = current?.Title ?? "";
            _titleEditor.Visibility = current != null && current.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            _contentEditor.Text = current?.Content ?? "";
            _isUpdating = false;
            UpdateStatus();
        }

        private static SolidColorBrush TabBrush(string color)
        {
            try { if (!string.IsNullOrEmpty(color)) return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); } catch { }
            return new SolidColorBrush(Color.FromRgb(0, 0, 0));
        }

        private void Tab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border b || b.Tag is not NoteItem note || _noteService == null) return;
            _noteService.SetCurrentNote(note);
            RefreshAll();
        }

        private void UpdateStatus()
        {
            // 状态栏由 Footer 提供，面板内简洁即可
        }

        private void ColorPicker()
        {
            var current = _noteService?.CurrentNote;
            if (current == null) return;
            using var dialog = new WinForms.ColorDialog();
            dialog.Color = HexToDrawing(current.Color);
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                string hex = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                _noteService.UpdateNoteColor(current, hex);
                RefreshAll();
            }
        }

        private System.Drawing.Color HexToDrawing(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return System.Drawing.Color.FromArgb(255, 0, 0, 0);
                if (hex.StartsWith("#")) hex = hex.Substring(1);
                if (hex.Length == 6)
                    return System.Drawing.Color.FromArgb(255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { }
            return System.Drawing.Color.FromArgb(255, 0, 0, 0);
        }
    }
}