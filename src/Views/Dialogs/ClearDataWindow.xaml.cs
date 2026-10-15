using System;
using System.Windows;
using System.Windows.Input;

namespace CustomBrowser.Views
{
    /// <summary>What the user picked in the "Clear browsing data" dialog. Range is null for "All time".</summary>
    public sealed record ClearDataChoice(
        TimeSpan? Range, string RangeLabel,
        bool History, bool Cookies, bool Cache, bool Passwords, bool Autofill);

    /// <summary>
    /// Asks what to clear and for which time range. It never clears anything itself: it hands a
    /// <see cref="ClearDataChoice"/> back to the main window.
    /// </summary>
    public partial class ClearDataWindow : Window
    {
        private static readonly (string Label, TimeSpan? Span)[] Ranges =
        {
            ("Last hour", TimeSpan.FromHours(1)),
            ("Last 24 hours", TimeSpan.FromHours(24)),
            ("Last 7 days", TimeSpan.FromDays(7)),
            ("Last 4 weeks", TimeSpan.FromDays(28)),
            ("All time", null)
        };

        public ClearDataChoice? Choice { get; private set; }

        public ClearDataWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;
            foreach (var range in Ranges) LstRange.Items.Add(range.Label);
            LstRange.SelectedIndex = 1; // last 24 hours, like other browsers
            UpdateClearButton();
        }

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DialogHelpers.DragCard(this);

        private void Option_Changed(object sender, RoutedEventArgs e) => UpdateClearButton();

        private void UpdateClearButton()
        {
            // Checked events also fire while the XAML is still being loaded, before the button exists.
            if (BtnClear == null) return;
            BtnClear.IsEnabled = ChkHistory.IsChecked == true || ChkCookies.IsChecked == true || ChkCache.IsChecked == true ||
                                 ChkPasswords.IsChecked == true || ChkAutofill.IsChecked == true;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            int index = Math.Clamp(LstRange.SelectedIndex, 0, Ranges.Length - 1);
            Choice = new ClearDataChoice(
                Ranges[index].Span, Ranges[index].Label,
                ChkHistory.IsChecked == true, ChkCookies.IsChecked == true, ChkCache.IsChecked == true,
                ChkPasswords.IsChecked == true, ChkAutofill.IsChecked == true);
            DialogResult = true;
        }
    }
}
