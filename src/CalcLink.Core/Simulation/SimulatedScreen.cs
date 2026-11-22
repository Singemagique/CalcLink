namespace CalcLink.Core.Simulation;

/// <summary>
/// Procedurally draws a plausible calculator screen (BGRA8). The picture depends on
/// <c>t</c> (seconds) so a live-mirror view visibly animates.
/// </summary>
internal static class SimulatedScreen
{
    public static ScreenImage Render(SimDevice device, double t) => device.Kind switch
    {
        SimulatedModel.NspireCXII => RenderNspire(device.LcdWidth, device.LcdHeight, t, device.KeyPresses),
        SimulatedModel.TI89Titanium => RenderTi89(device.LcdWidth, device.LcdHeight, t, device.KeyPresses),
        _ => RenderCe(device.LcdWidth, device.LcdHeight, t, device.KeyPresses),
    };

    private readonly struct Canvas(int w, int h)
    {
        public readonly int W = w, H = h;
        public readonly byte[] Px = new byte[w * h * 4];

        public void Set(int x, int y, uint rgb)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            int i = (y * W + x) * 4;
            Px[i] = (byte)rgb;             // B
            Px[i + 1] = (byte)(rgb >> 8);  // G
            Px[i + 2] = (byte)(rgb >> 16); // R
            Px[i + 3] = 255;               // A
        }

