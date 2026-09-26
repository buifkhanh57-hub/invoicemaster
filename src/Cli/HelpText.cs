using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InvoiceMaster.Cli
{
    /// <summary>
    /// Builds every piece of help output from the command registry, so usage text can
    /// never drift from what the parser actually accepts.
    /// </summary>
    public static class HelpText
    {
        /// <summary>Program tagline shown at the top of the main help.</summary>
        public const string Tagline = "invoicemaster — customers, invoices, payments and tax from your terminal.";

        /// <summary>Display order of command groups in the main help.</summary>
        private static readonly string[] GroupOrder =
        {
            "customer", "invoice", "payment", "tax", "report", "export", "remind", "help", "version",
        };

        /// <summary>
        /// Builds the main help screen: overview, global options, command list,
        /// examples and exit codes.
        /// </summary>
        /// <returns>The complete help text.</returns>
        public static string Main()
        {
            var builder = new StringBuilder();
            builder.Append("invoicemaster ").Append(Program.Version).Append(" — ").Append(Tagline).AppendLine();
            builder.AppendLine();
            builder.Append("Manage customers, invoices, payments, tax rules, reports and CSV exports.").AppendLine();
            builder.Append("All data is stored as JSON under ~/.invoicemaster (override with --json-dir).").AppendLine();
            builder.AppendLine();
            builder.AppendLine("Usage:");
            builder.AppendLine("  invoicemaster <command> [subcommand] [arguments] [options]");
            builder.AppendLine();
            builder.AppendLine("Global options:");
            builder.AppendLine("  --json-dir <path>   use an alternate data directory");
            builder.AppendLine("  --no-color          disable colored output");
            builder.AppendLine("  --version           print the version and exit");
            builder.AppendLine("  -h, --help          show this help, or help for one command");
            builder.AppendLine();
            builder.AppendLine("Commands:");
            foreach (string group in GroupOrder)
            {
                foreach (KeyValuePair<string, CommandSpec> entry in CliParser.AllSpecs
                    .Where(kv => kv.Key == group || kv.Key.StartsWith(group + " ", StringComparison.Ordinal))
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    builder.Append("  ").Append(entry.Key.PadRight(24)).Append(entry.Value.Summary).AppendLine();
                }
            }

            builder.AppendLine();
            builder.AppendLine("Examples:");
            builder.AppendLine("  invoicemaster customer add --name \"Acme Corp\" --region US-CA --currency USD");
            builder.AppendLine("  invoicemaster invoice create CUS-0001 --due 2026-03-01");
            builder.AppendLine("  invoicemaster invoice add-item INV-2026-0001 --description \"Consulting\" --qty 8 --price 120");
            builder.AppendLine("  invoicemaster invoice issue INV-2026-0001");
            builder.AppendLine("  invoicemaster payment record INV-2026-0001 --amount 500 --method bank");
            builder.AppendLine("  invoicemaster report aging --detail");
            builder.AppendLine("  invoicemaster export invoices --status overdue");
            builder.AppendLine();
            builder.AppendLine("Exit codes: 0 success, 1 runtime or validation error, 2 usage error.");
            builder.AppendLine();
            builder.Append("Run 'invoicemaster help <command>' for command details.");
            return builder.ToString();
        }

        /// <summary>
        /// Builds help for one command (or the group list when given a group word such
        /// as "invoice"). Falls back to the main help for unknown input.
        /// </summary>
        /// <param name="key">Command key, group word, or null/empty.</param>
        /// <returns>The help text to display.</returns>
        public static string For(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return Main();
            }

            CommandSpec? spec = CliParser.FindSpec(key);
            if (spec is not null)
            {
                return RenderSpec(spec);
            }

            var groupSpecs = CliParser.AllSpecs
                .Where(kv => kv.Key.StartsWith(key.Trim() + " ", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (groupSpecs.Count > 0)
            {
                var builder = new StringBuilder();
                builder.Append("Subcommands of '").Append(key.Trim()).Append("':").AppendLine();
                foreach (KeyValuePair<string, CommandSpec> entry in groupSpecs)
                {
                    builder.Append("  ").Append(entry.Key.PadRight(24)).Append(entry.Value.Summary).AppendLine();
                }

                builder.AppendLine();
                builder.Append("Run 'invoicemaster help <command>' for details, e.g. 'invoicemaster help ")
                    .Append(groupSpecs[0].Key)
                    .Append("'.");
                return builder.ToString();
            }

            return Main();
        }

        /// <summary>Builds the one-line version string.</summary>
        /// <returns>Version line.</returns>
        public static string Version()
        {
            return "invoicemaster " + Program.Version;
        }

        private static string RenderSpec(CommandSpec spec)
        {
            var builder = new StringBuilder();
            builder.Append(spec.Key).Append(" — ").Append(spec.Summary).AppendLine();
            builder.AppendLine();
            builder.AppendLine("Usage:");
            builder.Append("  invoicemaster ").Append(spec.Usage).AppendLine();
            if (spec.PositionalNames.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Arguments:");
                for (int i = 0; i < spec.PositionalNames.Count; i++)
                {
                    builder.Append("  <").Append(spec.PositionalNames[i]).AppendLine(">   positional argument " + (i + 1));
                }
            }

            if (spec.Options.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Options:");
                foreach (CommandOption option in spec.Options)
                {
                    string flag = option.TakesValue ? "--" + option.Name + " <value>" : "--" + option.Name;
                    builder.Append("  ").Append(flag.PadRight(24)).Append(option.Description).AppendLine();
                }
            }

            builder.AppendLine();
            builder.Append("Run 'invoicemaster help' for the full command list.");
            return builder.ToString();
        }
    }
}
