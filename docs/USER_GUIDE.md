# MuAgents 使用手册

本文面向部署人员、管理员和 API 调用方，说明 MuAgents 的配置、启动、身份初始化、会话调用以及扩展能力。项目文件职责请参阅[项目文件详细说明](FILE_REFERENCE.md)。

## 1. 运行环境

### 1.1 必需组件

- Windows、Linux 或 macOS；
- .NET 8 SDK（源码运行）或 .NET 8 Runtime（框架依赖发布）；
- 一个受支持协议的模型服务；
- 模型名称和 API Key。

### 1.2 可选组件

| 功能 | 外部程序 | 默认命令 |
| --- | --- | --- |
| PDF 文本提取 | Poppler | `pdftotext` |
| PDF 页面渲染 | Poppler | `pdftoppm` |
| 扫描件 OCR | Tesseract | `tesseract` |
| Skill 脚本 | 对应语言运行时 | `dotnet`、`python`、`node`、`pwsh`、`bash` |

外部程序可以位于系统 `PATH` 中，也可以在配置中填写其绝对路径。它们产生的 MuAgents 工作文件会放在当前项目的 `.muagent/data/temp/` 下。

## 2. 配置文件与加载顺序

API 应用按以下顺序加载 JSON 配置：

1. `<程序安装目录>/appsettings.json`：随程序发布的非敏感默认值；
2. `<程序安装目录>/muagents.settings.json`：随程序发布的模型和认证模板；
3. `<项目根目录>/.muagent/config/appsettings.json`：可选的项目运行参数覆盖；
4. `<项目根目录>/.muagent/config/muagents.settings.json`：项目的模型、认证、Web 和秘密配置，优先级最高。

“项目根目录”默认是启动 API 时终端所在目录，也可由 `-d <项目路径>` 或 `--directory <项目路径>` 指定。项目配置不存在时，API 会从安装目录复制模板到 `.muagent/config/muagents.settings.json`；模板中的密钥为空，因此首次启动可能在创建文件后提示配置错误，填写后重新启动即可。整个 `.muagent/` 已被 Git 忽略。API 每次启动都会明确打印项目根、状态目录以及本次实际加载的配置文件绝对路径；CLI 的 `/status` 也可查询同一清单。

### 2.1 模型配置

```json
{
  "MuAgents": {
    "Model": {
      "Protocol": "Responses",
      "BaseUrl": "http://10.1.1.226:10505/v1/",
      "Endpoint": "responses",
      "ApiKey": "请填写实际密钥",
      "Model": "请填写服务端存在的模型名",
      "MaxContextTokens": 128000,
      "MaxOutputTokens": 4096,
      "SupportsVision": true,
      "SupportsTools": true
    }
  }
}
```

支持的 `Protocol`：

| 值 | 典型路由 | 说明 |
| --- | --- | --- |
| `Responses` | `/v1/responses` | OpenAI Responses 风格，默认选择 |
| `ChatCompletions` | `/v1/chat/completions` | OpenAI Chat Completions 风格 |
| `Messages` | `/v1/messages` | Anthropic Messages 风格 |

`BaseUrl` 必须是带协议头的绝对 URL，并建议以 `/` 结尾。最终地址由 `BaseUrl` 和 `Endpoint` 组合，务必与模型服务实际路由一致。真实密钥只写入项目的 `.muagent/config/muagents.settings.json`，不要写入源码模板或文档。

### 2.2 认证配置

```json
{
  "MuAgents": {
    "Authentication": {
      "Issuer": "MuAgents",
      "Audience": "MuAgents.Api",
      "JwtSigningKey": "至少32个字符且不可公开的随机字符串",
      "AccessTokenMinutes": 60,
      "CookieDays": 7,
      "MinimumPasswordLength": 12,
      "DataProtectionKeysPath": "data/keys"
    }
  }
}
```

