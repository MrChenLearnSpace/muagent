using MuAgents.Abstractions;
using MuAgents.Content;

namespace MuAgents.UnitTests;

public sealed class MarkdownContentReaderTests
{
    [Fact]
    public async Task ReadAsync_ExtractsFrontMatterAndHeadings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"muagents-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "---\ntitle: Sample\n---\n# First\nBody\n## Second\nMore");
            var reader = new MarkdownContentReader();

            var document = await reader.ReadAsync(new ContentDescriptor(path), new ReadOptions());

            Assert.Equal("Sample", document.Metadata!["title"]);
            Assert.Contains(document.Sections, section => section.Heading == "First");
            Assert.Contains(document.Sections, section => section.Heading == "Second");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
