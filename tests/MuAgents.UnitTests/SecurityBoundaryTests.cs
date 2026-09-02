using System.Net;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Content;
using MuAgents.Skills;
using MuAgents.Web;

namespace MuAgents.UnitTests;

public sealed class SecurityBoundaryTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    public void WebFetcher_BlocksNonPublicAddresses(string value)
    {
        Assert.True(SafeWebContentFetcher.IsBlocked(IPAddress.Parse(value)));
    }

    [Fact]
    public void WebFetcher_AllowsPublicAddress()
    {
        Assert.False(SafeWebContentFetcher.IsBlocked(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void SkillCatalog_DeniesPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-root");
        var exception = Assert.Throws<MuAgentException>(() =>
            FileSystemSkillCatalog.ResolveWithin(root, Path.Combine("..", "secret.txt")));
        Assert.Equal(MuAgentErrorCategory.SecurityDenied, exception.Category);
    }

    [Fact]
    public async Task ScriptRunner_DeniedPolicyStopsBeforeExecution()
    {
        var runner = new ProcessScriptRunner(Options.Create(new SkillOptions
        {
            ScriptPolicy = ScriptExecutionPolicy.Denied
        }));
        var skill = new SkillManifest("test", "test", "1", "missing", "", [], []);

        var exception = await Assert.ThrowsAsync<MuAgentException>(() => runner.RunAsync(
            new ScriptRunRequest(skill, "run.py", [])));

        Assert.Equal(MuAgentErrorCategory.SecurityDenied, exception.Category);
    }

    [Fact]
    public async Task ImageProcessor_ValidatesAndNormalizesDataUrl()
    {
        const string onePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var processor = new ImageInputProcessor(Options.Create(new ImageOptions()));

        var result = await processor.ProcessAsync(
            new ImageSource(ImageSourceKind.DataUrl, $"data:image/png;base64,{onePixelPng}"), "image/png");

        Assert.Equal("image/png", result.MediaType);
        Assert.StartsWith("data:image/png;base64,", result.Source.Value);
    }
}
