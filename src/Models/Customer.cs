using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InvoiceMaster.Models
{
    /// <summary>
    /// A billing counterpart: company or person that receives invoices.
    /// Customers own the tax region and default currency used when invoicing them.
    /// </summary>
    public sealed class Customer
    {
        /// <summary>Prefix of every generated customer identifier.</summary>
        public const string IdPrefix = "CUS";

        /// <summary>Maximum accepted length of the customer name.</summary>
        public const int MaxNameLength = 120;

        /// <summary>Shape of a valid customer identifier, e.g. "CUS-0001".</summary>
        public static readonly Regex IdPattern =
            new(@"^CUS-\d{4,}$", RegexOptions.Compiled);

        /// <summary>Shape of a valid tax region, e.g. "US-CA", "GB" or "INTL".</summary>
        public static readonly Regex RegionPattern =
            new(@"^[A-Z]{2,4}(-[A-Z0-9]{1,3})?$", RegexOptions.Compiled);

        /// <summary>Loose shape of an email address used for a quick sanity check.</summary>
        public static readonly Regex EmailPattern =
            new(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", RegexOptions.Compiled);

        /// <summary>Allowed characters of a phone number.</summary>
        public static readonly Regex PhonePattern =
            new(@"^[0-9+()\- ]{5,25}$", RegexOptions.Compiled);

        /// <summary>Unique identifier in the form "CUS-0001".</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Display name of the person or company (required).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Contact e-mail; blank when unknown.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Contact phone number; blank when unknown.</summary>
        public string Phone { get; set; } = string.Empty;

        /// <summary>VAT / tax identifier printed on invoices; blank when unknown.</summary>
        public string TaxId { get; set; } = string.Empty;

        /// <summary>Tax region code controlling which tax rule applies, e.g. "US-CA".</summary>
        public string Region { get; set; } = string.Empty;

        /// <summary>Default ISO 4217 currency for new invoices.</summary>
        public string Currency { get; set; } = "USD";

        /// <summary>Postal address; may be null when not captured.</summary>
        public Address? Address { get; set; }

        /// <summary>Free-form internal notes.</summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>UTC timestamp of creation.</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>UTC timestamp of the last modification.</summary>
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>True when a non-empty address is attached.</summary>
        [JsonIgnore]
        public bool HasAddress => Address is not null && !Address.IsEmpty;

        /// <summary>Name when set, otherwise the identifier — safe for every display.</summary>
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

        /// <summary>Builds a zero-padded identifier, e.g. FormatId(7) is "CUS-0007".</summary>
        /// <param name="sequence">One-based sequence number.</param>
        /// <returns>Formatted identifier.</returns>
        public static string FormatId(int sequence)
        {
            if (sequence < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence), "Customer sequence numbers start at 1.");
            }

            return IdPrefix + "-" + sequence.ToString("D4", CultureInfo.InvariantCulture);
        }

        /// <summary>Refreshes <see cref="UpdatedAtUtc"/> to the current UTC time.</summary>
        public void Touch()
        {
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Validates every field and aggregates all problems.
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the customer is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            string trimmedName = (Name ?? string.Empty).Trim();
            if (trimmedName.Length == 0)
            {
                errors.Add("Name is required.");
            }
            else if (trimmedName.Length > MaxNameLength)
            {
                errors.Add($"Name must not exceed {MaxNameLength} characters.");
            }

            string email = (Email ?? string.Empty).Trim();
            if (email.Length > 0 && !EmailPattern.IsMatch(email))
            {
                errors.Add("Email does not look like a valid address: " + email);
            }

            string phone = (Phone ?? string.Empty).Trim();
            if (phone.Length > 0 && !PhonePattern.IsMatch(phone))
            {
                errors.Add("Phone may only contain digits, spaces, +, -, ( and ) and must be 5-25 characters long.");
            }

            string region = (Region ?? string.Empty).Trim();
            if (region.Length > 0 && !RegionPattern.IsMatch(region))
            {
                errors.Add("Region must look like 'US-CA', 'GB' or 'INTL' (2-4 letters, optional -suffix).");
            }

            string currency = (Currency ?? string.Empty).Trim();
            if (currency.Length == 0)
            {
                errors.Add("Currency is required (e.g. USD).");
            }
            else if (!Money.IsSupported(currency))
            {
                errors.Add("Currency '" + currency + "' is not supported. Run 'tax get' or see the README for the list.");
            }

            if (Address is not null)
            {
                foreach (string addressError in Address.Validate())
                {
                    errors.Add(addressError);
                }
            }

            if (CreatedAtUtc != default && UpdatedAtUtc != default && UpdatedAtUtc < CreatedAtUtc)
            {
                errors.Add("UpdatedAtUtc cannot be earlier than CreatedAtUtc.");
            }

            return errors;
        }

        /// <summary>
        /// Case-insensitive keyword search across id, name, email, tax id and notes.
        /// </summary>
        /// <param name="term">Raw user term; blank terms match everything.</param>
        /// <returns>True when the customer matches the term.</returns>
        public bool MatchesSearch(string? term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return true;
            }

            string needle = term.Trim();
            return (Name ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (Email ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (TaxId ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (Notes ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || (Id ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Creates a deep copy of this customer, including the address.</summary>
        /// <returns>A new independent Customer instance.</returns>
        public Customer Clone()
        {
            return new Customer
            {
                Id = Id,
                Name = Name,
                Email = Email,
                Phone = Phone,
                TaxId = TaxId,
                Region = Region,
                Currency = Currency,
                Address = Address?.Clone(),
                Notes = Notes,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedAtUtc = UpdatedAtUtc,
            };
        }

        /// <summary>Returns a compact "CUS-0001 — Name" representation.</summary>
        /// <returns>Short display string.</returns>
        public override string ToString()
        {
            return Id + " — " + DisplayName;
        }
    }
}
