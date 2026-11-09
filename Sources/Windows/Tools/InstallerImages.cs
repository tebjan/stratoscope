using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Stratoscope.Windows;

/// <summary>
/// The artwork of the Setup (scripts/windows/installer): drawn from the app's own icon and type, with the project's own headline from the
/// README, so the installer looks like the app.
/// The wizard image is the tall panel on the left of the last page; the small image sits in the header of the progress page.
/// </summary>
internal static class InstallerImages
{
    /// <summary>Twice the 202 x 386 area of a 100 % display, so it stays sharp on high-DPI screens.</summary>
    private const int WizardWidth = 404, WizardHeight = 772;
    /// <summary>One flat colour, no gradient and no glow. The same value is WizardImageBackColor in stratoscope.iss.</summary>
    private static readonly Color Background = Color.FromArgb(16, 17, 24);

    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var wizard = Wizard()) wizard.Save(Path.Combine(directory, "wizard.png"), ImageFormat.Png);
        using (var small = AppIconOnTransparent(110)) small.Save(Path.Combine(directory, "small.png"), ImageFormat.Png);
    }

    private static Bitmap AppIconOnTransparent(int size) => IconRenderer.AppIcon(size);

    private static Bitmap Wizard()
    {
        var bmp = new Bitmap(WizardWidth, WizardHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(Background);

            // The app icon, large, on the optical centre of the upper half.
            const int icon = 220;
            var iconX = (WizardWidth - icon) / 2; var iconY = 190;
            using (var mark = IconRenderer.AppIcon(icon)) g.DrawImage(mark, iconX, iconY, icon, icon);

            // The name and one line about what it does, centred under the icon.
            var centre = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center };
            using (var name = Typography.Strong(46))
            using (var line = Typography.Regular(23))
            using (var white = new SolidBrush(Color.FromArgb(244, 245, 250)))
            using (var dim = new SolidBrush(Color.FromArgb(150, 155, 178)))
            {
                g.DrawString("Stratoscope", name, white, new RectangleF(0, 470, WizardWidth, 70), centre);
                g.DrawString("A speedometer for your local LLM.", line, dim, new RectangleF(0, 548, WizardWidth, 36), centre);
            }
        }
        return bmp;
    }
}
