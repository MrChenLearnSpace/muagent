# MuAgents

MuAgents 是一个基于 .NET 8 的跨平台智能体运行时。项目提供流式智能体循环、工具调用、上下文压缩、租户隔离的会话持久化，以及 OpenAI Chat Completions、Responses 和 Anthropic Messages 风格协议的兼容适配。

## 主要能力

- 通过 NDJSON 实时推送文本、推理、工具调用、用量和完成事件。
- 内置 Cookie 与 JWT Bearer 认证，并按用户、租户隔离会话。
- 支持文本、Markdown、PDF、图片和 OCR 内容读取。
- 支持经过安全边界和审批策略控制的项目控制台命令、本地工具、Web 搜索/抓取、MCP 服务及目录式 Skill。
- CLI 支持文件引用、MCP/Skill 动态管理、上下文状态和手动压缩等斜杠命令。
- APP 默认以启动命令所在目录作为项目根，也可用 `-d <项目路径>` 显式指定；每个项目的配置、会话和缓存独立保存在自己的 `.muagent/` 中。
- 暴露 `ActivitySource` 和 `Meter`，可接入 OpenTelemetry。

## 环境要求

- .NET 8 SDK；
- 一个兼容的模型服务及 API Key；
- 可选：Poppler（PDF）、Tesseract（扫描件 OCR）以及 Skill 脚本所需运行时。

## 快速开始

1. 在准备使用 MuAgents 的项目根目录创建项目配置：

   ```powershell
   $muagentsSource = "F:\path\to\muagents"
   $projectPath = "D:\work\my-project"
   New-Item -ItemType Directory -Force "$projectPath\.muagent\config"
   Copy-Item "$muagentsSource\apps\MuAgents.App\muagents.settings.json" `
     "$projectPath\.muagent\config\muagents.settings.json"
   ```

   如果直接首次启动 API，也会自动复制这份模板；填写配置后重新启动即可。

2. 编辑 `.muagent/config/muagents.settings.json`。例如兼容 Responses API 的服务：

   ```json
   {
     "MuAgents": {
       "Model": {
         "Protocol": "Responses",
         "BaseUrl": "http://10.1.1.226:10505/v1/",
         "Endpoint": "responses",
         "ApiKey": "请填写实际密钥",
         "Model": "请填写实际模型名"
       },
       "Authentication": {
         "JwtSigningKey": "请替换为至少32个字符的随机字符串"
       }
     }
   }
   ```

   `BaseUrl` 必须包含 `http://` 或 `https://`。模型服务若不是 `/v1/responses` 路由，请相应调整 `BaseUrl`、`Endpoint` 和 `Protocol`。

3. 保持终端位于项目根目录并启动 API；也可在任意目录通过 `-d` 指向项目：

   ```powershell
   dotnet run --project "$muagentsSource\apps\MuAgents.App" -- -d $projectPath
   ```

4. 在另一个终端直接启动 CLI：

   ```powershell
   dotnet run --project "$muagentsSource\apps\MuAgents.Cli" -- `
     --url http://localhost:5000/
   ```

   全新项目会自动创建无密码的 `admin` 用户和 `Local` 租户，因此默认启动不再询问密码。仅监听本机的开发环境可以使用这种方式。如果首次运行就要启用密码，增加 `--setup-password`，CLI 会要求输入并确认密码；以后启动时无需再带该参数，但会自动提示登录密码：

   ```powershell
   dotnet run --project "$muagentsSource\apps\MuAgents.Cli" -- `
     --url http://localhost:5000/ --setup-password
   ```

   `--bootstrap` 继续作为 `--setup-password` 的兼容别名。密码长度必须满足 `MinimumPasswordLength`，默认至少 12 个字符。

进入 CLI 后，输入 `/` 再按 `Tab` 会列出命令，输入唯一前缀（例如 `/mcp_d`）再按 `Tab` 会补全命令。也可直接使用：

```text
/help                  查看所有命令
/model                 查看模型协议、端点、名称、上下文及能力
/add .                 引用当前目录下全部可读文本文件
/add D:\work\project   引用指定目录下全部可读文本文件
/context               查看当前文件上下文
/remove all            清空文件引用
/mcp                   查看 MCP 服务及配置文件路径
/mcp_add <url>         添加 HTTP MCP 服务
/mcp_disable <名称>    禁用 MCP 服务
/skills                查看 Skill、扫描目录及配置文件路径
/skills_add <目录>     添加 Skill 或 Skill 根目录
/skills_disable <名称> 禁用 Skill
/compact               把会话压缩到最大上下文的 1/3 以内
```

目录引用会递归处理，并自动跳过 `.git`、`bin`、`obj`、`data`、`node_modules`、本地密钥文件、二进制及超限文件。
MCP 与 Skill 的增删和启停会由 APP 立即持久化到 APP 项目根的 `.muagent/config/mcp.json` 与 `.muagent/config/skills.json`。每次模型回答结束后，终端都会显示“当前上下文 / 最大上下文”Token 数。CLI 重启时默认恢复当前用户最近更新的会话，`/new` 才会创建新会话；访问令牌到期前会自动重新登录，因此不会因 CLI 长时间空闲而丢失当前上下文。运行时会修复旧版本遗留的不完整工具历史，并对模型的空响应自动重试，避免连续对话出现空白或失忆。若模型输出上限截断了工具参数，协议层会将残缺内容替换为合法的恢复错误并要求模型拆小重试，不会把无效 JSON 写入会话、继而触发下一轮 HTTP 500。CLI 不接受 `-d`、不创建 `.muagent`；它的当前目录只作为 `/add` 本地文件引用的基准。

