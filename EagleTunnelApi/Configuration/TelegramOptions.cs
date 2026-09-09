namespace EagleTunnelApi.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public string SupportUrl { get; set; } = string.Empty;

    public string TributeSubscriptionUrl { get; set; } = string.Empty;

    public int[] DefaultInboundIds { get; set; } = [];

    public long[] AdminIds { get; set; } = [];

    public string WebhookUrl { get; set; } = string.Empty;

    public string WebhookPath { get; set; } = "/webhooks/telegram";

    public string WebhookSecretToken { get; set; } = string.Empty;

    public string BotUsername { get; set; } = string.Empty;

    public int ReferralBonusDays { get; set; } = 30;

    public int ReminderHourUtc { get; set; } = 9;
}