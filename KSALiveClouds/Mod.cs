// KSALiveClouds: replaces Earth's cloud coverage (KSA's two layers, EarthClouds and EarthClouds2) with the current
// real-world cloud cover from NOAA's hourly geostationary IR mosaic.
//
// At BeforeMain a background task fetches the newest mosaic (or reuses the cached build) and writes two KTX2
// cubemaps in the stock format into ~/.local/share/KSALiveClouds. When KSA loads the stock
// Earth_clouds_{base,top}_layer_4096.ktx2 (TextureReference.DoLoad), the prefix waits for that task and points the
// texture at the generated file for the duration of the load; the base layer's cloud-type selector
// (EarthTypeMap4096.dds) gets a satellite-made cubemap the same way. During play Live.cs refreshes hourly with a
// crossfade, holds the layers over the real weather, and lets them drift while time-warping. Anything failing
// leaves KSA's stock clouds.

using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using StarMap.API;

namespace KSALiveClouds;

[StarMapMod]
public sealed class Mod
{
    private Harmony? _harmony;
    private bool _tickFailed;

    [StarMapBeforeMain]
    public void BeforeMain()
    {
        try
        {
            Log($"{Version} (KSA {typeof(Program).Assembly.GetName().Version})");
            string modDir = Path.GetDirectoryName(typeof(Mod).Assembly.Location) ?? ".";
            Config cfg = Config.Load(Path.Combine(modDir, "live_clouds.cfg"));
            if (!cfg.Enabled)
            {
                LiveClouds.Status = "disabled in live_clouds.cfg";
                Log("disabled in live_clouds.cfg; stock clouds");
                return;
            }
            string gameDir = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".";
            string stockDir = Path.Combine(gameDir, "Content", "Core", "Textures", "Clouds", "Compressed");
            LiveClouds.Prepare(cfg, stockDir, Pipeline.DefaultCacheDir);
            // Sol only (WorldGuard). At BeforeMain KSAWorld gives the last system (read from settings.toml since
            // 2026-10-29; before that it said Sol in a Kerbol game). Sol (or no KSAWorld): fetch now, overlapping
            // the load. Kerbol: wait for the settled system in case the start-up popup picks Sol. The texture
            // load also starts it if needed.
            if (WorldGuard.IsKerbol)
                Task.Run(LiveClouds.LaunchWhenSol);
            else
                LiveClouds.Launch();
            _harmony = new Harmony("ksaliveclouds");
            Patches.Apply(_harmony);
            Live.StartRefresh(cfg, stockDir, Pipeline.DefaultCacheDir);
        }
        catch (Exception ex)
        {
            Log($"disabled: {ex}");
            _harmony?.UnpatchAll("ksaliveclouds");
        }
    }

    [StarMapAfterGui]
    public void AfterGui(double dt)
    {
        try { Live.Tick(); }
        catch (Exception ex)
        {
            if (!_tickFailed)
                Log($"tick failed (logged once): {ex}");
            _tickFailed = true;
        }
    }

    [StarMapUnload]
    public void Unload() => _harmony?.UnpatchAll("ksaliveclouds");

    internal static void Log(string s) => DefaultCategory.Log.Info("[KSALiveClouds] " + s);

    internal static string Version =>
        typeof(Mod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
}

/// <summary>Public face of the mod, for other mods by reflection (e.g. cloud-aware photo scoring):
/// Type.GetType("KSALiveClouds.LiveClouds, KSALiveClouds"). Only plain types in the signatures.</summary>
public static class LiveClouds
{
    private static Task<Snapshot?>? _task;
    private static DateTime _started;
    private static string _stockDir = "", _cacheDir = "";
    private static bool _launched;
    /// <summary>The launch-time fetch + build, once started (never cleared, unlike _task); the refresh loop waits on it.</summary>
    internal static Task<Snapshot?>? Initial { get; private set; }
    private static Config _cfg = new();
    private static Snapshot? _snap;
    private static byte[]? _small;                        // [layer][face][256 * 256], what the game is showing
    private static byte[]? _fadeFrom, _fadeTo;            // during a crossfade: old and new, blended by FadeT
    private const int SmallN = 256;

