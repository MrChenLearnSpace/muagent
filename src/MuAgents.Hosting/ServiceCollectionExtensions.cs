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

/// <summary>把 MuAgents 全部配置、基础设施和扩展能力注册到宿主依赖注入容器。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>绑定并验证 MuAgents 配置，同时注册运行时所需的所有默认实现。</summary>
    public static IServiceCollection AddMuAgents(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("MuAgents");
        // 核心上限在宿主启动时校验，避免错误配置等到长任务执行中途才暴露。
        services.AddOptions<AgentOptions>()
            .Bind(section.GetSection("Agent"))
            .Validate(options => options.MaxToolIterations is >= 1 and <= 100,
                "MaxToolIterations must be between 1 and 100.")
            .Validate(options => options.MaxEmptyResponseRetries is >= 0 and <= 10,
                "MaxEmptyResponseRetries must be between 0 and 10.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.DefaultSystemInstruction) &&
                                 options.DefaultSystemInstruction.Length <= 20_000,
                "DefaultSystemInstruction must contain 1-20000 characters.")
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
        services.AddOptions<CommandExecutionOptions>()
            .Bind(section.GetSection("CommandExecution"))
            .Validate(options => options.MaxExecutionSeconds is >= 1 and <= 3600,
                "Command execution timeout must be between 1 and 3600 seconds.")
            .Validate(options => options.ApprovalTimeoutSeconds is >= 1 and <= 3600,
                "Command approval timeout must be between 1 and 3600 seconds.")
            .Validate(options => options.MaxOutputCharacters > 0,
                "Command execution output limit must be positive.")
            .Validate(options => options.AllowedCommands.All(command =>
                    !string.IsNullOrWhiteSpace(command) && command.Length <= 1_024),
                "Allowed command entries must contain 1-1024 characters.")
            .ValidateOnStart();
        services.AddOptions<WorkspaceFileOptions>()
            .Bind(section.GetSection("WorkspaceFiles"))
            .Validate(options => options.MaxWriteCharacters is >= 1 and <= 10_000_000,
                "Workspace file write limit must be between 1 and 10000000 characters.")
            .Validate(options => options.MaxListEntries is >= 1 and <= 20_000,
                "Workspace file list limit must be between 1 and 20000 entries.")
            .ValidateOnStart();
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

        // 模型适配器自行使用取消令牌控制超时，禁用 HttpClient 的第二套超时可保留准确错误分类。
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
        services.AddSingleton<CommandApprovalCoordinator>();
        services.AddSingleton<IAgentTool, CommandExecutionTool>();
        services.AddSingleton<IAgentTool, ListWorkspaceFilesTool>();
        services.AddSingleton<IAgentTool, WriteWorkspaceFileTool>();
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
        services.AddSingleton<SkillConfigurationStore>();
        services.AddSingleton<FileSystemSkillCatalog>();
        services.AddSingleton<ISkillCatalog>(provider => provider.GetRequiredService<FileSystemSkillCatalog>());
        services.AddSingleton<IScriptRunner, ProcessScriptRunner>();
        services.AddHttpClient("MuAgents.Mcp", client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<McpConfigurationStore>();
        services.AddSingleton<IMcpClientManager, McpClientManager>();
        services.AddSingleton<IAgentTool, McpInvokeTool>();
        services.AddSingleton<AgentRuntime>();
        return services;
    }
}
