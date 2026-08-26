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

    // Dictation overlay (spec §4.2)
    public static string OverlayTranscribing => Loc.T("Transcribing…", "Распознаю…");
    public static string OverlayCancelTip => Loc.T("Cancel — discard the recording", "Отменить — запись выбрасывается");
    public static string OverlayStopTip => Loc.T("Stop and transcribe", "Стоп и распознать");
    public static string ChkOverlay => Loc.T(
        "Show a recording bar at the bottom of the screen",
        "Показывать плашку записи внизу экрана");
    public static string OverlayHint => Loc.T(
        "A floating capsule shows the level while recording, with buttons to cancel (discard the audio) or stop and transcribe. While it is on, the tray icon stays neutral; turn it off and the icon indicates the state instead.",
        "Плавающая капсула показывает уровень во время записи, с кнопками «отменить» (аудио выбрасывается) и «стоп и распознать». Пока она включена, иконка трея не меняется; выключите — состояние показывает иконка.");

    // Notices / errors (runtime)
    public static string NoticeInserted => Loc.T("Inserted (also copied to clipboard).", "Вставлено (также скопировано в буфер).");
    public static string NoticeNoSpeech => Loc.T("No speech recognized.", "Речь не распознана.");
    public static string NoticeCaptureLost => Loc.T(
        "The microphone stopped sending audio — transcribing what was recorded.",
        "Микрофон перестал отдавать звук — распознаю то, что успело записаться.");
    // Local engine chosen without its model (spec §2.5). The cloud is never used instead, so the
    // message has to name what is missing and lead straight to where it is fixed.
    public static string ErrModelMissing => Loc.T(
        "The local engine is selected, but its model is not installed. Download the GigaAM local model — or switch recognition back to the cloud."
        + "\n\nOpen Settings?",
        "Выбран локальный движок, но его модель не установлена. Скачайте локальную модель GigaAM — или переключите распознавание обратно на облако."
        + "\n\nОткрыть настройки?");
    // A model that arrived by hand can be truncated or from the wrong release; the checksums are
    // published in the README next to the manual-install instructions.
    public static string ErrModelCorrupt => Loc.T(
        "The local model files do not match their checksums. Delete the model in Settings → Data and install it again.",
        "Файлы локальной модели не совпадают с контрольными суммами. Удалите модель в Настройках → «Данные» и установите её заново.");

    // Network / proxy (spec §9.5). The address is the only thing interpolated: where it came from
    // is a separate line on the Network tab, so the message never mixes two languages.
    public static string ErrProxyAuthFmt => Loc.T(
        "Could not get through the proxy {0} — it wants credentials, or it is not answering.",
        "Не удалось пройти через прокси {0} — он требует авторизации либо не отвечает.");
    public static string NetTimeout => Loc.T("The request timed out.", "Запрос не ответил вовремя.");
    public static string ErrNoKey => Loc.T("No Groq API key set. Add it in Settings.", "Ключ Groq не задан. Укажите его в Настройках.");
    // Shown at the START of a dictation, like the missing model (§2.5): naming what is missing and
    // leading to where it is fixed, before anything has been said.
    public static string ErrNoKeyAsk => Loc.T(
        "The cloud engine is selected, but no Groq API key is set — there is nothing to transcribe with. Add your key — or switch recognition to the local engine, which needs no key."
        + "\n\nOpen Settings?",
        "Выбрано облако, но ключ Groq не задан — распознавать нечем. Укажите ключ — или переключите распознавание на локальный движок, которому ключ не нужен."
        + "\n\nОткрыть настройки?");
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
    public static string SttBlockedFmt => Loc.T(
        "Model {0} is blocked for your Groq org. Allow it at console.groq.com → Settings → Limits, or pick another model in Settings → Dictation.",
        "Модель {0} заблокирована для вашей организации Groq. Разрешите её в console.groq.com → Settings → Limits или выберите другую в Настройках → Dictation.");
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
    // Mirrors macOS 0.9.18: since §6.2/§6.3 the rules work on both engines, and the local one is
    // where they help most — the old wording made the vocabulary sound cloud-only.
    public static string EngineHint => Loc.T(
        "The local engine runs entirely on this PC: no internet or API key needed, punctuation included (model: GigaAM v3, Russian). Trade-offs: English words may come out in Cyrillic, and the vocabulary hint during recognition only works with the cloud engine. Terms are still fixed by rules — no key, no network — and that is where the rules help most.",
        "Локальный движок работает целиком на этом ПК: не нужны ни интернет, ни ключ, пунктуация — из коробки (модель GigaAM v3, русский). Особенности: английские слова могут записаться кириллицей, а словарь-подсказка при распознавании работает только с облаком. Термины при этом всё равно исправляются правилами — без ключа и сети, и пользы от них тут больше всего.");
    public static string ModelNotDownloadedFmt => Loc.T(
        "Local model is not downloaded ({0} MB). Cloud is used until it is.",
        "Локальная модель не скачана ({0} МБ). Пока используется облако.");
    public static string ModelDownloadedFmt => Loc.T("Local model is installed ({0} MB).", "Локальная модель установлена ({0} МБ).");
    public static string ModelDownloadingFmt => Loc.T("Downloading model… {0}%", "Скачиваю модель… {0}%");
    public static string ModelDownloadFailedFmt => Loc.T("Model download failed: {0}", "Не удалось скачать модель: {0}");
    public static string BtnDownloadModel => Loc.T("Download", "Скачать");
    public static string BtnCancelDownload => Loc.T("Cancel", "Отмена");
    public static string BtnDeleteModel => Loc.T("Delete model", "Удалить модель");
    public static string LocalPreparing => Loc.T("Preparing the local model…", "Готовлю локальную модель…");
    public static string NoticeOfflineFallback => Loc.T(
        "No network — transcribed with the local engine.",
        "Нет сети — распознано локальным движком.");

    // Settings window
    public static string SettingsTitle => Loc.T("Voica — Settings", "Voica — Настройки");
    public static string TabGeneral => Loc.T("General", "Общие");
    public static string TabDictation => Loc.T("Dictation", "Диктовка");
    public static string TabVocabulary => Loc.T("Vocabulary", "Словарь");
    public static string TabData => Loc.T("Data", "Данные");
    public static string TabNetwork => Loc.T("Network", "Сеть");
    public static string TabAbout => Loc.T("About", "О программе");

    // Network tab (spec §9.5/§11.4). The route line is deliberately separate from the error
    // message: the error names the ADDRESS, this line explains where that address came from.
    public static string LblProxy => Loc.T("Proxy", "Прокси");
    public static string ChkUseSystemProxy => Loc.T("Use the system proxy", "Использовать системный прокси");
    public static string ProxyHint => Loc.T(
        "On by default. Voica authenticates with your Windows sign-in, so a corporate proxy asking for credentials is answered without any password being typed or stored here. Turn it off to ignore the system settings and go straight out: a proxy left misconfigured in Windows blocks the app just as effectively as a missing one. The setting covers every request Voica makes — recognition, the model download and update checks.",
        "По умолчанию включено. Voica авторизуется учётными данными вашего входа в Windows, поэтому корпоративный прокси, требующий авторизации, получает ответ — пароль здесь не вводится и не хранится. Выключите, чтобы игнорировать системные настройки и ходить напрямую: криво прописанный в Windows прокси блокирует приложение не хуже отсутствующего. Настройка действует на все обращения Voica — распознавание, скачивание модели и проверку обновлений.");
    public static string ProxyRouteSystemFmt => Loc.T(
        "Requests go through the system proxy {0}.",
        "Запросы идут через системный прокси {0}.");
    public static string ProxyRouteForcedFmt => Loc.T(
        "Requests go through the proxy {0}, set for this run by {1}.",
        "Запросы идут через прокси {0}, заданный для этого запуска переменной {1}.");
    public static string ProxyRouteDirect => Loc.T(
        "Requests go straight out — Windows offers no proxy for these addresses.",
        "Запросы идут напрямую — Windows не предлагает прокси для этих адресов.");
    public static string ProxyRouteOff => Loc.T(
        "Requests go straight out — the system proxy is turned off here.",
        "Запросы идут напрямую — системный прокси здесь выключен.");
    public static string LblUpdates => Loc.T("Updates", "Обновления");
    public static string BtnCheckNow => Loc.T("Check now", "Проверить сейчас");
    public static string BtnDownloadUpdateFmt => Loc.T("Download {0}", "Скачать {0}");
    public static string BtnGitHub => Loc.T("GitHub", "GitHub");
    public static string BtnSupport => Loc.T("Support the project", "Поддержать проект");
    public static string SupportHint => Loc.T(
        "Voica is free and stays free — every feature, no subscription. Donations are optional.",
        "Voica бесплатна и останется бесплатной — все функции, без подписки. Донат — по желанию.");
    public static string AboutLicense => "© 2026 Ivan Ushakov · MIT License";
    public static string ModelDiskFmt => Loc.T("On disk: {0} MB.", "На диске: {0} МБ.");
    // Deleting the model is one click away from a 214 MB re-download, which in the network this
    // was built for (§9.5) is not a small thing to ask again. macOS confirms it too.
    public static string ModelDeleteTitle => Loc.T("Delete the local model?", "Удалить локальную модель?");
    public static string ModelDeleteAskFmt => Loc.T(
        "This frees {0} MB. You can download it again at any time."
        + "\n\nWhile it is missing the local engine has nothing to transcribe with, so dictation will refuse until you download it again or switch recognition to the cloud.",
        "Освободится {0} МБ. Скачать её снова можно в любой момент."
        + "\n\nПока модели нет, локальному движку распознавать нечем: диктовка будет отказывать, пока модель не скачана заново или распознавание не переключено на облако.");
    public static string LblDictationMode => Loc.T("Dictation mode", "Режим диктовки");
    public static string ModePtt => Loc.T("Push-to-talk (hold)", "Push-to-talk (удержание)");
    public static string ModeToggle => Loc.T("Toggle (press to start / stop)", "Toggle (нажать — старт/стоп)");
    public static string LblHotkey => Loc.T("Hotkey", "Хоткей");
    public static string BtnCustom => Loc.T("Custom…", "Свой…");
    public static string HotkeyCurrentFmt => Loc.T("Current: {0}", "Текущий: {0}");

    // Shown only when a bare Left Alt meets a system layout switch on Alt+Shift (spec §4).
    public static string HotkeyLayoutWarning => Loc.T(
        "⚠ The system switches the keyboard layout with Alt+Shift, and a bare hotkey is taken over entirely — the layout will stop switching. Take the right Alt or a combination.",
        "⚠ В системе раскладка переключается по Alt+Shift, а одиночная клавиша забирается целиком — раскладка переключаться перестанет. Возьмите правый Alt или сочетание.");
    public static string HotkeyHint => Loc.T(
        "A single key is reserved for dictation while Voica runs. A combination (e.g. Ctrl+Shift+Space) only triggers when pressed together, so it won't break other shortcuts.",
        "Одиночная клавиша занимается под диктовку, пока Voica запущена. Комбинация (напр. Ctrl+Shift+Space) срабатывает только целиком и не ломает другие сочетания.");
    // Cloud STT model / language (spec §2)
    public static string LblSttModel => Loc.T("Cloud model", "Облачная модель");
    public static string SttTurbo => Loc.T("whisper-large-v3-turbo (faster)", "whisper-large-v3-turbo (быстрее)");
    public static string SttLarge => Loc.T("whisper-large-v3 (more accurate)", "whisper-large-v3 (точнее)");
    public static string LblLanguage => Loc.T("Language", "Язык");
    public static string LangAuto => Loc.T("Auto-detect", "Определять автоматически");
    public static string LangRu => Loc.T("Russian", "Русский");
    public static string LangEn => Loc.T("English", "Английский");
    public static string SttHint => Loc.T(
        "Auto-detect covers about a hundred languages, not just these two — Russian and English are listed separately only because forcing one helps with short phrases that auto-detect reads wrong. Applies to the cloud engine only (the local engine is Russian-only).",
        "Автоопределение понимает около сотни языков, а не только эти два — русский и английский вынесены отдельно лишь потому, что жёсткий выбор помогает с короткими фразами, где автоопределение ошибается. Действует только для облачного движка (локальный — русскоязычный).");

    public static string ChkDoubleTap => Loc.T("Double-tap to start", "Двойной тап — старт");
    public static string LblCloudSection => Loc.T("Cloud recognition", "Облачное распознавание");
    public static string DoubleTapHint => Loc.T(
        "In Toggle mode, recording starts only on two quick presses — a stray press won't begin a dictation. Stopping is always a single press. May clash with other apps' shortcuts; doesn't affect push-to-talk or the tray's Dictate item.",
        "В режиме Toggle запись начинается только двумя быстрыми нажатиями — случайное нажатие не запустит диктовку. Остановка — всегда одно нажатие. Может конфликтовать с шорткатами других приложений; на PTT и пункт «Dictate» не влияет.");

    public static string LblOutput => Loc.T("Output", "Вывод");
    public static string OutputInsert => Loc.T("Insert into focused field", "Вставлять в активное поле");
    public static string OutputWindow => Loc.T("Show result window", "Показывать окно результата");
    public static string ChkStoreAudio => Loc.T("Store audio recordings", "Хранить аудиозаписи");
    public static string ChkNotify => Loc.T("Show a notification after inserting", "Показывать уведомление после вставки");
    public static string ChkCheckUpdates => Loc.T("Check for updates on launch", "Проверять обновления при запуске");
    public static string LblRetention => Loc.T("Delete audio older than", "Удалять аудио старше");
    public static string RetentionSuffix => Loc.T("days  (0 = keep forever)", "дней  (0 = хранить всегда)");
    public static string LblVocabulary => Loc.T("Vocabulary", "Словарь");
    // Wording mirrors macOS 0.9.15 (`settings.vocab.hint`): since §6.2 the vocabulary works on its
    // own — rules first, no key and no network — and the prompt hint is the cloud's extra.
    public static string VocabHint => Loc.T(
        "Terms speech recognition often mishears — names, jargon, anglicisms. E.g.: Kubernetes, kubectl, Groq, ЕИС, оферта. Voica pulls garbled spellings back to the ones listed here by rule — no key, no internet, both engines. The cloud engine also gets the list as a hint during recognition (not a strict rule); the local engine can't take one.",
        "Слова, которые распознавание часто коверкает — названия, жаргон, англицизмы. Напр.: Kubernetes, kubectl, Groq, ЕИС, оферта. Искажённые написания Voica подтягивает к словарным по правилам: без ключа, без интернета, на обоих движках. Облачному движку список вдобавок уходит подсказкой прямо при распознавании (не жёсткое правило); локальный подсказку принять не может.");
    public static string VocabCounterFmt => "{0} / {1}";

    // AI term correction (spec §6.1)
    // Text clean-up (spec §6.3/§6.4) — rules that change words, each with its own switch.
    public static string LblCleanup => Loc.T("Text clean-up", "Очистка текста");
    public static string ChkFillers => Loc.T(
        "Remove \"uh\", \"um\", \"hmm\"",
        "Убирать «э-э-э», «ммм», «хмм»");
    public static string FillersHint => Loc.T(
        "Drawn-out sounds that mean nothing in speech but clutter the text. Removed by rules, no network needed. Real words that were merely drawn out are straightened rather than dropped. Turn this off if you transcribe speech verbatim.",
        "Тянущиеся звуки, которые в речи не значат ничего, а в тексте мешают. Убираются по правилам, без сети. Растянутые настоящие слова не удаляются, а распрямляются: «ну-у-у» → «ну». Выключите, если расшифровываете речь дословно.");
    public static string ChkQuotes => Loc.T("Fix quotation marks", "Чинить кавычки");
    public static string QuotesHint => Loc.T(
        "Straight quotes become proper guillemets, unpaired ones are removed, and a missing space after a colon is restored. Recognition places quotes however it happens to — one sentence can hold both kinds. English text is left alone.",
        "Прямые кавычки заменяются ёлочками, непарные убираются, восстанавливается пробел после двоеточия. Распознавание ставит кавычки как придётся — в одной фразе встречаются и «ёлочки», и \"прямые\". Английский текст не трогается.");

    // Term rules (spec §6.2) — the switch sits above the AI pass: first what always works and
    // costs nothing, then the optional extra.
    public static string ChkTermRules => Loc.T("Fix terms by rules", "Исправлять термины правилами");
    public static string TermRulesHint => Loc.T(
        "Garbled spellings are pulled back to the ones you listed, right on this PC: no key, no internet, both engines. The rules stay quiet when unsure. Turn this off if they ever get one of your words wrong.",
        "Искажённые написания подтягиваются к словарным прямо на этом ПК: без ключа, без интернета, на обоих движках. Правила осторожны — если слово ни на что не похоже, они его не трогают. Выключите, если правило когда-нибудь ошибётся на ваших словах.");

    // First run without a key (spec §11.3): the key field is where the cursor is, so the way out
    // is said right there — and deliberately without a button.
    public static string KeyNoKeyHint => Loc.T(
        "No key? It's only needed for the cloud engine. Switch recognition to \"Local (offline)\" above — it runs with no key and no internet.",
        "Ключа нет? Он нужен только облачному движку. Переключите распознавание на «Локально (офлайн)» выше — оно работает без ключа и без интернета.");

    public static string ChkLlm => Loc.T(
        "Fix terms with AI (extra Groq request)",
        "Исправлять термины через ИИ (доп. запрос к Groq)");
    public static string LlmHint => Loc.T(
        "An extra pass on top of the rules: a Groq language model handles what rules cannot — grammatical case and badly garbled terms. Adds ~1–2 s and needs the API key and internet. If the request fails you keep the text the rules produced, so it never makes things worse. Works with both engines.",
        "Дополнительный проход поверх правил: языковая модель Groq разбирает то, что правилам не под силу — согласует падеж и узнаёт сильно искажённые термины. Добавляет ~1–2 с, нужны ключ и интернет. Если запрос не удался — остаётся текст после правил, хуже не станет. Работает с обоими движками.");
    public static string LlmChecking => Loc.T("Checking model availability…", "Проверка доступности модели…");
    public static string LlmAvailable => Loc.T("✓ Model is available.", "✓ Модель доступна.");
    public static string LlmAvailableFmt => Loc.T("✓ Model is available: {0}", "✓ Модель доступна: {0}");
    public static string LlmSwitchedFmt => Loc.T(
        "Selected model is unavailable — switched to {0}.",
        "Выбранная модель недоступна — переключились на {0}.");
    public static string LlmNoModels => Loc.T(
        "No suitable chat model is available for this key.",
        "Для этого ключа нет подходящей chat-модели.");
    public static string LblChatModel => Loc.T("Correction model", "Модель исправления");
    public static string ChatModelAuto => Loc.T("Recommended (automatic)", "Рекомендуемая (автоматически)");
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
    // The saved key is gone, but the development fallback (§9) can still supply one, and a filled
    // key field right after "everything is deleted" reads as a deletion that did not happen.
    public static string AllDeletedEnvKey => Loc.T(
        "The stored key was deleted, but GROQ_API_KEY is still set in your environment and is being used. Voica does not change Windows environment variables — clear it there if you want it gone.",
        "Сохранённый ключ удалён, но в вашем окружении задана переменная GROQ_API_KEY, и используется она. Переменные окружения Windows Voica не меняет — уберите её там, если она не нужна.");

    // History window
    public static string HistoryTitle => Loc.T("Voica — History", "Voica — История");
    public static string ColWhen => Loc.T("When", "Когда");
    public static string ColText => Loc.T("Text", "Текст");
    public static string ColLang => Loc.T("Lang", "Язык");
    public static string ColDur => Loc.T("Dur", "Длит.");
    public static string ColModel => Loc.T("Model", "Модель");
    public static string ColAudio => Loc.T("Audio", "Аудио");
    public static string BtnCopy => Loc.T("Copy", "Копировать");
    public static string BtnPlay => Loc.T("Play", "Играть");
    public static string BtnStop => Loc.T("Stop", "Стоп");
    public static string BtnDelete => Loc.T("Delete", "Удалить");
    public static string BtnRefresh => Loc.T("Refresh", "Обновить");
    public static string BtnExport => Loc.T("Export…", "Экспорт…");
    public static string ExportTitle => Loc.T("Export history", "Экспорт истории");
    public static string ExportFilters => Loc.T(
        "Markdown (*.md)|*.md|CSV (*.csv)|*.csv|JSON (*.json)|*.json",
        "Markdown (*.md)|*.md|CSV (*.csv)|*.csv|JSON (*.json)|*.json");
    public static string ExportDoneFmt => Loc.T("Exported {0} record(s).", "Экспортировано записей: {0}.");
    public static string ExportFailedFmt => Loc.T("Export failed: {0}", "Ошибка экспорта: {0}");
    public static string HistEmpty => Loc.T("No transcriptions yet.", "Пока нет расшифровок.");
    public static string HistCountFmt => Loc.T("{0} transcription(s).", "Записей: {0}.");

    // Search over the history (spec §7). An empty history and an empty result are different
    // states and must not share a message.
    public static string HistSearch => Loc.T("Search text", "Поиск по тексту");
    public static string HistSearchNone => Loc.T("Nothing found", "Ничего не найдено");
    public static string HistSearchMatchesFmt => Loc.T("matches: {0}", "совпадений: {0}");
    public static string HistSearchInRaw => Loc.T("found in the original text", "найдено в исходном тексте");
    public static string HistSearchRawPrefix => Loc.T("Original, before fixing:", "Исходно, до исправления:");
    public static string HistCopied => Loc.T("Copied to clipboard.", "Скопировано в буфер.");
    public static string HistNoAudio => Loc.T("No audio for this record.", "Для этой записи нет аудио.");
    public static string HistPlaying => Loc.T("Playing…", "Воспроизведение…");
    public static string HistPlayFailFmt => Loc.T("Playback failed: {0}", "Ошибка воспроизведения: {0}");
    public static string HistDeleteConfirm => Loc.T("Delete this transcription (and its audio)?", "Удалить эту запись (и аудио)?");
    public static string HistDeleteManyConfirmFmt => Loc.T(
        "Delete {0} transcriptions (and their audio)?",
        "Удалить записей: {0} (вместе с аудио)?");
    public static string HistDeleted => Loc.T("Deleted.", "Удалено.");
    public static string HistDeletedManyFmt => Loc.T("Deleted {0} record(s).", "Удалено записей: {0}.");
    public static string HistSelectedFmt => Loc.T("{0} selected.", "Выделено: {0}.");

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
    public static string AboutTagline => Loc.T(
        "Voice dictation with punctuation — Groq Whisper in the cloud or GigaAM on this PC.",
        "Диктовка с пунктуацией — Groq Whisper в облаке или GigaAM на этом ПК.");
    public static string AboutPrivacy => Loc.T(
        "Privacy: no backend, no telemetry. Network is used only for Groq (cloud transcription / AI correction) and GitHub (update checks, one-time model download). With the local engine, audio never leaves this PC.",
        "Приватность: нет бэкенда и телеметрии. Сеть — только Groq (облачная расшифровка / ИИ-исправление) и GitHub (обновления, разовая докачка модели). С локальным движком аудио не покидает этот ПК.");
    public static string AboutVersionFmt => Loc.T("Version {0}", "Версия {0}");
}
