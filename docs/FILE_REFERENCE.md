# MuAgents 项目文件详细说明

本文按目录解释源码和配置文件的职责。`bin/`、`obj/` 和项目级 `.muagent/` 属于构建或运行产物，不是项目源码。

## 1. 总体依赖关系

```text
MuAgents.Cli ──HTTP/NDJSON──> MuAgents.App ──> MuAgents.Hosting
                                      ├─ Core / OpenAI / Persistence
                                      ├─ Tools / Content / OCR / Web
                                      ├─ Skills / Mcp
                                      └─ MuAgents.Abstractions
```

CLI 是独立的展示客户端，没有对运行时类库的项目引用。`MuAgents.Abstractions` 位于 APP 模块依赖底层；具体实现依赖抽象，`MuAgents.Hosting` 负责注册实现，`MuAgents.App` 组合配置、中间件和 HTTP API。完整组件图和工具审批时序见根目录 `DESIGN.md`。

## 2. 根目录

| 文件 | 说明 |
| --- | --- |
| `MuAgents.sln` | Visual Studio/.NET 解决方案，汇总应用、类库和测试项目。 |
| `Directory.Build.props` | 全解决方案共享编译设置，例如目标框架、可空引用和隐式 using。 |
| `README.md` | 项目首页，提供能力概览、快速启动、目录约束和文档导航。 |
| `DESIGN.md` | 架构设计、模块边界、安全模型和核心运行流程。 |
| `todo.md` | 项目后续任务记录文件，由维护者记录未完成事项。 |
| `.gitignore` | 排除构建产物、运行数据、本地密钥配置和编辑器文件。 |

## 3. 应用入口 `apps/`

### 3.1 `MuAgents.App`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.App.csproj` | ASP.NET Core API 项目定义及对各功能模块的引用。 |
| `Program.cs` | API 主入口：解析 `-d` 和本机 `--set-password` 管理参数，创建并加载 `.muagent/config` 项目配置，打印配置清单，注册认证/授权/限流、初始化数据库并映射全部 `/api/v1` 路由；提供运行目录、命令审批、模型状态、上下文压缩、MCP/Skill 动态管理及 NDJSON 流。 |
| `Authentication.cs` | 本地认证服务：无密码首次管理员、密码哈希校验与本机重设、JWT 签发、用户/租户/成员管理；也定义认证配置和登录会话模型。 |
| `appsettings.json` | 非敏感运行默认值，包括智能体限制、控制台三档审批、上下文预算、SQLite、内容处理、OCR、Skill、Web、MCP 和日志。 |
| `muagents.settings.json` | 随程序发布的模型、Web 搜索和认证模板；每个项目首次启动时复制到 `.muagent/config/muagents.settings.json`。 |

### 3.2 `MuAgents.Cli`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Cli.csproj` | 交互式命令行客户端项目定义。 |
| `Program.cs` | 默认以无密码 `admin` 自动初始化/登录，`--setup-password` 时才首次设置密码，已有密码时按需提示；自动续期访问令牌并恢复当前用户最近会话，此外负责 HTTP/NDJSON 展示、本地操作审批、文件上下文、MCP/Skill 管理和 `/compact` 等命令。不接受 `-d`，不初始化 APP 路径或创建 `.muagent`。 |
| `FileReferenceSet.cs` | 管理 CLI 文件上下文：相对路径基于 CLI 当前终端目录，递归遍历时排除 `.muagent`、生成目录、敏感/二进制文件，执行文件数与字节上限并生成发送快照。 |
| `SlashCommandLine.cs` | 斜杠命令目录和交互式多行编辑器：统一生成帮助与 Tab 候选，支持唯一命令补全、共同前缀、候选列表、光标移动、`Enter` 换行、`Shift+Enter` 提交和上下方向键输入历史；重定向输入时兼容普通 `ReadLine`。 |
| `TerminalPresentation.cs` | CLI ANSI 主题和轻量 Markdown 渲染器：区分用户、Agent 与工具颜色，渲染常用块级/行内语法，支持 `NO_COLOR` 和重定向纯文本，并过滤不可信控制字符。 |

