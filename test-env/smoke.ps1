[CmdletBinding()]
param(
    [string]$WorkspaceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $env:LOCALAPPDATA 'GitSvnShuttle\TestWorkspace'
}
$settingsPath = Join-Path ([IO.Path]::GetFullPath($WorkspaceRoot)) 'test-environment.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw 'Test environment not found. Run .\test-env\setup.ps1 first.'
}

$environment = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json

$repositories = @($environment.externalRoot, $environment.solutionRoot)
$probeProject = Join-Path $repoRoot 'tests\GitSvnShuttle.DcommitProbe\GitSvnShuttle.DcommitProbe.csproj'
& dotnet run --project $probeProject -c Release -- $environment.git @repositories
if ($LASTEXITCODE -ne 0) {
    throw "Product dcommit probe failed with exit code $LASTEXITCODE."
}

Write-Host 'End-to-end product smoke test passed: both current branches were rewritten and have no pending commits.'
