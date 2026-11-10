using System.Net;
using System.Text.Json.Serialization;

namespace Squid.Message.Response;

public class SquidResponse : IResponse
{
    public string Msg { get; set; }

    public HttpStatusCode Code { get; set; }

    /// <summary>
    /// When <see cref="Code"/> is <see cref="HttpStatusCode.Forbidden"/>, names the
    /// permission the user lacks (e.g. <c>"MachineCreate"</c>). Lets Tentacle CLI and
    /// install-script consumers programmatically detect "this is a permission issue,
    /// here's exactly which permission" without parsing <see cref="Msg"/>.
    /// Null on success / non-403 responses.
    /// </summary>
    [JsonPropertyName("missingPermission")]
    public string MissingPermission { get; set; }

    /// <summary>
    /// When <see cref="MissingPermission"/> is set, lists the built-in roles
    /// that grant it (e.g. <c>["Environment Manager", "Space Owner", "System Administrator"]</c>).
    /// Empty array when no built-in role grants the permission (operator must
    /// add it to a custom role). Null on success / non-403 responses.
    /// </summary>
    [JsonPropertyName("suggestedRoles")]
    public IReadOnlyList<string> SuggestedRoles { get; set; }

    /// <summary>
    /// Stable machine-readable code for structured validation failures.
    /// Null on success and unstructured failures.
    /// </summary>
    [JsonPropertyName("validationCode")]
    public string ValidationCode { get; set; }

    /// <summary>
    /// The request field associated with <see cref="ValidationCode"/>.
    /// </summary>
    [JsonPropertyName("validationField")]
    public string ValidationField { get; set; }
}

public class SquidResponse<T> : SquidResponse
{
    public T Data { get; set; }
}
