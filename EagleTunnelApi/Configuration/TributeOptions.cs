namespace EagleTunnelApi.Configuration;

public sealed class TributeOptions
{
    public const string SectionName = "Tribute";

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUri { get; set; } = "https://tribute.tg/api/v1";

    public long? ShopId { get; set; }

    public string SuccessUrl { get; set; } = string.Empty;

    public string FailUrl { get; set; } = string.Empty;
}