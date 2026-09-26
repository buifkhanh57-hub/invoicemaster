using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InvoiceMaster
{
    /// <summary>
    /// Central helpers for decimal money arithmetic, rounding and currency formatting.
    /// All formatting and parsing is culture-invariant so exported CSV files and reports
    /// are byte-for-byte stable regardless of the machine locale.
    /// </summary>
    public static class Money
    {
        /// <summary>Default number of decimal places when a currency is unknown.</summary>
        public const int DefaultDecimals = 2;

        /// <summary>Highest amount accepted for a single line item or payment.</summary>
        public const decimal MaxAmount = 999_999_999_999m;

        private static readonly Dictionary<string, CurrencyInfo> Currencies =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["USD"] = new CurrencyInfo("$", 2),
                ["EUR"] = new CurrencyInfo("\u20AC", 2),
                ["GBP"] = new CurrencyInfo("\u00A3", 2),
                ["JPY"] = new CurrencyInfo("\u00A5", 0),
                ["CNY"] = new CurrencyInfo("\u00A5", 2),
                ["VND"] = new CurrencyInfo("\u20AB", 0),
                ["KRW"] = new CurrencyInfo("\u20A9", 0),
                ["SGD"] = new CurrencyInfo("S$", 2),
                ["HKD"] = new CurrencyInfo("HK$", 2),
                ["AUD"] = new CurrencyInfo("A$", 2),
                ["NZD"] = new CurrencyInfo("NZ$", 2),
                ["CAD"] = new CurrencyInfo("C$", 2),
                ["CHF"] = new CurrencyInfo("CHF ", 2),
                ["INR"] = new CurrencyInfo("\u20B9", 2),
                ["THB"] = new CurrencyInfo("\u0E3F", 2),
                ["MYR"] = new CurrencyInfo("RM ", 2),
                ["PHP"] = new CurrencyInfo("\u20B1", 2),
                ["IDR"] = new CurrencyInfo("Rp ", 0),
                ["SEK"] = new CurrencyInfo("kr ", 2),
                ["NOK"] = new CurrencyInfo("kr ", 2),
                ["DKK"] = new CurrencyInfo("kr ", 2),
                ["PLN"] = new CurrencyInfo("z\u0142 ", 2),
                ["TRY"] = new CurrencyInfo("\u20BA", 2),
                ["BRL"] = new CurrencyInfo("R$", 2),
                ["ZAR"] = new CurrencyInfo("R ", 2),
                ["MXN"] = new CurrencyInfo("MX$", 2),
                ["AED"] = new CurrencyInfo("AED ", 2),
                ["SAR"] = new CurrencyInfo("SAR ", 2),
            };

        /// <summary>Codes of every currency the formatter knows about.</summary>
        public static IReadOnlyCollection<string> SupportedCurrencies => Currencies.Keys;

        /// <summary>
        /// Determines whether a currency code is supported.
        /// </summary>
        /// <param name="code">ISO 4217 code such as "USD".</param>
        /// <returns>True when the code is present in the currency table.</returns>
        public static bool IsSupported(string? code)
        {
            return !string.IsNullOrWhiteSpace(code) && Currencies.ContainsKey(code.Trim());
        }

        /// <summary>
        /// Returns the minor-unit decimal places for a currency (2 for USD, 0 for VND).
        /// Unknown codes fall back to <see cref="DefaultDecimals"/>.
        /// </summary>
        /// <param name="code">ISO 4217 currency code.</param>
        /// <returns>Number of decimal places to round and display with.</returns>
        public static int DecimalsOf(string? code)
        {
            return TryResolve(code, out CurrencyInfo info) ? info.Decimals : DefaultDecimals;
        }

        /// <summary>
        /// Rounds to a fixed number of decimal places using half-up (away from zero)
        /// rounding, the convention expected on invoices.
        /// </summary>
        /// <param name="amount">Amount to round.</param>
        /// <param name="decimals">Target decimal places.</param>
        /// <returns>Rounded amount.</returns>
        public static decimal Round(decimal amount, int decimals)
        {
            if (decimals < 0 || decimals > 6)
            {
                throw new ArgumentOutOfRangeException(nameof(decimals), "Decimal places must be between 0 and 6.");
            }

            return Math.Round(amount, decimals, MidpointRounding.AwayFromZero);
        }

        /// <summary>Rounds to the default two currency decimals, half-up.</summary>
        /// <param name="amount">Amount to round.</param>
        /// <returns>Rounded amount.</returns>
        public static decimal RoundCurrency(decimal amount)
        {
            return Round(amount, DefaultDecimals);
        }

        /// <summary>
        /// Rounds an amount according to the minor units of the given currency.
        /// </summary>
        /// <param name="amount">Amount to round.</param>
        /// <param name="currencyCode">ISO 4217 currency code.</param>
        /// <returns>Rounded amount.</returns>
        public static decimal RoundToCurrency(decimal amount, string currencyCode)
        {
            return Round(amount, DecimalsOf(currencyCode));
        }

        /// <summary>
        /// Formats an amount for display, e.g. 1234.5 USD becomes "$1,234.50".
        /// Unknown currency codes render as "1,234.50 XYZ".
        /// </summary>
        /// <param name="amount">Amount to format.</param>
        /// <param name="currencyCode">ISO 4217 currency code.</param>
        /// <returns>Invariant, human-readable money string.</returns>
        public static string Format(decimal amount, string? currencyCode)
        {
            if (!TryResolve(currencyCode, out CurrencyInfo info))
            {
                string code = (currencyCode ?? "???").Trim().ToUpperInvariant();
                return amount.ToString("N" + DefaultDecimals, CultureInfo.InvariantCulture) + " " + code;
            }

            string number = amount.ToString("N" + info.Decimals, CultureInfo.InvariantCulture);
            if (info.Symbol.Length == 0)
            {
                return number;
            }

            string sign = amount < 0m ? "-" : string.Empty;
            return sign + info.Symbol + number.TrimStart('-');
        }

        /// <summary>
        /// Formats an amount without thousands separators, suitable for CSV cells,
        /// e.g. 1234.5 USD becomes "1234.50".
        /// </summary>
        /// <param name="amount">Amount to format.</param>
        /// <param name="currencyCode">ISO 4217 currency code controlling decimals.</param>
        /// <returns>Plain invariant number string.</returns>
        public static string FormatPlain(decimal amount, string? currencyCode)
        {
            int decimals = TryResolve(currencyCode, out CurrencyInfo info) ? info.Decimals : DefaultDecimals;
            return amount.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a percentage rate, trimming trailing zeros ("7.25%", "10%").</summary>
        /// <param name="percent">Rate in percent.</param>
        /// <returns>Invariant percentage string.</returns>
        public static string FormatPercent(decimal percent)
        {
            return percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>
        /// Computes a percentage of an amount rounded to currency decimals.
        /// </summary>
        /// <param name="amount">Base amount.</param>
        /// <param name="percent">Percentage, e.g. 10 for ten percent.</param>
        /// <returns>Rounded percentage amount.</returns>
        public static decimal PercentOf(decimal amount, decimal percent)
        {
            return RoundCurrency(amount * percent / 100m);
        }

        /// <summary>
        /// Divides two amounts, converting an unguarded zero denominator into a clear
        /// exception instead of the default unhelpful one.
        /// </summary>
        /// <param name="numerator">Dividend.</param>
        /// <param name="denominator">Divisor; must not be zero.</param>
        /// <returns>Quotient.</returns>
        public static decimal Divide(decimal numerator, decimal denominator)
        {
            if (denominator == 0m)
            {
                throw new DivideByZeroException("Cannot divide by a zero amount.");
            }

            return numerator / denominator;
        }

        /// <summary>
        /// Parses a user-supplied amount. Accepts optional currency symbols, spaces and
        /// thousands commas ("$1,234.56", "12 500", "-3.5"). The decimal point must be
        /// a dot because parsing is invariant-culture.
        /// </summary>
        /// <param name="text">Raw text to parse.</param>
        /// <param name="value">Parsed amount when the method returns true.</param>
        /// <returns>True when the text is a valid amount.</returns>
        public static bool TryParseAmount(string? text, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string cleaned = text.Trim();
            foreach (CurrencyInfo info in Currencies.Values)
            {
                if (info.Symbol.Length > 0)
                {
                    cleaned = cleaned.Replace(info.Symbol, string.Empty, StringComparison.Ordinal);
                }
            }

            cleaned = new string(cleaned.Where(ch => !char.IsWhiteSpace(ch) && ch != ',').ToArray());
            if (cleaned.Length == 0)
            {
                return false;
            }

            if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
            {
                return false;
            }

            if (Math.Abs(parsed) > MaxAmount)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static bool TryResolve(string? code, out CurrencyInfo info)
        {
            if (!string.IsNullOrWhiteSpace(code) && Currencies.TryGetValue(code.Trim(), out CurrencyInfo? found))
            {
                info = found;
                return true;
            }

            info = new CurrencyInfo(string.Empty, DefaultDecimals);
            return false;
        }

        /// <summary>Internal description of a currency: display symbol and minor units.</summary>
        private sealed class CurrencyInfo
        {
            public CurrencyInfo(string symbol, int decimals)
            {
                Symbol = symbol;
                Decimals = decimals;
            }

            public string Symbol { get; }

            public int Decimals { get; }
        }
    }
}
