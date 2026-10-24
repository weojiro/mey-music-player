using System.Numerics;

namespace TaskbarPlayerProto;

// The equalizer's sound processing on 2-channel float samples: preamp, the 20 bands, bass boost, the stereo mix
// (width, vocals, balance) and a small-room reverb. Set() may be called from any thread; Process() picks the new
// values up at its next block.
sealed class EqDsp
{
    const double BandQ = 2.0;
    readonly int _rate;
    Coeffs _c;

    // filter memory: [filter, channel]
    readonly double[] _ez1 = new double[2], _ez2 = new double[2];
    readonly double[,] _z1 = new double[Equalizer.Frequencies.Length + 1, 2], _z2 = new double[Equalizer.Frequencies.Length + 1, 2];
    readonly Reverb _reverb;
    double[]? _fitFor, _fitted;     // the last fit: dragging a slider doesn't fit the bands again
    readonly object _setLock = new();

    sealed class Coeffs
    {
        public bool On;
        public double Pre = 1;
        public double[] B0 = Array.Empty<double>(), B1 = Array.Empty<double>(), B2 = Array.Empty<double>(), A1 = Array.Empty<double>(), A2 = Array.Empty<double>();
        public double LL = 1, LR, RL, RR = 1;
        public float Dry = 1, Wet;
        public double Exc, EB0, EB1, EB2, EA1, EA2;      // the enhancer: how much, and its high-pass filter
    }

    public EqDsp(int rate)
    {
        _rate = rate;
        _reverb = new Reverb(rate);
        _c = new Coeffs();
    }

    public void Set(EqSettings s)
    {
        lock (_setLock) _c = Build(s);
    }

    Coeffs Build(EqSettings s)
    {
        var c = new Coeffs { On = s.On, Pre = Math.Pow(10, s.Preamp / 20) };
        var filters = new List<(double b0, double b1, double b2, double a1, double a2)>();
        if (s.Bands.Any(b => b != 0))
        {
            if (_fitFor == null || !_fitFor.SequenceEqual(s.Bands)) { _fitted = FitBands(s.Bands); _fitFor = (double[])s.Bands.Clone(); }
            filters.AddRange(_fitted!.Select((g, i) => Peaking(BandFreq(i), g, BandQ)));
        }
        if (s.Bass > 0) filters.Add(LowShelf(105, s.Bass));
        c.B0 = filters.Select(f => f.b0).ToArray();
        c.B1 = filters.Select(f => f.b1).ToArray();
        c.B2 = filters.Select(f => f.b2).ToArray();
        c.A1 = filters.Select(f => f.a1).ToArray();
        c.A2 = filters.Select(f => f.a2).ToArray();

        // the same mix as before: the middle (what both channels have, mostly the voice) and the sides turned up or
        // down, then each channel's share of the balance
        double mid = 1 - s.Vocals / 100.0, side = s.Width / 100.0;
        double left = s.Balance > 0 ? 1 - s.Balance / 100.0 : 1, right = s.Balance < 0 ? 1 + s.Balance / 100.0 : 1;
        double same = (mid + side) / 2, other = (mid - side) / 2;
        c.LL = same * left; c.LR = other * left; c.RL = other * right; c.RR = same * right;

        double r = s.Reverb / 100.0;
        c.Wet = (float)(r * 1.3);
        c.Dry = (float)(1 - r * 0.8);       // all the way up the reverb is as loud as the song itself, like AIMP's

        c.Exc = s.Enhancer / 100.0 * 0.6;
        (c.EB0, c.EB1, c.EB2, c.EA1, c.EA2) = HighPass(3000);
        return c;
    }

    // the top bands sit above what a 44.1 kHz stream can carry: they move just under it
    double BandFreq(int i) => Math.Min(Equalizer.Frequencies[i], _rate * 0.45);

