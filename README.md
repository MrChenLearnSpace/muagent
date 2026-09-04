# MuAgents

MuAgents 是一个基于 .NET 8 的跨平台智能体运行时。项目提供流式智能体循环、工具调用、上下文压缩、租户隔离的会话持久化，以及 OpenAI Chat Completions、Responses 和 Anthropic Messages 风格协议的兼容适配。

## 主要能力

- 通过 NDJSON 实时推送文本、推理、工具调用、用量和完成事件。
- 内置 Cookie 与 JWT Bearer 认证，并按用户、租户隔离会话。
- 支持文本、Markdown、PDF、图片和 OCR 内容读取。
- 支持本地工具、Web 搜索/抓取、MCP 服务及目录式 Skill。
- CLI 支持文件引用、MCP/Skill 动态管理、上下文状态和手动压缩等斜杠命令。
- 启动命令所在目录就是当前项目根目录；每个项目的配置、会话和缓存独立保存在自己的 `.muagent/` 中。
- 暴露 `ActivitySource` 和 `Meter`，可接入 OpenTelemetry。

## 环境要求

- .NET 8 SDK；
- 一个兼容的模型服务及 API Key；
- 可选：Poppler（PDF）、Tesseract（扫描件 OCR）以及 Skill 脚本所需运行时。

## 快速开始

1. 在准备使用 MuAgents 的项目根目录创建项目配置：

   ```powershell
   Set-Location D:\work\my-project
   New-Item -ItemType Directory -Force .muagent/config
   Copy-Item apps/MuAgents.App/muagents.settings.json `
     .muagent/config/muagents.settings.json
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

3. 保持终端位于项目根目录并启动 API：

   ```powershell
   dotnet run --project apps/MuAgents.App
   ```

4. 首次运行时，在另一个终端启动 CLI 并初始化管理员：

   ```powershell
   dotnet run --project apps/MuAgents.Cli -- `
     --url http://localhost:5000/ `
     --user admin `
     --bootstrap `
     --tenant-name Local
   ```

   按提示输入密码。密码长度必须满足配置中的 `MinimumPasswordLength`，默认至少 12 个字符。以后启动 CLI 时去掉 `--bootstrap`。

进入 CLI 后可直接使用：

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
MCP 与 Skill 的增删和启停会立即持久化到当前项目的 `.muagent/config/mcp.json` 与 `.muagent/config/skills.json`。每次模型回答结束后，终端都会显示“当前上下文 / 最大上下文”Token 数。

## 可移植目录约束

MuAgents 在进程最开始记录启动目录，并把它作为项目根目录。程序二进制可以安装在其他位置，但所有可写状态只进入当前项目的 `.muagent/`：

```text
my-project/                       # 启动 API/CLI 时所在目录
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

`/add .`、文件读取、图片和 Skill/MCP 相对路径以项目根目录解析；数据库、密钥、配置、临时目录和运行时缓存以 `.muagent/` 解析。所有可写路径如果逃逸 `.muagent/` 都会被拒绝。`TEMP`、`TMP`、`DOTNET_CLI_HOME` 和 NuGet 等缓存变量也会重定向到 `.muagent/data/`。

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
