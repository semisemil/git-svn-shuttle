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

function Invoke-GitSvn([string]$WorkingDirectory, [string[]]$Arguments) {
    Write-Host "[$WorkingDirectory] git $($Arguments -join ' ')"
    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $environment.git -C $WorkingDirectory @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
    if ($exitCode -ne 0) {
        throw "Git command failed with exit code $exitCode."
    }
}

$repositories = @($environment.externalRoot, $environment.solutionRoot)
foreach ($repository in $repositories) {
    Invoke-GitSvn $repository @('svn', 'rebase')
    Invoke-GitSvn $repository @('svn', 'dcommit', '--dry-run')
    Invoke-GitSvn $repository @('svn', 'dcommit')

    $baseline = (& $environment.git -C $repository log --grep='git-svn-id:' --format='%H' -1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($baseline)) {
        throw "No git-svn-id baseline found in $repository"
    }
    $pending = (& $environment.git -C $repository rev-list --count "$baseline..HEAD").Trim()
    if ($LASTEXITCODE -ne 0 -or $pending -ne '0') {
        throw "Pending commits remain after dcommit in $repository"
    }
}

Write-Host 'End-to-end smoke test passed: rebase, dry-run, and dcommit completed for both repositories.'
