using System;
using System.Numerics;

namespace LLD
{
    /// <summary>
    /// Two-bone arm driven by a spring-damper hand. The hand chases a target point with real
    /// mass, so a fast flick overshoots and settles — and the shot leaves along the forearm,
    /// not along the cursor, which is what makes the overshoot cost you.
    /// </summary>
    public class ArmRig
    {
        public Vector2 Shoulder;
        public Vector2 Hand;
        public Vector2 Vel;
        public Vector2 Elbow;

        public float UpperLen = 82f;
        public float ForeLen = 92f;
        public int Face = 1;

        public float Stiff = 210f;
        public float Damp = 17f;

        public bool Holstered = true;
        public Vector2 HolsterPoint;

        public float GunAngle;          // radians, world
        public float Recoil;            // 0..1 visual kick on the wrist
        public bool HasGun = true;

        // The action. The hammer stands back ready while the gun is out, drops when it fires,
        // and comes back as the cylinder carries the next round round under it - which is the
        // order it happens in on a real one, and the reason you can count his shots by watching.
        public float Hammer;            // 0 down on the frame, 1 fully back
        public float Cylinder;          // radians the cylinder has turned, cumulative
        public const float Chamber = MathF.PI * 2f / 6f;
        float _cylFrom, _cylTo, _cylT = 1f;
        float _fell;                    // how long the hammer still has to lie down

        /// <summary>
        /// What the barrel is lined up on. The wrist turns to keep the gun on this point, so the
        /// shot goes where you are pointing rather than wherever the forearm happens to lie.
        /// </summary>
        public Vector2 AimAt;

        public float Reach => UpperLen + ForeLen - 6f;

        public void Snap(Vector2 p) { Hand = p; Vel = Vector2.Zero; Solve(); }

        public void Update(float dt, Vector2 target, float weight)
        {
            // weight 0 = light and quick, 1 = heavy and swimmy
            float k = Art.Lerp(300f, 120f, weight);
            float c = Art.Lerp(22f, 12.5f, weight);
            if (Holstered) { k *= 2.4f; c *= 1.5f; }
            Stiff = k; Damp = c;

            Vector2 t = ClampReach(target);
            Vector2 acc = (t - Hand) * Stiff - Vel * Damp;
            Vel += acc * dt;

            // integrate with a cap so a huge dt (alt-tab) cannot explode the arm
            float sp = Vel.Length();
            if (sp > 6000f) Vel *= 6000f / sp;
            Hand += Vel * dt;
            Hand = ClampReach(Hand);

            Recoil = MathF.Max(0f, Recoil - dt * 6f);
            StepAction(dt);
            Solve();
        }

        /// <summary>
        /// The hammer and the cylinder, on their own clock. The hammer is slammed down by the
        /// shot and then thumbed back; the cylinder indexes one chamber while it comes.
        /// </summary>
        public void StepAction(float dt)
        {
            _fell = MathF.Max(0f, _fell - dt);
            float want = (Holstered || !HasGun || _fell > 0f) ? 0f : 1f;
            // it falls far faster than it is drawn back - that is the whole point of a hammer
            float rate = want > Hammer ? 11f : 70f;
            Hammer += (want - Hammer) * (1f - MathF.Exp(-rate * dt));

            if (_cylT < 1f)
            {
                _cylT = MathF.Min(1f, _cylT + dt / 0.18f);
                float e = _cylT * _cylT * (3f - 2f * _cylT);   // starts and stops, like a pawl
                Cylinder = _cylFrom + (_cylTo - _cylFrom) * e;
            }
        }

        public Vector2 ClampReach(Vector2 p)
        {
            Vector2 d = p - Shoulder;
            float len = d.Length();
            if (len < 0.001f) return Shoulder + new Vector2(0f, 30f);
            float min = 46f, max = Reach;
            if (len > max) return Shoulder + d / len * max;
            if (len < min) return Shoulder + d / len * min;
            return p;
        }

        void Solve()
        {
            Vector2 d = Hand - Shoulder;
            float dist = Math.Clamp(d.Length(), MathF.Abs(UpperLen - ForeLen) + 1f, UpperLen + ForeLen - 1f);
            if (d.LengthSquared() < 0.0001f) d = new Vector2(0f, 1f);
            Vector2 dir = Vector2.Normalize(d);
            float a = (UpperLen * UpperLen - ForeLen * ForeLen + dist * dist) / (2f * dist);
            float h = MathF.Sqrt(MathF.Max(0f, UpperLen * UpperLen - a * a));
            Vector2 perp = new Vector2(-dir.Y, dir.X);
            Elbow = Shoulder + dir * a + perp * h * Face;

            Vector2 outward = Hand - Shoulder;
            Vector2 line = AimAt - Hand;
            // if the aim point is behind the hand or right on top of it, line up from the
            // shoulder instead, so the gun always points away from him
            if (line.LengthSquared() < 3600f || Vector2.Dot(line, outward) <= 0f) line = AimAt - Shoulder;
            if (line.LengthSquared() < 1f) line = outward;
            if (line.LengthSquared() < 1f) line = new Vector2(Face, 0f);
            GunAngle = MathF.Atan2(line.Y, line.X) - Recoil * 0.55f * Face;
        }

        public Vector2 BarrelDir => Art.FromAngle(GunAngle);

        /// <summary>Elbow for an arbitrary hand position, without touching the live rig.</summary>
        public Vector2 ElbowFor(Vector2 hand)
        {
            Vector2 d = hand - Shoulder;
            if (d.LengthSquared() < 0.0001f) d = new Vector2(0f, 1f);
            float dist = Math.Clamp(d.Length(), MathF.Abs(UpperLen - ForeLen) + 1f, UpperLen + ForeLen - 1f);
            Vector2 dir = Vector2.Normalize(d);
            float a = (UpperLen * UpperLen - ForeLen * ForeLen + dist * dist) / (2f * dist);
            float h = MathF.Sqrt(MathF.Max(0f, UpperLen * UpperLen - a * a));
            Vector2 perp = new Vector2(-dir.Y, dir.X);
            return Shoulder + dir * a + perp * h * Face;
        }

        /// <summary>
        /// Where the hand goes for a given aim point. It follows the cursor itself, only clamped
        /// to what the arm can reach, so bringing the cursor in close folds the elbow and pushing
        /// it out straightens the arm.
        /// </summary>
        public Vector2 AimHandFor(Vector2 target, float radius = -1f)
        {
            if (radius > 0f)
            {
                Vector2 d0 = target - Shoulder;
                if (d0.LengthSquared() < 0.0001f) d0 = new Vector2(Face, 0f);
                return Shoulder + Vector2.Normalize(d0) * radius;
            }
            return ClampReach(target);
        }

        /// <summary>Barrel tip in gun-local space, matching the drawn revolver.</summary>
        public const float MuzzleX = 72.3f;
        public const float MuzzleY = -9f;

        /// <summary>Muzzle in world space (the revolver sits in the fist, barrel along the forearm).</summary>
        public Vector2 Muzzle => Hand + Art.Rot(new Vector2(MuzzleX, MuzzleY * Face), GunAngle);

        public void Kick(float power)
        {
            Recoil = 1f;
            Hammer = 0f;
            _fell = 0.06f;
            _cylFrom = Cylinder; _cylTo = Cylinder + Chamber; _cylT = 0f;
            Vector2 back = -BarrelDir;
            Vel += back * (520f * power) + new Vector2(0f, -300f * power);
        }
    }
}
