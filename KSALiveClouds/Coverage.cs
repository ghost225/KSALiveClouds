// KSALiveClouds: GMGSI IR counts -> "cloud signal" (how much colder than the local clear sky, in counts).
// IR alone can't tell a cold clear surface (winter land, sea ice) from cloud, so the clear-sky reference is the
// warm end (5th percentile) of each ~4 deg block, taken as the warmest of its 3x3 neighbours so a fully overcast
// block borrows a clear neighbour, and capped by latitude so clear never counts colder than ~285 K at the equator
// or ~250 K at 72 deg. Known blind spot: low warm stratus (marine stratocumulus) is barely colder than the sea.

namespace KSALiveClouds;

public static class Coverage
{
    private const int Block = 60;

    /// <summary>Counts -> brightness temperature (K), the GOES/GMGSI 8-bit IR scale.</summary>
    public static double CountToK(double c) => c <= 176 ? 330 - c / 2 : 418 - c;
    public static double KToCount(double k) => k >= 242 ? 2 * (330 - k) : 418 - k;

    public static float Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return (float)(t * t * (3 - 2 * t));
    }

    /// <summary>Per-pixel signal = counts - clear reference (NaN where there is no data).</summary>
    public static float[] Signal(IrGrid g)
    {
        int w = g.W, h = g.H;
        int nbx = (w + Block - 1) / Block, nby = (h + Block - 1) / Block;
        var refs = new float[nby * nbx];
        Parallel.For(0, nby, by =>
        {
            var vals = new List<float>(Block * Block);
            for (int bx = 0; bx < nbx; bx++)
            {
                vals.Clear();
                for (int y = by * Block; y < Math.Min(h, (by + 1) * Block); y++)
                for (int x = bx * Block; x < Math.Min(w, (bx + 1) * Block); x++)
                {
                    float v = g.Counts[y * w + x];
                    if (!float.IsNaN(v))
                        vals.Add(v);
                }
                if (vals.Count == 0)
                {
                    refs[by * nbx + bx] = float.NaN;
                    continue;
                }
                vals.Sort();
                refs[by * nbx + bx] = vals[vals.Count / 20];
            }
        });
        // warmest of the 3x3 neighbourhood (wrapping in longitude), then the latitude cap
        var m = new float[nby * nbx];
        for (int by = 0; by < nby; by++)
        {
            int row = Math.Min(h - 1, by * Block + Block / 2);
            double lat = LatOfRow(g, row);
            float cap = (float)(90 + Math.Pow(Math.Abs(lat) / 72.0, 2) * 70);
            for (int bx = 0; bx < nbx; bx++)
            {
                float best = float.NaN;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int yy = Math.Clamp(by + dy, 0, nby - 1), xx = ((bx + dx) % nbx + nbx) % nbx;
                    float v = refs[yy * nbx + xx];
                    if (!float.IsNaN(v) && (float.IsNaN(best) || v < best))
                        best = v;
                }
                m[by * nbx + bx] = float.IsNaN(best) ? cap : Math.Min(best, cap);
            }
        }
        var signal = new float[w * h];
        Parallel.For(0, h, y =>
        {
            double fy = Math.Clamp((y + 0.5) / Block - 0.5, 0, nby - 1);
            int y0 = (int)fy, y1 = Math.Min(nby - 1, y0 + 1);
            float ty = (float)(fy - y0);
            for (int x = 0; x < w; x++)
            {
                double fx = (x + 0.5) / Block - 0.5;
                int x0 = (int)Math.Floor(fx);
                float tx = (float)(fx - x0);
                int xa = (x0 % nbx + nbx) % nbx, xb = (xa + 1) % nbx;
                float top = m[y0 * nbx + xa] + (m[y0 * nbx + xb] - m[y0 * nbx + xa]) * tx;
                float bot = m[y1 * nbx + xa] + (m[y1 * nbx + xb] - m[y1 * nbx + xa]) * tx;
                float r = top + (bot - top) * ty;
                signal[y * w + x] = g.Counts[y * w + x] - r;
            }
        });
        return signal;
    }

    /// <summary>Regional lumpiness: std of counts over 5x5 pixels (~35 km), then averaged over 17x17 (~120 km) so
    /// a single cloud edge doesn't count but a field of small cells does. NaN-free (no-data pixels count as 0).</summary>
    public static float[] Lumpiness(IrGrid g)
    {
        int w = g.W, h = g.H;
        var c = new float[w * h];
        var c2 = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float v = g.Counts[y * w + x];
                v = float.IsNaN(v) ? 0 : v;
                c[y * w + x] = v;
                c2[y * w + x] = v * v;
            }
        });
        Box(c, w, h, 2);
        Box(c2, w, h, 2);
        Parallel.For(0, w * h, i => c[i] = MathF.Sqrt(MathF.Max(0, c2[i] - c[i] * c[i])));
        Box(c, w, h, 8);
        return c;
    }

    /// <summary>In-place separable box mean of radius r: wraps in longitude, clamps at the top and bottom rows.</summary>
    private static void Box(float[] a, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            var row = new float[w];
            double sum = 0;
            for (int k = -r; k <= r; k++)
                sum += a[y * w + ((k % w) + w) % w];
            for (int x = 0; x < w; x++)
            {
                row[x] = (float)(sum * inv);
                sum += a[y * w + (x + r + 1) % w] - a[y * w + ((x - r) % w + w) % w];
            }
            Array.Copy(row, 0, a, y * w, w);
        });
        Parallel.For(0, w, x =>
        {
            var col = new float[h];
            double sum = 0;
            for (int k = -r; k <= r; k++)
                sum += a[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                col[y] = (float)(sum * inv);
                sum += a[Math.Min(h - 1, y + r + 1) * w + x] - a[Math.Max(0, y - r) * w + x];
            }
            for (int y = 0; y < h; y++)
                a[y * w + x] = col[y];
        });
    }

    public static double LatOfRow(IrGrid g, double row)
    {
        double y = g.Y0 + g.Dy * row;
        return (2 * Math.Atan(Math.Exp(y)) - Math.PI / 2) * 180 / Math.PI;
    }
}
