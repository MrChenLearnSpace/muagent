namespace MuAgents.Content;

/// <summary>内容读取的总体大小限制以及 PDF 外部程序配置。</summary>
public sealed class ContentOptions
{
    /// <summary>允许读取的单文件最大字节数。</summary>
    public long MaxFileBytes { get; set; } = 25 * 1024 * 1024;
    /// <summary>Poppler 文本提取程序名或绝对路径。</summary>
    public string PdfTextExecutable { get; set; } = "pdftotext";
    /// <summary>Poppler 页面渲染程序名或绝对路径。</summary>
    public string PdfRenderExecutable { get; set; } = "pdftoppm";
    /// <summary>内容处理外部进程的超时秒数。</summary>
    public int ProcessTimeoutSeconds { get; set; } = 120;
}
