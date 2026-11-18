param([Parameter(Mandatory=$true)][string]$TokenFile)
$ErrorActionPreference='Stop'
# Never rotate an existing key during a restart/update.
if (Test-Path -LiteralPath $TokenFile -PathType Leaf) {
    if ([IO.File]::ReadAllText($TokenFile).Trim().Length -lt 32) { throw "Existing receiver key file is invalid: $TokenFile. Restore a valid key or deliberately remove the invalid file before initializing a new key. It was not overwritten." }
    return
}
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($TokenFile))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
# Protect the empty file before writing the secret; keep the launching account able to read it.
New-Item -ItemType File -Path $TokenFile -ErrorAction Stop | Out-Null
$currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $TokenFile /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "*$($currentSid):F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not protect receiver key file: $TokenFile. No secret was written." }
$bytes=New-Object byte[] 32
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
[IO.File]::WriteAllText($TokenFile,[Convert]::ToBase64String($bytes),(New-Object Text.UTF8Encoding($false)))
Write-Host "Created protected receiver key file: $TokenFile"
