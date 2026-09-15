// Build the DSH application icon straight from the favicon.svg that ships inside
// @deepseek-ai/dsh-web-frontend — no browser, no image library.
//
// The mark is a single filled path (the whale), whose eye and mouth are holes
// cut out by the nonzero fill rule, drawn on a 0 0 50 50 view box using only
// M / C / Z commands. This program parses exactly that command set, scales it
// onto a DeepSeek-blue rounded tile, and writes a multi-resolution .ico.
//
// Build: csc /target:exe /out:MakeIcon.exe MakeIcon.cs
// Run:   MakeIcon.exe <favicon.svg> <DSH.ico>

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

internal static class MakeIcon
{
    const int Master = 256;
    const float ViewBox = 50f;      // the favicon's viewBox side
    const float MarkFraction = 0.70f; // mark size relative to the tile
    static readonly Color Brand = Color.FromArgb(0x4D, 0x6B, 0xFE);

    static void Main(string[] args)
    {
        string svgPath = args[0];
        string outIco = args[1];

        string svg = File.ReadAllText(svgPath);
        var match = Regex.Match(svg, @"<path[^>]*\sd\s*=\s*""([^""]+)""", RegexOptions.Singleline);
        if (!match.Success) throw new Exception("no <path d=\"...\"> found in " + svgPath);

        using (var master = RenderMaster(match.Groups[1].Value))
        {
            int[] sizes = { 256, 128, 96, 64, 48, 40, 32, 24, 20, 16 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) pngs[i] = Encode(master, sizes[i]);
            WriteIco(outIco, sizes, pngs);
            Console.WriteLine("wrote " + outIco + " (" + sizes.Length + " sizes)");
        }
    }

    /// <summary>Blue rounded tile with the white whale centred on it.</summary>
    static Bitmap RenderMaster(string d)
    {
        var bmp = new Bitmap(Master, Master, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using (var tile = RoundedRect(new Rectangle(0, 0, Master, Master), 58))
            using (var brush = new SolidBrush(Brand))
                g.FillPath(brush, tile);

            float mark = Master * MarkFraction;
            float scale = mark / ViewBox;
            using (var gp = ParsePath(d))
            using (var white = new SolidBrush(Color.White))
            {
                var saved = g.Save();
                g.TranslateTransform((Master - mark) / 2f, (Master - mark) / 2f);
                g.ScaleTransform(scale, scale);
                g.FillPath(white, gp);          // nonzero fill keeps eye/mouth as holes
                g.Restore(saved);
            }
        }
        return bmp;
    }

    /// <summary>Parse SVG M / C / Z (absolute; the favicon uses nothing else).</summary>
    static GraphicsPath ParsePath(string d)
    {
        var gp = new GraphicsPath();
        var tokens = Regex.Matches(d, @"[MCZ]|-?\d*\.?\d+(?:[eE][-+]?\d+)?");
        float cx = 0, cy = 0, sx = 0, sy = 0;
        float[] p = new float[12];
        int n = 0, i = 0;

        while (i < tokens.Count)
        {
            string t = tokens[i].Value;
            if (t == "M")
            {
                if (n >= 2) { }
                i++;
                cx = sx = F(tokens[i++].Value);
                cy = sy = F(tokens[i++].Value);
                n = 0;
                while (i < tokens.Count && tokens[i].Value != "M" && tokens[i].Value != "C" && tokens[i].Value != "Z")
                {
                    float x = F(tokens[i++].Value), y = F(tokens[i++].Value);
                    gp.AddLine(cx, cy, x, y);
                    cx = x; cy = y;
                }
            }
            else if (t == "C")
            {
                i++;
                n = 0;
                while (i < tokens.Count && tokens[i].Value != "M" && tokens[i].Value != "C" && tokens[i].Value != "Z")
                {
                    p[n++] = F(tokens[i++].Value);
                    if (n == 6)
                    {
                        gp.AddBezier(cx, cy, p[0], p[1], p[2], p[3], p[4], p[5]);
                        cx = p[4]; cy = p[5];
                        n = 0;
                    }
                }
            }
            else if (t == "Z")
            {
                gp.CloseFigure();
                cx = sx; cy = sy;
                i++;
            }
            else i++;
        }
        return gp;
    }

    static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static byte[] Encode(Bitmap src, int size)
    {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static void WriteIco(string path, int[] sizes, byte[][] pngs)
    {
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(pngs[i].Length);
                w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }
    }
}
