namespace MuAgents.UnitTests;

/// <summary>验证 CLI 的无密码默认模式和显式密码初始化参数。</summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_UsesPasswordlessAdminDefaults()
    {
        var options = CliOptions.Parse([]);

        Assert.Equal("http://localhost:5000/", options.Url);
        Assert.Equal("admin", options.UserName);
        Assert.Equal("Local", options.TenantName);
        Assert.Null(options.TenantId);
        Assert.False(options.SetupPassword);
    }

    [Theory]
    [InlineData("--setup-password")]
    [InlineData("--bootstrap")]
    public void Parse_RecognizesPasswordSetupAndCompatibilityAlias(string argument)
    {
        var options = CliOptions.Parse([argument]);

        Assert.True(options.SetupPassword);
    }
}
