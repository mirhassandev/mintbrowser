using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace CustomBrowser.Models
{
    public class BrowserTab : INotifyPropertyChanged
    {
        private string _title = "New Tab";
        private string _url = string.Empty;
        private string? _favicon;
        private bool _isLoading;
        private bool _showSpinner;
        private DispatcherTimer? _spinnerTimer;
        private WebView2? _webView;

        /// <summary>True when this tab was opened by a link / window.open from another tab.</summary>
        public bool IsPopupSpawned { get; set; }
        /// <summary>The tab that opened this one (re-selected if this tab turns out to be only a download hand-off).</summary>
        public BrowserTab? Opener { get; set; }
        /// <summary>Set once a real document started loading here (a pure download navigation never does).</summary>
        public bool HasLoadedPage { get; set; }

        private readonly List<string> _blockedPopups = new();
        /// <summary>Addresses this page tried to open on its own (not after a click) and the browser blocked. Cleared when the tab navigates.</summary>
        public IReadOnlyList<string> BlockedPopups => _blockedPopups;
        /// <summary>Remembers a blocked pop-up (each address once, at most 8). Returns true when it was new.</summary>
        public bool AddBlockedPopup(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || _blockedPopups.Contains(url, StringComparer.OrdinalIgnoreCase)) return false;
            if (_blockedPopups.Count >= 8) _blockedPopups.RemoveAt(0);
            _blockedPopups.Add(url);
            return true;
        }
        public void RemoveBlockedPopup(string url) => _blockedPopups.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase));
        public void ClearBlockedPopups() => _blockedPopups.Clear();

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                    UpdateSpinner(value);
                }
            }
        }

        /// <summary>
        /// What the tab header binds to for its spinner. Turns on only if a load is still running after a short grace period
        /// (so cached / instant loads never flash favicon -> spinner -> favicon) and off immediately when loading ends.
        /// </summary>
        public bool ShowSpinner
        {
            get => _showSpinner;
            private set
            {
                if (_showSpinner != value)
                {
                    _showSpinner = value;
                    OnPropertyChanged();
                }
            }
        }

        private void UpdateSpinner(bool loading)
        {
            _spinnerTimer?.Stop();
            if (!loading)
            {
                ShowSpinner = false;
                return;
            }
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                ShowSpinner = _isLoading;
            };
            _spinnerTimer = timer;
            timer.Start();
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Url
        {
            get => _url;
            set
            {
                if (_url != value)
                {
                    _url = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? Favicon
        {
            get => _favicon;
            set
            {
                if (_favicon != value)
                {
                    _favicon = value;
                    OnPropertyChanged();
                }
            }
        }

        public WebView2? WebView
        {
            get => _webView;
            set
            {
                if (_webView != value)
                {
                    _webView = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}



