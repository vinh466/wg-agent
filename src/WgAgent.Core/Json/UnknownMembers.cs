using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WgAgent.Core.Json;

/// <summary>
/// Finds a member a type's schema does not define (REQ-VAL-050), reading the schema from the
/// source-generated contract, so it stays the one the serialiser uses.
/// </summary>
public static class UnknownMembers
{
    /// <summary>The path of the first member no property maps, or null when every one maps.</summary>
    public static string? Find(JsonElement element, JsonTypeInfo info, string path = "")
    {
        if (info.Kind == JsonTypeInfoKind.Object && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in element.EnumerateObject())
            {
                var here = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
                var property = info.Properties.FirstOrDefault(p => p.Name == member.Name);
                if (property is null) return here;
                if (Find(member.Value, info.Options.GetTypeInfo(property.PropertyType), here) is { } nested) return nested;
            }
        }
        else if (info.Kind == JsonTypeInfoKind.Enumerable && element.ValueKind == JsonValueKind.Array && info.ElementType is { } elementType)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                if (Find(item, info.Options.GetTypeInfo(elementType), $"{path}[{index++}]") is { } nested) return nested;
        }
        return null;
    }
}
