namespace MuAgents.Ocr;

/// <summary>Tesseract 可执行文件及资源限制配置。</summary>
public sealed class TesseractOcrOptions
{
    /// <summary>Tesseract 命令名或绝对路径。</summary>
    public string Executable { get; set; } = "tesseract";
    /// <summary>单页识别超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>识别文本允许返回的最大字符数。</summary>
    public int MaxOutputCharacters { get; set; } = 200_000;
}
