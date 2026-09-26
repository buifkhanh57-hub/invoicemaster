using System;
using System.Collections.Generic;
using System.Globalization;

namespace InvoiceMaster.Cli
{

    /// <summary>One option of a command: name (without dashes), whether it takes a value, help text.</summary>
    public sealed record CommandOption(string Name, bool TakesValue, string Description);

    /// <summary>
    /// Static specification of one command: usage line, accepted options, positional
    /// count bounds. Drives both argument validation and help rendering.
    /// </summary>
    public sealed class CommandSpec
    {
        /// <summary>
        /// Creates a command specification.
        /// </summary>
        /// <param name="key">Full command key, e.g. "invoice issue".</param>
        /// <param name="summary">One-line summary shown in the command list.</param>
        /// <param name="usage">Usage line after "invoicemaster".</param>
        /// <param name="options">Accepted options.</param>
        /// <param name="minPositionals">Minimum positional argument count.</param>
        /// <param name="maxPositionals">Maximum positional count; -1 means unlimited.</param>
        /// <param name="positionalNames">Display names of the positionals.</param>
        public CommandSpec(
            string key,
            string summary,
            string usage,
            IReadOnlyList<CommandOption> options,
            int minPositionals,
            int maxPositionals,
            IReadOnlyList<string> positionalNames)
        {
            Key = key;
            Summary = summary;
            Usage = usage;
            Options = options;
            MinPositionals = minPositionals;
            MaxPositionals = maxPositionals;
            PositionalNames = positionalNames;
        }

        /// <summary>Full command key, e.g. "invoice issue".</summary>
        public string Key { get; }

        /// <summary>One-line summary shown in overviews.</summary>
        public string Summary { get; }

        /// <summary>Usage line (without the leading program name).</summary>
        public string Usage { get; }

        /// <summary>Accepted options.</summary>
        public IReadOnlyList<CommandOption> Options { get; }

        /// <summary>Minimum positional argument count.</summary>
        public int MinPositionals { get; }

        /// <summary>Maximum positional count; -1 means unlimited.</summary>
        public int MaxPositionals { get; }

        /// <summary>Display names of the positional arguments.</summary>
        public IReadOnlyList<string> PositionalNames { get; }

