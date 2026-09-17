#Requires -Version 7.0
<#
.SYNOPSIS
    编译 Setup\installer.iss,生成 Setup\DSHLauncher-Setup-x64.exe。

.DESCRIPTION
    默认先把应用发布一遍(调用根目录的 Publish-App.ps1),再用 Inno Setup 的命令行编译器 ISCC
    编译安装包脚本;-NoPublish 则直接打包当前发布目录。

    打包源目录与产物名都从 installer.iss 里读(MyPublishDir / MyAppExeName / OutputDir /
    OutputBaseFilename),所以脚本不会和 .iss 里的定义漂移;反过来,如果 installer.iss 的
    MyPublishDir 指向的目录里没有 MyAppExeName,脚本会在调 ISCC 之前就报错并提示改哪一处。

    ISCC.exe 的查找顺序:-ISCC 参数 → PATH → 卸载信息注册表 → 常见安装位置
    (Program Files / Program Files (x86) / %LOCALAPPDATA%\Programs 下的 Inno Setup 6/7)。

.EXAMPLE
    .\Build-Installer.ps1

.EXAMPLE
    .\Build-Installer.ps1 -NoPublish

.EXAMPLE
    .\Build-Installer.ps1 -ISCC 'D:\Tools\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    # 直接打包当前的发布目录,不重新发布
    [switch] $NoPublish,

    # ISCC.exe 路径(默认自动查找)
    [string] $ISCC,

    # 转发给 Publish-App.ps1:发布时不结束正在运行的实例
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 脚本在仓库根目录;installer.iss 及它内部相对路径的基准仍是 Setup\
$repoRoot = $PSScriptRoot
$setupDir = Join-Path $repoRoot 'Setup'
$issPath = Join-Path $setupDir 'installer.iss'
$publishScript = Join-Path $repoRoot 'Publish-App.ps1'

<#
    读取 .iss 里的 #define "名字" 与 [Setup] 段里 key=value 形式的指令。
    installer.iss 是纯文本 #define + 指令,这里只需要这两种取值方式。
#>
function Read-IssText {
    param([Parameter(Mandatory)] [string] $Path)

    # TrimStart 去掉可能的 UTF-8 BOM
    return (Get-Content -LiteralPath $Path -Raw).TrimStart([char] 0xFEFF)
}

function Get-IssDefine {
    param(
        [Parameter(Mandatory)] [string] $Text,
        [Parameter(Mandatory)] [string] $Name
    )

    $match = [regex]::Match($Text, "(?m)^[ \t]*#define[ \t]+$([regex]::Escape($Name))[ \t]+""([^""]*)""")
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value.Trim()
}

function Get-IssDirective {
    param(
        [Parameter(Mandatory)] [string] $Text,
        [Parameter(Mandatory)] [string] $Name
    )

    $match = [regex]::Match($Text, "(?m)^[ \t]*$([regex]::Escape($Name))[ \t]*=[ \t]*(.+?)[ \t]*$")
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value.Trim()
}

<#
    查找 ISCC.exe。找不到返回 $null(由调用方给出带下载地址的提示)。
