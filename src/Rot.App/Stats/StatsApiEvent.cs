using System.Text.Json;

namespace Rot.App.Stats;

public sealed record StatsApiEvent(string Name, string? MatchGuid, bool HasMatchGuidField)
{
    public StatsApiEvent(string name, string matchGuid)
        : this(name, matchGuid, true)
    {
    }

    public bool HasOnlineMatchGuid => HasMatchGuidField && !string.IsNullOrWhiteSpace(MatchGuid);
    public bool HasKnownEmptyMatchGuid => HasMatchGuidField && string.IsNullOrEmpty(MatchGuid);
}

public static class StatsApiEventParser
{
    public static bool TryParse(string json, out StatsApiEvent? statsEvent, out string? error)
    {
        statsEvent = null;
        error = null;
        try
        {
            using var envelope = JsonDocument.Parse(json);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetString(envelope.RootElement, "Event", out var eventName) ||
                string.IsNullOrWhiteSpace(eventName))
            {
                error = "Stats API message has no Event name.";
                return false;
            }

            if (!TryGetProperty(envelope.RootElement, "Data", out var dataElement))
            {
                statsEvent = new StatsApiEvent(eventName, null, false);
                return true;
            }

            if (dataElement.ValueKind == JsonValueKind.String)
            {
                var encodedData = dataElement.GetString();
                if (string.IsNullOrWhiteSpace(encodedData))
                {
                    statsEvent = new StatsApiEvent(eventName, null, false);
                    return true;
                }

                using var dataDocument = JsonDocument.Parse(encodedData);
                dataElement = dataDocument.RootElement;
                statsEvent = CreateEvent(eventName, dataElement);
                return true;
            }

            statsEvent = CreateEvent(eventName, dataElement);
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static StatsApiEvent CreateEvent(string eventName, JsonElement dataElement)
    {
        if (dataElement.ValueKind == JsonValueKind.Object &&
            TryGetString(dataElement, "MatchGuid", out var matchGuid))
        {
            return new StatsApiEvent(eventName, matchGuid, true);
        }

        return new StatsApiEvent(eventName, null, false);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
