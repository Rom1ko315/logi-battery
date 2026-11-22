using System;
namespace LogiBattery
{
    internal enum DeviceKind { Other, Mouse, Keyboard, Headset }
    internal enum BatteryBand { Unknown, Critical, Low, Good, Full }

    internal sealed class Reading
    {
        public readonly string Name, Id;
        public readonly int? Level;
        public readonly bool Charging, Estimated, Available, AccessDenied;
        public readonly BatteryBand Band;
        public readonly DeviceKind Kind;

        public Reading(string name, int? level = null, bool charging = false, bool estimated = false,
            bool available = true, bool accessDenied = false, BatteryBand band = BatteryBand.Unknown,
            string id = null, DeviceKind kind = DeviceKind.Other)
        {
            Name = name; Level = level; Charging = charging; Estimated = estimated; Available = available;
            AccessDenied = accessDenied; Band = band; Id = id ?? name; Kind = kind;
        }

        public string Value { get { return Level.HasValue ? (Estimated ? "≈" : "") + Level.Value + "%" : BandLabel; } }
        public string Status { get { return Charging ? "Заряжается" : Level == 100 ? "Полный заряд" :
            Level.HasValue ? (Estimated ? "Оценка по напряжению" : "Подключено") :
            Band != BatteryBand.Unknown ? "Уровень по данным устройства" :
            AccessDenied ? "Нет доступа к HID" : Available ? "Ожидание данных" : "Нет связи"; } }

        public int BarLevel
        {
            get { return Level ?? (Band == BatteryBand.Unknown ? 0 : (int)Band * 25); }
        }

        private string BandLabel
        {
            get
            {
                switch (Band)
                {
                    case BatteryBand.Critical: return "Критич.";
                    case BatteryBand.Low: return "Низкий";
                    case BatteryBand.Good: return "Хороший";
                    case BatteryBand.Full: return "Полный";
                    default: return "—";
                }
            }
        }

        public Reading ForDevice(string id, DeviceKind kind)
        {
            return new Reading(Name, Level, Charging, Estimated, Available, AccessDenied, Band, id, kind);
        }
    }

    internal static class BatteryDecoder
    {
        private static readonly int[] MouseMillivolts = { 3500, 3671, 3717, 3751, 3778, 3811, 3859, 3922, 3989, 4067, 4186 };
        private static readonly int[] HeadsetMillivolts = { 3500, 3659, 3690, 3708, 3725, 3763, 3800, 3835, 3870, 3930, 3990 };
        internal static Reading Decode(string name, int type, int a, int b, int flags, bool hasPercentage = true)
        {
            if (type == 0x0D)
            {
                int status = flags & 0xF0;
                if (a > 100 || (status != 0x30 && status != 0x50 && status != 0x90))
                    return null;
                return new Reading(name, status == 0x90 ? 100 : a, charging: status == 0x50);
            }
            if (type == 0x07)
            {
                BatteryBand band = a == 7 ? BatteryBand.Full : a == 5 ? BatteryBand.Good :
                    a == 3 ? BatteryBand.Low : a == 1 ? BatteryBand.Critical : BatteryBand.Unknown;
                if ((b & 0x22) == 0x22)
                    band = BatteryBand.Full;
                bool charging = (b & 0x21) == 0x21;
                return band == BatteryBand.Unknown && !charging ? null : new Reading(name, charging: charging, band: band);
            }
            if (type == 0x1000 || type == 0x1004)
            {
                if (flags >= 5)
                    return null;
                bool charging = flags == 1 || flags == 2 || flags == 4;
                if (type == 0x1004 && !hasPercentage)
                {
                    BatteryBand band = flags == 3 || b == 8 ? BatteryBand.Full : b == 4 ? BatteryBand.Good :
                        b == 2 ? BatteryBand.Low : b == 1 ? BatteryBand.Critical : BatteryBand.Unknown;
                    return band == BatteryBand.Unknown && !charging ? null : new Reading(name, charging: charging, band: band);
                }
                if (a > 100)
                    return null;
                if (flags == 3)
                    return new Reading(name, 100);
                if (a == 0 && charging && type == 0x1000)
                    return new Reading(name, charging: true);
                return new Reading(name, a, charging);
            }

            bool headset = type == 0x1F20;
            int voltage = (a << 8) | b;
            if (headset && flags != 1 && flags != 3 && flags != 7)
                return null;
            if (voltage < 2500 || voltage > 5000)
                return null;
            bool powered = (flags & 0x80) != 0;
            int chargeStatus = flags & 7;
            if (headset ? flags == 7 : powered && chargeStatus == 1)
                return new Reading(name, 100);
            bool chargingVoltage = headset ? flags == 3 : powered && chargeStatus == 0;
            return new Reading(name, Estimate(voltage, headset && name == "G733" ? HeadsetMillivolts : MouseMillivolts),
                chargingVoltage, true);
        }

        internal static int Estimate(int value, int[] curve)
        {
            if (value <= curve[0])
                return 0;
            for (int i = 1; i < curve.Length; i++)
                if (value <= curve[i])
                    return 10 * (i - 1) + (10 * (value - curve[i - 1])) / (curve[i] - curve[i - 1]);
            return 100;
        }

    }

    internal sealed class LowCharge
    {
        public const int Threshold = 30;
        private bool alerted;

        public bool Observe(Reading reading)
        {
            if (!reading.Level.HasValue)
                return false;
            if (reading.Level.Value >= Threshold + 3)
                alerted = false;
            if (reading.Charging || reading.Level.Value >= Threshold || alerted)
                return false;
            alerted = true;
            return true;
        }
    }
}
