using System;
using System.Collections.Generic;
using System.Numerics;

namespace LLD
{
    public enum OrganKind
    {
        None, Brain, BrainStem, Jaw, Throat, Spine, Heart, Aorta,
        Lung, Liver, Kidney, Gut, Pelvis, Femur, Tibia, Humerus, Flesh
    }

    /// <summary>
    /// One structure inside the body, in body-local coordinates (feet at the origin, facing +x,
    /// up is negative y). A bullet is traced through this set, so what a shot does depends on the
    /// line it takes through the torso rather than on which rectangle it touched.
    /// </summary>
    public class Organ
    {
        public OrganKind Kind;
        public string Name;
        public Vector2 A, B;        // capsule; A == B means a sphere
        public float R;
        public float Shock;         // immediate stun, 0-100; over 100 and they go down
        public float Bleed;         // blood percent lost per second from this wound
        public bool Instant;        // lights out now
        public bool Cripple;        // cannot stay on their feet
        public bool Disarm;         // the gun hand stops working
        public float Gore = 1f;     // spray multiplier
    }

    public struct Strike
    {
        public Organ Organ;
        public Vector2 Local;       // entry point in body space
        public float T;             // distance along the bullet
    }

    public static class Anatomy
    {
        public const float BloodFull = 100f;

        public static readonly Organ[] Parts =
        {
            new Organ { Kind = OrganKind.Brain,     Name = "the brain",      A = new Vector2(8f, -404f),  B = new Vector2(8f, -404f),  R = 21f, Instant = true, Shock = 100f, Bleed = 14f, Gore = 2.4f },
            new Organ { Kind = OrganKind.BrainStem, Name = "the brain stem", A = new Vector2(-6f, -382f), B = new Vector2(-6f, -382f), R = 8f,  Instant = true, Shock = 100f, Bleed = 10f, Gore = 2.0f },
            new Organ { Kind = OrganKind.Jaw,       Name = "the jaw",        A = new Vector2(22f, -374f), B = new Vector2(22f, -374f), R = 13f, Shock = 48f, Bleed = 4.5f, Gore = 1.8f },
            new Organ { Kind = OrganKind.Throat,    Name = "the throat",     A = new Vector2(8f, -364f),  B = new Vector2(4f, -350f),  R = 9f,  Shock = 40f, Bleed = 11f, Gore = 1.5f },
            // A level bullet through a trunk nearly always crosses the column, so it is split:
            // high up it drops him where he stands, low down it is survivable for a while.
            new Organ { Kind = OrganKind.Spine,     Name = "the spine",      A = new Vector2(-21f, -348f),B = new Vector2(-19f, -282f),R = 4.5f,Shock = 96f, Bleed = 5f, Cripple = true, Gore = 1.3f },
            new Organ { Kind = OrganKind.Spine,     Name = "the lower spine",A = new Vector2(-19f, -280f),B = new Vector2(-18f, -206f),R = 4.5f,Shock = 58f, Bleed = 6f, Gore = 1.2f },
            new Organ { Kind = OrganKind.Heart,     Name = "the heart",      A = new Vector2(6f, -304f),  B = new Vector2(4f, -292f),  R = 13f, Instant = true, Shock = 100f, Bleed = 30f, Gore = 1.8f },
            new Organ { Kind = OrganKind.Aorta,     Name = "the aorta",      A = new Vector2(2f, -322f),  B = new Vector2(0f, -288f),  R = 6f,  Shock = 62f, Bleed = 38f, Cripple = true, Gore = 1.6f },
            new Organ { Kind = OrganKind.Lung,      Name = "a lung",         A = new Vector2(17f, -336f), B = new Vector2(14f, -270f), R = 14f, Shock = 46f, Bleed = 12f, Gore = 1.3f },
            new Organ { Kind = OrganKind.Lung,      Name = "a lung",         A = new Vector2(-11f, -336f),B = new Vector2(-9f, -270f), R = 14f, Shock = 46f, Bleed = 12f, Gore = 1.3f },
            new Organ { Kind = OrganKind.Liver,     Name = "the liver",      A = new Vector2(16f, -258f), B = new Vector2(16f, -258f), R = 15f, Shock = 52f, Bleed = 16f, Gore = 1.3f },
            new Organ { Kind = OrganKind.Kidney,    Name = "a kidney",       A = new Vector2(-14f, -252f),B = new Vector2(-14f, -252f),R = 9f,  Shock = 44f, Bleed = 10f, Gore = 1.2f },
            new Organ { Kind = OrganKind.Gut,       Name = "the gut",        A = new Vector2(4f, -248f),  B = new Vector2(2f, -214f),  R = 18f, Shock = 30f, Bleed = 4.5f, Gore = 1.1f },
            new Organ { Kind = OrganKind.Pelvis,    Name = "the hip",        A = new Vector2(4f, -198f),  B = new Vector2(4f, -198f),  R = 16f, Shock = 58f, Bleed = 14f, Cripple = true, Gore = 1.2f },
        };