## 4. 公共抽象 `src/MuAgents.Abstractions`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Abstractions.csproj` | 零业务实现的基础契约项目，其他模块通过它解耦。 |
| `Messages.cs` | 统一消息模型：角色、文本、图片、工具调用和工具结果消息片段，以及消息元数据。 |
| `Models.cs` | 模型调用请求、参数、能力描述、流式模型事件、模型接口及便于测试的委托实现。 |
| `Tools.cs` | 工具定义、调用、结果、带精确调用 ID 的执行上下文、单工具接口、独立超时标记和工具网关接口。 |
| `Persistence.cs` | 会话实体、会话存储接口、原子替换压缩历史的契约、统一错误类别和领域异常。 |
| `Identity.cs` | 用户、租户、成员关系实体及身份存储接口。 |
| `Content.cs` | 内容描述、读取选项、结构化文档、内容读取器、图片处理器和 OCR 接口。 |
| `Skills.cs` | Skill 清单、脚本执行策略、目录接口、脚本请求和执行结果。 |
| `Web.cs` | Web 搜索结果、搜索接口、网页内容和安全抓取接口。 |
| `RuntimePaths.cs` | APP 项目隔离路径边界：记录程序安装目录和 APP 项目目录，把状态根固定为 `<项目>/.muagent`，规范化并验证写路径，并为 APP 和子进程重定向 .NET/NuGet/临时缓存。 |
| `RuntimeLaunchArguments.cs` | APP 启动参数解析：处理 `-d`、`--directory`、本机 `--set-password` 及等号形式，相对路径基于启动终端解析，并从 ASP.NET Core 剩余参数中移除这些 APP 专用选项。 |
| `Telemetry.cs` | 全局 `ActivitySource` 与 `Meter` 名称和实例，供运行时与宿主统一采集。 |

## 5. 核心运行时 `src/MuAgents.Core`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Core.csproj` | 核心智能体运行时项目定义。 |
| `AgentRuntime.cs` | 智能体主循环：注入可信编码与连续对话规则、加载并修复旧工具历史、构造上下文、调用模型、重试空响应、转发工具事件、执行并落库工具结果、限制迭代且保证调用/结果成对；也查询会话 Token 状态，并在独占锁内持久化自动/手动压缩结果。 |
| `ContextManagement.cs` | Token 近似估算、上下文预算规划和历史压缩；自动压缩保留系统消息及近期轮次，手动压缩把全部历史折叠并收紧到指定硬目标。 |

## 6. 模型适配 `src/MuAgents.OpenAI`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.OpenAI.csproj` | 模型协议适配项目及 HTTP/JSON 依赖。 |
| `OpenAiCompatibleOptions.cs` | 协议枚举与模型连接选项，包括地址、端点、密钥、模型上限和能力开关。 |
| `OpenAiCompatibleChatModel.cs` | 把统一消息转换为 Responses、Chat Completions 或 Messages 请求，并把 SSE/JSON 响应还原为统一模型事件；聚合工具调用参数碎片，在落库前把截断或无效 JSON 转为安全恢复错误，同时处理用量信息。 |

## 7. 持久化 `src/MuAgents.Persistence`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Persistence.csproj` | SQLite 持久化项目定义。 |
| `PersistenceOptions.cs` | 数据库连接字符串配置。 |
| `SqliteConversationStore.cs` | 创建数据库表、保存和查询租户隔离的会话与消息；序列化多态消息片段，并通过事务保持追加或整批替换压缩历史的原子性。 |
| `SqliteIdentityStore.cs` | 创建身份相关表，读写用户、租户和成员关系；使用唯一约束和事务保证首次初始化与成员操作一致。 |

## 8. 工具系统 `src/MuAgents.Tools`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Tools.csproj` | 工具网关与内置工具项目定义。 |
| `ToolGatewayOptions.cs` | 工具调用超时、并发数和最大结果字符数配置。 |
| `ToolGateway.cs` | 按命名空间注册工具，校验调用参数，并以信号量限制并发；统一处理超时、异常、截断和遥测。 |
| `BuiltInTools.cs` | 内置基础工具，例如 UTC 时间和受控文本处理，作为工具协议的最小可用实现。 |
| `CommandExecutionTool.cs` | `local.execute_command` 控制台工具、`Denied`/`RequireApproval`/`Allowed` 三档策略及审批协调器；使用参数数组、项目内工作目录、命令白名单、独立审批/执行超时、输出限制和项目内可写环境，并拒绝在 Shell 参数中覆盖受保护的用户/缓存/临时目录变量。 |
| `WorkspaceFileTools.cs` | `local.list_files` 与 `local.write_file`：列举项目结构、创建/覆盖 UTF-8 文件、自动创建父目录；复用本地操作三档审批，阻止 `.muagent`、项目外路径和符号链接逃逸。 |

