using System.Text.Json.Serialization;

namespace EagleTunnelApi.Infrastructure.PanelApi.Models;

public sealed record PanelApiResponseDto<T>(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("msg")] string Msg,
    [property: JsonPropertyName("obj")] T? Obj
);
