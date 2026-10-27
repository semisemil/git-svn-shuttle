param(
    [Parameter(Mandatory = $true)][string]$GitRoot,
    [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$runtimePath = (Resolve-Path -LiteralPath $GitRoot).Path
$scriptPath = Join-Path $runtimePath 'mingw64/libexec/git-core/git-svn'
$gitPath = Join-Path $runtimePath 'cmd/git.exe'
$backupPath = $scriptPath + '.shuttle-original'
$originalHash = '1A023DF993C6122F62A1800EB0E28DAFFE81DB534884F9EF6E037C0B7BE7DF1C'
$optimizedHash = 'A922CBE3603C602271041B689ADCF56478E45AD605DBED36171221855ABE6677'

if (!(Test-Path -LiteralPath $gitPath -PathType Leaf)) { throw 'Expected a Git for Windows runtime with cmd/git.exe.' }
$currentHash = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash
if ($currentHash -notin @($originalHash, $optimizedHash)) {
    throw 'This Git-SVN script differs from the verified Git for Windows 2.51.2 source. No files were changed.'
}
if ((Test-Path -LiteralPath $backupPath) -and
    (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $originalHash) {
    throw 'The existing Git-SVN backup differs from the verified original. No files were changed.'
}

$targetHash = if ($Restore) { $originalHash } else { $optimizedHash }
if ($currentHash -ne $targetHash) {
    if ($Restore) {
        $replacement = [IO.File]::ReadAllBytes($backupPath)
    }
    else {
        $encoding = [Text.UTF8Encoding]::new($false)
        $sourceBytes = [IO.File]::ReadAllBytes($scriptPath)
        $source = $encoding.GetString($sourceBytes)
        $before = @'
sub read_git_config {
	my $opts = shift;
	my @config_only;
'@
        $after = @'
sub read_git_config {
	my $opts = shift;
	my @config_only;
	chomp(my @configured = `git config --name-only --get-regexp '^svn\\.' 2>/dev/null`);
	my $names_known = $? == 0 || $? == 256;
	my %configured = map { lc($_) => 1 } @configured;
'@
        $keyLine = "`t`t`$key =~ s/-//g;"
        $skipLine = @'
		next if $names_known && !$configured{'svn.' . lc($key)};
'@
        $source = $source.Replace($before.Replace("`r`n", "`n"), $after.Replace("`r`n", "`n"))
        $source = $source.Replace($keyLine, $keyLine + "`n" + $skipLine.Replace("`r`n", "`n"))
        $replacement = $encoding.GetBytes($source)
    }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $replacementHash = [BitConverter]::ToString($algorithm.ComputeHash($replacement)).Replace('-', '') }
    finally { $algorithm.Dispose() }
    if ($replacementHash -ne $targetHash) { throw 'The replacement did not match the verified Git-SVN source.' }
    if (!$Restore -and !(Test-Path -LiteralPath $backupPath)) { [IO.File]::WriteAllBytes($backupPath, $sourceBytes) }
    $temporaryPath = $scriptPath + '.shuttle-write-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $replacement)
        Move-Item -LiteralPath $temporaryPath -Destination $scriptPath -Force
    }
    finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath } }
}

[pscustomobject]@{ Enabled = !$Restore; GitExecutable = $gitPath; OriginalBackup = $backupPath }
