# MuAgents

MuAgents is a cross-platform .NET agent runtime. The first implementation slice includes a streaming agent loop, namespaced tools, tenant-aware SQLite conversations, context budgeting, and adapters for Chat Completions, Responses, and Messages-style APIs.

## Run

Copy `apps/MuAgents.App/muagents.settings.json` to `muagents.settings.local.json` in the same directory and set `MuAgents.Model.BaseUrl`, `ApiKey`, `Protocol`, and `Model`. The local file is ignored by Git and is copied beside the executable when present. Then start the API:

```powershell
dotnet run --project apps/MuAgents.App
```

Start the CLI in another terminal:

```powershell
dotnet run --project apps/MuAgents.Cli -- --url http://localhost:5000/
```

Model credentials and endpoint selection are loaded from `muagents.settings.json` and the optional `muagents.settings.local.json` beside the program. Environment variables are not used for model credentials.

Protected APIs require a Cookie or JWT Bearer identity. Tenant and user IDs are taken only from validated claims; request headers can no longer select an arbitrary tenant.

## Configuration

Sensitive endpoint configuration belongs in `apps/MuAgents.App/muagents.settings.local.json` during development or beside `MuAgents.App.exe` after publishing:

```json
{
  "MuAgents": {
    "Model": {
      "Protocol": "Responses",
      "BaseUrl": "https://provider.example/v1/",
      "Endpoint": "responses",
      "ApiKey": "your-key",
      "Model": "model-name"
    },
    "Authentication": {
      "JwtSigningKey": "replace-with-at-least-32-random-characters"
    },
    "Web": {
      "SearchEndpoint": "https://search.example/api?q={query}&count={count}",
      "ApiKey": "search-key",
      "ApiKeyHeader": "X-API-Key"
    }
  }
}
```

For local files and images, configure `MuAgents.Content.FileTool.WorkspaceRoots` and `MuAgents.Content.Images.AllowedRoots` in `appsettings.json`. Empty root lists restrict access to the process working directory.

PDF text extraction uses Poppler's `pdftotext`. Scanned pages additionally require `pdftoppm` and Tesseract with the configured language packs. Executable names or absolute paths are configured under `MuAgents.Content`.

MCP servers are configured in `MuAgents.Mcp.Servers`. Both `StreamableHttp` and `Stdio` transports are supported:

```json
{
  "Name": "example",
  "Enabled": true,
  "Transport": "Stdio",
  "Command": "node",
  "Arguments": [ "server.js" ],
  "AllowTools": []
}
```

Skill directories use the `skills/<name>/SKILL.md` layout. Scripts are denied, approval-gated, or allowed according to `MuAgents.Skills.ScriptPolicy`; approval-gated scripts require `Approved: true` on the script API request.

## First login

Set a random JWT signing key in `muagents.settings.local.json` before starting the application. Create the first local administrator and tenant exactly once:

```powershell
Invoke-RestMethod -Method Post http://localhost:5000/api/v1/auth/bootstrap `
  -ContentType application/json `
  -Body '{"userName":"admin","password":"a-long-unique-password","tenantName":"Local"}'
```

Then call `/api/v1/auth/login`, or start the CLI with `--bootstrap` on its first run. The login response contains a tenant-scoped JWT. Pass `useCookie: true` to also create an HTTP-only, same-site authentication cookie.
