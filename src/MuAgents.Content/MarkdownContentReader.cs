using System.Text;
using MuAgents.Abstractions;

namespace MuAgents.Content;

public sealed class MarkdownContentReader : IContentReader
{
    public bool CanRead(ContentDescriptor content) =>
        content.MediaType is "text/markdown" || Path.GetExtension(content.Source).Equals(".md", StringComparison.OrdinalIgnoreCase);

    public async Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = await File.ReadAllTextAsync(content.Source, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var metadata = ParseFrontMatter(ref source);
        var sections = SplitSections(source, options).ToArray();
        return new ContentDocument(content.Source, content.DisplayName ?? Path.GetFileName(content.Source),
            "text/markdown", sections, metadata);
    }

    private static Dictionary<string, string> ParseFrontMatter(ref string source)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!source.StartsWith("---\n", StringComparison.Ordinal) && !source.StartsWith("---\r\n", StringComparison.Ordinal))
            return metadata;
        var normalized = source.ReplaceLineEndings("\n");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return metadata;
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) metadata[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }
        source = normalized[(end + 5)..];
        return metadata;
    }

    private static IEnumerable<ContentSection> SplitSections(string source, ReadOptions options)
    {
        var remaining = Math.Max(0, options.MaxCharacters);
        string? heading = null;
        var buffer = new StringBuilder();
        foreach (var line in source.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith('#') && line.TrimStart('#').StartsWith(' '))
            {
                foreach (var section in Flush()) yield return section;
                heading = line.TrimStart('#', ' ');
            }
            buffer.AppendLine(line);
            if (buffer.Length >= options.ChunkCharacters)
            {
                foreach (var section in Flush()) yield return section;
            }
        }
        foreach (var section in Flush()) yield return section;

        IEnumerable<ContentSection> Flush()
        {
            if (buffer.Length == 0 || remaining == 0) yield break;
            var value = buffer.ToString();
            buffer.Clear();
            if (value.Length > remaining) value = value[..remaining];
            remaining -= value.Length;
            yield return new ContentSection(value, Heading: heading);
        }
    }
}
