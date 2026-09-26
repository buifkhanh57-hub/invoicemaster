using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace InvoiceMaster.Models
{
    /// <summary>
    /// Canonical exemption categories a line item can belong to. Tax rules map these
    /// categories to reduced or zero rates; unknown categories are rejected up front.
    /// </summary>
    public static class ExemptionCategories
    {
        /// <summary>Default category: fully taxable at the region's standard rate.</summary>
        public const string Standard = "Standard";

        /// <summary>Food and beverages.</summary>
        public const string Food = "Food";

        /// <summary>Medicine and medical devices.</summary>
        public const string Medicine = "Medicine";

        /// <summary>Education and training services.</summary>
        public const string Education = "Education";

        /// <summary>Printed books and publications.</summary>
        public const string Books = "Books";

        /// <summary>Cross-border exports (commonly zero-rated).</summary>
        public const string Export = "Export";

        /// <summary>Agricultural products and inputs.</summary>
        public const string Agriculture = "Agriculture";

        /// <summary>Supplies to registered non-profit organisations.</summary>
        public const string NonProfit = "NonProfit";

        /// <summary>Any other taxable supply.</summary>
        public const string Other = "Other";

        /// <summary>All known categories in display order.</summary>
        public static readonly string[] All =
        {
            Standard,
            Food,
            Medicine,
            Education,
            Books,
            Export,
            Agriculture,
            NonProfit,
            Other,
        };

        /// <summary>
        /// Determines whether a category string is known.
        /// </summary>
        /// <param name="category">Raw category value.</param>
        /// <returns>True when the category exists in <see cref="All"/>.</returns>
        public static bool IsKnown(string? category)
        {
            return TryNormalize(category) is not null;
        }

        /// <summary>
        /// Maps a user-typed category to its canonical spelling, case-insensitively.
        /// </summary>
        /// <param name="category">Raw user input.</param>
        /// <returns>Canonical category name, or null when unknown.</returns>
        public static string? TryNormalize(string? category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return null;
            }

            string trimmed = category.Trim();
            foreach (string candidate in All)
            {
                if (string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// One billable row of an invoice: description, quantity, unit price, an optional
    /// line discount and the exemption category driving the tax rate.
    /// </summary>
    public sealed class LineItem
    {
        /// <summary>Maximum accepted description length.</summary>
        public const int MaxDescriptionLength = 300;

        /// <summary>Highest accepted quantity.</summary>
        public const decimal MaxQuantity = 1_000_000m;

        /// <summary>Highest accepted unit price.</summary>
        public const decimal MaxUnitPrice = Money.MaxAmount;

        /// <summary>Human-readable description of the product or service.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Quantity delivered; must be greater than zero.</summary>
        public decimal Quantity { get; set; } = 1m;

        /// <summary>Price per unit before discount; must not be negative.</summary>
        public decimal UnitPrice { get; set; }

        /// <summary>Line discount in percent (0-100).</summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>Exemption category used by the tax engine.</summary>
        public string ExemptionCategory { get; set; } = ExemptionCategories.Standard;

        /// <summary>Quantity times unit price before the line discount.</summary>
        [JsonIgnore]
        public decimal GrossAmount => Quantity * UnitPrice;

        /// <summary>
        /// Computes the monetary value of the line discount, rounded to two decimals.
        /// </summary>
        /// <returns>Discount amount in currency units.</returns>
        public decimal ComputeDiscountAmount()
        {
            return Money.RoundCurrency(GrossAmount * DiscountPercent / 100m);
        }

        /// <summary>
        /// Computes the net amount after the line discount; the tax engine applies
        /// rates to this value.
        /// </summary>
        /// <returns>Net taxable amount in currency units.</returns>
        public decimal ComputeNetAmount()
        {
            return Money.RoundCurrency(GrossAmount - ComputeDiscountAmount());
        }

        /// <summary>
        /// Validates the line item and aggregates all problems.
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the item is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            string description = (Description ?? string.Empty).Trim();
            if (description.Length == 0)
            {
                errors.Add("Description is required.");
            }
            else if (description.Length > MaxDescriptionLength)
            {
                errors.Add($"Description must not exceed {MaxDescriptionLength} characters.");
            }

            if (Quantity <= 0m)
            {
                errors.Add("Quantity must be greater than zero.");
            }
            else if (Quantity > MaxQuantity)
            {
                errors.Add($"Quantity must not exceed {MaxQuantity}.");
            }

            if (UnitPrice < 0m)
            {
                errors.Add("Unit price cannot be negative.");
            }
            else if (UnitPrice > MaxUnitPrice)
            {
                errors.Add("Unit price exceeds the supported maximum.");
            }

            if (DiscountPercent < 0m || DiscountPercent > 100m)
            {
                errors.Add("Discount percent must be between 0 and 100.");
            }

            if (!ExemptionCategories.IsKnown(ExemptionCategory))
            {
                errors.Add("Exemption category '" + ExemptionCategory + "' is unknown. Known categories: "
                    + string.Join(", ", ExemptionCategories.All) + ".");
            }

            return errors;
        }

        /// <summary>Formats the quantity without trailing zeros ("2", "1.5").</summary>
        /// <returns>Compact invariant quantity string.</returns>
        public string FormatQuantity()
        {
            return Quantity.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>Creates a deep copy of this line item.</summary>
        /// <returns>A new independent LineItem instance.</returns>
        public LineItem Clone()
        {
            return new LineItem
            {
                Description = Description,
                Quantity = Quantity,
                UnitPrice = UnitPrice,
                DiscountPercent = DiscountPercent,
                ExemptionCategory = ExemptionCategory,
            };
        }

        /// <summary>Returns a compact "2 x Widget @ 50.00 = 100.00" style string.</summary>
        /// <returns>Short display string.</returns>
        public override string ToString()
        {
            return FormatQuantity() + " x " + (Description ?? string.Empty)
                + " @ " + Money.FormatPlain(UnitPrice, "USD")
                + " = " + Money.FormatPlain(ComputeNetAmount(), "USD");
        }
    }
}
