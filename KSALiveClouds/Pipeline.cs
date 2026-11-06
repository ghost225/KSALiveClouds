// KSALiveClouds: fetch -> build -> cache. Everything lives in <LocalApplicationData>/KSALiveClouds
// (~/.local/share/KSALiveClouds on Linux): the last GMGSI file, the two generated .ktx2 maps, a 256-texel copy of
// both layers for the coverage API, and snapshot.txt describing them.

using System.Diagnostics;
using System.Globalization;

namespace KSALiveClouds;

public sealed class Snapshot
{
    public string Source = "";
    public DateTime DataTimeUtc, BuiltUtc;
    public int FaceSize;
    public bool HasTop, HasType;
    public string BasePath = "", TopPath = "", TypePath = "", SmallPath = "";
    /// <summary>Why this snapshot (or none) is in use, for the log and the UI.</summary>
    public string Note = "";
    /// <summary>Set only on a fresh build: the maps as BC4 blocks [level][face], and the 256-texel copy, so an
    /// in-game refresh can crossfade without reading the files back.</summary>
    public byte[][][]? BaseLevels, TopLevels, TypeLevels;
    public byte[]? Small;

    public double AgeHours => (DateTime.UtcNow - DataTimeUtc).TotalHours;

    public void Save(string path) => File.WriteAllLines(path, new[]
    {
        "source = " + Source,
        "data_time_utc = " + DataTimeUtc.ToString("o", CultureInfo.InvariantCulture),
        "built_utc = " + BuiltUtc.ToString("o", CultureInfo.InvariantCulture),
        "face_size = " + FaceSize,
        "has_top = " + HasTop,
        "has_type = " + HasType,
    });

    public static Snapshot? Load(string dir)
    {
        string p = Path.Combine(dir, "snapshot.txt");
        if (!File.Exists(p))
            return null;
        var s = new Snapshot();
        foreach (string line in File.ReadAllLines(p))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
            switch (k)
            {
                case "source": s.Source = v; break;
                case "data_time_utc": s.DataTimeUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                case "built_utc": s.BuiltUtc = DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); break;
                case "face_size": s.FaceSize = int.Parse(v, CultureInfo.InvariantCulture); break;
                case "has_top": s.HasTop = bool.Parse(v); break;
                case "has_type": s.HasType = bool.Parse(v); break;
            }
        }
        s.BasePath = Path.Combine(dir, "Earth_clouds_base_live.ktx2");
        s.TopPath = Path.Combine(dir, "Earth_clouds_top_live.ktx2");
        s.TypePath = Path.Combine(dir, "Earth_clouds_type_live.ktx2");
        s.SmallPath = Path.Combine(dir, "coverage_256.bin");
        return File.Exists(s.BasePath) ? s : null;
    }
}

public static class Pipeline
{
    public const string BaseStock = "Earth_clouds_base_layer_4096.ktx2";
    public const string TopStock = "Earth_clouds_top_layer_4096.ktx2";
    public const string TypeStock = "EarthTypeMap4096.dds";

    /// <summary>GMGSI posts each hour's mosaic about 35 minutes after the hour.</summary>
    public const double PublishDelayMinutes = 35;

    private static string UserAgentVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "dev";

    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KSALiveClouds");

