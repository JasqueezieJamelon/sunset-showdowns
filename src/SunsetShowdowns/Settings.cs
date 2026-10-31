using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LLD
{
    public enum GoreLevel { Off = 0, Light = 1, Full = 2 }

    public class Settings
    {
        public bool Fullscreen = false;
        public int WindowW = 1600;
        public int WindowH = 900;
        public float Master = 0.85f;
        public float Sfx = 0.9f;
        public GoreLevel Gore = GoreLevel.Full;
        public bool ShowZones = false;
        public bool AutoUpdate = true;
        public string UpdateUrl = Updater.DefaultFeed;
        public bool VSync = true;
        public int MatchWins = 3;       // rounds a side must win; the host sets it in the lobby

        // who you are online, and what you look like. Name empty means never asked.
        public string Name = "";
        public Loadout Look = Loadout.Default;

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SunsetShowdowns");
        public static string Path_ => Path.Combine(Dir, "settings.ini");

        /// <summary>
        /// Where the game used to keep this, under its old name. Read once, so renaming the
        /// game does not cost anyone the name and the rig they had already chosen.
        /// </summary>
        static string OldPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LastLightDuels", "settings.ini");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                string from = File.Exists(Path_) ? Path_ : OldPath;
                if (!File.Exists(from)) return s;
                foreach (var raw in File.ReadAllLines(from))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "fullscreen": s.Fullscreen = v == "1"; break;
                        case "windowW": s.WindowW = ParseI(v, s.WindowW); break;
                        case "windowH": s.WindowH = ParseI(v, s.WindowH); break;
                        case "master": s.Master = ParseF(v, s.Master); break;
                        case "sfx": s.Sfx = ParseF(v, s.Sfx); break;
                        case "gore": s.Gore = (GoreLevel)Math.Clamp(ParseI(v, 2), 0, 2); break;
                        case "zones": s.ShowZones = v == "1"; break;
                        case "updates": s.AutoUpdate = v == "1"; break;
                        case "updateUrl": s.UpdateUrl = v; break;
                        case "vsync": s.VSync = v == "1"; break;
                        case "name": s.Name = v; break;
                        case "look": s.Look = ParseLook(v); break;
                        case "wins": s.MatchWins = Math.Clamp(ParseI(v, 3), 1, 9); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# Sunset Showdowns");
                sb.AppendLine("fullscreen=" + (Fullscreen ? 1 : 0));
                sb.AppendLine("windowW=" + WindowW);
                sb.AppendLine("windowH=" + WindowH);
                sb.AppendLine("master=" + Master.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("sfx=" + Sfx.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("gore=" + (int)Gore);
                sb.AppendLine("zones=" + (ShowZones ? 1 : 0));
                sb.AppendLine("updates=" + (AutoUpdate ? 1 : 0));
                sb.AppendLine("updateUrl=" + UpdateUrl);
                sb.AppendLine("vsync=" + (VSync ? 1 : 0));
                sb.AppendLine("name=" + Name);
                sb.AppendLine("look=" + LookString());
                sb.AppendLine("wins=" + MatchWins);
                File.WriteAllText(Path_, sb.ToString());
            }
            catch { }
        }

        // the loadout is nine small numbers; one line of them keeps the file readable
        string LookString()
        {
            var sb = new StringBuilder();
            for (int f = 0; f < Loadout.Fields; f++) { if (f > 0) sb.Append(','); sb.Append(Look.Get(f)); }
            return sb.ToString();
        }

        static Loadout ParseLook(string v)
        {
            var l = Loadout.Default;
            var parts = v.Split(',');
            for (int f = 0; f < Loadout.Fields && f < parts.Length; f++)
                l.Set(f, (byte)Math.Clamp(ParseI(parts[f], 0), 0, 255));
            return l.Sane();
        }

        static int ParseI(string v, int d) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : d;
        static float ParseF(string v, float d) => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : d;
    }
}