## 9. 内容与 OCR

### 9.1 `src/MuAgents.Content`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Content.csproj` | 内容读取和图片处理项目定义。 |
| `ContentOptions.cs` | 文件大小、外部进程、PDF、图片和文件工具的限制配置。 |
| `ContentReaderRegistry.cs` | 根据扩展名和媒体类型选择合适的内容读取器。 |
| `TextContentReader.cs` | 分段读取纯文本，在字符预算内返回结构化内容。 |
| `MarkdownContentReader.cs` | 根据 Markdown 标题切分章节并保持标题层级信息。 |
| `PdfContentReader.cs` | 调用 Poppler 提取指定页文本；无文本时可渲染页面并调用 OCR。 |
| `ImageInputProcessor.cs` | 处理 HTTPS URL、本地文件和 Data URL 图片，校验类型、字节数、像素数和允许目录，输出模型统一图片片段。 |
| `ReadFileTool.cs` | 将内容读取能力暴露为 `read_file` 工具，并执行工作区根目录边界检查。 |
| `ExternalProcess.cs` | 在 `.muagent/data/temp` 下运行受控外部内容进程，处理参数、超时、标准输出/错误及进程终止。 |

### 9.2 `src/MuAgents.Ocr`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Ocr.csproj` | Tesseract OCR 适配项目定义。 |
| `TesseractOcrOptions.cs` | 可执行文件、语言、超时和最大输出长度配置。 |
| `TesseractOcrEngine.cs` | 调用 Tesseract 识别图片，解析 TSV 区域和纯文本，并清理 `.muagent` 内的临时文件。 |

## 10. Web `src/MuAgents.Web`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Web.csproj` | Web 搜索和抓取项目定义。 |
| `WebOptions.cs` | 搜索端点、密钥头、抓取超时、重定向和响应大小限制。 |
| `JsonWebSearchProvider.cs` | 调用可配置 JSON 搜索 API，并把常见结果结构映射为统一搜索结果。 |
| `SafeWebContentFetcher.cs` | 安全抓取 HTTP/HTTPS 内容；逐次验证 DNS 和重定向目标，阻止环回、链路本地及私有地址并限制下载大小。 |
| `WebTools.cs` | 将搜索和网页读取能力包装为模型可调用工具。 |

## 11. Skill `src/MuAgents.Skills`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Skills.csproj` | Skill 发现和脚本执行项目定义。 |
| `SkillOptions.cs` | Skill 根目录、脚本策略、允许运行时和超时配置。 |
| `SkillConfigurationStore.cs` | 初始化并原子保存 `<项目>/.muagent/config/skills.json`，维护基于项目根解析的 Skill 扫描目录与禁用清单。 |
| `FileSystemSkillCatalog.cs` | 根据动态目录扫描单个 Skill 或根目录中的 `SKILL.md`，解析前置元数据和指令，校验路径，并按启用状态返回清单。 |
| `ProcessScriptRunner.cs` | 根据安全策略批准脚本，在允许的运行时中启动进程，限制路径、超时和输出，并使用根目录内临时工作区。 |

## 12. MCP `src/MuAgents.Mcp`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Mcp.csproj` | Model Context Protocol 客户端项目定义。 |
| `McpOptions.cs` | MCP 服务列表、传输方式、命令/地址、环境变量、工具允许列表和缓存时间。 |
| `McpConfigurationStore.cs` | 初始化并原子保存 `<项目>/.muagent/config/mcp.json`，校验和维护 MCP 服务的添加、更新、启用、禁用与删除。 |
| `McpClientManager.cs` | 动态读取配置，创建 Stdio 或 Streamable HTTP 客户端并管理连接；Stdio 工作目录是项目根，临时目录位于项目 `.muagent`。 |
| `McpInvokeTool.cs` | 把 MCP 工具统一映射到 MuAgents 工具网关，并转发参数、结果和错误。 |

## 13. 依赖注入装配 `src/MuAgents.Hosting`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.Hosting.csproj` | 宿主装配项目，引用所有运行模块。 |
| `ServiceCollectionExtensions.cs` | `AddMuAgents` 总注册入口：绑定并验证配置，注册 HTTP 客户端、存储、模型、运行时、内容读取器、工具、Web、Skill 和 MCP 服务。 |

