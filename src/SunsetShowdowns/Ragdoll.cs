using System;
using System.Collections.Generic;
using System.Numerics;

namespace LLD
{
    /// <summary>
    /// Twelve-point Verlet ragdoll. Bones are distance constraints, the torso is braced by a
    /// couple of diagonals so it does not fold in half, and every point collides with the street
    /// on its own. A body dropped by a head shot lands differently every time.
    /// </summary>
    public class Ragdoll
    {
        public const int Head = 0, Neck = 1, Chest = 2, Pelvis = 3,
                         KneeB = 4, FootB = 5, KneeF = 6, FootF = 7,
                         ElbowB = 8, HandB = 9, ElbowG = 10, HandG = 11;
        public const int Count = 12;

        public Vector2[] Pos = new Vector2[Count];
        public Vector2[] Prev = new Vector2[Count];
        public float[] Rad = new float[Count];
        public float[] Inv = new float[Count];
        public bool Active;
        public bool Asleep;
        public int Face = 1;

        struct Link { public int A, B; public float Len, Stiff; }
        readonly List<Link> _links = new();

        float _accum;
        const float Fixed = 1f / 120f;

        public void Build(Fighter f)
        {
            Vector2 P(float x, float y) => f.L2W(new Vector2(x, y));

            Pos[Head] = P(6f, -404f);
            Pos[Neck] = P(2f, -352f);
            Pos[Chest] = P(0f, -312f);
            Pos[Pelvis] = P(0f, -202f);

            // take the legs exactly where they already are, so nothing jumps as he goes down
            f.LegJoints(0, out _, out Vector2 kneeB, out Vector2 ankleB);
            f.LegJoints(1, out _, out Vector2 kneeF, out Vector2 ankleF);
            Pos[KneeB] = kneeB;
            Pos[FootB] = ankleB;
            Pos[KneeF] = kneeF;
            Pos[FootF] = ankleF;
            Pos[ElbowB] = P(-30f, -262f);
            Pos[HandB] = P(-26f, -196f);
            Pos[ElbowG] = f.Arm.Elbow;
            Pos[HandG] = f.Arm.Hand;

            float s = f.Scale;
            float[] radii = { 32f, 14f, 30f, 28f, 17f, 18f, 17f, 18f, 13f, 12f, 13f, 12f };
            for (int i = 0; i < Count; i++)
            {
                Prev[i] = Pos[i];
                Rad[i] = radii[i] * s;
                Inv[i] = 1f;
            }
            Inv[Chest] = 0.7f;      // the trunk carries the weight
            Inv[Pelvis] = 0.7f;

            _links.Clear();
            Bone(Head, Neck, 1f);
            Bone(Neck, Chest, 1f);
            Bone(Chest, Pelvis, 1f);
            Bone(Pelvis, KneeB, 1f); Bone(KneeB, FootB, 1f);
            Bone(Pelvis, KneeF, 1f); Bone(KneeF, FootF, 1f);
            Bone(Chest, ElbowB, 0.9f); Bone(ElbowB, HandB, 0.9f);
            Bone(Chest, ElbowG, 0.9f); Bone(ElbowG, HandG, 0.9f);
            // braces: keep the trunk from folding and the head from flopping through the chest
            Bone(Head, Chest, 0.45f);
            Bone(Neck, Pelvis, 0.45f);
            // the legs are only loosely braced, so they trail and fold instead of staying rigid
            Bone(Chest, KneeB, 0.07f);
            Bone(Chest, KneeF, 0.07f);
            Bone(KneeB, KneeF, 0.05f);

            Face = f.Face;
            Active = true;
            Asleep = false;
            _accum = 0f;
        }

        void Bone(int a, int b, float stiff)
            => _links.Add(new Link { A = a, B = b, Len = Vector2.Distance(Pos[a], Pos[b]), Stiff = stiff });

        /// <summary>Shove one point - used for the bullet's impulse and for the kick of landing.</summary>
        public void Kick(int idx, Vector2 vel) => Prev[idx] -= vel * Fixed;

        public void KickAll(Vector2 vel)
        {
            for (int i = 0; i < Count; i++) Prev[i] -= vel * Fixed;
        }

