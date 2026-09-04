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

外部程序可以位于系统 `PATH` 中，也可以在配置中填写其绝对路径。它们产生的 MuAgents 工作文件仍会放在程序根目录的 `data/temp/` 下。

## 2. 配置文件与加载顺序

API 应用使用三个 JSON 配置来源：

1. `appsettings.json`：运行时、内容、工具、持久化、MCP 等非敏感默认值；
2. `muagents.settings.json`：模型、认证和 Web 服务的结构化默认值；
3. `muagents.settings.local.json`：本机覆盖值，最后加载，适合保存 API Key 和 JWT 签名密钥。

三个文件均从可执行文件所在目录读取。开发时把本地配置放在 `apps/MuAgents.App/`；发布后把它放在 `MuAgents.App.exe` 同目录。该文件已被 Git 忽略。

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

`BaseUrl` 必须是带协议头的绝对 URL，并建议以 `/` 结尾。最终地址由 `BaseUrl` 和 `Endpoint` 组合，务必与模型服务实际路由一致。不要把真实密钥写入 `muagents.settings.json` 或任何文档。

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
- `DataProtectionKeysPath` 必须解析到程序根目录内。
- 登录和首次初始化接口按来源 IP 限制为每分钟 10 次。

### 2.3 数据与上下文配置

常用配置位于 `appsettings.json`：

- `MuAgents:Persistence:ConnectionString`：默认 `Data Source=data/muagents.db`；相对 SQLite 路径以程序根目录为基准。
- `MuAgents:Agent:MaxToolIterations`：单轮最多工具迭代次数，默认 12。
- `MuAgents:Agent:ToolTimeoutSeconds`：一次工具调用超时，默认 60 秒。
- `MuAgents:Agent:MaxConcurrency`：同一批工具调用并发数，默认 4。
- `MuAgents:Context:MaxContextTokens`：上下文窗口上限。
- `MuAgents:Context:ReservedOutputTokens`：为模型输出预留的 Token 数。
- `MuAgents:Context:RecentTurnsToKeep`：压缩时强制保留的最近轮次数。

模型配置的 `MaxContextTokens` 描述模型能力；上下文配置决定运行时实际预算。配置时应让运行时预算不超过模型限制。

## 3. 启动、发布与目录约束

### 3.1 从源码启动

```powershell
dotnet restore MuAgents.sln
dotnet run --project apps/MuAgents.App
```

默认监听地址以 ASP.NET Core 启动输出为准。另开终端检查：

```powershell
Invoke-RestMethod http://localhost:5000/api/v1/health
```

成功响应为 `{ "status": "ok" }`。

### 3.2 发布运行

```powershell
dotnet publish apps/MuAgents.App -c Release -o publish/MuAgents.App
dotnet publish apps/MuAgents.Cli -c Release -o publish/MuAgents.Cli
```

把配置、程序、`data/` 和 `skills/` 作为一个整体目录部署。API 与 CLI 都会把工作目录切换到各自可执行文件所在目录，`/add .` 也从 CLI 程序目录读取，不使用启动终端所在目录。

启动入口会在创建宿主或 HTTP 客户端之前重定向以下环境变量：

| 环境变量 | 程序目录内位置 |
| --- | --- |
| `TEMP`、`TMP`、`TMPDIR` | `data/temp/process/`；具体外部任务使用其独立子目录。 |
| `DOTNET_CLI_HOME` | `data/dotnet/home/` |
| `NUGET_PACKAGES` | `data/nuget/packages/` |
| `NUGET_HTTP_CACHE_PATH` | `data/nuget/http-cache/` |
| `DOTNET_BUNDLE_EXTRACT_BASE_DIR` | `data/temp/dotnet-bundle/` |

MCP、OCR、PDF/内容处理及 Skill 脚本子进程会再次覆盖这些变量，配置中的同名环境变量不能把写入位置改到程序根目录之外。程序不会把运行数据写入 Windows 用户目录、系统临时目录或 C 盘。

任何配置的写入路径如果逃逸程序根目录，程序会抛出异常并拒绝使用。读取文件和图片还受到各自允许根目录的额外限制。这里的“程序根目录”是构建/发布产物实际所在目录，不是源码仓库目录，也不是调用命令时的 PowerShell 目录。

## 4. 首次初始化和登录

身份库为空时，只能成功执行一次初始化：

```powershell
$body = @{
  userName = "admin"
  password = "请使用足够长的独立密码"
  tenantName = "Local"
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/api/v1/auth/bootstrap `
  -ContentType application/json -Body $body
