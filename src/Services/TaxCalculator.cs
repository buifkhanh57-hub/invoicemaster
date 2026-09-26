using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>Result of taxing a single line item.</summary>
    public sealed class TaxLineResult
    {
        /// <summary>Exemption category the item belongs to.</summary>
        public string Category { get; set; } = ExemptionCategories.Standard;

        /// <summary>Net amount the rate was applied to.</summary>
        public decimal NetAmount { get; set; }

        /// <summary>Effective rate in percent (0 for exempt items).</summary>
        public decimal RatePercent { get; set; }

        /// <summary>Computed tax, rounded per line.</summary>
        public decimal TaxAmount { get; set; }

        /// <summary>True when the category is exempt under the region rule.</summary>
        public bool IsExempt { get; set; }
    }

    /// <summary>Aggregated tax computation across an invoice's items.</summary>
    public sealed class TaxBreakdown
    {
        /// <summary>Sum of net amounts of taxable lines.</summary>
        public decimal TaxableNet { get; set; }

        /// <summary>Sum of net amounts of exempt lines.</summary>
        public decimal ExemptNet { get; set; }

        /// <summary>Total tax across all lines.</summary>
        public decimal TaxAmount { get; set; }

        /// <summary>Per-line results in item order.</summary>
        public IReadOnlyList<TaxLineResult> Lines { get; set; } = new List<TaxLineResult>();
    }

    /// <summary>
    /// Tax configuration of one region: standard rate, optional reduced rates per
    /// exemption category and a list of fully exempt categories. Rules are immutable
    /// once registered; edits replace the whole rule.
    /// </summary>
    public sealed class TaxRule
    {
        /// <summary>Shape of a valid region code, e.g. "US-CA", "GB" or "INTL".</summary>
        public static readonly Regex RegionPattern =
            new(@"^[A-Z]{2,4}(-[A-Z0-9]{1,3})?$", RegexOptions.Compiled);

        /// <summary>Unique region code, e.g. "US-CA".</summary>
        public string Region { get; set; } = string.Empty;

        /// <summary>Human-readable name, e.g. "California sales tax".</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Currency the rule's rounding follows.</summary>
        public string Currency { get; set; } = "USD";

        /// <summary>Standard rate in percent applied to non-exempt categories.</summary>
        public decimal StandardRatePercent { get; set; }

        /// <summary>Reduced rates per exemption category, in percent.</summary>
        public Dictionary<string, decimal> ReducedRatesPercent { get; set; } = new();

        /// <summary>Categories taxed at zero percent.</summary>
        public List<string> ExemptCategories { get; set; } = new();

        /// <summary>Free-form notes about the rule (legal reference, effective dates).</summary>
        public string Notes { get; set; } = string.Empty;

        /// <summary>
        /// Resolves the effective rate for a category: zero when exempt, the reduced
        /// rate when registered, otherwise the standard rate.
        /// </summary>
        /// <param name="category">Exemption category of the line.</param>
        /// <returns>Rate in percent.</returns>
        public decimal RateFor(string category)
        {
            if (IsExempt(category))
            {
                return 0m;
            }

            foreach (KeyValuePair<string, decimal> entry in ReducedRatesPercent)
            {
                if (string.Equals(entry.Key, category, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return StandardRatePercent;
        }

        /// <summary>Determines whether a category is exempt under this rule.</summary>
        /// <param name="category">Exemption category of the line.</param>
        /// <returns>True when the category is in the exempt list.</returns>
        public bool IsExempt(string category)
        {
            foreach (string candidate in ExemptCategories)
            {
                if (string.Equals(candidate, category, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validates the rule and aggregates all problems.
        /// </summary>
        /// <returns>A list of human-readable problems; empty when the rule is valid.</returns>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Region) || !RegionPattern.IsMatch(Region.Trim()))
            {
                errors.Add("Region must look like 'US-CA', 'GB' or 'INTL'.");
            }

            if (StandardRatePercent < 0m || StandardRatePercent > 100m)
            {
                errors.Add("Standard rate must be between 0 and 100 percent.");
            }

            if (!Money.IsSupported(Currency))
            {
                errors.Add("Currency '" + Currency + "' is not supported.");
            }

            foreach (KeyValuePair<string, decimal> entry in ReducedRatesPercent)
            {
                if (!ExemptionCategories.IsKnown(entry.Key))
                {
                    errors.Add("Reduced rate category '" + entry.Key + "' is unknown.");
                }

                if (entry.Value < 0m || entry.Value > 100m)
                {
                    errors.Add("Reduced rate for '" + entry.Key + "' must be between 0 and 100 percent.");
                }
            }

            foreach (string category in ExemptCategories)
            {
                if (!ExemptionCategories.IsKnown(category))
                {
                    errors.Add("Exempt category '" + category + "' is unknown.");
                }
            }

            return errors;
        }

        /// <summary>Renders a one-line summary of the rule for tables.</summary>
        /// <returns>Compact description.</returns>
        public string Describe()
        {
            string reduced = ReducedRatesPercent.Count == 0
                ? "-"
                : string.Join(", ", ReducedRatesPercent
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key + " " + Money.FormatPercent(kv.Value)));
            string exempt = ExemptCategories.Count == 0 ? "-" : string.Join(", ", ExemptCategories);
            return Region + " | " + DisplayName
                + " | standard " + Money.FormatPercent(StandardRatePercent)
                + " | reduced: " + reduced
                + " | exempt: " + exempt
                + " | " + Currency;
        }
    }

    /// <summary>
    /// Computes line and invoice taxes from region rules. Built-in rules cover common
    /// regions; user-defined rules are persisted in taxrules.json and override the
    /// built-ins per region. Rounding is per line, half-up, in the rule's minor units.
    /// </summary>
    public sealed class TaxCalculator
    {
        private readonly Dictionary<string, TaxRule> _rules;

        /// <summary>
        /// Creates a calculator seeded with the given rules (or none).
        /// </summary>
        /// <param name="seedRules">Initial rules; null means an empty registry.</param>
        public TaxCalculator(IEnumerable<TaxRule>? seedRules = null)
        {
            _rules = new Dictionary<string, TaxRule>(StringComparer.OrdinalIgnoreCase);
            if (seedRules is null)
            {
                return;
            }

            foreach (TaxRule rule in seedRules)
            {
                Register(rule);
            }
        }

        /// <summary>
        /// Built-in rules for commonly invoiced regions. INTL is the zero-percent
        /// fallback for customers without a specific region.
        /// </summary>
        /// <returns>Read-only list of default rules.</returns>
        public static IReadOnlyList<TaxRule> DefaultRules()
        {
            return new List<TaxRule>
            {
                new TaxRule
                {
                    Region = "US-CA",
                    DisplayName = "California sales tax",
                    Currency = "USD",
                    StandardRatePercent = 7.25m,
                    ExemptCategories = new List<string> { ExemptionCategories.Food, ExemptionCategories.Medicine, ExemptionCategories.Education, ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "US-NY",
                    DisplayName = "New York sales tax",
                    Currency = "USD",
                    StandardRatePercent = 8.875m,
                    ExemptCategories = new List<string> { ExemptionCategories.Food, ExemptionCategories.Medicine, ExemptionCategories.Education, ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "GB",
                    DisplayName = "United Kingdom VAT",
                    Currency = "GBP",
                    StandardRatePercent = 20m,
                    ExemptCategories = new List<string> { ExemptionCategories.Food, ExemptionCategories.Books, ExemptionCategories.Medicine, ExemptionCategories.Education, ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "DE",
                    DisplayName = "Germany VAT",
                    Currency = "EUR",
                    StandardRatePercent = 19m,
                    ReducedRatesPercent = new Dictionary<string, decimal>
                    {
                        [ExemptionCategories.Food] = 7m,
                        [ExemptionCategories.Books] = 7m,
                    },
                    ExemptCategories = new List<string> { ExemptionCategories.Medicine, ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "VN",
                    DisplayName = "Vietnam VAT",
                    Currency = "VND",
                    StandardRatePercent = 10m,
                    ReducedRatesPercent = new Dictionary<string, decimal>
                    {
                        [ExemptionCategories.Food] = 5m,
                        [ExemptionCategories.Agriculture] = 5m,
                        [ExemptionCategories.Medicine] = 5m,
                        [ExemptionCategories.Education] = 5m,
                        [ExemptionCategories.Books] = 5m,
                    },
                    ExemptCategories = new List<string> { ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "SG",
                    DisplayName = "Singapore GST",
                    Currency = "SGD",
                    StandardRatePercent = 9m,
                    ExemptCategories = new List<string> { ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "AU",
                    DisplayName = "Australia GST",
                    Currency = "AUD",
                    StandardRatePercent = 10m,
                    ExemptCategories = new List<string> { ExemptionCategories.Food, ExemptionCategories.Education, ExemptionCategories.Medicine, ExemptionCategories.Export },
                },
                new TaxRule
                {
                    Region = "INTL",
                    DisplayName = "International / default (no tax)",
                    Currency = "USD",
                    StandardRatePercent = 0m,
                },
            };
        }

        /// <summary>Registers or replaces a rule; the rule must pass validation.</summary>
        /// <param name="rule">Rule to upsert.</param>
        public void Register(TaxRule rule)
        {
            if (rule is null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            var errors = rule.Validate();
            if (errors.Count > 0)
            {
                throw ValidationException.ForErrors("Tax rule '" + rule.Region + "' is invalid:", errors);
            }

            _rules[NormalizeRegion(rule.Region)] = rule;
        }

        /// <summary>Determines whether a region has a registered rule.</summary>
        /// <param name="region">Region code.</param>
        /// <returns>True when a rule exists.</returns>
        public bool HasRule(string? region)
        {
            string key = NormalizeRegion(region);
            return key.Length > 0 && _rules.ContainsKey(key);
        }

        /// <summary>Returns a rule or throws a not-found error listing known regions.</summary>
        /// <param name="region">Region code.</param>
        /// <returns>The matching rule.</returns>
        public TaxRule RequireRule(string? region)
        {
            string key = NormalizeRegion(region);
            if (key.Length == 0)
            {
                throw new NotFoundException(
                    "No tax region was specified. Pass --region or set a region on the customer.");
            }

            if (_rules.TryGetValue(key, out TaxRule? rule))
            {
                return rule;
            }

            string known = string.Join(", ", AllRules().Select(r => r.Region));
            throw NotFoundException.For("tax rule", key, "Known regions: " + known + ".");
        }

        /// <summary>All registered rules sorted by region code.</summary>
        /// <returns>Read-only rule list.</returns>
        public IReadOnlyList<TaxRule> AllRules()
        {
            return _rules.Values.OrderBy(r => r.Region, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Taxes one line item under a region rule: net after discount times the
        /// effective rate, rounded half-up to the rule's minor units.
        /// </summary>
        /// <param name="item">Line item to tax.</param>
        /// <param name="region">Region code.</param>
        /// <returns>Per-line tax result.</returns>
        public TaxLineResult ComputeLine(LineItem item, string? region)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            TaxRule rule = RequireRule(region);
            decimal net = item.ComputeNetAmount();
            decimal rate = rule.RateFor(item.ExemptionCategory);
            int decimals = Money.DecimalsOf(rule.Currency);
            decimal tax = rate == 0m ? 0m : Money.Round(net * rate / 100m, decimals);
            return new TaxLineResult
            {
                Category = item.ExemptionCategory,
                NetAmount = net,
                RatePercent = rate,
                TaxAmount = tax,
                IsExempt = rule.IsExempt(item.ExemptionCategory),
            };
        }

        /// <summary>
        /// Computes the full tax breakdown of a set of items.
        /// </summary>
        /// <param name="items">Line items to tax.</param>
        /// <param name="region">Region code.</param>
        /// <returns>Aggregated breakdown with per-line details.</returns>
        public TaxBreakdown ComputeBreakdown(IEnumerable<LineItem> items, string? region)
        {
            if (items is null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            TaxRule rule = RequireRule(region);
            var lines = new List<TaxLineResult>();
            decimal taxable = 0m;
            decimal exempt = 0m;
            decimal tax = 0m;
            foreach (LineItem item in items)
            {
                TaxLineResult result = ComputeLine(item, region);
                lines.Add(result);
                if (result.IsExempt)
                {
                    exempt += result.NetAmount;
                }
                else
                {
                    taxable += result.NetAmount;
                }

                tax += result.TaxAmount;
            }

            return new TaxBreakdown
            {
                TaxableNet = Money.RoundCurrency(taxable),
                ExemptNet = Money.RoundCurrency(exempt),
                TaxAmount = Money.Round(tax, Money.DecimalsOf(rule.Currency)),
                Lines = lines,
            };
        }

        /// <summary>
        /// Computes the total tax for an invoice, rounded to the invoice currency's
        /// minor units.
        /// </summary>
        /// <param name="items">Line items to tax.</param>
        /// <param name="region">Region code.</param>
        /// <param name="currency">Invoice currency controlling final rounding.</param>
        /// <returns>Total tax amount.</returns>
        public decimal ComputeInvoiceTax(IEnumerable<LineItem> items, string? region, string? currency)
        {
            TaxBreakdown breakdown = ComputeBreakdown(items, region);
            return Money.Round(breakdown.TaxAmount, Money.DecimalsOf(currency));
        }

        /// <summary>Upper-cases and trims a region code; blank stays blank.</summary>
        /// <param name="region">Raw region input.</param>
        /// <returns>Normalized region code.</returns>
        public static string NormalizeRegion(string? region)
        {
            return (region ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
