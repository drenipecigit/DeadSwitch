using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadSwitch.Api.Json;

// A DateTimeOffset is written as +00:00 by default, while a UTC DateTime is written with Z.
// System.Text.Json has no setting for the format, so this converter makes every time in the API
// look the same. It changes the text in the JSON only, not the database or the logic.
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    // Must exist because the method is abstract. No request contains a time today.
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        // UtcDateTime converts to UTC, and a UTC DateTime is written with Z.
        // 13:00+01:00 therefore becomes 12:00Z, which is the same moment.
        writer.WriteStringValue(value.UtcDateTime);
    }
}
