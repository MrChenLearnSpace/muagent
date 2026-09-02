namespace MuAgents.Ocr;

public sealed class TesseractOcrOptions
{
    public string Executable { get; set; } = "tesseract";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxOutputCharacters { get; set; } = 200_000;
}
