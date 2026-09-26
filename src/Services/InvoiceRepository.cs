using System;
using System.Collections.Generic;
using System.Linq;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>A payment joined with the invoice it settles, for listings and exports.</summary>
    public sealed record PaymentRecord(Invoice Invoice, Payment Payment);

    /// <summary>
    /// Repository over <c>invoices.json</c>: numbering per year, lifecycle transitions,
    /// payment recording and the periodic overdue maintenance pass. Mutating methods
    /// persist atomically on success.
    /// </summary>
    public sealed class InvoiceRepository
    {
        private readonly JsonStore<List<Invoice>> _store;
        private readonly object _sync = new();
        private List<Invoice> _invoices;

        /// <summary>
        /// Creates the repository and loads existing invoices from the store.
        /// </summary>
        /// <param name="store">JSON store pointing at invoices.json.</param>
        public InvoiceRepository(JsonStore<List<Invoice>> store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _invoices = store.Load() ?? new List<Invoice>();
        }

        /// <summary>Path of the underlying invoices.json file.</summary>
        public string FilePath => _store.FilePath;

        /// <summary>Number of invoices currently stored (all statuses).</summary>
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _invoices.Count;
                }
            }
        }

        /// <summary>Returns all invoices sorted by number, newest sequence last.</summary>
        /// <returns>Snapshot list safe to iterate while the store changes.</returns>
        public IReadOnlyList<Invoice> GetAll()
        {
            lock (_sync)
            {
                return _invoices
                    .OrderBy(i => i.Number, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>Finds an invoice by exact number (case-insensitive).</summary>
        /// <param name="number">Invoice number such as "INV-2026-0001".</param>
        /// <returns>The invoice, or null when not found.</returns>
        public Invoice? FindByNumber(string? number)
        {
            if (string.IsNullOrWhiteSpace(number))
            {
                return null;
            }

            string needle = number.Trim().ToUpperInvariant();
            lock (_sync)
            {
                return _invoices.FirstOrDefault(i => string.Equals(i.Number, needle, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Finds an invoice by number or throws a not-found error.</summary>
        /// <param name="number">Invoice number such as "INV-2026-0001".</param>
        /// <returns>The matching invoice.</returns>
        public Invoice RequireByNumber(string? number)
        {
            Invoice? found = FindByNumber(number);
            if (found is null)
            {
                throw NotFoundException.For(
                    "invoice",
                    (number ?? string.Empty).Trim(),
                    "Run 'invoicemaster invoice list' to see existing invoices.");
            }

            return found;
        }

        /// <summary>Returns every invoice of one customer, sorted by number.</summary>
        /// <param name="customerId">Customer identifier.</param>
        /// <returns>Matching invoices.</returns>
        public IReadOnlyList<Invoice> GetByCustomer(string customerId)
        {
            lock (_sync)
            {
                return _invoices
                    .Where(i => string.Equals(i.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.Number, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>Applies the standard list filters in one pass.</summary>
        /// <param name="status">Status filter; null means every status.</param>
        /// <param name="customerId">Customer filter; null means every customer.</param>
        /// <param name="year">Invoice year filter (from the number); null means every year.</param>
        /// <returns>Matching invoices sorted by number.</returns>
        public IReadOnlyList<Invoice> Filter(InvoiceStatus? status, string? customerId, int? year)
        {
            lock (_sync)
            {
                return _invoices
                    .Where(i => status is null || i.Status == status.Value)
                    .Where(i => customerId is null
                        || string.Equals(i.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                    .Where(i => !year.HasValue
                        || (Invoice.TryParseNumber(i.Number, out int invoiceYear, out _) && invoiceYear == year.Value))
                    .OrderBy(i => i.Number, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>
        /// Computes the next free invoice number for a year by scanning stored numbers,
        /// so deleted drafts never cause number reuse.
        /// </summary>
        /// <param name="year">Invoice year, e.g. 2026.</param>
        /// <returns>Number such as "INV-2026-0007".</returns>
        public string NextNumber(int year)
        {
            lock (_sync)
            {
                int max = 0;
                foreach (Invoice invoice in _invoices)
                {
                    if (Invoice.TryParseNumber(invoice.Number, out int invoiceYear, out int sequence)
                        && invoiceYear == year
                        && sequence > max)
                    {
                        max = sequence;
                    }
                }

                return Invoice.MakeNumber(year, max + 1);
            }
        }

        /// <summary>Checks whether an invoice number is already taken.</summary>
        /// <param name="number">Invoice number to look up.</param>
        /// <returns>True when the number exists.</returns>
        public bool NumberExists(string number)
        {
            return FindByNumber(number) is not null;
        }

        /// <summary>
        /// Stores a prepared draft invoice. The number must already be assigned.
        /// </summary>
        /// <param name="draft">Invoice with number, customer, currency and region set.</param>
        /// <returns>The stored draft.</returns>
        public Invoice CreateDraft(Invoice draft)
        {
            if (draft is null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            lock (_sync)
            {
                var errors = new List<string>(draft.Validate());
                if (errors.Count > 0)
                {
                    throw ValidationException.ForErrors("The draft invoice cannot be created:", errors);
                }

                if (FindNumberLocked(draft.Number) is not null)
                {
                    throw new ConflictException("Invoice number '" + draft.Number + "' is already in use.");
                }

                _invoices.Add(draft);
                Persist();
                return draft;
            }
        }

        /// <summary>
        /// Appends a validated line item to a draft invoice.
        /// </summary>
        /// <param name="number">Invoice number.</param>
        /// <param name="item">Line item to append.</param>
        /// <returns>The updated invoice.</returns>
        public Invoice AddItem(string number, LineItem item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            lock (_sync)
            {
                Invoice invoice = RequireByNumber(number);
                invoice.AddItem(item);
                Persist();
                return invoice;
            }
        }

        /// <summary>
        /// Removes a zero-based line item from a draft invoice.
        /// </summary>
        /// <param name="number">Invoice number.</param>
        /// <param name="index">Zero-based item index.</param>
        /// <returns>The updated invoice.</returns>
        public Invoice RemoveItem(string number, int index)
        {
            lock (_sync)
            {
                Invoice invoice = RequireByNumber(number);
                invoice.RemoveItemAt(index);
                Persist();
                return invoice;
            }
        }

        /// <summary>
        /// Issues a draft: validates it, computes the tax snapshot with the current
        /// rules and freezes the document.
        /// </summary>
        /// <param name="number">Invoice number.</param>
        /// <param name="issueUtc">Issue date (UTC).</param>
        /// <param name="dueUtc">Due date (UTC).</param>
        /// <param name="tax">Tax calculator holding the region rules.</param>
        /// <returns>The issued invoice.</returns>
        public Invoice Issue(string number, DateTime issueUtc, DateTime dueUtc, TaxCalculator tax)
        {
            if (tax is null)
            {
                throw new ArgumentNullException(nameof(tax));
            }

            lock (_sync)
            {
                Invoice invoice = RequireByNumber(number);
                if (string.IsNullOrWhiteSpace(invoice.Region))
                {
                    throw new ValidationException(
                        "Invoice " + invoice.Number + " has no tax region. Pass --region or set one on the customer, then try again.");
                }

                if (!tax.HasRule(invoice.Region))
                {
                    throw NotFoundException.For(
                        "tax rule",
                        invoice.Region,
                        "Register one with 'invoicemaster tax set --region " + invoice.Region + " --rate ...' first.");
                }

                decimal taxAmount = tax.ComputeInvoiceTax(invoice.Items, invoice.Region, invoice.Currency);
                invoice.Issue(issueUtc, dueUtc, taxAmount);
                Persist();
                return invoice;
            }
        }

        /// <summary>
        /// Records a payment against an open invoice, generating the payment id,
        /// checking currency and (unless overpaying is allowed) capping at the balance.
        /// </summary>
        /// <param name="number">Invoice number.</param>
        /// <param name="payment">Payment without id; id is assigned here.</param>
        /// <param name="allowOverpay">True permits amounts above the balance (credit).</param>
        /// <returns>The updated invoice.</returns>
        public Invoice RecordPayment(string number, Payment payment, bool allowOverpay)
        {
            if (payment is null)
            {
                throw new ArgumentNullException(nameof(payment));
            }

            lock (_sync)
            {
                Invoice invoice = RequireByNumber(number);
                payment.InvoiceNumber = invoice.Number;
                payment.Id = NextPaymentIdLocked();
                payment.RecordedAtUtc = DateTime.UtcNow;

                var errors = new List<string>(payment.Validate());
                if (errors.Count > 0)
                {
                    throw ValidationException.ForErrors("The payment cannot be recorded:", errors);
                }

                if (!string.Equals(invoice.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ValidationException(
                        "Payment currency " + payment.Currency + " does not match invoice currency " + invoice.Currency + ".");
                }

                if (!invoice.IsOpen)
                {
                    throw new ConflictException(
                        "Invoice " + invoice.Number + " is " + invoice.Status + "; payments can only be recorded on issued or overdue invoices.");
                }

                decimal balance = invoice.BalanceDue();
                if (!allowOverpay && payment.Amount > balance)
                {
                    throw new ConflictException(
                        "Payment " + Money.FormatPlain(payment.Amount, payment.Currency)
                        + " exceeds the outstanding balance " + Money.FormatPlain(balance, invoice.Currency)
                        + ". Pass --allow-overpay to record the excess as credit.");
                }

                invoice.ApplyPayment(payment);
                Persist();
                return invoice;
            }
        }

        /// <summary>
        /// Voids an invoice with a mandatory reason.
        /// </summary>
        /// <param name="number">Invoice number.</param>
        /// <param name="reason">Why the invoice is cancelled.</param>
        /// <returns>The voided invoice.</returns>
        public Invoice Void(string number, string reason)
        {
            lock (_sync)
            {
                Invoice invoice = RequireByNumber(number);
                invoice.Void(reason, DateTime.UtcNow);
                Persist();
                return invoice;
            }
        }

        /// <summary>
        /// Maintenance pass: flips issued invoices that are past due with an open
        /// balance to Overdue and persists when anything changed.
        /// </summary>
        /// <param name="asOfUtc">Reference date (UTC).</param>
        /// <returns>Number of invoices transitioned to Overdue.</returns>
        public int RefreshOverdue(DateTime asOfUtc)
        {
            lock (_sync)
            {
                int changed = 0;
                foreach (Invoice invoice in _invoices)
                {
                    if (invoice.Status == InvoiceStatus.Issued
                        && invoice.DueDateUtc is not null
                        && invoice.DueDateUtc.Value.Date < asOfUtc.Date
                        && invoice.BalanceDue() > 0m)
                    {
                        invoice.Status = InvoiceStatus.Overdue;
                        changed++;
                    }
                }

                if (changed > 0)
                {
                    Persist();
                }

                return changed;
            }
        }

        /// <summary>Open invoices with a positive balance as of the reference date.</summary>
        /// <param name="asOfUtc">Reference date (UTC).</param>
        /// <param name="customerId">Optional customer filter.</param>
        /// <returns>Matching invoices sorted by due date.</returns>
        public IReadOnlyList<Invoice> GetOutstanding(DateTime asOfUtc, string? customerId)
        {
            lock (_sync)
            {
                return _invoices
                    .Where(i => i.IsOpen && i.BalanceDue() > 0m)
                    .Where(i => customerId is null
                        || string.Equals(i.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => i.DueDateUtc ?? DateTime.MaxValue)
                    .ThenBy(i => i.Number, StringComparer.Ordinal)
                    .ToList();
            }
        }

        /// <summary>
        /// Flattens payments across all non-void invoices with optional filters,
        /// sorted by payment date then id.
        /// </summary>
        /// <param name="customerId">Optional customer filter.</param>
        /// <param name="year">Optional payment year filter.</param>
        /// <returns>Joined payment records.</returns>
        public IReadOnlyList<PaymentRecord> GetPayments(string? customerId, int? year)
        {
            lock (_sync)
            {
                var records = new List<PaymentRecord>();
                foreach (Invoice invoice in _invoices)
                {
                    if (invoice.Status == InvoiceStatus.Void)
                    {
                        continue;
                    }

                    if (customerId is not null
                        && !string.Equals(invoice.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (Payment payment in invoice.Payments)
                    {
                        if (year.HasValue && payment.PaidAtUtc.Year != year.Value)
                        {
                            continue;
                        }

                        records.Add(new PaymentRecord(invoice, payment));
                    }
                }

                records.Sort((left, right) =>
                {
                    int byDate = left.Payment.PaidAtUtc.CompareTo(right.Payment.PaidAtUtc);
                    return byDate != 0
                        ? byDate
                        : string.CompareOrdinal(left.Payment.Id, right.Payment.Id);
                });
                return records;
            }
        }

        private Invoice? FindNumberLocked(string number)
        {
            string needle = number.Trim().ToUpperInvariant();
            return _invoices.FirstOrDefault(i => string.Equals(i.Number, needle, StringComparison.OrdinalIgnoreCase));
        }

        private string NextPaymentIdLocked()
        {
            int max = 0;
            foreach (Invoice invoice in _invoices)
            {
                foreach (Payment payment in invoice.Payments)
                {
                    if (Payment.IdPattern.IsMatch(payment.Id)
                        && int.TryParse(payment.Id.AsSpan(4), out int sequence)
                        && sequence > max)
                    {
                        max = sequence;
                    }
                }
            }

            return Payment.FormatId(max + 1);
        }

        private void Persist()
        {
            _store.Save(_invoices);
        }
    }
}
