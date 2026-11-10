[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolDirectory = Join-Path $root '.tools/inno-setup'
$compiler = Join-Path $toolDirectory 'compiler/ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    if ((& $compiler --version).Trim() -ne '7.1.1') { throw 'Expected Inno Setup 7.1.1.' }
    return
}
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$installer = Join-Path $toolDirectory 'innosetup-7.1.1-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0') {
    throw 'Inno Setup download hash does not match the pinned tool.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Pyrsys B.V.,*') {
    throw 'Inno Setup download signature is invalid.'
}
$destination = Join-Path $toolDirectory 'compiler'
$process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    '/CURRENTUSER', '/NOICONS', '/TASKS=""', ('/DIR="' + $destination + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw "Inno Setup installation failed: $($process.ExitCode)" }
if ((& $compiler --version).Trim() -ne '7.1.1') { throw 'Expected Inno Setup 7.1.1.' }
