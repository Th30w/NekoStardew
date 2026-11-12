using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NekoStardew.Monitor;

public partial class MainWindow : Window
{
    private const string AgentHealthUrl = "http://127.0.0.1:48921/health";
    private const string AgentStateUrl = "http://127.0.0.1:48921/state";

    /// <summary>
    /// 画面链路（按优先级）：
    ///   1. 桌面复制（DXGI Desktop Duplication）直接抓游戏窗口那块屏幕区域 —— 这是 60 帧的主链路，
    ///      抓的是 GPU 合成结果，只要窗口在屏幕上可见就能满帧拿到真实画面。
    ///   2. 共享内存 Local\NekoStardewFrame —— 游戏进程内读 back buffer 的兜底，
    ///      这台机器 GPU→CPU 回读一帧要 50–170ms，只能跑 5 帧/秒，用于窗口被遮挡/最小化时。
    ///   3. mod 落盘的 frame.png —— 最后兜底。
    /// </summary>
    private const string ShmName = @"Local\NekoStardewFrame";
    private const int ShmHeaderSize = 64;

    private static readonly string FramePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NekoStardew",
        "frame.png");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private MemoryMappedFile? _shm;
    private MemoryMappedViewAccessor? _shmView;
    private byte[] _shmBuf = Array.Empty<byte>();
    private int _lastSeq = -1;
    private int _shmW;
    private int _shmH;
    private WriteableBitmap? _wb;
    private string _shmError = string.Empty;

    // ---- 桌面复制（60 帧主链路）----
    private DesktopDuplicator? _dup;
    private string _dupError = string.Empty;
    private bool _dupDead;
    private IntPtr _gameHwnd;
    private int _cropX, _cropY, _cropW, _cropH;
    private long _locateAtMs;
    private byte[] _dupBuf = Array.Empty<byte>();
    private int _dupW, _dupH;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(ConsoleWindowProc lpEnumFunc, IntPtr lParam);

    private delegate bool ConsoleWindowProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private int _consolesHidden;

    /// <summary>
    /// 星露谷通过 SMAPI 启动时会带一个控制台窗口，默认就落在 (64,72)，正好压在游戏画面上。
    /// 抓屏抓到的是桌面合成结果，控制台盖着就只能拍到控制台，所以先把它们收起来。
    /// </summary>
    private bool HideConsoleWindow(IntPtr hWnd, IntPtr lParam)
    {
        if (!IsWindowVisible(hWnd))
            return true;

        var sb = new System.Text.StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        var cls = sb.ToString();
        if (cls.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("CASCADIA", StringComparison.OrdinalIgnoreCase))
        {
            ShowWindow(hWnd, 6); // SW_MINIMIZE
            _consolesHidden++;
        }
        return true;
    }

    private void HideCoveringConsoles()
    {
        _consolesHidden = 0;
        try
        {
            EnumWindows(HideConsoleWindow, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败不影响抓画面
        }
        if (_consolesHidden > 0)
            Log($"已收起 {_consolesHidden} 个控制台窗口（它们会压在游戏画面上）");
    }

    private DateTime _lastFrameWrite = DateTime.MinValue;
    private string _lastFrameError = string.Empty;

    private int _fpsFrames;
    private DateTime _fpsStart = DateTime.UtcNow;
    private DateTime _fpsLogAt = DateTime.UtcNow;
    private double _fpsShown;

    private readonly ObservableCollection<string> _logs = new();

    /// <summary>设置 NEKO_MONITOR_DUMP=路径 时存一张 PNG，用于自检抓屏是否真的出画面。
    /// 启动后先等 6 秒：窗口刚被挪位置时游戏会重绘一片白，那时存下来的图没有参考价值。</summary>
    private string _dumpPath = Environment.GetEnvironmentVariable("NEKO_MONITOR_DUMP") ?? string.Empty;
    private readonly long _startMs = Environment.TickCount64;

    private bool _lastNeko;
    private bool _lastMod;
    private string _lastMode = string.Empty;
    private bool _lastBusy;
    private string _lastGoal = string.Empty;
    private bool _lastHasState;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("画面源：桌面复制抓游戏窗口（60 帧）→ 共享内存兜底 → PNG 最后的兜底");

        ArrangeWindows();

        _frameTimer.Tick += OnFrameTick;
        _pollTimer.Tick += OnPollTick;

        _frameTimer.Start();
        _pollTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _frameTimer.Stop();
        _pollTimer.Stop();
        _dup?.Dispose();
        _shmView?.Dispose();
        _shm?.Dispose();
        _http.Dispose();
    }

    // ---------------- 画面 ----------------

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (TryDxgiFrame())
            return;

        if (TryShmFrame())
            return;

        TryPngFrame();
    }

    // ---------------- 桌面复制：60 帧主链路 ----------------

    /// <summary>
    /// 把游戏窗口挪到屏幕左上角，监控窗自己站到它右边，保证两块区域不重叠 ——
    /// 桌面复制抓的是合成后的屏幕，一旦被别的窗口压住，抓到的就是遮挡物。
    /// 移动窗口不需要前台权限，也不会抢用户的键盘焦点。
    /// </summary>
    private void ArrangeWindows()
    {
        try
        {
            HideCoveringConsoles();

            var gw = WindowFinder.Find();
            if (gw == null)
            {
                Log("还没找到星露谷窗口，等它起来再排布");
                return;
            }

            _gameHwnd = gw.Handle;
            ShowWindow(_gameHwnd, 4); // SW_SHOWNOACTIVATE，亮出来但不抢焦点
            SetWindowPos(_gameHwnd, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);

            if (!LocateGameClientRect())
                return;

            var wa = SystemParameters.WorkArea;
            this.Width = 620;

            double right = _cropX + _cropW + 4;
            if (right + this.Width <= wa.Right)
            {
                this.Left = right;
                this.Top = Math.Max(wa.Top, _cropY);
            }
            else
            {
                this.Left = Math.Max(wa.Left, _cropX);
                this.Top = Math.Max(wa.Top, Math.Min(_cropY + _cropH + 4, wa.Bottom - 360));
            }

            Log($"游戏窗口已排到 ({_cropX},{_cropY}) {_cropW}×{_cropH}，监控窗放在 ({this.Left},{this.Top})");
        }
        catch (Exception ex)
        {
            Log("排布窗口失败：" + ex.Message);
        }
    }

    private bool LocateGameClientRect()
    {
        if (_gameHwnd == IntPtr.Zero)
        {
            var gw = WindowFinder.Find();
            if (gw == null) return false;
            _gameHwnd = gw.Handle;
        }

        if (!GetClientRect(_gameHwnd, out var rc)) return false;

        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return false;

        var pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_gameHwnd, ref pt)) return false;

        _cropX = pt.X;
        _cropY = pt.Y;
        _cropW = w;
        _cropH = h;
        return true;
    }

    private bool TryDxgiFrame()
    {
        if (_dupDead) return false;

        if (_dup == null)
        {
            try
            {
                _dup = new DesktopDuplicator();
                Log("桌面复制已启动，抓游戏窗口区域，目标 60 帧");
            }
            catch (Exception ex)
            {
                _dupDead = true;
                _dupError = ex.Message;
                Log("桌面复制不可用（" + ex.Message + "），退回共享内存兜底");
                return false;
            }
        }

        var now = Environment.TickCount64;
        if (_cropW <= 0 || now - _locateAtMs > 1000)
        {
            _locateAtMs = now;
            if (!LocateGameClientRect())
                return false;
        }

        var frame = _dup.Grab(_cropX, _cropY, _cropW, _cropH, 16);
        if (frame == null)
        {
            // 桌面这一下没有新画面（静止或暂时拿不到），保持上一帧，别闪回占位图
            return _dupW > 0;
        }

        int w = frame.Width, h = frame.Height;
        if (_wb == null || _dupW != w || _dupH != h)
        {
            _dupW = w;
            _dupH = h;
            _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            ScreenImage.Source = _wb;
            CaptionText.Text = $"实时画面 · {w}×{h}";
        }

        _wb.WritePixels(new Int32Rect(0, 0, w, h), frame.Pixels, w * 4, 0);
        PlaceholderText.Visibility = Visibility.Collapsed;

        CountFrame();
        DumpIfRequested();
        return true;
    }

    private void CountFrame()
    {
        _fpsFrames++;
        var now = DateTime.UtcNow;
        var span = (now - _fpsStart).TotalSeconds;
        if (span < 1.0) return;

        _fpsShown = _fpsFrames / span;
        _fpsFrames = 0;
        _fpsStart = now;
        FrameText.Text = $"{Math.Round(_fpsShown)} 帧/秒　{_dupW}×{_dupH}";

        if (now - _fpsLogAt > TimeSpan.FromSeconds(5))
        {
            _fpsLogAt = now;
            Log($"实时帧率 {Math.Round(_fpsShown)} 帧/秒（{_dupW}×{_dupH}，桌面复制）");
        }
    }

    /// <summary>读共享内存里最新那帧，帧号没变就直接返回，不做多余拷贝。</summary>
    private bool TryShmFrame()
    {
        if (_shm == null)
        {
            try
            {
                _shm = MemoryMappedFile.OpenExisting(ShmName);
                _shmView = _shm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                Log("共享内存已连上，开始实时显示");
            }
            catch (Exception ex)
            {
                var msg = ex.GetType().Name;
                if (msg != _shmError)
                {
                    _shmError = msg;
                    Log("等待游戏端共享内存（" + msg + "），先看 PNG 兜底");
                }
                return false;
            }
        }

        try
        {
            int magic = _shmView!.ReadInt32(0);
            if (magic != 0x4E4B4652) return false;

            int w = _shmView.ReadInt32(4);
            int h = _shmView.ReadInt32(8);
            int active = _shmView.ReadInt32(12);
            int seq = _shmView.ReadInt32(16);
            if (w <= 0 || h <= 0 || active < 0 || active > 1) return false;
            if (seq == _lastSeq && _wb != null) return true;

            int size = w * h * 4;
            if (_shmBuf.Length != size) _shmBuf = new byte[size];
            _shmView.ReadArray(ShmHeaderSize + active * size, _shmBuf, 0, size);

            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                ScreenImage.Source = _wb;
                _shmW = w;
                _shmH = h;
                CaptionText.Text = $"实时画面 · {w}×{h}";
            }

            _wb.WritePixels(new Int32Rect(0, 0, w, h), _shmBuf, w * 4, 0);
            PlaceholderText.Visibility = Visibility.Collapsed;

            if (seq != _lastSeq)
            {
                _lastSeq = seq;
                _fpsFrames++;
            }

            var now = DateTime.UtcNow;
            var span = (now - _fpsStart).TotalSeconds;
            if (span >= 1.0)
            {
                _fpsShown = _fpsFrames / span;
                _fpsFrames = 0;
                _fpsStart = now;
                FrameText.Text = $"{Math.Round(_fpsShown)} 帧/秒　{_shmW}×{_shmH}";
            }

            DumpIfRequested();
            return true;
        }
        catch (Exception ex)
        {
            var msg = ex.GetType().Name + ": " + ex.Message;
            if (msg != _lastFrameError)
            {
                _lastFrameError = msg;
                Log("读共享内存失败：" + msg);
            }
            return false;
        }
    }

    /// <summary>共享内存还没建起来时（游戏没进档），退化到读 mod 落盘的 PNG。</summary>
    private void TryPngFrame()
    {
        try
        {
            if (!System.IO.File.Exists(FramePath))
            {
                PlaceholderText.Text = "等待游戏画面";
                PlaceholderText.Visibility = Visibility.Visible;
                CaptionText.Text = "实时画面";
                FrameText.Text = "未收到帧";
                return;
            }

            var write = System.IO.File.GetLastWriteTimeUtc(FramePath);
            if (write == _lastFrameWrite) return;

            BitmapImage bmp;
            using (var fs = new System.IO.FileStream(
                       FramePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var ms = new System.IO.MemoryStream())
            {
                fs.CopyTo(ms);
                ms.Position = 0;
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
            }

            _lastFrameWrite = write;
            ScreenImage.Source = bmp;
            PlaceholderText.Visibility = Visibility.Collapsed;
            CaptionText.Text = $"实时画面 · {bmp.PixelWidth}×{bmp.PixelHeight}（PNG 兜底）";
            FrameText.Text = $"PNG 兜底　{write.ToLocalTime():HH:mm:ss} 更新";
            DumpIfRequested();
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg != _lastFrameError)
            {
                _lastFrameError = msg;
                Log("读 PNG 失败：" + msg);
            }
        }
    }

    private void DumpIfRequested()
    {
        if (_dumpPath.Length == 0) return;
        if (Environment.TickCount64 - _startMs < 6000) return;
        var path = _dumpPath;
        _dumpPath = string.Empty;
        try
        {
            if (ScreenImage.Source is BitmapSource src)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(src));
                using var fs = System.IO.File.Create(path);
                encoder.Save(fs);
                Log("已保存自检帧：" + path);
            }
        }
        catch (Exception ex)
        {
            Log("保存自检帧失败：" + ex.Message);
        }
    }

    // ---------------- 状态 ----------------

    private async void OnPollTick(object? sender, EventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(AgentHealthUrl));
            var root = doc.RootElement;

            var neko = root.TryGetProperty("nekoConnected", out var n) && n.GetBoolean();
            var mod = root.TryGetProperty("modConnected", out var m) && m.GetBoolean();
            var mode = root.TryGetProperty("mode", out var md) ? md.GetString() ?? "--" : "--";
            var busy = root.TryGetProperty("busy", out var b) && b.GetBoolean();
            var goal = root.TryGetProperty("currentGoal", out var g) ? g.GetString() ?? "" : "";
            var hasState = root.TryGetProperty("hasState", out var hs) && hs.GetBoolean();

            PluginLinkText.Text = neko ? "已连接" : "未连接";
            PluginLinkText.Foreground = neko ? Brushes.Black : Brushes.Gray;
            ModLinkText.Text = mod ? "已连接" : "未连接";
            ModLinkText.Foreground = mod ? Brushes.Black : Brushes.Gray;
            ModeText.Text = mode;
            GoalText.Text = busy ? (string.IsNullOrEmpty(goal) ? "执行中" : goal) : "空闲";

            if (neko != _lastNeko)
            {
                Log(neko ? "插件已连上 agent" : "插件连接断开");
                _lastNeko = neko;
            }
            if (mod != _lastMod)
            {
                Log(mod ? "游戏端已连上 agent" : "游戏端连接断开");
                _lastMod = mod;
            }
            if (mode != _lastMode)
            {
                if (_lastMode.Length > 0) Log($"托管模式 -> {mode}");
                _lastMode = mode;
            }
            if (busy != _lastBusy)
            {
                Log(busy ? "开始执行任务" : "任务结束");
                _lastBusy = busy;
            }
            if (!string.Equals(goal, _lastGoal, StringComparison.Ordinal))
            {
                if (busy && !string.IsNullOrEmpty(goal)) Log("目标：" + goal);
                _lastGoal = goal;
            }
            if (hasState != _lastHasState)
            {
                Log(hasState ? "收到农场快照" : "农场快照丢失");
                _lastHasState = hasState;
            }

            if (hasState)
                await TryFillFarmState();
            else
                ClearFarmState();
        }
        catch (Exception)
        {
            PluginLinkText.Text = "未连接";
            PluginLinkText.Foreground = Brushes.Gray;
            ModLinkText.Text = "未连接";
            ModLinkText.Foreground = Brushes.Gray;
            ModeText.Text = "--";
            ClearFarmState();
        }
    }

    private async System.Threading.Tasks.Task TryFillFarmState()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(AgentStateUrl));
            var s = doc.RootElement;

            DateText.Text = $"{Str(s, "season")} {Num(s, "day")} 日";
            TimeText.Text = Str(s, "time");
            WeatherText.Text = Str(s, "weather");
            MoneyText.Text = Num(s, "money").ToString("N0") + " G";
            StaminaText.Text = $"{NumD(s, "energy"):0} / {NumD(s, "maxEnergy"):0}";
            LocationText.Text = Str(s, "location");

            var crops = s.TryGetProperty("crops", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.GetArrayLength() : 0;
            var animals = s.TryGetProperty("animals", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.GetArrayLength() : 0;
            CropText.Text = $"{crops} 块地 / {animals} 只动物";
        }
        catch
        {
            // 状态偶尔取不到不算错误，下一秒再试
        }
    }

    private void ClearFarmState()
    {
        DateText.Text = "--";
        TimeText.Text = "--";
        WeatherText.Text = "--";
        MoneyText.Text = "--";
        StaminaText.Text = "--";
        LocationText.Text = "--";
        CropText.Text = "--";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "--" : "--";

    private static int Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double NumD(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // ---------------- 日志 ----------------

    private static readonly string DiagnosticLog =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neko-stardew-monitor.log");

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _logs.Add(line);
        while (_logs.Count > 300)
            _logs.RemoveAt(0);
        LogList.ScrollIntoView(line);
        try { System.IO.File.AppendAllText(DiagnosticLog, line + Environment.NewLine); } catch { }
    }
}
