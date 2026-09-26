using System;
using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;

namespace ShoreHue.UI.Panels
{
    public partial class TaskbarView
    {
        private new void UpdateLayout()
        {
            if (_isLayoutUpdating) return;
            if (this.ActualHeight < 10 || this.ActualWidth < 10)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateLayout()), DispatcherPriority.Loaded);
                return;
            }

            _isLayoutUpdating = true;

            try
            {
                double availableWidth = Math.Max(1, this.ActualWidth);
                double availableHeight = Math.Max(1, this.ActualHeight);
                double iconSize = Math.Max(16, this.IconSize);
                int shortcutCount = _shortcuts.Count;
                int windowCount = _windowDisplay.Count;   // ★ 分组标签也算一个窗口位

                double aspectRatio = availableWidth / availableHeight;
                // ★ 布局阈值接入设置（默认 0.43，值越大越倾向竖向）
                //   加迟滞：切换后需要超过阈值 ±0.15 才再切换，避免拖拽调整大小时反复重建抽搐
                double layoutThreshold = _settings.HorizontalLayoutThreshold > 0
                    ? _settings.HorizontalLayoutThreshold
                    : 1.0;
                LayoutMode newMode;
                if (_currentLayoutMode == LayoutMode.Horizontal)
                {
                    newMode = aspectRatio >= layoutThreshold - 0.15
                        ? LayoutMode.Horizontal
                        : LayoutMode.Vertical;
                }
                else
                {
                    newMode = aspectRatio >= layoutThreshold
                        ? LayoutMode.Horizontal
                        : LayoutMode.Vertical;
                }

                bool layoutChanged =
                    newMode != _currentLayoutMode ||
                    Math.Abs(iconSize - _cachedIconSize) > 0.5 ||
                    shortcutCount != _cachedShortcutCount ||
                    windowCount != _cachedWindowCount;

                if (!layoutChanged) return;

                _currentLayoutMode = newMode;
                _cachedIconSize = iconSize;
                _cachedShortcutCount = shortcutCount;
                _cachedWindowCount = windowCount;

                if (newMode == LayoutMode.Horizontal)
                {
                    _shortcutRows = (shortcutCount > 0) ? 1 : 0;
                    _windowRows = (windowCount > 0) ? 1 : 0;
                    _totalRows = 1;
                    _isSingleRowLayout = true;
                    _cachedHasDivider = (shortcutCount > 0 && windowCount > 0);
                }
                else
                {
                    double shortcutItemWidth = Math.Max(MIN_ITEM_WIDTH, iconSize + ITEM_PADDING * 2);
                    double windowItemWidth = Math.Max(80, _settings.TagWidth + 30 + 8);
                    int maxShortcutPerRow = Math.Max(1, (int)Math.Floor((availableWidth - 8) / (shortcutItemWidth + 4)));
                    int maxWindowPerRow = Math.Max(1, (int)Math.Floor((availableWidth - 8) / (windowItemWidth + 4)));

                    int shortcutRows = (shortcutCount > 0) ? (int)Math.Ceiling((double)shortcutCount / maxShortcutPerRow) : 0;
                    int windowRows = (windowCount > 0) ? (int)Math.Ceiling((double)windowCount / maxWindowPerRow) : 0;

                    double rowHeight = iconSize + ITEM_PADDING * 2;
                    int maxRowsAvailable = (int)Math.Floor(availableHeight / rowHeight);
                    if (maxRowsAvailable < 1) maxRowsAvailable = 1;

                    int totalRows = shortcutRows + windowRows;
                    if (totalRows > maxRowsAvailable && maxRowsAvailable > 0)
                    {
                        if (shortcutCount > 0 && windowCount > 0)
                        {
                            double ratio = (double)shortcutCount / (shortcutCount + windowCount);
                            int rowsForShortcut = Math.Max(1, (int)Math.Round(ratio * maxRowsAvailable));
                            int rowsForWindow = maxRowsAvailable - rowsForShortcut;
                            if (rowsForWindow < 1) { rowsForShortcut--; rowsForWindow = 1; }
                            if (rowsForShortcut < 1) { rowsForWindow--; rowsForShortcut = 1; }
                            shortcutRows = Math.Min(shortcutRows, rowsForShortcut);
                            windowRows = Math.Min(windowRows, rowsForWindow);
                        }
                        else if (shortcutCount > 0)
                            shortcutRows = Math.Min(shortcutRows, maxRowsAvailable);
                        else if (windowCount > 0)
                            windowRows = Math.Min(windowRows, maxRowsAvailable);
                    }

                    _shortcutRows = shortcutRows;
                    _windowRows = windowRows;
                    _totalRows = shortcutRows + windowRows;
                    _isSingleRowLayout = (_totalRows == 1);
                    _cachedHasDivider = (shortcutCount > 0 && windowCount > 0);
                }

                RebuildLayout(availableWidth, availableHeight, iconSize);
            }
            finally
            {
                _isLayoutUpdating = false;
            }
        }

        private void RebuildLayout(double availableWidth, double availableHeight, double iconSize)
        {
            var oldShortcutHandler = _shortcutScrollHandler;
            var oldWindowHandler = _windowScrollHandler;

            MainGrid.Children.Clear();
            MainGrid.RowDefinitions.Clear();
            MainGrid.ColumnDefinitions.Clear();

            bool isSingleRow = _isSingleRowLayout;
            bool hasDivider = _cachedHasDivider;

            int totalRows;
            if (isSingleRow)
                totalRows = 1;
            else
            {
                totalRows = _shortcutRows + (hasDivider ? 1 : 0) + _windowRows;
                if (totalRows < 1) totalRows = 1;
            }

            double savedOffset = _settings.DividerOffset;
            if (savedOffset < 0.1 || savedOffset > 0.9) savedOffset = 0.4;

            for (int i = 0; i < totalRows; i++)
            {
                if (!isSingleRow && hasDivider && i == _shortcutRows)
                {
                    MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DIVIDER_THICKNESS + DIVIDER_MARGIN * 2, GridUnitType.Pixel) });
                }
                else
                {
                    if (!isSingleRow && hasDivider)
                    {
                        int rowIndex = i;
                        if (rowIndex < _shortcutRows)
                        {
                            int totalShortcutRows = _shortcutRows;
                            double ratio = savedOffset;
                            double height = (availableHeight - (DIVIDER_THICKNESS + DIVIDER_MARGIN * 2)) * ratio / totalShortcutRows;
                            height = Math.Max(25, height);
                            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height, GridUnitType.Pixel) });
                        }
                        else if (rowIndex > _shortcutRows)
                        {
                            int totalWindowRows = _windowRows;
                            double ratio = 1 - savedOffset;
                            double height = (availableHeight - (DIVIDER_THICKNESS + DIVIDER_MARGIN * 2)) * ratio / totalWindowRows;
                            height = Math.Max(25, height);
                            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height, GridUnitType.Pixel) });
                        }
                        else
                        {
                            MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                        }
                    }
                    else
                    {
                        MainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    }
                }
            }

            if (isSingleRow)
            {
                if (hasDivider)
                {
                    double totalWidth = availableWidth;
                    double iconWidth = iconSize + ITEM_PADDING * 2 + 4;
                    double twoIconsWidth = iconWidth * 2 + 8;
                    double minShortcutWidth = Math.Min(twoIconsWidth, totalWidth * 0.35);
                    minShortcutWidth = Math.Max(60, minShortcutWidth);

                    // ★ 内容较少时（刚添加第一/第二个快捷方式），让快捷方式列贴合实际内容，
                    //   避免“一个图标 + 分隔线前一大片空白”。
                    double contentShortcutWidth = _shortcuts.Count * iconWidth + 8;
                    if (contentShortcutWidth <= totalWidth * 0.45)
                    {
                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    }
                    else
                    {
                        double shortcutWidth = Math.Max(minShortcutWidth, totalWidth * savedOffset);
                        shortcutWidth = Math.Min(totalWidth - minShortcutWidth - DIVIDER_THICKNESS - 4, shortcutWidth);
                        if (shortcutWidth < minShortcutWidth || shortcutWidth > totalWidth - 10)
                            shortcutWidth = totalWidth * 0.4;

                        double windowWidth = totalWidth - shortcutWidth - DIVIDER_THICKNESS - 4;
                        if (windowWidth < 10)
                        {
                            shortcutWidth = totalWidth * 0.4;
                            windowWidth = totalWidth - shortcutWidth - DIVIDER_THICKNESS - 4;
                            if (windowWidth < 10) windowWidth = 10;
                        }

                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shortcutWidth, GridUnitType.Pixel) });
                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(windowWidth, GridUnitType.Pixel) });
                    }
                }
                else
                {
                    MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }
            }
            else
            {
                MainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            int currentRow = 0;

            if (_shortcutRows > 0 && _shortcuts.Count > 0)
            {
                var scroller = CreateScrollViewer("ShortcutScroller");
                var itemsControl = CreateItemsControl((IEnumerable)_shortcuts, "ShortcutTemplate");
                scroller.Content = itemsControl;
                _shortcutScrollViewer = scroller;

                Grid.SetRow(scroller, currentRow);
                Grid.SetRowSpan(scroller, _shortcutRows);
                Grid.SetColumn(scroller, isSingleRow ? 0 : 0);
                MainGrid.Children.Add(scroller);
                currentRow += _shortcutRows;
            }
            else
                _shortcutScrollViewer = null;

            if (hasDivider)
            {
                if (isSingleRow)
                    AddVerticalDivider();
                else
                    AddHorizontalDivider();
            }
            else
            {
                _dividerElement = null;
            }

            if (_windowRows > 0 && _windowDisplay.Count > 0)
            {
                var scroller = CreateScrollViewer("WindowScroller");
                var selector = new TaskbarTemplateSelector
                {
                    WindowTemplate = (DataTemplate)this.Resources["WindowTemplate"],
                    GroupTemplate = (DataTemplate)this.Resources["GroupTemplate"]
                };
                var itemsControl = CreateItemsControl((IEnumerable)_windowDisplay, "WindowTemplate", selector);
                scroller.Content = itemsControl;
                _windowScrollViewer = scroller;

                Grid.SetRow(scroller, currentRow);
                Grid.SetRowSpan(scroller, _windowRows);
                Grid.SetColumn(scroller, isSingleRow ? 2 : 0);
                MainGrid.Children.Add(scroller);
                currentRow += _windowRows;
            }
            else
                _windowScrollViewer = null;

            if (_shortcutScrollViewer != null)
            {
                if (oldShortcutHandler != null)
                {
                    _shortcutScrollHandler = oldShortcutHandler;
                    // ★ 必须把当前布局方向一并传回去：`Reattach` 的 isHorizontal 默认值是 true，
                    //   布局重建（切换布局模式/尺寸变化）后垂直布局的 handler 会被悄悄改成水平 ——
                    //   于是它去读 position.X、去滚 HorizontalOffset，而垂直布局下水平滚动是 Disabled
                    //   → 鼠标移到边缘自动滚动彻底失效。
                    _shortcutScrollHandler.Reattach(_shortcutScrollViewer, IsHorizontalLayout(_currentLayoutMode));
                }
                else
                    _shortcutScrollHandler = new TaskbarScrollHandler(_shortcutScrollViewer, "快捷方式");
                SetScrollDirection(_shortcutScrollViewer, _currentLayoutMode);
            }
            else
            {
                // ★ 复用 handler 停用（Detach 停计时器、解绑事件），避免滚动计时器滞留
                oldShortcutHandler?.Detach();
                _shortcutScrollHandler = null;
            }

            if (_windowScrollViewer != null)
            {
                if (oldWindowHandler != null)
                {
                    _windowScrollHandler = oldWindowHandler;
                    _windowScrollHandler.Reattach(_windowScrollViewer, IsHorizontalLayout(_currentLayoutMode));
                }
                else
                    _windowScrollHandler = new TaskbarScrollHandler(_windowScrollViewer, "任务标签");
                SetScrollDirection(_windowScrollViewer, _currentLayoutMode);
            }
            else
            {
                oldWindowHandler?.Detach();
                _windowScrollHandler = null;
            }
        }
    }
}