- `JwtSigningKey` 少于 32 个字符时应用会拒绝启动。
- 密码使用 ASP.NET Core PasswordHasher，迭代次数为 210,000。
- `DataProtectionKeysPath` 必须解析到项目的 `.muagent/` 内。
- 登录和首次初始化接口按来源 IP 限制为每分钟 10 次。

### 2.3 数据与上下文配置

常用配置位于 `appsettings.json`：

- `MuAgents:Persistence:ConnectionString`：默认 `Data Source=data/muagents.db`；实际保存为 `.muagent/data/muagents.db`。
- `MuAgents:Agent:MaxToolIterations`：单轮最多工具迭代次数，默认 12。
- `MuAgents:Agent:ToolTimeoutSeconds`：一次工具调用超时，默认 60 秒。
- `MuAgents:Agent:MaxConcurrency`：同一批工具调用并发数，默认 4。
- `MuAgents:CommandExecution:ApprovalMode`：控制台执行审批模式，默认 `RequireApproval`。
- `MuAgents:CommandExecution:AllowedCommands`：可执行程序白名单；空数组表示不额外限制。
- `MuAgents:CommandExecution:ApprovalTimeoutSeconds`：等待用户审批的最长时间，默认 120 秒。
- `MuAgents:CommandExecution:MaxExecutionSeconds`：单次控制台进程最长时间，默认 120 秒。
- `MuAgents:Context:MaxContextTokens`：上下文窗口上限。
- `MuAgents:Context:ReservedOutputTokens`：为模型输出预留的 Token 数。
- `MuAgents:Context:RecentTurnsToKeep`：压缩时强制保留的最近轮次数。

模型配置的 `MaxContextTokens` 描述模型能力；上下文配置决定运行时实际预算。配置时应让运行时预算不超过模型限制。

## 3. 启动、发布与目录约束

### 3.1 从源码启动

```powershell
Set-Location F:\project\Web\codex\muagents
dotnet restore MuAgents.sln
dotnet run --project apps/MuAgents.App
dotnet run --project apps/MuAgents.App -- -d D:\work\my-project --urls http://127.0.0.1:5000
```

APP 不传 `-d` 时，执行启动命令时的当前目录会成为项目根目录。传入相对路径时，相对于启动终端当前目录解析。CLI 不接受 `-d`，只通过 `--url` 连接 APP；其当前目录只影响 `/add` 读取哪些客户端本地文件。

默认监听地址以 ASP.NET Core 启动输出为准。另开终端检查：

```powershell
Invoke-RestMethod http://localhost:5000/api/v1/health
```

成功响应为 `{ "status": "ok" }`。

### 3.2 发布运行

框架依赖的 Windows x64 发布命令如下，目标机器需安装 .NET 8 Runtime：

```powershell
dotnet publish apps/MuAgents.App -c Release -r win-x64 --self-contained false -o publish/MuAgents.App
dotnet publish apps/MuAgents.Cli -c Release -r win-x64 --self-contained false -o publish/MuAgents.Cli
```

若目标机器没有 .NET Runtime，把 `--self-contained false` 改为 `--self-contained true`。Linux x64 使用 `-r linux-x64`，macOS 按处理器使用 `osx-x64` 或 `osx-arm64`。

程序可以集中安装，项目状态不需要放在安装目录。例如：

```powershell
Set-Location D:\work\my-project
D:\tools\MuAgents\MuAgents.App.exe
D:\tools\MuAgents\MuAgents.Cli.exe --url http://localhost:5000/ --user admin
```

也可保持终端在任意目录，显式指定同一个项目根：

```powershell
D:\tools\MuAgents\MuAgents.App.exe -d D:\work\my-project --urls http://127.0.0.1:5000
D:\tools\MuAgents\MuAgents.Cli.exe --url http://127.0.0.1:5000/ --user admin
```

