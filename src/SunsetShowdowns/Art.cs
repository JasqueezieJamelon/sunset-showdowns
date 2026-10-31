using System;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>Low-level drawing helpers. All shapes are flat colour with a dark ink outline.</summary>
    public static class Art
    {
        public static Font Display;   // Rye   - headlines, names
        public static Font Body;      // Special Elite - labels, menus

        public const float VW = 1920f;
        public const float VH = 1080f;

        public static Color Ink = new Color(28, 18, 16, 255);

        public static Vector2 V(float x, float y) => new Vector2(x, y);

        public static Vector2 Rot(Vector2 p, float a)
        {
            float c = MathF.Cos(a), s = MathF.Sin(a);
            return new Vector2(p.X * c - p.Y * s, p.X * s + p.Y * c);
        }

        public static float SignedArea(Vector2[] p)
        {
            float a = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 q = p[(i + 1) % p.Length];
                a += p[i].X * q.Y - q.X * p[i].Y;
            }
            return a * 0.5f;
        }

        static readonly Vector2[] _tmp = new Vector2[64];

        /// <summary>Fill a convex polygon. raylib only rasterises negative-area (CCW in screen space) fans.</summary>
        public static void Poly(Vector2[] pts, Color c)
        {
            int n = pts.Length;
            if (n < 3) return;
            if (n > _tmp.Length) return;
            bool flip = SignedArea(pts) > 0f;
            for (int i = 0; i < n; i++) _tmp[i] = flip ? pts[n - 1 - i] : pts[i];
            Raylib.DrawTriangleFan(_tmp, n, c);
        }

        public static void PolyLine(Vector2[] pts, float thick, Color c)
        {
            for (int i = 0; i < pts.Length; i++)
                Raylib.DrawLineEx(pts[i], pts[(i + 1) % pts.Length], thick, c);
            for (int i = 0; i < pts.Length; i++)
                Raylib.DrawCircleV(pts[i], thick * 0.5f, c);
        }

        /// <summary>
        /// When true, every Shape/Capsule/Circle draws only its ink silhouette, fattened by the
        /// outline width. Draw a character once with this on and once with it off and the whole
        /// body gets one continuous outline instead of an outline around each separate limb.
        /// </summary>
        public static bool InkPass;

        /// <summary>Filled shape with an ink outline, the house style for every character part.</summary>
        public static void Shape(Vector2[] pts, Color fill, float outline = 3.5f)
        {
            if (InkPass)
            {
                if (outline <= 0f) return;
                PolyLine(pts, outline * 2f, Ink);
                Poly(pts, Ink);
                return;
            }
            Poly(pts, fill);
        }

        /// <summary>An interior line that only exists in the fill pass: seams, plackets, straps.</summary>
        public static void Seam(Vector2 a, Vector2 b, float thick, Color c)
        {
            if (InkPass) return;
            Raylib.DrawLineEx(a, b, thick, c);
        }

        public static void Capsule(Vector2 a, Vector2 b, float r, Color c)
        {
            Raylib.DrawLineEx(a, b, r * 2f, c);
            Raylib.DrawCircleV(a, r, c);
            Raylib.DrawCircleV(b, r, c);
        }

        public static void CapsuleInked(Vector2 a, Vector2 b, float r, Color fill, float outline = 3.5f)
        {
            if (InkPass)
            {
                if (outline > 0f) Capsule(a, b, r + outline, Ink);
                return;
            }
            Capsule(a, b, r, fill);
        }

        /// <summary>
        /// A limb that is thicker at one end than the other: a thigh off the hip, a calf off the
        /// knee. A plain capsule is the same width all the way down, which is why a leg drawn
        /// out of two of them reads as a pipe instead of a leg.
        /// </summary>
        public static void Taper(Vector2 a, Vector2 b, float ra, float rb, Color c)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len < 0.001f) { Raylib.DrawCircleV(a, MathF.Max(ra, rb), c); return; }
            Vector2 n = new Vector2(-d.Y, d.X) / len;
            Poly(new[] { a + n * ra, b + n * rb, b - n * rb, a - n * ra }, c);
            Raylib.DrawCircleV(a, ra, c);
            Raylib.DrawCircleV(b, rb, c);
        }

        public static void TaperInked(Vector2 a, Vector2 b, float ra, float rb, Color fill, float outline = 3.5f)
        {
            if (InkPass)
            {
                if (outline > 0f) Taper(a, b, ra + outline, rb + outline, Ink);
                return;
            }
            Taper(a, b, ra, rb, fill);
        }

        public static void CircleInked(Vector2 c, float r, Color fill, float outline = 3.5f)
        {
            if (InkPass)
            {
                if (outline > 0f) Raylib.DrawCircleV(c, r + outline, Ink);
                return;
            }
            Raylib.DrawCircleV(c, r, fill);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Color(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t),
                (int)(a.A + (b.A - a.A) * t));
        }

        public static Color Shade(Color c, float f)
        {
            return new Color(
                (int)Math.Clamp(c.R * f, 0, 255),
                (int)Math.Clamp(c.G * f, 0, 255),
                (int)Math.Clamp(c.B * f, 0, 255),
                (int)c.A);
        }

        public static Color Alpha(Color c, float a) => new Color((int)c.R, (int)c.G, (int)c.B, (int)Math.Clamp(a * 255f, 0, 255));

        public static Vector2 Measure(Font f, string s, float size, float spacing = 1f)
            => Raylib.MeasureTextEx(f, s, size, spacing);

        public static void Text(Font f, string s, Vector2 pos, float size, Color c, bool center = false, float spacing = 1f)
        {
            if (center) pos.X -= Measure(f, s, size, spacing).X * 0.5f;
            Raylib.DrawTextEx(f, s, pos, size, spacing, c);
        }

        public static void TextInked(Font f, string s, Vector2 pos, float size, Color c, bool center = false, float spacing = 1f, float o = 3f)
        {
            if (center) pos.X -= Measure(f, s, size, spacing).X * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI * 0.25f;
                Raylib.DrawTextEx(f, s, pos + new Vector2(MathF.Cos(a) * o, MathF.Sin(a) * o), size, spacing, Ink);
            }
            Raylib.DrawTextEx(f, s, pos, size, spacing, c);
        }

        public static void VGradient(Rectangle r, Color top, Color bottom, int bands = 48)
        {
            float h = r.Height / bands;
            for (int i = 0; i < bands; i++)
                Raylib.DrawRectangleRec(new Rectangle(r.X, r.Y + i * h, r.Width, h + 1f),
                    Mix(top, bottom, i / (float)(bands - 1)));
        }

        /// <summary>Segment vs circle. Returns distance along the segment, or -1.</summary>
        public static float RayCircle(Vector2 o, Vector2 d, float len, Vector2 c, float r)
        {
            Vector2 m = o - c;
            float b = Vector2.Dot(m, d);
            float cc = Vector2.Dot(m, m) - r * r;
            if (cc > 0f && b > 0f) return -1f;
            float disc = b * b - cc;
            if (disc < 0f) return -1f;
            float t = -b - MathF.Sqrt(disc);
            if (t < 0f) t = 0f;
            return t <= len ? t : -1f;
        }

        /// <summary>Segment vs capsule (a-b, radius r). Returns distance along the segment, or -1.</summary>
        public static float RayCapsule(Vector2 o, Vector2 d, float len, Vector2 a, Vector2 b, float r)
        {
            // sample-march: cheap, robust, and plenty accurate at these scales
            float step = r * 0.5f;
            if (step < 2f) step = 2f;
            for (float t = 0f; t <= len; t += step)
            {
                Vector2 p = o + d * t;
                if (DistToSeg(p, a, b) <= r)
                {
                    // refine backwards
                    float lo = MathF.Max(0f, t - step), hi = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        if (DistToSeg(o + d * mid, a, b) <= r) hi = mid; else lo = mid;
                    }
                    return hi;
                }
            }
            return -1f;
        }

        public static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float l2 = Vector2.Dot(ab, ab);
            if (l2 < 0.0001f) return Vector2.Distance(p, a);
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        public static float Angle(Vector2 v) => MathF.Atan2(v.Y, v.X);

        public static Vector2 FromAngle(float a, float len = 1f) => new Vector2(MathF.Cos(a) * len, MathF.Sin(a) * len);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>Fold an angle into -pi..pi.</summary>
        public static float WrapPi(float a)
        {
            while (a > MathF.PI) a -= MathF.PI * 2f;
            while (a < -MathF.PI) a += MathF.PI * 2f;
            return a;
        }

        public static float Approach(float cur, float target, float rate, float dt)
        {
            float d = target - cur;
            float m = rate * dt;
            if (MathF.Abs(d) <= m) return target;
            return cur + MathF.Sign(d) * m;
        }
    }

    public static class Rng
    {
        static readonly Random R = new Random();
        public static float F(float a, float b) => a + (float)R.NextDouble() * (b - a);
        public static float F(float a) => (float)R.NextDouble() * a;
        public static int I(int a, int b) => R.Next(a, b);
        public static bool Chance(float p) => R.NextDouble() < p;
        public static Vector2 Dir() { float a = F(0f, MathF.PI * 2f); return new Vector2(MathF.Cos(a), MathF.Sin(a)); }
    }
}
