# 星露谷猫娘 · neko-stardew

让 N.E.K.O. 的猫娘在她**自己的**星露谷存档里自主经营农场：你用一个朴素的监控窗看她实时干活，
同时照常和她说话，像坐在旁边打游戏的朋友。

- 业务代码全 **C#**（agent / SMAPI mod / 监控面板）
- 唯一一层 Python 是 N.E.K.O 官方 SDK 强制要求的插件薄壳，零农场逻辑，只做协议转发
- 不联机、不冒充玩家：她玩的是一份独立存档，和你的存档互不相干

## 怎么启动

顺序有依赖，不能乱：**agent 必须先起**（它监听 `127.0.0.1:48921`），游戏里的 mod 和监控窗都是"连上来"的一方，
agent 没起它们只会反复重试。

### 一键

```powershell
powershell -ExecutionPolicy Bypass -File tools\Start-Neko.ps1
# 星露谷不在默认位置就传参：
powershell -ExecutionPolicy Bypass -File tools\Start-Neko.ps1 -GamePath "D:\Games\Stardew Valley"
```

脚本按 agent → 游戏 → 监控窗 的顺序拉，已经在跑的会跳过，可以反复执行。
三个构建产物缺哪个它会直接告诉你去哪个目录 `dotnet build`，不会含糊地报"启动失败"。

### 手动三件套

```powershell
# 1) agent（先起）
cd agent\NekoStardew.Agent
dotnet build -c Debug
"C:\Program Files\dotnet\dotnet.exe" bin\Debug\net10.0\NekoStardew.Agent.dll

# 2) 游戏
"<星露谷安装目录>\StardewModdingAPI.exe"

# 3) 监控窗（等游戏窗口出来再开）
monitor\NekoStardew.Monitor\bin\Debug\net10.0-windows\NekoStardew.Monitor.exe
```

### mod 装一次就够

```powershell
cd mod\NekoStardew
dotnet build -c Debug
# dll 和 pdb 必须一起拷：pdb 缺了崩溃堆栈就没有行号，排查基本没法做
copy bin\Debug\net6.0\NekoStardew.dll "<星露谷>\Mods\NekoStardew\"
copy bin\Debug\net6.0\NekoStardew.pdb "<星露谷>\Mods\NekoStardew\"
```

改 mod 之前必须先退游戏（dll 被占用写不进去）；改 agent 之前必须先停 agent 进程。

### 起完怎么确认她真的在动

```powershell
Invoke-RestMethod http://127.0.0.1:48921/health
#   mode: auto / modConnected: true / hasState: true

Invoke-RestMethod http://127.0.0.1:48921/state
#   day / time / money / tilled / plantable_seeds / crops / inventory
```

`state` 里的 `time` 会自己往下走（7 秒真实 = 10 游戏分钟），`crops` 会随她播种增长——
这两个数字在变，就说明整条链路活着，不是"接口通了但人不动"。

她派了什么活，看 `_pack\agent-out.log` 里的 `[hub] dispatch` 行。
**agent 启动时要把 stdout 重定向到这个文件才看得到**，不重定向什么都看不见。

### 让她先别自己动

默认 `auto`（她自己过日子是主场景）。想改成只等你下指令：

- 经 `ws://127.0.0.1:48921/neko` 发 `{"type":"command","mode":"manual"}`
- 或把 `agent\NekoStardew.Agent\Program.cs` 里 `Hub._mode` 的默认值改回 `"manual"` 重新构建

## 当前状态（本机实测）

| 部件 | 状态 | 证据 |
|------|------|------|
| N.E.K.O 用户插件总开关 | **已打开** | `48915 /health` → `agent_flags.user_plugin_enabled: true` |
| 桥接插件 | **已注册、运行中** | `48916 /plugins` → `status: running`、`effective_source: development`、3 个入口 |
| 插件 ↔ agent 链路 | **已连通** | agent 日志 `[Stardew] connected to ws://localhost:48921/neko`，`nekoConnected: true` |
| stardew-agent（C#） | 运行中 | `http://127.0.0.1:48921/health`，`mode: auto` |
| 协议闭环 | **5/5 通过** | `tools/SmokeTest` 实测输出见下 |
| SMAPI 4.5.2 + mod | **已装、已加载** | 游戏内日志 `星露谷猫娘 mod 已载入，等待存档载入后连接 agent` |
| 监控面板（C# WPF） | **抓屏已打通** | 桌面复制抓游戏窗口，1280×720；抓不到时回落共享内存 640×360 |
| 农场数据 | **在跑，第 5 天** | `/state` → `day 5 08:10, 24 株作物, 786 G`；连续自主运行跨了多个游戏日 |

