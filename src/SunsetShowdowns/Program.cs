using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Raylib_cs;

namespace LLD
{
    public enum Screen { Title, Play, Settings, RunOver, Paused, MpMenu, MpJoin, MpLobby, MpOver, Training, TrainPaused, Name, Customise }

    public static class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();
            // the freshly downloaded build swaps itself in, then hands back to the game
            if (args.Length > 0 && args[0] == "--apply-update")
            {
                Environment.Exit(Updater.ApplyUpdate(args));
                return;
            }
            if (args.Length > 0 && args[0] == "--version")
            {
                Console.WriteLine(App.Version);
                return;
            }

            if (args.Length > 0 && args[0] == "--nettest")
                app.RunNetTest();
            else if (args.Length > 0 && args[0] == "--shots")
                app.RunShots(args.Length > 1 ? args[1] : ".");
            else
                app.Run();
        }
    }

    public class App
    {
        /// Bumped by the last digit on every change. Stays on 0.1.x until told otherwise.
        public const string Version = "0.1.38";

        Settings S;
        RenderTexture2D _scene;
        Screen _screen = Screen.Title;
        Screen _backTo = Screen.Title;

        Duel _duel;
        int _round;
        int _lives;
        float _bestDraw = -1f;
        int _menuIndex;
        int _setIndex;
        float _titleT;

        readonly AmmoWheel _wheel = new AmmoWheel();
        Vector2 _wheelAt = new Vector2(194f, 902f);
        float _wheelR = 92f;

        Vector2 _vmouse;      // cursor in virtual screen space, for menus
        Vector2 _wmouse;      // cursor in world space, through the camera
        Vector2 _aimWorld;    // where the gun is pointed, carried by raw mouse movement
        bool _cursorLocked;

        /// <summary>
        /// While a gun is in his hand the pointer is taken off the desktop and only its movement
        /// is read. A clamped cursor would run out of screen to move across and the aim would
        /// quietly stop following the hand.
        /// </summary>
        void SetCursorLock(bool want)
        {
            if (want == _cursorLocked) return;
            _cursorLocked = want;
            if (want) Raylib.DisableCursor();
            else { Raylib.EnableCursor(); Raylib.HideCursor(); }
        }

        void CarryAim()
        {
            int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
            float scale = MathF.Min(sw / Art.VW, sh / Art.VH);
            float per = MathF.Max(scale * _cam.Zoom, 0.0001f);     // screen pixels to world
            _aimWorld += Raylib.GetMouseDelta() / per;
            _aimWorld.X = Math.Clamp(_aimWorld.X, -2200f, 4200f);
            _aimWorld.Y = Math.Clamp(_aimWorld.Y, -1400f, Scene.GroundY + 500f);
        }
        Camera2D _cam = new Camera2D
        {
            Offset = new Vector2(Art.VW * 0.5f, Art.VH * 0.5f),
            Target = new Vector2(Art.VW * 0.5f, 540f),
            Rotation = 0f,
            Zoom = 1f,
        };
        float _fade;

        const int Lives0 = 3;

        // ---------- lifecycle ----------
        public void Run()
        {
            S = Settings.Load();
            Boot(false);
            NewRun();
            _screen = Screen.Title;

            while (!Raylib.WindowShouldClose())
            {
                float dt = MathF.Min(Raylib.GetFrameTime(), 0.05f);
                Input();
                Update(dt);
                Render();
            }

            S.Save();
            Gore.Unload();
            Raylib.UnloadRenderTexture(_scene);
            Raylib.CloseAudioDevice();
            Raylib.CloseWindow();
        }

        void Boot(bool hidden)
        {
            var flags = ConfigFlags.Msaa4xHint | ConfigFlags.ResizableWindow;
            if (S.VSync) flags |= ConfigFlags.VSyncHint;
            if (hidden) flags |= ConfigFlags.HiddenWindow;
            Raylib.SetConfigFlags(flags);
            Raylib.InitWindow(S.WindowW, S.WindowH, "Sunset Showdowns v" + Version);
            Raylib.SetExitKey(KeyboardKey.Null);
            Raylib.InitAudioDevice();
            Raylib.SetTargetFPS(S.VSync ? 0 : 144);

            Art.Display = LoadFont("Rye-Regular.ttf", 110);
            Art.Body = LoadFont("SpecialElite-Regular.ttf", 72);
            Raylib.SetTextureFilter(Art.Display.Texture, TextureFilter.Bilinear);
            Raylib.SetTextureFilter(Art.Body.Texture, TextureFilter.Bilinear);

            Sfx.Init();
            Sfx.Master = S.Master;
            Sfx.Volume = S.Sfx;
            Gore.Init();
            Gore.Level = S.Gore;
            Gore.Ground = Scene.GroundY;

            if (!hidden && S.AutoUpdate) Updater.CheckInBackground(S.UpdateUrl);

            _scene = Raylib.LoadRenderTexture((int)Art.VW, (int)Art.VH);
            Raylib.SetTextureFilter(_scene.Texture, TextureFilter.Bilinear);
            if (!hidden) ApplyDisplay();
            Raylib.HideCursor();
        }

        static Font LoadFont(string suffix, int size)
        {
            var asm = Assembly.GetExecutingAssembly();
            string name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return Raylib.GetFontDefault();
            using var st = asm.GetManifestResourceStream(name);
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return Raylib.LoadFontFromMemory(".ttf", ms.ToArray(), size, null, 0);
        }

        void ApplyDisplay()
        {
            bool isFull = Raylib.IsWindowFullscreen();
            if (S.Fullscreen && !isFull)
            {
                int m = Raylib.GetCurrentMonitor();
                Raylib.SetWindowSize(Raylib.GetMonitorWidth(m), Raylib.GetMonitorHeight(m));
                Raylib.ToggleFullscreen();
            }
            else if (!S.Fullscreen && isFull)
            {
                Raylib.ToggleFullscreen();
                Raylib.SetWindowSize(S.WindowW, S.WindowH);
            }
        }

        void NewRun()
        {
            _round = 0;
            _lives = Lives0;
            _bestDraw = -1f;
            Gore.Reset();
            _duel = null;
        }

        void StartDuel()
        {
            Gore.Reset();
            _duel = new Duel(Roster.All[Math.Min(_round, Roster.All.Length - 1)], S);
            ResetCamera();
            _screen = Screen.Play;
        }

        // ---------- input ----------
        bool _firePressed, _reloadPressed;

        void Input()
        {
            int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
            float scale = MathF.Min(sw / Art.VW, sh / Art.VH);
            Vector2 off = new Vector2((sw - Art.VW * scale) * 0.5f, (sh - Art.VH * scale) * 0.5f);
            Vector2 m = Raylib.GetMousePosition();
            _vmouse = (m - off) / MathF.Max(scale, 0.0001f);
            _vmouse.X = Math.Clamp(_vmouse.X, 0f, Art.VW);
            _vmouse.Y = Math.Clamp(_vmouse.Y, 0f, Art.VH);
            _wmouse = Raylib.GetScreenToWorld2D(_vmouse, _cam);

            _firePressed = Raylib.IsMouseButtonPressed(MouseButton.Left);
            _reloadPressed = Raylib.IsKeyPressed(KeyboardKey.R);

            if (Raylib.IsKeyPressed(KeyboardKey.F11))
            {
                S.Fullscreen = !S.Fullscreen;
                ApplyDisplay();
            }
        }

        // ---------- update ----------
        void Update(float dt)
        {
            _titleT += dt;
            _fade = MathF.Max(0f, _fade - dt * 2f);

            bool gunInHand = _screen == Screen.Play || _screen == Screen.Training;
            SetCursorLock(gunInHand);
            if (gunInHand) CarryAim();

            switch (_screen)
            {
                case Screen.Title: UpdateTitle(dt); break;
                case Screen.Settings: UpdateSettings(dt); break;
                case Screen.Paused: UpdatePaused(); break;
                case Screen.RunOver: UpdateRunOver(); break;
                case Screen.MpMenu: UpdateMpMenu(); break;
                case Screen.Name: UpdateName(); break;
                case Screen.Customise: UpdateCustomise(); break;
                case Screen.MpJoin: UpdateMpJoin(); break;
                case Screen.MpLobby: UpdateMpLobby(dt); break;
                case Screen.MpOver: UpdateMpOver(); break;
                case Screen.Training: UpdateTraining(dt); break;
                case Screen.TrainPaused: UpdateTrainPaused(); break;
                case Screen.Play:
                    if (_duel != null && _duel.IsNet)
                    {
                        if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { LeaveMatch("you left the match"); break; }
                        if (_duel.AimReset) { _duel.AimReset = false; _aimWorld = _duel.AimResetTo; }
                        _duel.Update(dt, _aimWorld, _firePressed, _reloadPressed);
                        UpdateCamera(dt);
                        UpdateNetPlay(dt);
                        break;
                    }
                    if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _screen = Screen.Paused; _menuIndex = 0; break; }
                    if (_duel.AimReset) { _duel.AimReset = false; _aimWorld = _duel.AimResetTo; }
                    _duel.Update(dt, _aimWorld, _firePressed, _reloadPressed);
                    UpdateCamera(dt);
                    if (_duel.Finished) AfterDuel();
                    break;
            }

            if (_duel != null && _screen != Screen.Play && _screen != Screen.Paused)
                Gore.Update(dt * 0.4f);
        }

        /// <summary>
        /// Frames whatever is happening. As a man is driven down the street the view pulls back
        /// to keep him in it, so nothing ever has to stop at the edge of the screen.
        /// </summary>
        void UpdateCamera(float dt)
        {
            if (_duel == null) return;
            _duel.Interest(out float lo, out float hi);
            CameraTo(lo, hi, dt);
        }

        void CameraTo(float lo, float hi, float dt)
        {
            const float pad = 460f;
            float want = MathF.Max(1740f, (hi - lo) + pad * 2f);
            float zoom = Math.Clamp(Art.VW / want, 0.42f, 1f);
            float cx = (lo + hi) * 0.5f;
            // hold the street line near the bottom of frame whatever the zoom
            float cy = Scene.GroundY - (0.34f * Art.VH) / zoom;

            float k = 1f - MathF.Exp(-4.5f * dt);
            _cam.Zoom = Art.Lerp(_cam.Zoom, zoom, k);
            _cam.Target = Vector2.Lerp(_cam.Target, new Vector2(cx, cy), k);
            _cam.Offset = new Vector2(Art.VW * 0.5f, Art.VH * 0.5f);
        }

        void ResetCamera()
        {
            _cam.Zoom = 1f;
            _cam.Offset = new Vector2(Art.VW * 0.5f, Art.VH * 0.5f);
            _cam.Target = new Vector2((Scene.PlayerStand.X + Scene.EnemyStand.X) * 0.5f, Scene.GroundY - 0.34f * Art.VH);
        }

        void AfterDuel()
        {
            // a draw costs nobody anything: same man, same round, go again
            if (_duel.Drawn) { StartDuel(); _fade = 1f; return; }

            if (_duel.PlayerWon)
            {
                if (_duel.DrawTimeMs > 0f && (_bestDraw < 0f || _duel.DrawTimeMs < _bestDraw)) _bestDraw = _duel.DrawTimeMs;
                _round++;
                if (_round >= Roster.All.Length) { _screen = Screen.RunOver; return; }
                StartDuel();
            }
            else
            {
                _lives--;
                if (_lives <= 0) { _screen = Screen.RunOver; return; }
                StartDuel();
            }
            _fade = 1f;
        }

        // ---------- title ----------
        readonly string[] _mainItems = { "Begin a run", "Multiplayer", "Training", "Customise", "Settings", "Quit" };

        void UpdateTitle(float dt)
        {
            // an update is offered, never forced
            if (Updater.State == UpdateState.Available && Raylib.IsKeyPressed(KeyboardKey.U))
            {
                Sfx.Play("click", 0.8f);
                Updater.DownloadAndApply();
            }
            if (Updater.State == UpdateState.Ready)
            {
                S.Save();
                if (Updater.Handover()) { Raylib.CloseWindow(); Environment.Exit(0); }
            }

            MenuNav(_mainItems.Length, ref _menuIndex, MenuRowY(_mainItems.Length), out bool chose);
            if (chose)
            {
                Sfx.Play("click", 0.8f);
                switch (_menuIndex)
                {
                    case 0: NewRun(); StartDuel(); _fade = 1f; break;
                    case 1:
                        // nobody plays as whatever their Windows account is called: ask once
                        if (string.IsNullOrWhiteSpace(S.Name)) BeginNaming(Screen.MpMenu, true);
                        else { _screen = Screen.MpMenu; _menuIndex = 0; }
                        break;
                    case 2: StartTraining(); break;
                    case 3: _backTo = Screen.Title; _screen = Screen.Customise; _menuIndex = 0; break;
                    case 4: _backTo = Screen.Title; _screen = Screen.Settings; _setIndex = 0; break;
                    default: S.Save(); Environment.Exit(0); break;
                }
            }
        }

        void UpdatePaused()
        {
            string[] items = { "Back to the street", "Settings", "Give up the run" };
            MenuNav(items.Length, ref _menuIndex, MenuRowY(items.Length), out bool chose);
            if (EscBack()) _screen = Screen.Play;
            if (chose)
            {
                Sfx.Play("click", 0.8f);
                if (_menuIndex == 0) _screen = Screen.Play;
                else if (_menuIndex == 1) { _backTo = Screen.Paused; _screen = Screen.Settings; _setIndex = 0; }
                else { _screen = Screen.Title; _menuIndex = 0; NewRun(); }
            }
        }

        void UpdateRunOver()
        {
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) || Raylib.IsKeyPressed(KeyboardKey.Space) || Raylib.IsKeyPressed(KeyboardKey.Enter))
            {
                NewRun();
                _screen = Screen.Title;
                _menuIndex = 0;
            }
        }

        // ---------- two players ----------
        NetSession _net;
        string _joinIp = "127.0.0.1";
        string _joinPort = NetSession.DefaultPort.ToString();
        int _joinField;                // 0 = address, 1 = port
        static readonly Rectangle IpBox = new Rectangle(Art.VW * 0.5f - 330f, 340f, 660f, 80f);
        static readonly Rectangle PortBox = new Rectangle(Art.VW * 0.5f - 330f, 470f, 660f, 80f);
        string _hostAddr = "";
        static readonly Rectangle LanBox = new Rectangle(Art.VW * 0.5f - 360f, 326f, 720f, 80f);
        static readonly Rectangle WanBox = new Rectangle(Art.VW * 0.5f - 360f, 454f, 720f, 80f);
        int _copied;                   // which row was copied: 1 lan, 2 internet
        static readonly Rectangle WinsBox = new Rectangle(Art.VW * 0.5f - 150f, 636f, 300f, 72f);
        static readonly Rectangle WinsLess = new Rectangle(WinsBox.X - 92f, 636f, 76f, 72f);
        static readonly Rectangle WinsMore = new Rectangle(WinsBox.X + WinsBox.Width + 16f, 636f, 76f, 72f);
        float _netNextRound;
        int WinsNeeded => _net != null ? _net.Wins : 3;   // set by the host in the lobby

        // No Back row: the way back is the BACK button under the list, and two of them read
        // as two different things to click.
        readonly string[] _mpItems = { "Host a match", "Join by address", "Name", "Customise" };

        /// The name line carries the name itself, so you can see what you are playing as.
        string[] MpItems()
        {
            var a = (string[])_mpItems.Clone();
            a[2] = "Name: " + MyName;
            return a;
        }

        /// <summary>
        /// Stamp who you are and what you look like onto a session before it talks to anyone.
        /// Both travel in the handshake, so the other man sees the same figure you do.
        /// </summary>
        string MyName => string.IsNullOrWhiteSpace(S.Name) ? "drifter" : S.Name.Trim();

        // ---------- what you are called ----------
        string _nameDraft = "";
        Screen _nameBackTo = Screen.MpMenu;
        bool _nameForced;               // asked on the way in, not chosen from the menu
        static readonly Rectangle NameBox = new Rectangle(Art.VW * 0.5f - 330f, 420f, 660f, 88f);

        void BeginNaming(Screen backTo, bool forced = false)
        {
            _nameDraft = S.Name ?? "";
            _nameBackTo = backTo;
            _nameForced = forced;
            _screen = Screen.Name;
        }

        void UpdateName()
        {
            int c;
            while ((c = Raylib.GetCharPressed()) > 0)
                if (c >= 32 && c <= 126 && _nameDraft.Length < 16) _nameDraft += (char)c;

            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _nameDraft.Length > 0)
                _nameDraft = _nameDraft.Substring(0, _nameDraft.Length - 1);

            bool ok = _nameDraft.Trim().Length > 0;
            bool done = Raylib.IsKeyPressed(KeyboardKey.Enter)
                     || (Raylib.IsMouseButtonPressed(MouseButton.Left) && Raylib.CheckCollisionPointRec(_vmouse, OkBox));

            if (done && ok)
            {
                S.Name = _nameDraft.Trim();
                S.Save();
                Sfx.Play("click", 0.8f);
                if (_net != null) _net.LocalName = MyName;
                _screen = _nameBackTo;
                _menuIndex = 0;
                return;
            }

            // backing out of the first-time prompt leaves multiplayer rather than going nameless
            if (EscBack())
            {
                _screen = _nameForced ? Screen.Title : _nameBackTo;
                _menuIndex = 0;
            }
        }

        static readonly Rectangle OkBox = new Rectangle(Art.VW * 0.5f - 110f, 560f, 220f, 72f);

        void DrawName()
        {
            MpBackdrop("YOUR NAME");
            float cx = Art.VW * 0.5f;
            bool ok = _nameDraft.Trim().Length > 0;

            Raylib.DrawRectangleRec(NameBox, new Color(32, 22, 28, 238));
            Raylib.DrawRectangleLinesEx(NameBox, 2f, new Color(250, 214, 140, 255));
            string shown = _nameDraft + (((int)(Raylib.GetTime() * 2) & 1) == 0 ? "_" : "");
            Art.Text(Art.Body, shown, new Vector2(cx, NameBox.Y + 22f), 44f, new Color(246, 226, 190, 255), true);

            bool hover = Raylib.CheckCollisionPointRec(_vmouse, OkBox);
            Raylib.DrawRectangleRounded(OkBox, 0.3f, 8, ok
                ? (hover ? new Color(252, 222, 158, 255) : new Color(214, 174, 104, 255))
                : new Color(72, 60, 56, 255));
            Art.Text(Art.Body, "DONE", new Vector2(cx, OkBox.Y + 16f), 34f,
                ok ? new Color(34, 22, 20, 255) : new Color(140, 126, 118, 255), true);

            Art.Text(Art.Body, "this is what the other man sees",
                new Vector2(cx, 680f), 28f, new Color(190, 174, 154, 180), true);
            DrawEsc();
        }

        // ---------- how you look ----------
        void UpdateCustomise()
        {
            int n = Loadout.Fields;
            var ys = new float[n];
            for (int i = 0; i < n; i++) ys[i] = 258f + i * 66f;

            for (int i = 0; i < n; i++)
                if (MathF.Abs(_vmouse.Y - (ys[i] + 20f)) < 30f && _vmouse.X > 980f) _menuIndex = i;

            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _menuIndex = (_menuIndex + 1) % n;
            if (Raylib.IsKeyPressed(KeyboardKey.Up)) _menuIndex = (_menuIndex - 1 + n) % n;

            int step = 0;
            if (Raylib.IsKeyPressed(KeyboardKey.Right)) step = 1;
            if (Raylib.IsKeyPressed(KeyboardKey.Left)) step = -1;

            if (EscBack())
            {
                S.Save();
                if (_net != null && _net.Connected) _net.SendLook(S.Look);
                _screen = _backTo;
                _menuIndex = 0;
                return;
            }

            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && _vmouse.X > 980f) step = 1;
            if (step != 0)
            {
                S.Look.Step(_menuIndex, step);
                Sfx.Play("click", 0.5f);
            }
        }

        Fighter _dummy;
        Loadout _dummyLook;

        void DrawCustomise()
        {
            Scene.Draw(0f, Art.VW);
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(16, 10, 14, 205));
            Art.TextInked(Art.Display, "YOUR RIG", new Vector2(Art.VW * 0.5f, 130f), 86f,
                new Color(252, 214, 140, 255), true, 1f, 5f);

            // a stand-in rebuilt only when something actually changed
            if (_dummy == null || !Same(_dummyLook, S.Look))
            {
                _dummyLook = S.Look;
                _dummy = new Fighter(S.Look.ToCharDef(MyName), new Vector2(520f, Scene.GroundY), 1);
            }
            _dummy.Update(0f, _dummy.Arm.HolsterPoint, 0.5f, true);
            _dummy.Draw(false);

            for (int i = 0; i < Loadout.Fields; i++)
            {
                float y = 258f + i * 66f;
                bool on = i == _menuIndex;
                if (on) Raylib.DrawRectangleRec(new Rectangle(980f, y - 6f, 780f, 56f), new Color(250, 214, 140, 26));
                Art.Text(Art.Body, Loadout.FieldNames[i], new Vector2(1010f, y), 34f,
                    on ? new Color(252, 226, 164, 255) : new Color(206, 190, 170, 255));
                Art.Text(Art.Display, S.Look.ValueName(i), new Vector2(1400f, y - 2f), 36f,
                    on ? new Color(252, 214, 140, 255) : new Color(190, 172, 152, 255));
            }

            Art.Text(Art.Body, "click a line, or arrow keys",
                new Vector2(1010f, 258f + Loadout.Fields * 66f + 24f), 28f, new Color(190, 174, 154, 180));
            DrawEsc();
        }

        static bool Same(Loadout a, Loadout b)
        {
            for (int f = 0; f < Loadout.Fields; f++) if (a.Get(f) != b.Get(f)) return false;
            return true;
        }

        void UpdateMpMenu()
        {
            MenuNav(_mpItems.Length, ref _menuIndex, MenuRowY(_mpItems.Length), out bool chose);
            if (EscBack()) { _screen = Screen.Title; _menuIndex = 0; return; }
            if (!chose) return;
            Sfx.Play("click", 0.8f);
            switch (_menuIndex)
            {
                case 0:
                    _net = NetSession.StartHost(NetSession.DefaultPort, MyName, S.Look);
                    _net.Wins = Math.Clamp(S.MatchWins, 1, 9);
                    _hostAddr = NetSession.LocalAddress() + ":" + NetSession.DefaultPort;
                    NetSession.FindPublicAddress();
                    _copied = 0;
                    _screen = Screen.MpLobby;
                    break;
                case 1: _screen = Screen.MpJoin; break;
                case 2: BeginNaming(Screen.MpMenu); break;
                case 3: _backTo = Screen.MpMenu; _screen = Screen.Customise; _menuIndex = 0; break;
            }
        }

        void UpdateMpJoin()
        {
            bool ctrl = Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);

            if (ctrl && Raylib.IsKeyPressed(KeyboardKey.V)) PasteIntoJoin();

            int c;
            while ((c = Raylib.GetCharPressed()) > 0)
            {
                if (ctrl || c < 32 || c > 126) continue;
                if (_joinField == 0) { if (_joinIp.Length < 48) _joinIp += (char)c; }
                else if (c >= '0' && c <= '9' && _joinPort.Length < 5) _joinPort += (char)c;
            }

            if (Raylib.IsKeyPressed(KeyboardKey.Backspace))
            {
                if (_joinField == 0 && _joinIp.Length > 0) _joinIp = _joinIp.Substring(0, _joinIp.Length - 1);
                else if (_joinField == 1 && _joinPort.Length > 0) _joinPort = _joinPort.Substring(0, _joinPort.Length - 1);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Tab) || Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressed(KeyboardKey.Up))
                _joinField ^= 1;

            if (Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                if (Raylib.CheckCollisionPointRec(_vmouse, IpBox)) _joinField = 0;
                else if (Raylib.CheckCollisionPointRec(_vmouse, PortBox)) _joinField = 1;
            }

            if (EscBack()) { _screen = Screen.MpMenu; return; }

            if (Raylib.IsKeyPressed(KeyboardKey.Enter) && _joinIp.Trim().Length > 0)
            {
                if (!int.TryParse(_joinPort, out int port) || port <= 0 || port > 65535) port = NetSession.DefaultPort;
                _net = NetSession.Join(_joinIp.Trim(), port, MyName, S.Look);
                _screen = Screen.MpLobby;
            }
        }

        /// <summary>Takes a pasted "address:port" apart so one paste fills both lines.</summary>
        void PasteIntoJoin()
        {
            string t;
            try { t = Raylib.GetClipboardText_() ?? ""; } catch { return; }
            t = t.Trim();
            if (t.Length == 0) return;

            int colon = t.LastIndexOf(':');
            if (colon > 0 && colon < t.Length - 1 && int.TryParse(t.Substring(colon + 1), out _))
            {
                _joinIp = t.Substring(0, colon).Trim();
                _joinPort = t.Substring(colon + 1).Trim();
                return;
            }

            bool digits = true;
            foreach (char ch in t) if (ch < '0' || ch > '9') { digits = false; break; }
            if (digits && t.Length <= 5) _joinPort = t;
            else _joinIp = t;
        }

        void LeaveMatch(string why)
        {
            _net?.Close();
            _net = null;
            _duel = null;
            _screen = Screen.MpMenu;
            _menuIndex = 0;
            if (!string.IsNullOrEmpty(why)) _netNote = why;
        }

        string _netNote = "";

        void UpdateMpLobby(float dt)
        {
            if (_net == null) { _screen = Screen.MpMenu; return; }
            _net.Poll(dt);
            if (_net.IsHost) NetSession.FindPublicAddress();

            if (EscBack()) { LeaveMatch(""); return; }
            if (!string.IsNullOrEmpty(_net.Error)) { LeaveMatch(_net.Error); return; }


            // click an address to put it on the clipboard, ready to send to whoever is playing
            if (_net.IsHost && !_net.Connected && Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                if (Raylib.CheckCollisionPointRec(_vmouse, LanBox))
                {
                    try { Raylib.SetClipboardText(_hostAddr); _copied = 1; } catch { }
                    Sfx.Play("click", 0.7f);
                    return;
                }
                if (Raylib.CheckCollisionPointRec(_vmouse, WanBox) && NetSession.PublicAddress != null)
                {
                    try { Raylib.SetClipboardText(NetSession.PublicAddress + ":" + NetSession.DefaultPort); _copied = 2; } catch { }
                    Sfx.Play("click", 0.7f);
                    return;
                }
            }

            // the host sets how long the match runs; the client is told and watches it change
            if (_net.IsHost && !_net.LocalReady)
            {
                int step = 0;
                if (Raylib.IsKeyPressed(KeyboardKey.Right) || Raylib.IsKeyPressed(KeyboardKey.Up)) step = 1;
                if (Raylib.IsKeyPressed(KeyboardKey.Left) || Raylib.IsKeyPressed(KeyboardKey.Down)) step = -1;
                if (Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    if (Raylib.CheckCollisionPointRec(_vmouse, WinsLess)) step = -1;
                    else if (Raylib.CheckCollisionPointRec(_vmouse, WinsMore)) step = 1;
                }
                if (step != 0)
                {
                    _net.Wins = Math.Clamp(_net.Wins + step, 1, 9);
                    S.MatchWins = _net.Wins;
                    S.Save();
                    if (_net.Connected) _net.SendWins(_net.Wins);
                    Sfx.Play("click", 0.5f);
                    return;
                }
            }

            if (_net.Connected && !_net.LocalReady &&
                (Raylib.IsKeyPressed(KeyboardKey.Space) || Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsMouseButtonPressed(MouseButton.Left)))
            {
                _net.SendReady(true);
                Sfx.Play("click", 0.8f);
            }

            if (_net.IsHost && _net.Connected && _net.LocalReady && _net.RemoteReady)
            {
                _net.Score[0] = 0; _net.Score[1] = 0;
                _net.RoundIndex = 0;
                StartNetRound();
            }
            else if (!_net.IsHost && _net.RoundStartPending)
            {
                BeginClientRound();
            }
        }

        void StartNetRound()
        {
            Gore.Reset();
            _duel = new Duel(S, _net, _net.LocalSide);
            ResetCamera();
            double drawAt = _net.HostTime + Rng.F(1.8f, 3.4f);
            _net.SendRoundStart(_net.RoundIndex, drawAt, Rng.I(0, int.MaxValue));
            _duel.BeginNetRound(drawAt);
            _screen = Screen.Play;
            _fade = 1f;
        }

        void BeginClientRound()
        {
            _net.RoundStartPending = false;
            Gore.Reset();
            _duel = new Duel(S, _net, _net.LocalSide);
            ResetCamera();
            _duel.BeginNetRound(_net.PendingDrawTime);
            _net.RoundIndex = _net.PendingRound;
            _screen = Screen.Play;
            _fade = 1f;
        }

        /// <summary>Round bookkeeping during a networked duel.</summary>
        void UpdateNetPlay(float dt)
        {
            _net.Poll(dt);

            if (!string.IsNullOrEmpty(_net.Error)) { LeaveMatch(_net.Error); return; }

            if (_net.RoundOverPending)
            {
                _net.RoundOverPending = false;
                _duel.ApplyRoundEnd(_net.RoundWinner, _net.RoundNote, _net.RoundLoserNote);
            }
            if (_net.MatchOverPending)
            {
                _net.MatchOverPending = false;
                _screen = Screen.MpOver;
                return;
            }
            if (!_net.IsHost && _net.RoundStartPending) { BeginClientRound(); return; }

            if (_duel.Finished && _net.IsHost)
            {
                _netNextRound -= dt;
                if (_netNextRound <= 0f)
                {
                    if (_net.Score[0] >= WinsNeeded || _net.Score[1] >= WinsNeeded)
                    {
                        byte winner = (byte)(_net.Score[0] >= WinsNeeded ? 0 : 1);
                        _net.MatchWinner = winner;
                        _net.SendMatchEnd(winner);
                        _screen = Screen.MpOver;
                    }
                    else
                    {
                        _net.RoundIndex++;
                        StartNetRound();
                    }
                }
            }
            else if (_duel.Finished && !_net.IsHost)
            {
                _netNextRound = 2f;   // the host decides; we just wait
            }
            else
            {
                _netNextRound = 2f;
            }
        }

        // ---------- training ----------
        Training _train;

        void StartTraining()
        {
            _train = new Training(S);
            _aimWorld = _train.Player.Arm.Shoulder + new Vector2(340f, 10f);
            ResetCamera();
            _screen = Screen.Training;
            _menuIndex = 0;
            _fade = 1f;
        }

        void UpdateTraining(float dt)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) { _screen = Screen.TrainPaused; _menuIndex = 0; return; }
            _train.Update(dt, _aimWorld, _firePressed, _reloadPressed);
            _train.Interest(out float lo, out float hi);
            CameraTo(lo, hi, dt);
        }

        readonly string[] _trainItems = { "Back to the range", "Set them up again", "Main menu" };

        void UpdateTrainPaused()
        {
            MenuNav(_trainItems.Length, ref _menuIndex, MenuRowY(_trainItems.Length), out bool chose);
            if (EscBack()) { _screen = Screen.Training; return; }
            if (!chose) return;
            Sfx.Play("click", 0.8f);
            switch (_menuIndex)
            {
                case 0: _screen = Screen.Training; break;
                case 1: _train.Reset(); _screen = Screen.Training; _fade = 1f; break;
                case 2: _train = null; _screen = Screen.Title; _menuIndex = 0; break;
            }
        }

        void DrawTrainingHud()
        {
            if (_train == null) return;
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, 92f), new Color(18, 12, 16, 150));

            Art.Text(Art.Display, "TRAINING", new Vector2(Art.VW * 0.5f, 26f), 40f, new Color(250, 214, 140, 255), true);

            string left = _train.Standing + " STANDING";
            Art.Text(Art.Body, left, new Vector2(44f, 36f), 30f, new Color(226, 208, 182, 220));

            string acc = _train.Shots > 0 ? $"{_train.Hits}/{_train.Shots}" : "0/0";
            float aw = Art.Measure(Art.Body, acc, 30f).X;
            Art.Text(Art.Body, acc, new Vector2(Art.VW - 44f - aw, 36f), 30f, new Color(226, 208, 182, 220));

            const float WheelR = 92f;
            Vector2 wheel = new Vector2(194f, Art.VH - 178f);
            _wheel.Update(MathF.Min(Raylib.GetFrameTime(), 0.05f), _train.Ammo, Duel.MaxAmmo, wheel, 1, WheelR, _train.ReloadLeft, Duel.ReloadTime);
            _wheel.Draw(wheel, WheelR, _train.Ammo, Duel.MaxAmmo, _train.ReloadLeft > 0f);
            if (_train.Ammo == 0 && _train.ReloadLeft <= 0f)
                Art.TextInked(Art.Body, "R", new Vector2(wheel.X, wheel.Y - 16f), 44f,
                    new Color(240, 140, 110, 255), true, 1f, 3f);

            if (_train.Standing == 0)
                Art.TextInked(Art.Display, "ALL DOWN", new Vector2(Art.VW * 0.5f, 170f), 86f,
                    new Color(250, 214, 140, 255), true, 1f, 5f);
        }

        void DrawTrainPause()
        {
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(14, 8, 12, 190));
            Art.TextInked(Art.Display, "HOLD", new Vector2(Art.VW * 0.5f, 360f), 120f, new Color(250, 214, 140, 255), true, 1f, 5f);
            var ys = MenuRowY(_trainItems.Length);
            for (int i = 0; i < _trainItems.Length; i++) MenuRow(_trainItems[i], ys[i], i == _menuIndex);
            DrawEsc();
        }

        void UpdateMpOver()
        {
            if (_net == null) { _screen = Screen.MpMenu; return; }
            _net.Poll(0f);
            if (EscBack()) { LeaveMatch(""); return; }
            if (!string.IsNullOrEmpty(_net.Error)) { LeaveMatch(_net.Error); return; }
            if (Raylib.IsKeyPressed(KeyboardKey.Space) || Raylib.IsMouseButtonPressed(MouseButton.Left))
            {
                _net.Score[0] = 0; _net.Score[1] = 0;
                _net.RoundIndex = 0;
                _net.SendReady(false);
                _net.RemoteReady = false;
                _net.LocalReady = false;
                _screen = Screen.MpLobby;
            }
        }

        // ---------- the way back ----------
        // BACK is a button you can click, not a hint printed in the corner. It sits in the same
        // place on every screen that has a way back, and it is lit brightly enough to find.
        /// <summary>
        /// Where the way back sits on the screen you are looking at: directly under the last
        /// thing on it, so it reads as the bottom of the list rather than furniture in a corner.
        /// </summary>
        float EscRowY
        {
            get
            {
                switch (_screen)
                {
                    case Screen.MpMenu: return Last(MenuRowY(_mpItems.Length)) + 86f;
                    case Screen.Paused: return Last(MenuRowY(3)) + 86f;
                    case Screen.TrainPaused: return Last(MenuRowY(_trainItems.Length)) + 86f;
                    case Screen.Settings: return 300f + SetCount * 78f + 16f;
                    case Screen.Customise: return 258f + Loadout.Fields * 66f + 66f;
                    case Screen.MpJoin: return 672f;
                    case Screen.MpOver: return Art.VH - 108f;
                    default: return Art.VH - 230f;      // the lobby
                }
            }
        }

        static float Last(float[] a) => a[a.Length - 1];

        Rectangle EscBox => new Rectangle(Art.VW * 0.5f - 170f, EscRowY - 6f, 340f, 64f);

        /// <summary>
        /// Only the screens that actually draw a way back have one to click. Without this the
        /// box still existed on the title screen, invisible, sitting on the Settings row and
        /// eating the click.
        /// </summary>
        bool HasEsc => _screen == Screen.Settings || _screen == Screen.Customise
                    || _screen == Screen.Name || _screen == Screen.Paused
                    || _screen == Screen.TrainPaused || _screen == Screen.MpMenu
                    || _screen == Screen.MpJoin || _screen == Screen.MpLobby
                    || _screen == Screen.MpOver;

        bool EscHover => HasEsc && Raylib.CheckCollisionPointRec(_vmouse, EscBox);

        /// <summary>True when the player asked to go back, by key or by clicking the button.</summary>
        bool EscBack()
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Escape)) return true;
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && EscHover)
            {
                Sfx.Play("click", 0.8f);
                return true;
            }
            return false;
        }

        /// <summary>The way back, drawn and lit as the last row of whatever menu it sits under.</summary>
        void DrawEsc()
        {
            bool hover = EscHover;
            var box = EscBox;
            if (hover)
                Raylib.DrawRectangleRec(new Rectangle(Art.VW * 0.5f - 420f, box.Y, 840f, 64f),
                    new Color(250, 214, 140, 26));
            Art.Text(Art.Display, "BACK", new Vector2(Art.VW * 0.5f, EscRowY), 48f,
                hover ? new Color(252, 226, 164, 255) : new Color(222, 192, 150, 255), true);
        }

        float[] MenuRowY(int n)
        {
            // a long menu lifts off the bottom of the screen rather than running off it
            const float gap = 86f;
            float top = MathF.Min(620f, Art.VH - 150f - (n - 1) * gap);
            var ys = new float[n];
            for (int i = 0; i < n; i++) ys[i] = top + i * gap;
            return ys;
        }

        void MenuNav(int count, ref int index, float[] ys, out bool chose)
        {
            chose = false;
            for (int i = 0; i < count; i++)
                if (MathF.Abs(_vmouse.Y - (ys[i] + 26f)) < 36f && _vmouse.X > Art.VW * 0.5f - 420f && _vmouse.X < Art.VW * 0.5f + 420f)
                    index = i;

            if (Raylib.IsKeyPressed(KeyboardKey.Down)) index = (index + 1) % count;
            if (Raylib.IsKeyPressed(KeyboardKey.Up)) index = (index - 1 + count) % count;
            // a click on the way-back button is not a click on whatever row the cursor last lit
            if (Raylib.IsMouseButtonPressed(MouseButton.Left) && EscHover) return;

            if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.Space) || Raylib.IsMouseButtonPressed(MouseButton.Left))
                chose = true;
        }

        // ---------- settings ----------
        const int SetCount = 7;

        void UpdateSettings(float dt)
        {
            var ys = new float[SetCount];
            for (int i = 0; i < SetCount; i++) ys[i] = 300f + i * 78f;

            for (int i = 0; i < SetCount; i++)
                if (MathF.Abs(_vmouse.Y - (ys[i] + 24f)) < 34f) _setIndex = i;

            if (Raylib.IsKeyPressed(KeyboardKey.Down)) _setIndex = (_setIndex + 1) % SetCount;
            if (Raylib.IsKeyPressed(KeyboardKey.Up)) _setIndex = (_setIndex - 1 + SetCount) % SetCount;

            int step = 0;
            if (Raylib.IsKeyPressed(KeyboardKey.Right)) step = 1;
            if (Raylib.IsKeyPressed(KeyboardKey.Left)) step = -1;

            bool click = Raylib.IsMouseButtonPressed(MouseButton.Left);
            bool held = Raylib.IsMouseButtonDown(MouseButton.Left);
            float barX = Art.VW * 0.5f + 60f, barW = 420f;
            float slider = Math.Clamp((_vmouse.X - barX) / barW, 0f, 1f);
            bool onBar = _vmouse.X > barX - 20f && _vmouse.X < barX + barW + 20f;

            if (EscBack()) { Apply(); _screen = _backTo; return; }

            switch (_setIndex)
            {
                case 0:
                    if (step != 0 || click) { S.Fullscreen = !S.Fullscreen; ApplyDisplay(); }
                    break;
                case 1:
                    if (step != 0 || (click && !onBar)) S.VSync = !S.VSync;
                    break;
                case 2:
                    if (held && onBar) S.Master = slider;
                    if (step != 0) S.Master = Math.Clamp(S.Master + step * 0.05f, 0f, 1f);
                    Sfx.Master = S.Master;
                    break;
                case 3:
                    if (held && onBar) S.Sfx = slider;
                    if (step != 0) S.Sfx = Math.Clamp(S.Sfx + step * 0.05f, 0f, 1f);
                    Sfx.Volume = S.Sfx;
                    break;
                case 4:
                    if (step != 0 || click) { S.Gore = (GoreLevel)(((int)S.Gore + (step == -1 ? 2 : 1)) % 3); Gore.Level = S.Gore; }
                    break;
                case 5:
                    if (step != 0 || click) S.ShowZones = !S.ShowZones;
                    break;
                case 6:
                    if (step != 0 || click)
                    {
                        S.AutoUpdate = !S.AutoUpdate;
                        if (S.AutoUpdate && Updater.State == UpdateState.Off) Updater.CheckInBackground(S.UpdateUrl);
                        else if (!S.AutoUpdate) Updater.State = UpdateState.Off;
                    }
                    break;
            }

            if (step != 0 || click) Sfx.Play("click", 0.5f);
        }

        void Apply()
        {
            Sfx.Master = S.Master;
            Sfx.Volume = S.Sfx;
            Gore.Level = S.Gore;
            S.Save();
        }

        // ---------- render ----------
        void Render()
        {
            Gore.FlushStamps();

            Raylib.BeginTextureMode(_scene);
            Raylib.ClearBackground(new Color(18, 12, 16, 255));

            switch (_screen)
            {
                case Screen.Title: DrawTitle(); break;
                case Screen.Settings: DrawSettings(); break;
                case Screen.RunOver: DrawRunOver(); break;
                case Screen.MpMenu: DrawMpMenu(); break;
                case Screen.Name: DrawName(); break;
                case Screen.Customise: DrawCustomise(); break;
                case Screen.MpJoin: DrawMpJoin(); break;
                case Screen.MpLobby: DrawMpLobby(); break;
                case Screen.MpOver: DrawMpOver(); break;
                case Screen.Training:
                case Screen.TrainPaused:
                    if (_train != null)
                    {
                        Raylib.BeginMode2D(_cam);
                        Vector2 ta = Raylib.GetScreenToWorld2D(Vector2.Zero, _cam);
                        Vector2 tb = Raylib.GetScreenToWorld2D(new Vector2(Art.VW, Art.VH), _cam);
                        _train.DrawWorld(ta.X, tb.X);
                        Raylib.EndMode2D();
                    }
                    DrawTrainingHud();
                    if (_screen == Screen.TrainPaused) DrawTrainPause();
                    break;
                default:
                    if (_duel != null)
                    {
                        Raylib.BeginMode2D(_cam);
                        Vector2 a = Raylib.GetScreenToWorld2D(Vector2.Zero, _cam);
                        Vector2 b = Raylib.GetScreenToWorld2D(new Vector2(Art.VW, Art.VH), _cam);
                        _duel.DrawWorld(a.X, b.X);
                        Raylib.EndMode2D();
                    }
                    DrawHud();
                    if (_screen == Screen.Paused) DrawPause();
                    break;
            }

            DrawCursor();
            if (_fade > 0f) Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), Art.Alpha(Color.Black, _fade));
            Raylib.EndTextureMode();

            int sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
            float scale = MathF.Min(sw / Art.VW, sh / Art.VH);
            Vector2 off = new Vector2((sw - Art.VW * scale) * 0.5f, (sh - Art.VH * scale) * 0.5f);
            Vector2 shake = Vector2.Zero;
            if (_duel != null && _screen == Screen.Play && _duel.Shake > 0f)
            {
                float k = _duel.Shake * 14f * scale;
                shake = new Vector2(Rng.F(-k, k), Rng.F(-k, k));
            }

            Raylib.BeginDrawing();
            Raylib.ClearBackground(Color.Black);
            Raylib.DrawTexturePro(_scene.Texture,
                new Rectangle(0f, 0f, Art.VW, -Art.VH),
                new Rectangle(off.X + shake.X, off.Y + shake.Y, Art.VW * scale, Art.VH * scale),
                Vector2.Zero, 0f, Color.White);
            Raylib.EndDrawing();
        }

        void DrawCursor()
        {
            Vector2 m = _vmouse;
            Color c = new Color(246, 232, 200, 255);
            // In a fight there is no pointer: the gun is lined up on where you are pointing, so
            // the gun itself is the sight.
            if (_screen == Screen.Play || _screen == Screen.Training) { }
            else
            {
                Raylib.DrawRing(m, 10f, 13f, 0f, 360f, 28, Art.Alpha(c, 0.85f));
                Raylib.DrawCircleV(m, 2.5f, c);
            }
        }

        // ---------- hud ----------
        void DrawHud()
        {
            if (_duel == null) return;

            // top bar
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, 92f), new Color(18, 12, 16, 150));

            // Your own furniture sits on your own side of the street: in a match the man on the
            // right reads his cylinder on the right, and neither of you sees the other's rounds.
            int mySide = _duel.IsNet ? _net.LocalSide : 0;
            int dir = mySide == 0 ? 1 : -1;
            float corner = mySide == 0 ? 44f : Art.VW - 44f;

            // a match is as long as the host said it would be, so the row of skulls is too
            int total = _duel.IsNet ? WinsNeeded : Lives0;
            float skullR = total > 6 ? 12f : 15f;
            int lost = _duel.IsNet ? _net.Score[1 - mySide] : Lives0 - _lives;
            HudBits.Skulls(corner, 46f, total, lost, dir, skullR);

            // down out of the way of the fight, and big enough to read at a glance
            const float WheelR = 92f;
            Vector2 wheel = new Vector2(corner + dir * 150f, Art.VH - 178f);
            _wheel.Update(MathF.Min(Raylib.GetFrameTime(), 0.05f), _duel.Ammo, Duel.MaxAmmo, wheel, dir, WheelR, _duel.ReloadLeft, Duel.ReloadTime);
            _wheel.Draw(wheel, WheelR, _duel.Ammo, Duel.MaxAmmo, _duel.ReloadLeft > 0f);
            _wheelAt = wheel;
            _wheelR = WheelR;

            if (_duel.IsNet)
            {
                float farCorner = mySide == 0 ? Art.VW - 44f : 44f;
                HudBits.Skulls(farCorner, 46f, total, _net.Score[mySide], -dir, skullR);

                string them = (_net.RemoteName ?? "").ToUpperInvariant();
                Art.Text(Art.Display, them, new Vector2(Art.VW * 0.5f, 28f), 40f, new Color(250, 214, 140, 255), true);
                float tw = Art.Measure(Art.Display, them, 40f).X;
                Art.Text(Art.Body, _net.PingMs + " ms", new Vector2(Art.VW * 0.5f + tw * 0.5f + 22f, 36f), 26f, new Color(186, 168, 148, 200));
                DrawHudTail();
                return;
            }

            string name = _duel.Def.Name.ToUpperInvariant();
            Art.Text(Art.Display, name, new Vector2(Art.VW * 0.5f, 28f), 40f, new Color(250, 214, 140, 255), true);
            float nw = Art.Measure(Art.Display, name, 40f).X;
            Art.Text(Art.Body, $"{Math.Min(_round + 1, Roster.All.Length)}/{Roster.All.Length}",
                new Vector2(Art.VW * 0.5f + nw * 0.5f + 22f, 36f), 28f, new Color(186, 168, 148, 220));

            DrawHudTail();
        }

        /// <summary>Ammo, draw time and headline: the same in a run and in a match.</summary>
        void DrawHudTail()
        {
            if (_duel.Ammo == 0 && _duel.ReloadLeft <= 0f)
                Art.TextInked(Art.Body, "R", new Vector2(_wheelAt.X, _wheelAt.Y - 16f), 44f,
                    new Color(240, 140, 110, 255), true, 1f, 3f);

            // the draw time sits above the cylinder, on your own side
            if (_duel.DrawTimeMs > 0f)
                Art.Text(Art.Body, $"{_duel.DrawTimeMs:0} MS",
                    new Vector2(_wheelAt.X, _wheelAt.Y - _wheelR - 60f), 32f, new Color(226, 208, 182, 210), true);

            // headline
            if (!string.IsNullOrEmpty(_duel.Headline))
            {
                float size = _duel.Phase == Phase.Live ? 132f : 86f;
                Color hc = _duel.Phase switch
                {
                    Phase.Live => new Color(255, 238, 198, 255),
                    Phase.Foul => new Color(255, 120, 96, 255),
                    Phase.Resolve => _duel.PlayerWon ? new Color(250, 214, 140, 255) : new Color(255, 120, 96, 255),
                    _ => new Color(246, 226, 190, 255),
                };
                Art.TextInked(Art.Display, _duel.Headline, new Vector2(Art.VW * 0.5f, 170f), size, hc, true, 1f, 5f);
                if (!string.IsNullOrEmpty(_duel.Subline))
                    Art.TextInked(Art.Body, _duel.Subline, new Vector2(Art.VW * 0.5f, 170f + size * 0.95f), 36f, new Color(238, 222, 196, 255), true, 1f, 4f);
            }

            if (_duel.Phase == Phase.Count)
                Art.TextInked(Art.Body, "hold still", new Vector2(Art.VW * 0.5f, Art.VH - 74f),
                    30f, new Color(228, 212, 188, 180), true, 1f, 4f);
        }

        void DrawPause()
        {
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(14, 8, 12, 190));
            Art.TextInked(Art.Display, "HOLD", new Vector2(Art.VW * 0.5f, 360f), 120f, new Color(250, 214, 140, 255), true, 1f, 5f);
            string[] items = { "Back to the street", "Settings", "Give up the run" };
            var ys = MenuRowY(items.Length);
            for (int i = 0; i < items.Length; i++) MenuRow(items[i], ys[i], i == _menuIndex);
            DrawEsc();
        }

        void MenuRow(string label, float y, bool on)
        {
            float cx = Art.VW * 0.5f;
            if (on)
                Raylib.DrawRectangleRec(new Rectangle(cx - 420f, y - 6f, 840f, 64f), new Color(250, 214, 140, 26));
            Art.Text(Art.Display, label, new Vector2(cx, y), 48f,
                on ? new Color(252, 226, 164, 255) : new Color(196, 178, 158, 255), true);
        }

        // ---------- screens ----------
        void DrawTitle()
        {
            Scene.Draw(0f, Art.VW);

            // two silhouetted stand-ins on the street behind the title
            var a = new Fighter(Roster.All[4], Scene.PlayerStand + new Vector2(-60f, 0f), 1);
            var b = new Fighter(Roster.All[8], Scene.EnemyStand + new Vector2(60f, 0f), -1);
            a.Breath = _titleT; b.Breath = _titleT * 0.8f;
            a.Update(0f, a.Arm.HolsterPoint, 0.5f, true);
            b.Update(0f, b.Arm.HolsterPoint, 0.5f, true);
            a.Draw(false); b.Draw(false);

            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(20, 10, 18, 110));

            Art.TextInked(Art.Display, "SUNSET", new Vector2(Art.VW * 0.5f, 170f), 150f, new Color(252, 206, 120, 255), true, 2f, 6f);
            Art.TextInked(Art.Display, "SHOWDOWNS", new Vector2(Art.VW * 0.5f, 320f), 150f, new Color(236, 148, 86, 255), true, 2f, 6f);

            var ys = MenuRowY(_mainItems.Length);
            for (int i = 0; i < _mainItems.Length; i++) MenuRow(_mainItems[i], ys[i], i == _menuIndex);

            string v = "v" + Version;
            float vw = Art.Measure(Art.Body, v, 34f).X;
            Art.TextInked(Art.Body, v, new Vector2(Art.VW - 46f - vw, Art.VH - 74f), 34f,
                new Color(232, 202, 152, 220), false, 1f, 3f);

            string note = Updater.State switch
            {
                UpdateState.Available => "v" + Updater.Message + " is out  -  press U",
                UpdateState.Downloading => $"downloading  {Updater.Progress * 100f:0}%",
                UpdateState.Verifying => "checking the download",
                UpdateState.Ready => "restarting",
                UpdateState.Failed => Updater.Asked ? "update failed: " + Updater.Message : "",
                _ => "",
            };
            if (note.Length > 0)
            {
                Color nc = Updater.State == UpdateState.Failed
                    ? new Color(180, 150, 140, 150)
                    : new Color(250, 214, 140, 230);
                Art.TextInked(Art.Body, note, new Vector2(Art.VW * 0.5f, Art.VH - 150f), 32f, nc, true, 1f, 3f);
            }
        }

        // ---------- two-player screens ----------
        void MpBackdrop(string title)
        {
            Scene.Draw(0f, Art.VW);
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(16, 10, 14, 215));
            Art.TextInked(Art.Display, title, new Vector2(Art.VW * 0.5f, 140f), 92f, new Color(252, 214, 140, 255), true, 1f, 5f);
        }

        void DrawMpMenu()
        {
            MpBackdrop("MULTIPLAYER");
            var items = MpItems();
            var ys = MenuRowY(items.Length);
            for (int i = 0; i < items.Length; i++) MenuRow(items[i], ys[i], i == _menuIndex);
            if (!string.IsNullOrEmpty(_netNote))
                Art.Text(Art.Body, _netNote, new Vector2(Art.VW * 0.5f, Art.VH - 120f), 30f, new Color(236, 140, 118, 220), true);
            DrawEsc();
        }

        void DrawMpJoin()
        {
            MpBackdrop("JOIN");
            float cx = Art.VW * 0.5f;

            Field(IpBox, "ADDRESS", _joinIp, _joinField == 0);
            Field(PortBox, "PORT", _joinPort, _joinField == 1);

            Art.Text(Art.Body, "CTRL+V to paste  -  TAB to switch  -  ENTER to join",
                new Vector2(cx, 600f), 30f, new Color(206, 190, 170, 190), true);
            DrawEsc();
        }

        void Field(Rectangle box, string label, string value, bool focused)
        {
            Raylib.DrawRectangleRec(box, new Color(32, 22, 28, focused ? 238 : 200));
            Raylib.DrawRectangleLinesEx(box, 2f, focused ? new Color(250, 214, 140, 255) : new Color(110, 86, 72, 255));
            Art.Text(Art.Body, label, new Vector2(box.X + 4f, box.Y - 36f), 26f, new Color(190, 174, 154, 210));

            string shown = value;
            if (focused && ((int)(Raylib.GetTime() * 2) & 1) == 0) shown += "_";
            Art.Text(Art.Body, shown, new Vector2(box.X + box.Width * 0.5f, box.Y + 20f), 40f,
                new Color(246, 226, 190, 255), true);
        }

        /// <summary>One address line in the lobby: what it is for, the address, and a copy hint.</summary>
        void AddrRow(Rectangle box, string label, string value, bool copied, bool live = true)
        {
            bool hover = live && Raylib.CheckCollisionPointRec(_vmouse, box);
            Raylib.DrawRectangleRec(box, new Color(32, 22, 28, hover ? 238 : 198));
            Raylib.DrawRectangleLinesEx(box, 2f, !live ? new Color(86, 70, 64, 255)
                : hover ? new Color(255, 238, 190, 255) : new Color(196, 158, 100, 255));
            Art.Text(Art.Body, copied ? label + "  -  COPIED" : label,
                new Vector2(box.X + 2f, box.Y - 34f), 25f,
                copied ? new Color(250, 214, 140, 240) : new Color(196, 180, 160, 215));
            Art.Text(Art.Body, value, new Vector2(box.X + box.Width * 0.5f, box.Y + 19f), 40f,
                live ? new Color(246, 226, 190, 255) : new Color(150, 138, 126, 255), true);
        }

        /// <summary>One of the two steppers beside a number. dir -1 points left, +1 right.</summary>
        void Arrow(Rectangle box, int dir, bool live)
        {
            bool hover = live && Raylib.CheckCollisionPointRec(_vmouse, box);
            Raylib.DrawRectangleRounded(box, 0.3f, 8, live
                ? (hover ? new Color(252, 222, 158, 255) : new Color(58, 40, 42, 255))
                : new Color(40, 30, 34, 200));
            if (!hover)
                Raylib.DrawRectangleRoundedLines(box, 0.3f, 8, live
                    ? new Color(226, 180, 110, 255) : new Color(80, 66, 60, 255));

            float cx = box.X + box.Width * 0.5f, cy = box.Y + box.Height * 0.5f;
            float w = 15f, h = 18f;
            Color tip = hover ? new Color(34, 22, 20, 255)
                      : live ? new Color(252, 214, 140, 255) : new Color(104, 90, 82, 255);
            Art.Poly(new[]
            {
                new Vector2(cx + w * dir, cy),
                new Vector2(cx - w * dir, cy - h),
                new Vector2(cx - w * dir, cy + h),
            }, tip);
        }

        void DrawMpLobby()
        {
            MpBackdrop(_net != null && _net.IsHost ? "HOSTING" : "LOBBY");
            float cx = Art.VW * 0.5f;
            if (_net == null) return;

            if (!_net.Connected)
            {
                if (_net.IsHost)
                {
                    // Two addresses, because they are not interchangeable and sending the wrong
                    // one is the usual reason a match never connects.
                    AddrRow(LanBox, "SAME WIFI", _hostAddr, _copied == 1);

                    string pub = NetSession.PublicAddress;
                    string wan = pub == null
                        ? (NetSession.PublicLookupDone ? "could not look it up" : "looking it up")
                        : pub + ":" + NetSession.DefaultPort;
                    AddrRow(WanBox, "OVER THE INTERNET", wan, _copied == 2, pub != null);

                    Art.Text(Art.Body, "click either one to copy it",
                        new Vector2(cx, WanBox.Y + WanBox.Height + 14f), 26f,
                        new Color(206, 190, 170, 190), true);
                    Art.Text(Art.Body, "playing over the internet needs UDP 27555 forwarded on your router",
                        new Vector2(cx, WanBox.Y + WanBox.Height + 48f), 24f,
                        new Color(196, 180, 160, 150), true);
                }
                else
                {
                    Art.Text(Art.Display, "connecting", new Vector2(cx, 400f), 56f, new Color(246, 226, 190, 255), true);
                    Art.Text(Art.Body, _net.Status, new Vector2(cx, 486f), 30f, new Color(206, 190, 170, 190), true);
                }
            }
            else
            {
                Art.Text(Art.Display, _net.LocalName + "   vs   " + _net.RemoteName,
                    new Vector2(cx, 390f), 52f, new Color(246, 226, 190, 255), true);
                Art.Text(Art.Body, _net.PingMs + " ms", new Vector2(cx, 462f), 28f, new Color(190, 174, 154, 190), true);
                Art.Text(Art.Body, _net.LocalReady ? "waiting for them" : "SPACE when you are ready",
                    new Vector2(cx, 540f), 34f, new Color(250, 214, 140, 230), true);
            }

            // the match length, set by the host and read by both
            if (_net.Wins > 0)
            {
                bool mine = _net.IsHost && !_net.LocalReady;
                Raylib.DrawRectangleRec(WinsBox, new Color(32, 22, 28, mine ? 215 : 150));
                Raylib.DrawRectangleLinesEx(WinsBox, 2f, mine
                    ? new Color(226, 180, 110, 255) : new Color(92, 74, 64, 255));
                Art.Text(Art.Body, "LIVES", new Vector2(WinsBox.X + 26f, WinsBox.Y + 20f), 30f,
                    new Color(206, 190, 170, 220));
                Art.Text(Art.Display, _net.Wins.ToString(),
                    new Vector2(WinsBox.X + WinsBox.Width - 48f, WinsBox.Y + 14f), 44f,
                    new Color(252, 214, 140, 255), true);

                if (mine)
                {
                    Arrow(WinsLess, -1, _net.Wins > 1);
                    Arrow(WinsMore, 1, _net.Wins < 9);
                }
                Art.Text(Art.Body, mine ? "" : "the host decides",
                    new Vector2(cx, WinsBox.Y + WinsBox.Height + 14f), 26f,
                    new Color(190, 174, 154, 170), true);
            }
            DrawEsc();
        }

        void DrawMpOver()
        {
            MpBackdrop("");
            if (_net == null) return;
            bool won = _net.MatchWinner == _net.LocalSide;
            float cx = Art.VW * 0.5f;
            Art.TextInked(Art.Display, won ? "THE STREET IS YOURS" : "HE WALKS AWAY",
                new Vector2(cx, 300f), 104f, won ? new Color(252, 214, 140, 255) : new Color(226, 96, 80, 255), true, 1f, 6f);
            Art.TextInked(Art.Display, _net.Score[_net.LocalSide] + " - " + _net.Score[1 - _net.LocalSide],
                new Vector2(cx, 450f), 72f, new Color(246, 226, 190, 255), true, 1f, 5f);
            Art.Text(Art.Body, "SPACE for another", new Vector2(cx, Art.VH - 160f), 32f, new Color(214, 196, 174, 200), true);
            DrawEsc();
        }

        void DrawSettings()
        {
            Scene.Draw(0f, Art.VW);
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(16, 10, 14, 215));
            Art.TextInked(Art.Display, "SETTINGS", new Vector2(Art.VW * 0.5f, 140f), 96f, new Color(252, 214, 140, 255), true, 1f, 5f);

            string[] labels = { "Fullscreen", "V-Sync", "Master volume", "Sound effects", "Blood", "Show hit zones", "Check for updates" };
            for (int i = 0; i < SetCount; i++)
            {
                float y = 300f + i * 78f;
                bool on = i == _setIndex;
                float lx = Art.VW * 0.5f - 480f;
                if (on) Raylib.DrawRectangleRec(new Rectangle(lx - 30f, y - 8f, 1020f, 62f), new Color(250, 214, 140, 24));
                Art.Text(Art.Body, labels[i], new Vector2(lx, y), 38f, on ? new Color(252, 226, 164, 255) : new Color(206, 190, 170, 255));

                float vx = Art.VW * 0.5f + 60f;
                switch (i)
                {
                    case 0: Value(S.Fullscreen ? "ON" : "OFF", vx, y, on); break;
                    case 1: Value(S.VSync ? "ON" : "OFF", vx, y, on); break;
                    case 2: Bar(S.Master, vx, y, on); break;
                    case 3: Bar(S.Sfx, vx, y, on); break;
                    case 4: Value(S.Gore switch { GoreLevel.Off => "OFF", GoreLevel.Light => "LIGHT", _ => "FULL" }, vx, y, on); break;
                    case 5: Value(S.ShowZones ? "ON" : "OFF", vx, y, on); break;
                    case 6: Value(S.AutoUpdate ? "ON" : "OFF", vx, y, on); break;
                }
            }

            DrawEsc();
        }

        void Value(string v, float x, float y, bool on)
            => Art.Text(Art.Display, v, new Vector2(x, y - 2f), 40f, on ? new Color(252, 214, 140, 255) : new Color(190, 172, 152, 255));

        void Bar(float v, float x, float y, bool on, string note = null)
        {
            float w = 420f, h = 18f;
            Raylib.DrawRectangleRounded(new Rectangle(x, y + 14f, w, h), 0.6f, 8, new Color(60, 46, 44, 255));
            Raylib.DrawRectangleRounded(new Rectangle(x, y + 14f, MathF.Max(10f, w * v), h), 0.6f, 8,
                on ? new Color(252, 206, 120, 255) : new Color(186, 150, 96, 255));
            Raylib.DrawCircleV(new Vector2(x + w * v, y + 23f), 15f, on ? new Color(255, 232, 180, 255) : new Color(200, 180, 150, 255));
            string t = note ?? $"{(int)(v * 100f)}%";
            Art.Text(Art.Body, t, new Vector2(x + w + 40f, y + 4f), 30f, new Color(214, 196, 174, 220));
        }

        void DrawRunOver()
        {
            Scene.Draw(0f, Art.VW);
            Gore.DrawDecals();
            Raylib.DrawRectangleRec(new Rectangle(0f, 0f, Art.VW, Art.VH), new Color(14, 8, 12, 200));

            bool cleared = _round >= Roster.All.Length;
            Art.TextInked(Art.Display, cleared ? "LAST ONE STANDING" : "BURIED AT NOON",
                new Vector2(Art.VW * 0.5f, 300f), cleared ? 108f : 120f,
                cleared ? new Color(252, 214, 140, 255) : new Color(226, 96, 80, 255), true, 1f, 6f);

            if (_bestDraw > 0f)
                Art.TextInked(Art.Display, $"{_bestDraw:0} MS", new Vector2(Art.VW * 0.5f, 470f), 60f,
                    new Color(246, 226, 190, 255), true, 1f, 5f);
        }

        /// <summary>
        /// Drives a host and a client in one process over the loopback and plays a round out, to
        /// prove the handshake, the shared clock, the shot ruling and the round result all work.
        /// </summary>
        public void RunNetTest()
        {
            S = Settings.Load();
            Boot(true);
            void Log(string s) => Console.WriteLine("[nettest] " + s);

            var hostLook = Loadout.Default; hostLook.Hat = 1; hostLook.Gloves = 2; hostLook.Boots = 2;
            var clientLook = Loadout.Default; clientLook.Hat = 2; clientLook.Skin = 3; clientLook.Scarf = 3;
            var host = NetSession.StartHost(NetSession.DefaultPort, "Sheriff", hostLook);
            host.Wins = 5;
            var client = NetSession.Join("127.0.0.1", NetSession.DefaultPort, "Drifter", clientLook);
            const float dt = 1f / 120f;

            for (int i = 0; i < 1200 && !(host.Connected && client.Connected); i++)
            {
                host.Poll(dt); client.Poll(dt);
                System.Threading.Thread.Sleep(2);
            }
            Log("connected host=" + host.Connected + " client=" + client.Connected +
                " side=" + client.LocalSide + " name=" + host.RemoteName);
            if (!host.Connected || !client.Connected) { Log("FAILED to connect"); host.Close(); client.Close(); return; }

            // let the clock settle
            for (int i = 0; i < 300; i++) { host.Poll(dt); client.Poll(dt); System.Threading.Thread.Sleep(3); }
            Log("clock offset settled, ping=" + client.PingMs + "ms");
            Log("names host sees=" + host.RemoteName + " client sees=" + client.RemoteName);
            Log("looks swapped: host got hat=" + host.RemoteLook.Hat + " skin=" + host.RemoteLook.Skin +
                " scarf=" + host.RemoteLook.Scarf + " / client got hat=" + client.RemoteLook.Hat +
                " gloves=" + client.RemoteLook.Gloves + " boots=" + client.RemoteLook.Boots);
            Log("lives: host=" + host.Wins + " client=" + client.Wins);

            var hd = new Duel(S, host, 0);
            var cd = new Duel(S, client, 1);
            double drawAt = host.HostTime + 0.6;
            host.SendRoundStart(0, drawAt, 1);
            hd.BeginNetRound(drawAt);

            for (int i = 0; i < 400 && !client.RoundStartPending; i++) { host.Poll(dt); client.Poll(dt); System.Threading.Thread.Sleep(2); }
            Log("client got round start: " + client.RoundStartPending + " drawAt delta=" +
                Math.Round((client.PendingDrawTime - drawAt) * 1000.0, 2) + "ms");
            cd.BeginNetRound(client.PendingDrawTime);

            Vector2 hHolster = hd.HolsterPoint, cHolster = cd.HolsterPoint;
            bool shoved = false;
            double shovedAt = 0;
            float worstAfter = 0f;
            double liveAt = -1;
            int shots = 0;
            double lastShot = 0;
            int steps = 0;
            while (steps++ < 4000 && !hd.Finished)
            {
                host.Poll(dt); client.Poll(dt);
                double now = host.HostTime;
                Vector2 hAim = hHolster;

                if (hd.Phase == Phase.Live)
                {
                    if (liveAt < 0) liveAt = now;
                    // the host aims at the client's head, and only fires once the arm is up
                    Vector2 head = hd.Enemy.L2W(new Vector2(4f, -398f));
                    hAim = head;   // pull the gun up onto him, as a player would
                }

                bool fireNow = hd.Phase == Phase.Live && liveAt > 0 &&
                               now - liveAt > 0.7 && now - lastShot > 0.45 && shots < 4;
                if (fireNow) { shots++; lastShot = now; }

                hd.Update(dt, hAim, fireNow, false);
                cd.Update(dt, cHolster, false, false);

                // Shove the client's copy of the far man out of pose on purpose. Two machines
                // drift like this for real; the host's snapshot has to drag it back.
                if (!shoved && steps > 150)
                {
                    shoved = true;
                    shovedAt = now;
                    cd.Theirs.PelvisP += new Vector2(0f, -70f);
                    cd.Theirs.ChestP += new Vector2(0f, -70f);
                    cd.Theirs.Kneel += 30f;
                    Log("shoved the client's copy of the far man 70px out of pose");
                }
                else if (shoved && now - shovedAt > 0.05 && now - shovedAt < 1.2)
                    worstAfter = MathF.Max(worstAfter,
                        Vector2.Distance(cd.Theirs.PelvisP, hd.Player.PelvisP));

                System.Threading.Thread.Sleep(1);
            }
            Log("host fired " + shots + " shot(s)");
            Log("after the shove, worst pelvis gap was " + Math.Round(worstAfter) +
                "px; settled at " + Math.Round(Vector2.Distance(cd.Theirs.PelvisP, hd.Player.PelvisP)) + "px");

            Log("host round over: finished=" + hd.Finished + " won=" + hd.PlayerWon +
                " headline=" + hd.Headline + " / " + hd.Subline);
            Log("host score " + host.Score[0] + "-" + host.Score[1]);

            for (int i = 0; i < 400; i++) { host.Poll(dt); client.Poll(dt); cd.Update(dt, cHolster, false, false); System.Threading.Thread.Sleep(2); }
            Log("client score " + client.Score[0] + "-" + client.Score[1] +
                " phase=" + cd.Phase + " enemyBlood=" + Math.Round(cd.Theirs.Blood) + " mineBlood=" + Math.Round(cd.Mine.Blood));

            // a false start has to read as one on BOTH screens
            {
                var hd2 = new Duel(S, host, 0);
                var cd2 = new Duel(S, client, 1);
                client.RoundStartPending = false;      // round one left it set
                double d2 = host.HostTime + 3.0;
                host.SendRoundStart(1, d2, 2);
                hd2.BeginNetRound(d2);
                for (int i = 0; i < 400 && !client.RoundStartPending; i++) { host.Poll(dt); client.Poll(dt); System.Threading.Thread.Sleep(2); }
                cd2.BeginNetRound(client.PendingDrawTime);

                // the client jogs the mouse well before the call
                Vector2 jump = cd2.HolsterPoint + new Vector2(300f, -300f);
                for (int i = 0; i < 500; i++)
                {
                    host.Poll(dt); client.Poll(dt);
                    hd2.Update(dt, hd2.HolsterPoint, false, false);
                    cd2.Update(dt, i < 10 ? cd2.HolsterPoint : jump, false, false);
                    System.Threading.Thread.Sleep(1);
                }
                Log("false start: client sees \"" + cd2.Headline + "\"  host sees \"" + hd2.Headline + "\"");
                Log("false start score " + host.Score[0] + "-" + host.Score[1]);
            }

            // both men dead at once is a draw, and a draw scores for nobody
            {
                int s0 = host.Score[0], s1 = host.Score[1];
                var hd3 = new Duel(S, host, 0);
                var cd3 = new Duel(S, client, 1);
                client.RoundStartPending = false;
                double d3 = host.HostTime + 0.4;
                host.SendRoundStart(2, d3, 3);
                hd3.BeginNetRound(d3);
                for (int i = 0; i < 400 && !client.RoundStartPending; i++) { host.Poll(dt); client.Poll(dt); System.Threading.Thread.Sleep(2); }
                cd3.BeginNetRound(client.PendingDrawTime);

                bool killed = false;
                for (int i = 0; i < 900 && !hd3.Finished; i++)
                {
                    host.Poll(dt); client.Poll(dt);
                    if (!killed && hd3.Phase == Phase.Live)
                    {
                        killed = true;
                        hd3.Player.Killed = true;
                        hd3.Enemy.Killed = true;
                    }
                    hd3.Update(dt, hd3.HolsterPoint, false, false);
                    cd3.Update(dt, cd3.HolsterPoint, false, false);
                    System.Threading.Thread.Sleep(1);
                }
                for (int i = 0; i < 300; i++) { host.Poll(dt); client.Poll(dt); cd3.Update(dt, cd3.HolsterPoint, false, false); System.Threading.Thread.Sleep(2); }
                Log("both dead: host sees \"" + hd3.Headline + "\" drawn=" + hd3.Drawn +
                    "  client sees \"" + cd3.Headline + "\" drawn=" + cd3.Drawn);
                Log("score was " + s0 + "-" + s1 + ", now " + host.Score[0] + "-" + host.Score[1] +
                    " (client " + client.Score[0] + "-" + client.Score[1] + ")");
            }

            // version gate
            Log("protocol=" + NetSession.Protocol + " build=" + Version);

            host.Close(); client.Close();
            Gore.Unload();
            Raylib.UnloadRenderTexture(_scene);
            Raylib.CloseAudioDevice();
            Raylib.CloseWindow();
        }

        // ---------- offscreen capture for development ----------
        public void RunShots(string dir)
        {
            S = Settings.Load();
            S.Fullscreen = false;
            Boot(true);
            Directory.CreateDirectory(dir);
            NewRun();

            void Crop(string name, Vector2 centre, float w, float h)
            {
                var img = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref img);
                Raylib.ImageCrop(ref img, new Rectangle(centre.X - w * 0.5f, centre.Y - h * 0.5f, w, h));
                Raylib.ImageResizeNN(ref img, (int)(w * 2f), (int)(h * 2f));
                Raylib.ExportImage(img, Path.Combine(dir, name + ".png"));
                Raylib.UnloadImage(img);
            }

            void Shot(string name)
            {
                Gore.FlushStamps();
                Raylib.BeginTextureMode(_scene);
                Raylib.ClearBackground(new Color(18, 12, 16, 255));
                switch (_screen)
                {
                    case Screen.Title: DrawTitle(); break;
                    case Screen.Settings: DrawSettings(); break;
                    case Screen.RunOver: DrawRunOver(); break;
                    case Screen.MpMenu: DrawMpMenu(); break;
                case Screen.Name: DrawName(); break;
                case Screen.Customise: DrawCustomise(); break;
                    case Screen.MpJoin: DrawMpJoin(); break;
                    case Screen.MpLobby: DrawMpLobby(); break;
                    case Screen.MpOver: DrawMpOver(); break;
                    case Screen.Training:
                        if (_train != null)
                        {
                            Raylib.BeginMode2D(_cam);
                            Vector2 ta = Raylib.GetScreenToWorld2D(Vector2.Zero, _cam);
                            Vector2 tb = Raylib.GetScreenToWorld2D(new Vector2(Art.VW, Art.VH), _cam);
                            _train.DrawWorld(ta.X, tb.X);
                            Raylib.EndMode2D();
                        }
                        DrawTrainingHud();
                        break;
                    default:
                        if (_duel != null)
                        {
                            Raylib.BeginMode2D(_cam);
                            Vector2 va = Raylib.GetScreenToWorld2D(Vector2.Zero, _cam);
                            Vector2 vb = Raylib.GetScreenToWorld2D(new Vector2(Art.VW, Art.VH), _cam);
                            _duel.DrawWorld(va.X, vb.X);
                            Raylib.EndMode2D();
                        }
                        DrawHud();
                        if (_screen == Screen.Paused) DrawPause();
                        break;
                }
                DrawCursor();
                Raylib.EndTextureMode();
                var img = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref img);
                Raylib.ExportImage(img, Path.Combine(dir, name + ".png"));
                Raylib.UnloadImage(img);
            }

            const float dt = 1f / 120f;

            // The way-back box is invisible but still catches clicks. If it ever lands on a
            // menu row again, that row stops working - which is exactly how Settings broke.
            {
                void Overlap(Screen scr, float[] rows, string what)
                {
                    _screen = scr;
                    if (!HasEsc) { Console.WriteLine("[check] " + what + ": no esc box - ok"); return; }
                    var b = EscBox;
                    foreach (var y in rows)
                        if (y + 26f + 36f > b.Y && y + 26f - 36f < b.Y + b.Height)
                        {
                            Console.WriteLine("[check] FAIL " + what + ": esc box " + (int)b.Y + ".." +
                                (int)(b.Y + b.Height) + " sits on a menu row at " + (int)y);
                            return;
                        }
                    Console.WriteLine("[check] " + what + ": esc clear of every row - ok");
                }
                Overlap(Screen.Title, MenuRowY(_mainItems.Length), "title");
                Overlap(Screen.MpMenu, MenuRowY(_mpItems.Length), "mp menu");
                Overlap(Screen.Paused, MenuRowY(3), "pause");
                Overlap(Screen.TrainPaused, MenuRowY(_trainItems.Length), "training pause");
            }

            _screen = Screen.Title;
            _vmouse = new Vector2(960f, 646f);
            Shot("01-title");

            _screen = Screen.Settings;
            _setIndex = 5;
            Shot("02-settings");
            _vmouse = new Vector2(EscBox.X + EscBox.Width * 0.5f, EscBox.Y + EscBox.Height * 0.5f);
            Shot("02b-settings-esc-hover");
            _vmouse = new Vector2(960f, 646f);

            // duel: card -> holster -> draw -> aim -> fire
            StartDuel();
            _vmouse = _duel.HolsterPoint;
            for (float t = 0f; t < 2.4f; t += dt) _duel.Update(dt, _vmouse, false, false);
            Shot("03-card");

            _screen = Screen.Paused; _menuIndex = 0;
            Shot("03b-paused");
            _screen = Screen.Play;

            for (float t = 0f; t < 0.4f; t += dt) _duel.Update(dt, _vmouse, false, false);
            Shot("04-holster");
            Crop("04b-holster", _duel.Player.L2W(new System.Numerics.Vector2(28f, -192f)), 300f, 220f);

            for (float t = 0f; t < 4.0f && _duel.Phase != Phase.Live; t += dt) _duel.Update(dt, _vmouse, false, false);
            Shot("05-draw");

            // aim at the head: put the hand where the forearm lines up with it
            Vector2 head = _duel.Enemy.L2W(new Vector2(4f, -396f));
            Vector2 aim = head;
            for (float t = 0f; t < 0.45f; t += dt) _duel.Update(dt, aim, false, false);
            Shot("06-aim");
            Crop("06b-hand", _duel.Player.Arm.Hand, 340f, 240f);
            Crop("06e-gun", _duel.Player.Arm.Hand + new Vector2(_duel.Player.Face * 40f, 0f), 210f, 160f);
            // the gun hand on its own, large and level: the only way to judge the art on it
            {
                var pl = _duel.Player;
                Raylib.BeginTextureMode(_scene);
                Raylib.ClearBackground(new Color(224, 216, 202, 255));
                Vector2 gat = new Vector2(640f, 560f);
                Art.InkPass = true; pl.DrawRevolver(gat, 0f, pl.P, 6f, true, 1f, 0f); Art.InkPass = false;
                pl.DrawRevolver(gat, 0f, pl.P, 6f, true, 1f, 0f);
                Raylib.EndTextureMode();
                var gi = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref gi);
                Raylib.ImageCrop(ref gi, new Rectangle(300f, 280f, 1120f, 620f));
                Raylib.ExportImage(gi, Path.Combine(dir, "06f-gunplate.png"));
                Raylib.UnloadImage(gi);
            }
            // the action through one shot: cocked, dropped, and the cylinder a third and a
            // whole chamber round behind it
            {
                var pl = _duel.Player;
                Raylib.BeginTextureMode(_scene);
                Raylib.ClearBackground(new Color(224, 216, 202, 255));
                float[] ham = { 1f, 0f, 0.4f, 1f };
                float[] cyl = { 0f, 0f, ArmRig.Chamber * 0.4f, ArmRig.Chamber };
                for (int i = 0; i < 4; i++)
                {
                    Vector2 p = new Vector2(300f + (i % 2) * 700f, 300f + (i / 2) * 420f);
                    Art.InkPass = true; pl.DrawRevolver(p, 0f, pl.P, 3.2f, false, ham[i], cyl[i]); Art.InkPass = false;
                    pl.DrawRevolver(p, 0f, pl.P, 3.2f, false, ham[i], cyl[i]);
                }
                Raylib.EndTextureMode();
                var ai = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref ai);
                Raylib.ImageCrop(ref ai, new Rectangle(180f, 160f, 1300f, 700f));
                Raylib.ExportImage(ai, Path.Combine(dir, "06g-action.png"));
                Raylib.UnloadImage(ai);
            }
            S.ShowZones = true; Shot("06c-zones"); S.ShowZones = false;
            { int keep = _lives; _lives = 1; Shot("06d-lives"); _lives = keep; }

            // Putting it away: a slow straight drag down to the leather should stow it, and a
            // fast flick down the same path should not. On a duel of its own, with the other man
            // held off the trigger, so a stray bullet cannot decide the answer.
            {
                var hd = new Duel(Roster.All[0], S);
                Vector2 hip = hd.HolsterPoint;
                Vector2 above = hip - new Vector2(0f, 180f);
                void Step(Vector2 m)
                {
                    hd.Enemy.Disarmed = true; hd.Enemy.DisarmTimer = 9f;
                    hd.Update(dt, m, false, false);
                }
                // waggle it sideways over the leather until it is out: sideways is never the
                // put-away move, so this can only ever draw
                bool Arm_()
                {
                    for (float t = 0f; t < 8f && hd.Phase != Phase.Live; t += dt) Step(hip);
                    for (int i = 0; i < 500 && !hd._drawnOut; i++)
                        Step(above + new Vector2(i % 2 == 0 ? 0f : 16f, 0f));
                    for (float t = 0f; t < 0.25f; t += dt) Step(above);
                    return hd._drawnOut;
                }
                bool Drag(float seconds)
                {
                    int steps = Math.Max(1, (int)(seconds / dt));
                    for (int i = 1; i <= steps; i++)
                    {
                        Step(Vector2.Lerp(above, hip, i / (float)steps));
                        if (!hd._drawnOut) return true;
                    }
                    return false;
                }
                bool armed = Arm_();
                bool slow = Drag(1.2f);
                // straight after stowing it, swinging the hand about must not pick it back up
                bool grabbedBack = false;
                for (float t = 0f; t < Duel.DrawLock * 0.8f; t += dt)
                {
                    Step(above + new Vector2(t * 400f, 0f));
                    if (hd._drawnOut) { grabbedBack = true; break; }
                }
                bool redrew = Arm_();
                bool fast = Drag(0.1f);
                bool ok = armed && slow && !grabbedBack && redrew && !fast;
                Console.WriteLine($"[check] holster: out={armed} slow drag stows={slow}" +
                    $" stays put after={!grabbedBack} draws again={redrew} fast flick stows={fast}" +
                    (ok ? " - ok" : "  FAIL"));

                // the action: the shot drops the hammer, then it comes back as the cylinder
                // carries the next round round under it
                Arm_();
                var arm = hd.Mine.Arm;
                float cylWas = arm.Cylinder, hamWas = arm.Hammer;
                hd.Update(dt, above, true, false);
                float hamFell = arm.Hammer;
                for (float t = 0f; t < 0.6f; t += dt) Step(above);
                float turned = (arm.Cylinder - cylWas) / ArmRig.Chamber;
                bool actOk = hamWas > 0.9f && hamFell < 0.1f && arm.Hammer > 0.9f
                             && MathF.Abs(turned - 1f) < 0.01f;
                Console.WriteLine($"[check] action: hammer {hamWas:0.00} -> {hamFell:0.00} ->" +
                    $" {arm.Hammer:0.00}, cylinder turned {turned:0.000} chambers" +
                    (actOk ? " - ok" : "  FAIL"));
            }

            _duel.Update(dt, aim, true, false);
            for (float t = 0f; t < 0.12f; t += dt) _duel.Update(dt, aim, false, false);
            // heads on their own: a cheek shot, an eye taken out, and the top of the skull off
            {
                var e = _duel.Enemy;
                float keepScale = e.Scale;
                int kd = e.HeadDamage; float ka = e.HeadWoundAngle; bool ke = e.EyeOut;
                e.Scale = 3.4f;
                Raylib.BeginTextureMode(_scene);
                Raylib.ClearBackground(new Color(224, 216, 202, 255));
                float[] angs = { 0.15f, 2.6f, 1.5f };
                int[] dmg = { 1, 2, 2 };
                bool[] eyes = { false, true, true };
                for (int i = 0; i < 3; i++)
                {
                    e.HeadDamage = dmg[i]; e.HeadWoundAngle = angs[i]; e.EyeOut = eyes[i];
                    Vector2 hp = new Vector2(380f + i * 420f, 560f);
                    Art.InkPass = true; e.DrawHeadOnly(hp, false); Art.InkPass = false;
                    e.DrawHeadOnly(hp, false);
                }
                Raylib.EndTextureMode();
                var hi = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref hi);
                Raylib.ImageCrop(ref hi, new Rectangle(200f, 340f, 1460f, 440f));
                Raylib.ExportImage(hi, Path.Combine(dir, "07c-headplate.png"));
                Raylib.UnloadImage(hi);
                e.Scale = keepScale; e.HeadDamage = kd; e.HeadWoundAngle = ka; e.EyeOut = ke;
            }

            Shot("07-impact");
            Crop("07b-head", _duel.Enemy.HeadCenter, 280f, 230f);
            Console.WriteLine($"[check] head dmg={_duel.Enemy.HeadDamage} eyeOut={_duel.Enemy.EyeOut} jaw={_duel.Enemy.JawGone}");

            // the fall, frame by frame
            string[] stops = { "08-fall-a", "09-fall-b", "10-fall-c", "11-settled" };
            float[] waits = { 0.25f, 0.35f, 0.5f, 2.4f };
            for (int i = 0; i < stops.Length; i++)
            {
                for (float t = 0f; t < waits[i]; t += dt) { _duel.Update(dt, aim, false, false); UpdateCamera(dt); }
                Shot(stops[i]);
            }

            // second duel: a lung shot, to watch the stagger and the bleed-out
            StartDuel();
            _vmouse = _duel.HolsterPoint;
            for (float t = 0f; t < 2.4f; t += dt) _duel.Update(dt, _vmouse, false, false);
            for (float t = 0f; t < 6.0f && _duel.Phase != Phase.Live; t += dt) _duel.Update(dt, _vmouse, false, false);
            Vector2 chest = _duel.Enemy.Arm.Elbow;
            Vector2 aim2 = chest;
            for (float t = 0f; t < 0.5f; t += dt) _duel.Update(dt, aim2, false, false);
            _duel.Update(dt, aim2, true, false);
            string[] lung = { "12-gut-hit", "13-gut-stumble", "14-gut-stumble2", "15-gut-after" };
            float[] lw = { 0.14f, 0.22f, 0.34f, 0.55f };
            for (int i = 0; i < lung.Length; i++)
            {
                for (float t = 0f; t < lw[i]; t += dt) { _duel.Update(dt, aim2, false, false); UpdateCamera(dt); }
                Shot(lung[i]);
            }
            Crop("13b-head", _duel.Enemy.Doll.Active ? _duel.Enemy.Doll.Pos[0] : _duel.Enemy.L2W(new Vector2(4f, -398f)), 420f, 300f);

            S.Name = "Jasqueezie";
            S.Look = Loadout.Default; S.Look.Hat = 2; S.Look.Gloves = 1; S.Look.Boots = 2;
            S.Look.Scarf = 3; S.Look.Shirt = 2; S.Look.Skin = 2;
            {
                // the whole roster in a line: every face, hat and beard, to check they all
                // sit on a neck rather than floating clear of the collar
                Raylib.BeginTextureMode(_scene);
                Raylib.ClearBackground(new Color(214, 206, 196, 255));
                for (int i = 0; i < Roster.All.Length; i++)
                {
                    var f = new Fighter(Roster.All[i], new Vector2(150f + i * 196f, Scene.GroundY - 170f), 1);
                    f.Update(0f, f.Arm.HolsterPoint, 0.5f, true);
                    f.Draw(false);
                }
                Raylib.EndTextureMode();
                var ri = Raylib.LoadImageFromTexture(_scene.Texture);
                Raylib.ImageFlipVertical(ref ri);
                Raylib.ImageCrop(ref ri, new Rectangle(40f, 300f, 1840f, 440f));
                Raylib.ExportImage(ri, Path.Combine(dir, "26-roster.png"));
                Raylib.UnloadImage(ri);
            }

            _screen = Screen.Customise; _menuIndex = 6; _vmouse = new Vector2(1200f, 660f);
            Shot("19-customise");
            _nameDraft = "Jasqueezie"; _screen = Screen.Name; _vmouse = new Vector2(960f, 596f);
            Shot("20-name");

            _net = NetSession.StartHost(NetSession.DefaultPort, MyName, S.Look);
            _net.Wins = 5;
            _hostAddr = NetSession.LocalAddress() + ":" + NetSession.DefaultPort;
            NetSession.FindPublicAddress();
            for (int i = 0; i < 60 && !NetSession.PublicLookupDone; i++) System.Threading.Thread.Sleep(100);
            _screen = Screen.MpLobby; _vmouse = new Vector2(WinsMore.X + 38f, 672f); Shot("17-lobby");
            _copied = 2; Shot("18-lobby-copied");

            {
                // the row of skulls has to be as long as the match the host set up
                var keepDuel = _duel;
                _net.Wins = 7;
                _net.Score[0] = 2; _net.Score[1] = 1;
                _duel = new Duel(S, _net, 0);
                _screen = Screen.Play;
                Shot("25-net-lives");
                _duel = keepDuel;
            }
            _net.Close(); _net = null;
            // a man who goes down but does not die must still end the round
            {
                float t = 0f;
                while (t < 12f && !_duel.Finished) { _duel.Update(dt, aim2, false, false); t += dt; }
                Console.WriteLine($"[check] downed-not-dead round resolved={_duel.Finished} after {t:0.0}s " +
                                  $"enemyDown={_duel.Enemy.Down} enemyDead={_duel.Enemy.Dead} blood={_duel.Enemy.Blood:0}");
            }

            // the range: line up on a bottle and break it
            StartTraining();
            for (int i = 0; i < 90; i++) { _train.Update(dt, _train.Player.Arm.HolsterPoint, false, false); _train.Interest(out float tl, out float th); CameraTo(tl, th, dt); }
            Vector2 bottle = _train.Bottles[3].Foot + new Vector2(0f, -36f);
            Vector2 aim3 = bottle;   // the cursor goes straight on the target now
            for (int i = 0; i < 70; i++) { _train.Update(dt, aim3, false, false); _train.Interest(out float tl, out float th); CameraTo(tl, th, dt); }
            _train.Update(dt, aim3, true, false);
            for (int i = 0; i < 24; i++) _train.Update(dt, aim3, false, false);
            _vmouse = aim3;
            Shot("21-training");

            // cursor brought in close: the elbow should fold
            Vector2 near = _train.Player.Arm.Shoulder + new Vector2(96f, 40f);
            for (int i = 0; i < 80; i++) _train.Update(dt, near, false, false);
            _vmouse = near;
            Shot("22-elbow-near");

            // and pushed back out: the arm should straighten
            Vector2 far = _train.Player.Arm.Shoulder + new Vector2(900f, 20f);
            for (int i = 0; i < 80; i++) _train.Update(dt, far, false, false);
            _vmouse = far;
            Shot("23-elbow-far");

            // part way through a reload, a round on its way into the cylinder
            _train.Update(dt, far, false, true);
            for (int i = 0; i < 62; i++) _train.Update(dt, far, false, false);
            Shot("24-reload");

            _screen = Screen.MpJoin; _joinField = 0; _joinIp = "10.0.0.242"; _vmouse = new Vector2(1700f, 900f); Shot("20-join");
            _screen = Screen.Title; _menuIndex = 1; _vmouse = new Vector2(960f, 706f); Shot("19-menu");
            _screen = Screen.MpMenu; _menuIndex = 0; _vmouse = new Vector2(1700f, 1000f); Shot("19b-mp-menu");

            _round = 4; _lives = 1; _bestDraw = 268f;
            _screen = Screen.RunOver;
            Shot("16-runover");

            Gore.Unload();
            Raylib.UnloadRenderTexture(_scene);
            Raylib.CloseAudioDevice();
            Raylib.CloseWindow();
        }
    }
}
