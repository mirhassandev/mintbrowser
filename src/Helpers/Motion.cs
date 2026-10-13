using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace CustomBrowser.Helpers
{
    /// <summary>Reads the shared motion tokens from Themes/Motion.xaml so every animation in code uses one rhythm.</summary>
    internal static class Motion
    {
        public static TimeSpan Fast => Get("MintMotionFast", 90);

        public static TimeSpan Base => Get("MintMotionBase", 120);

        public static TimeSpan Slow => Get("MintMotionSlow", 180);

        public static IEasingFunction EaseOut =>
            Application.Current?.TryFindResource("MintEaseOut") as IEasingFunction
            ?? new CubicEase { EasingMode = EasingMode.EaseOut };

        private static TimeSpan Get(string key, int fallbackMs) =>
            Application.Current?.TryFindResource(key) is Duration d && d.HasTimeSpan
                ? d.TimeSpan
                : TimeSpan.FromMilliseconds(fallbackMs);
    }
}
