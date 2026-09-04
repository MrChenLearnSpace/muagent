using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>组合 Poppler 文本提取、页面渲染和可选 OCR 的 PDF 内容读取器。</summary>
public sealed class PdfContentReader(
    IOcrEngine ocr,
    IOptions<ContentOptions> options) : IContentReader
{
    private readonly ContentOptions _options = options.Value;

    public bool CanRead(ContentDescriptor content) =>
        content.MediaType == "application/pdf" || Path.GetExtension(content.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ContentDocument> ReadAsync(
        ContentDescriptor content,
        ReadOptions options,
        CancellationToken cancellationToken = default)
    {
        var extraction = await ExternalProcess.RunAsync(
            _options.PdfTextExecutable,
            ["-f", "1", "-l", options.MaxPages.ToString(), "-layout", content.Source, "-"],
            TimeSpan.FromSeconds(_options.ProcessTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        if (extraction.ExitCode != 0)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"PDF text extraction failed: {Limit(extraction.Error, 1_000)}");
        }

        var pages = extraction.Output.Split('\f');
        if (pages.Length > 0 && string.IsNullOrWhiteSpace(pages[^1])) pages = pages[..^1];
        var sections = new List<ContentSection>();
        var totalCharacters = 0;
        for (var index = 0; index < pages.Length && index < options.MaxPages; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = index + 1;
            var pageText = pages[index].Trim();
            // 文本层内容过少通常表示扫描页；只在这种情况下做昂贵的渲染和 OCR。
            if (NonWhitespaceCount(pageText) < 40 && options.EnableOcr)
            {
                var ocrResult = await RenderAndRecognizeAsync(content.Source, page, options, cancellationToken)
                    .ConfigureAwait(false);
                pageText = ocrResult.Text;
                sections.Add(new ContentSection(
                    LimitToRemaining(pageText), page, Confidence: ocrResult.Confidence,
                    Metadata: new Dictionary<string, string>
                    {
                        ["source"] = "ocr",
                        ["quality"] = ocrResult.Confidence < 0.7 ? "low-confidence" : "normal"
                    }));
            }
            else
            {
                sections.Add(new ContentSection(
                    LimitToRemaining(pageText), page,
                    Metadata: new Dictionary<string, string>
                    {
                        ["source"] = "text-layer",
                        ["quality"] = "layout-order-not-guaranteed"
                    }));
            }
            if (totalCharacters >= options.MaxCharacters) break;
        }

        return new ContentDocument(
            content.Source,
            content.DisplayName ?? Path.GetFileNameWithoutExtension(content.Source),
            "application/pdf",
            sections,
            new Dictionary<string, string>
            {
                ["pageCountRead"] = sections.Count.ToString(),
                ["ocrEnabled"] = options.EnableOcr.ToString()
            });

        string LimitToRemaining(string value)
        {
            var remaining = Math.Max(0, options.MaxCharacters - totalCharacters);
            if (value.Length > remaining) value = value[..remaining];
            totalCharacters += value.Length;
            return value;
        }
    }

    private async Task<OcrPageResult> RenderAndRecognizeAsync(
        string pdfPath,
        int page,
        ReadOptions options,
        CancellationToken cancellationToken)
    {
        // 每页使用独立目录，既避免并发文件名冲突，也保证异常时能整体清理中间图片。
        var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("pdf");
        var outputPrefix = Path.Combine(temporaryDirectory.DirectoryPath, $"page-{page}");
        try
        {
            var render = await ExternalProcess.RunAsync(
                _options.PdfRenderExecutable,
                ["-png", "-r", options.OcrDpi.ToString(), "-f", page.ToString(), "-l", page.ToString(), "-singlefile", pdfPath, outputPrefix],
                TimeSpan.FromSeconds(_options.ProcessTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (render.ExitCode != 0)
            {
                throw new MuAgentException(MuAgentErrorCategory.ContentFailure,
                    $"PDF page {page} could not be rendered for OCR: {Limit(render.Error, 1_000)}");
            }
            return await ocr.RecognizeAsync(
                outputPrefix + ".png", page, options.OcrLanguages ?? ["chi_sim", "eng"], cancellationToken)
                .ConfigureAwait(false);
        }
        finally { temporaryDirectory.Dispose(); }
    }

    private static int NonWhitespaceCount(string value) => value.Count(character => !char.IsWhiteSpace(character));
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
