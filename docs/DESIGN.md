# KSALiveClouds: design notes

How the mod works inside KSA, what was verified and how, and the known gaps. The player-facing README is
`../README.md`.

## Sol only (Kerbol games)

The mod is for Earth in KSA's Sol system. When the optional KSAWorld mod reports a Kerbol (KSP-world) game, the
mod does nothing: no fetch, no texture swaps, no rotation steering,
no refresh. `WorldGuard.cs` reads `KSAWorld.World.IsKerbol` / `IsFinal` by reflection, and with no KSAWorld it
assumes Sol.
- **At start-up:** KSAWorld gives the last system used, read from settings.toml.
  - **Sol, or no KSAWorld:** the fetch starts at once.
  - **Kerbol:** it waits up to 20 s for KSA to settle the system (`IsFinal`, about 4 s in) and fetches only if
    the popup picked Sol.
  - The first cloud texture load also starts the fetch if it hasn't started yet.
- **Once the system is final and Kerbol:** everything stays off for the session.
- **Why the mod needs its own guard:** KSAWorld's `SolOnly` list only keeps content out of a Kerbol load, and
  this mod has no content, so its own guard is the one that matters.
- **For other system mods:** the swap matches the file names `Earth_clouds_*_layer_4096.ktx2` /
  `EarthTypeMap4096.dds` and the layer ids `EarthClouds` / `EarthClouds2`, so don't reuse them for other
  bodies.

The cubemap face convention, with test vectors checked against KSA's own code, is in `cubemap-vectors.md`.

## KSA 5707 (2026-10-29)

The cloud renderer was refactored for multi-viewport clouds (r5663). Re-checked against 5707's KSA.dll and
Planet.Render.Core.dll:
- `CloudLayerRenderData.UpdateCommonFrameData` gained an `inShaderSlot` first parameter. It now runs once per
  viewport per frame, and per-viewport data lives in `ViewportData`. The old exact-signature lookup would have
  silently dropped the time-warp drift ("no time-warp drift" in the log).
- The fix: every overload by that name is patched (the prefix only needs `__instance`), and the drift state
  advances once per frame (`Program.FrameNumber`, as KSA does). The other viewports in the same frame get the
  same angle.
- Rotation is still `AngularRotationSpeed × elapsed`, from one `CloudLayerRenderData` shared by all viewports;
  the shadow volume uses the same product.
- Unchanged:
  - `TextureReference.DoLoad` / `LocalPath` / `ModPath` / `Texture`.
  - `Create` / `UpdateStaticData` (`cloudLayerReference`).
  - The `AngularRotationSpeed(Length)` setters, `BottomRadius` and `GetCloudLayerReference`.
  - `SimpleVkTexture.UploadData` and `VkUtils.UploadBufferToImage`'s face-major mip layout.
  - `Program.GetRenderer` and the `Universe` time calls.
  - The stock Earth cloud files (same sizes and formats), layer ids, the shader's coverage/selector code, and
    the cubemap convention: all 15 vectors still match `Celestial.DirectionToCubemap`.
- Crossfade re-tested outside the game: 18Z → 19Z mosaic, a 4096² × 13-mip blend in 0.38 s, the type map in
  0.09 s, all loaded by 5707's libKtx; LC-39A reads 26 → 29 → 42.
- Still not checked: the in-game upload under multi-viewport clouds, and the drift.

## How it works

1. **Fetch** (background task started at `StarMapBeforeMain`): list today's files in the public AWS bucket
   `noaa-gmgsi-pds` (`GMGSI_LW/yyyy/MM/dd/HH/…nc`). Download the newest one, about 7 MB. No key or account is
   needed, and the data is public domain. GMGSI blends GOES-East/West, Himawari and Meteosat at about 8 km.
   Each file is netCDF4 (HDF5): `data[1, 3000, 4999]`, 0–255 counts (higher = colder). The grid is
   **Mercator**, 72.7° S–72.7° N, 0.072° in longitude. It's read with PureHDF.
