using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace NekoStardew
{
    /// <summary>
    /// 直接从游戏自己的 D3D11 back buffer 取像素，绕开 PrintWindow / DXGI 抓屏。
    /// 星露谷是 MonoGame + flip model swapchain，任何窗口级的抓取都拿不到内容（实测全黑），
    /// 只有进程内读 back buffer 才有效。
    /// </summary>
    public static class FrameGrabber
    {
        private static uint[]? _buf;

        public static bool TryGrab(string path, out string error)
        {
            error = "";
            try
            {
                if (Game1.graphics == null || Game1.graphics.GraphicsDevice == null)
                {
                    error = "GraphicsDevice 还没就绪";
                    return false;
                }

                var gd = Game1.graphics.GraphicsDevice;
                var pp = gd.PresentationParameters;
                int fullW = pp.BackBufferWidth;
                int fullH = pp.BackBufferHeight;
                if (fullW <= 0 || fullH <= 0)
                {
                    error = "BackBuffer 尺寸异常 " + fullW + "x" + fullH;
                    return false;
                }

                // 全分辨率读，再隔点降到一半，保持清晰又能让文件小一点
                int n = fullW * fullH;
                if (_buf == null || _buf.Length != n) _buf = new uint[n];

                gd.GetBackBufferData(_buf);

                int outW = fullW / 2;
                int outH = fullH / 2;
                var rgb = new byte[outW * outH * 3];
                int o = 0;
                for (int y = 0; y < outH; y++)
                {
                    // 这版 MonoGame 读出来的 back buffer 是自下而上的（行 0 在屏幕底部），
                    // 不翻的话整幅画是上下颠倒的。
                    int sy = fullH - 1 - y * 2;
                    for (int x = 0; x < outW; x++)
                    {
                        uint c = _buf[sy * fullW + x * 2];
                        // 实测这版 MonoGame 的 back buffer 打包是 0xAABBGGRR（低字节 R），
                        // 直接按 AARRGGBB 解读会把红蓝对调、整屏偏青，这里按实测换位。
                        rgb[o++] = (byte)(c & 0xFF);
                        rgb[o++] = (byte)((c >> 8) & 0xFF);
                        rgb[o++] = (byte)((c >> 16) & 0xFF);
                    }
                }

                DirEnsure(path);
                WritePng(path, rgb, outW, outH);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static void DirEnsure(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static void WritePng(string path, byte[] rgb, int w, int h)
        {
            // 每行前面加一个 filter 字节 0
            int stride = w * 3;
            var raw = new byte[h * (1 + stride)];
            for (int y = 0; y < h; y++)
            {
                raw[y * (1 + stride)] = 0;
                Buffer.BlockCopy(rgb, y * stride, raw, y * (1 + stride) + 1, stride);
            }

            var comp = new MemoryStream();
            using (var ds = new DeflateStream(comp, CompressionLevel.Fastest, true))
                ds.Write(raw, 0, raw.Length);
            var deflated = comp.ToArray();

            // zlib 头 + deflate + adler32
            var idat = new byte[deflated.Length + 6];
            idat[0] = 0x78; idat[1] = 0x9C;
            Buffer.BlockCopy(deflated, 0, idat, 2, deflated.Length);
            uint adler = Adler32(raw);
            idat[idat.Length - 4] = (byte)((adler >> 24) & 0xFF);
            idat[idat.Length - 3] = (byte)((adler >> 16) & 0xFF);
            idat[idat.Length - 2] = (byte)((adler >> 8) & 0xFF);
            idat[idat.Length - 1] = (byte)(adler & 0xFF);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteSignature(fs);
            WriteChunk(fs, "IHDR", Ihdr(w, h));
            WriteChunk(fs, "IDAT", idat);
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteSignature(Stream s)
        {
            s.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        }

        private static byte[] Ihdr(int w, int h)
        {
            var b = new byte[13];
            b[0] = (byte)((w >> 24) & 0xFF); b[1] = (byte)((w >> 16) & 0xFF);
            b[2] = (byte)((w >> 8) & 0xFF); b[3] = (byte)(w & 0xFF);
            b[4] = (byte)((h >> 24) & 0xFF); b[5] = (byte)((h >> 16) & 0xFF);
            b[6] = (byte)((h >> 8) & 0xFF); b[7] = (byte)(h & 0xFF);
            b[8] = 8;   // bit depth
            b[9] = 2;   // color type: truecolor RGB
            b[10] = 0; b[11] = 0; b[12] = 0;
            return b;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            len[0] = (byte)((data.Length >> 24) & 0xFF);
            len[1] = (byte)((data.Length >> 16) & 0xFF);
            len[2] = (byte)((data.Length >> 8) & 0xFF);
            len[3] = (byte)(data.Length & 0xFF);
            s.Write(len, 0, 4);

            var tb = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(tb, 0, 4);
            s.Write(data, 0, data.Length);

            uint crc = Crc32(tb, data);
            var c = new byte[4];
            c[0] = (byte)((crc >> 24) & 0xFF); c[1] = (byte)((crc >> 16) & 0xFF);
            c[2] = (byte)((crc >> 8) & 0xFF); c[3] = (byte)(crc & 0xFF);
            s.Write(c, 0, 4);
        }

        private static uint[]? _crcTable;

        private static uint Crc32(byte[] type, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            foreach (var b in type) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Adler32(byte[] data)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            foreach (var x in data)
            {
                a = (a + x) % MOD;
                b = (b + a) % MOD;
            }
            return (b << 16) | a;
        }
    }
}
