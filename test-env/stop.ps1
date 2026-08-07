[CmdletBinding()]
param(
    [string]$WorkspaceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $env:LOCALAPPDATA 'GitSvnShuttle\TestWorkspace'
}
$settingsPath = Join-Path ([IO.Path]::GetFullPath($WorkspaceRoot)) 'test-environment.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    Write-Host 'No running test environment was found.'
    exit 0
}

$environment = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$process = Get-Process -Id ([int]$environment.svnservePid) -ErrorAction SilentlyContinue
if ($null -eq $process) {
    Write-Host 'The local svnserve process is already stopped.'
    exit 0
}
if ($process.Path -ne $environment.svnserve) {
    throw "PID $($process.Id) is not the svnserve process recorded by the test environment."
}

Stop-Process -Id $process.Id -Force
Write-Host 'The local Git-SVN test server has been stopped.'
