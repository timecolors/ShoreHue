using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShoreHue.Core.Services.Configuration;

namespace ShoreHue.UI.Panels
{
    public partial class TaskbarView
    {
        private void Divider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_dividerElement == null) return;

            if (this.ActualWidth < DIVIDER_MIN_WIDTH)
            {
                e.Handled = true;
                return;
            }

            _isDividerDragging = true;
            _dividerElement.CaptureMouse();

            if (_isSingleRowLayout)
            {
                _dividerStartPos = e.GetPosition(this).X;
                _dividerStartSize = MainGrid.ColumnDefinitions[0].ActualWidth;
                _dividerTotalSize = this.ActualWidth;
            }
            else
            {
                _dividerStartPos = e.GetPosition(this).Y;
                double totalShortcutHeight = 0;
                int shortcutRowCount = _shortcutRows > 0 ? _shortcutRows : 1;
                for (int i = 0; i < shortcutRowCount && i < MainGrid.RowDefinitions.Count; i++)
                {
                    totalShortcutHeight += MainGrid.RowDefinitions[i].ActualHeight;
                }
                _dividerStartSize = totalShortcutHeight;
                _dividerTotalSize = this.ActualHeight;
            }

            e.Handled = true;
        }

        private void Divider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDividerDragging || _dividerElement == null) return;

            if (this.ActualWidth < DIVIDER_MIN_WIDTH)
            {
                ForceDividerRelease();
                return;
            }

            try
            {
                double iconSize = Math.Max(16, this.IconSize);
                double minShortcutDimension = iconSize + ITEM_PADDING * 2 + 2;

                if (_isSingleRowLayout)
                {
                    double currentPos = e.GetPosition(this).X;
                    double delta = currentPos - _dividerStartPos;
                    double totalWidth = this.ActualWidth;
                    if (totalWidth < 10) return;

                    double minWidth = minShortcutDimension;
                    double newShortcutWidth = _dividerStartSize + delta;
                    newShortcutWidth = Math.Max(minWidth, Math.Min(totalWidth - minWidth - DIVIDER_THICKNESS - 4, newShortcutWidth));
                    double newWindowWidth = totalWidth - newShortcutWidth - DIVIDER_THICKNESS - 4;

                    if (newShortcutWidth > 10 && newWindowWidth > 10)
                    {
                        MainGrid.ColumnDefinitions[0].Width = new GridLength(newShortcutWidth, GridUnitType.Pixel);
                        MainGrid.ColumnDefinitions[2].Width = new GridLength(newWindowWidth, GridUnitType.Pixel);
                        _settings.DividerOffset = newShortcutWidth / totalWidth;
                    }
                }
                else
                {
                    double currentPos = e.GetPosition(this).Y;
                    double delta = currentPos - _dividerStartPos;
                    double totalHeight = this.ActualHeight;
                    if (totalHeight < 10) return;

                    double minHeight = Math.Max(25, minShortcutDimension);
                    double maxShortcutHeight = totalHeight - 25 - DIVIDER_THICKNESS - 4;
                    double newShortcutTotalHeight = _dividerStartSize + delta;
                    newShortcutTotalHeight = Math.Max(minHeight, Math.Min(maxShortcutHeight, newShortcutTotalHeight));
                    double newWindowTotalHeight = totalHeight - newShortcutTotalHeight - DIVIDER_THICKNESS - 4;

                    if (newShortcutTotalHeight >= minHeight && newWindowTotalHeight >= 25)
                    {
                        int shortcutRowCount = _shortcutRows > 0 ? _shortcutRows : 1;
                        int windowRowCount = _windowRows > 0 ? _windowRows : 1;

                        double shortcutHeightPerRow = newShortcutTotalHeight / shortcutRowCount;
                        for (int i = 0; i < shortcutRowCount && i < MainGrid.RowDefinitions.Count; i++)
                        {
                            MainGrid.RowDefinitions[i].Height = new GridLength(shortcutHeightPerRow, GridUnitType.Pixel);
                        }

                        double windowHeightPerRow = newWindowTotalHeight / windowRowCount;
                        int windowStartIndex = shortcutRowCount + 1;
                        for (int i = 0; i < windowRowCount && (windowStartIndex + i) < MainGrid.RowDefinitions.Count; i++)
                        {
                            MainGrid.RowDefinitions[windowStartIndex + i].Height = new GridLength(windowHeightPerRow, GridUnitType.Pixel);
                        }

                        _settings.DividerOffset = newShortcutTotalHeight / totalHeight;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Divider_PreviewMouseMove error: {ex.Message}");
                ForceDividerRelease();
            }
            finally
            {
                e.Handled = true;
            }
        }

        private void Divider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            ForceDividerRelease();
            e.Handled = true;
        }

        private void ForceDividerRelease()
        {
            if (_dividerElement != null)
            {
                try
                {
                    if (_dividerElement.IsMouseCaptured)
                        _dividerElement.ReleaseMouseCapture();
                }
                catch { /* 尽力而为：捕获没释放也只影响这一次拖分隔条（下面已把 _isDividerDragging 复位） */ }
            }
            _isDividerDragging = false;
            Mouse.OverrideCursor = null;
        }
    }
}