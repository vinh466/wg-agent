using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace WgAgent.Api;

/// <summary>
/// Logs as one JSON object per line on stdout (REQ-OBS-010). Every record carries <c>ts</c>,
/// <c>level</c> and <c>msg</c>; a logged exception adds <c>error</c>, and each field of a structured
/// message — <c>interface</c>, <c>peer</c>, <c>reason</c> — is written under its own name
/// (REQ-OBS-011). It reflects over nothing, so it holds under NativeAOT.
/// </summary>
public sealed class JsonLogFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "wgagent-json";

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTimeOffset.UtcNow);
            json.WriteString("level", Level(entry.LogLevel));
            json.WriteString("msg", message);
            if (entry.Exception is { } error)
                json.WriteString("error", error.Message);

            if (entry.State is IReadOnlyList<KeyValuePair<string, object?>> fields)
                foreach (var field in fields)
                    if (field.Key != "{OriginalFormat}")
                        json.WriteString(field.Key, field.Value?.ToString());

            json.WriteEndObject();
        }
        writer.Write(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        writer.Write('\n');
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error or LogLevel.Critical => "error",
        _ => "info",
    };

    /// <summary>The minimum level from the <c>log.level</c> configuration key.</summary>
    public static LogLevel Minimum(string logLevel) => logLevel switch
    {
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
