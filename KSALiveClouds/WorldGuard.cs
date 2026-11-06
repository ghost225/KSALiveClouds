// KSALiveClouds: Sol only. In a Kerbol game (the KSAWorld mod's KSP-world system) the mod must do nothing: no fetch, no texture
// swaps, no rotation steering, no refresh. KSAWorld knows the system; it's read by reflection so this mod runs
// fine without it (no KSAWorld = Sol). KSAWorld's SolOnly list only keeps mod *content* out of a Kerbol load,
// and this mod has none, so this is the real guard.
//
// Timing: before the start-up system popup closes, World.SystemId is the last system used (IsFinal false); after,
// it's the chosen one. BeforeMain skips its speculative fetch when the last system was Kerbol; the texture
// load (after the popup) then starts it late if Sol was picked after all.

using System.Reflection;

namespace KSALiveClouds;

internal static class WorldGuard
{
    private static PropertyInfo? _isKerbol, _isFinal;
    private static bool _searched, _kerbolLogged;
    private static bool _kerbolFinal;      // sticky: the system can't change once final

    private static bool Resolve()
    {
        if (_isKerbol != null)
            return true;
        // StarMap may load KSAWorld into its own load context: look among the loaded assemblies first, so we read
        // the copy KSA is actually using, then fall back to a normal type lookup.
        Type? t = AppDomain.CurrentDomain.GetAssemblies()
                           .FirstOrDefault(a => a.GetName().Name == "KSAWorld")?.GetType("KSAWorld.World");
        try { t ??= Type.GetType("KSAWorld.World, KSAWorld"); }
        catch { }
        if (t == null)
        {
            if (!_searched)
                Mod.Log("KSAWorld not found (yet); assuming Sol");
            _searched = true;
            return false;
        }
        _isKerbol = t.GetProperty("IsKerbol", BindingFlags.Public | BindingFlags.Static);
        _isFinal = t.GetProperty("IsFinal", BindingFlags.Public | BindingFlags.Static);
        return _isKerbol != null;
    }

    private static bool Read(PropertyInfo? p, bool fallback)
    {
        try { return p?.GetValue(null) is bool b ? b : fallback; }
        catch { return fallback; }
    }

    /// <summary>KSAWorld is loaded (without it the game is Sol).</summary>
    public static bool Present => Resolve();

    /// <summary>KSA has settled the system (KSAWorld.IsFinal); true without KSAWorld.</summary>
    public static bool IsFinal => !Resolve() || Read(_isFinal, false);

    /// <summary>This game is Kerbol for certain (the system popup has closed).</summary>
    public static bool KerbolFinal
    {
        get
        {
            _ = IsKerbol;
            return _kerbolFinal;
        }
    }

    /// <summary>The game is (or, before the popup closes, was last) a Kerbol game.</summary>
    public static bool IsKerbol
    {
        get
        {
            if (_kerbolFinal)
                return true;
            if (!Resolve())
                return false;
            bool kerbol = Read(_isKerbol, false);
            if (kerbol && Read(_isFinal, true))
            {
                _kerbolFinal = true;
                if (!_kerbolLogged)
                    Mod.Log("Kerbol game: live Earth clouds off (no fetch, swaps, steering or refresh)");
                _kerbolLogged = true;
            }
            return kerbol;
        }
    }
}
