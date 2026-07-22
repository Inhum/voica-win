using System.Globalization;

namespace Voica;

/// <summary>
/// Localization (spec §12): UI in English or Russian by the system language, decided once at
/// startup. <see cref="S"/> exposes every user-facing string; format templates use {0}/{1}.
/// XAML uses these via {x:Static l:S.Name}; code-behind reads them directly.
/// </summary>
public static class Loc
{
    /// <summary>True if the system UI language is Russian.</summary>
    public static bool IsRussian { get; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the Russian or English variant.</summary>
    public static string T(string en, string ru) => IsRussian ? ru : en;
}

/// <summary>User-facing strings (spec §12). Plain values are ready to use; *Fmt values are templates.</summary>
public static class S
{
    // Tray menu
    public static string MenuDictate => Loc.T("Dictate", "Диктовать");
    public static string MenuSettings => Loc.T("Settings…", "Настройки…");
    public static string MenuHistory => Loc.T("History…", "История…");
    public static string MenuCheckUpdates => Loc.T("Check for Updates…", "Проверить обновления…");
    public static string MenuDownloadUpdateFmt => Loc.T("Download update {0}…", "Скачать обновление {0}…");
    public static string MenuAbout => Loc.T("About Voica", "О программе Voica");
    public static string MenuQuit => Loc.T("Quit", "Выход");

    // Tray tooltips
    public static string Tray => "Voica";
    public static string TrayRecording => Loc.T("Voica — recording…", "Voica — запись…");
    public static string TrayTranscribing => Loc.T("Voica — transcribing…", "Voica — расшифровка…");

    // Notices / errors (runtime)
    public static string NoticeInserted => Loc.T("Inserted (also copied to clipboard).", "Вставлено (также скопировано в буфер).");
    public static string NoticeNoSpeech => Loc.T("No speech recognized.", "Речь не распознана.");
    public static string ErrNoKey => Loc.T("No Groq API key set. Add it in Settings.", "Ключ Groq не задан. Укажите его в Настройках.");
    public static string ErrRecordingStartFmt => Loc.T("Couldn't start recording: {0}", "Не удалось начать запись: {0}");
    public static string ErrRecordingFailedFmt => Loc.T("Recording failed: {0}", "Ошибка записи: {0}");
    public static string ErrHotkeyFmt => Loc.T("Couldn't register the global hotkey: {0}", "Не удалось зарегистрировать хоткей: {0}");

    // Update alerts
    public static string UpdateAvailableAskFmt => Loc.T("Voica {0} is available. Open the download page?", "Доступна версия Voica {0}. Открыть страницу загрузки?");
    public static string UpdateUpToDateFmt => Loc.T("You're on the latest version ({0}).", "Установлена последняя версия ({0}).");
    public static string UpdateNoReleases => Loc.T("No releases have been published yet.", "Релизы ещё не опубликованы.");
    public static string UpdateErrorFmt => Loc.T("Couldn't check for updates: {0}", "Не удалось проверить обновления: {0}");

    // Groq messages (spec §2)
    public static string GroqRejected => Loc.T("Groq rejected the API key. Check it in Settings.", "Groq отклонил ключ. Проверьте его в Настройках.");
    public static string GroqTooLong => Loc.T("Recording is too long — split it into shorter parts.", "Запись слишком длинная — разбейте её на части.");
    public static string GroqRateLimit => Loc.T("Groq rate limit reached. Please wait and try again.", "Достигнут лимит Groq. Подождите и повторите.");
    public static string GroqReturnedFmt => Loc.T("Groq returned {0}: {1}", "Groq вернул {0}: {1}");
    public static string GroqTimeout => Loc.T("Groq request timed out. Please try again.", "Groq не ответил вовремя. Повторите.");
    public static string GroqNetworkFmt => Loc.T("Network error contacting Groq: {0}", "Сетевая ошибка при обращении к Groq: {0}");
    public static string GroqNoText => Loc.T("Groq response did not contain text.", "Ответ Groq не содержит текста.");
    public static string GroqParse => Loc.T("Could not parse Groq response.", "Не удалось разобрать ответ Groq.");
    public static string KeyValidValid => Loc.T("Key is valid.", "Ключ рабочий.");
    public static string KeyValidRejected => Loc.T("Key was rejected by Groq.", "Ключ отклонён Groq.");
    public static string KeyValidTimeout => Loc.T("Validation timed out.", "Проверка не ответила вовремя.");

