using System;
using System.Linq;
using InvoiceMaster.Cli;

namespace InvoiceMaster
{
    /// <summary>
    /// Entry point: wires the CLI together, normalizes errors into exit codes and
    /// never lets an exception escape as a stack trace.
    /// Exit codes: 0 success, 1 runtime/validation error, 2 command-line usage error.
    /// </summary>
    public static class Program
    {
        /// <summary>Semantic version printed by --version and shown in help.</summary>
        public const string Version = "1.0.0";

        /// <summary>
        /// Program entry point.
        /// </summary>
        /// <param name="args">Command line arguments without the program name.</param>
        /// <returns>Process exit code: 0 success, 1 runtime error, 2 usage error.</returns>
        public static int Main(string[] args)
        {
            Ansi.Enabled = !ContainsNoColor(args)
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && !Console.IsOutputRedirected;

            try
            {
                ParsedCommand command = CliParser.Parse(args);
                if (command.WantsVersion)
                {
                    Console.WriteLine(HelpText.Version());
                    return 0;
                }

                if (command.WantsHelp)
                {
                    Console.WriteLine(HelpText.For(command.Command.Length == 0 ? null : command.Command));
                    return 0;
                }

                CliApp app = CliApp.Create(command);
                return app.Run(command);
            }
            catch (UsageException ex)
            {
                WriteError(ex.Message);
                WriteError("Run 'invoicemaster help' for the command list, or 'invoicemaster help <command>' for details.");
                return 2;
            }
            catch (InvoiceMasterException ex)
            {
                WriteError(ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                WriteError("Unexpected error: " + ex.GetType().Name + " — " + ex.Message);
                WriteError("This looks like a bug in invoicemaster; your data files were not modified by this failure.");
                return 1;
            }
        }

        /// <summary>
        /// Determines whether the caller explicitly asked for monochrome output so the
        /// decision happens before anything is printed.
        /// </summary>
        /// <param name="args">Raw command line arguments.</param>
        /// <returns>True when --no-color is present.</returns>
        private static bool ContainsNoColor(string[]? args)
        {
            return args is not null && args.Any(a => string.Equals(a, "--no-color", StringComparison.Ordinal));
        }

        /// <summary>Writes one line to standard error.</summary>
        /// <param name="message">Message text.</param>
        private static void WriteError(string message)
        {
            Console.Error.WriteLine("invoicemaster: error: " + message);
        }
    }
}
