using System.Net.Mail;

namespace EagleTunnelApi.Domain.Identity;

public static class EmailAddress
{
    private const int MaxLength = 254;

    public static string? Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        return email.Trim().ToLowerInvariant();
    }

    public static bool IsValid(string? email)
    {
        var normalized = Normalize(email);

        if (normalized is null || normalized.Length > MaxLength) return false;

        try
        {
            var address = new MailAddress(normalized);
            return address.Address == normalized && normalized.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
