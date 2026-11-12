using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NekoStardew.Agent;

var jsonOpts = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var hub = new Hub(jsonOpts);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:48921");

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", () => Results.Json(hub.Snapshot()));
app.MapGet("/state", () => Results.Text(hub.StateJson(), "application/json"));

// 插件走 /neko，SMAPI mod 走 /mod；两条连接职责不同，不共用一套会话语义。
app.Map("/neko", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    hub.AttachNeko(ws);
    try
    {
        await hub.PumpAsync(ws, isMod: false, ctx.RequestAborted);
    }
    finally
    {
        hub.DetachNeko(ws);
    }
});

app.Map("/mod", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    hub.AttachMod(ws);
    try
    {
        await hub.PumpAsync(ws, isMod: true, ctx.RequestAborted);
    }
    finally
    {
        hub.DetachMod(ws);
    }
});

// 自主经营的心跳：只有 mod 空闲、模式为 auto 时才由 agent 自己派活，
// 避免和对话模型刚派下去的任务互相踩。
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
    while (await timer.WaitForNextTickAsync())
    {
        try { await hub.AutoTickAsync(); }
        catch { /* 心跳不能把进程带走 */ }
    }
});

app.Run();

public sealed class Hub
{
    private readonly JsonSerializerOptions _json;
    private readonly TaskPlanner _planner = new();
    private readonly object _sync = new();

    private WebSocket? _neko;
    private WebSocket? _mod;
    private FarmState _state = new();
    // 默认就是 auto：她自己过日子是这个插件的主场景，不需要每次重启后再手动开一遍。
    // 想让她只等你说话，启动后发一条 {type:"command", mode:"manual"} 就行。
    private string _mode = "auto";
    private bool _modBusy;
    private string? _currentTaskId;
    private string _currentGoal = "";
    private DateTime _lastAutoAt = DateTime.MinValue;
    private int _autoIntervalSeconds = 15;

    public Hub(JsonSerializerOptions json) => _json = json;

    // ---------------- 连接管理 ----------------

    public void AttachNeko(WebSocket ws)
    {
        lock (_sync) _neko = ws;
        Console.WriteLine("[hub] neko attached");
        _ = SendToNekoAsync(new OutboundFrame
        {
            Type = "log",
            Text = "星露谷桥已连接，等待农场实例。",
        });
    }

    public void DetachNeko(WebSocket ws)
    {
        lock (_sync) if (ReferenceEquals(_neko, ws)) _neko = null;
        Console.WriteLine("[hub] neko detached");
    }

    public void AttachMod(WebSocket ws)
    {
        lock (_sync) _mod = ws;
        Console.WriteLine("[hub] mod attached");
        _ = SendToModAsync(new AgentCommand { Type = "hello", Mode = Mode });
    }

