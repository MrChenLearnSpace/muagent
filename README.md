# MuAgents

MuAgents 是一个基于 .NET 8 的跨平台智能体运行时。项目提供流式智能体循环、工具调用、上下文压缩、租户隔离的会话持久化，以及 OpenAI Chat Completions、Responses 和 Anthropic Messages 风格协议的兼容适配。

## 主要能力

- 通过 NDJSON 实时推送文本、推理、工具调用、用量和完成事件。
- 内置 Cookie 与 JWT Bearer 认证，并按用户、租户隔离会话。
- 支持文本、Markdown、PDF、图片和 OCR 内容读取。
- 支持本地工具、Web 搜索/抓取、MCP 服务及目录式 Skill。
- CLI 支持文件引用、MCP/Skill 动态管理、上下文状态和手动压缩等斜杠命令。
- API 与 CLI 都把可执行文件目录设为唯一根目录；SQLite、密钥、.NET/NuGet 缓存和临时文件不会写入 C 盘用户目录或系统临时目录。
- 暴露 `ActivitySource` 和 `Meter`，可接入 OpenTelemetry。

## 环境要求

- .NET 8 SDK；
- 一个兼容的模型服务及 API Key；
- 可选：Poppler（PDF）、Tesseract（扫描件 OCR）以及 Skill 脚本所需运行时。

## 快速开始

1. 复制本地配置文件：

   ```powershell
   Copy-Item apps/MuAgents.App/muagents.settings.json `
     apps/MuAgents.App/muagents.settings.local.json
   ```

2. 编辑 `muagents.settings.local.json`。例如兼容 Responses API 的服务：

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

3. 启动 API：

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
MCP 与 Skill 的增删和启停会立即持久化到程序根目录的 `config/mcp.json` 与 `config/skills.json`。每次模型回答结束后，终端都会显示“当前上下文 / 最大上下文”Token 数。

## 可移植目录约束

程序启动后会把工作目录固定为 `AppContext.BaseDirectory`，也就是发布后可执行文件所在目录。所有可写数据必须位于这个目录之下：

```text
MuAgents.App/
├─ MuAgents.App.exe
├─ appsettings.json
├─ muagents.settings.json
├─ muagents.settings.local.json   # 本地敏感配置，不提交 Git
├─ config/
│  ├─ mcp.json                    # MCP 服务及启停状态
│  └─ skills.json                 # Skill 扫描目录及禁用清单
├─ data/
│  ├─ muagents.db                 # 会话、身份和租户数据
│  ├─ keys/                       # ASP.NET Core Data Protection 密钥
│  ├─ temp/                       # 主进程、PDF、OCR、MCP 等临时文件
│  ├─ dotnet/                     # 子进程的 .NET CLI 主目录
│  └─ nuget/                      # 子进程的 NuGet 包与 HTTP 缓存
└─ skills/                        # Skill 目录及脚本
```

API、CLI、MCP、OCR、内容处理和 Skill 脚本都会继承这条规则。`TEMP`、`TMP`、`TMPDIR`、`DOTNET_CLI_HOME`、`NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH` 与 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 在进程启动时被重定向到 `data/`。相对读写路径基于程序根目录解析；指向根目录外部的写入路径会被拒绝。`data/keys` 中的便携式密钥没有绑定 Windows DPAPI，部署时应使用操作系统权限限制访问。

## 文档

- [完整使用手册](docs/USER_GUIDE.md)
- [项目文件详细说明](docs/FILE_REFERENCE.md)
- [架构设计](DESIGN.md)

## 构建与测试

```powershell
dotnet build MuAgents.sln -c Release
dotnet test MuAgents.sln -c Release --no-build
```

本地配置 `muagents.settings.local.json`、运行数据 `data/`、构建输出 `bin/` 和 `obj/` 均不应提交到版本库。