协议闭环实测（`tools/SmokeTest` 对真实运行的 agent）：

```
PASS state forwarded to plugin
PASS task decomposed into water_crops
      {"type":"task","goal":"给缺水的作物浇水","actions":[{"op":"water_crops"}]}
PASS task_finished returned to plugin
PASS vague task -> most urgent action
PASS off-topic task rejected with reason
RESULT: PASS
```

## 架构

```
星露谷（她的独立存档）
   └─ SMAPI mod (C#) ──ws──┐
                           ├─ stardew-agent (C# 48921) ──ws── N.E.K.O 插件（Python 薄壳）
   └─ 游戏窗口 ──DXGI── 监控面板 (C# WPF)                    └─ 对话 / Live2D / 语音
```

分层理由：

- **mod** 只做两件事——把农场真实数值导出来，把动作原语执行掉。不思考。
- **agent** 把「给没浇水的作物浇水」拆成 `water_crops` 这类语义动作；`auto` 模式下自己挑最急的活派下去。不碰游戏内存。
- **插件** 把状态翻成中文提示词、把画面推进对话的视觉上下文。不写农场规则。

拆任务要确定性（不能今天浇水明天漏），说话要人格（不能像报数），所以分开。

## 目录

```
neko-stardew/
├── stardew_companion/              # N.E.K.O 插件（Python，官方 SDK 必需的外壳）
│   ├── plugin.toml                 # 清单；entry = "plugin.plugins.stardew_companion:StardewCompanionPlugin"
│   ├── __init__.py                 # @neko_plugin / @llm_tool(stardew_task) / 两个 entry
│   ├── client.py                   # WebSocket + 5s 自动重连
│   ├── service.py                  # 状态缓存、PendingTask、20s 主动陪伴循环
│   ├── prompts.py                  # 全部中文文案（改口吻只动这里）
│   └── i18n/zh-CN.json
├── agent/NekoStardew.Agent/        # C# 服务（48921）
│   ├── Program.cs                  # /health /state /neko /mod + 3s 自主经营心跳
│   ├── Protocol.cs                 # 帧与农场状态模型（全 JsonPropertyName）
│   └── TaskPlanner.cs              # 中文目标 → 动作原语；最急动作挑选
├── mod/NekoStardew/                # SMAPI mod（net6.0）
│   ├── ModEntry.cs                 # 采样、收帧、主线程排队
│   ├── AgentLink.cs                # WS 长连接 + 3s 重连
│   ├── FarmSnapshot.cs             # 农场状态 → JSON（全字段兜底）
│   └── ActionRunner.cs             # 逐帧推进的动作执行器
├── monitor/NekoStardew.Monitor/    # 监控面板（WPF net10）
│   ├── MainWindow.xaml(.cs)        # 朴素双栏：画面 + 状态/日志
│   ├── DesktopDuplicator.cs        # DXGI Desktop Duplication + 裁剪
│   └── WindowFinder.cs             # 按类名 SDL_app 定位游戏窗口
├── tools/SmokeTest/                # 不依赖游戏的协议闭环测试
└── docs/
```

## 跑起来（三步）

```powershell
# 1. agent
dotnet run --project agent/NekoStardew.Agent

# 2. 监控面板
dotnet run --project monitor/NekoStardew.Monitor

# 3. 游戏（已装 SMAPI + mod 的情况下）
#    D:\SteamLibrary\steamapps\common\Stardew Valley\StardewModdingAPI.exe
```

agent 端点：

- `GET http://127.0.0.1:48921/health` — 连接与农场快照摘要
- `GET http://127.0.0.1:48921/state` — 完整 FarmState JSON（面板读这个）
- `ws://127.0.0.1:48921/neko` — 给插件
- `ws://127.0.0.1:48921/mod` — 给 SMAPI mod

