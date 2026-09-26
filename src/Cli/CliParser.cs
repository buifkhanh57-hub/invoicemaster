using System;
using System.Collections.Generic;
using System.Linq;

namespace InvoiceMaster.Cli
{
    /// <summary>
    /// Parses command lines into <see cref="ParsedCommand"/> objects, validating against
    /// the command registry. Supports --key value, --key=value, flags, -h, -- and global
    /// options anywhere on the line.
    /// </summary>
    public static class CliParser
    {
        private static readonly Dictionary<string, CommandSpec> Specs = BuildSpecs();

        private static readonly HashSet<string> TopLevelWords = new(StringComparer.Ordinal)
        {
            "customer", "invoice", "payment", "tax", "report", "export",
        };

        /// <summary>All registered command specifications keyed by command key.</summary>
        public static IReadOnlyDictionary<string, CommandSpec> AllSpecs => Specs;

        /// <summary>Finds a command specification by exact key.</summary>
        /// <param name="key">Command key such as "invoice issue".</param>
        /// <returns>The spec, or null when unknown.</returns>
        public static CommandSpec? FindSpec(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return Specs.TryGetValue(key.Trim(), out CommandSpec? spec) ? spec : null;
        }

        /// <summary>
        /// Parses and validates a command line.
        /// </summary>
        /// <param name="args">Raw arguments (without the program name).</param>
        /// <returns>The parsed command.</returns>
        public static ParsedCommand Parse(string[]? args)
        {
            if (args is null || args.Length == 0)
            {
                return new ParsedCommand { Command = string.Empty, WantsHelp = true };
            }

            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string command = string.Empty;
            bool wantsHelp = false;
            bool wantsVersion = false;
            bool noColor = false;
            string? jsonDir = null;
            bool optionsEnded = false;

            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];
                if (!optionsEnded && token == "--")
                {
                    optionsEnded = true;
                    continue;
                }

                if (!optionsEnded && token.StartsWith("--", StringComparison.Ordinal))
                {
                    string body = token[2..];
                    string? inlineValue = null;
                    int eq = body.IndexOf('=');
                    if (eq >= 0)
                    {
                        inlineValue = body[(eq + 1)..];
                        body = body[..eq];
                    }

                    if (body.Length == 0)
                    {
                        throw new UsageException("Empty option name '--'.");
                    }

                    if (body == "help")
                    {
                        wantsHelp = true;
                        continue;
                    }

                    if (body == "version")
                    {
                        wantsVersion = true;
                        continue;
                    }

                    if (body == "no-color")
                    {
                        noColor = true;
                        continue;
                    }

                    if (body == "json-dir")
                    {
                        jsonDir = inlineValue ?? TakeValue(args, ref i, body);
                        continue;
                    }

                    if (command.Length == 0)
                    {
                        throw new UsageException(
                            "Unknown global option '--" + body + "'. Global options: --json-dir, --no-color, --version, --help.");
                    }

                    CommandSpec spec = Specs[command];
                    if (!spec.HasOption(body))
                    {
                        throw new UsageException("Unknown option '--" + body + "' for '" + command + "'. " + spec.OptionsSummary());
                    }

                    if (!spec.OptionTakesValue(body))
                    {
                        if (inlineValue is not null)
                        {
                            throw new UsageException("Option --" + body + " is a flag and does not take a value.");
                        }

                        options[body] = string.Empty;
                        continue;
                    }

                    options[body] = inlineValue ?? TakeValue(args, ref i, body);
                    continue;
                }

                if (!optionsEnded && token == "-h")
                {
                    wantsHelp = true;
                    continue;
                }

                if (command.Length == 0)
                {
                    command = ResolveCommand(args, ref i, token);
                    continue;
                }

                positionals.Add(token);
            }

            if (command.Length == 0 && !wantsHelp && !wantsVersion)
            {
                throw new UsageException("No command specified. Run 'invoicemaster help' for the command list.");
            }

