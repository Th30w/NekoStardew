using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace NekoStardew
{
    public class ModConfig
    {
        public string AgentUrl { get; set; } = "ws://127.0.0.1:48921/mod";
        public int SampleIntervalMs { get; set; } = 500;

        /// <summary>停在标题界面时自己把最近存档读进来，不需要人手点。</summary>
        public bool AutoLoadSave { get; set; } = true;

        /// <summary>指定存档目录名，留空则取最新改动的那个。</summary>
        public string AutoLoadSaveName { get; set; } = "";

        /// <summary>
        /// 实时画面落盘路径。留空则写到 %APPDATA%\NekoStardew\frame.png。
        /// 画面是从游戏自己的 D3D11 back buffer 里直接读的，外部抓窗拿不到内容。
        /// </summary>
        public string FrameOutPath { get; set; } = "";

        /// <summary>落盘间隔（毫秒），0 表示关闭落盘。留一张图当截图证据即可，间隔不宜短：每次落盘都要 GPU 回读。</summary>
        public int FrameIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 共享内存传帧的上限帧率，监控窗显示就跟着这个走。0 表示关闭共享内存传帧。
        ///
        /// 实测（AMD R7 200 + 远程串流虚拟显示器）：回读一帧 640×360 要 58 毫秒（MonoGame 的
        /// GetBackBufferData），换成原生 glReadPixels 读 BGRA 反而要 108–165 毫秒。
        /// 也就是最多每秒十几帧，而每读一次都会把游戏的 Draw 往后拖。
        /// 所以默认关掉，画面改由监控窗直接用桌面复制抓游戏窗口——那条路不碰游戏的 GPU。
        /// </summary>
        public int SharedMemoryMaxFps { get; set; } = 0;

        /// <summary>
        /// 进档后把游戏窗口最小化。
        /// 试过：最小化之后 swap 并没有变快（实测仍是每秒四五帧），而且窗口一最小化，
        /// 监控窗的桌面复制就抓不到游戏画面了。所以默认关掉，让窗口留在屏幕上。
        /// </summary>
        public bool HideGameWindow { get; set; } = false;
    }

    public class FarmAction
    {
        public string Op { get; set; } = "";
        public string? Target { get; set; }

        /// <summary>数量/预算。买种子时表示这一趟最多花多少金，其余动作用不到。</summary>
        public int Count { get; set; }
    }

    public class AgentCommand
    {
        public string? TaskId { get; set; }
        public List<FarmAction> Actions { get; set; } = new List<FarmAction>();
    }

    public class ModEntry : Mod
    {
        internal static ModEntry Instance = null!;

        private ModConfig _config = new ModConfig();
        private AgentLink _link = null!;
        private ActionRunner _runner = null!;
        private long _sampleAccum;
        private bool _linkStarted;
        private bool _autoLoadDone;
        private long _titleTicks;
        private int _tickLogCount;

        private long _frameLastMs;
        private int _frameFails;
        private bool _pauseFixDone;
        private long _diagAccum;
        private int _diagCount;
        private int _activeFixCount;
        private int _eventFixCount;
        private int _menuFixCount;
        private int _frameCostLogAt;
        private int _renderedCount;
        private long _renderedStartMs;
        private int _renderingCount;
        private bool _glProbed;
        private long _renderT0;
        private long _drawUsSum;
        private int _drawUsN;
        private int _updateCount;
        private long _updateStartMs;

        // 时间保底推进用的状态
        private long _clockLastCheck;
        private int _lastClockTime;
        private long _clockStallMs;
        private int _clockPushCount;
        private bool _seedListLogged;

        private static readonly System.Reflection.PropertyInfo? _isActiveProp =
            typeof(Microsoft.Xna.Framework.Game).GetProperty("IsActive");

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private static IntPtr _gameHwndFound;
        private static uint _gameSelfPid;

        private static bool FindGameWindow(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != _gameSelfPid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hWnd, sb, 256);
            if (sb.ToString() == "SDL_app")
            {
                _gameHwndFound = hWnd;
                return false;
            }
            return true;
        }

        [System.Runtime.InteropServices.DllImport("opengl32.dll")]
        private static extern IntPtr glGetString(uint name);

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            _config = helper.ReadConfig<ModConfig>() ?? new ModConfig();

            _link = new AgentLink(Monitor, _config.AgentUrl);
            _runner = new ActionRunner(Monitor, _link);
            _link.FrameReceived += OnFrame;

            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Display.Rendered += OnRendered;
            helper.Events.Display.Rendering += OnRendering;

            Monitor.Log("星露谷猫娘 mod 已载入，等待存档载入后连接 agent", LogLevel.Info);
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            EnsureLink("存档载入事件");
        }

        /// <summary>
        /// 连上 agent。不走单一事件的触发时机——无论是正常点进存档还是 mod 自己
        /// 调 SaveGame.Load 载入，只要世界就绪就连，避免"事件没触发就永远不连"。
        /// </summary>
        private void EnsureLink(string reason)
        {
            if (_linkStarted) return;

            _link.Start();
            _linkStarted = true;

            _link.Send(FarmSnapshot.Envelope());
            Monitor.Log(
                "已接入 agent（" + reason + "），当前 " + Game1.currentSeason + " " + Game1.dayOfMonth + " 日",
                LogLevel.Info);
        }

        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            _link.Stop();
            _linkStarted = false;
        }

        private int _tickErrorCount;

        /// <summary>
        /// tick 的总入口。里面任何一步抛异常都不能让整条主循环事件挂掉——
        /// 一旦 SMAPI 把这个 mod 的 UpdateTicked 拉黑，时钟、动作、状态推送会一起停，
        /// 表现就是"她彻底不动了"。所以外面统一兜一层，并且把真实堆栈打进日志。
        /// </summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            try
            {
                OnUpdateTickedCore(sender, e);
            }
            catch (Exception ex)
            {
                _tickErrorCount++;
                if (_tickErrorCount <= 3 || _tickErrorCount % 300 == 0)
                {
                    Monitor.Log(
                        "tick 里出错了（第 " + _tickErrorCount + " 次）: " + ex,
                        LogLevel.Error);
                }
            }
        }

        private void OnUpdateTickedCore(object? sender, UpdateTickedEventArgs e)
        {
            // 不用 Context.IsWorldReady：mod 自己调 SaveGame.Load 进档时它的判定可能落后，
            // 直接看游戏本身有没有进入 playing 且拿到当前地点更可靠。
            if (Game1.gameMode != 3 || Game1.currentLocation == null)
            {
                TryAutoLoad(e);
                return;
            }

            // 统计 update / rendered 各自多少帧：update 也低说明主循环被人拖住，
            // 只有 rendered 低才说明是画面这条链路的问题。
            _updateCount++;
            var ut = Environment.TickCount64;
            if (_updateStartMs == 0) _updateStartMs = ut;
            else if (ut - _updateStartMs >= 3000)
            {                Monitor.Log(
                    "update 帧率≈" + (_updateCount * 1000.0 / (ut - _updateStartMs)).ToString("0.0") +
                    " 帧/秒，rendering 事件≈" + (_renderingCount * 1000.0 / (ut - _updateStartMs)).ToString("0.0") +
                    " 帧/秒，画一帧≈" + (_drawUsN > 0 ? (_drawUsSum / _drawUsN) / 1000.0 : 0).ToString("0.0") +
                    " 毫秒，rendered 已写 " + FrameShm.FramesWritten + " 帧",
                    LogLevel.Info);
                _updateCount = 0;
                _renderingCount = 0;
                _drawUsSum = 0;
                _drawUsN = 0;
                _updateStartMs = ut;
            }

            if (_tickLogCount < 2)
            {
                _tickLogCount++;
                Monitor.Log(
                    "tick 自检: gameMode=" + Game1.gameMode +
                    " loc=" + Game1.currentLocation.Name +
                    " menu=" + (Game1.activeClickableMenu?.GetType().Name ?? "-"),
                    LogLevel.Info);
            }

            // 自己载入存档后，标题菜单还留在 activeClickableMenu 上，会挡住玩家更新和动作执行。
            // NPC 对话框、信件这些同理：只要挂着，Game1.timeOfDay 就完全不走，
            // 状态里的时间会永远停在同一个点，看起来像"她不动了"。
            var blockingMenu = Game1.activeClickableMenu;
            // 后台托管时，凡是"挡着时间走的界面"都要清掉：标题菜单、NPC 对话框、信件。
            // 之前这里只判了 TitleMenu，结果 DialogueBox 挂在那儿（日志里那句"小伙子，早上好！"）
            // 一直清不掉，Game1.shouldTimePass() 于是常年 false，时间就停在进档那一刻。
            // 注意顺序：blockingMenu 为 null 时 is 判断全是 false，但最后那句 GetType() 会直接崩。
            // 之前它一直没崩，是因为对话盒常年挂在 activeClickableMenu 上；把对话盒清干净之后
            // 这里开始每 tick 抛一次 NullReferenceException，整条 tick 后面的逻辑（时钟、动作、状态推送）
            // 全部拿不到执行机会——时间永远停在 06:00 的真凶就是这个，不是 shouldTimePass。
            // 升级弹窗（LevelUpMenu）也算：它要玩家点一下 OK，后台托管没人点，
            // 会一直挂着等确认——实测画面就停在「升级 耕种 1 级」那个框上不动。
            var menuName = blockingMenu?.GetType().Name ?? "";
            if (blockingMenu != null &&
                (blockingMenu is TitleMenu || blockingMenu is DialogueBox ||
                 menuName == "LetterViewerMenu" || menuName == "LevelUpMenu"))
            {
                string detail = "";
                if (blockingMenu is StardewValley.Menus.DialogueBox db)
                {
                    try
                    {
                        var s = db.getCurrentString();
                        detail = " 内容=\"" + (string.IsNullOrEmpty(s) ? "-" : s.Length > 60 ? s.Substring(0, 60) : s) + "\"";
                    }
                    catch
                    {
                        detail = " 内容=?";
                    }
                }

                Game1.activeClickableMenu = null;
                _menuFixCount++;
                // 只在头几次和长期复发时打日志，否则它每帧重建会把日志刷爆
                if (_menuFixCount <= 5 || _menuFixCount % 200 == 0)
                {
                    Monitor.Log(
                        "已关掉残留的阻塞菜单（" + blockingMenu.GetType().Name + detail +
                        "）（第 " + _menuFixCount + " 次）恢复游戏推进",
                        LogLevel.Info);
                }
            }

            ForceGameActive();

            KeepClockMoving();

            // Game1.shouldTimePass() 会因"剧情进行中"返回 false，表现就是 Part 对话反复弹、
            // 时间永远停在进档那一刻。后台托管期间把卡住的剧情事件直接摘掉。
            if (Game1.eventUp || Game1.CurrentEvent != null)
            {
                if (_eventFixCount < 5)
                {
                    _eventFixCount++;
                    Monitor.Log(
                        "清掉卡住的剧情事件: eventUp=" + Game1.eventUp +
                        " CurrentEvent=" + (Game1.CurrentEvent?.GetType().Name ?? "-") +
                        "（第 " + _eventFixCount + " 次）",
                        LogLevel.Info);
                }
                Game1.eventUp = false;
                ClearCurrentEvent();
                Game1.activeClickableMenu = null;
            }

            EnsureLink("世界就绪");

            // 后台托管时游戏窗口没在前台，原版"失去焦点就暂停"会把时间冻住，
            // 表现就是 /state 里的 time 永远停在同一个点、她一动也不动。
            if (!_pauseFixDone)
            {
                _pauseFixDone = true;
                try
                {
                    if (Game1.options != null) Game1.options.pauseWhenOutOfFocus = false;
                    Monitor.Log("已关闭\"失去焦点自动暂停\"", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log("关闭自动暂停失败: " + ex.Message, LogLevel.Warn);
                }

                // 这台机器的主显示器是远程串流的虚拟显示适配器（144Hz），
                // 开着垂直同步时 Present 会被它卡住，Draw 掉到每秒三四帧。
                // 关掉垂直同步让主循环自己按 60 帧跑，画面该多快就多快。
                try
                {
                    Game1.graphics.SynchronizeWithVerticalRetrace = false;
                    Game1.graphics.PreferredBackBufferWidth = 640;
                    Game1.graphics.PreferredBackBufferHeight = 360;
                    Game1.graphics.IsFullScreen = false;
                    Game1.graphics.ApplyChanges();
                    Monitor.Log("已关闭垂直同步，640x360 窗口", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log("关闭垂直同步失败: " + ex.Message, LogLevel.Warn);
                }

                // 画一帧只要一点二毫秒，两帧之间却空等两百毫秒——这段等待全在 Present 里。
                // 这台机器的显示输出是远程串流的虚拟显示器（星露谷走的是 SDL+OpenGL），
                // 它的取帧节奏只有每秒四五次，窗口只要还摆在屏幕上，swap 就得排队等它。
                // 最小化之后 swap 立刻返回，主循环能跑满，画面改从 back buffer 送给监控窗。
                if (_config.HideGameWindow)
                {
                    try
                    {
                        var hwnd = IntPtr.Zero;
                        try
                        {
                            _gameSelfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                            _gameHwndFound = IntPtr.Zero;
                            EnumWindows(FindGameWindow, IntPtr.Zero);
                            hwnd = _gameHwndFound;
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log("枚举游戏窗口失败: " + ex.Message, LogLevel.Warn);
                        }
                        if (hwnd == IntPtr.Zero)
                            hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                        if (hwnd == IntPtr.Zero)
                            hwnd = Game1.graphics.GraphicsDevice.PresentationParameters.DeviceWindowHandle;
                        Monitor.Log("准备最小化的窗口句柄: " + hwnd, LogLevel.Info);
                        if (hwnd != IntPtr.Zero)
                        {
                            ShowWindow(hwnd, 6); // SW_MINIMIZE
                            Monitor.Log("游戏窗口已最小化：原窗口不再等显示器取帧，画面改从 back buffer 送给监控窗", LogLevel.Info);
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log("最小化游戏窗口失败: " + ex.Message, LogLevel.Warn);
                    }
                }
            }

            _runner.Tick();

            _diagAccum += e.Ticks;
            if (_diagCount < 3 && _diagAccum >= 5000)
            {
                _diagAccum = 0;
                _diagCount++;
                LogTimeDiagnostics();
            }

            // 只打一次货架清单：换季之后她会买什么，日志里能直接看到，不用再猜种子 ID。
            // 必须等地点真正载入完再问，进档那一瞬间 terrainFeatures 还是空的，只会得到 0 种。
            if (!_seedListLogged && _diagCount >= 1)
            {
                _seedListLogged = true;
                try
                {
                    var offers = SeedShop.SeasonSeeds();
                    Monitor.Log(
                        "当季能买的种子 " + offers.Count + " 种：" +
                        (offers.Count == 0
                            ? "无（可能没有空地当季节探针）"
                            : string.Join("、", offers.Select(o => o.Name + "(" + o.Id + ")" + o.Price + "金"))),
                        LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log("读取种子货架失败: " + ex.Message, LogLevel.Warn);
                }
            }

            _sampleAccum += e.Ticks;
            if (_sampleAccum >= _config.SampleIntervalMs)
            {
                _sampleAccum = 0;
                _link.Send(FarmSnapshot.Envelope());
            }
        }

        /// <summary>
        /// 标题界面停 3 秒后自动载入最近存档。这样没人点鼠标她也能自己进游戏，
        /// 也是"她自己在后台玩"这件事能真正跑起来的前提。
        /// </summary>
        private void TryAutoLoad(UpdateTickedEventArgs e)
        {
            if (!_config.AutoLoadSave || _autoLoadDone) return;
            if (Game1.activeClickableMenu is not TitleMenu) return;

            _titleTicks += e.Ticks;
            if (_titleTicks < 180) return;

            _autoLoadDone = true;

            try
            {
                var savesDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StardewValley",
                    "Saves");

                var name = _config.AutoLoadSaveName;

                if (string.IsNullOrWhiteSpace(name))
                {
                    if (!Directory.Exists(savesDir))
                    {
                        Monitor.Log("找不到存档目录: " + savesDir, LogLevel.Error);
                        return;
                    }

                    var best = Directory.GetDirectories(savesDir)
                        .OrderByDescending(d => Directory.GetLastWriteTime(d))
                        .FirstOrDefault();

                    if (best == null)
                    {
                        Monitor.Log("存档目录里没有可载入的存档", LogLevel.Warn);
                        return;
                    }

                    name = Path.GetFileName(best);
                }

                Monitor.Log("自动载入存档: " + name, LogLevel.Info);
                SaveGame.Load(name);
            }
            catch (Exception ex)
            {
                Monitor.Log("自动载入存档失败: " + ex.Message, LogLevel.Error);
            }
        }

        /// <summary>
        /// back buffer 只在这一帧画完、present 之前可读，所以抓帧必须挂在这里。
        /// </summary>
        private void OnRendering(object? sender, StardewModdingAPI.Events.RenderingEventArgs e)
        {
            _renderingCount++;
            _renderT0 = System.Diagnostics.Stopwatch.GetTimestamp();

            if (!_glProbed)
            {
                _glProbed = true;
                try
                {
                    Monitor.Log(
                        "OpenGL 供应商=" + GlString(0x1F00) +
                        " 渲染器=" + GlString(0x1F01) +
                        " 版本=" + GlString(0x1F02),
                        LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log("查 OpenGL 信息失败: " + ex.Message, LogLevel.Warn);
                }
            }
        }

        private static string GlString(uint name)
        {
            var p = glGetString(name);
            if (p == IntPtr.Zero) return "(空)";
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p) ?? "(空)";
        }

        private void OnRendered(object? sender, StardewModdingAPI.Events.RenderedEventArgs e)
        {
            // 这一对事件夹住的正好是原版 Game1.draw() 本身。
            // 差值小说明画一帧很快、慢在 Present；差值大说明是画本身慢。
            if (_renderT0 != 0)
            {
                var dt = System.Diagnostics.Stopwatch.GetTimestamp() - _renderT0;
                _drawUsSum += dt * 1000000L / System.Diagnostics.Stopwatch.Frequency;
                _drawUsN++;
                _renderT0 = 0;
            }
            if (Game1.gameMode != 3 || Game1.currentLocation == null) return;

            // 先量清楚游戏自己到底在跑多少帧，再谈监控端能不能显示 60 帧
            _renderedCount++;
            var rt = Environment.TickCount64;
            if (_renderedStartMs == 0) _renderedStartMs = rt;
            else if (rt - _renderedStartMs >= 3000)
            {
                var fps = _renderedCount * 1000.0 / (rt - _renderedStartMs);
                Monitor.Log(
                    "渲染帧率≈" + fps.ToString("0.0") + " 帧/秒（已写入共享内存 " +
                    FrameShm.FramesWritten + " 帧，单帧回读 " + FrameShm.LastReadUs + " 微秒）",
                    LogLevel.Info);
                _renderedCount = 0;
                _renderedStartMs = rt;
            }

            // 60 帧显示的主链路：每帧把画面推进共享内存，监控窗直接读。
            if (!FrameShm.Ready)
            {
                if (FrameShm.Init(_config.SharedMemoryMaxFps))
                    Monitor.Log("共享内存传帧已就绪，上限 " + _config.SharedMemoryMaxFps + " 帧/秒", LogLevel.Info);
                else if (_frameFails < 3)
                {
                    _frameFails++;
                    Monitor.Log("共享内存传帧初始化失败: " + FrameShm.LastError, LogLevel.Warn);
                }
            }
            FrameShm.Push();

            // PNG 落盘只是留一份截图证据，间隔单独控制，不再承担实时显示
            if (_config.FrameIntervalMs <= 0) return;

            var now = Environment.TickCount64;
            if (now - _frameLastMs < _config.FrameIntervalMs) return;
            _frameLastMs = now;

            var path = ResolveFramePath();

            if (!FrameGrabber.TryGrab(path, out var err))
            {
                if (_frameFails <= 3 || _frameFails % 50 == 0)
                    Monitor.Log("抓帧失败(" + _frameFails + "): " + err, LogLevel.Warn);
                _frameFails++;
            }
        }

        private string ResolveFramePath()
        {
            if (!string.IsNullOrWhiteSpace(_config.FrameOutPath)) return _config.FrameOutPath;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NekoStardew",
                "frame.png");
        }

        /// <summary>
        /// MonoGame 的 Game.IsActive 只在窗口拿到焦点时为 true；没有它游戏会认为自己在后台，
        /// 时间就不往前走（timeOfDay 永远停在进档那一刻）。
        /// 后台托管时窗口不可能拿到焦点，所以每 tick 反射把它强行置回 true。
        /// </summary>
        private void ForceGameActive()
        {
            try
            {
                if (_isActiveProp == null) return;
                var game = StardewValley.GameRunner.instance;
                if (game == null) return;
                if (_isActiveProp.GetValue(game) is bool on && on) return;
                var setter = _isActiveProp.GetSetMethod(true);
                if (setter == null) return;
                setter.Invoke(game, new object[] { true });
                if (_activeFixCount < 3)
                {
                    _activeFixCount++;
                    Monitor.Log("游戏窗口失焦，已强制置回 IsActive=true（第 " + _activeFixCount + " 次）", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                if (_activeFixCount == 0)
                    Monitor.Log("强制 IsActive 失败: " + ex.Message, LogLevel.Warn);
            }
        }

        /// <summary>
        /// 时间兜底推进。
        ///
        /// 原版 Game1.timeOfDay 由主循环按游戏内分钟自己往上走，但前提是
        /// Game1.shouldTimePass() 为 true。这台机器上它常年 false（后台托管、窗口失焦、
        /// 反复弹的 NPC 对话框都会让它 false），结果 timeOfDay 从进档起就钉死在 06:00，
        /// 作物永远长不大，到点也不会困——整条"自己过日子"的链条就断在这儿。
        ///
        /// 所以这里不管 shouldTimePass 说什么，只要发现时间超过 8 秒没动，
        /// 就自己把钟往前推 10 分钟。到 26:00 就交给睡觉处理，不越过一天。
        /// </summary>
        /// <summary>
        /// 时间兜底推进。
        ///
        /// 原版 Game1.timeOfDay 由主循环按游戏内分钟自己往上走，但前提是
        /// Game1.shouldTimePass() 为 true。这台机器上它常年 false（后台托管、窗口失焦、
        /// 反复弹的 NPC 对话框都会让它 false），结果 timeOfDay 从进档起就钉死在 06:00，
        /// 作物永远长不大，到点也不会困——整条"自己过日子"的链条就断在这儿。
        ///
        /// 早先这一版是"发现时间 8 秒没动才推一次"，实测一次都没推过：每个 tick 都被
        /// eventUp / CurrentEvent 那两个前置条件挡回去，而且挡回去的时候还把计时器清零了，
        /// 于是永远攒不满 8 秒。现在改成按真实毫秒自己攒，不参考 shouldTimePass，
        /// 也不让 eventUp 打断计时——只有真的到了 26:00 或者没玩家时才停手。
        /// </summary>
        private void KeepClockMoving()
        {
            var now = Environment.TickCount64;

            if (_clockLastCheck == 0)
            {
                _clockLastCheck = now;
                _lastClockTime = Game1.timeOfDay;
                return;
            }

            var delta = now - _clockLastCheck;
            _clockLastCheck = now;
            if (delta <= 0 || delta > 2000) return; // 卡顿/断点造成的跳变不算钱

            if (Game1.player == null) return;
            if (Game1.timeOfDay >= 2600) return;

            _clockStallMs += delta;
            if (_clockStallMs < 7000) return;
            _clockStallMs = 0;

            // 10 游戏分钟 = 7000 毫秒真实时间，和原版的节奏一致。
            // 不直接写 Game1.timeOfDay：这档是多人存档，timeOfDay 的 setter 会碰到
            // 联机状态里那些没初始化的对象，直接赋值会现场 NullReferenceException。
            // 改成调原版自己的十分钟推进，光照、通知、晕倒判定都走游戏原生那套。
            var advanced = TenMinuteTick();
            if (!advanced)
            {
                // 反射也拿不到才退回直接加，并且这条只在单机档上才敢用
                Game1.timeOfDay += 10;
            }
            Game1.gameTimeInterval = 0;

            _clockPushCount++;
            var crossed = Game1.timeOfDay != _lastClockTime;
            _lastClockTime = Game1.timeOfDay;

            if (_clockPushCount <= 5 || _clockPushCount % 30 == 0)
            {
                Monitor.Log(
                    "时钟自己走：" + ClockText(Game1.timeOfDay) +
                    "（第 " + _clockPushCount + " 次推进，" +
                    (advanced ? "走原版十分钟推进" : "直接加时间") +
                    (crossed ? "" : "，但读数没变") + "）",
                    LogLevel.Info);
            }
        }

        private static MethodInfo? _tenMinuteMethod;
        private static bool _tenMinuteResolved;

        /// <summary>
        /// 调原版的 performTenMinuteClockUpdate()。它内部会正确更新 timeOfDay、
        /// 推进光照、判定 02:00 晕倒，是官方唯一"往前推十分钟"的入口。
        /// 反射找不到就返回 false，让调用方退回直接加时间。
        /// </summary>
        private static bool TenMinuteTick()
        {
            if (!_tenMinuteResolved)
            {
                _tenMinuteResolved = true;
                try
                {
                    _tenMinuteMethod = typeof(Game1)
                        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "performTenMinuteClockUpdate");
                }
                catch
                {
                    _tenMinuteMethod = null;
                }
            }

            if (_tenMinuteMethod == null) return false;

            try
            {
                var ps = _tenMinuteMethod.GetParameters();
                object?[]? args = null;
                if (ps.Length > 0)
                {
                    args = new object?[ps.Length];
                    for (var i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                        else if (ps[i].ParameterType == typeof(bool)) args[i] = false;
                        else if (ps[i].ParameterType == typeof(int)) args[i] = 0;
                        else return false; // 有搞不定的参数类型就别硬来
                    }
                }

                _tenMinuteMethod.Invoke(null, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ClockText(int t)
        {
            var h = t / 100;
            var m = t % 100;
            return (h < 10 ? "0" : "") + h + ":" + (m < 10 ? "0" : "") + m;
        }

        /// <summary>
        /// Game1.CurrentEvent 是只读属性，只能反射摸到它的后备字段把它置空。
        /// 1.6 里它还挂在当前地点上（实测 eventUp 清成 False 后 CurrentEvent 仍是 Event），
        /// 所以地点那一份也要一起摘掉，否则时间推进和睡觉都会被它挡回去。
        /// </summary>
        private static void ClearCurrentEvent()
        {
            try
            {
                var loc = Game1.currentLocation;
                if (loc != null)
                {
                    foreach (var f in loc.GetType().GetFields(
                                 System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.NonPublic |
                                 System.Reflection.BindingFlags.Public))
                    {
                        if (f.FieldType != typeof(StardewValley.Event)) continue;
                        if (f.Name.IndexOf("event", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        f.SetValue(loc, null);
                    }
                }
            }
            catch
            {
                // 清不掉就保持原样，下一 tick 再试
            }

            try
            {
                var t = typeof(Game1);
                foreach (var f in t.GetFields(
                             System.Reflection.BindingFlags.Static |
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Public))
                {
                    if (f.FieldType == typeof(StardewValley.Event) &&
                        f.Name.IndexOf("CurrentEvent", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        f.SetValue(null, null);
                        return;
                    }
                }
            }
            catch
            {
                // 清不掉就保持原样，下一 tick 再试
            }
        }

        /// <summary>
        /// 直接问游戏"现在该不该走时间"，比猜各种标志位准得多。
        /// </summary>
        private static string CallShouldTimePass()
        {
            try
            {
                var m = typeof(Game1).GetMethod(
                    "shouldTimePass",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic);

                if (m == null) return "方法不存在";
                var args = m.GetParameters().Length == 0 ? null : new object[] { false };
                return m.Invoke(null, args)?.ToString() ?? "null";
            }
            catch (Exception ex)
            {
                return "调用失败:" + ex.GetType().Name + " " + ex.Message;
            }
        }

        /// <summary>
        /// 时间不走的时候，把所有可能冻住时间的开关挨个反射读出来，别再靠猜。
        /// </summary>
        private void LogTimeDiagnostics()
        {
            try
            {
                Monitor.Log(
                    "时间诊断: timeOfDay=" + Game1.timeOfDay +
                    " menu=" + (Game1.activeClickableMenu?.GetType().Name ?? "-") +
                    " location=" + (Game1.currentLocation?.Name ?? "-") +
                    " playerUsingTool=" + (Game1.player?.UsingTool.ToString() ?? "-"),
                    LogLevel.Info);

                var t = typeof(Game1);
                var names = new[]
                {
                    "isTimePaused", "paused", "gamePaused", "isPaused",
                    "freezeTime", "timePaused", "windowActive", "shouldTimePass", "newDay"
                };
                const System.Reflection.BindingFlags S =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic;

                foreach (var name in names)
                {
                    var p = t.GetProperty(name, S);
                    var f = t.GetField(name, S);
                    var m = t.GetMethod(name, S, null, Type.EmptyTypes, null);

                    if (p != null && p.GetIndexParameters().Length == 0)
                        Monitor.Log("  " + name + "(prop)=" + p.GetValue(null), LogLevel.Info);
                    else if (f != null)
                        Monitor.Log("  " + name + "(field)=" + f.GetValue(null), LogLevel.Info);
                    else if (m != null)
                        Monitor.Log("  " + name + "()=" + m.Invoke(null, null), LogLevel.Info);
                    else
                        Monitor.Log("  " + name + " = 不存在", LogLevel.Info);
                }

                var isActive = _isActiveProp?.GetValue(StardewValley.GameRunner.instance)?.ToString() ?? "?";
                Monitor.Log(
                    "  pauseWhenOutOfFocus=" + (Game1.options != null ? Game1.options.pauseWhenOutOfFocus.ToString() : "-") +
                    "  IsActive=" + isActive + "  shouldTimePass=" + CallShouldTimePass(),
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log("时间诊断失败: " + ex.Message, LogLevel.Warn);
            }
        }

        private void OnFrame(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp)) return;
                if (typeProp.GetString() != "task") return;

                string? taskId = root.TryGetProperty("task_id", out var idProp) ? idProp.GetString() : null;

                var actions = new List<FarmAction>();
                if (root.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("op", out var opProp)) continue;
                        var op = opProp.GetString();
                        if (string.IsNullOrEmpty(op)) continue;

                        string? target = item.TryGetProperty("target", out var tg) ? tg.GetString() : null;
                        actions.Add(new FarmAction { Op = op, Target = target });
                    }
                }

                if (actions.Count == 0) return;

                Monitor.Log(
                    "收到任务 " + (taskId ?? "-") + ": " + string.Join(",", actions.Select(a => a.Op)),
                    LogLevel.Info);

                _runner.Enqueue(new AgentCommand { TaskId = taskId, Actions = actions });
            }
            catch (Exception ex)
            {
                Monitor.Log("解析 agent 帧失败: " + ex.Message, LogLevel.Warn);
            }
        }
    }
}
