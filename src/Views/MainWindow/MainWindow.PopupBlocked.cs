using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CustomBrowser.Models;

namespace CustomBrowser
{
    /// <summary>The "pop-up blocked" icon in the address bar: it shows what a page tried to open and lets the user open it on purpose.</summary>
    public partial class MainWindow
    {
        /// <summary>Shows the icon only while the selected tab has blocked pop-ups. Called whenever the selected tab or its page changes.</summary>
        private void UpdatePopupBlockedIndicator()
        {
            if (BtnPopupBlocked == null) return;
            int count = SelectedTab?.BlockedPopups.Count ?? 0;
            BtnPopupBlocked.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (count > 0)
            {
                BtnPopupBlocked.ToolTip = count == 1 ? "A pop-up was blocked on this page" : $"{count} pop-ups were blocked on this page";
            }
            else if (PopupBlockedPopup != null)
            {
                PopupBlockedPopup.IsOpen = false;
            }
        }

        private void BtnPopupBlocked_Click(object sender, RoutedEventArgs e)
        {
            BrowserTab? tab = SelectedTab;
            if (tab == null || tab.BlockedPopups.Count == 0) return;
            FillPopupBlockedList(tab);
            PopupBlockedPopup.IsOpen = true;
        }

        private void FillPopupBlockedList(BrowserTab tab)
        {
            PopupBlockedList.Items.Clear();
            var itemStyle = (Style)FindResource("SidebarMenuItemStyle");
            var textBrush = (Brush)FindResource("MintTextBrush");
            foreach (string url in tab.BlockedPopups.ToList())
            {
                string shown = ShortAddress(url);
                var label = new TextBlock
                {
                    Text = shown,
                    Foreground = textBrush,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = url
                };
                var button = new Button { Style = itemStyle, Content = label };
                button.SetValue(AutomationProperties.NameProperty, "Open " + shown);
                string target = url;
                button.Click += (_, _) =>
                {
                    tab.RemoveBlockedPopup(target);
                    UpdatePopupBlockedIndicator();
                    if (tab.BlockedPopups.Count == 0) PopupBlockedPopup.IsOpen = false; else FillPopupBlockedList(tab);
                    _ = OpenTabSafeAsync(target, tab); // the user asked for this one
                };
                PopupBlockedList.Items.Add(button);
            }
        }

        private void BtnPopupBlockedDismiss_Click(object sender, RoutedEventArgs e)
        {
            SelectedTab?.ClearBlockedPopups();
            PopupBlockedPopup.IsOpen = false;
            UpdatePopupBlockedIndicator();
        }

        /// <summary>"example.com/path" - short enough for a menu row.</summary>
        private static string ShortAddress(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return url.Length > 60 ? url[..60] + "…" : url;
            string text = uri.Host + (uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery);
            return text.Length > 60 ? text[..60] + "…" : text;
        }
    }
}
