using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InvoiceMaster;
using InvoiceMaster.Cli;
using InvoiceMaster.Models;
using InvoiceMaster.Services;

namespace InvoiceMaster.Tests
{
    /// <summary>
    /// Self-contained test runner: plain assert-based checks with zero test frameworks.
    /// Build and run with the companion csproj:
    ///   dotnet run --project tests/invoicemaster.Tests.csproj
    /// Exits 0 when everything passes, 1 otherwise, so it slots into CI directly.
    /// </summary>
    public static class TestRunner
    {
        private static int _passed;
        private static readonly List<string> Failures = new();
        private static readonly List<string> TempDirectories = new();

        /// <summary>Runs every test group and returns the process exit code.</summary>
        /// <returns>0 when all checks pass, otherwise 1.</returns>
        public static int Main()
        {
            try { RunMoneyTests(); } catch (Exception ex) { Failures.Add("money: " + ex.Message); Console.WriteLine("CRASH money: " + ex); }
            try { RunLineItemTests(); } catch (Exception ex) { Failures.Add("lineitem: " + ex.Message); Console.WriteLine("CRASH lineitem: " + ex); }
            try { RunTaxTests(); } catch (Exception ex) { Failures.Add("tax: " + ex.Message); Console.WriteLine("CRASH tax: " + ex); }
            try { RunNumberingTests(); } catch (Exception ex) { Failures.Add("numbering: " + ex.Message); Console.WriteLine("CRASH numbering: " + ex); }
            try { RunLifecycleTests(); } catch (Exception ex) { Failures.Add("lifecycle: " + ex.Message); Console.WriteLine("CRASH lifecycle: " + ex); }
            try { RunTotalsTests(); } catch (Exception ex) { Failures.Add("totals: " + ex.Message); Console.WriteLine("CRASH totals: " + ex); }
            try { RunAgingTests(); } catch (Exception ex) { Failures.Add("aging: " + ex.Message); Console.WriteLine("CRASH aging: " + ex); }
            try { RunCsvTests(); } catch (Exception ex) { Failures.Add("csv: " + ex.Message); Console.WriteLine("CRASH csv: " + ex); }
            try { RunTableTests(); } catch (Exception ex) { Failures.Add("table: " + ex.Message); Console.WriteLine("CRASH table: " + ex); }
            try { RunStoreTests(); } catch (Exception ex) { Failures.Add("store: " + ex.Message); Console.WriteLine("CRASH store: " + ex); }
            try { RunRepositoryTests(); } catch (Exception ex) { Failures.Add("repository: " + ex.Message); Console.WriteLine("CRASH repository: " + ex); }
            try { RunPaymentTests(); } catch (Exception ex) { Failures.Add("payment: " + ex.Message); Console.WriteLine("CRASH payment: " + ex); }

            Console.WriteLine();
            Console.WriteLine("=== invoicemaster test summary ===");
            Console.WriteLine("Passed: " + _passed);
            Console.WriteLine("Failed: " + Failures.Count);
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }

            CleanupTempDirectories();
            return Failures.Count == 0 ? 0 : 1;
        }

        private static void RunMoneyTests()
        {
            Console.WriteLine("--- money math ---");
            CheckEqual(2.35m, Money.Round(2.345m, 2), "Round half-up 2.345 -> 2.35");
            CheckEqual(2.34m, Money.Round(2.344m, 2), "Round down 2.344 -> 2.34");
            CheckEqual(-2.35m, Money.Round(-2.345m, 2), "Round half-up negative");
            CheckEqual(1m, Money.Round(1.004999m, 2), "Round 1.004999 -> 1.00");
            CheckEqual(2, Money.DecimalsOf("USD"), "USD has 2 decimals");
            CheckEqual(0, Money.DecimalsOf("VND"), "VND has 0 decimals");
            CheckEqual(0, Money.DecimalsOf("JPY"), "JPY has 0 decimals");
            CheckEqual(2, Money.DecimalsOf("ZZZ"), "unknown currency falls back to 2");
            CheckEqual("$1,234.50", Money.Format(1234.5m, "USD"), "Format USD");
            CheckEqual("-\u20AC12.30", Money.Format(-12.3m, "EUR"), "Format negative EUR");
            CheckEqual("\u20AB123,456", Money.Format(123456m, "VND"), "Format VND without decimals");
            CheckEqual("5.00 XYZ", Money.Format(5m, "XYZ"), "Format unknown currency code");
            Check(Money.TryParseAmount("1,234.56", out decimal parsed) && parsed == 1234.56m, "Parse '1,234.56'");
            Check(Money.TryParseAmount("$49.90", out decimal withSymbol) && withSymbol == 49.90m, "Parse '$49.90'");
            Check(Money.TryParseAmount("-3.5", out decimal negative) && negative == -3.5m, "Parse '-3.5'");
            Check(!Money.TryParseAmount("abc", out _), "Reject 'abc'");
            Check(!Money.TryParseAmount("", out _), "Reject empty string");
            CheckEqual(7.25m, Money.PercentOf(100m, 7.25m), "PercentOf 7.25% of 100");
            CheckEqual(10m, Money.FormatPercent(10.00m) is "10%" ? 10m : 0m, "FormatPercent trims zeros");
        }

