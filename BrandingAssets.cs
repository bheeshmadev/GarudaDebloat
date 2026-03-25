using System.Drawing.Drawing2D;

namespace GarudaDebloat;

internal static class BrandingAssets
{
    public static Icon CreateAppIcon()
    {
        using Bitmap bmp = CreateHeaderLogoBitmap(64, 64);
        IntPtr handle = bmp.GetHicon();
        using Icon temp = Icon.FromHandle(handle);
        Icon clone = (Icon)temp.Clone();
        DestroyIcon(handle);
        return clone;
    }

    public static Bitmap CreateHeaderLogoBitmap(int width = 54, int height = 54)
    {
        Bitmap bmp = new(width, height);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        Color bg = ColorTranslator.FromHtml("#1a1a2a");
        Color accent = ColorTranslator.FromHtml("#e05252");
        Color white = ColorTranslator.FromHtml("#f3f3f7");

        RectangleF shieldRect = new(2, 2, width - 4, height - 4);
        using GraphicsPath shield = new();
        shield.AddPolygon(
        [
            new PointF(shieldRect.Left + shieldRect.Width * 0.5f, shieldRect.Top),
            new PointF(shieldRect.Right, shieldRect.Top + shieldRect.Height * 0.22f),
            new PointF(shieldRect.Right - 5, shieldRect.Bottom - shieldRect.Height * 0.33f),
            new PointF(shieldRect.Left + shieldRect.Width * 0.5f, shieldRect.Bottom),
            new PointF(shieldRect.Left + 5, shieldRect.Bottom - shieldRect.Height * 0.33f),
            new PointF(shieldRect.Left, shieldRect.Top + shieldRect.Height * 0.22f)
        ]);

        using SolidBrush bgBrush = new(bg);
        using Pen accentPen = new(accent, 3f);
        g.FillPath(bgBrush, shield);
        g.DrawPath(accentPen, shield);

        using Pen swordPen = new(white, 3f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawLine(
            swordPen,
            new PointF(width * 0.35f, height * 0.70f),
            new PointF(width * 0.67f, height * 0.30f));

        using Pen guardPen = new(accent, 3f);
        g.DrawLine(
            guardPen,
            new PointF(width * 0.36f, height * 0.58f),
            new PointF(width * 0.52f, height * 0.71f));

        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
