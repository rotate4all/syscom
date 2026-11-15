using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using Syscom.Shared;

namespace Syscom.Tray.Osd;

/// <summary>
/// Draws one group of OSD readings into a transparent picture. Each row is a line of readings; the readings in
/// the same place of every row share a column (label, number right-aligned, unit), so the numbers line up like a
/// table. The dashboard's preview lays it out the same way (web/js/osd.js, app.css).
/// </summary>
internal static class OsdRenderer
{
    private static readonly Dictionary<string, FontFamily> Families = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Bitmap Scratch = new(1, 1);
    private static readonly Graphics Measure = Graphics.FromImage(Scratch);
    private static readonly StringFormat Typo = MakeFormat();

    private const float UnitScale = 0.8f;   // units are a little smaller than the number

    private static StringFormat MakeFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    /// <summary>The font family, or Segoe UI when the PC doesn't have it.</summary>
    public static FontFamily Family(string name)
    {
        if (Families.TryGetValue(name, out var f)) return f;
        try { f = new FontFamily(name); }
        catch (ArgumentException)
        {
            try { f = new FontFamily("Segoe UI"); } catch (ArgumentException) { f = FontFamily.GenericSansSerif; }
        }
        return Families[name] = f;
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return Color.FromArgb(255, (rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
        return fallback;
    }

    private sealed record Run(string Text, float Em, float Width, Color Color);

    private sealed record Cell(Run Label, Run Number, Run Unit, float Em);

    /// <param name="scale">Windows' display scaling of the screen it goes on (1.5 = 150 %).</param>
    /// <param name="numberWidths">Widest number seen per column so far (kept by the caller), so the group doesn't
    /// jump around when a reading gains or loses a digit.</param>
    public static Bitmap Render(OsdGroup group, OsdLook look, double scale, List<float> numberWidths)
    {
        var family = Family(look.Font);
        var style = look.Bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold
            : family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
        var baseEm = (float)(look.Size * scale);
        var labelColor = ParseColor(look.LabelColor, Color.FromArgb(156, 195, 255));
        var valueColor = ParseColor(look.Color, Color.White);
        float emHeight = family.GetEmHeight(style);
        var ascent = family.GetCellAscent(style) / emHeight;
        var descent = family.GetCellDescent(style) / emHeight;

        float Width(string text, float em)
        {
            if (text.Length == 0) return 0;
            using var font = new Font(family, em, style, GraphicsUnit.Pixel);
            return Measure.MeasureString(text, font, PointF.Empty, Typo).Width;
        }
        Run MakeRun(string text, float em, Color color) => new(text, em, Width(text, em), color);

        var rows = group.Rows.Select(row => row.Select(t =>
        {
            var em = baseEm * (float)Math.Clamp(t.Scale, 0.3, 3);
            return new Cell(MakeRun(t.Label, em, labelColor), MakeRun(t.Number, em, ParseColor(t.Color, valueColor)),
                MakeRun(t.Unit, em * UnitScale, labelColor), em);
        }).ToList()).ToList();

        // Columns: widest label, number and unit in each, and the biggest size (for the gaps).
        var cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var labelW = new float[cols];
        var numW = new float[cols];
        var unitW = new float[cols];
        var colEm = new float[cols];
        foreach (var row in rows)
        {
            for (var k = 0; k < row.Count; k++)
            {
                labelW[k] = Math.Max(labelW[k], row[k].Label.Width);
                numW[k] = Math.Max(numW[k], row[k].Number.Width);
                unitW[k] = Math.Max(unitW[k], row[k].Unit.Width);
                colEm[k] = Math.Max(colEm[k], row[k].Em);
            }
        }
        while (numberWidths.Count < cols) numberWidths.Add(0);
        for (var k = 0; k < cols; k++) numW[k] = numberWidths[k] = Math.Max(numW[k], numberWidths[k]);

        var gapLabel = colEm.Select(e => 0.4f * e).ToArray();
        var gapUnit = colEm.Select(e => 0.2f * e).ToArray();
        var colX = new float[cols];
        var colW = new float[cols];
        var pad = look.Backdrop > 0 ? 0.45f * baseEm : look.Outline == "none" ? 0.1f * baseEm : 0.2f * baseEm;
        var x = pad;
        for (var k = 0; k < cols; k++)
        {
            colX[k] = x;
            colW[k] = labelW[k] + (labelW[k] > 0 ? gapLabel[k] : 0) + numW[k] + (unitW[k] > 0 ? gapUnit[k] + unitW[k] : 0);
            x += colW[k] + (k < cols - 1 ? 0.9f * colEm[k] : 0);
        }
        var width = Math.Max(1, (int)Math.Ceiling(x + pad));

        // Rows: every reading of a row sits on one baseline.
        var rowTop = new float[rows.Count];
        var rowBase = new float[rows.Count];
        var y = pad;
        for (var r = 0; r < rows.Count; r++)
        {
            var maxEm = rows[r].Count == 0 ? baseEm : rows[r].Max(c => c.Em);
            rowTop[r] = y;
            rowBase[r] = y + maxEm * ascent;
            y += maxEm * (ascent + descent) + (r < rows.Count - 1 ? 0.08f * maxEm : 0);
        }
        var height = Math.Max(1, (int)Math.Ceiling(y + pad));

        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        if (look.Backdrop > 0)
        {
            using var plate = RoundedRect(new RectangleF(0, 0, width, height), 0.35f * baseEm);
            using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(look.Backdrop * 2.55), ParseColor(look.BackdropColor, Color.Black)));
            g.FillPath(brush, plate);
        }

        using var outline = new Pen(Color.FromArgb(225, 0, 0, 0), Math.Max(1.5f, 0.14f * baseEm)) { LineJoin = LineJoin.Round };
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        var shadowOffset = Math.Max(1f, 0.07f * baseEm);
        void Draw(Run run, float left, float baseline)
        {
            if (run.Text.Length == 0) return;
            using var path = new GraphicsPath();
            path.AddString(run.Text, family, (int)style, run.Em, new PointF(left, baseline - run.Em * ascent), Typo);
            if (look.Outline == "outline") g.DrawPath(outline, path);
            else if (look.Outline == "shadow")
            {
                g.TranslateTransform(shadowOffset, shadowOffset);
                g.FillPath(shadow, path);
                g.ResetTransform();
            }
            using var fill = new SolidBrush(run.Color);
            g.FillPath(fill, path);
        }

        for (var r = 0; r < rows.Count; r++)
        {
            for (var k = 0; k < rows[r].Count; k++)
            {
                var c = rows[r][k];
                var numRight = colX[k] + labelW[k] + (labelW[k] > 0 ? gapLabel[k] : 0) + numW[k];
                Draw(c.Label, colX[k], rowBase[r]);
                Draw(c.Number, numRight - c.Number.Width, rowBase[r]);
                Draw(c.Unit, numRight + gapUnit[k], rowBase[r]);
            }
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
