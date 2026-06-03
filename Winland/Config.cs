using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Winland;

/// <summary>One non-comment config line, split into its keyword and the raw value after the '='.</summary>
internal sealed record ConfigEntry(string Keyword, string Value);

/// <summary>
/// Global config reader for Winland's Hyprland/Omarchy-style winland.conf. It reads the file, drops
/// blank and '#'-comment lines, and splits every remaining line on the first '=' into a
/// (keyword, value) entry. It deliberately does NOT interpret the values — feature modules consume the
/// entries they care about (e.g. <see cref="HotkeyConfig"/> reads "bind" entries). To add a new config
/// feature, read its own keyword via <see cref="ValuesOf"/>; nothing here needs to change.
/// </summary>
internal sealed class Config
{
    public IReadOnlyList<ConfigEntry> Entries { get; }

    private Config(IReadOnlyList<ConfigEntry> entries) => Entries = entries;

    /// <summary>The shipped config file, next to the executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.conf");

    /// <summary>Read and tokenize the config file. A missing/unreadable file yields an empty config.</summary>
    public static Config Load(string path)
    {
        var entries = new List<ConfigEntry>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return new Config(entries);
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            string keyword = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (keyword.Length == 0)
            {
                continue;
            }

            entries.Add(new ConfigEntry(keyword, value));
        }

        return new Config(entries);
    }

    /// <summary>All values whose keyword matches (case-insensitive), in file order.</summary>
    public IEnumerable<string> ValuesOf(string keyword) =>
        Entries.Where(e => e.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase))
               .Select(e => e.Value);
}
