using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using NAudio.Wave;

namespace Voica.UI;

/// <summary>
/// History window (spec §7): the list of transcriptions on the left (newest first), the selected
/// record's text on the right, with re-copy, audio playback, deletion, search and export.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>Row view-model wrapping a <see cref="Transcription"/> for the list.</summary>
    public sealed class Row
    {
        public required Transcription Item { get; init; }

        /// <summary>The active search query — the cell highlights its occurrences (spec §7).</summary>
        public string Query { get; init; } = "";

        public string When => Item.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        public string Preview => Item.Text.Replace("\r", " ").Replace("\n", " ");
    }

    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    /// <summary>The whole history as loaded; the list shows what the search leaves of it (spec §7).</summary>
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
        var q = Query;
        var rows = HistorySearch.Filter(_all, q).Select(t => new Row { Item = t, Query = q }).ToList();
        List.ItemsSource = rows;
        if (rows.Count > 0) List.SelectedIndex = 0;

        EmptyLabel.Text = _all.Count == 0 ? S.HistEmpty : S.HistSearchNone;
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetail();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Ctrl+F puts the caret in the search box wherever the focus stands — without it the field has
    /// to be found with the mouse. Escape clears the query, the way a search field is expected to
    /// behave.
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

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && List.SelectedItems.Count > 0)
        {
            OnDelete(sender, e);
            e.Handled = true;
        }
    }

    private Transcription? Selected =>
        List.SelectedItems.Count == 1 ? (List.SelectedItem as Row)?.Item : null;

    /// <summary>All selected records (spec §7: multi-select).</summary>
    private List<Transcription> SelectedItems =>
        List.SelectedItems.OfType<Row>().Select(r => r.Item).ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    /// <summary>
    /// Shows the selected record on the right and keeps the buttons honest. With more than one
    /// record picked, the pane shows a counter and Copy/Play go dark — they only mean something for
    /// a single record (spec §7).
    /// </summary>
    private void UpdateDetail()
    {
        int selected = List.SelectedItems.Count;
        CopyButton.IsEnabled = selected == 1;
        DeleteButton.IsEnabled = selected >= 1;
        RefreshPlayButton();

        if (selected == 0)
        {
            Detail.Document = new FlowDocument();
            StatusText.Text = "";
            SearchInfo.Text = "";
            return;
        }
        if (selected > 1)
        {
            var many = string.Format(S.HistSelectedFmt, selected);
            Detail.Document = Highlight.MutedDocument(many);
            StatusText.Text = many;
            SearchInfo.Text = "";
            return;
        }

        var record = Selected!;
        var q = Query;
        int matches = ShowRecord(record, q);
        StatusText.Text = HistoryFormat.MetaLine(record);

        // How many hits are in THIS record — on a long dictation only the first one is in view
        // otherwise, and it is unclear whether scrolling further is worth it. Separate case: the
        // record is in the list because of its raw text (spec §7) and the shown text has none of the
        // query — without a word about it that looks like a bug.
        if (q.Length == 0) SearchInfo.Text = "";
        else if (matches > 0) SearchInfo.Text = string.Format(S.HistSearchMatchesFmt, matches);
        else if (HistorySearch.MatchedOnlyInRaw(record, q)) SearchInfo.Text = S.HistSearchInRaw;
        else SearchInfo.Text = "";
    }

    /// <summary>
    /// Puts the record's text in the detail pane with the query highlighted, and returns how many
    /// times it was found there. When the record answered only through the engine's raw text, that
    /// raw text is appended below, muted: otherwise "found in the original" stays a claim the user
    /// cannot check. Highlighting the corresponding part of the final text instead is impossible —
    /// after an AI correction there is no correspondence, the model rewrites whole phrases.
    /// </summary>
    private int ShowRecord(Transcription record, string query)
    {
        var raw = HistorySearch.MatchedOnlyInRaw(record, query) ? record.RawText : null;
        var (document, matches, first) = Highlight.RecordDocument(record.Text, query, raw, S.HistSearchRawPrefix);
        Detail.Document = document;
        // Scrolling to the first hit only makes sense once the document has been laid out.
        if (first is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => first.BringIntoView()));
        return matches;
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
        // Clears "Playing…", whether stopped by hand or at the end of the file.
        if (wasPlaying && Selected is { } t) StatusText.Text = HistoryFormat.MetaLine(t);
    }

    /// <summary>Play ⇄ Stop on the button and the context menu, and keep Stop clickable.</summary>
    private void RefreshPlayButton()
    {
        PlayButton.Content = IsPlaying ? S.BtnStop : S.BtnPlay;
        PlayMenuItem.Header = IsPlaying ? S.BtnStop : S.BtnPlay;
        PlayButton.IsEnabled = IsPlaying || List.SelectedItems.Count == 1;
    }
}
