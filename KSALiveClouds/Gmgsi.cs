// KSALiveClouds: NOAA GMGSI (Global Mosaic of Geostationary Satellite Imagery), longwave IR, from the public
// AWS bucket noaa-gmgsi-pds (no key, ~7 MB a file, hourly). Each file is netCDF4 (HDF5): data[1, 3000, 4999]
// float32 in 0-255 counts (higher = colder), on a Mercator grid from 72.7 S to 72.7 N, 0.072 deg in longitude.

using System.Globalization;
using System.Text.RegularExpressions;
using PureHDF;
using PureHDF.VOL.Native;

namespace KSALiveClouds;

public sealed class IrGrid
{
    public int W, H;
    /// <summary>Row-major counts, row 0 = north. NaN = no data.</summary>
    public float[] Counts = Array.Empty<float>();
    /// <summary>Mercator y = ln(tan(pi/4 + lat/2)) of row 0 and its step per row (negative: rows go south).</summary>
    public double Y0, Dy;
    public double Lon0, Dlon;
    public double LatMax, LatMin;
    public DateTime TimeUtc;
    public string Source = "";

    public static double MercY(double latDeg) => Math.Log(Math.Tan(Math.PI / 4 + latDeg * Math.PI / 360));

    public static IrGrid Read(string path, string source)
    {
        using var f = H5File.OpenRead(path);
        // data is 16 gzip chunks of ~4 MB; PureHDF's default chunk cache is smaller than one, so without a big
        // cache it re-inflates a chunk for every row it copies (~40 s instead of ~1 s). One cache per dataset: the
        // cache is keyed by chunk index only, so a shared one hands lat the chunks of data.
        static float[] ReadAll(NativeFile f, string name) =>
            ((NativeDataset)f.Dataset(name)).Read<float[]>(new H5DatasetAccess(null!, null!, new SimpleReadingChunkCache(64, 512UL << 20)));
        float[] data = ReadAll(f, "data");
        float[] lat = ReadAll(f, "lat");
        float[] lon = ReadAll(f, "lon");
        ulong[] dims = f.Dataset("data").Space.Dimensions;
        int h = (int)dims[^2], w = (int)dims[^1];
        if (data.Length < w * h || lat.Length < w * h)
            throw new InvalidDataException($"GMGSI arrays too small ({data.Length} for {w}x{h})");
        var g = new IrGrid { W = w, H = h, Counts = data, Source = source };
        for (int i = 0; i < data.Length; i++)
            if (data[i] < 0 || data[i] > 300)
                data[i] = float.NaN;
        double latTop = lat[0], latBottom = lat[(h - 1) * w];
        g.LatMax = latTop;
        g.LatMin = latBottom;
        g.Y0 = MercY(latTop);
        g.Dy = (MercY(latBottom) - g.Y0) / (h - 1);
        // col 0 sits on the dateline (+179.9996), col 1 at -179.93: take the step from the interior columns
        g.Dlon = (lon[w - 1] - lon[1]) / (w - 2);
        g.Lon0 = lon[1] - g.Dlon;
        try
        {
            long t = f.Dataset("time").Read<int[]>()[0];
            g.TimeUtc = DateTime.UnixEpoch.AddSeconds(t);
        }
        catch { g.TimeUtc = TimeFromKey(source) ?? DateTime.UtcNow; }
        return g;
    }

    /// <summary>"..._s202610242300000_..." -> 2026-10-24 23:00 UTC.</summary>
    public static DateTime? TimeFromKey(string key)
    {
        Match m = Regex.Match(key, @"_s(\d{12})");
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d) ? d : null;
    }
}

public static class GmgsiFetch
{
    private const string Bucket = "https://noaa-gmgsi-pds.s3.amazonaws.com";

    /// <summary>The newest longwave-IR key, looking back over today and yesterday (UTC).</summary>
    public static async Task<string?> LatestKey(HttpClient http, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        for (int back = 0; back < 2; back++)
        {
            DateTime d = now.AddDays(-back);
            string url = $"{Bucket}/?list-type=2&prefix=GMGSI_LW/{d:yyyy}/{d:MM}/{d:dd}/";
            string xml = await http.GetStringAsync(url, ct);
            string? best = null;
            foreach (Match m in Regex.Matches(xml, @"<Key>([^<]+\.nc)</Key>"))
                if (best == null || string.CompareOrdinal(m.Groups[1].Value, best) > 0)
                    best = m.Groups[1].Value;
            if (best != null)
                return best;
        }
        return null;
    }

    public static async Task Download(HttpClient http, string key, string dest, CancellationToken ct)
    {
        string tmp = dest + ".part";
        using (HttpResponseMessage r = await http.GetAsync($"{Bucket}/{key}", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            r.EnsureSuccessStatusCode();
            await using FileStream fs = File.Create(tmp);
            await r.Content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, dest, true);
    }
}