    // Local engine (spec §2.5)
    public static string LblEngine => Loc.T("Recognition engine", "Движок распознавания");
    public static string EngineCloud => Loc.T("Cloud (Groq Whisper)", "Облако (Groq Whisper)");
    public static string EngineLocal => Loc.T("Local (offline, Russian)", "Локально (офлайн, русский)");
    public static string EngineHint => Loc.T(
        "Local runs entirely on this PC — no network, no API key (model: GigaAM v3, Russian with punctuation). The vocabulary hint works only with the cloud engine; AI term correction works with both.",
        "Локальный движок работает целиком на этом ПК — без сети и ключа (модель GigaAM v3, русский с пунктуацией). Подсказка-словарь работает только с облаком; ИИ-исправление — с обоими движками.");
    public static string ModelNotDownloadedFmt => Loc.T(
        "Local model is not downloaded ({0} MB). Cloud is used until it is.",
        "Локальная модель не скачана ({0} МБ). Пока используется облако.");
    public static string ModelDownloadedFmt => Loc.T("Local model is installed ({0} MB).", "Локальная модель установлена ({0} МБ).");
    public static string ModelDownloadingFmt => Loc.T("Downloading model… {0}%", "Скачиваю модель… {0}%");
    public static string ModelDownloadFailedFmt => Loc.T("Model download failed: {0}", "Не удалось скачать модель: {0}");
    public static string BtnDownloadModel => Loc.T("Download", "Скачать");
    public static string BtnDeleteModel => Loc.T("Delete model", "Удалить модель");
    public static string LocalPreparing => Loc.T("Preparing the local model…", "Готовлю локальную модель…");

    // Settings window
    public static string SettingsTitle => Loc.T("Voica — Settings", "Voica — Настройки");
    public static string LblDictationMode => Loc.T("Dictation mode", "Режим диктовки");
    public static string ModePtt => Loc.T("Push-to-talk (hold)", "Push-to-talk (удержание)");
    public static string ModeToggle => Loc.T("Toggle (press to start / stop)", "Toggle (нажать — старт/стоп)");
    public static string LblHotkey => Loc.T("Hotkey", "Хоткей");
    public static string BtnCustom => Loc.T("Custom…", "Свой…");
    public static string HotkeyCurrentFmt => Loc.T("Current: {0}", "Текущий: {0}");
    public static string HotkeyHint => Loc.T(
        "A single key is reserved for dictation while Voica runs. A combination (e.g. Ctrl+Shift+Space) only triggers when pressed together, so it won't break other shortcuts.",
        "Одиночная клавиша занимается под диктовку, пока Voica запущена. Комбинация (напр. Ctrl+Shift+Space) срабатывает только целиком и не ломает другие сочетания.");
    public static string LblOutput => Loc.T("Output", "Вывод");
    public static string OutputInsert => Loc.T("Insert into focused field", "Вставлять в активное поле");
    public static string OutputWindow => Loc.T("Show result window", "Показывать окно результата");
    public static string ChkStoreAudio => Loc.T("Store audio recordings", "Хранить аудиозаписи");
    public static string ChkNotify => Loc.T("Show a notification after inserting", "Показывать уведомление после вставки");
    public static string ChkCheckUpdates => Loc.T("Check for updates on launch", "Проверять обновления при запуске");
    public static string LblRetention => Loc.T("Delete audio older than", "Удалять аудио старше");
    public static string RetentionSuffix => Loc.T("days  (0 = keep forever)", "дней  (0 = хранить всегда)");
    public static string LblVocabulary => Loc.T("Vocabulary", "Словарь");
    public static string VocabHint => Loc.T(
        "Terms/names Whisper often gets wrong. A hint, not a hard replacement (kept to the last 800 characters).",
        "Слова/названия, которые Whisper часто коверкает. Подсказка, не жёсткая замена (учитываются последние 800 символов).");
    public static string VocabCounterFmt => "{0} / {1}";

