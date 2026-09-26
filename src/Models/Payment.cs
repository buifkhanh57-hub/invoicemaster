using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceMaster.Models
{
    /// <summary>How a payment was received.</summary>
    public enum PaymentMethod
    {
        /// <summary>Physical cash.</summary>
        Cash,

        /// <summary>Bank or wire transfer.</summary>
        BankTransfer,

        /// <summary>Credit card charge.</summary>
        CreditCard,

        /// <summary>Debit card charge.</summary>
        DebitCard,

        /// <summary>Paper or digital check.</summary>
        Check,

        /// <summary>Anything else (wallets, crypto, barter, ...).</summary>
        Other,
    }

    /// <summary>
    /// Money received against an invoice. Payments live inside their invoice so the
    /// balance is always consistent with a single JSON document.
    /// </summary>
    public sealed class Payment
    {
        /// <summary>Prefix of every generated payment identifier.</summary>
        public const string IdPrefix = "PAY";

        /// <summary>Shape of a valid payment identifier, e.g. "PAY-000001".</summary>
        public static readonly Regex IdPattern =
            new(@"^PAY-\d{4,}$", RegexOptions.Compiled);

        /// <summary>Unique identifier in the form "PAY-000001".</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Number of the invoice this payment settles, e.g. "INV-2026-0001".</summary>
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>Amount received; strictly positive.</summary>
        public decimal Amount { get; set; }

        /// <summary>ISO 4217 currency; must match the invoice currency.</summary>
        public string Currency { get; set; } = "USD";

        /// <summary>Channel the money arrived through.</summary>
        public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;

        /// <summary>UTC timestamp of when the money arrived (what aging uses).</summary>
        public DateTime PaidAtUtc { get; set; }

        /// <summary>Bank reference, check number or transaction id.</summary>
        public string Reference { get; set; } = string.Empty;

        /// <summary>Free-form internal notes.</summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>UTC timestamp of when the payment row was recorded.</summary>
        public DateTime RecordedAtUtc { get; set; }

        /// <summary>Builds a zero-padded identifier, e.g. FormatId(9) is "PAY-000009".</summary>
        /// <param name="sequence">One-based sequence number.</param>
        /// <returns>Formatted identifier.</returns>
        public static string FormatId(int sequence)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence), "Payment sequence numbers start at 1.");
            }

            return IdPrefix + "-" + sequence.ToString("D6", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a user-supplied payment method alias such as "bank", "wire" or "cheque".
        /// </summary>
        /// <param name="text">Raw alias (case-insensitive).</param>
        /// <param name="method">Resolved method when the method returns true.</param>
        /// <returns>True when the alias is recognised.</returns>
        public static bool TryParseMethod(string? text, out PaymentMethod method)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "cash":
                    method = PaymentMethod.Cash;
                    return true;
                case "bank":
                case "banktransfer":
                case "wire":
                    method = PaymentMethod.BankTransfer;
                    return true;
                case "credit":
                case "creditcard":
                case "card":
                    method = PaymentMethod.CreditCard;
                    return true;
                case "debit":
                case "debitcard":
                    method = PaymentMethod.DebitCard;
                    return true;
                case "check":
                case "cheque":
                    method = PaymentMethod.Check;
                    return true;
                case "other":
                    method = PaymentMethod.Other;
                    return true;
                default:
                    method = PaymentMethod.Other;
                    return false;
            }
        }

        /// <summary>All method names accepted by <see cref="TryParseMethod"/>.</summary>
        /// <returns>Comma separated alias list for error messages.</returns>
        public static string MethodAliases()
        {
            return "cash, bank, wire, credit, debit, check, cheque, other";
        }

        /// <summary>Friendly display name of the payment method ("Bank transfer").</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string MethodDisplay
        {
            get
            {
                switch (Method)
                {
                    case PaymentMethod.Cash:
                        return "Cash";
                    case PaymentMethod.BankTransfer:
                        return "Bank transfer";
                    case PaymentMethod.CreditCard:
                        return "Credit card";
                    case PaymentMethod.DebitCard:
                        return "Debit card";
                    case PaymentMethod.Check:
                        return "Check";
                    default:
                        return "Other";
                }
            }
        }

        /// <summary>
        /// Validates the payment and aggregates all problems.
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the payment is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (Id.Length == 0)
            {
                errors.Add("Payment id is assigned by the repository and must not be blank.");
            }
            else if (!IdPattern.IsMatch(Id))
            {
                errors.Add("Payment id '" + Id + "' does not match the expected pattern PAY-000001.");
            }

            if (!Invoice.TryParseNumber(InvoiceNumber, out _, out _))
            {
                errors.Add("Invoice number '" + InvoiceNumber + "' is not a valid invoice reference.");
            }

            if (Amount <= 0m)
            {
                errors.Add("Payment amount must be greater than zero.");
            }
            else if (Amount > Money.MaxAmount)
            {
                errors.Add("Payment amount exceeds the supported maximum.");
            }

            if (!Money.IsSupported(Currency))
            {
                errors.Add("Currency '" + Currency + "' is not supported.");
            }

            if (PaidAtUtc == default)
            {
                errors.Add("A payment date is required.");
            }

            if ((Reference ?? string.Empty).Length > 120)
            {
                errors.Add("Reference must not exceed 120 characters.");
            }

            return errors;
        }

        /// <summary>Returns a compact "PAY-000001 500.00 USD" representation.</summary>
        /// <returns>Short display string.</returns>
        public override string ToString()
        {
            return Id + " " + Money.FormatPlain(Amount, Currency) + " " + Currency;
        }
    }
}
