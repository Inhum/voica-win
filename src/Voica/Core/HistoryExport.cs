using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voica;

/// <summary>Export file formats for the history (spec §7).</summary>
public enum ExportFormat { Markdown, Csv, Json }

/// <summary>
/// Renders the whole transcription history to Markdown / CSV / JSON (spec §7). Text and metadata
/// only — audio is never included. Records keep list order (newest first). Pure formatting logic,
/// covered by the self-test.
/// </summary>
public static class HistoryExport
{
    /// <summary>Stable timestamp format for Markdown/CSV — independent of the system locale.</summary>
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Json => ".json",
        _ => ".md",
    };

    /// <summary>Renders records in the requested format.</summary>
    public static string Render(IReadOnlyList<Transcription> records, ExportFormat format) => format switch
    {
        ExportFormat.Csv => RenderCsv(records),
        ExportFormat.Json => RenderJson(records),
        _ => RenderMarkdown(records),
    };

    /// <summary>CSV needs a UTF-8 BOM so Excel reads Cyrillic correctly (spec §7).</summary>
    public static Encoding EncodingFor(ExportFormat format) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ExportFormat.Csv);

    private static string Local(Transcription r) => r.CreatedAt.LocalDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static string RenderMarkdown(IReadOnlyList<Transcription> records)
    {
        var sb = new StringBuilder();
        sb.Append("# Voica — history (").Append(records.Count).Append(")\n\n");
        foreach (var r in records)
        {
            sb.Append("## ").Append(Local(r)).Append("\n\n");

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Language)) meta.Add(r.Language!);
            if (r.Duration is { } d) meta.Add(d.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            if (!string.IsNullOrWhiteSpace(r.Model)) meta.Add(r.Model!);
            if (meta.Count > 0) sb.Append('_').Append(string.Join(" · ", meta)).Append("_\n\n");

            sb.Append(r.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string RenderCsv(IReadOnlyList<Transcription> records)
    {
        var sb = new StringBuilder();
        sb.Append("created_at,text,language,duration_sec,model\r\n");
        foreach (var r in records)
        {
            sb.Append(CsvField(Local(r))).Append(',')
              .Append(CsvField(r.Text)).Append(',')
              .Append(CsvField(r.Language ?? "")).Append(',')
              .Append(CsvField(r.Duration?.ToString("0.00", CultureInfo.InvariantCulture) ?? "")).Append(',')
              .Append(CsvField(r.Model ?? "")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180: quote fields containing a quote, comma or newline; double inner quotes.</summary>
    private static string CsvField(string value) =>
        value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static string RenderJson(IReadOnlyList<Transcription> records)
    {
        // SortedDictionary keeps keys alphabetical, matching the macOS export byte-for-byte (spec §7).
        var list = new List<SortedDictionary<string, object?>>();
        foreach (var r in records)
        {
            var obj = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = r.Id,
                ["created_at"] = r.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
                ["text"] = r.Text,
            };
            // Optional fields are omitted when empty (spec §7).
            if (!string.IsNullOrWhiteSpace(r.Language)) obj["language"] = r.Language;
            if (r.Duration is { } d) obj["duration_sec"] = d;
            if (!string.IsNullOrWhiteSpace(r.Model)) obj["model"] = r.Model;
            if (!string.IsNullOrWhiteSpace(r.AudioFilename)) obj["audio_filename"] = r.AudioFilename;
            list.Add(obj);
        }

        return JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep Cyrillic readable
        });
    }

    /// <summary>Default file name for the save dialog, e.g. <c>voica-history-2026-07-22.md</c>.</summary>
    public static string SuggestedFileName(ExportFormat format) =>
        $"voica-history-{DateTime.Now:yyyy-MM-dd}{Extension(format)}";
}
