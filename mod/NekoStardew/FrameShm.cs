using System;
using System.IO.MemoryMappedFiles;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace NekoStardew
{
    /// <summary>
    /// 把游戏画面以 60 帧/秒写进一块共享内存，监控窗直接读。
    /// 走文件（PNG）一帧要几十毫秒编码，最多两三帧/秒，撑不起 60 帧；
    /// 共享内存是零拷贝，游戏进程写完监控窗立刻能读。
    ///
    /// 布局（双缓冲，写一块、读另一块，避免读到写了一半的画面）：
    ///   0  magic      int
    ///   4  width      int
    ///   8  height     int
    ///   12 active     int   （当前可读的块号 0/1）
    ///   16 seq        int   （每次写完 +1）
    ///   20 maxFps     int
    ///   24 ticks      long
    ///   32（预留）
    ///   64 起是两块 BGRA32 像素数据
    /// </summary>
    public static class FrameShm
    {
        public const string MapName = @"Local\NekoStardewFrame";

        public const int OutW = 640;
        public const int OutH = 360;
        public const int HeaderSize = 64;
        public const int BlockSize = OutW * OutH * 4;
        private const int TotalSize = HeaderSize + BlockSize * 2;

        private const int OffMagic = 0;
        private const int OffWidth = 4;
        private const int OffHeight = 8;
        private const int OffActive = 12;
        private const int OffSeq = 16;
        private const int OffMaxFps = 20;
        private const int OffTicks = 24;
        private const int OffCostUs = 32;

        private static MemoryMappedFile? _mmf;
        private static MemoryMappedViewAccessor? _view;
        private static unsafe byte* _base;
        private static bool _acquired;

        private static uint[]? _src;
        private static IntPtr _tmpBuf;
        private static int _tmpSize;
        private static int _fullW, _fullH;
        private static int _writeBlock;
        private static int _seq;
        private static long _lastMs;
        private static int _maxFps = 60;

        [System.Runtime.InteropServices.DllImport("opengl32.dll")]
        private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, IntPtr pixels);

        private const uint GL_BGRA = 0x80E1;
        private const uint GL_UNSIGNED_BYTE = 0x1401;

        public static bool Ready { get; private set; }
        public static string LastError { get; private set; } = "";
        public static int FramesWritten { get; private set; }
        public static int LastReadUs { get; private set; }

        public static unsafe bool Init(int maxFps)
        {
            if (Ready) return true;
            _maxFps = maxFps < 0 ? 0 : maxFps;
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(MapName, TotalSize);
                _view = _mmf.CreateViewAccessor(0, TotalSize);
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
                _acquired = true;

                *At(OffMagic) = 0x4E4B4652; // 'NKFR'
                *At(OffWidth) = OutW;
                *At(OffHeight) = OutH;
                *At(OffActive) = 0;
                *At(OffSeq) = 0;
                *At(OffMaxFps) = _maxFps;
                *At64(OffTicks) = 0L;

                Ready = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                Ready = false;
                return false;
            }
        }

        private static unsafe int* At(int offset) => (int*)(_base + offset);
        private static unsafe long* At64(int offset) => (long*)(_base + offset);

        /// <summary>
        /// 读 back buffer → 降采样 → 写共享内存。只在 Display.Rendered 里调用。
        /// </summary>
        public static unsafe void Push()
        {
            if (!Ready) return;
            if (_maxFps == 0) return;

            var now = Environment.TickCount64;
            if (now - _lastMs < (1000 / _maxFps)) return;

            try
            {
                var gd = Game1.graphics?.GraphicsDevice;
                if (gd == null) return;

                var pp = gd.PresentationParameters;
                int fw = pp.BackBufferWidth;
                int fh = pp.BackBufferHeight;
                if (fw <= 0 || fh <= 0) return;

                int n = fw * fh;
                if (_src == null || _src.Length != n || fw != _fullW || fh != _fullH)
                {
                    _src = new uint[n];
                    _fullW = fw;
                    _fullH = fh;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // 先试最快的路：直接 glReadPixels 读 BGRA。星露谷在这台机器上跑的是 SDL+OpenGL，
                // MonoGame 的 GetBackBufferData 读的是 RGBA 再让驱动做一次格式转换，
                // 640x360 实测要 58 毫秒；而 BGRA 正是 NVIDIA 的 native 格式，能省掉这一趟。
                bool got = false;
                if (fw == OutW && fh == OutH)
                {
                    int need = fw * fh * 4;
                    if (_tmpBuf == IntPtr.Zero || _tmpSize != need)
                    {
                        if (_tmpBuf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(_tmpBuf);
                        _tmpBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(need);
                        _tmpSize = need;
                    }
                    try
                    {
                        glReadPixels(0, 0, fw, fh, GL_BGRA, GL_UNSIGNED_BYTE, _tmpBuf);
                        got = true;
                    }
                    catch
                    {
                        got = false;
                    }
                }

                if (!got)
                {
                    gd.GetBackBufferData(_src);
                }

                _lastMs = now;
                var readUs = (int)(sw.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency);
                LastReadUs = readUs;

                // 先写"另一块"，写完再切 active，读侧不会看到半张图
                int block = _writeBlock ^ 1;
                var dst = _base + HeaderSize + block * BlockSize;

                if (got)
                {
                    // glReadPixels 出来已经是 BGRA、四字节对齐，只需要把行倒过来（GL 原点在左下）。
                    var src = (byte*)_tmpBuf;
                    int rowBytes = OutW * 4;
                    for (int y = 0; y < OutH; y++)
                    {
                        int sy = OutH - 1 - y;
                        Buffer.MemoryCopy(src + sy * rowBytes, dst + y * rowBytes, rowBytes, rowBytes);
                    }
                }
                else
                {
                    // 回读出来是自下而上、像素打包 0x00BBGGRR（低字节 R）
                    for (int y = 0; y < OutH; y++)
                    {
                        int sy = fh - 1 - (int)((long)y * fh / OutH);
                        if (sy < 0) sy = 0;
                        int rowBase = sy * fw;
                        int o = y * OutW * 4;

                        for (int x = 0; x < OutW; x++)
                        {
                            int sx = (int)((long)x * fw / OutW);
                            uint c = _src[rowBase + sx];
                            // 写成 BGRA 字节序（内存里 B,G,R,A）
                            dst[o] = (byte)((c >> 16) & 0xFF);     // B
                            dst[o + 1] = (byte)((c >> 8) & 0xFF);  // G
                            dst[o + 2] = (byte)(c & 0xFF);         // R
                            dst[o + 3] = 0xFF;                     // A
                            o += 4;
                        }
                    }
                }

                _writeBlock = block;
                _seq++;
                *At(OffActive) = block;
                *At(OffSeq) = _seq;
                *At(OffCostUs) = readUs;
                *At64(OffTicks) = now;
                FramesWritten++;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Dispose()
        {
            try
            {
                if (_acquired && _view != null)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _acquired = false;
                }
                _view?.Dispose();
                _mmf?.Dispose();
                if (_tmpBuf != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(_tmpBuf);
                    _tmpBuf = IntPtr.Zero;
                    _tmpSize = 0;
                }
            }
            catch
            {
                // 退出阶段，忽略
            }
            Ready = false;
        }
    }
}
