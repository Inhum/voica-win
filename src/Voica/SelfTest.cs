using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Voica;

/// <summary>
/// Self-test without network or GUI (spec §12): <c>Voica.exe --test-all</c>.
/// Covers pure logic and restores any mutated state. Grows with each phase.
/// </summary>
public static class SelfTest
{
    public static bool Run()
    {
        int passed = 0, failed = 0;
        void Check(string name, bool cond)
        {
            if (cond) { passed++; Console.WriteLine($"  [+] {name}"); }
            else { failed++; Console.WriteLine($"  [-] {name}"); }
        }

        Console.WriteLine("Voica self-test");

        // The suite mutates the real settings file, so take a whole-settings snapshot and put it
        // back whatever happens — restoring field by field is what let AI correction get switched
        // off behind the user's back.
        var settingsSnapshot = Prefs.Snapshot();
        try
        {

        // --- AppInfo / version ---
        Check("version parses to 3+ components",
            AppInfo.Version.Split('.').Length >= 3 && AppInfo.Version != "0.0.0");
        Check("repo target is voica-win",
            AppInfo.RepoOwner == "Inhum" && AppInfo.RepoName == "voica-win");

        // --- Localization (spec §12) ---
        Check("loc picks language", Loc.T("en", "ru") == (Loc.IsRussian ? "ru" : "en"));
        Check("loc strings present", !string.IsNullOrEmpty(S.MenuSettings) && !string.IsNullOrEmpty(S.SettingsTitle));

        // --- Paths ---
        Check("data dir under APPDATA\\Voica",
            Paths.DataDir.EndsWith("Voica", StringComparison.OrdinalIgnoreCase));
        Check("audio dir under data dir",
            Paths.AudioDir.StartsWith(Paths.DataDir, StringComparison.OrdinalIgnoreCase));

        // --- Groq constants ---
        // --- STT model / language selection (spec §2) ---
        Check("groq default stt model", GroqClient.DefaultSttModel == "whisper-large-v3-turbo");
        Check("stt model list", GroqClient.SttModels.Length == 2
            && GroqClient.SttModels[0] == "whisper-large-v3-turbo" && GroqClient.SttModels[1] == "whisper-large-v3");
        Check("stt model normalize", GroqClient.NormalizeSttModel("whisper-large-v3") == "whisper-large-v3"
            && GroqClient.NormalizeSttModel("qwen-nope") == GroqClient.DefaultSttModel
            && GroqClient.NormalizeSttModel(null) == GroqClient.DefaultSttModel);
        Check("language list and normalize",
            GroqClient.Languages.Length == 3 && GroqClient.Languages[0] == "auto"
            && GroqClient.NormalizeLanguage("ru") == "ru" && GroqClient.NormalizeLanguage("xx") == "auto");

        var savedStt = Prefs.SttModel; var savedLang = Prefs.Language;
        Prefs.SttModel = "whisper-large-v3"; Prefs.Language = "en";
        Check("prefs stt/language round-trip", Prefs.SttModel == "whisper-large-v3" && Prefs.Language == "en");
        Prefs.SttModel = "bogus"; Prefs.Language = "bogus";
        Check("prefs stt/language reject invalid",
            Prefs.SttModel == GroqClient.DefaultSttModel && Prefs.Language == "auto");
        Prefs.SttModel = savedStt; Prefs.Language = savedLang;
        Check("groq endpoint host", GroqClient.Endpoint.Host == "api.groq.com");
        Check("groq models endpoint host", GroqClient.ModelsEndpoint.Host == "api.groq.com");

        // --- Vocabulary → prompt (spec §6) ---
        Check("prompt empty → null", GroqClient.PromptField("   \n ") is null);
        Check("prompt null → null", GroqClient.PromptField(null) is null);
        Check("prompt trims", GroqClient.PromptField("  Kubernetes, Groq  ") == "Kubernetes, Groq");
        var longVocab = string.Concat(System.Linq.Enumerable.Repeat("term ", 500)); // ~2500 chars
        var prepared = GroqClient.PromptField(longVocab);
        Check("prompt truncated to budget", prepared is not null && prepared.Length <= GroqClient.PromptCharBudget);
        Check("prompt keeps tail",
            prepared is not null && longVocab.Trim().EndsWith(prepared, StringComparison.Ordinal));

        // --- Deterministic term fixing (spec §6.2) ---
        // Fixtures frozen from the methodology run of 2026-08-20: the rules were driven over the
        // whole real history (24 dictations, 0 changes — plain Russian business speech) plus this
        // hand-written trap set. Both sides are mandatory and the negatives matter more: a miss is
        // picked up by the LLM right after, a corrupted word is not.
        const string vocab62 = "Claude Code, Cowork, ChatGPT, Voica, focus-radio, ЕИС, оферта, "
                             + "Groq, API, GigaAM, Tailscale, app-connector, exit-node, DeepSeek";
        string Fix(string s) => TermFix.Apply(s, vocab62);
        bool Untouched(string s) => Fix(s) == s;

        Check("skeleton drops vowels and folds c to k, but not q",
            TermFix.Skeleton("DeepSeek") == "dpsk" && TermFix.Skeleton("Claude Code") == "kldkd"
            && TermFix.Skeleton("Greek") == "grk" && TermFix.Skeleton("Groq") == "grq");
        Check("skeleton transliterates cyrillic to the same shape",
            TermFix.Skeleton("Dпсик") == "dpsk" && TermFix.Skeleton("Dpсиcк") == "dpsk"
            && TermFix.Skeleton("клодкод") == "kldkd");
        Check("similarity is 1 - levenshtein / max length",
            Math.Abs(TermFix.Similarity("deepsc", "deepseek") - 0.625) < 0.001
            && Math.Abs(TermFix.Similarity("greek", "groq") - 0.4) < 0.001
            && Math.Abs(TermFix.Similarity("vice", "voica") - 0.6) < 0.001);

        Check("mixed alphabet is fixed on the skeleton alone",
            Fix("Открой Dпсик и посмотри ответ.") == "Открой DeepSeek и посмотри ответ."
            && Fix("Через Dpсиcк я проверил.") == "Через DeepSeek я проверил.");
        // Mixed alphabet is proof of mangling, so one consonant may drift (0.8 here).
        Check("mixed alphabet tolerates one consonant off the skeleton",
            Fix("Спроси у Dпсиcт про это.") == "Спроси у DeepSeek про это.");
        Check("latin candidate needs letter closeness too",
            Fix("Модель Deepsc отвечает быстро.") == "Модель DeepSeek отвечает быстро.");
        Check("cyrillic candidate needs an exact skeleton",
            Fix("Надо чатгпт спросить.") == "Надо ChatGPT спросить."
            && Fix("Дипсик и Клод отвечают.") == "DeepSeek и Клод отвечают.");
        Check("window glues one word into a two-word term and back",
            Fix("Запусти клодкод в терминале.") == "Запусти Claude Code в терминале."
            && Fix("Он сказал: клод код лучше.") == "Он сказал: Claude Code лучше."
            && Fix("Поставь Tail scale на ноутбук.") == "Поставь Tailscale на ноутбук."
            && Fix("Проверь апп-коннектор.") == "Проверь app-connector.");
        Check("punctuation and spacing around the word survive",
            Fix("Открой Dпсик, потом — Deepsc!") == "Открой DeepSeek, потом — DeepSeek!");

        // Traps: ordinary Russian speech that sounds like a term. Live ones from the spec plus
        // `vice versa`, which only the corpus run found.
        Check("traps: sound-alikes are left alone",
            Untouched("Вика прислала кода на 200 строк.") && Untouched("Папа звонил утром.")
            && Untouched("Усы у него длинные.") && Untouched("Депеша пришла вчера.")
            && Untouched("Колодка тормозная стёрлась."));
        Check("traps: short skeletons and plain english are left alone",
            Untouched("The Greek alphabet is old.") && Untouched("Это работает vice versa.")
            && Untouched("Локальный движок гигаам скачивается."));
        // Why q is not folded into k: Grok is a product of its own, and on a folded skeleton it
        // would match Groq at a letter closeness of 0.75.
        Check("traps: Grok stays Grok", Untouched("Спросил у Grok вчера."));
        Check("russian terms are never substituted",
            Untouched("Отправь аферту сегодня.") && Untouched("Контракт с ЕИС города Радужный."));
        Check("the window never eats a neighbour",
            Untouched("Я работаю в Cowork каждый день.") && Untouched("Пиши в Voica сегодня.")
            && Fix("Дипсик и Клод.") == "DeepSeek и Клод.");
        Check("no vocabulary, no rules",
            TermFix.Apply("Открой Dпсик.", "") == "Открой Dпсик."
            && TermFix.Apply("Открой Dпсик.", null) == "Открой Dпсик.");
        Check("vocabulary splits on commas and newlines",
            TermFix.ParseTerms(" Groq ,\n DeepSeek \n\n Groq ").SequenceEqual(new[] { "Groq", "DeepSeek" }));

        // --- History search (spec §7) ---
        var searchRows = new[]
        {
            new Transcription(1, DateTimeOffset.UnixEpoch, "Запусти Claude Code в терминале.", null, null, null, null, "Запусти клодкод в терминале."),
            new Transcription(2, DateTimeOffset.UnixEpoch, "Отправь оферту сегодня.", null, null, null, null, null),
        };
        Check("search matches the shown text, case-insensitively",
            HistorySearch.Filter(searchRows, "claude code").Count == 1
            && HistorySearch.Filter(searchRows, "ОФЕРТУ").Single().Id == 2);
        // People remember what they SAID, not what the rules and the model made of it (spec §7).
        Check("search also matches the engine's raw text",
            HistorySearch.Filter(searchRows, "клодкод").Single().Id == 1);
        Check("empty query keeps the whole history, misses return nothing",
            HistorySearch.Filter(searchRows, "  ").Count == 2
            && HistorySearch.Filter(searchRows, null).Count == 2
            && HistorySearch.Filter(searchRows, "тайлскейл").Count == 0);
        // Finding the record is half the job on a long dictation — the place inside it has to show.
        var ranges = HistorySearch.MatchRanges("Claude Code и ещё раз claude code", "Claude");
        Check("match ranges cover every occurrence, case-insensitively",
            ranges.Count == 2 && ranges[0] == (0, 6) && ranges[1] == (22, 6));
        Check("match ranges are empty without a query",
            HistorySearch.MatchRanges("текст", "  ").Count == 0
            && HistorySearch.MatchRanges(null, "текст").Count == 0
            && HistorySearch.MatchRanges("текст", "нет").Count == 0);

        // The metadata line under the record's text: empty parts are skipped, not shown blank.
        var metaParts = HistoryFormat.MetaLine(
            new Transcription(1, DateTimeOffset.UnixEpoch, "t", "ru", 3.25, null, "gigaam-v3")).Split(" · ");
        Check("meta line joins what the record has",
            metaParts.Length == 4 && metaParts[1] == "ru" && metaParts[3] == "gigaam-v3"
            && !HistoryFormat.MetaLine(new Transcription(1, DateTimeOffset.UnixEpoch, "t", null, null, null, "  "))
                .Contains('·'));

        Check("a raw-only match is reported as such",
            HistorySearch.MatchedOnlyInRaw(searchRows[0], "клодкод")
            && !HistorySearch.MatchedOnlyInRaw(searchRows[0], "Claude")
            && !HistorySearch.MatchedOnlyInRaw(searchRows[1], "оферту")
            && !HistorySearch.MatchedOnlyInRaw(searchRows[0], ""));

        // --- LLM post-processing prompt (spec §6.1) ---
        Check("chat endpoint host", GroqClient.ChatEndpoint.Host == "api.groq.com");

        // --- Dynamic chat-model resolution / self-healing (spec §6.1) ---
        Check("chat denylist filters non-chat",
            !ChatModels.IsChatModel("whisper-large-v3") && !ChatModels.IsChatModel("playai-tts")
            && !ChatModels.IsChatModel("meta-llama/llama-guard-4-12b")
            && !ChatModels.IsChatModel("distil-whisper-large-v3-en")
            && ChatModels.IsChatModel("openai/gpt-oss-120b"));
        // Groq's agentic systems are not chat models for our purposes (spec §6.1).
        Check("chat denylist filters compound",
            !ChatModels.IsChatModel("groq/compound") && !ChatModels.IsChatModel("compound-mini"));
        // allam sorts first alphabetically, so without this it becomes the last-resort pick for
        // correcting Russian terms. The substring must not swallow the meta-llama family.
        Check("chat denylist filters allam but spares meta-llama",
            !ChatModels.IsChatModel("allam-2-7b")
            && ChatModels.IsChatModel("meta-llama/llama-4-scout-17b-16e-instruct")
            && ChatModels.IsChatModel("llama-3.1-8b-instant"));
        Check("chat resolve never falls back to allam",
            ChatModels.Resolve(new[] { "allam-2-7b", "some-new-model" }, ChatModels.Auto) == "some-new-model"
            && ChatModels.Resolve(new[] { "allam-2-7b" }, ChatModels.Auto) is null);
        Check("chat chain and seed hold only live models",
            ChatModels.Seed == "openai/gpt-oss-120b"
            && ChatModels.PriorityChain[0] == ChatModels.Seed
            && !ChatModels.PriorityChain.Contains("llama-3.3-70b-versatile")
            && !ChatModels.PriorityChain.Contains("gemma2-9b-it")
            && ChatModels.PriorityChain.All(ChatModels.IsChatModel));
        Check("chat resolve prefers priority chain",
            ChatModels.Resolve(new[] { "llama-3.1-8b-instant", "openai/gpt-oss-20b", "openai/gpt-oss-120b" }, ChatModels.Auto)
                == "openai/gpt-oss-120b");
        Check("chat resolve honours explicit choice",
            ChatModels.Resolve(new[] { "openai/gpt-oss-20b", "openai/gpt-oss-120b" }, "openai/gpt-oss-20b") == "openai/gpt-oss-20b");
        Check("chat resolve drops retired choice",
            ChatModels.Resolve(new[] { "openai/gpt-oss-20b" }, "qwen/qwen3-32b") == "openai/gpt-oss-20b");
        Check("chat resolve falls back to first live",
            ChatModels.Resolve(new[] { "some-new-model" }, ChatModels.Auto) == "some-new-model");
        Check("chat resolve null when nothing usable",
            ChatModels.Resolve(new[] { "whisper-large-v3", "playai-tts" }, ChatModels.Auto) is null);
        Check("chat choiceRetired detects gone model",
            ChatModels.ChoiceRetired(new[] { "openai/gpt-oss-20b" }, "llama-3.3-70b-versatile")
            && !ChatModels.ChoiceRetired(new[] { "openai/gpt-oss-20b" }, ChatModels.Auto));

        var savedChat = Prefs.ChatModel; var savedResolved = Prefs.ResolvedChatModel;
        Prefs.ChatModel = "gemma2-9b-it";
        Check("prefs chatModel round-trip and active", Prefs.ChatModel == "gemma2-9b-it" && Prefs.ActiveChatModel == "gemma2-9b-it");
        Prefs.ChatModel = "qwen/qwen3-32b";   // retired → migrated to auto on read
        Check("prefs migrates retired chat model", Prefs.ChatModel == ChatModels.Auto);
        Prefs.ChatModel = "llama-3.3-70b-versatile";   // withdrawn by Groq 2026-08-16
        Prefs.ResolvedChatModel = "llama-3.3-70b-versatile";
        Check("prefs migrates the withdrawn llama choice and its cached resolution",
            Prefs.ChatModel == ChatModels.Auto && Prefs.ResolvedChatModel == ChatModels.Seed
            && Prefs.ActiveChatModel == ChatModels.Seed);
        Prefs.ChatModel = ChatModels.Auto; Prefs.ResolvedChatModel = "openai/gpt-oss-120b";
        Check("prefs active uses cached resolution", Prefs.ActiveChatModel == "openai/gpt-oss-120b");
        Prefs.ChatModel = savedChat; Prefs.ResolvedChatModel = savedResolved;
        Check("postprocess prompt null on empty vocab",
            GroqClient.PostProcessPromptText("текст", "  \n ") is null);
        var ppPrompt = GroqClient.PostProcessPromptText("привет кубер стил", "kubectl, Kubernetes");
        Check("postprocess prompt contains vocab and text",
            ppPrompt is not null && ppPrompt.Contains("СЛОВАРЬ: kubectl, Kubernetes")
            && ppPrompt.Contains("ТЕКСТ: привет кубер стил"));

        // --- Reasoning cleanup and the plausibility guard (spec §6.1) ---
        Check("strip removes a plain think block",
            GroqClient.StripReasoning("<think>размышляю</think>готовый текст") == "готовый текст");
        Check("strip is case-insensitive and takes attributes",
            GroqClient.StripReasoning("<Think type=\"x\">хм</THINK> текст") == "текст");
        Check("strip handles several blocks and newlines inside",
            GroqClient.StripReasoning("<think>раз\nдва</think>а<think>ещё\nблок</think>б") == "аб");
        // An unclosed tag means max_completion_tokens cut the answer mid-thought (spec §6.1).
        Check("strip drops everything after an unclosed tag",
            GroqClient.StripReasoning("текст <think>думаю и думаю") == "текст");
        Check("strip leaves an ordinary answer alone",
            GroqClient.StripReasoning("  привет kubectl  ") == "привет kubectl");
        // Boundary is measured off the original, not a hardcoded count (spec §6.1: len * 2 + 50).
        var guardSource = "коротко";
        Check("plausibility rejects empty and rambling answers",
            !GroqClient.IsPlausibleCorrection("исходный текст", "")
            && !GroqClient.IsPlausibleCorrection(guardSource, new string('x', guardSource.Length * 2 + 51)));
        Check("plausibility accepts a normal correction and the exact limit",
            GroqClient.IsPlausibleCorrection("привет кубер стил", "привет kubectl")
            && GroqClient.IsPlausibleCorrection(guardSource, new string('x', guardSource.Length * 2 + 50)));

        var savedLlm = Prefs.LlmPostProcess;
        Prefs.LlmPostProcess = !savedLlm;
        Check("prefs llmPostProcess round-trip", Prefs.LlmPostProcess == !savedLlm);
        Prefs.LlmPostProcess = savedLlm;

        // --- Reset-settings semantics (spec §11): vocabulary is user content, survives reset ---
        // Reset wipes the chat model and its cached resolution too, so snapshot both — otherwise
        // running the self-test silently drops a model the user picked by hand (spec §6.1).
        var rsChat = Prefs.ChatModel; var rsResolved = Prefs.ResolvedChatModel;
        var rsVocab = Prefs.Vocabulary; var rsLlm = Prefs.LlmPostProcess; var rsDays = Prefs.RetentionDays;
        Prefs.Vocabulary = "__voica_reset_test__"; Prefs.LlmPostProcess = true; Prefs.RetentionDays = 7;
        var keepVocab = Prefs.Vocabulary;
        Prefs.Reset(); Prefs.Vocabulary = keepVocab;   // the reset-settings flow
        Check("reset-settings keeps vocabulary, resets the rest",
            Prefs.Vocabulary == "__voica_reset_test__" && !Prefs.LlmPostProcess && Prefs.RetentionDays == 30);
        Prefs.Vocabulary = rsVocab; Prefs.LlmPostProcess = rsLlm; Prefs.RetentionDays = rsDays;
        Prefs.ChatModel = rsChat; Prefs.ResolvedChatModel = rsResolved;

        // --- Local engine (spec §2.5): pure logic, no model file needed ---
        Check("mel frame count", MelFrontend.FrameCount(16000) == (16000 - 320) / 160 + 1);
        Check("mel too-short is zero frames", MelFrontend.FrameCount(100) == 0);
        var sine = new float[16000];
        for (int i = 0; i < sine.Length; i++) sine[i] = (float)Math.Sin(2 * Math.PI * 440 * i / 16000.0);
        var melOut = MelFrontend.Compute(sine);
        bool melFinite = true;
        for (int m = 0; m < MelFrontend.NMels && melFinite; m++)
            for (int t = 0; t < melOut.GetLength(1) && melFinite; t++)
                melFinite = float.IsFinite(melOut[m, t]);
        Check("mel output shape and finite",
            melOut.GetLength(0) == 64 && melOut.GetLength(1) == MelFrontend.FrameCount(16000) && melFinite);

        var testVocab = LocalEngine.ParseVocab(new[] { "<unk> 0", "▁ 1", "п 2", "ри 3", "вет 4", "<blk> 256" });
        Check("vocab parse", testVocab.Count == 6 && testVocab[4] == "вет" && testVocab[256] == "<blk>");

        // CTC decode: blank-collapsed sequence "▁ п ри вет" → "привет"
        var ctcLogits = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new[] { 1, 6, 257 });
        int[] frameIds = { 1, 256, 2, 2, 3, 4 };   // ▁, blank, п, п(repeat), ри, вет
        for (int t = 0; t < frameIds.Length; t++) ctcLogits[0, t, frameIds[t]] = 10f;
        Check("ctc greedy decode", LocalEngine.CtcGreedyDecode(ctcLogits, testVocab) == "привет");

        var chunks = LocalEngine.Chunks(1_000_000, 400_000).ToArray();
        Check("chunking splits correctly",
            chunks.Length == 3 && chunks[0] == (0, 400_000) && chunks[2] == (800_000, 200_000));

        Check("stitch drops overlapping words",
            LocalEngine.StitchOverlap("привет как дела", "как дела друзья") == "привет как дела друзья");
        Check("stitch ignores case/punctuation at seam",
            LocalEngine.StitchOverlap("это тест.", "Тест, дальше") == "это тест. дальше");
        Check("stitch falls back to space-join",
            LocalEngine.StitchOverlap("привет", "мир") == "привет мир");
        // Live cases from the spec (§2.5): neighbouring windows spell the overlap differently, and
        // exact comparison let both copies through.
        Check("stitch tolerates a different ending at the seam",
            LocalEngine.StitchOverlap(
                "нужно было собрать руководителя одного отдела, руководителя другого отдела",
                "руководитель одного отдела, руководитель другого отдела приходилось согласовывать")
            == "нужно было собрать руководителя одного отдела, руководителя другого отдела приходилось согласовывать");
        // A window cuts a word in half; the stub matches nothing, so it is dropped and the whole
        // word comes from the next chunk.
        Check("stitch drops a half-heard word and finds the overlap behind it",
            LocalEngine.StitchOverlap(
                "может быть, из чего вообще строить, из кип",
                "может быть, из чего вообще строить, из кирпича или из дерева")
            == "может быть, из чего вообще строить, из кирпича или из дерева");
        Check("stitch keeps short lookalikes apart",
            !LocalEngine.SameWord("стол", "стоп")
            && LocalEngine.StitchOverlap("поставь стол", "стоп машина") == "поставь стол стоп машина");
        Check("stitch tolerance needs a long common prefix",
            LocalEngine.SameWord("руководителя", "руководитель")
            && !LocalEngine.SameWord("руководителя", "рукоятка")
            && LocalEngine.SameWord("согласование", "согласования"));
        Check("stitch handles empty",
            LocalEngine.StitchOverlap("", "мир") == "мир" && LocalEngine.StitchOverlap("привет", "") == "привет");

        var savedEngine = Prefs.Engine;
        Prefs.Engine = EngineKind.Local;
        Check("prefs engine round-trip", Prefs.Engine == EngineKind.Local);
        Prefs.Engine = savedEngine;

        // SHA-256 helper against a known vector ("abc").
        var shaTmp = Path.Combine(Path.GetTempPath(), $"voica-sha-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(shaTmp, "abc");
        var shaVal = ModelManager.ComputeSha256Async(shaTmp).GetAwaiter().GetResult();
        File.Delete(shaTmp);
        Check("sha-256 helper", shaVal == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Check("model files declared", ModelManager.Files.Length == 3 && ModelManager.TotalSize > 200_000_000);
        Check("groq network-error flag",
            new GroqException("x", isNetworkError: true).IsNetworkError && !new GroqException("y").IsNetworkError);

        // --- History export (spec §7) ---
        var exportRecords = new[]
        {
            new Transcription(2, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), "Привет, \"мир\"", "Russian", 2.5, null, "whisper-large-v3-turbo"),
            new Transcription(1, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "Line, with comma", null, null, "a.wav", null),
        };
        Check("export extensions",
            HistoryExport.Extension(ExportFormat.Markdown) == ".md"
            && HistoryExport.Extension(ExportFormat.Csv) == ".csv"
            && HistoryExport.Extension(ExportFormat.Json) == ".json");

        var md = HistoryExport.Render(exportRecords, ExportFormat.Markdown);
        Check("export markdown structure",
            md.StartsWith("# Voica — history (2)") && md.Contains("## ")
            && md.Contains("_Russian · 2.5s · whisper-large-v3-turbo_") && md.Contains("Привет, \"мир\""));

        var csv = HistoryExport.Render(exportRecords, ExportFormat.Csv);
        Check("export csv header and escaping",
            csv.StartsWith("created_at,text,language,duration_sec,model\r\n")
            && csv.Contains("\"Привет, \"\"мир\"\"\"")          // quote + comma → quoted, quotes doubled
            && csv.Contains("\"Line, with comma\"")
            && csv.Contains(",2.50,"));
        Check("export csv encoding has BOM",
            HistoryExport.EncodingFor(ExportFormat.Csv).GetPreamble().Length == 3
            && HistoryExport.EncodingFor(ExportFormat.Markdown).GetPreamble().Length == 0);

        var json = HistoryExport.Render(exportRecords, ExportFormat.Json);
        Check("export json fields and omission",
            json.Contains("\"created_at\"") && json.Contains("\"duration_sec\": 2.5")
            && json.Contains("\"audio_filename\": \"a.wav\"")
            && !json.Contains("\"language\": null") && json.Contains("Привет"));
        Check("export json keys sorted",
            json.IndexOf("\"created_at\"", StringComparison.Ordinal) < json.IndexOf("\"id\"", StringComparison.Ordinal)
            && json.IndexOf("\"id\"", StringComparison.Ordinal) < json.IndexOf("\"text\"", StringComparison.Ordinal));
        Check("export suggested filename",
            HistoryExport.SuggestedFileName(ExportFormat.Json).StartsWith("voica-history-")
            && HistoryExport.SuggestedFileName(ExportFormat.Json).EndsWith(".json"));

        // --- Updater version comparison (spec §10) ---
        Check("update normalize v-prefix", Updater.Normalize("v0.5.0") == "0.5.0");
        Check("update isNewer patch", Updater.IsNewer("0.4.1", "0.4.0"));
        Check("update isNewer minor", Updater.IsNewer("0.5.0", "0.4.9"));
        Check("update not newer equal", !Updater.IsNewer("0.4.0", "0.4.0"));
        Check("update not newer older", !Updater.IsNewer("0.3.9", "0.4.0"));
        Check("update double-digit", Updater.IsNewer("0.10.0", "0.9.0"));

        // --- Hotkey binding (spec §4) ---
        Check("hotkey default is right alt, bare",
            HotkeyBinding.Default.MainVk == HotkeyBinding.VK_RMENU && !HotkeyBinding.Default.HasModifiers);
        Check("hotkey parse legacy names",
            HotkeyBinding.Parse("RightAlt").MainVk == HotkeyBinding.VK_RMENU
            && HotkeyBinding.Parse("LeftAlt").MainVk == HotkeyBinding.VK_LMENU);
        var comboKey = new HotkeyBinding { Ctrl = true, Shift = true, MainVk = HotkeyBinding.VK_SPACE };
        Check("hotkey combo storage round-trip", HotkeyBinding.Parse(comboKey.ToStorage()) == comboKey);
        Check("hotkey combo display", comboKey.DisplayName() == "Ctrl+Shift+Space");
        Check("hotkey combo valid", comboKey.IsValid());
        Check("hotkey bare letter invalid", !(new HotkeyBinding { MainVk = 0x41 }).IsValid());        // bare 'A'
        Check("hotkey bare ctrl invalid", !(new HotkeyBinding { MainVk = HotkeyBinding.VK_LCONTROL }).IsValid());
        Check("hotkey bare capslock valid", new HotkeyBinding { MainVk = HotkeyBinding.VK_CAPITAL }.IsValid());
        Check("hotkey parse fallback to default", HotkeyBinding.Parse("garbage") == HotkeyBinding.Default);
        Check("hotkey presets include right alt and capslock",
            HotkeyBinding.Presets.Any(p => p.MainVk == HotkeyBinding.VK_RMENU)
            && HotkeyBinding.Presets.Any(p => p.MainVk == HotkeyBinding.VK_CAPITAL));

        var savedHotkey = Prefs.Hotkey;
        Prefs.Hotkey = comboKey;
        Check("prefs hotkey round-trip", Prefs.Hotkey == comboKey);
        Prefs.Hotkey = savedHotkey;

        Check("double-tap window is 0.35 s", HotkeyManager.DoubleTapWindow == TimeSpan.FromMilliseconds(350));
        var savedDoubleTap = Prefs.DoubleTapToStart;
        Prefs.DoubleTapToStart = !savedDoubleTap;
        Check("prefs doubleTap round-trip", Prefs.DoubleTapToStart == !savedDoubleTap);
        Prefs.DoubleTapToStart = savedDoubleTap;

        // --- Dictation overlay geometry / wave math (spec §4.2) ---
        Check("overlay centers on the work area",
            OverlayLayout.Left(workLeft: 0, workWidth: 1920, width: 200) == 860
            && OverlayLayout.Left(workLeft: -1920, workWidth: 1920, width: 200) == -1060);
        Check("overlay sits above the work area bottom",
            OverlayLayout.Top(workTop: 0, workHeight: 1040, height: 80, margin: 40) == 920);
        Check("overlay bar scale clamps to 0..1",
            OverlayLayout.BarScale(0) == 0 && OverlayLayout.BarScale(-1) == 0
            && OverlayLayout.BarScale(1) == 1 && OverlayLayout.BarScale(50) == 1);
        Check("overlay bar scale is monotonic and lifts quiet speech",
            OverlayLayout.BarScale(0.05) < OverlayLayout.BarScale(0.2)
            && OverlayLayout.BarScale(0.05) > 0.05);
        Check("overlay bar height spans min..max",
            OverlayLayout.BarHeight(0, 1) == OverlayLayout.MinBarHeight
            && OverlayLayout.BarHeight(1, 1) == OverlayLayout.MaxBarHeight
            && OverlayLayout.BarHeight(2, 1) == OverlayLayout.MaxBarHeight);
        Check("overlay wave has a symmetric 7-bar weight profile (macOS parity)",
            OverlayLayout.BarWeights.Length == 7
            && OverlayLayout.BarWeights[0] == OverlayLayout.BarWeights[6]
            && OverlayLayout.BarWeights[1] == OverlayLayout.BarWeights[5]
            && OverlayLayout.BarWeights[2] == OverlayLayout.BarWeights[4]
            && OverlayLayout.BarWeights[3] == 1.0);
        Check("overlay wave fits the 72 px well",
            OverlayLayout.BarWeights.Length * (OverlayLayout.BarWidth + OverlayLayout.BarGap) <= 72);

        var savedOverlay = Prefs.ShowOverlay;
        Prefs.ShowOverlay = !savedOverlay;
        Check("prefs showOverlay round-trip", Prefs.ShowOverlay == !savedOverlay);
        Prefs.ShowOverlay = savedOverlay;

        // --- AutoInsert native INPUT struct size (regression guard for SendInput) ---
        Check("INPUT struct size matches arch",
            AutoInsert.NativeInputSize == (Environment.Is64BitProcess ? 40 : 28));

        // --- Prefs round-trip (restore originals) ---
        var savedDays = Prefs.RetentionDays;
        Prefs.RetentionDays = 7;
        Check("prefs retentionDays round-trip", Prefs.RetentionDays == 7);
        Prefs.RetentionDays = savedDays;

        var savedOutput = Prefs.Output;
        Prefs.Output = OutputMode.Window;
        Check("prefs output round-trip", Prefs.Output == OutputMode.Window);
        Prefs.Output = savedOutput;

        var savedMode = Prefs.Mode;
        Prefs.Mode = DictationMode.Ptt;
        Check("prefs mode round-trip", Prefs.Mode == DictationMode.Ptt);
        Prefs.Mode = savedMode;

        var savedVocab = Prefs.Vocabulary;
        Prefs.Vocabulary = "test-term";
        Check("prefs vocabulary round-trip", Prefs.Vocabulary == "test-term");
        Prefs.Vocabulary = savedVocab;

        var savedNotify = Prefs.NotifyOnInsert;
        Prefs.NotifyOnInsert = !savedNotify;
        Check("prefs notifyOnInsert round-trip", Prefs.NotifyOnInsert == !savedNotify);
        Prefs.NotifyOnInsert = savedNotify;

        // --- Prefs.Reset() yields the Windows defaults (spec §4/§5/§8/§11) ---
        var snapMode = Prefs.Mode; var snapHotkey = Prefs.Hotkey; var snapOut = Prefs.Output;
        var snapDays = Prefs.RetentionDays; var snapStore = Prefs.StoreAudio;
        var snapVocab2 = Prefs.Vocabulary; var snapCheck = Prefs.CheckUpdatesOnLaunch;
        var snapOverlay = Prefs.ShowOverlay; var snapTap = Prefs.DoubleTapToStart;
        var snapNotify = Prefs.NotifyOnInsert;
        var snapChat = Prefs.ChatModel; var snapResolved = Prefs.ResolvedChatModel;
        Prefs.Reset();
        Check("reset yields windows defaults",
            Prefs.Mode == DictationMode.Toggle && Prefs.Hotkey == HotkeyBinding.Default
            && Prefs.Output == OutputMode.Insert && Prefs.RetentionDays == 30
            && Prefs.StoreAudio && Prefs.Vocabulary == "" && Prefs.CheckUpdatesOnLaunch
            && Prefs.NotifyOnInsert && !Prefs.LlmPostProcess
            && Prefs.SttModel == GroqClient.DefaultSttModel && Prefs.Language == "auto"
            && Prefs.DoubleTapToStart && Prefs.ShowOverlay);
        Prefs.Mode = snapMode; Prefs.Hotkey = snapHotkey; Prefs.Output = snapOut;
        Prefs.RetentionDays = snapDays; Prefs.StoreAudio = snapStore;
        Prefs.Vocabulary = snapVocab2; Prefs.CheckUpdatesOnLaunch = snapCheck;
        Prefs.ShowOverlay = snapOverlay; Prefs.DoubleTapToStart = snapTap;
        Prefs.NotifyOnInsert = snapNotify;
        Prefs.ChatModel = snapChat; Prefs.ResolvedChatModel = snapResolved;

        // --- KeyStore round-trip (restore the exact original file, if any) ---
        var savedKey = KeyStore.Load();
        var savedCred = System.IO.File.Exists(Paths.CredentialsFile)
            ? System.IO.File.ReadAllBytes(Paths.CredentialsFile) : null;
        KeyStore.Save("voica-selftest-key");
        Check("keystore save/load", KeyStore.Load() == "voica-selftest-key");
        if (savedCred is not null) System.IO.File.WriteAllBytes(Paths.CredentialsFile, savedCred);
        else KeyStore.Delete();
        Check("keystore restored", KeyStore.Load() == savedKey);

        // --- Store: insert/delete round-trip, count unchanged (spec §7) ---
        int before = Store.Shared.Count();
        var id = Store.Shared.Insert("__voica_selftest__", "ru", 1.0, "test", null);
        Check("store insert", id is not null && Store.Shared.All().Any(t => t.Id == id.Value));
        if (id is not null) Store.Shared.Delete(id.Value);
        Check("store delete", id is not null && Store.Shared.All().All(t => t.Id != id.Value));
        Check("store count unchanged", Store.Shared.Count() == before);

        // --- Store: concurrent stress — serialized access must not corrupt (spec §7) ---
        int stressBefore = Store.Shared.Count();
        var stressIds = new ConcurrentBag<long>();
        Parallel.For(0, 50, i =>
        {
            var sid = Store.Shared.Insert($"__voica_stress__{i}", null, null, "stress", null);
            if (sid is not null) stressIds.Add(sid.Value);
            _ = Store.Shared.All();   // read concurrently with others' inserts
        });
        Check("store concurrent inserts", stressIds.Count == 50);
        // Batch delete doubles as the cleanup for the stress rows (spec §7).
        int batchDeleted = Store.Shared.DeleteMany(stressIds.ToList());
        Check("store batch delete removes the whole selection",
            batchDeleted == stressIds.Count && Store.Shared.Count() == stressBefore);
        Check("store batch delete on empty list is a no-op", Store.Shared.DeleteMany(Array.Empty<long>()) == 0);

        // --- raw_text: engine text before correction (spec §7) ---
        var rawId = Store.Shared.Insert("исправленный текст", null, null, "test", null,
            rawText: "сырой текст");
        var rawRow = rawId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == rawId.Value);
        Check("store keeps raw_text when correction changed the text",
            rawRow?.RawText == "сырой текст" && rawRow.Text == "исправленный текст");
        if (rawId is not null) Store.Shared.Delete(rawId.Value);

        var sameId = Store.Shared.Insert("одинаковый текст", null, null, "test", null,
            rawText: "одинаковый текст");
        var sameRow = sameId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == sameId.Value);
        Check("store drops raw_text when nothing changed", sameRow is not null && sameRow.RawText is null);
        if (sameId is not null) Store.Shared.Delete(sameId.Value);

