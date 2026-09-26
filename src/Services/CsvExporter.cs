using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using InvoiceMaster.Models;

namespace InvoiceMaster.Services
{
    /// <summary>
    /// RFC 4180 primitives: escaping single fields and joining them into one row.
    /// Shared by every exporter so quoting rules live in exactly one place.
    /// </summary>
    public static class Csv
    {
        /// <summary>
        /// Escapes one CSV field per RFC 4180: fields containing commas, quotes,
        /// carriage returns or line feeds are wrapped in double quotes and embedded
        /// quotes are doubled. Null becomes an empty field.
        /// </summary>
        /// <param name="value">Raw field value.</param>
        /// <returns>Escaped field, ready to join.</returns>
        public static string EscapeField(string? value)
        {
            string text = value ?? string.Empty;
            bool needsQuotes = text.IndexOf(',') >= 0
                || text.IndexOf('"') >= 0
                || text.IndexOf('\r') >= 0
                || text.IndexOf('\n') >= 0;
            if (!needsQuotes)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        /// <summary>
        /// Escapes and joins one row of fields with commas.
        /// </summary>
        /// <param name="fields">Raw field values.</param>
        /// <returns>One complete CSV line without a trailing newline.</returns>
        public static string WriteRow(IEnumerable<string?> fields)
        {
            if (fields is null)
            {
                throw new ArgumentNullException(nameof(fields));
            }

            return string.Join(",", fields.Select(EscapeField));
        }
    }

    /// <summary>
    /// Exports invoices and payments as RFC 4180 CSV files with invariant numbers and
    /// ISO dates, so the output loads cleanly in Excel, Sheets and pandas alike.
    /// </summary>
    public sealed class CsvExporter
    {
        /// <summary>Column headers of the invoice export.</summary>
        public static readonly string[] InvoiceHeaders =
        {
            "invoice_number",
            "status",
            "customer_id",
            "customer_name",
            "issue_date",
            "due_date",
            "currency",
            "subtotal",
            "discount",
            "tax",
            "total",
            "paid",
            "balance",
            "items_count",
        };

        /// <summary>Column headers of the payment export.</summary>
        public static readonly string[] PaymentHeaders =
        {
            "payment_id",
            "invoice_number",
            "customer_id",
            "customer_name",
            "paid_at",
            "method",
            "amount",
            "currency",
            "reference",
        };

        /// <summary>
        /// Exports invoices to a CSV file.
        /// </summary>
        /// <param name="invoices">Invoices to export (any status).</param>
        /// <param name="customerNameResolver">Maps a customer id to a display name.</param>
        /// <param name="outputPath">Target file path; parent directories are created.</param>
        /// <returns>The absolute path of the written file.</returns>
        public string ExportInvoices(
            IReadOnlyList<Invoice> invoices,
            Func<string, string> customerNameResolver,
            string outputPath)
        {
            if (invoices is null)
            {
                throw new ArgumentNullException(nameof(invoices));
            }

            if (customerNameResolver is null)
            {
                throw new ArgumentNullException(nameof(customerNameResolver));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("An output path is required.", nameof(outputPath));
            }

            var builder = new StringBuilder();
            builder.AppendLine(Csv.WriteRow(InvoiceHeaders));
            foreach (Invoice invoice in invoices)
            {
                InvoiceTotals totals = invoice.ComputeTotals(invoice.TaxAmountSnapshot ?? 0m);
                builder.AppendLine(Csv.WriteRow(new[]
                {
                    invoice.Number,
                    invoice.Status.ToString(),
                    invoice.CustomerId,
                    customerNameResolver(invoice.CustomerId),
                    FormatDate(invoice.IssueDateUtc),
                    FormatDate(invoice.DueDateUtc),
                    invoice.Currency,
                    Money.FormatPlain(totals.Subtotal, invoice.Currency),
                    Money.FormatPlain(totals.DiscountTotal, invoice.Currency),
                    Money.FormatPlain(totals.TaxAmount, invoice.Currency),
                    Money.FormatPlain(totals.GrandTotal, invoice.Currency),
                    Money.FormatPlain(totals.PaidTotal, invoice.Currency),
                    Money.FormatPlain(totals.BalanceDue, invoice.Currency),
                    invoice.Items.Count.ToString(CultureInfo.InvariantCulture),
                }));
            }

            return WriteFile(outputPath, builder.ToString());
        }

        /// <summary>
        /// Exports payments to a CSV file.
        /// </summary>
        /// <param name="records">Payment/invoice joins to export.</param>
        /// <param name="customerNameResolver">Maps a customer id to a display name.</param>
        /// <param name="outputPath">Target file path; parent directories are created.</param>
        /// <returns>The absolute path of the written file.</returns>
        public string ExportPayments(
            IReadOnlyList<PaymentRecord> records,
            Func<string, string> customerNameResolver,
            string outputPath)
        {
            if (records is null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            if (customerNameResolver is null)
            {
                throw new ArgumentNullException(nameof(customerNameResolver));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("An output path is required.", nameof(outputPath));
            }

            var builder = new StringBuilder();
            builder.AppendLine(Csv.WriteRow(PaymentHeaders));
            foreach (PaymentRecord record in records)
            {
                builder.AppendLine(Csv.WriteRow(new[]
                {
                    record.Payment.Id,
                    record.Payment.InvoiceNumber,
                    record.Invoice.CustomerId,
                    customerNameResolver(record.Invoice.CustomerId),
                    FormatDate(record.Payment.PaidAtUtc),
                    record.Payment.MethodDisplay,
                    Money.FormatPlain(record.Payment.Amount, record.Payment.Currency),
                    record.Payment.Currency,
                    record.Payment.Reference,
                }));
            }

            return WriteFile(outputPath, builder.ToString());
        }

        private static string FormatDate(DateTime? utc)
        {
            return utc is null
                ? string.Empty
                : utc.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string WriteFile(string outputPath, string content)
        {
            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            JsonStore<List<Customer>>.EnsureDirectory(directory);
            try
            {
                File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (IOException ex)
            {
                throw new StorageException("Unable to write CSV file '" + fullPath + "': " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new StorageException("Permission denied while writing '" + fullPath + "': " + ex.Message, ex);
            }

            return fullPath;
        }
    }
}
