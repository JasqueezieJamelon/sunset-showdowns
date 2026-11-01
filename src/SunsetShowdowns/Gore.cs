using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    public enum PKind { Blood, Mist, Gib, Dust, Smoke, Casing, Hat, Gun, Bone, Glass, Brain }

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

        // What is inside a head, and it is not the colour of what is inside a chest. Brain is
        // pale and pink and slightly grey - nearer raw sausage meat than blood - and the blood
        // that comes with it only darkens it in patches. Thrown in plain red it read as more
        // of the same mess coming out of a hole, which is exactly what it should not read as:
        // the whole point of a head coming apart is that something different comes out.
        public static readonly Color BrainPale = new Color(222, 176, 172, 255);
        public static readonly Color BrainMid = new Color(196, 138, 134, 255);
        public static readonly Color BrainDeep = new Color(158, 90, 92, 255);
        /// the thin wash that soaks in under a piece of it
        static readonly Color BrainWet = new Color(176, 104, 104, 255);
        /// the aerosol off a skull coming apart: pink, because it is mostly not blood
        static readonly Color BrainMist = new Color(224, 164, 166, 255);
        /// bone, seen from the outside and from the broken edge
        public static readonly Color SkullBone = new Color(232, 222, 196, 255);
        static readonly Color SkullInner = new Color(198, 150, 140, 255);

        static Color BrainTone() =>
            Rng.Chance(0.18f) ? BrainDeep :
            Rng.Chance(0.45f) ? BrainMid : BrainPale;

        static void Add(Particle p) { if (Parts.Count < 1400) Parts.Add(p); }

        /// <summary>
        /// Dev only. Called the instant after a burst, before anything has had time to move,
        /// this reports where each sort of thing STARTED - which is the only way to tell
        /// whether the brain came out of the brain or out of a convenient average point.
        /// </summary>
        public static void Audit(Vector2 brainAt, Vector2 skullAt, float skullR,
                                 out int brainN, out int boneN, out float brainFar, out float boneNearRim)
        {
            brainN = boneN = 0;
            brainFar = 0f;
            boneNearRim = 0f;
            foreach (var p in Parts)
            {
                if (p.Kind == PKind.Brain)
                {
                    brainN++;
                    brainFar = MathF.Max(brainFar, Vector2.Distance(p.Pos, brainAt));
                }
                else if (p.Kind == PKind.Bone)
                {
                    boneN++;
                    // how far off the dome it started, as a fraction of the dome
                    boneNearRim = MathF.Max(boneNearRim,
                        MathF.Abs(Vector2.Distance(p.Pos, skullAt) - skullR * 0.875f) / skullR);
                }
            }
        }

        /// <summary>Dev only: is this the colour of brain rather than the colour of blood?</summary>
        public static bool LooksLikeBrain(Color c)
            => c.R > 150 && c.R > c.G + 25 && MathF.Abs(c.G - c.B) < 22 && c.G > 80;

        public static bool AllBrainPink()
        {
            foreach (var p in Parts)
                if (p.Kind == PKind.Brain && !LooksLikeBrain(p.Col)) return false;
            return true;
        }

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
                // A hit throws a lot of liquid and only a little solid. It used to throw
                // ten lumps of meat out of a head, which read as gravel.
                int g = (int)((zone == Zone.Head ? 3 : 1) * GoreScale * gore);
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
                for (int i = 0; i < (int)(2 * GoreScale * gore); i++) Gib(p, dir, true);
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
        /// A sheet of face. Not a pellet of meat - a wide, flat, slow piece with his own skin
        /// colour on it, so when a head comes apart you can see what used to be on the front
        /// of it turning over in the air on its way to the street.
        /// </summary>
        public static void FaceSkin(Vector2 p, Vector2 dir, Color skin, int count)
        {
            if (Level == GoreLevel.Off) return;
            count = Math.Max(1, (int)(count * GoreScale));
            for (int i = 0; i < count; i++)
            {
                int sides = Rng.I(5, 8);
                var shape = new Vector2[sides];
                float baseR = Rng.F(14f, 24f);
                // flat: long one way and thin the other, so it reads as a sheet and not a lump
                float squash = Rng.F(0.26f, 0.46f);
                for (int k = 0; k < sides; k++)
                {
                    float a = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.72f, 1.15f);
                    shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr * squash);
                }
                Add(new Particle
                {
                    Kind = PKind.Gib,
                    Pos = p + Rng.Dir() * 6f,
                    Vel = dir * Rng.F(90f, 320f) + Rng.Dir() * Rng.F(50f, 190f) + new Vector2(0f, -300f),
                    Rad = baseR,
                    MaxLife = 18f, Life = 18f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-7f, 7f),
                    Col = i == 0 ? skin : Art.Mix(skin, Blood(0.9f), 0.35f),
                    Shape = shape,
                    Bounce = 0.14f,
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

        /// <summary>
        /// A piece that was hanging off somebody and has stopped hanging off them. It keeps
        /// the swing it had when it let go, so it carries on the way it was already going.
        /// </summary>
        public static void DropPiece(Vector2 at, Vector2 vel, Color col, float rad)
        {
            if (Level == GoreLevel.Off) return;
            int sides = Rng.I(5, 8);
            var shape = new Vector2[sides];
            for (int k = 0; k < sides; k++)
            {
                float a = k / (float)sides * MathF.PI * 2f;
                float rr = rad * Rng.F(0.7f, 1.2f);
                shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr * 0.5f);
            }
            Add(new Particle
            {
                Kind = PKind.Gib,
                Pos = at,
                Vel = vel + new Vector2(Rng.F(-40f, 40f), Rng.F(-60f, 10f)),
                Rad = rad,
                MaxLife = 20f, Life = 20f,
                Rot = Rng.F(0f, 6.28f),
                RotVel = Rng.F(-5f, 5f),
                Col = col,
                Shape = shape,
                Bounce = 0.1f,
            });
        }

        /// <summary>
        /// What comes out of a brain. Soft, lobed, wet lumps - not the jagged shards a bone
        /// breaks into - and pale pink rather than red.
        /// </summary>
        public static void BrainMatter(Vector2 at, Vector2 dir, int count, float power = 1f)
        {
            if (Level == GoreLevel.Off) return;
            count = Math.Max(1, (int)(count * GoreScale));
            for (int i = 0; i < count; i++)
            {
                int sides = Rng.I(7, 11);
                var shape = new Vector2[sides];
                float baseR = Rng.F(6f, 15f);
                // lobed, not spiked: every corner stays near the radius, so it comes out
                // looking like something soft torn apart rather than something shattered
                for (int k = 0; k < sides; k++)
                {
                    float a = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.82f, 1.18f);
                    shape[k] = new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                }
                Add(new Particle
                {
                    Kind = PKind.Brain,
                    Pos = at + Rng.Dir() * Rng.F(0f, 9f),
                    Vel = dir * Rng.F(120f, 540f) * power + Rng.Dir() * Rng.F(70f, 330f) * power
                          + new Vector2(0f, -220f * power),
                    Rad = baseR,
                    MaxLife = 16f, Life = 16f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-8f, 8f),
                    Col = BrainTone(),
                    Shape = shape,
                    Bounce = 0.08f,     // it does not bounce. It lands and it stays landed.
                    Drag = 0.5f,
                });
            }
        }

        /// <summary>
        /// A skull coming apart. The pieces do not come from one point - they come off the
        /// vault, each one from its own place on the dome, flying outward from the middle of
        /// the head as well as along the bullet. Ivory on the outside, pink on the break.
        /// </summary>
        public static void SkullBurst(Vector2 centre, float radius, Vector2 dir, int count)
        {
            if (Level == GoreLevel.Off) return;
            count = Math.Max(2, (int)(count * GoreScale));
            for (int i = 0; i < count; i++)
            {
                // somewhere on the upper dome, biased the way the bullet went
                float a = Rng.F(0f, MathF.PI) * (Rng.Chance(0.5f) ? 1f : -1f);
                Vector2 outward = new Vector2(MathF.Cos(a), -MathF.Abs(MathF.Sin(a)) * Rng.F(0.5f, 1.3f));
                outward = Vector2.Normalize(outward + dir * 0.7f);
                Vector2 from = centre + outward * radius * Rng.F(0.75f, 1.0f);

                int sides = Rng.I(3, 6);
                var shape = new Vector2[sides];
                float baseR = Rng.F(7f, 16f);
                for (int k = 0; k < sides; k++)
                {
                    float t = k / (float)sides * MathF.PI * 2f;
                    float rr = baseR * Rng.F(0.5f, 1.3f);     // plates of bone break jagged
                    shape[k] = new Vector2(MathF.Cos(t) * rr, MathF.Sin(t) * rr * 0.55f);
                }
                Add(new Particle
                {
                    Kind = PKind.Bone,
                    Pos = from,
                    Vel = outward * Rng.F(220f, 700f) + dir * Rng.F(60f, 260f) + new Vector2(0f, -260f),
                    Rad = baseR,
                    MaxLife = 20f, Life = 20f,
                    Rot = Rng.F(0f, 6.28f),
                    RotVel = Rng.F(-13f, 13f),
                    Col = Rng.Chance(0.3f) ? SkullInner : SkullBone,
                    Shape = shape,
                    Bounce = 0.26f,
                });
            }
        }

        /// <summary>
        /// A head coming apart, built out of what is actually in one and thrown from where
        /// that thing actually sits: the brain out of the brain, the bone off the vault, the
        /// blood out of everything, and a pink cloud over the lot of it.
        /// </summary>
        /// <param name="open">
        /// How far the vault came open: 0 is a bullet through with a hole either side, 1 is
        /// a head emptied. It decides how much of what comes out, and whether any brain
        /// comes out at all - at the low end you get a little bone and a lot of blood and
        /// nothing else, which is most of what makes one head shot look unlike the last.
        /// </param>
        public static void HeadBurst(Vector2 skull, float radius, Vector2 brain, Vector2 dir, Color skin, float open = 1f)
        {
            if (Level == GoreLevel.Off) return;
            open = Math.Clamp(open, 0f, 1f);

            // the aerosol, which is pink and not red, and which is the first thing you see
            int m = (int)((5f + 13f * open) * GoreScale);
            for (int i = 0; i < m; i++)
                Add(new Particle
                {
                    Kind = PKind.Mist,
                    Pos = brain + Rng.Dir() * Rng.F(0f, 14f),
                    Vel = dir * Rng.F(120f, 460f) + Rng.Dir() * Rng.F(60f, 260f),
                    Rad = Rng.F(6f, 13f),
                    MaxLife = 0.5f, Life = 0.5f,
                    Col = Rng.Chance(0.3f) ? Blood(1f) : BrainMist,
                    Drag = 1.6f,
                });

            // blood, from the whole head rather than from one spot. Blood is the one thing
            // a head always gives you, however little else it gives.
            int n = (int)((20f + 22f * open) * GoreScale);
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Kind = PKind.Blood,
                    Pos = skull + Rng.Dir() * Rng.F(0f, radius),
                    Vel = dir * Rng.F(200f, 760f) + Rng.Dir() * Rng.F(80f, 420f) + new Vector2(0f, -180f),
                    Rad = Rng.F(2.5f, 7.5f),
                    MaxLife = 2.6f, Life = 2.6f,
                    Col = Rng.Chance(0.35f) ? BloodDark : Blood(Rng.F(0.85f, 1.1f)),
                });

            // What else comes out, and how much, is rolled around that number rather than
            // being the same handful every time. Two shots that open a skull the same
            // amount still do not throw the same thing out of it.
            //
            // Bone comes off first: even a hole through a head takes a chip or two with it.
            int boneN = Rng.I(1, 3) + (int)Rng.F(open * 2f, open * 9f);
            SkullBurst(skull, radius, dir, boneN);

            // Brain needs the vault genuinely off before any of it is going anywhere. Below
            // that it is a hole in a head, and a hole in a head is bone and blood.
            if (open > 0.34f || Rng.Chance(0.18f))
            {
                int brainN = (int)Rng.F(2f + open * 4f, 3f + open * 16f);
                BrainMatter(brain, dir, brainN, 0.6f + open * 0.6f);
            }

            // and a sheet of his face only when there is enough gone to take one
            if (open > 0.55f && Rng.Chance(0.5f + open * 0.4f))
                FaceSkin(skull + dir * radius * 0.4f, dir, skin, Rng.I(1, 3));

            // meat only: every piece of bone in a burst skull comes off the dome, where
            // bone is, and not out of the middle of his head where bone is not
            for (int i = 0; i < (int)((1f + open * 2f) * GoreScale); i++) Gib(skull, dir, true, false);
        }

        static void Gib(Vector2 p, Vector2 dir, bool big, bool allowBone = true)
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
            bool bone = allowBone && Rng.Chance(0.14f);
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
                        // a drop that arrives fast throws a longer mark along the ground than
                        // one that drips straight down: it runs where it was going
                        float sp = MathF.Min(p.Vel.Length(), 900f);
                        Stamp(p.Pos, p.Rad * Rng.F(1.8f, 3.4f), Art.Alpha(p.Col, 0.85f));
                        if (sp > 260f)
                        {
                            Vector2 run = new Vector2(MathF.Sign(p.Vel.X), 0f) * (sp * 0.035f);
                            Stamp(p.Pos + run * 0.5f, p.Rad * 1.5f, Art.Alpha(p.Col, 0.6f));
                            Stamp(p.Pos + run, p.Rad * 1.1f, Art.Alpha(p.Col, 0.45f));
                        }
                        Parts.RemoveAt(i);
                        continue;
                    }
                    p.Vel.Y *= -p.Bounce;
                    p.Vel.X *= 0.6f;
                    p.RotVel *= 0.5f;
                    if (p.Kind == PKind.Gib || p.Kind == PKind.Bone)
                        Stamp(p.Pos, p.Rad * 1.6f, Art.Alpha(Blood(0.8f), 0.8f));
                    // brain leaves a pink stain, not a red one, and a wider one: it spreads
                    else if (p.Kind == PKind.Brain)
                        Stamp(p.Pos, p.Rad * 2.1f, Art.Alpha(BrainWet, 0.7f));
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
                        {
                            // Blood is a LIQUID. Drawn as a disc it is a pellet, and a
                            // hundred pellets is gravel in the air rather than a spray; a
                            // flying drop is pulled out long behind itself along the way it
                            // is going, and only looks round once it has nearly stopped.
                            float sp = p.Vel.Length();
                            if (sp < 70f) { Raylib.DrawCircleV(p.Pos, p.Rad, p.Col); break; }
                            Vector2 u = p.Vel / sp;
                            float tail = MathF.Min(sp * 0.016f, p.Rad * 4.5f);
                            Art.Capsule(p.Pos, p.Pos - u * tail, p.Rad, p.Col);
                            break;
                        }
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
                    case PKind.Brain:
                        {
                            var pts = new Vector2[p.Shape.Length];
                            bool glass = p.Kind == PKind.Glass;
                            bool brain = p.Kind == PKind.Brain;
                            // brain squashes more than meat does: it has no structure to it
                            float squash = p.Rest && !glass ? (brain ? 0.3f : 0.4f) : 1f;
                            for (int i = 0; i < pts.Length; i++)
                            {
                                var v = Art.Rot(p.Shape[i], p.Rot);
                                pts[i] = p.Pos + new Vector2(v.X, v.Y * squash);
                            }
                            if (p.Rest && !glass)
                            {
                                // NO DARK RING ROUND IT. A settled piece used to be given a
                                // wide translucent disc underneath to read as wet, and at
                                // rest that disc is a dark circle drawn round every scrap on
                                // the street - dozens of them, all the same shape, which is
                                // the one thing nothing else in this game looks like. What
                                // soaks out of a piece goes into the stain layer when it
                                // lands, where it belongs, and is already there.
                                Art.Poly(pts, p.Col);
                            }
                            else if (brain)
                            {
                                // in the air it is soft and shining: no hard ink line round it,
                                // a pale highlight on the upper side of the lump
                                Art.Poly(pts, p.Col);
                                Raylib.DrawCircleV(p.Pos - new Vector2(0f, p.Rad * 0.3f),
                                    p.Rad * 0.34f, Art.Alpha(BrainPale, 0.55f));
                            }
                            // and nothing in flight gets an ink line round it either: at this
                            // size the outline is most of the piece, so a shower of gore came
                            // out looking like a handful of black beads with colour in them
                            else if (glass) Art.Shape(pts, p.Col, 1.2f);
                            else Art.Poly(pts, p.Col);
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
