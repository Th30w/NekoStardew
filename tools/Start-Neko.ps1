# 一键启动 N.E.K.O. 星露谷猫娘（Windows / PowerShell）
#
# 顺序是有讲究的：agent 必须先起——它监听 127.0.0.1:48921，
# 游戏里的 mod 和监控窗都是"连上来"的一方，agent 没起它们只会连不上然后反复重试。
#
#   1. agent   （dotnet NekoStardew.Agent.dll）监听 48921
#   2. 游戏    （StardewModdingAPI.exe）mod 连 agent、自己载入存档
#   3. 监控窗  （NekoStardew.Monitor.exe）桌面复制抓游戏窗口，边聊边看
#
# 每个都可以重复跑：已经在跑的会被跳过，不会开出第二份。

param(
    # 星露谷安装目录。装了别的地方就改这里，或者跑的时候传参：
    #   .\Start-Neko.ps1 -GamePath "D:\Games\Stardew Valley"
    [string]$GamePath = "D:\SteamLibrary\steamapps\common\Stardew Valley",

    # 源码工程根目录，默认是脚本上一级
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),

    # 只起一部分：agent / game / monitor 任选，默认全起
    [string[]]$Only = @("agent", "game", "monitor"),

    # agent 的串流日志放这儿，排查"她为什么不动"时看它派了什么活
    [string]$LogDir = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $RepoRoot "_pack" }

$agentDll   = Join-Path $RepoRoot "agent\NekoStardew.Agent\bin\Debug\net10.0\NekoStardew.Agent.dll"
$agentWork  = Split-Path -Parent $agentDll
$monitorExe = Join-Path $RepoRoot "monitor\NekoStardew.Monitor\bin\Debug\net10.0-windows\NekoStardew.Monitor.exe"
$gameExe    = Join-Path $GamePath "StardewModdingAPI.exe"
$dotnetExe  = "C:\Program Files\dotnet\dotnet.exe"

function Say($msg) { Write-Host $msg }

# 三个产物都必须是构建过的；缺了就直接说清楚怎么补，不要含糊地"启动失败"
foreach ($pair in @(
        @($agentDll,   "agent",   "cd agent\NekoStardew.Agent; dotnet build -c Debug"),
        @($monitorExe, "monitor", "cd monitor\NekoStardew.Monitor; dotnet build -c Debug"),
        @($gameExe,    "游戏",    "检查 -GamePath 是否指向星露谷安装目录")
    )) {
    if (-not (Test-Path -LiteralPath $pair[0])) {
        Say "缺 $($pair[1]) 的产物：$($pair[0])"
        Say "  先构建：$($pair[2])"
        exit 1
    }
}

# ---------- 1. agent ----------
if ($Only -contains "agent") {
    $running = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains("NekoStardew.Agent.dll") }

    if ($running) {
        Say "[1/3] agent 已在跑（pid $($running.ProcessId -join ',')），跳过"
    } else {
        if (-not (Test-Path -LiteralPath $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

        Say "[1/3] 启动 agent ..."
        Start-Process -FilePath $dotnetExe `
            -ArgumentList "`"$agentDll`"" `
            -WorkingDirectory $agentWork `
            -RedirectStandardOutput (Join-Path $LogDir "agent-out.log") `
            -RedirectStandardError  (Join-Path $LogDir "agent-err.log") `
            -WindowStyle Hidden

        # 48921 起来之前游戏连不上，等一小会儿再继续
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 400
            $ok = $false
            try { $ok = (Invoke-RestMethod "http://127.0.0.1:48921/health" -TimeoutSec 2).mode -ne $null } catch { }
        } while (-not $ok -and (Get-Date) -lt $deadline)

        if ($ok) { Say "      agent 就绪：http://127.0.0.1:48921/health" }
        else     { Say "      agent 20 秒内没响应，看 $LogDir\agent-err.log" }
    }
}

# ---------- 2. 游戏 ----------
if ($Only -contains "game") {
    if (Get-Process -Name StardewModdingAPI -ErrorAction SilentlyContinue) {
        Say "[2/3] 游戏已在跑，跳过"
    } else {
        Say "[2/3] 启动游戏（首次进档要 50~70 秒）..."
        Start-Process -FilePath $gameExe -WorkingDirectory $GamePath
    }
}

# ---------- 3. 监控窗 ----------
if ($Only -contains "monitor") {
    if (Get-Process -Name "NekoStardew.Monitor" -ErrorAction SilentlyContinue) {
        Say "[3/3] 监控窗已在跑，跳过"
    } else {
        Say "[3/3] 启动监控窗 ..."
        # 要在游戏窗口出现之后再开，否则它找不到窗口只会空转
        Start-Sleep -Seconds 8
        Start-Process -FilePath $monitorExe -WorkingDirectory (Split-Path -Parent $monitorExe)
    }
}

Say ""
Say "都拉起来了。看状态："
Say "  Invoke-RestMethod http://127.0.0.1:48921/health    # mode / modConnected / busy"
Say "  Invoke-RestMethod http://127.0.0.1:48921/state     # 日期 时间 金钱 作物 背包"
Say "她派了什么活：$LogDir\agent-out.log 里搜 [hub] dispatch"
Say ""
Say "想让她先别自己动、只等指令："
Say "  经 ws://127.0.0.1:48921/neko 发 {`"type`":`"command`",`"mode`":`"manual`"}，"
Say "  或把 agent\NekoStardew.Agent\Program.cs 里 Hub._mode 的默认值改回 `"manual`" 再重新构建。"
