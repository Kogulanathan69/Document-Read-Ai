# Live web search setup

GazetteAI uses Tavily only after the user confirms that the assistant may
search outside the uploaded document.

1. Create a Tavily API key at `https://app.tavily.com/`.
2. From the `backend` folder, store it with .NET user-secrets:

```powershell
$secureTavilyKey = Read-Host "Enter Tavily API key" -AsSecureString
$tavilyKey = [System.Net.NetworkCredential]::new(
    "",
    $secureTavilyKey
).Password

dotnet user-secrets set "Tavily:ApiKey" "$tavilyKey" `
    --project .\GazetteAI.Api\GazetteAI.Api.csproj

Remove-Variable secureTavilyKey, tavilyKey
```

3. Verify that the secret name exists without printing its value:

```powershell
dotnet user-secrets list `
    --project .\GazetteAI.Api\GazetteAI.Api.csproj |
    ForEach-Object { ($_ -split " = ")[0] }
```

4. Build and run:

```powershell
dotnet build
dotnet run --project .\GazetteAI.Api\GazetteAI.Api.csproj `
    --urls http://localhost:5000
```

Suggested test:

1. `Who is the president of Sri Lanka?`
2. `yes, tamil la sollu`

The second response should have `answerSource: "live-web"` and a non-empty
`webSources` array.
