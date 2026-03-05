using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record GetUserResponse(
    [property: JsonPropertyName("response")]
    IReadOnlyList<RemnawaveUser> Response
);

public record RemnawaveUser(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("shortUuid")]
    string ShortUuid,
    [property: JsonPropertyName("username")]
    string Username,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("telegramId")]
    long TelegramId
);