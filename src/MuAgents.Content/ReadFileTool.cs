using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Content;

public sealed class FileToolOptions
{
    public List<string> WorkspaceRoots { get; set; } = [];
    public int MaxCharacters { get; set; } = 100_000;
    public int MaxPages { get; set; } = 100;
    public bool EnableOcr { get; set; } = true;
}

public sealed class ReadFileTool(
    IContentReaderRegistry readers,
    IOptions<FileToolOptions> options) : IAgentTool
{
    private readonly FileToolOptions _options = options.Value;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
        """).RootElement.Clone();

    public ToolDefinition Definition { get; } = new(
        "local.read_file", "Reads a Markdown, text, or PDF file inside an allowed workspace root.", Schema);

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("path", out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            return new ToolResult("path is required.", true);
        var fullPath = Path.GetFullPath(value.GetString()!);
        var roots = _options.WorkspaceRoots.Count == 0
            ? new[] { Path.GetFullPath(Directory.GetCurrentDirectory()) }
            : _options.WorkspaceRoots.Select(Path.GetFullPath);
        if (!roots.Any(root => IsWithin(fullPath, root)))
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "File path is outside configured workspace roots.");
        var document = await readers.ReadAsync(
            new ContentDescriptor(fullPath),
            new ReadOptions(MaxCharacters: _options.MaxCharacters, EnableOcr: _options.EnableOcr, MaxPages: _options.MaxPages),
            cancellationToken).ConfigureAwait(false);
        return new ToolResult(JsonSerializer.Serialize(new
        {
            document.Source,
            document.Title,
            document.MediaType,
            document.Metadata,
            sections = document.Sections.Select(section => new
            {
                section.Page, section.Heading, section.Confidence, section.Metadata, section.Text
            })
        }));
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