## 把插件装进 N.E.K.O（真实可用的路径）

N.E.K.O 有三套东西容易混：**目录里放着 ≠ 已安装 ≠ 已注册**。下面这条链路是本机验证过的唯一可行路径。
所有端点都在 **48916**（user_plugin server），不是 48911。

```powershell
# 0) 打开「用户插件」总开关（body 必须是嵌套形态，扁平 body 无效）
Invoke-RestMethod http://127.0.0.1:48911/api/agent/flags -Method Post -ContentType 'application/json' `
  -Body '{"agent_flags":{"user_plugin_enabled":true}}'

# 1) 开开发模式（不开发模式，注册会报 "Enable development mode first"）
Invoke-RestMethod http://127.0.0.1:48916/plugins/development/settings -Method Put `
  -ContentType 'application/json' -Body '{"enabled":true}'

# 2) 拿 CSRF token（就在 48916）
$token = (Invoke-RestMethod http://127.0.0.1:48916/security/csrf-token).csrf_token

# 3) 注册（必须带 X-Neko-Development + Origin + Referer，否则报
#    "Development plugins require a local N.E.K.O page and local backend connection"）
$h = @{ 'X-CSRF-Token'=$token; 'X-Neko-Development'='1'; 'Content-Type'='application/json';
        'Origin'='http://127.0.0.1:48911'; 'Referer'='http://127.0.0.1:48911/' }
$src = '.\stardew_companion'   # 指向你 clone 下来的工程里的 stardew_companion 目录
$body = @{ source_dir=$src; preview=$true } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:48916/plugins/development/registrations -Method Post -Headers $h -Body $body
# 预览 OK 后把 preview 改 false 再发一次，返回 registration_id + runtime_alive: true

# 4) 触发懒启动（插件启动后不会自己连 agent，必须被调一次入口）
$run = @{ plugin_id='stardew_companion'; entry_id='stardew_status'; args=@{} } | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:48916/runs -Method Post -Headers $h -Body $run
# 5 秒后 http://127.0.0.1:48921/health 变 nekoConnected: true
```

几条踩过的坑，写下来省得重复：

- **目录名必须等于插件 id**。`entry = "plugin.plugins.X:Class"` 里的 `X` 与 `plugin.toml` 所在目录名不一致时，
  加载直接报 `describe_plugin_entry_directory_mismatch`。所以目录叫 `stardew_companion/`。
- **开发模式注册是临时的**（`disable_timeout_ms: 300000`）。N.E.K.O 重启后要按上面重跑一遍；
  想长期驻留，走 `POST /plugin-cli/build` 打成 `.neko-plugin` 再 `install`。
- 插件 SDK 用一次性 `asyncio.run()` 跑 `@lifecycle(id="startup")`，返回即关 loop、所有 `create_task` 被取消。
  长连服务必须**懒启动**（`__init__.py` 的 `_ensure_service_started`），不能写在 startup 里。
- `chat-card/action/{id}` 不是通用入口调用器（要 `card_id` + `target_lanlan`）；通用入口是 `POST /runs`。

## 监控面板

朴素双栏，无渐变无 emoji：左边实时画面，右边 316px 状态栏（插件/游戏/托管/画面 + 农场七行 + 当前任务 + 日志）。

抓屏走 **DXGI Desktop Duplication**（`Vortice.Direct3D11` + `Vortice.DXGI`），抓整屏后按窗口 rect 裁剪。
不用 `PrintWindow` 的原因是它对 MonoGame/DX11 窗口返回纯黑。

两个必须记住的细节：

1. **`D3D11CreateDevice` 传了 adapter 时，`DriverType` 必须是 `UNKNOWN`**。传 `Hardware` + 非空 adapter
   一律返回 `E_INVALIDARG`（参数错误）。这是 D3D11 的硬性规则，不看代码根本猜不到。
2. **游戏窗口类名是 `SDL_app`，标题却是 `Stardew Valley 1.6.15 - running SMAPI 4.5.2 with 3 mods`** ——
   和 SMAPI 控制台（`CASCADIA_HOSTING_WINDOW_CLASS`）撞标题。只能按类名排控制台，按标题排会把游戏窗口本身排掉。

`NEKO_MONITOR_DUMP=<路径>` 环境变量可让它抓到第一帧就存一张 PNG，用于自检抓屏是否真的出画面。
运行日志同时写一份到 `%TEMP%\neko-stardew-monitor.log`。

## 协议

插件 → agent：

| type | 字段 | 用途 |
|------|------|------|
| `task` | `task`, `task_id` | 派一个自然语言目标 |
| `command` | `action: status / set_mode` | 查状态、切 idle/auto/manual |

agent → 插件：`log`、`screenshot`、`task_finished`、`state`、`event`、`agent_status`。
agent → mod：`task`（含 `actions` 动作原语）、`command`（`mode`）、`hello`。
mod → agent：`state`、`screenshot`、`log`、`task_finished`、`event`。

动作原语：`water_crops`、`harvest_crops`、`plant_seeds`、`feed_animals`、`pet_animals`、
`clear_debris`、`ship_items`、`sleep`、`go_to`。

语义化而不是坐标：坐标会过期，由 mod 在游戏内按当前状态解析真正的地块。

FarmState 字段（面板与插件统一口径）：

```
season day year time weather money energy maxEnergy location playerTile mode
crops[]   { name, tile[], stage, daysLeft, watered, harvestable }
animals[] { name, building, fed, hearts }
inventory[] { name, count }
notes
```

## 动作原语的实现细节（按 1.6.15 真实签名写的）

用 `System.Reflection.MetadataLoadContext` 读了 `Stardew Valley.dll` 校准，不是猜的：

- `HoeDirt.state` 是 `NetInt`：0 = 干、1 = 浇过；另有 `isWatered()` / `needsWatering()` / `readyForHarvest()`
- `HoeDirt.performUseAction(Vector2)`、`HoeDirt.plant(string, Farmer, bool)`
- `Crop`：`currentPhase` / `phaseDays` / `fullyGrown` / `dead` / `indexOfHarvest` /
  `harvest(int, int, HoeDirt, JunimoHarvester, bool)`
- `FarmAnimal`：`fullness`(NetInt) / `wasPet`(NetBool) / `friendshipTowardFarmer` / `happiness` / `home`
- `AnimalHouse.feedAllAnimals()`

所以：WaterAll 用 `dirt.state.Value = 1`；HarvestAll 用 `crop.harvest(x, y, dirt, null, false)`；
FeedAll 用 `house.feedAllAnimals()`；PetAll 置 `wasPet.Value = true` 并 `friendshipTowardFarmer += 5`。

`Farmer.startSleep` 在 1.6.15 **不存在**，所以 `sleep` 走的是反射调原版 `Game1.endDay()`
（含过夜生长、体力恢复、自动存档）。要求 `timeOfDay >= 1800` 才会执行，白天喊睡觉会被拒。

## 已知限制

1. **抓屏三条链路，各有各的前提**。① 桌面复制（DXGI）抓游戏窗口——快，但只能抓到
   「屏幕上看得见的东西」，被别的窗口盖住或最小化就没了，本机偶尔还会整帧抓黑；
   ② 共享内存兜底——mod 直接把 backbuffer 回读出来，不受遮挡影响，但回读一次 58~165ms，
   开着它会把游戏拖慢（所以 `SharedMemoryMaxFps` 默认 0，只在桌面复制失败时临时开）；
   ③ PNG 文件兜底——最慢，只用于取证。
2. **SMAPI 控制台会浮在游戏窗口上面**，把画面挡掉一截。监控窗启动时会自己枚举控制台类窗口
   并最小化（`HideCoveringConsoles()`），不用手工处理。
3. **升级弹窗会跳过专精**。`LevelUpMenu` 现在直接被清掉，那一级的两点专精不入账——
   后台托管没人点 OK，只能这样。要保住奖励得人工点一次，或以后改成反射点它的 OK 按钮。
4. **开发模式注册重启后失效**，见上文「把插件装进 N.E.K.O」。
5. 星露谷 1.6.x 小版本会改方法签名，mod 要跟着重编（当前 1.6.15.24356）。
   已经踩过的：`Game1.endDay()` 不存在（要走 `newDayAfterFade(Action)`）、
   `Game1.performTenMinuteClockUpdate()` 要反射、`Game1.CurrentEvent` 挂在当前地点上。
6. 双实例靠 `APPDATA` 隔离，两个进程同时握 Steam + 存档不串尚未实测。
7. N.E.K.O 单帧有大小上限，截图必须 agent 侧先缩，插件不做二次处理。
8. `harvest_crops` / `feed_animals` / `pet_animals` / `clear_debris` 代码写全了但**没在实机跑过**：
   这档当时没有成熟作物、也没有动物，凑不出触发条件。作物成熟后会自动进收获分支。

## 帧率：为什么这台机器上做不到 60 帧（2026-11-12 实测）

监控窗的抓屏链路本身是好的（能拿到方向、颜色都正确的 1280×720 真实画面），
卡住的是游戏自己的绘制节奏。以下是排除到最后剩下的硬数据，再有人提「60 帧」先看这一节。

### 硬数据

| 指标 | 实测值 | 说明 |
|---|---|---|
| `OnUpdateTicked` | 55–65 帧/秒 | 逻辑帧正常 |
| `Display.Rendering` / `Display.Rendered` | 3–5 帧/秒 | 真正画出画面的次数 |
| Draw 本身耗时 | 1.2–2.4 毫秒 | 画一帧很快 |
| 两帧之间的空等 | ~200 毫秒 | 全在 Present 里 |
| 桌面复制抓窗口 | 6–8 帧/秒 | 屏幕上真实变化的速度 |
| 共享内存回读（640×360） | 58 毫秒（GetBackBufferData）／108–165 毫秒（原生 glReadPixels BGRA） | 最多十几帧 |

结论：**游戏每秒只画 4–5 次，任何抓屏方式都不可能超过这个数**。
Present 一次要等 200 毫秒，这才是根。

### Present 为什么等 200 毫秒

- 这台机器的主显示器是 **GameViewer 虚拟显示适配器**（1920×1080@144），
  用户在外面靠远程串流看机；机器上物理显卡只有 Tesla P40 ×2（无显示输出）和
  AMD Radeon R7 200（640×480），另有三四个虚拟显示适配器（Parsec／MuMu／USB Monitor）。
- 星露谷实际是 **SDL + OpenGL** 后端（`Context` 实际类型 `MonoGame.OpenGL.GraphicsContext`），
  `glGetString` 报 **vendor=ATI、renderer=AMD Radeon R7 200 Series、GL 4.6**，
  也就是说画面在 AMD 卡上画、却要在虚拟显示器上合成，swap 要排队等它来取帧。
- CPU 整体占用 90%（`kryptex_SRBMiner-MULTI` 在跑 CPU 挖矿），
  GPU copy 引擎被 `GameViewerServer` 占约 50%。这些进程有保护，改不了优先级。

### 逐一排除过的（别再试）

- 关垂直同步（`SynchronizeWithVerticalRetrace=false` + `ApplyChanges`）：仍 3.6 帧
- 分辨率降到 640×360：仍 3.6 帧（不是带宽问题）
- 独占全屏（`HardwareModeSwitch=true` + `IsFullScreen=true`）：仍 4 帧
- BLT（Discard）交换链：MonoGame 的 `PresentationParameters` 没有 `SwapEffect`，公开 API 改不了
- 窗口置顶 TOPMOST、最小化 SMAPI 控制台：仍 3.3 帧
- 把游戏进程优先级提到 High：仍 3.3 帧
- 移除本 mod 单独跑（`NekoStardew.dll` 改名 `.off`）：桌面复制仍 6–8 帧 → **与本 mod 无关**
- 窗口最小化后 swap 一度测到 25–35 帧，但复现不稳定，之后每次都回到 4–5 帧，不能作为方案

### 要真 60 帧，只有一条路

把游戏窗口放到 **接在 AMD 卡上的真实显示器** 上（机器上检测到一台 Dell **S2719DGF**，
但当前显示拓扑只启用了 GameViewer 虚拟屏，物理屏没进桌面）。
同卡真实输出时 Present 不必等虚拟显示器取帧，回读也会走正常的 PCIe 路径。

这一步要改显示拓扑，**有可能让远程串流断开**（用户在外面就是靠它看机的），所以没有擅自做。

### 现在的实际表现

监控窗用桌面复制抓游戏窗口，稳定 6–8 帧/秒，画面方向、颜色都正确，
控制台窗口会被监控端自动收起。这一帧的截图见 `docs/monitor-final.png`。

## 2026-11-12 收尾：把她卡住的四个点修掉

上架前的这一轮不是加功能，是把"明明写了实现却根本轮不到执行"的地方逐个接上。
按影响从大到小记，后面调的人先看这一节。

### 一、时间永远停在 06:00 的真凶：一个判空写反了

`OnUpdateTicked` 里清理挡时间界面的那段：

```csharp
var blockingMenu = Game1.activeClickableMenu;
if (blockingMenu is TitleMenu || blockingMenu is DialogueBox ||
    blockingMenu.GetType().Name == "LetterViewerMenu")   // ← 前两个为 null 时是 false，但这一句直接崩
```

`activeClickableMenu` 为 null 时，前两个 `is` 判断都是 false，第三个 `GetType()` 抛
`NullReferenceException`。**之后每 tick 都抛一次，时钟推进、动作执行、状态推送、
种子货架诊断全部拿不到执行机会**——表现就是"她彻底不动了、时间钉在 06:00"。

早年没崩，是因为 NPC 对话框常年挂在 `activeClickableMenu` 上；一旦把对话盒清干净
（为了治 `shouldTimePass = false` 做的清理），这行就开始每帧崩。

改成 `blockingMenu != null && (...)` 之后时钟立刻走起来。

### 二、tick 外面包一层异常壳（别再让它整条链停掉）

`OnUpdateTicked` 拆成薄壳 + `OnUpdateTickedCore`：

- 壳里 `try/catch`，打 `tick 里出错了（第 N 次）: <完整堆栈>`，前 3 次与每 300 次各打一条
- 理由：SMAPI 一旦把这个 mod 的 `UpdateTicked` 拉黑，时钟、动作、状态推送会一起停
- **部署时 `NekoStardew.pdb` 必须跟 dll 一起拷**，否则堆栈里没有行号，上面那个 265 行就是这么找出来的

### 三、时钟推进走原版十分钟推进，不再手改 timeOfDay

`KeepClockMoving()` 按真实毫秒自攒（7 秒真实 = 10 游戏分钟），满一轮就反射调原版
`performTenMinuteClockUpdate()`：

- 直接 `Game1.timeOfDay += 10` 在多人在线存档上会现场抛 NRE（setter 会碰联机状态里未初始化的对象）
- 调原版方法则光照、通知、02:00 晕倒判定全都走游戏自己那套
- 日志格式：`时钟自己走：06:10（第 1 次推进，走原版十分钟推进）`

### 四、买种子：`SeedShop.cs`

之前完全没这个能力，`plant_seeds` 只会回"没有当季能种的种子"，然后空转。

`SeasonSeeds()` 运行时从 `Game1.objectData` 筛 `Category == -74`、`Price > 0` 的种子，
**不写死任何种子 ID**（换季、换游戏版本都不会拿错）。季节判断做了两路：

1. 拿一块已锄空的地当探针调 `canPlantThisSeedHere(id, false)`——最准
2. 探针拿不到（一格地都没锄）时，查反射拿到的 `Game1.cropData` 里这作物的 `Seasons`

只写第一路的时候，进档瞬间 / 没锄地的时候所有种子都被判成"不能种"，
买种子永远买不成。**两路都判断不出来时乐观放行**，让 `plant_seeds` 自己去试。

买的时候逐包扣钱 + `addItemToInventoryBool`；背包满会先自己腾格子（`MakeRoom`：
卖掉非工具、非种子的材料，一次最多两样），卖的钱正好抵种子钱。

### 五、agent 侧配套

- `PickMostUrgent` 的顺序：没种子且钱够 → 不在农场先 `go_to Farm` → `tilled < 1` 先锄一格
  → 否则才 `buy_seeds`。**先锄地是有依赖的**：没有空地就没有季节探针
- `Hub._mode` 默认从 `manual` 改成 `auto`，她自己过日子是主场景，不用每次重启后再手动开
- 纯"去某地"的指令只认农场范围内的地方：`Mine` / `Town` 直接拒绝。
  去了就回不来，人在那儿锄不动地也浇不了水，还让 `tilled` 归零

### 六、工程上两处必须知道的

- `StardewValley.GameData.dll` 必须引用：1.6 把 `Season`、`ObjectData`、`CropData`
  挪到了独立程序集，碰季节判断就会报 `CS0012`
- C# **不允许在 catch 块里 yield**（CS1631）。序列方法里所有异常都要先收进变量、
  出了 try 再报——这个坑踩过两次

### 这一轮跑通的链路（实测日志）

```
go_to(传送到 Farm 64,15) → till_soil(锄开 75,19) → buy_seeds(买到当季种子)
→ plant_seeds(种下齐瓜/辣椒/咖啡豆/小麦) → water_crops(浇水)
```

同一天内实测到 `crops` 从 2 涨到 18、`money` 450 → 938（卖材料腾格子的钱）。
时钟从 06:00 走到 12:30，节奏 7 秒真实 = 10 游戏分钟。

### 七、睡觉：两个坑，实测都堵上了

睡觉这一步卡得最久，连着三层，一层比一层深。

**第一层：剧情事件挡着。** `TrySleep` 原本见到 `Game1.eventUp || Game1.CurrentEvent != null`
就直接回"有剧情卡着，先不睡"。后台托管没有真人推进剧情，这个 event 会一直挂着，
于是她永远睡不了，时间一路跑到 26:00 强制晕倒——还要扣钱。

**第二层：只清 `Game1` 的静态字段清不掉。** 加了 `ClearEventForSleep()` 反射把 `Game1`
上所有 `Event` 类型、名字带 `CurrentEvent` 的字段置空，日志打出来的仍然是
`eventUp=False CurrentEvent=Event`——`eventUp` 清掉了，事件还在。原因是 **1.6 的
`Game1.CurrentEvent` 实际挂在当前地点上**。所以 `ClearCurrentEvent()`（tick 里那个）
和 `ClearEventForSleep()` 都要先遍历 `Game1.currentLocation` 的实例字段，把 `Event`
类型、名字带 `event` 的都置空，再清 `Game1` 自己的那一份。

**第三层：1.6 里没有 `endDay()`。** 反射 `GetMethod("endDay", ..., Type.EmptyTypes, null)`
拿到 null，回的是"这一版游戏里找不到结束一天的方法"。用 `ApiProbe` 把
`StardewValley.Game1` 的成员打出来才看到，睡觉真正要走的是：

```
Void newDayAfterFade(Action after)
```

原版玩家躺下之后就是它负责"存档 → 换天"，作物过夜生长、当日进度落盘都在里面。
现在的写法：

```csharp
var m = typeof(Game1).GetMethod("newDayAfterFade", flags, null,
                                new[] { typeof(Action) }, null);
Game1.newDay = true;
m.Invoke(null, new object[] { null });
```

实测日志：

```
收到任务 ...: sleep
[任务 ...] ok: 睡下了，这一天结束
→ time=07:50 day=3 money=786 crops=24
```

day 2 睡着、day 3 早上 07:50 在农场醒着，24 株作物原样在长。这一步通了，
"自己过日子"才算整圈闭上：早上锄地播种 → 傍晚出货 → 夜里睡觉 → 第二天接着长。

### 八、升级弹窗：画面卡在「升级 耕种 1 级」不动

托管跑了几天之后，实拍屏幕上是这样一个画面：游戏停在一个大对话框，
「升级 · 耕种 1 级 · +1 喷壶专精 · +1 锄头专精 · 新制造配方：稻草人 / 初级肥料」，
右下角一个 OK 按钮，**在等玩家点**。

`LevelUpMenu` 是典型的"要玩家确认才关"的菜单，原本那个清理列表里只有
`TitleMenu` / `DialogueBox` / `LetterViewerMenu` 三种，漏了它。所以每升一级，
她就站在那儿不动，时间照样走（因为时钟推进不依赖 `shouldTimePass`），
但人不干活了——看起来像"她又卡住了"。

现在清理列表加上 `LevelUpMenu`：

```csharp
var menuName = blockingMenu?.GetType().Name ?? "";
if (blockingMenu != null &&
    (blockingMenu is TitleMenu || blockingMenu is DialogueBox ||
     menuName == "LetterViewerMenu" || menuName == "LevelUpMenu"))
```

顺手把类型名先取到 `menuName` 里判，避免每次都 `GetType()`——之前那个
`NullReferenceException` 就是在这里判空写反炸的。

**代价：** 直接置空会跳过专精选择那个界面，那一级的两点专精不会入账。
后台托管没有真人点 OK，两害相权只能这样；要保住专精奖励，就得人工点一次，
或者以后改成反射点它的 OK 按钮。这一点写在 `已知限制` 里。

## 0.2.0：把空壳动作接上，把卡死的时间推开（2026-11-12）

### 三个原本只回一句话的动作，现在真会干了

| 动作 | 之前 | 现在 |
|---|---|---|
| `sleep` | 回“睡觉动作还没接上，这次先不睡” | 反射调 `Game1.endDay()`，真结束这一天（存档 + 过夜） |
| `clear_debris` | 回“还没接上，这次跳过” | 清杂草/石头/树枝/木头，一次最多 10 格，每格扣 2 体力 |
| `ship_items` | 回“还没接上，这次跳过” | 把背包里能卖的按售价卖断换钱，工具和种子留着 |

为什么 `ship_items` 是“卖断”而不是“放出货箱”：1.6 里已经没有 `StardewValley.Objects.ShippingBin`
这个类型（编译直接报 CS0234），`GameLocation` 上也没有 `getShippingBin`，
`Farm.getShippingBin(Farmer)` 返回的是 `IInventory`（没有 `addItem`）。
也就是说往出货箱里塞东西这条路在 1.6 的公开 API 里走不通，所以改成直接结算金币，效果等价。

`sleep` 为什么要反射：`Game1.endDay()` 在 1.6 里不是公开成员，直接写 `Game1.endDay()` 报 CS0117。
反射按名字拿 private static，拿不到就退回一句提示，不会抛异常把 mod 弄崩。

### 时间卡死的真正原因

`Game1.shouldTimePass()` 常年 false，`timeOfDay` 从进档起就钉在 06:00。两个原因叠在一起：

1. **清理阻塞界面的判断写窄了。** 原来只判 `blockingMenu is TitleMenu`，
   而日志里反复出现的那句“小伙子，早上好！”是个 `DialogueBox`——它不是 `TitleMenu` 的子类，
   所以从来没被清掉，一直挂着，`shouldTimePass()` 就一直是 false。
   现在把 `DialogueBox` 和 `LetterViewerMenu` 一并纳入清理。
2. **没人兜底。** 即使界面都清了，后台托管 + 窗口失焦的情况下 `shouldTimePass()` 依旧可能 false。
   所以加了 `KeepClockMoving()`：只要发现 `timeOfDay` 超过 8 秒没动，就自己把钟往前拨 10 分钟，
   到 26:00 停下不越过一天。日志里能看到“时间被卡住（shouldTimePass 为 false），手动推到 X”。

### 决策顺序也改了

`TaskPlanner.PickMostUrgent`：

- **22:00 先把活停下睡觉**（之前睡觉排在所有农活后面，她会被“还有活要干”一直拖到天亮又清零）
- **18:00 先把能卖的卖一次**，换出第二天买种子的本钱
- 没当季种子且地已锄满 18 格 → **清杂物**（之前直接返回 null，她就站着不动）

### 已知还没做的

- **买种子**：`buy_seeds` 没实现。背包里没有当季种子时，她只能锄地浇水清杂物，种不了东西。
- **锄地区域写死**在 `(62..82, 8..22)`，锄满之后不再扩。
- **时间靠手动拨钟**：作物日增长靠过夜（`endDay`），而白天的时间是 mod 自己推的，
  不是原版主循环走的，所以 NPC 作息、商店开关时间这些不会跟着变。