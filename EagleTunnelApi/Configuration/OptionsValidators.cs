using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Configuration;

public sealed class TelegramOptionsValidator(IHostEnvironment environment) : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BotToken)) errors.Add("Telegram:BotToken is required.");

        if (string.IsNullOrWhiteSpace(options.SupportUrl)) errors.Add("Telegram:SupportUrl is required.");

        if (string.IsNullOrWhiteSpace(options.TributeSubscriptionUrl))
            errors.Add("Telegram:TributeSubscriptionUrl is required.");

        if (options.DefaultInboundIds is { Length: 0 })
            errors.Add("Telegram:DefaultInboundIds must contain at least one inbound id.");

        if (options.DefaultInboundIds.Any(id => id <= 0))
            errors.Add("Telegram:DefaultInboundIds must be comma-separated positive integers.");

        if (options.AdminIds.Any(id => id <= 0))
            errors.Add("Telegram:AdminIds must be comma-separated positive Telegram ids.");

        if (!string.IsNullOrWhiteSpace(options.WebhookPath) && !options.WebhookPath.StartsWith('/'))
            errors.Add("Telegram:WebhookPath must start with '/'.");

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(options.WebhookUrl))
            errors.Add("Telegram:WebhookUrl is required in the current environment.");

        if (string.IsNullOrWhiteSpace(options.BotUsername))
            errors.Add("Telegram:BotUsername is required.");
        else if (options.BotUsername.StartsWith('@') || options.BotUsername.Any(char.IsWhiteSpace))
            errors.Add("Telegram:BotUsername must be the bare username without '@' or spaces.");

        if (options.ReferralBonusDays < 0 || options.ReferralBonusDays > 365)
            errors.Add("Telegram:ReferralBonusDays must be between 0 and 365.");

        if (options.ReminderHourUtc < 0 || options.ReminderHourUtc > 23)
            errors.Add("Telegram:ReminderHourUtc must be between 0 and 23.");

        if (options.TrialDurationHours < 0 || options.TrialDurationHours > 168)
            errors.Add("Telegram:TrialDurationHours must be between 0 and 168.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class PanelOptionsValidator : IValidateOptions<PanelOptions>
{
    public ValidateOptionsResult Validate(string? name, PanelOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUri))
            errors.Add("Panel:BaseUri is required.");
        else if (!Uri.TryCreate(options.BaseUri, UriKind.Absolute, out _))
            errors.Add("Panel:BaseUri must be an absolute URI.");

        if (string.IsNullOrWhiteSpace(options.ApiKey)) errors.Add("Panel:ApiKey is required.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class TributeOptionsValidator : IValidateOptions<TributeOptions>
{
    public ValidateOptionsResult Validate(string? name, TributeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) return ValidateOptionsResult.Fail("Tribute:ApiKey is required.");

        return ValidateOptionsResult.Success;
    }
}