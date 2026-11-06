// KSALiveClouds: in-game refresh, crossfade and time-warp drift.
//
// Refresh: a background loop asks the pipeline for a newer mosaic every check_minutes (it only downloads once one
// is due, ~hourly). A new build is crossfaded in on the wall clock: every fade_step_seconds the worker blends the
// old and new BC4 blocks (decode, lerp, re-encode, level by level) and the main thread uploads the whole mip chain
// into the texture KSA already has (SimpleVkTexture.UploadData, the KSAIva/FastThumbnails recipe). If the game
// started on stock clouds (offline, timed out), the first refresh fades the live map in over them.
//
// Drift: the live layers are held still, so KSA's own rotation (AngularRotationSpeed x elapsed game time) is
// replaced every frame by angle / elapsed. While time-warping, the angle grows at the stock wind speed with the
// game time that runs ahead of the wall clock; once the warp ends it eases back to 0 (the real positions).
// The cloud shadow volume uses the same speed x elapsed product, so shadows follow.

using System.Diagnostics;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using RenderCore;

namespace KSALiveClouds;

internal sealed class LayerSlot
{
    public readonly string Which, LayerId;
    public TextureReference? Tex;
    /// <summary>The file whose contents are on the GPU now (our map or KSA's stock one).</summary>
    public string? ShownPath;
    public bool Live;
    // crossfade (worker thread)
    public byte[][][]? From, To;
    // drift (render thread)
    public double3 OrigSpeed;                 // the layer's stock RotationSpeed (m/s at the cloud base)
    public bool HasOrigSpeed;
    public double Drift;                      // game seconds of stock-speed drift currently applied
    public double LastReal = -1, LastGame;
    public ulong LastFrame = ulong.MaxValue;  // since 5707 the frame update runs once per viewport: advance once a frame
    public double AngleZ;                     // current drift angle about the pole (radians), for the API

    public LayerSlot(string which, string layerId) { Which = which; LayerId = layerId; }
}

internal static class Live
{
    public static readonly LayerSlot Base = new("base", "EarthClouds"), Top = new("top", "EarthClouds2");
    /// <summary>The base layer's cloud-type selector (not a layer of its own: no rotation of its own either, KSA
    /// samples it in the base layer's frame).</summary>
    public static readonly LayerSlot Type = new("type", "-");
    private static readonly LayerSlot[] Slots = { Base, Top, Type };
    public static LayerSlot? ByWhich(string w) => w == "base" ? Base : w == "top" ? Top : w == "type" ? Type : null;
    public static LayerSlot? ByLayerId(string? id) => id == Base.LayerId ? Base : id == Top.LayerId ? Top : null;

    private static Config _cfg = new();
    private static string _stockDir = "", _cacheDir = "";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // ---------------------------------------------------------------- refresh loop (background)

    public static void StartRefresh(Config cfg, string stockDir, string cacheDir)
    {
        _cfg = cfg;
        _stockDir = stockDir;
        _cacheDir = cacheDir;
        if (cfg.LiveRefresh)
            Task.Run(RefreshLoop);
    }

