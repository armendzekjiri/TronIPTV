namespace TronIPTV.Core;

/// <summary>Minimal SemVer ordering for update checks (major.minor.patch with optional pre-release).</summary>
public static class ReleaseVersion
{
    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="current"/>. A leading "v" is ignored.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var (c, cPre) = Parse(candidate);
        var (cur, curPre) = Parse(current);
        var cmp = c.CompareTo(cur);
        // A stable release beats a pre-release of the same version (0.2.0 > 0.2.0-alpha.1).
        return cmp > 0 || (cmp == 0 && curPre && !cPre);
    }

    private static (Version Core, bool PreRelease) Parse(string value)
    {
        var v = value.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0)
            v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var parsed = Version.TryParse(core, out var version) ? version : new Version(0, 0, 0);
        return (parsed, dash >= 0);
    }
}
