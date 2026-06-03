using System;
using System.Collections.Generic;

namespace Winland;

[Flags]
internal enum BindModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4
}

/// <summary>
/// The action to run when a bind fires, as the raw text after the comma. Resolved at execution time:
/// if its first token names a "&lt;token&gt;.ps1" script next to the exe, that script is run with the
/// remaining text as arguments; otherwise the whole text is executed as a command line.
/// </summary>
internal sealed record BindAction(string Command);

/// <summary>A configured shortcut. SUPER (the Win key) is implicit/required; Modifiers holds the
/// extra Shift/Alt/Ctrl state, Vk is the virtual-key code of the main key.</summary>
internal sealed record KeyBind(BindModifiers Modifiers, int Vk, BindAction Action);

/// <summary>
/// Interprets the "bind" entries of the global <see cref="Config"/>. Each bind value looks like:
///   SUPER SHIFT B, launch-or-focus firefox
/// Everything before the first comma is the key combo (space-separated, last token = key); the rest
/// is the action (a "&lt;verb&gt;.ps1" script or a bare command). File reading lives in <see cref="Config"/>.
/// </summary>
internal static class HotkeyConfig
{
    /// <summary>Parse the "bind = ..." entries of the config into key binds, in file order.</summary>
    public static List<KeyBind> Parse(Config config)
    {
        var binds = new List<KeyBind>();

        foreach (string value in config.ValuesOf("bind"))
        {
            if (TryParseBind(value, out KeyBind? bind))
            {
                binds.Add(bind!);
            }
            else
            {
                Log($"ignored bind: {value}");
            }
        }

        return binds;
    }

    private static bool TryParseBind(string value, out KeyBind? bind)
    {
        bind = null;

        int comma = value.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        string comboStr = value[..comma].Trim();
        string actionStr = value[(comma + 1)..].Trim();
        if (comboStr.Length == 0 || actionStr.Length == 0)
        {
            return false;
        }

        if (!TryParseCombo(comboStr, out BindModifiers mods, out int vk))
        {
            return false;
        }

        bind = new KeyBind(mods, vk, ParseAction(actionStr));
        return true;
    }

    private static bool TryParseCombo(string combo, out BindModifiers mods, out int vk)
    {
        mods = BindModifiers.None;
        vk = 0;

        string[] tokens = combo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return false; // need at least SUPER + a key
        }

        bool hasSuper = false;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToUpperInvariant())
            {
                case "SUPER":
                case "WIN":
                case "META":
                case "MOD":
                    hasSuper = true;
                    break;
                case "SHIFT":
                    mods |= BindModifiers.Shift;
                    break;
                case "ALT":
                    mods |= BindModifiers.Alt;
                    break;
                case "CTRL":
                case "CONTROL":
                    mods |= BindModifiers.Ctrl;
                    break;
                default:
                    return false; // unknown modifier token
            }
        }

        // SUPER is required — the keyboard hook only acts while the Win key is held.
        return hasSuper && KeyNameToVk(tokens[^1], out vk);
    }

    // The action is kept as raw text; its meaning ("<verb>.ps1" script vs. plain command) is resolved
    // at execution time, so nothing is hardcoded here.
    private static BindAction ParseAction(string action) => new(action);

    public static bool KeyNameToVk(string name, out int vk)
    {
        name = name.ToUpperInvariant();
        vk = 0;

        if (name.Length == 1)
        {
            char c = name[0];
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }

        switch (name)
        {
            case "RETURN":
            case "ENTER": vk = 0x0D; return true;
            case "SPACE": vk = 0x20; return true;
            case "TAB": vk = 0x09; return true;
            case "ESC":
            case "ESCAPE": vk = 0x1B; return true;
            case "LEFT": vk = 0x25; return true;
            case "UP": vk = 0x26; return true;
            case "RIGHT": vk = 0x27; return true;
            case "DOWN": vk = 0x28; return true;
            case "BACKSPACE": vk = 0x08; return true;
            case "DELETE":
            case "DEL": vk = 0x2E; return true;
            case "HOME": vk = 0x24; return true;
            case "END": vk = 0x23; return true;
            case "COMMA": vk = 0xBC; return true;
            case "PERIOD":
            case "DOT": vk = 0xBE; return true;
        }

        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name[1..], out int n) && n >= 1 && n <= 24)
        {
            vk = 0x70 + (n - 1); // VK_F1 = 0x70
            return true;
        }

        return false;
    }

    private static void Log(string message) => Winland.Log.Line(message);
}