    // AI term correction (spec §6.1)
    public static string ChkLlm => Loc.T("AI term correction (Groq LLM)", "ИИ-исправление терминов (Groq LLM)");
    public static string LlmHint => Loc.T(
        "After transcription, mangled vocabulary terms are fixed by a Groq chat model. The vocabulary sets the canonical spelling (including case). Works only when the vocabulary is not empty; if it fails, the original text is delivered.",
        "После распознавания искажённые термины из словаря исправляет chat-модель Groq. Словарь задаёт каноническое написание (включая регистр). Работает только при непустом словаре; при сбое доставляется исходный текст.");
    public static string LlmChecking => Loc.T("Checking model availability…", "Проверка доступности модели…");
    public static string LlmAvailable => Loc.T("✓ Model is available.", "✓ Модель доступна.");
    public static string LlmUnavailableFmt => Loc.T("✗ {0}", "✗ {0}");
    public static string LlmBlockedFmt => Loc.T(
        "Model {0} is blocked for your Groq org. Allow it at console.groq.com → Settings → Limits.",
        "Модель {0} заблокирована для вашей организации Groq. Разрешите её в console.groq.com → Settings → Limits.");
    public static string LlmNotFoundFmt => Loc.T(
        "Model {0} is unavailable (Groq may have renamed or removed it) — please update the app.",
        "Модель {0} недоступна (Groq мог переименовать или убрать её) — обновите приложение.");

    // Reset settings (spec §11)
    public static string BtnResetSettings => Loc.T("Reset settings…", "Сбросить настройки…");
    public static string ResetTitle => Loc.T("Reset settings?", "Сбросить настройки?");
    public static string ResetMsg => Loc.T(
        "Settings will return to their defaults. Your API key, history, audio, and vocabulary are kept.",
        "Настройки вернутся к значениям по умолчанию. Ключ, история, аудио и словарь сохранятся.");
    public static string ResetDone => Loc.T("Settings reset to defaults.", "Настройки сброшены к значениям по умолчанию.");
    public static string LblApiKey => Loc.T("Groq API key", "Ключ Groq API");
    public static string ChkShow => Loc.T("Show", "Показать");
    public static string BtnValidate => Loc.T("Validate", "Проверить");
    public static string BtnSave => Loc.T("Save", "Сохранить");
    public static string BtnDeleteAll => Loc.T("Delete all data…", "Удалить все данные…");
    public static string BtnClose => Loc.T("Close", "Закрыть");
    public static string KeySaved => Loc.T("A key is saved (encrypted with DPAPI).", "Ключ сохранён (шифрование DPAPI).");
    public static string KeyEnv => Loc.T("Using GROQ_API_KEY from the environment.", "Используется GROQ_API_KEY из окружения.");
    public static string KeyNone => Loc.T("No key set. Paste your Groq key and click Save.", "Ключ не задан. Вставьте ключ Groq и нажмите «Сохранить».");
    public static string KeyEnterValidate => Loc.T("Enter a key to validate.", "Введите ключ для проверки.");
    public static string KeyValidating => Loc.T("Validating…", "Проверка…");
    public static string KeyValidOk => Loc.T("✓ Key is valid.", "✓ Ключ рабочий.");
    public static string KeyInvalidFmt => Loc.T("✗ {0}", "✗ {0}");
    public static string KeyEnterSave => Loc.T("Enter a key to save.", "Введите ключ для сохранения.");
    public static string KeySavedNow => Loc.T("Key saved (encrypted with DPAPI).", "Ключ сохранён (шифрование DPAPI).");
    public static string AllDeleted => Loc.T("All data deleted. Settings reset to defaults.", "Все данные удалены. Настройки сброшены.");

