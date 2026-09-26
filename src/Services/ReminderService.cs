using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>Tone of an overdue reminder, derived from how many days late the invoice is.</summary>
    public enum ReminderSeverity
    {
        /// <summary>1-15 days late: friendly nudge.</summary>
        Gentle,

        /// <summary>16-45 days late: firm request with a deadline.</summary>
        Firm,

        /// <summary>46+ days late: final notice before escalation.</summary>
        Final,
    }

    /// <summary>A drafted reminder letter for one overdue invoice.</summary>
    public sealed class ReminderLetter
    {
        /// <summary>Invoice number the letter is about.</summary>
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>Customer identifier the letter is addressed to.</summary>
        public string CustomerId { get; set; } = string.Empty;

        /// <summary>Customer display name.</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Derived tone of the letter.</summary>
        public ReminderSeverity Severity { get; set; }

        /// <summary>Whole days the invoice is past due.</summary>
        public int DaysOverdue { get; set; }

        /// <summary>Outstanding balance at draft time.</summary>
        public decimal BalanceDue { get; set; }

        /// <summary>Invoice currency.</summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>UTC timestamp the letter was generated.</summary>
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>Full letter body, plain text.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Suggested file name when saving the letter to disk.</summary>
        /// <returns>File name such as "reminder-INV-2026-0001-20260210.txt".</returns>
        public string ToFileName()
        {
            return "reminder-" + InvoiceNumber + "-"
                + GeneratedAtUtc.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt";
        }
    }

    /// <summary>
    /// Drafts plain-text overdue reminder letters. Severity escalates automatically:
    /// gentle up to 15 days late, firm up to 45 days, final beyond that. Letters are
    /// deterministic for a given as-of date, so regenerating produces the same text.
    /// </summary>
    public sealed class ReminderService
    {
        /// <summary>Maximum days overdue that still counts as a gentle reminder.</summary>
        public const int GentleMaxDays = 15;

        /// <summary>Maximum days overdue that still counts as a firm reminder.</summary>
        public const int FirmMaxDays = 45;

        private readonly InvoiceRepository _invoices;
        private readonly CustomerRepository _customers;

        /// <summary>
        /// Creates the reminder service over the given repositories.
        /// </summary>
        /// <param name="invoices">Invoice repository.</param>
        /// <param name="customers">Customer repository.</param>
        public ReminderService(InvoiceRepository invoices, CustomerRepository customers)
        {
            _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
            _customers = customers ?? throw new ArgumentNullException(nameof(customers));
        }

        /// <summary>
        /// Maps days overdue to a reminder severity.
        /// </summary>
        /// <param name="daysOverdue">Whole days past due.</param>
        /// <returns>Gentle, Firm or Final.</returns>
        public static ReminderSeverity DetermineSeverity(int daysOverdue)
        {
            if (daysOverdue <= GentleMaxDays)
            {
                return ReminderSeverity.Gentle;
            }

            if (daysOverdue <= FirmMaxDays)
            {
                return ReminderSeverity.Firm;
            }

            return ReminderSeverity.Final;
        }

        /// <summary>
        /// Drafts the reminder letter for one overdue invoice.
        /// </summary>
        /// <param name="invoice">Overdue invoice to write about.</param>
        /// <param name="asOfUtc">Reference date for overdue math.</param>
        /// <returns>The letter.</returns>
        public ReminderLetter BuildLetter(Invoice invoice, DateTime asOfUtc)
        {
            if (invoice is null)
            {
                throw new ArgumentNullException(nameof(invoice));
            }

            if (!invoice.IsOverdue(asOfUtc))
            {
                throw new ConflictException(
                    "Invoice " + invoice.Number + " is not overdue as of "
                    + asOfUtc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "; no reminder is needed.");
            }

            Customer customer = _customers.RequireById(invoice.CustomerId);
            int days = invoice.DaysOverdue(asOfUtc);
            ReminderSeverity severity = DetermineSeverity(days);
            InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
            string dueText = invoice.DueDateUtc is null
                ? "(no due date)"
                : invoice.DueDateUtc.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var builder = new StringBuilder();
            builder.Append("============================================================").AppendLine();
            builder.Append("INVOICE PAYMENT REMINDER — ").Append(SeverityTitle(severity)).AppendLine();
            builder.Append("============================================================").AppendLine();
            builder.Append("Date: ").Append(asOfUtc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).AppendLine();
            builder.AppendLine();
            builder.Append("To:").AppendLine();
            builder.Append("  ").Append(customer.DisplayName).AppendLine();
            if (customer.HasAddress)
            {
                foreach (string line in customer.Address!.ToLines())
                {
                    builder.Append("  ").Append(line).AppendLine();
                }
            }

            builder.AppendLine();
            builder.Append("Dear ").Append(customer.DisplayName).Append(",").AppendLine();
            builder.AppendLine();
            builder.Append("Our records show that the following invoice is past due:").AppendLine();
            builder.AppendLine();
            builder.Append("  Invoice number : ").Append(invoice.Number).AppendLine();
            builder.Append("  Issued on      : ").Append(FormatOrNull(invoice.IssueDateUtc)).AppendLine();
            builder.Append("  Due date       : ").Append(dueText).AppendLine();
            builder.Append("  Days overdue   : ").Append(days.ToString(CultureInfo.InvariantCulture)).AppendLine();
            builder.Append("  Invoice total  : ").Append(Money.Format(totals.GrandTotal, invoice.Currency)).AppendLine();
            builder.Append("  Amount paid    : ").Append(Money.Format(totals.PaidTotal, invoice.Currency)).AppendLine();
            builder.Append("  Balance due    : ").Append(Money.Format(totals.BalanceDue, invoice.Currency)).AppendLine();
            builder.AppendLine();
            builder.Append(SeverityParagraph(severity)).AppendLine();
            builder.AppendLine();
            builder.Append("Please reference the invoice number with your payment. If you have").AppendLine();
            builder.Append("already arranged payment or believe this letter is in error, contact").AppendLine();
            builder.Append("our accounts receivable team and we will reconcile immediately.").AppendLine();
            builder.AppendLine();
            builder.Append("Sincerely,").AppendLine();
            builder.Append("Accounts Receivable").AppendLine();
            builder.Append("============================================================").AppendLine();

            return new ReminderLetter
            {
                InvoiceNumber = invoice.Number,
                CustomerId = customer.Id,
                CustomerName = customer.DisplayName,
                Severity = severity,
                DaysOverdue = days,
                BalanceDue = totals.BalanceDue,
                Currency = invoice.Currency,
                GeneratedAtUtc = DateTime.UtcNow,
                Text = builder.ToString(),
            };
        }

        /// <summary>
        /// Drafts letters for every overdue invoice, optionally scoped to one customer,
        /// sorted most-overdue first.
        /// </summary>
        /// <param name="asOfUtc">Reference date for overdue math.</param>
        /// <param name="customerId">Optional customer filter.</param>
        /// <returns>The letters.</returns>
        public IReadOnlyList<ReminderLetter> BuildAll(DateTime asOfUtc, string? customerId = null)
        {
            var letters = new List<ReminderLetter>();
            foreach (Invoice invoice in _invoices.GetAll())
            {
                if (customerId is not null
                    && !string.Equals(invoice.CustomerId, customerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!invoice.IsOverdue(asOfUtc))
                {
                    continue;
                }

                letters.Add(BuildLetter(invoice, asOfUtc));
            }

            return letters
                .OrderByDescending(l => l.DaysOverdue)
                .ThenBy(l => l.InvoiceNumber, StringComparer.Ordinal)
                .ToList();
        }

        private static string SeverityTitle(ReminderSeverity severity)
        {
            switch (severity)
            {
                case ReminderSeverity.Gentle:
                    return "GENTLE REMINDER";
                case ReminderSeverity.Firm:
                    return "FIRM REMINDER";
                default:
                    return "FINAL NOTICE";
            }
        }

        private static string SeverityParagraph(ReminderSeverity severity)
        {
            switch (severity)
            {
                case ReminderSeverity.Gentle:
                    return "This is a friendly reminder that the invoice above has passed its"
                        + " due date. If you have already sent payment, please disregard this"
                        + " letter and accept our thanks.";
                case ReminderSeverity.Firm:
                    return "Despite our previous reminders, this invoice remains unpaid."
                        + " Please settle the outstanding balance within 7 days to avoid"
                        + " late fees and a hold on future deliveries.";
                default:
                    return "This is a final notice. Unless payment is received within 5"
                        + " business days, the account will be handed to collections and"
                        + " all further deliveries will be suspended.";
            }
        }

        private static string FormatOrNull(DateTime? utc)
        {
            return utc is null
                ? "(not issued)"
                : utc.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
