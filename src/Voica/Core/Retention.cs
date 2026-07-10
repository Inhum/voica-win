using System;

namespace Voica;

/// <summary>
/// Audio retention (spec §8): on launch, delete audio files older than N days and clear their
/// <c>audio_filename</c> (the text stays). N = <see cref="Prefs.RetentionDays"/>; 0 means keep forever.
/// </summary>
public static class Retention
{
    public static void RunOnLaunch()
    {
        try
        {
            int days = Prefs.RetentionDays;
            if (days <= 0) return;   // 0 = keep audio forever

            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            int affected = Store.Shared.PurgeAudioOlderThan(cutoff);
            if (affected > 0)
                Log.Info($"retention: cleared audio for {affected} record(s) older than {days} day(s)");
        }
        catch (Exception ex)
        {
            Log.Error("retention cleanup failed", ex);
        }
    }
}
