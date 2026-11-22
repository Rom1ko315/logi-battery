using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LogiBattery
{
    internal sealed class HidDevice
    {
        public string Path, Group, Name;
        public int Product, Input, Output, UsagePage, Usage, AccessError;
        public bool Bluetooth;
        public bool SupportsHidpp
        {
            get { return Input >= 7 && Output >= 7 &&
                ((UsagePage == 0xFF43 && Usage == 0x0202) || (UsagePage == 0xFF00 && (Usage == 1 || Usage == 2))); }
        }
    }

    internal sealed class Hid : IDisposable
    {
        private readonly HidDevice device;
        private readonly SafeFileHandle handle;
        private readonly EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        private readonly IntPtr operation = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Io)));
        private const int SoftwareId = 0x0C;
        public bool IsOpen { get { return !handle.IsInvalid; } }
        public int AccessError { get; private set; }

        public Hid(HidDevice device)
        {
            this.device = device;
            handle = CreateFile(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
                AccessError = Marshal.GetLastWin32Error();
        }

        public void Dispose()
        {
            handle.Dispose();
            ready.Dispose();
            Marshal.FreeHGlobal(operation);
        }

        public byte[] Query(int address, int feature, int function, int p0 = 0, int p1 = 0)
        {
            return Send(address, feature, (function << 4) | SoftwareId, p0, p1, device.Output >= 20);
        }

        public int Protocol(int address)
        {
            int marker = Environment.TickCount & 255;
            byte[] answer = Send(address, 0, 0x10 | SoftwareId, 0, 0, device.Output >= 20, marker, true);
            if (answer == null)
                return 0;
            if (answer[2] == 0x8F && answer[5] == 1)
                return 1;
            return answer[2] == 0 && answer[6] == marker ? answer[4] : 0;
        }

        public byte[] ReadRegister(int address, int register)
        {
            if (device.UsagePage != 0xFF00 || device.Usage != 1)
                return null;
            return Send(address, 0x81, register, 0, 0, false);
        }

        private byte[] Send(int address, int feature, int command, int p0, int p1, bool longReport,
            int p2 = 0, bool returnError = false)
        {
            byte[] output = new byte[device.Output];
            output[0] = longReport ? (byte)0x11 : (byte)0x10;
            output[1] = (byte)address;
            output[2] = (byte)feature;
            output[3] = (byte)command;
            output[4] = (byte)p0;
            output[5] = (byte)p1;
            output[6] = (byte)p2;
            if (Transfer(output, true, 250) != output.Length)
                return null;

            byte[] input = new byte[device.Input];
            Stopwatch time = Stopwatch.StartNew();
            int remaining;
            while ((remaining = 250 - (int)time.ElapsedMilliseconds) > 0)
            {
                if (Transfer(input, false, remaining) < 7)
                    return null;
                if ((input[0] != 0x10 && input[0] != 0x11) ||
                    (input[1] != address && !(device.Bluetooth && address == 0xFF && input[1] == 0)))
                    continue;
                if ((input[2] == 0xFF || input[2] == 0x8F) && input[3] == feature && input[4] == command)
                    return returnError ? input : null;
                if (input[2] == feature && input[3] == command)
                    return input;
            }
            return null;
        }

        private int Transfer(byte[] buffer, bool write, int timeout)
        {
            var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                ready.Reset();
                var io = new Io { Event = ready.SafeWaitHandle.DangerousGetHandle() };
                Marshal.StructureToPtr(io, operation, false);
                int length;
                bool completed = write
                    ? WriteFile(handle, pinned.AddrOfPinnedObject(), buffer.Length, out length, operation)
                    : ReadFile(handle, pinned.AddrOfPinnedObject(), buffer.Length, out length, operation);
                if (completed)
                    return length;
                if (Marshal.GetLastWin32Error() != 997)
                    return 0;
                if (ready.WaitOne(timeout))
                    return GetOverlappedResult(handle, operation, out length, false) ? length : 0;
                CancelIoEx(handle, operation);
                GetOverlappedResult(handle, operation, out length, true);
                return 0;
            }
            finally { pinned.Free(); }
        }

        public static List<HidDevice> Enumerate(Dictionary<string, HidDevice> descriptors)
        {
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            var devices = new List<HidDevice>();
            if (set == new IntPtr(-1))
                return devices;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                        break;
                    int bytes;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out bytes, IntPtr.Zero);
                    if (bytes < 8)
                        continue;
                    IntPtr detail = Marshal.AllocHGlobal(bytes);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var info = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, bytes, out bytes, ref info))
                            continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        HidDevice device;
                        if (descriptors.TryGetValue(path, out device))
                        {
                            devices.Add(device);
                            continue;
                        }
                        bool bluetooth;
                        int pid;
                        if (!TryLogitechProduct(path, out pid, out bluetooth))
                            continue;
                        string group = Container(set, ref info, path) + ":" + pid.ToString("X4");
                        device = Describe(path, group, pid, bluetooth);
                        if (device != null)
                        {
                            devices.Add(device);
                            if (device.AccessError == 0)
                                descriptors[path] = device;
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            var present = new HashSet<string>(devices.Select(device => device.Path));
            foreach (string path in descriptors.Keys.Where(path => !present.Contains(path)).ToArray())
                descriptors.Remove(path);
            return devices;
        }

        internal static bool TryLogitechProduct(string path, out int product, out bool bluetooth)
        {
            const string usb = "vid_046d&pid_";
            const string ble = "vid&0002046d_pid&";
            const string bleUsbAssigned = "vid&0001046d_pid&";
            product = 0;
            bluetooth = false;
            foreach (string prefix in new[] { usb, ble, bleUsbAssigned })
            {
                int start = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (start < 0 || path.Length < start + prefix.Length + 4)
                    continue;
                bluetooth = prefix != usb;
                return int.TryParse(path.Substring(start + prefix.Length, 4), NumberStyles.HexNumber, null, out product);
            }
            return false;
        }

        private static string Container(IntPtr set, ref InterfaceData info, string path)
        {
            var key = new PropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
            byte[] value = new byte[16];
            uint type, required;
            if (SetupDiGetDeviceProperty(set, ref info, ref key, out type, value, 16, out required, 0) &&
                type == 13 && required == 16 && new Guid(value) != Guid.Empty)
                return new Guid(value).ToString("N");
            string[] parts = path.Split('#');
            string interfaceName = parts[1];
            int collection = interfaceName.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            string instance = parts[2];
            int suffix = instance.LastIndexOf('&');
            return collection >= 0 && suffix >= 0
                ? interfaceName.Substring(0, collection) + "#" + instance.Substring(0, suffix)
                : path;
        }

        private static HidDevice Describe(string path, string group, int product, bool bluetooth)
        {
            using (var file = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var device = new HidDevice { Path = path, Group = group, Product = product, Bluetooth = bluetooth };
                IntPtr preparsed;
                if (file.IsInvalid)
                {
                    device.AccessError = Marshal.GetLastWin32Error();
                    return device;
                }
                if (!HidD_GetPreparsedData(file, out preparsed))
                    return null;
                try
                {
                    Caps caps;
                    if (HidP_GetCaps(preparsed, out caps) != 0x110000)
                        return null;
                    device.UsagePage = caps.UsagePage;
                    device.Usage = caps.Usage;
                    device.Input = caps.Input;
                    device.Output = caps.Output;
                    if (device.SupportsHidpp)
                    {
                        var label = new StringBuilder(128);
                        if (HidD_GetProductString(file, label, label.Capacity * 2))
                            device.Name = label.ToString().Trim();
                    }
                    return device;
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Io { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct Caps
        {
            [FieldOffset(0)] public ushort Usage;
            [FieldOffset(2)] public ushort UsagePage;
            [FieldOffset(4)] public ushort Input;
            [FieldOffset(6)] public ushort Output;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle file, StringBuilder value, int bytes);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr dev);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, ref InterfaceData dev);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)] private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref InterfaceData dev, ref PropertyKey key, out uint type, [Out] byte[] value, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int length, out int transferred, IntPtr operation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int length, out int transferred, IntPtr operation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr operation, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr operation);
    }
}