        public void Fill(int x, int y, int w, int h, uint rgb)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(H, y + h); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(W, x + w); xx++)
                    Set(xx, yy, rgb);
        }

        public void HLine(int x0, int x1, int y, uint rgb) { for (int x = x0; x <= x1; x++) Set(x, y, rgb); }
        public void VLine(int x, int y0, int y1, uint rgb) { for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++) Set(x, y, rgb); }

        /// <summary>Plots y = f(x) (in pixels), joining consecutive samples so steep parts stay solid.</summary>
        public void Plot(int x0, int x1, Func<int, double> f, uint rgb, int thickness, int clipTop, int clipBottom)
        {
            int? prev = null;
            for (int x = x0; x <= x1; x++)
            {
                int y = (int)Math.Round(f(x));
                int from = prev ?? y;
                for (int k = 0; k < thickness; k++)
                    for (int yy = Math.Min(from, y); yy <= Math.Max(from, y); yy++)
                        if (yy + k >= clipTop && yy + k <= clipBottom)
                            Set(x, yy + k, rgb);
                prev = y;
            }
        }

        /// <summary>Grey blocks that read as a line of text from a distance.</summary>
        public void FakeText(int x, int y, string pattern, int cell, int height, uint rgb)
        {
            foreach (char c in pattern)
            {
                if (c != ' ')
                    Fill(x, y, cell - 1, height, rgb);
                x += cell;
            }
        }
    }

    private static ScreenImage RenderCe(int w, int h, double t, int keys)
    {
        var c = new Canvas(w, h);
        c.Fill(0, 0, w, h, 0xFFFFFF);

        // Status bar: "NORMAL FLOAT AUTO REAL RADIAN MP" + battery.
        const int bar = 30;
        c.Fill(0, 0, w, bar, 0x2B2B2B);
        c.FakeText(6, 5, "NORMAL FLOAT AUTO REAL RADIAN MP", 7, 6, 0xE0E0E0);
        c.FakeText(6, 17, "PRESS + FOR ΔTbl", 7, 6, 0x8FB3E0);
        c.Fill(w - 26, 9, 18, 11, 0xE0E0E0);
        c.Fill(w - 8, 12, 2, 5, 0xE0E0E0);
        int charge = 4 + (int)(t / 30) % 12;
        c.Fill(w - 24, 11, Math.Min(14, charge), 7, 0x3CB043);
        c.HLine(0, w - 1, bar, 0x000000);

        // Graph: grid, axes, Y1 = sin(X + t), Y2 = 0.5 cos(2X - t).
        int top = bar + 1, bottom = h - 1, midY = (top + bottom) / 2, midX = w / 2;
        for (int x = midX % 20; x < w; x += 20) c.VLine(x, top, bottom, 0xE8E8E8);
        for (int y = midY; y >= top; y -= 20) c.HLine(0, w - 1, y, 0xE8E8E8);
        for (int y = midY; y <= bottom; y += 20) c.HLine(0, w - 1, y, 0xE8E8E8);
        c.HLine(0, w - 1, midY, 0x000000);
        c.VLine(midX, top, bottom, 0x000000);
        double scale = 2 * Math.PI / 160;
        c.Plot(0, w - 1, x => midY - 70 * Math.Sin((x - midX) * scale + t), 0x0044CC, 2, top, bottom);
        c.Plot(0, w - 1, x => midY - 35 * Math.Cos(2 * (x - midX) * scale - t * 0.7), 0xCC2222, 2, top, bottom);

        // Trace cursor running along Y1.
        int tx = (int)((t * 40 + keys * 13) % w);
        int ty = (int)Math.Round(midY - 70 * Math.Sin((tx - midX) * scale + t));
        for (int d = -5; d <= 5; d++) { c.Set(tx + d, ty, 0xFF8800); c.Set(tx, ty + d, 0xFF8800); }
        c.FakeText(6, h - 14, "X=1.5707963  Y=1", 7, 6, 0x000000);
        return new ScreenImage(w, h, c.Px);
    }

    private static ScreenImage RenderNspire(int w, int h, double t, int keys)
    {
        var c = new Canvas(w, h);
        c.Fill(0, 0, w, h, 0xFFFFFF);

        // Document tab bar ("1.1 1.2 ▸ Doc") and scratch toolbar.
        const int bar = 22;
        c.Fill(0, 0, w, bar, 0xD6DEE8);
        c.Fill(4, 3, 30, bar - 5, 0xFFFFFF);
        c.FakeText(9, 9, "1.1", 7, 5, 0x334455);
        c.FakeText(40, 9, "1.2", 7, 5, 0x6A7A8A);
        c.FakeText(w - 120, 9, "*Projectile", 8, 5, 0x334455);
        c.Fill(w - 22, 6, 14, 10, 0x55AA55);
        c.HLine(0, w - 1, bar, 0x8899AA);

        // Graphs app: f1(x) = a·x² with a oscillating; a draggable point.
        int top = bar + 1, bottom = h - 1, midY = (top + bottom) / 2 + 30, midX = w / 2;
        for (int x = midX % 25; x < w; x += 25) c.VLine(x, top, bottom, 0xF0F0F0);
        for (int y = midY % 25 + top; y <= bottom; y += 25) c.HLine(0, w - 1, y, 0xF0F0F0);
        c.HLine(0, w - 1, midY, 0x333333);
        c.VLine(midX, top, bottom, 0x333333);
        double a = 0.012 + 0.008 * Math.Sin(t * 0.8);
        c.Plot(0, w - 1, x => midY - a * (x - midX) * (x - midX) + 60, 0x1D5FB5, 2, top, bottom);
        c.Plot(0, w - 1, x => midY - 40 * Math.Sin((x - midX) / 30.0 - t), 0x2E9E3E, 2, top, bottom);
        c.FakeText(midX + 60, top + 10, "f1(x)=a·x²", 7, 5, 0x1D5FB5);
        c.FakeText(midX + 60, top + 22, "f2(x)=2·sin(x)", 7, 5, 0x2E9E3E);

        int px = midX + (int)(90 * Math.Sin(t * 0.5 + keys * 0.3));
        int py = (int)Math.Round(midY - a * (px - midX) * (px - midX) + 60);
        c.Fill(px - 3, py - 3, 7, 7, 0x000000);
        c.Fill(px - 2, py - 2, 5, 5, 0xFFCC00);
        return new ScreenImage(w, h, c.Px);
    }

    private static ScreenImage RenderTi89(int w, int h, double t, int keys)
    {
        // Monochrome LCD rendered with the classic grey-green palette.
        const uint bg = 0xB7C4A8, ink = 0x1E241C;
        var c = new Canvas(w, h);
        c.Fill(0, 0, w, h, bg);

        // F1..F6 toolbar.
        for (int i = 0; i < 6; i++)
        {
            int x = 1 + i * 26;
            c.HLine(x, x + 24, 0, ink);
            c.VLine(x + 24, 0, 7, ink);
            c.FakeText(x + 3, 2, i == 0 ? "Tools" : i == 1 ? "Zoom" : "Trace", 4, 4, ink);
        }
        c.HLine(0, w - 1, 8, ink);

        // Graph of y1(x) = 2 sin(x + t) between the toolbar and the status line.
        int top = 9, bottom = h - 9, midY = (top + bottom) / 2, midX = w / 2;
        c.HLine(0, w - 1, midY, ink);
        c.VLine(midX, top, bottom, ink);
        for (int x = midX % 10; x < w; x += 10) c.Set(x, midY + 1, ink);
        c.Plot(0, w - 1, x => midY - 30 * Math.Sin((x - midX) / 12.0 + t), ink, 1, top, bottom);
        int tx = (int)((t * 20 + keys * 7) % w);
        int ty = (int)Math.Round(midY - 30 * Math.Sin((tx - midX) / 12.0 + t));
        for (int d = -3; d <= 3; d++) { c.Set(tx + d, ty, ink); c.Set(tx, ty + d, ink); }

        // Status line: "MAIN   RAD AUTO   FUNC".
        c.HLine(0, w - 1, h - 8, ink);
        c.FakeText(2, h - 6, "MAIN  RAD AUTO  FUNC", 5, 4, ink);
        if (((int)(t * 2) & 1) == 0)
            c.Fill(w - 10, h - 6, 6, 4, ink); // "BUSY" blinker
        return new ScreenImage(w, h, c.Px);
    }
}
