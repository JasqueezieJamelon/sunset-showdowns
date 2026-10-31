using System;
using System.Numerics;

namespace LLD
{
    /// <summary>
    /// Reads the mouse for the one motion that puts the gun away: a pull down that finishes back
    /// at the hip. It must not be a flick - a fast jab downwards is aiming at his boots - but it
    /// does not have to be ruler-straight either, because nobody drags a mouse in a true line.
    /// A frame that strays sideways takes some of the travel back rather than cancelling it, and
    /// only going back up or snatching the mouse wipes the move out.
    /// </summary>
    public class HolsterGesture
    {
        public const float MaxSpeed = 950f;   // units a second; faster than this is a swing, not a holster
        public const float Travel = 60f;      // how far down it has to come
        public const float Near = 120f;       // and how close to the leather it has to end up

        Vector2 _last;
        bool _have;
        float _down;

        public void Reset(Vector2 mouse) { _last = mouse; _have = true; _down = 0f; }

        /// <summary>True on the frame the gesture completes.</summary>
        public bool Update(float dt, Vector2 mouse, Vector2 holster)
        {
            if (!_have) { Reset(mouse); return false; }

            Vector2 d = mouse - _last;
            _last = mouse;

            float speed = dt > 0.0001f ? d.Length() / dt : 0f;
            if (speed > MaxSpeed) { _down = 0f; return false; }

            if (d.Y < -0.6f) { _down = 0f; return false; }          // taking it back up
            if (d.LengthSquared() < 0.04f) return false;            // held still: neither helps nor hurts

            if (MathF.Abs(d.X) < d.Y * 1.8f + 2f) _down += d.Y;     // down, near enough straight
            else _down = MathF.Max(0f, _down - d.Length());         // wandering sideways

            if (_down < Travel) return false;
            if (Vector2.Distance(mouse, holster) > Near) return false;

            _down = 0f;
            return true;
        }
    }
}
