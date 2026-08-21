using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voica;

/// <summary>
/// Local offline transcription engine (spec §2.5): GigaAM v3 e2e CTC (int8 ONNX) via ONNX Runtime.
/// Pipeline: WAV 16 kHz mono → log-mel (<see cref="MelFrontend"/>) → encoder → CTC greedy decode.
/// The session loads lazily on first use (one-time hardware init, several seconds — surfaced via
/// <see cref="PreparingModel"/>) and is unloaded from RAM after idling.
/// </summary>
public sealed class LocalEngine : IDisposable
{
    public const int BlankId = 256;
    /// <summary>Chunk long recordings into ~25 s windows (the model is trained on short segments).</summary>
    public const int ChunkSeconds = 25;
    /// <summary>Adjacent chunks overlap by this much so a word at the seam lands whole in one of them.</summary>
    public const int OverlapSeconds = 2;
    /// <summary>Cap on how many words the seam de-duplication compares.</summary>
    private const int MaxOverlapWords = 12;
    /// <summary>Glued comparison needs this much text: on short pieces any two phrases look alike.</summary>
    private const int MinGluedChars = 10;
    private const double MinGluedSimilarity = 0.8;

    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    /// <summary>Raised (on a worker thread) when a slow first-time session load is about to happen.</summary>
    public event Action? PreparingModel;

    private readonly object _gate = new();
    private InferenceSession? _session;
    private Dictionary<int, string>? _vocab;
    private DateTime _lastUse = DateTime.MinValue;
    private Timer? _idleTimer;

    /// <summary>Transcribes a 16 kHz mono WAV file. Call off the UI thread.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            float[] samples = WavReader.ReadMono16k(wavPath);
            double duration = samples.Length / (double)MelFrontend.SampleRate;

            var (session, vocab) = EnsureLoaded();

            int window = ChunkSeconds * MelFrontend.SampleRate;
            int step = (ChunkSeconds - OverlapSeconds) * MelFrontend.SampleRate;
            string acc = "";
            for (int offset = 0; offset < samples.Length; offset += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(window, samples.Length - offset);
                if (MelFrontend.FrameCount(count) >= 1)
                {
                    var chunk = new float[count];
                    Array.Copy(samples, offset, chunk, 0, count);
                    string piece = Recognize(session, vocab, chunk);
                    acc = acc.Length == 0 ? piece : StitchOverlap(acc, piece);
                }
                if (offset + count >= samples.Length) break;   // this window reached the end
            }

