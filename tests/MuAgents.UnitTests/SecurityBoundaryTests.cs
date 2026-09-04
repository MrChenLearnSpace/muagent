using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Content;
using MuAgents.Mcp;
using MuAgents.Persistence;
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
        var root = TestPaths.NewDirectoryPath("skill-root");
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

    [Fact]
    public void RuntimeWritePathsCannotEscapeApplicationRoot()
    {
        var outside = Path.GetFullPath(Path.Combine(RuntimePaths.RootDirectory, "..", "outside.db"));

        var exception = Assert.Throws<MuAgentException>(() =>
            RuntimePaths.ResolveWritePath(outside, "test path"));

        Assert.Equal(MuAgentErrorCategory.Configuration, exception.Category);
    }

    [Fact]
    public void RelativeDatabasePathIsRootedAtApplicationDirectory()
    {
        var connectionString = new PersistenceOptions
        {
            ConnectionString = "Data Source=data/test.db"
        }.ResolveConnectionString();

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        Assert.StartsWith(RuntimePaths.RootDirectory, dataSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeTemporaryDirectoryIsCreatedAndRemovedUnderApplicationRoot()
    {
        string path;
        using (var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("path-test"))
        {
            path = temporaryDirectory.DirectoryPath;
            Assert.StartsWith(RuntimePaths.RootDirectory, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void ChildProcessEnvironmentOverridesEveryWritableRuntimeCache()
    {
        using var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("child-environment-test");
        var startInfo = new ProcessStartInfo();
        var outside = Path.GetFullPath(Path.Combine(RuntimePaths.RootDirectory, "..", "outside"));
        foreach (var name in new[]
                 {
                     "TEMP", "TMP", "TMPDIR", "DOTNET_CLI_HOME", "NUGET_PACKAGES",
                     "NUGET_HTTP_CACHE_PATH", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
                 })
        {
            startInfo.Environment[name] = outside;
        }

        RuntimePaths.ConfigureChildProcess(startInfo, temporaryDirectory.DirectoryPath);

        foreach (var name in new[]
                 {
                     "TEMP", "TMP", "TMPDIR", "DOTNET_CLI_HOME", "NUGET_PACKAGES",
                     "NUGET_HTTP_CACHE_PATH", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"
                 })
        {
            Assert.StartsWith(RuntimePaths.RootDirectory, startInfo.Environment[name], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task McpStdioProcess_UsesOnlyApplicationRootForWorkingAndTemporaryDirectories()
    {
        // 此集成测试用系统自带 Windows PowerShell 模拟一个最小 MCP Stdio 服务。
        if (!OperatingSystem.IsWindows()) return;
        const string server = """
            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $request = $line | ConvertFrom-Json
                if ($request.method -eq 'initialize') {
                    $result = @{ protocolVersion = '2025-06-18'; capabilities = @{}; serverInfo = @{ name = 'test'; version = '1' } }
                } elseif ($request.method -eq 'tools/list') {
                    $result = @{ tools = @(@{ name = 'environment'; description = 'test'; inputSchema = @{ type = 'object' } }) }
                } elseif ($request.method -eq 'tools/call') {
                    $paths = "$(Get-Location)`t$env:TEMP`t$env:TMP`t$env:TMPDIR"
                    $result = @{ content = @(@{ type = 'text'; text = $paths }); isError = $false }
                } else {
                    continue
                }
                if ($null -ne $request.id) {
                    [Console]::Out.WriteLine((@{ jsonrpc = '2.0'; id = $request.id; result = $result } | ConvertTo-Json -Compress -Depth 10))
                    [Console]::Out.Flush()
                }
            }
            """;
        var settings = Options.Create(new McpOptions
        {
            Servers =
            [
                new McpServerProfile
                {
                    Name = "local-test",
                    Transport = McpTransport.Stdio,
                    Command = "powershell.exe",
                    Arguments = ["-NoProfile", "-NonInteractive", "-Command", server]
                }
            ]
        });
        var configuration = new McpConfigurationStore(
            settings,
            Path.Combine(TestPaths.NewDirectoryPath("mcp-configuration"), "mcp.json"));

        string temporaryPath;
        await using (var manager = new McpClientManager(
                         configuration,
                         new UnexpectedHttpClientFactory(),
                         NullLogger<McpClientManager>.Instance))
        {
            using var arguments = JsonDocument.Parse("{}");
            var result = await manager.InvokeAsync("local-test", "environment", arguments.RootElement);
            Assert.False(result.IsError);
            var paths = result.Content.Split('\t');
            Assert.Equal(4, paths.Length);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(RuntimePaths.RootDirectory),
                Path.TrimEndingDirectorySeparator(paths[0]),
                ignoreCase: true);
            temporaryPath = paths[1];
            var expectedParent = Path.Combine(RuntimePaths.RootDirectory, "data", "temp", "mcp");
            Assert.StartsWith(expectedParent, temporaryPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(temporaryPath, paths[2], ignoreCase: true);
            Assert.Equal(temporaryPath, paths[3], ignoreCase: true);
            Assert.True(Directory.Exists(temporaryPath));
        }

        Assert.False(Directory.Exists(temporaryPath));
    }

    private sealed class UnexpectedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Stdio MCP test must not create an HTTP client.");
    }
}
