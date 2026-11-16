param([int]$Port = 5099)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$profile = Get-Content -LiteralPath (Join-Path $repoPath 'research/paper-profile.json') -Raw | ConvertFrom-Json
$arguments = [System.Collections.Generic.List[string]]::new()
foreach ($section in $profile.PSObject.Properties) {
    foreach ($setting in $section.Value.PSObject.Properties) {
        $value = [string]$setting.Value
        if ($setting.Value -is [bool]) { $value = $value.ToLowerInvariant() }
        $arguments.Add("--$($section.Name):$($setting.Name)=$value")
    }
}
# Explicit command-line overrides outrank existing development settings/environment variables.
$arguments.Add('--Live:Enabled=false')
$arguments.Add('--OpenAI:Enabled=false')
$arguments.Add('--Persistence:Provider=Sqlite')
$arguments.Add("--Persistence:DatabasePath=$(Join-Path $repoPath 'data/paper-research.db')")
$arguments.Add('--Api:Key=paper-local-development-only')
$arguments.Add("--urls=http://127.0.0.1:$Port")
$outputPath = Join-Path $repoPath 'artifacts/paper-research/'
& dotnet build (Join-Path $repoPath 'src/TradingBot.Worker/TradingBot.Worker.csproj') "-p:OutputPath=$outputPath" --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location (Join-Path $repoPath 'src/TradingBot.Worker')
try { & dotnet (Join-Path $outputPath 'TradingBot.Worker.dll') @arguments }
finally { Pop-Location }
