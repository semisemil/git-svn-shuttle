[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) (
    'GitSvnShuttle-AcpIsolation-' + [Guid]::NewGuid().ToString('N'))))
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$repository = Join-Path $testRoot 'repository'
$probeProject = Join-Path $PSScriptRoot 'GitSvnShuttle.AcpProbe\GitSvnShuttle.AcpProbe.csproj'
$gitPath = (Get-Command git.exe -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source
$author = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('7ZmN6ri464+Z'))
$subject = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('7ZWc6riAIOy7pOuwiyDsoJzrqqk='))
$baselineSubject = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('U1ZOIOq4sOykgA=='))
$inheritedPath = $env:Path
[Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
[Environment]::SetEnvironmentVariable('Path', $null, 'Process')
[Environment]::SetEnvironmentVariable('Path', $inheritedPath, 'Process')

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments
    )

    $commandOutput = & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    $commandOutput | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

function Build-Probe {
    param(
        [Parameter(Mandatory = $true)][string] $Locale
    )

    $artifacts = Join-Path $testRoot ('artifacts-' + $Locale)
    Invoke-Checked 'dotnet.exe' @(
        'restore', $probeProject,
        '--artifacts-path', $artifacts,
        '--disable-build-servers',
        ('-p:AcpLocale=' + $Locale))
    Invoke-Checked 'dotnet.exe' @(
        'build', $probeProject,
        '-c', 'Release',
        '--no-restore',
        '--artifacts-path', $artifacts,
        '--disable-build-servers',
        ('-p:AcpLocale=' + $Locale),
        '-v', 'minimal')

    $probe = Get-ChildItem -LiteralPath $artifacts -Recurse -Filter 'GitSvnShuttle.AcpProbe.exe' |
        Select-Object -First 1
    if ($null -eq $probe) {
        throw "ACP probe executable was not produced for $Locale."
    }

    return $probe.FullName
}

function Run-Probe {
    param(
        [Parameter(Mandatory = $true)][string] $ProbePath,
        [Parameter(Mandatory = $true)][int] $ExpectedAcp
    )

    $output = & $ProbePath $gitPath $repository $author $subject $ExpectedAcp
    if ($LASTEXITCODE -ne 0) {
        throw "ACP probe failed with exit code $LASTEXITCODE at expected ACP $ExpectedAcp."
    }

    return ConvertFrom-StringData ($output -join [Environment]::NewLine)
}

try {
    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    Invoke-Checked $gitPath @('-C', $repository, 'init')
    Invoke-Checked $gitPath @('-C', $repository, 'config', 'user.name', $author)
    Invoke-Checked $gitPath @('-C', $repository, 'config', 'user.email', 'hong@example.test')
    Invoke-Checked $gitPath @(
        '-C', $repository, 'commit', '--allow-empty',
        '-m', $baselineSubject,
        '-m', 'git-svn-id: https://svn.example.test/demo/trunk@1 00000000-0000-0000-0000-000000000000')
    Invoke-Checked $gitPath @(
        '-C', $repository, 'commit', '--allow-empty', '-m', $subject)

    $koProbe = Build-Probe 'ko-KR'
    $enProbe = Build-Probe 'en-US'
    $koResult = Run-Probe $koProbe 949
    $enResult = Run-Probe $enProbe 1252

    if ($koResult.AUTHOR_UTF8_BASE64 -ne $enResult.AUTHOR_UTF8_BASE64 -or
        $koResult.SUBJECT_UTF8_BASE64 -ne $enResult.SUBJECT_UTF8_BASE64) {
        throw 'The isolated ACP probes returned different UTF-8 commit metadata.'
    }

    Write-Output 'ACP_ISOLATION=PASS'
    Write-Output ('ko-KR_ACP=' + $koResult.ACP)
    Write-Output ('en-US_ACP=' + $enResult.ACP)
    Write-Output ('AUTHOR_UTF8_BASE64=' + $koResult.AUTHOR_UTF8_BASE64)
    Write-Output ('SUBJECT_UTF8_BASE64=' + $koResult.SUBJECT_UTF8_BASE64)
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTarget = [System.IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTarget.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([System.IO.Path]::GetFileName($resolvedTarget)).StartsWith(
                'GitSvnShuttle-AcpIsolation-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected test path: $resolvedTarget"
        }

        Get-ChildItem -LiteralPath $resolvedTarget -Recurse -Force -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = [System.IO.FileAttributes]::Normal }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