        private static void RunLineItemTests()
        {
            Console.WriteLine("--- line item math ---");
            var item = new LineItem
            {
                Description = "Consulting",
                Quantity = 2m,
                UnitPrice = 50m,
                DiscountPercent = 10m,
            };
            CheckEqual(100m, item.GrossAmount, "Gross = qty x price");
            CheckEqual(10m, item.ComputeDiscountAmount(), "Discount 10% of 100");
            CheckEqual(90m, item.ComputeNetAmount(), "Net after discount");
            CheckEqual("2", item.FormatQuantity(), "Quantity formats without trailing zeros");
            var fractional = new LineItem { Description = "Hours", Quantity = 1.5m, UnitPrice = 33.333m };
            CheckEqual(33.33m, fractional.ComputeNetAmount(), "Net rounds to two decimals");
            Check(ExemptionCategories.IsKnown("food"), "Category match is case-insensitive");
            Check(ExemptionCategories.TryNormalize("EXPORT") == "Export", "TryNormalize returns canonical spelling");
            Check(!ExemptionCategories.IsKnown("Spaceships"), "Unknown category rejected");
            CheckEqual(0, item.Validate().Count, "Valid item passes validation");
            var invalid = new LineItem { Description = "", Quantity = 0m, UnitPrice = -1m, DiscountPercent = 150m };
            Check(invalid.Validate().Count >= 4, "Invalid item reports every problem");
        }

        private static void RunTaxTests()
        {
            Console.WriteLine("--- tax calculator ---");
            var tax = new TaxCalculator(TaxCalculator.DefaultRules());
            var standard = new LineItem { Description = "Gadget", Quantity = 1m, UnitPrice = 100m };
            var food = new LineItem { Description = "Groceries", Quantity = 1m, UnitPrice = 100m, ExemptionCategory = ExemptionCategories.Food };

            TaxLineResult standardResult = tax.ComputeLine(standard, "US-CA");
            CheckEqual(7.25m, standardResult.TaxAmount, "US-CA standard 7.25% on 100");
            CheckEqual(0m, tax.ComputeLine(food, "US-CA").TaxAmount, "US-CA food is exempt");
            Check(tax.ComputeLine(food, "US-CA").IsExempt, "Exempt flag set for food");

            TaxLineResult reduced = tax.ComputeLine(food, "DE");
            CheckEqual(7m, reduced.TaxAmount, "DE food reduced rate 7%");
            Check(!reduced.IsExempt, "DE food is reduced, not exempt");

            var precise = new LineItem { Description = "Part", Quantity = 1m, UnitPrice = 33.33m };
            CheckEqual(2.42m, tax.ComputeLine(precise, "US-CA").TaxAmount, "Per-line rounding 33.33 @ 7.25%");

            var second = new LineItem { Description = "Part", Quantity = 1m, UnitPrice = 33.33m };
            CheckEqual(4.84m, tax.ComputeInvoiceTax(new[] { precise, second }, "US-CA", "USD"), "Invoice tax sums rounded lines");

            TaxBreakdown breakdown = tax.ComputeBreakdown(new[] { standard, food }, "US-CA");
            CheckEqual(100m, breakdown.TaxableNet, "Breakdown taxable net");
            CheckEqual(100m, breakdown.ExemptNet, "Breakdown exempt net");
            CheckEqual(7.25m, breakdown.TaxAmount, "Breakdown tax total");
            CheckThrows<NotFoundException>(() => tax.ComputeLine(standard, "US-TX"), "Unknown region throws");
            Check(tax.HasRule("intl"), "INTL default rule registered");
            CheckEqual(0m, tax.ComputeLine(standard, "intl").TaxAmount, "INTL zero-percent fallback");

            var custom = new TaxRule
            {
                Region = "US-TX",
                DisplayName = "Texas sales tax",
                Currency = "USD",
                StandardRatePercent = 8.25m,
            };
            tax.Register(custom);
            CheckEqual(8.25m, tax.ComputeLine(standard, "us-tx").TaxAmount, "Custom rule registered case-insensitively");
            Check(custom.Validate().Count == 0, "Valid custom rule passes validation");
        }

