namespace MuAgents.Abstractions;

public sealed record ContentDescriptor(
    string Source,
    string? MediaType = null,
    string? DisplayName = null,
    long? Length = null);

public sealed record ReadOptions(
    int MaxCharacters = 200_000,
    int ChunkCharacters = 8_000,
    bool EnableOcr = true,
    IReadOnlyList<string>? OcrLanguages = null,
    int OcrDpi = 300,
    int MaxPages = 100);

public sealed record ContentSection(
    string Text,
    int? Page = null,
    string? Heading = null,
    double? Confidence = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ContentDocument(
    string Source,
    string? Title,
    string MediaType,
    IReadOnlyList<ContentSection> Sections,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public int CharacterCount => Sections.Sum(section => section.Text.Length);
}

public interface IContentReader
{
    bool CanRead(ContentDescriptor content);

    Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default);
}

public interface IContentReaderRegistry
{
    Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default);
}

public interface IImageInputProcessor
{
    Task<ImagePart> ProcessAsync(
        ImageSource source,
        string? declaredMediaType,
        CancellationToken cancellationToken = default);
}

public sealed record OcrTextRegion(
    string Text,
    double Confidence,
    int Left,
    int Top,
    int Width,
    int Height);

public sealed record OcrPageResult(
    int Page,
    string Text,
    double Confidence,
    IReadOnlyList<OcrTextRegion> Regions);

public interface IOcrEngine
{
    Task<OcrPageResult> RecognizeAsync(
        string imagePath,
        int page,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken = default);
}
