$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

$destination = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$executable = Join-Path $destination 'LogiBattery.exe'
$icon = Join-Path $PSScriptRoot 'app.ico'
$sources = 'Battery.cs', 'Hid.cs', 'Inventory.cs', 'Panel.cs', 'Program.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }

$options = '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warnaserror+', '/codepage:65001'
& $compiler @options "/win32icon:$icon" "/out:$executable" @sources
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}
Write-Output $executable