    /// <summary>Returns the snapshot KSA should use, or null for stock clouds. stockDir = the folder holding KSA's
    /// Earth_clouds_*_4096.ktx2. beforeBuild runs just before a new build overwrites the cached files (the in-game
    /// refresh reads the maps it is showing first).</summary>
    public static async Task<Snapshot?> Run(Config cfg, string stockDir, string cacheDir, Action<string> log, CancellationToken ct,
                                            Action? beforeBuild = null)
    {
        Directory.CreateDirectory(cacheDir);
        Snapshot? cached = Snapshot.Load(cacheDir);
        bool cacheUsable = cached != null && cached.FaceSize == cfg.FaceSize && cached.HasTop == cfg.TopLayer &&
                           cached.HasType == cfg.TypeMap;
        // the next mosaic can't exist before data time + refresh + publishing delay: don't even list until then
        if (cacheUsable && (DateTime.UtcNow - cached!.DataTimeUtc).TotalMinutes < cfg.RefreshMinutes + PublishDelayMinutes)
        {
            cached.Note = $"cached snapshot, data {(DateTime.UtcNow - cached.DataTimeUtc).TotalMinutes:0} min old";
            return cached;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"KSALiveClouds/{UserAgentVersion} (Kitten Space Agency mod)");
            string? key = await GmgsiFetch.LatestKey(http, ct) ?? throw new IOException("no GMGSI files listed for today or yesterday");
            if (cacheUsable && cached!.Source == key)
            {
                cached.Note = "no newer satellite mosaic yet";
                return cached;
            }
            var sw = Stopwatch.StartNew();
            string nc = Path.Combine(cacheDir, "gmgsi_lw.nc");
            await GmgsiFetch.Download(http, key, nc, ct);
            log($"downloaded {Path.GetFileName(key)} ({new FileInfo(nc).Length / 1e6:0.0} MB) in {sw.Elapsed.TotalSeconds:0.0} s");
            beforeBuild?.Invoke();
            return Build(cfg, nc, key, stockDir, cacheDir, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"live fetch failed: {ex.GetType().Name}: {ex.Message}");
            if (cfg.Offline == "cache" && cached != null && cached.AgeHours <= cfg.MaxCacheAgeHours && cacheUsable)
            {
                cached.Note = $"offline: last snapshot, {cached.AgeHours:0.0} h old";
                return cached;
            }
            return null;
        }
    }

    public static Snapshot Build(Config cfg, string ncPath, string source, string stockDir, string cacheDir, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        IrGrid g = IrGrid.Read(ncPath, source);
        float[] signal = Coverage.Signal(g);
        int valid = g.Counts.Count(v => !float.IsNaN(v));
        log($"read + signal {g.W}x{g.H} ({g.TimeUtc:yyyy-MM-dd HH:mm}Z) in {sw.Elapsed.TotalSeconds:0.0} s: " +
            $"{100.0 * valid / g.Counts.Length:0}% valid, lat {g.LatMin:0.0}..{g.LatMax:0.0}, lon0 {g.Lon0:0.000} step {g.Dlon:0.0000}");
        var snap = new Snapshot
        {
            Source = source, DataTimeUtc = g.TimeUtc, FaceSize = cfg.FaceSize, HasTop = cfg.TopLayer, HasType = cfg.TypeMap,
            BasePath = Path.Combine(cacheDir, "Earth_clouds_base_live.ktx2"),
            TopPath = Path.Combine(cacheDir, "Earth_clouds_top_live.ktx2"),
            TypePath = Path.Combine(cacheDir, "Earth_clouds_type_live.ktx2"),
            SmallPath = Path.Combine(cacheDir, "coverage_256.bin"),
        };
        var smalls = new List<byte[][]>();
        byte[]? dfd = null;                    // BC4 UNORM cube descriptor, from the stock maps
        foreach ((CubeBuilder.Layer layer, string stockName, string outPath) in new[]
                 {
                     (CubeBuilder.Layer.Base, BaseStock, snap.BasePath),
                     (CubeBuilder.Layer.Top, TopStock, snap.TopPath),
                 })
        {
            if (layer == CubeBuilder.Layer.Top && !cfg.TopLayer)
                continue;
            Ktx2.Cube stock = Ktx2.Cube.Open(Path.Combine(stockDir, stockName));
            dfd ??= stock.Dfd;
            (byte[][][] levels, byte[][] small) = CubeBuilder.BuildLayer(g, signal, stock, cfg, layer);
            Ktx2.WriteCube(outPath, cfg.FaceSize, levels, stock.Dfd);
            smalls.Add(small);
            if (layer == CubeBuilder.Layer.Base)
                snap.BaseLevels = levels;
            else
                snap.TopLevels = levels;
            log($"{layer} layer written ({new FileInfo(outPath).Length / 1e6:0.0} MB) at {sw.Elapsed.TotalSeconds:0.0} s");
        }
        if (cfg.TypeMap)
        {
            float[] lumpy = Coverage.Lumpiness(g);
            var stockSel = Dds.ReadBc4Level0(Path.Combine(stockDir, TypeStock));
            snap.TypeLevels = CubeBuilder.BuildTypeLayer(g, lumpy, stockSel, cfg);
            Ktx2.WriteCube(snap.TypePath, CubeBuilder.TypeFace, snap.TypeLevels, dfd!);
            log($"cloud-type layer written ({new FileInfo(snap.TypePath).Length / 1e6:0.0} MB) at {sw.Elapsed.TotalSeconds:0.0} s");
        }
        snap.Small = smalls.SelectMany(faces => faces).SelectMany(f => f).ToArray();
        File.WriteAllBytes(snap.SmallPath + ".part", snap.Small);
        File.Move(snap.SmallPath + ".part", snap.SmallPath, true);
        snap.BuiltUtc = DateTime.UtcNow;
        snap.Save(Path.Combine(cacheDir, "snapshot.txt"));
        snap.Note = $"fresh: {Path.GetFileName(source)}";
        return snap;
    }
}
