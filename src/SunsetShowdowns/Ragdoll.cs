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

        /// <summary>
        /// What his legs have left, 0 to 1. A man who has already thrown himself about to stay
        /// up has less of it, and when there is not enough left to carry him the hips come
        /// down on their own. Nothing decides that he falls: the legs just stop being enough.
        /// </summary>
        public float LegPower = 1f;

        /// <summary>
        /// He is on his feet. NOT the same as having strength left: a man whose leg has
        /// been shot out from under him has no muscle to speak of and is still standing on
        /// the thing, and he braces with it until he actually goes down. Gating the braced
        /// behaviour on muscle instead put a wounded man on ice with soft joints while his
        /// legs were still pushing, which is half of why he pogoed.
        /// </summary>
        public bool Upright;

        /// <summary>How hard each leg is working this tick, 0 to 1 of what it has.</summary>
        public float LoadB, LoadF;

        /// <summary>The leg with the knee straight, and the length the muscles are aiming at.</summary>
        public float LegLen = 188f, LegRest = 188f;

        /// <summary>Diagnostics for the test run: what each leg thinks it is doing.</summary>
        public float DbgSquashB, DbgSquashF, DbgShareB, DbgShareF;
        public bool DbgPlantB, DbgPlantF;

        // What one leg is worth. The spring is the thigh, the damper is the knee, and the
        // ankle numbers are small on purpose: sideways is the weak direction, and a man is
        // taken off his feet through his ankles or not at all.
        const float Carry = 1400f;      // what the pair of them hold up between them
        const float LegSpring = 520f;   // the trim on top: push per pixel short of standing
        const float LegDamp = 44f;      // the knee soaking up the drop
        const float LegMax = 13000f;    // all one leg has
        const float AnkleSpring = 50f;
        const float AnkleDamp = 28f;
        const float AnkleMax = 2600f;
        const float Mu = 0.95f;
        const float SwingInv = 4f;      // how light a leg is once it is off the street         // how hard a boot can shove sideways before it slides

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
        public bool Planted(int leg) => Grip(leg) > 0.5f;

        /// <summary>
        /// How much of him that boot is in a position to carry, 1 flat on the street and 0
        /// clear of it, fading over the depth of a boot rather than switching.
        /// </summary>
        public float Grip(int leg)
        {
            if (StepLeg == leg) return 0f;
            int f = Foot(leg);
            float above = (GroundY - Clear[f]) - Pos[f].Y;     // how far off the dirt
            return Math.Clamp(1f - above / 18f, 0f, 1f);
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
            Pos[Pelvis] = P(0f, -226f);

            Pos[FootB] = P(-13f, -38f);
            Pos[FootF] = P(21f, -38f);
            Pos[KneeB] = P(16f, -134f);     // knees a shade forward, as a standing leg is
            Pos[KneeF] = P(26f, -134f);

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

            // What the leg measures with the knee straight. The muscles have to want a length
            // the bones can actually be, or they spend all day hauling against their own
            // skeleton: asking for a leg shorter than it is left him hanging on straight bones
            // with the spring pulling downwards, which is not a man standing on his legs, it
            // is a man suspended from them.
            LegLen = Vector2.Distance(Pos[Pelvis], Pos[KneeB]) + Vector2.Distance(Pos[KneeB], Pos[FootB]);
            LegRest = LegLen;
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
            Upright = true;
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
                Plant(ground);
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
            // A leg in the air weighs almost nothing next to the man standing on the other one.
            // The bone solver was treating a swinging boot as exactly as heavy as him, so when
            // it was thrown at a spot on the street it hauled his whole body along behind it
            // and flung him into the next step - he marched sideways across the road with his
            // boots four feet apart and never once settled. Make the swinging leg light and
            // the step moves the leg instead of the man.
            Inv[FootB] = Inv[KneeB] = StepLeg == 0 ? SwingInv : 1f;
            Inv[FootF] = Inv[KneeF] = StepLeg == 1 ? SwingInv : 1f;

            if (StepLeg >= 0)
            {
                StepT = MathF.Min(1f, StepT + Fixed / MathF.Max(0.02f, StepDur));
                Vector2 p = Vector2.Lerp(StepFrom, StepTarget, StepT);
                p.Y -= MathF.Sin(StepT * MathF.PI) * StepLift;
                int sf = Foot(StepLeg);

                // The boot is driven at the spot, not placed on it. It used to be moved
                // halfway there every tick whatever else was happening to him, which is a
                // puppet string: the leg could not be knocked off its path and it could not
                // arrive early, so a stumble always looked like the same hop. Carrying the
                // boot's own motion through the pull leaves it something to be shoved off.
                Vector2 keep = (Pos[sf] - Prev[sf]) * 0.55f;
                Pos[sf] += (p - Pos[sf]) * 0.30f;
                Prev[sf] = Pos[sf] - keep;

                // A reaching leg finds the street the moment the street is there. Waiting out
                // the arc is what left a boot hanging in the air with his weight already past
                // it; getting it down early is most of what catching yourself is.
                bool grounded = StepT > 0.4f && Pos[sf].Y >= GroundY - Clear[sf] - 2.5f;
                if (StepT >= 1f || grounded) { StepLeg = -1; StepLanded = true; }
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

            // A shove rather than a teleport: it changes where the part is going instead of
            // where it is. That is what lets two legs add up - two pulls on the same point
            // just fight over it, two pushes are twice the push.
            void Push(int i, Vector2 accel)
            {
                Vector2 v = Pos[i] - Prev[i] + accel * (Fixed * Fixed);
                Prev[i] = Pos[i] - v;
            }

            // Legs.
            //
            // Each planted boot is its own strut, and the two of them add up. This used to be
            // one pull on the hips towards a point above the MIDDLE of the feet, which is not
            // two legs holding a man up - it is one invisible leg standing between his boots,
            // and it is why the moment a real boot left the ground the whole body sagged onto
            // the other one and pogoed. Now each leg pushes along its own bone from its own
            // foot, so two legs down carry him with room to spare, one leg down only just
            // holds him, and none at all is a man on his way to the dirt.
            //
            // Three things come out of each leg:
            //   - a spring along the bone, strong, because that is thigh and femur;
            //   - a damper along the same line, which is what a knee is for. Without it the
            //     leg is a pogo stick and the boot bounces for ever;
            //   - the ankle, sideways, weak and hard-limited. All a standing man has to drag
            //     his weight back over his boots is his ankles, and that is exactly why a hard
            //     enough shove takes him off them.
            float power = Math.Clamp(LegPower, 0f, 1f);
            LoadB = LoadF = 0f;

            // How his weight is split between the two boots. The boot nearer to being under
            // him carries more of him, by the plain lever rule, and the split slides as he
            // moves across them. This is the part that was missing: without it each leg was
            // only a spring, and two stiff springs onto one point do not share anything - the
            // one that happens to be a hair shorter takes the lot, which is why he stood with
            // all of himself on the back boot and the front one along for the ride.
            // A BOOT DOES NOT STOP CARRYING HIM THE INSTANT IT LEAVES THE DIRT. Planted was
            // a yes-or-no three pixels wide, so a boot hovering a hair off the ground handed
            // its entire share to the other leg in one frame and took it all back the next.
            // That is the man standing on one locked leg with the other bouncing underneath
            // him: not a wound, not the springs, just a flag flipping. How much a boot
            // carries now fades out over the length of a boot, so the load crosses between
            // his legs instead of jumping.
            float gB = Grip(0), gF = Grip(1);
            bool pB = gB > 0.5f, pF = gF > 0.5f;
            float shareB, shareF;
            {
                float lB, lF;
                float xb = Pos[Foot(0)].X, xf = Pos[Foot(1)].X, span = xf - xb;
                if (MathF.Abs(span) > 2f)
                {
                    lB = Math.Clamp((xf - Pos[Pelvis].X) / span, 0f, 1f);
                    lF = 1f - lB;
                }
                else lB = lF = 0.5f;

                // the lever rule says how it SHOULD split; the grip says how much of that
                // each boot is actually in a position to take
                float wB = lB * gB, wF = lF * gF;
                float tot = wB + wF;
                float carry = MathF.Min(1f, gB + gF);
                if (tot > 0.0001f) { shareB = wB / tot * carry; shareF = wF / tot * carry; }
                else { shareB = shareF = 0f; }
            }
            DbgShareB = shareB; DbgShareF = shareF; DbgPlantB = pB; DbgPlantF = pF;

            for (int leg = 0; leg < 2; leg++)
            {
                if (!Planted(leg)) continue;
                int f = Foot(leg);
                float dx = Pos[Pelvis].X - Pos[f].X;

                // What a leg does is hold him UP and lean him about; it is not a rocket
                // pointed along the thigh. Pushing straight up the bone meant a splayed leg
                // shovelled him sideways as hard as it held him, and with the other boot in
                // the air there was nothing to cancel it - so every save flung him the other
                // way, which bought another step, which flung him back. He marched across the
                // street with his boots a hundred and fifteen pixels apart and never settled.
                // The vertical and the sideways are worked out separately now, and the
                // sideways is kept inside what the boot can actually grip.

                // how high this leg can carry him at all, given how far out the boot is
                float reach = MathF.Sqrt(MathF.Max(16f, LegRest * LegRest - dx * dx));
                float carryHgt = Pos[f].Y - Pos[Pelvis].Y;      // how high it is carrying him
                float squash = reach - carryHgt;                // + means he has sunk on it
                if (leg == 0) DbgSquashB = squash; else DbgSquashF = squash;

                float share = leg == 0 ? shareB : shareF;
                // his share of standing, the trim on top, and the knee soaking up the drop
                float lift = Carry * share + squash * LegSpring
                           + (Vel(Pelvis).Y - Vel(f).Y) * LegDamp;
                // a leg is a strut, not a rope: it holds him up, it cannot haul him down
                if (lift < 0f) lift *= 0.22f;
                lift = Math.Clamp(lift, -LegMax * 0.35f, LegMax * power);

                // The ankle, hauling him back over this boot - and never harder than the boot
                // is pressed into the street, because past that the foot just slides. That
                // limit is the whole reason a hard enough shove takes a man off his feet
                // instead of him simply leaning it off.
                float side = -dx * AnkleSpring - Vel(Pelvis).X * AnkleDamp;
                // what the boot is actually pressed into the street with: his share of him, plus
                // whatever the leg is adding. Sizing the grip off the muscle alone left a
                // standing man with almost no sideways authority at all.
                float normal = Carry * share + MathF.Max(0f, lift);
                float grip = MathF.Min(AnkleMax * power, normal * Mu);
                side = Math.Clamp(side, -grip, grip);

                Push(Pelvis, new Vector2(side, -lift) * m);
                // the reaction presses the boot into the street rather than sliding him along it
                Push(f, new Vector2(-side, lift) * (0.10f * m));

                float load = MathF.Abs(lift) / MathF.Max(1f, LegMax);
                if (leg == 0) LoadB = load; else LoadF = load;
            }

            // Trunk: hold the chest over the hips at the angle he is trying for, and his head
            // over that. The head is the weakest of the three, so it is the first thing to loll.
            Vector2 up = Art.Rot(new Vector2(0f, -1f), Lean);
            Pull(Chest, Pos[Pelvis] + up * SpineLen, 0.17f * m * m, 0.26f * m);
            Pull(Neck, Pos[Chest] + up * NeckLen, 0.15f * m * m, 0.24f * m);
            Pull(Head, Pos[Neck] + up * HeadLen, 0.11f * m * m, 0.20f * m);

            // Knees. Left alone a planted leg folds under the weight before the hips get the
            // message, so the knee is held on the line from hip to ankle - on it, not a step
            // forward of it. Holding it off that line is a push the bones have to answer, and
            // the lightest thing on the end of them is his boot, so he slides backwards across
            // the street for ever, a little every tick, with nobody laying a finger on him.
            // Which way a knee bends is the hinge's business, not a muscle's.
            for (int leg = 0; leg < 2; leg++)
            {
                int k = Knee(leg), f = Foot(leg);
                // A leg in the air still has a knee in it. Holding the swinging one together,
                // weakly, is the difference between a leg reaching for the street and a boot
                // towed through the air on the end of a rope.
                float kk = Planted(leg) ? 0.26f : 0.12f;
                Pull(k, (Pos[Pelvis] + Pos[f]) * 0.5f, kk * m, 0.40f * m);
            }
        }

        /// <summary>
        /// Joints only bend one way. A knee must stay forward of the line from hip to ankle and
        /// an elbow behind the line from shoulder to hand, so nothing folds the wrong way.
        /// </summary>
        void Joints()
        {
            Hinge(Pelvis, KneeB, FootB, -1f, 2f);
            Hinge(Pelvis, KneeF, FootF, -1f, 2f);
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

            // HOW FAR THE LIMB CAN ACTUALLY BEND. A knee can only come off the line between
            // the hip and the boot if the two bones are longer than that line - pulled
            // straight, there is nowhere for it to go, and asking anyway is asking for the
            // bones to stretch.
            //
            // That was the corpse bug. A dead man with a straight leg had this stop demanding
            // a bend the skeleton could not give, the bones hauling the knee back onto the
            // line, and the stop shoving it off again - eighty pixels a pass, six passes a
            // substep, a hundred and twenty substeps a second, with the two of them handing
            // energy to each other the whole time. He crept, then he bounced off the street,
            // then he went down the road at seventeen thousand pixels a second. Neither one
            // of them was wrong on its own; they simply could not both be satisfied, and
            // nothing was telling them to stop trying.
            // The two bones and the line between their ends make a triangle, and the furthest
            // the joint can sit off that line is the height of it. Ask for more and the only
            // way to get it is to stretch the bones, which the bones will not have.
            float l1 = Vector2.Distance(A, B), l2 = Vector2.Distance(B, C);
            float sp = (l1 + l2 + len) * 0.5f;
            float area2 = sp * (sp - l1) * (sp - l2) * (sp - len);
            float room = area2 <= 0f ? 0f : 2f * MathF.Sqrt(area2) / len;
            if (room < 0.5f) return;

            // and it can never be shoved further than that, in one go
            float off = want - d;
            float lim = MathF.Min(MathF.Abs(off), room);
            off = off < 0f ? -lim : lim;

            // The joint takes most of it, and the two ends take the kick back - except where an
            // end is standing on the street. A knee resting against its stop all day otherwise
            // shoves his hip and his boot the same way every tick and walks him down the road
            // with nobody touching him; the ground is what should be swallowing that, and it
            // is. A limp body still gets the reaction, which is what lets it come to rest.
            // The joint takes most of it. Whether the two ends take the kick back depends on
            // whether he is holding himself up: a braced limb puts that into the ground, and a
            // knee resting against its stop all day would otherwise shove his hip and his boot
            // the same way every tick and walk him down the street with nobody touching him.
            // A limp body has nothing to brace with, and it is the kick back through the ends
            // that lets it stop moving at all.
            // A JOINT STOP MUST NOT MOVE THE MAN. Push the knee one way and the hip and the
            // boot have to take the same amount back between them, or every tick the stop is
            // touched the whole body is quietly shoved - and in a position solver a shove IS
            // speed. A limp man with a knee resting against its stop was being pushed six
            // times a substep, a hundred and twenty substeps a second: he crept, then he
            // bounced off the street, then he went up the road at twenty thousand pixels a
            // second. It was not the collision and it was not the bones. It was this.
            //
            // A man holding himself up is the exception, and a real one: what a braced limb
            // pushes against goes into the street through his boots, so the ends keep still
            // and the ground swallows it. Only a body with no strength left in it has to
            // obey the books.
            // A MAN HOLDING HIMSELF UP AND A DEAD ONE NEED DIFFERENT STOPS.
            //
            // Upright, this stop is load-bearing: it is most of what keeps his knee from
            // folding under him, so it goes in hard and the reaction goes into the street
            // through his boots rather than back up through him. Ration it and he sags -
            // measured, his weight came off his boots and his stance shut from 38 to 33.
            //
            // Limp, there is nothing to brace against and nothing it needs to hold up, and
            // a joint that is hard against a stop it can never get off - a leg folded the
            // wrong way under a man who has rolled over - gets shoved at full strength six
            // times a substep, a hundred and twenty substeps a second, with the bones
            // shoving back every time. The two of them hand energy to each other until he
            // bounces off the street and leaves it at seventeen thousand pixels a second.
            // So a dead man's joints are eased straight a few pixels at a time, and the two
            // ends take back exactly what the joint is given, because nothing about a corpse
            // should be able to move a corpse.
            const float Push = 0.8f;       // what a braced joint gets
            const float LimpPush = 0.14f;  // what a dead one gets
            const float LimpStep = 2f;     // and never more than this far in one pass

            float push = Push;
            float back = 0f;
            if (!Upright)
            {
                push = LimpPush;
                back = push * 0.5f;
                off = Math.Clamp(off, -LimpStep, LimpStep);
            }

            Vector2 fix = n * off;
            Pos[b] += fix * push;
            Pos[a] -= fix * back;
            Pos[c] -= fix * back;
        }

        /// <summary>
        /// A boot on the street stays on the street.
        ///
        /// The leg is solved as two rigid bones, and when his weight runs out past his foot the
        /// solver finds it cheaper to drag the boot up into the air than to haul the rest of
        /// him back - a boot weighs nothing next to a man. A foot in the air carries nothing,
        /// so the leg he was standing on quietly stopped being a leg halfway through every
        /// stumble, which is what left him with nothing under him, bouncing on one boot and
        /// then going over as though his legs had never been there at all. While there is any
        /// strength left in him a planted boot is held down onto the dirt. Lift it further
        /// than this and he really is coming off his feet, and then it is allowed to go.
        /// </summary>
        void Plant(float ground)
        {
            if (!Upright) return;
            for (int leg = 0; leg < 2; leg++)
            {
                if (StepLeg == leg) continue;
                int f = Foot(leg);
                float floor = ground - Clear[f];
                float lift = floor - Pos[f].Y;          // above the dirt by this much
                if (lift <= 0f || lift > Clear[f] * 0.5f) continue;
                Ground(f, floor);
            }
        }

        /// <summary>
        /// A point resting on the street: it stops sinking, and the dirt scrubs off the
        /// sideways motion it had. Holding a boot down and NOT giving it this is a boot on
        /// ice, which is how the standing foot came to slide away backwards underneath him.
        /// </summary>
        void Ground(int i, float floor)
        {
            Pos[i].Y = floor;
            float vx = Pos[i].X - Prev[i].X;
            Prev[i].X = Pos[i].X - vx * 0.3f;
            Prev[i].Y = Pos[i].Y;
        }

        void Collide(float ground)
        {
            for (int i = 0; i < Count; i++)
            {
                float floor = ground - Clear[i];
                if (Pos[i].Y <= floor) continue;
                Ground(i, floor);
            }
        }

        public Vector2 Up => SafeDir(Pos[Head] - Pos[Pelvis], new Vector2(0f, -1f));
        public Vector2 HeadUp => SafeDir(Pos[Head] - Pos[Neck], new Vector2(0f, -1f));

        static Vector2 SafeDir(Vector2 v, Vector2 fallback)
            => v.LengthSquared() < 0.0001f ? fallback : Vector2.Normalize(v);
    }
}
