using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NekoStardew.Monitor;

/// <summary>
/// 桌面复制抓屏。DXGI Desktop Duplication 抓的是整块输出（显示器），
/// 这里负责把它裁成目标窗口区域，交给 UI 显示。
/// 之所以不用 PrintWindow：星露谷是 MonoGame/DX 窗口，PrintWindow 抓回来是纯黑。
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput _output;
    private readonly IDXGIOutput1 _output1;

    private ID3D11Texture2D? _staging;
    private int _outWidth;
    private int _outHeight;
    private bool _disposed;

    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    public DesktopDuplicator(int adapterIndex = 0, int outputIndex = 0)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        if (!factory.EnumAdapters1((uint)adapterIndex, out _adapter).Success)
            throw new InvalidOperationException($"找不到显示适配器 #{adapterIndex}");

        if (!_adapter.EnumOutputs((uint)outputIndex, out _output).Success)
            throw new InvalidOperationException(
                $"适配器上没有第 {outputIndex} 号输出（外接屏未扩展时通常只有 #0）");

        _output1 = _output.QueryInterface<IDXGIOutput1>();

        // 老显卡 / 老驱动对 FeatureLevel 11_1 会直接返回 E_INVALIDARG（Vortice 抛异常），
        // 所以逐级降级尝试，直到设备建起来为止。
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        Exception? lastError = null;

        var attempts = new[]
        {
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            new[] { FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            new[] { FeatureLevel.Level_10_0 },
        };

        foreach (var levels in attempts)
        {
            try
            {
                ID3D11Device outDevice;
                ID3D11DeviceContext outContext;
                // D3D11 的硬规则：pAdapter 非空时，DriverType 必须传 UNKNOWN，
                // 否则 D3D11CreateDevice 一律返回 E_INVALIDARG（参数错误）。
                var hr = D3D11.D3D11CreateDevice(
                    _adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    levels,
                    out outDevice,
                    out outContext);
                if (hr.Failure)
                {
                    lastError = new InvalidOperationException("HRESULT " + hr + " " + hr.Description);
                    continue;
                }
                device = outDevice;
                context = outContext;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (device is null || context is null)
            throw new InvalidOperationException(
                "D3D11 设备创建失败：" + (lastError?.Message ?? "未知错误"), lastError);

        _device = device;
        _context = context;

        try
        {
            _duplication = _output1.DuplicateOutput(_device);
        }
        catch (SharpGenException ex)
        {
            throw new InvalidOperationException(
                "桌面复制初始化失败（可能被别的抓屏软件占用，或当前是远程桌面会话）：" + ex.Message, ex);
        }

        var desc = _output.Description;
        _outWidth = (int)desc.DesktopCoordinates.Right - (int)desc.DesktopCoordinates.Left;
        _outHeight = (int)desc.DesktopCoordinates.Bottom - (int)desc.DesktopCoordinates.Top;
        EnsureStaging(_outWidth, _outHeight);
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging != null && _staging.Description.Width == (uint)width && _staging.Description.Height == (uint)height)
            return;

        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    /// <summary>一帧 BGRA32 紧凑像素（stride = Width * 4）。</summary>
    public sealed record FrameData(byte[] Pixels, int Width, int Height);

    /// <summary>
    /// 抓一帧并裁出指定窗口区域。返回 BGRA32 紧凑缓冲（stride = width * 4）。
    /// 没有新帧时返回 null（不是错误，桌面静止时本来就没有）。
    /// </summary>
    public unsafe FrameData? Grab(int cropX, int cropY, int cropWidth, int cropHeight, int timeoutMs = 50)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopDuplicator));

        if (cropWidth <= 0 || cropHeight <= 0)
            return null;

        cropX = Math.Clamp(cropX, 0, Math.Max(0, _outWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, _outHeight - 1));
        cropWidth = Math.Min(cropWidth, _outWidth - cropX);
        cropHeight = Math.Min(cropHeight, _outHeight - cropY);
        if (cropWidth <= 0 || cropHeight <= 0)
            return null;

        IDXGIResource? resource = null;
        try
        {
            var acq = _duplication.AcquireNextFrame((uint)timeoutMs, out _, out resource);
            if (acq.Failure || resource == null)
                return null;

            using var frameTexture = resource.QueryInterface<ID3D11Texture2D>();
            var frameDesc = frameTexture.Description;

            // 分辨率变了（改显示设置 / 换屏）就重建 staging
            EnsureStaging((int)frameDesc.Width, (int)frameDesc.Height);
            _outWidth = (int)frameDesc.Width;
            _outHeight = (int)frameDesc.Height;

            _context.CopyResource(_staging!, frameTexture);

            var mapped = _context.Map(_staging!, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* srcBase = (byte*)mapped.DataPointer;
                int srcPitch = (int)mapped.RowPitch;

                var buffer = new byte[cropWidth * cropHeight * 4];
                fixed (byte* dstBase = buffer)
                {
                    for (int row = 0; row < cropHeight; row++)
                    {
                        byte* src = srcBase + (long)(cropY + row) * srcPitch + (long)cropX * 4;
                        byte* dst = dstBase + (long)row * cropWidth * 4;
                        Buffer.MemoryCopy(src, dst, cropWidth * 4, cropWidth * 4);
                    }
                }
                return new FrameData(buffer, cropWidth, cropHeight);
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch (SharpGenException)
        {
            // AccessLost 之类的瞬态错误：下一轮重试即可
            return null;
        }
        finally
        {
            resource?.Dispose();
            try { _duplication.ReleaseFrame(); } catch { /* 没成功 Acquire 时 ReleaseFrame 会报错，忽略 */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staging?.Dispose();
        _duplication.Dispose();
        _output1.Dispose();
        _output.Dispose();
        _adapter.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