    public void DetachMod(WebSocket ws)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_mod, ws)) _mod = null;
            _modBusy = false;
            _currentTaskId = null;
            _currentGoal = "";
        }
        Console.WriteLine("[hub] mod detached");
    }

    private string Mode { get { lock (_sync) return _mode; } }

    // ---------------- 收发循环 ----------------

    public async Task PumpAsync(WebSocket ws, bool isMod, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var chunk = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }
                chunk.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue; // 截图帧可能被切多片

                var text = chunk.ToString();
                chunk.Clear();
                if (text.Length == 0) continue;

                InboundFrame? frame;
                try { frame = JsonSerializer.Deserialize<InboundFrame>(text, _json); }
                catch { continue; }
                if (frame is null) continue;

                try
                {
                    if (isMod) await HandleModFrameAsync(frame);
                    else await HandleNekoFrameAsync(frame);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[hub] frame error type={frame.Type}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[hub] pump ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // 插件侧：task / command
    private async Task HandleNekoFrameAsync(InboundFrame f)
    {
        switch (f.Type)
        {
            case "task":
                await OnPluginTaskAsync(f);
                break;

            case "command":
                await OnPluginCommandAsync(f);
                break;
        }
    }

    private async Task OnPluginTaskAsync(InboundFrame f)
    {
        var taskText = (f.Task ?? "").Trim();
        var taskId = f.TaskId ?? Guid.NewGuid().ToString();
        if (taskText.Length == 0)
        {
            await ReplyFailedAsync(taskId, "任务为空");
            return;
        }

        FarmState snapshot;
        bool modUp;
        lock (_sync)
        {
            snapshot = _state;
            modUp = _mod is not null && _mod.State == WebSocketState.Open;
            if (_modBusy)
            {
                _ = ReplyFailedAsync(taskId, "mod 还在执行上一个动作");
                return;
            }
            _modBusy = true;
            _currentTaskId = taskId;
        }

        if (!modUp)
        {
            lock (_sync) _modBusy = false;
            await ReplyFailedAsync(taskId, "星露谷实例没连上");
            return;
        }

        var plan = _planner.Plan(taskText, snapshot);
        if (!plan.Ok)
        {
            lock (_sync) { _modBusy = false; _currentTaskId = null; }
            await ReplyFailedAsync(taskId, plan.Reason ?? "无法拆解这个任务");
            await SendToNekoAsync(OutboundFrame.Make("log",
                $"任务「{Trim(taskText, 20)}」没能拆成动作：{plan.Reason}"));
            return;
        }

        await DispatchAsync(taskId, plan.Goal, plan.Actions);
    }

    private async Task OnPluginCommandAsync(InboundFrame f)
    {
        switch ((f.Action ?? "").ToLowerInvariant())
        {
            case "status":
                FarmState snapshot;
                lock (_sync) snapshot = _state;
                await SendToNekoAsync(new OutboundFrame { Type = "state", Data = snapshot });
                break;

            case "set_mode":
                var mode = (f.Mode ?? "auto").ToLowerInvariant();
                if (mode is not ("idle" or "auto" or "manual")) mode = "auto";
                lock (_sync) _mode = mode;
                await SendToModAsync(new AgentCommand { Type = "command", Mode = mode });
                await SendToNekoAsync(new OutboundFrame
                {
                    Type = "agent_status",
                });
                Console.WriteLine($"[hub] mode -> {mode}");
                break;
        }
    }

    // mod 侧：state / screenshot / log / task_finished / event
    private async Task HandleModFrameAsync(InboundFrame f)
    {
        switch (f.Type)
        {
            case "state":
                var snapshot = f.Snapshot;
                if (snapshot is not null)
                {
                    snapshot.Mode = Mode;
                    lock (_sync) _state = snapshot;
                    await SendToNekoAsync(new OutboundFrame { Type = "state", Data = snapshot });
                }
                break;

            case "screenshot":
                await SendToNekoAsync(new OutboundFrame
                {
                    Type = "screenshot",
                    Image = f.Image,
                    Encoding = f.Encoding ?? "jpeg",
                });
                break;

            case "log":
                await SendToNekoAsync(OutboundFrame.Make("log", f.Text ?? ""));
                break;

            case "event":
                await SendToNekoAsync(new OutboundFrame
                {
                    Type = "event",
                    Kind = f.Kind,
                    Text = f.Text,
                });
                break;

            case "task_finished":
                lock (_sync)
                {
                    _modBusy = false;
                    _currentTaskId = null;
                    _currentGoal = "";
                }
                await SendToNekoAsync(new OutboundFrame
                {
                    Type = "task_finished",
                    TaskId = f.TaskId,
                    Status = f.Status ?? "ok",
                    Text = f.Text ?? "",
                });
                break;
        }
    }

    // ---------------- 自主经营 ----------------

    public async Task AutoTickAsync()
    {
        string mode;
        bool idle;
        FarmState snapshot;
        lock (_sync)
        {
            mode = _mode;
            idle = !_modBusy && _mod is not null && _mod.State == WebSocketState.Open;
            snapshot = _state;
        }
        if (mode != "auto" || !idle) return;
        if ((DateTime.UtcNow - _lastAutoAt).TotalSeconds < _autoIntervalSeconds) return;
        if (string.IsNullOrEmpty(snapshot.Season)) return; // 还没有状态，别瞎派

        var next = _planner.PickMostUrgent(snapshot);
        if (next is null) return;

        var taskId = Guid.NewGuid().ToString();
        lock (_sync)
        {
            _modBusy = true;
            _currentTaskId = taskId;
            _currentGoal = TaskPlanner.Describe(next.Op);
        }
        _lastAutoAt = DateTime.UtcNow;
        await DispatchAsync(taskId, TaskPlanner.Describe(next.Op), new List<FarmAction> { next });
    }

    private async Task DispatchAsync(string taskId, string goal, List<FarmAction> actions)
    {
        lock (_sync)
        {
            _modBusy = true;
            _currentTaskId = taskId;
            _currentGoal = goal;
        }
        await SendToModAsync(new AgentCommand
        {
            Type = "task",
            TaskId = taskId,
            Goal = goal,
            Actions = actions,
        });
        Console.WriteLine($"[hub] dispatch {goal} -> {string.Join(",", actions.Select(a => a.Op))}");
        await SendToNekoAsync(OutboundFrame.Make("log", $"开始：{goal}"));
    }

    // ---------------- 底层发送 ----------------

    private async Task SendToNekoAsync(OutboundFrame frame) =>
        await SendAsync(isMod: false, JsonSerializer.Serialize(frame, _json));

    private async Task SendToModAsync(AgentCommand frame) =>
        await SendAsync(isMod: true, JsonSerializer.Serialize(frame, _json));

    private async Task SendAsync(bool isMod, string payload)
    {
        WebSocket? ws;
        lock (_sync) ws = isMod ? _mod : _neko;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            // 同一个 socket 上并发 Send 是非法的，这里用 Monitor 串行化。
            lock (ws)
            {
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                  .GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hub] send failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ReplyFailedAsync(string taskId, string reason)
    {
        await SendToNekoAsync(new OutboundFrame
        {
            Type = "task_finished",
            TaskId = taskId,
            Status = "failed",
            Text = reason,
        });
    }

    public object Snapshot()
    {
        lock (_sync)
        {
            return new
            {
                nekoConnected = _neko is not null && _neko.State == WebSocketState.Open,
                modConnected = _mod is not null && _mod.State == WebSocketState.Open,
                mode = _mode,
                busy = _modBusy,
                currentGoal = _currentGoal,
                hasState = !string.IsNullOrEmpty(_state.Season),
                season = _state.Season,
                day = _state.Day,
                time = _state.Time,
            };
        }
    }

    /// <summary>完整农场快照，给监控面板用。</summary>
    public string StateJson()
    {
        lock (_sync) return JsonSerializer.Serialize(_state, _json);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