        private static void RunNumberingTests()
        {
            Console.WriteLine("--- invoice numbering ---");
            CheckEqual("INV-2026-0001", Invoice.MakeNumber(2026, 1), "First number of the year");
            CheckEqual("INV-2026-12345", Invoice.MakeNumber(2026, 12345), "Sequence grows past four digits");
            CheckThrows<ArgumentOutOfRangeException>(() => Invoice.MakeNumber(2026, 0), "Sequence must start at 1");
            Check(Invoice.TryParseNumber("inv-2026-0007", out int year, out int sequence)
                && year == 2026 && sequence == 7, "TryParse is case-insensitive");
            Check(!Invoice.TryParseNumber("INV-26-0007", out _, out _), "Rejects two-digit year");
            Check(!Invoice.TryParseNumber("PAY-2026-0007", out _, out _), "Rejects wrong prefix");
            CheckThrows<ArgumentOutOfRangeException>(() => Invoice.MakeNumber(26, 1), "Year must be four digits");
        }

        private static void RunLifecycleTests()
        {
            Console.WriteLine("--- invoice lifecycle ---");
            Invoice invoice = NewDraft();
            var item = new LineItem { Description = "Widget", Quantity = 2m, UnitPrice = 50m };
            invoice.AddItem(item);
            CheckEqual(1, invoice.Items.Count, "Draft accepts items");

            var payment = new Payment
            {
                Id = "PAY-000001",
                InvoiceNumber = invoice.Number,
                Amount = 107.2m,
                Currency = "USD",
                PaidAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            CheckThrows<ConflictException>(() => invoice.ApplyPayment(payment), "Draft rejects payments");

            invoice.Issue(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc), 7.2m);
            Check(invoice.Status == InvoiceStatus.Issued, "Issue transitions to Issued");
            CheckEqual(7.2m, invoice.TaxAmountSnapshot ?? 0m, "Tax snapshot frozen at issue");
            CheckThrows<ValidationException>(
                () => invoice.Issue(new DateTime(2026, 1, 6), new DateTime(2026, 2, 6), 1m),
                "Issue twice is rejected");
            CheckThrows<ConflictException>(() => invoice.AddItem(item), "Issued invoice rejects new items");
            Check(!invoice.IsOverdue(new DateTime(2026, 2, 5)), "Not overdue on the due date");
            Check(invoice.IsOverdue(new DateTime(2026, 2, 10)), "Overdue past the due date");
            CheckEqual(6, invoice.DaysOverdue(new DateTime(2026, 2, 11)), "Days overdue counts date difference");

            invoice.ApplyPayment(payment);
            CheckEqual(0m, invoice.BalanceDue(), "Balance fully settled");
            Check(invoice.Status == InvoiceStatus.Paid, "Paid once balance reaches zero");
            Check(!invoice.IsOverdue(new DateTime(2026, 3, 1)), "Paid invoices are never overdue");
            CheckThrows<ConflictException>(
                () => invoice.Void("changed my mind", DateTime.UtcNow),
                "Paid invoices cannot be voided");
        }

        private static void RunTotalsTests()
        {
            Console.WriteLine("--- invoice totals ---");
            var items = new List<LineItem>
            {
                new LineItem { Description = "A", Quantity = 2m, UnitPrice = 50m, DiscountPercent = 10m },
                new LineItem { Description = "B", Quantity = 1m, UnitPrice = 33.333m },
            };
            var payments = new List<Payment>
            {
                new Payment { Id = "PAY-000001", InvoiceNumber = "INV-2026-0001", Amount = 40m, Currency = "USD", PaidAtUtc = DateTime.UtcNow },
            };
            InvoiceTotals totals = InvoiceTotals.Compute(items, 8m, payments);
            CheckEqual(2, totals.ItemCount, "Item count");
            CheckEqual(133.33m, totals.GrossTotal, "Gross total");
            CheckEqual(10m, totals.DiscountTotal, "Discount total");
            CheckEqual(123.33m, totals.Subtotal, "Subtotal after discounts");
            CheckEqual(8m, totals.TaxAmount, "Tax amount");
            CheckEqual(131.33m, totals.GrandTotal, "Grand total");
            CheckEqual(40m, totals.PaidTotal, "Paid total");
            CheckEqual(91.33m, totals.BalanceDue, "Balance due");
        }

