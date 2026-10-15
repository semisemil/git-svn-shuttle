[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$VsixPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload = (Resolve-Path -LiteralPath $VsixPath).Path
$publishManifestPath = Join-Path $repoRoot 'marketplace/publishManifest.json'
$publishManifest = Get-Content -LiteralPath $publishManifestPath -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'src/GitSvnShuttle.Vsix/GitSvnShuttle.Vsix.csproj') -Raw
[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'src/GitSvnShuttle.Vsix/source.extension.vsixmanifest') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$identity = $sourceManifest.PackageManifest.Metadata.Identity
$extensionName = "$($publishManifest.publisher).$($publishManifest.identity.internalName)"
$extensionUrl = "https://marketplace.visualstudio.com/items?itemName=$extensionName"
if ($identity.Version -cne $version -or $identity.Publisher -cne $publishManifest.publisher) {
    throw 'The project version, VSIX version, and Marketplace publisher must match.'
}
if ((Get-Content -LiteralPath (Join-Path $repoRoot 'RELEASE_NOTES.txt') -TotalCount 1) -cne "Git-SVN Shuttle $version") {
    throw 'Release notes must start with the version being published.'
}

$archive = [IO.Compression.ZipFile]::OpenRead($payload)
try {
    $entry = $archive.GetEntry('extension.vsixmanifest')
    if ($null -eq $entry) { throw 'The payload has no extension.vsixmanifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$packagedManifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $packagedIdentity = $packagedManifest.PackageManifest.Metadata.Identity
    if ($packagedIdentity.Version -cne $version -or
        $packagedIdentity.Id -cne $identity.Id -or
        $packagedIdentity.Publisher -cne $identity.Publisher) {
        throw 'The tested VSIX payload does not match the checkout version and identity.'
    }
}
finally { $archive.Dispose() }

function Get-PublishedVersions {
    $query = @{
        filters = @(@{
            criteria = @(@{ filterType = 7; value = $extensionName })
            pageNumber = 1
            pageSize = 1
        })
        flags = 1
    } | ConvertTo-Json -Depth 6
    $response = Invoke-RestMethod -Method Post -ContentType 'application/json' `
        -Uri 'https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery?api-version=7.1-preview.1' `
        -Body $query -TimeoutSec 30
    $extensions = @($response.results | ForEach-Object { $_.extensions } | Where-Object {
        "$($_.publisher.publisherName).$($_.extensionName)" -ceq $extensionName
    })
    if ($extensions.Count -ne 1) { throw "The existing Marketplace listing $extensionName could not be identified." }
    @($extensions[0].versions | ForEach-Object { [version]$_.version })
}

$publishedVersions = @(Get-PublishedVersions)
if ($publishedVersions.Count -eq 0) { throw 'The Marketplace listing returned no published versions.' }
$latest = $publishedVersions | Sort-Object -Descending | Select-Object -First 1
if ([version]$version -le $latest) {
    throw "Prepared version $version must be newer than the published version $latest."
}
Write-Host "MARKETPLACE_CURRENT_VERSION=$latest"
Write-Host "MARKETPLACE_PREPARED_VERSION=$version"
Write-Host "VSIX_SHA256=$((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash)"
Write-Host "MARKETPLACE_URL=$extensionUrl"

if ($ValidateOnly) {
    Write-Host 'MARKETPLACE_PREFLIGHT=PASS'
    exit 0
}
if ([string]::IsNullOrWhiteSpace($env:VS_MARKETPLACE_TOKEN)) {
    throw 'Repository secret VS_MARKETPLACE_TOKEN is required for Marketplace publishing.'
}
$buildToolsVersion = [string]$project.SelectSingleNode(
    "/Project/ItemGroup/PackageReference[@Include='Microsoft.VSSDK.BuildTools']").Version
$packageRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $env:USERPROFILE '.nuget/packages'
} else { $env:NUGET_PACKAGES }
$publisher = Join-Path $packageRoot "microsoft.vssdk.buildtools/$buildToolsVersion/tools/vssdk/bin/VsixPublisher.exe"
if (-not (Test-Path -LiteralPath $publisher)) {
    throw 'VsixPublisher.exe is missing. Restore the VSIX project on Windows first.'
}

Push-Location (Join-Path $repoRoot 'marketplace')
try {
    & $publisher publish -payload $payload -publishManifest $publishManifestPath `
        -personalAccessToken $env:VS_MARKETPLACE_TOKEN
    if ($LASTEXITCODE -ne 0) { throw "Marketplace upload failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

for ($attempt = 0; $attempt -lt 24; $attempt++) {
    if (@(Get-PublishedVersions) -contains [version]$version) {
        Write-Host "MARKETPLACE_VERIFIED_VERSION=$version"
        if ($env:GITHUB_STEP_SUMMARY) {
            "Published and verified [$extensionName $version]($extensionUrl)." |
                Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
        }
        exit 0
    }
    Start-Sleep -Seconds 5
}
throw 'Upload succeeded, but the new version is not yet visible in the public Marketplace query. Check publication status before retrying an upload.'
