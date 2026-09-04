using MuAgents.Abstractions;
using MuAgents.Content;

namespace MuAgents.UnitTests;

/// <summary>验证编码代理常见源码格式会进入严格 UTF-8 文本读取器。</summary>
public sealed class TextContentReaderTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("app.tsx")]
    [InlineData("Program.cs")]
    [InlineData("project.csproj")]
    [InlineData("main.py")]
    [InlineData("Dockerfile")]
    public void CanRead_CommonSourceFiles(string fileName)
    {
        Assert.True(new TextContentReader().CanRead(new ContentDescriptor(fileName)));
    }

    [Fact]
    public void CanRead_DoesNotTreatBinaryAsText()
    {
        Assert.False(new TextContentReader().CanRead(new ContentDescriptor("application.dll")));
    }
}
