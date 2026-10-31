using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>Small corner furniture: the skulls you have left and the rounds in your cylinder.</summary>
    public static class HudBits
    {
        static Color C(int r, int g, int b, int a = 255) => new Color(r, g, b, a);

        /// <summary>A cartoon skull. Crossed out once that life is gone.</summary>
        public static void Skull(Vector2 c, float r, bool lost)
        {
            Color bone = lost ? C(96, 86, 80) : C(238, 230, 212);
            Color hole = lost ? C(40, 34, 32) : C(32, 24, 22);
            Color ink = C(26, 18, 18);

            // cranium and jaw as one silhouette, then the face
            Raylib.DrawCircleV(c, r + 2f, ink);
            Raylib.DrawRectangleRounded(new Rectangle(c.X - r * 0.56f - 2f, c.Y + r * 0.45f - 2f, r * 1.12f + 4f, r * 0.72f + 4f), 0.45f, 6, ink);
            Raylib.DrawCircleV(c, r, bone);
            Raylib.DrawRectangleRounded(new Rectangle(c.X - r * 0.56f, c.Y + r * 0.45f, r * 1.12f, r * 0.72f), 0.45f, 6, bone);

            // eye sockets and nose
            Raylib.DrawCircleV(new Vector2(c.X - r * 0.38f, c.Y - r * 0.12f), r * 0.27f, hole);
            Raylib.DrawCircleV(new Vector2(c.X + r * 0.38f, c.Y - r * 0.12f), r * 0.27f, hole);
            Raylib.DrawTriangle(
                new Vector2(c.X, c.Y + r * 0.12f),
                new Vector2(c.X - r * 0.15f, c.Y + r * 0.42f),
                new Vector2(c.X + r * 0.15f, c.Y + r * 0.42f), hole);
            // teeth
            for (int i = -1; i <= 1; i++)
                Raylib.DrawLineEx(new Vector2(c.X + i * r * 0.3f, c.Y + r * 0.5f),
                                  new Vector2(c.X + i * r * 0.3f, c.Y + r * 1.1f), 2f, hole);

            if (!lost) return;
            Color x = C(196, 46, 40);
            Raylib.DrawLineEx(new Vector2(c.X - r * 1.25f, c.Y - r * 1.15f), new Vector2(c.X + r * 1.25f, c.Y + r * 1.25f), 5f, x);
            Raylib.DrawLineEx(new Vector2(c.X + r * 1.25f, c.Y - r * 1.15f), new Vector2(c.X - r * 1.25f, c.Y + r * 1.25f), 5f, x);
        }

        public static void Skulls(float cornerX, float y, int total, int lost, int dir, float r = 15f)
        {
            for (int i = 0; i < total; i++)
            {
                float x = cornerX + dir * (r + i * (r * 2.5f));
                Skull(new Vector2(x, y), r, i >= total - lost);
            }
        }
    }

    /// <summary>
    /// The cylinder of your own revolver: six chambers, one emptied per shot, each spitting a
    /// case and a wisp of smoke as it goes. Small and off to the side on purpose.
    /// </summary>
    public class AmmoWheel
    {
        class Case
        {
            public Vector2 P, V;
            public float Rot, Spin, Life, Max, S;
        }

        class Puff
        {
            public Vector2 P, V;
            public float Life, Max, R;
        }

        readonly List<Case> _cases = new();
        readonly List<Puff> _puffs = new();
        int _lastAmmo = -1;
        float _spin;            // cylinder rotation, eased toward the target
        float _spinTarget;
        float _reloadLeft, _reloadTotal;
        int _seated;            // rounds pushed home so far this reload
        float _pulse;           // little kick as each one seats

        public void Update(float dt, int ammo, int maxAmmo, Vector2 centre, int dir, float r,
                           float reloadLeft = 0f, float reloadTotal = 0f)
        {
            _pulse = MathF.Max(0f, _pulse - dt * 5f);
            _reloadLeft = reloadLeft;
            _reloadTotal = reloadTotal;

            if (reloadLeft > 0f && reloadTotal > 0.001f)
            {
                float prog = Math.Clamp(1f - reloadLeft / reloadTotal, 0f, 1f);
                int seated = Math.Clamp((int)(prog * maxAmmo), 0, maxAmmo);
                if (seated > _seated)
                {
                    _seated = seated;
                    _pulse = 1f;
                    _spinTarget += MathF.PI * 2f / maxAmmo;
                    Sfx.Play("click", 0.22f, Rng.F(1.5f, 1.8f));
                }
            }
            else _seated = 0;

            if (_lastAmmo < 0) _lastAmmo = ammo;

            if (ammo < _lastAmmo)
            {
                for (int n = 0; n < _lastAmmo - ammo; n++) Eject(centre, dir, r);
                _spinTarget += MathF.PI * 2f / maxAmmo * (_lastAmmo - ammo);
            }
            else if (ammo > _lastAmmo)
            {
                _spinTarget += MathF.PI * 2f / maxAmmo;   // a little turn as it is reloaded
            }
            _lastAmmo = ammo;

            _spin += (_spinTarget - _spin) * (1f - MathF.Exp(-10f * dt));

            for (int i = _cases.Count - 1; i >= 0; i--)
            {
                var c = _cases[i];
                c.V.Y += 900f * dt;
                c.P += c.V * dt;
                c.Rot += c.Spin * dt;
                c.Life -= dt;
                if (c.Life <= 0f) _cases.RemoveAt(i);
            }
            for (int i = _puffs.Count - 1; i >= 0; i--)
            {
                var p = _puffs[i];
                p.V.Y -= 30f * dt;
                p.P += p.V * dt;
                p.Life -= dt;
                if (p.Life <= 0f) _puffs.RemoveAt(i);
            }
        }

        void Eject(Vector2 centre, int dir, float r)
        {
            float k = r / 30f;                      // everything scales with the cylinder
            float v = MathF.Sqrt(k);
            _cases.Add(new Case
            {
                P = centre + new Vector2(dir * 10f, 12f) * k,
                V = new Vector2(dir * Rng.F(40f, 90f), Rng.F(-70f, -20f)) * v,
                Rot = Rng.F(0f, 6.28f),
                Spin = Rng.F(-12f, 12f),
                Life = 1.4f,
                Max = 1.4f,
                S = k,
            });
            for (int i = 0; i < 3; i++)
                _puffs.Add(new Puff
                {
                    P = centre + new Vector2(dir * Rng.F(2f, 14f), Rng.F(-6f, 8f)) * k,
                    V = new Vector2(dir * Rng.F(4f, 16f), Rng.F(-26f, -10f)) * v,
                    Life = Rng.F(0.5f, 0.9f),
                    Max = 0.9f,
                    R = Rng.F(3f, 6f) * k,
                });
        }

        public void Draw(Vector2 c, float r, int ammo, int maxAmmo, bool reloading)
        {
            // smoke behind the steel
            foreach (var p in _puffs)
            {
                float t = Math.Clamp(p.Life / p.Max, 0f, 1f);
                Raylib.DrawCircleV(p.P, p.R * (1.8f - t), new Color(210, 202, 188, (int)(70 * t)));
            }

            bool loading = _reloadLeft > 0f && _reloadTotal > 0.001f;
            r *= 1f + 0.07f * _pulse;          // a kick as each round seats

            Color steel = new Color(126, 132, 140, 255);
            Raylib.DrawCircleV(c, r + 3f, new Color(24, 18, 18, 255));
            Raylib.DrawCircleV(c, r, Art.Shade(steel, 0.62f));
            Raylib.DrawRing(c, r * 0.9f, r, 0f, 360f, 36, Art.Shade(steel, 0.95f));

            int shown = loading ? _seated : ammo;
            Vector2 Chamber(int i)
            {
                float a = _spin + i * MathF.PI * 2f / maxAmmo - MathF.PI * 0.5f;
                return c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (r * 0.54f);
            }

            for (int i = 0; i < maxAmmo; i++)
            {
                Vector2 p = Chamber(i);
                bool loaded = i < shown;
                Raylib.DrawCircleV(p, r * 0.21f + 1.5f, new Color(22, 16, 16, 255));
                Raylib.DrawCircleV(p, r * 0.21f, loaded ? new Color(226, 184, 96, 255) : new Color(44, 34, 32, 255));
                if (loaded) Raylib.DrawCircleV(p - new Vector2(0f, r * 0.05f), r * 0.1f, new Color(248, 222, 160, 255));
            }

            Raylib.DrawCircleV(c, r * 0.13f, Art.Shade(steel, 0.8f));

            // the round on its way into the next chamber
            if (loading && _seated < maxAmmo)
            {
                float prog = Math.Clamp(1f - _reloadLeft / _reloadTotal, 0f, 1f) * maxAmmo;
                float f = Math.Clamp(prog - _seated, 0f, 1f);
                f = f * f * (3f - 2f * f);                     // ease it home
                Vector2 to = Chamber(_seated);
                Vector2 from = c + new Vector2(0f, r * 1.9f);
                Vector2 at = Vector2.Lerp(from, to, f);

                float len = r * 0.3f;
                Vector2 up = Vector2.Normalize(to - from + new Vector2(0.001f, 0f));
                Art.Capsule(at - up * len * 0.5f, at + up * len * 0.5f, r * 0.14f, new Color(206, 172, 92, 255));
                Raylib.DrawCircleV(at + up * len * 0.5f, r * 0.15f, new Color(238, 206, 140, 255));
            }

            // the cases fall in front
            foreach (var cs in _cases)
            {
                float t = Math.Clamp(cs.Life / cs.Max, 0f, 1f);
                float s = cs.S <= 0f ? 1f : cs.S;
                Vector2 a = cs.P + Art.Rot(new Vector2(-4f * s, 0f), cs.Rot);
                Vector2 b = cs.P + Art.Rot(new Vector2(4f * s, 0f), cs.Rot);
                Art.Capsule(a, b, 2.2f * s, new Color(206, 172, 92, (int)(255 * t)));
            }
        }
    }
}
