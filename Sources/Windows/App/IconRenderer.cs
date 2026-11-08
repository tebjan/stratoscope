using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Stratoscope.Windows;

/// <summary>Draws the application icon (the icon of the exe and of the Start menu entry). The app has no notification-area icon.</summary>
internal static class IconRenderer
{
    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The Stratoscope icon: a gauge on a dark rounded square, at any size.</summary>
    public static Bitmap AppIcon(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);
            var inset = size * 0.04f;
            var square = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
            using (var path = Round(square, size * 0.22f))
            using (var brush = new LinearGradientBrush(square, Color.FromArgb(44, 46, 62), Color.FromArgb(22, 23, 32), 90f))
                g.FillPath(brush, path);
            // The gauge arc runs from green through yellow to orange, like the cache colours.
            var cx = size / 2f;
            var cy = size * 0.64f;
            var pen = Math.Max(2f, size * 0.12f);
            var r = size * 0.31f;
            var arc = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), pen) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, arc, 180, 180);
            var colours = new[] { Color.FromArgb(52, 199, 89), Color.FromArgb(255, 204, 0), Color.FromArgb(255, 149, 0) };
            for (int i = 0; i < 3; i++)
                using (var p = new Pen(colours[i], pen) { StartCap = i == 0 ? LineCap.Round : LineCap.Flat, EndCap = i == 2 ? LineCap.Round : LineCap.Flat })
                    g.DrawArc(p, arc, 180 + i * 60, i == 2 ? 60 : 62);
            var angle = -52.0 * Math.PI / 180;
            using (var p = new Pen(Color.White, Math.Max(1.5f, size * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(p, cx, cy, cx + (float)Math.Cos(angle) * r * 0.9f, cy + (float)Math.Sin(angle) * r * 0.9f);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, cx - pen * 0.55f, cy - pen * 0.55f, pen * 1.1f, pen * 1.1f);
        }
        return bmp;
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, which Windows Vista and newer read).</summary>
    public static void WriteAppIcon(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (var size in sizes)
            using (var bmp = AppIcon(size))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) w.Write(image);
        }
    }
}