        /// <summary>
        /// The bones of one leg, built from where that leg actually is. They cannot live in the
        /// fixed body map above: the boots step, the knee breaks forward, and a man on the
        /// ground has his legs folded under him. A thigh takes him to one knee; a shin is less
        /// than that but it is still bone, and before this there was nothing below the knee to
        /// hit at all, so shooting a man's calf only ever tore cloth.
        /// </summary>
        public static void AddLegBones(List<Organ> into, Vector2 hip, Vector2 knee, Vector2 ankle)
        {
            into.Add(new Organ { Kind = OrganKind.Femur, Name = "the thigh", A = hip, B = knee, R = 11f, Shock = 44f, Bleed = 7f });
            into.Add(new Organ { Kind = OrganKind.Tibia, Name = "the shin", A = knee, B = ankle, R = 8.5f, Shock = 32f, Bleed = 5f });
        }

        /// <summary>Everything the bullet passes through, nearest first.</summary>
        public static List<Strike> Trace(Vector2 from, Vector2 dir, float len, List<Organ> also = null)
        {
            var hits = new List<Strike>(4);
            if (also != null)
                foreach (var o in also)
                {
                    float t2 = o.A == o.B
                        ? Art.RayCircle(from, dir, len, o.A, o.R)
                        : Art.RayCapsule(from, dir, len, o.A, o.B, o.R);
                    if (t2 >= 0f) hits.Add(new Strike { Organ = o, Local = from + dir * t2, T = t2 });
                }
            foreach (var o in Parts)
            {
                float t = o.A == o.B
                    ? Art.RayCircle(from, dir, len, o.A, o.R)
                    : Art.RayCapsule(from, dir, len, o.A, o.B, o.R);
                if (t < 0f) continue;
                hits.Add(new Strike { Organ = o, Local = from + dir * t, T = t });
            }
            hits.Sort((a, b) => a.T.CompareTo(b.T));
            return hits;
        }

        /// <summary>A hit that found nothing vital still tears muscle and bleeds a little.</summary>
        public static readonly Organ FleshWound = new Organ
        {
            Kind = OrganKind.Flesh, Name = "meat", Shock = 16f, Bleed = 2.2f, Gore = 0.9f
        };

        public static readonly Organ ArmBone = new Organ
        {
            Kind = OrganKind.Humerus, Name = "the gun arm", Shock = 26f, Bleed = 4f, Disarm = true, Gore = 1f
        };
    }

    /// <summary>What one bullet did, for gore, messaging and the opponent's behaviour.</summary>
    public class ShotReport
    {
        public readonly List<Organ> Organs = new();
        public float Shock, Bleed, Gore = 1f;
        public bool Instant, Cripple, Disarm;
        public Vector2 Entry, Exit;
        public bool ThroughAndThrough;

        // Decided once, where the shot is judged, then replayed the same way on both machines.
        public Vector2 LocalEntry, LocalExit;
        public int HeadSeverity;        // 0 none, 1 torn, 2 skull opened
        /// How far the vault actually came open, 0 a neat hole through, 1 emptied. Rolled
        /// here with every other coin toss so both machines open the same skull.
        public float HeadOpen;
        public bool EyeOut, JawGone;
        public string Note = "";

        public string Describe()
        {
            if (Organs.Count == 0) return "a clean miss of anything that matters";
            string worst = Organs[0].Name;
            float best = -1f;
            foreach (var o in Organs)
            {
                float w = (o.Instant ? 1000f : 0f) + o.Bleed + o.Shock * 0.2f;
                if (w > best) { best = w; worst = o.Name; }
            }
            return "through " + worst;
        }
    }
}
