// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace MusicBeePlugin
{
    /// <summary>
    /// Ready-to-draw icons for the panel and the context menu. Only used on MusicBee's UI thread.
    /// The MusicBrainz release group gets a blue "RG" badge, the release an orange "R" badge.
    /// </summary>
    internal sealed class IconBitmaps : IDisposable
    {
        private static readonly Color ReleaseGroupColor = Color.FromArgb(37, 99, 235);
        private static readonly Color ReleaseColor = Color.FromArgb(234, 88, 12);

        private readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        public Bitmap Get(SiteLink link, int size)
        {
            string key = size + "|" + link.IconHost + (link.IconData != null ? "" : "#fallback") + "|" + link.Badge;
            if (cache.TryGetValue(key, out Bitmap cached)) return cached;

            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                if (!DrawFavicon(g, link.IconData, size)) DrawFallback(g, link.FallbackText, size);
                if (!string.IsNullOrEmpty(link.Badge)) DrawBadge(g, link.Badge, size);
            }
            cache[key] = bitmap;
            return bitmap;
        }

        /// <summary>Disposes all bitmaps. Only call when no control shows them any more.</summary>
        public void Clear()
        {
            foreach (Bitmap bitmap in cache.Values) bitmap.Dispose();
            cache.Clear();
        }

        public void Dispose() => Clear();

        private static bool DrawFavicon(Graphics g, byte[] data, int size)
        {
            if (data == null) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (Image image = Image.FromStream(ms))
                {
                    g.DrawImage(image, new Rectangle(0, 0, size, size));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Neutral abbreviation icon for links whose favicon is not available.</summary>
        private static void DrawFallback(Graphics g, string text, int size)
        {
            var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using (GraphicsPath path = RoundedRectangle(rect, size * 0.22f))
            using (var brush = new SolidBrush(Color.FromArgb(255, 105, 105, 105)))
                g.FillPath(brush, path);

            float fontSize = size * (text.Length >= 3 ? 0.32f : 0.42f);
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, Brushes.White, rect, format);
        }

        /// <summary>Coloured pill with "RG" or "R" in the bottom right corner.</summary>
        private static void DrawBadge(Graphics g, string text, int size)
        {
            Color fill = text == "RG" ? ReleaseGroupColor : ReleaseColor;
            float height = Math.Max(7f, size * 0.46f);

            using (var font = new Font("Segoe UI", height * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                SizeF textSize = g.MeasureString(text, font, PointF.Empty, format);
                float width = Math.Min(size, Math.Max(height, textSize.Width + height * 0.5f));
                var rect = new RectangleF(size - width, size - height, width - 0.5f, height - 0.5f);

                using (GraphicsPath path = RoundedRectangle(rect, Math.Min(rect.Height, rect.Width) / 2f))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(Color.White, Math.Max(1f, size / 24f)))
                {
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
                g.DrawString(text, font, Brushes.White, rect, format);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
        {
            float d = Math.Max(0.5f, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
