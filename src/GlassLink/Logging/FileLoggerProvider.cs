using System.Text;

namespace GlassLink.Logging;

/// <summary>
/// Writes diagnostics for the silent WinExe host. A tray/background application
/// has no console, so file logs are the primary support artifact.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
    }

    public static string? LogFilePath { get; private set; }

    public static string Initialize(string? preferredDirectory = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredDirectory)) candidates.Add(preferredDirectory);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink", "logs"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "logs"));

        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "glasslink.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 10 * 1024 * 1024)
                {
                    var archive = Path.Combine(directory, $"glasslink-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    try { File.Move(path, archive, overwrite: true); } catch { }
                }
                LogFilePath = path;
                FileSink.Configure(path);
                return path;
            }
            catch
            {
                // Try the next writable location.
            }
        }

        return "";
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _minimumLevel);

    public void Dispose() => FileSink.Flush();

    private static class FileSink
    {
        private static readonly object Gate = new();
        private static string? _path;

        public static void Configure(string path) => _path = path;

        public static void Write(string line)
        {
            if (string.IsNullOrEmpty(_path)) return;
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine, System.Text.Encoding.UTF8);
                }
                catch
                {
                    // Logging must never take down the host.
                }
            }
        }

        public static void Flush()
        {
            // AppendAllText flushes per call; kept as an explicit lifecycle hook.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public FileLogger(string category, LogLevel minimumLevel)
        {
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var builder = new StringBuilder(256);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(LevelName(logLevel)).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
                builder.AppendLine().Append(exception);
            FileSink.Write(builder.ToString());
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
