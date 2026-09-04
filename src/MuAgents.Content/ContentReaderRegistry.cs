using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>按照注册顺序选择支持输入的读取器，并在无匹配格式时返回统一内容错误。</summary>
public sealed class ContentReaderRegistry(
    IEnumerable<IContentReader> readers,
    IOptions<ContentOptions> options) : IContentReaderRegistry
{
    private readonly IReadOnlyList<IContentReader> _readers = readers.ToArray();
    private readonly ContentOptions _options = options.Value;

    public async Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(content.Source, RuntimePaths.ProjectDirectory);
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("Content file was not found.", fullPath);
        if (file.Length > _options.MaxFileBytes)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"File size {file.Length} exceeds the {_options.MaxFileBytes} byte limit.");
        }

        var normalized = content with { Source = fullPath, Length = file.Length };
        var reader = _readers.FirstOrDefault(candidate => candidate.CanRead(normalized));
        if (reader is null)
        {
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "No content reader supports this file type.");
        }
        return await reader.ReadAsync(normalized, options, cancellationToken).ConfigureAwait(false);
    }
}
