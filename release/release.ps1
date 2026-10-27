# Builds a FileStar release with Velopack and (with -Publish) puts it on GitHub Releases, where installed copies
# of FileStar find it and update themselves.
#
#   1. Raise <Version> in FileStar\FileStar.csproj (e.g. 1.0.0 -> 1.0.1).
#   2. powershell -ExecutionPolicy Bypass -File release\release.ps1 -Publish
#      (needs $env:GITHUB_TOKEN: a token with "Contents: read and write" on sorrymommy/FileStar)
#
# Without -Publish it only builds, into release\out: FileStar-win-Setup.exe (the installer to hand out),
# FileStar-win-Portable.zip, and the update packages. GitHub Actions runs this same script for a "v*" tag
# (.github\workflows\release.yml).

param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'FileStar\FileStar.csproj'
$icon = Join-Path $root 'FileStar\FileStar.ico'
$appDir = Join-Path $PSScriptRoot 'build\app'
$outDir = Join-Path $PSScriptRoot 'out'
$repoUrl = 'https://github.com/sorrymommy/FileStar'
$vpkVersion = '1.2.161'   # keep in step with the Velopack package in FileStar.csproj

[xml]$csproj = Get-Content $project -Raw -Encoding UTF8
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw '<Version> is missing from FileStar.csproj' }
# A tag build must match the project's version, so the release and the app agree.
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") {
    throw "Tag $env:GITHUB_REF_NAME doesn't match <Version>$version</Version> in FileStar.csproj"
}
Write-Host "FileStar $version" -ForegroundColor Cyan

if ($Publish -and -not $env:GITHUB_TOKEN) { throw 'Set $env:GITHUB_TOKEN to publish (a GitHub token with Contents: read and write).' }

# vpk: Velopack's packaging tool.
$tools = dotnet tool list -g
if ($tools -notmatch "^vpk\s+$([regex]::Escape($vpkVersion))") {
    if ($tools -match '^vpk\s') { dotnet tool update -g vpk --version $vpkVersion } else { dotnet tool install -g vpk --version $vpkVersion }
    if ($LASTEXITCODE -ne 0) { throw 'Could not install vpk' }
}
$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"

# Self-contained, so the other PC needs no .NET runtime.
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $appDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory $outDir | Out-Null
if ($Publish) {
    # The previous release, so this one also gets a small delta update package.
    vpk download github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $outDir
    if ($LASTEXITCODE -ne 0) { Write-Warning 'No earlier release to download (first release?); continuing without a delta.' }
}

vpk pack --packId FileStar --packVersion $version --packDir $appDir --mainExe FileStar.exe `
    --packTitle FileStar --packAuthors FileStar --icon $icon -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

if ($Publish) {
    vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $outDir `
        --publish --releaseName "FileStar $version" --tag "v$version"
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }
    Write-Host "Published: $repoUrl/releases/tag/v$version" -ForegroundColor Green
}
Get-ChildItem $outDir -File | Where-Object { $_.Name -like "*$version*" -or $_.Name -like 'FileStar-win-*' } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
