using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace NekoStardew
{
    /// <summary>
    /// 到 agent 的长连接。发送走队列，接收在后台任务里，不阻塞游戏主线程。
    /// </summary>
    public class AgentLink
    {
        private readonly IMonitor _monitor;
        private readonly Uri _uri;
        private readonly ConcurrentQueue<string> _outbox = new ConcurrentQueue<string>();

        private CancellationTokenSource? _cts;
        private ClientWebSocket? _socket;
        private Task? _loop;

        public event Action<string>? FrameReceived;

        public AgentLink(IMonitor monitor, string url)
        {
            _monitor = monitor;
            _uri = new Uri(url);
        }

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        public void Start()
        {
            if (_loop != null && !_loop.IsCompleted) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
        }

        /// <summary>从任意线程排一条消息，由后台发送循环真正写出去。</summary>
        public void Send(string json)
        {
            if (!string.IsNullOrEmpty(json)) _outbox.Enqueue(json);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = new ClientWebSocket();
                try
                {
                    await ws.ConnectAsync(_uri, ct);
                    _socket = ws;
                    _monitor.Log("已连上 agent " + _uri, LogLevel.Info);

                    var sender = SendLoopAsync(ws, ct);
                    await ReceiveLoopAsync(ws, ct);
                    await sender;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _monitor.Log("agent 连接异常: " + ex.Message, LogLevel.Warn);
                }
                finally
                {
                    _socket = null;
                    try { ws.Dispose(); } catch { }
                }

                try
                {
                    await Task.Delay(3000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (!_outbox.TryDequeue(out var json))
                {
                    try
                    {
                        await Task.Delay(50, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    continue;
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                catch
                {
                    // 掉线时把消息塞回去，重连后还能发出去
                    _outbox.Enqueue(json);
                    break;
                }
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                if (sb.Length > 0)
                {
                    try
                    {
                        FrameReceived?.Invoke(sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        _monitor.Log("帧处理出错: " + ex.Message, LogLevel.Error);
                    }
                }
            }
        }
    }
}
