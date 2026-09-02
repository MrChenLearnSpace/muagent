using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Core;
using MuAgents.OpenAI;
using MuAgents.Persistence;
using MuAgents.Tools;
using MuAgents.Content;
using MuAgents.Mcp;
using MuAgents.Ocr;
using MuAgents.Skills;
using MuAgents.Web;

namespace MuAgents.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMuAgents(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("MuAgents");
        services.AddOptions<AgentOptions>()
            .Bind(section.GetSection("Agent"))
            .Validate(options => options.MaxToolIterations is >= 0 and <= 100,
                "MaxToolIterations must be between 0 and 100.")
            .ValidateOnStart();
        services.AddOptions<ContextOptions>()
            .Bind(section.GetSection("Context"))
            .Validate(options => options.MaxContextTokens > options.ReservedOutputTokens + options.SafetyMarginTokens,
                "Context budget must leave room for input.")
            .Validate(options => options.CompactionRatio is > 0 and <= 1,
                "CompactionRatio must be in (0, 1].")
            .ValidateOnStart();
        services.AddOptions<ToolGatewayOptions>()
            .Bind(section.GetSection("Agent"))
            .Validate(options => options.Timeout > TimeSpan.Zero && options.MaxConcurrency > 0 && options.MaxResultCharacters > 0,
                "Tool gateway options must be positive.")
            .ValidateOnStart();
        services.PostConfigure<ToolGatewayOptions>(options =>
        {
            var seconds = section.GetSection("Agent").GetValue<int?>("ToolTimeoutSeconds");
            if (seconds is > 0) options.Timeout = TimeSpan.FromSeconds(seconds.Value);
        });
        services.AddOptions<OpenAiCompatibleOptions>()
            .Bind(section.GetSection("Model"))
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "Model BaseUrl must be absolute.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Model), "Model is required.")
            .ValidateOnStart();
        services.AddOptions<PersistenceOptions>()
            .Bind(section.GetSection("Persistence"))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "Persistence connection string is required.")
            .ValidateOnStart();
        services.AddOptions<ContentOptions>().Bind(section.GetSection("Content"));
        services.AddOptions<ImageOptions>().Bind(section.GetSection("Content").GetSection("Images"));
        services.AddOptions<FileToolOptions>().Bind(section.GetSection("Content").GetSection("FileTool"));
        services.AddOptions<TesseractOcrOptions>().Bind(section.GetSection("Content").GetSection("Ocr"));
        services.AddOptions<WebOptions>().Bind(section.GetSection("Web"));
        services.AddOptions<SkillOptions>().Bind(section.GetSection("Skills"));
        services.AddOptions<McpOptions>().Bind(section.GetSection("Mcp"));

        services.AddHttpClient<IChatModel, OpenAiCompatibleChatModel>((provider, client) =>
        {
            var model = provider.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MuAgents/0.1");
            client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
        });
        services.AddSingleton<ITokenEstimator, ApproximateTokenEstimator>();
        services.AddSingleton<IContextManager, ContextManager>();
        services.AddSingleton<IAgentTool, CurrentTimeTool>();
        services.AddSingleton<IToolGateway, ToolGateway>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<IIdentityStore, SqliteIdentityStore>();
        services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
        services.AddSingleton<IContentReader, MarkdownContentReader>();
        services.AddSingleton<IContentReader, PdfContentReader>();
        services.AddSingleton<IContentReader, TextContentReader>();
        services.AddSingleton<IContentReaderRegistry, ContentReaderRegistry>();
        services.AddSingleton<IImageInputProcessor, ImageInputProcessor>();
        services.AddSingleton<IAgentTool, ReadFileTool>();
        services.AddSingleton<IWebContentFetcher, SafeWebContentFetcher>();
        services.AddHttpClient<IWebSearchProvider, JsonWebSearchProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IAgentTool, WebFetchTool>();
        services.AddSingleton<IAgentTool, WebSearchTool>();
        services.AddSingleton<ISkillCatalog, FileSystemSkillCatalog>();
        services.AddSingleton<IScriptRunner, ProcessScriptRunner>();
        services.AddHttpClient("MuAgents.Mcp", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IMcpClientManager, McpClientManager>();
        services.AddSingleton<IAgentTool, McpInvokeTool>();
        services.AddSingleton<AgentRuntime>();
        return services;
    }
}
