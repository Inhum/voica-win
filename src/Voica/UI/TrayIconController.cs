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
    private MenuItem? _updateMenuItem;
    private string? _updateUrl;
    private OverlayWindow? _overlay;
    private IntPtr _sessionMonitor;

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
        _controller.ModelMissing += ShowModelMissing;
        _controller.KeyMissing += ShowKeyMissing;

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

    /// <summary>
    /// Reflects the dictation state (spec §4.2). Exactly one indicator lights up: with the overlay
    /// on, the capsule carries the state and the tray icon stays neutral; with it off, the icon does
    /// the whole job (recording pulses, transcribing is a static accent).
    /// </summary>
    private void SetState(DictationState state)
    {
        // One monitor per dictation, decided when recording starts and used by both the bar and the
        // result window (spec §4.2/§5) — the screen with the focused window, where the text goes.
        if (state == DictationState.Recording && _sessionMonitor == IntPtr.Zero)
            _sessionMonitor = ScreenPlacement.PickMonitor();
        else if (state == DictationState.Idle)
            _sessionMonitor = IntPtr.Zero;

        if (Prefs.ShowOverlay) UpdateOverlay(state);
        else HideOverlay();

        if (_icon is null) return;

        _pulseTimer.Stop();
        if (Prefs.ShowOverlay)
        {
            _icon.IconSource = _idleIcon;
            _icon.ToolTipText = S.Tray;
            return;
        }

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

    private void UpdateOverlay(DictationState state)
    {
        if (state == DictationState.Idle)
        {
            HideOverlay();
            return;
        }

        if (_overlay is null)
        {
            _overlay = new OverlayWindow(() => _controller?.InputLevel ?? 0);
            _overlay.Cancelled += () => _controller?.CancelDictation();
            _overlay.Stopped += () => _controller?.ToggleDictation();
        }

        if (state == DictationState.Recording) _overlay.ShowRecording(_sessionMonitor);
        else _overlay.ShowTranscribing(_sessionMonitor);
    }

    private void HideOverlay() => _overlay?.HideOverlay();

    /// <summary>Re-applies settings that affect the running app (hotkey + indicator, spec §4/§4.2).</summary>
    private void OnSettingsChanged()
    {
        _controller?.ApplySettings();
        SetState(_controller?.State ?? DictationState.Idle);
    }

    /// <summary>
    /// A modal message that actually reaches the person: raised to the front, and never stacked on
    /// top of an identical one (spec §11.4). Both rules live in <see cref="DialogHost"/>.
    /// </summary>
    private static MessageBoxResult? ShowOnce(string message, MessageBoxButton buttons, MessageBoxImage icon)
        => DialogHost.ShowOnce(message, buttons, icon);

    /// <summary>
    /// The local engine is selected without its model (spec §2.5). A balloon is not enough here:
    /// the dictation did not start at all, and the way out is two clicks away, so the message says
    /// what is missing and offers to go there.
    /// </summary>
    private void ShowModelMissing()
    {
        if (ShowOnce(S.ErrModelMissing, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            OpenSettings(0);   // General: engine + download
    }

    /// <summary>
    /// The cloud engine is selected without a key (spec §9). Same treatment as the missing model
    /// and for the same reason: the dictation did not start, and a balloon would be missed by
    /// someone who is already talking.
    /// </summary>
    private void ShowKeyMissing()
    {
        if (ShowOnce(S.ErrNoKeyAsk, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            OpenSettings(0);   // General: the key field
    }

    private void ShowError(string message) => _icon?.ShowBalloonTip("Voica", message, BalloonIcon.Error);


    private void ShowNotice(string message) => _icon?.ShowBalloonTip("Voica", message, BalloonIcon.Info);

    private void ShowResultWindow(string text)
    {
        // Same screen as the bar, not the one under the mouse (spec §5).
        var window = new ResultWindow(text, _sessionMonitor);
        window.Show();
        window.Activate();
    }

    private void OpenSettings(int? tabIndex = null)
    {
        bool existed = _settingsWindow is not null;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(OnSettingsChanged);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            // ⚠️ The tab is chosen BEFORE the window is shown (spec §11.4). Showing first and
            // switching after means the window appears on General and visibly jumps to About —
            // both the tab and the height change in front of the person. This is the same rake
            // macOS documents for the first show; here it costs one line of ordering.
            if (tabIndex is { } first) _settingsWindow.SelectTab(first);
            _settingsWindow.Show();
        }
        else if (tabIndex is { } later)
        {
            _settingsWindow.SelectTab(later);
        }
        Raise(_settingsWindow);
        Log.Info($"settings opened at tab {tabIndex?.ToString() ?? "-"} (already open: {existed})");
    }

    private void OpenHistory()
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow();
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        Raise(_historyWindow);
    }

    /// <summary>
    /// Brings one of our windows to the front, for real.
    ///
    /// ⚠️ <c>Activate()</c> alone is not enough for a tray-only app. Windows refuses to let a
    /// process that does not own the foreground steal it, and the tray menu closing hands the
    /// foreground back to whatever the person was working in — so the call quietly turns into a
    /// blink of the taskbar button, which reads as "the menu item does nothing". A minimized
    /// window has to be restored first, and the brief Topmost bounce is what actually raises it.
    /// </summary>
    private static void Raise(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        bool wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Activate();
        window.Topmost = wasTopmost;
        window.Focus();
    }

    /// <summary>About lives as a Settings tab now (parity with macOS) — open Settings there.</summary>
    private void OpenAbout()
    {
        Log.Info("tray: About");
        OpenSettings(SettingsWindow.AboutTabIndex);
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

    /// <summary>
    /// One update check (spec §10). A background check is SILENT whatever happens — it goes to the
    /// log and nowhere else; only a check the user asked for is allowed to put a window on screen.
    /// Behind a proxy that refuses (§9.5) the background check fails at every launch, and a chatty
    /// one would turn that into a stream of windows nobody can act on.
    /// </summary>
    private async Task RunUpdateCheckAsync(bool manual)
    {
        // Before the request, not after (spec §10): the slot belongs to the attempt, not to its
        // outcome. See Updater.TakeDailySlot.
        Updater.TakeDailySlot();
        var result = await Updater.CheckAsync();
        Log.Info($"update check ({(manual ? "manual" : "launch")}): {result.Outcome} {result.Version ?? ""} {result.Message ?? ""}".TrimEnd());

        switch (result.Outcome)
        {
            case UpdateOutcome.Available:
                _updateUrl = result.Url;
                if (_updateMenuItem is not null)
                    _updateMenuItem.Header = string.Format(S.MenuDownloadUpdateFmt, result.Version);
                if (manual && result.Url is not null
                    && ShowOnce(string.Format(S.UpdateAvailableAskFmt, result.Version),
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    OpenUrl(result.Url);
                break;

            case UpdateOutcome.UpToDate:
                if (manual)
                    ShowOnce(string.Format(S.UpdateUpToDateFmt, AppInfo.Version),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateOutcome.NoRelease:
                if (manual)
                    ShowOnce(S.UpdateNoReleases, MessageBoxButton.OK, MessageBoxImage.Information);
                break;

            case UpdateOutcome.Error:
                if (manual)
                    ShowOnce(string.Format(S.UpdateErrorFmt, result.Message),
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
        _overlay?.Close();
        _overlay = null;
        _controller?.Dispose();
        _controller = null;
        _icon?.Dispose();
        _icon = null;
    }
}
