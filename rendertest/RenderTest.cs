// Off-screen tray-icon render test harness.
// Renders the same sample strings via several strategies, at 16/20/24 px,
// against dark and light backgrounds, and stitches a labelled comparison grid.
// Usage:  csc /target:exe /out:RenderTest.exe RenderTest.cs
//         RenderTest.exe <outPath.png>

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

internal static class RenderTest
{
    delegate void DrawFn(Graphics g, string text, Color color, Color halo, RectangleF box);

    class Strategy { public string Name; public DrawFn Fn; }
    class Bg { public string Label; public Color Color; }
    class Offset { public int Dx, Dy; }

    static readonly string[] Samples = new[] { "1.5", "0.7", "67" };
    static readonly int[] Sizes = new[] { 16 };
    static readonly Bg[] Bgs = new[] {
        new Bg { Label = "dark",  Color = Color.FromArgb(32, 32, 32) },
        new Bg { Label = "light", Color = Color.FromArgb(230, 230, 230) }
    };
    const int Zoom = 16;
    const int CellPad = 10;

    static readonly Strategy[] Strategies = new[] {
        new Strategy { Name = "A: path-fill + stroked halo (original)",  Fn = DrawPathFillStrokedHalo },
        new Strategy { Name = "B: hinted DrawString, grid-fit AA",       Fn = DrawHintedAA },
        new Strategy { Name = "C: hinted DrawString, no halo",           Fn = DrawHintedAANoHalo },
        new Strategy { Name = "D: hinted DrawString, ClearType grid-fit",Fn = DrawHintedClearType },
        new Strategy { Name = "E: TextRenderer (GDI), AA",               Fn = DrawTextRendererAA },
        new Strategy { Name = "F: TextRenderer (GDI), no halo",          Fn = DrawTextRendererNoHalo },
        new Strategy { Name = "G: path-fill + offset halo",              Fn = DrawPathFillOffsetHalo },
        new Strategy { Name = "H: hinted DrawString, 8-way halo",        Fn = DrawHintedAA8Halo }
    };

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "render-comparison.png";
        using (var grid = BuildGrid())
        {
            grid.Save(outPath, ImageFormat.Png);
        }
        Console.WriteLine("Wrote " + Path.GetFullPath(outPath));
        return 0;
    }

    static Bitmap BuildGrid()
    {
        int cols = Bgs.Length * Sizes.Length * Samples.Length;
        int rows = Strategies.Length;

        int cellW = Sizes[Sizes.Length - 1] * Zoom;
        int cellH = cellW;
        int labelW = 340;
        int headerH = 52;

        int totalW = labelW + cols * (cellW + CellPad) + CellPad;
        int totalH = headerH + rows * (cellH + CellPad) + CellPad;

        var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var hdrFont = new Font("Consolas", 10f, FontStyle.Bold))
            using (var rowFont = new Font("Consolas", 9.5f, FontStyle.Bold))
            using (var subFont = new Font("Consolas", 8f, FontStyle.Regular))
            {
                int x = labelW + CellPad;
                for (int b = 0; b < Bgs.Length; b++)
                {
                    for (int s = 0; s < Sizes.Length; s++)
                    {
                        for (int t = 0; t < Samples.Length; t++)
                        {
                            string h1 = Bgs[b].Label + " " + Sizes[s] + "px";
                            string h2 = Samples[t];
                            g.DrawString(h1, subFont, Brushes.DimGray, new PointF(x, 2));
                            g.DrawString(h2, hdrFont, Brushes.Black, new PointF(x, 16));
                            x += cellW + CellPad;
                        }
                    }
                }

                for (int r = 0; r < Strategies.Length; r++)
                {
                    Strategy strat = Strategies[r];
                    int yRow = headerH + r * (cellH + CellPad);
                    g.DrawString(strat.Name, rowFont, Brushes.Black, new RectangleF(4, yRow + 2, labelW - 8, cellH));

                    int cx = labelW + CellPad;
                    for (int b = 0; b < Bgs.Length; b++)
                    {
                        bool light = Bgs[b].Label == "light";
                        for (int s = 0; s < Sizes.Length; s++)
                        {
                            for (int t = 0; t < Samples.Length; t++)
                            {
                                using (Bitmap cell = RenderCell(strat.Fn, Samples[t], Sizes[s], Bgs[b].Color, light))
                                {
                                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    int drawSize = Sizes[s] * Zoom;
                                    int cellOffsetX = (cellW - drawSize) / 2;
                                    int cellOffsetY = (cellH - drawSize) / 2;
                                    g.DrawImage(cell, new Rectangle(cx + cellOffsetX, yRow + cellOffsetY, drawSize, drawSize),
                                        new Rectangle(0, 0, Sizes[s], Sizes[s]), GraphicsUnit.Pixel);
                                }
                                cx += cellW + CellPad;
                            }
                        }
                    }
                }
            }
        }
        return bmp;
    }

    static Bitmap RenderCell(DrawFn draw, string text, int size, Color bgColor, bool light)
    {
        Bitmap cell = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cell))
        {
            g.Clear(bgColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = light ? Color.FromArgb(255, 20, 100, 20) : Color.FromArgb(255, 110, 230, 110);
            Color halo = light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(190, 15, 15, 15);
            float m = size / 32f;
            draw(g, text, color, halo, new RectangleF(m, 2 * m, size - 2 * m, size - 4 * m));
        }
        return cell;
    }

    // ----- shared font helpers -----

    const float InkGap = 9f, MinSqueeze = 0.85f;

    static GraphicsPath TightText(string text)
    {
        var path = new GraphicsPath();
        FontStyle style = FontStyle.Regular;
        FontFamily family;
        try { family = new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); style = FontStyle.Bold; }
        using (family)
        {
            float x = 0;
            foreach (char ch in text)
            {
                using (var glyph = new GraphicsPath())
                {
                    glyph.AddString(ch.ToString(), family, (int)style, 100f, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF cb = glyph.GetBounds();
                    if (cb.Width <= 0) continue;
                    using (var mat = new Matrix())
                    {
                        mat.Translate(x - cb.X, 0);
                        glyph.Transform(mat);
                    }
                    path.AddPath(glyph, false);
                    x += cb.Width + InkGap;
                }
            }
        }
        return path;
    }

    static void FitPath(GraphicsPath path, string text, RectangleF box)
    {
        RectangleF b = path.GetBounds();
        if (b.Width <= 0 || b.Height <= 0) return;
        RectangleF r = b;
        if (text.Length == 2 && (char.IsDigit(text[0]) || text[0] == '<') && char.IsDigit(text[1]))
            using (var rp = TightText("88")) r = rp.GetBounds();
        float fitW = box.Width / r.Width, fitH = box.Height / r.Height;
        float sy = Math.Min(fitH, fitW / MinSqueeze);
        float sx = Math.Min(Math.Min(sy, fitW), box.Width / b.Width);
        using (var m = new Matrix())
        {
            m.Translate(box.X + box.Width / 2f, box.Y + box.Height / 2f);
            m.Scale(sx, sy);
            m.Translate(-(b.X + b.Width / 2f), -(r.Y + r.Height / 2f));
            path.Transform(m);
        }
    }

    // ----- strategies -----

    static void DrawPathFillStrokedHalo(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        using (var path = TightText(text))
        {
            FitPath(path, text, box);
            float stroke = Math.Max(1.25f, box.Height * 0.08f);
            if (halo.A > 0) using (var pen = new Pen(halo, stroke) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
        }
    }

    static void DrawPathFillOffsetHalo(Graphics g, string text, Color color, Color halo, RectangleF box)
    {
        using (var path = TightText(text))
        {
            FitPath(path, text, box);
            if (halo.A > 0)
            {
                using (var brush = new SolidBrush(halo))
                {
                    int[,] offs = new int[,] { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
                    for (int i = 0; i < 4; i++)
                    {
                        int dx = offs[i, 0], dy = offs[i, 1];
                        g.TranslateTransform(dx, dy);
                        g.FillPath(brush, path);
                        g.TranslateTransform(-dx, -dy);
                    }
                }
            }
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
        }
    }

    static Font PickFont(Graphics g, string text, RectangleF box, StringFormat sf, FontFamily family, FontStyle style)
    {
        int size = (int)Math.Round(box.Height);
        while (size >= 5)
        {
            Font font = new Font(family, size, style, GraphicsUnit.Pixel);
            SizeF m = g.MeasureString(text, font, int.MaxValue, sf);
            if (m.Width <= box.Width && m.Height <= box.Height + 1) return font;
            font.Dispose();
            size--;
        }
        return new Font(family, 5f, style, GraphicsUnit.Pixel);
    }

    static void DrawHintedCore(Graphics g, string text, Color color, Color halo, RectangleF box, bool halo8, TextRenderingHint hint)
    {
        if (string.IsNullOrEmpty(text) || box.Width < 2 || box.Height < 2) return;
        FontFamily family;
        FontStyle style = FontStyle.Regular;
        try { family = new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); style = FontStyle.Bold; }
        using (family)
        using (var sf = new StringFormat(StringFormat.GenericTypographic))
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            TextRenderingHint prev = g.TextRenderingHint;
            g.TextRenderingHint = hint;
            Font font = PickFont(g, text, box, sf, family, style);
            try
            {
                float cx = (float)Math.Round(box.X + box.Width / 2f);
                float cy = (float)Math.Round(box.Y + box.Height / 2f);
                if (halo.A > 0)
                {
                    using (var hb = new SolidBrush(halo))
                    {
                        int[,] offs4 = new int[,] { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
                        int[,] offs8 = new int[,] { { -1, -1 }, { 0, -1 }, { 1, -1 }, { -1, 0 }, { 1, 0 }, { -1, 1 }, { 0, 1 }, { 1, 1 } };
                        int[,] offs = halo8 ? offs8 : offs4;
                        for (int i = 0; i < offs.GetLength(0); i++)
                            g.DrawString(text, font, hb, new PointF(cx + offs[i, 0], cy + offs[i, 1]), sf);
                    }
                }
                using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, new PointF(cx, cy), sf);
            }
            finally { font.Dispose(); g.TextRenderingHint = prev; }
        }
    }

    static void DrawHintedAA(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawHintedCore(g, text, color, halo, box, false, TextRenderingHint.AntiAliasGridFit); }
    static void DrawHintedAA8Halo(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawHintedCore(g, text, color, halo, box, true, TextRenderingHint.AntiAliasGridFit); }
    static void DrawHintedAANoHalo(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawHintedCore(g, text, color, Color.Transparent, box, false, TextRenderingHint.AntiAliasGridFit); }
    static void DrawHintedClearType(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawHintedCore(g, text, color, halo, box, false, TextRenderingHint.ClearTypeGridFit); }

    static void DrawTextRendererCore(Graphics g, string text, Color color, Color halo, RectangleF box, bool drawHalo)
    {
        if (string.IsNullOrEmpty(text) || box.Width < 2 || box.Height < 2) return;
        FontStyle style = FontStyle.Regular;
        FontFamily family;
        try { family = new FontFamily("Segoe UI Semibold"); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); style = FontStyle.Bold; }
        using (family)
        {
            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                                  | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
            int size = (int)Math.Round(box.Height);
            Font font = null;
            while (size >= 5)
            {
                font = new Font(family, size, style, GraphicsUnit.Pixel);
                Size m = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), flags);
                if (m.Width <= (int)box.Width && m.Height <= (int)box.Height + 1) break;
                font.Dispose(); font = null;
                size--;
            }
            if (font == null) font = new Font(family, 5f, style, GraphicsUnit.Pixel);
            try
            {
                Rectangle r = new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
                if (drawHalo && halo.A > 0)
                {
                    int[,] offs = new int[,] { { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 } };
                    for (int i = 0; i < 4; i++)
                        TextRenderer.DrawText(g, text, font, new Rectangle(r.X + offs[i, 0], r.Y + offs[i, 1], r.Width, r.Height), halo, flags);
                }
                TextRenderer.DrawText(g, text, font, r, color, flags);
            }
            finally { font.Dispose(); }
        }
    }

    static void DrawTextRendererAA(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawTextRendererCore(g, text, color, halo, box, true); }
    static void DrawTextRendererNoHalo(Graphics g, string text, Color color, Color halo, RectangleF box)
    { DrawTextRendererCore(g, text, color, halo, box, false); }
}
