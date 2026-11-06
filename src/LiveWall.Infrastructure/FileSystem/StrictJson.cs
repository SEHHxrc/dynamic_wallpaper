using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveWall.Infrastructure.FileSystem;

/// <summary>Serializer settings for persisted documents: unknown members and integer enums are rejected.</summary>
public static class StrictJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
