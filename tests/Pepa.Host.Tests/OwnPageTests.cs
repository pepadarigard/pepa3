using Pepa.Host.Security;

namespace Pepa.Host.Tests;

/// <summary>Окно Pepa и мост к Windows — только для страниц самой Pepa.</summary>
public sealed class OwnPageTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5187/");

    [Theory]
    [InlineData("http://127.0.0.1:5187/", true)]
    [InlineData("http://127.0.0.1:5187/#/process/abc", true)]
    [InlineData("blob:http://127.0.0.1:5187/1f2e3d", true)]
    [InlineData("http://127.0.0.1:51870/", false)]
    [InlineData("http://127.0.0.1:5187@evil.example/", false)]
    [InlineData("https://127.0.0.1:5187/", false)]
    [InlineData("http://localhost:5187/", false)]
    [InlineData("blob:http://127.0.0.1:51870/1f2e", false)]
    [InlineData("about:blank", false)]
    [InlineData("", false)]
    public void Only_the_exact_origin_is_own(string uri, bool own) => Assert.Equal(own, OwnPage.Is(Base, uri));

    [Theory]
    [InlineData(@"\\host\share", true)]
    [InlineData("//host/share", true)]
    [InlineData(@"C:\Users\me\Documents", false)]
    [InlineData("", false)]
    public void Network_paths_are_recognized(string path, bool network) => Assert.Equal(network, OwnPage.IsNetworkPath(path));
}
