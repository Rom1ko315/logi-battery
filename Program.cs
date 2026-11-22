using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using FormsTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("Logi Battery")]
[assembly: AssemblyDescription("Logitech battery levels in the Windows notification area")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace LogiBattery
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--diagnose")
            {
                using (var log = new StreamWriter(args[1]))
                    foreach (var reading in new Monitor(log.WriteLine).Poll())
                        log.WriteLine("{0}: {1} · {2}", reading.Name, reading.Value, reading.Status);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--preview")
            {
                using (var panel = new BatteryPanel())
                {
                    panel.UpdateReadings(new[] {
                        new Reading("PRO X Superlight", 74, kind: DeviceKind.Mouse),
                        new Reading("G733", 26, estimated: true, kind: DeviceKind.Headset),
                        new Reading("MX Keys", 91, charging: true, kind: DeviceKind.Keyboard)
                    }, new DateTime(2026, 1, 1, 19, 42, 0));
                    using (var preview = new Bitmap(panel.Width, panel.Height))
                    {
                        panel.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                        preview.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return;
            }
            bool first;
            using (var instance = new Mutex(true, @"Local\LogiBatteryTray.4bb36eaa", out first))
                if (first)
                    using (var tray = new Tray())
                        Application.Run(tray);
        }
    }

    internal sealed class Tray : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "LogiBatteryTray";
        private const int Interval = 120000;
        private readonly NotifyIcon icon;
        private Icon artwork;
        private string iconKey;
        private readonly FormsTimer timer = new FormsTimer { Interval = Interval };
        private readonly Monitor monitor = new Monitor();
        private readonly Dictionary<string, LowCharge> alerts = new Dictionary<string, LowCharge>();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem autoStart;
        private Reading[] readings = new Reading[0];
        private BatteryPanel panel;
        private DateTime? updated;
        private bool busy, closing;

        public Tray()
        {
            menu.Items.Add("Открыть виджет", null, (sender, args) => TogglePanel());
            menu.Items.Add("Обновить заряд", null, (sender, args) => RefreshBattery());
            autoStart = new ToolStripMenuItem("Запускать с Windows");
            autoStart.Click += (sender, args) => ToggleAutoStart();
            menu.Items.Add(autoStart);
            menu.Items.Add("Проверить уведомление", null, (sender, args) =>
                Notify("Проверка уведомления", "Заряд ниже 30%. Пора поставить устройство на зарядку."));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (sender, args) => ExitThread());
            menu.Opening += (sender, args) => autoStart.Checked = HasAutoStart();
            icon = new NotifyIcon { ContextMenuStrip = menu };
            UpdateIcon();
            icon.Visible = true;
            icon.MouseClick += (sender, args) => { if (args.Button == MouseButtons.Left) TogglePanel(); };
            icon.BalloonTipClicked += (sender, args) => { if (panel == null) TogglePanel(); };
            timer.Tick += (sender, args) => RefreshBattery();
            timer.Interval = 50;
            timer.Start();
        }

        private async void RefreshBattery()
        {
            if (closing || busy)
                return;
            busy = true;
            timer.Stop();
            try
            {
                readings = await Task.Run(() => monitor.Poll());
                if (closing)
                    return;
                updated = DateTime.Now;
                UpdateIcon();
                if (panel != null)
                    panel.UpdateReadings(readings, updated);
                var low = readings.Where(ShouldNotify).ToArray();
                if (low.Length != 0)
                    Notify("Пора зарядить Logitech",
                        string.Join("  ·  ", low.Take(4).Select(reading => Limit(reading.Name, 36) + " " + reading.Value))
                        + (low.Length > 4 ? "  ·  +" + (low.Length - 4) : "")
                        + "\nЗаряд ниже 30%.");
            }
            catch (Exception error)
            {
                if (!(error is System.ComponentModel.Win32Exception ||
                    error is UnauthorizedAccessException || error is IOException))
                    throw;
                Debug.WriteLine(error);
            }
            finally
            {
                busy = false;
                if (!closing)
                {
                    timer.Interval = Interval;
                    timer.Start();
                }
            }
        }

        private bool ShouldNotify(Reading reading)
        {
            if (!reading.Level.HasValue)
                return false;
            LowCharge alert;
            if (!alerts.TryGetValue(reading.Id, out alert))
            {
                alert = new LowCharge();
                alerts.Add(reading.Id, alert);
            }
            return alert.Observe(reading);
        }

        private void UpdateIcon()
        {
            Reading[] visible = readings.Length <= 3 ? readings : readings
                .OrderByDescending(row => row.Level.HasValue || row.Band != BatteryBand.Unknown)
                .ThenBy(row => row.BarLevel).Take(3).ToArray();
            string key = string.Join("|", visible.Select(row => row.Value + row.Charging + row.Kind));
            if (key != iconKey)
            {
                Icon replacement = BatteryArt.TrayIcon(visible);
                icon.Icon = replacement;
                if (artwork != null)
                    artwork.Dispose();
                artwork = replacement;
                iconKey = key;
            }
            string tooltip = readings.Length == 0 ? "Заряд Logitech" :
                string.Join(" · ", visible.Select(row => Limit(row.Name, visible.Length > 2 ? 12 : 22) + " " + row.Value));
            icon.Text = Limit(tooltip + (readings.Length > visible.Length ? " +" + (readings.Length - visible.Length) : ""), 63);
        }

        private static string Limit(string value, int length)
        {
            return value.Length <= length ? value : value.Substring(0, length - 1) + "…";
        }

        private void TogglePanel()
        {
            if (panel != null)
            {
                panel.Close();
                return;
            }
            panel = new BatteryPanel();
            panel.FormClosed += (sender, args) => panel = null;
            panel.UpdateReadings(readings, updated);
            panel.ShowAt(Cursor.Position);
            if (!updated.HasValue || DateTime.Now - updated.Value > TimeSpan.FromSeconds(20))
                RefreshBattery();
        }

        private void Notify(string title, string message)
        {
            if (!WindowsToast.TryShow(title, message))
                icon.ShowBalloonTip(8000, title, message, ToolTipIcon.Warning);
        }

        private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }

        private static bool HasAutoStart()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && string.Equals(key.GetValue(RunName) as string, StartupCommand,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void ToggleAutoStart()
        {
            try
            {
                bool enabled = HasAutoStart();
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled)
                        key.DeleteValue(RunName, false);
                    else
                        key.SetValue(RunName, StartupCommand);
                }
            }
            catch (Exception error)
            {
                if (!(error is System.Security.SecurityException ||
                    error is UnauthorizedAccessException || error is IOException))
                    throw;
                MessageBox.Show("Windows не разрешила изменить автозапуск.", "Logi Battery");
            }
        }

        protected override void ExitThreadCore()
        {
            closing = true;
            timer.Stop();
            if (panel != null)
                panel.Close();
            icon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                icon.Dispose();
                menu.Dispose();
                if (artwork != null)
                    artwork.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class WindowsToast
    {
        private const string AppId = "LogiBatteryTray.PeripheralCharge";
        private static bool registered;

        internal static bool TryShow(string title, string body)
        {
            try
            {
                if (!registered)
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
                        key.SetValue("DisplayName", "Logi Battery");
                    registered = true;
                }
                Type xmlType = RuntimeType("Windows.Data.Xml.Dom.XmlDocument");
                Type toastType = RuntimeType("Windows.UI.Notifications.ToastNotification");
                Type manager = RuntimeType("Windows.UI.Notifications.ToastNotificationManager");
                Type notifierType = RuntimeType("Windows.UI.Notifications.ToastNotifier");
                if (xmlType == null || toastType == null || manager == null || notifierType == null)
                    return false;
                object xml = Activator.CreateInstance(xmlType);
                string content = "<toast><visual><binding template=\"ToastGeneric\"><text>" +
                    System.Security.SecurityElement.Escape(title) + "</text><text>" +
                    System.Security.SecurityElement.Escape(body) + "</text></binding></visual></toast>";
                xmlType.GetMethod("LoadXml", new[] { typeof(string) }).Invoke(xml, new object[] { content });
                object toast = Activator.CreateInstance(toastType, xml);
                object notifier = manager.GetMethod("CreateToastNotifier", new[] { typeof(string) })
                    .Invoke(null, new object[] { AppId });
                notifierType.GetMethod("Show", new[] { toastType }).Invoke(notifier, new[] { toast });
                return true;
            }
            catch (Exception error)
            {
                if (!(error is TargetInvocationException || error is COMException ||
                    error is UnauthorizedAccessException || error is System.Security.SecurityException ||
                    error is MissingMethodException || error is IOException))
                    throw;
                return false;
            }
        }

        private static Type RuntimeType(string name)
        {
            return Type.GetType(name + ", Windows, ContentType=WindowsRuntime");
        }
    }
}
