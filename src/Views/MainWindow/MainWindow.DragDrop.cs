using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using CustomBrowser.Helpers;
using CustomBrowser.Models;
using CustomBrowser.Services;
namespace CustomBrowser
{
    public partial class MainWindow
    {
        // ------------------------------------------------------------------
        // Tab drag: reorder inside the strip, or pull a tab out into its own window
        // ------------------------------------------------------------------
        private static bool IsInsideButton(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is Button) return true;
                if (d is ListBoxItem) return false;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }
        // Capture the press at the ListBox level, before ListBoxItem's class handler can
        // select the tab. The tab isn't selected until mouse-up unless a real drag starts.
        private void TabListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListBoxItem)
            {
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            if (source is ListBoxItem item && item.DataContext is BrowserTab tab)
            {
                _dragTab = tab;
                _dragStartPoint = e.GetPosition(this);
                _dragPointerOffset = e.GetPosition(item);
                _tabDragActive = false;
                e.Handled = true;
                SelectedTab = tab; // switch on press (Chrome/Firefox), not on release: no click-length delay
            }
        }
        private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // A torn-off window is driven by its own polling timer; normal drag handling must stay out of the way.
            if (_dragTab == null || _externalDraggedWindow != null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndTabDrag();
                return;
            }
            Point inWindow = e.GetPosition(this);
            if (!_tabDragActive)
            {
                if (Math.Abs(inWindow.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(inWindow.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }
                _tabDragActive = true;
                SelectedTab = _dragTab;
                HideTabPreview();
                CaptureMouse();
            }
            Point inStrip = e.GetPosition(TabListBox);
            // A held drag can move directly into another browser window's tab strip.
            MainWindow? directTarget = GetBrowserWindowAtCursor(GetCursorDevicePoint(), this, tabStripOnly: true);
            if (directTarget != null && !ReferenceEquals(directTarget, this))
            {
                BrowserTab transferring = _dragTab;
                EndTabDrag();
                TransferTabToWindow(directTarget, transferring, GetCursorDevicePoint());
                return;
            }
            bool pulledOut = inStrip.Y > TabListBox.ActualHeight + TearOffDistance
                          || inStrip.Y < -TearOffDistance
                          || inWindow.X < -TearOffDistance
                          || inWindow.X > ActualWidth + TearOffDistance;
            if (pulledOut)
            {
                BrowserTab tab = _dragTab;
                if (Tabs.Count > 1)
                {
                    EndTabDrag();
                    TearOffTab(tab);
                }
                else
                {
                    EndTabDrag();
                    BeginExternalTabDrag(tab);
                }
                return;
            }
            int from = Tabs.IndexOf(_dragTab);
            if (from < 0)
            {
                EndTabDrag();
                return;
            }
            int to = GetTabSlotAt(inStrip.X);
            if (to >= 0 && to != from) MoveTab(from, to);
            UpdateDraggedTabVisual(inStrip.X);
        }
        private int GetTabSlotAt(double x)
        {
            int last = -1;
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || item.ActualWidth <= 0) continue;
                last = i;
                double right = GetTabLayoutLeft(item) + item.ActualWidth;
                if (x < right) return i;
            }
            return last;
        }
        /// <summary>
        /// Left edge of a tab in layout space, ignoring the RenderTransform used for drag offsets and animations.
        /// (Using the transformed position made tab reordering flicker back and forth while dragging.)
        /// </summary>
        private double GetTabLayoutLeft(ListBoxItem item)
        {
            double left = item.TranslatePoint(new Point(0, 0), TabListBox).X;
            if (item.RenderTransform is TransformGroup group)
            {
                TranslateTransform? translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (translate != null) left -= translate.X;
                ScaleTransform? scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
                if (scale != null) left -= item.ActualWidth * (1 - scale.ScaleX) / 2;
            }
            return left;
        }
        private Dictionary<BrowserTab, double> CaptureTabPositions()
        {
            var positions = new Dictionary<BrowserTab, double>();
            foreach (BrowserTab tab in Tabs)
            {
                if (TabListBox.ItemContainerGenerator.ContainerFromItem(tab) is ListBoxItem item)
                {
                    positions[tab] = item.TranslatePoint(new Point(0, 0), TabListBox).X;
                }
            }
            return positions;
        }
        // IMPORTANT: do not put these transforms inside the ControlTemplate. WPF may seal/freeze
        // Freezables created by a template/style, which makes BeginAnimation throw at runtime.
        // We own this TransformGroup on the ListBoxItem instance, so it remains mutable.
        private static TransformGroup EnsureTabTransformGroup(ListBoxItem item)
        {
            if (item.RenderTransform is TransformGroup existing &&
                !existing.IsFrozen &&
                existing.Children.OfType<ScaleTransform>().Any() &&
                existing.Children.OfType<TranslateTransform>().Any())
            {
                return existing;
            }
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(1, 1));
            group.Children.Add(new TranslateTransform(0, 0));
            item.RenderTransform = group;
            return group;
        }
        private static TranslateTransform GetTabTranslateTransform(ListBoxItem item)
        {
            return EnsureTabTransformGroup(item).Children
                .OfType<TranslateTransform>()
                .First();
        }
        private static ScaleTransform GetTabScaleTransform(ListBoxItem item)
        {
            return EnsureTabTransformGroup(item).Children
                .OfType<ScaleTransform>()
                .First();
        }
        private static void AnimateTabToOffset(ListBoxItem item, double offset, int milliseconds = 80)
        {
            TranslateTransform? transform = GetTabTranslateTransform(item);
            if (transform == null) return;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = offset;
            if (Math.Abs(offset) < 0.5)
            {
                transform.X = 0;
                return;
            }
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(milliseconds),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                },
                System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }
        private void AnimateTabsToPositions(Dictionary<BrowserTab, double> previousPositions)
        {
            TabListBox.UpdateLayout();
            foreach (BrowserTab tab in Tabs)
            {
                if (ReferenceEquals(tab, _dragTab)) continue;
                if (!previousPositions.TryGetValue(tab, out double oldX)) continue;
                if (TabListBox.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item) continue;
                double newX = GetTabLayoutLeft(item);
                AnimateTabToOffset(item, oldX - newX, 80);  // 80 ms — snappy like Chrome
            }
        }
        private void UpdateDraggedTabVisual(double pointerX)
        {
            if (_dragTab == null) return;
            if (TabListBox.ItemContainerGenerator.ContainerFromItem(_dragTab) is not ListBoxItem item) return;
            TranslateTransform? transform = GetTabTranslateTransform(item);
            if (transform == null) return;
            double itemLeft = GetTabLayoutLeft(item);
            double targetX = pointerX - itemLeft - _dragPointerOffset.X;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = targetX;
            ScaleTransform? scale = GetTabScaleTransform(item);
            if (scale != null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.015;
                scale.ScaleY = 1.015;
            }
        }
        private void MoveTab(int from, int to)
        {
            if (from == to) return;
            Dictionary<BrowserTab, double> previousPositions = CaptureTabPositions();
            BrowserTab? keepSelected = _selectedTab;
            Tabs.Move(from, to);
            if (keepSelected != null && !ReferenceEquals(TabListBox.SelectedItem, keepSelected))
            {
                TabListBox.SelectedItem = keepSelected;
            }
            TabListBox.UpdateLayout();
            AnimateTabsToPositions(previousPositions);
        }
        private void MainWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragTab == null || _externalDraggedWindow != null) return;
            BrowserTab clickedTab = _dragTab;
            bool wasDragging = _tabDragActive;
            EndTabDrag();
            if (!wasDragging && Tabs.Contains(clickedTab))
            {
                SelectedTab = clickedTab;
            }
            if (wasDragging) e.Handled = true;
        }
        private void MainWindow_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_tabDragActive && ReferenceEquals(e.OriginalSource, this))
            {
                ResetTabDragVisual();
                _tabDragActive = false;
                _dragTab = null;
            }
        }
        private void ResetTabDragVisual()
        {
            if (_dragTab != null &&
                TabListBox.ItemContainerGenerator.ContainerFromItem(_dragTab) is ListBoxItem item)
            {
                TranslateTransform? transform = GetTabTranslateTransform(item);
                if (transform != null)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.X = 0;
                }
                ScaleTransform? scale = GetTabScaleTransform(item);
                if (scale != null)
                {
                    var animX = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(100),
                        EasingFunction = new System.Windows.Media.Animation.CubicEase
                        {
                            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                        }
                    };
                    var animY = animX.Clone();
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
                }
            }
        }
        private void EndTabDrag()
        {
            ResetTabDragVisual();
            bool wasActive = _tabDragActive;
            _dragTab = null;
            _tabDragActive = false;
            if (wasActive && IsMouseCaptured) ReleaseMouseCapture();
        }
        /// <summary>
        /// Moves a tab into a brand-new browser window while preserving the live WebView2 instance.
        /// The new window follows the pointer without the native DragMove hand-off jump, and the
        /// detached window can be dragged back onto another browser window to reattach the tab.
        /// </summary>
        private void TearOffTab(BrowserTab tab)
        {
            if (!Tabs.Contains(tab)) return;
            if (tab.WebView?.CoreWebView2 == null)
            {
                Debug.WriteLine("Ignoring tear-off before WebView2 initialization has completed.");
                return;
            }
            Point cursor = GetCursorDevicePoint();
            PresentationSource? presentation = PresentationSource.FromVisual(this);
            DetachTabForTransfer(tab);
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;
            var newWindow = new MainWindow(tab)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowState = WindowState.Normal,
                Width = bounds.Width,
                Height = bounds.Height,
                Opacity = 0.96,
                ShowInTaskbar = true
            };
            // Pre-position before showing so the window doesn't flash at a default location first.
            if (presentation?.CompositionTarget != null)
            {
                Point dip = presentation.CompositionTarget.TransformFromDevice.Transform(new Point(cursor.X - 100, cursor.Y - 20));
                newWindow.Left = dip.X;
                newWindow.Top = dip.Y;
            }
            newWindow.Show();
            newWindow.Activate();
            PositionWindowAtCursor(newWindow, cursor);
            newWindow.BeginAnimation(
                Window.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.96,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(110),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
            newWindow.BeginExternalTabDrag(tab);
        }
        private void BeginExternalTabDrag(BrowserTab tab)
        {
            _externalDraggedWindow = this;
            _externalDropTarget = null;
            _dragTab = tab;
            _externalTabDragTimer?.Stop();
            _externalTabDragTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Input,
                ExternalTabDragTimer_Tick,
                Dispatcher)
            {
                IsEnabled = true
            };
        }
        private void ExternalTabDragTimer_Tick(object? sender, EventArgs e)
        {
            if (_externalDraggedWindow == null || _dragTab == null)
            {
                StopExternalTabDrag();
                return;
            }
            if (!IsLeftMouseButtonDown())
            {
                FinishExternalTabDrag(GetCursorDevicePoint());
                return;
            }
            Point cursor = GetCursorDevicePoint();
            // Check for a drop target BEFORE repositioning the dragged window, so the dragged
            // window does not occlude the target window's tab strip during the bounds check.
            MainWindow? target = GetBrowserWindowAtCursor(cursor, _externalDraggedWindow, tabStripOnly: true);
            if (target != null && !ReferenceEquals(target, _externalDraggedWindow))
            {
                if (!ReferenceEquals(_externalDropTarget, target))
                {
                    _externalDropTarget?.HideDropTargetIndicator();
                    _externalDropTarget = target;
                    target.ShowDropTargetIndicator();
                }
            }
            else
            {
                _externalDropTarget?.HideDropTargetIndicator();
                _externalDropTarget = null;
            }
            // Reposition the dragged window to follow the cursor.
            PositionWindowAtCursor(_externalDraggedWindow, cursor);
        }
        private void FinishExternalTabDrag(Point cursor)
        {
            BrowserTab? tab = _dragTab;
            MainWindow? currentWindow = _externalDraggedWindow;
            MainWindow? target = _externalDropTarget;
            StopExternalTabDrag();
            if (tab == null || currentWindow == null) return;
            if (target != null && !ReferenceEquals(target, currentWindow))
            {
                target.ReceiveTransferredTabFromExternalDrag(currentWindow, tab, cursor);
                return;
            }
            // Dropped outside another tab strip: keep the detached window where the pointer left it.
            currentWindow.SelectedTab = tab;
        }
        private void StopExternalTabDrag()
        {
            _externalTabDragTimer?.Stop();
            _externalTabDragTimer = null;
            _externalDropTarget?.HideDropTargetIndicator();
            _externalDropTarget = null;
            _externalDraggedWindow = null;
            _dragTab = null;
        }
        private static void PositionWindowAtCursor(MainWindow window, Point cursorDevice)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            GetWindowRect(hwnd, out Win32Rect rect);
            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            const int cursorOffsetX = 100;
            const int cursorOffsetY = 20;
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(cursorDevice.X - cursorOffsetX),
                (int)Math.Round(cursorDevice.Y - cursorOffsetY),
                width, height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        private void ShowDropTargetIndicator()
        {
            HideTabPreview();
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(90),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            TabDropIndicator.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        private void HideDropTargetIndicator()
        {
            TabDropIndicator.BeginAnimation(UIElement.OpacityProperty, null);
            TabDropIndicator.Opacity = 0;
        }
        private void ReceiveTransferredTabFromExternalDrag(MainWindow source, BrowserTab tab, Point cursorDevice)
        {
            if (ReferenceEquals(source, this)) return;
            int insertIndex = GetDropInsertIndex(cursorDevice);
            source.DetachTabForTransfer(tab);
            ReceiveTransferredTab(tab, insertIndex, animate: true);
            HideDropTargetIndicator();
            Activate();
            // The torn-off window is now empty - close it instead of leaving a blank shell behind.
            if (source.Tabs.Count == 0) source.Close();
        }
        private void TransferTabToWindow(MainWindow target, BrowserTab tab, Point cursorDevice)
        {
            if (ReferenceEquals(target, this) || !Tabs.Contains(tab)) return;
            int insertIndex = target.GetDropInsertIndex(cursorDevice);
            DetachTabForTransfer(tab);
            target.ReceiveTransferredTab(tab, insertIndex, animate: true);
            target.Activate();
            if (Tabs.Count == 0)
            {
                Close();
            }
        }
        private void DetachTabForTransfer(BrowserTab tab)
        {
            int index = Tabs.IndexOf(tab);
            if (index < 0) return;
            bool wasSelected = ReferenceEquals(_selectedTab, tab);
            WebView2? webView = tab.WebView;
            HideTabPreview();
            _thumbnails.Remove(tab);
            if (webView != null)
            {
                TabContentGrid.Children.Remove(webView);
            }
            Tabs.RemoveAt(index);
            if (wasSelected && Tabs.Count > 0)
            {
                SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            }
            else if (Tabs.Count == 0)
            {
                _selectedTab = null;
                _activeTab = null;
            }
            if (ReferenceEquals(_activeTab, tab)) _activeTab = null;
        }
        private void ReceiveTransferredTab(BrowserTab tab, int insertIndex, bool animate)
        {
            if (tab.WebView == null || Tabs.Contains(tab)) return;
            _tabOwners[tab] = this;
            TabContentGrid.Children.Add(tab.WebView);
            int clampedIndex = insertIndex < 0 ? Tabs.Count : Math.Max(0, Math.Min(insertIndex, Tabs.Count));
            Tabs.Insert(clampedIndex, tab);
            SelectedTab = tab;
            ApplyWebViewVisibility(tab);
            UpdateNavigationButtons();
            ScrollTabIntoView(tab);
            TabListBox.UpdateLayout();
            if (animate)
            {
                AnimateInsertedTab(tab);
            }
        }
        private void AnimateInsertedTab(BrowserTab tab)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (TabListBox.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem item) return;
                ScaleTransform? scale = GetTabScaleTransform(item);
                TranslateTransform? translate = GetTabTranslateTransform(item);
                if (scale == null || translate == null) return;
                scale.ScaleX = 0.94;
                scale.ScaleY = 0.94;
                translate.X = 10;
                var easing = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(150))
                    { EasingFunction = easing });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(150))
                    { EasingFunction = easing });
                translate.BeginAnimation(TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150))
                    { EasingFunction = easing });
            }), DispatcherPriority.Loaded);
        }
        private int GetDropInsertIndex(Point cursorDevice)
        {
            double x = GetCursorPointInTabList(cursorDevice).X;
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (TabListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || item.ActualWidth <= 0) continue;
                if (x < GetTabLayoutLeft(item) + item.ActualWidth / 2) return i; // drop before the tab whose middle we're left of
            }
            return Tabs.Count; // past the last tab: append (previously landed one slot too early)
        }
        private Point GetCursorPointInTabList(Point cursorDevice)
        {
            PresentationSource? presentation = PresentationSource.FromVisual(this);
            if (presentation?.CompositionTarget == null) return new Point();
            Point screenDip = presentation.CompositionTarget.TransformFromDevice.Transform(cursorDevice);
            Point windowPoint = PointFromScreen(screenDip);
            Point listOrigin = TabListBox.TranslatePoint(new Point(0, 0), this);
            return new Point(windowPoint.X - listOrigin.X, windowPoint.Y - listOrigin.Y);
        }
        private static MainWindow? GetTabOwner(BrowserTab tab)
        {
            return _tabOwners.TryGetValue(tab, out MainWindow? owner) && owner.IsVisible ? owner : null;
        }
        private static Point GetCursorDevicePoint()
        {
            return GetCursorPos(out Win32Point point)
                ? new Point(point.X, point.Y)
                : new Point();
        }
        private static bool IsLeftMouseButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }
        private static MainWindow? GetBrowserWindowAtCursor(Point cursorDevice, MainWindow? exclude, bool tabStripOnly)
        {
            MainWindow? best = null;
            bool bestActive = false;
            foreach (MainWindow window in _openBrowserWindows.ToArray())
            {
                if (ReferenceEquals(window, exclude) || !window.IsVisible ||
                    window.WindowState == WindowState.Minimized) continue;
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Win32Rect rect)) continue;
                if (tabStripOnly)
                {
                    if (!window.IsCursorOverTabStrip(cursorDevice)) continue;
                }
                else
                {
                    if (cursorDevice.X < rect.Left || cursorDevice.X > rect.Right ||
                        cursorDevice.Y < rect.Top || cursorDevice.Y > rect.Bottom) continue;
                }
                if (best == null || (window.IsActive && !bestActive))
                {
                    best = window;
                    bestActive = window.IsActive;
                }
            }
            return best;
        }
        private bool IsCursorOverTabStrip(Point cursorDevice)
        {
            PresentationSource? presentation = PresentationSource.FromVisual(this);
            if (presentation?.CompositionTarget == null) return false;
            Point screenDip = presentation.CompositionTarget.TransformFromDevice.Transform(cursorDevice);
            Point windowPoint = PointFromScreen(screenDip);
            // Generous snap zone for tab strip area across top and header
            const double snapPadY = 40;
            const double snapPadX = 30;
            double stripHeight = TabBarBorder.ActualHeight > 0 ? TabBarBorder.ActualHeight : 38;
            double stripWidth = TabBarBorder.ActualWidth > 0 ? TabBarBorder.ActualWidth : ActualWidth;
            return windowPoint.Y >= -snapPadY &&
                   windowPoint.Y <= stripHeight + snapPadY &&
                   windowPoint.X >= -snapPadX &&
                   windowPoint.X <= stripWidth + snapPadX;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        private const int VK_LBUTTON = 0x01;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32Point lpPoint);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
        // ------------------------------------------------------------------
        // Drag and drop onto the title bar / tab strip:
        // Supports:
        //   - Text / search terms (searches or navigates to domain)
        //   - Hyperlinks / URLs (from web pages, other browsers, shell shortcuts)
        //   - Images (dragged from web pages or image files)
        //   - Files (HTML, PDF, media, images, text, and .url Internet Shortcuts)
        //   - Position-aware tab insertion at drop location
        //   - Debouncing to prevent duplicate tab creation
        // ------------------------------------------------------------------
        private long _lastDropTimestamp;
        private static readonly char[] LineBreakChars = { '\r', '\n' };
        private string? _lastDropPayload;

        private bool IsOverTitleBar(DragEventArgs e)
        {
            Point p = e.GetPosition(TabBarBorder);
            // Over the tab bar / title bar area, excluding window caption buttons on the top right
            return p.X >= 0 && p.Y >= 0 &&
                   p.X <= Math.Max(0, TabBarBorder.ActualWidth - 140) &&
                   p.Y <= TabBarBorder.ActualHeight;
        }

        private static bool HasSupportedDropData(System.Windows.IDataObject data)
        {
            try
            {
                return data.GetDataPresent(DataFormats.FileDrop) ||
                       data.GetDataPresent("UniformResourceLocatorW") ||
                       data.GetDataPresent("UniformResourceLocator") ||
                       data.GetDataPresent("text/x-moz-url") ||
                       data.GetDataPresent(DataFormats.Html) ||
                       data.GetDataPresent(DataFormats.UnicodeText) ||
                       data.GetDataPresent(DataFormats.Text);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Error inspecting drag data formats", ex);
                return false;
            }
        }

        private static string? ExtractUrlFromShortcutFile(string path)
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = trimmed.Substring(4).Trim();
                        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Failed to parse .url shortcut file: {path}", ex);
            }
            return null;
        }

        private static string? ExtractUniformResourceLocator(System.Windows.IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("UniformResourceLocatorW"))
                {
                    object? val = data.GetData("UniformResourceLocatorW");
                    if (val is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
                    if (val is MemoryStream ms)
                    {
                        string sUni = System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                        if (!string.IsNullOrWhiteSpace(sUni)) return sUni;
                    }
                }
                if (data.GetDataPresent("UniformResourceLocator"))
                {
                    object? val = data.GetData("UniformResourceLocator");
                    if (val is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
                    if (val is MemoryStream ms)
                    {
                        string sAnsi = System.Text.Encoding.Default.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                        if (!string.IsNullOrWhiteSpace(sAnsi)) return sAnsi;
                    }
                }
                if (data.GetDataPresent("text/x-moz-url"))
                {
                    object? val = data.GetData("text/x-moz-url");
                    string? text = val as string;
                    if (val is MemoryStream ms) text = System.Text.Encoding.Unicode.GetString(ms.ToArray());
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string[] lines = text.Split(LineBreakChars, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0 && Uri.TryCreate(lines[0].Trim(), UriKind.Absolute, out _))
                        {
                            return lines[0].Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Failed to extract UniformResourceLocator", ex);
            }
            return null;
        }

        private static string? ExtractUrlFromHtml(System.Windows.IDataObject data)
        {
            try
            {
                if (data.GetDataPresent(DataFormats.Html))
                {
                    if (data.GetData(DataFormats.Html) is string html && !string.IsNullOrWhiteSpace(html))
                    {
                        var aMatch = Regex.Match(html, @"<a\s+[^>]*?href=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        if (aMatch.Success)
                        {
                            string href = WebUtility.HtmlDecode(aMatch.Groups[1].Value).Trim();
                            if (Uri.TryCreate(href, UriKind.Absolute, out _)) return href;
                        }

                        var imgMatch = Regex.Match(html, @"<img\s+[^>]*?src=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                        if (imgMatch.Success)
                        {
                            string src = WebUtility.HtmlDecode(imgMatch.Groups[1].Value).Trim();
                            if (Uri.TryCreate(src, UriKind.Absolute, out _)) return src;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Failed to extract URL from HTML format", ex);
            }
            return null;
        }

        private static string? ExtractText(System.Windows.IDataObject data)
        {
            try
            {
                if (data.GetDataPresent(DataFormats.UnicodeText))
                {
                    string? s = data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (data.GetDataPresent(DataFormats.Text))
                {
                    string? s = data.GetData(DataFormats.Text) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Failed to extract text from data object", ex);
            }
            return null;
        }

        private List<string> ExtractDropTargets(System.Windows.IDataObject data)
        {
            var targets = new List<string>();

            // 1. Files dropped from Windows Explorer
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    foreach (string file in files)
                    {
                        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                        if (file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        {
                            string? shortcutUrl = ExtractUrlFromShortcutFile(file);
                            if (!string.IsNullOrWhiteSpace(shortcutUrl))
                            {
                                targets.Add(shortcutUrl);
                                continue;
                            }
                        }
                        try
                        {
                            targets.Add(new Uri(file).AbsoluteUri);
                        }
                        catch
                        {
                            targets.Add("file:///" + file.Replace('\\', '/'));
                        }
                    }
                    if (targets.Count > 0) return targets;
                }
            }

            // 2. Direct UniformResourceLocator (link dragged from web page, another browser, or shell)
            string? urlFromShell = ExtractUniformResourceLocator(data);
            if (!string.IsNullOrWhiteSpace(urlFromShell))
            {
                targets.Add(urlFromShell);
                return targets;
            }

            // 3. HTML anchor link or image (dragged from web page)
            string? urlFromHtml = ExtractUrlFromHtml(data);
            if (!string.IsNullOrWhiteSpace(urlFromHtml))
            {
                targets.Add(urlFromHtml);
                return targets;
            }

            // 4. Plain text / query
            string? rawText = ExtractText(data);
            if (!string.IsNullOrWhiteSpace(rawText))
            {
                string cleaned = Regex.Replace(rawText, @"\s+", " ").Trim();
                if (cleaned.Length > 500) cleaned = cleaned[..500];
                if (cleaned.Length > 0)
                {
                    string resolved = ResolveNavigationTarget(cleaned);
                    targets.Add(resolved);
                }
            }

            return targets;
        }

        private bool IsCursorOverTitleBar(Point cursorDevice)
        {
            PresentationSource? presentation = PresentationSource.FromVisual(this);
            if (presentation?.CompositionTarget == null) return false;
            Point screenDip = presentation.CompositionTarget.TransformFromDevice.Transform(cursorDevice);
            Point windowPoint = PointFromScreen(screenDip);
            double stripHeight = TabBarBorder.ActualHeight > 0 ? TabBarBorder.ActualHeight : 38;
            double stripWidth = TabBarBorder.ActualWidth > 0 ? TabBarBorder.ActualWidth : ActualWidth;
            return windowPoint.Y >= -10 &&
                   windowPoint.Y <= stripHeight + 15 &&
                   windowPoint.X >= 0 &&
                   windowPoint.X <= Math.Max(0, stripWidth - 140);
        }

        private DispatcherTimer? _webViewDragTracker;
        private string? _webViewDragText;

        public void TrackWebViewDragForTitleBarSearch(string text)
        {
            if (!IsLeftMouseButtonDown()) return; // a real drag holds the mouse button; a page script cannot fake that
            _webViewDragTracker?.Stop();
            _webViewDragText = text;
            _webViewDragTracker = new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Input,
                WebViewDragTracker_Tick,
                Dispatcher)
            {
                IsEnabled = true
            };
        }

        public void HandleWebViewDragEnd(string text)
        {
            bool hadMatchingStart = !string.IsNullOrWhiteSpace(_webViewDragText); // otherwise a page sent "dragend" out of the blue
            _webViewDragTracker?.Stop();
            _webViewDragTracker = null;
            _webViewDragText = null;
            if (!hadMatchingStart) return;

            Point cursor = GetCursorDevicePoint();
            if (IsCursorOverTitleBar(cursor))
            {
                ProcessTitleBarDrop(text, cursor);
            }
        }

        private void WebViewDragTracker_Tick(object? sender, EventArgs e)
        {
            if (IsLeftMouseButtonDown()) return; // Still dragging; keep polling.

            _webViewDragTracker?.Stop();
            _webViewDragTracker = null;
            string? text = _webViewDragText;
            _webViewDragText = null;
            if (string.IsNullOrWhiteSpace(text)) return;

            Point cursor = GetCursorDevicePoint();
            if (IsCursorOverTitleBar(cursor))
            {
                ProcessTitleBarDrop(text, cursor);
            }
        }

        private void ProcessTitleBarDrop(string payload, Point cursorDevice)
        {
            if (string.IsNullOrWhiteSpace(payload)) return;

            // Deduplication check: prevent multiple rapid triggers (e.g. dragend + timer + drop)
            long now = Environment.TickCount64;
            string key = payload.Trim();
            if (string.Equals(key, _lastDropPayload, StringComparison.OrdinalIgnoreCase) && (now - _lastDropTimestamp) < 1000)
            {
                return;
            }
            _lastDropPayload = key;
            _lastDropTimestamp = now;

            string target = ResolveNavigationTarget(key);
            // This payload was sent by a page script, so only web addresses (or searches, which are https) are accepted.
            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            int insertIndex = GetDropInsertIndex(cursorDevice);

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await OpenTabSafeAsync(target, insertIndex: insertIndex);
            }));
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (IsOverTitleBar(e) && HasSupportedDropData(e.Data))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                ShowDropTargetIndicator();
            }
            else
            {
                HideDropTargetIndicator();
            }
        }

        private void Window_PreviewDragLeave(object sender, DragEventArgs e)
        {
            HideDropTargetIndicator();
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            HideDropTargetIndicator();
            if (!IsOverTitleBar(e)) return;

            List<string> targets = ExtractDropTargets(e.Data);
            if (targets.Count == 0) return;

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;

            Point cursor = GetCursorDevicePoint();
            int insertIndex = GetDropInsertIndex(cursor);

            long now = Environment.TickCount64;
            string key = string.Join("|", targets);
            if (string.Equals(key, _lastDropPayload, StringComparison.OrdinalIgnoreCase) && (now - _lastDropTimestamp) < 1000)
            {
                return;
            }
            _lastDropPayload = key;
            _lastDropTimestamp = now;

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    int targetIndex = insertIndex >= 0 && insertIndex <= Tabs.Count ? insertIndex + i : -1;
                    await OpenTabSafeAsync(targets[i], insertIndex: targetIndex);
                }
            }));
        }
    }
}