        private static void RunAgingTests()
        {
            Console.WriteLine("--- aging buckets ---");
            CheckEqual("Current", ReportService.BucketLabelFor(0), "Day 0 is Current");
            CheckEqual("Current", ReportService.BucketLabelFor(-5), "Negative days are Current");
            CheckEqual("1-30", ReportService.BucketLabelFor(1), "Day 1 enters 1-30");
            CheckEqual("1-30", ReportService.BucketLabelFor(30), "Day 30 stays 1-30");
            CheckEqual("31-60", ReportService.BucketLabelFor(31), "Day 31 enters 31-60");
            CheckEqual("31-60", ReportService.BucketLabelFor(60), "Day 60 stays 31-60");
            CheckEqual("61-90", ReportService.BucketLabelFor(61), "Day 61 enters 61-90");
            CheckEqual("61-90", ReportService.BucketLabelFor(90), "Day 90 stays 61-90");
            CheckEqual("90+", ReportService.BucketLabelFor(91), "Day 91 enters 90+");
            CheckEqual("90+", ReportService.BucketLabelFor(365), "Day 365 stays 90+");

            string dir = NewTempDirectory();
            CustomerRepository customers = NewCustomerRepository(dir);
            InvoiceRepository invoices = NewInvoiceRepository(dir);
            var tax = new TaxCalculator(TaxCalculator.DefaultRules());
            Customer customer = customers.Add(new Customer { Name = "Aging Co", Currency = "USD", Region = "US-CA" });
            DateTime asOf = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
            int[] daysOverdue = { 10, 45, 75, 120 };
            foreach (int days in daysOverdue)
            {
                var draft = new Invoice
                {
                    Number = invoices.NextNumber(2026),
                    CustomerId = customer.Id,
                    Currency = "USD",
                    Region = "US-CA",
                    CreatedAtUtc = asOf.AddDays(-200),
                };
                invoices.CreateDraft(draft);
                invoices.AddItem(draft.Number, new LineItem { Description = "Work", Quantity = 1m, UnitPrice = 100m });
                invoices.Issue(draft.Number, asOf.AddDays(-days - 5), asOf.AddDays(-days), tax);
            }

            AgingReport report = new ReportService(invoices, customers).BuildAging(asOf, null);
            CheckEqual(1, report.Buckets.Single(b => b.Label == "1-30").InvoiceCount, "Bucket 1-30 holds one invoice");
            CheckEqual(1, report.Buckets.Single(b => b.Label == "31-60").InvoiceCount, "Bucket 31-60 holds one invoice");
            CheckEqual(1, report.Buckets.Single(b => b.Label == "61-90").InvoiceCount, "Bucket 61-90 holds one invoice");
            CheckEqual(1, report.Buckets.Single(b => b.Label == "90+").InvoiceCount, "Bucket 90+ holds one invoice");
            CheckEqual(0, report.Buckets.Single(b => b.Label == "Current").InvoiceCount, "Bucket Current is empty");
            CheckEqual(4 * 107.25m, report.TotalOutstanding, "Aging totals the balances (100 + 7.25 tax)");
            CheckEqual(4, report.Rows.Count, "Aging detail lists every invoice");
        }

        private static void RunCsvTests()
        {
            Console.WriteLine("--- csv escaping ---");
            CheckEqual("plain", Csv.EscapeField("plain"), "Plain field unchanged");
            CheckEqual("\"a,b\"", Csv.EscapeField("a,b"), "Comma triggers quoting");
            CheckEqual("\"say \"\"hi\"\"\"", Csv.EscapeField("say \"hi\""), "Quotes are doubled");
            CheckEqual("\"line1\nline2\"", Csv.EscapeField("line1\nline2"), "Newline triggers quoting");
            CheckEqual(string.Empty, Csv.EscapeField(null), "Null renders as empty field");
            CheckEqual("a,\"b,c\",,\"d\"\"e\"", Csv.WriteRow(new string?[] { "a", "b,c", null, "d\"e" }), "Row joins escaped fields");
        }

