#Requires -Version 7.0
<#
.SYNOPSIS
    把 DSH Launcher 发布到 bin\Publish(供 Build-Installer.ps1 打包)。.DESCRIPTION
    使用 Properties\PublishProfiles 下的发布配置(默认 DSH Launcher_Windows_x64,框架依赖版)。
    实际输出目录从 .pubxml 的 <PublishDir> 读出,以保证与 installer.iss 的 MyPublishDir 一致。

    默认会做两件"破坏性"的事,都是为了产出干净、可打包的发布目录:

      1. 结束正在运行的 DSH Launcher。应用是常驻托盘的"单实例"程序,如果它正从发布目录
         运行,exe/dll 被占用会让发布失败。
      2. 清空发布目录。dotnet publish 是增量写入,不会删除旧产物 —— 例如本次改动之前
         随应用部署的 Assets\logo.ico / logo-512.png,或没有 DebugType=embedded 时
         留下的 .pdb,都会一直留在目录里并被 installer.iss 一起打进安装包。

    两者都可以关掉:-KeepRunning、-NoClean。

.EXAMPLE
    .\Build-Publish.ps1

.EXAMPLE
    .\Build-Publish.ps1 -NoClean -KeepRunning
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $PublishProfile = 'DSH Launcher_Windows_x64',

    # 保留发布目录中已有的文件(默认先清空)
    [switch] $NoClean,

    # 即使检测到 DSH Launcher 正在运行也不结束它(发布目录被占用时发布自身会失败)
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appExeName = 'DSH Launcher.exe'

# 脚本在仓库根目录
$repoRoot    = $PSScriptRoot
$projectDir  = Join-Path $repoRoot 'DSH Launcher'
$csproj      = Join-Path $projectDir 'DSH Launcher.csproj'
$profilePath = Join-Path $projectDir "Properties\PublishProfiles\$PublishProfile.pubxml"

<#
    取 .pubxml 里声明的 PublishDir。相对路径由 MSBuild 按【项目目录】解析
    (所以 bin\Publish\x 指的是 DSH Launcher\bin\Publish\x)。
#>
function Get-PublishDirFromProfile {
    param([Parameter(Mandatory)] [string] $Path)

    # TrimStart 去掉可能的 UTF-8 BOM,否则 [xml] 转换会报 "Data at the root level is invalid"
    $raw = (Get-Content -LiteralPath $Path -Raw).TrimStart([char] 0xFEFF)

    [xml] $profile = $raw
    $declared = $profile.SelectNodes('//PublishDir') |
        Where-Object { $_.InnerText.Trim() } |
        Select-Object -First 1

    if ($null -eq $declared) { return $null }
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

# --- 前置检查 -----------------------------------------------------------------

if (-not (Test-Path -LiteralPath $csproj)) {
    throw "找不到项目文件: $csproj"
}
if (-not (Test-Path -LiteralPath $profilePath)) {
    $available = @(Get-ChildItem -LiteralPath (Split-Path -Parent $profilePath) -Filter *.pubxml -File -ErrorAction SilentlyContinue).BaseName
    throw "找不到发布配置 $PublishProfile(.pubxml)。已有配置: $($available -join ', ')"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet CLI。请安装 .NET SDK 10 并确保它在 PATH 中。'
}

$declaredPublishDir = Get-PublishDirFromProfile -Path $profilePath
if (-not $declaredPublishDir) {
    Write-Warning "$PublishProfile.pubxml 未声明 <PublishDir>,按 bin\Publish\$PublishProfile 处理。"
    $declaredPublishDir = "bin\Publish\$PublishProfile"
}
$publishDir = Resolve-PublishDir -ProjectDir $projectDir -RelativeOrAbsolute $declaredPublishDir

Write-Host '发布 DSH Launcher'
Write-Host "  项目      : $csproj"
Write-Host "  配置      : $Configuration"
Write-Host "  发布配置  : $PublishProfile"
Write-Host "  输出目录  : $publishDir"

# --- 结束正在运行的实例 -------------------------------------------------------

$running = @(Get-Process -Name 'DSH Launcher' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($KeepRunning) {
        Write-Warning "有 $($running.Count) 个 DSH Launcher 正在运行(-KeepRunning 未结束它们),文件被占用时发布可能失败。"
    } else {
        Write-Host "结束正在运行的 DSH Launcher(共 $($running.Count) 个进程)…"
        $running | Stop-Process -Force
        # 等文件句柄真正释放,否则紧接着的清理/写入可能失败
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

# --- 清理旧产物 ---------------------------------------------------------------

if (-not $NoClean -and (Test-Path -LiteralPath $publishDir)) {
    Write-Host '清空发布目录…'
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

# --- 发布 ---------------------------------------------------------------------

$dotnetArgs = @(
    'publish', $csproj
    '--configuration', $Configuration
    "-p:PublishProfile=$PublishProfile"
    '--nologo'
)

Write-Host "> dotnet $($dotnetArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败(退出码 $LASTEXITCODE)。"
}

# --- 校验产物 -----------------------------------------------------------------

$appExe = Join-Path $publishDir $appExeName
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "发布目录里没有 $appExeName,发布结果不符合预期: $publishDir"
}

# 以下两种情况说明发布配置/项目文件被改坏了,先警告,不直接失败
$pdbs = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter *.pdb -File)
if ($pdbs.Count -gt 0) {
    Write-Warning "发布目录里仍有 $($pdbs.Count) 个 .pdb(csproj 的 RemoveSymbolsFromPublish 应把它们剔除),它们会被打进安装包。"
}

# 图标已作为 AvaloniaResource 嵌入程序集,不再随应用部署
$unexpectedAssets = @(
    @('Assets\logo.ico', 'Assets\logo-512.png') |
        Where-Object { Test-Path -LiteralPath (Join-Path $publishDir $_) }
)
if ($unexpectedAssets.Count -gt 0) {
    Write-Warning "发布目录里出现了本不该部署的 $($unexpectedAssets -join '、')(图标已嵌入程序集)。"
}

$exeItem = Get-Item -LiteralPath $appExe
$sizeMb = [math]::Round($exeItem.Length / 1MB, 2)
$fileVersion = $exeItem.VersionInfo.FileVersion

Write-Host ''
Write-Host '发布完成' -ForegroundColor Green
Write-Host "  程序      : $appExe"
Write-Host "  大小      : $sizeMb MB"
Write-Host "  文件版本  : $fileVersion  (installer.iss 用它当作 AppVersion)"
Write-Host ''
Write-Host '下一步: .\Build-Installer.ps1 -NoPublish'
