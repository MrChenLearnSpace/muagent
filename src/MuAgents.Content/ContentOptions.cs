namespace MuAgents.Content;

public sealed class ContentOptions
{
    public long MaxFileBytes { get; set; } = 25 * 1024 * 1024;
    public string PdfTextExecutable { get; set; } = "pdftotext";
    public string PdfRenderExecutable { get; set; } = "pdftoppm";
    public int ProcessTimeoutSeconds { get; set; } = 120;
}
