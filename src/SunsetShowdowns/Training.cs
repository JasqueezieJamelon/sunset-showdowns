using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>
    /// A quiet range: a rail of bottles across the street and no one shooting back. Same gun,
    /// same arm, same reload, so what you practise here is what you take into a duel.
    /// </summary>
    public class Training
    {
        public class Bottle
        {
            public Vector2 Foot;        // where it stands on the rail
            public Color Glass;
            public bool Alive = true;
            public float Wobble;        // set when a near miss rattles it
            public float Phase;
        }

        public Fighter Player;
        public readonly List<Bottle> Bottles = new();
        public int Hits, Shots;
        public float Shake;

        public int Ammo = Duel.MaxAmmo;
        public float ReloadLeft;

        const float RailY = Scene.GroundY - 152f;
        const float RailX0 = 1180f, RailX1 = 1760f;

        float _fireCd;

        // the same put-it-away move as in a duel, so what you practise here is what you take out
        public bool Holstered;
        public float DrawLocked;
        readonly HolsterGesture _putAway = new HolsterGesture();
        Vector2 _drawAnchor;

        Vector2 _lastShotFrom, _lastShotTo;
        float _tracer, _flash;
        Vector2 _flashAt;
        readonly Settings _s;

        public Training(Settings s)
        {
            _s = s;
            Player = new Fighter(Roster.Player, Scene.PlayerStand, 1);
            Reset();
        }

        public void Reset()
        {
            Gore.Reset();
            Bottles.Clear();
            Color[] glass =
            {
                new Color(78, 124, 88, 255),
                new Color(124, 78, 40, 255),
                new Color(150, 168, 162, 255),
                new Color(92, 110, 134, 255),
            };
            const int count = 7;
            for (int i = 0; i < count; i++)
            {
                float t = (i + 0.5f) / count;
                Bottles.Add(new Bottle
                {
                    Foot = new Vector2(Art.Lerp(RailX0 + 40f, RailX1 - 40f, t), RailY),
                    Glass = glass[i % glass.Length],
                    Phase = i * 1.37f,
                });
            }
            Hits = 0;
            Shots = 0;
            Ammo = Duel.MaxAmmo;
            ReloadLeft = 0f;
            Holstered = false;
            DrawLocked = 0f;
            Player = new Fighter(Roster.Player, Scene.PlayerStand, 1);
        }

        public int Standing
        {
            get { int n = 0; foreach (var b in Bottles) if (b.Alive) n++; return n; }
        }

        public void Interest(out float lo, out float hi)
        {
            lo = MathF.Min(Player.Root.X - 160f, RailX0);
            hi = MathF.Max(Player.Root.X, RailX1);
        }

        public void Update(float dt, Vector2 mouse, bool firePressed, bool reloadPressed)
        {
            Shake = MathF.Max(0f, Shake - dt * 4.5f);
            _tracer = MathF.Max(0f, _tracer - dt * 9f);
            _flash = MathF.Max(0f, _flash - dt * 14f);
            _fireCd = MathF.Max(0f, _fireCd - dt);
            Scene.Update(dt);

            if (reloadPressed && Ammo < Duel.MaxAmmo && ReloadLeft <= 0f)
            {
                ReloadLeft = Duel.ReloadTime;
                for (int i = 0; i < 6; i++) Gore.Casing(Player.Arm.Hand, Player.Face);
                Sfx.Play("holster", 0.7f, 0.85f);
            }
            if (ReloadLeft > 0f)
            {
                ReloadLeft -= dt;
                if (ReloadLeft <= 0f) { Ammo = Duel.MaxAmmo; Sfx.Play("holster", 0.7f, 1.2f); }
            }

            // slow and straight down to the hip stows it; anything after that brings it back out
            DrawLocked = MathF.Max(0f, DrawLocked - dt);
            if (Holstered)
            {
                // his hand stays off it for a moment, or the move that stowed it draws it again
                if (DrawLocked > 0f) _drawAnchor = mouse;
                else if (firePressed || Vector2.Distance(mouse, _drawAnchor) > 10f)
                {
                    Holstered = false;
                    Sfx.Play("holster", 0.8f, 0.9f);
                }
                _putAway.Reset(mouse);
            }
            else if (_putAway.Update(dt, mouse, Player.Arm.HolsterPoint))
            {
                Holstered = true;
                _drawAnchor = mouse;
                DrawLocked = Duel.DrawLock;
                Sfx.Play("holster", 0.75f, 0.72f);
            }

            if (firePressed && !Holstered) Shoot();

            foreach (var b in Bottles)
                if (b.Wobble > 0f) b.Wobble = MathF.Max(0f, b.Wobble - dt * 2.4f);

            Player.Arm.AimAt = mouse;
            Player.Update(dt, Player.Arm.AimHandFor(mouse), Fighter.ArmWeight, Holstered);
            Gore.Update(dt);
        }

        void Shoot()
        {
            if (ReloadLeft > 0f) return;
            if (_fireCd > 0f) return;
            if (Ammo <= 0) { Sfx.Play("click", 0.7f, 1.2f); return; }

            Ammo--;
            Shots++;
            _fireCd = 0.26f;

            Vector2 from = Player.Arm.Muzzle;
            Vector2 dir = Player.Arm.BarrelDir;
            Player.Arm.Kick(1f);
            Sfx.Play("shot", 1f, Rng.F(0.94f, 1.07f));
            Gore.MuzzleSmoke(from, dir);
            Gore.Casing(Player.Arm.Hand, Player.Face);
            _flash = 1f;
            _flashAt = from;
            Shake = MathF.Max(Shake, 0.5f);

            const float len = 2800f;
            _lastShotFrom = from;
            _lastShotTo = from + dir * len;
            _tracer = 1f;

            // nearest bottle on the line
            Bottle best = null;
            float bestT = float.MaxValue;
            foreach (var b in Bottles)
            {
                if (!b.Alive) continue;
                float t = Art.RayCapsule(from, dir, len,
                    b.Foot + new Vector2(0f, -62f), b.Foot + new Vector2(0f, -8f), 15f);
                if (t >= 0f && t < bestT) { bestT = t; best = b; }
            }

            float groundT = dir.Y > 0.001f ? (Scene.GroundY - from.Y) / dir.Y : float.MaxValue;
            float railT = Art.RayCapsule(from, dir, len, new Vector2(RailX0, RailY + 12f), new Vector2(RailX1, RailY + 12f), 12f);

            if (best != null && bestT <= MathF.Min(groundT, railT < 0f ? float.MaxValue : railT))
            {
                Vector2 p = from + dir * bestT;
                _lastShotTo = p;
                best.Alive = false;
                Hits++;
                Shake = MathF.Max(Shake, 0.7f);
                Sfx.Play("glass", 0.9f, Rng.F(0.92f, 1.12f));
                Gore.Shatter(p, dir, best.Glass, 16);
                // the neighbours feel it
                foreach (var b in Bottles)
                    if (b.Alive && MathF.Abs(b.Foot.X - best.Foot.X) < 150f) b.Wobble = 1f;
                return;
            }

            if (railT >= 0f && railT < groundT)
            {
                Vector2 p = from + dir * railT;
                _lastShotTo = p;
                Gore.DustPuff(p, dir, 0.7f);
                Sfx.Play("ric", 0.5f, Rng.F(0.9f, 1.4f));
                return;
            }

            if (groundT > 0f && groundT < len)
            {
                Vector2 gp = from + dir * groundT;
                _lastShotTo = gp;
                Gore.DustPuff(gp, dir, 1.2f);
                Sfx.Play("ric", 0.5f, Rng.F(0.8f, 1.3f));
                return;
            }

            Sfx.Play("ric", 0.35f, Rng.F(1.1f, 1.6f));
        }

        // ---------- drawing ----------
        public void DrawWorld(float viewX0, float viewX1)
        {
            Scene.Draw(viewX0, viewX1);
            Gore.DrawDecals();

            DrawRail();
            foreach (var b in Bottles) if (b.Alive) DrawBottle(b);

            Player.Draw(_s.ShowZones);
            Gore.Draw();

            if (_tracer > 0f)
                Raylib.DrawLineEx(_lastShotFrom, _lastShotTo, 2.5f,
                    Art.Alpha(new Color(255, 236, 180, 255), _tracer * 0.5f));

            if (_flash > 0f)
            {
                float k = _flash;
                Raylib.DrawCircleV(_flashAt, 46f * k, Art.Alpha(new Color(255, 226, 150, 255), 0.75f * k));
                Raylib.DrawCircleV(_flashAt, 96f * k, Art.Alpha(new Color(255, 196, 110, 255), 0.25f * k));
            }

        }

        void DrawRail()
        {
            Color wood = new Color(104, 72, 50, 255);
            Art.Shape(new[]
            {
                new Vector2(RailX0, RailY + 4f), new Vector2(RailX1, RailY + 4f),
                new Vector2(RailX1, RailY + 22f), new Vector2(RailX0, RailY + 22f)
            }, wood, 3f);

            foreach (float px in new[] { RailX0 + 40f, (RailX0 + RailX1) * 0.5f, RailX1 - 40f })
                Art.Shape(new[]
                {
                    new Vector2(px - 9f, RailY + 20f), new Vector2(px + 9f, RailY + 20f),
                    new Vector2(px + 7f, Scene.GroundY), new Vector2(px - 7f, Scene.GroundY)
                }, Art.Shade(wood, 0.82f), 3f);
        }

        void DrawBottle(Bottle b)
        {
            float lean = b.Wobble * MathF.Sin((float)Raylib.GetTime() * 18f + b.Phase) * 0.12f;
            Vector2 F = b.Foot;
            Vector2 P(float x, float y)
            {
                Vector2 v = new Vector2(x, y);
                if (MathF.Abs(lean) > 0.0001f) v = Art.Rot(v, lean);
                return F + v;
            }

            // body, shoulder, neck, lip: one silhouette
            Art.Shape(new[]
            {
                P(-13f, -2f), P(13f, -2f), P(13f, -34f), P(8f, -44f),
                P(6f, -58f), P(7f, -64f), P(-7f, -64f), P(-6f, -58f),
                P(-8f, -44f), P(-13f, -34f)
            }, b.Glass, 3f);

            // a highlight down the glass
            Art.Seam(P(-7f, -8f), P(-7f, -36f), 4f, Art.Shade(b.Glass, 1.45f));
            // label
            Art.Shape(new[] { P(-11f, -14f), P(11f, -14f), P(11f, -26f), P(-11f, -26f) },
                Art.Shade(b.Glass, 0.62f), 2.5f);
        }
    }
}
