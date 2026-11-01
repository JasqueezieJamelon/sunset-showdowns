using System;
using Raylib_cs;

namespace LLD
{
    public class Palette
    {
        public Color Skin, SkinDark, Hat, HatBand, Shirt, ShirtDark, Vest, VestDark,
                     Pants, PantsDark, Boots, Scarf, Hair, Metal, Wood;
        public Color Hand;          // what the gun hand is drawn in: skin, or a glove
        public bool Gloved;

        public static Palette Make(int skin, Color hat, Color band, Color shirt, Color vest, Color pants, Color boots, Color scarf, Color hair)
        {
            Color[] skins = {
                new Color(236, 190, 150, 255),
                new Color(214, 160, 118, 255),
                new Color(176, 122, 82, 255),
                new Color(132, 88, 58, 255),
                new Color(246, 206, 172, 255),
            };
            var s = skins[skin % skins.Length];
            return new Palette
            {
                Skin = s,
                SkinDark = Art.Shade(s, 0.78f),
                Hat = hat,
                HatBand = band,
                Shirt = shirt,
                ShirtDark = Art.Shade(shirt, 0.76f),
                Vest = vest,
                VestDark = Art.Shade(vest, 0.74f),
                Pants = pants,
                PantsDark = Art.Shade(pants, 0.76f),
                Boots = boots,
                Scarf = scarf,
                Hair = hair,
                Metal = new Color(126, 132, 140, 255),
                Wood = new Color(118, 68, 38, 255),
                Hand = s,
            };
        }
    }

    public class CharDef
    {
        public string Name;
        public string Trade;
        public string Quip;
        public Palette P;
        public int HatStyle;     // 0 wide brim, 1 bowler, 2 flat preacher, 3 bareheaded
        public int HairStyle;    // 0 cropped, 1 swept, 2 slicked, 3 long, 4 shaggy, 5 balding
        public int Face;         // 0 clean, 1 moustache, 2 full beard, 3 stubble
        public int BootStyle;    // 0 plain, 1 tall cuff, 2 spurred
        public bool ScarfOn = true;
        public float ReactMin, ReactMax;   // seconds from DRAW to beginning the pull
        public float AimError;             // radians of aim scatter
        public float Refire;               // seconds between shots
        public float ArmStiff;             // how fast their arm comes up
        public float Height;               // body scale

        // ---- the dials ----
        //
        // Nobody in this game has a special move. Every man waits for the call, draws, aims
        // and fires, exactly as you do. What makes one different from another is where these
        // are set. A man who is quick and wild and a man who is slow and exact are opposite
        // problems for the player without either of them doing anything new.

        public int Skulls = 1;             // what his paper says the day you first see him
        public int SkullCap = 5;           // how bad he is allowed to get if you leave him
        public string Tell = "";           // the rumour on his paper: an honest hint, vaguely put

        /// He answers your draw instead of the call, so he cannot be waited out.
        public bool Mirror;

        /// Nerves. He sometimes goes before the call and hands you the fight.
        public bool FalseStart;

        /// Down is not done: one shot left in a sleeve, from the floor.
        public bool Holdout;

        /// He preaches through the stand-off and the call lands somewhere inside it.
        public bool Sermon;

        /// A hit that does not kill him does not stagger him either, so it buys you nothing.
        public bool NoFlinch;

        /// Where he puts them. Mixed is everybody else.
        public AimAt Aim = AimAt.Mixed;

        public CharDef Clone() => (CharDef)MemberwiseClone();
    }

    /// <summary>Where a man points, which is a dial and not a trick.</summary>
    public enum AimAt
    {
        Mixed,      // head sometimes, chest mostly, a leg or an arm now and then
        Centre,     // the middle of you, every time
        Arm,        // your gun hand: he is not trying to kill you
        Worst,      // whatever is already broken on you
    }

    public static class Roster
    {
        static Color C(int r, int g, int b) => new Color(r, g, b, 255);

        public static readonly CharDef Player = new CharDef
        {
            Name = "You",
            Trade = "drifter",
            P = Palette.Make(0, C(92, 70, 56), C(52, 38, 30), C(176, 196, 206), C(74, 58, 48), C(86, 78, 92), C(70, 48, 34), C(168, 62, 52), C(64, 46, 34)),
            HatStyle = 0,
            Face = 3,
            HairStyle = 1,
            Height = 1f,
            ArmStiff = 1f,
        };

        /// The other side of a two-player match: same build, different rig.
        public static readonly CharDef Challenger = new CharDef
        {
            Name = "Challenger",
            Trade = "drifter",
            P = Palette.Make(2, C(64, 48, 44), C(150, 60, 50), C(206, 178, 132), C(70, 54, 48), C(72, 64, 70), C(58, 42, 32), C(126, 86, 58), C(46, 34, 28)),
            HatStyle = 0,
            Face = 1,
            HairStyle = 4,
            Height = 1f,
            ArmStiff = 1f,
        };

