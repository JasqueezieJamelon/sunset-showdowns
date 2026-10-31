using Raylib_cs;

namespace LLD
{
    /// <summary>
    /// What a man looks like, stored as plain indices into the tables below. The art each index
    /// selects is deliberately thin for now - the point is the system around it, so the look can
    /// be redrawn without touching the storage, the menu, or the wire format. Adding a style
    /// means adding one entry here and one case in the drawing code; nothing else changes.
    /// </summary>
    public struct Loadout
    {
        public byte Skin, Hat, Face, Hair, Cut, Shirt, Pants, Boots, Gloves, Scarf;

        public const int Fields = 10;

        public static Loadout Default => new Loadout
        {
            Skin = 0, Hat = 0, Face = 3, Hair = 0, Cut = 0, Shirt = 0, Pants = 0, Boots = 0, Gloves = 0, Scarf = 1,
        };

        static Color C(int r, int g, int b) => new Color(r, g, b, 255);

        // ---- the tables. Names are what the customise screen shows. ----
        public static readonly string[] SkinNames = { "Fair", "Tan", "Brown", "Dark", "Pale" };
        public static readonly string[] HatNames = { "Wide brim", "Bowler", "Preacher", "Bareheaded" };
        public static readonly string[] FaceNames = { "Clean", "Moustache", "Full beard", "Stubble" };
        public static readonly string[] BootNames = { "Plain", "Tall cuff", "Spurred" };
        public static readonly string[] GloveNames = { "Bare hands", "Worn leather", "Black" };

        public static readonly string[] HairNames = { "Brown", "Black", "Sandy", "Grey", "Red" };
        public static readonly string[] CutNames = { "Cropped", "Swept", "Slicked", "Long", "Shaggy", "Balding" };
        public static readonly Color[] HairColours =
            { C(64, 46, 34), C(28, 22, 20), C(138, 102, 62), C(206, 202, 194), C(146, 52, 34) };

        public static readonly string[] ShirtNames = { "Slate", "Bone", "Wine", "Steel", "Sand", "White" };
        public static readonly Color[] ShirtColours =
            { C(176, 196, 206), C(206, 186, 140), C(176, 74, 92), C(126, 142, 158), C(158, 136, 96), C(228, 226, 220) };

        public static readonly string[] PantsNames = { "Indigo", "Charcoal", "Dust", "Navy", "Olive" };
        public static readonly Color[] PantsColours =
            { C(86, 78, 92), C(60, 52, 56), C(96, 86, 72), C(48, 50, 58), C(74, 78, 62) };

        public static readonly string[] ScarfNames = { "None", "Red", "Blue", "Gold", "Green" };
        public static readonly Color[] ScarfColours =
            { C(0, 0, 0), C(168, 62, 52), C(96, 118, 130), C(228, 196, 110), C(142, 150, 112) };

        public static readonly Color[] GloveColours = { C(0, 0, 0), C(142, 104, 64), C(48, 42, 44) };

        /// <summary>How many choices each field has, in the order the menu lists them.</summary>
        public static int Count(int field) => field switch
        {
            0 => HatNames.Length,
            1 => FaceNames.Length,
            2 => SkinNames.Length,
            3 => HairNames.Length,
            4 => CutNames.Length,
            5 => ShirtNames.Length,
            6 => PantsNames.Length,
            7 => BootNames.Length,
            8 => GloveNames.Length,
            _ => ScarfNames.Length,
        };

        public static readonly string[] FieldNames =
            { "Hat", "Face", "Skin", "Hair colour", "Hair style", "Shirt", "Trousers", "Boots", "Gloves", "Neckerchief" };

        public byte Get(int field) => field switch
        {
            0 => Hat, 1 => Face, 2 => Skin, 3 => Hair, 4 => Cut, 5 => Shirt,
            6 => Pants, 7 => Boots, 8 => Gloves, _ => Scarf,
        };

        public void Set(int field, byte v)
        {
            switch (field)
            {
                case 0: Hat = v; break;
                case 1: Face = v; break;
                case 2: Skin = v; break;
                case 3: Hair = v; break;
                case 4: Cut = v; break;
                case 5: Shirt = v; break;
                case 6: Pants = v; break;
                case 7: Boots = v; break;
                case 8: Gloves = v; break;
                default: Scarf = v; break;
            }
        }

        /// <summary>Step one field around its ring of choices.</summary>
        public void Step(int field, int by)
        {
            int n = Count(field);
            Set(field, (byte)(((Get(field) + by) % n + n) % n));
        }

        public string ValueName(int field) => field switch
        {
            0 => HatNames[Hat % HatNames.Length],
            1 => FaceNames[Face % FaceNames.Length],
            2 => SkinNames[Skin % SkinNames.Length],
            3 => HairNames[Hair % HairNames.Length],
            4 => CutNames[Cut % CutNames.Length],
            5 => ShirtNames[Shirt % ShirtNames.Length],
            6 => PantsNames[Pants % PantsNames.Length],
            7 => BootNames[Boots % BootNames.Length],
            8 => GloveNames[Gloves % GloveNames.Length],
            _ => ScarfNames[Scarf % ScarfNames.Length],
        };

        /// <summary>Clamp anything that arrived off the wire or out of an edited settings file.</summary>
        public Loadout Sane()
        {
            var l = this;
            for (int f = 0; f < Fields; f++) l.Set(f, (byte)(l.Get(f) % Count(f)));
            return l;
        }

        public CharDef ToCharDef(string name)
        {
            var l = Sane();
            var p = Palette.Make(l.Skin,
                C(92, 70, 56), C(52, 38, 30),
                ShirtColours[l.Shirt],
                C(74, 58, 48),
                PantsColours[l.Pants],
                C(70, 48, 34),
                ScarfColours[l.Scarf],
                HairColours[l.Hair]);
            p.Gloved = l.Gloves != 0;
            p.Hand = p.Gloved ? GloveColours[l.Gloves] : p.Skin;
            return new CharDef
            {
                Name = name,
                Trade = "drifter",
                P = p,
                HatStyle = l.Hat,
                HairStyle = l.Cut,
                Face = l.Face,
                BootStyle = l.Boots,
                ScarfOn = l.Scarf != 0,
                Height = 1f,
                ArmStiff = 1f,
            };
        }
    }
}