    // History window
    public static string HistoryTitle => Loc.T("Voica — History", "Voica — История");
    public static string ColWhen => Loc.T("When", "Когда");
    public static string ColText => Loc.T("Text", "Текст");
    public static string ColLang => Loc.T("Lang", "Язык");
    public static string ColDur => Loc.T("Dur", "Длит.");
    public static string ColAudio => Loc.T("Audio", "Аудио");
    public static string BtnCopy => Loc.T("Copy", "Копировать");
    public static string BtnPlay => Loc.T("Play", "Играть");
    public static string BtnDelete => Loc.T("Delete", "Удалить");
    public static string BtnRefresh => Loc.T("Refresh", "Обновить");
    public static string HistEmpty => Loc.T("No transcriptions yet.", "Пока нет расшифровок.");
    public static string HistCountFmt => Loc.T("{0} transcription(s).", "Записей: {0}.");
    public static string HistCopied => Loc.T("Copied to clipboard.", "Скопировано в буфер.");
    public static string HistNoAudio => Loc.T("No audio for this record.", "Для этой записи нет аудио.");
    public static string HistPlaying => Loc.T("Playing…", "Воспроизведение…");
    public static string HistPlayFailFmt => Loc.T("Playback failed: {0}", "Ошибка воспроизведения: {0}");
    public static string HistDeleteConfirm => Loc.T("Delete this transcription (and its audio)?", "Удалить эту запись (и аудио)?");
    public static string HistDeleted => Loc.T("Deleted.", "Удалено.");

    // Result window
    public static string ResultCopied => Loc.T("Copied", "Скопировано");

    // Delete-data dialog
    public static string DeleteDataTitle => Loc.T("Voica — Delete all data", "Voica — Удалить все данные");
    public static string DeleteDataWarning => Loc.T(
        "This permanently deletes ALL transcriptions, audio recordings, your saved API key, and settings — and resets everything to defaults. This cannot be undone.",
        "Это безвозвратно удалит ВСЕ расшифровки, аудиозаписи, сохранённый ключ и настройки — и сбросит всё к значениям по умолчанию. Отменить нельзя.");
    public static string DeleteDataConfirmFmt => Loc.T("To confirm, type {0} below:", "Для подтверждения введите {0} ниже:");
    public static string BtnDeleteEverything => Loc.T("Delete everything", "Удалить всё");
    public static string BtnCancel => Loc.T("Cancel", "Отмена");

    // Hotkey capture dialog
    public static string CaptureTitle => Loc.T("Voica — Set hotkey", "Voica — Задать хоткей");
    public static string CaptureInstr => Loc.T(
        "Press a combination (e.g. Ctrl+Shift+Space), or a dedicated key like CapsLock. For a plain Right/Left Alt, use the dropdown instead. Press Esc to cancel.",
        "Нажмите сочетание (напр. Ctrl+Shift+Space) или выделенную клавишу вроде CapsLock. Для обычного Right/Left Alt используйте список. Esc — отмена.");
    public static string CaptureHintMainKey => Loc.T("…now press the main key.", "…теперь нажмите основную клавишу.");
    public static string CaptureHintNeedModifier => Loc.T(
        "That key needs a modifier (Ctrl/Alt/Shift/Win), or pick a dedicated key.",
        "Этой клавише нужен модификатор (Ctrl/Alt/Shift/Win), либо выберите выделенную клавишу.");

    // About window
    public static string AboutTitle => Loc.T("About Voica", "О программе Voica");
    public static string AboutTagline => Loc.T("Dictation to punctuated text via Groq Whisper.", "Диктовка → текст с пунктуацией через Groq Whisper.");
    public static string AboutPrivacy => Loc.T(
        "Privacy: no backend, no telemetry. Network is used only for Groq (transcription) and GitHub (optional update checks).",
        "Приватность: нет бэкенда и телеметрии. Сеть используется только для Groq (расшифровка) и GitHub (проверка обновлений).");
    public static string AboutVersionFmt => Loc.T("Version {0}", "Версия {0}");
}