```

它会原子创建系统管理员、首个租户和 `Owner` 成员关系。重复调用返回 HTTP 409。

登录并保存 JWT：

```powershell
$loginBody = @{
  userName = "admin"
  password = "请使用初始化时的密码"
  useCookie = $false
} | ConvertTo-Json

$login = Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/api/v1/auth/login `
  -ContentType application/json -Body $loginBody

$headers = @{ Authorization = "Bearer $($login.accessToken)" }
```

用户属于多个租户时，登录请求要传 `tenantId`。签发的 JWT 只代表本次选择的租户，客户端不能用请求头任意切换租户。

## 5. CLI 对话

首次初始化并登录：

```powershell
dotnet run --project apps/MuAgents.Cli -- `
  --url http://localhost:5000/ --user admin `
  --bootstrap --tenant-name Local
```

常规登录：

```powershell
dotnet run --project apps/MuAgents.Cli -- `
  --url http://localhost:5000/ --user admin
```

多租户用户额外传 `--tenant <tenant-id>`。CLI 每次启动会新建会话；普通文本直接发送给模型，以 `/` 开头的内容由 CLI 解释为本地命令。

### 5.1 斜杠命令

| 命令 | 说明 |
| --- | --- |
| `/help` | 显示命令帮助。 |
| `/model` | 从 API 查询当前模型名、协议、完整端点、上下文/输出上限、图片/工具能力和密钥是否已配置；不会显示密钥值。 |
| `/status` | 显示 API、用户、租户、会话、CLI 程序根目录、程序内临时目录、引用文件统计，以及当前/最大上下文 Token。 |
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

### 5.2 引用当前或指定目录

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
- 自动跳过 `.git`、`.svn`、`.hg`、`.vs`、`.idea`、`bin`、`obj`、`node_modules`、`data`；
- 自动跳过 `.env`、`muagents.settings.local.json`、证书/私钥、程序集、压缩包、图片和 PDF。

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

## 8. 文件、PDF、图片和 OCR

### 8.1 安全根目录

- `MuAgents:Content:FileTool:WorkspaceRoots` 控制 `read_file` 工具可访问的文件目录；
- `MuAgents:Content:Images:AllowedRoots` 控制图片文件引用可访问的目录；
- 空数组表示只允许程序根目录，不表示允许任意磁盘路径；
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
<程序根目录>/config/mcp.json
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

前三个写操作要求系统管理员。Stdio 子进程的工作目录固定为程序根目录，`TEMP`、`TMP`、`TMPDIR` 固定到 `data/temp/mcp/`，不会使用 C 盘系统临时目录。

### 9.3 Skill

Skill 的目录和禁用清单持久化在：

```text
<程序根目录>/config/skills.json
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

`/skills_add` 接受单个 Skill 目录，也接受其直接子目录各自包含 `SKILL.md` 的根目录。相对路径以 API 程序根目录为基准；绝对路径必须是 API 所在机器可读的目录。命令只修改扫描配置，不复制或删除 Skill 文件。启停立即生效，写操作要求系统管理员。

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
- **模型返回 401/403**：检查本地配置中的 API Key，确认配置文件位于实际可执行文件旁。
- **模型拒绝输出长度**：降低 `MaxOutputTokens`，并确认模型服务接受相同字段和范围。
- **文件访问被拒绝**：把文件放入程序根目录，或把规范化后的绝对目录加入相应允许根目录。
- **PDF 无文本**：安装 Poppler；扫描 PDF 还要安装 Tesseract 及对应语言包。
- **脚本被拒绝**：检查 `ScriptPolicy`、`AllowedRuntimes` 和请求的 `approved`。
- **HTTP 429**：登录或初始化请求过于频繁，等待固定窗口重置。
- **需要迁移程序**：停止服务后整体复制程序目录，特别是 `data/muagents.db` 与 `data/keys/`，不要只复制数据库。

## 11. 安全建议

- 不提交 `muagents.settings.local.json`，不在日志和文档中输出 API Key、密码或 JWT；
- 生产环境使用 HTTPS，并设置高强度随机 JWT 签名密钥；
- 限制程序目录、SQLite 数据库和 `data/keys` 的操作系统访问权限；
- 仅配置必要的文件根目录、MCP 工具、Skill 运行时和 Web 服务；
- 对允许执行脚本的部署使用低权限专用账户；
- 备份时同时保存数据库和 Data Protection 密钥，并采用加密存储。
