namespace MuAgents.OpenAI;

public enum ModelProtocol
{
    ChatCompletions,
    Responses,
    Messages
}

public sealed class OpenAiCompatibleOptions
{
    public ModelProtocol Protocol { get; set; } = ModelProtocol.Responses;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string? Endpoint { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-5-mini";
    public int MaxContextTokens { get; set; } = 128_000;
    public int MaxOutputTokens { get; set; } = 4_096;
    public bool SupportsVision { get; set; } = true;
    public bool SupportsTools { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public string ResolveEndpoint() => Endpoint ?? Protocol switch
    {
        ModelProtocol.ChatCompletions => "chat/completions",
        ModelProtocol.Responses => "responses",
        ModelProtocol.Messages => "messages",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol))
    };
}
