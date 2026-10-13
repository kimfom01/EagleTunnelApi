namespace EagleTunnelApi.Configuration;

public sealed class TributeOptions
{
    public const string SectionName = "Tribute";

    public string ApiKey { get; set; } = string.Empty;
}