#>
function Find-ISCC {
    param([string] $Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        throw "-ISCC 指定的文件不存在: $Explicit"
    }

    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) { return $command.Source }

    # Inno Setup 的卸载信息键名形如 "Inno Setup 6_is1"
    $registryPaths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*_is1'
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*_is1'
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup*_is1'
    )
    foreach ($registryPath in $registryPaths) {
        foreach ($key in @(Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue)) {
            # Inno Setup 的安装目录写在 InstallLocation;老版本只有 "Inno Setup: App Path"
            foreach ($propertyName in 'InstallLocation', 'Inno Setup: App Path') {
                $property = $key.PSObject.Properties[$propertyName]
                $installDir = if ($property) { $property.Value } else { $null }
                if (-not $installDir) { continue }
                $candidate = Join-Path $installDir 'ISCC.exe'
                if (Test-Path -LiteralPath $candidate) { return $candidate }
            }
        }
    }

    $bases = @(
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        [Environment]::GetEnvironmentVariable('ProgramFiles')
        (Join-Path ([Environment]::GetEnvironmentVariable('LOCALAPPDATA')) 'Programs')
    ) | Where-Object { $_ }

    foreach ($base in $bases) {
        foreach ($version in 'Inno Setup 7', 'Inno Setup 6', 'Inno Setup') {
            $candidate = Join-Path $base (Join-Path $version 'ISCC.exe')
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    return $null
}

# --- 解析 installer.iss -------------------------------------------------------

if (-not (Test-Path -LiteralPath $issPath)) {
    throw "找不到安装包脚本: $issPath"
}

$issText = Read-IssText -Path $issPath

$myPublishDir = Get-IssDefine -Text $issText -Name 'MyPublishDir'
$myAppExeName = Get-IssDefine -Text $issText -Name 'MyAppExeName'
$myAppNameNoSpace = Get-IssDefine -Text $issText -Name 'MyAppNameNoSpace'
if (-not $myPublishDir -or -not $myAppExeName) {
    throw "$issPath 里读不到 MyPublishDir / MyAppExeName,脚本无法确认打包源。"
}

# installer.iss 里的相对路径(Inno 内部规则)是相对【.iss 所在目录,即 Setup\】解析的
$sourceDir = if ([IO.Path]::IsPathRooted($myPublishDir)) {
    [IO.Path]::GetFullPath($myPublishDir)
} else {
    [IO.Path]::GetFullPath((Join-Path $setupDir $myPublishDir))
}
$sourceDir = $sourceDir.TrimEnd('\')

$outputDir = Get-IssDirective -Text $issText -Name 'OutputDir'
if (-not $outputDir -or $outputDir -eq '.') {
    $installerDir = $setupDir
} elseif ([IO.Path]::IsPathRooted($outputDir)) {
    $installerDir = [IO.Path]::GetFullPath($outputDir)
} else {
    $installerDir = [IO.Path]::GetFullPath((Join-Path $setupDir $outputDir))
}
$installerDir = $installerDir.TrimEnd('\')

# OutputBaseFilename 引用了 {#MyAppNameNoSpace},这里就地展开成实际文件名
$outputBaseFilename = Get-IssDirective -Text $issText -Name 'OutputBaseFilename'
if (-not $outputBaseFilename) { throw "$issPath 里读不到 OutputBaseFilename。" }
$outputBaseFilename = $outputBaseFilename -replace '\{#MyAppNameNoSpace\}', $myAppNameNoSpace
$installerExe = Join-Path $installerDir "$outputBaseFilename.exe"

Write-Host '构建安装包'
Write-Host "  脚本      : $issPath"
Write-Host "  打包源    : $sourceDir"
Write-Host "  产物      : $installerExe"

# --- 发布 ---------------------------------------------------------------------

if ($NoPublish) {
    Write-Host '跳过发布(-NoPublish)'
} else {
    $publishParams = @{}
    if ($KeepRunning) { $publishParams['KeepRunning'] = $true }
    & $publishScript @publishParams
}

# 发布目录里必须有 MyAppExeName,否则 ISCC 会在 [Files] 通配符或 GetVersionNumbersString 上失败,
# 报错信息远不如这里直白
$appExe = Join-Path $sourceDir $myAppExeName
if (-not (Test-Path -LiteralPath $appExe)) {
    throw @"
打包源里找不到 $myAppExeName(完整路径: $appExe)
发布目录和 installer.iss 的 MyPublishDir 对不上。请检查:
  - $issPath 的 #define MyPublishDir 是否指向 Properties\PublishProfiles 里发布配置的 PublishDir;
  - 或先运行 .\Publish-App.ps1 生成发布目录。
"@
}

# --- 查找 ISCC -----------------------------------------------------------------

$iscc = Find-ISCC -Explicit $ISCC
if (-not $iscc) {
    throw @'
未找到 Inno Setup 的命令行编译器 ISCC.exe。
请安装 Inno Setup 6 或更高版本(https://jrsoftware.org/isdl.php),
或用 -ISCC 显式指定路径,例如:
  .\Build-Installer.ps1 -ISCC "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
'@
}
Write-Host "  编译器    : $iscc"

# --- 编译 ---------------------------------------------------------------------

Write-Host "> ISCC.exe $issPath" -ForegroundColor DarkGray
& $iscc $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC 编译失败(退出码 $LASTEXITCODE)。"
}

if (-not (Test-Path -LiteralPath $installerExe)) {
    $found = @(Get-ChildItem -LiteralPath $installerDir -Filter *.exe -File -ErrorAction SilentlyContinue)
    $hint = if ($found.Count -gt 0) {
        "该目录下现有: $($found.Name -join ', ')"
    } else {
        '该目录下没有任何 .exe'
    }
    throw "编译已返回成功,但没有生成 $installerExe。$hint"
}

$installerItem = Get-Item -LiteralPath $installerExe
$sizeMb = [math]::Round($installerItem.Length / 1MB, 2)
$sha256 = (Get-FileHash -LiteralPath $installerExe -Algorithm SHA256).Hash

# Inno 把 AppVersion 写进【ProductVersion】;它的 FileVersion 只有一串填充空格,读不出东西
$productVersion = $installerItem.VersionInfo.ProductVersion
$shownVersion = if ($productVersion -and $productVersion.Trim()) { $productVersion.Trim() } else { '(未写入版本信息)' }

Write-Host ''
Write-Host '安装包构建完成' -ForegroundColor Green
Write-Host "  安装包    : $installerExe"
Write-Host "  大小      : $sizeMb MB"
Write-Host "  版本      : $shownVersion"
Write-Host "  SHA256    : $sha256"
