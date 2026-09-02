using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Ocr;

public sealed class TesseractOcrEngine(IOptions<TesseractOcrOptions> options) : IOcrEngine
{
    private readonly TesseractOcrOptions _options = options.Value;

    public async Task<OcrPageResult> RecognizeAsync(
        string imagePath,
        int page,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("OCR image was not found.", fullPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fullPath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(string.Join('+', languages.Count == 0 ? ["eng"] : languages));
        startInfo.ArgumentList.Add("tsv");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Tesseract could not be started.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"OCR executable '{_options.Executable}' is unavailable. Install Tesseract or update MuAgents.Content.OcrExecutable.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new MuAgentException(MuAgentErrorCategory.Timeout, "OCR execution timed out.");
        }

        var tsv = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"OCR failed with exit code {process.ExitCode}: {Limit(error, 1_000)}");
        }

        return ParseTsv(tsv, page);
    }

    private OcrPageResult ParseTsv(string tsv, int page)
    {
        var regions = new List<OcrTextRegion>();
        var text = new StringBuilder();
        var confidenceTotal = 0d;
        var previousLine = string.Empty;
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!double.TryParse(fields[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)) continue;
            _ = int.TryParse(fields[6], out var left);
            _ = int.TryParse(fields[7], out var top);
            _ = int.TryParse(fields[8], out var width);
            _ = int.TryParse(fields[9], out var height);
            var lineKey = $"{fields[2]}:{fields[3]}:{fields[4]}";
            if (text.Length > 0) text.Append(lineKey == previousLine ? ' ' : '\n');
            text.Append(fields[11]);
            previousLine = lineKey;
            regions.Add(new OcrTextRegion(fields[11], Math.Max(0, confidence) / 100d, left, top, width, height));
            confidenceTotal += Math.Max(0, confidence) / 100d;
            if (text.Length >= _options.MaxOutputCharacters) break;
        }

        return new OcrPageResult(page, text.ToString(),
            regions.Count == 0 ? 0 : confidenceTotal / regions.Count, regions);
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