            Touch();
            // "Russian" (not "ru") to match the language names Groq verbose_json reports.
            return new TranscriptionResult(acc.Trim(), "Russian", duration);
        }, cancellationToken);
    }

    /// <summary>Splits a sample count into (offset, count) windows of at most chunkSamples.</summary>
    public static IEnumerable<(int Offset, int Count)> Chunks(int totalSamples, int chunkSamples)
    {
        for (int offset = 0; offset < totalSamples; offset += chunkSamples)
            yield return (offset, Math.Min(chunkSamples, totalSamples - offset));
    }

    /// <summary>
    /// Joins two adjacent chunk transcripts, removing the text that overlapping audio produced
    /// twice (spec §2.5): finds the largest word-run where the tail of <paramref name="prev"/>
    /// matches the head of <paramref name="next"/> and drops that duplicated head. Matching is
    /// deliberately tolerant — see <see cref="SameWord"/>. When nothing matches, the last word of
    /// the first chunk is dropped and the search repeats: a window often cuts a word in half
    /// ("из кип" for "из кирпича"), and that stub resembles nothing, breaking the whole run. The
    /// whole word is present in the next chunk, so the stub is safe to lose. Still nothing —
    /// plain space-join, never worse than a hard cut.
    /// </summary>
    public static string StitchOverlap(string prev, string next)
    {
        if (prev.Length == 0) return next;
        if (next.Length == 0) return prev;

        var pw = prev.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var nw = next.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (TryJoin(prev, pw, nw, out var joined)) return joined;

        int cut = prev.TrimEnd().LastIndexOf(' ');
        if (cut > 0 && pw.Length > 1 && TryJoin(prev[..cut], pw[..^1], nw, out var withoutStub))
            return withoutStub;

        // Word by word found nothing — the windows may have split the same place into a DIFFERENT
        // number of words, and then the alignment breaks in principle rather than by a threshold.
        if (GluedOverlap(pw, nw) is { } drop)
            return prev + " " + string.Join(' ', nw.Skip(drop));

        return prev + " " + next;
    }

    /// <summary>
    /// Words in a run this long may include one the two windows heard differently. A single word is
    /// enough to break a run: "управляющий филиала сказал" against "управляющего филиала сказал"
    /// shares only 9 of 12 letters on the first word — under the 80 % bar of <see cref="SameWord"/>
    /// — and both copies of the whole phrase reached the text (a live 40 s dictation). Four words
    /// with one off is still three that agree, over two seconds of overlapping audio; below that
    /// the run is too short to risk it. The word at the seam itself is never the forgiven one.
    /// </summary>
    private const int ForgivingRunWords = 4;

    private static bool TryJoin(string prevText, string[] pw, string[] nw, out string result)
    {
        int max = Math.Min(Math.Min(pw.Length, nw.Length), MaxOverlapWords);
        for (int k = max; k >= 1; k--)
        {
            int misses = 0, missAt = -1;
            for (int i = 0; i < k && misses <= 1; i++)
                if (!SameWord(pw[pw.Length - k + i], nw[i])) { misses++; missAt = i; }
            // The forgiven word may not be the last one of the run: right at the seam a mismatch
            // usually means the window cut a word in half ("из кип" for "из кирпича"), and dropping
            // that stub below recovers the whole word — forgiving it here would keep the stub and
            // lose the word instead.
            bool match = misses == 0
                || (misses == 1 && k >= ForgivingRunWords && missAt < k - 1);
            if (match)
            {
                var rest = string.Join(' ', nw.Skip(k));
                result = rest.Length == 0 ? prevText : prevText + " " + rest;
                return true;
            }
        }
        result = string.Empty;
        return false;
    }

    /// <summary>
    /// Fallback search for the overlap, for when comparing word by word cannot work: the windows
    /// split the same place into a DIFFERENT number of words, so a word-to-word alignment has
    /// nothing to line up. Live cases: one window heard "3кар" where its neighbour heard "Три кар"
    /// — four words against five.
    ///
    /// The comparison is over the words glued together without spaces, because it is exactly the
    /// word boundaries that moved. It looks for the longest pair "j words from the end of prev, k
    /// words from the start of next" that is similar above the threshold, and returns how many
    /// words of next to drop.
    ///
    /// ⚠️ ONLY as a fallback, after word-by-word found nothing: gluing compares noticeably more
    /// loosely, and running it first risks eating text that belongs. The ten-character floor is
    /// there for the same reason — on short pieces any two phrases look alike.
    /// </summary>
    private static int? GluedOverlap(string[] pw, string[] nw)
    {
        static string Glue(IEnumerable<string> words) =>
            new string(string.Concat(words).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        int? bestDrop = null;
        int bestWeight = 0;
        for (int j = 1; j <= Math.Min(MaxOverlapWords, pw.Length); j++)
        {
            var tail = Glue(pw.Skip(pw.Length - j));
            if (tail.Length < MinGluedChars) continue;
            for (int k = 1; k <= Math.Min(MaxOverlapWords, nw.Length); k++)
            {
                var head = Glue(nw.Take(k));
                if (head.Length < MinGluedChars) continue;
                if (TermFix.Similarity(tail, head) < MinGluedSimilarity) continue;
                int weight = tail.Length + head.Length;
                if (bestDrop is null || weight > bestWeight) { bestDrop = k; bestWeight = weight; }
            }
        }
        return bestDrop;
    }

    /// <summary>
    /// Whether two words at a chunk seam are the same word (spec §2.5). Exact comparison does not
    /// work: neighbouring windows hear the overlap with different context and write it differently
    /// ("руководителя" / "руководитель"), the run then fails to match, and BOTH copies reach the
    /// text. So words also count as equal when they diverge only in the tail — a common prefix of
    /// at least 80 % of the longer one. Words shorter than 6 characters are compared exactly, or
    /// "стол" would merge with "стоп".
    /// </summary>
    public static bool SameWord(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;

        int longer = Math.Max(x.Length, y.Length);
        if (longer < 6) return false;

        int common = 0;
        while (common < x.Length && common < y.Length && x[common] == y[common]) common++;
        return common * 5 >= longer * 4;
    }

    private static string Normalize(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private string Recognize(InferenceSession session, Dictionary<int, string> vocab, float[] samples)
    {
        var mel = MelFrontend.Compute(samples);
        int frames = mel.GetLength(1);

        var features = new DenseTensor<float>(new[] { 1, MelFrontend.NMels, frames });
        for (int m = 0; m < MelFrontend.NMels; m++)
            for (int t = 0; t < frames; t++)
                features[0, m, t] = mel[m, t];
        var lengths = new DenseTensor<long>(new[] { 1 });
        lengths[0] = frames;

        var names = session.InputMetadata.Keys.ToArray();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(names[0], features),
            NamedOnnxValue.CreateFromTensor(names[1], lengths),
        };

        using var results = session.Run(inputs);
        var logits = results[0].AsTensor<float>();   // [1, T', classes]
        return CtcGreedyDecode(logits, vocab);
    }

    /// <summary>CTC greedy decode: per-frame argmax → collapse repeats → drop blank → join tokens.</summary>
    public static string CtcGreedyDecode(Tensor<float> logits, Dictionary<int, string> vocab)
    {
        int tSteps = logits.Dimensions[1];
        int classes = logits.Dimensions[2];
        var sb = new StringBuilder();
        int prev = -1;
        for (int t = 0; t < tSteps; t++)
        {
            int best = 0;
            float bestVal = float.MinValue;
            for (int c = 0; c < classes; c++)
            {
                float v = logits[0, t, c];
                if (v > bestVal) { bestVal = v; best = c; }
            }
            if (best != prev && best != BlankId
                && vocab.TryGetValue(best, out var tok) && tok != "<unk>")
                sb.Append(tok);
            prev = best;
        }
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    /// <summary>Parses "token id" lines into an id→token map (vocab file of the export).</summary>
    public static Dictionary<int, string> ParseVocab(IEnumerable<string> lines)
    {
        var vocab = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            int sep = line.LastIndexOf(' ');
            if (sep > 0 && int.TryParse(line[(sep + 1)..], out int id))
                vocab[id] = line[..sep];
        }
        return vocab;
    }

    private (InferenceSession, Dictionary<int, string>) EnsureLoaded()
    {
        lock (_gate)
        {
            if (_session is null)
            {
                if (!ModelManager.IsInstalled())
                    throw new InvalidOperationException("Local model is not installed.");
                PreparingModel?.Invoke();
                Log.Info("loading local model session…");
                var started = DateTime.UtcNow;
                _session = new InferenceSession(ModelManager.OnnxPath);
                _vocab = ParseVocab(File.ReadAllLines(ModelManager.VocabPath));
                Log.Info($"local model ready in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            }
            Touch();
            return (_session, _vocab!);
        }
    }

    private void Touch()
    {
        _lastUse = DateTime.UtcNow;
        _idleTimer ??= new Timer(_ => MaybeUnload(), null, IdleUnload, IdleUnload);
    }

    private void MaybeUnload()
    {
        lock (_gate)
        {
            if (_session is not null && DateTime.UtcNow - _lastUse >= IdleUnload)
            {
                _session.Dispose();
                _session = null;
                _vocab = null;
                Log.Info("local model unloaded after idle");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _session?.Dispose();
            _session = null;
        }
    }
}

/// <summary>Minimal RIFF reader for our own 16 kHz mono 16-bit recordings.</summary>
public static class WavReader
{
    public static float[] ReadMono16k(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        short channels = 0, bits = 0;
        int rate = 0;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (chunkId == "fmt ")
            {
                br.ReadInt16();
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16();
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (chunkId == "data")
            {
                if (rate != MelFrontend.SampleRate || channels != 1 || bits != 16)
                    throw new InvalidDataException($"Expected 16 kHz mono 16-bit WAV, got {rate} Hz {channels}ch {bits}-bit.");
                int n = size / 2;
                var samples = new float[n];
                for (int i = 0; i < n; i++) samples[i] = br.ReadInt16() / 32768f;
                return samples;
            }
            else
            {
                br.ReadBytes(size);
            }
        }
        throw new InvalidDataException("No data chunk found.");
    }
}
