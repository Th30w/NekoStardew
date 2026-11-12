# 星露谷猫娘 · NekoStardew

让 N.E.K.O 的猫娘住进《星露谷物语》：她能看见你的农场、跟着你一起干活、边聊边玩。

这是**插件壳**部分。完整项目还有三层：SMAPI mod（游戏内执行）、桌面 agent（任务规划）、DXGI 监控端（画面采集），见仓库根目录。

## 她能做什么

- **看得见**：mod 每帧抓一张 960px 的 JPEG 通过共享内存喂给监控端，由插件转成视觉输入交给模型。作物状态、背包、天气、时间、NPC 位置都在快照里。
- **聊得来**：走 N.E.K.O 的常规对话通道，不打断语音。插件以 20 秒为间隔把农场快照压成系统提示，猫娘会主动接话（"西边那块地该浇水了"）。
- **下得去手**：任务交给 agent 规划成动作序列，再由 mod 在游戏里真正执行——走位、挥锄、浇水、播种、收获、进商店买种子。执行有超时（默认 180 秒）和回合上限。

## 需要什么

| 组件 | 说明 |
| --- | --- |
| 《星露谷物语》 | 1.6.x，装 SMAPI 4.x |
| SMAPI mod | `mod/NekoStardew`，编译后丢进 `Mods/` |
| 桌面 agent | `agent/NekoStardew.Agent`，.NET，负责动作规划 |
| 监控端 | `monitor/NekoStardew.Monitor`，DXGI 桌面复制，采集游戏窗口画面 |
| N.E.K.O | 支持插件运行时（`>=0.1.0,<0.2.0` 推荐） |

四层之间只走一个 WebSocket：`ws://localhost:48921/neko`。

## 配置

主配置在 `plugin.toml`，常用项：

```toml
[stardew]
ws_url = "ws://localhost:48921/neko"     # 四层互联地址
reconnect_interval_seconds = 5.0
task_timeout_seconds = 180.0
system_prompt_interval_seconds = 20.0    # 主动陪伴节奏
stream_screenshots_to_llm = true
screenshot_max_edge_px = 960
screenshot_jpeg_quality = 78
```

画面质量换延迟：`screenshot_max_edge_px` 降到 640、质量降到 60，弱机上会更顺。

## 启动顺序

1. 装好 SMAPI，进游戏，确认 mod 加载（控制台出现 `NekoStardew` 初始化日志）。
2. 起 agent 与监控端（或直接用 `tools/Start-Neko.ps1` 一把起）。
3. N.E.K.O 里启用本插件，`plugin_runtime.auto_start = true` 时随主程序自启。

端口没通的话，插件会在日志里连续报 `ws 连接失败`，不影响猫娘正常聊天。

## 已知边界

- 只支持单人存档；联机时快照只反映主机视角。
- 动作执行依赖 mod 暴露的接口，游戏版本跨大版本升级需要重新对齐。
- 首次进入新地图（矿洞、沙漠）需要等一帧完整快照，前几秒可能看不到场景。

## 反馈

出问题带上 `_pack/` 下的日志和当时截图，联系 2364573684。

## 许可

随 N.E.K.O 插件市场规范发布，代码以仓库 LICENSE 为准。
