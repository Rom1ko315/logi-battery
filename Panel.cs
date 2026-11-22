using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LogiBattery
{
    internal sealed class BatteryPanel : Form
    {
        private const int PanelWidth = 318, RowsPerPage = 4;
        private static readonly Color Surface = Color.FromArgb(23, 26, 34);
        private static readonly Color Card = Color.FromArgb(33, 38, 49);
        private static readonly Color MainText = Color.FromArgb(242, 247, 251);
        private static readonly Color SecondaryText = Color.FromArgb(167, 178, 194);
        private Reading[] readings = new Reading[0];
        private DateTime? updated;
        private Point? anchor;
        private int page;
        private int PageCount { get { return Math.Max(1, (readings.Length + RowsPerPage - 1) / RowsPerPage); } }
        private int PanelHeight { get { return 75 + 69 * Math.Max(1, Math.Min(RowsPerPage, readings.Length - page * RowsPerPage)); } }
        private readonly Font title = new Font("Segoe UI Semibold", 18, GraphicsUnit.Pixel);
        private readonly Font name = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
        private readonly Font value = new Font("Segoe UI Semibold", 19, GraphicsUnit.Pixel);
        private readonly Font caption = new Font("Segoe UI", 11, GraphicsUnit.Pixel);
        private float scale = 1;

        public BatteryPanel()
        {
            Text = "Заряд Logitech";
            AccessibleName = Text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Surface;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics graphics = CreateGraphics())
                scale = graphics.DpiX / 96f;
            ClientSize = new Size((int)Math.Ceiling(PanelWidth * scale), (int)Math.Ceiling(PanelHeight * scale));
            int rounded = 2;
            DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            Deactivate += (sender, args) => Close();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80;
                parameters.ClassStyle |= 0x00020000;
                return parameters;
            }
        }

        public void ShowAt(Point pointer)
        {
            anchor = pointer;
            PlaceAt(pointer);
            Show();
            Activate();
        }

        private void PlaceAt(Point pointer)
        {
            Screen monitor = Screen.FromPoint(pointer);
            Rectangle work = monitor.WorkingArea;
            bool top = monitor.Bounds.Top < work.Top && pointer.Y < work.Top;
            int x = Math.Max(work.Left + 6, Math.Min(pointer.X - Width / 2, work.Right - Width - 6));
            int y = top ? work.Top + 6 : Math.Min(work.Bottom - Height - 6, pointer.Y - Height - 10);
            Location = new Point(x, Math.Max(work.Top + 6, y));
        }

        public void UpdateReadings(Reading[] readings, DateTime? updated)
        {
            this.readings = readings;
            this.updated = updated;
            page = Math.Min(page, PageCount - 1);
            AccessibleDescription = string.Join(", ", readings.Select(row => row.Name + " " + row.Value));
            ResizePanel();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key == Keys.Escape)
            {
                Close();
                return true;
            }
            if (key == Keys.Left || key == Keys.Right)
            {
                TurnPage(key == Keys.Left ? -1 : 1);
                return true;
            }
            return base.ProcessCmdKey(ref message, key);
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            TurnPage(args.Delta > 0 ? -1 : 1);
            base.OnMouseWheel(args);
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            if (PageCount > 1 && args.Button == MouseButtons.Left && args.Y / scale > PanelHeight - 34)
            {
                float x = args.X / scale;
                if (x >= 223 && x <= 254)
                    TurnPage(-1);
                else if (x >= 282 && x <= 315)
                    TurnPage(1);
            }
            base.OnMouseDown(args);
        }

        private void TurnPage(int direction)
        {
            page = Math.Max(0, Math.Min(PageCount - 1, page + direction));
            ResizePanel();
        }

        private void ResizePanel()
        {
            ClientSize = new Size((int)Math.Ceiling(PanelWidth * scale), (int)Math.Ceiling(PanelHeight * scale));
            if (anchor.HasValue)
                PlaceAt(anchor.Value);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            Graphics graphics = args.Graphics;
            graphics.ScaleTransform(scale, scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var box = new Pen(Color.FromArgb(62, 70, 85)))
            using (var frame = BatteryArt.Round(new RectangleF(.5f, .5f, PanelWidth - 1, PanelHeight - 1), 9))
                graphics.DrawPath(box, frame);
            Label(graphics, Text, title, MainText, 15, 10);
            if (readings.Length == 0)
            {
                BatteryArt.FillRound(graphics, new RectangleF(12, 43, 294, 62), 9, Card);
                Label(graphics, updated.HasValue ? "Устройства с данными о заряде не найдены" : "Считываю заряд…",
                    name, SecondaryText, 22, 64);
            }
            int first = page * RowsPerPage;
            for (int i = first; i < Math.Min(readings.Length, first + RowsPerPage); i++)
                DrawDevice(graphics, readings[i], i, 43 + (i - first) * 69);
            if (updated.HasValue)
                Label(graphics, "Обновлено в " + updated.Value.ToString("HH:mm"),
                    caption, SecondaryText, 16, PanelHeight - 25);
            if (PageCount > 1)
            {
                int y = PanelHeight - 28;
                Label(graphics, "‹", title, page > 0 ? MainText : SecondaryText, 232, y - 3);
                Label(graphics, (page + 1) + " / " + PageCount, caption, SecondaryText, 252, y + 3);
                Label(graphics, "›", title, page < PageCount - 1 ? MainText : SecondaryText, 290, y - 3);
            }
        }

        private void DrawDevice(Graphics graphics, Reading row, int slot, int top)
        {
            Color accent = BatteryArt.Accent(row, slot);
            BatteryArt.FillRound(graphics, new RectangleF(12, top, 294, 62), 9, Card);
            BatteryArt.FillRound(graphics, new RectangleF(21, top + 12, 34, 34), 8, Color.FromArgb(46, 53, 66));
            BatteryArt.DeviceGlyph(graphics, row.Kind, 28, top + 19, accent);
            float labelWidth = graphics.MeasureString(row.Value, value).Width;
            LabelClipped(graphics, row.Name, name, MainText, new RectangleF(65, top + 7, 221 - labelWidth, 18));
            LabelClipped(graphics, row.Status, caption, SecondaryText, new RectangleF(65, top + 25, 221 - labelWidth, 17));
            Label(graphics, row.Value, value, row.Available ? accent : SecondaryText,
                295 - labelWidth, top + 11);
            BatteryArt.FillRound(graphics, new RectangleF(65, top + 47, 228, 4), 2, Color.FromArgb(60, 68, 84));
            if (row.BarLevel > 0)
                BatteryArt.FillRound(graphics, new RectangleF(65, top + 47, 228 * row.BarLevel / 100f, 4), 2, accent);
        }

        private static void Label(Graphics graphics, string text, Font font, Color color, float x, float y)
        {
            using (var brush = new SolidBrush(color))
                graphics.DrawString(text, font, brush, x, y);
        }

        private static void LabelClipped(Graphics graphics, string text, Font font, Color color, RectangleF box)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                graphics.DrawString(text, font, brush, box, format);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { title.Dispose(); name.Dispose(); value.Dispose(); caption.Dispose(); }
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal static class BatteryArt
    {
        public static Color Accent(Reading row, int slot)
        {
            if (row.Charging)
                return Color.FromArgb(125, 231, 161);
            if (!row.Level.HasValue && row.Band == BatteryBand.Unknown)
                return Color.FromArgb(151, 162, 180);
            if (row.Level < LowCharge.Threshold || row.Band == BatteryBand.Low || row.Band == BatteryBand.Critical)
                return Color.FromArgb(255, 176, 116);
            if (row.Kind == DeviceKind.Keyboard)
                return Color.FromArgb(135, 198, 255);
            return slot % 2 == 0 ? Color.FromArgb(105, 227, 207) : Color.FromArgb(191, 173, 255);
        }

        public static Icon TrayIcon(Reading[] rows)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                FillRound(graphics, new RectangleF(1, 1, 30, 30), 8, Color.FromArgb(25, 29, 37));
                if (rows.Length == 0)
                    FillRound(graphics, new RectangleF(9, 15, 14, 2), 1, Color.FromArgb(151, 162, 180));
                float width = rows.Length == 3 ? 7 : 10;
                float left = (32 - rows.Length * width - (rows.Length - 1) * 2) / 2;
                for (int i = 0; i < rows.Length; i++)
                {
                    Reading row = rows[i];
                    float x = left + (width + 2) * i;
                    Color accent = Accent(row, i);
                    FillRound(graphics, new RectangleF(x + (width - 4) / 2, 5, 4, 3), 1, accent);
                    FillRound(graphics, new RectangleF(x, 8, width, 18), 2, Color.FromArgb(77, 86, 104));
                    if (row.BarLevel > 0)
                    {
                        int fill = Math.Max(1, (int)Math.Round(14 * row.BarLevel / 100f));
                        FillRound(graphics, new RectangleF(x + 1, 24 - fill, width - 2, fill), 1, accent);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        public static void DeviceGlyph(Graphics graphics, DeviceKind kind, float x, float y, Color accent)
        {
            using (var pen = new Pen(accent, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (kind == DeviceKind.Mouse)
                {
                    using (var outline = Round(new RectangleF(x + 4, y + 1, 12, 18), 5))
                        graphics.DrawPath(pen, outline);
                    graphics.DrawLine(pen, x + 10, y + 5, x + 10, y + 8);
                }
                else if (kind == DeviceKind.Headset)
                {
                    graphics.DrawArc(pen, x + 2, y + 2, 16, 16, 180, 180);
                    using (var left = Round(new RectangleF(x + 2, y + 10, 4, 7), 1.5f))
                    using (var right = Round(new RectangleF(x + 14, y + 10, 4, 7), 1.5f))
                    {
                        graphics.DrawPath(pen, left);
                        graphics.DrawPath(pen, right);
                    }
                }
                else if (kind == DeviceKind.Keyboard)
                {
                    using (var outline = Round(new RectangleF(x + 1, y + 5, 18, 11), 2))
                        graphics.DrawPath(pen, outline);
                    for (int i = 0; i < 3; i++)
                        graphics.DrawLine(pen, x + 5 + 5 * i, y + 9, x + 6 + 5 * i, y + 9);
                    graphics.DrawLine(pen, x + 7, y + 12, x + 13, y + 12);
                }
                else
                {
                    using (var outline = Round(new RectangleF(x + 4, y + 4, 12, 12), 3))
                        graphics.DrawPath(pen, outline);
                    graphics.FillEllipse(pen.Brush, x + 9, y + 9, 2, 2);
                }
            }
        }

        public static GraphicsPath Round(RectangleF area, float radius)
        {
            var path = new GraphicsPath();
            float size = Math.Min(2 * radius, Math.Min(area.Width, area.Height));
            path.AddArc(area.Left, area.Top, size, size, 180, 90);
            path.AddArc(area.Right - size, area.Top, size, size, 270, 90);
            path.AddArc(area.Right - size, area.Bottom - size, size, size, 0, 90);
            path.AddArc(area.Left, area.Bottom - size, size, size, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics graphics, RectangleF area, float radius, Color color)
        {
            using (var path = Round(area, radius))
            using (var brush = new SolidBrush(color))
                graphics.FillPath(brush, path);
        }

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
