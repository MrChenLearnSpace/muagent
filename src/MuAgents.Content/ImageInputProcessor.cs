using System.Buffers.Binary;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>图片输入的字节、像素和本地读取目录限制。</summary>
public sealed class ImageOptions
{
    /// <summary>单张图片允许的最大编码前字节数。</summary>
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>宽乘高允许的最大像素数，用于限制解码资源消耗。</summary>
    public long MaxPixels { get; set; } = 40_000_000;
    /// <summary>允许引用图片文件的根目录；为空时仅允许当前项目目录。</summary>
    public List<string> AllowedRoots { get; set; } = [];
}

/// <summary>校验图片来源和真实文件头，并把本地/Data URL 输入规范化为 Data URL。</summary>
public sealed class ImageInputProcessor(IOptions<ImageOptions> options) : IImageInputProcessor
{
    private readonly ImageOptions _options = options.Value;

    public async Task<ImagePart> ProcessAsync(
        ImageSource source,
        string? declaredMediaType,
        CancellationToken cancellationToken = default)
    {
        // 远程图片由模型服务获取，因此这里只接受加密 HTTPS，绝不自动降级到 HTTP。
        if (source.Kind == ImageSourceKind.HttpsUrl)
        {
            if (!Uri.TryCreate(source.Value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw Security("Image URL must use HTTPS.");
            return new ImagePart(source, declaredMediaType);
        }

        byte[] bytes;
        if (source.Kind == ImageSourceKind.DataUrl)
        {
            var comma = source.Value.IndexOf(',');
            if (comma < 0 || !source.Value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Image data URL must contain base64 data.");
            try { bytes = Convert.FromBase64String(source.Value[(comma + 1)..]); }
            catch (FormatException exception)
            {
                throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Image base64 data is invalid.", exception);
            }
        }
        else
        {
            var path = ResolveAllowedPath(source.Value);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Image file was not found.", path);
            if (info.Length > _options.MaxImageBytes) throw TooLarge(info.Length);
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (bytes.LongLength > _options.MaxImageBytes) throw TooLarge(bytes.LongLength);
        // 不信任调用方声明的 MIME，必须用魔数识别并与声明交叉验证。
        var (mediaType, width, height) = Inspect(bytes);
        if (declaredMediaType is not null && !declaredMediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Declared image type does not match its bytes.");
        if (width > 0 && height > 0 && (long)width * height > _options.MaxPixels)
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Image pixel count exceeds the configured limit.");
        return new ImagePart(
            new ImageSource(ImageSourceKind.DataUrl, $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"),
            mediaType);
    }

    private string ResolveAllowedPath(string value)
    {
        var path = Path.GetFullPath(value, RuntimePaths.ProjectDirectory);
        // 空白名单不是“允许全部”；默认安全边界是启动 MuAgents 的项目目录。
        var roots = _options.AllowedRoots.Count == 0
            ? new[] { RuntimePaths.ProjectDirectory }
            : _options.AllowedRoots.Select(root => Path.GetFullPath(root, RuntimePaths.ProjectDirectory));
        if (!roots.Any(root => IsWithin(path, root))) throw Security("Image path is outside configured roots.");
        return path;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static (string MediaType, int Width, int Height) Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ("image/png", BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return ("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]));
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ("image/webp", 0, 0);
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 9 < bytes.Length)
            {
                if (bytes[offset] != 0xff) { offset++; continue; }
                var marker = bytes[offset + 1];
                if (marker is >= 0xc0 and <= 0xc3)
                    return ("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]));
                if (offset + 4 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
                if (length < 2) break;
                offset += 2 + length;
            }
            return ("image/jpeg", 0, 0);
        }
        throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Unsupported or invalid image format.");
    }

    private MuAgentException TooLarge(long length) => new(
        MuAgentErrorCategory.ContentFailure,
        $"Image size {length} exceeds the {_options.MaxImageBytes} byte limit.");

    private static MuAgentException Security(string message) => new(MuAgentErrorCategory.SecurityDenied, message);
}
