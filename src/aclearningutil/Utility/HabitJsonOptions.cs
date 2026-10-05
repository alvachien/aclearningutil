using System.Text.Json;
using System.Text.Json.Serialization;

namespace aclearningutil.Utility;

/// <summary>
/// Shared JSON wiring for the habit-tracking wire format. Registered on the MVC JSON
/// options in Program.cs; unit tests assert the converter behaviour through this same
/// entry point so the tested configuration can never drift from the deployed one.
/// </summary>
public static class HabitJsonOptions
{
    /// <summary>
    /// Enum members travel as the spec's lowercase/snake strings ("daily", "per_cycle",
    /// "boolean", ...) — and strings ONLY: allowIntegerValues is false, so an integer enum
    /// value in a request body is rejected at binding time (400) instead of silently
    /// mapping to whatever member owns that number.
    /// </summary>
    public static void ConfigureEnumConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }
}
