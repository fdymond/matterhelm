using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace MatterHelm.Ui;

/// <summary>
/// Runtime-drawn tray iconography (no .ico assets): the HELM as the single
/// glyph (owner pick 2026-08-23; trademark-safe original geometry - the
/// prior CSA-mark glyph stays archived below), tinted by bridge state. When idle it is
/// monochrome and theme-aware — near-white on a dark taskbar, near-black on
/// a light one — matching how Windows 11 system tray glyphs (OneDrive,
/// Teams, Defender) read. Everything is drawn proportionally to the
/// requested pixel size so 16 px tray reality and larger exports stay crisp
/// from the same geometry.
/// </summary>
public static class TrayIcons
{
    /// <summary>Glyph tint per bridge state (same palette the tray previously used as full-circle icons).</summary>
    private static readonly Dictionary<BridgeState, Color> DotColors = new()
    {
        [BridgeState.Disabled] = Color.FromArgb(158, 158, 158),
        [BridgeState.Running] = Color.FromArgb(255, 179, 0),
        [BridgeState.Connected] = Color.FromArgb(46, 160, 67),
        [BridgeState.Faulted] = Color.FromArgb(229, 72, 77),
    };

    /// <summary>
    /// Builds the per-state tray icons at the system small-icon size, themed
    /// for the current taskbar. Caller owns (and disposes) the icons.
    /// Theme is sampled once here; a taskbar theme flip mid-session keeps the
    /// old tint until the next app start — acceptable for a tray glyph.
    /// </summary>
    public static Dictionary<BridgeState, Icon> CreateStateIcons()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        bool dark = TaskbarIsDark();
        var icons = new Dictionary<BridgeState, Icon>();
        foreach (BridgeState state in DotColors.Keys)
        {
            using Bitmap bitmap = Render(state, size, dark);
            icons[state] = ToIcon(bitmap);
        }

