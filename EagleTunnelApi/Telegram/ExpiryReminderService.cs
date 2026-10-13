using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EagleTunnelApi.Telegram;

public sealed class ExpiryReminderService(
    IPanelClient panelClient,
    ITelegramBotClient botClient,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<ExpiryReminderService> logger) : BackgroundService
{
    private static readonly int[] ReminderDays = [3, 2, 1, 0];

    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun(DateTime.UtcNow, _telegramOptions.ReminderHourUtc);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var sent = await SendRemindersAsync(stoppingToken);
                logger.LogInformation("Expiry reminder pass complete. Sent: {Sent}", sent);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Expiry reminder pass failed.");
            }
        }
    }

    public async Task<int> SendRemindersAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sent = 0;

        IReadOnlyList<PanelClientSummary> summaries;
        try
        {
            summaries = await panelClient.GetAllClientsAsync(cancellationToken);
        }
        catch (PanelApiException ex)
        {
            logger.LogError(ex, "Failed to list clients for expiry reminders.");
            return 0;
        }

        foreach (var summary in summaries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!summary.Enable || summary.ExpiryTime <= 0 ||
                ReferralService.IsLegacyEmail(summary.Email))
                continue;

            var expiryDate = DateOnly.FromDateTime(
                DateTimeOffset.FromUnixTimeMilliseconds(summary.ExpiryTime).UtcDateTime);
            var daysLeft = expiryDate.DayNumber - today.DayNumber;

            if (!ReminderDays.Contains(daysLeft)) continue;

            try
            {
                if (await TryRemindAsync(summary.Email, expiryDate, daysLeft, cancellationToken)) sent++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to remind client. Email: {Email}", summary.Email);
            }
        }

        return sent;
    }

    private async Task<bool> TryRemindAsync(string email, DateOnly expiryDate, int daysLeft,
        CancellationToken cancellationToken)
    {
        var response = await panelClient.GetClientByEmailAsync(email, cancellationToken);
        var client = response?.Client;

        if (client is null || client.TgId <= 0) return false;

        if (!ReferralService.IsCancelled(client.Comment)) return false;

        if (ReferralService.HasReminderMarker(client.Comment, daysLeft, expiryDate)) return false;

        await botClient.SendMessage(client.TgId,
            BuildReminderText(daysLeft, expiryDate, _telegramOptions.TributeSubscriptionUrl,
                _telegramOptions.SupportUrl),
            cancellationToken: cancellationToken);

        await panelClient.UpdateClientAsync(client.ToUpdateRequest() with
        {
            Comment = ReferralService.AppendTag(client.Comment,
                ReferralService.BuildReminderMarker(daysLeft, expiryDate))
        }, cancellationToken);

        logger.LogInformation("Expiry reminder sent. Email: {Email}, DaysLeft: {DaysLeft}", email, daysLeft);
        return true;
    }

    public static string BuildReminderText(int daysLeft, DateOnly expiryDate, string tributeUrl,
        string supportUrl)
    {
        if (daysLeft < 0)
            return $"🚨 Your VPN access expired on {expiryDate:yyyy-MM-dd}.\n\n" +
                   $"Resubscribe here to get back online:\n{tributeUrl}\n\n" +
                   $"Need help? Message support: {supportUrl}";

        if (daysLeft == 0)
            return $"🚨 Your VPN access ends today ({expiryDate:yyyy-MM-dd}).\n\n" +
                   $"Resubscribe here to stay connected:\n{tributeUrl}\n\n" +
                   $"Need help? Message support: {supportUrl}";

        var dayWord = daysLeft == 1 ? "day" : "days";
        return $"⏳ Heads up: your VPN access ends in {daysLeft} {dayWord} ({expiryDate:yyyy-MM-dd}).\n\n" +
               "Your subscription is currently cancelled — " +
               $"resubscribe here to stay connected:\n{tributeUrl}\n\n" +
               $"Need help? Message support: {supportUrl}";
    }

    public static TimeSpan TimeUntilNextRun(DateTime utcNow, int hourUtc)
    {
        var next = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, hourUtc, 0, 0, DateTimeKind.Utc);

        if (next <= utcNow) next = next.AddDays(1);

        return next - utcNow;
    }
}