        /// <summary>Finds an option by name (case-insensitive, without dashes).</summary>
        /// <param name="name">Option name.</param>
        /// <returns>The option, or null when unknown.</returns>
        public CommandOption? FindOption(string name)
        {
            foreach (CommandOption option in Options)
            {
                if (string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            return null;
        }

        /// <summary>Determines whether the option name is accepted by this command.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>True when known.</returns>
        public bool HasOption(string name)
        {
            return FindOption(name) is not null;
        }

        /// <summary>Determines whether the option expects a value.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>True when the option takes a value.</returns>
        public bool OptionTakesValue(string name)
        {
            return FindOption(name)?.TakesValue ?? false;
        }

        /// <summary>Compact "--a, --b <value>" summary for error messages.</summary>
        /// <returns>Option summary, or "(no options)" when empty.</returns>
        public string OptionsSummary()
        {
            if (Options.Count == 0)
            {
                return "(no options)";
            }

            return "Options: " + string.Join(", ", Options.Select(o =>
                o.TakesValue ? "--" + o.Name + " <value>" : "--" + o.Name));
        }
    }

    /// <summary>
    /// Result of parsing the command line: resolved command, positionals, options and
    /// typed getters that turn malformed values into clean usage errors.
    /// </summary>
    public sealed class ParsedCommand
    {
        /// <summary>Resolved command key, e.g. "invoice issue". Empty for bare --help.</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>Positional arguments left after the command tokens.</summary>
        public IReadOnlyList<string> Positionals { get; set; } = new List<string>();

        /// <summary>Options keyed by name (case-insensitive); flags map to the empty string.</summary>
        public IReadOnlyDictionary<string, string> Options { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when -h/--help or the help command was requested.</summary>
        public bool WantsHelp { get; set; }

        /// <summary>True when --version or the version command was requested.</summary>
        public bool WantsVersion { get; set; }

        /// <summary>True when --no-color was passed.</summary>
        public bool NoColor { get; set; }

        /// <summary>Value of --json-dir, or null when the default location applies.</summary>
        public string? JsonDir { get; set; }

        /// <summary>Determines whether an option was passed (flags included).</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>True when present.</returns>
        public bool HasOption(string name)
        {
            return Options.ContainsKey(name);
        }

        /// <summary>Returns the option value or null when absent.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>Raw value; empty string for flags.</returns>
        public string? GetOption(string name)
        {
            return Options.TryGetValue(name, out string? value) ? value : null;
        }

        /// <summary>Returns the option value or throws a usage error when absent.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>Non-empty value.</returns>
        public string RequireOption(string name)
        {
            string? value = GetOption(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new UsageException("Option --" + name + " is required but missing.");
            }

            return value.Trim();
        }

        /// <summary>Returns the option value or the fallback; flags count as absent.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <param name="fallback">Value used when the option is absent or blank.</param>
        /// <returns>The chosen string.</returns>
        public string GetStringOr(string name, string fallback)
        {
            string? value = GetOption(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        /// <summary>Parses a required money/number option.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>Parsed decimal.</returns>
        public decimal GetDecimal(string name)
        {
            string raw = RequireOption(name);
            if (!Money.TryParseAmount(raw, out decimal value))
            {
                throw new UsageException("Option --" + name + " must be a number (got '" + raw + "').");
            }

            return value;
        }

        /// <summary>Parses a number option with a default.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <param name="fallback">Default when absent.</param>
        /// <returns>Parsed decimal or the fallback.</returns>
        public decimal GetDecimalOr(string name, decimal fallback)
        {
            return HasOption(name) ? GetDecimal(name) : fallback;
        }

        /// <summary>Parses an integer option with a default.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <param name="fallback">Default when absent.</param>
        /// <returns>Parsed integer or the fallback.</returns>
        public int GetIntOr(string name, int fallback)
        {
            string? raw = GetOption(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new UsageException("Option --" + name + " must be a whole number (got '" + raw.Trim() + "').");
            }

            return value;
        }

        /// <summary>Parses a required ISO date option (yyyy-MM-dd) into UTC midnight.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <returns>Parsed UTC date.</returns>
        public DateTime GetDate(string name)
        {
            string raw = RequireOption(name);
            if (!TryParseDate(raw, out DateTime value))
            {
                throw new UsageException("Option --" + name + " must be an ISO date (yyyy-MM-dd), got '" + raw + "'.");
            }

            return value;
        }

        /// <summary>Parses an ISO date option with a fallback.</summary>
        /// <param name="name">Option name without dashes.</param>
        /// <param name="fallback">Default when absent.</param>
        /// <returns>Parsed UTC date or the fallback.</returns>
        public DateTime? GetDateOr(string name, DateTime? fallback)
        {
            string? raw = GetOption(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!TryParseDate(raw, out DateTime value))
            {
                throw new UsageException("Option --" + name + " must be an ISO date (yyyy-MM-dd), got '" + raw + "'.");
            }

            return value;
        }

        /// <summary>Returns the positional at an index or throws a usage error.</summary>
        /// <param name="index">Zero-based position.</param>
        /// <param name="displayName">Name used in the error message.</param>
        /// <returns>The positional value.</returns>
        public string PositionalAt(int index, string displayName)
        {
            if (index < 0 || index >= Positionals.Count)
            {
                throw new UsageException("Missing required argument <" + displayName + ">.");
            }

            return Positionals[index].Trim();
        }

        private static bool TryParseDate(string text, out DateTime value)
        {
            if (DateTime.TryParseExact(
                    text.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime parsed))
            {
                value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }

            value = default;
            return false;
        }
    }
}
