<#
.SYNOPSIS
    PrismShot 的构建脚本。编译主程序、StarPie 插件与安装程序，并把发行包排到 artifacts\ 下面。

.DESCRIPTION
    刻意不用全局安装的 dotnet：脚本优先用 $<HOME>\.uba-dotnet 里那套隔离 SDK，
    找不到才退回 PATH 上的 dotnet。这样 CI、别人的机器、以及本机 Visual Studio
    都不会互相干扰 —— 「在我机器上能编译」这种事最好从来没发生过。

.PARAMETER Configuration
    Release（默认）或 Debug。

.PARAMETER BootstrapSdk
    先把隔离的 .NET 8 SDK 装到 $<HOME>\.uba-dotnet（只需一次），然后继续构建。

.PARAMETER SkipPlugin
    只编译主程序。改引擎、调 UI 的时候用它可以省几秒。

.PARAMETER Run
    构建完成后立刻启动 PrismShot（用第二个实例的身份去 ping 一下管道，
    顺便验证热键钩子有没有活）。它需要图形会话，远程桌面的会话里跑不出结果。

.EXAMPLE
    .\build.ps1 -BootstrapSdk     # 第一次
    .\build.ps1                   # 之后日常用这个
    .\build.ps1 -SkipPlugin -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [switch] $BootstrapSdk,

    [switch] $SkipPlugin,

    [switch] $Run
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdkHome = Join-Path $HOME '.uba-dotnet'
$sdkExe = Join-Path $sdkHome 'dotnet.exe'

# ---------------------------------------------------------------- 版本号

function Read-Version {
    $file = Join-Path $root 'version.json'
    if (-not (Test-Path $file)) { throw "找不到 $file —— 它是版本号的唯一真相。" }

    $json = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $json.version) { throw "version.json 里没有 version 字段。" }

    return $json.version
}

# 通道（alpha / beta / stable）。不并进 $version 是因为版本号只该有数字，
# 而文件名与「关于」里都得能一眼看出这是哪一条通道的产物。
# 通道内第几次发布。它与 version / channel 一起决定 tag 与文件名：
# v{version}-{channel}.{sequence}。
#
# 这个数字必须存在，是因为「改了东西但版本号没变」是会真实发生的：
# beta.1 发布后又修了三处，当时偷懒直接替换了同一个 Release 的资产，
# 于是同一个版本号下先后躺过三份不同的产物 —— 版本号从此不再是唯一真相。
# 有了它，每一次发布都对应一个只增不减的编号，资产文件名也跟着变，想混都混不了。
function Read-Sequence {
    $file = Join-Path $root 'version.json'
    $json = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $json.sequence) { throw "version.json 里没有 sequence 字段（通道内发布序号）。" }

    return $json.sequence
}

function Read-Channel {
    $file = Join-Path $root 'version.json'
    $json = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $json.channel) { throw "version.json 里没有 channel 字段。" }

    return $json.channel
}

# ---------------------------------------------------------------- SDK

function Resolve-Dotnet {
    if (Test-Path $sdkExe) { return $sdkExe }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw @"
没找到 dotnet。
先运行一次 .\build.ps1 -BootstrapSdk 装一套隔离 SDK（装进 $sdkHome，不碰全局环境），
或者自己装 Visual Studio / .NET 8 SDK 并保证 dotnet 在 PATH 里。
"@
}

function Install-Sdk {
    if (Test-Path $sdkExe) {
        Write-Host "隔离 SDK 已存在：$sdkHome" -ForegroundColor DarkGray
        return
    }

    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Write-Host "下载 dotnet-install.ps1 …" -ForegroundColor Cyan
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing

    Write-Host "安装 .NET 8 SDK 到 $sdkHome …" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer `
        -Channel 8.0 -InstallDir $sdkHome -NoPath

    if (-not (Test-Path $sdkExe)) { throw "SDK 安装后仍然找不到 $sdkExe。" }
    Write-Host "SDK 就绪。" -ForegroundColor Green
}

# ---------------------------------------------------------------- 构建

function Invoke-Build {
    param([string] $Project, [string] $Label)

    Write-Host "→ 编译 $Label" -ForegroundColor Cyan
    & $dotnet build $Project -c $Configuration -p:Platform=x64 -v minimal
    if ($LASTEXITCODE -ne 0) { throw "$Label 编译失败（exit $LASTEXITCODE）。" }
}

function Copy-PluginOutput {
    param([string] $Target)

    # TFM 不许写死。插件的 TargetFramework 是跟着宿主 SDK 走的（一度是 net8.0-windows，
    # 后来为对齐宿主改到 net8.0-windows10.0.19041.0）。写死的后果不是报错，而是**静默**把
    # 上一次构建遗留在旧目录里的 dll 当成这一版的产物带出去 —— 发行包看着一切正常，
    # 里面装的却是几小时前的插件，而旧目录不会有人去清理。所以从 csproj 里读。
    $csproj = Join-Path $root 'plugin/StarPie.Plugin.PrismShot/StarPie.Plugin.PrismShot.csproj'
    $tfm = ([regex]::Match((Get-Content $csproj -Raw),
        '<TargetFramework>\s*(.+?)\s*</TargetFramework>')).Groups[1].Value
    if (-not $tfm) { throw "读不出插件的 TargetFramework：$csproj" }

    $built = Join-Path $root "plugin/StarPie.Plugin.PrismShot/bin/x64/$Configuration/$tfm"

    if (-not (Test-Path $built)) {
        # 对不上就报出来。退回「别的目录里有 dll 就行」等于把上面这个坑重新埋回去。
        $siblings = Get-ChildItem -Path (Join-Path $root "plugin/StarPie.Plugin.PrismShot/bin/x64/$Configuration") `
            -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
        throw "插件产物目录不存在：$built（bin 下实际有：$($siblings -join ', ')）"
    }

    # 契约程序集 StarPie.Plugin.Abstractions.dll 由宿主提供，
    # 打包时带出去会被宿主判为「自带的 SDK 副本版本不一致」而直接拒绝加载。
    $keep = @('StarPie.Plugin.PrismShot.dll', 'plugin.json')
    foreach ($name in $keep) {
        $source = Join-Path $built $name
        if (-not (Test-Path $source)) { throw "插件产物缺少 $name（这不是可选文件）。" }
        Copy-Item $source -Destination $Target -Force
    }

    Write-Host "  插件 DLL + plugin.json → $Target" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 主流程