此时 APP 项目根目录为 `D:\work\my-project`，全部状态位于 `D:\work\my-project\.muagent`。CLI 的 `/add .` 始终引用 CLI 启动终端的当前目录；如需引用 APP 项目，可先切换到该目录启动 CLI，或使用 `/add D:\work\my-project`。用另一个 `-d` 启动 APP，会得到另一套完全独立的配置、身份库和会话。

APP 启动入口会在创建宿主之前重定向以下环境变量；CLI 不修改进程环境，也不创建本地 `.muagent`：

| 环境变量 | 项目内位置 |
| --- | --- |
| `TEMP`、`TMP`、`TMPDIR` | `.muagent/data/temp/process/`；具体外部任务使用独立子目录。 |
| `DOTNET_CLI_HOME` | `.muagent/data/dotnet/home/` |
| `NUGET_PACKAGES` | `.muagent/data/nuget/packages/` |
| `NUGET_HTTP_CACHE_PATH` | `.muagent/data/nuget/http-cache/` |
| `DOTNET_BUNDLE_EXTRACT_BASE_DIR` | `.muagent/data/temp/dotnet-bundle/` |

MCP、OCR、PDF/内容处理及 Skill 脚本子进程会再次覆盖这些变量，配置中的同名环境变量不能把写入位置改到 `.muagent/` 之外。

任何可写路径如果逃逸 `.muagent/`，程序会抛出异常并拒绝使用。文件、图片、Skill 和 MCP Stdio 的相对读取路径以项目根目录解析，并受到各自允许目录的额外限制。

## 4. 首次初始化和登录

CLI 默认用户为 `admin`。身份库为空时，普通 CLI 启动会自动创建无密码的 `admin` 用户、`Local` 租户和 `Owner` 成员关系，然后直接登录：

```powershell
dotnet run --project apps/MuAgents.Cli -- --url http://localhost:5000/
```

无密码模式面向仅监听 `127.0.0.1` 的本地开发环境。首次启动就要设置密码时，使用 `--setup-password`；CLI 会要求输入两遍密码，成功初始化后，以后启动不再需要该参数，但会在无密码登录失败后自动提示密码：

```powershell
dotnet run --project apps/MuAgents.Cli -- `
  --url http://localhost:5000/ --setup-password
