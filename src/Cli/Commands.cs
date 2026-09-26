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
    /// Wires the repositories and services together and executes parsed commands.
    /// Split across Commands.cs (wiring, customers, invoices) and CommandModules.cs
    /// (payments, tax, reports, exports, reminders).
    /// </summary>
    public sealed partial class CliApp
    {
        private readonly string _dataDirectory;
        private readonly CustomerRepository _customers;
        private readonly InvoiceRepository _invoices;
        private readonly TaxCalculator _tax;
        private readonly ReportService _reports;
        private readonly CsvExporter _csv;
        private readonly ReminderService _reminders;
        private readonly JsonStore<List<TaxRule>> _taxStore;

        private CliApp(
            string dataDirectory,
            CustomerRepository customers,
            InvoiceRepository invoices,
            TaxCalculator tax,
            ReportService reports,
            CsvExporter csv,
            ReminderService reminders,
            JsonStore<List<TaxRule>> taxStore)
        {
            _dataDirectory = dataDirectory;
            _customers = customers;
            _invoices = invoices;
            _tax = tax;
            _reports = reports;
            _csv = csv;
            _reminders = reminders;
            _taxStore = taxStore;
        }

        /// <summary>Absolute path of the active data directory.</summary>
        public string DataDirectory => _dataDirectory;

        /// <summary>
        /// Resolves the data directory (option, environment, default), creates it and
        /// wires every repository and service, loading persisted tax overrides.
        /// </summary>
        /// <param name="command">Parsed command carrying global options.</param>
        /// <returns>The ready application.</returns>
        public static CliApp Create(ParsedCommand command)
        {
            string dataDirectory = ResolveDataDirectory(command.JsonDir);
            try
            {
                JsonStore<List<Customer>>.EnsureDirectory(dataDirectory);
            }
            catch (Exception ex)
            {
                throw new StorageException("Cannot create data directory '" + dataDirectory + "': " + ex.Message, ex);
            }

            var customerStore = new JsonStore<List<Customer>>(Path.Combine(dataDirectory, "customers.json"));
            var invoiceStore = new JsonStore<List<Invoice>>(Path.Combine(dataDirectory, "invoices.json"));
            var taxStore = new JsonStore<List<TaxRule>>(Path.Combine(dataDirectory, "taxrules.json"));

            var customers = new CustomerRepository(customerStore);
            var invoices = new InvoiceRepository(invoiceStore);
            var tax = new TaxCalculator(TaxCalculator.DefaultRules());
            foreach (TaxRule rule in taxStore.Load() ?? new List<TaxRule>())
            {
                tax.Register(rule);
            }

            var reports = new ReportService(invoices, customers);
            var csv = new CsvExporter();
            var reminders = new ReminderService(invoices, customers);
            return new CliApp(dataDirectory, customers, invoices, tax, reports, csv, reminders, taxStore);
        }

        /// <summary>Executes the parsed command and returns the exit code.</summary>
        /// <param name="command">Parsed command.</param>
        /// <returns>Zero on success.</returns>
        public int Run(ParsedCommand command)
        {
            // Flip past-due issued invoices to Overdue before anything reads them.
            _invoices.RefreshOverdue(DateTime.UtcNow.Date);

            switch (command.Command)
            {
                case "customer add":
                    return AddCustomer(command);
                case "customer list":
                    return ListCustomers(command);
                case "customer show":
                    return ShowCustomer(command);
                case "customer update":
                    return UpdateCustomer(command);
                case "customer delete":
                    return DeleteCustomer(command);
                case "invoice create":
                    return CreateInvoice(command);
                case "invoice add-item":
                    return AddInvoiceItem(command);
                case "invoice issue":
                    return IssueInvoice(command);
                case "invoice list":
                    return ListInvoices(command);
                case "invoice show":
                    return ShowInvoice(command);
                case "invoice void":
                    return VoidInvoice(command);
                case "help":
                    Console.WriteLine(HelpText.For(command.Positionals.Count > 0 ? command.Positionals[0] : null));
                    return 0;
                case "version":
                    Console.WriteLine(HelpText.Version());
                    return 0;
                default:
                    return RunModuleCommand(command);
            }
        }

        private int AddCustomer(ParsedCommand command)
        {
            var customer = new Customer
            {
                Name = command.RequireOption("name"),
                Email = command.GetStringOr("email", string.Empty),
                Phone = command.GetStringOr("phone", string.Empty),
                TaxId = command.GetStringOr("tax-id", string.Empty),
                Region = TaxCalculator.NormalizeRegion(command.GetStringOr("region", string.Empty)),
                Currency = command.GetStringOr("currency", "USD"),
                Notes = command.GetStringOr("notes", string.Empty),
                Address = BuildAddress(command),
            };

            customer = _customers.Add(customer);
            Console.WriteLine("Added customer " + customer.Id + " — " + customer.Name);
            Console.WriteLine("  Region   : " + Display(customer.Region));
            Console.WriteLine("  Currency : " + customer.Currency);
            Console.WriteLine("  Email    : " + Display(customer.Email));
            return 0;
        }

        private int ListCustomers(ParsedCommand command)
        {
            string? search = command.GetStringOr("search", string.Empty);
            IReadOnlyList<Customer> customers = search.Length == 0
                ? _customers.GetAll()
                : _customers.Search(search);
            if (customers.Count == 0)
            {
                Console.WriteLine("No customers found." + (search.Length > 0 ? " Search term: '" + search + "'." : string.Empty));
                return 0;
            }

            var rows = customers
                .Select(c => new IReadOnlyList<string?>[]
                {
                    new string?[] { c.Id, c.Name, Display(c.Region), c.Currency, Display(c.Email), TableRenderer.FormatDate(c.CreatedAtUtc) },
                })
                .SelectMany(r => r)
                .ToList();
            Console.WriteLine(TableRenderer.Render(
                new[] { "ID", "Name", "Region", "Currency", "Email", "Created" },
                rows));
            Console.WriteLine();
            Console.WriteLine(customers.Count + " customer(s).");
            return 0;
        }

        private int ShowCustomer(ParsedCommand command)
        {
            string id = command.PositionalAt(0, "customer-id");
            Customer customer = _customers.RequireById(id);
            IReadOnlyList<Invoice> invoices = _invoices.GetByCustomer(customer.Id);
            decimal outstanding = invoices
                .Where(i => i.IsOpen && i.BalanceDue() > 0m)
                .Sum(i => i.BalanceDue());
            int openCount = invoices.Count(i => i.IsOpen);

            Console.WriteLine("Customer " + customer.Id);
            Console.WriteLine("  Name     : " + customer.DisplayName);
            Console.WriteLine("  Email    : " + Display(customer.Email));
            Console.WriteLine("  Phone    : " + Display(customer.Phone));
            Console.WriteLine("  Tax ID   : " + Display(customer.TaxId));
            Console.WriteLine("  Region   : " + Display(customer.Region));
            Console.WriteLine("  Currency : " + customer.Currency);
            Console.WriteLine("  Address  : " + (customer.HasAddress ? customer.Address!.ToSingleLine() : "(none)"));
            Console.WriteLine("  Notes    : " + Display(customer.Notes));
            Console.WriteLine("  Created  : " + TableRenderer.FormatDate(customer.CreatedAtUtc));
            Console.WriteLine();
            Console.WriteLine("Invoices: " + invoices.Count + " total, " + openCount + " open, outstanding "
                + Money.Format(outstanding, customer.Currency) + ".");
            return 0;
        }

        private int UpdateCustomer(ParsedCommand command)
        {
            string id = command.PositionalAt(0, "customer-id");
            _customers.RequireById(id);
            Customer updated = _customers.Update(id, customer =>
            {
                if (command.HasOption("name"))
                {
                    customer.Name = command.RequireOption("name");
                }

                if (command.HasOption("email"))
                {
                    customer.Email = command.RequireOption("email");
                }

                if (command.HasOption("phone"))
                {
                    customer.Phone = command.RequireOption("phone");
                }

                if (command.HasOption("tax-id"))
                {
                    customer.TaxId = command.RequireOption("tax-id");
                }

                if (command.HasOption("region"))
                {
                    customer.Region = TaxCalculator.NormalizeRegion(command.RequireOption("region"));
                }

                if (command.HasOption("currency"))
                {
                    customer.Currency = command.RequireOption("currency").ToUpperInvariant();
                }

                if (command.HasOption("notes"))
                {
                    customer.Notes = command.RequireOption("notes");
                }

                if (HasAnyAddressOption(command))
                {
                    customer.Address = BuildAddress(command) ?? new Address();
                }
            });

            Console.WriteLine("Updated customer " + updated.Id + " — " + updated.DisplayName);
            return 0;
        }

        private int DeleteCustomer(ParsedCommand command)
        {
            string id = command.PositionalAt(0, "customer-id");
            _customers.RequireById(id);
            int invoiceCount = _invoices.GetByCustomer(id).Count;
            if (invoiceCount > 0)
            {
                throw new ConflictException(
                    "Customer " + id + " still has " + invoiceCount + " invoice(s). Void or export them first.");
            }

            if (!command.HasOption("force"))
            {
                throw new UsageException("Deleting a customer is permanent; re-run with --force to confirm.");
            }

            _customers.Delete(id);
            Console.WriteLine("Deleted customer " + id + ".");
            return 0;
        }

        private int CreateInvoice(ParsedCommand command)
        {
            string customerId = command.PositionalAt(0, "customer-id");
            Customer customer = _customers.RequireById(customerId);
            string currency = command.GetStringOr("currency", customer.Currency).ToUpperInvariant();
            if (!Money.IsSupported(currency))
            {
                throw new UsageException("Unsupported currency '" + currency + "'.");
            }

            string region = TaxCalculator.NormalizeRegion(command.GetStringOr("region", customer.Region));
            DateTime? due = command.GetDateOr("due", null);
            var draft = new Invoice
            {
                CustomerId = customer.Id,
                Currency = currency,
                Region = region,
                DueDateUtc = due,
                Notes = command.GetStringOr("notes", string.Empty),
                CreatedAtUtc = DateTime.UtcNow,
            };
            draft.Number = _invoices.NextNumber(DateTime.UtcNow.Year);
            Invoice created = _invoices.CreateDraft(draft);
            Console.WriteLine("Created draft invoice " + created.Number + " for " + customer.Id + " (" + customer.Name + ").");
            Console.WriteLine("Add items with: invoicemaster invoice add-item " + created.Number
                + " --description \"...\" --qty 1 --price 100");
            return 0;
        }

        private int AddInvoiceItem(ParsedCommand command)
        {
            string number = command.PositionalAt(0, "invoice-number");
            Invoice invoice = _invoices.RequireByNumber(number);
            string categoryText = command.GetStringOr("category", ExemptionCategories.Standard);
            string category = ExemptionCategories.TryNormalize(categoryText)
                ?? throw new UsageException(
                    "Unknown exemption category '" + categoryText + "'. Known: "
                    + string.Join(", ", ExemptionCategories.All) + ".");
            var item = new LineItem
            {
                Description = command.RequireOption("description"),
                Quantity = command.GetDecimalOr("qty", 1m),
                UnitPrice = command.GetDecimal("price"),
                DiscountPercent = command.GetDecimalOr("discount", 0m),
                ExemptionCategory = category,
            };
            Invoice updated = _invoices.AddItem(invoice.Number, item);
            Console.WriteLine("Added item to " + updated.Number + ": " + item.FormatQuantity() + " x "
                + item.Description + " — net " + Money.Format(item.ComputeNetAmount(), updated.Currency)
                + ", items now " + updated.Items.Count + ".");
            return 0;
        }

        private int IssueInvoice(ParsedCommand command)
        {
            string number = command.PositionalAt(0, "invoice-number");
            Invoice invoice = _invoices.RequireByNumber(number);
            DateTime issueDate = command.GetDateOr("date", DateTime.UtcNow.Date).Value;
            DateTime? due = command.GetDateOr("due", invoice.DueDateUtc);
            if (due is null)
            {
                throw new UsageException("Option --due is required because the draft has no due date yet.");
            }

            Invoice issued = _invoices.Issue(invoice.Number, issueDate, due.Value, _tax);
            InvoiceTotals totals = issued.ComputeTotals(issued.TaxAmountSnapshot ?? 0m);
            Console.WriteLine("Issued invoice " + issued.Number + " (due "
                + TableRenderer.FormatDate(issued.DueDateUtc) + ").");
            Console.WriteLine("  Subtotal : " + Money.Format(totals.Subtotal, issued.Currency));
            Console.WriteLine("  Tax      : " + Money.Format(totals.TaxAmount, issued.Currency));
            Console.WriteLine("  Total    : " + Money.Format(totals.GrandTotal, issued.Currency));
            return 0;
        }

        private int ListInvoices(ParsedCommand command)
        {
            InvoiceStatus? status = ParseStatusFilter(command.GetStringOr("status", string.Empty));
            string? customerId = null;
            string customerOption = command.GetStringOr("customer", string.Empty);
            if (customerOption.Length > 0)
            {
                customerId = _customers.RequireById(customerOption).Id;
            }

            int? year = ReadOptionalYear(command, "year");
            IReadOnlyList<Invoice> invoices = _invoices.Filter(status, customerId, year);
            if (invoices.Count == 0)
            {
                Console.WriteLine("No invoices match the given filters.");
                return 0;
            }

            var rows = new List<IReadOnlyList<string?>>();
            foreach (Invoice invoice in invoices)
            {
                InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
                rows.Add(new string?[]
                {
                    invoice.Number,
                    _customers.FindById(invoice.CustomerId)?.Name ?? invoice.CustomerId,
                    ColorStatus(invoice),
                    TableRenderer.FormatDate(invoice.IssueDateUtc),
                    TableRenderer.FormatDate(invoice.DueDateUtc),
                    Money.Format(totals.GrandTotal, invoice.Currency),
                    Money.Format(totals.PaidTotal, invoice.Currency),
                    Money.Format(totals.BalanceDue, invoice.Currency),
                });
            }

            Console.WriteLine(TableRenderer.Render(
                new[] { "Number", "Customer", "Status", "Issued", "Due", "Total", "Paid", "Balance" },
                rows,
                new[] { ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right }));
            decimal outstanding = invoices
                .Where(i => i.IsOpen)
                .Sum(i => Math.Max(0m, i.BalanceDue()));
            Console.WriteLine();
            Console.WriteLine(invoices.Count + " invoice(s), open balance " + Money.Format(outstanding, invoices[0].Currency) + ".");
            return 0;
        }

        private int ShowInvoice(ParsedCommand command)
        {
            string number = command.PositionalAt(0, "invoice-number");
            Invoice invoice = _invoices.RequireByNumber(number);
            Customer? customer = _customers.FindById(invoice.CustomerId);
            InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);

            Console.WriteLine("Invoice " + invoice.Number + "  [" + ColorStatus(invoice) + "]");
            Console.WriteLine("  Customer : " + invoice.CustomerId
                + (customer is null ? string.Empty : " (" + customer.Name + ")"));
            Console.WriteLine("  Currency : " + invoice.Currency);
            Console.WriteLine("  Region   : " + Display(invoice.Region));
            Console.WriteLine("  Issued   : " + Display(TableRenderer.FormatDate(invoice.IssueDateUtc)));
            Console.WriteLine("  Due      : " + Display(TableRenderer.FormatDate(invoice.DueDateUtc)));
            if (invoice.Status == InvoiceStatus.Void)
            {
                Console.WriteLine("  Voided   : " + Display(TableRenderer.FormatDate(invoice.VoidedAtUtc))
                    + " — " + Display(invoice.VoidedReason ?? string.Empty));
            }

            if (invoice.Notes.Length > 0)
            {
                Console.WriteLine("  Notes    : " + invoice.Notes);
            }

            Console.WriteLine();
            if (invoice.Items.Count == 0)
            {
                Console.WriteLine("(no line items yet)");
            }
            else
            {
                var rows = new List<IReadOnlyList<string?>>();
                for (int i = 0; i < invoice.Items.Count; i++)
                {
                    LineItem item = invoice.Items[i];
                    rows.Add(new string?[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        item.Description,
                        item.FormatQuantity(),
                        Money.FormatPlain(item.UnitPrice, invoice.Currency),
                        Money.FormatPercent(item.DiscountPercent),
                        Money.Format(item.ComputeNetAmount(), invoice.Currency),
                        item.ExemptionCategory,
                        PerLineTax(invoice, item),
                    });
                }

                Console.WriteLine(TableRenderer.Render(
                    new[] { "#", "Description", "Qty", "Unit", "Disc %", "Net", "Category", "Tax" },
                    rows,
                    new[] { ColumnAlignment.Right, ColumnAlignment.Left, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Right, ColumnAlignment.Left, ColumnAlignment.Right }));
            }

            Console.WriteLine();
            Console.WriteLine("  Subtotal  : " + Money.Format(totals.Subtotal, invoice.Currency));
            Console.WriteLine("  Discount  : " + Money.Format(totals.DiscountTotal, invoice.Currency));
            Console.WriteLine("  Tax       : " + (invoice.TaxAmountSnapshot is null
                ? "(tax computed at issue time)"
                : Money.Format(totals.TaxAmount, invoice.Currency)));
            Console.WriteLine("  Total     : " + Money.Format(totals.GrandTotal, invoice.Currency));
            Console.WriteLine("  Paid      : " + Money.Format(totals.PaidTotal, invoice.Currency));
            Console.WriteLine("  Balance   : " + Money.Format(totals.BalanceDue, invoice.Currency));
            if (invoice.Payments.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Payments:");
                foreach (Payment payment in invoice.Payments)
                {
                    Console.WriteLine("  " + payment.Id + "  " + TableRenderer.FormatDate(payment.PaidAtUtc)
                        + "  " + payment.MethodDisplay.PadRight(13)
                        + Money.Format(payment.Amount, payment.Currency)
                        + (payment.Reference.Length > 0 ? "  ref: " + payment.Reference : string.Empty));
                }
            }

            return 0;
        }

        private int VoidInvoice(ParsedCommand command)
        {
            string number = command.PositionalAt(0, "invoice-number");
            string reason = command.RequireOption("reason");
            Invoice voided = _invoices.Void(number, reason);
            Console.WriteLine("Voided invoice " + voided.Number + " — " + voided.VoidedReason);
            return 0;
        }

        private static string? BuildAddress(ParsedCommand command)
        {
            if (!HasAnyAddressOption(command))
            {
                return null;
            }

            return new Address
            {
                Street = command.GetStringOr("street", string.Empty),
                City = command.GetStringOr("city", string.Empty),
                State = command.GetStringOr("state", string.Empty),
                PostalCode = command.GetStringOr("zip", string.Empty),
                Country = command.GetStringOr("country", string.Empty),
            };
        }

        private static bool HasAnyAddressOption(ParsedCommand command)
        {
            return command.HasOption("street") || command.HasOption("city") || command.HasOption("state")
                || command.HasOption("zip") || command.HasOption("country");
        }

        private static InvoiceStatus? ParseStatusFilter(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "draft":
                    return InvoiceStatus.Draft;
                case "issued":
                    return InvoiceStatus.Issued;
                case "overdue":
                    return InvoiceStatus.Overdue;
                case "paid":
                    return InvoiceStatus.Paid;
                case "void":
                    return InvoiceStatus.Void;
                default:
                    throw new UsageException("Unknown status '" + text + "'. Use draft, issued, overdue, paid, void or all.");
            }
        }

        private static int? ReadOptionalYear(ParsedCommand command, string name)
        {
            if (!command.HasOption(name))
            {
                return null;
            }

            int year = command.GetIntOr(name, 0);
            if (year < 1000 || year > 9999)
            {
                throw new UsageException("Option --" + name + " must be a four-digit year.");
            }

            return year;
        }

        private static string ColorStatus(Invoice invoice)
        {
            string label = invoice.Status.ToString();
            switch (invoice.Status)
            {
                case InvoiceStatus.Overdue:
                    return Ansi.Warning(label);
                case InvoiceStatus.Paid:
                    return Ansi.Success(label);
                case InvoiceStatus.Void:
                    return Ansi.Faint(label);
                case InvoiceStatus.Draft:
                    return Ansi.Faint(label);
                default:
                    return label;
            }
        }

        private string PerLineTax(Invoice invoice, LineItem item)
        {
            if (invoice.Region.Length == 0 || !_tax.HasRule(invoice.Region))
            {
                return "-";
            }

            return Money.Format(_tax.ComputeLine(item, invoice.Region).TaxAmount, invoice.Currency);
        }

        private static string Display(string value)
        {
            return value.Length == 0 ? "(none)" : value;
        }

        private static string ResolveDataDirectory(string? explicitDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                return explicitDir;
            }

            string? fromEnvironment = Environment.GetEnvironmentVariable("INVOICEMASTER_HOME");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            return JsonStore<List<Customer>>.DefaultDataDirectory();
        }
    }
}
