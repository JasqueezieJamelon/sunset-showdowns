using System;
using System.Collections.Generic;
using System.Numerics;
using Raylib_cs;

namespace LLD
{
    /// <summary>
    /// A line to type at, and whatever it has said back.
    ///
    /// Nothing anywhere points at this. There is no prompt on the title screen, no hint in
    /// the settings and no mention of it in anything a player reads: it opens on the tilde
    /// key from the main menu and that is the only way in. Somebody who does not know it is
    /// here will not find it by looking.
    /// </summary>
    public class DevConsole
    {
        public bool Open;
        public string Input = "";
        public float Caret;
        readonly List<string> _log = new();

        public IReadOnlyList<string> Log => _log;

        public const int Keep = 12;

        public void Say(string s)
        {
            foreach (var line in s.Split('\n'))
            {
                _log.Add(line);
                if (_log.Count > Keep) _log.RemoveAt(0);
            }
        }

        public void Wipe() => _log.Clear();

        /// <summary>Everything typed since the last Enter, minus the stray spaces.</summary>
        public string Take()
        {
            string s = Input.Trim();
            Input = "";
            return s;
        }

        /// <summary>Characters, backspace and nothing else. Returns true if Enter was hit.</summary>
        public bool Type(float dt)
        {
            Caret += dt;
            int c;
            while ((c = Raylib.GetCharPressed()) > 0)
            {
                // the key that opens it must never be able to type itself into it
                if (c == '`' || c == '~') continue;
                if (c >= 32 && c < 127 && Input.Length < 40) Input += (char)c;
            }
            if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && Input.Length > 0)
                Input = Input.Substring(0, Input.Length - 1);
            return Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter);
        }
    }

    /// <summary>One thing the test range can do to the man standing in it.</summary>
    public class DevButton
    {
        public string Label;
        public KeyboardKey Key;
        public string KeyName;
        public Action Do;
        public Rectangle Box;
    }
}
