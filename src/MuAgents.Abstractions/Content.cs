namespace MuAgents.Abstractions;

/// <summary>描述一个待读取的内容源。Source 可以是文件路径或由具体读取器理解的地址。</summary>
public sealed record ContentDescriptor(
    string Source,
    string? MediaType = null,
    string? DisplayName = null,
    long? Length = null);

/// <summary>限制单次内容读取的字符数、分块大小、OCR 行为和最大页数。</summary>
public sealed record ReadOptions(
    int MaxCharacters = 200_000,
    int ChunkCharacters = 8_000,
    bool EnableOcr = true,
    IReadOnlyList<string>? OcrLanguages = null,
    int OcrDpi = 300,
    int MaxPages = 100);

/// <summary>内容文档中的一个逻辑片段，可附带页码、标题、置信度和读取器元数据。</summary>
public sealed record ContentSection(
    string Text,
    int? Page = null,
    string? Heading = null,
    double? Confidence = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>读取器返回的统一文档结构，使文本、Markdown、PDF 和 OCR 可以被上层一致处理。</summary>
public sealed record ContentDocument(
    string Source,
    string? Title,
    string MediaType,
    IReadOnlyList<ContentSection> Sections,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>所有片段的文本字符总数，不包含元数据。</summary>
    public int CharacterCount => Sections.Sum(section => section.Text.Length);
}

/// <summary>单一内容格式的读取器；实现应先通过 CanRead 声明是否支持输入。</summary>
public interface IContentReader
{
    bool CanRead(ContentDescriptor content);

    Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>内容读取器注册表，负责从全部读取器中选择能够处理输入的实现。</summary>
public interface IContentReaderRegistry
{
    Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>验证并规范化模型图片输入，禁止未经校验的文件或网络内容直接进入模型请求。</summary>
public interface IImageInputProcessor
{
    Task<ImagePart> ProcessAsync(
        ImageSource source,
        string? declaredMediaType,
        CancellationToken cancellationToken = default);
}

/// <summary>OCR 识别出的文本区域及其像素坐标和置信度。</summary>
public sealed record OcrTextRegion(
    string Text,
    double Confidence,
    int Left,
    int Top,
    int Width,
    int Height);

/// <summary>一页图片的 OCR 文本、总体置信度和细粒度文本区域。</summary>
public sealed record OcrPageResult(
    int Page,
    string Text,
    double Confidence,
    IReadOnlyList<OcrTextRegion> Regions);

/// <summary>OCR 引擎抽象；imagePath 必须指向调用方已经完成安全校验的本地图片。</summary>
public interface IOcrEngine
{
    Task<OcrPageResult> RecognizeAsync(
        string imagePath,
        int page,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken = default);
}