        public static readonly CharDef[] All =
        {
            // Dusty and Marta are the lessons: wait for the call, and finish what you put
            // down. Both are riddles with one answer, which is fine for a man you meet first
            // and never fine for a man you meet late - so neither of them ever ripens past 2.
            new CharDef {
                Name = "Dusty Pell", Trade = "freight hauler", Quip = "I only ever shot at cans.",
                P = Palette.Make(1, C(150, 122, 86), C(84, 64, 44), C(206, 186, 140), C(108, 92, 66), C(96, 86, 72), C(78, 58, 40), C(142, 150, 112), C(96, 72, 44)),
                HatStyle = 0, Face = 3, HairStyle = 4, ReactMin = 0.72f, ReactMax = 0.95f, AimError = 0.085f, Refire = 1.05f, ArmStiff = 0.55f, Height = 1.0f,
                Skulls = 1, SkullCap = 2, FalseStart = true,
                Tell = "they say his nerve goes before the word does" },

            new CharDef {
                Name = "Marta Cruz", Trade = "card dealer", Quip = "The deal was fair. You were not.",
                P = Palette.Make(2, C(48, 40, 52), C(188, 150, 70), C(176, 74, 92), C(54, 44, 58), C(60, 52, 64), C(52, 38, 32), C(228, 196, 110), C(34, 26, 24)),
                HatStyle = 1, Face = 0, HairStyle = 3, ReactMin = 0.60f, ReactMax = 0.82f, AimError = 0.070f, Refire = 0.95f, ArmStiff = 0.68f, Height = 0.96f,
                Skulls = 1, SkullCap = 2, Holdout = true,
                Tell = "they say she never shows you the whole hand" },

            // the call is buried somewhere in the sermon, in a different place every time
            new CharDef {
                Name = "Reverend Hollow", Trade = "circuit preacher", Quip = "I will read for you after.",
                P = Palette.Make(4, C(32, 30, 34), C(22, 20, 24), C(228, 226, 220), C(38, 36, 40), C(40, 38, 44), C(36, 30, 28), C(60, 56, 60), C(206, 202, 194)),
                HatStyle = 2, Face = 2, HairStyle = 5, ReactMin = 0.52f, ReactMax = 0.70f, AimError = 0.058f, Refire = 0.88f, ArmStiff = 0.78f, Height = 1.03f,
                Skulls = 2, Sermon = true,
                Tell = "they say he talks right over the word" },

            // she answers your draw and she shoots the hand, not the man: she is not trying
            // to kill you, she is trying to cripple the rest of your run
            new CharDef {
                Name = "Ida Blackwood", Trade = "town sheriff", Quip = "This street closes at sundown.",
                P = Palette.Make(0, C(104, 84, 62), C(64, 48, 34), C(126, 142, 158), C(88, 72, 54), C(74, 68, 78), C(62, 44, 32), C(96, 118, 130), C(138, 102, 62)),
                HatStyle = 0, Face = 0, HairStyle = 3, ReactMin = 0.30f, ReactMax = 0.40f, AimError = 0.046f, Refire = 0.82f, ArmStiff = 0.92f, Height = 0.98f,
                Skulls = 3, Mirror = true, Aim = AimAt.Arm,
                Tell = "they say she takes men in alive, mostly" },

            // first off the mark and the worst shot in the territory: he will beat you to it
            // and he will not finish you, he will just keep putting holes in you
            new CharDef {
                Name = "Coyote Finn", Trade = "horse thief", Quip = "Your mare liked me better.",
                P = Palette.Make(1, C(122, 96, 58), C(70, 52, 36), C(158, 136, 96), C(92, 108, 78), C(88, 76, 60), C(74, 54, 36), C(176, 118, 60), C(118, 86, 48)),
                HatStyle = 0, Face = 1, HairStyle = 1, ReactMin = 0.21f, ReactMax = 0.30f, AimError = 0.115f, Refire = 0.74f, ArmStiff = 1.1f, Height = 0.95f,
                Skulls = 2,
                Tell = "they say he is quick, and they do not say more than that" },

            // the exact opposite of Finn: all the time in the world, and the middle of you
            new CharDef {
                Name = "Bonesaw Gantry", Trade = "bounty man", Quip = "Paper says dead is fine.",
                P = Palette.Make(3, C(70, 58, 50), C(44, 36, 30), C(104, 86, 74), C(66, 52, 44), C(70, 60, 54), C(50, 38, 30), C(132, 60, 48), C(28, 22, 20)),
                HatStyle = 0, Face = 2, HairStyle = 0, ReactMin = 0.62f, ReactMax = 0.78f, AimError = 0.018f, Refire = 0.80f, ArmStiff = 0.86f, Height = 1.06f,
                Skulls = 3, Aim = AimAt.Centre,
                Tell = "they say he is in no hurry whatsoever" },

            // he does not flinch, so a wounding shot buys you nothing it buys you off anyone else
            new CharDef {
                Name = "The Tinsmith", Trade = "nobody asks", Quip = "",
                P = Palette.Make(4, C(58, 62, 66), C(150, 152, 150), C(86, 92, 96), C(52, 56, 60), C(56, 58, 62), C(44, 42, 44), C(120, 126, 128), C(174, 176, 172)),
                HatStyle = 1, Face = 0, HairStyle = 2, ReactMin = 0.34f, ReactMax = 0.45f, AimError = 0.034f, Refire = 0.64f, ArmStiff = 1.15f, Height = 1.0f,
                Skulls = 4, NoFlinch = true,
                Tell = "they say nothing moves him" },

            // no trick at all. Every roster wants one man who is simply better than you.
            new CharDef {
                Name = "Nine-Finger Quint", Trade = "rail enforcer", Quip = "Lost the finger. Kept the hand.",
                P = Palette.Make(2, C(44, 48, 58), C(126, 96, 52), C(70, 84, 102), C(40, 44, 54), C(48, 50, 58), C(40, 34, 30), C(98, 72, 56), C(40, 32, 26)),
                HatStyle = 0, Face = 1, HairStyle = 1, ReactMin = 0.23f, ReactMax = 0.31f, AimError = 0.030f, Refire = 0.58f, ArmStiff = 1.3f, Height = 1.02f,
                Skulls = 4,
                Tell = "they say he is the fastest hand on this line, and that is all" },

            // Ida taught you the mirror. He has mastered it, and he aims at whatever the rest
            // of them already broke. VANE MUST STAY LAST IN THIS LIST.
            new CharDef {
                Name = "Mordecai Vane", Trade = "last light", Quip = "Hold still. It is quicker.",
                P = Palette.Make(4, C(28, 24, 30), C(140, 30, 40), C(52, 48, 56), C(24, 22, 28), C(30, 28, 34), C(26, 22, 24), C(138, 32, 40), C(214, 210, 200)),
                HatStyle = 2, Face = 1, HairStyle = 2, ReactMin = 0.19f, ReactMax = 0.26f, AimError = 0.022f, Refire = 0.52f, ArmStiff = 1.4f, Height = 1.04f,
                Skulls = 5, Mirror = true, Aim = AimAt.Worst,
                Tell = "" },
        };

