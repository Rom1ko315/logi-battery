using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LogiBattery
{
    internal sealed class Monitor
    {
        internal const string MouseName = "G PRO Wireless", HeadsetName = "G733";
        private static readonly int[] BatteryFeatures = { 0x1004, 0x1000, 0x1001, 0x1F20 };
        private static readonly int[] HeadsetFeature = { 0x1F20 };
        private static readonly int[] LegacyRegisters = { 0x0D, 0x07 };
        private readonly Dictionary<string, Endpoint> endpoints = new Dictionary<string, Endpoint>();
        private readonly Dictionary<string, HidDevice> descriptors = new Dictionary<string, HidDevice>();
        private readonly Action<string> log;

        public Monitor(Action<string> log = null) { this.log = log; }

        public Reading[] Poll()
        {
            var devices = Hid.Enumerate(descriptors);
            if (log != null)
                foreach (var device in devices)
                    log(string.Format("{0:X4} {1:X4}:{2:X4} in={3} out={4} {5} openError={6}",
                        device.Product, device.UsagePage, device.Usage, device.Input, device.Output,
                        device.Path.Split('#')[1], device.AccessError));

            var readings = new List<Reading>();
            var present = new HashSet<string>();
            foreach (var group in devices.GroupBy(device => device.Group))
            {
                var candidates = group.Where(device => device.SupportsHidpp)
                    .OrderByDescending(device => Math.Min(device.Input, device.Output)).ToArray();
                int product = group.First().Product;
                if (candidates.Length == 0)
                {
                    AddUnavailable(readings, group.Key, product, group.Any(device => device.AccessError == 5));
                    continue;
                }
                int slots = ReceiverSlots(product);
                int[] addresses = slots == 0 ? new[] { 0xFF } : Enumerable.Range(1, slots).ToArray();
                var channels = new Dictionary<string, Hid>();
                try
                {
                    int count = readings.Count;
                    foreach (int address in addresses)
                    {
                        string key = group.Key + ":" + address.ToString("X2");
                        present.Add(key);
                        Reading reading = PollDevice(candidates, channels, key, address);
                        if (reading != null)
                            readings.Add(reading);
                    }
                    if (readings.Count == count)
                        AddUnavailable(readings, group.Key, product, channels.Count != 0 &&
                            channels.Values.All(hid => hid.AccessError == 5 || hid.AccessError == 32));
                }
                finally
                {
                    foreach (Hid channel in channels.Values)
                        channel.Dispose();
                }
            }
            foreach (string key in endpoints.Keys.Where(key => !present.Contains(key)).ToArray())
                endpoints.Remove(key);

            return MergeReadings(readings);
        }

        internal static Reading[] MergeReadings(IEnumerable<Reading> readings)
        {
            return readings.GroupBy(reading => reading.Id)
                .Select(group => group.OrderByDescending(row => row.Available)
                    .ThenByDescending(row => row.Charging).ThenByDescending(row => row.Level.HasValue)
                    .ThenBy(row => row.Estimated).First())
                .OrderBy(row => row.Kind == DeviceKind.Mouse ? 0 : row.Kind == DeviceKind.Headset ? 1 : 2)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Id, StringComparer.Ordinal)
                .ToArray();
        }

        private Reading PollDevice(HidDevice[] candidates, Dictionary<string, Hid> channels, string key, int address)
        {
            Endpoint known;
            endpoints.TryGetValue(key, out known);
            Reading last = null;
            if (known != null)
            {
                HidDevice device = candidates.FirstOrDefault(candidate => candidate.Path == known.Path);
                if (device != null)
                {
                    Hid channel = Open(device, channels);
                    Reading reading = channel.IsOpen ? Read(channel, address, known) : null;
                    if (reading != null)
                    {
                        last = reading;
                        known.Misses = 0;
                        if (++known.Reads < 10)
                            return reading;
                        known.Reads = 0;
                    }
                    else if (++known.Misses < 3 || known.Kind == DeviceKind.Headset && channel.IsOpen)
                    {
                        return Offline(known, channel.AccessError == 5 || channel.AccessError == 32);
                    }
                }
            }

            foreach (HidDevice device in candidates)
            {
                Hid channel = Open(device, channels);
                if (!channel.IsOpen)
                    continue;
                Endpoint found = Discover(device, channel, key, address);
                if (found == null)
                    continue;
                endpoints[key] = found;
                return Read(channel, address, found) ?? Offline(found, false);
            }
            return last ?? (known == null ? null : Offline(known, false));
        }

        private Hid Open(HidDevice device, Dictionary<string, Hid> channels)
        {
            Hid channel;
            if (channels.TryGetValue(device.Path, out channel))
                return channel;
            channel = new Hid(device);
            channels.Add(device.Path, channel);
            if (!channel.IsOpen && log != null)
                log(string.Format("HID open failed for {0:X4}: {1}", device.Product, channel.AccessError));
            return channel;
        }

        private Endpoint Discover(HidDevice device, Hid channel, string key, int address)
        {
            bool receiver = address != 0xFF;
            int protocol = receiver ? channel.Protocol(address) : 0;
            if (receiver && protocol == 0)
                return null;
            string name = receiver ? null : KnownName(device.Product) ?? device.Name;
            var endpoint = new Endpoint { Id = key, Path = device.Path };
            if (protocol == 1)
            {
                endpoint.Name = name ?? "Logitech · слот " + address;
                foreach (int register in LegacyRegisters)
                {
                    endpoint.Type = register;
                    if (Read(channel, address, endpoint) != null)
                        return endpoint;
                }
                return null;
            }

            int index = 0;
            foreach (int feature in IsG733(device.Product) ? HeadsetFeature : BatteryFeatures)
            {
                byte[] root = channel.Query(address, 0, 0, feature >> 8, feature & 255);
                if (root == null)
                    break;
                if (root[4] == 0)
                    continue;
                endpoint.Type = feature;
                index = root[4];
                break;
            }
            if (index == 0)
            {
                // Legacy direct USB peripherals use the same battery registers as paired devices.
                if (!receiver && channel.Protocol(address) == 1)
                {
                    endpoint.Name = name ?? "Logitech " + device.Product.ToString("X4");
                    foreach (int register in LegacyRegisters)
                    {
                        endpoint.Type = register;
                        if (Read(channel, address, endpoint) != null)
                            return endpoint;
                    }
                }
                return null;
            }

            if (endpoint.Type == 0x1004)
            {
                byte[] caps = channel.Query(address, index, 0);
                endpoint.Percent = caps != null && (caps[5] & 2) != 0;
            }
            if (!IsG733(device.Product))
            {
                int nameFeature = Find(channel, address, 0x0005);
                if (nameFeature != 0)
                {
                    string reported = DeviceName(channel, address, nameFeature);
                    if (!string.IsNullOrWhiteSpace(reported))
                        name = CanonicalName(reported);
                    byte[] kind = channel.Query(address, nameFeature, 2);
                    if (kind != null)
                        endpoint.Kind = kind[4] == 0 || kind[4] == 2 ? DeviceKind.Keyboard :
                            kind[4] >= 3 && kind[4] <= 5 ? DeviceKind.Mouse : DeviceKind.Other;
                }
                int firmware = Find(channel, address, 0x0003);
                if (firmware != 0)
                {
                    byte[] identity = channel.Query(address, firmware, 0);
                    if (identity != null && identity[0] == 0x11 && identity.Length >= 17 &&
                        identity.Skip(5).Take(4).Any(value => value != 0) &&
                        identity.Skip(11).Take(6).Any(value => value != 0))
                        endpoint.Id = "unit:" + BitConverter.ToString(identity, 5, 4) + ":" +
                            BitConverter.ToString(identity, 11, 6);
                }
            }
            endpoint.Name = name ?? (receiver ? "Logitech · слот " + address : "Logitech " + device.Product.ToString("X4"));
            if (endpoint.Type == 0x1F20)
                endpoint.Kind = DeviceKind.Headset;
            else if (endpoint.Name == MouseName || endpoint.Name == "PRO X Superlight")
                endpoint.Kind = DeviceKind.Mouse;
            return endpoint;
        }

        private Reading Read(Hid channel, int address, Endpoint endpoint)
        {
            byte[] reply;
            if (endpoint.Type < 0x1000)
                reply = channel.Protocol(address) == 1 ? channel.ReadRegister(address, endpoint.Type) : null;
            else
            {
                // Resolve the current index before calling it, including after a receiver is re-paired.
                int index = Find(channel, address, endpoint.Type);
                reply = index == 0 ? null : channel.Query(address, index, endpoint.Type == 0x1004 ? 1 : 0);
            }
            if (log != null)
                log(string.Format("{0}, slot {1:X2}, feature {2:X4}: {3}", endpoint.Name, address,
                    endpoint.Type, reply == null ? "no reply" : BitConverter.ToString(reply, 0, 7)));
            if (reply == null)
                return null;
            Reading reading = BatteryDecoder.Decode(endpoint.Name, endpoint.Type, reply[4], reply[5], reply[6], endpoint.Percent);
            return reading == null ? null : reading.ForDevice(endpoint.Id, endpoint.Kind);
        }

        private static int Find(Hid channel, int address, int feature)
        {
            byte[] reply = channel.Query(address, 0, 0, feature >> 8, feature & 255);
            return reply == null ? 0 : reply[4];
        }

        private static string DeviceName(Hid channel, int address, int feature)
        {
            byte[] length = channel.Query(address, feature, 0);
            if (length == null || length[4] > 96)
                return null;
            var bytes = new List<byte>();
            while (bytes.Count < length[4])
            {
                byte[] part = channel.Query(address, feature, 1, bytes.Count);
                if (part == null)
                    return null;
                int payload = part[0] == 0x11 ? 16 : 3;
                bytes.AddRange(part.Skip(4).Take(Math.Min(payload, length[4] - bytes.Count)));
            }
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        }

        private static Reading Offline(Endpoint endpoint, bool denied)
        {
            return new Reading(endpoint.Name, available: false, accessDenied: denied, id: endpoint.Id, kind: endpoint.Kind);
        }

        private static void AddUnavailable(List<Reading> readings, string key, int product, bool denied)
        {
            string name = KnownName(product);
            if (name == null && denied && ReceiverSlots(product) != 0)
                name = ReceiverSlots(product) == 6 && product == 0xC548 ? "Приёмник Logi Bolt" : "Приёмник Logitech";
            if (name != null)
                readings.Add(new Reading(name, available: false, accessDenied: denied, id: key,
                    kind: IsG733(product) ? DeviceKind.Headset : DeviceKind.Other));
        }

        private static string KnownName(int product)
        {
            if (IsG733(product))
                return HeadsetName;
            if (product == 0xC088 || product == 0x4079)
                return MouseName;
            return product == 0xC094 || product == 0x4093 ? "PRO X Superlight" : null;
        }

        private static string CanonicalName(string name)
        {
            if (name.Equals("PRO X Wireless", StringComparison.OrdinalIgnoreCase))
                return "PRO X Superlight";
            return name.Equals("G Pro", StringComparison.OrdinalIgnoreCase) ? MouseName : name;
        }

        private static bool IsG733(int product)
        {
            return product == 0x0AB5 || product == 0x0AFE || product == 0x0B1F;
        }

        internal static int ReceiverSlots(int product)
        {
            switch (product)
            {
                case 0xC52B: case 0xC532: case 0xC548: case 0xC54D: case 0xC54F:
                    return 6;
                case 0xC517:
                    return 4;
                case 0xC518: case 0xC51A: case 0xC51B: case 0xC521: case 0xC525: case 0xC526:
                case 0xC52E: case 0xC52F: case 0xC531: case 0xC534: case 0xC535: case 0xC537:
                case 0xC539: case 0xC53A: case 0xC53D: case 0xC53F: case 0xC541: case 0xC545: case 0xC547:
                    return 2;
                default:
                    return 0;
            }
        }

        private sealed class Endpoint
        {
            public string Id, Name, Path;
            public int Type, Misses, Reads;
            public DeviceKind Kind;
            public bool Percent;
        }
    }
}
