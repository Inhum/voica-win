using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voica;

public enum UpdateOutcome { UpToDate, Available, NoRelease, Error }

/// <summary>Result of an update check (spec §10).</summary>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, string? Version = null, string? Url = null, string? Message = null);

/// <summary>
/// Update checking against this OS's own GitHub repo (spec §10): Windows → Inhum/voica-win.
/// Anonymous GET of the latest release; compares versions; only ever opens the release page —
/// never downloads or installs. Throttled to once per day on launch.
/// </summary>
public static class Updater
{
    private static readonly Uri LatestReleaseEndpoint =
        new($"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LaunchThrottle = TimeSpan.FromDays(1);

    // The shared client (spec §9.5). The Accept header rides on the request rather than the client,
    // because the client is shared with recognition and the model download.
    private static HttpClient Http => Net.Shared;

    /// <summary>Normalizes a release tag: trims and drops a leading "v" (spec §10).</summary>
    public static string Normalize(string tag)
    {
        var t = (tag ?? string.Empty).Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
        return t.Trim();
    }

    /// <summary>
    /// True if <paramref name="candidate"/> is a newer version than <paramref name="current"/>.
    ///
    /// Numbers first, component-wise; the release-candidate suffix decides only when the numbers
    /// are equal, and there a finished release beats its own candidate (spec §13) — otherwise the
    /// tester running <c>0.8.2-rc.1</c> is never told that <c>0.8.2</c> came out.
    ///
    /// ⚠️ The suffix has to come off BEFORE the split on dots. Left on, <c>2-rc</c> parses as 0 and
    /// the app offers the tester an "update" to the previous release.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Components(Core(candidate));
        var b = Components(Core(current));
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }

        // Same numbers: a release (no suffix) is newer than any candidate of it, and rc.2 is newer
        // than rc.1. Two identical strings are not newer than each other.
        var (candidateRc, currentRc) = (Candidate(candidate), Candidate(current));
        if (candidateRc == currentRc) return false;
        if (candidateRc is null) return true;     // release vs its candidate
        if (currentRc is null) return false;      // candidate vs the release it precedes
        return candidateRc > currentRc;
    }

    /// <summary>The numeric part of a version: <c>v0.8.2-rc.1</c> → <c>0.8.2</c> (spec §13).</summary>
    public static string Core(string version)
    {
        var v = Normalize(version);
        int dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }

    /// <summary>The candidate number in <c>-rc.N</c>, or null for a finished release (spec §13).</summary>
    public static int? Candidate(string version)
    {
        var v = Normalize(version);
        int dash = v.IndexOf('-');
        if (dash < 0) return null;
        var suffix = v[(dash + 1)..];
        var digits = new string(suffix.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        // An unknown suffix (say "-beta") still means "not the finished release"; without a number
        // of its own it sorts as the earliest candidate rather than as the release.
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static int[] Components(string version) =>
        version.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var v) ? v : 0).ToArray();

    /// <summary>Whether an automatic launch check should run now (throttled to once/day, spec §10).</summary>
    public static bool ShouldCheckOnLaunch()
    {
        if (!Prefs.CheckUpdatesOnLaunch) return false;
        var last = Prefs.LastUpdateCheck;
        return last is null || DateTime.UtcNow - last.Value >= LaunchThrottle;
    }

    /// <summary>
    /// Takes today's check slot, and it has to be called BEFORE the request goes out (spec §10).
    ///
    /// ⚠️ A failure must occupy the daily window exactly as a success does. In a closed network
    /// (§9.5) the check fails on every launch, and a slot taken only on success means trying again
    /// every single time — twenty seconds of timeout at every start, forever. Stamping first also
    /// survives the app being quit while the request is still in the air.
    /// </summary>
    public static void TakeDailySlot() => Prefs.LastUpdateCheck = DateTime.UtcNow;

    /// <summary>Queries the latest release and compares it to the running version.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");   // spec §10
            using var response = await Http.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateOutcome.NoRelease);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateOutcome.Error, Message: $"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheckResult(UpdateOutcome.NoRelease);

            var latest = Normalize(tag);
            return IsNewer(latest, AppInfo.Version)
                ? new UpdateCheckResult(UpdateOutcome.Available, latest, url)
                : new UpdateCheckResult(UpdateOutcome.UpToDate, latest);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // From the localization, like every other network message (spec §9.5): a manual check
            // shows this text in a dialog, and half a sentence in English is how macOS got caught.
            return new UpdateCheckResult(UpdateOutcome.Error, Message: S.NetTimeout);
        }
        catch (Exception ex)
        {
            // Through the shared translation (spec §9.5): behind a proxy this is the difference
            // between "прокси требует авторизации" and an opaque socket error.
            return new UpdateCheckResult(UpdateOutcome.Error, Message: Net.Describe(ex, LatestReleaseEndpoint));
        }
    }
}
