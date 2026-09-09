using System.Security.Cryptography;

namespace EagleTunnelApi.PanelApi;

public static class RandomString
{
    private const string LowerAndNumChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string LowerAndNum(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        // GetItems performs rejection sampling, avoiding the modulo bias of `bytes[i] % chars.Length`.
        return new string(RandomNumberGenerator.GetItems<char>(LowerAndNumChars, length));
    }
}