        private static void RunTableTests()
        {
            Console.WriteLine("--- table renderer ---");
            string table = TableRenderer.Render(
                new[] { "AB", "C" },
                new List<IReadOnlyList<string?>> { new string?[] { "x", "y" } });
            string firstLine = table.Split('\n')[0].TrimEnd('\r');
            Check(firstLine.StartsWith("AB  C", StringComparison.Ordinal), "Header uses computed widths");
            string[] lines = table.Split('\n');
            Check(lines.Length == 4, "Header, separator and one data row");
            Check(lines[1].TrimEnd('\r').StartsWith("--", StringComparison.Ordinal), "Separator under the header");
            CheckEqual("abc...", TableRenderer.Truncate("abcdefgh", 6), "Truncate appends ellipsis");
            CheckEqual("ab", TableRenderer.Truncate("abcdefgh", 2), "Truncate honors tiny widths");
            string rightAligned = TableRenderer.Render(
                new[] { "N" },
                new List<IReadOnlyList<string?>> { new string?[] { "12" }, new string?[] { "7" } },
                new[] { ColumnAlignment.Right });
            string[] rightLines = rightAligned.Split('\n');
            Check(rightLines[3].TrimEnd('\r').EndsWith(" 7", StringComparison.Ordinal), "Right alignment pads on the left");
        }

        private static void RunStoreTests()
        {
            Console.WriteLine("--- json store ---");
            string dir = NewTempDirectory();
            string path = Path.Combine(dir, "customers.json");
            var store = new JsonStore<List<Customer>>(path, 3);
            Check(store.Load() is null, "Missing file loads as null");
            Check(!store.Exists, "Exists is false before the first save");

            var original = new List<Customer>
            {
                new Customer { Id = "CUS-0001", Name = "Acme Corp", Currency = "USD" },
            };
            store.Save(original);
            Check(store.Exists, "Exists is true after save");
            List<Customer>? loaded = store.Load();
            Check(loaded is not null && loaded.Count == 1 && loaded[0].Name == "Acme Corp", "Round trip preserves data");

            store.Save(new List<Customer>
            {
                new Customer { Id = "CUS-0001", Name = "Acme Corp", Currency = "USD" },
                new Customer { Id = "CUS-0002", Name = "Beta LLC", Currency = "EUR" },
            });
            CheckEqual(1, store.BackupFiles().Count, "Second save creates one backup");

            File.WriteAllText(path, "{ this is not json");
            List<Customer>? recovered = store.Load();
            Check(recovered is not null && recovered.Count == 2, "Corrupt file recovers from backup");

            string secondStorePath = Path.Combine(dir, "nested", "deeper.json");
            var nestedStore = new JsonStore<List<Customer>>(secondStorePath, 0);
            nestedStore.Save(original);
            Check(nestedStore.Load() is not null, "Store creates missing directories");
        }

