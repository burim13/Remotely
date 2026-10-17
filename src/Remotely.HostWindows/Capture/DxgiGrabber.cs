using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Remotely.Host.Capture;

/// <summary>
/// Desktop Duplication (DXGI) capture of single displays. Each display has its own duplication and keeps its own copy of the last picture, so
/// "no new frame from Windows" costs nothing: the old picture is returned with the same version number. Anything that goes wrong surfaces as
/// <see cref="Unavailable"/> so the caller can fall back to GDI. Must be used from the thread attached to the input desktop.
/// </summary>
internal sealed class DxgiGrabber : IDisposable
{
    internal sealed class Unavailable(string message, Exception? inner = null) : Exception(message, inner);

    private sealed class OutputState : IDisposable
    {
        public required string Device;
        public required string Adapter;
        public required ID3D11Device D3D;
        public required ID3D11DeviceContext Context;
        public required IDXGIOutputDuplication Duplication;
        public required int Width, Height;
        public byte[]? Image;
        public long Version;
        public ID3D11Texture2D? Staging;

        public void Dispose()
        {
            Staging?.Dispose();
            Duplication.Dispose();
            Context.Dispose();
            D3D.Dispose();
        }
    }

    private readonly Dictionary<string, OutputState> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private string? _desktop;

    /// <summary>Description of the adapter and output last used, for the log.</summary>
    public string LastAdapter { get; private set; } = "";

    public (byte[] Image, long Version) Grab(string desktopName, MonitorList.Entry monitor, Func<long> nextVersion)
    {
        if (!string.Equals(_desktop, desktopName, StringComparison.Ordinal))
        {
            DisposeOutputs();
            _desktop = desktopName;
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!_outputs.TryGetValue(monitor.Device, out var output)) _outputs[monitor.Device] = output = Create(monitor);
                LastAdapter = output.Adapter;
                return Acquire(output, nextVersion);
            }
            catch (AccessLostException) when (attempt == 0)
            {
                DisposeOutputs(); // the desktop or display mode changed under us: start over once
            }
            catch (AccessLostException ex)
            {
                DisposeOutputs();
                throw new Unavailable("Desktop duplication was lost twice in a row.", ex);
            }
            catch (Unavailable)
            {
                DisposeOutputs();
                throw;
            }
            catch (Exception ex) when (ex is SharpGenException or COMException or InvalidOperationException or ArgumentException)
            {
                DisposeOutputs();
                throw new Unavailable($"{ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    private sealed class AccessLostException : Exception;

    private static OutputState Create(MonitorList.Entry monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var seen = new List<string>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var adapterName = adapter.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        seen.Add($"{adapterName}: {description.DeviceName}");
                        if (!string.Equals(description.DeviceName, monitor.Device, StringComparison.OrdinalIgnoreCase)) continue;
                        if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            throw new Unavailable($"Display {description.DeviceName} is rotated ({description.Rotation}); duplication would need rotating.");
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                        try
                        {
                            var duplication = output1.DuplicateOutput(device);
                            var info = duplication.Description;
                            var rect = description.DesktopCoordinates;
                            return new OutputState
                            {
                                Device = monitor.Device,
                                Adapter = $"{adapterName} / {description.DeviceName}",
                                D3D = device,
                                Context = context,
                                Duplication = duplication,
                                Width = rect.Right - rect.Left,
                                Height = rect.Bottom - rect.Top,
                            };
                        }
                        catch
                        {
                            context.Dispose();
                            device.Dispose();
                            throw;
                        }
                    }
                }
            }
        }
        throw new Unavailable(seen.Count == 0
            ? "DXGI lists no display outputs on this machine."
            : $"No DXGI output matches {monitor.Device}; DXGI sees: {string.Join("; ", seen)}.");
    }

    private (byte[] Image, long Version) Acquire(OutputState output, Func<long> nextVersion)
    {
        var timeout = output.Image is null ? 500u : 0u; // the very first call has to produce a picture; later calls only ask "anything new?"
        var result = output.Duplication.AcquireNextFrame(timeout, out var frame, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            if (output.Image is null) throw new Unavailable("Windows produced no first frame within 500 ms.");
            return (output.Image, output.Version);
        }
        if (result == Vortice.DXGI.ResultCode.AccessLost) throw new AccessLostException();
        result.CheckError();

        try
        {
            if (frame.LastPresentTime == 0 && output.Image is not null) return (output.Image, output.Version); // pointer moved, pixels did not
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            Copy(output, texture);
            output.Version = nextVersion();
            return (output.Image!, output.Version);
        }
        finally
        {
            resource.Dispose();
            output.Duplication.ReleaseFrame();
        }
    }

    private static unsafe void Copy(OutputState output, ID3D11Texture2D texture)
    {
        var description = texture.Description;
        if (description.Format != Vortice.DXGI.Format.B8G8R8A8_UNorm)
            throw new Unavailable($"Desktop surface format {description.Format} is not 8-bit BGRA (HDR or wide colour?).");
        var width = (int)description.Width;
        var height = (int)description.Height;
        if (width != output.Width || height != output.Height)
            throw new Unavailable($"Duplicated surface is {width}x{height} but the display reports {output.Width}x{output.Height} (display scaling?).");

        if (output.Staging is null || output.Staging.Description.Width != description.Width || output.Staging.Description.Height != description.Height)
        {
            output.Staging?.Dispose();
            var staging = description;
            staging.Usage = ResourceUsage.Staging;
            staging.BindFlags = BindFlags.None;
            staging.CPUAccessFlags = CpuAccessFlags.Read;
            staging.MiscFlags = ResourceOptionFlags.None;
            staging.MipLevels = 1;
            staging.ArraySize = 1;
            output.Staging = output.D3D.CreateTexture2D(staging);
        }
        output.Context.CopyResource(output.Staging, texture);
        output.Context.Map(output.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            if (output.Image is null || output.Image.Length != width * height * 4) output.Image = new byte[width * height * 4];
            fixed (byte* destination = output.Image)
            {
                var source = (byte*)mapped.DataPointer;
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + (long)y * mapped.RowPitch, destination + (long)y * width * 4, width * 4, width * 4);
            }
        }
        finally
        {
            output.Context.Unmap(output.Staging, 0);
        }
    }

    private void DisposeOutputs()
    {
        foreach (var o in _outputs.Values) o.Dispose();
        _outputs.Clear();
    }

    public void Dispose() => DisposeOutputs();
}
