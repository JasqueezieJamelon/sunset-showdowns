using System;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    public enum Phase { Card, Holster, Count, Live, Resolve, Foul }

    public class Duel
    {
        public Fighter Player, Enemy;
        public CharDef Def;
        public Phase Phase = Phase.Card;

        public bool Finished;
        public bool PlayerWon;
        public float Shake;
        public string Headline = "";
        public string Subline = "";

        public int Ammo = 6;
        public const int MaxAmmo = 6;
        public const float ReloadTime = 1.25f;
        public float ReloadLeft;
        public float DrawTimeMs = -1f;   // player reaction, ms from the call to the first shot
        public int Hits, Misses;

        float _phaseT;
        float _countdown;
        float _fireCd;
        float _slow;            // seconds of slow motion left
        float _aiNext;          // next AI shot attempt
        float _aiAimSet;        // time the current aim was chosen
        bool _aiDrawn;
        Vector2 _aiTarget;
        Vector2 _lastShotFrom, _lastShotTo;
        string _lastHit;
        float _tracer;
        float _flash;
        Vector2 _flashAt;
        readonly Settings _s;

        // ---- two-player match ----
        public NetSession Net;
        public int LocalSide;            // 0 = the left fighter, 1 = the right
        public bool IsNet => Net != null;
        public Fighter Mine => LocalSide == 0 ? Player : Enemy;
        public Fighter Theirs => LocalSide == 0 ? Enemy : Player;
        Fighter Side(int i) => i == 0 ? Player : Enemy;
        float _inputTimer;
        bool _foulSent;
        const float FireGrace = 0.12f;   // how long the host waits so the slower line still counts

        /// How far the cursor may drift during the call before it counts as reaching for the gun.
        public const float ReadyJitter = 22f;

        Vector2 _readyMouse;
        bool _readyArmed;

        // The gun clears the leather pointing at the dirt. From the moment of the call, mouse
        // movement is measured from where it was, so you have to pull the gun up yourself
        // instead of it arriving already lined up on him.
        Vector2 _aimAnchor;
        bool _aimAnchored;

        /// <summary>
        /// On the call the aim is put back down at the leather. The screen asks for this rather
        /// than the duel tracking a cursor, so the gun comes up only as far as you move the mouse.
        /// </summary>
        public bool AimReset;
        public Vector2 AimResetTo;

        void AnchorAim(Vector2 mouse)
        {
            AimResetTo = Mine.Arm.HolsterPoint + new Vector2(Mine.Face * 30f, 240f);
            AimReset = true;
            _aimAnchor = AimResetTo;
            _aimAnchored = true;
            _drawnOut = false;
        }

        Vector2 _lastMouse;
        bool _justDrew;

        /// The iron stays in the leather until you reach for it.
        public bool _drawnOut;

        /// Bringing it slowly and straight back down to the hip puts it away again.
        readonly HolsterGesture _putAway = new HolsterGesture();

        /// <summary>
        /// After it goes in the leather his hand stays off it for a moment. Without this the
        /// same movement that stowed the gun carried straight on into drawing it again, and it
        /// was back in his fist before he had let go of it.
        /// </summary>
        public const float DrawLock = 1.0f;
        public float DrawLocked;
        public bool RemoteDrawn;

        /// Both men dead. Nobody scores and nobody loses a life; the round is simply replayed.
        public bool Drawn;
        const float DrawTrigger = 10f;

        // ---------- the run ----------

        /// What the player is carrying into this fight, scars and all. Empty outside a run.
        public Wounds Hurts = new Wounds();

        /// Where he took the worst of it this fight, which is the scar he leaves with.
        public Hurt PlayerHurt = Hurt.None;

        /// No call, no countdown: the fight is already running when the screen comes up.
        public bool Ambush;

        /// Down to your last go, he has watched you take two, and he is half a beat slow
        /// out of the leather for it. Set to 0 to take the mercy back out.
        public float Cocky;

        bool _aiWaiting;        // a mirror, holding until you move
        float _aiPatience;      // ...but not forever
        float _jumpAt = -1f;    // when a man with no nerve goes before the word
        bool _holdoutSpent;
        float _sermonT;
        int _sermonLine;

        /// <summary>
        /// What the preacher says instead of READY. It goes in the same place, in the same
        /// letters, so there is no second thing to watch - the word you are waiting for
        /// arrives in the middle of the words you are not, and lands somewhere different
        /// every time. Kept short: it is set in the headline, and the headline is enormous.
        /// </summary>
        static readonly string[] Sermon =
        {
            "THE WAGES OF SIN",
            "BEHOLD A PALE HORSE",
            "THE NIGHT IS FAR SPENT",
            "DUST THOU ART",
            "LET HIM BE UNJUST STILL",
            "NO PEACE FOR THE WICKED",
            "THE DAY IS AT HAND",
            "AND DEATH FOLLOWED",
        };

        /// How big the headline is drawn, as a fraction. A sermon is a sentence, not a shout.
        public float HeadlineScale = 1f;

        public Duel(CharDef def, Settings s, Wounds hurts = null, bool ambush = false)
        {
            Def = def;
            _s = s;
            Hurts = hurts ?? new Wounds();
            Ambush = ambush;
            Player = new Fighter(s.Look.ToCharDef("You"), Scene.PlayerStand, 1);
            Enemy = new Fighter(def, Scene.EnemyStand, -1);
            Enemy.NoFlinch = def.NoFlinch;
            // a leg he has been shot in before is a leg he still stands badly on
            Player.Crouch = Hurts[Hurt.Leg] * 4.5f;
            Headline = ambush ? "AMBUSH" : def.Name.ToUpperInvariant();
            Subline = ambush ? "\"you are a hard man to find, mister-\"" : def.Trade;
            _phaseT = ambush ? 1.5f : 2.0f;
            if (def.FalseStart && Rng.Chance(0.45f)) _jumpAt = Rng.F(0.25f, 0.8f);
        }

        /// <summary>
        /// A round of a two-player match. Both sides build the same scene, and each man is
        /// dressed from the loadout his own machine chose, swapped during the handshake.
        /// </summary>
        public Duel(Settings s, NetSession net, int localSide)
        {
            _s = s;
            Net = net;
            LocalSide = localSide;
            Loadout mine = net.LocalLook, theirs = net.RemoteLook;
            string myName = string.IsNullOrWhiteSpace(net.LocalName) ? "drifter" : net.LocalName;
            string theirName = string.IsNullOrWhiteSpace(net.RemoteName) ? "challenger" : net.RemoteName;
            CharDef left = (localSide == 0 ? mine : theirs).ToCharDef(localSide == 0 ? myName : theirName);
            CharDef right = (localSide == 1 ? mine : theirs).ToCharDef(localSide == 1 ? myName : theirName);
            Def = localSide == 0 ? right : left;
            Player = new Fighter(left, Scene.PlayerStand, 1);
            Enemy = new Fighter(right, Scene.EnemyStand, -1);
            Phase = Phase.Count;
            Headline = "";
            Subline = "";
        }

        public Vector2 HolsterPoint => Mine.Arm.HolsterPoint;
        public float TimeScale => _slow > 0f ? 0.28f : 1f;

        public void Update(float rdt, Vector2 mouse, bool firePressed, bool reloadPressed)
        {
            float dt = rdt * TimeScale;
            _slow = MathF.Max(0f, _slow - rdt);
            Shake = MathF.Max(0f, Shake - rdt * 4.5f);
            _tracer = MathF.Max(0f, _tracer - rdt * 9f);
            _flash = MathF.Max(0f, _flash - rdt * 14f);
            Scene.Update(dt);

            _lastMouse = mouse;
            bool holstered = Phase == Phase.Card || Phase == Phase.Count;

            // The iron stays in the leather until you reach for it, and the motion that clears
            // it is not also the shot. A disqualified man may still draw; he just cannot fire.
            _justDrew = false;
            DrawLocked = MathF.Max(0f, DrawLocked - rdt);
            // while his hand is off the gun the cursor is only moving the empty hand, so the
            // anchor comes with it and the draw is measured from wherever he lets it settle
            if (DrawLocked > 0f) _aimAnchor = mouse;

            if (!_drawnOut && _aimAnchored && DrawLocked <= 0f &&
                (Phase == Phase.Live || Phase == Phase.Foul) &&
                (firePressed || Vector2.Distance(mouse, _aimAnchor) > DrawTrigger))
            {
                _drawnOut = true;
                _justDrew = true;
                Sfx.Play("holster", 0.8f, 0.9f);
            }

            // ...and he can put it back. Only while he is on his feet and still holding it:
            // a man on the ground has other problems and a disarmed one has nothing to stow.
            if (_drawnOut && Mine.Arm.HasGun && !Mine.Down &&
                (Phase == Phase.Live || Phase == Phase.Foul))
            {
                if (_putAway.Update(rdt, mouse, Mine.Arm.HolsterPoint))
                {
                    _drawnOut = false;
                    _aimAnchor = mouse;     // so the next flick counts as a fresh draw
                    DrawLocked = DrawLock;
                    Sfx.Play("holster", 0.75f, 0.72f);
                }
            }
            else _putAway.Reset(mouse);

            // the host's ruling ends the round wherever it is read from
            if (IsNet && Net.RoundOverPending && Phase != Phase.Resolve)
            {
                Net.RoundOverPending = false;
                ApplyRoundEnd(Net.RoundWinner, Net.RoundNote, Net.RoundLoserNote);
            }

            switch (Phase)
            {
                case Phase.Card:
                    _phaseT -= rdt;
                    if (_phaseT <= 0f)
                    {
                        // An ambush does not get a call. There is no READY, there is no
                        // countdown and nobody says DRAW - the line he was speaking stops in
                        // the middle and the fight is already going.
                        if (Ambush) { GoLive(mouse, "", Rng.F(0.30f, 0.46f)); break; }

                        Phase = Phase.Count;
                        // a preacher talks over the word, so the word can come a long way in
                        _countdown = Def.Sermon ? Rng.F(1.6f, 5.4f) : Rng.F(1.1f, 2.9f);
                        // He does not get a READY at all. The sermon IS the ready, which is
                        // the whole of him: you cannot watch for one word arriving in an
                        // empty space, you have to read the one that is already there.
                        Headline = Def.Sermon ? Sermon[_sermonLine = Rng.I(0, Sermon.Length)] : "READY";
                        HeadlineScale = Def.Sermon ? 0.52f : 1f;
                        Subline = "";
                        _sermonT = 1.1f;
                        _readyArmed = false;
                        _aimAnchored = false;
                        Sfx.Play("ready", 0.7f);
                    }
                    break;

                case Phase.Count:
                    {
                        // Hold still until the call. Nudging the mouse enough to swing the gun is
                        // the same as reaching for it early.
                        if (!_readyArmed) { _readyMouse = mouse; _readyArmed = true; }
                        bool moved = Vector2.Distance(mouse, _readyMouse) > ReadyJitter;

                        if (IsNet)
                        {
                            if (moved) { NetFoul("YOU DREW EARLY"); break; }
                            if (firePressed) { NetFoul("YOU FIRED EARLY"); break; }
                            if (Net.HostTime >= NetDrawTime)
                            {
                                Phase = Phase.Live;
                                AnchorAim(mouse);
                                Headline = "DRAW!";
                                Subline = "";
                                _phaseT = 0f;
                                Sfx.Play("cue", 0.85f);
                                Sfx.Play("holster", 0.8f, 0.9f);
                            }
                            break;
                        }
                        _countdown -= rdt;

                        // the sermon runs on, and the word is somewhere inside it
                        if (Def.Sermon)
                        {
                            _sermonT -= rdt;
                            if (_sermonT <= 0f)
                            {
                                _sermonT = Rng.F(0.85f, 1.35f);
                                _sermonLine = (_sermonLine + 1) % Sermon.Length;
                                Headline = Sermon[_sermonLine];
                            }
                        }

                        // A man whose nerve goes first. He jumps the call, which ends the
                        // count - so you are not disqualified for going with him - and he
                        // arrives rushed, with his gun half up and a long fumble in front
                        // of him. Hold, and he has handed you the fight.
                        if (_jumpAt > 0f && _countdown <= _jumpAt)
                        {
                            GoLive(mouse, "HE DREW EARLY", Rng.F(0.62f, 0.92f));
                            Subline = "take your time";
                            _jumpAt = -1f;
                            break;
                        }

                        if (moved) { Foul("YOU DREW EARLY"); break; }
                        if (firePressed) { Foul("YOU FIRED EARLY"); break; }
                        if (_countdown <= 0f) GoLive(mouse, "DRAW!", Rng.F(Def.ReactMin, Def.ReactMax));
                        break;
                    }

                case Phase.Live:
                    _phaseT += rdt;
                    if (_phaseT > 0.75f) Headline = "";

                    if (!IsNet) UpdateAi(dt);
                    if (firePressed && !_justDrew) PlayerShoot();
                    if (reloadPressed && Ammo < MaxAmmo && ReloadLeft <= 0f) StartReload();
                    if (ReloadLeft > 0f)
                    {
                        ReloadLeft -= dt;
                        if (ReloadLeft <= 0f) { Ammo = MaxAmmo; Sfx.Play("holster", 0.7f, 1.2f); }
                    }
                    if (IsNet)
                    {
                        if (Net.IsHost)
                        {
                            HostResolveFires();
                            if (Net.RemoteFoul) { Net.RemoteFoul = false; HostEndRound(0, "THEY DREW EARLY", FoulWord(Net.RemoteFoulNote)); break; }
                            int netOut = RoundOutcome(rdt);
                            if (netOut == 1) HostEndRound(0, Enemy.Killed ? "HE DROPS" : "HE IS FINISHED");
                            else if (netOut == 2) HostEndRound(1, Player.Killed ? "HE DROPS" : "HE IS FINISHED");
                            else if (netOut == 3) HostEndRound(2, "A DRAW", "A DRAW");
                        }
                        break;
                    }
                    int outcome = RoundOutcome(rdt);
                    if (outcome == 1) EndRound(true, Enemy.Killed ? "HE DROPS" : "HE IS FINISHED");
                    else if (outcome == 2) EndRound(false, "YOU ARE DOWN");
                    else if (outcome == 3) { Drawn = true; EndRound(false, "A DRAW"); }
                    break;

                case Phase.Resolve:
                case Phase.Foul:
                    _phaseT -= rdt;
                    if (Enemy.Down && Rng.Chance(rdt * 3f)) Gore.Pool(Enemy.Root + new Vector2(Enemy.Face * -30f, 0f), Rng.F(14f, 30f));
                    if (Player.Down && Rng.Chance(rdt * 3f)) Gore.Pool(Player.Root + new Vector2(Player.Face * -30f, 0f), Rng.F(14f, 30f));
                    if (_phaseT <= 0f) Finished = true;
                    break;
            }

            _fireCd = MathF.Max(0f, _fireCd - dt);

            // the hand reaches out along the aim point and the wrist lines the barrel up on it,
            // so the shot goes where you are pointing - give or take whatever is wrong with you
            Vector2 aimPoint = mouse + Wander(rdt);
            Mine.Arm.AimAt = aimPoint;
            Vector2 armTarget = Mine.Arm.AimHandFor(aimPoint);
            Mine.Update(dt, armTarget, Fighter.ArmWeight * (1f + Hurts[Hurt.GunArm] * 0.22f),
                holstered || !_drawnOut || Mine.Dead);

            if (IsNet)
            {
                ApplyNetHits();
                if (Net.RemoteHandValid) Theirs.Arm.AimAt = Net.RemoteHand;
                Vector2 remote = Net.RemoteHandValid
                    ? Theirs.Arm.AimHandFor(Net.RemoteHand)
                    : Theirs.Arm.HolsterPoint;
                Theirs.Update(dt, remote, Fighter.ArmWeight, holstered || !Net.RemoteDrawn || Theirs.Dead);

                // tell them what we are aiming at, thirty times a second
                _inputTimer -= rdt;
                if (_inputTimer <= 0f)
                {
                    _inputTimer = 1f / 30f;
                    Net.SendInput(Mine.Arm.AimAt, _drawnOut);
                }
                if (Net.IsHost) HostApplyRemoteFoulOutsideLive();

                // The stance is physical, so two machines running the same inputs still drift
                // apart - one screen has him crouched, the other stood up. The host is the
                // referee for bodies as well as bullets: it states the pose ten times a second
                // and the client eases onto it.
                if (Net.IsHost)
                {
                    _snapTimer -= rdt;
                    if (_snapTimer <= 0f) { _snapTimer = 0.1f; Net.SendSnapshot(Player, Enemy); }
                }
                else if (Net.HasSnapshot)
                {
                    Net.HasSnapshot = false;
                    Player.ApplySnap(Net.SnapLeft, Player == Mine ? 0.12f : 0.35f);
                    Enemy.ApplySnap(Net.SnapRight, Enemy == Mine ? 0.12f : 0.35f);
                }
            }
            else
            {
                if (_aiDrawn) Enemy.Arm.AimAt = _aiTarget;
                Enemy.Update(dt, _aiDrawn ? Enemy.Arm.AimHandFor(_aiTarget) : Enemy.Arm.HolsterPoint,
                    0.35f, holstered || !_aiDrawn || Enemy.Dead);
            }

            Gore.Update(dt);
        }

        float _snapTimer;
        float _outT0, _outT1, _liveT;
        const float OutGrace = 4f;      // how long a man gets to fight from the ground
        const float RoundCap = 45f;     // nobody stands in the street forever

        /// <summary>Within a hair of each other counts as level rather than a win.</summary>
        int Settle()
        {
            float gap = Player.Blood - Enemy.Blood;
            if (MathF.Abs(gap) < 2f) return 3;
            return gap > 0f ? 1 : 2;
        }

        /// <summary>0 nobody yet, 1 the left man wins, 2 the right man wins, 3 a draw.</summary>
        int RoundOutcome(float dt)
        {
            _liveT += dt;
            bool d0 = Player.Down, d1 = Enemy.Down;
            _outT0 = (d0 && !d1) ? _outT0 + dt : 0f;
            _outT1 = (d1 && !d0) ? _outT1 + dt : 0f;

            // both of them gone: neither man won that, and pretending one did was arbitrary
            if (Player.Dead && Enemy.Dead) return 3;
            if (Player.Dead) return 2;
            if (Enemy.Dead) return 1;

            // off his feet and still breathing: he gets a few seconds to shoot back, then it is over
            if (d0 && _outT0 > OutGrace) return 2;
            if (d1 && _outT1 > OutGrace) return 1;

            // both on the ground, or a stalemate: the one with more blood in him takes it,
            // and if there is nothing between them that is a draw too
            if (d0 && d1) return Settle();
            if (_liveT > RoundCap) return Settle();
            return 0;
        }

        /// <summary>The moment it starts, however it started.</summary>
        void GoLive(Vector2 mouse, string headline, float react)
        {
            Phase = Phase.Live;
            AnchorAim(mouse);
            Headline = headline;
            HeadlineScale = 1f;   // whatever he was saying, DRAW is said at full volume
            Subline = "";
            _phaseT = 0f;
            _aiDrawn = false;
            _aiNext = react + Cocky;
            // A mirror does not answer the call, he answers YOUR draw, so there is no
            // waiting him out. He will not stand there all evening either.
            _aiWaiting = Def.Mirror;
            _aiPatience = Rng.F(2.6f, 3.8f);
            Sfx.Play("cue", 0.85f);
            Sfx.Play("holster", 0.8f, 0.9f);
        }

        void Foul(string why)
        {
            Phase = Phase.Foul;
            Headline = "DISQUALIFIED";
            Subline = why.ToLowerInvariant();
            _phaseT = 2.2f;
            PlayerWon = false;
            Sfx.Play("foul", 0.9f);
            // he is out of the round, but he can still clear leather; he just has nothing to shoot for
            if (!_aimAnchored) AnchorAim(_lastMouse);
        }

        void EndRound(bool won, string headline)
        {
            Phase = Phase.Resolve;
            PlayerWon = won;
            Headline = headline;
            Subline = Drawn ? "both of you" : won ? (_lastHit ?? "") : Def.Quip;
            _phaseT = 2.6f;
            _slow = 0.9f;
            Shake = 1f;
            if (won) Sfx.Play("win", 0.8f);
        }

        float _wanderT;

        /// <summary>
        /// How far off your own aim is, and it is not the gun. A shot arm will not hold the
        /// line, a leg you cannot stand on puts a slow sway through everything above it, and
        /// a gut wound is steady at first and worse the longer you are out there - which is
        /// exactly why Gantry, who has all day, is the man you least want to meet carrying one.
        /// Nothing here is a new rule: the barrel still points where the hand points, and the
        /// bullet still goes where the barrel points.
        /// </summary>
        Vector2 Wander(float rdt)
        {
            int arm = Hurts[Hurt.GunArm], leg = Hurts[Hurt.Leg], gut = Hurts[Hurt.Gut], head = Hurts[Hurt.Head];
            if (arm + leg + gut + head == 0) return Vector2.Zero;
            _wanderT += rdt;

            float shake = arm * 5.5f;
            float sway = leg * 4.0f;
            float sick = gut * 3.0f * MathF.Min(1f, _liveT / 9f);
            // a graze across the scalp: everything swims for a moment after the call, then clears
            float blur = head * 16f * MathF.Max(0f, 1f - _phaseT * 1.6f) * (Phase == Phase.Live ? 1f : 0f);

            float a = _wanderT * 9.3f, b = _wanderT * 2.1f, c = _wanderT * 5.7f;
            return new Vector2(
                MathF.Sin(a) * shake + MathF.Sin(b * 0.8f) * sway + MathF.Sin(c * 0.6f) * (sick + blur),
                MathF.Cos(a * 1.17f) * shake + MathF.Cos(b) * sway + MathF.Cos(c * 0.9f) * (sick + blur));
        }

        void StartReload()
        {
            // the hand that works the cylinder is the hand that has been shot
            ReloadLeft = ReloadTime * (1f + Hurts[Hurt.GunArm] * 0.30f);
            for (int i = 0; i < 6; i++) Gore.Casing(Player.Arm.Hand, Player.Face);
            Sfx.Play("holster", 0.7f, 0.85f);
        }

        // ---------- shooting ----------
        void PlayerShoot()
        {
            if (Phase != Phase.Live || Mine.Dead || !_drawnOut) return;
            if (ReloadLeft > 0f) return;
            if (_fireCd > 0f) return;
            if (!Mine.Arm.HasGun) { Sfx.Play("click", 0.6f); return; }
            if (Ammo <= 0) { Sfx.Play("click", 0.7f, 1.2f); return; }

            Ammo--;
            _fireCd = 0.26f;
            if (DrawTimeMs < 0f) DrawTimeMs = _phaseT * 1000f;

            if (IsNet)
            {
                // the flash and the noise are ours to play at once; the outcome is the host's
                Vector2 hand = Mine.Arm.Hand;
                Vector2 aim = Mine.Arm.AimAt;
                Flash(Mine, Mine.Arm.Muzzle, Mine.Arm.BarrelDir, 1f);
                Gore.Casing(hand, Mine.Face);
                double t = Net.HostTime;
                if (Net.IsHost) Net.HostFireQueue.Add((t, hand, aim, (byte)LocalSide));
                else Net.SendFire(t, hand, aim);
                return;
            }

            Fire(Player, Enemy, 1f);
            Gore.Casing(Player.Arm.Hand, Player.Face);
        }

        /// <summary>Everything a shot does that is not damage: kick, noise, smoke, flash.</summary>
        void Flash(Fighter shooter, Vector2 from, Vector2 dir, float power)
        {
            shooter.Arm.Kick(power);
            Sfx.Play(shooter == Mine ? "shot" : "shot_far", 1f, Rng.F(0.94f, 1.07f));
            Gore.MuzzleSmoke(from, dir);
            _flash = 1f;
            _flashAt = from;
            Shake = MathF.Max(Shake, shooter == Mine ? 0.5f : 0.35f);
            _lastShotFrom = from;
            _lastShotTo = from + dir * 2800f;
            _tracer = 1f;
        }

        void Fire(Fighter shooter, Fighter target, float power)
            => FireFrom(shooter, target, shooter.Arm.Muzzle, shooter.Arm.BarrelDir, power);

        /// <summary>
        /// A shot, from a stated place along a stated line. Nearly always that is the muzzle
        /// pointing where the barrel points; a gun palmed in a sleeve is the exception.
        /// </summary>
        void FireFrom(Fighter shooter, Fighter target, Vector2 from, Vector2 dir, float power)
        {
            Flash(shooter, from, dir, power);

            const float len = 2800f;
            bool hit = target.Raycast(from, dir, len, out Zone zone, out Vector2 point, out float dist);

            // the street stops a low shot before it reaches anyone
            float groundT = dir.Y > 0.001f ? (Scene.GroundY - from.Y) / dir.Y : float.MaxValue;
            if (groundT > 0f && groundT < (hit ? dist : len))
            {
                Vector2 gp = from + dir * groundT;
                _lastShotTo = gp;
                Gore.DustPuff(gp, dir, 1.2f);
                Sfx.Play("ric", 0.5f, Rng.F(0.8f, 1.3f));
                if (shooter == Player) Misses++;
                return;
            }

            if (!hit)
            {
                if (shooter == Player) Misses++;
                Sfx.Play("ric", 0.35f, Rng.F(1.1f, 1.6f));
                return;
            }

            _lastShotTo = point;
            if (zone == Zone.Hat)
            {
                target.KnockHat(dir);
                if (shooter == Player) Misses++;
                return;
            }

            // what the bullet did is decided by the line it takes through the body
            var rep = target.TakeShot(from, dir, len, zone, point);
            Gore.Impact(point, dir, zone, rep.Gore, rep.Instant);
            if (rep.ThroughAndThrough) Gore.ExitBurst(rep.Exit, dir, rep.Gore);

            bool big = rep.Instant || target.Down;
            Shake = MathF.Max(Shake, big ? 1f : 0.6f);
            if (shooter == Player)
            {
                Hits++;
                _lastHit = rep.Describe();
            }
            // what he put in you is what you walk away carrying, so it is worth knowing
            // where the worst of it landed
            else if (target == Player)
            {
                Hurt h = zone switch
                {
                    Zone.Head => Hurt.Head,
                    Zone.Leg => Hurt.Leg,
                    Zone.Arm or Zone.Hand => Hurt.GunArm,
                    _ => Hurt.Gut,
                };
                if (PlayerHurt == Hurt.None || h == Hurt.Head) PlayerHurt = h;
            }
            if (big) _slow = 1.0f;
        }

        // ---------- opponent ----------
        void UpdateAi(float dt)
        {
            if (Enemy.Dead || Player.Dead || Enemy.StunLeft > 0f) return;

            // A mirror's clock does not start on the call. It starts when your hand moves,
            // so standing still buys you nothing at all against him - though he will not
            // wait out the whole evening for you.
            if (_aiWaiting)
            {
                _aiPatience -= dt;
                if (_drawnOut || _aiPatience <= 0f) _aiWaiting = false;
                else return;
            }

            _aiNext -= dt;

            if (!_aiDrawn)
            {
                if (_aiNext <= 0f)
                {
                    _aiDrawn = true;
                    PickAim();
                    _aiNext = 0f;
                }
                return;
            }

            // Down is not done. There is one more in a sleeve and it goes off from the floor.
            if (Def.Holdout && !_holdoutSpent && Enemy.Down && !Enemy.Dead && _aiNext <= 0f)
            {
                _holdoutSpent = true;
                _aiNext = 1.4f;
                Vector2 at = Player.L2W(new Vector2(0f, -280f));
                Vector2 from = Enemy.Arm.Hand;
                Vector2 d = Vector2.Normalize(at - from + new Vector2(0.001f, 0f));
                float sp = Def.AimError * 2.4f;
                d = Vector2.Normalize(d + new Vector2(Rng.F(-sp, sp), Rng.F(-sp, sp)));
                FireFrom(Enemy, Player, from, d, 0.7f);
                return;
            }

            if (Enemy.Disarmed) { _aiAimSet = 0f; return; }

            _aiAimSet += dt;
            // fire once the barrel has settled near the chosen line, or when patience runs out
            Vector2 want = _aiTarget - Enemy.Arm.Shoulder;
            Vector2 have = Enemy.Arm.Hand - Enemy.Arm.Shoulder;
            float off = MathF.Abs(MathF.Atan2(want.Y, want.X) - MathF.Atan2(have.Y, have.X));
            bool settled = off < 0.06f && Enemy.Arm.Vel.Length() < 260f;

            if (_aiNext <= 0f && (settled || _aiAimSet > 1.3f))
            {
                Fire(Enemy, Player, 0.9f);
                // a man losing blood works the hammer slower and slower
                _aiNext = Def.Refire * Rng.F(0.85f, 1.2f) * (1f + Enemy.Impairment * 0.8f) + Enemy.Kneel * 0.25f;
                PickAim();
            }
        }

        Vector2 Spot(Hurt h) => h switch
        {
            Hurt.Head => Player.L2W(new Vector2(4f, -396f)),
            Hurt.Leg => Player.L2W(new Vector2(0f, -120f)),
            Hurt.GunArm => Player.Arm.Elbow,
            _ => Player.L2W(new Vector2(0f, -280f)),
        };

        void PickAim()
        {
            _aiAimSet = 0f;
            Vector2 aimAt;
            switch (Def.Aim)
            {
                case AimAt.Centre:
                    // the middle of you, every time, and he has all day to find it
                    aimAt = Spot(Hurt.Gut);
                    break;

                case AimAt.Arm:
                    // she is not trying to kill you. She is trying to take the gun off you,
                    // which under a run that carries its wounds is arguably worse.
                    aimAt = Rng.Chance(0.78f) ? Spot(Hurt.GunArm) : Spot(Hurt.Gut);
                    break;

                case AimAt.Worst:
                    // he reads what the rest of them left of you and finishes it
                    {
                        int worst = Hurts.Worst(out Hurt where);
                        aimAt = worst > 0 && Rng.Chance(0.7f) ? Spot(where)
                              : Rng.Chance(0.3f) ? Spot(Hurt.Head) : Spot(Hurt.Gut);
                    }
                    break;

                default:
                    {
                        float r = Rng.F(0f, 1f);
                        if (r < 0.18f) aimAt = Spot(Hurt.Head);
                        else if (r < 0.74f) aimAt = Spot(Hurt.Gut);
                        else if (r < 0.88f) aimAt = Spot(Hurt.Leg);
                        else aimAt = Spot(Hurt.GunArm);
                    }
                    break;
            }

            // scatter the point of aim, then solve for the hand position that actually points there
            float spread = Def.AimError * (1f + Enemy.Kneel * 1.5f + Enemy.Impairment * 2.2f);
            Vector2 sh = Enemy.Arm.Shoulder;
            float dist = Vector2.Distance(aimAt, sh);
            Vector2 perp = new Vector2(0f, 1f);
            aimAt += perp * Rng.F(-spread, spread) * dist;
            aimAt += new Vector2(Rng.F(-spread, spread) * dist * 0.3f, 0f);
            _aiTarget = aimAt;
        }

        // ---------- two-player match ----------
        public double NetDrawTime;

        /// <summary>Both sides are told the same moment on the same clock.</summary>
        public void BeginNetRound(double drawTime)
        {
            NetDrawTime = drawTime;
            Phase = Phase.Count;
            Headline = "READY";
            Subline = "";
            _foulSent = false;
            _readyArmed = false;        // the cursor is sampled on the first frame of the call
            _aimAnchored = false;
            Sfx.Play("ready", 0.7f);
        }

        void NetFoul(string why)
        {
            if (_foulSent) return;
            _foulSent = true;
            Net.SendFoul(why);
            Phase = Phase.Foul;
            Headline = "DISQUALIFIED";
            Subline = why.ToLowerInvariant();
            _phaseT = 2.4f;
            Sfx.Play("foul", 0.9f);
            if (!_aimAnchored) AnchorAim(_lastMouse);
            if (Net.IsHost) HostEndRound((byte)(1 - LocalSide), "THEY DREW EARLY", why);
        }

        static string FoulWord(string s) => string.IsNullOrEmpty(s) ? "YOU DREW EARLY" : s;

        void HostApplyRemoteFoulOutsideLive()
        {
            if (!Net.RemoteFoul || Phase == Phase.Live) return;
            Net.RemoteFoul = false;
            HostEndRound((byte)LocalSide, "THEY DREW EARLY", FoulWord(Net.RemoteFoulNote));
        }

        /// <param name="winner">0 the left man, 1 the right man, 2 nobody - a draw.</param>
        void HostEndRound(byte winner, string note, string loserNote = "YOU ARE DOWN")
        {
            if (Phase == Phase.Resolve) return;
            if (winner < 2) Net.Score[winner]++;
            Net.SendRoundEnd(winner, note, loserNote);
            ApplyRoundEnd(winner, note, loserNote);
        }

        /// <param name="loserNote">What the side that lost is told. A false start has to say so
        /// on both screens, or the man who jumped the call is told he was shot.</param>
        public void ApplyRoundEnd(byte winner, string note, string loserNote)
        {
            Phase = Phase.Resolve;
            Drawn = winner == 2;
            PlayerWon = !Drawn && winner == LocalSide;
            Headline = Drawn
                ? "A DRAW"
                : PlayerWon
                    ? (string.IsNullOrEmpty(note) ? "HE DROPS" : note)
                    : (string.IsNullOrEmpty(loserNote) ? "YOU ARE DOWN" : loserNote);
            Subline = Drawn ? "both of you" : PlayerWon ? (_lastHit ?? "") : "";
            _phaseT = 2.6f;
            _slow = 0.9f;
            Shake = 1f;
            if (PlayerWon) Sfx.Play("win", 0.8f);
        }

        /// <summary>
        /// The host waits a moment before judging, so a shot that left a slower connection still
        /// counts at the instant it was actually fired rather than the instant it arrived.
        /// </summary>
        void HostResolveFires()
        {
            var q = Net.HostFireQueue;
            if (q.Count == 0) return;
            double now = Net.HostTime;
            q.Sort((a, b) => a.time.CompareTo(b.time));
            while (q.Count > 0 && q[0].time <= now - FireGrace)
            {
                var e = q[0];
                q.RemoveAt(0);
                HostShoot(e.side, e.hand, e.aim);
            }
        }

        void HostShoot(byte side, Vector2 hand, Vector2 aim)
        {
            Fighter shooter = Side(side), target = Side(1 - side);
            if (shooter.Dead || target.Dead) return;

            // rebuild the barrel line from the hand and the aim point recorded as it was fired
            Vector2 line = aim - hand;
            Vector2 outward = hand - shooter.Arm.Shoulder;
            if (line.LengthSquared() < 3600f || Vector2.Dot(line, outward) <= 0f) line = aim - shooter.Arm.Shoulder;
            if (line.LengthSquared() < 1f) line = outward;
            float ang = Art.Angle(line);
            Vector2 from = hand + Art.Rot(new Vector2(ArmRig.MuzzleX, ArmRig.MuzzleY * shooter.Face), ang);
            Vector2 dir = Art.FromAngle(ang);

            NetHit h = ResolveNetShot(side, shooter, target, from, dir);
            Net.SendHit(h);
        }

        NetHit ResolveNetShot(byte side, Fighter shooter, Fighter target, Vector2 from, Vector2 dir)
        {
            const float len = 2800f;
            var h = new NetHit
            {
                Shooter = side,
                Target = (byte)(1 - side),
                MuzzleX = from.X, MuzzleY = from.Y,
                DirX = dir.X, DirY = dir.Y,
                Zone = (byte)Zone.None,
                Note = "",
            };

            if (shooter != Mine) Flash(shooter, from, dir, 0.9f);

            bool hit = target.Raycast(from, dir, len, out Zone zone, out Vector2 point, out float dist);
            float groundT = dir.Y > 0.001f ? (Scene.GroundY - from.Y) / dir.Y : float.MaxValue;

            if (groundT > 0f && groundT < (hit ? dist : len))
            {
                Vector2 gp = from + dir * groundT;
                h.EndX = gp.X; h.EndY = gp.Y;
                _lastShotTo = gp;
                Gore.DustPuff(gp, dir, 1.2f);
                Sfx.Play("ric", 0.5f, Rng.F(0.8f, 1.3f));
                if (side == LocalSide) Misses++;
                return h;
            }

            if (!hit)
            {
                Vector2 end = from + dir * len;
                h.EndX = end.X; h.EndY = end.Y;
                Sfx.Play("ric", 0.35f, Rng.F(1.1f, 1.6f));
                if (side == LocalSide) Misses++;
                return h;
            }

            h.EndX = point.X; h.EndY = point.Y;
            h.Zone = (byte)zone;
            _lastShotTo = point;

            if (zone == Zone.Hat)
            {
                target.KnockHat(dir);
                if (side == LocalSide) Misses++;
                return h;
            }

            var rep = target.TraceShot(from, dir, len, zone, point);
            target.ApplyShot(rep, zone, dir);

            h.Damaged = true;
            h.LocalX = rep.LocalEntry.X; h.LocalY = rep.LocalEntry.Y;
            h.ExitX = rep.LocalExit.X; h.ExitY = rep.LocalExit.Y;
            h.Shock = rep.Shock; h.Bleed = rep.Bleed; h.Gore = rep.Gore;
            h.Instant = rep.Instant; h.Cripple = rep.Cripple;
            h.Disarm = rep.Disarm; h.Through = rep.ThroughAndThrough;
            h.Head = (byte)(rep.HeadSeverity | (rep.EyeOut ? 4 : 0) | (rep.JawGone ? 8 : 0));
            h.Note = rep.Note;

            Gore.Impact(point, dir, zone, rep.Gore, rep.Instant);
            if (rep.ThroughAndThrough) Gore.ExitBurst(rep.Exit, dir, rep.Gore);

            bool big = rep.Instant || target.Down;
            Shake = MathF.Max(Shake, big ? 1f : 0.6f);
            if (big) _slow = 1.0f;
            if (side == LocalSide) { Hits++; _lastHit = rep.Note; }
            return h;
        }

        /// <summary>Replay the host's rulings. Only the client has anything in this queue.</summary>
        void ApplyNetHits()
        {
            while (Net.Hits.Count > 0)
            {
                var h = Net.Hits.Dequeue();
                Fighter shooter = Side(h.Shooter), target = Side(h.Target);
                Vector2 from = new Vector2(h.MuzzleX, h.MuzzleY);
                Vector2 end = new Vector2(h.EndX, h.EndY);
                Vector2 dir = new Vector2(h.DirX, h.DirY);

                if (h.Shooter != LocalSide) Flash(shooter, from, dir, 0.9f);
                _lastShotFrom = from;
                _lastShotTo = end;
                _tracer = 1f;

                var zone = (Zone)h.Zone;
                if (!h.Damaged)
                {
                    if (zone == Zone.Hat) target.KnockHat(dir);
                    else if (end.Y >= Scene.GroundY - 4f) { Gore.DustPuff(end, dir, 1.2f); Sfx.Play("ric", 0.5f, Rng.F(0.8f, 1.3f)); }
                    else Sfx.Play("ric", 0.35f, Rng.F(1.1f, 1.6f));
                    if (h.Shooter == LocalSide) Misses++;
                    continue;
                }

                var rep = new ShotReport
                {
                    Shock = h.Shock,
                    Bleed = h.Bleed,
                    Gore = h.Gore,
                    Instant = h.Instant,
                    Cripple = h.Cripple,
                    Disarm = h.Disarm,
                    ThroughAndThrough = h.Through,
                    LocalEntry = new Vector2(h.LocalX, h.LocalY),
                    LocalExit = new Vector2(h.ExitX, h.ExitY),
                    HeadSeverity = h.Head & 3,
                    EyeOut = (h.Head & 4) != 0,
                    JawGone = (h.Head & 8) != 0,
                    Note = h.Note,
                };
                rep.Entry = target.L2W(rep.LocalEntry);
                rep.Exit = target.L2W(rep.LocalExit);

                target.ApplyShot(rep, zone, dir);
                Gore.Impact(end, dir, zone, rep.Gore, rep.Instant);
                if (rep.ThroughAndThrough) Gore.ExitBurst(rep.Exit, dir, rep.Gore);

                bool big = rep.Instant || target.Down;
                Shake = MathF.Max(Shake, big ? 1f : 0.6f);
                if (big) _slow = 1.0f;
                if (h.Shooter == LocalSide) { Hits++; _lastHit = h.Note; }
            }
        }

        // ---------- predicted point of impact ----------
        public bool Predict(out Vector2 point, out Zone zone)
        {
            point = Vector2.Zero; zone = Zone.None;
            if (Phase != Phase.Live || Player.Dead || !Player.Arm.HasGun) return false;
            Vector2 from = Player.Arm.Muzzle;
            Vector2 dir = Player.Arm.BarrelDir;
            const float len = 2800f;
            bool hit = Enemy.Raycast(from, dir, len, out zone, out point, out float dist);
            float groundT = dir.Y > 0.001f ? (Scene.GroundY - from.Y) / dir.Y : float.MaxValue;
            if (groundT > 0f && groundT < (hit ? dist : len))
            {
                point = from + dir * groundT;
                zone = Zone.None;
                return true;
            }
            if (!hit) { point = from + dir * len; zone = Zone.None; }
            return true;
        }

        // ---------- draw ----------
        /// <summary>Extent of everything worth keeping in frame, so the camera can pull back.</summary>
        public void Interest(out float lo, out float hi)
        {
            lo = MathF.Min(Player.Root.X, Enemy.Root.X);
            hi = MathF.Max(Player.Root.X, Enemy.Root.X);
            foreach (var f in new[] { Player, Enemy })
            {
                if (!f.Down || !f.Doll.Active) continue;
                for (int i = 0; i < Ragdoll.Count; i++)
                {
                    lo = MathF.Min(lo, f.Doll.Pos[i].X);
                    hi = MathF.Max(hi, f.Doll.Pos[i].X);
                }
            }
        }

        public void DrawWorld(float viewX0, float viewX1)
        {
            Scene.Draw(viewX0, viewX1);
            Gore.DrawDecals();

            if (Enemy.Down && !Player.Down) { Player.Draw(_s.ShowZones); Enemy.Draw(_s.ShowZones); }
            else { Enemy.Draw(_s.ShowZones); Player.Draw(_s.ShowZones); }

            Gore.Draw();

            if (_tracer > 0f)
            {
                float a = _tracer * 0.5f;
                Raylib.DrawLineEx(_lastShotFrom, _lastShotTo, 2.5f, Art.Alpha(new Color(255, 236, 180, 255), a));
            }
            if (_flash > 0f)
            {
                float k = _flash;
                Raylib.DrawCircleV(_flashAt, 46f * k, Art.Alpha(new Color(255, 226, 150, 255), 0.75f * k));
                Raylib.DrawCircleV(_flashAt, 96f * k, Art.Alpha(new Color(255, 196, 110, 255), 0.25f * k));
            }

        }
    }
}
