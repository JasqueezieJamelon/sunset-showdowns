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
        /// <summary>How far above the dirt each point comes to rest. An ankle is not a ball:
        /// it sits a boot's height up, and that height is what his legs have to push from.</summary>
        public float[] Clear = new float[Count];
        public bool Active;
        public bool Asleep;
        public int Face = 1;

        struct Link { public int A, B; public float Len, Stiff; }
        readonly List<Link> _links = new();

        float _accum;
        public const float Fixed = 1f / 120f;

        // ---------- muscles ----------
        // What turns a sack of bones into a man standing up. These are the only difference
        // between him on his feet and him in the dirt: the bones, the joints and the ground do
        // not change, the strength behind them does. Drop Muscle to nothing and he falls the
        // same way a dead man does, because he is one.
        public float Muscle;            // 0 limp, 1 holding himself up
        public float Lean;              // radians the trunk is trying for, his facing applied
        public float StandHeight;       // how far above the dirt he is trying to carry his hips
        public float GroundY;
        public int StepLeg = -1;        // the boot that is off the ground, -1 for neither
        public Vector2 StepFrom, StepTarget;
        public float StepT, StepDur = 0.24f, StepLift = 34f;
        public bool StepLanded;         // true for the frame a boot comes down

        public float SpineLen { get; private set; }
        public float NeckLen { get; private set; }
        public float HeadLen { get; private set; }

        public static int Foot(int leg) => leg == 0 ? FootB : FootF;
        public static int Knee(int leg) => leg == 0 ? KneeB : KneeF;

        /// <summary>Velocity of one point, in units a second.</summary>
        public Vector2 Vel(int i) => (Pos[i] - Prev[i]) / Fixed;

        public void SetVel(int i, Vector2 v) => Prev[i] = Pos[i] - v * Fixed;

        /// <summary>Move a point without changing what it was doing.</summary>
        public void Place(int i, Vector2 p)
        {
            Vector2 v = Pos[i] - Prev[i];
            Pos[i] = p;
            Prev[i] = p - v;
            Asleep = false;
        }

        public void Shift(Vector2 d)
        {
            for (int i = 0; i < Count; i++) { Pos[i] += d; Prev[i] += d; }
        }

        /// <summary>True when that boot is taking weight.</summary>
        public bool Planted(int leg)
        {
            int f = Foot(leg);
            return StepLeg != leg && Pos[f].Y >= GroundY - Clear[f] - 3f;
        }

        /// <summary>
        /// Stands the body up from nothing, in his own coordinates, because at the moment this
        /// runs there is no body yet to ask where anything is. This is the only pose in the
        /// game: everything after it is the physics and the muscles arguing.
        /// </summary>
        public void Stand(Vector2 origin, int face, float scale)
        {
            Vector2 P(float x, float y) => origin + new Vector2(x * face * scale, y * scale);

            Pos[Head] = P(6f, -404f);
            Pos[Neck] = P(2f, -352f);
            Pos[Chest] = P(0f, -312f);
            Pos[Pelvis] = P(0f, -202f);

            Pos[FootB] = P(-13f, -38f);
            Pos[FootF] = P(21f, -38f);
            Pos[KneeB] = P(16f, -120f);     // knees a shade forward, as a standing leg is
            Pos[KneeF] = P(26f, -120f);

            Pos[ElbowB] = P(-30f, -252f);
            Pos[HandB] = P(-33f, -186f);
            Pos[ElbowG] = P(26f, -250f);
            Pos[HandG] = P(48f, -158f);

            float s = scale;
            float[] radii = { 32f, 14f, 30f, 28f, 17f, 18f, 17f, 18f, 13f, 12f, 13f, 12f };
            for (int i = 0; i < Count; i++)
            {
                Prev[i] = Pos[i];
                Rad[i] = radii[i] * s;
                Inv[i] = 1f;
                Clear[i] = Rad[i] * 0.45f;
            }
            Clear[FootB] = Clear[FootF] = 38f * s;        // the ankle rides a boot off the dirt
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

            SpineLen = Vector2.Distance(Pos[Chest], Pos[Pelvis]);
            NeckLen = Vector2.Distance(Pos[Neck], Pos[Chest]);
            HeadLen = Vector2.Distance(Pos[Head], Pos[Neck]);

            Face = face;
            Active = true;
            Asleep = false;
            StepLeg = -1;
            _accum = 0f;
        }

        void Bone(int a, int b, float stiff)
            => _links.Add(new Link { A = a, B = b, Len = Vector2.Distance(Pos[a], Pos[b]), Stiff = stiff });

        /// <summary>
        /// Shove one point - used for the bullet.s impulse and for the kick of landing. Any
        /// shove wakes the body: a corpse that has settled still has to jump when it is hit.
        /// </summary>
        public void Kick(int idx, Vector2 vel) { Prev[idx] -= vel * Fixed; Asleep = false; }

        public void KickAll(Vector2 vel)
        {
            for (int i = 0; i < Count; i++) Prev[i] -= vel * Fixed;
            Asleep = false;
        }

        public void Spin(float radPerSec)
        {
            Asleep = false;
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

            Muscles();

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
            if (moved < 0.5f && Muscle <= 0.01f) Asleep = true;
        }

        /// <summary>
        /// The muscles, applied before the bones are resolved so the bones have the last word -
        /// he can pull as hard as he likes, his thigh is still the length it is.
        ///
        /// Everything here is a pull towards where that part of him is trying to be, at a
        /// strength set by how much is left of him. Nothing is scripted and nothing is a pose:
        /// take the strength away and the same body falls over, which is the whole point.
        /// </summary>
        void Muscles()
        {
            // A boot on its way somewhere. It is the one thing that still works when the rest
            // of him has gone, because throwing a leg out is the last thing a falling man does.
            // The arc is the only shaped movement in the body, and even that is only a target:
            // the leg has to reach it through its own bones, and often does not.
            if (StepLeg >= 0)
            {
                StepT = MathF.Min(1f, StepT + Fixed / MathF.Max(0.02f, StepDur));
                Vector2 p = Vector2.Lerp(StepFrom, StepTarget, StepT);
                p.Y -= MathF.Sin(StepT * MathF.PI) * StepLift;
                int sf = Foot(StepLeg);
                Pos[sf] += (p - Pos[sf]) * 0.45f;
                if (StepT >= 1f) { StepLeg = -1; StepLanded = true; }
            }

            float m = Muscle;
            if (m <= 0.002f) return;

            // A muscle moves a part of him without hurling it. Shifting Pos alone in a Verlet
            // body is a velocity, and at a hundred and twenty ticks a second a muscle fighting
            // gravity for ever would pump energy in until he came apart; carrying Prev most of
            // the way along with it leaves the pull without the kick.
            void Pull(int i, Vector2 want, float k, float brake)
            {
                Vector2 v = (Pos[i] - Prev[i]) * (1f - brake);
                Pos[i] += (want - Pos[i]) * k;
                Prev[i] = Pos[i] - v;
            }

            // Legs. Each planted boot pushes the hips back up to standing height above itself.
            // Straight up is strong - that is bone and the big muscles of the thigh. Sideways
            // is weak, because all he has to drag his weight back over his feet with is his
            // ankles, and that is exactly why a hard enough shove takes him off them.
            int down = 0;
            float sumX = 0f;
            for (int leg = 0; leg < 2; leg++)
                if (Planted(leg)) { down++; sumX += Pos[Foot(leg)].X; }

            if (down > 0)
            {
                Vector2 want = new Vector2(sumX / down, GroundY - StandHeight);
                Vector2 e = want - Pos[Pelvis];
                // Straight up is strong - that is bone and the whole of his thigh. Sideways is
                // weak, because all he has to haul his weight back over his boots with is his
                // ankles, and that is exactly why a hard enough shove takes him off them.
                Pull(Pelvis, Pos[Pelvis] + new Vector2(e.X * 0.10f, e.Y), 0.34f * m, 0.45f * m);
            }

            // Trunk: hold the chest over the hips at the angle he is trying for, and his head
            // over that. The head is the weakest of the three, so it is the first thing to loll.
            Vector2 up = Art.Rot(new Vector2(0f, -1f), Lean);
            Pull(Chest, Pos[Pelvis] + up * SpineLen, 0.34f * m, 0.45f * m);
            Pull(Neck, Pos[Chest] + up * NeckLen, 0.30f * m, 0.40f * m);
            Pull(Head, Pos[Neck] + up * HeadLen, 0.22f * m, 0.35f * m);

            // Knees. Left alone a planted leg folds under the weight before the hips get the
            // message, so the knee is held just forward of the line from hip to ankle.
            for (int leg = 0; leg < 2; leg++)
            {
                if (!Planted(leg)) continue;
                int k = Knee(leg), f = Foot(leg);
                Vector2 chord = Pos[f] - Pos[Pelvis];
                float len = chord.Length();
                if (len < 0.001f) continue;
                Vector2 n = new Vector2(-chord.Y, chord.X) / len * -Face;
                Pull(k, (Pos[Pelvis] + Pos[f]) * 0.5f + n * 8f, 0.30f * m, 0.40f * m);
            }
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
                float floor = ground - Clear[i];
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
