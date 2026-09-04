using System.Text;

/// <summary>发送给 API 的单个文本文件引用。</summary>
public sealed record ReferencedFilePayload(string Path, string Content);

/// <summary>一次添加操作的统计和跳过原因。</summary>
public sealed record AddReferenceResult(int Added, int Updated, IReadOnlyList<string> Skipped);

/// <summary>
/// 管理 CLI 会话中持续生效的文件上下文。目录会递归展开，但生成目录、秘密文件、二进制和超限文件不会发送给模型。
/// </summary>
public sealed class FileReferenceSet
{
    public const int MaxFiles = 200;
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxTotalBytes = 2 * 1024 * 1024;
    private const int MaxScannedCandidates = 2_000;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".muagent", ".svn", ".hg", ".vs", ".idea", "bin", "obj", "node_modules", "data"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "muagents.settings.json", "muagents.settings.local.json"
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".key", ".pem", ".pfx", ".p12", ".snk", ".dll", ".exe", ".so", ".dylib",
        ".zip", ".7z", ".rar", ".gz", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf"
    };

    private readonly Dictionary<string, ReferencedFilePayload> _files;

    public FileReferenceSet(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _files = new Dictionary<string, ReferencedFilePayload>(PathComparer);
    }

    /// <summary>CLI 启动时的目录，也是相对引用路径的解析基准。</summary>
    public string RootDirectory { get; }

    /// <summary>当前会随每条消息发送的文件数。</summary>
    public int Count => _files.Count;

    /// <summary>当前引用内容的 UTF-8 总字节数。</summary>
    public int TotalBytes => _files.Values.Sum(file => Encoding.UTF8.GetByteCount(file.Content));

    /// <summary>返回按显示路径排序的不可变发送快照。</summary>
    public IReadOnlyList<ReferencedFilePayload> Snapshot() =>
        _files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>添加单文件或递归添加目录；省略路径时引用 CLI 当前目录。</summary>
    public async Task<AddReferenceResult> AddAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? RootDirectory : Unquote(path), RootDirectory);
        var candidates = new List<string>();
        var skipped = new List<string>();
        if (File.Exists(target))
        {
            candidates.Add(target);
        }
        else if (Directory.Exists(target))
        {
            EnumerateTextCandidates(target, candidates, skipped, cancellationToken);
        }
        else
        {
            throw new FileNotFoundException("指定的文件或目录不存在。", target);
        }

        var added = 0;
        var updated = 0;
        foreach (var file in candidates.OrderBy(value => value, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSensitiveOrBinaryByName(file))
            {
                AddSkipped(skipped, $"跳过敏感或二进制文件：{DisplayPath(file)}");
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                AddSkipped(skipped, $"文件超过 {MaxFileBytes} 字节：{DisplayPath(file)}");
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddSkipped(skipped, $"文件无法读取：{DisplayPath(file)}");
                continue;
            }

            if (!TryDecodeText(bytes, out var content))
            {
                AddSkipped(skipped, $"不是 UTF 文本：{DisplayPath(file)}");
                continue;
            }

            var previousBytes = _files.TryGetValue(file, out var previous)
                ? Encoding.UTF8.GetByteCount(previous.Content)
                : 0;
            var projectedBytes = TotalBytes - previousBytes + Encoding.UTF8.GetByteCount(content);
            if (!_files.ContainsKey(file) && _files.Count >= MaxFiles)
            {
                AddSkipped(skipped, $"已达到 {MaxFiles} 个文件上限。");
                break;
            }
            if (projectedBytes > MaxTotalBytes)
            {
                AddSkipped(skipped, $"已达到 {MaxTotalBytes} 字节总上限，后续文件未加入。");
                break;
            }

            _files[file] = new ReferencedFilePayload(DisplayPath(file), content);
            if (previous is null) added++;
            else updated++;
        }

        return new AddReferenceResult(added, updated, skipped);
    }

    /// <summary>移除一个文件或某个目录下的所有引用；传入 all 或 * 清空全部。</summary>
    public int Remove(string path)
    {
        if (path.Equals("all", StringComparison.OrdinalIgnoreCase) || path == "*")
        {
            var count = _files.Count;
            _files.Clear();
            return count;
        }

        var target = Path.GetFullPath(Unquote(path), RootDirectory);
        if (_files.Remove(target)) return 1;
        var removed = _files.Keys.Where(file => IsWithin(file, target)).ToArray();
        foreach (var file in removed) _files.Remove(file);
        return removed.Length;
    }

    private void EnumerateTextCandidates(
        string root,
        ICollection<string> files,
        ICollection<string> skipped,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddSkipped(skipped, $"目录无法读取：{DisplayPath(directory)}");
                continue;
            }

            foreach (var child in children)
            {
                if (Directory.Exists(child))
                {
                    var info = new DirectoryInfo(child);
                    // 不跟随目录链接，避免递归环和无意越出用户指定目录。
                    if (ExcludedDirectories.Contains(info.Name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    pending.Push(child);
                }
                else if (File.Exists(child))
                {
                    files.Add(Path.GetFullPath(child));
                    if (files.Count >= MaxScannedCandidates)
                    {
                        AddSkipped(skipped, $"目录候选文件超过 {MaxScannedCandidates} 个，已停止继续扫描。");
                        return;
                    }
                }
            }
        }
    }

    private string DisplayPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return IsWithin(fullPath, RootDirectory) ? Path.GetRelativePath(RootDirectory, fullPath) : fullPath;
    }

    private static bool IsSensitiveOrBinaryByName(string path)
    {
        var name = Path.GetFileName(path);
        return ExcludedFileNames.Contains(name) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               ExcludedExtensions.Contains(Path.GetExtension(path));
    }

    private static bool TryDecodeText(byte[] bytes, out string content)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
                content = Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length);
            else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
                content = Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length);
            else
                content = new UTF8Encoding(false, true).GetString(
                    bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) &&
               !Path.IsPathRooted(relative);
    }

    private static string Unquote(string value) => value.Trim().Trim('"');

    private static void AddSkipped(ICollection<string> skipped, string message)
    {
        // 控制终端输出量；最后一条说明还有多少细节未显示并无实际帮助，因此只保留前 20 条原因。
        if (skipped.Count < 20) skipped.Add(message);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
