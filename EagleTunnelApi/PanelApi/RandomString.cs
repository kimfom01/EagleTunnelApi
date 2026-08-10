using System.Security.Cryptography;

namespace EagleTunnelApi.PanelApi;

public static class RandomString
{
    private const string LowerAndNumChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string LowerAndNum(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];

        for (var i = 0; i < length; i++)
        {
            chars[i] = LowerAndNumChars[bytes[i] % LowerAndNumChars.Length];
        }

        return new string(chars);
    }
}
