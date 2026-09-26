using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using InvoiceMaster.Models;
using InvoiceMaster.Services;

namespace InvoiceMaster.Cli
{
    /// <summary>
    /// Second half of the command implementations: payments, tax rules, reports,
    /// CSV exports and reminder letters.
    /// </summary>
    public sealed partial class CliApp
    {
        private int RunModuleCommand(ParsedCommand command)
        {
            switch (command.Command)
            {
                case "payment record":
                    return RecordPayment(command);
                case "payment list":
                    return ListPayments(command);
                case "tax set":
                    return TaxSet(command);
                case "tax get":
                    return TaxGet(command);
                case "report outstanding":
                    return ReportOutstanding(command);
                case "report aging":
                    return ReportAging(command);
                case "report revenue":
                    return ReportRevenue(command);
                case "report top-customers":
                    return ReportTopCustomers(command);
                case "export invoices":
                    return ExportInvoices(command);
                case "export payments":
                    return ExportPayments(command);
                case "remind":
                    return Remind(command);
                default:
                    throw new UsageException("Unknown command '" + command.Command + "'. Run 'invoicemaster help'.");
            }
        }

        private int RecordPayment(ParsedCommand command)
        {
            string number = command.PositionalAt(0, "invoice-number");
            Invoice invoice = _invoices.RequireByNumber(number);
            string methodText = command.GetStringOr("method", "bank");
            if (!Payment.TryParseMethod(methodText, out PaymentMethod method))
            {
                throw new UsageException(
                    "Unknown payment method '" + methodText + "'. Accepted: " + Payment.MethodAliases() + ".");
            }

            var payment = new Payment
            {
                Amount = command.GetDecimal("amount"),
                Currency = invoice.Currency,
                Method = method,
                PaidAtUtc = command.GetDateOr("date", DateTime.UtcNow.Date).Value,
                Reference = command.GetStringOr("ref", string.Empty),
                Notes = command.GetStringOr("notes", string.Empty),
            };

            bool allowOverpay = command.HasOption("allow-overpay");
            Invoice updated = _invoices.RecordPayment(invoice.Number, payment, allowOverpay);
            Payment stored = updated.Payments[^1];
            Console.WriteLine("Recorded payment " + stored.Id + " ("
                + Money.Format(stored.Amount, stored.Currency) + ") on " + updated.Number + ".");
            Console.WriteLine("  Balance : " + Money.Format(updated.BalanceDue(), updated.Currency)
                + "  Status: " + updated.Status);
            return 0;
        }

