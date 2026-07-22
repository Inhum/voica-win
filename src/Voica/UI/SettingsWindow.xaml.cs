using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Voica.UI;

/// <summary>
/// Settings window (spec §3–§11). All settings apply immediately; the Groq API key applies only on
/// Save (spec §9). Also hosts "Delete all data" with random-phrase confirmation (spec §11).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Action _onHotkeyChanged;
    private bool _loaded;

    public SettingsWindow(Action onHotkeyChanged)
    {
        _onHotkeyChanged = onHotkeyChanged;
        InitializeComponent();
        LoadFromPrefs();
        Closed += (_, _) => _downloadCts?.Cancel();
    }

    private void LoadFromPrefs()
    {
        _loaded = false;

        EngineCombo.ItemsSource = new[] { S.EngineCloud, S.EngineLocal };
        EngineCombo.SelectedIndex = Prefs.Engine == EngineKind.Local ? 1 : 0;
        RefreshModelStatus();

        ModeCombo.ItemsSource = new[] { S.ModePtt, S.ModeToggle };
        ModeCombo.SelectedIndex = Prefs.Mode == DictationMode.Ptt ? 0 : 1;

        KeyCombo.ItemsSource = HotkeyBinding.Presets.Select(p => p.DisplayName()).ToList();
        RefreshHotkeyUi();

        OutputCombo.ItemsSource = new[] { S.OutputInsert, S.OutputWindow };
        OutputCombo.SelectedIndex = Prefs.Output == OutputMode.Insert ? 0 : 1;

        StoreAudioCheck.IsChecked = Prefs.StoreAudio;
        NotifyInsertCheck.IsChecked = Prefs.NotifyOnInsert;
        CheckUpdatesCheck.IsChecked = Prefs.CheckUpdatesOnLaunch;
        RetentionBox.Text = Prefs.RetentionDays.ToString();
        VocabularyBox.Text = Prefs.Vocabulary;
        UpdateVocabCounter();
        LlmCheck.IsChecked = Prefs.LlmPostProcess;
        LlmStatusText.Text = "";

        // Prefill the saved key (masked) so "Show" can reveal it, like the macOS app.
        KeyBox.Password = KeyStore.Load() ?? "";
        RefreshKeyStatus();
        _loaded = true;

        // Spec §6.1 UX: probe model availability when opening with the toggle already on.
        if (Prefs.LlmPostProcess)
            _ = ProbeChatModelAsync();
    }

    // --- Recognition engine + model (spec §2.5) ---

    private System.Threading.CancellationTokenSource? _downloadCts;

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.Engine = EngineCombo.SelectedIndex == 1 ? EngineKind.Local : EngineKind.Cloud;

        // Spec §2.5: the model downloads on demand when the local engine is first enabled.
        if (Prefs.Engine == EngineKind.Local && !ModelManager.IsInstalled() && _downloadCts is null)
            _ = DownloadModelAsync();
        RefreshModelStatus();
    }

    private void RefreshModelStatus()
    {
        bool installed = ModelManager.IsInstalled();
        bool downloading = _downloadCts is not null;
        long mb = ModelManager.TotalSize / (1024 * 1024);

        if (!downloading)
            ModelStatusText.Text = installed
                ? string.Format(S.ModelDownloadedFmt, mb)
                : string.Format(S.ModelNotDownloadedFmt, mb);

        DownloadModelButton.Visibility = !installed && !downloading ? Visibility.Visible : Visibility.Collapsed;
        DeleteModelButton.Visibility = installed && !downloading ? Visibility.Visible : Visibility.Collapsed;
        ModelProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        if (_downloadCts is null) _ = DownloadModelAsync();
    }

    private async System.Threading.Tasks.Task DownloadModelAsync()
    {
        _downloadCts = new System.Threading.CancellationTokenSource();
        RefreshModelStatus();
        try
        {
            var progress = new Progress<double>(p =>
            {
                ModelProgress.Value = p * 100;
                ModelStatusText.Text = string.Format(S.ModelDownloadingFmt, (int)(p * 100));
            });
            await ModelManager.DownloadAsync(progress, _downloadCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error("model download failed", ex);
            MessageBox.Show(string.Format(S.ModelDownloadFailedFmt, ex.Message), "Voica",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            RefreshModelStatus();
        }
    }

    private void OnDeleteModel(object sender, RoutedEventArgs e)
    {
        ModelManager.Delete();
        RefreshModelStatus();
    }

    // --- Immediate-apply settings ---

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.Mode = ModeCombo.SelectedIndex == 0 ? DictationMode.Ptt : DictationMode.Toggle;
        _onHotkeyChanged();
    }

    private void OnKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || KeyCombo.SelectedIndex < 0) return;
        Prefs.Hotkey = HotkeyBinding.Presets[KeyCombo.SelectedIndex];
        _onHotkeyChanged();
        RefreshHotkeyUi();
    }

    private void OnCustomKey(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyCaptureDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } binding)
        {
            Prefs.Hotkey = binding;
            _onHotkeyChanged();
            RefreshHotkeyUi();
        }
    }

    /// <summary>Reflects the current binding: selects the matching preset (or none) and shows its name.</summary>
    private void RefreshHotkeyUi()
    {
        var wasLoaded = _loaded;
        _loaded = false;   // avoid re-entering OnKeyChanged while we set the selection
        var current = Prefs.Hotkey;
        int index = HotkeyBinding.Presets.ToList().FindIndex(p => p == current);
        KeyCombo.SelectedIndex = index;   // -1 for a custom combo
        CurrentHotkeyText.Text = string.Format(S.HotkeyCurrentFmt, current.DisplayName());
        _loaded = wasLoaded;
    }

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.Output = OutputCombo.SelectedIndex == 0 ? OutputMode.Insert : OutputMode.Window;
    }

    private void OnStoreAudioChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.StoreAudio = StoreAudioCheck.IsChecked == true;
    }

    private void OnNotifyInsertChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.NotifyOnInsert = NotifyInsertCheck.IsChecked == true;
    }

    private void OnCheckUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.CheckUpdatesOnLaunch = CheckUpdatesCheck.IsChecked == true;
    }

    private void OnRetentionChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (int.TryParse(RetentionBox.Text, out int days) && days >= 0)
            Prefs.RetentionDays = days;
        else
            RetentionBox.Text = Prefs.RetentionDays.ToString();   // revert invalid input
    }

    private void OnVocabularyChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.Vocabulary = VocabularyBox.Text;
    }

    private void OnVocabularyTextChanged(object sender, RoutedEventArgs e) => UpdateVocabCounter();

    /// <summary>Live counter "N / budget" (spec §11); warning color when over — only the tail is sent.</summary>
    private void UpdateVocabCounter()
    {
        int len = VocabularyBox.Text.Trim().Length;
        VocabCounter.Text = string.Format(S.VocabCounterFmt, len, GroqClient.PromptCharBudget);
        VocabCounter.Foreground = len > GroqClient.PromptCharBudget
            ? System.Windows.Media.Brushes.Firebrick
            : System.Windows.SystemColors.GrayTextBrush;
    }

    // --- AI term correction (spec §6.1) ---

    private void OnLlmChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Prefs.LlmPostProcess = LlmCheck.IsChecked == true;
        LlmStatusText.Text = "";
        if (Prefs.LlmPostProcess)
            _ = ProbeChatModelAsync();
    }

    /// <summary>Non-blocking availability probe of the chat model (spec §6.1 UX).</summary>
    private async System.Threading.Tasks.Task ProbeChatModelAsync()
    {
        var key = KeyStore.Load();
        if (key is null)
        {
            LlmStatusText.Text = string.Format(S.LlmUnavailableFmt, S.KeyNone);
            LlmStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        LlmStatusText.Text = S.LlmChecking;
        LlmStatusText.Foreground = System.Windows.SystemColors.GrayTextBrush;
        var problem = await GroqClient.ValidateChatModelAsync(key);
        if (problem is null)
        {
            LlmStatusText.Text = S.LlmAvailable;
            LlmStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else
        {
            LlmStatusText.Text = string.Format(S.LlmUnavailableFmt, problem);
            LlmStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    // --- Groq API key (Save-gated) ---

    /// <summary>The key currently typed, from whichever box (masked / shown) is visible.</summary>
    private string CurrentKey =>
        (ShowKeyCheck.IsChecked == true ? KeyBoxVisible.Text : KeyBox.Password).Trim();

    private void OnToggleShowKey(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            KeyBoxVisible.Text = KeyBox.Password;
            KeyBoxVisible.Visibility = Visibility.Visible;
            KeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyBox.Password = KeyBoxVisible.Text;
            KeyBox.Visibility = Visibility.Visible;
            KeyBoxVisible.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshKeyStatus()
    {
        if (File.Exists(Paths.CredentialsFile))
            KeyStatusText.Text = S.KeySaved;
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")))
            KeyStatusText.Text = S.KeyEnv;
        else
            KeyStatusText.Text = S.KeyNone;
    }

    private async void OnValidateKey(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey;
        if (key.Length == 0) key = KeyStore.Load() ?? "";
        if (key.Length == 0)
        {
            KeyStatusText.Text = S.KeyEnterValidate;
            return;
        }

        ValidateButton.IsEnabled = false;
        SaveKeyButton.IsEnabled = false;
        KeyStatusText.Text = S.KeyValidating;
        try
        {
            var result = await GroqClient.ValidateKeyAsync(key);
            KeyStatusText.Text = result.Status == KeyStatus.Valid
                ? S.KeyValidOk
                : string.Format(S.KeyInvalidFmt, result.Message);
        }
        finally
        {
            ValidateButton.IsEnabled = true;
            SaveKeyButton.IsEnabled = true;
        }
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey;
        if (key.Length == 0)
        {
            KeyStatusText.Text = S.KeyEnterSave;
            return;
        }

        KeyStore.Save(key);
        // Keep the key in the box so it can still be shown/edited (matches macOS).
        RefreshKeyStatus();
        KeyStatusText.Text = S.KeySavedNow;
        Log.Info("Groq key saved");
    }

    // --- Reset settings (spec §11): keeps key, history, audio, and vocabulary ---

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(S.ResetMsg, S.ResetTitle,
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var vocab = Prefs.Vocabulary;   // vocabulary is user content, not a setting
        Prefs.Reset();
        Prefs.Vocabulary = vocab;
        _onHotkeyChanged();
        LoadFromPrefs();
        KeyStatusText.Text = S.ResetDone;
        Log.Info("settings reset to defaults (key/history/audio/vocabulary kept)");
    }

    // --- Delete all data (spec §11) ---

    private void OnDeleteAllData(object sender, RoutedEventArgs e)
    {
        var phrase = "delete-" + Guid.NewGuid().ToString("N")[..4];
        var dialog = new DeleteDataDialog(phrase) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Store.Shared.DeleteAll();
        KeyStore.Delete();
        Prefs.Reset();
        _onHotkeyChanged();       // re-apply default hotkey/mode
        LoadFromPrefs();
        KeyStatusText.Text = S.AllDeleted;
        Log.Info("all data deleted and settings reset");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
