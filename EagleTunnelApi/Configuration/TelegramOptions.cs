namespace EagleTunnelApi.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public string SupportUrl { get; set; } = string.Empty;

    public int[] DefaultInboundIds { get; set; } = [];

    public string WebhookUrl { get; set; } = string.Empty;

    public string WebhookPath { get; set; } = "/webhook/telegram";

    public string WebhookSecretToken { get; set; } = string.Empty;
}