        private static void RunRepositoryTests()
        {
            Console.WriteLine("--- repositories ---");
            string dir = NewTempDirectory();
            CustomerRepository customers = NewCustomerRepository(dir);
            InvoiceRepository invoices = NewInvoiceRepository(dir);

            Customer customer = customers.Add(new Customer { Name = "Repo Co", Email = "billing@repo.example", Currency = "USD", Region = "US-CA" });
            CheckEqual("CUS-0001", customer.Id, "First customer id is CUS-0001");
            CheckEqual("CUS-0002", customers.NextId(), "Next id increments");
            CheckThrows<ConflictException>(
                () => customers.Add(new Customer { Name = "Twin", Email = "billing@repo.example" }),
                "Duplicate emails are rejected");
            CheckThrows<ValidationException>(
                () => customers.Add(new Customer { Name = string.Empty }),
                "Blank names are rejected");

            Customer renamed = customers.Update(customer.Id, c => c.Name = "Repo Renamed");
            CheckEqual("Repo Renamed", renamed.Name, "Update applies mutations");
            CheckEqual("Repo Renamed", customers.RequireById(customer.Id).Name, "Update persists");

            var tax = new TaxCalculator(TaxCalculator.DefaultRules());
            var draft = new Invoice
            {
                Number = invoices.NextNumber(2026),
                CustomerId = customer.Id,
                Currency = "USD",
                Region = "US-CA",
                CreatedAtUtc = DateTime.UtcNow,
            };
            invoices.CreateDraft(draft);
            CheckEqual("INV-2026-0001", draft.Number, "First invoice number of the year");
            CheckEqual("INV-2026-0002", invoices.NextNumber(2026), "Numbering advances after drafts");
            CheckThrows<ConflictException>(() => invoices.CreateDraft(draft), "Duplicate numbers rejected");

            invoices.AddItem(draft.Number, new LineItem { Description = "Work", Quantity = 1m, UnitPrice = 200m });
            Invoice issued = invoices.Issue(draft.Number, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(30), tax);
            CheckEqual(14.5m, issued.TaxAmountSnapshot ?? 0m, "Issued snapshot uses US-CA 7.25% on 200");
            Check(issued.Status == InvoiceStatus.Issued, "Repo issue persists the transition");

            var payment = new Payment
            {
                Amount = 214.50m,
                Currency = "USD",
                PaidAtUtc = DateTime.UtcNow,
                Reference = "TRX-1",
            };
            Invoice paid = invoices.RecordPayment(draft.Number, payment, allowOverpay: false);
            Check(paid.Status == InvoiceStatus.Paid, "Full payment flips to Paid");
            Check(paid.Payments[0].Id == "PAY-000001", "Payment id auto-generated");
            CheckThrows<ConflictException>(
                () => invoices.RecordPayment(draft.Number, new Payment { Amount = 5m, Currency = "USD", PaidAtUtc = DateTime.UtcNow }, false),
                "Paid invoices reject payments");

            IReadOnlyList<PaymentRecord> records = invoices.GetPayments(null, null);
            CheckEqual(1, records.Count, "Payment listing finds the payment");
            CheckEqual(0, invoices.RefreshOverdue(DateTime.UtcNow.Date), "Paid invoices never refresh to overdue");
        }

        private static void RunPaymentTests()
        {
            Console.WriteLine("--- payment helpers ---");
            Check(Payment.TryParseMethod("wire", out PaymentMethod wire) && wire == PaymentMethod.BankTransfer, "'wire' maps to BankTransfer");
            Check(Payment.TryParseMethod("CHEQUE", out PaymentMethod cheque) && cheque == PaymentMethod.Check, "'CHEQUE' maps to Check");
            Check(Payment.TryParseMethod("cash", out PaymentMethod cash) && cash == PaymentMethod.Cash, "'cash' maps to Cash");
            Check(!Payment.TryParseMethod("barter", out _), "Unknown methods rejected");
            CheckEqual("PAY-000009", Payment.FormatId(9), "Payment ids are zero padded");
            Check(!Payment.IdPattern.IsMatch("PAY-1"), "Short payment ids rejected");
        }

        private static Invoice NewDraft()
        {
            return new Invoice
            {
                Number = Invoice.MakeNumber(2026, 1),
                CustomerId = "CUS-0001",
                Currency = "USD",
                Region = "US-CA",
                CreatedAtUtc = DateTime.UtcNow,
            };
        }

        private static CustomerRepository NewCustomerRepository(string dir)
        {
            return new CustomerRepository(new JsonStore<List<Customer>>(Path.Combine(dir, "customers.json"), 2));
        }

        private static InvoiceRepository NewInvoiceRepository(string dir)
        {
            return new InvoiceRepository(new JsonStore<List<Invoice>>(Path.Combine(dir, "invoices.json"), 2));
        }

        private static string NewTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "invoicemaster-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            TempDirectories.Add(dir);
            return dir;
        }

        private static void CleanupTempDirectories()
        {
            foreach (string dir in TempDirectories)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best effort cleanup; temp dirs are harmless.
                }
            }
        }

        private static void Check(bool condition, string name)
        {
            if (condition)
            {
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                Failures.Add(name);
                Console.WriteLine("FAIL " + name);
            }
        }

        private static void CheckEqual<T>(T expected, T actual, string name)
            where T : notnull
        {
            if (Equals(expected, actual))
            {
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                Failures.Add(name);
                Console.WriteLine("FAIL " + name + " — expected [" + expected + "] got [" + actual + "]");
            }
        }

        private static void CheckThrows<TException>(Action action, string name)
            where TException : Exception
        {
            try
            {
                action();
                Failures.Add(name);
                Console.WriteLine("FAIL " + name + " — expected " + typeof(TException).Name + " but nothing was thrown");
            }
            catch (TException)
            {
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                Failures.Add(name);
                Console.WriteLine("FAIL " + name + " — wrong exception " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
