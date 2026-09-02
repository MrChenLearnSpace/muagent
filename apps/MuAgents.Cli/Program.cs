using System.Net.Http.Json;
using System.Text.Json;

var options = CliOptions.Parse(args);
using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
client.DefaultRequestHeaders.Add("X-Tenant-Id", options.TenantId);
client.DefaultRequestHeaders.Add("X-User-Id", options.UserId);

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

internal sealed record CliOptions(string Url, string TenantId, string UserId)
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
            Value("--tenant", "local"),
            Value("--user", Environment.UserName));
    }
}