2. **Cloud signal** (`Coverage.cs`): IR shows temperature, not cloud. So for each pixel it measures how much
   colder it is than the local clear sky. The clear-sky value comes from the warm end (5th percentile) of
   each ~4° block, taken as the warmest of its 3×3 neighbourhood. A latitude cap stops a cold clear surface
   being read as a cloud top. In the cap's numbers: clear ground is never taken as colder than ~285 K at the
   equator or ~250 K at 72°.
3. **Cubemaps** (`CubeBuilder.cs`): both coverage maps are rebuilt in KSA's stock format, using KSA's own
   face convention (`Celestial.DirectionToCubemap` on planet-fixed directions, Z = north,
   lon = atan2(Y, X)). The format is a BC4 cubemap, 4096² faces, 13 mips, zstd-supercompressed.
   - Base layer (`EarthClouds`, the volumetric clouds from 2 km, 1.2–6.7 km thick by type):
     `smoothstep(cloud_start, cloud_full, signal)`.
   - Top layer (`EarthClouds2`, cirrostratus at 11 km): cloud tops colder than 238 → 218 K.
   - **No double counting:** where the tops are that cold (high cloud), the base layer keeps only
     `base_under_cirrus` (35%) of its coverage, since IR can't see underneath. The exception is deep
     convection (tops 212 → 202 K), which keeps its whole column because the towers reach down. On the
     2026-10-25 14Z image, under solid cirrus the base layer went from 99% to 52% cover. Globally it lost 2
     points.
   - **Cloud types:** KSA's `EarthTypeMap4096.dds` isn't a type map as such. It's a 0–1 selector between two
     400 km detail tiles in `CloudDetail2048.png`. Tile A (RG) is near-solid stratus (types 0–0.15). Tile B
     (BA) is broken cumulus (types to ~0.36, rarely congestus). The stock selector is pure random noise.
     Ours is a 1024² cubemap:
     - `smoothstep(lumpy_low, lumpy_high, lumpiness)` maxed with deep convection. Lumpiness is the std of
       the IR over ~35 km, averaged over ~120 km, so a field of small cells counts and a single cloud edge
       doesn't.
     - Mixed `type_from_data` (75%) with the stock noise for variety.
     - Stock alone poleward of the data.
     - Smooth decks lean stratus; popcorn fields and storms lean cumulus. KSA's loader dispatches on file
       extension and the shaders accept a cubemap selector, so the swap works like the coverage maps.
   - Fine detail at cloud edges is borrowed from the stock map: its full-resolution level minus its 8×
     smaller mip. Clear sky gets none, so there's no speckle.
   - Poleward of the satellites' reach (|lat| > ~72°) the stock map takes over, blended over the last ~6°.
4. **Swap** (`Mod.cs`): a Harmony prefix on `TextureReference.DoLoad` catches the two stock files
   (`Earth_clouds_{base,top}_layer_4096.ktx2`), plus `EarthTypeMap4096.dds`. It waits for the build (up to `timeout_seconds`). Then it
   points `LocalPath` at the generated file, an absolute path, which wins in `Mod.GetPath`'s
   `Path.Combine`. A finalizer restores the path. Every other use of those texture ids, including the top
   layer's DetailSelector, gets the new map.
5. **Hold still**: prefixes on `CloudLayerRenderData.Create` / `UpdateStaticData` set both Earth layers'
   `RotationSpeed` to 0, so the weather stays over the place it really is. This applies only to layers that
   were replaced. They also record the stock speed, which time warp uses (step 7).
6. **Hourly refresh with a crossfade** (`Live.cs`, wall clock):
   - Every `check_minutes` a background loop asks the pipeline for a newer mosaic. It only lists or downloads
     once one is due: data time + `refresh_minutes` + the ~35 min publishing delay.
   - Before a new build overwrites the cache, the loop reads the maps currently on the GPU.
   - Over `fade_minutes` it then blends old into new in steps `fade_step_seconds` apart. Each step decodes
     the BC4 blocks, lerps them with a smoothstep curve and re-encodes them, for every mip level. That takes
     ~0.4 s per layer on a quarter of the CPU threads.
   - The main thread (`StarMapAfterGui`) uploads each step into the texture KSA already has, using
     `SimpleVkTexture.UploadData` from a staging pool. The upload is the
     whole mip chain, face by face, as `VkUtils.UploadBufferToImage` expects.
   - The last step copies the new blocks exactly.
   - If the game started on stock clouds (offline, or the build timed out), the first refresh fades the live
     map in over them.