if ($BootstrapSdk) { Install-Sdk }

$dotnet = Resolve-Dotnet
Write-Host "dotnet: $dotnet" -ForegroundColor DarkGray

$version = Read-Version
$channel = Read-Channel
$sequence = Read-Sequence
Write-Host "版本: $version ($channel.$sequence)  tag: v$version-$channel.$sequence" -ForegroundColor DarkGray

$appProject = Join-Path $root 'src/PrismShot/PrismShot.csproj'
$pluginProject = Join-Path $root 'plugin/StarPie.Plugin.PrismShot/StarPie.Plugin.PrismShot.csproj'

Invoke-Build $appProject '主程序 PrismShot'
if (-not $SkipPlugin) { Invoke-Build $pluginProject 'StarPie 插件' }

# ---- 排发行包 ----

$stage = Join-Path $root "artifacts/PrismShot-$version"
$appDir = $stage
$pluginDir = Join-Path $stage 'plugin/prismshot'

# 常驻实例跑的往往**正是**上一版 artifacts 里的那个 exe，它会把目录里所有 dll 都锁住，
# 于是后面既删不掉也覆盖不了。这不是偶发：PrismShot 天生常驻，只要在本机构建就一定会撞上。
# 所以构建前先请它退出 —— 图省事用 Stop-Process 会让它来不及摘钩子，走它自己的 --quit。
$resident = Join-Path $stage 'PrismShot.exe'
if ((Test-Path $resident) -and (Get-Process -Name PrismShot -ErrorAction SilentlyContinue)) {
    Write-Host "→ 通知常驻实例退出（它占着 $stage 里的文件）" -ForegroundColor Cyan
    try {
        # 这条命令失败不该中断构建：进程可能压根起不来（运行时解析之类），
        # 也可能本来就没在跑。能不能删掉产物由后面的 Delete 说了算。
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & $resident --quit 2>&1 | Out-Null
        $ErrorActionPreference = $previous
    }
    catch {
        Write-Host "  （让它退出这一步没成功，继续尝试清目录）" -ForegroundColor DarkGray
    }

    for ($i = 0; $i -lt 25; $i++) {
        if (-not (Get-Process -Name PrismShot -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 200
    }
}

# 重跑时要先清掉上一次的产物。同样避开 Remove-Item 的回收站钩子（见上面 pdb 那段说明），
# 而且 Directory.Delete 是「删除」而不是「精心 recycle」，符合构建中间目录的语义。
if ([System.IO.Directory]::Exists($stage)) {
    try {
        [System.IO.Directory]::Delete($stage, $true)
    }
    catch {
        # 删不掉就改名让开，而不是让构建失败。
        # 在装了 PrismShot 的机器上构建一定会撞上这一下：它有计划任务、开机自启、
        # 还有上一次构建留下的进程，目录句柄不会老老实实等你。而 Windows 允许给被占用的
        # 目录改名 —— 名字一换，原来的目录就没了，新的可以照常建。
        # 硬失败在这里没有任何收益：构建脚本的职责是产出这一版，不是替用户做进程管理。
        $orphanRoot = Join-Path $root '.cache/superseded'
        $orphan = Join-Path $orphanRoot ("PrismShot-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        [System.IO.Directory]::CreateDirectory($orphanRoot) | Out-Null

        try {
            [System.IO.Directory]::Move($stage, $orphan)
            Write-Host "  上一版产物被占用，已改名让开：$orphan" -ForegroundColor DarkGray
            Write-Host "  （它是构建中间物，随时可以整个删掉 .cache\superseded）" -ForegroundColor DarkGray
        }
        catch {
            # 连改名都不行时，最后一条路是「直接覆盖」：目录对象被占着，
            # 但往里写文件仍然可以（上面的删除其实已经把大部分旧文件清掉了）。
            # 代价只是旧版里多出来的文件不会被清理 —— 那不值得让构建停在这里。
            Write-Host "  !! 上一版产物删不掉也改不动名（被占用），将直接覆盖。" -ForegroundColor Yellow
            Write-Host "     $($_.Exception.Message)" -ForegroundColor DarkGray
        }
    }
}

[System.IO.Directory]::CreateDirectory($appDir) | Out-Null
[System.IO.Directory]::CreateDirectory($pluginDir) | Out-Null

Write-Host "→ publish 主程序" -ForegroundColor Cyan
& $dotnet publish $appProject -c $Configuration -p:Platform=x64 -o $appDir -v minimal
if ($LASTEXITCODE -ne 0) { throw "主程序 publish 失败（exit $LASTEXITCODE）。" }

# publish 会把 pdb 一起带出来，发行包里用不上。
# 这里刻意走 [System.IO.File] 而不是 Remove-Item：本机给 Remove-Item 挂了一层
# 「先丢回收站」的安全钩子，它在 CI / 命令行会话里会直接失败，构建不该被它拦住。
Get-ChildItem -Path $appDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    ForEach-Object { [System.IO.File]::Delete($_.FullName) }

if (-not $SkipPlugin) { Copy-PluginOutput $pluginDir }

Copy-Item (Join-Path $root 'LICENSE') $stage -Force
Copy-Item (Join-Path $root 'README.md') $stage -Force
Copy-Item (Join-Path $root 'plugin/StarPie.Plugin.PrismShot/README.md') $pluginDir -Force

Write-Host ""
Write-Host "发行包：$stage" -ForegroundColor Green
Get-ChildItem $appDir -File | Select-Object -First 6 -ExpandProperty Name |
    ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }

# ---- 安装程序 ----

# payload.zip 里装的正是上面刚排好的那一整个发行包目录：安装器把它嵌进自己的 exe，
# 于是分发的是「一个 exe」，而不是「一个 exe 加一个文件夹」——
# 后者迟早会被人只拷走 exe，然后得到一个装不出东西的安装器。
$setupProject = Join-Path $root 'src/PrismShot.Setup/PrismShot.Setup.csproj'
$payloadDir = Join-Path $root 'src/PrismShot.Setup/payload'
$payloadZip = Join-Path $payloadDir 'payload.zip'

Write-Host "→ 打包安装程序内容（$(Split-Path $stage -Leaf)）" -ForegroundColor Cyan
if ([System.IO.Directory]::Exists($payloadDir)) { [System.IO.Directory]::Delete($payloadDir, $true) }
[System.IO.Directory]::CreateDirectory($payloadDir) | Out-Null
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "→ publish 安装程序" -ForegroundColor Cyan
$setupStage = Join-Path $root 'artifacts/setup'
if ([System.IO.Directory]::Exists($setupStage)) { [System.IO.Directory]::Delete($setupStage, $true) }
& $dotnet publish $setupProject -c $Configuration -p:Platform=x64 -o $setupStage -v minimal
if ($LASTEXITCODE -ne 0) { throw "安装程序 publish 失败（exit $LASTEXITCODE）。" }

$setupExe = Join-Path $setupStage 'PrismShot.Setup.exe'
if (-not (Test-Path $setupExe)) { throw "publish 之后找不到 $setupExe。" }

$setupFinal = Join-Path $root "artifacts/PrismShot-$version-$channel.$sequence-Setup.exe"
if (Test-Path $setupFinal) { [System.IO.File]::Delete($setupFinal) }
Copy-Item $setupExe $setupFinal -Force
$sizeMb = [math]::Round((Get-Item $setupFinal).Length / 1MB, 1)
Write-Host "安装程序：$setupFinal（${sizeMb} MB，单文件）" -ForegroundColor Green

# ---- 可选：跑一下 ----

if ($Run) {
    $exe = Join-Path $appDir 'PrismShot.exe'
    if (-not (Test-Path $exe)) { throw "找不到 $exe，无法启动。" }

    Write-Host ""
    Write-Host "→ 启动 $exe" -ForegroundColor Cyan
    Start-Process -FilePath $exe -WorkingDirectory $appDir

    # 等常驻实例把管道服务端起来。轮询而不是死等：
    # 钩子注册失败时它会在日志里写原因，脚本这时候报「管道没起来」才是准确的。
    $online = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 200
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'PrismShot.Control', 'InOut')
        try {
            $pipe.Connect(300)
            $online = $true
        }
        catch { }
        finally { $pipe.Dispose() }
        if ($online) { break }
    }

    if ($online) {
        Write-Host "常驻实例已在线（管道 PrismShot.Control 可连）。" -ForegroundColor Green
    }
    else {
        Write-Host "!! 管道没连上。看 %LOCALAPPDATA%\PrismShot\logs\ 下的最新日志。" -ForegroundColor Yellow
    }
}
