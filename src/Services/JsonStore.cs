using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceMaster.Services
{
    /// <summary>
    /// Raised when a JSON data file cannot be read, written or safely recovered.
    /// Derives from <see cref="InvoiceMasterException"/> so the CLI maps it to exit code 1.
    /// </summary>
    public sealed class JsonStoreException : InvoiceMasterException
    {
        /// <summary>Creates a storage-layer failure message.</summary>
        /// <param name="message">Human-readable description of the failure.</param>
        public JsonStoreException(string message)
            : base(message)
        {
        }

        /// <summary>Creates a storage-layer failure wrapping the IO/JSON cause.</summary>
        /// <param name="message">Human-readable description of the failure.</param>
        /// <param name="innerException">Underlying exception.</param>
        public JsonStoreException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Generic durable store for one JSON file. Guarantees:
    /// <list type="bullet">
    /// <item>Atomic writes: data lands via a temp file plus an atomic move, so a crash
    /// never leaves a half-written document behind.</item>
    /// <item>Backup rotation: every successful save shifts .bak.1..N copies so the last
    /// N good versions are always available.</item>
    /// <item>Self-healing loads: a corrupt main file is transparently restored from the
    /// newest readable backup.</item>
    /// </list>
    /// Instances are thread-safe within a single process.
    /// </summary>
    /// <typeparam name="T">Root document type, typically a List of entities.</typeparam>
    public sealed class JsonStore<T>
        where T : class
    {
        /// <summary>Serializer settings shared by every store: indented, camelCase,
        /// enums written as readable strings.</summary>
        public static readonly JsonSerializerOptions DefaultOptions = CreateDefaultOptions();

        private readonly string _filePath;
        private readonly string _backupDirectory;
        private readonly int _backupCount;
        private readonly object _sync = new();

        /// <summary>
        /// Creates a store for one JSON file.
        /// </summary>
        /// <param name="filePath">Absolute or relative path of the data file.</param>
        /// <param name="backupCount">How many rotated backups to keep (0 disables backups).</param>
        public JsonStore(string filePath, int backupCount = 5)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("A file path is required.", nameof(filePath));
            }

            if (backupCount < 0 || backupCount > 50)
            {
                throw new ArgumentOutOfRangeException(nameof(backupCount), "Backup count must be between 0 and 50.");
            }

            _filePath = Path.GetFullPath(filePath);
            string? directory = Path.GetDirectoryName(_filePath);
            _backupDirectory = string.IsNullOrEmpty(directory)
                ? "."
                : Path.Combine(directory, "backups");
            _backupCount = backupCount;
        }

        /// <summary>Full path of the managed data file.</summary>
        public string FilePath => _filePath;

        /// <summary>True when the data file currently exists on disk.</summary>
        public bool Exists
        {
            get
            {
                lock (_sync)
                {
                    return File.Exists(_filePath);
                }
            }
        }

        /// <summary>
        /// Loads the document, returning null when the file is missing or empty. If the
        /// main file is corrupt the newest readable backup is used instead and a warning
        /// is written to stderr.
        /// </summary>
        /// <returns>The deserialized document, or null when nothing is stored yet.</returns>
        public T? Load()
        {
            lock (_sync)
            {
                if (!File.Exists(_filePath))
                {
                    return null;
                }

                string json;
                try
                {
                    json = File.ReadAllText(_filePath);
                }
                catch (IOException ex)
                {
                    throw new JsonStoreException("Unable to read data file '" + _filePath + "': " + ex.Message, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new JsonStoreException("Permission denied while reading '" + _filePath + "': " + ex.Message, ex);
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(json, DefaultOptions);
                }
                catch (JsonException ex)
                {
                    return RecoverFromBackups(ex);
                }
            }
        }

        /// <summary>
        /// Atomically persists the document: rotates backups, serializes to a temp file
        /// in the same directory, then moves it over the destination in one step.
        /// </summary>
        /// <param name="entity">Document to store; must not be null.</param>
        public void Save(T entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            lock (_sync)
            {
                EnsureDirectory(Path.GetDirectoryName(_filePath));
                if (_backupCount > 0 && File.Exists(_filePath))
                {
                    RotateBackups();
                }

                string tempPath = _filePath
                    + ".tmp-"
                    + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "-"
                    + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

                try
                {
                    string json = JsonSerializer.Serialize(entity, DefaultOptions);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _filePath, overwrite: true);
                }
                catch (IOException ex)
                {
                    CleanupTemp(tempPath);
                    throw new JsonStoreException("Unable to write data file '" + _filePath + "': " + ex.Message, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    CleanupTemp(tempPath);
                    throw new JsonStoreException("Permission denied while writing '" + _filePath + "': " + ex.Message, ex);
                }
                catch (JsonException ex)
                {
                    CleanupTemp(tempPath);
                    throw new JsonStoreException("Unable to serialize the document for '" + _filePath + "': " + ex.Message, ex);
                }
            }
        }

        /// <summary>Paths of the rotated backups that currently exist (oldest last).</summary>
        /// <returns>Existing backup file paths in rotation order.</returns>
        public IReadOnlyList<string> BackupFiles()
        {
            lock (_sync)
            {
                var found = new List<string>();
                string baseName = Path.GetFileName(_filePath);
                for (int i = 1; i <= _backupCount; i++)
                {
                    string candidate = Path.Combine(_backupDirectory, baseName + ".bak." + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (File.Exists(candidate))
                    {
                        found.Add(candidate);
                    }
                }

                return found;
            }
        }

        /// <summary>Default data directory "~/.invoicemaster" of the current user.</summary>
        /// <returns>Absolute path of the default data directory.</returns>
        public static string DefaultDataDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.CurrentDirectory;
            }

            return Path.Combine(home, ".invoicemaster");
        }

        /// <summary>
        /// Creates a directory (including parents) when it does not exist yet.
        /// </summary>
        /// <param name="directory">Directory to create; null or empty is a no-op.</param>
        public static void EnsureDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private T? RecoverFromBackups(JsonException cause)
        {
            foreach (string backup in ListNewestFirst())
            {
                try
                {
                    string backupJson = File.ReadAllText(backup);
                    T? restored = JsonSerializer.Deserialize<T>(backupJson, DefaultOptions);
                    if (restored is not null)
                    {
                        Console.Error.WriteLine(
                            "invoicemaster: warning: data file '" + _filePath
                            + "' was unreadable (" + cause.Message + "); restored the newest backup '" + backup + "'.");
                        return restored;
                    }
                }
                catch (IOException)
                {
                    // Try the next-older backup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Try the next-older backup.
                }
                catch (JsonException)
                {
                    // Try the next-older backup.
                }
            }

            throw new JsonStoreException(
                "Data file '" + _filePath + "' is corrupt and none of its backups could be read. "
                + "Move the file away (it will be recreated empty) or restore a backup from '"
                + _backupDirectory + "' manually.",
                cause);
        }

        private List<string> ListNewestFirst()
        {
            var names = new List<string>();
            string baseName = Path.GetFileName(_filePath);
            for (int i = 1; i <= _backupCount; i++)
            {
                string candidate = Path.Combine(_backupDirectory, baseName + ".bak." + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (File.Exists(candidate))
                {
                    names.Add(candidate);
                }
            }

            return names;
        }

        private void RotateBackups()
        {
            EnsureDirectory(_backupDirectory);
            string baseName = Path.GetFileName(_filePath);
            string oldest = Path.Combine(_backupDirectory, baseName + ".bak." + _backupCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (int i = _backupCount - 1; i >= 1; i--)
            {
                string source = Path.Combine(_backupDirectory, baseName + ".bak." + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (File.Exists(source))
                {
                    string target = Path.Combine(_backupDirectory, baseName + ".bak." + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(source, target, overwrite: true);
                }
            }

            File.Copy(_filePath, Path.Combine(_backupDirectory, baseName + ".bak.1"), overwrite: true);
        }

        private static void CleanupTemp(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Best effort only; the next save retries with a fresh temp name.
            }
        }

        private static JsonSerializerOptions CreateDefaultOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