        private int ListPayments(ParsedCommand command)
        {
            string invoiceOption = command.GetStringOr("invoice", string.Empty);
            string customerOption = command.GetStringOr("customer", string.Empty);
            string? customerId = customerOption.Length == 0 ? null : _customers.RequireById(customerOption).Id;
            int? year = ReadOptionalYear(command, "year");
            IReadOnlyList<PaymentRecord> records = _invoices.GetPayments(customerId, year);
            if (invoiceOption.Length > 0)
            {
                records = records
                    .Where(r => string.Equals(r.Payment.InvoiceNumber, invoiceOption.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (records.Count == 0 && _invoices.FindByNumber(invoiceOption) is null)
                {
                    throw NotFoundException.For("invoice", invoiceOption);
                }
            }

            if (records.Count == 0)
            {
                Console.WriteLine("No payments match the given filters.");
                return 0;
            }

            var rows = new List<IReadOnlyList<string?>>();
            foreach (PaymentRecord record in records)
            {
                rows.Add(new string?[]
                {
                    record.Payment.Id,
                    record.Payment.InvoiceNumber,
                    _customers.FindById(record.Invoice.CustomerId)?.Name ?? record.Invoice.CustomerId,
                    TableRenderer.FormatDate(record.Payment.PaidAtUtc),
                    record.Payment.MethodDisplay,
                    Money.Format(record.Payment.Amount, record.Payment.Currency),
                    record.Payment.Reference,
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "ID", "Invoice", "Customer", "Date", "Method", "Amount", "Reference" },
                rows,
                new[] { ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Left }));
            Console.WriteLine();
            Console.WriteLine(records.Count + " payment(s).");
            return 0;
        }

        private int TaxSet(ParsedCommand command)
        {
            string region = TaxCalculator.NormalizeRegion(command.RequireOption("region"));
            if (region.Length == 0)
            {
                throw new UsageException("Option --region must be a region code such as US-TX.");
            }

            decimal rate = command.GetDecimal("rate");
            if (rate < 0m || rate > 100m)
            {
                throw new UsageException("Option --rate must be between 0 and 100 percent.");
            }

            string currency = command.GetStringOr("currency", "USD").ToUpperInvariant();
            if (!Money.IsSupported(currency))
            {
                throw new UsageException("Unsupported currency '" + currency + "'.");
            }

            var rule = new TaxRule
            {
                Region = region,
                DisplayName = command.GetStringOr("name", region + " tax"),
                Currency = currency,
                StandardRatePercent = rate,
                Notes = command.GetStringOr("notes", string.Empty),
            };
            rule.ExemptCategories = ParseCategories(command.GetStringOr("exempt", string.Empty));
            rule.ReducedRatesPercent = ParseReducedRates(command.GetStringOr("reduced", string.Empty));

            var errors = rule.Validate();
            if (errors.Count > 0)
            {
                throw ValidationException.ForErrors("Tax rule '" + region + "' is invalid:", errors);
            }

            _tax.Register(rule);
            var stored = _taxStore.Load() ?? new List<TaxRule>();
            stored.RemoveAll(r => string.Equals(r.Region, region, StringComparison.OrdinalIgnoreCase));
            stored.Add(rule);
            stored.Sort((left, right) => string.CompareOrdinal(left.Region, right.Region));
            _taxStore.Save(stored);

            Console.WriteLine("Saved tax rule " + region + ": standard " + Money.FormatPercent(rate)
                + ", " + rule.ExemptCategories.Count + " exempt categories, "
                + rule.ReducedRatesPercent.Count + " reduced rates.");
            return 0;
        }

        private int TaxGet(ParsedCommand command)
        {
            string regionOption = command.GetStringOr("region", string.Empty);
            if (regionOption.Length > 0)
            {
                TaxRule rule = _tax.RequireRule(regionOption);
                Console.WriteLine(rule.Describe());
                if (rule.Notes.Length > 0)
                {
                    Console.WriteLine("Notes: " + rule.Notes);
                }

                return 0;
            }

            IReadOnlyList<TaxRule> rules = _tax.AllRules();
            var rows = new List<IReadOnlyList<string?>>();
            foreach (TaxRule rule in rules)
            {
                rows.Add(new string?[]
                {
                    rule.Region,
                    rule.DisplayName,
                    rule.Currency,
                    Money.FormatPercent(rule.StandardRatePercent),
                    rule.ReducedRatesPercent.Count.ToString(CultureInfo.InvariantCulture),
                    string.Join(", ", rule.ExemptCategories),
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "Region", "Name", "Cur", "Standard", "Reduced", "Exempt categories" },
                rows));
            Console.WriteLine();
            Console.WriteLine(rules.Count + " tax rule(s). Custom rules persist in "
                + Path.Combine(_dataDirectory, "taxrules.json") + ".");
            return 0;
        }

        private int ReportOutstanding(ParsedCommand command)
        {
            DateTime asOf = command.GetDateOr("as-of", DateTime.UtcNow.Date).Value;
            string? customerId = ResolveOptionalCustomer(command);
            OutstandingReport report = _reports.BuildOutstanding(asOf, customerId);
            if (report.Rows.Count == 0)
            {
                Console.WriteLine("No outstanding invoices as of " + TableRenderer.FormatDate(asOf) + ".");
                return 0;
            }

            var rows = new List<IReadOnlyList<string?>>();
            foreach (OutstandingRow row in report.Rows)
            {
                rows.Add(new string?[]
                {
                    row.InvoiceNumber,
                    row.CustomerName,
                    row.Status,
                    TableRenderer.FormatDate(row.DueDate),
                    row.DaysOverdue.ToString(CultureInfo.InvariantCulture),
                    Money.Format(row.Total, row.Currency),
                    Money.Format(row.Paid, row.Currency),
                    Money.Format(row.Balance, row.Currency),
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "Number", "Customer", "Status", "Due", "Days", "Total", "Paid", "Balance" },
                rows,
                new[] { ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right }));
            Console.WriteLine();
            Console.WriteLine(report.InvoiceCount + " open invoice(s), outstanding "
                + Money.Format(report.TotalOutstanding, report.Currencies.Count == 1 ? report.Currencies[0] : null) + ".");
            return 0;
        }

        private int ReportAging(ParsedCommand command)
        {
            DateTime asOf = command.GetDateOr("as-of", DateTime.UtcNow.Date).Value;
            string? customerId = ResolveOptionalCustomer(command);
            AgingReport report = _reports.BuildAging(asOf, customerId);

            var rows = new List<IReadOnlyList<string?>>();
            foreach (AgingBucket bucket in report.Buckets)
            {
                rows.Add(new string?[]
                {
                    bucket.Label,
                    bucket.InvoiceCount.ToString(CultureInfo.InvariantCulture),
                    bucket.Amount.ToString(CultureInfo.InvariantCulture),
                });
            }

            Console.WriteLine("Aging as of " + TableRenderer.FormatDate(asOf)
                + " (amounts in raw currency units):");
            Console.WriteLine();
            Console.WriteLine(TableRenderer.Render(
                new[] { "Bucket", "Invoices", "Amount" },
                rows,
                new[] { ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right }));
            Console.WriteLine();
            Console.WriteLine("Total outstanding: " + report.TotalOutstanding.ToString(CultureInfo.InvariantCulture));
            if (report.CurrencyNote is not null)
            {
                Console.WriteLine("Note: " + report.CurrencyNote);
            }

            if (command.HasOption("detail") && report.Rows.Count > 0)
            {
                Console.WriteLine();
                var detailRows = new List<IReadOnlyList<string?>>();
                foreach (AgingDetailRow row in report.Rows)
                {
                    detailRows.Add(new string?[]
                    {
                        row.InvoiceNumber,
                        row.CustomerName,
                        row.BucketLabel,
                        TableRenderer.FormatDate(row.DueDate),
                        row.DaysOverdue.ToString(CultureInfo.InvariantCulture),
                        Money.Format(row.Balance, row.Currency),
                    });
                }

                Console.WriteLine(TableRenderer.Render(
                    new[] { "Number", "Customer", "Bucket", "Due", "Days", "Balance" },
                    detailRows,
                    new[] { ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right }));
            }

            return 0;
        }

        private int ReportRevenue(ParsedCommand command)
        {
            DateTime? from = command.GetDateOr("from", null);
            DateTime? to = command.GetDateOr("to", null);
            if (from is not null && to is not null && from.Value > to.Value)
            {
                throw new UsageException("Option --from must not be after --to.");
            }

            RevenueReport report = _reports.BuildRevenue(from, to);
            if (report.Rows.Count == 0)
            {
                Console.WriteLine("No revenue activity in the requested range.");
                return 0;
            }

            var rows = new List<IReadOnlyList<string?>>();
            foreach (MonthRevenueRow row in report.Rows)
            {
                rows.Add(new string?[]
                {
                    row.Month,
                    row.Invoiced.ToString(CultureInfo.InvariantCulture),
                    row.Collected.ToString(CultureInfo.InvariantCulture),
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "Month", "Invoiced", "Collected" },
                rows,
                new[] { ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right }));
            Console.WriteLine();
            Console.WriteLine("Totals: invoiced " + report.TotalInvoiced.ToString(CultureInfo.InvariantCulture)
                + ", collected " + report.TotalCollected.ToString(CultureInfo.InvariantCulture) + ".");
            return 0;
        }

        private int ReportTopCustomers(ParsedCommand command)
        {
            int top = command.GetIntOr("top", 10);
            string metric = command.GetStringOr("metric", "collected");
            DateTime asOf = command.GetDateOr("as-of", DateTime.UtcNow.Date).Value;
            IReadOnlyList<CustomerRevenueRow> rowsData = _reports.BuildTopCustomers(top, metric, asOf);
            if (rowsData.Count == 0)
            {
                Console.WriteLine("No invoiced customers yet.");
                return 0;
            }

            var rows = new List<IReadOnlyList<string?>>();
            for (int i = 0; i < rowsData.Count; i++)
            {
                CustomerRevenueRow row = rowsData[i];
                rows.Add(new string?[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    row.CustomerId,
                    row.CustomerName,
                    row.InvoiceCount.ToString(CultureInfo.InvariantCulture),
                    row.Invoiced.ToString(CultureInfo.InvariantCulture),
                    row.Collected.ToString(CultureInfo.InvariantCulture),
                    row.Outstanding.ToString(CultureInfo.InvariantCulture),
                    TableRenderer.FormatDate(row.LastActivity),
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "Rank", "Customer", "Name", "Invoices", "Invoiced", "Collected", "Outstanding", "Last" },
                rows,
                new[] { ColumnAlignment.Right, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Left }));
            Console.WriteLine();
            Console.WriteLine("Ranked by " + metric + ". Amounts in raw currency units.");
            return 0;
        }

        private int ExportInvoices(ParsedCommand command)
        {
            InvoiceStatus? status = ParseStatusFilter(command.GetStringOr("status", string.Empty));
            int? year = ReadOptionalYear(command, "year");
            IReadOnlyList<Invoice> invoices = _invoices.Filter(status, null, year);
            string outputPath = command.GetStringOr("out", DefaultExportName("invoices"));
            string written = _csv.ExportInvoices(invoices, CustomerName, outputPath);
            Console.WriteLine("Exported " + invoices.Count + " invoice(s) to " + written + ".");
            return 0;
        }

        private int ExportPayments(ParsedCommand command)
        {
            int? year = ReadOptionalYear(command, "year");
            IReadOnlyList<PaymentRecord> records = _invoices.GetPayments(null, year);
            string outputPath = command.GetStringOr("out", DefaultExportName("payments"));
            string written = _csv.ExportPayments(records, CustomerName, outputPath);
            Console.WriteLine("Exported " + records.Count + " payment(s) to " + written + ".");
            return 0;
        }

        private int Remind(ParsedCommand command)
        {
            DateTime asOf = command.GetDateOr("as-of", DateTime.UtcNow.Date).Value;
            string? customerId = ResolveOptionalCustomer(command);
            IReadOnlyList<ReminderLetter> letters = _reminders.BuildAll(asOf, customerId);
            if (letters.Count == 0)
            {
                Console.WriteLine("No overdue invoices as of " + TableRenderer.FormatDate(asOf) + " — nothing to remind.");
                return 0;
            }

            string? outDir = command.GetStringOr("out", string.Empty);
            if (outDir.Length > 0)
            {
                JsonStore<List<Customer>>.EnsureDirectory(outDir);
                foreach (ReminderLetter letter in letters)
                {
                    string path = Path.Combine(outDir, letter.ToFileName());
                    File.WriteAllText(path, letter.Text, System.Text.Encoding.UTF8);
                    Console.WriteLine("Wrote " + path + " (" + letter.Severity + ", "
                        + letter.DaysOverdue + " days overdue, "
                        + Money.Format(letter.BalanceDue, letter.Currency) + ").");
                }
            }
            else
            {
                for (int i = 0; i < letters.Count; i++)
                {
                    if (i > 0)
                    {
                        Console.WriteLine();
                    }

                    Console.WriteLine("[draft letter " + (i + 1) + " of " + letters.Count + "]");
                    Console.WriteLine(letters[i].Text);
                }
            }

            Console.WriteLine();
            Console.WriteLine(letters.Count + " reminder letter(s) drafted.");
            return 0;
        }

        private string CustomerName(string customerId)
        {
            return _customers.FindById(customerId)?.Name ?? customerId;
        }

        private string? ResolveOptionalCustomer(ParsedCommand command)
        {
            string option = command.GetStringOr("customer", string.Empty);
            return option.Length == 0 ? null : _customers.RequireById(option).Id;
        }

        private static string DefaultExportName(string kind)
        {
            return kind + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";
        }

        private static List<string> ParseCategories(string text)
        {
            var categories = new List<string>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string canonical = ExemptionCategories.TryNormalize(part)
                    ?? throw new UsageException(
                        "Unknown exemption category '" + part.Trim() + "'. Known: "
                        + string.Join(", ", ExemptionCategories.All) + ".");
                if (!categories.Contains(canonical))
                {
                    categories.Add(canonical);
                }
            }

            return categories;
        }

        private static Dictionary<string, decimal> ParseReducedRates(string text)
        {
            var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (text.Length == 0)
            {
                return rates;
            }

            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0 || eq == part.Length - 1)
                {
                    throw new UsageException("Reduced rates must look like Food=5,Books=2 (got '" + part.Trim() + "').");
                }

                string key = part[..eq].Trim();
                string canonical = ExemptionCategories.TryNormalize(key)
                    ?? throw new UsageException(
                        "Unknown exemption category '" + key + "'. Known: "
                        + string.Join(", ", ExemptionCategories.All) + ".");
                string rawValue = part[(eq + 1)..].Trim();
                if (!Money.TryParseAmount(rawValue, out decimal rate) || rate < 0m || rate > 100m)
                {
                    throw new UsageException("Reduced rate for '" + canonical + "' must be a number between 0 and 100.");
                }

                rates[canonical] = rate;
            }

            return rates;
        }
    }
}