    /// <summary>True once live maps are in the game (both layers or just the base one, see HasTopLayer).</summary>
    public static bool Active { get; private set; }
    public static bool HasTopLayer { get; private set; }
    /// <summary>The layers are held over the real weather (default); CoverageAt then needs no time argument.</summary>
    public static bool Frozen => _cfg.FreezeRotation;
    /// <summary>When the satellites saw the clouds being shown (UTC; the newer one during a crossfade); null when
    /// stock clouds are showing.</summary>
    public static DateTime? DataTimeUtc => Active ? _snap?.DataTimeUtc : null;
    /// <summary>Crossfade progress 0-1 while one runs, else 1.</summary>
    public static float FadeT { get; internal set; } = 1;
    public static string Status { get; internal set; } = "starting";

    internal static void Prepare(Config cfg, string stockDir, string cacheDir)
    {
        _cfg = cfg;
        _stockDir = stockDir;
        _cacheDir = cacheDir;
    }

    /// <summary>Starts the launch-time fetch + build (once).</summary>
    internal static void Launch()
    {
        lock (LaunchGate)
        {
            if (_launched)
                return;
            _launched = true;
        }
        _started = DateTime.UtcNow;
        Config cfg = _cfg;
        string stockDir = _stockDir, cacheDir = _cacheDir;
        _task = Initial = Task.Run(() => Pipeline.Run(cfg, stockDir, cacheDir, Mod.Log, CancellationToken.None));
    }

    private static readonly object Gate = new(), LaunchGate = new();