        // The column must appear in databases created before it existed: CREATE TABLE IF NOT
        // EXISTS leaves an old table alone, so the migration is what carries them (spec §7).
        var legacyDb = Path.Combine(Path.GetTempPath(), $"voica-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDb}"))
            {
                legacy.Open();
                using var create = legacy.CreateCommand();
                create.CommandText = """
                    CREATE TABLE transcriptions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, created_at INTEGER NOT NULL,
                        text TEXT NOT NULL, language TEXT, duration_sec REAL,
                        audio_filename TEXT, model TEXT);
                    """;
                create.ExecuteNonQuery();
            }
            Check("legacy database gains raw_text and survives a second run",
                Store.MigrateRawText(legacyDb) && Store.MigrateRawText(legacyDb)
                && Store.HasRawTextColumn(legacyDb));
        }
        finally
        {
            try { File.Delete(legacyDb); } catch { }
        }

        var rawJson = HistoryExport.Render(
            new[] { new Transcription(7, DateTimeOffset.UnixEpoch, "итог", null, null, null, "m", "сырое") },
            ExportFormat.Json);
        Check("json export carries raw_text, csv header does not",
            rawJson.Contains("raw_text")
            && !HistoryExport.Render(Array.Empty<Transcription>(), ExportFormat.Csv).Contains("raw_text"));
        Check("json export omits raw_text when there is none",
            !HistoryExport.Render(
                new[] { new Transcription(8, DateTimeOffset.UnixEpoch, "итог", null, null, null, "m") },
                ExportFormat.Json).Contains("raw_text"));

        // Audio retention (spec §8): a stored recording keeps its file next to the record.
        var savedStoreAudio = Prefs.StoreAudio;
        Prefs.StoreAudio = true;
        var keepWav = Path.Combine(Paths.AudioDir, $"rec-selftest-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(keepWav, new byte[2048]);
        var keepId = Store.Shared.Insert("__voica_audio_selftest__", null, null, "test", keepWav);
        var keepRow = keepId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == keepId.Value);
        Check("store keeps audio when enabled",
            keepRow?.AudioPath is not null && File.Exists(keepRow.AudioPath));

        // Retention must NOT be exercised with a future cutoff here: this runs against the real
        // database, and that would delete every stored recording the user owns. Only verify that a
        // past cutoff is a no-op; the destructive path is covered by the app's launch cleanup.
        int purged = Store.Shared.PurgeAudioOlderThan(DateTimeOffset.FromUnixTimeSeconds(0));
        var keepRow2 = keepId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == keepId.Value);
        Check("retention past cutoff is a no-op",
            purged == 0 && keepRow2?.AudioFilename is not null && File.Exists(keepWav));
        if (keepId is not null) Store.Shared.Delete(keepId.Value);   // also removes its audio file

        Prefs.StoreAudio = false;
        var dropWav = Path.Combine(Paths.AudioDir, $"rec-selftest-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(dropWav, new byte[2048]);
        var dropId = Store.Shared.Insert("__voica_noaudio_selftest__", null, null, "test", dropWav);
        var dropRow = dropId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == dropId.Value);
        Check("store drops audio when disabled",
            dropRow is not null && dropRow.AudioFilename is null && !File.Exists(dropWav));
        if (dropId is not null) Store.Shared.Delete(dropId.Value);

        Prefs.StoreAudio = savedStoreAudio;

        Console.WriteLine($"Result: {passed} passed, {failed} failed");
        return failed == 0;
        }
        finally
        {
            Prefs.Restore(settingsSnapshot);
        }
    }
}
