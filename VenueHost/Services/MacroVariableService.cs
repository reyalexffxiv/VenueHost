using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VenueHost.Services;

/// <summary>
/// Expands user-editable macro variables like {CurrentDJName}.
/// Unknown variables are left visible on purpose, which makes typo debugging easier.
/// </summary>
public static class MacroVariableService
{
    private static readonly Regex VariableRegex = new(@"\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static string Expand(string macroText, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(macroText))
            return string.Empty;

        var expandedLines = new List<string>();

        foreach (var rawLine in macroText.Replace("\r", string.Empty).Split('\n'))
        {
            var skipLine = false;
            var expandedLine = VariableRegex.Replace(rawLine, match =>
            {
                var name = match.Groups["name"].Value;
                if (!variables.TryGetValue(name, out var value))
                    return match.Value;

                if ((name.Equals("GiveawayText", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GiveawayLine", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GiveawayCommandLine", StringComparison.OrdinalIgnoreCase)) &&
                    string.IsNullOrWhiteSpace(value))
                {
                    skipLine = true;
                }

                return value;
            });

            if (!skipLine)
                expandedLines.Add(expandedLine);
        }

        return string.Join("\n", expandedLines);
    }
}
