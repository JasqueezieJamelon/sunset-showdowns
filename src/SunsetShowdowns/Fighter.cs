using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    public enum Zone { None, Hat, Head, Torso, Arm, Hand, Leg }

    public struct HitVolume
    {
        public Zone Zone;
        public Vector2 A, B;
        public float R;
    }

    public struct Wound
    {
        public Vector2 Local;     // body space, so it rides the body however it ends up lying
        public float Size;
        public float Age;
        public bool Exit;         // exit wounds are bigger and messier
        public float Bleed;       // how hard this one runs
        public Vector2 Dir;       // spray direction, body space
        public float Seed;        // keeps each tear a different shape
    }

    public partial class Fighter
    {
        public CharDef Def;
        public Palette P => Def.P;
        public Vector2 Origin;
        public int Face = 1;

        // ---- physiology ----
        public float Blood = Anatomy.BloodFull;
        public float BleedRate;
        public float Shock;
        public bool Crippled;
        public bool Collapsed;      // off their feet, maybe still alive
        public bool Killed;         // brain or heart: lights out at once
        public bool Dead => Killed || Blood <= 0f;
        public bool Down => Collapsed || Dead;
        public float StunLeft;      // cannot aim or fire while this runs
        public float Impairment => Math.Clamp((Anatomy.BloodFull - Blood) / 70f + Shock / 160f, 0f, 2.2f);

        public bool HatOn = true;
        public bool Disarmed;

        /// A hit that does not kill him does not stagger him either. See ApplyShot.
        public bool NoFlinch;

        public ArmRig Arm = new ArmRig();
        public List<Wound> Wounds = new();
        public Ragdoll Doll = new Ragdoll();

        public int HeadDamage;          // 0 whole, 1 torn open, 2 skull emptied
        public float HeadWoundAngle;    // body-space angle of the missing piece
        public float HeadOpen;          // 0 a neat hole through, 1 the vault emptied
        public bool JawGone;            // lower face torn away
        public bool EyeOut;             // eyeball hanging out of the socket
        Vector2 _eyePos, _eyePrev;      // the eye is its own little pendulum
        float[] _skullJitter;

        public float Flinch;
        public float FlinchDir;
        public float Kneel;
        public float DeadTime;
        public float Breath;
        public float Scale = 1f;
        float _pulse;

        // Standing is an inverted pendulum: the trunk carries momentum, the feet are planted,
        // and the lean is whatever the gap between them says it is. Push the trunk far enough
        // past the feet and he has to step back to get under his own weight again.
        public float ComX;           // centre of mass, offset from his mark
        public float ComVx;
        public float LeanAng;
        public int Steps;

        // Each boot is a body of its own: it rests on the dirt under friction, gets thrown when
        // he has to step, and falls where it falls. Nothing about a stride is scripted.
        // The boots are the ragdoll's own feet now, not a second pair of points kept alongside
        // it, so there is nothing left for the two to disagree about.
        Vector2 FootP(int i) => Doll.Pos[Ragdoll.Foot(i)];
        bool Airborne(int i) => Doll.StepLeg == i;
        float _stepDelay;
        int _failedSteps;
        public float Crouch;         // kept for leg wounds only

        public float AnkleHeight => 38f * Scale;
        public float FootFloor => Scene.GroundY - AnkleHeight;
        public Vector2 FootWorld(int i) => FootP(i);

        public float SupportX => (FootP(0).X + FootP(1).X) * 0.5f;
        public float FootX => SupportX - Origin.X;
        public bool Stepping => Doll.StepLeg >= 0;
        public float Stance => MathF.Abs(FootP(0).X - FootP(1).X);

        const float FootGravity = 2600f;
        const float StepAir = 0.22f;        // roughly how long a boot is off the ground
        const float LegRestAnkle = 186f;    // pelvis to ankle, stood up straight
        const float LegMaxAnkle = 188f;
        const float MaxStance = 150f;   // how far apart the boots may ever get
        // hips this far off the dirt and his legs have plainly gone; normal is about 200
        const float SagFloor = 123f;
        const float MaxReach = 92f;    // how far a boot may sit from the hips before it must move
        public const float ArmWeight = 0.5f;   // one standard feel for the gun hand

        public Fighter(CharDef def, Vector2 origin, int face)
        {
            Def = def;
            Origin = origin;
            Face = face;
            Scale = def.Height;

            // Stand the body up before anything asks it where his shoulder is. From here on
            // it is the only body he has, upright or otherwise.
            Doll.Stand(origin, face, Scale);
            Settle();

            Arm.Face = face;
            Arm.UpperLen = 82f * Scale;
            Arm.ForeLen = 92f * Scale;
            Arm.Shoulder = L2W(new Vector2(10f, -318f));
            Arm.HolsterPoint = L2W(new Vector2(48f, -172f));
            Arm.Snap(Arm.HolsterPoint);
            _skullJitter = new float[24];
            for (int i = 0; i < _skullJitter.Length; i++) _skullJitter[i] = Rng.F(0.62f, 1.0f);
        }

        // ---------- the body itself ----------
        // The trunk is two physical points, pelvis and chest, carried by the legs. Everything
        // drawn on him is placed relative to those, so the art cannot come apart from the
        // physics: there is only one body, and it is the simulated one.
        // The trunk is the ragdoll's pelvis and chest. They read and write as they always did,
        // so everything drawn on him and every hit box still comes off these two points - but
        // underneath there is only the one body now, upright or in the dirt.
        public Vector2 PelvisP
        {
            get => Doll.Pos[Ragdoll.Pelvis];
            set => Doll.Place(Ragdoll.Pelvis, value);
        }
        public Vector2 PelvisV
        {
            get => Doll.Vel(Ragdoll.Pelvis);
            set => Doll.SetVel(Ragdoll.Pelvis, value);
        }
        public Vector2 ChestP
        {
            get => Doll.Pos[Ragdoll.Chest];
            set => Doll.Place(Ragdoll.Chest, value);
        }
        public Vector2 ChestV
        {
            get => Doll.Vel(Ragdoll.Chest);
            set => Doll.SetVel(Ragdoll.Chest, value);
        }

        /// <summary>
        /// Everything about a body that two machines can disagree about. The stance is physical,
        /// so without this the far man can be stood up straight on one screen and crouched on
        /// the other and never recover - the simulations simply drift apart.
        /// </summary>
        public struct Snap
        {
            public Vector2 Pelvis, PelvisV, Chest, ChestV, Foot0, Foot1;
            public float Blood, Shock, Kneel;
            public bool Down, Dead, Air0, Air1;
        }

        public Snap Capture() => new Snap
        {
            Pelvis = PelvisP, PelvisV = PelvisV, Chest = ChestP, ChestV = ChestV,
            Foot0 = FootP(0), Foot1 = FootP(1),
            Blood = Blood, Shock = Shock, Kneel = Kneel,
            Down = Collapsed, Dead = Killed, Air0 = Airborne(0), Air1 = Airborne(1),
        };

        /// <summary>
        /// Pull this body towards where the host says it is. A hard snap every tenth of a second
        /// would read as stutter, so position is eased and only a big disagreement is corrected
        /// outright; the things a player can see at a glance - down, dead, which boot is in the
        /// air - are taken as stated.
        /// </summary>
        public void ApplySnap(in Snap s, float blend)
        {
            const float Jump = 90f;     // past this it is not drift, it is a different pose
            Vector2 Ease(Vector2 cur, Vector2 want)
                => Vector2.Distance(cur, want) > Jump ? want : Vector2.Lerp(cur, want, blend);

            PelvisP = Ease(PelvisP, s.Pelvis);
            ChestP = Ease(ChestP, s.Chest);
            Doll.Place(Ragdoll.FootB, Ease(FootP(0), s.Foot0));
            Doll.Place(Ragdoll.FootF, Ease(FootP(1), s.Foot1));
            PelvisV = Vector2.Lerp(PelvisV, s.PelvisV, blend);
            ChestV = Vector2.Lerp(ChestV, s.ChestV, blend);
            Kneel = Art.Lerp(Kneel, s.Kneel, blend);
            Doll.StepLeg = s.Air0 ? 0 : s.Air1 ? 1 : -1;
            Blood = s.Blood;
            Shock = s.Shock;
            Collapsed = s.Down;
            Killed = s.Dead;
        }
        float _stagger;             // how fast he is running out of steps
        float _legSpent;            // 0 fresh, 1 nothing left in them
        float _sagging;             // how long his hips have been down near the dirt
        public float Muscle = 1f;          // 1 standing strong, 0 limp
        public const float SpineLen = 112f;
        public const float RestLeg = 224f; // pelvis to the sole when he is stood up straight
        public const float MaxLeg = 211f;

        static readonly Vector2 Com = new Vector2(0f, -200f);

        public float Tilt => LeanAng;
        public Vector2 Root => new Vector2(PelvisP.X, Scene.GroundY);

        /// <summary>Up the spine, and the way he is facing, taken from the physical trunk.</summary>
        public void TrunkBasis(out Vector2 up, out Vector2 fwd)
        {
            Vector2 d = ChestP - PelvisP;
            up = d.LengthSquared() < 0.0001f ? new Vector2(0f, -1f) : Vector2.Normalize(d);
            fwd = new Vector2(-up.Y, up.X) * Face;
        }

        public Vector2 L2W(Vector2 l)
        {
            if (Down && Doll.Active) return DollL2W(l);
            TrunkBasis(out Vector2 up, out Vector2 fwd);
            return PelvisP + fwd * (l.X * Scale) + up * (-(l.Y - Com.Y) * Scale);
        }

        public Vector2 W2L(Vector2 w)
        {
            if (Down && Doll.Active) return DollW2L(w);
            TrunkBasis(out Vector2 up, out Vector2 fwd);
            Vector2 v = w - PelvisP;
            return new Vector2(Vector2.Dot(v, fwd) / Scale, Com.Y - Vector2.Dot(v, up) / Scale);
        }

        public Vector2 W2LDir(Vector2 d)
        {
            TrunkBasis(out Vector2 up, out Vector2 fwd);
            return new Vector2(Vector2.Dot(d, fwd), -Vector2.Dot(d, up));
        }

        // once he is down the ragdoll owns the trunk
        Vector2 DollL2W(Vector2 l)
        {
            Vector2 pelvis = Doll.Pos[Ragdoll.Pelvis], chest = Doll.Pos[Ragdoll.Chest];
            Vector2 d = chest - pelvis;
            Vector2 up = d.LengthSquared() < 0.0001f ? new Vector2(0f, -1f) : Vector2.Normalize(d);
            Vector2 fwd = new Vector2(-up.Y, up.X) * Face;
            return pelvis + fwd * (l.X * Scale) + up * (-(l.Y - Com.Y) * Scale);
        }

        Vector2 DollW2L(Vector2 w)
        {
            Vector2 pelvis = Doll.Pos[Ragdoll.Pelvis], chest = Doll.Pos[Ragdoll.Chest];
            Vector2 d = chest - pelvis;
            Vector2 up = d.LengthSquared() < 0.0001f ? new Vector2(0f, -1f) : Vector2.Normalize(d);
            Vector2 fwd = new Vector2(-up.Y, up.X) * Face;
            Vector2 v = w - pelvis;
            return new Vector2(Vector2.Dot(v, fwd) / Scale, Com.Y - Vector2.Dot(v, up) / Scale);
        }

        public Vector2 HeadCenter => (Down && Doll.Active) ? Doll.Pos[Ragdoll.Head] : L2W(new Vector2(4f, -398f));

        /// <summary>Where the eye sits in its socket, whichever way the head is turned.</summary>
        public Vector2 EyeSocket()
        {
            Basis(out Vector2 up, out Vector2 fwd);
            if (Down && Doll.Active)
            {
                up = Doll.HeadUp;
                fwd = new Vector2(-up.Y, up.X) * Face;
            }
            return HeadCenter + fwd * 20f * Scale + up * 6f * Scale;
        }

        void UpdateEye(float dt)
        {
            if (!EyeOut) return;
            Vector2 socket = EyeSocket();
            Vector2 v = (_eyePos - _eyePrev) * 0.97f;
            _eyePrev = _eyePos;
            _eyePos += v + new Vector2(0f, 1900f) * (dt * dt);

            // the optic nerve keeps it on a short leash
            Vector2 d = _eyePos - socket;
            float len = d.Length();
            float max = 34f * Scale;
            if (len > max) _eyePos = socket + d / len * max;
            if (len < 6f * Scale) _eyePos = socket + (len < 0.001f ? new Vector2(0f, 1f) : d / len) * 6f * Scale;
            if (_eyePos.Y > Scene.GroundY - 4f) _eyePos.Y = Scene.GroundY - 4f;
        }

        /// <summary>Body up-vector and facing-vector in world space, standing or fallen.</summary>
        public void Basis(out Vector2 up, out Vector2 fwd)
        {
            if (Down && Doll.Active) up = Doll.HeadUp;
            else up = Art.Rot(new Vector2(0f, -1f), Tilt);
            fwd = new Vector2(-up.Y, up.X) * Face;
        }

        // ---------- hit volumes ----------
        /// <summary>Breathing sway and flinch, applied to the drawn body and its hit volumes alike.</summary>
        public Vector2 PoseOffset()
        {
            float sway = Down ? 0f : MathF.Sin(Breath * 1.6f) * 2.2f;
            float fl = Flinch * 12f * -FlinchDir;
            return new Vector2(fl * Face * 0.35f, sway * 0.25f);
        }

        Vector2 Pose(float x, float y)
        {
            Vector2 o = PoseOffset();
            return L2W(new Vector2(x + o.X, y + o.Y));
        }

        /// <summary>
        /// Hip, knee and ankle of one leg, exactly where they are drawn. Both the artwork and the
        /// hit volumes come from here, so the boxes always sit on the man you can see.
        /// </summary>
        // How wide the leg is at each landmark. One set of numbers so the drawing, the hit
        // volumes and the downed body all agree about the shape of the limb.
        public const float ThighTop = 17f, ThighLow = 13.5f, CalfTop = 16f, AnkleW = 13f;
        // how far forward a standing knee sits off the hip-to-ankle line
        public const float KneeBreak = 21f;

        public void LegJoints(int i, out Vector2 hip, out Vector2 knee, out Vector2 ankle)
        {
            const float Thigh = 99f, Shin = 97f;
            hip = Pose(i == 0 ? -7f : 15f, -201f);
            ankle = FootP(i);

            // the leg is a fixed length: it cannot reach past itself to a boot
            Vector2 d = ankle - hip;
            float len = d.Length();
            float reach = (Thigh + Shin - 2f) * Scale;
            if (len > reach && len > 0.001f) ankle = hip + d / len * reach;

            knee = SolveKnee(hip, ankle, Thigh * Scale, Shin * Scale, Face);

            // Stood up straight, hip and ankle are nearly as far apart as the two bones are
            // long, so the solve has almost nothing to work with and the knee comes out all
            // but on the line between them - which is what made the leg read as a post. Hold
            // it out to where a standing knee actually sits. The moment the leg is genuinely
            // bent the solve is already further forward than this and nothing here applies.
            Vector2 axis = ankle - hip;
            float alen = axis.Length();
            if (alen > 0.001f)
            {
                axis /= alen;
                Vector2 fwd = new Vector2(axis.Y, -axis.X) * Face;
                float got = Vector2.Dot(knee - hip, fwd);
                float want = KneeBreak * Scale;
                if (got < want) knee += fwd * (want - got);
            }

            if (Kneel > 0.01f) knee += new Vector2(Face * 18f * Kneel * Scale, 10f * Kneel * Scale);
        }

        /// <summary>
        /// The leg bones where the legs actually are, in this body's own space, so what a
        /// bullet finds is the leg you can see rather than a pair of sticks left behind at the
        /// pose the man was first drawn in.
        /// </summary>
        List<Organ> LegBones()
        {
            var bones = new List<Organ>(4);
            if (Down && Doll.Active)
            {
                var d = Doll;
                Vector2 p = DollW2L(d.Pos[Ragdoll.Pelvis]);
                Anatomy.AddLegBones(bones, p, DollW2L(d.Pos[Ragdoll.KneeB]), DollW2L(d.Pos[Ragdoll.FootB]));
                Anatomy.AddLegBones(bones, p, DollW2L(d.Pos[Ragdoll.KneeF]), DollW2L(d.Pos[Ragdoll.FootF]));
                return bones;
            }
            for (int i = 0; i < 2; i++)
            {
                LegJoints(i, out Vector2 hip, out Vector2 knee, out Vector2 ankle);
                Anatomy.AddLegBones(bones, W2L(hip), W2L(knee), W2L(ankle));
            }
            return bones;
        }

        public List<HitVolume> Volumes()
        {
            var list = new List<HitVolume>(10);
            if (Down && Doll.Active)
            {
                list.Add(new HitVolume { Zone = Zone.Head, A = Doll.Pos[Ragdoll.Head], B = Doll.Pos[Ragdoll.Neck], R = 30f * Scale });
                list.Add(new HitVolume { Zone = Zone.Torso, A = Doll.Pos[Ragdoll.Chest], B = Doll.Pos[Ragdoll.Pelvis], R = 34f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = Doll.Pos[Ragdoll.Pelvis], B = Doll.Pos[Ragdoll.KneeF], R = 18f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = Doll.Pos[Ragdoll.KneeF], B = Doll.Pos[Ragdoll.FootF], R = 15f * Scale });
                return list;
            }

            // head sits at -398 with a 34 radius; the hat rides just above it
            if (HatOn)
                list.Add(new HitVolume { Zone = Zone.Hat, A = Pose(2f, -432f), B = Pose(2f, -450f), R = 40f * Scale });
            list.Add(new HitVolume { Zone = Zone.Head, A = Pose(4f, -404f), B = Pose(4f, -392f), R = 33f * Scale });
            // the drawn trunk runs x -26..28 and y -208..-346, so it is narrower than it was boxed
            list.Add(new HitVolume { Zone = Zone.Torso, A = Pose(1f, -338f), B = Pose(1f, -216f), R = 29f * Scale });

            // legs follow the boots wherever they have stepped
            for (int i = 0; i < 2; i++)
            {
                LegJoints(i, out Vector2 hip, out Vector2 knee, out Vector2 ankle);
                list.Add(new HitVolume { Zone = Zone.Leg, A = hip, B = knee, R = 18f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = knee, B = ankle, R = 15f * Scale });
            }
            list.Add(new HitVolume { Zone = Zone.Arm, A = Arm.Shoulder, B = Arm.Elbow, R = 15f * Scale });
            list.Add(new HitVolume { Zone = Zone.Arm, A = Arm.Elbow, B = Arm.Hand, R = 14f * Scale });
            list.Add(new HitVolume { Zone = Zone.Hand, A = Arm.Hand, B = Arm.Hand, R = 14f * Scale });
            return list;
        }

        public bool Raycast(Vector2 origin, Vector2 dir, float len, out Zone zone, out Vector2 point, out float dist)
        {
            zone = Zone.None; point = Vector2.Zero; dist = float.MaxValue;
            foreach (var v in Volumes())
            {
                float t = (v.A == v.B)
                    ? Art.RayCircle(origin, dir, len, v.A, v.R)
                    : Art.RayCapsule(origin, dir, len, v.A, v.B, v.R);
                if (t >= 0f && t < dist) { dist = t; zone = v.Zone; }
            }
            if (zone == Zone.None) return false;
            point = origin + dir * dist;
            return true;
        }

        // ---------- taking a bullet ----------
        /// <summary>Trace and apply in one go. Single player, and the host's own shots.</summary>
        public ShotReport TakeShot(Vector2 worldFrom, Vector2 worldDir, float len, Zone zone, Vector2 entry)
        {
            var rep = TraceShot(worldFrom, worldDir, len, zone, entry);
            ApplyShot(rep, zone, worldDir);
            return rep;
        }

        /// <summary>
        /// Work out what the bullet did, deciding every coin toss here and nowhere else, so the
        /// same report can be replayed on another machine and come out identical.
        /// </summary>
        public ShotReport TraceShot(Vector2 worldFrom, Vector2 worldDir, float len, Zone zone, Vector2 entry)
        {
            var rep = new ShotReport { Entry = entry };
            if (Dead) return rep;

            Vector2 lf = W2L(worldFrom);
            Vector2 ld = Vector2.Normalize(W2LDir(worldDir));
            Vector2 lEntry = W2L(entry);

            var strikes = Anatomy.Trace(lf, ld, len / Scale, LegBones());
            float worstShock = 0f, sumShock = 0f;
            foreach (var s in strikes)
            {
                rep.Organs.Add(s.Organ);
                worstShock = MathF.Max(worstShock, s.Organ.Shock);
                sumShock += s.Organ.Shock;
                rep.Bleed += s.Organ.Bleed;
                rep.Instant |= s.Organ.Instant;
                rep.Cripple |= s.Organ.Cripple;
                rep.Gore = MathF.Max(rep.Gore, s.Organ.Gore);
            }
            // shock does not simply add up: the worst wound dominates and the rest compound it
            rep.Shock = worstShock + (sumShock - worstShock) * 0.35f;

            if (zone == Zone.Arm || zone == Zone.Hand)
            {
                var o = Anatomy.ArmBone;
                rep.Organs.Add(o);
                rep.Shock += o.Shock;
                rep.Bleed += o.Bleed;
                rep.Disarm = true;
            }
            else if (strikes.Count == 0)
            {
                var o = Anatomy.FleshWound;
                rep.Shock += o.Shock;
                rep.Bleed += o.Bleed;
            }

            // where it went in and where it came out, in this body's own space
            float depth = strikes.Count > 0 ? strikes[^1].T - strikes[0].T + 34f : 30f;
            Vector2 lExit = lEntry + ld * depth;
            rep.LocalEntry = lEntry;
            rep.LocalExit = lExit;
            rep.Exit = L2W(lExit);
            rep.ThroughAndThrough = strikes.Count > 0;

            bool headHit = false, jaw = false;
            foreach (var o in rep.Organs)
            {
                if (o.Kind == OrganKind.Brain || o.Kind == OrganKind.BrainStem || o.Kind == OrganKind.Jaw) headHit = true;
                if (o.Kind == OrganKind.Jaw) jaw = true;
            }
            if (headHit)
            {
                rep.HeadSeverity = rep.Instant ? 2 : 1;
                rep.JawGone = jaw;
                float angle = MathF.Atan2(-(lExit.Y - -400f), lExit.X - 6f);
                bool frontOfFace = MathF.Abs(Art.WrapPi(angle)) < 1.3f;
                rep.EyeOut = frontOfFace && !EyeOut && Rng.Chance(rep.HeadSeverity >= 2 ? 0.85f : 0.55f);

                // HOW BADLY IT OPENS, which is not the same question as whether it killed him.
                //
                // Every killing head shot used to do the identical thing - same size hole in
                // the same shape with the same shower out of it - so the tenth one looked
                // exactly like the first. A bullet into a skull does not do one thing. It
                // can pass through and leave a neat hole either side, it can lift the vault
                // off and take very little else, it can take the vault and empty what is
                // under it, or it can take the lot. Which of those happens is rolled here,
                // once, with the rest of the shot, so both machines open the same skull.
                if (rep.HeadSeverity >= 2)
                {
                    float roll = Rng.F(0f, 1f);
                    rep.HeadOpen =
                        roll < 0.22f ? Rng.F(0.06f, 0.26f) :   // straight through: a hole
                        roll < 0.54f ? Rng.F(0.28f, 0.52f) :   // the vault lifts, little else
                        roll < 0.86f ? Rng.F(0.54f, 0.78f) :   // bone and what is under it
                                       Rng.F(0.80f, 1.00f);    // the lot
                }
                else rep.HeadOpen = Rng.F(0.04f, 0.24f);
            }

            // whether the gun actually leaves his hand is decided once, here
            if (rep.Disarm) rep.Disarm = Rng.Chance(zone == Zone.Hand ? 0.8f : 0.45f);
            rep.Note = rep.Describe();
            return rep;
        }

        /// <summary>Replay a judged shot. Pure consequence, no decisions.</summary>
        public void ApplyShot(ShotReport rep, Zone zone, Vector2 worldDir)
        {
            if (Dead) return;

            Vector2 lEntry = rep.LocalEntry, lExit = rep.LocalExit;
            Vector2 ld = lExit - lEntry;
            if (ld.LengthSquared() > 0.0001f) ld = Vector2.Normalize(ld);
            Vector2 worldExit = L2W(lExit);

            AddWound(lEntry, ld, false, rep.Bleed, rep.Gore);
            if (rep.ThroughAndThrough) AddWound(lExit, ld, true, rep.Bleed, rep.Gore);

            if (rep.HeadSeverity > 0)
            {
                if (rep.HeadSeverity > HeadDamage || rep.HeadOpen > HeadOpen)
                {
                    HeadDamage = Math.Max(HeadDamage, rep.HeadSeverity);
                    HeadOpen = MathF.Max(HeadOpen, rep.HeadOpen);
                    HeadWoundAngle = MathF.Atan2(-(lExit.Y - -400f), lExit.X - 6f);
                    // The shape of the hole is rolled fresh for every wound instead of once
                    // per man at birth, which is what made it the same hole every time. The
                    // seed comes out of where the bullet left him and how far it opened, so
                    // it is different every shot and identical on both machines.
                    ReshapeSkull(lExit, rep.HeadOpen);
                }
                KnockHat(worldDir);

                if (rep.HeadSeverity >= 2)
                {
                    // A skull coming apart, thrown from where the parts of a head actually
                    // are rather than all out of the one hole: the brain out of the brain,
                    // which Anatomy puts at (8, -404) with a radius of 21, and the bone off
                    // the vault of the skull around (4, -398). Both are read from the same
                    // numbers the bullet was traced against, so what you see leave him is
                    // what the shot said it hit.
                    Gore.HeadBurst(L2W(new Vector2(4f, -398f)), 34f * Scale,
                                   L2W(new Vector2(8f, -404f)), worldDir, P.Skin, HeadOpen);
                }
                else
                {
                    Gore.Chunks(worldExit, worldDir, P.Skin, 5);
                    Gore.SkullPieces(worldExit, worldDir, P.Skin, 1);
                    // a graze opens the vault without emptying it: a little of him comes out
                    if (Rng.Chance(0.5f)) Gore.BrainMatter(L2W(new Vector2(8f, -404f)), worldDir, 2, 0.5f);
                }
                // and the parts of him that came loose without coming off
                TearFace(HeadWoundAngle, rep.HeadSeverity, worldDir, worldExit);
                if (rep.JawGone) { JawGone = true; if (rep.HeadSeverity < 2) HangJaw(); }
                if (rep.EyeOut && !EyeOut)
                {
                    EyeOut = true;
                    _eyePos = EyeSocket();
                    _eyePrev = _eyePos - worldDir * 7f;
                }
            }
            else if (rep.ThroughAndThrough)
            {
                Gore.Chunks(worldExit, worldDir, P.Skin, (int)(2f + 5f * rep.Gore));
            }

            Shock += rep.Shock;
            BleedRate += rep.Bleed;
            Crippled |= rep.Cripple;
            // A hit that does not kill a man normally staggers him, and that stagger is the
            // second you use to aim properly. One man in the roster does not give it to you:
            // a wounding shot on him is a wasted shot and he is still coming up.
            if (!NoFlinch)
                StunLeft = MathF.Max(StunLeft, Math.Clamp(rep.Shock / 90f, 0.12f, 1.4f));

            Flinch = 1f;
            FlinchDir = MathF.Sign(worldDir.X) == 0 ? Face : MathF.Sign(worldDir.X);
            float heft = Math.Clamp(rep.Shock / 55f, 0.4f, 1.6f);
            // The bullet hits the trunk, not the feet. The chest takes most of it, the spine
            // drags the hips after it, and the legs are left to catch what is coming.
            float push = MathF.Sign(worldDir.X);
            ChestV += new Vector2(push * Rng.F(300f, 520f) * heft, -Rng.F(30f, 90f) * heft);
            PelvisV += new Vector2(push * Rng.F(110f, 190f) * heft, 0f);
            Muscle = MathF.Max(0.15f, Muscle - 0.45f * heft);
            Arm.Vel += worldDir * 420f * heft + new Vector2(0f, 300f * heft);

            if (rep.Disarm) Drop();

            if (rep.Instant) KillNow(worldDir, rep);
            else if (rep.Cripple || Shock >= 78f) Collapse(worldDir, rep.Shock / 70f);
        }

        void AddWound(Vector2 local, Vector2 dir, bool exit, float bleed, float gore)
        {
            // keep the mark on the body: an exit point extrapolated past him would otherwise
            // draw out in mid air
            local.X = Math.Clamp(local.X, -40f, 46f);
            local.Y = Math.Clamp(local.Y, -438f, -12f);
            Wounds.Add(new Wound
            {
                Local = local,
                Dir = dir,
                Exit = exit,
                // a wound mark is only ever the size of the hole the bullet made; the chunks,
                // the spray and the torn skull are what sell the damage
                Size = exit ? 5.2f : 3.6f,
                Bleed = bleed,
                Age = 0f,
                Seed = Rng.F(0f, 6.283f),
            });
            if (Wounds.Count > 18) Wounds.RemoveAt(0);
        }

        public void KnockHat(Vector2 dir)
        {
            if (!HatOn) return;
            HatOn = false;
            Gore.LaunchHat(L2W(new Vector2(2f, -438f)), dir * Rng.F(240f, 420f) + new Vector2(0f, -400f),
                P.Hat, P.HatBand, Def.HatStyle, Face, Scale);
            Sfx.Play("ric", 0.6f, Rng.F(0.9f, 1.2f));
        }

        public void Drop()
        {
            if (!Arm.HasGun) return;
            Arm.HasGun = false;
            // Once it is out of his hand it stays out. He is not picking it back up: the round
            // is what he has lost, not a second and a half.
            Disarmed = true;
            Gore.LaunchGun(Arm.Hand, new Vector2(Face * Rng.F(90f, 220f), -Rng.F(240f, 400f)), Arm.GunAngle, P, Face);
            Sfx.Play("thud", 0.4f, 1.4f);
        }

        /// <summary>Off their feet. Still breathing unless the blood runs out.</summary>
        public void Collapse(Vector2 dir, float force)
        {
            if (Collapsed) return;
            Collapsed = true;
            DeadTime = 0f;
            // Nothing is rebuilt and nothing is swapped in. The same body carries on from
            // exactly where it was; all that happens is that the strength goes out of it.
            Muscle = 0f;
            Doll.Muscle = 0f;
            Doll.StepLeg = -1;
            Doll.KickAll(new Vector2(PelvisV.X * 0.6f + dir.X * 34f * force, -30f));
            Doll.Kick(Ragdoll.Chest, new Vector2(ChestV.X * 0.5f, ChestV.Y * 0.4f));
            Doll.Kick(Ragdoll.Chest, dir * 120f * force);
            Doll.Spin(-Face * Rng.F(1.2f, 2.6f) * force);
            if (Arm.HasGun) Drop();
            Sfx.Play("flesh", 0.7f, Rng.F(0.85f, 1.05f));
        }

        void KillNow(Vector2 dir, ShotReport rep)
        {
            Killed = true;
            Blood = MathF.Min(Blood, 42f);
            BleedRate += 12f;
            if (!Collapsed) Collapse(dir, 1.35f);
            Sfx.Play(HeadDamage >= 2 ? "skull" : "flesh", 1f, Rng.F(0.92f, 1.08f));
        }

        // ---------- update ----------
        public void Update(float dt, Vector2 armTarget, float armWeight, bool holstered)
        {
            Breath += dt;
            _pulse += dt * (Down ? 2.2f : 3.4f);
            StunLeft = MathF.Max(0f, StunLeft - dt);
            UpdateEye(dt);

            // blood loss and clotting
            // once he is off his feet the bleeding never quite stops, so a man who goes down
            // does eventually go out rather than lying there indefinitely
            if (Collapsed && !Dead) BleedRate = MathF.Max(BleedRate, 5f);

            if (BleedRate > 0f)
            {
                Blood -= BleedRate * dt;
                BleedRate = MathF.Max(0f, BleedRate - dt * 0.55f);
                if (Blood < 0f) Blood = 0f;
            }
            Shock = MathF.Max(0f, Shock - dt * 15f);

            for (int i = 0; i < Wounds.Count; i++)
            {
                var w = Wounds[i]; w.Age += dt; Wounds[i] = w;
                float rate = Down ? 3.4f : 1.6f;
                if (w.Bleed > 4f && Rng.Chance(dt * rate)) Gore.Drip(WoundPos(w), P.Skin);
                // wounds spurt with the pulse while there is any pressure left, hardest while dying
                if (w.Bleed > 4.5f && Blood > 6f && MathF.Sin(_pulse * MathF.PI) > (Down ? 0.6f : 0.86f) && Rng.Chance(dt * (Down ? 16f : 9f)))
                {
                    Basis(out Vector2 up, out Vector2 fwd);
                    Vector2 outward = Vector2.Normalize(fwd * w.Dir.X * Face + up * -w.Dir.Y + Rng.Dir() * 0.25f);
                    Gore.Spurt(WoundPos(w), outward, Math.Clamp(w.Bleed / 22f, 0.35f, 1.2f) * (Blood / 100f));
                }
            }

            if (Down)
            {
                DeadTime += dt;
                Doll.Step(dt, Scene.GroundY);
                if (!Dead && Blood <= 0f) Killed = true;

                Arm.Shoulder = Doll.Pos[Ragdoll.Chest];
                if (Dead)
                {
                    Arm.Snap(Doll.Pos[Ragdoll.HandG]);
                }
                else
                {
                    // on the ground but not finished: he can still work the gun, badly
                    Arm.Holstered = false;
                    Arm.Update(dt, Arm.ClampReach(armTarget), armWeight + Impairment * 0.4f);
                }
                // what runs out of him gathers under the body
                if (BleedRate > 0.5f || Wounds.Count > 0)
                    foreach (var w in Wounds)
                        if (Rng.Chance(dt * 2.2f))
                            Gore.Pool(WoundPos(w) + new Vector2(Rng.F(-24f, 24f), 0f), Rng.F(10f, 24f) * Math.Clamp(w.Bleed / 12f, 0.5f, 1.8f));
                UpdateFlaps(dt);
                return;
            }

            Flinch = MathF.Max(0f, Flinch - dt * 3.2f);
            Kneel = Math.Clamp(Kneel + (Shock > 40f ? dt * 0.8f : -dt * 0.25f), 0f, 1f);

            Balance(dt);
            Doll.Step(dt, Scene.GroundY);

            Arm.Shoulder = L2W(new Vector2(10f, -318f));
            Arm.HolsterPoint = L2W(new Vector2(48f, -172f));
            Arm.Holstered = holstered;

            // a wounded man cannot hold the gun still
            Vector2 target = armTarget;
            if (!holstered)
            {
                float shake = Impairment * 26f;
                if (shake > 0.5f)
                    target += new Vector2(MathF.Sin(Breath * 7.3f) * shake, MathF.Cos(Breath * 5.1f) * shake);
                if (StunLeft > 0f) target += new Vector2(Face * -40f, 60f) * StunLeft;
            }
            Arm.Update(dt, holstered ? Arm.HolsterPoint : target, armWeight + Impairment * 0.25f);

            // Last of all, once the body and the ragdoll have both finished moving. Solved
            // any earlier and every loose piece of him is hanging off where his head WAS a
            // frame ago, which during a fall is several inches behind where it is now.
            UpdateFlaps(dt);
        }
        /// <summary>
        /// What keeps him on his feet. There is no separate standing simulation any more - the
        /// body is the ragdoll whether he is upright or in the dirt - so all this does is tell
        /// his muscles what to try for: how hard to pull, what angle he is holding himself at,
        /// and where to throw a boot when his weight gets away from him. Take the strength out
        /// and the very same body falls over, because falling over is not a different mode.
        /// </summary>
        void Balance(float dt)
        {
            const float G = 2000f;
            float h = RestLeg * Scale;
            float omega = MathF.Sqrt(G / h);

            // shock takes the strength out of him, and a man with a leg gone has nothing left
            // to stand on at all
            float wantM = 1f - Math.Clamp(Shock / 110f, 0f, 0.85f);
            wantM *= Math.Clamp(Blood / 55f, 0.3f, 1f);    // bleeding out takes the starch out of him
            if (Crippled) wantM = 0f;
            Muscle += (wantM - Muscle) * (1f - MathF.Exp(-6f * dt));

            // No formula for how low he stands. A wounded knee gives, and a wide stance puts
            // the legs at an angle so the hips come down on their own.
            Crouch += (Kneel * 40f - Crouch) * (1f - MathF.Exp(-7f * dt));

            float footWorld = SupportX;
            float lever = PelvisP.X - footWorld;
            ComX = PelvisP.X - Origin.X;        // the feet are kept in his own coordinates
            ComVx = PelvisV.X;

            // the lean he is trying to hold: leaning into it is how a man rides a shove out
            LeanAng = Math.Clamp(MathF.Atan2(lever, h) * 0.95f, -0.55f, 0.55f);

            // the orders his muscles get this frame, and the whole of them
            Doll.Muscle = Down ? 0f : Muscle;
            Doll.Lean = LeanAng;
            Doll.StandHeight = RestLeg * Scale - Crouch * Scale;
            // the legs aim at a length their own bones can actually be, less however far he sinks
            Doll.LegRest = Doll.LegLen - Crouch * Scale;
            Doll.GroundY = Scene.GroundY;
            Doll.Face = Face;
            Doll.StepLift = 34f * Scale;

            // What his legs have left. Every stagger spends some of it and standing quietly
            // buys it back, so a man can save himself once or twice and then cannot. This is
            // the only thing that ends a stagger now: there is no count of steps and no timer
            // that decides he has had enough. When there is not enough leg left to carry him
            // the hips come down, and coming down is what takes him off his feet.
            _legSpent = Math.Clamp(_legSpent - dt * 0.55f, 0f, 1f);
            Doll.LegPower = Math.Clamp((1f - _legSpent * 0.85f) * (0.45f + Blood / 110f), 0.1f, 1f);

            if (Doll.StepLanded)
            {
                Doll.StepLanded = false;
                Sfx.Play("thud", 0.3f, Rng.F(1.2f, 1.45f));
                Gore.DustPuff(new Vector2(SupportX, Scene.GroundY), new Vector2(0f, -1f), 0.34f);
                _stepDelay = 0.05f;
            }

            if (Down) { Doll.StepLeg = -1; return; }

            // His hips are on the dirt. Whatever the rest of this thinks he is not standing,
            // and going on throwing boots is what left a downed man twitching a leg for ever.
            if (Scene.GroundY - PelvisP.Y < SagFloor * Scale)
            {
                _sagging += dt;
                if (_sagging > 0.25f)
                {
                    float away = PelvisV.X != 0f ? MathF.Sign(PelvisV.X) : -Face;
                    Collapse(new Vector2(away, -0.1f),
                             Math.Clamp(MathF.Abs(PelvisV.X) / 300f, 0.4f, 1.2f));
                    return;
                }
            }
            else _sagging = 0f;

            _stepDelay = MathF.Max(0f, _stepDelay - dt);
            float hurt = Math.Clamp(Impairment * 0.6f + Kneel * 0.3f, 0f, 1f);

            // How badly he is losing it. One step is a man catching himself and it costs him
            // nothing; three in a couple of seconds is a man who has already lost and has not
            // worked it out yet. Without this he never runs out of steps - he just throws one
            // leg out and plants it, over and over, upright and on the spot for ever, which
            // reads as a bouncing boot rather than a man going over. Staggering has to end in
            // the dirt or it is not staggering.
            _stagger = MathF.Max(0f, _stagger - dt * 1.2f);

            if (!Stepping && _stepDelay <= 0f)
            {
                // Capture point: the spot he would have to plant a boot on to stop dead. A
                // wider stance covers more ground, so a man who has already staggered once is
                // harder to tip than one stood square.
                float capture = PelvisP.X + PelvisV.X / omega;
                float support = 20f * Scale + MathF.Min(Stance, 90f) * 0.45f;

                if (MathF.Abs(capture - footWorld) > support)
                {
                    float maxStep = Art.Lerp(200f, 120f, hurt) * Scale;
                    float d = MathF.Sign(capture - footWorld);
                    bool desperate = MathF.Abs(capture - footWorld) > maxStep;

                    // He does not give up at a count of two any more. He throws the leg out
                    // every time, and whether it saves him is settled by whether the leg
                    // reaches the street in front of his weight and has anything left to push
                    // with when it gets there. A step he cannot reach costs him the strength
                    // he spent on it, which is why the third one is weaker than the first.
                    if (desperate) _failedSteps++;

                    // the trailing boot swings: whichever is already furthest the way he is going
                    int leg = (d * FootP(0).X >= d * FootP(1).X) ? 0 : 1;
                    float from = FootP(leg).X;
                    // a stumbling man throws short of where he needs to, so one step rarely does
                    float reach = (capture - from) * Rng.F(0.6f, 0.92f) + d * 10f;
                    float to = from + Math.Clamp(reach, -maxStep, maxStep);

                    // boots do not pass through each other, and a man cannot do the splits
                    float other = FootP(1 - leg).X;
                    if (d > 0f) to = MathF.Max(to, other + 6f);
                    else to = MathF.Min(to, other - 6f);
                    to = Math.Clamp(to, other - MaxStance * Scale, other + MaxStance * Scale);

                    ThrowFoot(leg, to, hurt);
                }
            }

            // A boot can never be left stranded: once it is further from the hips than the leg
            // can reach, that leg has to come back under him whatever the balance says.
            if (!Stepping && _stepDelay <= 0f)
            {
                for (int i = 0; i < 2; i++)
                {
                    float out_ = FootP(i).X - PelvisP.X;
                    if (MathF.Abs(out_) <= MaxReach * Scale) continue;
                    ThrowFoot(i, PelvisP.X + MathF.Sign(out_) * MaxReach * 0.55f * Scale, hurt);
                    break;
                }
            }

            // settled: he gathers his feet back under him and walks back to his mark
            if (!Stepping && MathF.Abs(PelvisV.X) < 110f && MathF.Abs(lever) < 32f)
            {
                _failedSteps = 0;
                for (int i = 0; i < 2; i++)
                {
                    float wantX = Origin.X + (i == 0 ? -13f : 21f) * Face * Scale;
                    int f = Ragdoll.Foot(i);
                    float gap = wantX - Doll.Pos[f].X;
                    // the further a boot is from where it belongs the harder he pulls it in,
                    // or a wide stance he never asked for just stays wide
                    float rate = MathF.Max(38f, MathF.Abs(gap) * 10f);
                    float shift = MathF.Min(rate * dt, MathF.Abs(gap)) * MathF.Sign(gap);
                    Doll.Pos[f].X += shift;
                    Doll.Prev[f].X += shift;
                }

                // and he walks himself back to his mark, trunk first with the legs following
                // it. Without this, every stagger he rides out leaves him a few feet further
                // down the street than he started, and he never gets them back.
                float home = Origin.X - PelvisP.X;
                if (MathF.Abs(home) > 0.5f)
                {
                    Vector2 d = new Vector2(MathF.Min(38f * dt, MathF.Abs(home)) * MathF.Sign(home), 0f);
                    Doll.Place(Ragdoll.Pelvis, PelvisP + d);
                    Doll.Place(Ragdoll.Chest, ChestP + d);
                }
            }
        }

        /// <summary>
        /// Lets him find his feet before anybody looks at him. A pose drawn by hand is never
        /// quite what the muscles would hold, and the difference is enough that his first act
        /// on being built was to stagger backwards out of it - five steps before the round had
        /// even been called. So he does his settling here, with the stepping switched off, and
        /// is then put back on his mark with the speed taken out of him.
        /// </summary>
        void Settle()
        {
            Doll.Muscle = 1f;
            Doll.Lean = 0f;
            Doll.StandHeight = RestLeg * Scale;
            Doll.GroundY = Scene.GroundY;
            Doll.Face = Face;
            Doll.StepLeg = -1;
            for (int i = 0; i < 200; i++) Doll.Step(Ragdoll.Fixed, Scene.GroundY);

            // Put him on his mark without touching the shape he settled into - dragging a foot
            // to where the drawing says it belongs puts the legs back out of agreement with
            // the muscles, which is the very thing this is here to avoid.
            Doll.Shift(new Vector2(Origin.X - SupportX, 0f));
            for (int i = 0; i < Ragdoll.Count; i++) Doll.Prev[i] = Doll.Pos[i];
        }

        /// <summary>
        /// Throws a boot at a spot on the street. The arc is a target, not a path: the leg has
        /// to get there through its own bones against everything else pulling on it, and a man
        /// with little left in him swings slow and lands short.
        /// </summary>
        void ThrowFoot(int leg, float targetX, float hurt)
        {
            Doll.StepLeg = leg;
            Doll.StepFrom = FootP(leg);
            Doll.StepTarget = new Vector2(targetX, FootFloor);
            Doll.StepT = 0f;
            Doll.StepDur = Art.Lerp(0.20f, 0.34f, hurt) * Rng.F(0.85f, 1.15f);
            Doll.Asleep = false;
            Steps++;
            _stagger += 1f;

            // The step costs him. A long one he is only just going to reach costs him more
            // than a short gather, and it is this, not a counter, that runs him out of legs.
            float lunge = MathF.Abs(targetX - Doll.StepFrom.X) / (MaxReach * Scale);
            _legSpent = Math.Clamp(_legSpent + 0.16f + lunge * 0.30f, 0f, 1f);
        }

        // ---------- wound placement ----------
        public Vector2 WoundPos(Wound w) => WoundPos(w.Local);

        /// <summary>Body-space point to world, following the ragdoll once they are on the ground.</summary>
        public Vector2 WoundPos(Vector2 local)
        {
            if (!Down || !Doll.Active) return L2W(local);

            var d = Doll;
            Vector2 up, fwd;
            Basis(out up, out fwd);

            if (local.Y < -352f)   // head
            {
                Vector2 hUp = d.HeadUp;
                Vector2 hFwd = new Vector2(-hUp.Y, hUp.X) * Face;
                return d.Pos[Ragdoll.Head] + hFwd * local.X + hUp * -(local.Y + 404f);
            }
            if (local.Y < -200f)   // trunk: lerp between pelvis and chest
            {
                float t = Math.Clamp((local.Y + 200f) / -112f, 0f, 1.6f);
                Vector2 axis = d.Pos[Ragdoll.Chest] - d.Pos[Ragdoll.Pelvis];
                Vector2 b = d.Pos[Ragdoll.Pelvis] + axis * t;
                Vector2 side = Vector2.Normalize(new Vector2(-axis.Y, axis.X)) * Face;
                return b + side * local.X;
            }
            // legs
            {
                float t = Math.Clamp((local.Y + 200f) / -186f, 0f, 1f);
                int knee = local.X < 0f ? Ragdoll.KneeB : Ragdoll.KneeF;
                int foot = local.X < 0f ? Ragdoll.FootB : Ragdoll.FootF;
                Vector2 b = t < 0.5f
                    ? Vector2.Lerp(d.Pos[Ragdoll.Pelvis], d.Pos[knee], t * 2f)
                    : Vector2.Lerp(d.Pos[knee], d.Pos[foot], (t - 0.5f) * 2f);
                return b;
            }
        }

        // ---------- drawing ----------
        // Everything is drawn twice: once with Art.InkPass on, which lays down one fattened ink
        // silhouette for the whole body, then once for the colour. That is what keeps the man
        // reading as one figure instead of a stack of separate pills.
        public void Draw(bool showZones)
        {
            Shadow();

            Art.InkPass = true;
            Body();
            Art.InkPass = false;
            Body();

            Details();

            if (showZones) DrawZones();
        }

        void Body()
        {
            if (Down && Doll.Active) FallenBody();
            else StandingBody();
        }

        void Details()
        {
            DrawWounds();
            DrawEye();
        }

        void DrawZones()
        {
            foreach (var v in Volumes())
            {
                Color c = v.Zone switch
                {
                    Zone.Head => Color.Red,
                    Zone.Torso => Color.Orange,
                    Zone.Arm => Color.SkyBlue,
                    Zone.Hand => Color.Blue,
                    Zone.Leg => Color.Green,
                    _ => Color.Yellow,
                };
                Art.Capsule(v.A, v.B, v.R, Art.Alpha(c, 0.2f));
            }
            if (!Down)
                foreach (var o in Anatomy.Parts)
                    Art.Capsule(L2W(o.A), L2W(o.B), o.R * Scale, Art.Alpha(Color.Magenta, 0.25f));
        }

        void Shadow()
        {
            Vector2 at = (Down && Doll.Active) ? Doll.Pos[Ragdoll.Pelvis] : Root;
            float w = (Down && Doll.Active) ? 96f : 78f;
            float h = (Down && Doll.Active) ? 12f : 15f;
            Raylib.DrawEllipse((int)at.X, (int)(Scene.GroundY + 6f), w * Scale, h * Scale, new Color(24, 16, 20, 70));
        }

        // ---------- standing ----------
        void StandingBody()
        {
            float sway = MathF.Sin(Breath * 1.6f) * 2.2f;
            float fl = Flinch * 12f * -FlinchDir;
            Vector2 Lp(float x, float y) => L2W(new Vector2(x + fl * Face * 0.35f, y + sway * 0.25f));

            DrawBackArm(Lp);
            DrawLeg(Lp, 0, P.PantsDark, Art.Shade(P.Boots, 0.82f));
            DrawNeck(Lp);
            DrawTorso(Lp);
            DrawLeg(Lp, 1, P.Pants, P.Boots);
            DrawBeltAndHolster(Lp);

            Basis(out Vector2 up, out Vector2 fwd);
            DrawHead(L2W(new Vector2(4f, -398f)), up, fwd, HatOn);
            DrawGunArm();
        }

        /// <summary>
        /// Seen from the side, so the body is narrow: the shirt front is the edge facing the
        /// other man, and the vest covers the flank we are looking at.
        /// </summary>
        /// <summary>
        /// The neck, drawn under the torso so the collar sits on it and under the head so the
        /// jaw covers its top. Without it the head floats clear of the shoulders on anyone whose
        /// beard does not happen to fill the gap - which is why some men looked to have a neck
        /// and others did not.
        /// </summary>
        void DrawNeck(Func<float, float, Vector2> Lp)
            => Art.CapsuleInked(Lp(0f, -344f), Lp(5f, -374f), 13f * Scale, P.SkinDark);

        void DrawTorso(Func<float, float, Vector2> Lp)
        {
            Art.Shape(new[]
            {
                Lp(-24f, -208f), Lp(-26f, -306f), Lp(-15f, -342f),
                Lp(14f, -346f), Lp(28f, -312f), Lp(26f, -208f)
            }, P.Shirt);

            Art.Shape(new[]
            {
                Lp(-26f, -212f), Lp(-27f, -304f), Lp(-14f, -338f),
                Lp(8f, -340f), Lp(11f, -212f)
            }, P.Vest, 3f);
            Art.Seam(Lp(10f, -214f), Lp(8f, -336f), 3f * Scale, P.VestDark);

            Art.Seam(Lp(21f, -216f), Lp(19f, -334f), 3f * Scale, P.ShirtDark);
            if (!Art.InkPass)
                for (int i = 0; i < 4; i++)
                    Raylib.DrawCircleV(Lp(20f, -230f - i * 28f), 2.6f * Scale, P.ShirtDark);

            // The neckerchief is worn ON the neck, up under the jaw, not down on the collar
            // bone where it used to sit half swallowed by the shirt. The neck runs from
            // -344 at the shoulders to -374 at the skull, so the band goes round the middle
            // of that and the knot hangs off the front of it.
            if (Def.ScarfOn)
            {
                Art.Shape(new[] { Lp(-13f, -366f), Lp(16f, -370f), Lp(19f, -350f), Lp(-11f, -346f) }, P.Scarf, 3f);
                Art.Shape(new[] { Lp(12f, -362f), Lp(23f, -358f), Lp(18f, -344f), Lp(8f, -349f) }, P.Scarf, 3f);
            }
        }

        /// <summary>
        /// One leg. The hip stays with the pelvis while the boot carries its own displacement and
        /// its own lift, so each leg swings and plants on its own during a stumble.
        /// </summary>
        /// <summary>
        /// Knee solved by two-bone IK with the bend locked forward, so the leg straightens as the
        /// boot goes out behind him and folds as it comes back under. It can never bend backwards.
        /// </summary>
        public static Vector2 SolveKnee(Vector2 hip, Vector2 foot, float thigh, float shin, int face)
        {
            Vector2 d = foot - hip;
            float dist = d.Length();
            if (dist < 0.001f) { d = new Vector2(0f, 1f); dist = 1f; }
            float reach = thigh + shin - 1f;
            float min = MathF.Abs(thigh - shin) + 1f;
            float clamped = Math.Clamp(dist, min, reach);
            Vector2 dir = d / dist;
            float a = (thigh * thigh - shin * shin + clamped * clamped) / (2f * clamped);
            float h = MathF.Sqrt(MathF.Max(0f, thigh * thigh - a * a));
            Vector2 perp = new Vector2(-dir.Y, dir.X);
            return hip + dir * a - perp * h * face;     // knee forward of the hip-ankle line
        }

        void DrawLeg(Func<float, float, Vector2> Lp, int leg, Color pants, Color boot)
        {
            LegJoints(leg, out Vector2 hip, out Vector2 knee, out Vector2 ankle);
            // A leg is not a pipe. The thigh is at its thickest where it leaves the seat and
            // narrows into the knee; the calf picks up again just below the knee and runs down
            // to an ankle barely half the width. Drawn as two even capsules it had no knee at
            // all, which is what made the man look like he was standing on two posts.
            Art.TaperInked(hip, knee, ThighTop * Scale, ThighLow * Scale, pants);
            Art.TaperInked(knee, ankle, CalfTop * Scale, AnkleW * Scale, pants);

            // stood up, the boot belongs to the ground: it sits flat however the shin is angled
            DrawBoot(ankle, new Vector2(0f, 1f), new Vector2(Face, 0f), boot);
        }

        /// <summary>
        /// One boot, built along whatever direction the leg is pointing. <paramref name="down"/>
        /// runs from the ankle towards the sole and <paramref name="fwd"/> towards the toe, so
        /// a man stood up gets a boot flat on the street and a man face down in it gets the
        /// same boot lying the way his leg fell. He wore boots alive; he keeps them.
        /// </summary>
        void DrawBoot(Vector2 ankle, Vector2 down, Vector2 fwd, Color boot)
        {
            float s = Scale;
            Vector2 B(float dx, float dy) => ankle + fwd * (dx * s) + down * (dy * s);
            Art.Shape(new[]
            {
                B(-16f, -6f), B(14f, -6f), B(16f, 24f),
                B(34f, 28f), B(34f, 38f), B(-18f, 38f), B(-17f, 22f)
            }, boot);
            Art.Seam(B(-16f, 6f), B(14f, 6f), 3f * s, Art.Shade(boot, 0.78f));

            switch (Def.BootStyle)
            {
                case 1:     // a cuff turned down over the top. It flares at the mouth and
                            // comes back flush with the shaft at the bottom: squared off, its
                            // wider bottom corners hung out past the boot on either side and
                            // the cuff read as a band sitting beside the leg, not on it.
                            // The shaft runs -16..14 across, so the cuff ends there too.
                    Art.Shape(new[] { B(-20f, -22f), B(18f, -22f), B(14f, -4f), B(-16f, -4f) },
                        Art.Shade(boot, 1.18f), 2.5f);
                    Art.Seam(B(-18f, -13f), B(16f, -13f), 2.5f * s, Art.Shade(boot, 0.9f));
                    break;
                case 2:     // a spur off the heel
                    Art.CapsuleInked(B(-18f, 30f), B(-30f, 30f), 2.5f * s, P.Metal, 2f);
                    Art.CircleInked(B(-34f, 30f), 6.5f * s, P.Metal, 2f);
                    if (!Art.InkPass) Raylib.DrawCircleV(B(-34f, 30f), 2.2f * s, Art.Shade(P.Metal, 0.6f));
                    break;
            }
        }

        /// <summary>A fallen boot, laid along the shin that is wearing it.</summary>
        void DrawFallenBoot(Vector2 knee, Vector2 foot, Color boot)
        {
            Vector2 down = foot - knee;
            float len = down.Length();
            down = len > 0.001f ? down / len : new Vector2(0f, 1f);
            DrawBoot(foot, down, new Vector2(down.Y, -down.X) * Face, boot);
        }

        void DrawBackArm(Func<float, float, Vector2> Lp)
        {
            Vector2 sh = Lp(-10f, -312f);
            // it hangs clear of the hip rather than straight down the flank, or the hand ends up
            // behind the belt and the thigh and you never see it at all
            Vector2 el = Lp(-30f - Flinch * 8f, -252f);
            Vector2 hd = Lp(-33f, -186f);
            Art.CapsuleInked(sh, el, 14f * Scale, P.ShirtDark);
            Art.CapsuleInked(el, hd, 12f * Scale, P.ShirtDark);
            // the far hand, in shadow, but the same hand as the other one
            DrawFist(hd, HandAngle(el, hd), Art.Shade(P.Hand, 0.86f), 0.9f, P.Gloved, false);
        }

        void DrawBeltAndHolster(Func<float, float, Vector2> Lp)
        {
            Color leather = new Color(66, 44, 30, 255);
            Art.Shape(new[] { Lp(-26f, -216f), Lp(28f, -218f), Lp(28f, -196f), Lp(-26f, -194f) }, leather, 3f);
            if (!Art.InkPass)
            {
                Raylib.DrawCircleV(Lp(22f, -206f), 8f * Scale, new Color(198, 164, 86, 255));
                for (int i = -1; i <= 2; i++)
                    Raylib.DrawCircleV(Lp(i * -13f - 2f, -206f), 3.2f * Scale, new Color(196, 170, 96, 255));
            }

            // The revolver rides muzzle-down in the leather. The small cant is what a rig does
            // for you: with the barrel hanging a little back of straight down, the raked grip
            // comes up and over the hip where a hand can get at it.
            bool showGun = Arm.HasGun && Arm.Holstered && !Down;
            float holsterAngle = HolsterAngle;
            Vector2 gunAt = HolsterGunAt(Lp);

            // The gun goes down before the leather does, so the barrel ends up inside the
            // holster and only the grip and the top of the frame stand clear behind his hip.
            // In the colour pass it needs an outline of its own, or dark wood on dark leather
            // vanishes into the belt.
            if (showGun)
            {
                if (Art.InkPass) DrawRevolver(gunAt, holsterAngle, P, HolsterScale, false, 0f, Arm.Cylinder);
                else
                {
                    Art.InkPass = true;
                    DrawRevolver(gunAt, holsterAngle, P, HolsterScale, false, 0f, Arm.Cylinder);
                    Art.InkPass = false;
                    DrawRevolver(gunAt, holsterAngle, P, HolsterScale, false, 0f, Arm.Cylinder);
                }
            }

            Art.Shape(new[] { Lp(18f, -206f), Lp(52f, -204f), Lp(48f, -142f), Lp(22f, -144f) },
                new Color(82, 52, 34, 255), 3f);
            Art.Seam(Lp(20f, -176f), Lp(49f, -174f), 3f * Scale, new Color(58, 36, 24, 255));
            Art.Seam(Lp(20f, -200f), Lp(51f, -198f), 4f * Scale, new Color(58, 36, 24, 255));

        }

        // Where the gun sits in the leather. The arm is on our side of it, so the forearm
        // passes in front of the gun and only what clears the sleeve is on view.
        const float HolsterScale = 0.7f;
        static float HolsterAngle => MathF.PI * 0.5f + 0.16f;
        static Vector2 HolsterGunAt(Func<float, float, Vector2> Lp) => Lp(36f, -228f);

        /// <summary>
        /// Where the sleeve ends. Arm.Hand is the gun's origin, which sits up at the frame; the
        /// wrist is down in the fist, so the forearm runs to here instead. Only the drawing
        /// moves - the aim solver and the muzzle still work off Arm.Hand.
        /// </summary>
        Vector2 WristPoint =>
            (Arm.HasGun && !Arm.Holstered && !Down)
                ? Arm.Hand + Art.Rot(new Vector2(-10f, 13f * Face) * Scale, Arm.GunAngle)
                : Arm.Hand;

        void DrawGunArm()
        {
            Vector2 wrist = WristPoint;
            Art.CapsuleInked(Arm.Shoulder, Arm.Elbow, 15f * Scale, P.Shirt);
            Art.CapsuleInked(Arm.Elbow, wrist, 13f * Scale, P.Shirt);
            Vector2 fore = Vector2.Normalize(wrist - Arm.Elbow + new Vector2(0.001f, 0f));
            Art.Seam(wrist - fore * 20f * Scale, wrist - fore * 12f * Scale, 26f * Scale, P.ShirtDark);

            if (Arm.HasGun && !Arm.Holstered && !Down)
            {
                DrawRevolver(Arm.Hand, Arm.GunAngle, P, 1f, true, Arm.Hammer, Arm.Cylinder);
            }
            else
            {
                // empty, but still the same hand - the shape only loses its trigger finger
                DrawFist(Arm.Hand, HandAngle(Arm.Elbow, Arm.Hand), P.Hand, 0.95f, P.Gloved, true);
            }
        }

        // ---------- fallen ----------
        void FallenBody()
        {
            var d = Doll;
            Vector2 chest = d.Pos[Ragdoll.Chest], pelvis = d.Pos[Ragdoll.Pelvis], neck = d.Pos[Ragdoll.Neck];

            Art.CapsuleInked(chest, d.Pos[Ragdoll.ElbowB], 14f * Scale, P.ShirtDark);
            Art.CapsuleInked(d.Pos[Ragdoll.ElbowB], d.Pos[Ragdoll.HandB], 12f * Scale, P.ShirtDark);
            DrawFist(d.Pos[Ragdoll.HandB], HandAngle(d.Pos[Ragdoll.ElbowB], d.Pos[Ragdoll.HandB]), Art.Shade(P.Hand, 0.86f), 0.9f, P.Gloved, false);

            Art.TaperInked(pelvis, d.Pos[Ragdoll.KneeB], ThighTop * Scale, ThighLow * Scale, P.PantsDark);
            Art.TaperInked(d.Pos[Ragdoll.KneeB], d.Pos[Ragdoll.FootB], CalfTop * Scale, AnkleW * Scale, P.PantsDark);
            DrawFallenBoot(d.Pos[Ragdoll.KneeB], d.Pos[Ragdoll.FootB], Art.Shade(P.Boots, 0.82f));

            Vector2 axis = chest - pelvis;
            if (axis.LengthSquared() < 1f) axis = new Vector2(0f, -1f);
            Vector2 side = Vector2.Normalize(new Vector2(-axis.Y, axis.X)) * 27f * Scale;
            Art.Shape(new[] { pelvis + side, chest + side * 1.05f, neck + side * 0.6f, neck - side * 0.6f, chest - side * 1.05f, pelvis - side }, P.Shirt);
            Art.Shape(new[] { pelvis - side * 0.95f, chest - side, neck - side * 0.55f, neck - side * 0.05f, pelvis - side * 0.1f }, P.Vest, 3f);
            Art.Shape(new[] { pelvis + side * 1.04f + axis * 0.07f, pelvis - side * 1.04f + axis * 0.07f, pelvis - side * 1.04f - axis * 0.07f, pelvis + side * 1.04f - axis * 0.07f },
                new Color(66, 44, 30, 255), 3f);

            Art.TaperInked(pelvis, d.Pos[Ragdoll.KneeF], ThighTop * Scale, ThighLow * Scale, P.Pants);
            Art.TaperInked(d.Pos[Ragdoll.KneeF], d.Pos[Ragdoll.FootF], CalfTop * Scale, AnkleW * Scale, P.Pants);
            DrawFallenBoot(d.Pos[Ragdoll.KneeF], d.Pos[Ragdoll.FootF], P.Boots);

            Vector2 hUp = d.HeadUp;
            Vector2 hFwd = new Vector2(-hUp.Y, hUp.X) * Face;
            Art.CapsuleInked(neck, d.Pos[Ragdoll.Head] - hUp * 26f * Scale, 11f * Scale, P.SkinDark);
            DrawHead(d.Pos[Ragdoll.Head], hUp, hFwd, HatOn);

            if (!Dead)
            {
                // still working the gun from the dirt: the arm is his, not the ragdoll's
                DrawGunArm();
            }
            else
            {
                Art.CapsuleInked(chest, d.Pos[Ragdoll.ElbowG], 15f * Scale, P.Shirt);
                Art.CapsuleInked(d.Pos[Ragdoll.ElbowG], d.Pos[Ragdoll.HandG], 13f * Scale, P.Shirt);
                DrawFist(d.Pos[Ragdoll.HandG], HandAngle(d.Pos[Ragdoll.ElbowG], d.Pos[Ragdoll.HandG]), P.Hand, 0.95f, P.Gloved, true);
            }
        }

        // ---------- head ----------
        void DrawHead(Vector2 c, Vector2 up, Vector2 fwd, bool withHat)
        {
            float r = 34f * Scale;
            Vector2 L(float x, float y) => c + fwd * x * Scale + up * -y * Scale;
            // The wound angle is measured in head space: 0 is straight out of the face, +pi/2 is
            // the crown. Anything that places the wound goes through here, or it lands somewhere
            // else entirely once his head is turned or he is lying on the ground.
            Vector2 Dir(float a) => fwd * MathF.Cos(a) + up * MathF.Sin(a);

            if (HeadDamage > 0 && !Art.InkPass)
            {
                // Under the skin is bone. This is what shows through where the silhouette has
                // been bitten away; the hole itself is drawn over the face further down.
                Raylib.DrawCircleV(c, r * 1.0f, Bone);
                Raylib.DrawCircleV(c, r * 0.84f, BoneDark);
            }

            int n = _skullJitter.Length;
            var outline = new Vector2[n];
            // An emptied skull has a lot more of it missing than a grazed one: the bite
            // out of the silhouette runs most of the way round the wound side, and takes
            // most of the way in to the middle where it is deepest.
            // how wide the bite is, and how deep: both follow how far it actually opened
            float arc = Art.Lerp(0.95f, 3.0f, HeadOpen);
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * MathF.PI * 2f;
                float rr = r;
                if (HeadDamage > 0)
                {
                    float diff = MathF.Abs(Art.WrapPi(a - HeadWoundAngle));
                    if (diff < arc * 0.5f)
                    {
                        float bite = 1f - MathF.Cos(diff / (arc * 0.5f) * MathF.PI * 0.5f);
                        rr = r * Art.Lerp(Art.Lerp(0.66f, 0.06f, HeadOpen), 1f, bite) * _skullJitter[i];
                    }
                }
                outline[i] = c + Dir(a) * rr;
            }

            // The face is never simply deleted. Whatever the wound covers, it covers - the hole is
            // drawn over the top of all this, so a shot to the cheek takes the cheek and no more.

            if (!JawGone)
                Art.Shape(new[] { L(-26f, 26f), L(26f, 24f), L(38f, 6f), L(30f, -6f), L(34f, -18f), L(10f, -32f), L(-22f, -22f) }, P.Skin, 3.5f);
            else
                Art.Shape(new[] { L(-24f, 20f), L(6f, 16f), L(16f, 2f), L(30f, -6f), L(34f, -18f), L(10f, -32f), L(-22f, -22f) }, P.Skin, 3.5f);

            Art.Shape(outline, P.Skin, 3.5f);

            {
                Art.Shape(new[] { L(30f, -6f), L(46f, 2f), L(30f, 10f) }, P.Skin, 3f);
                if (!Art.InkPass)
                {
                    if (EyeOut)
                    {
                        // Nothing left in the socket, so what you are looking at is the orbit:
                        // bare bone round the rim and a hole going back into the skull.
                        Vector2 e = L(20f, -6f);
                        Raylib.DrawCircleV(e, 12f * Scale, Bone);
                        Raylib.DrawCircleV(e, 9.5f * Scale, BoneDark);
                        Raylib.DrawCircleV(e, 6.8f * Scale, new Color(44, 8, 12, 255));
                        Raylib.DrawCircleV(e, 4f * Scale, new Color(14, 3, 6, 255));
                        // cheekbone under it, and what has run out of the socket since
                        Art.Capsule(L(10f, -16f), L(31f, -12f), 2.2f * Scale, Art.Alpha(Bone, 0.75f));
                        Raylib.DrawLineEx(e, L(19f, 8f), 6f * Scale, new Color(104, 12, 16, 230));
                        Raylib.DrawLineEx(L(19f, 8f), L(19f, 18f), 3.5f * Scale, new Color(104, 12, 16, 230));
                    }
                    else
                    {
                        Raylib.DrawCircleV(L(20f, -6f), 6.5f * Scale, Color.White);
                        Raylib.DrawCircleV(L(23f, -6f), 3.4f * Scale, new Color(34, 26, 22, 255));
                    }
                    Raylib.DrawLineEx(L(10f, -18f), L(30f, -15f), 5f * Scale, P.Hair);
                }
                if (!JawGone)
                {
                    switch (Def.Face)
                    {
                        case 1: Art.Shape(new[] { L(16f, 14f), L(40f, 12f), L(38f, 20f), L(16f, 21f) }, P.Hair, 2.5f); break;
                        case 2: Art.Shape(new[] { L(-20f, 16f), L(34f, 14f), L(36f, 26f), L(18f, 46f), L(-8f, 44f), L(-22f, 30f) }, P.Hair, 3f); break;
                        case 3:
                            if (!Art.InkPass)
                                for (int i = 0; i < 14; i++)
                                    Raylib.DrawCircleV(L(-16f + ((i * 37) % 46), 16f + ((i * 17) % 16)), 1.7f * Scale, Art.Alpha(P.Hair, 0.5f));
                            break;
                    }
                }
            }

            Art.CircleInked(L(-12f, -2f), 8f * Scale, P.SkinDark, 2.5f);
            DrawHair(L, withHat);

            if (!Art.InkPass)
            {
                if (JawGone)
                {
                    // With the jaw still hanging off him by one hinge there is no need to
                    // paint a row of blobs where it used to be: you can see where it went.
                    int blobs = JawHanging ? 4 : 7;
                    float br = JawHanging ? 3.4f : 5.5f;
                    for (int i = 0; i < blobs; i++)
                        Raylib.DrawCircleV(L(-18f + i * 8f, 18f + (i % 3) * 4f), br * Scale, new Color(112, 14, 19, 240));
                    if (!JawHanging) Raylib.DrawCircleV(L(4f, 22f), 9f * Scale, new Color(40, 6, 10, 255));
                }
                if (HeadDamage > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float a = i / (float)n * MathF.PI * 2f;
                        if (MathF.Abs(Art.WrapPi(a - HeadWoundAngle)) >= arc * 0.5f) continue;
                        Raylib.DrawCircleV(outline[i], 4.5f * Scale * _skullJitter[i], new Color(128, 16, 20, 255));
                    }
                    // the hole, wherever it landed - cheek, brow or the side of the crown
                    Vector2 wAt = c + Dir(HeadWoundAngle) * r * 0.5f;
                    float wRad = r * Art.Lerp(0.24f, 0.80f, HeadOpen);
                    SkullWound(wAt, wRad);
                    // what runs out of it starts at the underside of the hole, not the middle,
                    // or the drip is drawn straight over the skull
                    Vector2 low = wAt + new Vector2(0f, wRad * 0.85f);
                    float run = MathF.Min((20f + MathF.Min(DeadTime, 3f) * 20f) * Scale, MathF.Max(0f, Scene.GroundY - low.Y - 2f));
                    if (run > 3f)
                    {
                        // tapering, so it reads as something running down him rather than a bar
                        var bl = new Color(96, 12, 16, 220);
                        Raylib.DrawLineEx(low, low + new Vector2(0f, run * 0.55f), 5.5f * Scale, bl);
                        Raylib.DrawLineEx(low + new Vector2(0f, run * 0.5f), low + new Vector2(0f, run * 0.85f), 3.6f * Scale, bl);
                        Raylib.DrawCircleV(low + new Vector2(0f, run * 0.9f), 3f * Scale, bl);
                    }
                }
            }

            // The loose pieces go on last of all, over the hole and over the hair, because
            // that is where they are: hanging off the front of him.
            DrawFlaps(c, up, fwd);

            if (withHat) DrawHat(L);
        }

        /// <summary>Dev only: the head on its own, so wound art can be checked without a duel.</summary>
        public void DrawHeadOnly(Vector2 at, bool hat) => DrawHead(at, new Vector2(0f, -1f), new Vector2(Face, 0f), hat);

        static readonly Color Bone = new Color(228, 219, 195, 255);
        static readonly Color BoneDark = new Color(190, 179, 150, 255);

        /// <summary>
        /// The hole a bullet leaves in a head, drawn over whatever was there. Skin does not end
        /// cleanly, so the rim is a torn ring; inside it is bare skull, cracked away from the
        /// hole, and in the middle the cavity. Put this anywhere on the head and it reads - on
        /// the cheek it takes the face off, on the crown it lifts the top of the skull.
        /// </summary>
        void SkullWound(Vector2 at, float rad)
        {
            if (Art.InkPass || rad < 1f) return;
            int n = _skullJitter.Length;

            // torn skin round the rim: a thin uneven band, or it reads as a flower
            var torn = new Color(126, 18, 22, 255);
            for (int i = 0; i < n * 2; i++)
            {
                float a = i / (float)(n * 2) * MathF.PI * 2f;
                float j = _skullJitter[i % n] * _skullJitter[(i * 5 + 3) % n] + 0.4f;
                Raylib.DrawCircleV(at + Art.FromAngle(a) * rad * (0.88f + j * 0.14f), rad * 0.13f, torn);
            }
            Raylib.DrawCircleV(at, rad * 0.88f, torn);

            // bone under it, ragged where the skin has lifted off
            for (int i = 0; i < n; i++)
            {
                float a = i / (float)n * MathF.PI * 2f;
                Raylib.DrawCircleV(at + Art.FromAngle(a) * rad * 0.74f * _skullJitter[i], rad * 0.14f, Bone);
            }
            Blob(at, rad * 0.80f, Bone, 0);
            Blob(at, rad * 0.66f, BoneDark, 5);

            // cracks running out of the hole - what says skull rather than a red patch. They stay
            // on the bone; a crack drawn out over the torn skin looks like a whisker.
            var crack = new Color(132, 120, 94, 255);
            for (int i = 0; i < 7; i++)
            {
                float a = _skullJitter[i * 7 % n] * MathF.PI * 2f;
                Vector2 d = Art.FromAngle(a);
                Vector2 mid = at + d * rad * 0.52f + Art.FromAngle(a + 1.57f) * rad * 0.1f;
                float w = MathF.Max(1f, rad * 0.05f);
                Raylib.DrawLineEx(at + d * rad * 0.3f, mid, w, crack);
                Raylib.DrawLineEx(mid, at + d * rad * 0.76f, w * 0.8f, crack);
            }

            // The cavity, off to one side of the crater so the whole thing does not read as
            // an eye. A hole in the side of a head is a hole; a hole in the TOP of an emptied
            // one is looking down into the cranium, and what is in a cranium is not a black
            // pit, it is brain - pale, pink, and shining wet where the light catches it.
            Vector2 cav = at + Art.FromAngle(_skullJitter[2] * MathF.PI * 2f) * rad * 0.3f;
            // brain only shows once enough of the vault is gone to see any
            if (HeadOpen >= 0.5f)
            {
                Blob(cav, rad * 0.52f, new Color(104, 30, 34, 255), 11);
                Blob(cav, rad * 0.44f, Gore.BrainDeep, 3);
                Blob(cav, rad * 0.34f, Gore.BrainMid, 9);
                Blob(cav, rad * 0.20f, Gore.BrainPale, 14);
                // the folds of it, which is what stops it reading as a pink coin
                for (int i = 0; i < 4; i++)
                {
                    float a = _skullJitter[(i * 5) % n] * MathF.PI * 2f;
                    Vector2 o = cav + Art.FromAngle(a) * rad * 0.2f;
                    Art.Capsule(o, o + Art.FromAngle(a + 1.9f) * rad * 0.2f, rad * 0.06f, Gore.BrainDeep);
                }
            }
            else
            {
                Blob(cav, rad * 0.38f, new Color(92, 12, 16, 255), 11);
                Blob(cav, rad * 0.23f, new Color(32, 5, 9, 255), 17);
            }
        }

        /// <summary>A circle knocked out of true, so torn flesh and bone never read as geometry.</summary>
        void Blob(Vector2 at, float rad, Color c, int seed)
        {
            int n = _skullJitter.Length;
            var p = new Vector2[n];
            for (int i = 0; i < n; i++)
                p[i] = at + Art.FromAngle(i / (float)n * MathF.PI * 2f)
                          * rad * (0.45f + 0.85f * _skullJitter[(i + seed) % n] * _skullJitter[(i * 3 + seed) % n]);
            Art.Poly(p, c);
        }

        void DrawEye()
        {
            if (!EyeOut) return;
            Vector2 socket = EyeSocket();
            Art.Capsule(socket, _eyePos, 2.6f * Scale, new Color(128, 18, 22, 255));
            Raylib.DrawCircleV(_eyePos, 8.5f * Scale, new Color(28, 10, 12, 255));
            Raylib.DrawCircleV(_eyePos, 7f * Scale, new Color(238, 232, 220, 255));
            Vector2 look = Vector2.Normalize(socket - _eyePos + new Vector2(0.001f, 0f));
            Raylib.DrawCircleV(_eyePos - look * 2.5f * Scale, 3.2f * Scale, new Color(34, 26, 22, 255));
            Raylib.DrawCircleV(socket, 3.5f * Scale, new Color(92, 11, 16, 220));
        }

        /// <summary>
        /// The hair, in head space: +x towards the face, +y down, the skull a circle of 34
        /// about the origin, the brow around -16 and the ear at (-12, -2).
        ///
        /// Every cut is ONE piece: the band between an outer edge that follows the skull - out
        /// past it where the hair has bulk, and on down the neck for a long one - and an inner
        /// edge that is simply where his hairline is. Nothing is assembled out of parts, so
        /// there are no parts to come apart.
        ///
        /// Note that hat style 3 is bareheaded: it draws no hat at all, so the hair must not be
        /// cut off at a brim that is not there. Getting that wrong shaved every man in the game
        /// who was not wearing a hat, and the plate that was supposed to catch it was drawing
        /// its bareheaded row a different way from the game, so it showed nothing wrong.
        /// </summary>
        void DrawHair(Func<float, float, Vector2> L, bool withHat)
        {
            const int N = 26;
            Color hair = P.Hair;
            Color lit = Art.Shade(hair, 1.2f);
            bool hatted = withHat && Def.HatStyle != 3;

            // A run along the skull, the radius easing from r0 through r1 to r2 so a cut can
            // carry some bulk above the bone instead of being painted straight onto it.
            Vector2[] Arc(float fromDeg, float toDeg, float r0, float r1, float r2)
            {
                var p = new Vector2[N];
                for (int i = 0; i < N; i++)
                {
                    float t = i / (float)(N - 1);
                    float a = (fromDeg + (toDeg - fromDeg) * t) * MathF.PI / 180f;
                    float r = t < 0.5f ? Art.Lerp(r0, r1, t * 2f) : Art.Lerp(r1, r2, (t - 0.5f) * 2f);
                    p[i] = L(MathF.Cos(a) * r, -MathF.Sin(a) * r);
                }
                return p;
            }

            // The same run, but with the radius rolling in and out as it goes, so the edge
            // comes out lumpy the way grown-out hair sits rather than turned on a lathe.
            Vector2[] Mop(float fromDeg, float toDeg, float r0, float r1, float r2, float lobe, float waves)
            {
                var p = Arc(fromDeg, toDeg, r0, r1, r2);
                for (int i = 0; i < N; i++)
                {
                    float t = i / (float)(N - 1);
                    float a = (fromDeg + (toDeg - fromDeg) * t) * MathF.PI / 180f;
                    float push = lobe * MathF.Sin(t * waves * MathF.PI * 2f);
                    p[i] += L(MathF.Cos(a) * push, -MathF.Sin(a) * push) - L(0f, 0f);
                }
                return p;
            }

            Vector2[] Edge(params float[] xy)
            {
                var p = new Vector2[xy.Length / 2];
                for (int i = 0; i < p.Length; i++) p[i] = L(xy[i * 2], xy[i * 2 + 1]);
                return Art.Along(p, N);
            }

            Vector2[] outer, inner;

            switch (Def.HairStyle)
            {
                case 1:     // swept: a deep side parting, the whole top carried forward and
                            // down to a point above the eye, and the back of it left long so
                            // it carries on down the nape instead of stopping at the ear.
                    outer = Art.Along(Join(Arc(14f, 196f, 36f, 39f, 35f),
                                           Edge(-35f, 13f, -33f, 23f, -29f, 31f)), N);
                    inner = Edge(32f, -9f, 25f, -18f, 10f, -24f, -6f, -19f, -18f, -8f, -24f, 6f, -23f, 17f, -21f, 27f);
                    break;

                case 2:     // slicked: flat to the bone and combed straight back. The hairline
                            // is high, but it still has to START at the front of the skull -
                            // begun at 32 degrees the band was a sliver over the top and the
                            // front of the crown was bare skin with the comb marks lying on it.
                    outer = Arc(17f, 196f, 34f, 35f, 34f);
                    inner = Edge(28f, -17f, 18f, -25f, 0f, -27f, -13f, -20f, -21f, -8f, -26f, 4f);
                    break;

                case 3:     // long: over the crown, round the back, and down past the jaw
                    outer = Art.Along(Join(Arc(28f, 186f, 35.5f, 37f, 36f),
                                           Edge(-34f, 12f, -32f, 24f, -27f, 34f, -20f, 40f)), N);
                    inner = Edge(28f, -17f, 14f, -22f, -2f, -22f, -9f, -12f, -11f, 2f, -13f, 17f, -17f, 29f, -19f, 38f);
                    break;

                case 4:     // shaggy: a big lumpy mop, grown out well past the bone, hanging
                            // in points at the front and down over the nape at the back
                    outer = Art.Along(Join(Mop(8f, 198f, 40f, 44f, 39f, 2.6f, 3.5f),
                                           Edge(-38f, 12f, -36f, 22f, -31f, 30f)), N);
                    inner = Edge(33f, -13f, 23f, -22f, 4f, -25f, -13f, -17f, -22f, -5f, -25f, 8f, -24f, 18f, -21f, 26f);
                    break;

                case 5:     // balding: the crown gone, a band left from the temple round the back
                    outer = Arc(98f, 204f, 34.5f, 35f, 34f);
                    inner = Edge(-3f, -24f, -14f, -17f, -22f, -6f, -26f, 7f);
                    break;

                default:    // cropped: cut short and close, but still coming down to a front
                            // corner over the brow rather than stopping on top of the skull.
                            // The corner is part of the band, carried round on the skull: laid
                            // over the top as its own tab it poked out past the head.
                    outer = Arc(18f, 198f, 34.5f, 35.5f, 34.5f);
                    inner = Edge(30f, -14f, 22f, -23f, 6f, -26f, -10f, -21f, -20f, -9f, -26f, 5f);
                    break;
            }

            // Under a real brim, only what hangs below the felt is worth drawing.
            if (hatted)
            {
                float brim = Def.HatStyle == 1 ? -25f : -20f;
                Vector2 line = L(0f, brim), down = L(0f, brim + 1f) - line;
                for (int i = 0; i < N; i++)
                {
                    if (Vector2.Dot(outer[i] - line, down) < 0f) outer[i] = line + Project(outer[i], line, down);
                    if (Vector2.Dot(inner[i] - line, down) < 0f) inner[i] = line + Project(inner[i], line, down);
                }
            }

            Art.Band(outer, inner, hair, 3f);

            // A sideburn belongs to the same head of hair, so its top edge has to start well
            // above the hairline and inside the band - started level with the hairline it
            // came away as a slab of hair floating on the cheek with daylight behind it. It
            // also sits in front of the ear rather than on top of it.
            if (Def.HairStyle == 1 || Def.HairStyle == 4)
                Art.Shape(new[] { L(-13f, -20f), L(-4f, -25f), L(-5f, 2f), L(-12f, 4f) }, hair, 2.5f);

            if (Art.InkPass || hatted) return;

            // the light along the top of a sweep, and the comb marks in a slicked head
            if (Def.HairStyle == 1)
                Art.Poly(new[] { L(4f, -31f), L(23f, -26f), L(30f, -14f), L(9f, -22f) }, lit);
            if (Def.HairStyle == 2)
            {
                Art.Seam(L(-15f, -28f), L(13f, -31f), 3f * Scale, lit);
                Art.Seam(L(-17f, -21f), L(21f, -27f), 2.5f * Scale, lit);
            }
            if (Def.HairStyle == 0)
                Art.Seam(L(-18f, -28f), L(14f, -27f), 2.5f * Scale, lit);
        }

        static Vector2[] Join(Vector2[] a, Vector2[] b)
        {
            var r = new Vector2[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        /// <summary>Slide a point along <paramref name="down"/> until it is on the brim line.</summary>
        static Vector2 Project(Vector2 p, Vector2 line, Vector2 down)
        {
            float k = Vector2.Dot(p - line, down) / MathF.Max(0.0001f, Vector2.Dot(down, down));
            return (p - line) - down * k;
        }


        void DrawHat(Func<float, float, Vector2> L)
        {
            switch (Def.HatStyle)
            {
                // The bowler and the preacher used to be pitched five units higher than the
                // slouch hat, which left a strip of bare forehead showing between the brim and
                // the hair: the hat read as hovering over the man rather than being worn by
                // him. Both are seated down onto the skull.
                case 1:
                    Art.Shape(new[] { L(-38f, -25f), L(42f, -25f), L(40f, -35f), L(-36f, -35f) }, P.Hat, 3f);
                    Art.Shape(new[] { L(-26f, -33f), L(-20f, -61f), L(26f, -61f), L(32f, -33f) }, P.Hat, 3.5f);
                    Art.Seam(L(-25f, -37f), L(31f, -37f), 6f * Scale, P.HatBand);
                    break;
                case 2:
                    Art.Shape(new[] { L(-52f, -20f), L(56f, -20f), L(54f, -32f), L(-50f, -32f) }, P.Hat, 3.5f);
                    Art.Shape(new[] { L(-28f, -30f), L(-26f, -60f), L(28f, -60f), L(30f, -30f) }, P.Hat, 3.5f);
                    Art.Seam(L(-27f, -36f), L(29f, -36f), 7f * Scale, P.HatBand);
                    break;
                case 3: break;
                default:
                    Art.Shape(new[] { L(-58f, -28f), L(-46f, -38f), L(48f, -40f), L(62f, -30f), L(46f, -22f), L(-44f, -20f) }, P.Hat, 3.5f);
                    Art.Shape(new[] { L(-30f, -36f), L(-22f, -70f), L(4f, -78f), L(28f, -68f), L(34f, -36f) }, P.Hat, 3.5f);
                    Art.Seam(L(-29f, -42f), L(33f, -42f), 8f * Scale, P.HatBand);
                    break;
            }
        }

        void DrawWounds()
        {
            foreach (var w in Wounds)
            {
                Vector2 p = WoundPos(w);
                float s = w.Size * Scale;

                // just the hole, plus however far the blood has run down from it
                float maxRun = MathF.Max(0f, Scene.GroundY - p.Y - 2f);
                float run = MathF.Min(MathF.Min(w.Age * 22f, 20f + w.Bleed * 1.6f) * Scale, maxRun);
                if (run > 3f)
                    Art.Capsule(p, p + new Vector2(0f, run), s * 0.52f, new Color(86, 10, 15, 190));

                Raylib.DrawCircleV(p, s * 1.15f, new Color(96, 12, 17, 225));
                Raylib.DrawCircleV(p, s * 0.66f, new Color(26, 4, 8, 255));
            }
        }

        /// <summary>
        /// The revolver: a medium-frame four-inch gun, traced off a photograph of one. Every
        /// number below is in that photograph's own pixels, converted on the way out by
        /// <c>T</c> - the origin is the web of the hand at (218, 192) and <c>Gk</c> game units
        /// to the pixel - so any part of it can be measured against the picture directly.
        /// Anchoring at the web is what puts the grip inside the fist and runs the barrel out
        /// along the forearm, on the line the bullet will take.
        /// </summary>
        public void DrawRevolver(Vector2 at, float angle, Palette pal, float scale,
                                 bool withHand = true, float hammer = 0f, float cyl = 0f)
            => Revolver(at, angle, pal, Scale * scale, Face, withHand, hammer, cyl);

        /// <summary>
        /// The same gun with no fighter behind it, for the one that gets knocked out of a hand
        /// and lands in the street. <paramref name="s"/> is the finished pixel scale.
        /// </summary>
        public static void Revolver(Vector2 at, float angle, Palette pal, float s, int face,
                                    bool withHand = false, float hammer = 0f, float cyl = 0f)
        {
            const float Gx = 218f, Gy = 192f, Gk = 0.15f;
            Vector2 T(float px, float py)
                => at + Art.Rot(new Vector2((px - Gx) * Gk, (py - Gy) * Gk * face) * s, angle);

            Color metal = pal.Metal;
            Color dark = Art.Shade(pal.Metal, 0.74f);
            Color deep = Art.Shade(pal.Metal, 0.52f);

            // A part that lies wholly inside another one cannot get its outline from the ink
            // pass, because the fill behind it is painted afterwards and covers it. These get
            // their edge drawn in the colour pass instead.
            void Inner(Vector2[] pts, Color fill, Color line, float w)
            {
                if (Art.InkPass) return;
                Art.Poly(pts, fill);
                Art.PolyLine(pts, w, line);
            }

            // ---------- hammer ----------
            // thumbing it back swings the spur to the rear and down about its pin; the shot
            // drops it again, which is the whole of what you see the action do
            float ha = -0.62f * hammer, hc = MathF.Cos(ha), hsn = MathF.Sin(ha);
            Vector2 H(float px, float py)
            {
                float x = px - 302f, y = py - 152f;
                return T(302f + x * hc - y * hsn, 152f + x * hsn + y * hc);
            }
            Art.Shape(new[]
            {
                H(245f, 125f), H(251f, 116f), H(266f, 116f), H(282f, 126f), H(298f, 145f),
                H(311f, 165f), H(313f, 187f), H(291f, 187f), H(281f, 164f), H(267f, 144f),
                H(253f, 132f), H(246f, 130f),
            }, dark, 2.5f);
            Art.Seam(H(251f, 123f), H(270f, 131f), 1.4f * s, deep);

            Grip(at, angle, pal, s, face);

            // ---------- frame ----------
            Art.Shape(new[]
            {
                T(305f, 100f), T(489f, 99f), T(493f, 120f), T(491f, 196f), T(483f, 248f),
                T(460f, 255f), T(424f, 251f), T(413f, 244f), T(380f, 243f), T(356f, 247f),
                T(337f, 251f), T(321f, 236f), T(305f, 206f), T(296f, 160f), T(297f, 116f),
            }, metal, 2.5f);

            // the rear sight, a shallow notch standing on the back of the topstrap
            Art.Shape(new[]
            {
                T(302f, 83f), T(317f, 83f), T(317f, 96f), T(356f, 96f), T(356f, 106f), T(302f, 106f),
            }, dark, 2f);

            // ---------- cylinder ----------
            Inner(new[]
            {
                T(352f, 112f), T(450f, 112f), T(452f, 124f), T(452f, 205f), T(450f, 217f),
                T(352f, 217f), T(350f, 205f), T(350f, 124f),
            }, dark, deep, 1.6f * s * Gk * 6f);

            // Six flutes on a drum that rolls, but a groove is a line, not a panel: drawn as
            // outlined boxes they put two lines on the cylinder for every one groove and made a
            // grille of it. Only the ones squarely on our side are on view - the rest are round
            // the curve where they would be too foreshortened to read as anything - so one or
            // two show at a time and the count changes as it turns.
            for (int i = 0; i < 6; i++)
            {
                float a = cyl + i * (MathF.PI * 2f / 6f);
                float c = MathF.Cos(a);
                if (c < 0.6f) continue;
                float yc = 164.5f + 40f * MathF.Sin(a);
                Art.Seam(T(362f, yc), T(441f, yc), 2.4f * s * c, deep);
            }

            if (!Art.InkPass) Raylib.DrawCircleV(T(409f, 229f), 7f * Gk * s, deep);

            // ---------- barrel ----------
            Art.Shape(new[]
            {
                T(487f, 104f), T(696f, 104f), T(700f, 113f), T(700f, 152f), T(698f, 161f), T(487f, 161f),
            }, metal, 2.5f);
            // the ejector shroud slung underneath, which is what makes it look as heavy as it is
            Art.Shape(new[]
            {
                T(487f, 157f), T(698f, 157f), T(695f, 176f), T(688f, 186f), T(487f, 186f),
            }, dark, 2.5f);
            Art.Seam(T(500f, 112f), T(686f, 112f), 2f * s, Art.Shade(pal.Metal, 1.14f));

            // the front sight: a ramp standing off the muzzle end
            Art.Shape(new[] { T(612f, 106f), T(659f, 87f), T(678f, 81f), T(690f, 87f), T(697f, 105f) },
                metal, 2f);


            // ---------- trigger, and the bow round it ----------
            Art.CapsuleInked(T(352f, 246f), T(361f, 272f), 7f * Gk * s, deep, 2f);
            Art.CapsuleInked(T(361f, 272f), T(377f, 294f), 7f * Gk * s, deep, 2f);

            Art.CapsuleInked(T(332f, 243f), T(324f, 274f), 9f * Gk * s, dark, 2f);
            Art.CapsuleInked(T(324f, 274f), T(339f, 305f), 9f * Gk * s, dark, 2f);
            Art.CapsuleInked(T(339f, 305f), T(373f, 316f), 9f * Gk * s, dark, 2f);
            Art.CapsuleInked(T(373f, 316f), T(409f, 307f), 9f * Gk * s, dark, 2f);
            Art.CapsuleInked(T(409f, 307f), T(431f, 280f), 9f * Gk * s, dark, 2f);
            Art.CapsuleInked(T(431f, 280f), T(440f, 247f), 9f * Gk * s, dark, 2f);

            if (!withHand) return;

            // the wrist sits down inside the fist, below and behind the gun's own origin
            Fist(T(151f, 279f), angle, pal.Hand, s, face, pal.Gloved, true, true);
        }

        /// <summary>
        /// The one hand this game has. It is anchored at the wrist and runs out along
        /// <paramref name="angle"/>, so the same shape serves the fist round the revolver, the
        /// empty gun hand and the hand on the far arm - nobody has a plain circle for a hand.
        /// One chunky block with a heavy outline and a step where the index knuckle stands
        /// proud. <paramref name="gunHand"/> is his shooting hand, full or empty: it carries the
        /// knuckle creases either way. The far hand does not, because hanging at the hip they
        /// read as two scratches rather than fingers. <paramref name="onTrigger"/> adds the
        /// index finger, which only exists when there is a trigger under it.
        /// </summary>
        public void DrawFist(Vector2 wrist, float angle, Color hand, float scale, bool gloved,
                             bool gunHand, bool onTrigger = false)
            => Fist(wrist, angle, hand, Scale * scale, Face, gloved, gunHand, onTrigger);

        public static void Fist(Vector2 wrist, float angle, Color hand, float s, int face,
                                bool gloved, bool gunHand, bool onTrigger = false)
        {
            Vector2 T(float x, float y) => wrist + Art.Rot(new Vector2(x, y * face) * s, angle);

            var pts = new[]
            {
                T(-2f, -12f), T(12f, -11f), T(18f, -7f), T(11f, -1f),
                T(17f, 5f), T(10f, 11f), T(6f, 17f), T(-5f, 14f), T(-9f, 0f),
            };
            Vector2 fa = T(16f, -6f), fb = T(29f, -4f);
            float fr = 2.8f * s;

            if (Art.InkPass)
            {
                Art.PolyLine(pts, 10f, Art.Ink);
                Art.Poly(pts, Art.Ink);
                if (onTrigger) Art.Capsule(fa, fb, fr + 2.5f, Art.Ink);
                if (gloved) Art.Capsule(T(-5f, -3f), T(-9f, 7f), 7f * s + 2.5f, Art.Ink);
                return;
            }

            // The hand needs an edge of its own in the colour pass: where it lies on the gun,
            // the grip and the bow are filled in after the ink pass and paint over it.
            // The finger's edge goes down in the same breath and both fills follow, so the two
            // of them come out under one unbroken outline with no line drawn between them.
            Art.PolyLine(pts, 3f, Art.Ink);
            if (onTrigger) Art.Capsule(fa, fb, fr + 1.6f, Art.Ink);
            Art.Poly(pts, hand);
            if (onTrigger) Art.Capsule(fa, fb, fr, Art.Shade(hand, 1.06f));

            if (gunHand)
            {
                var crease = Art.Shade(hand, 0.64f);
                Raylib.DrawLineEx(T(1f, -6f), T(8f, -7f), 2.2f * s, crease);
                Raylib.DrawLineEx(T(-1f, 4f), T(6f, 3f), 2.2f * s, crease);
            }

            // a glove shows where it ends: a cuff across the wrist
            if (gloved)
                Art.CapsuleInked(T(-5f, -3f), T(-9f, 7f), 7f * s, Art.Shade(hand, 0.84f), 2.5f);
        }

        /// <summary>
        /// The grip on its own, in the same frame of reference as the rest of the gun. It runs
        /// right up under the hammer - the backstrap is part of it here - which is what closes
        /// the gap between it and the frame. It is a piece of its own because the butt of a
        /// holstered gun has to be painted again over the forearm that hangs across it.
        /// </summary>
        public void DrawGrip(Vector2 at, float angle, Palette pal, float scale)
            => Grip(at, angle, pal, Scale * scale, Face);

        public static void Grip(Vector2 at, float angle, Palette pal, float s, int face)
        {
            const float Gx = 218f, Gy = 192f, Gk = 0.15f;
            Vector2 T(float px, float py)
                => at + Art.Rot(new Vector2((px - Gx) * Gk, (py - Gy) * Gk * face) * s, angle);

            Art.Shape(new[]
            {
                T(270f, 142f), T(288f, 158f), T(300f, 182f), T(305f, 210f), T(301f, 238f),
                T(290f, 264f), T(279f, 294f), T(269f, 322f), T(258f, 350f), T(246f, 376f),
                T(232f, 400f), T(208f, 415f), T(186f, 421f), T(160f, 419f), T(142f, 412f),
                T(134f, 398f), T(128f, 364f), T(132f, 332f), T(141f, 300f), T(152f, 274f),
                T(169f, 248f), T(182f, 218f), T(190f, 190f), T(206f, 172f), T(230f, 160f),
                T(252f, 150f),
            }, pal.Wood, 3f);
            Art.Seam(T(146f, 320f), T(170f, 252f), 2f * s, Art.Shade(pal.Wood, 0.78f));
            if (!Art.InkPass) Raylib.DrawCircleV(T(176f, 382f), 15f * Gk * s, Art.Shade(pal.Wood, 1.25f));
        }

        /// <summary>The way a free hand points: straight on out of the forearm.</summary>
        static float HandAngle(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            if (d.LengthSquared() < 0.0001f) return MathF.PI * 0.5f;
            return MathF.Atan2(d.Y, d.X);
        }
    }
}
