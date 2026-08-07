[CmdletBinding()]
param(
    [switch]$Reset,
    [string]$WorkspaceRoot,
    [string]$ToolsRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $env:LOCALAPPDATA 'GitSvnShuttle\TestWorkspace'
}
if ([string]::IsNullOrWhiteSpace($ToolsRoot)) {
    $ToolsRoot = Join-Path $repoRoot '.test-tools'
}

$WorkspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot)
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$seedRoot = Join-Path $PSScriptRoot 'seed'
$svnRepository = Join-Path $WorkspaceRoot 'svn-repository'
$solutionRoot = Join-Path $WorkspaceRoot 'ShuttleDemo'
$externalRoot = Join-Path $solutionRoot 'Externals\Common'
$writerRoot = Join-Path $WorkspaceRoot 'svn-writer'
$stagedSeedRoot = Join-Path $WorkspaceRoot 'seed'
$settingsPath = Join-Path $WorkspaceRoot 'test-environment.json'

function Remove-VerifiedDirectory([string]$Path, [string]$AllowedRoot) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the test root: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = $repoRoot) {
    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
    if ($exitCode -ne 0) {
        throw "Command failed ($exitCode): $FilePath $($Arguments -join ' ')"
    }
}

function Find-SubversionTools([string]$Root) {
    $admin = Get-ChildItem -LiteralPath $Root -Recurse -Filter svnadmin.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $client = Get-ChildItem -LiteralPath $Root -Recurse -Filter svn.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $server = Get-ChildItem -LiteralPath $Root -Recurse -Filter svnserve.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $admin -or $null -eq $client -or $null -eq $server) {
        return $null
    }
    return [pscustomobject]@{ SvnAdmin = $admin.FullName; Svn = $client.FullName; SvnServe = $server.FullName }
}

function Stop-PreviousServer([string]$SettingsFile) {
    if (-not (Test-Path -LiteralPath $SettingsFile)) {
        return
    }
    $previous = Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json
    if ($null -eq $previous.svnservePid) {
        return
    }
    $process = Get-Process -Id ([int]$previous.svnservePid) -ErrorAction SilentlyContinue
    if ($null -ne $process -and $process.Path -eq $previous.svnserve) {
        Stop-Process -Id $process.Id -Force
    }
}

if ((Test-Path -LiteralPath $WorkspaceRoot) -and -not $Reset) {
    throw "Test workspace already exists. Run .\test-env\setup.ps1 -Reset to recreate it."
}

if ($Reset -and (Test-Path -LiteralPath $WorkspaceRoot)) {
    Stop-PreviousServer $settingsPath
    $workspaceParent = Split-Path $WorkspaceRoot -Parent
    Remove-VerifiedDirectory -Path $WorkspaceRoot -AllowedRoot $workspaceParent
}

$git = (Get-Command git.exe -ErrorAction Stop).Source
Invoke-Checked $git @('svn', '--version')

New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
$svnTools = Find-SubversionTools $ToolsRoot
if ($null -eq $svnTools) {
    $archive = Join-Path $ToolsRoot 'Apache-Subversion-1.14.5-4.zip'
    $extractRoot = Join-Path $ToolsRoot 'subversion'
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host 'Downloading the standalone VisualSVN Subversion command-line tools...'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing `
            -Uri 'https://www.visualsvn.com/files/Apache-Subversion-1.14.5-4.zip' `
            -OutFile $archive
    }
    if (-not (Test-Path -LiteralPath $extractRoot)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    }
    $svnTools = Find-SubversionTools $extractRoot
}

if ($null -eq $svnTools) {
    throw 'svn.exe and svnadmin.exe were not found after extracting the command-line tools.'
}

New-Item -ItemType Directory -Path $WorkspaceRoot -Force | Out-Null
Copy-Item -LiteralPath $seedRoot -Destination $stagedSeedRoot -Recurse
Invoke-Checked $svnTools.SvnAdmin @('create', $svnRepository)
$svnServeConfiguration = Join-Path $svnRepository 'conf\svnserve.conf'
@"
[general]
anon-access = write
auth-access = write
"@ | Set-Content -LiteralPath $svnServeConfiguration -Encoding ASCII

$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$svnServePort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()

