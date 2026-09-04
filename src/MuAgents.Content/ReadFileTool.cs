using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>read_file 工具的工作区白名单、字符、页数和 OCR 限制。</summary>
public sealed class FileToolOptions
{
    public List<string> WorkspaceRoots { get; set; } = [];
    public int MaxCharacters { get; set; } = 100_000;
    public int MaxPages { get; set; } = 100;
    public bool EnableOcr { get; set; } = true;
}

/// <summary>在允许工作区内读取文本、Markdown 或 PDF，并返回统一结构化 JSON 的工具。</summary>
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
        var fullPath = Path.GetFullPath(value.GetString()!, RuntimePaths.ProjectDirectory);
        // 即使模型构造了 .. 路径，也必须先规范化，再用相对路径判断真实目录边界。
        var roots = _options.WorkspaceRoots.Count == 0
            ? new[] { RuntimePaths.ProjectDirectory }
            : _options.WorkspaceRoots.Select(root => Path.GetFullPath(root, RuntimePaths.ProjectDirectory));
        if (!roots.Any(root => IsWithin(fullPath, root)))
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "File path is outside configured workspace roots.");
        // 模型无需读取运行时数据库、密钥或项目秘密；这些内容也不能因项目根是默认白名单而泄露。
        if (IsWithin(fullPath, RuntimePaths.RootDirectory) || IsSensitiveFile(fullPath))
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "Runtime state and sensitive files cannot be read by the model.");
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
                section.Page,
                section.Heading,
                section.Confidence,
                section.Metadata,
                section.Text
            })
        }));
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsSensitiveFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("muagents.settings.json", StringComparison.OrdinalIgnoreCase) ||
               new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}
