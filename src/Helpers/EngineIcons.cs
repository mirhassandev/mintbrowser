using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CustomBrowser.Helpers;

namespace CustomBrowser
{
    /// <summary>
    /// Icons of the built-in search engines. A logo file named after the engine in asset\engines (google.png, bing.png, ...)
    /// is used when it exists; otherwise a letter tile is drawn locally. Nothing is ever requested from a server.
    /// </summary>
    public static class EngineIcons
    {
        public static ImageSource Google { get; } = Load("google") ?? LetterTile.Create('G', Color.FromRgb(0x42, 0x85, 0xF4));
        public static ImageSource Bing { get; } = Load("bing") ?? LetterTile.Create('B', Color.FromRgb(0x00, 0x83, 0x73));
        public static ImageSource DuckDuckGo { get; } = Load("duckduckgo") ?? LetterTile.Create('D', Color.FromRgb(0xDE, 0x58, 0x33));
        public static ImageSource Mdn { get; } = Load("mdn") ?? LetterTile.Create('M', Color.FromRgb(0x4A, 0x55, 0x68));
        public static ImageSource Perplexity { get; } = Load("perplexity") ?? LetterTile.Create('P', Color.FromRgb(0x20, 0xB8, 0xCD));
        public static ImageSource Wikipedia { get; } = Load("wikipedia") ?? LetterTile.Create('W', Color.FromRgb(0x63, 0x64, 0x66));

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Returned as ImageSource so it combines with the letter-tile fallback through ??.")]
        private static ImageSource? Load(string name)
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "asset", "engines");
            return TryLoad(Path.Combine(folder, name + ".png")) ?? TryLoad(Path.Combine(folder, name + ".ico"));
        }

        private static BitmapImage? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 64;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not load a search engine logo: " + path, ex);
                return null;
            }
        }
    }
}
