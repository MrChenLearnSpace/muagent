using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

var options = CliOptions.Parse(args);
using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
Console.Write($"Password for {options.UserName}: ");
var password = ReadPassword();
if (options.Bootstrap)
{
    var bootstrap = await client.PostAsJsonAsync("api/v1/auth/bootstrap", new
    {
        userName = options.UserName,
        password,
        tenantName = options.TenantName
    });
    if (!bootstrap.IsSuccessStatusCode && bootstrap.StatusCode != System.Net.HttpStatusCode.Conflict)
        bootstrap.EnsureSuccessStatusCode();
}
var login = await client.PostAsJsonAsync("api/v1/auth/login", new
{
    userName = options.UserName,
    password,
    tenantId = options.TenantId,
    useCookie = false
});
login.EnsureSuccessStatusCode();
using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Bearer", loginDocument.RootElement.GetProperty("accessToken").GetString());

var createResponse = await client.PostAsJsonAsync("api/v1/conversations", new { title = "CLI conversation" });
createResponse.EnsureSuccessStatusCode();
using var conversationDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
var conversationId = conversationDocument.RootElement.GetProperty("id").GetString()!;
Console.WriteLine($"MuAgents conversation {conversationId}. Type /exit to quit.");

while (true)
{
    Console.Write("you> ");
    var input = Console.ReadLine();
    if (input is null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(input)) continue;

    using var response = await client.PostAsJsonAsync(
        $"api/v1/conversations/{conversationId}/messages", new { text = input });
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    Console.Write("agent> ");
    while (await reader.ReadLineAsync() is { } line)
    {
        using var item = JsonDocument.Parse(line);
        var root = item.RootElement;
        var type = root.GetProperty("type").GetString();
        var data = root.GetProperty("data");
        if (type == "text_delta") Console.Write(data.GetProperty("delta").GetString());
        if (type == "warning") Console.Error.WriteLine($"\nwarning: {data.GetProperty("message").GetString()}");
        if (type == "error") Console.Error.WriteLine($"\nerror: {data.GetProperty("message").GetString()}");
    }
    Console.WriteLine();
}

static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
    var password = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is { } key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
    return password.ToString();
}

internal sealed record CliOptions(
    string Url,
    string UserName,
    string? TenantId,
    bool Bootstrap,
    string TenantName)
{
    public static CliOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        return new CliOptions(
            Value("--url", "http://localhost:5000/"),
            Value("--user", Environment.UserName),
            Value("--tenant", string.Empty) is { Length: > 0 } tenant ? tenant : null,
            args.Contains("--bootstrap", StringComparer.OrdinalIgnoreCase),
            Value("--tenant-name", "Local"));
    }
}