    /// <summary>Waits (up to 20 s) for KSA to settle the system, then starts the fetch unless it's Kerbol.</summary>
    internal static async Task LaunchWhenSol()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!WorldGuard.IsFinal && sw.Elapsed.TotalSeconds < 20)
                await Task.Delay(250);
            if (WorldGuard.KerbolFinal)
                return;
            Mod.Log(WorldGuard.IsFinal ? $"Sol game: fetching ({sw.Elapsed.TotalSeconds:0.0} s after start-up)"
                                       : "system not settled after 20 s; fetching anyway");
            Launch();
        }
        catch (Exception ex) { Mod.Log($"start-up fetch: {ex}"); }
    }

    /// <summary>Blocks (up to the configured timeout from start) until the build is done; null = stock clouds.
    /// The two layers may load on different threads, hence the lock.</summary>
    internal static Snapshot? Wait()
    {
        lock (Gate)
            return WaitLocked();
    }

    private static Snapshot? WaitLocked()
    {
        if (WorldGuard.IsKerbol)
            return null;
        if (!_launched)
        {
            Mod.Log("Sol picked at start-up: fetching now");
            Launch();
        }
        if (_task == null)
            return null;
        if (!_task.IsCompleted)
        {
            TimeSpan left = TimeSpan.FromSeconds(_cfg.TimeoutSeconds) - (DateTime.UtcNow - _started);
            Mod.Log($"waiting up to {Math.Max(0, left.TotalSeconds):0} s for the live cloud maps");
            if (left <= TimeSpan.Zero || !_task.Wait(left))
            {
                Status = $"timed out after {_cfg.TimeoutSeconds:0} s; stock clouds until the in-game refresh fades the live ones in";
                Mod.Log(Status);
                _task = null;
                return null;
            }
        }
        Snapshot? s;
        try { s = _task.Result; }
        catch (Exception ex)
        {
            Status = "build failed; stock clouds";
            Mod.Log($"{Status}: {ex}");
            _task = null;
            return null;
        }
        if (s == null)
        {
            Status = "no live data (offline?) and no usable cache; stock clouds";
            Mod.Log(Status);
        }
        else if (_snap != s)
        {
            _snap = s;
            Status = $"satellite clouds of {s.DataTimeUtc:yyyy-MM-dd HH:mm} UTC ({s.Note})";
            Mod.Log(Status);
        }
        return s;
    }

    /// <summary>A layer's texture now shows (or is fading to) live clouds.</summary>
    internal static void MarkLoaded(bool top)
    {
        Active = true;
        if (top)
            HasTopLayer = true;
        if (_small == null && _snap != null)
        {
            try { _small = _snap.Small ?? (File.Exists(_snap.SmallPath) ? File.ReadAllBytes(_snap.SmallPath) : null); }
            catch (Exception ex) { Mod.Log($"coverage copy unreadable: {ex.Message}"); }
        }
    }

    /// <summary>Before a refresh overwrites the cache: the copy of what is showing (null when stock is showing).</summary>
    internal static void LoadFadeFromSmall() => _fadeFrom = Active ? _small : null;

    internal static void BeginFade(Snapshot next, bool top)
    {
        _fadeTo = next.Small;
        FadeT = 0;
        _snap ??= next;
        if (top)
            HasTopLayer = true;
    }

    internal static void EndFade(Snapshot next, bool done)
    {
        if (done)
        {
            _snap = next;
            _small = next.Small;
            Status = $"satellite clouds of {next.DataTimeUtc:yyyy-MM-dd HH:mm} UTC (refreshed in game)";
        }
        _fadeFrom = _fadeTo = null;
        FadeT = 1;
    }

    /// <summary>Coverage 0-1 the game is drawing at a planet-fixed (CCF) direction of Earth: x, y, z as in
    /// Celestial.GetDirCcfFromLatLon (z = north). layer 0 = low/mid clouds, 1 = cirrus. Follows crossfades and the
    /// time-warp drift. NaN when the live maps are not in use (stock clouds), the layer was not replaced, or the
    /// layers rotate the stock way (freeze_rotation off).</summary>
    public static float CoverageAt(double x, double y, double z, int layer = 0)
    {
        if (!Active || !Frozen || layer < 0 || layer > 1 || (layer == 1 && !HasTopLayer))
            return float.NaN;
        // the texture is sampled at the planet direction turned by the layer's drift angle about the pole
        double a = (layer == 0 ? Live.Base : Live.Top).AngleZ;
        if (a != 0)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            (x, y) = (x * c - y * s, x * s + y * c);
        }
        byte[]? to = _fadeTo, from = _fadeFrom, cur = _small;
        float t = FadeT;
        if (to != null)
        {
            float b = Sample(to, x, y, z, layer);
            return from == null || float.IsNaN(Sample(from, x, y, z, layer)) ? b : Sample(from, x, y, z, layer) + (b - Sample(from, x, y, z, layer)) * t;
        }
        return cur == null ? float.NaN : Sample(cur, x, y, z, layer);
    }

    private static float Sample(byte[] small, double x, double y, double z, int layer)
    {
        int per = 6 * SmallN * SmallN;
        if (small.Length < (layer + 1) * per)
            return float.NaN;
        int face = CubeBuilder.DirToFace(x, y, z, out double u, out double v);
        double fx = Math.Clamp(u * SmallN - 0.5, 0, SmallN - 1), fy = Math.Clamp(v * SmallN - 0.5, 0, SmallN - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(SmallN - 1, x0 + 1), y1 = Math.Min(SmallN - 1, y0 + 1);
        double tx = fx - x0, ty = fy - y0;
        int b = layer * per + face * SmallN * SmallN;
        double top = small[b + y0 * SmallN + x0] + (small[b + y0 * SmallN + x1] - small[b + y0 * SmallN + x0]) * tx;
        double bot = small[b + y1 * SmallN + x0] + (small[b + y1 * SmallN + x1] - small[b + y1 * SmallN + x0]) * tx;
        return (float)((top + (bot - top) * ty) / 255.0);
    }

    /// <summary>Same, by latitude / longitude in degrees.</summary>
    public static float CoverageAtLatLon(double latDeg, double lonDeg, int layer = 0)
    {
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        return CoverageAt(Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la), layer);
    }
}

internal static class Patches
{
    private static readonly MethodInfo DoLoad = AccessTools.Method(typeof(TextureReference), "DoLoad", Type.EmptyTypes)
                                                ?? throw new MissingMethodException("TextureReference.DoLoad");

