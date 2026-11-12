using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SmokeTest;

/// <summary>
/// 不依赖星露谷本身，用两个 WebSocket 客户端分别扮演 SMAPI mod 和 N.E.K.O 插件，
/// 验证 agent 的协议闭环：state 转发 → 任务拆解 → 指令下发 → 回执回传。
/// </summary>
internal static class Program
{
    private const string Base = "ws://127.0.0.1:48921";

    private static async Task<int> Main()
    {
        var failures = new List<string>();

        // ---- 1. 扮演 mod 连接 ----
        using var mod = new ClientWebSocket();
        await ConnectAsync(mod, Base + "/mod").ConfigureAwait(false);
        Console.WriteLine("[test] mod connected");

        // ---- 2. 扮演插件连接 ----
        using var neko = new ClientWebSocket();
        await ConnectAsync(neko, Base + "/neko").ConfigureAwait(false);
        Console.WriteLine("[test] neko connected");

        // 连上后会先收到一条问候日志，读掉避免干扰后续断言。
        _ = await ReceiveAsync(neko, 2000).ConfigureAwait(false);

        // ---- 3. mod 推送农场快照 ----
        var state = new
        {
            type = "state",
            data = new
            {
                season = "spring",
                day = 13,
                year = 1,
                time = "06:20",
                weather = "sunny",
                money = 1200,
                energy = 250.0,
                maxEnergy = 270.0,
                location = "Farm",
                mode = "auto",
                crops = new object[]
                {
                    new { name = "Parsnip", stage = "growing", daysLeft = 2, watered = false, harvestable = false },
                    new { name = "Parsnip", stage = "growing", daysLeft = 2, watered = true, harvestable = false },
                    new { name = "Cauliflower", stage = "mature", daysLeft = 0, watered = true, harvestable = true },
                },
                animals = new object[]
                {
                    new { name = "Chicken", building = "Coop", fed = false, hearts = 2 },
                },
                inventory = new object[] { new { name = "Parsnip Seeds", count = 15 } },
            },
        };
        await SendAsync(mod, JsonSerializer.Serialize(state)).ConfigureAwait(false);
        Console.WriteLine("[test] mod sent state");

        // ---- 4. 断言插件侧收到了转发的 state ----
        var nekoGot = await ReceiveUntilAsync(neko, "\"type\":\"state\"", 3000).ConfigureAwait(false);
        if (nekoGot is null || !nekoGot.Contains("\"type\":\"state\""))
            failures.Add($"插件没有收到 state 转发: {Trunc(nekoGot)}");
        else
            Console.WriteLine("[test] PASS state forwarded to plugin");

        // ---- 5. 插件下发任务 ----
        var taskId = Guid.NewGuid().ToString();
        var task = new { type = "task", task = "给所有没浇水的作物浇水", task_id = taskId };
        await SendAsync(neko, JsonSerializer.Serialize(task)).ConfigureAwait(false);
        Console.WriteLine("[test] plugin sent task");

        // ---- 6. 断言 mod 收到了 water_crops 指令 ----
        var modGot = await ReceiveUntilAsync(mod, "\"type\":\"task\"", 3000).ConfigureAwait(false);
        if (modGot is null)
        {
            failures.Add("mod 没收到任何指令");
        }
        else if (!modGot.Contains("\"op\":\"water_crops\""))
        {
            failures.Add($"mod 收到的指令不对: {Trunc(modGot)}");
        }
        else
        {
            Console.WriteLine("[test] PASS task decomposed into water_crops");
            Console.WriteLine("       " + Trunc(modGot));
        }

        // ---- 7. mod 回执，断言插件收到 ----
        var done = new { type = "task_finished", task_id = taskId, status = "ok", text = "浇了 2 块地" };
        await SendAsync(mod, JsonSerializer.Serialize(done)).ConfigureAwait(false);

        var nekoDone = await ReceiveUntilAsync(neko, "\"type\":\"task_finished\"", 3000).ConfigureAwait(false);
        if (nekoDone is null || !nekoDone.Contains("\"status\":\"ok\""))
            failures.Add($"插件没收到完成回执: {Trunc(nekoDone)}");
        else
            Console.WriteLine("[test] PASS task_finished returned to plugin");

        // ---- 8. 泛指令：状态里有急事时应该转成最紧急的动作，而不是硬拒 ----
        var vague = new { type = "task", task = "随便整点啥吧", task_id = Guid.NewGuid().ToString() };
        await SendAsync(neko, JsonSerializer.Serialize(vague)).ConfigureAwait(false);
        var modVague = await ReceiveUntilAsync(mod, "\"type\":\"task\"", 3000).ConfigureAwait(false);
        if (modVague is null || !modVague.Contains("\"op\":\"harvest_crops\""))
            failures.Add($"泛指令没有转成最急动作: {Trunc(modVague)}");
        else
            Console.WriteLine("[test] PASS vague task -> most urgent action");

        await SendAsync(mod, JsonSerializer.Serialize(new
        {
            type = "task_finished",
            status = "ok",
            text = "收好了",
        })).ConfigureAwait(false);

        // ---- 9. 完全无关的指令应被拒并给出原因 ----
        var offTopic = new { type = "task", task = "给我讲个笑话", task_id = Guid.NewGuid().ToString() };
        await SendAsync(neko, JsonSerializer.Serialize(offTopic)).ConfigureAwait(false);
        var nekoReject = await ReceiveUntilAsync(neko, "\"status\":\"failed\"", 3000).ConfigureAwait(false);
        if (nekoReject is null || !nekoReject.Contains("\"status\":\"failed\""))
            failures.Add($"无关指令没有按预期拒绝: {Trunc(nekoReject)}");
        else
            Console.WriteLine("[test] PASS off-topic task rejected with reason");

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("RESULT: PASS  全部 5 项断言通过");
            return 0;
        }
        Console.WriteLine("RESULT: FAIL");
        foreach (var f in failures) Console.WriteLine("  - " + f);
        return 1;
    }

    private static async Task ConnectAsync(ClientWebSocket ws, string url)
    {
        await ws.ConnectAsync(new Uri(url), CancellationToken.None);
    }

    private static async Task SendAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (r.EndOfMessage) return sb.ToString();
            }
        }
        catch (OperationCanceledException)
        {
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }

    /// <summary>读到含指定标记的帧才返回，跳过问候、日志等噪声帧。</summary>
    private static async Task<string?> ReceiveUntilAsync(ClientWebSocket ws, string marker, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remain = Math.Max(200, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            var msg = await ReceiveAsync(ws, remain).ConfigureAwait(false);
            if (msg is not null && msg.Contains(marker, StringComparison.Ordinal)) return msg;
        }
        return null;
    }

    private static string Trunc(string? s) =>
        s is null ? "<null>" : (s.Length <= 160 ? s : s[..160] + "…");
}
