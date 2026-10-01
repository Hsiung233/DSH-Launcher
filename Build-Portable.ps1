#Requires -Version 7.0
<#
.SYNOPSIS
    把发布目录压缩成 Portable 绿色版压缩包,产物落在仓库根的 build\DSHLauncher-Portable-Win-x64.zip。

.DESCRIPTION
    绿色版与安装包同源:默认先调用根目录的 Build-Publish.ps1 发布(框架依赖版,目标机器
    需已安装 .NET 10 Desktop Runtime),再把发布目录整体压缩成 zip;-NoPublish 则直接
    压缩当前发布目录(例如刚跑完 Build-Installer.ps1 之后接着打绿色版)。

    发布目录位置从 win_x64.pubxml 的 <PublishDir> 读出,与 Build-Publish.ps1 的解析规则
    一致,不会和发布配置漂移。zip 内不含 .pdb(即使发布目录里残留了,也会被剔除)。

.EXAMPLE
    .\Build-Portable.ps1

.EXAMPLE
    .\Build-Portable.ps1 -NoPublish

.EXAMPLE
    .\Build-Portable.ps1 -KeepRunning
#>
[CmdletBinding()]
param(
    # 直接压缩当前的发布目录,不重新发布
    [switch] $NoPublish,

    # 转发给 Build-Publish.ps1:发布时不结束正在运行的实例
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appExeName = 'DSH Launcher.exe'
$zipName = 'DSHLauncher-Portable-Win-x64.zip'

# 脚本在仓库根目录
$repoRoot = $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\DSH Launcher'
$publishScript = Join-Path $repoRoot 'Build-Publish.ps1'
$publishProfilePath = Join-Path $projectDir 'Properties\PublishProfiles\win_x64.pubxml'
$outputDir = Join-Path $repoRoot 'build'
$zipPath = Join-Path $outputDir $zipName

<#
    取 .pubxml 里声明的 PublishDir;相对路径由 MSBuild 按【项目目录】解析。
    与 Build-Publish.ps1 里的同名函数保持一致 —— 那边改了这边也要跟着改。
#>
function Get-PublishDirFromProfile {
    param([Parameter(Mandatory)] [string] $Path)

    # TrimStart 去掉可能的 UTF-8 BOM,否则 [xml] 转换会报 "Data at the root level is invalid"
    $raw = (Get-Content -LiteralPath $Path -Raw).TrimStart([char] 0xFEFF)

    [xml] $profile = $raw
    $declared = $profile.SelectNodes('//PublishDir') |
        Where-Object { $_.InnerText.Trim() } |
        Select-Object -First 1

    if ($null -eq $declared) { return "bin\Publish\win_x64" }
    return $declared.InnerText.Trim()
}

function Resolve-PublishDir {
    param(
        [Parameter(Mandatory)] [string] $ProjectDir,
        [Parameter(Mandatory)] [string] $RelativeOrAbsolute
    )

    $full = if ([IO.Path]::IsPathRooted($RelativeOrAbsolute)) {
        $RelativeOrAbsolute
    } else {
        Join-Path $ProjectDir $RelativeOrAbsolute
    }
    return [IO.Path]::GetFullPath($full).TrimEnd('\')
}

# --- 发布 ---------------------------------------------------------------------

if ($NoPublish) {
    Write-Host '跳过发布(-NoPublish)'
} else {
    $publishParams = @{}
    if ($KeepRunning) { $publishParams['KeepRunning'] = $true }
    & $publishScript @publishParams
}

if (-not (Test-Path -LiteralPath $publishProfilePath)) {
    throw "找不到发布配置: $publishProfilePath"
}
$publishDir = Resolve-PublishDir -ProjectDir $projectDir -RelativeOrAbsolute (Get-PublishDirFromProfile -Path $publishProfilePath)

# 发布目录里必须有主程序,否则压出来的 zip 是坏的
$appExe = Join-Path $publishDir $appExeName
if (-not (Test-Path -LiteralPath $appExe)) {
    throw @"
打包源里找不到 $appExeName(完整路径: $appExe)
发布目录和 win_x64.pubxml 的 PublishDir 对不上。请先运行 .\Build-Publish.ps1。
"@
}

Write-Host '打包绿色版压缩包'
Write-Host "  打包源    : $publishDir"
Write-Host "  产物      : $zipPath"

# --- 压缩 ---------------------------------------------------------------------

# 产物目录不进版本库(.gitignore 的 /build),全新克隆里它不存在 —— 自己建好
if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

# Compress-Archive 不支持排除,先枚举条目再传进去;.pdb 不进绿色版
$items = @(
    Get-ChildItem -LiteralPath $publishDir -Force -File |
        Where-Object { $_.Name -notlike '*.pdb' } |
        Select-Object -ExpandProperty FullName
)
$directories = @(
    Get-ChildItem -LiteralPath $publishDir -Force -Directory |
        Select-Object -ExpandProperty FullName
)
if ($items.Count -eq 0 -and $directories.Count -eq 0) {
    throw "发布目录是空的: $publishDir"
}
# 目录和文件一起传给 Compress-Archive(它不支持排除,所以上面已先枚举)
$paths = @($directories) + @($items)
Compress-Archive -Path $paths -DestinationPath $zipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "压缩已执行,但没有生成 $zipPath。"
}

$zipItem = Get-Item -LiteralPath $zipPath
$sizeMb = [math]::Round($zipItem.Length / 1MB, 2)
$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$exeVersionInfo = (Get-Item -LiteralPath $appExe).VersionInfo
$shownVersion = if ($exeVersionInfo -and $exeVersionInfo.ProductVersion -and $exeVersionInfo.ProductVersion.Trim()) {
    $exeVersionInfo.ProductVersion.Trim()
} else {
    '(未写入版本信息)'
}

Write-Host ''
Write-Host '绿色版压缩包构建完成' -ForegroundColor Green
Write-Host "  压缩包    : $zipPath"
Write-Host "  大小      : $sizeMb MB"
Write-Host "  版本      : $shownVersion"
Write-Host "  SHA256    : $sha256"
Write-Host '  (框架依赖版:目标机器需已安装 .NET 10 Desktop Runtime)'
