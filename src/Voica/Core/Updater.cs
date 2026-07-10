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

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Voica");   // spec §10
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Normalizes a release tag: trims and drops a leading "v" (spec §10).</summary>
    public static string Normalize(string tag)
    {
        var t = (tag ?? string.Empty).Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
        return t.Trim();
    }

    /// <summary>True if <paramref name="candidate"/> is a newer version than <paramref name="current"/>, compared component-wise.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Components(Normalize(candidate));
        var b = Components(Normalize(current));
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
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

    /// <summary>Queries the latest release and compares it to the running version.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var response = await Http.GetAsync(LatestReleaseEndpoint, cts.Token);
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
            return new UpdateCheckResult(UpdateOutcome.Error, Message: "Timed out.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateOutcome.Error, Message: ex.Message);
        }
    }
}
