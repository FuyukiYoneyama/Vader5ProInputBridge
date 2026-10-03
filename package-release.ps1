param()
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'VERSION') -Raw).Trim()
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'apps\build-manifest.json') -Raw | ConvertFrom-Json
if ($taskManifest.buildId -ne $taskVersion) { throw 'VERSIONとビルド版を合わせてください。' }
$taskSourceCommit = (& git -C $taskRoot rev-parse HEAD).Trim()
if ($taskSourceCommit -ne $taskManifest.sourceCommit) { throw '現在のコミットでビルドしてから配布物を生成してください。' }
if ($taskManifest.sourceDirty -or (& git -C $taskRoot status --porcelain)) { throw '変更をGitへ保存し、確定した版から配布物を生成してください。' }
$taskBridge = Join-Path $taskRoot 'apps\VADERBridge'
foreach ($taskFile in $taskManifest.files | Where-Object { $_.path.StartsWith('apps/VADERBridge/', [StringComparison]::OrdinalIgnoreCase) }) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskFile.path) -Algorithm SHA256).Hash -ne $taskFile.sha256) { throw '実行物の識別値を確認してください。' }
}
$taskName = 'VADERBridge-' + $taskVersion
$taskPackage = Join-Path $taskRoot ('dist\' + $taskName)
$taskArchive = $taskPackage + '.zip'
if ((Test-Path -LiteralPath $taskPackage) -or (Test-Path -LiteralPath $taskArchive)) { throw 'この版の配布物が存在します。次の版番号を指定してください。' }
New-Item -ItemType Directory -Path $taskPackage | Out-Null
foreach ($taskFile in @('VADERBridge.exe','VADERBridge.dll','VADERBridge.deps.json','VADERBridge.runtimeconfig.json','InputTools.Common.dll')) {
    Copy-Item -LiteralPath (Join-Path $taskBridge $taskFile) -Destination $taskPackage
}
New-Item -ItemType Directory -Path (Join-Path $taskPackage 'config') | Out-Null
$taskConfig = Get-Content -LiteralPath (Join-Path $taskRoot 'config\bridge.json') -Raw | ConvertFrom-Json
$taskConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskPackage 'config\bridge.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\USAGE.md') -Destination (Join-Path $taskPackage '利用ガイド.md')
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\images') -Destination (Join-Path $taskPackage 'images') -Recurse
Copy-Item -LiteralPath (Join-Path $taskRoot 'CHANGELOG.md'),(Join-Path $taskRoot 'VERSION'),(Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD_PARTY_NOTICES.md') -Destination $taskPackage
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination (Join-Path $taskPackage 'licenses') -Recurse
$taskFiles = @(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($taskPackage, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
[ordered]@{ version = $taskVersion; sourceCommit = $taskSourceCommit; utc = [DateTime]::UtcNow.ToString('o'); files = $taskFiles } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPackage 'release-manifest.json') -Encoding utf8
Compress-Archive -Path (Join-Path $taskPackage '*') -DestinationPath $taskArchive
((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($taskArchive)) |
    Set-Content -LiteralPath ($taskArchive + '.sha256') -Encoding ascii
Write-Output $taskArchive