    public static void Apply(Harmony h)
    {
        h.Patch(DoLoad, prefix: new HarmonyMethod(typeof(Patches), nameof(DoLoadPrefix)),
                finalizer: new HarmonyMethod(typeof(Patches), nameof(DoLoadFinalizer)));
        // Both take the layer as 'cloudLayerReference'; patch every overload that has it so a new one can't slip by.
        int n = 0;
        foreach (MethodInfo m in typeof(CloudLayerRenderData).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (m.Name is "Create" or "UpdateStaticData" &&
                m.GetParameters().Any(p => p.Name == "cloudLayerReference" && p.ParameterType == typeof(CloudLayerReference)))
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(Patches), nameof(LayerPrefix)));
                n++;
            }
        if (n == 0)
            Mod.Log("CloudLayerRenderData.Create/UpdateStaticData not found; clouds will drift");
        // The frame update's parameters change between KSA builds (5707 added the viewport's shader slot); the prefix
        // only needs __instance, so patch every overload of it rather than one exact signature.
        int frames = 0;
        if (Live.CanSteer)
            foreach (MethodInfo m in typeof(CloudLayerRenderData).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == nameof(CloudLayerRenderData.UpdateCommonFrameData))
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Patches), nameof(FramePrefix)));
                    frames++;
                }
        if (frames == 0)
            Mod.Log("CloudLayerRenderData.UpdateCommonFrameData / rotation setters not found; no time-warp drift");
    }

    private static string? Which(string localPath)
    {
        string f = Path.GetFileName(localPath.Replace('\\', '/'));
        return f.Equals(Pipeline.BaseStock, StringComparison.OrdinalIgnoreCase) ? "base"
             : f.Equals(Pipeline.TopStock, StringComparison.OrdinalIgnoreCase) ? "top"
             : f.Equals(Pipeline.TypeStock, StringComparison.OrdinalIgnoreCase) ? "type" : null;
    }

    private static readonly HashSet<string> Replaced = new();

    internal static void MarkReplaced(string which)
    {
        lock (Replaced)
            Replaced.Add(which);
    }

    // __state = the original LocalPath while we point it at our file
    private static void DoLoadPrefix(TextureReference __instance, out string? __state)
    {
        __state = null;
        try
        {
            string? which = Which(__instance.LocalPath ?? "");
            LayerSlot? slot = which == null ? null : Live.ByWhich(which);
            if (slot == null || WorldGuard.IsKerbol)
                return;
            slot.Tex = __instance;
            slot.ShownPath = __instance.ModPath;  // stock, unless replaced below
            Snapshot? s = LiveClouds.Wait();
            if (s == null)
                return;
            string path = which == "base" ? s.BasePath : which == "top" ? s.TopPath : s.TypePath;
            if (which == "top" && !s.HasTop || which == "type" && !s.HasType || !File.Exists(path))
                return;
            __state = __instance.LocalPath;
            __instance.LocalPath = path;          // Mod.GetPath = Path.Combine(dir, local): an absolute path wins
            slot.ShownPath = path;
            slot.Live = true;
            MarkReplaced(which!);
            if (which != "type")
                LiveClouds.MarkLoaded(which == "top");
            Mod.Log($"{which} layer <- {path}");
        }
        catch (Exception ex) { Mod.Log($"DoLoad prefix: {ex}"); }
    }

    private static Exception? DoLoadFinalizer(TextureReference __instance, string? __state, Exception? __exception)
    {
        try
        {
            if (__state != null)
                __instance.LocalPath = __state;
        }
        catch (Exception ex) { Mod.Log($"DoLoad finalizer: {ex}"); }
        return __exception;
    }

    private static void LayerPrefix(CloudLayerReference cloudLayerReference)
    {
        try
        {
            if (cloudLayerReference?.RotationSpeed == null)
                return;
            LayerSlot? slot = Live.ByLayerId(cloudLayerReference.Id);
            if (slot == null || WorldGuard.IsKerbol)
                return;
            Vector3Reference r = cloudLayerReference.RotationSpeed;
            if (!slot.HasOrigSpeed && (r.X != 0 || r.Y != 0 || r.Z != 0))
            {
                slot.OrigSpeed = new Brutal.Numerics.double3(r.X, r.Y, r.Z);   // the time-warp drift speed
                slot.HasOrigSpeed = true;
            }
            bool replaced;
            lock (Replaced)
                replaced = Replaced.Contains(slot.Which);
            if (!LiveClouds.Frozen || !replaced)
                return;
            if (r.X != 0 || r.Y != 0 || r.Z != 0)
            {
                Mod.Log($"{cloudLayerReference.Id}: rotation ({r.X}, {r.Y}, {r.Z}) -> 0 so the weather stays put");
                r.X = r.Y = r.Z = 0;
            }
        }
        catch (Exception ex) { Mod.Log($"layer prefix: {ex}"); }
    }

    private static bool _frameFailed;

    private static void FramePrefix(CloudLayerRenderData __instance)
    {
        try { Live.Steer(__instance); }
        catch (Exception ex)
        {
            if (!_frameFailed)
                Mod.Log($"rotation steer failed (logged once): {ex}");
            _frameFailed = true;
        }
    }
}
