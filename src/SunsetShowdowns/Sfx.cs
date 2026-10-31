using System;
using System.Collections.Generic;
using System.IO;
using Raylib_cs;

namespace LLD
{
    /// <summary>Every sound is synthesised at startup, so the game ships as one file with no audio assets.</summary>
    public static class Sfx
    {
        const int Rate = 44100;

        class Bank
        {
            public Sound[] Voices;
            public int Next;
            public float Gain;
        }

        static readonly Dictionary<string, Bank> Banks = new();
        public static float Master = 1f;
        public static float Volume = 1f;

        public static void Init()
        {
            Add("shot", Gunshot(), 4, 1.0f);
            Add("shot_far", Gunshot(0.65f, 0.75f), 4, 0.85f);
            Add("flesh", Flesh(), 4, 0.9f);
            Add("skull", Skull(), 3, 1.0f);
            Add("ric", Ricochet(), 4, 0.55f);
            Add("cue", Cue(), 2, 0.7f);
            Add("ready", Ready(), 2, 0.6f);
            Add("foul", Foul(), 2, 0.8f);
            Add("win", Win(), 2, 0.7f);
            Add("click", Click(), 3, 0.5f);
            Add("holster", Leather(), 3, 0.6f);
            Add("thud", Thud(), 3, 0.9f);
            Add("glass", Glass(), 4, 0.85f);
        }

        static void Add(string name, float[] samples, int voices, float gain)
        {
            var wave = Raylib.LoadWaveFromMemory(".wav", Encode(samples));
            var b = new Bank { Voices = new Sound[voices], Gain = gain };
            for (int i = 0; i < voices; i++) b.Voices[i] = Raylib.LoadSoundFromWave(wave);
            Raylib.UnloadWave(wave);
            Banks[name] = b;
        }

        public static void Play(string name, float vol = 1f, float pitch = 1f)
        {
            if (!Banks.TryGetValue(name, out var b)) return;
            var s = b.Voices[b.Next];
            b.Next = (b.Next + 1) % b.Voices.Length;
            Raylib.SetSoundVolume(s, Math.Clamp(vol * b.Gain * Volume * Master, 0f, 1f));
            Raylib.SetSoundPitch(s, Math.Clamp(pitch, 0.25f, 3f));
            Raylib.PlaySound(s);
        }

