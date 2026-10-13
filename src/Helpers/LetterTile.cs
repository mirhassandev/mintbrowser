using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// A rounded coloured square with one letter: the stand-in icon for sites and search engines that have no icon of their own.
    /// Nothing is downloaded; the picture is drawn here.
    /// </summary>
    public static class LetterTile
    {
        private const double Box = 64;

        /// <summary>A vector picture (sharp at every size), frozen so it can be shared.</summary>
        public static DrawingImage Create(char letter, Color background)
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(background), null, new RectangleGeometry(new Rect(0, 0, Box, Box), 14, 14)));

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var text = new FormattedText(letter.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 38, Brushes.White, 1.0);
            Geometry shape = text.BuildGeometry(new Point((Box - text.Width) / 2, (Box - text.Height) / 2));
            group.Children.Add(new GeometryDrawing(Brushes.White, null, shape));

            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        /// <summary>The same tile as a PNG file (for the on-disk icon cache). Must run on the UI thread.</summary>
        public static byte[] ToPng(char letter, Color background, int size = 64)
        {
            DrawingImage image = Create(letter, background);
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawImage(image, new Rect(0, 0, size, size));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