```

`--bootstrap` 是 `--setup-password` 的兼容别名。自定义用户名和租户仍可增加 `--user <用户名>` 与 `--tenant-name <租户名>`。

直接调用 HTTP API 时，身份库为空只能成功初始化一次；`password` 为空串会建立无密码账户：

```powershell
$body = @{
  userName = "admin"
  password = ""
  tenantName = "Local"
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/api/v1/auth/bootstrap `
  -ContentType application/json -Body $body
```

需要从一开始启用密码时，把空串替换为满足 `MinimumPasswordLength` 的密码。初始化会原子创建系统管理员、首个租户和 `Owner` 成员关系，重复调用返回 HTTP 409。

登录并保存 JWT：

```powershell
$loginBody = @{
  userName = "admin"
  password = "" # 设置过密码时填写对应密码
  useCookie = $false
} | ConvertTo-Json

$login = Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/api/v1/auth/login `
  -ContentType application/json -Body $loginBody

$headers = @{ Authorization = "Bearer $($login.accessToken)" }
```

用户属于多个租户时，登录请求要传 `tenantId`。签发的 JWT 只代表本次选择的租户，客户端不能用请求头任意切换租户。

## 5. CLI 对话

无密码默认启动：

```powershell
dotnet run --project apps/MuAgents.Cli -- --url http://localhost:5000/
```

首次运行即设置密码：

```powershell
dotnet run --project apps/MuAgents.Cli -- `
  --url http://localhost:5000/ --setup-password
```

设置密码后，常规启动命令不变，CLI 会先尝试无密码登录，收到拒绝后才提示输入密码。多租户用户额外传 `--tenant <tenant-id>`。CLI 每次启动会新建会话；普通文本直接发送给模型，以 `/` 开头的内容由 CLI 解释为本地命令。

交互终端支持与 Codex/Claude Code 类似的斜杠命令补全：输入 `/` 后按 `Tab` 显示所有候选；输入命令前缀后按 `Tab`，唯一候选会自动补齐，多个候选会先扩展共同前缀、再次按 `Tab` 显示候选清单。上下方向键可浏览本次运行的输入历史。输入被管道重定向时自动退回普通逐行读取。

### 5.1 斜杠命令

| 命令 | 说明 |
| --- | --- |
| `/help` | 显示命令帮助。 |
| `/model` | 从 API 查询当前模型名、协议、完整端点、上下文/输出上限、图片/工具能力和密钥是否已配置；不会显示密钥值。 |
| `/status` | 显示 API、用户、租户、会话、CLI 文件工作目录、APP 项目根、APP `.muagent` 状态目录、控制台审批模式、服务端已加载配置文件、引用统计，以及当前/最大上下文 Token。 |
| `/compact` | 手动持久化压缩当前会话；压缩后不超过最大上下文的 1/3。若原本已低于目标则不改写。 |
| `/new [标题]` | 新建会话，当前文件引用继续保留。 |
| `/add [文件或目录]` | 添加单文件或递归添加目录；省略路径等同 `/add .`。包含空格的路径可用双引号包围。 |
| `/context`、`/files` | 列出当前引用文件和 UTF-8 字节数。 |
| `/remove <文件或目录>` | 移除一个文件，或移除目录下的全部引用。 |
| `/remove all` | 清空全部文件引用。 |
| `/mcp`、`/mcp_list` | 查看 MCP 服务、启停状态和配置文件绝对路径。 |
| `/mcp_add <URL>` | 添加或更新 HTTP MCP；名称默认从 URL 主机名生成。 |
| `/mcp_add <名称> <URL>` | 使用指定名称添加或更新 HTTP MCP。 |
| `/mcp_remove <名称>` | 删除 MCP 配置并关闭已缓存的连接。 |
| `/mcp_enable <名称>`、`/mcp_disable <名称>` | 启用或禁用 MCP。 |
| `/mcp_tools <名称>` | 连接已启用的 MCP 并列出其可用工具。 |
| `/skills`、`/skills_list` | 查看 Skill、启停状态、扫描目录和配置文件绝对路径。 |
| `/skills_add <目录>` | 添加一个含 `SKILL.md` 的 Skill 目录，或含多个 Skill 子目录的根目录。 |
| `/skills_remove <目录>` | 删除扫描目录配置，不删除磁盘上的文件。 |
| `/skills_enable <名称>`、`/skills_disable <名称>` | 启用或禁用已发现的 Skill。 |
| `/exit`、`/quit` | 退出 CLI。 |

MCP 与 Skill 配置修改命令要求当前登录用户是系统管理员。普通用户仍可查看配置、列出 MCP 工具和在对话请求中使用已启用的扩展。每次模型回答结束后，CLI 自动输出：

```text
[上下文: 12,345 / 128,000 tokens，9.6%]
```

这里的当前值按模型请求相同的估算器计算，包含已持久化会话消息和工具定义；它是预算估算值，不是模型供应商最终账单值。

### 5.2 控制台执行和审批

APP 向模型注册 `local.execute_command` 工具。模型必须分别提交可执行文件、参数数组、可选项目内工作目录和超时，不会由 APP 隐式拼接 Shell 字符串。例如模型可请求：

```json
{
  "command": "dotnet",
  "arguments": [ "test", "MuAgents.sln", "-c", "Release" ],
  "workingDirectory": ".",
  "timeoutSeconds": 120
}
```

在项目级 `.muagent/config/appsettings.json` 中配置审批：

```json
{
  "MuAgents": {
    "CommandExecution": {
      "ApprovalMode": "RequireApproval",
      "AllowedCommands": [ "dotnet", "git", "pwsh.exe" ],
      "ApprovalTimeoutSeconds": 120,
      "MaxExecutionSeconds": 120,
      "MaxOutputCharacters": 48000
    }
  }
}
```

三种模式的精确行为：

| 模式 | 行为 | 建议场景 |
| --- | --- | --- |
| `Denied` | 工具保留可解释的拒绝结果，但绝不启动进程。 | 生产、只读问答或不允许本地执行的服务。 |
| `RequireApproval` | 每次工具调用通过 NDJSON 返回调用 ID、命令和参数；CLI 显示详情并要求当前用户输入 `y`，其他输入均拒绝。 | 默认开发模式。 |
| `Allowed` | 通过参数、目录和白名单校验后自动启动，不等待客户端。 | 可信、单用户且已有外部沙箱的环境。 |

审批决定绑定到“租户 + 用户 + 会话 + 工具调用 ID”，不能用一个批准释放其他用户或其他调用。等待审批超过 `ApprovalTimeoutSeconds` 会自动失败。`AllowedCommands` 为空表示允许任意可执行程序入口；非空时只接受列出的文件名或精确路径。命令工作目录必须位于 APP 项目根内。修改本节配置后需要重启 APP。

控制台进程由 APP 启动，工作目录是项目根或其子目录。`TEMP`、`TMP`、`.NET CLI` 和 NuGet 缓存继续重定向到项目 `.muagent/data/`；标准输入关闭，标准输出/错误受字符上限约束，超时会终止整个进程树。命令本身拥有运行 APP 的操作系统账户权限，因此 `Allowed` 并不等同于沙箱，处理不可信项目时应使用容器或低权限账户。

自定义客户端在 `RequireApproval` 模式下应监听 `tool_call_started` 事件中的 `callId`、`name` 和 `argumentsJson`。确认后调用：

```http
POST /api/v1/command-approvals/{conversationId}/{callId}
Authorization: Bearer <token>
Content-Type: application/json

{"approved":true}
```

### 5.3 引用当前或指定目录

```text
/add .
/add src
/add "D:\work\another project"
```

引用会持续附加到之后每一条消息，直到使用 `/remove` 或退出 CLI。目录采用递归扫描，限制如下：

- 最多 200 个文件；
- 单文件最多 256 KiB，总计最多 2 MiB；
- 接受有效 UTF-8、UTF-8 BOM、UTF-16 LE/BE 文本；
- 不跟随目录符号链接；
- 自动跳过 `.git`、`.muagent`、`.svn`、`.hg`、`.vs`、`.idea`、`bin`、`obj`、`node_modules`、`data`；
- 自动跳过 `.env`、`muagents.settings.json`、证书/私钥、程序集、压缩包、图片和 PDF。

跳过原因会显示在终端。文件由 CLI 读取后作为不可信的 User 消息内容上传，API 不会根据客户端路径直接读取服务器磁盘。引用大量文件仍会占用模型上下文；如果超过模型预算，请缩小目录或分批引用。

## 6. 会话 API

### 6.1 创建会话

```powershell
$conversation = Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/api/v1/conversations `
  -Headers $headers -ContentType application/json `
  -Body '{"title":"演示会话"}'
```

### 6.2 发送消息

消息接口返回 `application/x-ndjson`，每一行是独立 JSON 事件。使用 `curl.exe` 可以直接观察流式输出：

```powershell
$id = $conversation.id
curl.exe -N -X POST "http://localhost:5000/api/v1/conversations/$id/messages" `
  -H "Authorization: Bearer $($login.accessToken)" `
  -H "Content-Type: application/json" `
  -d '{"text":"你好，请介绍你自己"}'
```

请求字段：

| 字段 | 类型 | 用途 |
| --- | --- | --- |
| `text` | string/null | 用户文本 |
| `model` | string/null | 本次覆盖默认模型 |
| `maxOutputTokens` | integer/null | 本次最大输出 Token |
| `temperature` | number/null | 采样温度 |
| `systemInstruction` | string/null | 本次系统指令 |
| `images` | array/null | 图片输入 |
| `skills` | array/null | 要注入的 Skill 名称 |
| `references` | array/null | 文本文件引用，每项包含 `path` 和 `content`；服务端再次执行数量和长度限制。 |

事件 `type` 可能是：`text_delta`、`reasoning_delta`、`tool_call_started`、`tool_call_completed`、`compaction_started`、`compaction_completed`、`usage_updated`、`warning`、`completed` 或 `error`。客户端必须逐行解析，不能把整个响应当成一个 JSON 对象。
`tool_call_started` 始终包含 `callId` 和工具 `name`；当工具是 `local.execute_command` 时还包含 `argumentsJson`，用于客户端在执行前展示并审批具体命令。其他工具不会在开始事件中公开参数。

### 6.3 查询历史

```powershell
Invoke-RestMethod `
  -Uri "http://localhost:5000/api/v1/conversations/$id" `
  -Headers $headers
```

只能读取当前 JWT 租户下的会话。

### 6.4 上下文状态与手动压缩

```powershell
Invoke-RestMethod `
  -Uri "http://localhost:5000/api/v1/conversations/$id/context" `
  -Headers $headers

Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5000/api/v1/conversations/$id/compact" `
  -Headers $headers
```

两个接口都返回 `currentTokens`、`maxContextTokens` 和 `compactTargetTokens`。`POST .../compact` 会在会话独占锁内把历史折叠为持久化检查点，目标为 `MaxContextTokens / 3`（向下取整）；后续请求和应用重启后都会继续使用压缩结果。若工具定义本身已经超过该目标，接口会返回配置错误，需缩小工具清单或提高上下文上限。

## 7. 用户与租户管理

拥有项目目录和数据库访问权的服务器管理员可以在 APP 停止后修改任意现有用户密码。该管理模式不会监听 HTTP 端口，密码不会出现在启动参数中；输入并确认成功后进程立即退出：

```powershell
dotnet run --project apps/MuAgents.App -- `
  -d D:\work\my-project --set-password admin

# 发布后二进制形式
D:\tools\MuAgents\MuAgents.App.exe `
  -d D:\work\my-project --set-password admin
```

`--set-password=<用户名>` 也可使用。新密码必须满足项目 `MinimumPasswordLength`，默认至少 12 个字符。目标用户不存在、两次输入不同或密码不合规时退出码为 2，数据库不会写入新密码。

创建普通用户和租户要求系统管理员权限：

```powershell
Invoke-RestMethod -Method Post http://localhost:5000/api/v1/admin/users `
  -Headers $headers -ContentType application/json `
  -Body '{"userName":"operator","password":"another-long-password"}'

Invoke-RestMethod -Method Post http://localhost:5000/api/v1/admin/tenants `
  -Headers $headers -ContentType application/json `
  -Body '{"name":"Operations","ownerUserName":"admin"}'
```

系统管理员，或当前租户中的 `Owner`，可以设置成员。角色仅允许 `Owner`、`Admin`、`Member`：

```powershell
Invoke-RestMethod -Method Put `
  -Uri "http://localhost:5000/api/v1/tenants/$tenantId/members" `
  -Headers $headers -ContentType application/json `
  -Body '{"userName":"operator","role":"Member"}'
```

`GET /api/v1/auth/tenants` 返回当前用户的全部租户成员关系；`GET /api/v1/auth/me` 返回当前 Token 的身份和租户。

`GET /api/v1/model` 返回当前模型配置摘要，供 CLI `/model` 使用。响应只包含协议、端点、模型名、Token 上限、能力开关和 `apiKeyConfigured` 布尔值，不返回 API Key。

`GET /api/v1/runtime` 返回 APP 项目根、`.muagent` 状态根、当前控制台审批模式及实际加载的配置文件，供 CLI `/status` 和部署检查使用。

## 8. 文件、PDF、图片和 OCR

### 8.1 安全根目录

- `MuAgents:Content:FileTool:WorkspaceRoots` 控制 `read_file` 工具可访问的文件目录；
- `MuAgents:Content:Images:AllowedRoots` 控制图片文件引用可访问的目录；
- 空数组表示只允许当前项目根目录，不表示允许任意磁盘路径；
- 路径会规范化后再校验，防止 `..` 或相似前缀绕过边界。

### 8.2 支持内容

- 纯文本：按字符数和分段限制读取；
- Markdown：按标题分节，在预算内返回内容；
- PDF：优先使用 `pdftotext`，必要时将页面渲染后交给 OCR；
- 图片：支持 HTTPS URL、允许目录内的文件引用和 Data URL，并校验大小、像素数及媒体类型。

发送图片示例：

```json
{
  "text": "描述这张图片",
  "images": [
    {
      "kind": "FileReference",
      "value": "samples/example.png",
      "mediaType": "image/png"
    }
  ]
}
```

`kind` 只能是 `HttpsUrl`、`FileReference` 或 `DataUrl`。是否真正可用视觉输入还取决于模型服务和 `SupportsVision`。

## 9. Web、MCP 与 Skill

### 9.1 Web

配置 `MuAgents:Web:SearchEndpoint` 后启用 JSON 搜索提供方。地址模板应包含 `{query}` 和可选的 `{count}`；API Key 通过 `ApiKeyHeader` 指定的请求头发送。网页抓取只允许 HTTP/HTTPS，并限制重定向、响应大小和超时，同时阻止访问本机及私有网络地址以降低 SSRF 风险。

### 9.2 MCP

首次启动会把 `MuAgents:Mcp` 默认值写入下面的运行时配置，之后终端命令直接维护该文件：

```text
<项目根目录>/.muagent/config/mcp.json
```

HTTP MCP 最便捷的管理方式：

```text
/mcp_add https://mcp.example.com/mcp
/mcp_add company https://mcp.example.com/mcp
/mcp
/mcp_tools company
/mcp_disable company
/mcp_enable company
/mcp_remove company
```

配置文件完整结构如下；需要 Stdio、请求头、环境变量或工具白名单时可停服后直接编辑：

```json
{
  "servers": [
    {
      "name": "example",
      "enabled": true,
      "transport": "Stdio",
      "url": null,
      "command": "node",
      "arguments": ["server.js"],
      "environment": {},
      "headers": {},
      "allowTools": [],
      "denyTools": [],
      "timeoutSeconds": 30
    }
  ],
  "toolCacheSeconds": 60
}
```

支持 `Stdio` 和 `StreamableHttp`。`/mcp_add` 当前用于 HTTP(S)；Stdio 高级字段通过 JSON 配置。配置变更会清除该服务的工具缓存和连接状态，无需重启应用。禁用的服务不能发现或调用工具。`allowTools` 非空时只有名单内工具会暴露，`denyTools` 始终优先拒绝。

已启用的 MCP 由模型通过内置 `mcp.call` 工具使用；先执行 `/mcp_tools <名称>` 可确认服务端工具名。HTTP API 对应为：

- `GET /api/v1/mcp`：查看服务与配置路径；
- `POST /api/v1/mcp`：添加或更新 HTTP MCP；
- `PUT /api/v1/mcp/{name}/enabled`：启用/禁用；
- `DELETE /api/v1/mcp/{name}`：删除；
- `GET /api/v1/mcp/{server}/tools`：发现工具。

前三个写操作要求系统管理员。Stdio 子进程的工作目录固定为项目根目录，`TEMP`、`TMP`、`TMPDIR` 固定到 `.muagent/data/temp/mcp/`。

### 9.3 Skill

Skill 的目录和禁用清单持久化在：

```text
<项目根目录>/.muagent/config/skills.json
```

```json
{
  "directories": ["skills"],
  "disabledSkills": ["temporarily-disabled-skill"]
}
```

动态管理示例：

```text
/skills
/skills_add skills/example
/skills_add D:\shared-skills
/skills_disable example
/skills_enable example
/skills_remove D:\shared-skills
```

`/skills_add` 接受单个 Skill 目录，也接受其直接子目录各自包含 `SKILL.md` 的根目录。相对路径以项目根目录为基准；绝对路径必须是 API 所在机器可读的目录。命令只修改 `.muagent/config/skills.json`，不复制或删除 Skill 文件。启停立即生效，写操作要求系统管理员。

```text
skills/
└─ example/
   ├─ SKILL.md
   └─ scripts/
      └─ task.py
```

Skill 指令被视为不可信内容并包装后加入系统指令。其声明的工具必须实际可用，否则请求会被拒绝。脚本策略 `ScriptPolicy` 支持拒绝、要求批准和允许；默认 `RequireApproval`，调用脚本接口时必须显式提交 `approved: true`。

HTTP API 对应为 `GET /api/v1/skills/config`、`POST/DELETE /api/v1/skills/directories` 和 `PUT /api/v1/skills/{name}/enabled`。

## 10. 可观测性与排错

每个 HTTP 响应包含 `X-Trace-Id`。`MuAgents` 的 `ActivitySource` 覆盖智能体运行、模型请求和工具调用；`Meter` 记录请求量、耗时、失败、上下文压缩、Token 用量及首事件耗时。

常见问题：

- **应用启动即报 JWT 配置错误**：检查 `JwtSigningKey` 是否至少 32 个字符。
- **模型返回 404**：检查 `BaseUrl`、`Endpoint` 与 `Protocol` 是否匹配服务端真实路由。
- **模型返回 401/403**：检查当前项目 `.muagent/config/muagents.settings.json` 中的 API Key。
- **模型拒绝输出长度**：降低 `MaxOutputTokens`，并确认模型服务接受相同字段和范围。
- **文件访问被拒绝**：把文件放入当前项目目录，或把规范化后的绝对目录加入相应允许根目录。
- **PDF 无文本**：安装 Poppler；扫描 PDF 还要安装 Tesseract 及对应语言包。
- **脚本被拒绝**：检查 `ScriptPolicy`、`AllowedRuntimes` 和请求的 `approved`。
- **控制台命令被拒绝或一直等待**：检查 `CommandExecution:ApprovalMode`、`AllowedCommands`，并确认 `RequireApproval` 模式下 CLI 仍连接且在超时前提交了决定。
- **HTTP 429**：登录或初始化请求过于频繁，等待固定窗口重置。
- **CLI 突然要求密码**：该用户已经设置密码；输入现有密码，遗忘时在服务器项目目录使用 APP `--set-password <用户名>` 管理模式重设。
- **需要迁移项目状态**：停止服务后复制整个 `.muagent/`，不要只复制数据库；不同项目不要共用同一个状态目录。

## 11. 安全建议

- 不提交 `.muagent/`，不在日志和文档中输出 API Key、密码或 JWT；
- 生产环境使用 HTTPS，并设置高强度随机 JWT 签名密钥；
- 无密码管理员仅适合绑定 `127.0.0.1` 的可信本机开发；监听局域网或公网前必须用 `--setup-password` 或 APP `--set-password admin` 设置密码；
- 限制项目 `.muagent/`、SQLite 数据库和 `.muagent/data/keys` 的操作系统访问权限；
- 仅配置必要的文件根目录、MCP 工具、Skill 运行时和 Web 服务；
- 生产环境优先使用 `CommandExecution:ApprovalMode=Denied`；如需执行，使用绝对命令白名单、低权限账户或容器，并保留逐次审批；
- 对允许执行脚本的部署使用低权限专用账户；
- 备份时同时保存数据库和 Data Protection 密钥，并采用加密存储。
