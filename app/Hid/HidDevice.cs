using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AttackOnShadow.Hid
{
    /// <summary>Description of one HID interface found on the system.</summary>
    public sealed class HidInfo
    {
        public string Path;
        public ushort VendorId, ProductId, Version;
        public ushort UsagePage, Usage;
        public int FeatureReportLength;
        public override string ToString() => $"{VendorId:X4}:{ProductId:X4} up={UsagePage:X4} u={Usage:X4} feat={FeatureReportLength}";
    }

    /// <summary>Minimal Win32 HID access: enumerate interfaces and exchange feature reports.</summary>
    public sealed class HidDevice : IDisposable
    {
        readonly SafeFileHandle handle;
        public HidInfo Info { get; }

        HidDevice(SafeFileHandle h, HidInfo info) { handle = h; Info = info; }

        public static List<HidInfo> Enumerate(ushort? vid = null, ushort? pid = null)
        {
            var result = new List<HidInfo>();
            Native.HidD_GetHidGuid(out var guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;
                    string path = InterfacePath(set, ref did);
                    if (path == null) continue;
                    var info = Probe(path);
                    if (info == null) continue;
                    if (vid.HasValue && info.VendorId != vid.Value) continue;
                    if (pid.HasValue && info.ProductId != pid.Value) continue;
                    result.Add(info);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        static string InterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA did)
        {
            Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Reads VID/PID and usage without needing read/write access.</summary>
        static HidInfo Probe(string path)
        {
            using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
                if (!Native.HidD_GetAttributes(h, ref attr)) return null;
                var info = new HidInfo { Path = path, VendorId = attr.VendorID, ProductId = attr.ProductID, Version = attr.VersionNumber };
                if (Native.HidD_GetPreparsedData(h, out IntPtr pp))
                {
                    try
                    {
                        if (Native.HidP_GetCaps(pp, out var caps) == Native.HIDP_STATUS_SUCCESS)
                        {
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.FeatureReportLength = caps.FeatureReportByteLength;
                        }
                    }
                    finally { Native.HidD_FreePreparsedData(pp); }
                }
                return info;
            }
        }

        public static HidDevice Open(HidInfo info)
        {
            var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                h.Dispose();
                // some HID stacks only allow feature reports through a zero-access handle
                h = Native.CreateFile(info.Path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть устройство");
            }
            return new HidDevice(h, info);
        }

        /// <summary>Sends a feature report. <paramref name="data"/> is the payload without the report id.</summary>
        public void SetFeature(byte[] data, byte reportId = 0)
        {
            int len = Math.Max(Info.FeatureReportLength, data.Length + 1);
            var buf = new byte[len];
            buf[0] = reportId;
            Array.Copy(data, 0, buf, 1, Math.Min(data.Length, len - 1));
            if (!Native.HidD_SetFeature(handle, buf, buf.Length))
                throw new IOException("HidD_SetFeature: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        /// <summary>Reads a feature report and returns the payload without the report id.</summary>
        public byte[] GetFeature(byte reportId = 0)
        {
            int len = Math.Max(Info.FeatureReportLength, 65);
            var buf = new byte[len];
            buf[0] = reportId;
            if (!Native.HidD_GetFeature(handle, buf, buf.Length))
                throw new IOException("HidD_GetFeature: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var data = new byte[len - 1];
            Array.Copy(buf, 1, data, 0, data.Length);
            return data;
        }

        public void Dispose() => handle.Dispose();

        static class Native
        {
            public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
            public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
            public const int HIDP_STATUS_SUCCESS = 0x00110000;

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

            [StructLayout(LayoutKind.Sequential)]
            public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

            [StructLayout(LayoutKind.Sequential)]
            public struct HIDP_CAPS
            {
                public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
                public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                    NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                    NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
            }

            [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
            [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
            [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
            [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA did);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
            public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, IntPtr detail, int size, out int required, IntPtr devInfo);
            [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        }
    }
}
