param([string]$BuildId = '')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildId)) { $BuildId = (Get-Content -LiteralPath (Join-Path $taskRoot 'VERSION') -Raw).Trim() }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.dotnet-cli'
$env:APPDATA = Join-Path $taskRoot '.appdata'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskTargets = @(
    @{ Project = 'XInputReader'; App = 'XInputReader' },
    @{ Project = 'DInputReader'; App = 'DInputReader1'; Id = 1 },
    @{ Project = 'DInputReader'; App = 'DInputReader2'; Id = 2 },
    @{ Project = 'VaderBridge'; App = 'VADERBridge' },
    @{ Project = 'VaderHidProbe'; App = 'VaderHidProbe' }
)
foreach ($taskTarget in $taskTargets) {
    $taskProject = $taskTarget.Project; $taskName = $taskTarget.App
    $taskSource = Join-Path $taskRoot "src\$taskProject\$taskProject.csproj"
    & dotnet restore $taskSource --configfile (Join-Path $taskRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "restore: $taskName ($LASTEXITCODE)" }
    $taskArguments = @('build', $taskSource, '--configuration', 'Release', '--output', (Join-Path $taskRoot "apps\$taskName"), '--no-restore', "-p:InformationalVersion=$BuildId")
    if ($taskTarget.ContainsKey('Id')) { $taskArguments += "-p:DInputVJoyId=$($taskTarget.Id)" }
    & dotnet @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "build: $taskName ($LASTEXITCODE)" }
}
$taskFiles = @($taskTargets | ForEach-Object {
    $taskApp = Join-Path $taskRoot "apps\$($_.App)"
    $taskAppFiles = @("$($_.App).exe", "$($_.App).dll")
    if ($_.Project -ne 'VaderHidProbe') { $taskAppFiles += 'InputTools.Common.dll' }
    foreach ($taskFile in $taskAppFiles) { Get-Item -LiteralPath (Join-Path $taskApp $taskFile) }
})
$taskManifest = [ordered]@{
    buildId = $BuildId
    sourceCommit = (& git -C $taskRoot rev-parse HEAD)
    sourceDirty = [bool](& git -C $taskRoot status --porcelain)
    utc = [DateTime]::UtcNow.ToString('o')
    config = (Get-FileHash -LiteralPath (Join-Path $taskRoot 'config\bridge.json') -Algorithm SHA256).Hash
    files = @($taskFiles | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($taskRoot, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; version = $_.VersionInfo.FileVersion } })
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'apps\build-manifest.json') -Encoding utf8
