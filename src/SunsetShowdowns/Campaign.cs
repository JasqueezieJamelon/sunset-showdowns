using System;
using System.Collections.Generic;

namespace LLD
{
    /// <summary>Where a bullet left its mark. The same list serves a scar you carry between
    /// fights and a hit you have taken inside the fight you are in.</summary>
    public enum Hurt { None, GunArm, Leg, Gut, Head }

    /// <summary>
    /// What a body is carrying: one severity per place, 0 for untouched.
    ///
    /// A scar never comes off. Resting takes it down a step, and the step it stops at is 1,
    /// so a gun arm that has been shot is a gun arm that has been shot for the rest of the run.
    /// </summary>
    public class Wounds
    {
        public const int Max = 3;
        public const int Floor = 1;         // what resting can never take a scar below
        readonly int[] _v = new int[5];

        public int this[Hurt h] => _v[(int)h];
        public int Worst(out Hurt where)
        {
            where = Hurt.None;
            int best = 0;
            for (int i = 1; i < _v.Length; i++) if (_v[i] > best) { best = _v[i]; where = (Hurt)i; }
            return best;
        }
        public int Total { get { int t = 0; for (int i = 1; i < _v.Length; i++) t += _v[i]; return t; } }
        public bool Any => Total > 0;

        /// A fresh wound lands at 2. A place already hurt gets worse.
        public void Take(Hurt h)
        {
            if (h == Hurt.None) return;
            int i = (int)h;
            _v[i] = _v[i] == 0 ? 2 : Math.Min(Max, _v[i] + 1);
        }

        public void Set(Hurt h, int v) { if (h != Hurt.None) _v[(int)h] = Math.Clamp(v, 0, Max); }

        /// <summary>A night's rest. One step off the worst of it, and never off entirely.</summary>
        public Hurt Rest()
        {
            int worst = Worst(out Hurt where);
            if (worst > Floor) _v[(int)where] = worst - 1;
            return where;
        }

        public void Clear() { for (int i = 0; i < _v.Length; i++) _v[i] = 0; }

        /// <summary>Everything this body is carrying, added to everything it has taken since
        /// the fight started.</summary>
        public static Wounds Sum(Wounds a, Wounds b)
        {
            var r = new Wounds();
            for (int i = 1; i < 5; i++) r.Set((Hurt)i, Math.Min(Max, a._v[i] + b._v[i]));
            return r;
        }

        public static string Name(Hurt h) => h switch
        {
            Hurt.GunArm => "gun arm",
            
            Hurt.Leg => "leg",
            Hurt.Gut => "gut",
            Hurt.Head => "head",
            _ => "",
        };

        /// <summary>"bad gut", for a sentence that wants it that way round.</summary>
        public static string Phrase(Hurt h, int v)
        {
            if (h == Hurt.None || v <= 0) return "";
            return (v == 1 ? "stiff " : v == 2 ? "bad " : "ruined ") + Name(h);
        }

        public static string Note(Hurt h, int v)
        {
            if (h == Hurt.None || v <= 0) return "";
            string how = v == 1 ? "stiff" : v == 2 ? "bad" : "ruined";
            return Name(h) + ", " + how;
        }
    }

    /// <summary>One name on the board, and what he is worth today.</summary>
    public class Contract
    {
        public int Man;          // index into Roster.All
        public int Skulls;
        public bool Taken;
        public CharDef Def => Roster.All[Man];
    }

    /// <summary>
    /// A run. You take names off a board, you get hurt doing it, and Mordecai Vane does not
    /// come out to meet you until you have taken enough of them.
    ///
    /// There is no money in this. The skulls on a man's paper ARE the economy: a hard man
    /// brings Vane out sooner, an easy one keeps you whole, and every name you leave alone
    /// ripens while you are busy with somebody else.
    /// </summary>
    public class Campaign
    {
        public const int Showing = 3;        // names on the board at once
        public const int VaneAt = 12;        // skulls taken before the last man comes out
        /// One. A run is one life - three made a bad fight cost nothing much, and the
        /// wounds you carry out of a fight only mean anything if losing one is the end.
        public const int Tries = 1;
        public const int MaxSkulls = 5;

        public readonly Wounds Scars = new Wounds();
        public readonly Wounds Fresh = new Wounds();   // taken inside the fight you are in
        public Wounds Carried => Wounds.Sum(Scars, Fresh);

        public int SkullsTaken;
        public int Kills;
        public int TriesLeft = Tries;
        public int Ambushes;             // how many have come for you so far
        public bool AmbushPending;
        public bool Finished;
        public bool Won;

