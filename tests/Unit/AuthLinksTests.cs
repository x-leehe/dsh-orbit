using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// AuthLinks 是静态可变状态（TokenUrl / RelayPort），每个用例前后必须还原，
/// 否则会污染同程序集里的其它测试。用 IDisposable 夹具：xUnit 每个测试新建实例、测试后 Dispose。
/// </summary>
public sealed class AuthLinksTests : IDisposable
{
    private readonly string? _originalTokenUrl;
    private readonly int _originalRelayPort;

    public AuthLinksTests()
    {
        _originalTokenUrl = AuthLinks.TokenUrl;
        _originalRelayPort = AuthLinks.RelayPort;

        // 每个用例从「没有任何可用登录链接」的干净状态开始。
        AuthLinks.TokenUrl = null;
        AuthLinks.RelayPort = 0;
    }

    public void Dispose()
    {
        AuthLinks.TokenUrl = _originalTokenUrl;
        AuthLinks.RelayPort = _originalRelayPort;
    }

    [Fact]
    public void For_ReturnsTokenUrlWhenPresent()
    {
        AuthLinks.TokenUrl = "http://127.0.0.1:3080/?token=secret";
        AuthLinks.RelayPort = 5000;

        Assert.Equal("http://127.0.0.1:3080/?token=secret", AuthLinks.For("http://127.0.0.1:3080/"));
        Assert.True(AuthLinks.HasAuthenticatedLink);
    }

    [Fact]
    public void For_WrapsThroughRelayWhenOnlyRelayPortIsSet()
    {
        AuthLinks.TokenUrl = null;
        AuthLinks.RelayPort = 51234;
        const string plain = "http://127.0.0.1:3080/";

        var expected = "http://127.0.0.1:51234/open?to=" + Uri.EscapeDataString(plain);
        Assert.Equal(expected, AuthLinks.For(plain));
        Assert.True(AuthLinks.HasAuthenticatedLink);
    }

    [Fact]
    public void For_TreatsEmptyTokenUrlAsAbsent()
    {
        AuthLinks.TokenUrl = "";
        AuthLinks.RelayPort = 1234;

        var expected = "http://127.0.0.1:1234/open?to=" + Uri.EscapeDataString("http://127.0.0.1:3080/");
        Assert.Equal(expected, AuthLinks.For("http://127.0.0.1:3080/"));
        Assert.True(AuthLinks.HasAuthenticatedLink); // 只靠 RelayPort 也算有可用链接
    }

    [Fact]
    public void For_ReturnsOriginalUrlWhenNoAuthenticatedLinkExists()
    {
        AuthLinks.TokenUrl = null;
        AuthLinks.RelayPort = 0;
        const string plain = "http://127.0.0.1:3080/";

        Assert.False(AuthLinks.HasAuthenticatedLink);
        Assert.Equal(plain, AuthLinks.For(plain));
    }

    [Fact]
    public void For_UrlEncodesTarget()
    {
        AuthLinks.TokenUrl = null;
        AuthLinks.RelayPort = 4321;
        const string target = "http://127.0.0.1:3080/a b?x=1&y=2";

        var result = AuthLinks.For(target);
        Assert.Equal("http://127.0.0.1:4321/open?to=" + Uri.EscapeDataString(target), result);
        Assert.Contains("%20", result); // 空格
        Assert.Contains("%3F", result); // ?
        Assert.Contains("%26", result); // &
        Assert.Contains("%3D", result); // =
    }
}