7. **Time warp** (real clock + drift): the live layers' rotation is set every frame in a
   `CloudLayerRenderData.UpdateCommonFrameData` prefix. KSA rotates by `AngularRotationSpeed × elapsed game
   time`, so the prefix sets that speed to angle / elapsed. The cloud shadow volume uses the same product, so
   shadows follow.
   - While the simulation runs faster than real time, the angle grows at the layer's stock wind speed with
     the game time that runs ahead of the wall clock. That's about 20 m/s at the cloud base: ~11° per real
     minute at 1000×, a few turns a minute at 100 000×.
   - When the warp ends, the angle eases back the short way round to 0, the real positions, over
     `warp_ease_seconds`.
   - The weather data itself stays on the wall clock. Warping doesn't fast-forward it, because nothing newer
     than "now" exists.

A full build takes ~4 s: download ~1 s, HDF5 read ~0.5 s, two layers ~3 s. At launch a cached build is reused
instantly until a newer mosaic is due, and also when the newest mosaic is the one already built.

Files live in `~/.local/share/KSALiveClouds/` on Linux and `%LOCALAPPDATA%\KSALiveClouds\` on Windows:

- `gmgsi_lw.nc`: the last download.
- `Earth_clouds_base_live.ktx2` (~35–50 MB), `Earth_clouds_top_live.ktx2` (~5–25 MB) and
  `Earth_clouds_type_live.ktx2` (~4 MB).
- `coverage_256.bin`: both layers at 256² per face, for the API.
- `snapshot.txt`: what the build is and when.

If anything fails, the game keeps its stock clouds. If the fetch fails, the last snapshot is used when it's
younger than `max_cache_age_hours`; otherwise you get the stock clouds.

## Settings: `KSALiveClouds/live_clouds.cfg`

| key | default | |
|---|---|---|
| `enabled` | true | false = stock clouds, nothing fetched |
| `refresh_minutes` | 60 | look for a newer mosaic once the data is older than this + 35 min |
| `timeout_seconds` | 45 | longest the loading screen waits for fetch + build |
| `offline` | cache | `cache` = last snapshot, `stock` = KSA's clouds |
| `max_cache_age_hours` | 72 | oldest snapshot `offline = cache` will use |
| `freeze_rotation` | true | hold the Earth layers over the real weather (off = stock drift, no warp handling) |
| `live_refresh` | true | look for newer mosaics during play and crossfade to them |
| `check_minutes` | 10 | how often the in-game refresh checks |
| `fade_minutes` / `fade_step_seconds` | 10 / 30 | crossfade length and the gap between texture uploads (wall clock) |
| `warp_drift` | true | drift at the stock wind speed while time-warping |
| `warp_ease_seconds` | 8 | how long the layers take to ease back onto the real weather after a warp |
| `detail` | 0.45 | stock small-scale texture at cloud edges (0 = smooth ~8 km pixels) |
| `top_layer` | true | also replace the cirrus layer |
| `face_size` | 4096 | 2048 = a quarter of the VRAM, still finer than the data |
| `type_map` | true | replace the stratus/cumulus selector too |
| `type_from_data` | 0.75 | share of satellite vs stock noise in the selector |
| `lumpy_low` / `lumpy_high` | 4 / 12 | IR lumpiness (counts) where the selector leans cumulus |
| `deep_start_k` / `deep_full_k` | 212 / 202 | cloud tops this cold are convective towers (cumulus, full base cloud) |
| `base_under_cirrus` | 0.35 | share of base coverage kept under high cloud (not under deep convection) |
| `cloud_start` / `cloud_full` | 10 / 42 | counts below the clear sky where cloud starts / is solid (~0.5 K per count) |
| `cirrus_start_k` / `cirrus_full_k` | 238 / 218 | cloud-top temperatures for the cirrus layer |

## Build / install / try outside the game

```bash
tools/build.sh                 # refuses while KSA runs; --when-closed waits (run that in the background)
tools/install.sh               # links out/KSALiveClouds into the active Borea instance (backs up its manifest)
LIVE_CLOUDS_CFG=KSALiveClouds/live_clouds.cfg tools/LiveCloudsCli/bin/Release/net10.0/LiveCloudsCli fetch
tools/package.sh               # release zip in dist/ (refuses a dirty or unpushed tree)
LiveCloudsCli build <file.nc> [cacheDir]        # from a downloaded GMGSI file
LiveCloudsCli preview <file.ktx2> <out.pgm> [w] # equirectangular preview, read back through KSA's face convention
LiveCloudsCli probe <file.ktx2> 28.608 -80.604  # coverage (0-255) at LC-39A
LiveCloudsCli blend <a.ktx2> <b.ktx2> 0.5 <out.ktx2>  # one in-game crossfade step, timed
LiveCloudsCli vectors                            # direction -> face/uv table (KSA cubemap convention)
```

Running `fetch` before launching makes the game start with a fresh map and no wait.

Log lines start with `[KSALiveClouds]`. Expect `satellite clouds of … UTC`, `base layer <- …`,
`top layer <- …` and `EarthClouds: rotation … -> 0`.

## Checked so far (2026-10-24, outside the game)

- GMGSI 2026-10-24 23Z: LC-39A coverage was 4/255. KXMR and KTTS reported FEW039 and SCT065 (mostly clear).
- GMGSI 2026-10-25 03Z: LC-39A coverage was 202/255. Showers were moving in off the Atlantic: KTTS had
  rain and BKN027, KMLB BKN043.
- Previews show the real cyclones and fronts in the right places. Stock clouds fill in beyond 72°.
- Both generated `.ktx2` files load with KSA's `libKtx.so` (`ktxTexture2_CreateFromNamedFile` with image
  data).
- Crossfade (2026-10-25, `LiveCloudsCli blend`): 23Z → 03Z at t = 0, 0.5, 1 previews as expected. LC-39A
  reads 1 → 101 → 202. A full 4096² × 13-mip chain blends in 0.37 s on 6 of 24 threads. KSA's libKtx loads
  the blended file.
- Type map and split (2026-10-25 14Z): the selector follows the satellite (smooth cyclone cores stratus,
  broken fields cumulus). All three generated files load in libKtx. A full build is 4.2–4.7 s.
- Not checked yet:
  - The in-game look, including the new type map's effect on cloud shapes: 2D clouds from orbit, and volumetrics near the ground.
  - The load-time wait.
  - That StarMap picks up `PureHDF.dll` / `ZstdSharp.dll` from the mod folder. KSAIva ships StbImageSharp
    the same way.
  - The in-game parts of the refresh: the texture upload, any frame hitch it causes, and the time-warp
    drift and ease-back. Log lines to look for: `crossfading to …`, `fade step took …`, `crossfade done`.

## Limits / known gaps

- **IR can't see warm low cloud.** Marine stratocumulus and fog are barely colder than the sea, so they
  mostly come out clear. Cold clear land at night (high plateaus, winter interiors) can show as thin grey
  haze.
- **The crossfade is a dissolve, not motion.** Clouds fade out in one place and in at the next rather than
  sliding. With hourly mosaics that's gentle. Motion would need optical flow or GFS winds.
- **Each fade step is two big uploads on the main thread.** That's about 67 MB of BC4 per layer at 4096²,
  every `fade_step_seconds`, which may show as a small hitch. `face_size = 2048` makes it a quarter of that.
  RAM use peaks at ~400 MB during a fade (old, new and working copies of both layers).
- **Cloud types are inferred from IR texture and temperature.** Single-band IR can't really tell stratus
  from cumulus. KSA's tiles also rarely reach congestus, so even storms mostly render as cumulus.
- **Under-cirrus coverage is a guess.** IR can't see below high cloud. 35% is a compromise between thin
  cirrus over clear ground (should be 0) and frontal decks (should be ~100%).
- **Stock normals.** The 2D layer's normal map is still stock. It only shades where there is cloud, so the
  effect is extra texture rather than something broken.
- **The type map won't fade in mid-game from stock.** If the game started on stock clouds, the stock type
  map is a 2D texture, and it can't be refreshed into a cubemap until the next launch. The coverage maps do
  fade in.
- **Game time vs real time.** KSA's calendar isn't today. The clouds are today's, put at the real places.
- **RealAtmospheres** keeps a shadow `Core/Astronomicals.xml` (in `_RealSharedShadow`) with the same texture
  ids. The swap matches on file name, so it works either way.

## Possible future work

1. **Data source.** Now: GMGSI IR only, which is real observation, sharp, hourly, and blind to warm low
   cloud. Option: add NOAA GFS 0.25° (low/mid/high cloud cover, ~1 MB through the NOMADS grib filter, no
   key). It would fill in low cloud and the poles and could drive the cloud-type map. Its clouds are a
   model analysis and smoother. GRIB2 decoding would be a new dependency.
2. **Offline.** Now: last snapshot up to 72 h old, then stock. Option: always stock when offline, or keep the
   last snapshot forever.
3. **Motion between mosaics** (optical flow or model winds) instead of a dissolve.
4. A normal map for the 2D layer generated from the live coverage.

## For other mods: the coverage API

A mod that needs to know how cloudy the sky is (photo scoring, say) can read the live coverage by reflection.
It needs plain types only, and no reference to this assembly:

```csharp
Type? t = Type.GetType("KSALiveClouds.LiveClouds, KSALiveClouds");
bool active = t != null && (bool)t.GetProperty("Active")!.GetValue(null)!;
// CoverageAt(double x, double y, double z, int layer): planet-fixed (CCF) direction as in
// Celestial.GetDirCcfFromLatLon; layer 0 = base, 1 = cirrus. Returns 0..1, or NaN when stock clouds are
// showing, the layer wasn't replaced, or freeze_rotation is off. Follows crossfades (FadeT) and the warp
// drift. CoverageAtLatLon(latDeg, lonDeg, layer) too.
var at = t?.GetMethod("CoverageAt", new[] { typeof(double), typeof(double), typeof(double), typeof(int) });
float cov = (float)at!.Invoke(null, new object[] { x, y, z, 0 })!;
```

Fall back to your own idea of the stock clouds when the result is NaN. No time argument is needed: outside a warp the
layers sit on the real weather, and during a warp the API applies the current drift angle. The drift's sign
comes from reading KSA's quaternion convention and hasn't been checked in game. With stock clouds,
the caller has to apply the stock drift itself.

## Releasing

Releases go out on GitHub (`ghost225/KSALiveClouds`), and Borea picks them up from there.
1. Bump `<Version>` in `KSALiveClouds/KSALiveClouds.csproj`.
2. Commit and push. The repo's local git identity is the release account's.
3. Run `tools/package.sh`. It refuses a dirty tree or an unpushed `HEAD`, builds into a temp folder (not the
   live `out/` install), and writes `dist/KSALiveClouds-v<version>.zip` with one `KSALiveClouds/` folder. It
   prints the SHA-256.
4. `gh release create v<version> dist/KSALiveClouds-v<version>.zip --target <sha> --notes-file …`
   - Add `--prerelease` for a pre-release.
   - The zip must be the only asset.
   - Check that the release page's digest matches.
5. The Borea listing is `docs/borea/KSALiveClouds.toml`, as submitted to `KSAModding/content-index` at
   `listings/KSALiveClouds.toml`. Once it's merged, the index's watcher stamps each new GitHub release by
   itself. Edit the listing only to change its text, images, compatibility or loader bounds.
