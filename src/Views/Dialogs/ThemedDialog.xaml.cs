using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CustomBrowser.Helpers;

namespace CustomBrowser.Views
{
    /// <summary>
    /// Dark, rounded replacement for the stock Windows MessageBox, matching the browser's look.
    /// Use ThemedDialog.Show(...) exactly like MessageBox.Show(...): same arguments, same MessageBoxResult.
    /// It can never make an error worse: if anything goes wrong while building it, the stock MessageBox is shown instead.
    /// </summary>
    public partial class ThemedDialog : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;
        private MessageBoxResult _fallbackResult = MessageBoxResult.OK; // what Esc / Alt+F4 mean for this button set
        private Button? _defaultButton;

        private ThemedDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            InitializeComponent();

            // Long messages (crash details) get a wider card.
            bool isLong = message.Length > 300 || message.Count(c => c == '\n') > 5;
            Width = isLong ? 640 : 488;

            TxtTitle.Text = string.IsNullOrWhiteSpace(title) ? "Mint Browser" : title;
            TxtMessage.Text = message;

            ApplyIcon(icon);
            BuildButtons(buttons, defaultResult);

            Loaded += (_, _) =>
            {
                _defaultButton?.Focus();
                var scale = new ScaleTransform(0.96, 0.96);
                Card.RenderTransformOrigin = new Point(0.5, 0.5);
                Card.RenderTransform = scale;
                Card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Motion.Base) { EasingFunction = Motion.EaseOut });
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0.96, 1, Motion.Slow) { EasingFunction = Motion.EaseOut };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            };
        }

        private MessageBoxResult Result => _result == MessageBoxResult.None ? _fallbackResult : _result;

        // ------------------------------------------------------------------
        // Public API (mirrors System.Windows.MessageBox.Show)
        // ------------------------------------------------------------------
        public static MessageBoxResult Show(
            string message,
            string title = "",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None) =>
            ShowCore(null, message, title, buttons, icon, defaultResult);

        /// <summary>Same as above, but centred on a specific window (like MessageBox.Show(owner, ...)).</summary>
        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None,
            MessageBoxResult defaultResult = MessageBoxResult.None) =>
            ShowCore(owner, message, title, buttons, icon, defaultResult);

        private static MessageBoxResult ShowCore(
            Window? preferredOwner,
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
        {
            message ??= string.Empty;
            title ??= string.Empty;
            try
            {
                Application? app = Application.Current;
                if (app == null) return StockFallback(message, title, buttons, icon, defaultResult);

                // May be called from a background thread (e.g. the crash handler): hop onto the UI thread.
                if (!app.Dispatcher.CheckAccess())
                {
                    return app.Dispatcher.Invoke(() => ShowCore(preferredOwner, message, title, buttons, icon, defaultResult));
                }

                var dialog = new ThemedDialog(message, title, buttons, icon, defaultResult);
                bool ownerUsable = preferredOwner != null && preferredOwner.IsVisible && preferredOwner.WindowState != WindowState.Minimized;
                Window? owner = ownerUsable ? preferredOwner : FindOwner(app);
                if (owner != null)
                {
                    dialog.Owner = owner;
                }
                else
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                dialog.ShowDialog();
                return dialog.Result;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Themed dialog failed - showing the standard message box instead", ex);
                return StockFallback(message, title, buttons, icon, defaultResult);
            }
        }

        private static MessageBoxResult StockFallback(string message, string title, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) =>
            System.Windows.MessageBox.Show(message, title, buttons, icon, defaultResult);

        /// <summary>The window the user is working in (a window that was never shown cannot own a dialog).</summary>
        private static Window? FindOwner(Application app)
        {
            Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
            if (owner == null && app.MainWindow != null && app.MainWindow.IsVisible) owner = app.MainWindow;
            if (owner != null && owner.WindowState == WindowState.Minimized) return null;
            return owner;
        }

        // ------------------------------------------------------------------
        // Building the dialog
        // ------------------------------------------------------------------
        private void ApplyIcon(MessageBoxImage icon)
        {
            string glyph;
            string color;
            switch (icon)
            {
                case MessageBoxImage.Error: glyph = "\u00D7"; color = "#FF6961"; break;
                case MessageBoxImage.Warning: glyph = "!"; color = "#FFB340"; break;
                case MessageBoxImage.Question: glyph = "?"; color = "#FAFAFA"; break;
                case MessageBoxImage.Information: glyph = "i"; color = "#FAFAFA"; break;
                default:
                    IconHost.Visibility = Visibility.Collapsed;
                    return;
            }

            Color c = (Color)ColorConverter.ConvertFromString(color);
            TxtIcon.Text = glyph;
            TxtIcon.Foreground = new SolidColorBrush(c);
            IconHost.Background = new SolidColorBrush(Color.FromArgb(0x2A, c.R, c.G, c.B));
            IconHost.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        }

        private void BuildButtons(MessageBoxButton buttons, MessageBoxResult defaultResult)
        {
            (string Text, MessageBoxResult Result)[] set;
            switch (buttons)
            {
                case MessageBoxButton.OKCancel:
                    set = new[] { ("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel) };
                    _fallbackResult = MessageBoxResult.Cancel;
                    break;
                case MessageBoxButton.YesNo:
                    set = new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No) };
                    _fallbackResult = MessageBoxResult.No;
                    break;
                case MessageBoxButton.YesNoCancel:
                    set = new[] { ("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel) };
                    _fallbackResult = MessageBoxResult.Cancel;
                    break;
                default:
                    set = new[] { ("OK", MessageBoxResult.OK) };
                    _fallbackResult = MessageBoxResult.OK;
                    break;
            }

            int defaultIndex = Array.FindIndex(set, s => s.Result == defaultResult);
            if (defaultIndex < 0) defaultIndex = 0;

            for (int i = 0; i < set.Length; i++)
            {
                MessageBoxResult result = set[i].Result;
                bool isDefault = i == defaultIndex;
                var button = new Button
                {
                    Content = set[i].Text,
                    Style = (Style)FindResource(isDefault ? "MintPrimaryButton" : "MintButton"),
                    IsDefault = isDefault,
                    Margin = i > 0 ? new Thickness(8, 0, 0, 0) : new Thickness(0)
                };
                button.Click += (_, _) =>
                {
                    _result = result;
                    Close();
                };
                ButtonRow.Children.Add(button);
                if (isDefault) _defaultButton = button;
            }
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------
        private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _result = _fallbackResult; // Esc = the safe answer (No / Cancel / OK)
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && TxtMessage.SelectionLength == 0)
            {
                // Handy for error details: Ctrl+C with nothing selected copies the whole message.
                try { Clipboard.SetText(TxtMessage.Text); }
                catch (Exception ex) { AppLog.Debug("Could not copy dialog text", ex); }
                e.Handled = true;
            }
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* mouse button was already released */ }
        }
    }
}



