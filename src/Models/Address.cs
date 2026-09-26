using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InvoiceMaster.Models
{
    /// <summary>
    /// Postal address of a customer. Every field is optional so partial addresses can be
    /// stored, but <see cref="Validate"/> keeps the data sane (lengths, completeness).
    /// </summary>
    public sealed class Address
    {
        /// <summary>Maximum characters accepted per address field.</summary>
        public const int MaxFieldLength = 120;

        /// <summary>Street, number and optional unit, e.g. "12 Main St, Suite 4".</summary>
        public string Street { get; set; } = string.Empty;

        /// <summary>City or locality name.</summary>
        public string City { get; set; } = string.Empty;

        /// <summary>State, province or region code.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>Postal or ZIP code.</summary>
        public string PostalCode { get; set; } = string.Empty;

        /// <summary>Country name or ISO country code, e.g. "US".</summary>
        public string Country { get; set; } = string.Empty;

        /// <summary>Creates an empty address (all fields blank).</summary>
        public Address()
        {
        }

        /// <summary>Creates a fully populated address.</summary>
        /// <param name="street">Street line.</param>
        /// <param name="city">City name.</param>
        /// <param name="state">State or province.</param>
        /// <param name="postalCode">Postal code.</param>
        /// <param name="country">Country name or code.</param>
        public Address(string street, string city, string state, string postalCode, string country)
        {
            Street = street ?? string.Empty;
            City = city ?? string.Empty;
            State = state ?? string.Empty;
            PostalCode = postalCode ?? string.Empty;
            Country = country ?? string.Empty;
        }

        /// <summary>True when every field is blank.</summary>
        [JsonIgnore]
        public bool IsEmpty
        {
            get
            {
                return Blank(Street) && Blank(City) && Blank(State) && Blank(PostalCode) && Blank(Country);
            }
        }

        /// <summary>True when the minimum useful subset (street, city, country) is present.</summary>
        [JsonIgnore]
        public bool IsComplete
        {
            get
            {
                return !Blank(Street) && !Blank(City) && !Blank(Country);
            }
        }

        /// <summary>
        /// Validates the address fields.
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the address is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            CheckLength(errors, nameof(Street), Street);
            CheckLength(errors, nameof(City), City);
            CheckLength(errors, nameof(State), State);
            CheckLength(errors, nameof(PostalCode), PostalCode);
            CheckLength(errors, nameof(Country), Country);

            bool anyFilled = !Blank(Street) || !Blank(City) || !Blank(State) || !Blank(PostalCode) || !Blank(Country);
            if (anyFilled && (Blank(City) || Blank(Country)))
            {
                errors.Add("A partially filled address still needs at least a city and a country.");
            }

            return errors;
        }

        /// <summary>Renders the address on one line, skipping blank fields, e.g.
        /// "12 Main St, Springfield, IL 62704, US".</summary>
        /// <returns>Single-line address; empty string when the address is empty.</returns>
        public string ToSingleLine()
        {
            var parts = new List<string>();
            AddIfPresent(parts, Street);
            AddIfPresent(parts, City);
            if (!Blank(State) && !Blank(PostalCode))
            {
                AddIfPresent(parts, State + " " + PostalCode);
            }
            else
            {
                AddIfPresent(parts, State);
                AddIfPresent(parts, PostalCode);
            }

            AddIfPresent(parts, Country);
            return string.Join(", ", parts);
        }

        /// <summary>Renders the address as postal-style lines for letters.</summary>
        /// <returns>One entry per non-blank line; empty list when the address is empty.</returns>
        public IReadOnlyList<string> ToLines()
        {
            var lines = new List<string>();
            AddIfPresent(lines, Street);
            AddIfPresent(lines, City);
            if (!Blank(State) && !Blank(PostalCode))
            {
                lines.Add(State + " " + PostalCode);
            }
            else
            {
                AddIfPresent(lines, State);
                AddIfPresent(lines, PostalCode);
            }

            AddIfPresent(lines, Country);
            return lines;
        }

        /// <summary>Returns <see cref="ToSingleLine"/>.</summary>
        /// <returns>Single-line address representation.</returns>
        public override string ToString()
        {
            return ToSingleLine();
        }

        /// <summary>Creates a deep copy of this address.</summary>
        /// <returns>A new independent Address instance.</returns>
        public Address Clone()
        {
            return new Address(Street, City, State, PostalCode, Country);
        }

        /// <summary>
        /// Compares two addresses field by field, ignoring leading/trailing whitespace.
        /// </summary>
        /// <param name="other">Address to compare with.</param>
        /// <returns>True when every field matches.</returns>
        public bool EqualsValue(Address? other)
        {
            if (other is null)
            {
                return IsEmpty;
            }

            return Same(Street, other.Street)
                && Same(City, other.City)
                && Same(State, other.State)
                && Same(PostalCode, other.PostalCode)
                && Same(Country, other.Country);
        }

        private static void CheckLength(List<string> errors, string fieldName, string value)
        {
            if (value != null && value.Length > MaxFieldLength)
            {
                errors.Add($"Address field '{fieldName}' exceeds {MaxFieldLength} characters.");
            }
        }

        private static void AddIfPresent(List<string> parts, string value)
        {
            if (!Blank(value))
            {
                parts.Add(value.Trim());
            }
        }

        private static bool Blank(string value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        private static bool Same(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
