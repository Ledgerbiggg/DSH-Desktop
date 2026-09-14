# ============================================================
# release.ps1 — 一键发布：升版本 + 写更新说明 + 提交 + 推送
# 用法: .\scripts\release.ps1 -Notes "更新内容" [-Part patch|minor|major]
# ============================================================
param(
    [Parameter(Mandatory=$true)]
    [string]$Notes,
    [ValidateSet('patch','minor','major')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

# 1. 升版本号
Write-Host "[1/3] Bumping version ($Part)..." -ForegroundColor Cyan
$bumpScript = Join-Path $root 'scripts\bump_version.ps1'
& $bumpScript -Part $Part | Out-Null

# 重新读取版本号（显式 UTF-8：csproj 含中文注释）
$csprojPath = Join-Path $root 'Dsh\Dsh.csproj'
$csproj = Get-Content $csprojPath -Raw -Encoding UTF8
if ($csproj -match '<Version>([^<]+)</Version>') {
    $newVer = $matches[1].Trim()
} else {
    Write-Host "ERROR: version not found after bump" -ForegroundColor Red
    exit 1
}
Write-Host "  Version: $newVer" -ForegroundColor Green

# 2. 更新 version.json 的 notes 字段
Write-Host "[2/3] Writing release notes..." -ForegroundColor Cyan
$versionJsonPath = Join-Path $root 'version.json'
# 显式 UTF-8 读取：默认编码会把中文 notes 读成乱码再写回，污染更新说明
$vj = Get-Content $versionJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$vj.notes = $Notes
$json = $vj | ConvertTo-Json -Depth 10
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($versionJsonPath, $json, $utf8NoBom)

# 3. Git 提交并推送
Write-Host "[3/3] Committing and pushing..." -ForegroundColor Cyan
Set-Location $root
git add -A
git commit -m "release: bump to $newVer"
git push origin main

Write-Host ""
Write-Host "Done! Version $newVer pushed to main." -ForegroundColor Green
Write-Host "GitHub Actions will build & publish the installer automatically." -ForegroundColor Yellow
Write-Host "  -> https://github.com/Ledgerbiggg/DSH-Desktop/actions" -ForegroundColor DarkGray
Write-Host "  -> Release will appear at https://github.com/Ledgerbiggg/DSH-Desktop/releases" -ForegroundColor DarkGray
