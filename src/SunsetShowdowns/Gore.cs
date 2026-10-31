using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    public enum PKind { Blood, Mist, Gib, Dust, Smoke, Casing, Hat, Gun, Bone, Glass }

    public class Particle
    {
        public PKind Kind;
        public Vector2 Pos, Vel;
        public float Rad, Life, MaxLife, Rot, RotVel, Drag = 0.2f, Bounce = 0.25f;
        public Color Col;
        public bool Rest;
        public int Style;
        public Palette Pal;
        public int Face = 1;
        public float Scale = 1f;
        public Vector2[] Shape;
    }

    /// <summary>Blood, bone, dust, smoke, dropped props — plus the permanent stain layer on the street.</summary>
    public static class Gore
    {
        public static GoreLevel Level = GoreLevel.Full;
        public static float Ground = 905f;

        static readonly List<Particle> Parts = new();
        static RenderTexture2D _decals;
        static bool _decalsReady;
        struct Splat { public Vector2 P; public float R; public Color C; }
        static readonly List<Splat> _pending = new();

        // The stain layer covers far more ground than one screen, because a fight can travel.
        public static readonly Vector2 DecalOrigin = new Vector2(-2100f, 260f);
        public const int DecalW = 6144, DecalH = 896;

        public static void Init()
        {
            _decals = Raylib.LoadRenderTexture(DecalW, DecalH);
            _decalsReady = true;
            ClearDecals();
        }

        public static void ClearDecals()
        {
            if (!_decalsReady) return;
            Raylib.BeginTextureMode(_decals);
            Raylib.ClearBackground(new Color(0, 0, 0, 0));
            Raylib.EndTextureMode();
            _pending.Clear();
        }

        public static void Reset()
        {
            Parts.Clear();
            ClearDecals();
        }

        public static void Stamp(Vector2 p, float r, Color c)
        {
            if (Level == GoreLevel.Off) return;
            _pending.Add(new Splat { P = p, R = r, C = c });
        }

        /// <summary>Must run outside any other render-target pass.</summary>
        public static void FlushStamps()
        {
            if (!_decalsReady || _pending.Count == 0) return;
            Raylib.BeginTextureMode(_decals);
            foreach (var s in _pending)
            {
                Vector2 p = s.P - DecalOrigin;
                Raylib.DrawCircleV(p, s.R, s.C);
                int n = (int)Math.Clamp(s.R * 0.5f, 1f, 7f);
                for (int i = 0; i < n; i++)
                    Raylib.DrawCircleV(p + Rng.Dir() * Rng.F(s.R * 0.4f, s.R * 1.6f), Rng.F(1.5f, s.R * 0.5f), s.C);
            }
            Raylib.EndTextureMode();
            _pending.Clear();
        }

        public static void DrawDecals()
        {
            if (!_decalsReady) return;
            Raylib.DrawTextureRec(_decals.Texture, new Rectangle(0f, 0f, DecalW, -DecalH), DecalOrigin, Color.White);
        }

        public static void Unload()
        {
            if (_decalsReady) Raylib.UnloadRenderTexture(_decals);
            _decalsReady = false;
        }

        static Color Blood(float v = 1f) => new Color((int)(128 * v), (int)(14 * v), (int)(18 * v), 255);
        static Color BloodDark => new Color(72, 8, 12, 255);

        static void Add(Particle p) { if (Parts.Count < 1400) Parts.Add(p); }

        static float GoreScale => Level switch { GoreLevel.Off => 0f, GoreLevel.Light => 0.45f, _ => 1f };

        // ---------- spawners ----------
        /// <summary>Spray from an entry wound. <paramref name="gore"/> scales with what was hit.</summary>
        public static void Impact(Vector2 p, Vector2 dir, Zone zone, float gore, bool lethal)
        {
            Sfx.Play(zone == Zone.Head ? "skull" : "flesh", 0.85f, Rng.F(0.92f, 1.1f));
            if (Level == GoreLevel.Off)
            {
                for (int i = 0; i < 6; i++)
                    Add(new Particle { Kind = PKind.Dust, Pos = p, Vel = Rng.Dir() * Rng.F(40f, 160f), Rad = Rng.F(3f, 6f), MaxLife = Life(0.5f), Life = Life(0.5f), Col = new Color(120, 90, 80, 180) });
                return;
            }

            int n = (int)((zone == Zone.Head ? 34 : 20) * GoreScale * Math.Clamp(gore, 0.5f, 2.4f)) + 4;
            for (int i = 0; i < n; i++)
            {
                Vector2 v = dir * Rng.F(90f, 520f) + Rng.Dir() * Rng.F(40f, 260f);
                Add(new Particle
                {
                    Kind = PKind.Blood,
                    Pos = p + Rng.Dir() * 6f,
                    Vel = v,
                    Rad = Rng.F(2.2f, 6.5f),
                    MaxLife = 2.4f,
                    Life = 2.4f,
                    Col = Rng.Chance(0.3f) ? BloodDark : Blood(Rng.F(0.8f, 1.1f)),
                });
            }
            // fine mist on the exit side
            int m = (int)(12 * GoreScale);
            for (int i = 0; i < m; i++)
                Add(new Particle
                {
                    Kind = PKind.Mist,
                    Pos = p + dir * Rng.F(6f, 30f),
                    Vel = dir * Rng.F(160f, 620f) + Rng.Dir() * 90f,
                    Rad = Rng.F(3f, 9f),
                    MaxLife = 0.55f,
                    Life = 0.55f,
                    Col = Art.Alpha(Blood(0.95f), 0.5f),
                });

            if (lethal || zone == Zone.Head || gore > 1.2f)
            {
                int g = (int)((zone == Zone.Head ? 10 : 4) * GoreScale * gore);
                for (int i = 0; i < g; i++) Gib(p, dir, zone == Zone.Head);
            }
        }

        /// <summary>The far side of a through-and-through: most of the mess leaves this way.</summary>
        public static void ExitBurst(Vector2 p, Vector2 dir, float gore)
        {
            if (Level == GoreLevel.Off) return;
            int n = (int)(26 * GoreScale * Math.Clamp(gore, 0.6f, 2.4f));
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Kind = PKind.Blood,
                    Pos = p,
                    Vel = dir * Rng.F(240f, 900f) + Rng.Dir() * Rng.F(60f, 300f),
                    Rad = Rng.F(2.5f, 7f),
                    MaxLife = 2.6f, Life = 2.6f,
                    Col = Rng.Chance(0.3f) ? BloodDark : Blood(Rng.F(0.8f, 1.1f)),
                });
            int m = (int)(14 * GoreScale * gore);
            for (int i = 0; i < m; i++)
                Add(new Particle
                {
                    Kind = PKind.Mist,
                    Pos = p + dir * Rng.F(4f, 40f),
                    Vel = dir * Rng.F(300f, 1000f) + Rng.Dir() * 120f,
                    Rad = Rng.F(4f, 12f),
                    MaxLife = 0.6f, Life = 0.6f,
                    Col = Art.Alpha(Blood(0.95f), 0.5f),
                });
            if (gore > 1.4f)
                for (int i = 0; i < (int)(6 * GoreScale * gore); i++) Gib(p, dir, true);
        }

        /// <summary>
        /// Pieces of the man himself: skin on one side, raw on the other. They tumble, land and
        /// stay on the street, so what came off him is still there when the dust settles.
        /// </summary>
        public static void Chunks(Vector2 p, Vector2 dir, Color skin, int count)
        {
            if (Level == GoreLevel.Off) return;
            count = (int)(count * GoreScale);
            for (int i = 0; i < count; i++)
            {
                int sides = Rng.I(5, 8);
                var shape = new Vector2[sides];
                float baseR = Rng.F(6f, 14f);
                for (int k = 0; k < sides; k++)
                {
                    float a = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.55f, 1.3f);
                    shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                }
                Add(new Particle
                {
                    Kind = PKind.Gib,
                    Pos = p + Rng.Dir() * 8f,
                    Vel = dir * Rng.F(150f, 640f) + Rng.Dir() * Rng.F(80f, 320f) + new Vector2(0f, -220f),
                    Rad = baseR,
                    MaxLife = 14f, Life = 14f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-16f, 16f),
                    Col = Rng.Chance(0.45f) ? skin : Art.Mix(skin, Blood(0.9f), 0.75f),
                    Shape = shape,
                    Bounce = 0.28f,
                });
            }
        }

        /// <summary>
        /// The piece of him that came off: skin on the outside, bone underneath. Bigger and
        /// slower than the spray, so it lands somewhere you can see it.
        /// </summary>
        public static void SkullPieces(Vector2 p, Vector2 dir, Color skin, int count)
        {
            if (Level == GoreLevel.Off) return;
            count = Math.Max(1, (int)(count * GoreScale));
            for (int i = 0; i < count; i++)
            {
                int sides = Rng.I(4, 7);
                var shape = new Vector2[sides];
                float baseR = Rng.F(9f, 17f);
                for (int k = 0; k < sides; k++)
                {
                    float a = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.6f, 1.25f);
                    shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                }
                Add(new Particle
                {
                    Kind = PKind.Bone,
                    Pos = p + Rng.Dir() * 8f,
                    Vel = dir * Rng.F(120f, 420f) + Rng.Dir() * Rng.F(60f, 240f) + new Vector2(0f, -260f),
                    Rad = baseR,
                    MaxLife = 20f, Life = 20f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-10f, 10f),
                    Col = i == 0 ? new Color(226, 214, 186, 255) : Art.Mix(skin, new Color(214, 202, 174, 255), 0.5f),
                    Shape = shape,
                    Bounce = 0.22f,
                });
            }
        }

        /// <summary>Glass going everywhere. Shards keep their colour and skid rather than splat.</summary>
        public static void Shatter(Vector2 p, Vector2 dir, Color glass, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int sides = Rng.I(3, 6);
                var shape = new Vector2[sides];
                float baseR = Rng.F(3f, 9f);
                for (int k = 0; k < sides; k++)
                {
                    float a = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.5f, 1.4f);
                    shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                }
                Add(new Particle
                {
                    Kind = PKind.Glass,         // skids and keeps its colour, and leaves no blood
                    Pos = p + Rng.Dir() * 6f,
                    Vel = dir * Rng.F(60f, 360f) + Rng.Dir() * Rng.F(90f, 420f) + new Vector2(0f, -180f),
                    Rad = baseR,
                    MaxLife = 8f, Life = 8f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-18f, 18f),
                    Col = Rng.Chance(0.3f) ? Art.Shade(glass, 1.4f) : glass,
                    Shape = shape,
                    Bounce = 0.42f,
                });
            }
            for (int i = 0; i < 6; i++)
                Add(new Particle
                {
                    Kind = PKind.Dust,
                    Pos = p,
                    Vel = Rng.Dir() * Rng.F(40f, 160f),
                    Rad = Rng.F(3f, 7f),
                    MaxLife = 0.6f, Life = 0.6f,
                    Col = Art.Alpha(Art.Shade(glass, 1.6f), 0.5f),
                    Drag = 2f,
                });
        }

        /// <summary>A pressurised wound throwing blood in time with the heartbeat.</summary>
        public static void Spurt(Vector2 p, Vector2 dir, float power)
        {
            if (Level == GoreLevel.Off) return;
            int n = (int)(7 * GoreScale * Math.Clamp(power, 0.3f, 1.4f)) + 2;
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Kind = PKind.Blood,
                    Pos = p,
                    Vel = dir * Rng.F(160f, 520f) * power + Rng.Dir() * 50f,
                    Rad = Rng.F(2.5f, 5.5f),
                    MaxLife = 2.2f, Life = 2.2f,
                    Col = Blood(Rng.F(0.85f, 1.1f)),
                });
        }

        public static void HeadBurst(Vector2 p, Vector2 dir, Color skin)
        {
            if (Level == GoreLevel.Off) return;
            int n = (int)(40 * GoreScale);
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Kind = PKind.Blood,
                    Pos = p,
                    Vel = dir * Rng.F(200f, 760f) + Rng.Dir() * Rng.F(80f, 420f) + new Vector2(0f, -180f),
                    Rad = Rng.F(2.5f, 7.5f),
                    MaxLife = 2.6f, Life = 2.6f,
                    Col = Rng.Chance(0.35f) ? BloodDark : Blood(Rng.F(0.85f, 1.1f)),
                });
            int g = (int)(12 * GoreScale);
            for (int i = 0; i < g; i++) Gib(p, dir, true);
        }

        static void Gib(Vector2 p, Vector2 dir, bool big)
        {
            int sides = Rng.I(5, 8);
            var shape = new Vector2[sides];
            float baseR = Rng.F(5f, big ? 13f : 9f);
            for (int i = 0; i < sides; i++)
            {
                float a = i / (float)sides * MathF.PI * 2f;
                float r = baseR * Rng.F(0.6f, 1.25f);
                shape[i] = new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
            }
            bool bone = Rng.Chance(0.14f);
            Add(new Particle
            {
                Kind = bone ? PKind.Bone : PKind.Gib,
                Pos = p,
                Vel = dir * Rng.F(120f, 560f) + Rng.Dir() * Rng.F(60f, 300f) + new Vector2(0f, -200f),
                Rad = baseR,
                MaxLife = 9f, Life = 9f,
                Rot = Rng.F(0f, 6.28f),
                RotVel = Rng.F(-14f, 14f),
                Col = bone ? new Color(202, 186, 154, 255) : Blood(Rng.F(0.65f, 1.0f)),
                Shape = shape,
                Bounce = 0.3f,
            });
        }

        public static void Drip(Vector2 p, Color skin)
        {
            if (Level == GoreLevel.Off) return;
            Add(new Particle
            {
                Kind = PKind.Blood,
                Pos = p,
                Vel = new Vector2(Rng.F(-20f, 20f), Rng.F(20f, 90f)),
                Rad = Rng.F(2f, 4.5f),
                MaxLife = 3f, Life = 3f,
                Col = BloodDark,
            });
        }

        public static void Pool(Vector2 p, float r)
        {
            Stamp(new Vector2(p.X, Ground - 2f), r, new Color(86, 10, 14, 215));
        }

        public static void DustPuff(Vector2 p, Vector2 dir, float power = 1f)
        {
            int n = (int)(10 * power) + 4;
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Kind = PKind.Dust,
                    Pos = p,
                    Vel = -dir * Rng.F(60f, 220f) + Rng.Dir() * Rng.F(40f, 180f) - new Vector2(0f, Rng.F(20f, 120f)),
                    Rad = Rng.F(5f, 16f) * power,
                    MaxLife = 1.1f, Life = 1.1f,
                    Col = new Color(176, 148, 112, 190),
                    Drag = 1.8f,
                });
        }

        public static void MuzzleSmoke(Vector2 p, Vector2 dir)
        {
            for (int i = 0; i < 10; i++)
                Add(new Particle
                {
                    Kind = PKind.Smoke,
                    Pos = p + dir * Rng.F(0f, 30f),
                    Vel = dir * Rng.F(60f, 320f) + Rng.Dir() * 60f + new Vector2(0f, -40f),
                    Rad = Rng.F(6f, 18f),
                    MaxLife = 1.4f, Life = 1.4f,
                    Col = new Color(206, 198, 184, 150),
                    Drag = 1.4f,
                });
        }

        public static void Casing(Vector2 p, int face)
        {
            Add(new Particle
            {
                Kind = PKind.Casing,
                Pos = p,
                Vel = new Vector2(-face * Rng.F(60f, 180f), -Rng.F(180f, 320f)),
                Rad = 4f,
                MaxLife = 6f, Life = 6f,
                Rot = Rng.F(0f, 6f), RotVel = Rng.F(-18f, 18f),
                Col = new Color(206, 172, 92, 255),
                Bounce = 0.4f,
            });
        }

        public static void LaunchHat(Vector2 p, Vector2 vel, Color hat, Color band, int style, int face, float scale)
        {
            Add(new Particle
            {
                Kind = PKind.Hat,
                Pos = p, Vel = vel,
                Rad = 40f * scale, Scale = scale,
                MaxLife = 20f, Life = 20f,
                Rot = 0f, RotVel = Rng.F(-9f, 9f),
                Col = hat, Style = style, Face = face,
                Pal = new Palette { Hat = hat, HatBand = band },
                Bounce = 0.35f,
            });
        }

        public static void LaunchGun(Vector2 p, Vector2 vel, float angle, Palette pal, int face)
        {
            Add(new Particle
            {
                Kind = PKind.Gun,
                Pos = p, Vel = vel,
                Rad = 22f,
                MaxLife = 20f, Life = 20f,
                Rot = angle, RotVel = Rng.F(-12f, 12f),
                Pal = pal, Face = face,
                Col = pal.Metal,
                Bounce = 0.2f,
            });
        }

        static float Life(float f) => f;

        // ---------- sim ----------
        public static void Update(float dt)
        {
            for (int i = Parts.Count - 1; i >= 0; i--)
            {
                var p = Parts[i];
                if (p.Rest)
                {
                    p.Life -= dt * 0.25f;
                    if (p.Life <= 0f && p.Kind != PKind.Hat && p.Kind != PKind.Gun) Parts.RemoveAt(i);
                    continue;
                }

                float g = p.Kind switch
                {
                    PKind.Smoke => -140f,
                    PKind.Mist => 240f,
                    PKind.Dust => -40f,
                    _ => 1750f,
                };
                p.Vel += new Vector2(0f, g) * dt;
                p.Vel *= MathF.Max(0f, 1f - p.Drag * dt);
                p.Pos += p.Vel * dt;
                p.Rot += p.RotVel * dt;
                p.Life -= dt;

                if (p.Pos.Y >= Ground && p.Kind != PKind.Smoke && p.Kind != PKind.Dust && p.Kind != PKind.Mist)
                {
                    p.Pos.Y = Ground;
                    if (p.Kind == PKind.Blood)
                    {
                        Stamp(p.Pos, p.Rad * Rng.F(1.4f, 2.8f), Art.Alpha(p.Col, 0.85f));
                        Parts.RemoveAt(i);
                        continue;
                    }
                    p.Vel.Y *= -p.Bounce;
                    p.Vel.X *= 0.6f;
                    p.RotVel *= 0.5f;
                    if (p.Kind == PKind.Gib || p.Kind == PKind.Bone)
                        Stamp(p.Pos, p.Rad * 1.6f, Art.Alpha(Blood(0.8f), 0.8f));
                    if (MathF.Abs(p.Vel.Y) < 40f) { p.Rest = true; p.Vel = Vector2.Zero; }
                }

                if (p.Life <= 0f || p.Pos.X < DecalOrigin.X - 400f || p.Pos.X > DecalOrigin.X + DecalW + 400f) Parts.RemoveAt(i);
            }
        }

        public static void Draw()
        {
            foreach (var p in Parts)
            {
                float t = Math.Clamp(p.Life / p.MaxLife, 0f, 1f);
                switch (p.Kind)
                {
                    case PKind.Blood:
                        Raylib.DrawCircleV(p.Pos, p.Rad, p.Col);
                        break;
                    case PKind.Mist:
                        Raylib.DrawCircleV(p.Pos, p.Rad * (1.6f - t), Art.Alpha(p.Col, t * 0.55f));
                        break;
                    case PKind.Smoke:
                        Raylib.DrawCircleV(p.Pos, p.Rad * (2.2f - t * 1.2f), Art.Alpha(p.Col, t * 0.45f));
                        break;
                    case PKind.Dust:
                        Raylib.DrawCircleV(p.Pos, p.Rad * (1.8f - t), Art.Alpha(p.Col, t * 0.6f));
                        break;
                    case PKind.Casing:
                        {
                            Vector2 a = p.Pos + Art.Rot(new Vector2(-5f, 0f), p.Rot);
                            Vector2 b = p.Pos + Art.Rot(new Vector2(5f, 0f), p.Rot);
                            Art.Capsule(a, b, 2.4f, p.Col);
                            break;
                        }
                    case PKind.Gib:
                    case PKind.Bone:
                    case PKind.Glass:
                        {
                            var pts = new Vector2[p.Shape.Length];
                            bool glass = p.Kind == PKind.Glass;
                            float squash = p.Rest && !glass ? 0.4f : 1f;   // meat flattens, glass does not
                            for (int i = 0; i < pts.Length; i++)
                            {
                                var v = Art.Rot(p.Shape[i], p.Rot);
                                pts[i] = p.Pos + new Vector2(v.X, v.Y * squash);
                            }
                            if (p.Rest && !glass)
                            {
                                // settled meat reads as a wet smear, not a paper cut-out
                                Raylib.DrawCircleV(p.Pos, p.Rad * 1.5f, Art.Alpha(Blood(0.55f), 0.5f));
                                Art.Poly(pts, p.Col);
                            }
                            else Art.Shape(pts, p.Col, 1.2f);
                            break;
                        }
                    case PKind.Hat:
                        DrawLooseHat(p);
                        break;
                    case PKind.Gun:
                        DrawLooseGun(p);
                        break;
                }
            }
        }

        static void DrawLooseHat(Particle p)
        {
            float s = p.Scale;
            Vector2 T(float x, float y) => p.Pos + Art.Rot(new Vector2(x, y) * s, p.Rot);
            switch (p.Style)
            {
                case 1:
                    Art.Shape(new[] { T(-38f, 4f), T(42f, 4f), T(40f, -6f), T(-36f, -6f) }, p.Pal.Hat, 3f);
                    Art.Shape(new[] { T(-26f, -4f), T(-20f, -30f), T(26f, -30f), T(32f, -4f) }, p.Pal.Hat, 3f);
                    break;
                case 2:
                    Art.Shape(new[] { T(-52f, 4f), T(56f, 4f), T(54f, -8f), T(-50f, -8f) }, p.Pal.Hat, 3f);
                    Art.Shape(new[] { T(-28f, -6f), T(-26f, -34f), T(28f, -34f), T(30f, -6f) }, p.Pal.Hat, 3f);
                    break;
                default:
                    Art.Shape(new[] { T(-58f, 2f), T(-46f, -8f), T(48f, -10f), T(62f, 0f), T(46f, 8f), T(-44f, 10f) }, p.Pal.Hat, 3f);
                    Art.Shape(new[] { T(-30f, -6f), T(-22f, -40f), T(4f, -48f), T(28f, -38f), T(34f, -6f) }, p.Pal.Hat, 3f);
                    break;
            }
            Raylib.DrawLineEx(T(-29f, -12f), T(33f, -12f), 7f * s, p.Pal.HatBand);
        }

        /// <summary>
        /// A gun knocked out of a hand is the same gun it was a moment ago, so it is the real
        /// drawing rather than a stand-in. It is anchored at the web of the hand, which is up
        /// at one end of it, so the particle is offset back to the middle of the gun and spins
        /// about that instead of about its own butt. The two passes are done here because the
        /// gore layer is drawn once, with no ink pass of its own.
        /// </summary>
        static void DrawLooseGun(Particle p)
        {
            const float s = 1f;
            Vector2 at = p.Pos - Art.Rot(new Vector2(29f, -5f * p.Face) * s, p.Rot);
            Art.InkPass = true;
            Fighter.Revolver(at, p.Rot, p.Pal, s, p.Face);
            Art.InkPass = false;
            Fighter.Revolver(at, p.Rot, p.Pal, s, p.Face);
        }
    }
}
