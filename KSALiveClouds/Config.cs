// KSALiveClouds: settings from live_clouds.cfg next to the dll (key = value, '#' comments). Every key is optional.

using System.Globalization;

namespace KSALiveClouds;

public sealed class Config
{
    /// <summary>Off: KSA gets its stock Earth clouds and nothing is fetched.</summary>
    public bool Enabled = true;
    /// <summary>Don't look for a newer mosaic until the cached data is older than this + 35 min (GMGSI publishes
    /// one an hour, about 35 minutes after the hour).</summary>
    public double RefreshMinutes = 60;
    /// <summary>How long KSA's texture load may wait for the fetch + build before it falls back.</summary>
    public double TimeoutSeconds = 45;
    /// <summary>What to show when the fetch fails: "cache" = the last snapshot (if younger than MaxCacheAgeHours),
    /// "stock" = KSA's own clouds.</summary>
    public string Offline = "cache";
    public double MaxCacheAgeHours = 72;
    /// <summary>Hold the cloud layers still (stock Earth clouds drift ~1 revolution per 23 game days), so the real
    /// weather stays over the places it belongs.</summary>
    public bool FreezeRotation = true;
    /// <summary>Check for a newer mosaic during play and crossfade to it.</summary>
    public bool LiveRefresh = true;
    /// <summary>How often the in-game refresh looks for a newer mosaic (it only downloads once a new one is due).</summary>
    public double CheckMinutes = 10;
    /// <summary>Crossfade from the old map to the new one over this much wall-clock time, in uploads this far apart.</summary>
    public double FadeMinutes = 10, FadeStepSeconds = 30;
    /// <summary>While time-warping, let the frozen layers drift at the stock wind speed with game time, and ease
    /// back over the real positions this many seconds (wall clock) after the warp ends.</summary>
    public bool WarpDrift = true;
    public double WarpEaseSeconds = 8;
    /// <summary>Strength of the small-scale texture borrowed from the stock cloud map (0 = smooth satellite data).</summary>
    public double Detail = 0.45;
    /// <summary>Also replace the high (cirrus, 11 km) layer from the coldest cloud tops.</summary>
    public bool TopLayer = true;
    /// <summary>Output cube face size: 4096 matches stock; 2048 is a quarter of the VRAM and still finer than the data (~8 km).</summary>
    public int FaceSize = 4096;
    /// <summary>Also replace KSA's cloud-type selector (EarthTypeMap4096.dds: 0 = the stratus detail tile, 1 = the
    /// cumulus one; stock is random noise) with one from the satellite: lumpy cloud fields and very cold tops
    /// lean cumulus, smooth decks stratus. type_from_data = how much of it is data vs the stock noise.</summary>
    public bool TypeMap = true;
    public double TypeFromData = 0.75;
    /// <summary>Regional lumpiness of the IR (std of counts over ~35 km, averaged over ~120 km) where the selector
    /// starts / is fully cumulus.</summary>
    public double LumpyLow = 4, LumpyHigh = 12;
    /// <summary>Deep convection: cloud tops this cold (K) are towers with cloud all the way down.</summary>
    public double DeepStartK = 212, DeepFullK = 202;
    /// <summary>Under high cloud the base layer keeps only this fraction of its coverage (IR can't see beneath,
    /// and without this cirrus over clear ground also drew a 2 km deck), except under deep convection.</summary>
    public double BaseUnderCirrus = 0.35;
    /// <summary>Thresholds in GMGSI counts (~0.5 K per count over warm scenes) above the local clear-sky reference.</summary>
    public double CloudStart = 10, CloudFull = 42;
    /// <summary>Cirrus layer: cloud-top brightness temperatures (K) where the top layer starts / is full.</summary>
    public double CirrusStartK = 238, CirrusFullK = 218;

    public static Config Load(string path)
    {
        var c = new Config();
        if (!File.Exists(path))
            return c;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            string k = line[..eq].Trim().ToLowerInvariant(), v = line[(eq + 1)..].Trim().Trim('"');
            double d = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN;
            bool b = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
            switch (k)
            {
                case "enabled": c.Enabled = b; break;
                case "refresh_minutes": if (!double.IsNaN(d)) c.RefreshMinutes = d; break;
                case "timeout_seconds": if (!double.IsNaN(d)) c.TimeoutSeconds = d; break;
                case "offline": c.Offline = v.ToLowerInvariant(); break;
                case "max_cache_age_hours": if (!double.IsNaN(d)) c.MaxCacheAgeHours = d; break;
                case "freeze_rotation": c.FreezeRotation = b; break;
                case "live_refresh": c.LiveRefresh = b; break;
                case "check_minutes": if (!double.IsNaN(d)) c.CheckMinutes = Math.Max(1, d); break;
                case "fade_minutes": if (!double.IsNaN(d)) c.FadeMinutes = Math.Max(0, d); break;
                case "fade_step_seconds": if (!double.IsNaN(d)) c.FadeStepSeconds = Math.Max(2, d); break;
                case "warp_drift": c.WarpDrift = b; break;
                case "warp_ease_seconds": if (!double.IsNaN(d)) c.WarpEaseSeconds = Math.Max(0.5, d); break;
                case "detail": if (!double.IsNaN(d)) c.Detail = d; break;
                case "top_layer": c.TopLayer = b; break;
                case "face_size": if (!double.IsNaN(d)) c.FaceSize = (int)d is 1024 or 2048 or 4096 ? (int)d : 4096; break;
                case "cloud_start": if (!double.IsNaN(d)) c.CloudStart = d; break;
                case "cloud_full": if (!double.IsNaN(d)) c.CloudFull = d; break;
                case "type_map": c.TypeMap = b; break;
                case "type_from_data": if (!double.IsNaN(d)) c.TypeFromData = Math.Clamp(d, 0, 1); break;
                case "lumpy_low": if (!double.IsNaN(d)) c.LumpyLow = d; break;
                case "lumpy_high": if (!double.IsNaN(d)) c.LumpyHigh = d; break;
                case "deep_start_k": if (!double.IsNaN(d)) c.DeepStartK = d; break;
                case "deep_full_k": if (!double.IsNaN(d)) c.DeepFullK = d; break;
                case "base_under_cirrus": if (!double.IsNaN(d)) c.BaseUnderCirrus = Math.Clamp(d, 0, 1); break;
                case "cirrus_start_k": if (!double.IsNaN(d)) c.CirrusStartK = d; break;
                case "cirrus_full_k": if (!double.IsNaN(d)) c.CirrusFullK = d; break;
            }
        }
        return c;
    }
}