        public void Spin(float radPerSec)
        {
            Vector2 c = Pos[Pelvis];
            for (int i = 0; i < Count; i++)
            {
                Vector2 r = Pos[i] - c;
                Vector2 v = new Vector2(-r.Y, r.X) * radPerSec;
                Prev[i] -= v * Fixed;
            }
        }

        public void Step(float dt, float ground)
        {
            if (!Active || Asleep) return;
            _accum += MathF.Min(dt, 0.1f);
            int guard = 0;
            while (_accum >= Fixed && guard++ < 16)
            {
                _accum -= Fixed;
                Integrate(ground);
            }
        }

        void Integrate(float ground)
        {
            const float Gravity = 2300f;
            const float Damp = 0.992f;

            // No part of a body travels faster than this. Without the ceiling a bad contact can
            // compound into a corpse launched off the screen.
            const float MaxSpeed = 2300f;
            float maxStep = MaxSpeed * Fixed;

            for (int i = 0; i < Count; i++)
            {
                Vector2 v = (Pos[i] - Prev[i]) * Damp;
                float sp = v.Length();
                if (sp > maxStep) v *= maxStep / sp;
                Prev[i] = Pos[i];
                Pos[i] += v + new Vector2(0f, Gravity) * (Fixed * Fixed);
            }

            for (int iter = 0; iter < 6; iter++)
            {
                foreach (var l in _links)
                {
                    Vector2 d = Pos[l.B] - Pos[l.A];
                    float len = d.Length();
                    if (len < 0.0001f) continue;
                    float diff = (len - l.Len) / len * l.Stiff * 0.5f;
                    Vector2 corr = d * diff;
                    float wa = Inv[l.A], wb = Inv[l.B];
                    float sum = wa + wb;
                    if (sum <= 0f) continue;
                    Pos[l.A] += corr * (2f * wa / sum);
                    Pos[l.B] -= corr * (2f * wb / sum);
                }
                Joints();
                Collide(ground);
            }

            float moved = 0f;
            for (int i = 0; i < Count; i++) moved += (Pos[i] - Prev[i]).Length();
            if (moved < 0.5f) Asleep = true;
        }

        /// <summary>
        /// Joints only bend one way. A knee must stay forward of the line from hip to ankle and
        /// an elbow behind the line from shoulder to hand, so nothing folds the wrong way.
        /// </summary>
        void Joints()
        {
            Hinge(Pelvis, KneeB, FootB, -1f, 5f);
            Hinge(Pelvis, KneeF, FootF, -1f, 5f);
            Hinge(Chest, ElbowB, HandB, 1f, 7f);
            Hinge(Chest, ElbowG, HandG, 1f, 7f);
        }

        /// <param name="side">-1 keeps the middle joint forward of the chord, +1 keeps it behind.</param>
        void Hinge(int a, int b, int c, float side, float minBend)
        {
            Vector2 A = Pos[a], B = Pos[b], C = Pos[c];
            Vector2 chord = C - A;
            float len = chord.Length();
            if (len < 0.001f) return;

            Vector2 n = new Vector2(-chord.Y, chord.X) / len;   // perpendicular to the chord
            float d = Vector2.Dot(B - A, n);                    // where the joint sits across it
            float want = side * minBend * Face;

            // valid when d is past `want` on the correct side
            bool bad = (side * Face > 0f) ? d < want : d > want;
            if (!bad) return;

            Vector2 fix = n * (want - d);
            Pos[b] += fix * 0.8f;
            Pos[a] -= fix * 0.1f;
            Pos[c] -= fix * 0.1f;
        }

        void Collide(float ground)
        {
            for (int i = 0; i < Count; i++)
            {
                float floor = ground - Rad[i] * 0.45f;
                if (Pos[i].Y <= floor) continue;
                Pos[i].Y = floor;
                // friction: scrub off the horizontal motion the point had
                float vx = Pos[i].X - Prev[i].X;
                Prev[i].X = Pos[i].X - vx * 0.3f;
                Prev[i].Y = Pos[i].Y;
            }
        }

        public Vector2 Up => SafeDir(Pos[Head] - Pos[Pelvis], new Vector2(0f, -1f));
        public Vector2 HeadUp => SafeDir(Pos[Head] - Pos[Neck], new Vector2(0f, -1f));

        static Vector2 SafeDir(Vector2 v, Vector2 fallback)
            => v.LengthSquared() < 0.0001f ? fallback : Vector2.Normalize(v);
    }
}
