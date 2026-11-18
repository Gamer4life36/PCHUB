using PcHub.Platform;
using PcHub.Services;
using System.Runtime.InteropServices;

namespace PcHub.Platform.Windows;

/// <summary>A physical display adapter as Windows sees it (any vendor).</summary>
public sealed record AdapterInfo(string Name, uint VendorId, ulong DedicatedBytes, string LuidTag)
{
    public const uint Nvidia = 0x10DE;
    public const uint Amd = 0x1002;
    public const uint Intel = 0x8086;

    public string Vendor => VendorId switch
    {
        Nvidia => "NVIDIA",
        Amd => "AMD",
        Intel => "Intel",
        _ => "GPU",
    };
}

/// <summary>
/// Lists hardware adapters through DXGI. The LUID tag ("luid_0x..._0x...") matches the
/// instance names of Windows' GPU performance counters, which work for every vendor.
/// </summary>
internal static unsafe partial class Dxgi
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint FlagSoftware = 2;
    private const uint MicrosoftBasicRender = 0x1414;
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>Hardware adapters, largest dedicated memory first. Empty if DXGI is unavailable.</summary>
    public static List<AdapterInfo> EnumerateAdapters()
    {
        var list = new List<AdapterInfo>();
        nint factory;
        try
        {
            if (CreateDXGIFactory1(IidFactory1, out factory) < 0 || factory == 0)
            {
                return list;
            }
        }
        catch (DllNotFoundException)
        {
            return list;
        }

        try
        {
            void** factoryVtbl = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)factoryVtbl[12];
            for (uint i = 0; i < 16; i++)
            {
                nint adapter;
                int hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                {
                    break;
                }

                try
                {
                    void** adapterVtbl = *(void***)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)adapterVtbl[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) < 0 || (desc.Flags & FlagSoftware) != 0 || desc.VendorId == MicrosoftBasicRender)
                    {
                        continue;
                    }

                    string name = new string(desc.Description).Trim();
                    string luid = $"luid_0x{(uint)desc.LuidHigh:X8}_0x{desc.LuidLow:X8}";
                    if (!list.Exists(a => a.LuidTag == luid))
                    {
                        list.Add(new AdapterInfo(name, desc.VendorId, desc.DedicatedVideoMemory, luid));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        list.Sort((a, b) => b.DedicatedBytes.CompareTo(a.DedicatedBytes));
        return list;
    }

    private static void Release(nint comObject)
    {
        void** vtbl = *(void***)comObject;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(comObject);
    }
}
