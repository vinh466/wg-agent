using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using WgAgent.Core;
using WgAgent.Core.Json;

namespace WgAgent.Api;

/// <summary>
/// Reads a request body against its schema: a member the schema does not define is
/// <c>FIELD_UNKNOWN</c> (REQ-VAL-050), and a body that does not parse or gives a field the wrong
/// type is <c>REQUEST_MALFORMED</c> (REQ-API-086). Both precede any rule of SPEC-07.
/// </summary>
internal static class Bodies
{
    public static async Task<T> Read<T>(HttpContext context, JsonTypeInfo<T> info)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            throw new AgentException(ReasonCodes.RequestMalformed, "The request body is not valid JSON.");
        }

        using (document)
        {
            if (UnknownMembers.Find(document.RootElement, info) is { } member)
                throw new AgentException(ReasonCodes.FieldUnknown, $"The request carries '{member}', which its schema does not define.");
            try
            {
                return document.RootElement.Deserialize(info) ?? throw new AgentException(ReasonCodes.RequestMalformed, "The request body is empty.");
            }
            catch (JsonException)
            {
                throw new AgentException(ReasonCodes.RequestMalformed, "A field of the request body has the wrong type.");
            }
        }
    }
}
