using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>One row of the outstanding report.</summary>
    public sealed class OutstandingRow
    {
        /// <summary>Invoice number.</summary>
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>Billed customer id.</summary>
        public string CustomerId { get; set; } = string.Empty;

        /// <summary>Resolved customer display name.</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Invoice currency.</summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>Lifecycle status (Issued or Overdue).</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Issue date, or null for legacy drafts.</summary>
        public DateTime? IssueDate { get; set; }

        /// <summary>Due date.</summary>
        public DateTime? DueDate { get; set; }

        /// <summary>Whole days past due (0 when not yet due).</summary>
        public int DaysOverdue { get; set; }

        /// <summary>Grand total of the invoice.</summary>
        public decimal Total { get; set; }

        /// <summary>Amount already paid.</summary>
        public decimal Paid { get; set; }

        /// <summary>Remaining balance.</summary>
        public decimal Balance { get; set; }
    }

    /// <summary>Aggregated outstanding report.</summary>
    public sealed class OutstandingReport
    {
        /// <summary>Reference date used for overdue math.</summary>
        public DateTime AsOf { get; set; }

        /// <summary>Rows sorted by due date.</summary>
        public IReadOnlyList<OutstandingRow> Rows { get; set; } = new List<OutstandingRow>();

        /// <summary>Sum of all balances.</summary>
        public decimal TotalOutstanding { get; set; }

        /// <summary>Number of open invoices.</summary>
        public int InvoiceCount { get; set; }

        /// <summary>Distinct currencies present in the rows.</summary>
        public IReadOnlyList<string> Currencies { get; set; } = new List<string>();
    }

    /// <summary>One aging bucket (Current, 1-30, 31-60, 61-90, 90+).</summary>
    public sealed class AgingBucket
    {
        /// <summary>Bucket label.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>Number of invoices in the bucket.</summary>
        public int InvoiceCount { get; set; }

        /// <summary>Sum of balances in the bucket.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>One invoice row inside the aging detail.</summary>
    public sealed class AgingDetailRow
    {
        /// <summary>Invoice number.</summary>
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>Resolved customer display name.</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Invoice currency.</summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>Due date.</summary>
        public DateTime? DueDate { get; set; }

        /// <summary>Whole days past due.</summary>
        public int DaysOverdue { get; set; }

        /// <summary>Outstanding balance.</summary>
        public decimal Balance { get; set; }

        /// <summary>Bucket label the row belongs to.</summary>
        public string BucketLabel { get; set; } = string.Empty;
    }

    /// <summary>Aggregated aging report.</summary>
    public sealed class AgingReport
    {
        /// <summary>Reference date used for the bucket math.</summary>
        public DateTime AsOf { get; set; }

        /// <summary>All buckets in fixed order, including empty ones.</summary>
        public IReadOnlyList<AgingBucket> Buckets { get; set; } = new List<AgingBucket>();

        /// <summary>Detail rows sorted by days overdue, most overdue first.</summary>
        public IReadOnlyList<AgingDetailRow> Rows { get; set; } = new List<AgingDetailRow>();

        /// <summary>Sum of all outstanding balances.</summary>
        public decimal TotalOutstanding { get; set; }

        /// <summary>Warning text when multiple currencies are summed together.</summary>
        public string? CurrencyNote { get; set; }
    }

    /// <summary>Revenue of one calendar month.</summary>
    public sealed class MonthRevenueRow
    {
        /// <summary>Month key in "yyyy-MM" form.</summary>
        public string Month { get; set; } = string.Empty;

        /// <summary>Amount invoiced that month (issue-date basis).</summary>
        public decimal Invoiced { get; set; }

        /// <summary>Amount collected that month (payment-date basis).</summary>
        public decimal Collected { get; set; }
    }

    /// <summary>Aggregated revenue report.</summary>
    public sealed class RevenueReport
    {
        /// <summary>Monthly rows, contiguous between the report bounds.</summary>
        public IReadOnlyList<MonthRevenueRow> Rows { get; set; } = new List<MonthRevenueRow>();

        /// <summary>Sum of invoiced amounts across the rows.</summary>
        public decimal TotalInvoiced { get; set; }

        /// <summary>Sum of collected amounts across the rows.</summary>
        public decimal TotalCollected { get; set; }
    }

    /// <summary>Per-customer revenue aggregation.</summary>
    public sealed class CustomerRevenueRow
    {
        /// <summary>Customer identifier.</summary>
        public string CustomerId { get; set; } = string.Empty;

        /// <summary>Resolved customer display name.</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Number of counted invoices.</summary>
        public int InvoiceCount { get; set; }

        /// <summary>Total invoiced (grand totals of non-draft, non-void invoices).</summary>
        public decimal Invoiced { get; set; }

        /// <summary>Total collected (payments on non-void invoices).</summary>
        public decimal Collected { get; set; }

        /// <summary>Current outstanding balance.</summary>
        public decimal Outstanding { get; set; }

        /// <summary>Date of the latest invoice activity.</summary>
        public DateTime? LastActivity { get; set; }
    }

    /// <summary>
    /// Builds business reports from the repositories: outstanding balances, 30/60/90
    /// aging buckets, monthly revenue series and top-customer rankings.
    /// </summary>
    public sealed class ReportService
    {
        /// <summary>Fixed bucket definitions: label with inclusive day bounds (null = open end).</summary>
        public static readonly (string Label, int MinDays, int? MaxDays)[] BucketDefinitions =
        {
            ("Current", int.MinValue, 0),
            ("1-30", 1, 30),
            ("31-60", 31, 60),
            ("61-90", 61, 90),
            ("90+", 91, null),
        };

        /// <summary>Maximum number of months a revenue series will span.</summary>
        public const int MaxRevenueMonths = 600;

        private readonly InvoiceRepository _invoices;
        private readonly CustomerRepository _customers;

        /// <summary>
        /// Creates the report service over the given repositories.
        /// </summary>
        /// <param name="invoices">Invoice repository.</param>
        /// <param name="customers">Customer repository (for name resolution).</param>
        public ReportService(InvoiceRepository invoices, CustomerRepository customers)
        {
            _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
            _customers = customers ?? throw new ArgumentNullException(nameof(customers));
        }

        /// <summary>
        /// Maps a days-overdue value to its bucket label.
        /// </summary>
        /// <param name="daysOverdue">Whole days past due (0 or negative = not yet due).</param>
        /// <returns>"Current", "1-30", "31-60", "61-90" or "90+".</returns>
        public static string BucketLabelFor(int daysOverdue)
        {
            if (daysOverdue <= 0)
            {
                return "Current";
            }

            if (daysOverdue <= 30)
            {
                return "1-30";
            }

            if (daysOverdue <= 60)
            {
                return "31-60";
            }

            if (daysOverdue <= 90)
            {
                return "61-90";
            }

            return "90+";
        }

        /// <summary>Formats a UTC date as the canonical "yyyy-MM" month key.</summary>
        /// <param name="utc">Date to format.</param>
        /// <returns>Month key string.</returns>
        public static string MonthKey(DateTime utc)
        {
            return utc.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Builds the outstanding report: every open invoice with a positive balance.
        /// </summary>
        /// <param name="asOfUtc">Reference date for overdue math.</param>
        /// <param name="customerId">Optional customer filter.</param>
        /// <returns>The report.</returns>
        public OutstandingReport BuildOutstanding(DateTime asOfUtc, string? customerId = null)
        {
            var rows = new List<OutstandingRow>();
            var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Invoice invoice in _invoices.GetOutstanding(asOfUtc, customerId))
            {
                InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
                rows.Add(new OutstandingRow
                {
                    InvoiceNumber = invoice.Number,
                    CustomerId = invoice.CustomerId,
                    CustomerName = NameOf(invoice.CustomerId),
                    Currency = invoice.Currency,
                    Status = invoice.Status.ToString(),
                    IssueDate = invoice.IssueDateUtc,
                    DueDate = invoice.DueDateUtc,
                    DaysOverdue = invoice.DaysOverdue(asOfUtc),
                    Total = totals.GrandTotal,
                    Paid = totals.PaidTotal,
                    Balance = totals.BalanceDue,
                });
                currencies.Add(invoice.Currency);
            }

            return new OutstandingReport
            {
                AsOf = asOfUtc,
                Rows = rows,
                TotalOutstanding = rows.Sum(r => r.Balance),
                InvoiceCount = rows.Count,
                Currencies = currencies.ToList(),
            };
        }

        /// <summary>
        /// Builds the aging report with the fixed 30/60/90 buckets plus detail rows.
        /// </summary>
        /// <param name="asOfUtc">Reference date for the bucket math.</param>
        /// <param name="customerId">Optional customer filter.</param>
        /// <returns>The report.</returns>
        public AgingReport BuildAging(DateTime asOfUtc, string? customerId = null)
        {
            var rows = new List<AgingDetailRow>();
            var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Invoice invoice in _invoices.GetOutstanding(asOfUtc, customerId))
            {
                int days = invoice.DaysOverdue(asOfUtc);
                rows.Add(new AgingDetailRow
                {
                    InvoiceNumber = invoice.Number,
                    CustomerName = NameOf(invoice.CustomerId),
                    Currency = invoice.Currency,
                    DueDate = invoice.DueDateUtc,
                    DaysOverdue = days,
                    Balance = invoice.BalanceDue(),
                    BucketLabel = BucketLabelFor(days),
                });
                currencies.Add(invoice.Currency);
            }

            var buckets = new List<AgingBucket>();
            decimal total = 0m;
            foreach ((string Label, int MinDays, int? MaxDays) definition in BucketDefinitions)
            {
                List<AgingDetailRow> inBucket = rows
                    .Where(r => string.Equals(r.BucketLabel, definition.Label, StringComparison.Ordinal))
                    .ToList();
                buckets.Add(new AgingBucket
                {
                    Label = definition.Label,
                    InvoiceCount = inBucket.Count,
                    Amount = inBucket.Sum(r => r.Balance),
                });
                total += inBucket.Sum(r => r.Balance);
            }

            return new AgingReport
            {
                AsOf = asOfUtc,
                Buckets = buckets,
                Rows = rows
                    .OrderByDescending(r => r.DaysOverdue)
                    .ThenBy(r => r.InvoiceNumber, StringComparer.Ordinal)
                    .ToList(),
                TotalOutstanding = total,
                CurrencyNote = currencies.Count > 1
                    ? "Mixed currencies present; bucket totals sum across currencies."
                    : null,
            };
        }

        /// <summary>
        /// Builds the monthly revenue series. Invoiced amounts are attributed to the
        /// issue month, collected amounts to the payment month; gaps are filled with zeros.
        /// </summary>
        /// <param name="fromUtc">Optional lower bound (inclusive).</param>
        /// <param name="toUtc">Optional upper bound (inclusive).</param>
        /// <returns>The report.</returns>
        public RevenueReport BuildRevenue(DateTime? fromUtc, DateTime? toUtc)
        {
            var invoicedByMonth = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
            var collectedByMonth = new SortedDictionary<string, decimal>(StringComparer.Ordinal);

            foreach (Invoice invoice in _invoices.GetAll())
            {
                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                {
                    continue;
                }

                if (invoice.IssueDateUtc is null)
                {
                    continue;
                }

                string key = MonthKey(invoice.IssueDateUtc.Value);
                InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
                AddTo(invoicedByMonth, key, totals.GrandTotal);
            }

            foreach (PaymentRecord record in _invoices.GetPayments(null, null))
            {
                AddTo(collectedByMonth, MonthKey(record.Payment.PaidAtUtc), record.Payment.Amount);
            }

            string minKey = fromUtc is not null
                ? MonthKey(fromUtc.Value)
                : MinKey(invoicedByMonth.Keys, collectedByMonth.Keys);
            string maxKey = toUtc is not null
                ? MonthKey(toUtc.Value)
                : MaxKey(invoicedByMonth.Keys, collectedByMonth.Keys);
            if (minKey.Length == 0 || maxKey.Length == 0 || string.CompareOrdinal(minKey, maxKey) > 0)
            {
                return new RevenueReport
                {
                    Rows = new List<MonthRevenueRow>(),
                    TotalInvoiced = 0m,
                    TotalCollected = 0m,
                };
            }

            var rows = new List<MonthRevenueRow>();
            foreach (string month in EnumerateMonths(minKey, maxKey))
            {
                rows.Add(new MonthRevenueRow
                {
                    Month = month,
                    Invoiced = invoicedByMonth.TryGetValue(month, out decimal invoiced) ? invoiced : 0m,
                    Collected = collectedByMonth.TryGetValue(month, out decimal collected) ? collected : 0m,
                });
            }

            return new RevenueReport
            {
                Rows = rows,
                TotalInvoiced = rows.Sum(r => r.Invoiced),
                TotalCollected = rows.Sum(r => r.Collected),
            };
        }

        /// <summary>
        /// Ranks customers by invoiced, collected or outstanding amounts.
        /// </summary>
        /// <param name="top">Maximum number of rows to return (1-500).</param>
        /// <param name="metric">Ranking metric: "invoiced", "collected" or "outstanding".</param>
        /// <param name="asOfUtc">Reference date for outstanding balances.</param>
        /// <returns>Ranked rows, best first.</returns>
        public IReadOnlyList<CustomerRevenueRow> BuildTopCustomers(int top, string metric, DateTime asOfUtc)
        {
            if (top < 1 || top > 500)
            {
                throw new UsageException("--top must be between 1 and 500.");
            }

            string normalized = (metric ?? "collected").Trim().ToLowerInvariant();
            if (normalized != "invoiced" && normalized != "collected" && normalized != "outstanding")
            {
                throw new UsageException("--metric must be one of: invoiced, collected, outstanding.");
            }

            var aggregates = new Dictionary<string, CustomerAgg>(StringComparer.OrdinalIgnoreCase);
            foreach (Invoice invoice in _invoices.GetAll())
            {
                if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
                {
                    continue;
                }

                CustomerAgg agg = GetOrAdd(aggregates, invoice.CustomerId);
                InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
                agg.Invoiced += totals.GrandTotal;
                agg.InvoiceCount++;
                DateTime activity = invoice.IssueDateUtc ?? invoice.CreatedAtUtc;
                if (agg.LastActivity is null || activity > agg.LastActivity.Value)
                {
                    agg.LastActivity = activity;
                }

                if (invoice.IsOpen && invoice.BalanceDue() > 0m)
                {
                    agg.Outstanding += invoice.BalanceDue();
                }
            }

            foreach (PaymentRecord record in _invoices.GetPayments(null, null))
            {
                CustomerAgg agg = GetOrAdd(aggregates, record.Invoice.CustomerId);
                agg.Collected += record.Payment.Amount;
            }

            IEnumerable<CustomerAgg> ordered = normalized switch
            {
                "invoiced" => aggregates.Values.OrderByDescending(a => a.Invoiced).ThenBy(a => a.CustomerId, StringComparer.Ordinal),
                "outstanding" => aggregates.Values.OrderByDescending(a => a.Outstanding).ThenBy(a => a.CustomerId, StringComparer.Ordinal),
                _ => aggregates.Values.OrderByDescending(a => a.Collected).ThenBy(a => a.CustomerId, StringComparer.Ordinal),
            };

            return ordered
                .Take(top)
                .Select(a => new CustomerRevenueRow
                {
                    CustomerId = a.CustomerId,
                    CustomerName = NameOf(a.CustomerId),
                    InvoiceCount = a.InvoiceCount,
                    Invoiced = a.Invoiced,
                    Collected = a.Collected,
                    Outstanding = a.Outstanding,
                    LastActivity = a.LastActivity,
                })
                .ToList();
        }

        private static void AddTo(IDictionary<string, decimal> map, string key, decimal amount)
        {
            map[key] = map.TryGetValue(key, out decimal current) ? current + amount : amount;
        }

        private static CustomerAgg GetOrAdd(Dictionary<string, CustomerAgg> map, string customerId)
        {
            if (!map.TryGetValue(customerId, out CustomerAgg? agg))
            {
                agg = new CustomerAgg { CustomerId = customerId };
                map[customerId] = agg;
            }

            return agg;
        }

        private static string MinKey(IEnumerable<string> left, IEnumerable<string> right)
        {
            string min = string.Empty;
            foreach (string key in left.Concat(right))
            {
                if (min.Length == 0 || string.CompareOrdinal(key, min) < 0)
                {
                    min = key;
                }
            }

            return min;
        }

        private static string MaxKey(IEnumerable<string> left, IEnumerable<string> right)
        {
            string max = string.Empty;
            foreach (string key in left.Concat(right))
            {
                if (max.Length == 0 || string.CompareOrdinal(key, max) > 0)
                {
                    max = key;
                }
            }

            return max;
        }

        private static IEnumerable<string> EnumerateMonths(string startKey, string endKey)
        {
            int year = int.Parse(startKey.AsSpan(0, 4), CultureInfo.InvariantCulture);
            int month = int.Parse(startKey.AsSpan(5, 2), CultureInfo.InvariantCulture);
            for (int guard = 0; guard < MaxRevenueMonths; guard++)
            {
                string current = string.Format(CultureInfo.InvariantCulture, "{0:D4}-{1:D2}", year, month);
                if (string.CompareOrdinal(current, endKey) > 0)
                {
                    yield break;
                }

                yield return current;
                month++;
                if (month > 12)
                {
                    month = 1;
                    year++;
                }
            }
        }

        private string NameOf(string customerId)
        {
            Customer? customer = _customers.FindById(customerId);
            return customer is null ? customerId : customer.Name;
        }

        private sealed class CustomerAgg
        {
            public string CustomerId { get; set; } = string.Empty;
            public int InvoiceCount { get; set; }
            public decimal Invoiced { get; set; }
            public decimal Collected { get; set; }
            public decimal Outstanding { get; set; }
            public DateTime? LastActivity { get; set; }
        }
    }
}
