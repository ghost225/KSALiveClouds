// KSALiveClouds: satellite signal -> KSA coverage cubemaps (base + top layer) in KSA's own face convention
// (Celestial.DirectionToCubemap on planet-fixed directions: Z = north pole, longitude = atan2(Y, X)).
// The data (~8 km) is coarser than a 4096 face (~2.4 km), so the live signal is sampled on a half-size grid and
// upsampled, then the stock map's small-scale texture (its full level minus its 8x-smaller mip) is added before
// the coverage curve so cloud edges look like clouds rather than blurred pixels. Beyond the satellites' reach
// (|lat| > ~72.7) the stock map takes over, blended over the last few degrees.

namespace KSALiveClouds;

public static class CubeBuilder
{
    /// <summary>Planet-fixed direction of a face texel; the inverse of Celestial.DirectionToCubemap.</summary>
    public static (double X, double Y, double Z) FaceDir(int face, double u, double v)
    {
        double s = 2 * u - 1, t = 2 * v - 1;
        (double x, double y, double z) = face switch
        {
            0 => (1.0, -t, -s),
            1 => (-1.0, -t, s),
            2 => (s, 1.0, t),
            3 => (s, -1.0, -t),
            4 => (s, -t, 1.0),
            _ => (-s, -t, -1.0),
        };
        double n = Math.Sqrt(x * x + y * y + z * z);
        return (x / n, y / n, z / n);
    }

