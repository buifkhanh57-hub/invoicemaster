using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InvoiceMaster.Cli
{
    /// <summary>
    /// Minimal ANSI styling helper. Respect the NO_COLOR convention and redirected
    /// output: when disabled every method returns the text unchanged.
    /// </summary>
    public static class Ansi
    {
        /// <summary>When false, no escape sequences are emitted.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Reset sequence.</summary>
        public const string Reset = "\x1b[0m";

        /// <summary>Bold attribute.</summary>
        public const string Bold = "\x1b[1m";

        /// <summary>Dim attribute.</summary>
        public const string Dim = "\x1b[2m";

        /// <summary>Red foreground.</summary>
        public const string Red = "\x1b[31m";

        /// <summary>Green foreground.</summary>
        public const string Green = "\x1b[32m";

        /// <summary>Yellow foreground.</summary>
        public const string Yellow = "\x1b[33m";

        /// <summary>Cyan foreground.</summary>
        public const string Cyan = "\x1b[36m";

        /// <summary>
        /// Wraps text in an escape sequence when colors are enabled.
        /// </summary>
        /// <param name="text">Text to wrap.</param>
        /// <param name="code">Escape sequence without the reset.</param>
        /// <returns>Styled text, or the original text when disabled.</returns>
        public static string Style(string text, string code)
        {
            if (!Enabled || text.Length == 0)
            {
                return text;
            }

            return code + text + Reset;
        }

        /// <summary>Renders text bold.</summary>
        /// <param name="text">Text to style.</param>
        /// <returns>Styled text.</returns>
        public static string Emphasis(string text)
        {
            return Style(text, Bold);
        }

        /// <summary>Renders text dimmed.</summary>
        /// <param name="text">Text to style.</param>
        /// <returns>Styled text.</returns>
        public static string Faint(string text)
        {
            return Style(text, Dim);
        }

        /// <summary>Renders text in red (errors, negative amounts).</summary>
        /// <param name="text">Text to style.</param>
        /// <returns>Styled text.</returns>
        public static string Danger(string text)
        {
            return Style(text, Red);
        }

        /// <summary>Renders text in green (paid, zero balances).</summary>
        /// <param name="text">Text to style.</param>
        /// <returns>Styled text.</returns>
        public static string Success(string text)
        {
            return Style(text, Green);
        }

        /// <summary>Renders text in yellow (warnings, overdue).</summary>
        /// <param name="text">Text to style.</param>
        /// <returns>Styled text.</returns>
        public static string Warning(string text)
        {
            return Style(text, Yellow);
        }
    }

    /// <summary>Horizontal alignment of one table column.</summary>
    public enum ColumnAlignment
    {
        /// <summary>Pad on the right (default).</summary>
        Left,

        /// <summary>Pad on the left (numbers).</summary>
        Right,
    }

    /// <summary>
    /// Renders fixed-width aligned tables. Column widths are computed from content,
    /// ANSI escape sequences are ignored for width purposes and cells beyond the
    /// width cap are truncated with "...".
    /// </summary>
    public static class TableRenderer
    {
        /// <summary>Default per-column width cap.</summary>
        public const int DefaultMaxColumnWidth = 60;

        /// <summary>
        /// Renders a table with a colored header and a dash separator.
        /// </summary>
        /// <param name="headers">Column headers.</param>
        /// <param name="rows">Data rows; null cells render as empty.</param>
        /// <param name="alignments">Per-column alignment; null entries default to left.</param>
        /// <param name="color">False disables header coloring even when ANSI is on.</param>
        /// <param name="maxWidth">Per-column width cap.</param>
        /// <returns>The complete table as a multi-line string.</returns>
        public static string Render(
            IReadOnlyList<string> headers,
            IEnumerable<IReadOnlyList<string?>> rows,
            IReadOnlyList<ColumnAlignment>? alignments = null,
            bool color = true,
            int maxWidth = DefaultMaxColumnWidth)
        {
            if (headers is null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            if (rows is null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            if (maxWidth < 4)
            {
                throw new ArgumentOutOfRangeException(nameof(maxWidth), "Column width cap must be at least 4.");
            }

            var dataRows = new List<IReadOnlyList<string>>();
            foreach (IReadOnlyList<string?> row in rows)
            {
                var cells = new string[headers.Count];
                for (int c = 0; c < headers.Count; c++)
                {
                    cells[c] = c < row.Count ? row[c] ?? string.Empty : string.Empty;
                }

                dataRows.Add(cells);
            }

            var widths = new int[headers.Count];
            for (int c = 0; c < headers.Count; c++)
            {
                int width = VisibleLength(headers[c]);
                foreach (IReadOnlyList<string> row in dataRows)
                {
                    int length = VisibleLength(row[c]);
                    if (length > width)
                    {
                        width = length;
                    }
                }

                widths[c] = Math.Min(width, maxWidth);
            }

            var lines = new List<string>();
            var headerLine = new StringBuilder();
            for (int c = 0; c < headers.Count; c++)
            {
                if (c > 0)
                {
                    headerLine.Append("  ");
                }

                headerLine.Append(Pad(headers[c], widths[c], ColumnAlignment.Left, maxWidth));
            }

            lines.Add(color ? Ansi.Style(headerLine.ToString(), Ansi.Bold + Ansi.Cyan) : headerLine.ToString());

            var separator = new StringBuilder();
            for (int c = 0; c < headers.Count; c++)
            {
                if (c > 0)
                {
                    separator.Append("  ");
                }

                separator.Append('-', widths[c]);
            }

            lines.Add(separator.ToString());

            foreach (IReadOnlyList<string> row in dataRows)
            {
                var line = new StringBuilder();
                for (int c = 0; c < headers.Count; c++)
                {
                    if (c > 0)
                    {
                        line.Append("  ");
                    }

                    ColumnAlignment alignment = alignments is not null && c < alignments.Count
                        ? alignments[c]
                        : ColumnAlignment.Left;
                    line.Append(Pad(row[c], widths[c], alignment, maxWidth));
                }

                lines.Add(line.ToString());
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Truncates a plain string to a width, appending "..." when cut.
        /// </summary>
        /// <param name="value">Value to truncate (must be plain text, no ANSI codes).</param>
        /// <param name="width">Target width.</param>
        /// <returns>Truncated string.</returns>
        public static string Truncate(string value, int width)
        {
            if (value.Length <= width)
            {
                return value;
            }

            if (width <= 3)
            {
                return value[..Math.Max(0, width)];
            }

            return value[..(width - 3)] + "...";
        }

        /// <summary>Formats a date cell as "yyyy-MM-dd" (empty for null).</summary>
        /// <param name="utc">Date to format.</param>
        /// <returns>Formatted date cell.</returns>
        public static string FormatDate(DateTime? utc)
        {
            return utc is null
                ? string.Empty
                : utc.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>Colors a money cell: red when negative, dim when zero.</summary>
        /// <param name="amount">Amount driving the color choice.</param>
        /// <param name="formatted">Already formatted money string.</param>
        /// <returns>Styled cell.</returns>
        public static string ColorizeAmount(decimal amount, string formatted)
        {
            if (amount < 0m)
            {
                return Ansi.Danger(formatted);
            }

            if (amount == 0m)
            {
                return Ansi.Faint(formatted);
            }

            return formatted;
        }

        private static string Pad(string cell, int width, ColumnAlignment alignment, int maxWidth)
        {
            string content = cell;
            if (VisibleLength(content) > maxWidth)
            {
                content = Truncate(content, maxWidth);
            }

            int padding = width - VisibleLength(content);
            if (padding <= 0)
            {
                return content;
            }

            return alignment == ColumnAlignment.Right
                ? new string(' ', padding) + content
                : content + new string(' ', padding);
        }

        private static int VisibleLength(string text)
        {
            int count = 0;
            bool inEscape = false;
            foreach (char ch in text)
            {
                if (ch == '\x1b')
                {
                    inEscape = true;
                    continue;
                }

                if (inEscape)
                {
                    if (ch == 'm')
                    {
                        inEscape = false;
                    }

                    continue;
                }

                count++;
            }

            return count;
        }
    }
}
