using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>The whole history as loaded; the grid shows what the search leaves of it (spec §7).</summary>
    private IReadOnlyList<Transcription> _all = Array.Empty<Transcription>();

    /// <summary>Filtering runs on a typing pause, not on every letter (spec §7).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };

    public HistoryWindow()
    {
        InitializeComponent();
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplyFilter(); };
        Loaded += (_, _) => Reload();
        Closed += (_, _) => { _searchDebounce.Stop(); StopPlayback(); };
    }

    private void Reload()
    {
        _all = Store.Shared.All();
        // Export stays an action over the WHOLE history (spec §7), so the search never disables it.
        ExportButton.IsEnabled = _all.Count > 0;
        ApplyFilter();
    }

    private string Query => SearchBox.Text.Trim();

    /// <summary>Rebuilds the list for the current query, keeping a usable selection.</summary>
    private void ApplyFilter()
    {
        var rows = HistorySearch.Filter(_all, Query).Select(t => new Row { Item = t }).ToList();
        Grid.ItemsSource = rows;
        if (rows.Count > 0 && Grid.SelectedIndex < 0) Grid.SelectedIndex = 0;
        ResetStatus();
        UpdateSearchInfo();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Ctrl+F puts the caret in the search box — without it the field has to be found with the
    /// mouse. Escape clears the query, the way a search field is expected to behave.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            _searchDebounce.Stop();
            ApplyFilter();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Says when the selected record answers the query only through the engine's raw text (spec §7):
    /// its shown text then lacks what was typed, which reads as a bug unless it is spelled out.
    /// </summary>
    private void UpdateSearchInfo()
    {
        var q = Query;
        SearchInfo.Text = q.Length > 0 && Selected is { } t && HistorySearch.MatchedOnlyInRaw(t, q)
            ? S.HistSearchInRaw
            : "";
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

    /// <summary>All selected records (spec §7: multi-select).</summary>
    private List<Transcription> SelectedItems =>
        Grid.SelectedItems.OfType<Row>().Select(r => r.Item).ToList();

    /// <summary>Copy/Play only make sense for a single record; delete works on the whole selection.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int count = Grid.SelectedItems.Count;
        CopyButton.IsEnabled = count == 1;
        DeleteButton.IsEnabled = count >= 1;
        RefreshPlayButton();   // stays enabled (as Stop) while something is playing
        ResetStatus();
        UpdateSearchInfo();
    }

    /// <summary>
    /// The idle status line: the size of a multi-selection, otherwise how much of the history is on
    /// screen. Called whenever a transient message ("Playing…", "7 selected") stops being true.
    /// </summary>
    private void ResetStatus()
    {
        int selected = Grid.SelectedItems.Count;
        if (selected > 1)
        {
            StatusText.Text = string.Format(S.HistSelectedFmt, selected);
            return;
        }
        int shown = Grid.Items.Count;
        // An empty history and an empty result are different states (spec §7) — "nothing found"
        // must not read as "you have never dictated anything".
        if (_all.Count == 0) StatusText.Text = S.HistEmpty;
        else if (Query.Length == 0) StatusText.Text = string.Format(S.HistCountFmt, shown);
        else if (shown == 0) StatusText.Text = S.HistSearchNone;
        else StatusText.Text = string.Format(S.HistSearchFoundFmt, shown, _all.Count);
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Grid.SelectedItems.Count > 0)
        {
            OnDelete(sender, e);
            e.Handled = true;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t)
        {
            AutoInsert.CopyToClipboard(t.Text);
            StatusText.Text = S.HistCopied;
        }
    }

    /// <summary>True while audio is playing — the Play button doubles as Stop then (spec §7).</summary>
    private bool IsPlaying => _output is not null;

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        // Same button toggles: playing → stop (parity with the macOS history window).
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

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
            RefreshPlayButton();
        }
        catch (Exception ex)
        {
            // Stop first — it resets the status line — then leave the error showing.
            StopPlayback();
            StatusText.Text = string.Format(S.HistPlayFailFmt, ex.Message);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems;
        if (items.Count == 0) return;

        var question = items.Count == 1
            ? S.HistDeleteConfirm
            : string.Format(S.HistDeleteManyConfirmFmt, items.Count);
        if (MessageBox.Show(question, "Voica", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        StopPlayback();
        // One batch delete (single transaction), not a loop of single deletes (spec §7).
        int deleted = Store.Shared.DeleteMany(items.Select(t => t.Id).ToList());
        Reload();
        StatusText.Text = deleted == 1 ? S.HistDeleted : string.Format(S.HistDeletedManyFmt, deleted);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void StopPlayback()
    {
        bool wasPlaying = IsPlaying;
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
        RefreshPlayButton();
        if (wasPlaying) ResetStatus();   // clears "Playing…", whether stopped by hand or at the end
    }

    /// <summary>Play ⇄ Stop on the button and the context menu, and keep Stop clickable.</summary>
    private void RefreshPlayButton()
    {
        PlayButton.Content = IsPlaying ? S.BtnStop : S.BtnPlay;
        PlayMenuItem.Header = IsPlaying ? S.BtnStop : S.BtnPlay;
        PlayButton.IsEnabled = IsPlaying || Grid.SelectedItems.Count == 1;
    }
}
