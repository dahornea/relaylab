using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayLab.Core;

public sealed record EventData(string? DocumentId);
public sealed record EventRequest(string? DestinationId, string? EventType, EventData? Data);
public sealed record WorkEnvelope(int Version, Guid DeliveryId, Guid WorkId);

public static class EventContract
{
    public const int BodyLimit = 4096;
    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && key.Length <= 128 && key.All(c => c >= '!' && c <= '~');
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static Dictionary<string, string[]> Validate(EventRequest? request, string? key = null, bool requireKey = true)
    {
        var errors = new Dictionary<string, string[]>();
        if (requireKey && !IsValidKey(key))
            errors["Idempotency-Key"] = ["Use 1-128 visible ASCII characters without whitespace."];
        if (request?.DestinationId != "demo")
            errors["destinationId"] = ["The configured destination is demo."];
        if (request?.EventType != "document.ready")
            errors["eventType"] = ["The supported event type is document.ready."];
        if (string.IsNullOrWhiteSpace(request?.Data?.DocumentId) || request.Data.DocumentId.Length > 128)
            errors["data.documentId"] = ["Supply a nonempty identifier of at most 128 characters."];
        return errors;
    }

    public static bool Matches(EventRequest request, string destination, string eventType, string document) =>
        string.Equals(request.DestinationId, destination, StringComparison.Ordinal) &&
        string.Equals(request.EventType, eventType, StringComparison.Ordinal) &&
        string.Equals(request.Data?.DocumentId, document, StringComparison.Ordinal);
}