        static byte[] Encode(float[] s)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int n = s.Length;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2);
            w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            for (int i = 0; i < n; i++) w.Write((short)(Math.Clamp(s[i], -1f, 1f) * 31000f));
            w.Flush();
            return ms.ToArray();
        }

        static float[] Buf(float seconds) => new float[(int)(Rate * seconds)];
        static readonly Random R = new Random(7);
        static float N() => (float)(R.NextDouble() * 2.0 - 1.0);

        // one-pole low pass
        static void LowPass(float[] s, float cut)
        {
            float a = MathF.Exp(-2f * MathF.PI * cut / Rate), y = 0f;
            for (int i = 0; i < s.Length; i++) { y = s[i] * (1 - a) + y * a; s[i] = y; }
        }
        static void HighPass(float[] s, float cut)
        {
            float a = MathF.Exp(-2f * MathF.PI * cut / Rate), y = 0f, px = 0f;
            for (int i = 0; i < s.Length; i++) { y = a * (y + s[i] - px); px = s[i]; s[i] = y; }
        }

        static float[] Gunshot(float lenScale = 1f, float pitch = 1f)
        {
            var s = Buf(0.55f * lenScale);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float crack = N() * MathF.Exp(-t * 95f / lenScale);
                float body = N() * MathF.Exp(-t * 16f / lenScale) * 0.8f;
                float thump = MathF.Sin(2f * MathF.PI * (78f * pitch) * t) * MathF.Exp(-t * 22f) * 0.75f;
                float tail = N() * MathF.Exp(-t * 5.5f) * 0.18f;
                s[i] = crack * 0.9f + body + thump + tail;
            }
            var low = (float[])s.Clone();
            LowPass(low, 420f * pitch);
            for (int i = 0; i < s.Length; i++) s[i] = Math.Clamp(s[i] * 0.55f + low[i] * 1.5f, -1f, 1f);
            Shape(s, 2.2f);
            return s;
        }

        static float[] Flesh()
        {
            var s = Buf(0.22f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = N() * MathF.Exp(-t * 38f) + MathF.Sin(2f * MathF.PI * 120f * t) * MathF.Exp(-t * 30f) * 0.6f;
            }
            LowPass(s, 900f);
            return s;
        }

        static float[] Skull()
        {
            var s = Buf(0.4f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float snap = N() * MathF.Exp(-t * 55f);
                float wet = N() * MathF.Exp(-t * 11f) * 0.7f;
                float low = MathF.Sin(2f * MathF.PI * 92f * t) * MathF.Exp(-t * 17f);
                s[i] = snap + wet + low;
            }
            LowPass(s, 1500f);
            Shape(s, 1.6f);
            return s;
        }

        static float[] Ricochet()
        {
            var s = Buf(0.5f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float f = 2400f - 1500f * t / 0.5f;
                s[i] = MathF.Sin(2f * MathF.PI * f * t) * MathF.Exp(-t * 9f) * 0.6f + N() * MathF.Exp(-t * 60f) * 0.4f;
            }
            HighPass(s, 500f);
            return s;
        }

        static float[] Cue()
        {
            var s = Buf(0.6f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = (MathF.Sin(2f * MathF.PI * 1180f * t) * 0.6f + MathF.Sin(2f * MathF.PI * 1760f * t) * 0.35f)
                       * MathF.Exp(-t * 7f);
            }
            return s;
        }

        static float[] Ready()
        {
            var s = Buf(0.35f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = MathF.Sin(2f * MathF.PI * 520f * t) * MathF.Exp(-t * 11f) * 0.7f;
            }
            return s;
        }

        static float[] Foul()
        {
            var s = Buf(0.8f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float f = 190f - 90f * t;
                s[i] = MathF.Sign(MathF.Sin(2f * MathF.PI * f * t)) * 0.4f * MathF.Exp(-t * 4f);
            }
            LowPass(s, 700f);
            return s;
        }

        static float[] Win()
        {
            var s = Buf(1.0f);
            float[] notes = { 392f, 523f, 659f };
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float v = 0f;
                for (int k = 0; k < notes.Length; k++)
                {
                    float st = k * 0.12f;
                    if (t < st) continue;
                    float u = t - st;
                    v += MathF.Sin(2f * MathF.PI * notes[k] * u) * MathF.Exp(-u * 4.2f) * 0.33f;
                }
                s[i] = v;
            }
            return s;
        }

        static float[] Click()
        {
            var s = Buf(0.09f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = N() * MathF.Exp(-t * 180f) * 0.8f + MathF.Sin(2f * MathF.PI * 900f * t) * MathF.Exp(-t * 90f) * 0.3f;
            }
            HighPass(s, 900f);
            return s;
        }

        static float[] Leather()
        {
            var s = Buf(0.25f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = N() * MathF.Exp(-t * 26f) * 0.5f;
            }
            LowPass(s, 2200f);
            HighPass(s, 400f);
            return s;
        }

        static float[] Thud()
        {
            var s = Buf(0.5f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = MathF.Sin(2f * MathF.PI * 62f * t) * MathF.Exp(-t * 13f)
                     + N() * MathF.Exp(-t * 30f) * 0.5f;
            }
            LowPass(s, 400f);
            return s;
        }

        /// <summary>A bottle going: one bright crack, then shards raining down.</summary>
        static float[] Glass()
        {
            var s = Buf(0.7f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] = N() * MathF.Exp(-t * 70f) * 0.9f;                  // the break
                s[i] += N() * MathF.Exp(-t * 7f) * 0.25f;                 // the fall
            }
            HighPass(s, 1800f);
            // a few ringing pieces over the top
            float[] tones = { 3200f, 4300f, 5600f, 2600f };
            for (int k = 0; k < tones.Length; k++)
            {
                float start = k * 0.035f;
                for (int i = 0; i < s.Length; i++)
                {
                    float t = i / (float)Rate;
                    if (t < start) continue;
                    float u = t - start;
                    s[i] += MathF.Sin(2f * MathF.PI * tones[k] * u) * MathF.Exp(-u * 26f) * 0.18f;
                }
            }
            return s;
        }

        static void Shape(float[] s, float drive)
        {
            for (int i = 0; i < s.Length; i++) s[i] = MathF.Tanh(s[i] * drive) * 0.92f;
        }
    }
}