        return icons;
    }

    /// <summary>
    /// The Windows taskbar is dark unless <c>SystemUsesLightTheme</c> says
    /// otherwise (missing value = classic dark taskbar).
    /// </summary>
    private static bool TaskbarIsDark()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                0);
            return value is not int light || light == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Renders one state's icon into a fresh 32bpp bitmap of the given square
    /// size. The glyph is the HELM (candidate A, owner pick 2026-08-23 —
    /// trademark-safe original geometry replacing the CSA certification
    /// mark, which stays archived in <see cref="DrawMatterMark"/>). State
    /// language unchanged: white/theme silhouette = disabled, amber =
    /// enabling/connecting, green = bridge running (hub-connected), red =
    /// faulted.
    /// </summary>
    public static Bitmap Render(BridgeState state, int size, bool darkTaskbar)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        Color glyph = state switch
        {
            BridgeState.Running => DotColors[BridgeState.Running],
            BridgeState.Connected => DotColors[BridgeState.Connected],
            BridgeState.Faulted => DotColors[BridgeState.Faulted],
            _ => darkTaskbar ? Color.FromArgb(245, 245, 245) : Color.FromArgb(32, 32, 32),
        };

        DrawHelm(g, size, glyph);
        return bitmap;
    }

    /// <summary>
    /// ARCHIVED (owner request 2026-08-23: keep, do not delete): the product
    /// glyph 2026-08-09 → 2026-08-23, replaced by <see cref="DrawHelm"/> for
    /// trademark safety. Still rendered as the reference row of
    /// <see cref="ExportLogoCandidates"/>. A faithful
    /// rendition of the Matter certification mark — three units at 120°
    /// rotational symmetry, each a thick radial arm plus an arc whose circle
    /// is centered on that arm's OUTER tip (proportions measured from the
    /// reference art: arm ~0.17r→0.42r, arc radius ~0.32r, sweep ~96°
    /// straddling the inward direction, so the segment bows toward the glyph
    /// center and its ends flare toward the neighbouring units). Drawn for
    /// the owner's personal build; the mark belongs to the CSA — revisit
    /// before any distribution.
    /// </summary>
    private static void DrawMatterMark(Graphics g, int size, Color glyph)
    {
        float s = size;
        var center = new PointF(0.50f * s, 0.52f * s);
        float innerR = 0.15f * s;
        float outerR = 0.40f * s;
        // Tighter circle + wider sweep than the raw reference measurements:
        // at tray sizes a large-radius 96° arc renders nearly straight and
        // the three units read as a six-armed asterisk; ~0.26r/112° keeps
        // the arcs legibly curved while preserving the mark's silhouette.
        float arcR = 0.26f * s;
        const float ArcSweepDegrees = 112f;
        float stroke = Math.Max(1.6f, 0.10f * s);

        using var pen = new Pen(glyph, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        for (int i = 0; i < 3; i++)
        {
            float outwardDeg = -90f + (i * 120f);
            double outward = outwardDeg * Math.PI / 180.0;
            var dir = new PointF((float)Math.Cos(outward), (float)Math.Sin(outward));

            var inner = new PointF(center.X + (dir.X * innerR), center.Y + (dir.Y * innerR));
            var tip = new PointF(center.X + (dir.X * outerR), center.Y + (dir.Y * outerR));
            g.DrawLine(pen, inner, tip);

            g.DrawArc(
                pen,
                tip.X - arcR,
                tip.Y - arcR,
                2 * arcR,
                2 * arcR,
                outwardDeg + 180f - (ArcSweepDegrees / 2f),
                ArcSweepDegrees);
        }
    }

    /// <summary>Clones the bitmap into a GDI+-owned <see cref="Icon"/> and destroys the intermediate native handle.</summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(hIcon);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    // ---- S10-2 logo marks: DrawHelm is the production glyph (candidate A,
    // adopted 2026-08-23); B and C remain as design-aid alternatives in the
    // ExportLogoCandidates sheet. Original geometry throughout - no
    // tri-radial arm-and-arc motif (the launch checklist's trademark
    // blocker).

    /// <summary>THE production mark since 2026-08-23 (was candidate A) — ship's helm: outer ring, 8 handle stubs, 4 inner spokes, hub dot. "You're at the helm."</summary>
    private static void DrawHelm(Graphics g, int size, Color glyph)
    {
        float s = size;
        var center = new PointF(0.5f * s, 0.5f * s);
        float ringR = 0.30f * s;
        float handleOuterR = 0.46f * s;
        float hubR = Math.Max(1.6f, 0.10f * s);
        float ringStroke = Math.Max(1.5f, 0.085f * s);
        float spokeStroke = Math.Max(1.1f, 0.055f * s);

        using var ringPen = new Pen(glyph, ringStroke);
        g.DrawEllipse(ringPen, center.X - ringR, center.Y - ringR, 2 * ringR, 2 * ringR);

        using var handlePen = new Pen(glyph, ringStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var spokePen = new Pen(glyph, spokeStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = (i * 45.0) * Math.PI / 180.0;
            var dir = new PointF((float)Math.Cos(a), (float)Math.Sin(a));
            g.DrawLine(
                handlePen,
                center.X + (dir.X * ringR), center.Y + (dir.Y * ringR),
                center.X + (dir.X * handleOuterR), center.Y + (dir.Y * handleOuterR));
            if (i % 2 == 0)
            {
                g.DrawLine(
                    spokePen,
                    center.X + (dir.X * hubR), center.Y + (dir.Y * hubR),
                    center.X + (dir.X * ringR), center.Y + (dir.Y * ringR));
            }
        }

        using var hub = new SolidBrush(glyph);
        g.FillEllipse(hub, center.X - hubR, center.Y - hubR, 2 * hubR, 2 * hubR);
    }

    /// <summary>Candidate B — helm around a home: ring + 6 handles, solid house silhouette at the hub. "Steer your home."</summary>
    private static void DrawHelmHouse(Graphics g, int size, Color glyph)
    {
        float s = size;
        var center = new PointF(0.5f * s, 0.5f * s);
        float ringR = 0.33f * s;
        float handleOuterR = 0.48f * s;
        float ringStroke = Math.Max(1.5f, 0.08f * s);

        using var ringPen = new Pen(glyph, ringStroke);
        g.DrawEllipse(ringPen, center.X - ringR, center.Y - ringR, 2 * ringR, 2 * ringR);

        using var handlePen = new Pen(glyph, ringStroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 6; i++)
        {
            double a = (-90.0 + (i * 60.0)) * Math.PI / 180.0;
            var dir = new PointF((float)Math.Cos(a), (float)Math.Sin(a));
            g.DrawLine(
                handlePen,
                center.X + (dir.X * ringR), center.Y + (dir.Y * ringR),
                center.X + (dir.X * handleOuterR), center.Y + (dir.Y * handleOuterR));
        }

        // House silhouette filling the hub: roof apex → eaves → walls → floor.
        float h = 0.42f * s; // house box edge
        float left = center.X - (h / 2f);
        float top = center.Y - (h / 2f) + (0.02f * s);
        using var house = new GraphicsPath();
        house.AddPolygon(
        [
            new PointF(center.X, top),
            new PointF(left + h, top + (0.42f * h)),
            new PointF(left + (0.82f * h), top + (0.42f * h)),
            new PointF(left + (0.82f * h), top + h),
            new PointF(left + (0.18f * h), top + h),
            new PointF(left + (0.18f * h), top + (0.42f * h)),
            new PointF(left, top + (0.42f * h)),
        ]);
        using var fill = new SolidBrush(glyph);
        g.FillPath(fill, house);
        using var soften = new Pen(glyph, Math.Max(1f, 0.05f * s)) { LineJoin = LineJoin.Round };
        g.DrawPath(soften, house);
    }

    /// <summary>Candidate C — the literal bridge: arch + deck between two endpoint nodes, house above. "A bridge into the home."</summary>
    private static void DrawBridgeHouse(Graphics g, int size, Color glyph)
    {
        float s = size;
        float stroke = Math.Max(1.5f, 0.085f * s);
        float deckY = 0.78f * s;
        float leftX = 0.10f * s;
        float rightX = 0.90f * s;
        float nodeR = Math.Max(1.5f, 0.075f * s);

        using var pen = new Pen(glyph, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // Deck + arch (the arch bows up from the deck ends).
        g.DrawLine(pen, leftX, deckY, rightX, deckY);
        var archRect = new RectangleF(leftX, deckY - (0.42f * s), rightX - leftX, 0.84f * s);
        g.DrawArc(pen, archRect.X, archRect.Y, archRect.Width, archRect.Height, 180f, 180f);
        using var node = new SolidBrush(glyph);
        g.FillEllipse(node, leftX - nodeR, deckY - nodeR, 2 * nodeR, 2 * nodeR);
        g.FillEllipse(node, rightX - nodeR, deckY - nodeR, 2 * nodeR, 2 * nodeR);

        // Small house resting on the arch apex.
        float h = 0.34f * s;
        float cx = 0.5f * s;
        float top = 0.06f * s;
        using var house = new GraphicsPath();
        house.AddPolygon(
        [
            new PointF(cx, top),
            new PointF(cx + (h / 2f), top + (0.45f * h)),
            new PointF(cx + (0.36f * h), top + (0.45f * h)),
            new PointF(cx + (0.36f * h), top + h),
            new PointF(cx - (0.36f * h), top + h),
            new PointF(cx - (0.36f * h), top + (0.45f * h)),
            new PointF(cx - (h / 2f), top + (0.45f * h)),
        ]);
        using var fill = new SolidBrush(glyph);
        g.FillPath(fill, house);
    }

    /// <summary>
    /// Design aid (<c>--export-logo-candidates</c>): renders the S10-2
    /// trademark-safe logo candidates in all four state tints at tray sizes,
    /// on dark and light strips, into one contact sheet. Returns the file path.
    /// </summary>
    public static string ExportLogoCandidates(string? directory = null)
    {
        string dir = directory ?? Path.Combine(AppContext.BaseDirectory, "logo-candidates");
        Directory.CreateDirectory(dir);
        int[] sizes = [16, 24, 32, 48, 64];
        Action<Graphics, int, Color>[] candidates = [DrawMatterMark, DrawHelm, DrawHelmHouse, DrawBridgeHouse];
        BridgeState[] states =
            [BridgeState.Disabled, BridgeState.Running, BridgeState.Connected, BridgeState.Faulted];

        int cell = 76;
        int width = (sizes.Length * states.Length * cell) + cell;
        int height = candidates.Length * cell * 2;
        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int row = 0;
            foreach (Action<Graphics, int, Color> candidate in candidates)
            {
                foreach (bool dark in (bool[])[true, false])
                {
                    using (var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238)))
                    {
                        g.FillRectangle(back, 0, row * cell, width, cell);
                    }

                    int col = 0;
                    foreach (BridgeState state in states)
                    {
                        Color glyph = state switch
                        {
                            BridgeState.Running => DotColors[BridgeState.Running],
                            BridgeState.Connected => DotColors[BridgeState.Connected],
                            BridgeState.Faulted => DotColors[BridgeState.Faulted],
                            _ => dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(32, 32, 32),
                        };
                        foreach (int size in sizes)
                        {
                            using var tile = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                            using (var tg = Graphics.FromImage(tile))
                            {
                                tg.SmoothingMode = SmoothingMode.AntiAlias;
                                tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                tg.Clear(Color.Transparent);
                                candidate(tg, size, glyph);
                            }

                            int x = (col * cell) + ((cell - size) / 2);
                            int y = (row * cell) + ((cell - size) / 2);
                            g.DrawImage(tile, x, y, size, size);
                            col++;
                        }
                    }

                    row++;
                }
            }
        }

        string path = Path.Combine(dir, "logo-candidates.png");
        sheet.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// Debug/design aid (<c>--export-tray-icons</c>): writes per-state PNGs at
    /// several sizes plus a contact sheet on dark and light taskbar strips.
    /// Returns the export directory.
    /// </summary>
    public static string ExportPreviews(string? directory = null)
    {
        string dir = directory ?? Path.Combine(AppContext.BaseDirectory, "tray-icons");
        Directory.CreateDirectory(dir);
        int[] sizes = [16, 24, 32, 48];
        BridgeState[] states =
            [BridgeState.Disabled, BridgeState.Running, BridgeState.Connected, BridgeState.Faulted];

        foreach (bool dark in (bool[])[true, false])
        {
            foreach (BridgeState state in states)
            {
                foreach (int size in sizes)
                {
                    using Bitmap bmp = Render(state, size, dark);
                    bmp.Save(
                        Path.Combine(dir, $"{state}-{size}px-{(dark ? "dark" : "light")}.png"),
                        ImageFormat.Png);
                }
            }
        }

        // Contact sheet: each theme gets a strip per size, states left→right,
        // drawn on the matching taskbar color with generous padding.
        const int pad = 12;
        int cell = 48 + pad;
        int sheetW = (states.Length * cell) + pad;
        int stripH = sizes.Sum(sz => sz + pad) + pad;
        using var sheet = new Bitmap(sheetW * 2, stripH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int t = 0; t < 2; t++)
            {
                bool dark = t == 0;
                using var back = new SolidBrush(
                    dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238));
                g.FillRectangle(back, t * sheetW, 0, sheetW, stripH);
                int y = pad;
                foreach (int size in sizes)
                {
                    for (int i = 0; i < states.Length; i++)
                    {
                        using Bitmap bmp = Render(states[i], size, dark);
                        g.DrawImageUnscaled(bmp, (t * sheetW) + pad + (i * cell), y);
                    }

                    y += size + pad;
                }
            }
        }

        sheet.Save(Path.Combine(dir, "contact-sheet.png"), ImageFormat.Png);
        return dir;
    }
}
