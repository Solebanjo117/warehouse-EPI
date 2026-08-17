using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WarehouseEPI.Web.Observability;

internal sealed class JsonRollingFileLoggerProvider(ObservabilitySettings settings) : ILoggerProvider
{
    private readonly object sync = new();

    public ILogger CreateLogger(string categoryName) => new JsonRollingFileLogger(categoryName, settings, sync);
    public void Dispose() { }

    private sealed class JsonRollingFileLogger(string category, ObservabilitySettings settings, object sync) : ILogger
    {
        private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
        {
            "CorrelationId", "RequestMethod", "RequestPath", "StatusCode", "ElapsedMilliseconds",
            "FailureCategory", "HealthStatus", "DatabaseLatencyMs"
        };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information &&
            category.StartsWith("WarehouseEPI.Observability", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var pair in pairs)
                    if (AllowedProperties.Contains(pair.Key)) values[pair.Key] = pair.Value;

            var entry = new Dictionary<string, object?>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["level"] = logLevel.ToString(),
                ["category"] = category,
                ["eventId"] = eventId.Id,
                ["eventName"] = eventId.Name,
                ["properties"] = values
            };
            if (exception is not null) entry["exceptionType"] = exception.GetType().Name;
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            var lineLength = System.Text.Encoding.UTF8.GetByteCount(line);

            lock (sync)
            {
                Directory.CreateDirectory(settings.LogDirectory);
                var path = GetWritablePath(lineLength);
                File.AppendAllText(path, line);
                RemoveExpiredFiles();
            }
        }

        private string GetWritablePath(int incomingLength)
        {
            var baseName = $"warehouse-{DateTime.UtcNow:yyyyMMdd}";
            for (var sequence = 0; ; sequence++)
            {
                var path = Path.Combine(settings.LogDirectory, sequence == 0 ? $"{baseName}.jsonl" : $"{baseName}.{sequence}.jsonl");
                if (!File.Exists(path) || new FileInfo(path).Length + incomingLength <= settings.FileSizeLimitBytes) return path;
            }
        }

        private void RemoveExpiredFiles()
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-settings.RetentionDays);
            foreach (var file in Directory.EnumerateFiles(settings.LogDirectory, "warehouse-*.jsonl"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }
}
