using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WgAgent.Api;

namespace WgAgent.Tests.Api;

public sealed class JsonLogFormatterTests
{
    /// <summary>Formats one record the way the console provider would, and returns the parsed JSON line.</summary>
    private static JsonElement Format<TState>(LogLevel level, TState state, Exception? error, Func<TState, Exception?, string> message)
    {
        var entry = new LogEntry<TState>(level, "wg-agent", new EventId(0), state, error, message);
        using var writer = new StringWriter();
        new JsonLogFormatter().Write(entry, null, writer);
        var line = writer.ToString();
        Assert.EndsWith("\n", line);                       // one object per line
        return JsonDocument.Parse(line).RootElement;
    }

    [Fact]
    public void EachRecord_IsOneJsonObjectWithTsLevelMsg_REQ_OBS_010()
    {
        var json = Format(LogLevel.Information, "state", null, (_, _) => "serving on 127.0.0.1:9585");

        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.True(DateTimeOffset.TryParse(json.GetProperty("ts").GetString(), out _));
        Assert.Equal("info", json.GetProperty("level").GetString());
        Assert.Equal("serving on 127.0.0.1:9585", json.GetProperty("msg").GetString());
    }

    [Fact]
    public void Level_MapsToLowercaseNames_REQ_OBS_010()
    {
        Assert.Equal("debug", Format(LogLevel.Debug, "", null, (_, _) => "m").GetProperty("level").GetString());
        Assert.Equal("warn", Format(LogLevel.Warning, "", null, (_, _) => "m").GetProperty("level").GetString());
        Assert.Equal("error", Format(LogLevel.Error, "", null, (_, _) => "m").GetProperty("level").GetString());
    }

    [Fact]
    public void StructuredFields_AndError_AreTheirOwnMembers_REQ_OBS_011()
    {
        // A structured message carries its placeholders as fields; an exception adds "error".
        var state = new List<KeyValuePair<string, object?>>
        {
            new("reason", "APPLY_FAILED"),
            new("interface", "wg0"),
            new("{OriginalFormat}", "request refused {reason}"),
        };
        var json = Format(LogLevel.Warning, (IReadOnlyList<KeyValuePair<string, object?>>)state,
            new InvalidOperationException("the unit would not start"), (_, _) => "request refused APPLY_FAILED");

        Assert.Equal("APPLY_FAILED", json.GetProperty("reason").GetString());
        Assert.Equal("wg0", json.GetProperty("interface").GetString());
        Assert.Equal("the unit would not start", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("{OriginalFormat}", out _));   // the template itself is not a field
    }

    [Fact]
    public void Minimum_ReadsTheLogLevelKey_REQ_OBS_010()
    {
        Assert.Equal(LogLevel.Debug, JsonLogFormatter.Minimum("debug"));
        Assert.Equal(LogLevel.Information, JsonLogFormatter.Minimum("info"));
        Assert.Equal(LogLevel.Warning, JsonLogFormatter.Minimum("warn"));
        Assert.Equal(LogLevel.Error, JsonLogFormatter.Minimum("error"));
    }
}