## 14. 测试 `tests/MuAgents.UnitTests`

| 文件 | 说明 |
| --- | --- |
| `MuAgents.UnitTests.csproj` | xUnit 测试项目及被测模块引用。 |
| `GlobalUsings.cs` | 测试项目共享命名空间。 |
| `AgentRuntimeTests.cs` | 验证文本流、工具循环、并发、迭代限制、异常、落库，以及手动压缩到三分之一后的检查点持久化。 |
| `AuthenticationTests.cs` | 验证初始化、密码认证、JWT 身份和租户选择。 |
| `ContextManagerTests.cs` | 验证 Token 预算、近期轮次保留和上下文压缩。 |
| `MarkdownContentReaderTests.cs` | 验证 Markdown 标题分节和字符限制。 |
| `ProtocolAdapterTests.cs` | 验证三类模型协议的请求映射与流式响应解析。 |
| `SecurityBoundaryTests.cs` | 验证文件根目录、图片路径、URL/SSRF 和脚本安全边界。 |
| `SqliteConversationStoreTests.cs` | 验证 SQLite 初始化、会话、消息持久化和租户隔离。 |
| `TelemetryTests.cs` | 验证 Activity 与 Meter 的关键遥测信号。 |
| `ToolGatewayTests.cs` | 验证工具参数、调用结果、精确调用 ID 传递、超时、并发和截断。 |
| `EventEnvelopeTests.cs` | 验证 NDJSON 外壳和事件数据统一输出 camelCase，并验证审批客户端能收到工具参数。 |
| `CommandExecutionToolTests.cs` | 验证控制台禁止、逐次批准/拒绝、自动允许和工作目录越界拒绝。 |
| `WorkspaceFileToolTests.cs` | 验证项目文件真实写入与列举、禁止模式、项目越界及 `.muagent` 状态目录拒绝。 |
| `FileReferenceSetTests.cs` | 验证目录递归引用、生成/敏感文件排除和按目录移除。 |
| `DynamicConfigurationTests.cs` | 验证 MCP 与 Skill 添加、删除、启停、持久化、重新加载和默认目录去重。 |
| `RuntimeLaunchArgumentsTests.cs` | 验证默认项目目录、相对 `-d`、密码修改参数、长参数形式、参数透传和不存在目录拒绝。 |
| `CliOptionsTests.cs` | 验证 CLI 默认无密码 `admin` 配置、`--setup-password` 和旧 `--bootstrap` 兼容别名。 |
| `CliTerminalPresentationTests.cs` | 验证只有 `Shift+Enter` 提交，以及 Markdown 的纯文本/ANSI 标题、列表、引用、代码块和标记清理。 |
| `SlashCommandCompletionTests.cs` | 验证斜杠命令的唯一前缀补全、多候选返回，以及带参数或普通文本不被误改。 |
| `TestPaths.cs` | 在测试项目的 `.muagent/data/tests` 下创建独立临时目录，确保测试状态遵守项目隔离。 |

## 15. 运行时生成内容

| 路径 | 是否提交 | 说明 |
| --- | --- | --- |
| `**/bin/`、`**/obj/` | 否 | 编译和 NuGet 中间产物。 |
| `.muagent/config/muagents.settings.json` | 否 | 当前项目的模型、认证、Web 和秘密配置。 |
| `.muagent/config/appsettings.json` | 否 | 可选的项目运行参数覆盖。 |
| `.muagent/config/mcp.json` | 否 | 当前项目的 MCP 服务、过滤规则和启停状态。 |
| `.muagent/config/skills.json` | 否 | 当前项目的 Skill 扫描目录和禁用清单。 |
| `.muagent/data/muagents.db` | 否 | 当前项目独立的 SQLite 业务数据。 |
| `.muagent/data/keys/` | 否 | Cookie/Data Protection 密钥，应和数据库一起备份。 |
| `.muagent/data/temp/` | 否 | PDF、OCR、脚本和受控进程临时数据。 |
| `.muagent/data/dotnet/` | 否 | 子进程使用的 .NET CLI 主目录。 |
| `.muagent/data/nuget/` | 否 | 子进程使用的 NuGet 包与 HTTP 缓存。 |
| `skills/` | 视部署而定 | 业务 Skill 和脚本；若属于项目能力可纳入版本控制，若含私密数据则单独部署。 |