            if (command.Length > 0 && !wantsHelp && !wantsVersion)
            {
                CommandSpec spec = Specs[command];
                if (positionals.Count < spec.MinPositionals
                    || (spec.MaxPositionals >= 0 && positionals.Count > spec.MaxPositionals))
                {
                    throw new UsageException(
                        "'" + command + "' expects " + PositionalExpectation(spec)
                        + ", got " + positionals.Count + ". Usage: invoicemaster " + spec.Usage);
                }
            }

            return new ParsedCommand
            {
                Command = command,
                Positionals = positionals,
                Options = options,
                WantsHelp = wantsHelp,
                WantsVersion = wantsVersion,
                NoColor = noColor,
                JsonDir = jsonDir,
            };
        }

        private static string ResolveCommand(string[] args, ref int index, string token)
        {
            if (FindSpec(token) is not null)
            {
                return token;
            }

            if (TopLevelWords.Contains(token))
            {
                if (index + 1 < args.Length && FindSpec(token + " " + args[index + 1]) is not null)
                {
                    string combined = token + " " + args[index + 1];
                    index++;
                    return combined;
                }

                var subCommands = Specs.Keys.Where(k => k.StartsWith(token + " ", StringComparison.Ordinal))
                    .Select(k => k[(token.Length + 1)..])
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                throw new UsageException(
                    "'" + token + "' needs a subcommand (" + string.Join(", ", subCommands) + ").");
            }

            var known = Specs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            throw new UsageException(
                "Unknown command '" + token + "'. Known commands: " + string.Join(", ", known) + ".");
        }

        private static string TakeValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new UsageException("Option --" + name + " requires a value.");
            }

            index++;
            return args[index];
        }

        private static string PositionalExpectation(CommandSpec spec)
        {
            if (spec.MaxPositionals < 0)
            {
                return "at least " + spec.MinPositionals + " argument(s)";
            }

            if (spec.MinPositionals == spec.MaxPositionals)
            {
                return spec.MinPositionals + " argument(s)";
            }

            return spec.MinPositionals + "-" + spec.MaxPositionals + " argument(s)";
        }

        private static Dictionary<string, CommandSpec> BuildSpecs()
        {
            var specs = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);
            void Add(string key, string summary, string usage, CommandOption[] options, int min, int max, string[] positionalNames)
            {
                specs[key] = new CommandSpec(key, summary, usage, options, min, max, positionalNames);
            }

            Add("customer add", "register a new customer",
                "customer add --name <name> [options]",
                new[]
                {
                    new CommandOption("name", true, "(required) customer or company name"),
                    new CommandOption("email", true, "contact email"),
                    new CommandOption("phone", true, "contact phone"),
                    new CommandOption("tax-id", true, "VAT / tax identifier"),
                    new CommandOption("region", true, "tax region code, e.g. US-CA"),
                    new CommandOption("currency", true, "default invoice currency (default USD)"),
                    new CommandOption("street", true, "address: street line"),
                    new CommandOption("city", true, "address: city"),
                    new CommandOption("state", true, "address: state or province"),
                    new CommandOption("zip", true, "address: postal code"),
                    new CommandOption("country", true, "address: country"),
                    new CommandOption("notes", true, "free-form notes"),
                },
                0, 0, Array.Empty<string>());

            Add("customer list", "list or search customers",
                "customer list [--search <text>]",
                new[] { new CommandOption("search", true, "keyword filter across name, email, id") },
                0, 0, Array.Empty<string>());

            Add("customer show", "show one customer in detail",
                "customer show <customer-id>",
                Array.Empty<CommandOption>(), 1, 1, new[] { "customer-id" });

            Add("customer update", "edit fields of one customer",
                "customer update <customer-id> [options]",
                new[]
                {
                    new CommandOption("name", true, "new name"),
                    new CommandOption("email", true, "new email"),
                    new CommandOption("phone", true, "new phone"),
                    new CommandOption("tax-id", true, "new tax identifier"),
                    new CommandOption("region", true, "new tax region code"),
                    new CommandOption("currency", true, "new default currency"),
                    new CommandOption("street", true, "replaces the address: street line"),
                    new CommandOption("city", true, "replaces the address: city"),
                    new CommandOption("state", true, "replaces the address: state"),
                    new CommandOption("zip", true, "replaces the address: postal code"),
                    new CommandOption("country", true, "replaces the address: country"),
                    new CommandOption("notes", true, "new notes"),
                },
                1, 1, new[] { "customer-id" });

            Add("customer delete", "permanently delete a customer",
                "customer delete <customer-id> --force",
                new[] { new CommandOption("force", false, "confirm the deletion") },
                1, 1, new[] { "customer-id" });

            Add("invoice create", "create a draft invoice",
                "invoice create <customer-id> [options]",
                new[]
                {
                    new CommandOption("currency", true, "invoice currency (default: customer currency)"),
                    new CommandOption("region", true, "tax region (default: customer region)"),
                    new CommandOption("due", true, "due date, yyyy-MM-dd"),
                    new CommandOption("notes", true, "notes or payment terms"),
                },
                1, 1, new[] { "customer-id" });

            Add("invoice add-item", "append a line item to a draft",
                "invoice add-item <invoice-number> --description <text> --price <amount> [options]",
                new[]
                {
                    new CommandOption("description", true, "(required) item description"),
                    new CommandOption("qty", true, "quantity (default 1)"),
                    new CommandOption("price", true, "(required) unit price"),
                    new CommandOption("discount", true, "line discount percent (default 0)"),
                    new CommandOption("category", true, "exemption category (default Standard)"),
                },
                1, 1, new[] { "invoice-number" });

            Add("invoice issue", "issue a draft invoice",
                "invoice issue <invoice-number> [options]",
                new[]
                {
                    new CommandOption("date", true, "issue date, yyyy-MM-dd (default today)"),
                    new CommandOption("due", true, "due date, yyyy-MM-dd (default draft due date)"),
                },
                1, 1, new[] { "invoice-number" });

            Add("invoice list", "list invoices with filters",
                "invoice list [--status <status>] [--customer <id>] [--year <yyyy>]",
                new[]
                {
                    new CommandOption("status", true, "draft, issued, overdue, paid, void or all"),
                    new CommandOption("customer", true, "filter by customer id"),
                    new CommandOption("year", true, "filter by invoice year"),
                },
                0, 0, Array.Empty<string>());

            Add("invoice show", "show one invoice in detail",
                "invoice show <invoice-number>",
                Array.Empty<CommandOption>(), 1, 1, new[] { "invoice-number" });

            Add("invoice void", "cancel an invoice",
                "invoice void <invoice-number> --reason <text>",
                new[] { new CommandOption("reason", true, "(required) why the invoice is cancelled") },
                1, 1, new[] { "invoice-number" });

            Add("payment record", "record a payment on an invoice",
                "payment record <invoice-number> --amount <amount> [options]",
                new[]
                {
                    new CommandOption("amount", true, "(required) amount received"),
                    new CommandOption("method", true, "cash, bank, wire, credit, debit, check, other (default bank)"),
                    new CommandOption("date", true, "payment date, yyyy-MM-dd (default today)"),
                    new CommandOption("ref", true, "bank reference or check number"),
                    new CommandOption("notes", true, "free-form notes"),
                    new CommandOption("allow-overpay", false, "allow amounts above the balance"),
                },
                1, 1, new[] { "invoice-number" });

            Add("payment list", "list recorded payments",
                "payment list [--invoice <number>] [--customer <id>] [--year <yyyy>]",
                new[]
                {
                    new CommandOption("invoice", true, "filter by invoice number"),
                    new CommandOption("customer", true, "filter by customer id"),
                    new CommandOption("year", true, "filter by payment year"),
                },
                0, 0, Array.Empty<string>());

            Add("tax set", "register or replace a tax rule",
                "tax set --region <code> --rate <percent> [options]",
                new[]
                {
                    new CommandOption("region", true, "(required) region code, e.g. US-TX"),
                    new CommandOption("rate", true, "(required) standard rate in percent"),
                    new CommandOption("currency", true, "rounding currency (default USD)"),
                    new CommandOption("name", true, "display name of the rule"),
                    new CommandOption("exempt", true, "comma separated exempt categories"),
                    new CommandOption("reduced", true, "reduced rates, e.g. Food=5,Books=2"),
                    new CommandOption("notes", true, "free-form notes"),
                },
                0, 0, Array.Empty<string>());

            Add("tax get", "show tax rules",
                "tax get [--region <code>]",
                new[] { new CommandOption("region", true, "show only this region's rule") },
                0, 0, Array.Empty<string>());

            Add("report outstanding", "open balances per invoice",
                "report outstanding [--as-of <date>] [--customer <id>]",
                new[]
                {
                    new CommandOption("as-of", true, "reference date, yyyy-MM-dd (default today)"),
                    new CommandOption("customer", true, "filter by customer id"),
                },
                0, 0, Array.Empty<string>());

            Add("report aging", "30/60/90 day aging of receivables",
                "report aging [--as-of <date>] [--customer <id>] [--detail]",
                new[]
                {
                    new CommandOption("as-of", true, "reference date, yyyy-MM-dd (default today)"),
                    new CommandOption("customer", true, "filter by customer id"),
                    new CommandOption("detail", false, "also list every invoice in the buckets"),
                },
                0, 0, Array.Empty<string>());

            Add("report revenue", "monthly invoiced vs collected series",
                "report revenue [--from <date>] [--to <date>]",
                new[]
                {
                    new CommandOption("from", true, "first month to include, yyyy-MM-dd"),
                    new CommandOption("to", true, "last month to include, yyyy-MM-dd"),
                },
                0, 0, Array.Empty<string>());

            Add("report top-customers", "rank customers by revenue",
                "report top-customers [--top <n>] [--metric <name>] [--as-of <date>]",
                new[]
                {
                    new CommandOption("top", true, "row count (default 10)"),
                    new CommandOption("metric", true, "collected, invoiced or outstanding (default collected)"),
                    new CommandOption("as-of", true, "reference date, yyyy-MM-dd (default today)"),
                },
                0, 0, Array.Empty<string>());

            Add("export invoices", "export invoices to CSV",
                "export invoices [--out <path>] [--status <status>] [--year <yyyy>]",
                new[]
                {
                    new CommandOption("out", true, "output CSV path (default ./invoices-<timestamp>.csv)"),
                    new CommandOption("status", true, "status filter"),
                    new CommandOption("year", true, "invoice year filter"),
                },
                0, 0, Array.Empty<string>());

            Add("export payments", "export payments to CSV",
                "export payments [--out <path>] [--year <yyyy>]",
                new[]
                {
                    new CommandOption("out", true, "output CSV path (default ./payments-<timestamp>.csv)"),
                    new CommandOption("year", true, "payment year filter"),
                },
                0, 0, Array.Empty<string>());

            Add("remind", "draft overdue reminder letters",
                "remind [--as-of <date>] [--customer <id>] [--out <dir>]",
                new[]
                {
                    new CommandOption("as-of", true, "reference date, yyyy-MM-dd (default today)"),
                    new CommandOption("customer", true, "only this customer"),
                    new CommandOption("out", true, "write letters as files into this directory"),
                },
                0, 0, Array.Empty<string>());

            Add("help", "show help",
                "help [command]",
                Array.Empty<CommandOption>(), 0, 1, new[] { "command" });

            Add("version", "show the version",
                "version",
                Array.Empty<CommandOption>(), 0, 0, Array.Empty<string>());

            return specs;
        }
    }
}