## 控制台执行与三种审批模式

模型现在会收到固定的编码代理规则：当用户要求创建、修改、修复、编译或测试项目时，应先检查项目，再通过 `local.write_file` 实际写入文件，并用 `local.execute_command` 验证；只有用户明确只要示例时才仅在聊天中给代码。`local.list_files`、`local.read_file` 和 `local.write_file` 都以 APP 项目根为边界，写入工具禁止访问 `.muagent`、`.git` 和项目外路径。

文件写入与控制台执行共用三档本地操作审批。默认采用逐次审批，CLI 会显示命令详情或文件路径、字符数和覆盖方式，并要求当前用户明确输入 `y`：

```json
{
  "MuAgents": {
    "CommandExecution": {
      "ApprovalMode": "RequireApproval",
      "AllowedCommands": [],
      "ApprovalTimeoutSeconds": 120,
      "MaxExecutionSeconds": 120,
      "MaxOutputCharacters": 48000
    },
    "WorkspaceFiles": {
      "Enabled": true,
      "MaxWriteCharacters": 2000000,
      "MaxListEntries": 2000
    }
  }
}
```

- `Denied`：禁止模型写文件和执行控制台命令。
- `RequireApproval`：每次文件写入或命令执行都向当前 CLI 用户询问，默认拒绝。
- `Allowed`：模型可自动写文件和执行命令，建议只用于可信且隔离的开发环境。

`AllowedCommands` 为空表示不额外限制；也可填写 `dotnet`、`git`、`pwsh.exe` 等允许项。工作目录只能是 APP 项目根或其子目录，临时目录和运行时缓存仍固定在项目 `.muagent/data/`。修改审批配置后重启 APP 生效。

## 发布与二进制启动

下面生成依赖目标机器 .NET 8 Runtime 的可执行文件：

```powershell
dotnet publish apps/MuAgents.App -c Release -r win-x64 --self-contained false -o publish/app
dotnet publish apps/MuAgents.Cli -c Release -r win-x64 --self-contained false -o publish/cli
```

发布完成后无需 `dotnet run`，可从任何目录直接运行 `.exe`。只有 APP 使用 `-d`；CLI 通过 `--url` 连接 APP：

```powershell
.\publish\app\MuAgents.App.exe -d D:\work\my-project --urls http://127.0.0.1:5000
.\publish\cli\MuAgents.Cli.exe `
  --url http://127.0.0.1:5000/
```

Linux 发布时把运行时标识改成 `linux-x64`，二进制入口分别为 `MuAgents.App` 和 `MuAgents.Cli`。目标机器没有 .NET Runtime 时，将 `--self-contained false` 改为 `--self-contained true`。APP 未指定 `-d` 时使用当前终端目录；路径包含空格时请加引号。API 启动输出会列出项目根、状态目录以及本次实际加载的每个配置文件。

服务器管理员可在 APP 停止后从项目本机修改指定用户密码；程序会安全读取两遍新密码，修改后直接退出，不启动 HTTP 服务：

```powershell
.\publish\app\MuAgents.App.exe -d D:\work\my-project --set-password admin
```

## 可移植目录约束

MuAgents APP 在进程最开始解析 `-d`，未提供时记录启动目录，并把结果作为项目根目录。程序二进制可以安装在其他位置，但 APP 的所有可写状态只进入当前项目的 `.muagent/`：

```text
my-project/                       # APP 的 -d 项目目录或 APP 启动目录
├─ .muagent/                      # 本项目独立状态，已加入 .gitignore
│  ├─ config/
│  │  ├─ muagents.settings.json  # 模型、认证和 Web 配置
│  │  ├─ appsettings.json        # 可选的运行参数覆盖
│  │  ├─ mcp.json                # MCP 服务及启停状态
│  │  └─ skills.json             # Skill 扫描目录及禁用清单
│  └─ data/
│     ├─ muagents.db             # 会话、身份和租户数据
│     ├─ keys/                   # ASP.NET Core Data Protection 密钥
│     ├─ temp/                   # 主进程和扩展临时文件
│     ├─ dotnet/                 # 子进程的 .NET CLI 主目录
│     └─ nuget/                  # 子进程的 NuGet 缓存
├─ skills/                        # 项目 Skill，默认从这里发现
└─ ...                            # 项目源码和其他文件
```

APP 的文件读取、图片、控制台工作目录和 Skill/MCP 相对路径以 APP 项目根解析；数据库、密钥、配置、临时目录和运行时缓存以 `.muagent/` 解析。首次启动会自动复制配置模板到项目 `.muagent/config/`，后续升级或重新编译不会覆盖项目副本。所有 APP 管理的可写状态如果逃逸 `.muagent/` 都会被拒绝。用户目录、应用数据、ProgramData、XDG、`TEMP`、`DOTNET_CLI_HOME` 和 NuGet 等可写环境变量均重定向到 `.muagent/data/`，命令参数不能覆盖这些保护。CLI 的 `/add .` 则以 CLI 当前终端目录为准，文件内容通过认证 API 上传，不让 APP 按客户端路径读取磁盘。

## 文档

- [完整使用手册](docs/USER_GUIDE.md)
- [项目文件详细说明](docs/FILE_REFERENCE.md)
- [架构设计](DESIGN.md)

## 构建与测试

```powershell
dotnet build MuAgents.sln -c Release
dotnet test MuAgents.sln -c Release --no-build
```

项目状态目录 `.muagent/`、构建输出 `bin/` 和 `obj/` 均不应提交到版本库。
