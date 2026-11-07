using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland.Services
{
    /// <summary>Декод обложки и извлечение её «живого» цвета для волны и подсветки.</summary>
    public static class ArtTools
    {
        public static BitmapSource Decode(byte[] data, int px)
        {
            using var ms = new MemoryStream(data);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = px;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        public static Color Vivid(BitmapSource src)
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(20.0 / src.PixelWidth, 20.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, tw = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                double val = mx / 255.0;
                double wgt = sat * sat * val * val + 0.002; // яркие насыщенные пиксели важнее
                r += R * wgt; g += G * wgt; b += B * wgt; tw += wgt;
            }
            if (tw <= 0) return Colors.White;
            r /= tw; g /= tw; b /= tw;

            double m = Math.Max(r, Math.Max(g, b));
            if (m < 1) return Color.FromRgb(0xE5, 0xE5, 0xEA);
            double k = 238 / m; // поднимаем яркость, чтобы цвет был виден на чёрном
            return Color.FromRgb(C(r * k), C(g * k), C(b * k));
        }

        static byte C(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

        /// <summary>Демо-обложка для режима --snapshot.</summary>
        public static BitmapSource Demo()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x5E, 0x62), Color.FromRgb(0x7B, 0x2F, 0xF7), 45), null, new Rect(0, 0, 200, 200));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xD2, 0x6A)), null, new Point(140, 60), 46, 46);
            }
            var rtb = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
