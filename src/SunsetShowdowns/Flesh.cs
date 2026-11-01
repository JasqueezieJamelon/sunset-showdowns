using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>
    /// A piece of him that came loose at one end and did not come off.
    ///
    /// The hole in a head used to be painted on: a torn ring, bone under it, a cavity, all of
    /// it stuck flat to the skull and all of it holding perfectly still while he fell down a
    /// flight of stairs. It read as a decal because it WAS a decal. What was missing is that
    /// skin tears before it parts - a bullet lifts a piece and leaves it hanging by the other
    /// edge, and that piece swings.
    ///
    /// So a flap is a hinge and a loose end. The hinge is a point on the head in head space,
    /// which means it follows him wherever the head goes without anything having to be told
    /// about it. The loose end is one free point under gravity on a short tether - the same
    /// trick the hanging eye has always used, because it works. Everything else (which way
    /// round it is lying, whether you are looking at his cheek or the underside of his cheek,
    /// how far it has peeled) falls out of where that one point ends up.
    /// </summary>
    public class Flap
    {
        /// Where it is still attached, in head space: +x towards the face, +y DOWN.
        public Vector2 At;

        /// Which way it hangs when nothing is pulling on it, as a head-space angle:
        /// 0 is straight out of the face, +pi/2 is out of the crown.
        public float Out;

        public float Len;        // how far the loose end reaches, unscaled
        public float Wide;       // half-width at the hinge, unscaled
        public float Peel;       // creeps up as he bleeds out: skin lets go slowly

        public Vector2 Tip, Prev;
        public bool Started;

        /// Scalp rather than cheek: hair on the outside of it.
        public bool Hair;

        /// What is left of his jaw, swinging off the hinge of it.
        public bool Jaw;

        public float[] Jag = new float[9];

        public Flap()
        {
            for (int i = 0; i < Jag.Length; i++) Jag[i] = Rng.F(0.5f, 1.28f);
        }
    }

    public partial class Fighter
    {
        readonly List<Flap> _flaps = new List<Flap>();
        public int FlapCount => _flaps.Count;
        public bool JawHanging { get { foreach (var f in _flaps) if (f.Jaw) return true; return false; } }

        static readonly Color Meat = new Color(118, 20, 24, 255);
        static readonly Color MeatDark = new Color(76, 11, 16, 255);

        // Dev only: the head plate draws a head on its own, away from the body it belongs to.
        // Without this the flaps would hang off where the man actually is and the plate would
        // show a clean head with its face swinging about somewhere off the side of the picture.
        public bool FrameOverride;
        Vector2 _frameC, _frameUp, _frameFwd;

        public void PoseHeadAt(Vector2 at)
        {
            FrameOverride = true;
            _frameC = at;
            _frameUp = new Vector2(0f, -1f);
            _frameFwd = new Vector2(Face, 0f);
        }
        public void PoseHeadClear() => FrameOverride = false;
        public void ClearFlaps() => _flaps.Clear();

        /// <summary>The head's own frame, standing or fallen. Everything about a flap is in it.</summary>
        public void HeadFrame(out Vector2 c, out Vector2 up, out Vector2 fwd)
        {
            if (FrameOverride) { c = _frameC; up = _frameUp; fwd = _frameFwd; return; }
            Basis(out up, out fwd);
            if (Down && Doll.Active)
            {
                up = Doll.HeadUp;
                fwd = new Vector2(-up.Y, up.X) * Face;
            }
            c = HeadCenter;
        }

        Vector2 HeadPoint(Vector2 c, Vector2 up, Vector2 fwd, Vector2 local)
            => c + fwd * local.X * Scale + up * -local.Y * Scale;

        static Vector2 HeadDir(Vector2 up, Vector2 fwd, float a)
            => fwd * MathF.Cos(a) + up * MathF.Sin(a);

        /// <summary>
        /// Tear a piece loose. <paramref name="angle"/> is where on the skull it is still
        /// attached, in head space, and it hangs outward from there.
        /// </summary>
        public void TearFlap(float angle, float len, float wide, bool hair = false, bool jaw = false)
        {
            if (_flaps.Count >= 4) return;
            HeadFrame(out Vector2 c, out Vector2 up, out Vector2 fwd);
            var f = new Flap
            {
                At = new Vector2(MathF.Cos(angle) * 30f, -MathF.Sin(angle) * 30f),
                Out = angle,
                Len = len,
                Wide = wide,
                Hair = hair,
                Jaw = jaw,
            };
            Vector2 anchor = HeadPoint(c, up, fwd, f.At);
            // it starts lying against the head and is thrown out from there, rather than
            // appearing already hanging: the first half second of a flap is the good part
            f.Tip = anchor + HeadDir(up, fwd, angle) * f.Len * Scale * 0.6f;
            f.Prev = f.Tip - HeadDir(up, fwd, angle) * 5f * Scale;
            f.Started = true;
            _flaps.Add(f);
        }

        /// <summary>
        /// Everything a head shot takes off him, decided once so the same shot comes out the
        /// same on both machines in a match.
        /// </summary>
        public void TearFace(float woundAngle, int severity, Vector2 worldDir, Vector2 worldAt)
        {
            // the cheek, or whatever the bullet actually crossed, lifted and left hanging
            float a = woundAngle + Rng.F(-0.35f, 0.35f);
            TearFlap(a, Rng.F(15f, 22f), Rng.F(8f, 12f));

            if (severity >= 2)
            {
                // the crown comes up with the hair still on it
                TearFlap(woundAngle + Rng.F(1.0f, 1.9f) * (Rng.Chance(0.5f) ? 1f : -1f),
                         Rng.F(14f, 20f), Rng.F(9f, 13f), hair: true);
                // and a piece of him goes properly: a flat sheet of face, not a pellet
                Gore.FaceSkin(worldAt, worldDir, P.Skin, 2);
            }
            else if (Rng.Chance(0.5f))
            {
                Gore.FaceSkin(worldAt, worldDir, P.Skin, 1);
            }
        }

        /// <summary>A jaw that is off but not away: hinged at the back and swinging.</summary>
        public void HangJaw()
        {
            foreach (var f0 in _flaps) if (f0.Jaw) return;
            TearFlap(-2.2f, 19f, 11f, jaw: true);
        }

        public Vector2 FlapTip(int i) => _flaps[i].Tip;

        /// <summary>
        /// A fresh shape for the hole. Seeded off where the bullet came out and how far the
        /// vault opened - both of which the other machine already has - so the wound looks
        /// different for every shot and the same on both screens.
        /// </summary>
        public void ReshapeSkull(Vector2 localExit, float open)
        {
            int seed = (int)(localExit.X * 8f) * 73856093
                     ^ (int)(localExit.Y * 8f) * 19349663
                     ^ (int)(open * 1024f) * 83492791;
            var r = new Random(seed);
            for (int i = 0; i < _skullJitter.Length; i++)
                _skullJitter[i] = 0.62f + (float)r.NextDouble() * 0.38f;
        }

        // ---------- the sandbox ----------
        //
        // Nothing in the game calls either of these. They exist so a man can be put back on
        // his feet and taken apart again without restarting anything, which is the whole
        // point of having somewhere to test.

        /// <summary>Back on their feet with whatever damage they are carrying.</summary>
        public void StandUp()
        {
            Killed = false;
            Collapsed = false;
            DeadTime = 0f;
            Shock = 0f;
            StunLeft = 0f;
            Flinch = 0f;
            Kneel = 0f;
            Crippled = false;
            Blood = MathF.Max(Blood, Anatomy.BloodFull * 0.5f);
            BleedRate = MathF.Min(BleedRate, 2f);
            Muscle = 1f;
            _legSpent = 0f;

            // THE BODY IS THE RAGDOLL. Clearing the flags and leaving the ragdoll lying in
            // the dirt does not stand anybody up - it leaves a corpse on the floor with the
            // strength switched back on, twitching, which is why both of these buttons
            // looked like they did nothing at all. The body has to be stood up too, where it
            // is lying, and then settled onto its feet.
            Vector2 where = new Vector2(Doll.Pos[Ragdoll.Pelvis].X, Scene.GroundY);
            Doll.Stand(where, Face, Scale);
            Settle();

            Doll.Muscle = 1f;
            Doll.LegPower = 1f;
            Doll.StepLeg = -1;
            Doll.Asleep = false;

            Arm.Shoulder = L2W(new Vector2(10f, -318f));
            Arm.HolsterPoint = L2W(new Vector2(48f, -172f));
            Arm.Snap(Arm.HolsterPoint);
            Arm.Holstered = true;
        }

        /// <summary>Whole again: no holes, no scars, no pieces hanging off, gun back in hand.</summary>
        public void Patch()
        {
            Blood = Anatomy.BloodFull;
            BleedRate = 0f;
            Wounds.Clear();
            HeadDamage = 0;
            HeadWoundAngle = 0f;
            JawGone = false;
            EyeOut = false;
            ClearFlaps();
            Disarmed = false;
            Arm.HasGun = true;
            HatOn = true;
            StandUp();
        }

        /// <summary>
        /// Dev only. Two things must always be true of a piece hanging off a man: it is still
        /// attached, and it is not hanging through his own skull. Both are easy to break by
        /// tuning, and both look ridiculous rather than subtly wrong when they are.
        /// </summary>
        public void FlapCheck(out float worstStretch, out float deepestInside, out int pinched)
        {
            worstStretch = 0f;
            deepestInside = 0f;
            pinched = 0;
            HeadFrame(out Vector2 c, out Vector2 up, out Vector2 fwd);
            float skull = 26f * Scale;
            foreach (var f in _flaps)
            {
                // A head lying ON the street has its own skull through the ground, and then
                // there is nowhere a piece hanging off it can be that is both outside the
                // bone and above the dirt. That is geometry, not a bug, so it is counted
                // rather than failed - what must not happen is the tether letting go while
                // the man is stood upright with room underneath him.
                if (Scene.GroundY - c.Y < skull + 6f) { pinched++; continue; }
                Vector2 anchor = HeadPoint(c, up, fwd, f.At);
                float reach = f.Len * (1f + f.Peel * 0.45f) * Scale;
                worstStretch = MathF.Max(worstStretch, Vector2.Distance(f.Tip, anchor) - reach);
                deepestInside = MathF.Max(deepestInside, skull - Vector2.Distance(f.Tip, c));
            }
        }

        public void UpdateFlaps(float dt)
        {
            if (_flaps.Count == 0) return;
            HeadFrame(out Vector2 c, out Vector2 up, out Vector2 fwd);

            // Skin lets go in the end. A piece hanging off a dead man peels until it is only
            // holding by nothing, and then it is not holding at all - it drops, with whatever
            // swing it had at the time, and lies in the street with the rest of him. Left
            // attached forever they read as flags nailed to his head.
            for (int i = _flaps.Count - 1; i >= 0; i--)
            {
                var fl = _flaps[i];
                if (Dead && fl.Peel >= 1f)
                {
                    Vector2 fell = (fl.Tip - fl.Prev) / MathF.Max(dt, 0.0001f);
                    Gore.DropPiece(fl.Tip, fell * 0.5f,
                        fl.Hair ? Art.Shade(P.Hair, 0.86f) : Art.Shade(P.Skin, 0.84f),
                        fl.Wide * Scale * 0.9f);
                    _flaps.RemoveAt(i);
                }
            }
            if (_flaps.Count == 0) return;

            foreach (var f in _flaps)
            {
                // skin keeps letting go for a while after the shot, so it hangs further the
                // longer he is bleeding - a corpse is messier a minute later than it was
                if (f.Peel < 1f) f.Peel = MathF.Min(1f, f.Peel + dt * (Dead ? 0.35f : 0.12f));
                float reach = f.Len * (1f + f.Peel * 0.45f) * Scale;

                Vector2 anchor = HeadPoint(c, up, fwd, f.At);
                Vector2 v = (f.Tip - f.Prev) * 0.965f;
                f.Prev = f.Tip;
                f.Tip += v + new Vector2(0f, 2400f) * (dt * dt);

                // Skin has some stiffness in it. Left to gravity alone a flap lies flat
                // against the side of his head and reads as a patch of paint; what it should
                // do is stand out from the hole it was lifted from and swing from there. The
                // stiffness goes out of it as it peels, so an old corpse hangs slacker than
                // a fresh one. This is a bias on the way it wants to lie, not a constraint,
                // so it is applied once, before the constraints get their turn.
                Vector2 outv = HeadDir(up, fwd, f.Out);
                float stiff = 0.5f * (1f - f.Peel * 0.6f);
                // ...but it is not stiff enough to hold itself UP. A piece lifted off the
                // crown flops over; one hanging off a cheek stands out from it.
                if (outv.Y < 0f) stiff *= 0.3f + 0.7f * (1f + outv.Y);
                {
                    Vector2 d0 = f.Tip - anchor;
                    float l0 = d0.Length();
                    if (l0 < 0.0001f) { d0 = outv; l0 = 1f; }
                    Vector2 mix = d0 / l0 + outv * stiff;
                    float ml = mix.Length();
                    f.Tip = anchor + (ml > 0.0001f ? mix / ml : outv) * reach;
                }

                // Three things are true of that loose end at once: it is attached, it is not
                // inside his skull, and it is not under the street. Solved one after another
                // each undoes the one before - that is how a flap ended up stretched off his
                // head by the ground, and then shoved back inside his head by the fix for it.
                // So they are solved together, a few passes, which settles all three.
                float skull = 26f * Scale;
                float floor = Scene.GroundY - 2f;
                // Attachment goes LAST of the three, every pass, because it is the one
                // that must never visibly fail: a piece floating a few pixels off the man
                // reads instantly, where a piece a few pixels into his own skull does not.
                for (int pass = 0; pass < 4; pass++)
                {
                    // not through his own skull
                    Vector2 fromC = f.Tip - c;
                    float rr = fromC.Length();
                    if (rr < skull && rr > 0.0001f) f.Tip = c + fromC / rr * skull;

                    // not under the street: it goes down flat and then slides ALONG the
                    // ground back towards him, rather than being lifted off it again
                    if (f.Tip.Y > floor)
                    {
                        f.Tip.Y = floor;
                        float dy = floor - anchor.Y;
                        float dx = MathF.Sqrt(MathF.Max(0f, reach * reach - dy * dy));
                        float side = f.Tip.X < anchor.X ? -1f : 1f;
                        if (dx > 0.0001f) f.Tip.X = anchor.X + side * dx;
                    }

                    // attached, at its own length
                    Vector2 d = f.Tip - anchor;
                    float len = d.Length();
                    if (len < 0.0001f) { d = outv; len = 1f; }
                    f.Tip = anchor + d / len * reach;
                }
            }
        }

        /// <summary>
        /// The loose pieces, drawn in the head's frame. Which face of a flap you are looking
        /// at is not a flag anybody sets - it is which way it happens to be lying. Swung out
        /// past the hinge you see his cheek; folded back over itself you see the underside of
        /// his cheek, and that is a different colour entirely.
        /// </summary>
        void DrawFlaps(Vector2 c, Vector2 up, Vector2 fwd)
        {
            if (_flaps.Count == 0) return;

            foreach (var f in _flaps)
            {
                Vector2 anchor = HeadPoint(c, up, fwd, f.At);
                Vector2 rest = HeadDir(up, fwd, f.Out);
                Vector2 spine = f.Tip - anchor;
                float len = spine.Length();
                if (len < 1f) continue;
                Vector2 u = spine / len;
                Vector2 pp = new Vector2(-u.Y, u.X);

                // Folded back on itself: you are looking at the inside of him. The test is
                // whether it points back across his own head, not whether it points down -
                // a flap hanging off the front of a face is hanging down and is still the
                // outside of his face, which is how everything came out raw the first time.
                Vector2 outward = anchor - c;
                float ol = outward.Length();
                outward = ol > 0.0001f ? outward / ol : rest;
                bool inside = Vector2.Dot(u, outward) < -0.4f;
                // A flap of his own skin laid over his own face is invisible, so the side of it
                // you are looking at is shaded: it is the underneath of the skin, not the top.
                Color face = inside ? Meat : f.Hair ? Art.Shade(P.Hair, 0.86f) : Art.Shade(P.Skin, 0.84f);
                Color edge = inside ? MeatDark : Meat;

                const int K = 8;
                var left = new Vector2[K];
                var right = new Vector2[K];
                for (int k = 0; k < K; k++)
                {
                    float t = k / (float)(K - 1);
                    // wide at the hinge, torn to a ragged point at the loose end
                    // wide at the hinge and torn down to nothing, never a cardboard tab
                    float w = f.Wide * Scale * (1f - 0.46f * t * t - 0.12f * t);
                    float jl = f.Jag[k % f.Jag.Length];
                    float jr = f.Jag[(k + 3) % f.Jag.Length];
                    Vector2 on = anchor + u * (len * t);
                    left[k] = on + pp * w * jl;
                    right[k] = on - pp * w * jr;
                }

                Art.Band(left, right, face, 3f);

                if (Art.InkPass) continue;

                // the raw edge where it tore away, which is what stops it reading as a sticker
                Art.Capsule(Vector2.Lerp(left[0], right[0], 0.1f), Vector2.Lerp(left[0], right[0], 0.9f), 2.1f * Scale, edge);

                // a jaw still has teeth in it
                if (f.Jaw)
                    for (int k = 0; k < 5; k++)
                    {
                        Vector2 on = Vector2.Lerp(left[1], right[1], 0.18f + k * 0.16f);
                        Raylib.DrawCircleV(on, 2.3f * Scale, new Color(232, 226, 208, 235));
                    }

                // and it drips from the low end of itself, wherever that has ended up
                if (Dead && Rng.Chance(0.02f))
                    Gore.Drip(f.Tip, P.Skin);
            }
        }
    }
}
