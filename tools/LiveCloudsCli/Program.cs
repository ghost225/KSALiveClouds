// LiveCloudsCli: run the KSALiveClouds pipeline outside the game.
//   LiveCloudsCli fetch [cacheDir]            fetch the newest GMGSI mosaic and build (honours live_clouds.cfg rules)
//   LiveCloudsCli build <file.nc> [cacheDir]  build from a downloaded GMGSI file
//   LiveCloudsCli preview <file.ktx2> <out.pgm> [width]   equirectangular preview read back through KSA's face convention
//   LiveCloudsCli probe <file.ktx2> <lat> <lon>            coverage at a place (0-255)
//   LiveCloudsCli blend <a.ktx2> <b.ktx2> <t> <out.ktx2>   the in-game crossfade step at t, timed, as a file
//   LiveCloudsCli vectors                                  direction -> face/uv test vectors (KSA's cubemap convention)

using KSALiveClouds;

string stockDir = Path.Combine(Environment.GetEnvironmentVariable("KSA_DIR") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "Games/KSA/current"),
    "Content/Core/Textures/Clouds/Compressed");
string cfgPath = Environment.GetEnvironmentVariable("LIVE_CLOUDS_CFG") ?? "";
Config cfg = Config.Load(cfgPath);
void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

switch (args.ElementAtOrDefault(0))
{
    case "fetch":
    {
        Snapshot? s = await Pipeline.Run(cfg, stockDir, args.ElementAtOrDefault(1) ?? Pipeline.DefaultCacheDir, Log, CancellationToken.None);
        Log(s == null ? "-> stock clouds" : $"-> {s.BasePath} ({s.Note}, data {s.DataTimeUtc:yyyy-MM-dd HH:mm}Z)");
        break;
    }
    case "build":
    {
        string dir = args.ElementAtOrDefault(2) ?? Pipeline.DefaultCacheDir;
        Directory.CreateDirectory(dir);
        Snapshot s = Pipeline.Build(cfg, args[1], Path.GetFileName(args[1]), stockDir, dir, Log);
        Log($"-> {s.BasePath}");
        break;
    }
    case "preview":
    {
        var cube = Ktx2.Cube.Open(args[1]);
        int w = int.Parse(args.ElementAtOrDefault(3) ?? "2048"), h = w / 2;
        int level = Math.Max(0, (int)Math.Log2(cube.Size / (double)Math.Max(1, w / 4)));
        int fs = cube.Size >> level;
        byte[] blocks = cube.LevelBlocks(level);
        var faces = Enumerable.Range(0, 6).Select(f => cube.Face(level, f, blocks)).ToArray();
        var img = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            double lat = (90 - (y + 0.5) * 180.0 / h) * Math.PI / 180;
            for (int x = 0; x < w; x++)
            {
                double lon = (-180 + (x + 0.5) * 360.0 / w) * Math.PI / 180;
                int f = CubeBuilder.DirToFace(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), out double u, out double v);
                int i = Math.Clamp((int)(u * fs), 0, fs - 1), j = Math.Clamp((int)(v * fs), 0, fs - 1);
                img[y * w + x] = faces[f][j * fs + i];
            }
        });
        using var o = File.Create(args[2]);
        o.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"));
        o.Write(img);
        Log($"preview {w}x{h} from level {level} ({fs}px faces)");
        break;
    }
    case "probe":
    {
        var cube = Ktx2.Cube.Open(args[1]);
        double lat = double.Parse(args[2]) * Math.PI / 180, lon = double.Parse(args[3]) * Math.PI / 180;
        int f = CubeBuilder.DirToFace(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), out double u, out double v);
        byte[] face = cube.Face(0, f);
        int i = (int)(u * cube.Size), j = (int)(v * cube.Size);
        Log($"face {f} texel ({i},{j}) = {face[Math.Clamp(j, 0, cube.Size - 1) * cube.Size + Math.Clamp(i, 0, cube.Size - 1)]}");
        break;
    }
    case "blend":
    {
        var a = Ktx2.Cube.Open(args[1]);
        var b = Ktx2.Cube.Open(args[2]);
        float t = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        byte[][][] la = a.AllLevels(), lb = b.AllLevels();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 4) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outp = lb.Select(l => l.Select(f => new byte[f.Length]).ToArray()).ToArray();
        var items = new List<(int L, int F, int S, int N)>();
        for (int l = 0; l < lb.Length; l++)
            for (int f = 0; f < 6; f++)
                for (int s0 = 0; s0 < lb[l][f].Length / 8; s0 += 8192)
                    items.Add((l, f, s0, Math.Min(8192, lb[l][f].Length / 8 - s0)));
        Parallel.ForEach(items, po, it => Bc4.Blend(la[it.L][it.F].AsSpan(it.S * 8, it.N * 8), lb[it.L][it.F].AsSpan(it.S * 8, it.N * 8), t,
                                                    outp[it.L][it.F].AsSpan(it.S * 8, it.N * 8)));
        Log($"blend t={t} of {a.Size}px x{lb.Length} mips on {po.MaxDegreeOfParallelism} threads: {sw.Elapsed.TotalSeconds:0.00} s");
        Ktx2.WriteCube(args[4], b.Size, outp, b.Dfd);
        break;
    }
    case "vectors":
    {
        // KSA Celestial.DirectionToCubemap, as CubeBuilder.DirToFace: ties go X before Y before Z. uv in [0,1],
        // v = 0 is image row 0. Planet-fixed (CCF): Z = north pole, lon = atan2(Y, X).
        string[] names = { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
        var dirs = new (string Note, double X, double Y, double Z)[]
        {
            ("lat 0 lon 0 (+X centre)", 1, 0, 0),
            ("lat 0 lon 180 (-X centre)", -1, 0, 0),
            ("lat 0 lon 90 (+Y centre)", 0, 1, 0),
            ("lat 0 lon -90 (-Y centre)", 0, -1, 0),
            ("north pole (+Z centre)", 0, 0, 1),
            ("south pole (-Z centre)", 0, 0, -1),
            ("lat 0 lon 45: X/Y edge, tie -> +X", 1, 1, 0),
            ("lat 45 lon 0: X/Z edge, tie -> +X", 1, 0, 1),
            ("lat 45 lon 90: Y/Z edge, tie -> +Y", 0, 1, 1),
            ("lat -45 lon 180: -X/-Z edge, tie -> -X", -1, 0, -1),
            ("cube corner (1,1,1), tie -> +X", 1, 1, 1),
            ("lat 28.608 lon -80.604 (LC-39A)", Math.Cos(28.608 * Math.PI / 180) * Math.Cos(-80.604 * Math.PI / 180), Math.Cos(28.608 * Math.PI / 180) * Math.Sin(-80.604 * Math.PI / 180), Math.Sin(28.608 * Math.PI / 180)),
            ("lat 0 lon -100", Math.Cos(-100 * Math.PI / 180), Math.Sin(-100 * Math.PI / 180), 0),
            ("lat 60 lon 30", Math.Cos(Math.PI / 3) * Math.Cos(Math.PI / 6), Math.Cos(Math.PI / 3) * Math.Sin(Math.PI / 6), Math.Sin(Math.PI / 3)),
            ("lat -70 lon 135", Math.Cos(-70 * Math.PI / 180) * Math.Cos(135 * Math.PI / 180), Math.Cos(-70 * Math.PI / 180) * Math.Sin(135 * Math.PI / 180), Math.Sin(-70 * Math.PI / 180)),
        };
        Console.WriteLine("| direction (x, y, z), unnormalised ok | face | u | v | note |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var d in dirs)
        {
            int f = CubeBuilder.DirToFace(d.X, d.Y, d.Z, out double u, out double v);
            (double bx, double by, double bz) = CubeBuilder.FaceDir(f, u, v);
            double n = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            double err = Math.Abs(bx - d.X / n) + Math.Abs(by - d.Y / n) + Math.Abs(bz - d.Z / n);
            if (err > 1e-9)
                throw new Exception($"round trip failed for {d.Note}: {err}");
            Console.WriteLine($"| ({d.X:0.######}, {d.Y:0.######}, {d.Z:0.######}) | {f} {names[f]} | {u:0.######} | {v:0.######} | {d.Note} |");
        }
        Console.WriteLine();
        Console.WriteLine("Inverse (face, s = 2u-1, t = 2v-1) -> direction before normalising: +X (1,-t,-s), -X (-1,-t,s), +Y (s,1,t), -Y (s,-1,-t), +Z (s,-t,1), -Z (-s,-t,-1). All round trips above checked to 1e-9.");
        break;
    }
    default:
        Console.WriteLine("usage: fetch [cacheDir] | build <file.nc> [cacheDir] | preview <ktx2> <out.pgm> [width] | probe <ktx2> <lat> <lon>");
        break;
}