        public readonly List<Contract> Board = new List<Contract>();
        readonly List<int> _pool = new List<int>();
        public Contract Current;
        public bool InAmbush;
        public bool FacingVane;

        /// Vane is always the last entry in the roster, and he is never on the board.
        public static int VaneIndex => Roster.All.Length - 1;

        public bool VaneReady => SkullsTaken >= VaneAt;

        public Campaign()
        {
            for (int i = 0; i < Roster.All.Length; i++) if (i != VaneIndex) _pool.Add(i);
            Shuffle(_pool);
            // the two easy men are always among the first names you see: a run that opened on
            // the Tinsmith and Quint was over before it started
            _pool.Sort((a, b) => Roster.All[a].Skulls.CompareTo(Roster.All[b].Skulls));
            for (int i = 0; i < Showing; i++) Deal();
        }

        static void Shuffle(List<int> xs)
        {
            for (int i = xs.Count - 1; i > 0; i--)
            {
                int j = Rng.I(0, i + 1);
                (xs[i], xs[j]) = (xs[j], xs[i]);
            }
        }

        void Deal()
        {
            if (_pool.Count == 0) return;
            // the first three come off the easy end, everything after that is luck
            int at = Board.Count < Showing ? 0 : Rng.I(0, Math.Min(3, _pool.Count));
            int man = _pool[at];
            _pool.RemoveAt(at);
            Board.Add(new Contract { Man = man, Skulls = Roster.All[man].Skulls });
        }

        /// <summary>
        /// Time passing. Every name you did not take gets a little worse while you were busy,
        /// except the two that never do - a man everybody has already beaten does not become
        /// the hardest thing on the street because you ignored him for an afternoon.
        /// </summary>
        public void Ripen(float odds = 0.5f)
        {
            foreach (var c in Board)
            {
                if (c == Current) continue;
                int cap = Math.Min(MaxSkulls, c.Def.SkullCap);
                if (c.Skulls >= cap) continue;
                if (Rng.Chance(odds)) c.Skulls++;
            }
        }

        public void Take(Contract c)
        {
            Current = c;
            InAmbush = false;
            FacingVane = false;
            TriesLeft = Tries;
            Fresh.Clear();
        }

        public void TakeVane()
        {
            Current = null;
            InAmbush = false;
            FacingVane = true;
            TriesLeft = Tries;
            Fresh.Clear();
        }

        public void TakeAmbush()
        {
            AmbushPending = false;
            Ambushes++;
            Current = null;
            InAmbush = true;
            FacingVane = false;
            TriesLeft = Tries;
            Fresh.Clear();
        }

        /// <summary>The man standing opposite you right now, dialled up to whatever his paper says.</summary>
        public CharDef Opponent()
        {
            if (FacingVane) return Roster.Tune(Roster.All[VaneIndex], Roster.All[VaneIndex].Skulls);
            if (InAmbush) return Roster.Hired(Ambushes);
            return Current == null ? Roster.Tune(Roster.All[0], 1) : Roster.Tune(Current.Def, Current.Skulls);
        }

        /// <summary>A go lost. The hole he put in you stays open for the rest of this fight.</summary>
        /// <returns>true if that was the last of your three.</returns>
        public bool LostATry(Hurt where)
        {
            Fresh.Take(where == Hurt.None ? Hurt.Gut : where);
            TriesLeft--;
            if (TriesLeft <= 0) { Finished = true; Won = false; return true; }
            return false;
        }

        /// <summary>
        /// He is down. What you took getting there stops being a hole and becomes a scar, and
        /// the board moves on without you.
        /// </summary>
        public void WonTheFight()
        {
            int worst = Fresh.Worst(out Hurt where);
            if (worst > 0) Scars.Take(where);
            // a fight that went to three goes marks you twice over
            if (TriesLeft <= Tries - 2 && where != Hurt.None) Scars.Take(where);
            Fresh.Clear();

            if (FacingVane) { Finished = true; Won = true; return; }

            if (InAmbush)
            {
                InAmbush = false;
                Ripen(0.3f);
                return;
            }

            Kills++;
            if (Current != null)
            {
                SkullsTaken += Current.Skulls;
                Board.Remove(Current);
                Current = null;
                if (!VaneReady) Deal();
            }
            Ripen();
            // somebody has been paying men to come looking for you, and they turn up
            // between jobs rather than on the board
            if ((Kills == 3 || Kills == 6) && Ambushes < 2) AmbushPending = true;
        }

        /// <summary>A night off. One step off the worst of it, and the board ripens for it.</summary>
        public Hurt Rest()
        {
            Hurt h = Scars.Rest();
            Ripen(0.75f);
            return h;
        }

        public bool CanRest => Scars.Worst(out _) > Wounds.Floor;
    }
}
