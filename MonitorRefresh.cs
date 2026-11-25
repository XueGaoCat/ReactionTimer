using System;
using System.Runtime.InteropServices;

namespace ReactionTimer;

/// <summary>
/// 读取当前窗口所在显示器的实际刷新率。
/// 优先走 CCD（QueryDisplayConfig）拿活动路径的目标刷新率，
/// 失败时退回 EnumDisplaySettings 的当前模式。
/// </summary>
internal static class MonitorRefresh
{
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    private struct Rational
    {
        internal uint Numerator;
        internal uint Denominator;

        internal double AsHertz()
        {
            if (Denominator == 0)
            {
                return 0.0;
            }
            return (double)Numerator / Denominator;
        }
    }

    private struct PathSourceInfo
    {
        internal Luid Adapter;
        internal uint Id;
        internal uint ModeIndex;
        internal uint StatusFlags;
    }

    private struct PathTargetInfo
    {
        internal Luid Adapter;
        internal uint Id;
        internal uint ModeIndex;
        internal uint OutputTechnology;
        internal uint Rotation;
        internal uint Scaling;
        internal Rational RefreshRate;
        internal uint ScanlineOrdering;
        internal int Available;
        internal uint StatusFlags;
    }

    private struct PathInfo
    {
        internal PathSourceInfo Source;
        internal PathTargetInfo Target;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct ModeInfo
    {
        [FieldOffset(0)]
        internal uint Type;

        [FieldOffset(32)]
        internal Rational VerticalSyncFrequency;
    }

    private struct DeviceInfoHeader
    {
        internal uint Type;
        internal uint Size;
        internal Luid Adapter;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        internal DeviceInfoHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DeviceMode
    {
        [FieldOffset(68)]
        internal ushort Size;

        [FieldOffset(184)]
        internal uint Frequency;
    }

    private const uint MonitorDefaultToNearest = 2u;
    private const uint OnlyActivePaths = 2u;
    private const int InsufficientBuffer = 122;
    private const uint TargetMode = 2u;
    private const uint PathSupportsVirtualMode = 8u;
    private const uint DisplayConfigGetSourceName = 1u;
    private const int MaxQueryAttempts = 3;
    private const uint MaxDisplayRecords = 1024u;

    internal static double ReadHz(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return 0.0;
        }

        try
        {
            IntPtr monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
            MonitorInfo info = new MonitorInfo
            {
                Size = Marshal.SizeOf(typeof(MonitorInfo))
            };

            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || string.IsNullOrEmpty(info.DeviceName))
            {
                return 0.0;
            }

            double hz = ReadActivePathHz(info.DeviceName);
            if (hz > 0.0)
            {
                return hz;
            }

            DeviceMode mode = new DeviceMode
            {
                Size = (ushort)Marshal.SizeOf(typeof(DeviceMode))
            };

            return EnumDisplaySettings(info.DeviceName, -1, ref mode) && mode.Frequency > 1 ? mode.Frequency : 0u;
        }
        catch (DllNotFoundException)
        {
            return 0.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0.0;
        }
    }

    private static double ReadActivePathHz(string monitorDeviceName)
    {
        // 缓冲区大小可能因为热插拔变化，允许重试几次。
        for (int attempt = 0; attempt < MaxQueryAttempts; attempt++)
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0
                || pathCount == 0
                || pathCount > MaxDisplayRecords
                || modeCount > MaxDisplayRecords)
            {
                return 0.0;
            }

            PathInfo[] paths = new PathInfo[pathCount];
            ModeInfo[] modes = new ModeInfo[Math.Max(1u, modeCount)];
            modeCount = (uint)modes.Length;

            int status = QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == InsufficientBuffer)
            {
                continue;
            }

            if (status != 0 || pathCount > paths.Length || modeCount > modes.Length)
            {
                return 0.0;
            }

            for (int i = 0; i < pathCount; i++)
            {
                PathInfo path = paths[i];
                if (path.Target.Available == 0)
                {
                    continue;
                }

                SourceDeviceName source = new SourceDeviceName
                {
                    Header = new DeviceInfoHeader
                    {
                        Type = DisplayConfigGetSourceName,
                        Size = (uint)Marshal.SizeOf(typeof(SourceDeviceName)),
                        Adapter = path.Source.Adapter,
                        Id = path.Source.Id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref source) != 0
                    || !string.Equals(source.DeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double hz = path.Target.RefreshRate.AsHertz();

                // 虚拟模式下的路径不会填 RefreshRate，需要到 mode 数组里查。
                if (hz == 0.0
                    && (path.Flags & PathSupportsVirtualMode) == 0
                    && path.Target.ModeIndex < modeCount
                    && modes[path.Target.ModeIndex].Type == TargetMode)
                {
                    hz = modes[path.Target.ModeIndex].VerticalSyncFrequency.AsHertz();
                }

                if (hz > 0.0)
                {
                    return hz;
                }
            }

            return 0.0;
        }

        return 0.0;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName source);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeIndex, ref DeviceMode mode);
}