    /// <summary>KSA's Celestial.DirectionToCubemap: face and uv in [0, 1].</summary>
    public static int DirToFace(double x, double y, double z, out double u, out double v)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        int face;
        double a, b;
        if (ax >= ay && ax >= az)
        {
            if (x > 0) { face = 0; a = -z; b = -y; } else { face = 1; a = z; b = -y; }
            a /= ax; b /= ax;
        }
        else if (ay >= ax && ay >= az)
        {
            if (y > 0) { face = 2; a = x; b = z; } else { face = 3; a = x; b = -z; }
            a /= ay; b /= ay;
        }
        else
        {
            if (z > 0) { face = 4; a = x; b = -y; } else { face = 5; a = -x; b = -y; }
            a /= az; b /= az;
        }
        u = a * 0.5 + 0.5;
        v = b * 0.5 + 0.5;
        return face;
    }

    private sealed class LiveFace
    {
        public float[] Signal = null!, Counts = null!, StockWeight = null!;
        public int N;
    }

    /// <summary>Signal, counts and stock weight on an n x n grid of one face (bilinear from the GMGSI grid).</summary>
    private static LiveFace SampleFace(IrGrid g, float[] signal, int face, int n)
    {
        var lf = new LiveFace { N = n, Signal = new float[n * n], Counts = new float[n * n], StockWeight = new float[n * n] };
        double latEdgeN = g.LatMax, latEdgeS = g.LatMin;
        Parallel.For(0, n, j =>
        {
            for (int i = 0; i < n; i++)
            {
                (double x, double y, double z) = FaceDir(face, (i + 0.5) / n, (j + 0.5) / n);
                double lat = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI;
                double lon = Math.Atan2(y, x) * 180 / Math.PI;
                float w = Math.Max(Coverage.Smooth(latEdgeN - 6, latEdgeN - 0.5, lat), Coverage.Smooth(-latEdgeS - 6, -latEdgeS - 0.5, -lat));
                int k = j * n + i;
                lf.StockWeight[k] = w;
                if (w >= 1)
                {
                    lf.Signal[k] = 0;
                    lf.Counts[k] = 0;
                    continue;
                }
                double row = (IrGrid.MercY(Math.Clamp(lat, latEdgeS, latEdgeN)) - g.Y0) / g.Dy;
                double col = (lon - g.Lon0) / g.Dlon;
                (float sg, float ct, bool ok) = Bilinear(g, signal, row, col);
                if (!ok)
                {
                    lf.StockWeight[k] = 1;
                    continue;
                }
                lf.Signal[k] = sg;
                lf.Counts[k] = ct;
            }
        });
        return lf;
    }

    private static (float Signal, float Counts, bool Ok) Bilinear(IrGrid g, float[] signal, double row, double col)
    {
        row = Math.Clamp(row, 0, g.H - 1);
        int r0 = (int)row, r1 = Math.Min(g.H - 1, r0 + 1);
        double tr = row - r0;
        int c0 = (int)Math.Floor(col);
        double tc = col - c0;
        c0 = (c0 % g.W + g.W) % g.W;
        int c1 = (c0 + 1) % g.W;
        float s = 0, c = 0, wsum = 0;
        Span<int> rr = stackalloc int[] { r0, r0, r1, r1 };
        Span<int> cc = stackalloc int[] { c0, c1, c0, c1 };
        Span<double> ww = stackalloc double[] { (1 - tr) * (1 - tc), (1 - tr) * tc, tr * (1 - tc), tr * tc };
        for (int q = 0; q < 4; q++)
        {
            int idx = rr[q] * g.W + cc[q];
            float sv = signal[idx];
            if (float.IsNaN(sv))
                continue;
            s += (float)(sv * ww[q]);
            c += (float)(g.Counts[idx] * ww[q]);
            wsum += (float)ww[q];
        }
        if (wsum < 1e-4f)
            return (0, 0, false);
        return (s / wsum, c / wsum, true);
    }

    private static float Upsample(float[] a, int n, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, n - 1);
        fy = Math.Clamp(fy, 0, n - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(n - 1, x0 + 1), y1 = Math.Min(n - 1, y0 + 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float t = a[y0 * n + x0] + (a[y0 * n + x1] - a[y0 * n + x0]) * tx;
        float b = a[y1 * n + x0] + (a[y1 * n + x1] - a[y1 * n + x0]) * tx;
        return t + (b - t) * ty;
    }

    private static float Upsample(byte[] a, int n, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, n - 1);
        fy = Math.Clamp(fy, 0, n - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(n - 1, x0 + 1), y1 = Math.Min(n - 1, y0 + 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float t = a[y0 * n + x0] + (a[y0 * n + x1] - a[y0 * n + x0]) * tx;
        float b = a[y1 * n + x0] + (a[y1 * n + x1] - a[y1 * n + x0]) * tx;
        return t + (b - t) * ty;
    }

    public enum Layer { Base, Top }

    public const int TypeFace = 1024;     // ~10 km a texel: the selector is a smooth field, finer than its data

    /// <summary>KSA's detail selector for the base layer as a cubemap (TypeFace faces, BC4 mip chain [level][face]):
    /// 0 = detail tile A (stratus types), 1 = tile B (cumulus, a little congestus). Satellite: lumpy fields and
    /// deep-convective tops lean 1, smooth decks 0, mixed with the stock noise for variety; stock alone poleward
    /// of the data. stockSel = the stock 2D map (equirectangular, row 0 north).</summary>
    public static byte[][][] BuildTypeLayer(IrGrid g, float[] lumpy, (byte[] Px, int W, int H) stockSel, Config cfg)
    {
        int n = TypeFace, levels = (int)Math.Log2(n) + 1;
        float l0 = (float)cfg.LumpyLow, l1 = (float)cfg.LumpyHigh, mixData = (float)cfg.TypeFromData;
        float cd0 = (float)Coverage.KToCount(cfg.DeepStartK), cd1 = (float)Coverage.KToCount(cfg.DeepFullK);
        var outLevels = new byte[levels][][];
        for (int l = 0; l < levels; l++)
            outLevels[l] = new byte[6][];
        for (int face = 0; face < 6; face++)
        {
            var px = new byte[n * n];
            Parallel.For(0, n, j =>
            {
                for (int i = 0; i < n; i++)
                {
                    (double x, double y, double z) = FaceDir(face, (i + 0.5) / n, (j + 0.5) / n);
                    double lat = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI, lon = Math.Atan2(y, x) * 180 / Math.PI;
                    float stock = SampleEquirect(stockSel, lat, lon) / 255f;
                    float w = Math.Max(Coverage.Smooth(g.LatMax - 6, g.LatMax - 0.5, lat), Coverage.Smooth(-g.LatMin - 6, -g.LatMin - 0.5, -lat));
                    float v = stock;
                    if (w < 1)
                    {
                        double row = (IrGrid.MercY(Math.Clamp(lat, g.LatMin, g.LatMax)) - g.Y0) / g.Dy, col = (lon - g.Lon0) / g.Dlon;
                        (float lu, float ct, bool ok) = Bilinear(g, lumpy, row, col);
                        if (ok)
                        {
                            if (float.IsNaN(ct))
                                ct = 0;
                            float phys = Math.Max(Coverage.Smooth(l0, l1, lu), Coverage.Smooth(cd0, cd1, ct));
                            v = stock + (phys - stock) * mixData;
                            v += (stock - v) * w;
                        }
                    }
                    px[j * n + i] = (byte)Math.Clamp(v * 255 + 0.5f, 0, 255);
                }
            });
            byte[] cur = px;
            int cs = n;
            for (int l = 0; l < levels; l++)
            {
                outLevels[l][face] = Bc4.Encode(cur, cs);
                if (cs == 1)
                    break;
                cur = Half(cur, cs);
                cs /= 2;
            }
        }
        return outLevels;
    }

    private static float SampleEquirect((byte[] Px, int W, int H) m, double lat, double lon)
    {
        double fx = ((lon + 180) / 360 * m.W - 0.5 + m.W) % m.W, fy = Math.Clamp((90 - lat) / 180 * m.H - 0.5, 0, m.H - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = (x0 + 1) % m.W, y1 = Math.Min(m.H - 1, y0 + 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        float t = m.Px[y0 * m.W + x0] + (m.Px[y0 * m.W + x1] - m.Px[y0 * m.W + x0]) * tx;
        float b = m.Px[y1 * m.W + x0] + (m.Px[y1 * m.W + x1] - m.Px[y1 * m.W + x0]) * tx;
        return t + (b - t) * ty;
    }

    private static byte[] Half(byte[] src, int srcS)
    {
        int ns = srcS / 2;
        var next = new byte[ns * ns];
        Parallel.For(0, ns, y =>
        {
            for (int x = 0; x < ns; x++)
            {
                int a = (2 * y) * srcS + 2 * x;
                next[y * ns + x] = (byte)((src[a] + src[a + 1] + src[a + srcS] + src[a + srcS + 1] + 2) >> 2);
            }
        });
        return next;
    }

    /// <summary>Builds one layer's full mip chain as BC4 blocks [level][face], and its 256-texel faces (for the
    /// coverage API) as bytes [face].</summary>
    public static (byte[][][] Levels, byte[][] Small) BuildLayer(IrGrid g, float[] signal, Ktx2.Cube stock, Config cfg, Layer layer, Action<string>? log = null)
    {
        int size = cfg.FaceSize;
        int stockLevel = Math.Max(0, (int)Math.Round(Math.Log2(stock.Size / (double)size)));
        int lowLevel = Math.Min(stock.Levels - 1, stockLevel + 3);
        int stockSize = Math.Max(1, stock.Size >> stockLevel), lowSize = Math.Max(1, stock.Size >> lowLevel);
        if (stockSize != size)
            throw new InvalidDataException($"stock map is {stock.Size}, cannot make {size} faces from it");
        byte[] stockBlocks = stock.LevelBlocks(stockLevel), lowBlocks = stock.LevelBlocks(lowLevel);
        int levels = (int)Math.Log2(size) + 1;
        var outLevels = new byte[levels][][];
        for (int l = 0; l < levels; l++)
            outLevels[l] = new byte[6][];
        var small = new byte[6][];
        int liveN = Math.Max(256, size / 2);
        float c0 = (float)cfg.CloudStart, c1 = (float)cfg.CloudFull, detail = (float)cfg.Detail;
        float ci0 = (float)Coverage.KToCount(cfg.CirrusStartK), ci1 = (float)Coverage.KToCount(cfg.CirrusFullK);
        float cd0 = (float)Coverage.KToCount(cfg.DeepStartK), cd1 = (float)Coverage.KToCount(cfg.DeepFullK);
        float keepUnder = (float)cfg.BaseUnderCirrus;
        for (int face = 0; face < 6; face++)
        {
            LiveFace lf = SampleFace(g, signal, face, liveN);
            byte[] st = stock.Face(stockLevel, face, stockBlocks);
            byte[] lo = stock.Face(lowLevel, face, lowBlocks);
            var px = new byte[size * size];
            double toLive = liveN / (double)size, toLow = lowSize / (double)size;
            Parallel.For(0, size, j =>
            {
                for (int i = 0; i < size; i++)
                {
                    double lx = (i + 0.5) * toLive - 0.5, ly = (j + 0.5) * toLive - 0.5;
                    float w = Upsample(lf.StockWeight, liveN, lx, ly);
                    float stockV = st[j * size + i];
                    float live = 0;
                    if (w < 1)
                    {
                        float hp = stockV - Upsample(lo, lowSize, (i + 0.5) * toLow - 0.5, (j + 0.5) * toLow - 0.5);
                        float raw = Upsample(lf.Signal, liveN, lx, ly);
                        // detail only where the satellite sees cloud: none in clear sky (no speckle), full at the
                        // edges, half inside solid decks
                        float cov0 = Coverage.Smooth(c0, c1, raw);
                        float edge = Coverage.Smooth(0, 0.3, cov0) * (1 - 0.5f * Coverage.Smooth(0.7, 1, cov0));
                        float sg = raw + detail * edge * hp;
                        if (layer == Layer.Base)
                        {
                            live = Coverage.Smooth(c0, c1, sg);
                            // high cloud belongs to the top layer: under it keep only part of the 2 km deck
                            // (IR can't see below), all of it under deep convection (towers reach the ground)
                            float ct = Upsample(lf.Counts, liveN, lx, ly);
                            float high = Coverage.Smooth(ci0, ci1, ct), deep = Coverage.Smooth(cd0, cd1, ct);
                            float keep = keepUnder + (1 - keepUnder) * deep;
                            live *= 1 - (1 - keep) * high;
                        }
                        else
                        {
                            float ct = Upsample(lf.Counts, liveN, lx, ly) + detail * 0.5f * hp;
                            live = Coverage.Smooth(ci0, ci1, ct) * Coverage.Smooth(c0, c0 + 20, sg);
                        }
                        live *= 255;
                    }
                    float v = live + (stockV - live) * w;
                    px[j * size + i] = (byte)Math.Clamp(v + 0.5f, 0, 255);
                }
            });
            // mip chain: 2x2 box down to 1x1
            byte[] cur = px;
            int cs = size;
            for (int l = 0; l < levels; l++)
            {
                outLevels[l][face] = Bc4.Encode(cur, cs);
                if (cs == 256 || (l == 0 && cs < 256))
                    small[face] = cur;
                if (cs == 1)
                    break;
                cur = Half(cur, cs);
                cs /= 2;
            }
            log?.Invoke($"  {layer} face {face} done");
        }
        return (outLevels, small);
    }
}
