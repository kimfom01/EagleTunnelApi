using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Configuration;

public sealed class TelegramOptionsValidator(IHostEnvironment environment) : IValidateOptions<TelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            errors.Add("Telegram:BotToken is required.");
        }

        if (string.IsNullOrWhiteSpace(options.SupportUrl))
        {
            errors.Add("Telegram:SupportUrl is required.");
        }

        if (options.DefaultInboundIds is { Length: 0 })
        {
            errors.Add("Telegram:DefaultInboundIds must contain at least one inbound id.");
        }

        if (options.DefaultInboundIds.Any(id => id <= 0))
        {
            errors.Add("Telegram:DefaultInboundIds must be comma-separated positive integers.");
        }

        if (!string.IsNullOrWhiteSpace(options.WebhookPath) && !options.WebhookPath.StartsWith('/'))
        {
            errors.Add("Telegram:WebhookPath must start with '/'.");
        }

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            errors.Add("Telegram:WebhookUrl is required in the current environment.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class PanelOptionsValidator : IValidateOptions<PanelOptions>
{
    public ValidateOptionsResult Validate(string? name, PanelOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUri))
        {
            errors.Add("Panel:BaseUri is required.");
        }
        else if (!Uri.TryCreate(options.BaseUri, UriKind.Absolute, out _))
        {
            errors.Add("Panel:BaseUri must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.Add("Panel:ApiKey is required.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class TributeOptionsValidator : IValidateOptions<TributeOptions>
{
    public ValidateOptionsResult Validate(string? name, TributeOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.Add("Tribute:ApiKey is required.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUri))
        {
            errors.Add("Tribute:BaseUri is required.");
        }
        else if (!Uri.TryCreate(options.BaseUri, UriKind.Absolute, out _))
        {
            errors.Add("Tribute:BaseUri must be an absolute URI.");
        }

        if (options.ShopId is <= 0)
        {
            errors.Add("Tribute:ShopId must be a positive integer when set.");
        }

        if (!string.IsNullOrWhiteSpace(options.SuccessUrl) &&
            (!Uri.TryCreate(options.SuccessUrl, UriKind.Absolute, out var successUri) ||
             successUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Tribute:SuccessUrl must be a valid https:// URL when set.");
        }

        if (!string.IsNullOrWhiteSpace(options.FailUrl) &&
            (!Uri.TryCreate(options.FailUrl, UriKind.Absolute, out var failUri) ||
             failUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Tribute:FailUrl must be a valid https:// URL when set.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
