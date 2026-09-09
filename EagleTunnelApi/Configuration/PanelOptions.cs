namespace EagleTunnelApi.Configuration;

public sealed class PanelOptions
{
    public const string SectionName = "Panel";

    public string BaseUri { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}