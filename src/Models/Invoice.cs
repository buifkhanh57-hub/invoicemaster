using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InvoiceMaster.Models
{
    /// <summary>Lifecycle state of an invoice: Draft, Issued, Overdue, Paid or Void.</summary>
    public enum InvoiceStatus
    {
        /// <summary>Being edited; no tax snapshot, payments not allowed.</summary>
        Draft,

        /// <summary>Sent to the customer; payments accepted.</summary>
        Issued,

        /// <summary>Issued and past its due date with an open balance.</summary>
        Overdue,

        /// <summary>Fully settled.</summary>
        Paid,

        /// <summary>Cancelled after creation; excluded from reports.</summary>
        Void,
    }

    /// <summary>
    /// Immutable result of summing an invoice: line totals, tax, payments and balance.
    /// </summary>
    public sealed class InvoiceTotals
    {
        /// <summary>Number of line items included.</summary>
        public int ItemCount { get; private set; }

        /// <summary>Sum of quantity x price before discounts.</summary>
        public decimal GrossTotal { get; private set; }

        /// <summary>Sum of line discounts.</summary>
        public decimal DiscountTotal { get; private set; }

        /// <summary>Sum of net line amounts (after discounts).</summary>
        public decimal Subtotal { get; private set; }

        /// <summary>Tax amount supplied by the tax engine.</summary>
        public decimal TaxAmount { get; private set; }

        /// <summary>Subtotal plus tax.</summary>
        public decimal GrandTotal { get; private set; }

        /// <summary>Sum of recorded payments.</summary>
        public decimal PaidTotal { get; private set; }

        /// <summary>Grand total minus payments; negative means overpaid credit.</summary>
        public decimal BalanceDue { get; private set; }

        /// <summary>
        /// Computes totals for a set of items, a tax amount and the recorded payments.
        /// </summary>
        /// <param name="items">Line items to sum.</param>
        /// <param name="taxAmount">Tax to add (the issued snapshot, or 0 for drafts).</param>
        /// <param name="payments">Payments recorded on the invoice.</param>
        /// <returns>Fully populated totals object.</returns>
        public static InvoiceTotals Compute(IEnumerable<LineItem> items, decimal taxAmount, IReadOnlyList<Payment> payments)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (payments is null)
            {
                throw new ArgumentNullException(nameof(payments));
            }

            var totals = new InvoiceTotals();
            decimal gross = 0m;
            decimal discount = 0m;
            decimal net = 0m;
            foreach (LineItem item in items)
            {
                gross += item.GrossAmount;
                discount += item.ComputeDiscountAmount();
                net += item.ComputeNetAmount();
                totals.ItemCount++;
            }

            totals.GrossTotal = Money.RoundCurrency(gross);
            totals.DiscountTotal = Money.RoundCurrency(discount);
            totals.Subtotal = Money.RoundCurrency(net);
            totals.TaxAmount = Money.RoundCurrency(taxAmount);
            totals.GrandTotal = Money.RoundCurrency(totals.Subtotal + totals.TaxAmount);

            decimal paid = 0m;
            foreach (Payment payment in payments)
            {
                paid += payment.Amount;
            }

            totals.PaidTotal = Money.RoundCurrency(paid);
            totals.BalanceDue = Money.RoundCurrency(totals.GrandTotal - totals.PaidTotal);
            return totals;
        }
    }

    /// <summary>
    /// The invoice aggregate: numbered document, line items, payments and lifecycle.
    /// Numbers are per-year sequences like INV-2026-0001; the tax amount is snapshotted
    /// when the invoice is issued so later tax-rule edits never change history.
    /// </summary>
    public sealed class Invoice
    {
        /// <summary>Shape of a valid invoice number, e.g. "INV-2026-0001".</summary>
        public static readonly Regex NumberPattern =
            new(@"^INV-(\d{4})-(\d{1,})$", RegexOptions.Compiled);

        /// <summary>Unique document number, e.g. "INV-2026-0001".</summary>
        public string Number { get; set; } = string.Empty;

        /// <summary>Identifier of the billed customer.</summary>
        public string CustomerId { get; set; } = string.Empty;

        /// <summary>ISO 4217 currency of every amount on this invoice.</summary>
        public string Currency { get; set; } = "USD";

        /// <summary>Tax region snapshot taken when the draft was created.</summary>
        public string Region { get; set; } = string.Empty;

        /// <summary>Current lifecycle state.</summary>
        public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

        /// <summary>UTC timestamp of draft creation.</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>UTC issue date; null while in draft.</summary>
        public DateTime? IssueDateUtc { get; set; }

        /// <summary>UTC due date; may be pre-set on drafts.</summary>
        public DateTime? DueDateUtc { get; set; }

        /// <summary>Tax amount frozen at issue time; null while in draft.</summary>
        public decimal? TaxAmountSnapshot { get; set; }

        /// <summary>Reason recorded when the invoice was voided.</summary>
        public string? VoidedReason { get; set; }

        /// <summary>UTC timestamp of voiding, if voided.</summary>
        public DateTime? VoidedAtUtc { get; set; }

        /// <summary>Free-form notes or payment terms shown to the customer.</summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>Billable line items.</summary>
        public List<LineItem> Items { get; set; } = new();

        /// <summary>Payments received against this invoice.</summary>
        public List<Payment> Payments { get; set; } = new();

        /// <summary>Builds a zero-padded number, e.g. MakeNumber(2026, 1) is "INV-2026-0001".</summary>
        /// <param name="year">Four-digit invoice year.</param>
        /// <param name="sequence">One-based per-year sequence.</param>
        /// <returns>Formatted invoice number.</returns>
        public static string MakeNumber(int year, int sequence)
        {
            if (year < 1000 || year > 9999)
            {
                throw new ArgumentOutOfRangeException(nameof(year), "Invoice years must be four digits.");
            }

            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence), "Invoice sequences start at 1.");
            }

            return "INV-" + year.ToString("D4", CultureInfo.InvariantCulture)
                + "-" + sequence.ToString("D4", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Splits an invoice number into year and sequence.
        /// </summary>
        /// <param name="number">Candidate number.</param>
        /// <param name="year">Parsed year when the method returns true.</param>
        /// <param name="sequence">Parsed sequence when the method returns true.</param>
        /// <returns>True when the number matches INV-yyyy-nnnn.</returns>
        public static bool TryParseNumber(string? number, out int year, out int sequence)
        {
            year = 0;
            sequence = 0;
            if (string.IsNullOrWhiteSpace(number))
            {
                return false;
            }

            Match match = NumberPattern.Match(number.Trim().ToUpperInvariant());
            if (!match.Success)
            {
                return false;
            }

            year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            sequence = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>True while the invoice is Issued or Overdue (open for payments).</summary>
        [JsonIgnore]
        public bool IsOpen => Status == InvoiceStatus.Issued || Status == InvoiceStatus.Overdue;

        /// <summary>Sum of recorded payment amounts.</summary>
        /// <returns>Total paid so far.</returns>
        public decimal TotalPaid()
        {
            decimal paid = 0m;
            foreach (Payment payment in Payments)
            {
                paid += payment.Amount;
            }

            return Money.RoundCurrency(paid);
        }

        /// <summary>
        /// Computes the full totals of this invoice.
        /// </summary>
        /// <param name="taxAmount">Tax to include (pass TaxAmountSnapshot, or 0 for drafts).</param>
        /// <returns>Fully populated totals.</returns>
        public InvoiceTotals ComputeTotals(decimal taxAmount)
        {
            return InvoiceTotals.Compute(Items, taxAmount, Payments);
        }

        /// <summary>Grand total (subtotal plus tax snapshot); drafts carry 0 tax.</summary>
        /// <returns>Total amount of the document.</returns>
        public decimal GrandTotal()
        {
            return ComputeTotals(TaxAmountSnapshot ?? 0m).GrandTotal;
        }

        /// <summary>Grand total minus payments; negative means the customer overpaid.</summary>
        /// <returns>Outstanding balance.</returns>
        public decimal BalanceDue()
        {
            return ComputeTotals(TaxAmountSnapshot ?? 0m).BalanceDue;
        }

        /// <summary>
        /// Days past the due date as of the given date; zero when not yet due.
        /// </summary>
        /// <param name="asOfUtc">Reference date (UTC).</param>
        /// <returns>Whole days overdue, floored at zero.</returns>
        public int DaysOverdue(DateTime asOfUtc)
        {
            if (DueDateUtc is null)
            {
                return 0;
            }

            int days = (asOfUtc.Date - DueDateUtc.Value.Date).Days;
            return days > 0 ? days : 0;
        }

        /// <summary>
        /// Determines whether the invoice counts as overdue as of a reference date:
        /// open, past due, and carrying a positive balance.
        /// </summary>
        /// <param name="asOfUtc">Reference date (UTC).</param>
        /// <returns>True when the invoice is overdue.</returns>
        public bool IsOverdue(DateTime asOfUtc)
        {
            if (Status is InvoiceStatus.Draft or InvoiceStatus.Void or InvoiceStatus.Paid)
            {
                return false;
            }

            if (Status == InvoiceStatus.Overdue)
            {
                return BalanceDue() > 0m;
            }

            return DueDateUtc is not null
                && DueDateUtc.Value.Date < asOfUtc.Date
                && BalanceDue() > 0m;
        }

        /// <summary>
        /// Appends a line item; only drafts accept items.
        /// </summary>
        /// <param name="item">Validated line item to append.</param>
        public void AddItem(LineItem item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (Status != InvoiceStatus.Draft)
            {
                throw new ConflictException("Line items can only be added while invoice " + Number + " is a draft (current status: " + Status + ").");
            }

            var errors = new List<string>(item.Validate());
            if (errors.Count > 0)
            {
                throw ValidationException.ForErrors("The line item is invalid:", errors);
            }

            Items.Add(item);
        }

        /// <summary>
        /// Removes the zero-based line item at the given position; drafts only.
        /// </summary>
        /// <param name="index">Zero-based item index.</param>
        /// <returns>The removed line item.</returns>
        public LineItem RemoveItemAt(int index)
        {
            if (Status != InvoiceStatus.Draft)
            {
                throw new ConflictException("Line items can only be removed while invoice " + Number + " is a draft.");
            }

            if (index < 0 || index >= Items.Count)
            {
                throw new NotFoundException("Line item " + (index + 1) + " does not exist on invoice " + Number + ".");
            }

            LineItem removed = Items[index];
            Items.RemoveAt(index);
            return removed;
        }

        /// <summary>
        /// Transitions the draft to Issued, freezing dates and the tax amount.
        /// </summary>
        /// <param name="issueUtc">Issue date (UTC).</param>
        /// <param name="dueUtc">Due date (UTC); must not precede the issue date.</param>
        /// <param name="taxAmount">Tax snapshot computed by the tax engine.</param>
        public void Issue(DateTime issueUtc, DateTime dueUtc, decimal taxAmount)
        {
            var errors = new List<string>();
            if (Status != InvoiceStatus.Draft)
            {
                errors.Add("Only drafts can be issued (current status: " + Status + ").");
            }

            if (Items.Count == 0)
            {
                errors.Add("Add at least one line item before issuing.");
            }

            if (dueUtc.Date < issueUtc.Date)
            {
                errors.Add("The due date cannot be before the issue date.");
            }

            if (string.IsNullOrWhiteSpace(CustomerId))
            {
                errors.Add("The invoice has no customer assigned.");
            }

            if (errors.Count > 0)
            {
                throw ValidationException.ForErrors("Invoice " + Number + " cannot be issued:", errors);
            }

            IssueDateUtc = issueUtc.Date;
            DueDateUtc = dueUtc.Date;
            TaxAmountSnapshot = Money.RoundToCurrency(taxAmount, Currency);
            Status = InvoiceStatus.Issued;
        }

        /// <summary>
        /// Records a payment against the invoice and flips the status to Paid when the
        /// balance reaches zero or below.
        /// </summary>
        /// <param name="payment">Validated payment to append.</param>
        public void ApplyPayment(Payment payment)
        {
            if (payment is null)
            {
                throw new ArgumentNullException(nameof(payment));
            }

            if (Status == InvoiceStatus.Draft)
            {
                throw new ConflictException("Invoice " + Number + " is still a draft; issue it before recording payments.");
            }

            if (Status == InvoiceStatus.Void)
            {
                throw new ConflictException("Invoice " + Number + " is void; payments cannot be recorded against it.");
            }

            Payments.Add(payment);
            if (BalanceDue() <= 0m)
            {
                Status = InvoiceStatus.Paid;
            }
        }

        /// <summary>
        /// Flips an issued invoice to Overdue; called by the repository's maintenance pass.
        /// </summary>
        /// <param name="asOfUtc">Reference date used for the check.</param>
        public void MarkOverdue(DateTime asOfUtc)
        {
            if (Status != InvoiceStatus.Issued)
            {
                throw new ConflictException("Only issued invoices can be marked overdue (invoice " + Number + " is " + Status + ").");
            }

            if (DueDateUtc is null || DueDateUtc.Value.Date >= asOfUtc.Date || BalanceDue() <= 0m)
            {
                throw new ConflictException("Invoice " + Number + " does not qualify as overdue as of " + asOfUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
            }

            Status = InvoiceStatus.Overdue;
        }

        /// <summary>
        /// Cancels the invoice. Paid invoices cannot be voided — refund via payments.
        /// </summary>
        /// <param name="reason">Short human-readable reason, stored on the invoice.</param>
        /// <param name="atUtc">Voiding timestamp (UTC).</param>
        public void Void(string reason, DateTime atUtc)
        {
            if (Status == InvoiceStatus.Paid)
            {
                throw new ConflictException("Invoice " + Number + " is fully paid and cannot be voided; record a refund instead.");
            }

            if (Status == InvoiceStatus.Void)
            {
                throw new ConflictException("Invoice " + Number + " is already void.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ValidationException("A reason is required when voiding an invoice.");
            }

            VoidedReason = reason.Trim();
            VoidedAtUtc = atUtc;
            Status = InvoiceStatus.Void;
        }

        /// <summary>
        /// Validates invoice-level invariants (items are validated separately).
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the invoice is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (!TryParseNumber(Number, out _, out _))
            {
                errors.Add("Number '" + Number + "' does not match the INV-yyyy-nnnn pattern.");
            }

            if (string.IsNullOrWhiteSpace(CustomerId))
            {
                errors.Add("CustomerId is required.");
            }

            if (!Money.IsSupported(Currency))
            {
                errors.Add("Currency '" + Currency + "' is not supported.");
            }

            if (Region.Length > 0 && !Customer.RegionPattern.IsMatch(Region))
            {
                errors.Add("Region '" + Region + "' does not look like a tax region code.");
            }

            if (IssueDateUtc is not null && DueDateUtc is not null && DueDateUtc.Value.Date < IssueDateUtc.Value.Date)
            {
                errors.Add("DueDateUtc cannot be before IssueDateUtc.");
            }

            return errors;
        }
    }
}
