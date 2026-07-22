using System;

namespace Voica;

/// <summary>
/// Log-mel spectrogram frontend for the local GigaAM engine (spec §2.5). Ports the reference
/// preprocessor with the v3 export parameters (v3_e2e_ctc.yaml): 16 kHz, n_fft=320, win=320,
/// hop=160, center=false, periodic Hann window, power spectrum, HTK mel scale, no filter
/// normalization, 64 mels, then log(clamp(x, 1e-9, 1e9)).
/// </summary>
public static class MelFrontend
{
    public const int SampleRate = 16000;
    public const int NFft = 320;
    public const int WinLength = 320;
    public const int HopLength = 160;
    public const int NMels = 64;
    public const int Bins = NFft / 2 + 1;

    private static readonly double[] Window = BuildWindow();
    private static readonly double[,] Cos = BuildDftTable(sin: false);
    private static readonly double[,] Sin = BuildDftTable(sin: true);
    private static readonly double[,] Fbank = BuildMelFilterbank();

    /// <summary>Number of frames the frontend produces for a given sample count (center=false).</summary>
    public static int FrameCount(int sampleCount) =>
        sampleCount < WinLength ? 0 : (sampleCount - WinLength) / HopLength + 1;

    /// <summary>Computes the [NMels x frames] log-mel spectrogram of 16 kHz mono samples.</summary>
    public static float[,] Compute(float[] samples)
    {
        int frames = FrameCount(samples.Length);
        if (frames < 1) throw new ArgumentException("Recording is too short for feature extraction.");

        var mel = new float[NMels, frames];
        var frame = new double[NFft];
        var power = new double[Bins];

        for (int t = 0; t < frames; t++)
        {
            int offset = t * HopLength;
            for (int i = 0; i < NFft; i++)
                frame[i] = samples[offset + i] * Window[i];

            for (int k = 0; k < Bins; k++)
            {
                double re = 0, im = 0;
                for (int n = 0; n < NFft; n++)
                {
                    re += frame[n] * Cos[k, n];
                    im -= frame[n] * Sin[k, n];
                }
                power[k] = re * re + im * im;   // power spectrum (power = 2)
            }

            for (int m = 0; m < NMels; m++)
            {
                double sum = 0;
                for (int k = 0; k < Bins; k++)
                    sum += power[k] * Fbank[m, k];
                mel[m, t] = (float)Math.Log(Math.Clamp(sum, 1e-9, 1e9));
            }
        }
        return mel;
    }

    private static double[] BuildWindow()
    {
        // Periodic Hann (torch.hann_window default).
        var w = new double[WinLength];
        for (int i = 0; i < WinLength; i++)
            w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / WinLength));
        return w;
    }

    private static double[,] BuildDftTable(bool sin)
    {
        var t = new double[Bins, NFft];
        for (int k = 0; k < Bins; k++)
            for (int n = 0; n < NFft; n++)
            {
                double ang = 2.0 * Math.PI * k * n / NFft;
                t[k, n] = sin ? Math.Sin(ang) : Math.Cos(ang);
            }
        return t;
    }

    private static double[,] BuildMelFilterbank()
    {
        // HTK mel scale, f_min=0, f_max=sr/2, norm=None (torchaudio melscale_fbanks equivalent).
        static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
        static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

        double fMax = SampleRate / 2.0;
        var freqs = new double[Bins];
        for (int k = 0; k < Bins; k++) freqs[k] = fMax * k / (Bins - 1);

        var melPts = new double[NMels + 2];
        double melMax = HzToMel(fMax);
        for (int i = 0; i < NMels + 2; i++)
            melPts[i] = MelToHz(melMax * i / (NMels + 1));

        var fb = new double[NMels, Bins];
        for (int m = 0; m < NMels; m++)
        {
            double left = melPts[m], center = melPts[m + 1], right = melPts[m + 2];
            for (int k = 0; k < Bins; k++)
            {
                double up = (freqs[k] - left) / (center - left);
                double down = (right - freqs[k]) / (right - center);
                fb[m, k] = Math.Max(0.0, Math.Min(up, down));
            }
        }
        return fb;
    }
}