    private static async Task RefreshLoop()
    {
        // the launch-time build may start late (WorldGuard: last game was Kerbol, Sol picked at start-up)
        while (LiveClouds.Initial == null)
        {
            if (WorldGuard.KerbolFinal)
                return;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        try { await LiveClouds.Initial; } catch { /* reported by LiveClouds.Wait */ }
        string? lastError = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(_cfg.CheckMinutes));
            if (WorldGuard.KerbolFinal)
                return;                       // Sol only
            if (Base.Tex == null)
                continue;                     // the textures haven't loaded yet
            try
            {
                Snapshot? s = await Pipeline.Run(_cfg, _stockDir, _cacheDir, Mod.Log, CancellationToken.None, LoadShown);
                if (s?.BaseLevels == null)
                {
                    DropFadeData();           // nothing new (or the fetch failed and the cache answered)
                    continue;
                }
                await Fade(s);
                lastError = null;
            }
            catch (Exception ex)
            {
                DropFadeData();
                if (ex.Message != lastError)
                    Mod.Log($"refresh failed (keeping the current clouds): {ex}");
                lastError = ex.Message;
            }
        }
    }

    /// <summary>Runs just before a new build overwrites the cache: read what each texture is showing.</summary>
    private static void LoadShown()
    {
        foreach (LayerSlot s in Slots)
        {
            s.From = null;
            // a stock 2D .dds (the type map before it was ever replaced) can't fade into a cubemap: skip it
            if (s.Tex == null || s.ShownPath == null || !s.ShownPath.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase) || !File.Exists(s.ShownPath))
                continue;
            try { s.From = Ktx2.Cube.Open(s.ShownPath).AllLevels(); }
            catch (Exception ex) { Mod.Log($"{s.Which}: can't read {s.ShownPath} ({ex.Message}); it won't refresh this time"); }
        }
        LiveClouds.LoadFadeFromSmall();
    }

    private static void DropFadeData()
    {
        foreach (LayerSlot s in Slots)
            s.From = s.To = null;
    }

    private sealed record Plan(LayerSlot Slot, SimpleVkTexture Tex, int FromLevel, int ToLevel, int[] MipSizes, byte[] Work);

    private static Plan? PlanFor(LayerSlot slot, byte[][][]? to)
    {
        SimpleVkTexture? tex = slot.Tex?.Texture;
        if (to == null || slot.From == null || tex == null)
            return null;
        if (tex.Format != VkFormat.Bc4UNormBlock || tex.ArrayLayerCount != 6)
        {
            Mod.Log($"{slot.Which}: texture is {tex.Format} x{tex.ArrayLayerCount}, not a BC4 cubemap; can't refresh it");
            return null;
        }
        int LevelOf(byte[][][] lv) => Array.FindIndex(lv, l => l[0].Length == BlockBytes(tex.Width));
        int fromL = LevelOf(slot.From), toL = LevelOf(to), mips = tex.MipMapCount;
        if (fromL < 0 || toL < 0 || fromL + mips > slot.From.Length || toL + mips > to.Length)
        {
            Mod.Log($"{slot.Which}: texture is {tex.Width}px x{mips} mips; the maps don't cover it, can't refresh it");
            return null;
        }
        int[] sizes = Enumerable.Range(0, mips).Select(j => to[toL + j][0].Length).ToArray();
        return new Plan(slot, tex, fromL, toL, sizes, new byte[6 * sizes.Sum()]);
    }

    private static int BlockBytes(int size) => Math.Max(1, (size + 3) / 4) * Math.Max(1, (size + 3) / 4) * 8;

    private static async Task Fade(Snapshot snap)
    {
        Base.To = snap.BaseLevels;
        Top.To = snap.TopLevels;
        Type.To = snap.TypeLevels;
        var plans = new List<Plan>();
        foreach (LayerSlot s in Slots)
            if (PlanFor(s, s.To) is Plan p)
                plans.Add(p);
        if (plans.Count == 0)
        {
            DropFadeData();
            return;
        }
        int steps = Math.Max(1, (int)Math.Round(_cfg.FadeMinutes * 60 / _cfg.FadeStepSeconds));
        Mod.Log($"crossfading to satellite clouds of {snap.DataTimeUtc:yyyy-MM-dd HH:mm} UTC over {_cfg.FadeMinutes:0.#} min ({steps} steps)");
        LiveClouds.BeginFade(snap, plans.Any(p => p.Slot == Top));
        foreach (Plan p in plans)
            if (!p.Slot.Live)
            {
                if (p.Slot == Type)
                    p.Slot.Live = true;
                else
                    Activate(p.Slot);
            }             // fading in over stock clouds: hold the layer still from now on
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) };
        var sw = new Stopwatch();
        for (int k = 1; k <= steps; k++)
        {
            double x = (double)k / steps;
            float t = (float)(x * x * (3 - 2 * x));
            sw.Restart();
            foreach (Plan p in plans)
            {
                Compose(p, t, k == steps, po);
                if (!await Upload(p))
                {
                    LiveClouds.EndFade(snap, false);
                    DropFadeData();
                    return;
                }
            }
            LiveClouds.FadeT = t;
            if (k == 1)
                Mod.Log($"fade step took {sw.Elapsed.TotalSeconds:0.00} s (blend + upload)");
            if (k < steps)
                await Task.Delay(TimeSpan.FromSeconds(_cfg.FadeStepSeconds));
        }
        foreach (Plan p in plans)
            p.Slot.ShownPath = p.Slot == Base ? snap.BasePath : p.Slot == Top ? snap.TopPath : snap.TypePath;
        LiveClouds.EndFade(snap, true);
        DropFadeData();
        Mod.Log("crossfade done");
    }

    /// <summary>Upload layout (VkUtils.UploadBufferToImage): face-major, each face's mips largest first.</summary>
    private static void Compose(Plan p, float t, bool last, ParallelOptions po)
    {
        int mips = p.MipSizes.Length;
        var items = new List<(byte[] A, byte[] B, int Dst, int Start, int Count)>();
        int off = 0;
        for (int f = 0; f < 6; f++)
            for (int j = 0; j < mips; j++)
            {
                byte[] a = p.Slot.From![p.FromLevel + j][f], b = p.Slot.To![p.ToLevel + j][f];
                int blocks = b.Length / 8;
                for (int s = 0; s < blocks; s += 8192)
                    items.Add((a, b, off, s, Math.Min(8192, blocks - s)));
                off += b.Length;
            }
        Parallel.ForEach(items, po, it =>
        {
            Span<byte> dst = p.Work.AsSpan(it.Dst + it.Start * 8, it.Count * 8);
            if (last)
                it.B.AsSpan(it.Start * 8, it.Count * 8).CopyTo(dst);
            else
                Bc4.Blend(it.A.AsSpan(it.Start * 8, it.Count * 8), it.B.AsSpan(it.Start * 8, it.Count * 8), t, dst);
        });
    }

    // ---------------------------------------------------------------- uploads (main thread)

    private sealed class Pending(Plan plan)
    {
        public readonly Plan Plan = plan;
        public readonly TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Pending? _pending;
    private static bool _uploadWarned;

    private static async Task<bool> Upload(Plan p)
    {
        var job = new Pending(p);
        Volatile.Write(ref _pending, job);
        return await job.Done.Task;           // the main thread may be on a loading screen: just wait
    }

    /// <summary>Called every frame from StarMapAfterGui.</summary>
    public static void Tick()
    {
        Pending? job = Interlocked.Exchange(ref _pending, null);
        if (job == null)
            return;
        try
        {
            Renderer r = Program.GetRenderer();
            using StagingPool pool = r.Allocator.CreateStagingPool(r.Graphics, 1);
            CommandBuffer cb = pool.NextCommandBuffer();
            cb.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            job.Plan.Tex.UploadData(pool, cb, job.Plan.Work.AsSpan(), job.Plan.MipSizes);
            cb.End();
            pool.Submit();
            pool.Wait();
            job.Done.TrySetResult(true);
        }
        catch (Exception ex)
        {
            if (!_uploadWarned)
                Mod.Log($"texture upload failed; live refresh stops: {ex}");
            _uploadWarned = true;
            job.Done.TrySetResult(false);
        }
    }

    // ---------------------------------------------------------------- rotation / drift (render thread)

    /// <summary>A layer KSA loaded with stock clouds turns live: it has been rotating at the stock speed for the
    /// whole game, so start the drift at that angle and let it ease back to the real positions.</summary>
    private static void Activate(LayerSlot s)
    {
        s.Drift = Universe.GetElapsedSeconds();
        s.Live = true;
        Patches.MarkReplaced(s.Which);
        LiveClouds.MarkLoaded(s == Top);
    }

    private static readonly Action<CloudLayerRenderData, double3>? SetSpeed =
        AccessTools.PropertySetter(typeof(CloudLayerRenderData), nameof(CloudLayerRenderData.AngularRotationSpeed)) is { } m1
            ? AccessTools.MethodDelegate<Action<CloudLayerRenderData, double3>>(m1) : null;
    private static readonly Action<CloudLayerRenderData, double>? SetSpeedLength =
        AccessTools.PropertySetter(typeof(CloudLayerRenderData), nameof(CloudLayerRenderData.AngularRotationSpeedLength)) is { } m2
            ? AccessTools.MethodDelegate<Action<CloudLayerRenderData, double>>(m2) : null;

    public static bool CanSteer => SetSpeed != null && SetSpeedLength != null;

    /// <summary>Prefix of CloudLayerRenderData.UpdateCommonFrameData (once per layer per frame).</summary>
    public static void Steer(CloudLayerRenderData layer)
    {
        LayerSlot? s = ByLayerId(layer.GetCloudLayerReference()?.Id);
        if (s == null || !s.Live || !_cfg.FreezeRotation || !CanSteer)
            return;
        double now = Clock.Elapsed.TotalSeconds, game = Universe.GetElapsedSeconds();
        double3 omega = s.HasOrigSpeed && layer.BottomRadius > 0 ? s.OrigSpeed / layer.BottomRadius : double3.Zero;
        if (s.LastFrame == Program.FrameNumber)
        {
            Apply(layer, omega * s.Drift, game);   // another viewport this frame: same angle, no new step
            return;
        }
        s.LastFrame = Program.FrameNumber;
        if (s.LastReal < 0)
        {
            s.LastReal = now;
            s.LastGame = game;
        }
        double realDt = now - s.LastReal, gameDt = game - s.LastGame;
        s.LastReal = now;
        s.LastGame = game;
        double w = omega.Length();
        double ahead = gameDt - realDt;       // game time running ahead of the wall clock = warp
        if (_cfg.WarpDrift && gameDt > 0 && gameDt < 1e9 && ahead > 0 && Universe.GetSimulationSpeed() > 1.01)
            s.Drift += ahead;
        else if (s.Drift != 0)
        {
            if (w > 0)
            {
                double period = 2 * Math.PI / w;  // ease back the short way round
                s.Drift -= period * Math.Round(s.Drift / period);
            }
            s.Drift *= Math.Exp(-Math.Max(0, realDt) * 3 / _cfg.WarpEaseSeconds);
            if (Math.Abs(s.Drift * w) < 1e-7)
                s.Drift = 0;
        }
        double3 angle = omega * s.Drift;
        s.AngleZ = angle.Z;
        Apply(layer, angle, game);
    }

    /// <summary>KSA turns the layer by AngularRotationSpeed x elapsed game time (frame update and shadow volume).</summary>
    private static void Apply(CloudLayerRenderData layer, double3 angle, double game)
    {
        double3 speed = game > 1e-3 ? angle / game : double3.Zero;
        SetSpeed!(layer, speed);
        SetSpeedLength!(layer, speed.Length());
    }
}
