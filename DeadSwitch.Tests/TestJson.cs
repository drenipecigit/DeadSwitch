using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadSwitch.Tests;

// Lets the tests read the API's status text back into the enum, the same way Program.cs writes it.
// This copies the setting, so it proves nothing about the API itself. ResponseShapeTests does that
// by reading the raw JSON.
internal static class TestJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }
}