$svnServeProcess = Start-Process -FilePath $svnTools.SvnServe `
    -ArgumentList @('--daemon', '--foreground', '--root', $WorkspaceRoot, '--listen-host', '127.0.0.1', '--listen-port', $svnServePort) `
    -PassThru -WindowStyle Hidden
$repositoryUrl = "svn://127.0.0.1:$svnServePort/svn-repository"

$serverReady = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $svnTools.Svn info $repositoryUrl *> $null
        $infoExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
    if ($infoExitCode -eq 0) {
        $serverReady = $true
        break
    }
    Start-Sleep -Milliseconds 100
}
if (-not $serverReady) {
    Stop-Process -Id $svnServeProcess.Id -Force -ErrorAction SilentlyContinue
    throw 'Local svnserve did not become ready.'
}

Invoke-Checked $svnTools.Svn @('import', (Join-Path $stagedSeedRoot 'main'), "$repositoryUrl/main", '-m', 'Create main project')
Invoke-Checked $svnTools.Svn @('import', (Join-Path $stagedSeedRoot 'external'), "$repositoryUrl/external", '-m', 'Create external project')

Invoke-Checked $git @('svn', 'clone', "$repositoryUrl/main/trunk", $solutionRoot)
New-Item -ItemType Directory -Path (Split-Path $externalRoot -Parent) -Force | Out-Null
Invoke-Checked $git @('svn', 'clone', "$repositoryUrl/external/trunk", $externalRoot)

foreach ($workingCopy in @($solutionRoot, $externalRoot)) {
    Invoke-Checked $git @('-C', $workingCopy, 'config', 'user.name', 'Shuttle Tester')
    Invoke-Checked $git @('-C', $workingCopy, 'config', 'user.email', 'shuttle@example.invalid')
}

Add-Content -LiteralPath (Join-Path $solutionRoot 'ShuttleDemo.Core\Shuttle.cs') `
    -Value "`r`n// Local Git commit waiting for dcommit."
Invoke-Checked $git @('-C', $solutionRoot, 'add', 'ShuttleDemo.Core/Shuttle.cs')
Invoke-Checked $git @('-C', $solutionRoot, 'commit', '-m', 'Update shuttle locally')

Add-Content -LiteralPath (Join-Path $externalRoot 'Common.cs') `
    -Value "`r`n// Local external commit waiting for dcommit."
Invoke-Checked $git @('-C', $externalRoot, 'add', 'Common.cs')
Invoke-Checked $git @('-C', $externalRoot, 'commit', '-m', 'Update external locally')

$mainWriter = Join-Path $writerRoot 'main'
$externalWriter = Join-Path $writerRoot 'external'
Invoke-Checked $svnTools.Svn @('checkout', "$repositoryUrl/main/trunk", $mainWriter)
Invoke-Checked $svnTools.Svn @('checkout', "$repositoryUrl/external/trunk", $externalWriter)

Set-Content -LiteralPath (Join-Path $mainWriter 'RemoteMain.txt') -Value 'A newer SVN revision for rebase testing.'
Invoke-Checked $svnTools.Svn @('add', (Join-Path $mainWriter 'RemoteMain.txt'))
Invoke-Checked $svnTools.Svn @('commit', $mainWriter, '-m', 'Remote main change')

Set-Content -LiteralPath (Join-Path $externalWriter 'RemoteCommon.txt') -Value 'A newer external SVN revision for rebase testing.'
Invoke-Checked $svnTools.Svn @('add', (Join-Path $externalWriter 'RemoteCommon.txt'))
Invoke-Checked $svnTools.Svn @('commit', $externalWriter, '-m', 'Remote external change')

[ordered]@{
    repositoryUrl = $repositoryUrl
    solutionRoot = $solutionRoot
    solutionFile = (Join-Path $solutionRoot 'ShuttleDemo.sln')
    externalRoot = $externalRoot
    git = $git
    svn = $svnTools.Svn
    svnadmin = $svnTools.SvnAdmin
    svnserve = $svnTools.SvnServe
    svnservePid = $svnServeProcess.Id
    svnservePort = $svnServePort
} | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

Write-Host ''
Write-Host 'Git-SVN Shuttle test environment is ready.'
Write-Host "Solution: $solutionRoot\ShuttleDemo.sln"
Write-Host 'Expected initial state: 2 Git-SVN repositories, 1 pending commit each, and newer SVN revisions ready for rebase.'
