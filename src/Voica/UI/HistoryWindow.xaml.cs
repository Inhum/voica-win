using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using NAudio.Wave;

namespace Voica.UI;

/// <summary>
/// History window (spec §7): lists transcriptions (newest first) with re-copy, audio playback,
/// and deletion.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>Row view-model wrapping a <see cref="Transcription"/> for display.</summary>
    public sealed class Row
    {
        public required Transcription Item { get; init; }
        public string When => Item.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string Preview => Item.Text.Replace("\r", " ").Replace("\n", " ");
        public string Lang => Item.Language ?? "";
        public string Duration => Item.Duration is { } d ? $"{d:0.0}s" : "";
        public string ModelName => Item.Model ?? "";
        public string AudioMark => Item.AudioPath is not null && File.Exists(Item.AudioPath) ? "♪" : "";
    }

    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public HistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        Closed += (_, _) => StopPlayback();
    }

    private void Reload()
    {
        var rows = new List<Row>();
        foreach (var t in Store.Shared.All())
            rows.Add(new Row { Item = t });
        Grid.ItemsSource = rows;
        StatusText.Text = rows.Count == 0 ? S.HistEmpty : string.Format(S.HistCountFmt, rows.Count);
        ExportButton.IsEnabled = rows.Count > 0;   // spec §7: disabled on an empty history
    }

    /// <summary>Exports the whole history to Markdown / CSV / JSON (spec §7); audio is not included.</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        var records = Store.Shared.All();
        if (records.Count == 0) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = S.ExportTitle,
            Filter = S.ExportFilters,
            FilterIndex = 1,                      // Markdown by default
            AddExtension = true,
            DefaultExt = ".md",
            FileName = HistoryExport.SuggestedFileName(ExportFormat.Markdown),
        };
        if (dialog.ShowDialog() != true) return;

        // The chosen filter decides the format, so switching it in the dialog switches the output.
        var format = dialog.FilterIndex switch
        {
            2 => ExportFormat.Csv,
            3 => ExportFormat.Json,
            _ => ExportFormat.Markdown,
        };

        try
        {
            var content = HistoryExport.Render(records, format);
            File.WriteAllText(dialog.FileName, content, HistoryExport.EncodingFor(format));
            StatusText.Text = string.Format(S.ExportDoneFmt, records.Count);
            Log.Info($"history exported: {records.Count} records → {format}");
        }
        catch (Exception ex)
        {
            Log.Error("history export failed", ex);
            StatusText.Text = string.Format(S.ExportFailedFmt, ex.Message);
        }
    }

    private Transcription? Selected => (Grid.SelectedItem as Row)?.Item;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t)
        {
            AutoInsert.CopyToClipboard(t.Text);
            StatusText.Text = S.HistCopied;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } t) return;
        if (t.AudioPath is null || !File.Exists(t.AudioPath))
        {
            StatusText.Text = S.HistNoAudio;
            return;
        }

        try
        {
            StopPlayback();
            _reader = new AudioFileReader(t.AudioPath);
            _output = new WaveOutEvent();
            _output.Init(_reader);
            _output.PlaybackStopped += (_, _) => Dispatcher.Invoke(StopPlayback);
            _output.Play();
            StatusText.Text = S.HistPlaying;
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(S.HistPlayFailFmt, ex.Message);
            StopPlayback();
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } t) return;
        var confirm = MessageBox.Show(S.HistDeleteConfirm, "Voica",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        StopPlayback();
        Store.Shared.Delete(t.Id);
        Reload();
        StatusText.Text = S.HistDeleted;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void StopPlayback()
    {
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
    }
}