    // The bands overlap, so neighbours add up (three bands at +6 would give about +9). The filters' gains are fitted
    // until the curve passes through each band's value, the way the graph shows it.
    double[] FitBands(double[] target)
    {
        int n = target.Length;
        var gains = (double[])target.Clone();
        for (int round = 0; round < 12; round++)
        {
            var filters = gains.Select((g, i) => Peaking(BandFreq(i), g, BandQ)).ToArray();
            for (int i = 0; i < n; i++)
            {
                double w = 2 * Math.PI * BandFreq(i) / _rate, db = 0;
                foreach (var f in filters) db += Db(f, w);
                gains[i] = Math.Clamp(gains[i] + 0.8 * (target[i] - db), -24, 24);
            }
        }
        return gains;
    }

    static double Db((double b0, double b1, double b2, double a1, double a2) f, double w)
    {
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        return 20 * Math.Log10(Complex.Abs((f.b0 + f.b1 * z1 + f.b2 * z2) / (1 + f.a1 * z1 + f.a2 * z2)));
    }

    // RBJ cookbook filters, normalised so a0 = 1
    (double, double, double, double, double) Peaking(double freq, double gain, double q)
    {
        double a = Math.Pow(10, gain / 40), w = 2 * Math.PI * freq / _rate, alpha = Math.Sin(w) / (2 * q), cos = Math.Cos(w);
        double a0 = 1 + alpha / a;
        return ((1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
    }

    (double, double, double, double, double) HighPass(double freq)
    {
        double w = 2 * Math.PI * freq / _rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        double a0 = 1 + alpha;
        return ((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    (double, double, double, double, double) LowShelf(double freq, double gain)
    {
        double a = Math.Pow(10, gain / 40), w = 2 * Math.PI * freq / _rate, cos = Math.Cos(w);
        double alpha = Math.Sin(w) / 2 * Math.Sqrt(2), sq = 2 * Math.Sqrt(a) * alpha;
        double a0 = (a + 1) + (a - 1) * cos + sq;
        return (a * ((a + 1) - (a - 1) * cos + sq) / a0, 2 * a * ((a - 1) - (a + 1) * cos) / a0, a * ((a + 1) - (a - 1) * cos - sq) / a0,
            -2 * ((a - 1) + (a + 1) * cos) / a0, ((a + 1) + (a - 1) * cos - sq) / a0);
    }

    // interleaved stereo, in place
    public void Process(float[] buf, int frames)
    {
        var c = _c;
        if (!c.On) return;
        int nf = c.B0.Length;
        bool mix = c.LL != 1 || c.LR != 0 || c.RL != 0 || c.RR != 1;
        for (int i = 0; i < frames; i++)
        {
            double l = buf[2 * i] * c.Pre, r = buf[2 * i + 1] * c.Pre;
            for (int f = 0; f < nf; f++)
            {
                // transposed direct form II
                double yl = c.B0[f] * l + _z1[f, 0];
                _z1[f, 0] = c.B1[f] * l - c.A1[f] * yl + _z2[f, 0];
                _z2[f, 0] = c.B2[f] * l - c.A2[f] * yl;
                l = yl;
                double yr = c.B0[f] * r + _z1[f, 1];
                _z1[f, 1] = c.B1[f] * r - c.A1[f] * yr + _z2[f, 1];
                _z2[f, 1] = c.B2[f] * r - c.A2[f] * yr;
                r = yr;
            }
            if (mix)
            {
                double nl = c.LL * l + c.LR * r;
                r = c.RL * l + c.RR * r;
                l = nl;
            }
            if (c.Exc > 0)
            {
                // the enhancer: the highs, lightly saturated (new overtones), added back on top
                l += c.Exc * Math.Tanh(3 * Excite(c, l, 0));
                r += c.Exc * Math.Tanh(3 * Excite(c, r, 1));
            }
            buf[2 * i] = (float)l;
            buf[2 * i + 1] = (float)r;
        }
        if (c.Wet > 0) _reverb.Process(buf, frames, c.Dry, c.Wet);
        else _reverb.Clear();
        Clip(buf, frames);
    }

    double Excite(Coeffs c, double x, int ch)
    {
        double y = c.EB0 * x + _ez1[ch];
        _ez1[ch] = c.EB1 * x - c.EA1 * y + _ez2[ch];
        _ez2[ch] = c.EB2 * x - c.EA2 * y;
        return y;
    }

    // Turned far up, the sound goes over the top. Windows' own limiter would then turn everything down and it pumps.
    // Here nothing is turned down: only what goes over the top is rounded off, like a player that clips (AIMP), so
    // turned all the way up it stays as loud as it gets.
    const float Knee = 0.8f, Room = 1 - Knee;

    static void Clip(float[] buf, int frames)
    {
        for (int i = 0; i < frames * 2; i++)
        {
            float v = buf[i], a = MathF.Abs(v);
            if (a > Knee) buf[i] = MathF.CopySign(Knee + Room * MathF.Tanh((a - Knee) / Room), v);     // never past 1
        }
    }

    // Freeverb (Jezar's public domain reverb) in a small room, tuned against AIMP's reverb by measuring both on the
    // same song: as strong, as wide and as bright.
    sealed class Reverb
    {
        static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
        const int Spread = 150;        // the left and right rooms differ a lot: a wide sound
        const float Feedback = 0.78f, Damp = 0.35f, InputGain = 0.015f, Cross = -0.4f;
        const float LowHz = 80, HighHz = 8000;

        readonly float[][] _comb = new float[16][], _allpass = new float[8][];
        readonly int[] _combPos = new int[16], _allpassPos = new int[8];
        readonly float[] _combStore = new float[16];
        bool _clear = true;

        // what goes into the room: no deep bass (it booms) and no top highs (they hiss), like AIMP's
        readonly float _lowCut, _highCut;
        float _lowState, _highState;

        public Reverb(int rate)
        {
            _lowCut = 1 - MathF.Exp(-2 * MathF.PI * LowHz / rate);
            _highCut = 1 - MathF.Exp(-2 * MathF.PI * HighHz / rate);
            double scale = rate / 44100.0 * 0.55;      // a smaller room than Freeverb's own
            for (int i = 0; i < 8; i++)
            {
                _comb[i] = new float[Math.Max(1, (int)(CombTuning[i] * scale))];
                _comb[i + 8] = new float[Math.Max(1, (int)((CombTuning[i] + Spread) * scale))];
            }
            for (int i = 0; i < 4; i++)
            {
                _allpass[i] = new float[Math.Max(1, (int)(AllpassTuning[i] * scale))];
                _allpass[i + 4] = new float[Math.Max(1, (int)((AllpassTuning[i] + Spread) * scale))];
            }
        }

        public void Clear()
        {
            if (_clear) return;
            foreach (var a in _comb) Array.Clear(a);
            foreach (var a in _allpass) Array.Clear(a);
            Array.Clear(_combStore);
            _lowState = _highState = 0;
            _clear = true;
        }

        public void Process(float[] buf, int frames, float dry, float wet)
        {
            _clear = false;
            for (int i = 0; i < frames; i++)
            {
                float inL = buf[2 * i], inR = buf[2 * i + 1];
                float input = (inL + inR) * InputGain;
                _highState += (input - _highState) * _highCut;
                _lowState += (_highState - _lowState) * _lowCut;
                input = _highState - _lowState;
                float outL = 0, outR = 0;
                for (int c = 0; c < 8; c++)
                {
                    outL += Comb(c, input);
                    outR += Comb(c + 8, input);
                }
                for (int a = 0; a < 4; a++)
                {
                    outL = Allpass(a, outL);
                    outR = Allpass(a + 4, outR);
                }
                // a little of the other side, upside down: wider still
                buf[2 * i] = inL * dry + (outL + outR * Cross) * wet;
                buf[2 * i + 1] = inR * dry + (outR + outL * Cross) * wet;
            }
        }

        float Comb(int c, float input)
        {
            var b = _comb[c];
            ref int pos = ref _combPos[c];
            float output = b[pos];
            _combStore[c] = output * (1 - Damp) + _combStore[c] * Damp + 1e-20f;      // + a tiny bit: no denormals
            b[pos] = input + _combStore[c] * Feedback;
            if (++pos >= b.Length) pos = 0;
            return output;
        }

        float Allpass(int a, float input)
        {
            var b = _allpass[a];
            ref int pos = ref _allpassPos[a];
            float stored = b[pos];
            float output = stored - input;
            b[pos] = input + stored * 0.5f;
            if (++pos >= b.Length) pos = 0;
            return output;
        }
    }
}

