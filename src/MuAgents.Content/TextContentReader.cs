using System.Text;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>读取常见纯文本格式并按配置字符数分块的内容读取器。</summary>
public sealed class TextContentReader : IContentReader
{
    // 编码代理需要读取实际源码，而不仅是文档；保持显式白名单可避免把程序集或图片误当文本。
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".md", ".html", ".htm", ".css", ".scss", ".less", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".vue", ".svelte", ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".sln", ".props", ".targets",
        ".razor", ".cshtml", ".py", ".java", ".kt", ".kts", ".go", ".rs", ".rb", ".php", ".swift",
        ".c", ".h", ".cc", ".cpp", ".hpp", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".sql",
        ".graphql", ".gql", ".gradle", ".properties", ".config"
    };
    private static readonly HashSet<string> ExtensionlessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "Procfile", ".editorconfig", ".gitignore", ".gitattributes"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public bool CanRead(ContentDescriptor content) =>
        content.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
        Extensions.Contains(Path.GetExtension(content.Source)) ||
        ExtensionlessNames.Contains(Path.GetFileName(content.Source));

    public async Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(content.Source, StrictUtf8, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                "Source file is not valid UTF-8 text.",
                exception);
        }
        if (text.Length > options.MaxCharacters) text = text[..options.MaxCharacters];
        var sections = Enumerable.Range(0, (text.Length + options.ChunkCharacters - 1) / options.ChunkCharacters)
            .Select(index => new ContentSection(text.Substring(
                index * options.ChunkCharacters,
                Math.Min(options.ChunkCharacters, text.Length - index * options.ChunkCharacters))))
            .ToArray();
        return new ContentDocument(content.Source, content.DisplayName ?? Path.GetFileName(content.Source),
            content.MediaType ?? "text/plain", sections);
    }
}
