using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Web;

public sealed partial class SafeWebContentFetcher(IOptions<WebOptions> options) : IWebContentFetcher
{
    private readonly WebOptions _options = options.Value;

    public async Task<WebContent> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        var current = uri;
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            var addresses = await ResolvePublicAddressesAsync(current, cancellationToken).ConfigureAwait(false);
            using var handler = CreatePinnedHandler(addresses[0]);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MuAgents/0.2");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == _options.MaxRedirects || response.Headers.Location is null)
                    throw Security("Web redirect limit exceeded or redirect target is missing.");
                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
            if (mediaType is not ("text/html" or "text/plain" or "application/json" or "application/xhtml+xml"))
                throw Security($"Web content type '{mediaType}' is not allowed.");
            var (raw, truncated) = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var text = mediaType.Contains("html", StringComparison.Ordinal)
                ? ExtractHtml(raw)
                : raw;
            if (text.Length > _options.MaxExtractedCharacters)
            {
                text = text[.._options.MaxExtractedCharacters];
                truncated = true;
            }
            var title = mediaType.Contains("html", StringComparison.Ordinal)
                ? WebUtility.HtmlDecode(TitleRegex().Match(raw).Groups[1].Value).Trim()
                : null;
            return new WebContent(current, string.IsNullOrWhiteSpace(title) ? null : title,
                text, mediaType, DateTimeOffset.UtcNow, truncated);
        }
        throw Security("Web redirect limit exceeded.");
    }

    private async Task<IPAddress[]> ResolvePublicAddressesAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw Security("Only absolute HTTP/HTTPS URLs without embedded credentials are allowed.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false); }
        catch (SocketException exception)
        {
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Web host could not be resolved.", exception);
        }
        if (addresses.Length == 0 || addresses.Any(IsBlocked))
            throw Security("URL resolves to a loopback, private, link-local, or reserved address.");
        return addresses;
    }

    private static SocketsHttpHandler CreatePinnedHandler(IPAddress address)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private async Task<(string Text, bool Truncated)> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var accepted = Math.Min(read, _options.MaxResponseBytes - total);
            if (accepted > 0) memory.Write(buffer, 0, accepted);
            total += accepted;
            if (accepted < read || total == _options.MaxResponseBytes)
                return (Encoding.UTF8.GetString(memory.ToArray()), true);
        }
        return (Encoding.UTF8.GetString(memory.ToArray()), false);
    }

    private static string ExtractHtml(string html)
    {
        var cleaned = ScriptRegex().Replace(html, " ");
        cleaned = StyleRegex().Replace(cleaned, " ");
        cleaned = TagRegex().Replace(cleaned, " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        return WhitespaceRegex().Replace(cleaned, " ").Trim();
    }

    public static bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] >= 224 ||
               (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
               (bytes[0] == 169 && bytes[1] == 254) ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 198 && bytes[1] is 18 or 19);
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;
    private static MuAgentException Security(string message) => new(MuAgentErrorCategory.SecurityDenied, message);

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1_000)]
    private static partial Regex TitleRegex();
    [GeneratedRegex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1_000)]
    private static partial Regex ScriptRegex();
    [GeneratedRegex("<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1_000)]
    private static partial Regex StyleRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline, 1_000)]
    private static partial Regex TagRegex();
    [GeneratedRegex("\\s+", RegexOptions.None, 1_000)]
    private static partial Regex WhitespaceRegex();
}