        /// <summary>
        /// The same man, turned up to what his paper says today.
        ///
        /// A skull does not give him anything new - there is nothing new to give, because
        /// nobody in this game has a move you do not have. It turns the dials he already has
        /// further: quicker off the mark, truer, less time between shots.
        /// </summary>
        public static CharDef Tune(CharDef d, int skulls)
        {
            int up = Math.Max(0, skulls - d.Skulls);
            var c = d.Clone();
            c.Skulls = skulls;
            if (up == 0) return c;
            float quick = MathF.Pow(0.88f, up);
            c.ReactMin *= quick;
            c.ReactMax *= quick;
            c.AimError *= MathF.Pow(0.87f, up);
            c.Refire *= MathF.Pow(0.93f, up);
            c.ArmStiff *= MathF.Pow(1.06f, up);
            return c;
        }

        static readonly string[] HiredNames = { "a hired man", "another hired man", "the last of them" };

        /// <summary>
        /// Nobody's name is on this one. Somebody has been paying men to come looking for you
        /// and they do not wait for a call; they are a plain shooter, a little better each time.
        /// </summary>
        public static CharDef Hired(int nth)
        {
            int i = Math.Clamp(nth - 1, 0, HiredNames.Length - 1);
            return new CharDef
            {
                Name = HiredNames[i],
                Trade = "no paper on him",
                Quip = "",
                P = Palette.Make(3, C(40, 36, 40), C(70, 30, 34), C(92, 80, 76), C(44, 40, 42), C(52, 48, 50), C(38, 32, 30), C(108, 44, 40), C(34, 28, 24)),
                HatStyle = 0, Face = 2, HairStyle = 0, BootStyle = 1,
                ReactMin = 0.40f - i * 0.06f, ReactMax = 0.54f - i * 0.07f,
                AimError = 0.050f - i * 0.008f, Refire = 0.78f - i * 0.06f,
                ArmStiff = 1.0f + i * 0.1f, Height = 1.0f + i * 0.02f,
                Skulls = 2 + i, SkullCap = 5,
            };
        }
    }
}
