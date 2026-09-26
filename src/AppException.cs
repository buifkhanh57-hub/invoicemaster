using System;
using System.Collections.Generic;
using System.Text;

namespace InvoiceMaster
{
    /// <summary>
    /// Base exception for every deliberate, user-facing error raised by invoicemaster.
    /// Carries the process exit code the CLI should return for this failure so that
    /// shell scripts can distinguish validation problems (1) from usage problems (2).
    /// </summary>
    public class InvoiceMasterException : Exception
    {
        /// <summary>Exit code the CLI should return for this exception.</summary>
        public int ExitCode { get; }

        /// <summary>Creates a new invoicemaster exception with the default exit code 1.</summary>
        /// <param name="message">Human-readable description of the failure.</param>
        public InvoiceMasterException(string message)
            : base(message)
        {
            ExitCode = 1;
        }

        /// <summary>Creates a new invoicemaster exception with an explicit exit code.</summary>
        /// <param name="message">Human-readable description of the failure.</param>
        /// <param name="exitCode">Process exit code to return.</param>
        public InvoiceMasterException(string message, int exitCode)
            : base(message)
        {
            ExitCode = exitCode;
        }

        /// <summary>Creates a new invoicemaster exception wrapping an inner cause.</summary>
        /// <param name="message">Human-readable description of the failure.</param>
        /// <param name="innerException">The underlying cause, if any.</param>
        public InvoiceMasterException(string message, Exception innerException)
            : base(message, innerException)
        {
            ExitCode = 1;
        }
    }

    /// <summary>
    /// Thrown when the command line itself is wrong: unknown options, missing required
    /// values, malformed numbers or dates, bad positional counts. Maps to exit code 2.
    /// </summary>
    public sealed class UsageException : InvoiceMasterException
    {
        private const int UsageExitCode = 2;

        /// <summary>Creates a usage error with exit code 2.</summary>
        /// <param name="message">Description of what the caller typed incorrectly.</param>
        public UsageException(string message)
            : base(message, UsageExitCode)
        {
        }
    }

    /// <summary>
    /// Thrown when business data fails validation (empty names, negative quantities,
    /// invalid tax rules, ...). Maps to the default runtime exit code 1.
    /// </summary>
    public sealed class ValidationException : InvoiceMasterException
    {
        /// <summary>Creates a validation error.</summary>
        /// <param name="message">Description of the violated rule.</param>
        public ValidationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Builds a single validation exception from a list of individual problems so the
        /// user can fix every field in one pass instead of one round trip per error.
        /// </summary>
        /// <param name="heading">Sentence introducing the list of problems.</param>
        /// <param name="errors">Individual, self-contained problem descriptions.</param>
        public static ValidationException ForErrors(string heading, IEnumerable<string> errors)
        {
            var list = new List<string>(errors);
            var builder = new StringBuilder();
            builder.Append(heading);
            if (list.Count == 0)
            {
                builder.Append(" (no details provided)");
                return new ValidationException(builder.ToString());
            }

            foreach (string error in list)
            {
                builder.AppendLine();
                builder.Append("  - ").Append(error);
            }

            return new ValidationException(builder.ToString());
        }
    }

    /// <summary>Thrown when a referenced entity (customer, invoice, tax rule) does not exist.</summary>
    public sealed class NotFoundException : InvoiceMasterException
    {
        /// <summary>Creates a not-found error.</summary>
        /// <param name="message">Description of what could not be found.</param>
        public NotFoundException(string message)
            : base(message)
        {
        }

        /// <summary>Builds a standard not-found message for a typed entity.</summary>
        /// <param name="kind">Entity kind, e.g. "customer" or "tax rule".</param>
        /// <param name="id">Identifier the user asked for.</param>
        /// <param name="hint">Optional actionable hint appended to the message.</param>
        public static NotFoundException For(string kind, string id, string? hint = null)
        {
            string message = $"The {kind} '{id}' was not found.";
            if (!string.IsNullOrWhiteSpace(hint))
            {
                message += " " + hint;
            }

            return new NotFoundException(message);
        }
    }

    /// <summary>
    /// Thrown when an operation conflicts with current state: issuing a non-draft
    /// invoice, recording a payment on a void invoice, deleting a referenced customer.
    /// </summary>
    public sealed class ConflictException : InvoiceMasterException
    {
        /// <summary>Creates a conflict error.</summary>
        /// <param name="message">Description of the conflicting state.</param>
        public ConflictException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Thrown when the JSON data directory or its files cannot be read or written.</summary>
    public sealed class StorageException : InvoiceMasterException
    {
        /// <summary>Creates a storage error.</summary>
        /// <param name="message">Description of the storage failure.</param>
        public StorageException(string message)
            : base(message)
        {
        }

        /// <summary>Creates a storage error wrapping the underlying IO cause.</summary>
        /// <param name="message">Description of the storage failure.</param>
        /// <param name="innerException">The underlying IO exception.</param>
        public StorageException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
