# Build the DSH Windows launcher from source.
#   .\build.ps1              build into .\dist
#   .\build.ps1 -Install     also install to %LOCALAPPDATA%\DSH and make shortcuts
[CmdletBinding()]
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# csc from the .NET Framework that ships with Windows: no SDK, no downloads.
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found; Windows/.NET Framework 4.x is required.' }

Write-Host 'building make-icon.exe ...'
& $csc /nologo /target:exe /optimize+ "/out:$dist\make-icon.exe" `
    /reference:System.Drawing.dll (Join-Path $root 'src\MakeIcon.cs')
if ($LASTEXITCODE -ne 0) { throw 'make-icon build failed' }

Write-Host 'generating DSH.ico + DSH-dim.ico ...'
& "$dist\make-icon.exe" (Join-Path $root 'assets\favicon.svg') "$dist\DSH.ico" "$dist\DSH-dim.ico"
if ($LASTEXITCODE -ne 0) { throw 'icon generation failed' }

Write-Host 'building DSH.exe ...'
& $csc /nologo /target:winexe /optimize+ "/out:$dist\DSH.exe" "/win32icon:$dist\DSH.ico" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    (Join-Path $root 'src\DSH.cs')
if ($LASTEXITCODE -ne 0) { throw 'launcher build failed' }

Write-Host "built:"; Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'DSH'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item "$dist\DSH.exe" $target -Force
    Copy-Item "$dist\DSH.ico" $target -Force
    # The faint twin the launcher blinks while the server starts.
    Copy-Item "$dist\DSH-dim.ico" $target -Force
    $exe = Join-Path $target 'DSH.exe'
    $ico = Join-Path $target 'DSH.ico'
    $startDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($spec in @(@{p = Join-Path $startDir 'DSH.lnk'; a = '' },
                        @{p = Join-Path $env:USERPROFILE 'Desktop\DSH.lnk'; a = '' },
                        @{p = Join-Path $startDir 'DSH (app window).lnk'; a = '--app' })) {
        $lnk = $shell.CreateShortcut($spec.p)
        $lnk.TargetPath = $exe
        $lnk.Arguments = $spec.a
        $lnk.WorkingDirectory = $target
        $lnk.IconLocation = "$ico,0"
        $lnk.Save()
    }
    Write-Host "installed to $target (Start Menu + Desktop shortcuts created)"
}
