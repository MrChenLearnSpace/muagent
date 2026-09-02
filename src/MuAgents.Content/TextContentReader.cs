using System.Text;
using MuAgents.Abstractions;

namespace MuAgents.Content;

public sealed class TextContentReader : IContentReader
{
    private static readonly string[] Extensions = [".txt", ".log", ".csv", ".json", ".xml", ".yaml", ".yml"];

    public bool CanRead(ContentDescriptor content) =>
        content.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
        Extensions.Contains(Path.GetExtension(content.Source), StringComparer.OrdinalIgnoreCase);

    public async Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(content.Source, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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
