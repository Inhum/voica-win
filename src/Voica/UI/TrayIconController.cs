using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;

namespace Voica.UI;

/// <summary>
/// Owns the system-tray icon and its context menu, hosts the dictation controller, reflects the
/// dictation state through the icon (spec §4, recording pulses), and drives update checks (§10).
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private TaskbarIcon? _icon;
    private DictationController? _controller;
    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private MenuItem? _updateMenuItem;
    private string? _updateUrl;

    private readonly ImageSource _idleIcon = Load("tray-idle.ico");
    private readonly ImageSource _recordingIcon = Load("tray-recording.ico");
    private readonly ImageSource _recordingDimIcon = Load("tray-recording-dim.ico");
    private readonly ImageSource _transcribingIcon = Load("tray-transcribing.ico");

    private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _pulseOn;

    public void Initialize()
    {
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseOn = !_pulseOn;
            if (_icon is not null) _icon.IconSource = _pulseOn ? _recordingIcon : _recordingDimIcon;
        };

        _icon = new TaskbarIcon
        {
            IconSource = _idleIcon,
            ToolTipText = S.Tray,
            ContextMenu = BuildMenu(),
            // Spec §4.1: right click is the standard tray menu; left click duplicates it.
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };
        // Clicking a Voica notification opens History (to review the result).
        _icon.TrayBalloonTipClicked += (_, _) => OpenHistory();

        _controller = new DictationController(Application.Current.Dispatcher);
        _controller.StateChanged += SetState;
        _controller.Error += ShowError;
        _controller.Notice += ShowNotice;
        _controller.ResultReady += ShowResultWindow;

        try
        {
            _controller.Start();
            Log.Info($"hotkey installed: mode={Prefs.Mode}, key={Prefs.Hotkey.DisplayName()}");
        }
        catch (Exception ex)
        {
            Log.Error("hotkey install failed", ex);
            ShowError(string.Format(S.ErrHotkeyFmt, ex.Message));
        }

        SetState(DictationState.Idle);
        Log.Info($"Voica {AppInfo.Version} started");

        if (!KeyStore.HasKey)
            OpenSettings();

        _ = MaybeCheckUpdatesOnLaunchAsync();
    }

    private void SetState(DictationState state)
    {
        if (_icon is null) return;

        _pulseTimer.Stop();
        switch (state)
        {
            case DictationState.Recording:
                _pulseOn = true;
                _icon.IconSource = _recordingIcon;
                _icon.ToolTipText = S.TrayRecording;
                _pulseTimer.Start();
                break;
            case DictationState.Transcribing:
                _icon.IconSource = _transcribingIcon;
                _icon.ToolTipText = S.TrayTranscribing;
                break;
            default:
                _icon.IconSource = _idleIcon;
                _icon.ToolTipText = S.Tray;
                break;
        }
    }

    private void ShowError(string message) => _icon?.ShowBalloonTip("Voica", message, BalloonIcon.Error);

    private void ShowNotice(string message) => _icon?.ShowBalloonTip("Voica", message, BalloonIcon.Info);

    private void ShowResultWindow(string text)
    {
        var window = new ResultWindow(text);
        window.Show();
        window.Activate();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(() => _controller?.ApplySettings());
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    private void OpenHistory()
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow();
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else
        {
            _historyWindow.Activate();
        }
    }

    private void OpenAbout()
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
        }
        _aboutWindow.Activate();
    }

    // --- Updates (spec §10) ---

    private async Task MaybeCheckUpdatesOnLaunchAsync()
    {
        if (!Updater.ShouldCheckOnLaunch()) return;
        await RunUpdateCheckAsync(manual: false);
    }

    private async void OnUpdateMenuClick()
    {
        if (_updateUrl is not null)
        {
            OpenUrl(_updateUrl);
            return;
        }
        await RunUpdateCheckAsync(manual: true);
    }

    private async Task RunUpdateCheckAsync(bool manual)
    {
        var result = await Updater.CheckAsync();
        Prefs.LastUpdateCheck = DateTime.UtcNow;   // cache the check moment (throttle, spec §10)
        Log.Info($"update check: {result.Outcome} {result.Version ?? ""} {result.Message ?? ""}".TrimEnd());

        switch (result.Outcome)
        {
            case UpdateOutcome.Available:
                _updateUrl = result.Url;
                if (_updateMenuItem is not null)
                    _updateMenuItem.Header = string.Format(S.MenuDownloadUpdateFmt, result.Version);
                if (manual && result.Url is not null)
                {
                    var open = MessageBox.Show(
                        string.Format(S.UpdateAvailableAskFmt, result.Version),
                        "Voica", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (open == MessageBoxResult.Yes) OpenUrl(result.Url);
                }
                break;

            case UpdateOutcome.UpToDate:
                if (manual)
                    MessageBox.Show(string.Format(S.UpdateUpToDateFmt, AppInfo.Version), "Voica",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateOutcome.NoRelease:
                if (manual)
                    MessageBox.Show(S.UpdateNoReleases, "Voica",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateOutcome.Error:
                if (manual)
                    MessageBox.Show(string.Format(S.UpdateErrorFmt, result.Message), "Voica",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // --- Menu ---

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // Order per spec §4.1: Dictate · — · History, Settings, About, Check for Updates · — · Quit.
        menu.Items.Add(MenuItem(S.MenuDictate, (_, _) => _controller?.ToggleDictation()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(S.MenuHistory, (_, _) => OpenHistory()));
        menu.Items.Add(MenuItem(S.MenuSettings, (_, _) => OpenSettings()));
        menu.Items.Add(MenuItem(S.MenuAbout, (_, _) => OpenAbout()));
        _updateMenuItem = MenuItem(S.MenuCheckUpdates, (_, _) => OnUpdateMenuClick());
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(S.MenuQuit, (_, _) => Application.Current.Shutdown()));

        return menu;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private static ImageSource Load(string fileName) =>
        new BitmapImage(new Uri($"pack://application:,,,/Resources/{fileName}", UriKind.Absolute));

    public void Dispose()
    {
        _pulseTimer.Stop();
        _controller?.Dispose();
        _controller = null;
        _icon?.Dispose();
        _icon = null;
    }
}
