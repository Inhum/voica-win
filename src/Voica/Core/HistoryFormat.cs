using System;
using System.Collections.Generic;

namespace Voica;

/// <summary>Formatting shared by the history window (spec §7).</summary>
public static class HistoryFormat
{
    /// <summary>
    /// The metadata line under a record's text: when it was dictated, its language, its length and
    /// the engine that ran (whisper… / gigaam…). Empty parts are skipped rather than shown blank.
    /// </summary>
    public static string MetaLine(Transcription record)
    {
        var parts = new List<string> { record.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") };
        if (!string.IsNullOrWhiteSpace(record.Language)) parts.Add(record.Language!);
        if (record.Duration is { } d) parts.Add($"{d:0.0} s");
        if (!string.IsNullOrWhiteSpace(record.Model)) parts.Add(record.Model!);
        return string.Join(" · ", parts);
    }
}
