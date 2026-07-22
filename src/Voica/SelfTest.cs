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
        Check("groq model", GroqClient.Model == "whisper-large-v3-turbo");
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

        // --- LLM post-processing prompt (spec §6.1) ---
        Check("postprocess model", GroqClient.PostProcessModel == "llama-3.3-70b-versatile");
        Check("chat endpoint host", GroqClient.ChatEndpoint.Host == "api.groq.com");
        Check("postprocess prompt null on empty vocab",
            GroqClient.PostProcessPromptText("текст", "  \n ") is null);
        var ppPrompt = GroqClient.PostProcessPromptText("привет кубер стил", "kubectl, Kubernetes");
        Check("postprocess prompt contains vocab and text",
            ppPrompt is not null && ppPrompt.Contains("СЛОВАРЬ: kubectl, Kubernetes")
            && ppPrompt.Contains("ТЕКСТ: привет кубер стил"));

        var savedLlm = Prefs.LlmPostProcess;
        Prefs.LlmPostProcess = !savedLlm;
        Check("prefs llmPostProcess round-trip", Prefs.LlmPostProcess == !savedLlm);
        Prefs.LlmPostProcess = savedLlm;

        // --- Reset-settings semantics (spec §11): vocabulary is user content, survives reset ---
        var rsVocab = Prefs.Vocabulary; var rsLlm = Prefs.LlmPostProcess; var rsDays = Prefs.RetentionDays;
        Prefs.Vocabulary = "__voica_reset_test__"; Prefs.LlmPostProcess = true; Prefs.RetentionDays = 7;
        var keepVocab = Prefs.Vocabulary;
        Prefs.Reset(); Prefs.Vocabulary = keepVocab;   // the reset-settings flow
        Check("reset-settings keeps vocabulary, resets the rest",
            Prefs.Vocabulary == "__voica_reset_test__" && !Prefs.LlmPostProcess && Prefs.RetentionDays == 30);
        Prefs.Vocabulary = rsVocab; Prefs.LlmPostProcess = rsLlm; Prefs.RetentionDays = rsDays;

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
        Prefs.Reset();
        Check("reset yields windows defaults",
            Prefs.Mode == DictationMode.Toggle && Prefs.Hotkey == HotkeyBinding.Default
            && Prefs.Output == OutputMode.Insert && Prefs.RetentionDays == 30
            && Prefs.StoreAudio && Prefs.Vocabulary == "" && Prefs.CheckUpdatesOnLaunch
            && Prefs.NotifyOnInsert && !Prefs.LlmPostProcess);
        Prefs.Mode = snapMode; Prefs.Hotkey = snapHotkey; Prefs.Output = snapOut;
        Prefs.RetentionDays = snapDays; Prefs.StoreAudio = snapStore;
        Prefs.Vocabulary = snapVocab2; Prefs.CheckUpdatesOnLaunch = snapCheck;

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
        Parallel.ForEach(stressIds, sid => Store.Shared.Delete(sid));
        Check("store concurrent cleanup", Store.Shared.Count() == stressBefore);

        // --- Store: audio lifecycle + retention purge (spec §7, §8) ---
        var savedStoreAudio = Prefs.StoreAudio;
        Paths.EnsureCreated();

        Prefs.StoreAudio = true;
        var keepWav = Path.Combine(Paths.AudioDir, $"rec-selftest-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(keepWav, new byte[2048]);
        var keepId = Store.Shared.Insert("__voica_audio_selftest__", "ru", 1.0, "test", keepWav);
        var keepRow = keepId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == keepId.Value);
        Check("store keeps audio when enabled",
            keepRow?.AudioPath is not null && File.Exists(keepRow.AudioPath));

        int purged = Store.Shared.PurgeAudioOlderThan(DateTimeOffset.UtcNow.AddDays(1));
        var keepRow2 = keepId is null ? null : Store.Shared.All().FirstOrDefault(t => t.Id == keepId.Value);
        Check("retention clears audio but keeps text",
            purged >= 1 && keepRow2 is not null && keepRow2.AudioFilename is null
            && keepRow2.Text == "__voica_audio_selftest__" && !File.Exists(keepWav));
        if (keepId is not null) Store.Shared.Delete(keepId.Value);

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
}
