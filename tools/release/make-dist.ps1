# Builds the shareable mod tool zips into dist\ - no game data, nothing that identifies whoever built them:
#   AbsolverModTool-win-x64-<ver>.zip              one exe (self-contained: no .NET install needed)  + README + notices
#   AbsolverModTool-win-x64-needs-dotnet8-<ver>.zip   the same exe without the .NET runtime inside (tiny; needs the .NET 8 Desktop Runtime)
# Both are a single AbsolverModTool.Gui.exe: the 3D viewer page and all libraries are compiled/bundled into it. Each zip is scanned for
# personal data and deleted if anything is found.
#
#   powershell -ExecutionPolicy Bypass -File tools\release\make-dist.ps1
param([string]$Version = (Get-Date -Format 'yyyyMMdd'))
$ErrorActionPreference = 'Stop'
$repo  = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dist  = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'

function CopyLic($lic, $pkg, $file, $name) {
    $d = Get-ChildItem (Join-Path $nuget $pkg) -Directory -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if ($d -and (Test-Path (Join-Path $d.FullName $file))) { Copy-Item (Join-Path $d.FullName $file) (Join-Path $lic $name) } else { Write-Warning "license file $pkg/$file not found" }
}

function Build-Variant([string]$suffix, [bool]$selfContained) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) "AbsolverModToolDist_$([guid]::NewGuid().ToString('N'))"
    $app   = Join-Path $stage 'AbsolverModTool'
    New-Item -ItemType Directory -Force $app | Out-Null

    Write-Host "publishing $suffix ..."
    # DebugType none = no .pdb (they carry source paths); PathMap hides the checkout path inside the assemblies.
    $args = @('publish', (Join-Path $repo 'src\AbsolverModTool.Gui'), '-c', 'Release', '-r', 'win-x64', '-o', $app,
              '--self-contained', $selfContained.ToString().ToLower(),
              '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:EnableCompressionInSingleFile=$($selfContained.ToString().ToLower())",
              '-p:DebugType=none', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=en', '-p:GenerateDocumentationFile=false', "-p:PathMap=$repo=/src")
    & dotnet @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }

    # keep only the exe: drop loose docs/symbols that rode along from NuGet packages
    Get-ChildItem $app -File | Where-Object { $_.Name -ne 'AbsolverModTool.Gui.exe' } | Remove-Item -Force
    Get-ChildItem $app -Directory | Remove-Item -Recurse -Force

    Copy-Item (Join-Path $PSScriptRoot 'README.txt'), (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') $app
    if (-not $selfContained) {
        Add-Content (Join-Path $app 'README.txt') "`r`nThis is the small build: it needs the .NET 8 Desktop Runtime (https://dotnet.microsoft.com/download/dotnet/8.0). The other zip has it built in."
    }
    $lic = Join-Path $app 'licenses'; New-Item -ItemType Directory -Force $lic | Out-Null
    Copy-Item (Join-Path $repo 'viewer\LICENSE-SifuMovesetMaker.txt') $lic
    CopyLic $lic 'uassetapi' 'LICENSE' 'UAssetAPI-LICENSE.txt'
    CopyLic $lic 'skiasharp' 'LICENSE.txt' 'SkiaSharp-LICENSE.txt'
    CopyLic $lic 'microsoft.web.webview2' 'LICENSE.txt' 'WebView2-LICENSE.txt'
    CopyLic $lic 'microsoft.web.webview2' 'NOTICE.txt' 'WebView2-NOTICE.txt'

    $zip = Join-Path $dist "AbsolverModTool-win-x64$suffix-$Version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("zip: {0} ({1:N1} MB, exe {2:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB), ((Get-Item (Join-Path $app 'AbsolverModTool.Gui.exe')).Length / 1MB))

    Write-Host "scanning for anything personal..."
    python (Join-Path $PSScriptRoot 'scan_personal.py') $zip
    $scan = $LASTEXITCODE
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    if ($scan -ne 0) { Remove-Item $zip -Force; throw "personal data found in $zip - deleted" }
    return $zip
}

$a = Build-Variant '' $true
$b = Build-Variant '-needs-dotnet8' $false
Write-Host "done:`n  $a`n  $b"
