using EagleTunnelApi.Domain.Identity;
using Xunit;

namespace EagleTunnelApi.Tests.Domain;

public class EmailAddressTests
{
    [Theory]
    [InlineData("  USER@Example.COM ", "user@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Normalize_NormalizesOrNull(string? input, string? expected)
    {
        Assert.Equal(expected, EmailAddress.Normalize(input));
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("USER@Example.COM", true)]
    [InlineData("  spaced@example.com  ", true)]
    [InlineData("not-an-email", false)]
    [InlineData("user@localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_Validates(string? input, bool expected)
    {
        Assert.Equal(expected, EmailAddress.IsValid(input));
    }
}
