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

    public class Fighter
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

        public ArmRig Arm = new ArmRig();
        public List<Wound> Wounds = new();
        public Ragdoll Doll = new Ragdoll();

        public int HeadDamage;          // 0 whole, 1 torn open, 2 skull emptied
        public float HeadWoundAngle;    // body-space angle of the missing piece
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
        readonly Vector2[] _footP = new Vector2[2];   // ankle, world
        readonly Vector2[] _footV = new Vector2[2];
        readonly bool[] _airborne = new bool[2];
        float _stepDelay;
        int _failedSteps;
        public float Crouch;         // kept for leg wounds only

        public float AnkleHeight => 38f * Scale;
        public float FootFloor => Scene.GroundY - AnkleHeight;
        public Vector2 FootWorld(int i) => _footP[i];

        public float SupportX => (_footP[0].X + _footP[1].X) * 0.5f;
        public float FootX => SupportX - Origin.X;
        public bool Stepping => _airborne[0] || _airborne[1];
        public float Stance => MathF.Abs(_footP[0].X - _footP[1].X);

        const float FootGravity = 2600f;
        const float StepAir = 0.22f;        // roughly how long a boot is off the ground
        const float LegRestAnkle = 166f;    // pelvis to ankle, stood up straight
        const float LegMaxAnkle = 168f;
        const float MaxStance = 150f;   // how far apart the boots may ever get
        // hips this far off the dirt and his legs have plainly gone; normal is about 200
        const float SagFloor = 110f;
        const float MaxReach = 92f;    // how far a boot may sit from the hips before it must move
        public const float ArmWeight = 0.5f;   // one standard feel for the gun hand

        public Fighter(CharDef def, Vector2 origin, int face)
        {
            Def = def;
            Origin = origin;
            Face = face;
            Scale = def.Height;

            // stand him up before anything asks where his shoulder is
            PelvisP = origin + new Vector2(0f, Com.Y * Scale);
            ChestP = PelvisP + new Vector2(0f, -SpineLen * Scale);
            _footP[0] = new Vector2(origin.X - 13f * face * Scale, Scene.GroundY - 38f * Scale);
            _footP[1] = new Vector2(origin.X + 21f * face * Scale, Scene.GroundY - 38f * Scale);

            Arm.Face = face;
            Arm.UpperLen = 82f * Scale;
            Arm.ForeLen = 92f * Scale;
            Arm.Shoulder = L2W(new Vector2(10f, -318f));
            Arm.HolsterPoint = L2W(new Vector2(48f, -158f));
            Arm.Snap(Arm.HolsterPoint);
            _skullJitter = new float[24];
            for (int i = 0; i < _skullJitter.Length; i++) _skullJitter[i] = Rng.F(0.62f, 1.0f);
        }

        // ---------- the body itself ----------
        // The trunk is two physical points, pelvis and chest, carried by the legs. Everything
        // drawn on him is placed relative to those, so the art cannot come apart from the
        // physics: there is only one body, and it is the simulated one.
        public Vector2 PelvisP, PelvisV;
        public Vector2 ChestP, ChestV;

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
            Foot0 = _footP[0], Foot1 = _footP[1],
            Blood = Blood, Shock = Shock, Kneel = Kneel,
            Down = Collapsed, Dead = Killed, Air0 = _airborne[0], Air1 = _airborne[1],
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
            _footP[0] = Ease(_footP[0], s.Foot0);
            _footP[1] = Ease(_footP[1], s.Foot1);
            PelvisV = Vector2.Lerp(PelvisV, s.PelvisV, blend);
            ChestV = Vector2.Lerp(ChestV, s.ChestV, blend);
            Kneel = Art.Lerp(Kneel, s.Kneel, blend);
            _airborne[0] = s.Air0;
            _airborne[1] = s.Air1;
            Blood = s.Blood;
            Shock = s.Shock;
            Collapsed = s.Down;
            Killed = s.Dead;
        }
        float _sagging;             // how long his hips have been down near the dirt
        public float Muscle = 1f;          // 1 standing strong, 0 limp
        public const float SpineLen = 112f;
        public const float RestLeg = 200f; // pelvis to the sole when he is stood up straight
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
        public void LegJoints(int i, out Vector2 hip, out Vector2 knee, out Vector2 ankle)
        {
            const float Thigh = 92f, Shin = 90f;
            hip = Pose(i == 0 ? -7f : 15f, -212f);
            ankle = _footP[i];

            // the leg is a fixed length: it cannot reach past itself to a boot
            Vector2 d = ankle - hip;
            float len = d.Length();
            float reach = (Thigh + Shin - 2f) * Scale;
            if (len > reach && len > 0.001f) ankle = hip + d / len * reach;

            knee = SolveKnee(hip, ankle, Thigh * Scale, Shin * Scale, Face);
            if (Kneel > 0.01f) knee += new Vector2(Face * 18f * Kneel * Scale, 10f * Kneel * Scale);
        }

        public List<HitVolume> Volumes()
        {
            var list = new List<HitVolume>(10);
            if (Down && Doll.Active)
            {
                list.Add(new HitVolume { Zone = Zone.Head, A = Doll.Pos[Ragdoll.Head], B = Doll.Pos[Ragdoll.Neck], R = 30f * Scale });
                list.Add(new HitVolume { Zone = Zone.Torso, A = Doll.Pos[Ragdoll.Chest], B = Doll.Pos[Ragdoll.Pelvis], R = 34f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = Doll.Pos[Ragdoll.Pelvis], B = Doll.Pos[Ragdoll.KneeF], R = 19f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = Doll.Pos[Ragdoll.KneeF], B = Doll.Pos[Ragdoll.FootF], R = 17f * Scale });
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
                list.Add(new HitVolume { Zone = Zone.Leg, A = hip, B = knee, R = 20f * Scale });
                list.Add(new HitVolume { Zone = Zone.Leg, A = knee, B = ankle, R = 16f * Scale });
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

            var strikes = Anatomy.Trace(lf, ld, len / Scale);
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
                if (rep.HeadSeverity > HeadDamage)
                {
                    HeadDamage = rep.HeadSeverity;
                    HeadWoundAngle = MathF.Atan2(-(lExit.Y - -400f), lExit.X - 6f);
                }
                KnockHat(worldDir);
                Gore.Chunks(worldExit, worldDir, P.Skin, (int)(5 + 7 * (rep.HeadSeverity - 1)));
                // the piece of skull that came away with it
                Gore.SkullPieces(worldExit, worldDir, P.Skin, rep.HeadSeverity >= 2 ? 3 : 1);
                if (rep.JawGone) JawGone = true;
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
            Doll.Build(this);
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
                return;
            }

            Flinch = MathF.Max(0f, Flinch - dt * 3.2f);
            Kneel = Math.Clamp(Kneel + (Shock > 40f ? dt * 0.8f : -dt * 0.25f), 0f, 1f);

            Balance(dt);
            Simulate(dt);

            Arm.Shoulder = L2W(new Vector2(10f, -318f));
            Arm.HolsterPoint = L2W(new Vector2(48f, -158f));
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
        }

        /// <summary>
        /// Keeps him upright the way a person does: legs push the trunk back over the boots, and
        /// when the trunk gets too far ahead of them the boots have to move. Enough momentum and
        /// no number of steps saves him.
        /// </summary>
        /// <summary>
        /// Decides where the boots go. It reads the real pelvis rather than an abstract point, so
        /// the stepping answers to the physics instead of running alongside it.
        /// </summary>
        void Balance(float dt)
        {
            const float G = 2000f;
            float h = RestLeg * Scale;
            float omega = MathF.Sqrt(G / h);

            // His legs are not holding him up any more. A man sat on his own hips is not
            // standing, whatever the rest of this thinks, and the stepping below will go on
            // throwing a boot out and planting it for as long as you let it - which is what
            // the twitching leg on a downed man was. Once he is this low he goes down properly.
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

            float footWorld = SupportX;
            float lever = PelvisP.X - footWorld;
            ComX = PelvisP.X - Origin.X;        // the feet are kept in his own coordinates
            ComVx = PelvisV.X;

            float hurt = Math.Clamp(Impairment * 0.6f + Kneel * 0.3f, 0f, 1f);

            if (!Stepping && _stepDelay <= 0f)
            {
                // ankle and hip hauling the weight back over the boots, but only so hard
                float maxAnkle = Art.Lerp(2400f, 800f, hurt) * Muscle;
                float corrective = -(lever * 46f + PelvisV.X * 9f);
                PelvisV.X += Math.Clamp(corrective, -maxAnkle, maxAnkle) * dt;
            }

            // the lean he is trying to hold; the trunk muscle chases it
            LeanAng = Math.Clamp(MathF.Atan2(lever, h) * 0.95f, -0.55f, 0.55f);

            _stepDelay = MathF.Max(0f, _stepDelay - dt);

            if (!Stepping && _stepDelay <= 0f)
            {
                // Capture point: the spot he would have to plant a boot on to stop dead. A wider
                // stance covers more ground, so a man who has already staggered is harder to tip.
                float capture = PelvisP.X + PelvisV.X / omega;
                // the stance helps, but only so far: let this grow without limit and he simply
                // stops stepping, leaving a boot stranded behind him
                float support = 20f * Scale + MathF.Min(Stance, 90f) * 0.45f;

                if (MathF.Abs(capture - footWorld) > support)
                {
                    float maxStep = Art.Lerp(200f, 120f, hurt) * Scale;
                    float d = MathF.Sign(capture - footWorld);
                    bool desperate = MathF.Abs(capture - footWorld) > maxStep;

                    // He does not simply fall over the moment he is out of balance. He throws a
                    // leg out and tries, and only goes down once two of those have failed him.
                    if (Crippled || (desperate && _failedSteps >= 2))
                    {
                        Collapse(new Vector2(MathF.Sign(PelvisV.X == 0f ? 1f : PelvisV.X), -0.15f),
                                 Math.Clamp(MathF.Abs(PelvisV.X) / 300f, 0.7f, 1.6f));
                        return;
                    }
                    if (desperate) _failedSteps++;

                    // the trailing boot swings: whichever one is already furthest the way he is going
                    int leg = (d * _footP[0].X >= d * _footP[1].X) ? 0 : 1;
                    float from = _footP[leg].X;
                    // a stumbling man throws short of where he needs to, so one step is rarely enough
                    float reach = (capture - from) * Rng.F(0.6f, 0.92f) + d * 10f;
                    float to = from + Math.Clamp(reach, -maxStep, maxStep);

                    // boots do not pass through each other, and a man cannot do the splits
                    float other = _footP[1 - leg].X;
                    if (d > 0f) to = MathF.Max(to, other + 6f);
                    else to = MathF.Min(to, other - 6f);
                    to = Math.Clamp(to, other - MaxStance * Scale, other + MaxStance * Scale);

                    ThrowFoot(leg, to);
                    _stepDelay = 0.05f;
                }
            }

            // A boot can never be left stranded: once it is further from the hips than the leg
            // can reach, that leg has to come back under him whatever the balance says.
            if (!Stepping && _stepDelay <= 0f)
            {
                for (int i = 0; i < 2; i++)
                {
                    float out_ = _footP[i].X - PelvisP.X;
                    if (MathF.Abs(out_) <= MaxReach * Scale) continue;
                    ThrowFoot(i, PelvisP.X + MathF.Sign(out_) * MaxReach * 0.55f * Scale);
                    _stepDelay = 0.05f;
                    break;
                }
            }

            // settled: he gathers his feet back under him and walks back to his mark
            if (!Stepping && MathF.Abs(PelvisV.X) < 40f && MathF.Abs(lever) < 12f)
            {
                _failedSteps = 0;
                for (int i = 0; i < 2; i++)
                {
                    float want = Origin.X + (i == 0 ? -13f : 21f) * Face * Scale;
                    float gap = want - _footP[i].X;
                    _footP[i].X += MathF.Min(38f * dt, MathF.Abs(gap)) * MathF.Sign(gap);
                }
                float drift = MathF.Min(38f * dt, MathF.Abs(ComX)) * -MathF.Sign(ComX);
                PelvisP.X += drift;
                ChestP.X += drift;
            }

            // No formula for how low he stands. A wide stance puts the legs at an angle and the
            // hips come down on their own; the only thing worth adding is a wounded knee giving.
            Crouch += (Kneel * 40f - Crouch) * (1f - MathF.Exp(-7f * dt));
        }

        /// <summary>
        /// Throws a boot at a spot on the street. It leaves the ground with a real velocity and
        /// lands wherever gravity puts it, so no two strides are the same shape.
        /// </summary>
        void ThrowFoot(int leg, float targetX)
        {
            _airborne[leg] = true;
            float t = StepAir * Rng.F(0.85f, 1.15f);
            float dx = targetX - _footP[leg].X;
            _footV[leg] = new Vector2(dx / t, -0.5f * FootGravity * t);
            Steps++;
        }

        /// <summary>Boots under their own gravity: in the air, or resting on the dirt.</summary>
        void StepFeet(float dt)
        {
            float floor = FootFloor;
            float maxLen = LegMaxAnkle * Scale;

            for (int i = 0; i < 2; i++)
            {
                if (_airborne[i])
                {
                    _footV[i].Y += FootGravity * dt;
                    _footP[i] += _footV[i] * dt;
                    if (_footP[i].Y >= floor && _footV[i].Y > 0f)
                    {
                        _footP[i].Y = floor;
                        _footV[i] = Vector2.Zero;
                        _airborne[i] = false;
                        Sfx.Play("thud", 0.3f, Rng.F(1.2f, 1.45f));
                        Gore.DustPuff(new Vector2(_footP[i].X, Scene.GroundY), new Vector2(0f, -1f), 0.34f);
                    }
                }
                else
                {
                    _footP[i].Y = floor;
                    _footV[i] = Vector2.Zero;
                }

                // a leg is only so long: an overstretched boot gets dragged through the dirt
                Vector2 d = PelvisP - _footP[i];
                float len = d.Length();
                if (!_airborne[i] && len > maxLen)
                {
                    _footP[i] += d / len * (len - maxLen);
                    _footP[i].Y = floor;
                }
            }
        }

        /// <summary>
        /// The trunk under gravity, carried by two legs that push along their own length. That
        /// push is what holds him up, and it is also what tips him over when his weight drifts
        /// off his feet: there is no separate rule for standing and for falling.
        /// </summary>
        void Simulate(float dt)
        {
            const float G = 2000f;
            PelvisV.Y += G * dt;
            ChestV.Y += G * dt;

            StepFeet(dt);

            float rest = LegRestAnkle * Scale - Crouch;
            float maxLen = LegMaxAnkle * Scale;

            for (int i = 0; i < 2; i++)
            {
                Vector2 foot = _footP[i];
                Vector2 d = PelvisP - foot;
                float len = d.Length();
                if (len < 1f) continue;
                Vector2 dir = d / len;

                // a boot in the air carries nothing, which is exactly why a stumble is unstable
                if (!_airborne[i])
                {
                    float push = ((rest - len) * 260f - Vector2.Dot(PelvisV, dir) * 16f) * Muscle;
                    if (push > 0f) PelvisV += dir * push * dt;   // legs push, never pull
                }

                if (!_airborne[i] && len > maxLen)
                {
                    PelvisP = foot + dir * maxLen;
                    float into = Vector2.Dot(PelvisV, dir);
                    if (into > 0f) PelvisV -= dir * into;
                }
            }

            // trunk muscle, holding the chest over the hips at the lean he is trying for
            Vector2 want = Art.Rot(new Vector2(0f, -1f), LeanAng);
            Vector2 chestTarget = PelvisP + want * (SpineLen * Scale);
            ChestV += ((chestTarget - ChestP) * (300f * Muscle) - ChestV * 24f) * dt;

            PelvisP += PelvisV * dt;
            ChestP += ChestV * dt;

            // the spine does not stretch, whatever the muscles are doing
            Vector2 s = ChestP - PelvisP;
            float sl = s.Length();
            if (sl > 0.001f)
            {
                Vector2 corr = s / sl * (sl - SpineLen * Scale);
                PelvisP += corr * 0.35f;
                ChestP -= corr * 0.65f;
            }

            float floor = Scene.GroundY - 46f * Scale;
            if (PelvisP.Y > floor) { PelvisP.Y = floor; if (PelvisV.Y > 0f) PelvisV.Y = 0f; }

            PelvisV *= 1f - MathF.Min(1f, 0.6f * dt);
            ChestV *= 1f - MathF.Min(1f, 1.4f * dt);

            // shock takes the strength out of him
            float want2 = 1f - Math.Clamp(Shock / 110f, 0f, 0.85f);
            Muscle += (want2 - Muscle) * (1f - MathF.Exp(-6f * dt));
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

            if (Def.ScarfOn)
            {
                Art.Shape(new[] { Lp(-14f, -340f), Lp(12f, -344f), Lp(16f, -330f), Lp(-10f, -326f) }, P.Scarf, 3f);
                Art.Shape(new[] { Lp(10f, -334f), Lp(20f, -330f), Lp(14f, -312f), Lp(6f, -318f) }, P.Scarf, 3f);
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
            Art.CapsuleInked(hip, knee, 20f * Scale, pants);
            Art.CapsuleInked(knee, ankle, 16f * Scale, pants);

            // the boot belongs to the ground, not to the body, so it is drawn in world space
            float s = Scale;
            Vector2 B(float dx, float dy) => ankle + new Vector2(dx * Face * s, dy * s);
            Art.Shape(new[]
            {
                B(-16f, -6f), B(14f, -6f), B(16f, 24f),
                B(34f, 28f), B(34f, 38f), B(-18f, 38f), B(-17f, 22f)
            }, boot);
            Art.Seam(B(-16f, 6f), B(14f, 6f), 3f * Scale, Art.Shade(boot, 0.78f));

            switch (Def.BootStyle)
            {
                case 1:     // a cuff turned down over the top
                    Art.Shape(new[] { B(-20f, -22f), B(18f, -22f), B(19f, -2f), B(-21f, -2f) },
                        Art.Shade(boot, 1.18f), 2.5f);
                    Art.Seam(B(-19f, -12f), B(17f, -12f), 2.5f * Scale, Art.Shade(boot, 0.9f));
                    break;
                case 2:     // a spur off the heel
                    Art.CapsuleInked(B(-18f, 30f), B(-30f, 30f), 2.5f * s, P.Metal, 2f);
                    Art.CircleInked(B(-34f, 30f), 6.5f * s, P.Metal, 2f);
                    if (!Art.InkPass) Raylib.DrawCircleV(B(-34f, 30f), 2.2f * s, Art.Shade(P.Metal, 0.6f));
                    break;
            }
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

            Art.CapsuleInked(pelvis, d.Pos[Ragdoll.KneeB], 20f * Scale, P.PantsDark);
            Art.CapsuleInked(d.Pos[Ragdoll.KneeB], d.Pos[Ragdoll.FootB], 16f * Scale, P.PantsDark);
            Art.CircleInked(d.Pos[Ragdoll.FootB], 17f * Scale, Art.Shade(P.Boots, 0.82f), 3f);

            Vector2 axis = chest - pelvis;
            if (axis.LengthSquared() < 1f) axis = new Vector2(0f, -1f);
            Vector2 side = Vector2.Normalize(new Vector2(-axis.Y, axis.X)) * 27f * Scale;
            Art.Shape(new[] { pelvis + side, chest + side * 1.05f, neck + side * 0.6f, neck - side * 0.6f, chest - side * 1.05f, pelvis - side }, P.Shirt);
            Art.Shape(new[] { pelvis - side * 0.95f, chest - side, neck - side * 0.55f, neck - side * 0.05f, pelvis - side * 0.1f }, P.Vest, 3f);
            Art.Shape(new[] { pelvis + side * 1.04f + axis * 0.07f, pelvis - side * 1.04f + axis * 0.07f, pelvis - side * 1.04f - axis * 0.07f, pelvis + side * 1.04f - axis * 0.07f },
                new Color(66, 44, 30, 255), 3f);

            Art.CapsuleInked(pelvis, d.Pos[Ragdoll.KneeF], 20f * Scale, P.Pants);
            Art.CapsuleInked(d.Pos[Ragdoll.KneeF], d.Pos[Ragdoll.FootF], 16f * Scale, P.Pants);
            Art.CircleInked(d.Pos[Ragdoll.FootF], 17f * Scale, P.Boots, 3f);

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
            float arc = HeadDamage >= 2 ? 2.1f : 1.25f;
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
                        rr = r * Art.Lerp(0.28f, 1f, bite) * _skullJitter[i];
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
            Art.Shape(new[] { L(-30f, -20f), L(-4f, -32f), L(-6f, -6f), L(-28f, 12f) }, P.Hair, 3f);

            if (!Art.InkPass)
            {
                if (JawGone)
                {
                    for (int i = 0; i < 7; i++)
                        Raylib.DrawCircleV(L(-18f + i * 8f, 18f + (i % 3) * 4f), 5.5f * Scale, new Color(112, 14, 19, 240));
                    Raylib.DrawCircleV(L(4f, 22f), 9f * Scale, new Color(40, 6, 10, 255));
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
                    float wRad = r * (HeadDamage >= 2 ? 0.62f : 0.34f);
                    SkullWound(wAt, wRad);
                    // what runs out of it starts at the underside of the hole, not the middle,
                    // or the drip is drawn straight over the skull
                    Vector2 low = wAt + new Vector2(0f, wRad * 0.85f);
                    float run = MathF.Min((20f + MathF.Min(DeadTime, 3f) * 20f) * Scale, MathF.Max(0f, Scene.GroundY - low.Y - 2f));
                    if (run > 3f)
                    {
                        // tapering, so it reads as something running down him rather than a bar
                        var bl = new Color(96, 12, 16, 220);
                        Raylib.DrawLineEx(low, low + new Vector2(0f, run * 0.55f), 8f * Scale, bl);
                        Raylib.DrawLineEx(low + new Vector2(0f, run * 0.5f), low + new Vector2(0f, run * 0.85f), 5f * Scale, bl);
                        Raylib.DrawCircleV(low + new Vector2(0f, run * 0.9f), 3f * Scale, bl);
                    }
                }
            }

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

            // the cavity, off to one side of the crater so the whole thing does not read as an eye
            Vector2 cav = at + Art.FromAngle(_skullJitter[2] * MathF.PI * 2f) * rad * 0.3f;
            Blob(cav, rad * 0.38f, new Color(92, 12, 16, 255), 11);
            Blob(cav, rad * 0.23f, new Color(32, 5, 9, 255), 17);
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

        void DrawHat(Func<float, float, Vector2> L)
        {
            switch (Def.HatStyle)
            {
                case 1:
                    Art.Shape(new[] { L(-38f, -30f), L(42f, -30f), L(40f, -40f), L(-36f, -40f) }, P.Hat, 3f);
                    Art.Shape(new[] { L(-26f, -38f), L(-20f, -66f), L(26f, -66f), L(32f, -38f) }, P.Hat, 3.5f);
                    Art.Seam(L(-25f, -42f), L(31f, -42f), 6f * Scale, P.HatBand);
                    break;
                case 2:
                    Art.Shape(new[] { L(-52f, -32f), L(56f, -32f), L(54f, -44f), L(-50f, -44f) }, P.Hat, 3.5f);
                    Art.Shape(new[] { L(-28f, -42f), L(-26f, -72f), L(28f, -72f), L(30f, -42f) }, P.Hat, 3.5f);
                    Art.Seam(L(-27f, -48f), L(29f, -48f), 7f * Scale, P.HatBand);
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
