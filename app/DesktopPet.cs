using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace YierBubu
{
    enum Hand { None, Left, Right }

    struct PhysicalKey : IEquatable<PhysicalKey>
    {
        public long Device;
        public ushort Scan;
        public bool Extended;
        public bool Equals(PhysicalKey other) { return Device == other.Device && Scan == other.Scan && Extended == other.Extended; }
        public override bool Equals(object obj) { return obj is PhysicalKey && Equals((PhysicalKey)obj); }
        public override int GetHashCode() { return Device.GetHashCode() ^ (Scan << 1) ^ (Extended ? 1 : 0); }
    }

    sealed class HeldKey
    {
        public Hand Hand;
        public int VirtualKey;
        public long Since;
    }

    sealed class InputState
    {
        readonly Dictionary<PhysicalKey, HeldKey> held = new Dictionary<PhysicalKey, HeldKey>();
        long leftUntil, rightUntil;
        public int Count { get { return held.Count; } }

        public static Hand Classify(ushort scan, bool extended)
        {
            if (scan == 0 || scan == 0xff) return Hand.None;
            if (extended && scan == 0x5b) return Hand.Left;
            if (extended) return Hand.Right;
            // Set-1 physical scan codes. B is in the left bank, Y in the right.
            if (scan == 1 || (scan >= 2 && scan <= 6) || scan == 15 ||
                (scan >= 16 && scan <= 20) || scan == 29 ||
                (scan >= 30 && scan <= 34) || scan == 42 ||
                (scan >= 44 && scan <= 48) || scan == 56 || scan == 57 || scan == 58 ||
                (scan >= 59 && scan <= 62)) return Hand.Left;
            return Hand.Right;
        }

        public void Edge(PhysicalKey key, int virtualKey, bool down, long now)
        {
            if (!down) { held.Remove(key); return; }
            if (held.ContainsKey(key)) return; // OS auto-repeat must not extend the pulse.
            Hand hand = Classify(key.Scan, key.Extended);
            if (hand == Hand.None) return;
            held.Add(key, new HeldKey { Hand = hand, VirtualKey = virtualKey, Since = now });
            if (hand == Hand.Left) leftUntil = now + 90;
            else rightUntil = now + 90;
        }

        public bool Active(Hand side, long now)
        {
            foreach (HeldKey key in held.Values) if (key.Hand == side) return true;
            return now < (side == Hand.Left ? leftUntil : rightUntil);
        }

        public void Reconcile(Func<int, bool> isDown, long now)
        {
            var released = new List<PhysicalKey>();
            foreach (var pair in held)
                if (now - pair.Value.Since > 120 && !isDown(pair.Value.VirtualKey)) released.Add(pair.Key);
            foreach (PhysicalKey key in released) held.Remove(key);
        }

        public void RemoveDevice(long device)
        {
            var removed = new List<PhysicalKey>();
            foreach (PhysicalKey key in held.Keys) if (key.Device == device) removed.Add(key);
            foreach (PhysicalKey key in removed) held.Remove(key);
            leftUntil = rightUntil = 0;
        }

        public void Reset() { held.Clear(); leftUntil = rightUntil = 0; }
    }

    sealed class MouseState
    {
        readonly Dictionary<long, int> devices = new Dictionary<long, int>();
        long leftUntil, rightUntil;
        public void Edges(long device, ushort flags, long now)
        {
            int bits; devices.TryGetValue(device, out bits);
            if ((flags & 1) != 0) { bits |= 1; leftUntil = now + 90; }
            if ((flags & 2) != 0) bits &= ~1;
            if ((flags & 4) != 0) { bits |= 2; rightUntil = now + 90; }
            if ((flags & 8) != 0) bits &= ~2;
            if (bits == 0) devices.Remove(device); else devices[device] = bits;
        }
        public bool Active(bool left, long now)
        {
            int mask = left ? 1 : 2;
            foreach (int bits in devices.Values) if ((bits & mask) != 0) return true;
            return now < (left ? leftUntil : rightUntil);
        }
        public void Reconcile(Func<int, bool> down, long now)
        {
            int mask = (!down(1) && now >= leftUntil ? 1 : 0) | (!down(2) && now >= rightUntil ? 2 : 0);
            foreach (long device in new List<long>(devices.Keys))
            {
                int bits = devices[device] & ~mask;
                if (bits == 0) devices.Remove(device); else devices[device] = bits;
            }
        }
        public void RemoveDevice(long device) { devices.Remove(device); leftUntil = rightUntil = 0; }
        public void Reset() { devices.Clear(); leftUntil = rightUntil = 0; }
    }

    sealed class PetConfig
    {
        public int Width = 187;
        public double GapRatio = 0;
        public static int ClampWidth(int width) { return Math.Max(120, Math.Min(1000, width)); }
        public static PetConfig Load(string path)
        {
            var config = new PetConfig();
            if (!File.Exists(path)) return config;
            foreach (string line in File.ReadAllLines(path))
            {
                string clean = line.Trim();
                if (clean.StartsWith("#") || clean.StartsWith(";")) continue;
                int equal = clean.IndexOf('='); if (equal < 0) continue;
                string key = clean.Substring(0, equal).Trim().ToLowerInvariant();
                string value = clean.Substring(equal + 1).Trim();
                int width; double gap;
                if (key == "width" && int.TryParse(value, out width)) config.Width = ClampWidth(width);
                if (key == "gap_ratio" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out gap) && !double.IsNaN(gap) && !double.IsInfinity(gap)) config.GapRatio = Math.Max(0, Math.Min(0.15, gap));
            }
            return config;
        }
        public void Save(string path)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, "# Width in pixels (120..1000). Ctrl + mouse wheel also resizes.\r\nwidth=" + Width + "\r\n# Extra gap as fraction of total width (0..0.15).\r\ngap_ratio=" + GapRatio.ToString(CultureInfo.InvariantCulture) + "\r\n");
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { public int W, H; public Size(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] internal struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)] internal struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
        [StructLayout(LayoutKind.Sequential)] internal struct RawKeyboard { public ushort Scan, Flags, Reserved, VKey; public uint Message, Extra; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destDC, ref Point dest, ref Size size, IntPtr sourceDC, ref Point source, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
        internal delegate bool EnumWindowCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        internal static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
    }

    sealed class SpriteRenderer : IDisposable
    {
        readonly Bitmap sheet;
        readonly Bitmap bubuSheet;
        readonly Rectangle[] bubuSources = new Rectangle[4];
        int bubuWidth, bubuHeight;
        public SpriteRenderer(string path)
        {
            sheet = new Bitmap(path);
            if (sheet.Width < 800 || sheet.Height < 400) throw new InvalidDataException("Sprite sheet is too small.");
            bubuSheet = new Bitmap(Path.Combine(Path.GetDirectoryName(path), "bubu-single-paw-v1.png"));
            if (bubuSheet.Width < 400 || bubuSheet.Height < 400) throw new InvalidDataException("Bubu sprite sheet is too small.");
            for (int pose = 0; pose < 4; pose++)
            {
                int x0 = (pose % 2) * bubuSheet.Width / 2, x1 = (pose % 2 + 1) * bubuSheet.Width / 2;
                int y0 = (pose / 2) * bubuSheet.Height / 2, y1 = (pose / 2 + 1) * bubuSheet.Height / 2;
                int left = x1, top = y1, right = x0, bottom = y0;
                // Cached source bounds only: the PNG is never rewritten or modified.
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    if (bubuSheet.GetPixel(x, y).A > 24)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
                if (left >= right || top >= bottom) throw new InvalidDataException("A Bubu pose is empty.");
                bubuSources[pose] = Rectangle.FromLTRB(Math.Max(x0, left - 2), Math.Max(y0, top - 2), Math.Min(x1, right + 2), Math.Min(y1, bottom + 2));
                bubuWidth = Math.Max(bubuWidth, bubuSources[pose].Width);
                bubuHeight = Math.Max(bubuHeight, bubuSources[pose].Height);
            }
        }

        void Bubu(Graphics g, int pose, RectangleF target)
        {
            Rectangle source = bubuSources[pose];
            float scale = Math.Min(target.Width / bubuWidth, target.Height / bubuHeight) * 0.94f;
            float w = source.Width * scale, h = source.Height * scale;
            var dest = new RectangleF(target.X + (target.Width - w) / 2, target.Bottom - target.Height * 0.04f - h, w, h);
            // A single complete sprite owns head, ears, torso, mouse and both paws.
            // No idle silhouette exists underneath a turned-head pose.
            // Counter-mirror Bubu locally: the scene-wide flip still positions him
            // on the left, while his mouse stays on the outer screen-left paw.
            var corners = new[] { new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Bottom) };
            g.DrawImage(bubuSheet, corners, source, GraphicsUnit.Pixel);
        }

        void Sprite(Graphics g, int row, int column, RectangleF target)
        {
            float cellW = sheet.Width / 4f, cellH = sheet.Height / 2f;
            // Common source rectangle and baseline keep all frames on the same anchors.
            var source = new RectangleF(column * cellW + 45 * cellW / 443.5f,
                row * cellH + 15 * cellH / 443.5f, 355 * cellW / 443.5f, 410 * cellH / 443.5f);
            g.DrawImage(sheet, target, source, GraphicsUnit.Pixel);
        }

        public static int HeightFor(int width) { return (int)Math.Ceiling(width * 0.59); }
        public static int KeyboardPose(bool left, bool right, bool happy) { return left && right ? 0 : left ? 1 : right ? 2 : happy ? 3 : 0; }
        public static int BubuPose(int gaze, bool left, bool right) { return left || right ? 3 : gaze < 0 ? 1 : gaze > 0 ? 2 : 0; }

        public Bitmap BubuReference(int width, int pose, double gapRatio)
        {
            var frame = new Bitmap(width, HeightFor(width), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(width, 0); g.ScaleTransform(-1, 1);
                float pet = width * (1f - (float)gapRatio) / 2;
                Bubu(g, pose, new RectangleF(width - pet, 0, pet, pet * 410f / 355f));
            }
            return frame;
        }

        public Bitmap Render(int width, bool left, bool right, int gaze, bool mouseLeft, bool mouseRight, bool happy, double gapRatio)
        {
            int height = HeightFor(width);
            var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                // Mirror the layout and Yier; Bubu counter-mirrors inside his own target.
                g.TranslateTransform(width, 0);
                g.ScaleTransform(-1, 1);
                float pet = width * (1f - (float)gapRatio) / 2;
                float bodyHeight = pet * 410f / 355f;
                var yier = new RectangleF(0, 0, pet, bodyHeight);
                int pose = KeyboardPose(left, right, happy);
                Sprite(g, 0, pose, yier);
                if (left && right)
                {
                    // Compose independent lower halves from the two paw poses.
                    // Only the body/paws area changes; face and keyboard remain anchored.
                    GraphicsState saved = g.Save();
                    g.SetClip(new RectangleF(0, bodyHeight * 0.65f, pet / 2, bodyHeight * 0.24f));
                    Sprite(g, 0, 1, yier);
                    g.Restore(saved);
                    saved = g.Save();
                    g.SetClip(new RectangleF(pet / 2, bodyHeight * 0.65f, pet / 2, bodyHeight * 0.24f));
                    Sprite(g, 0, 2, yier);
                    g.Restore(saved);
                }
                var bubuTarget = new RectangleF(width - pet, 0, pet, bodyHeight);
                Bubu(g, BubuPose(gaze, mouseLeft, mouseRight), bubuTarget);
                // Small visual hand indicators also make simultaneous chords unambiguous.
                using (var on = new SolidBrush(Color.FromArgb(240, 152, 124, 211)))
                using (var off = new SolidBrush(Color.FromArgb(110, 160, 151, 175)))
                {
                    float dot = Math.Max(2, width * 0.009f);
                    g.FillEllipse(left ? on : off, pet * 0.28f, height - dot, dot, dot);
                    g.FillEllipse(right ? on : off, pet * 0.72f, height - dot, dot, dot);
                    g.FillEllipse(mouseLeft ? on : off, bubuTarget.X + pet * 0.36f, height - dot, dot, dot);
                    g.FillEllipse(mouseRight ? on : off, bubuTarget.X + pet * 0.64f, height - dot, dot, dot);
                }
            }
            return frame;
        }

        public void Dispose() { bubuSheet.Dispose(); sheet.Dispose(); }
    }

    sealed class PetWindow : Form
    {
        readonly InputState input = new InputState();
        readonly MouseState mouseInput = new MouseState();
        readonly PetConfig config;
        readonly string configPath;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SpriteRenderer renderer;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly bool smoke;
        bool registered, sessionRegistered, clickThrough, stopped;
        int petWidth, lastState = -1;
        long lastInput, lastReconcile;
        int rawEvents;
        int smokeResizeStep;

        public PetWindow(string asset, bool smokeMode)
        {
            smoke = smokeMode;
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
            config = PetConfig.Load(configPath);
            petWidth = config.Width;
            renderer = new SpriteRenderer(asset);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(petWidth, SpriteRenderer.HeightFor(petWidth));
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - Width - 32, work.Bottom - Height - 20);
            menu = new ContextMenuStrip();
            var passthrough = new ToolStripMenuItem("Mouse passthrough");
            passthrough.CheckOnClick = true;
            passthrough.CheckedChanged += delegate { clickThrough = passthrough.Checked; ApplyPassthrough(); };
            menu.Items.Add(passthrough);
            menu.Items.Add("Tiny (187 px)", null, delegate { ResizePet(187, true); });
            menu.Items.Add("Small (280 px)", null, delegate { ResizePet(280, true); });
            menu.Items.Add("Medium (420 px)", null, delegate { ResizePet(420, true); });
            menu.Items.Add("Original (560 px)", null, delegate { ResizePet(560, true); });
            menu.Items.Add("Reload config.ini", null, delegate { PetConfig loaded = PetConfig.Load(configPath); config.GapRatio = loaded.GapRatio; ResizePet(loaded.Width, false); });
            menu.Items.Add("Reset position", null, delegate { Rectangle r = Screen.FromPoint(Cursor.Position).WorkingArea; Location = new Point(r.Right - Width - 32, r.Bottom - Height - 20); lastState = -1; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Yier & Bubu - keyboard / mouse pet", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { passthrough.Checked = false; };
            timer.Interval = 16;
            timer.Tick += Tick;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80000 | 0x80 | 0x8000000; return p; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var devices = new[] { new Native.RawDevice { Page = 1, Usage = 6, Flags = 0x100 | 0x2000, Target = Handle }, new Native.RawDevice { Page = 1, Usage = 2, Flags = 0x100 | 0x2000, Target = Handle } };
            registered = Native.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(Native.RawDevice)));
            if (!registered) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Raw Input registration failed");
            sessionRegistered = Native.WTSRegisterSessionNotification(Handle, 0);
            timer.Start();
            Tick(this, EventArgs.Empty);
        }

        void ApplyPassthrough()
        {
            long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
            Native.SetWindowLongPtr(Handle, -20, new IntPtr(clickThrough ? style | 0x20 : style & ~0x20));
        }

        void ResizePet(int width, bool save)
        {
            int right = Right, bottom = Bottom;
            petWidth = PetConfig.ClampWidth(width);
            Size = new Size(petWidth, SpriteRenderer.HeightFor(petWidth));
            Location = new Point(right - Width, bottom - Height);
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
            lastState = -1;
            if (save && !smoke)
            {
                config.Width = petWidth;
                try { config.Save(configPath); }
                catch (IOException) { tray.ShowBalloonTip(2500, "Yier & Bubu", "Size changed, but config.ini could not be saved.", ToolTipIcon.Warning); }
                catch (UnauthorizedAccessException) { tray.ShowBalloonTip(2500, "Yier & Bubu", "Size changed, but config.ini is read-only.", ToolTipIcon.Warning); }
            }
        }

        void ReadKeyboard(IntPtr handle)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RawHeader));
            uint size = 0;
            if (Native.GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size < headerSize + 16 || size > 4096) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize) != size) return;
                var header = (Native.RawHeader)Marshal.PtrToStructure(buffer, typeof(Native.RawHeader));
                if (header.Type == 0)
                {
                    ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 4);
                    mouseInput.Edges(header.Device.ToInt64(), flags, clock.ElapsedMilliseconds);
                    if ((flags & 0x400) != 0 && Native.Down(0x11) && !clickThrough && Bounds.Contains(Cursor.Position))
                    {
                        short delta = Marshal.ReadInt16(buffer, (int)headerSize + 6);
                        if (delta != 0) ResizePet(petWidth + (delta > 0 ? 12 : -12), true);
                    }
                    return;
                }
                if (header.Type != 1) return;
                var key = (Native.RawKeyboard)Marshal.PtrToStructure(IntPtr.Add(buffer, (int)headerSize), typeof(Native.RawKeyboard));
                if (key.VKey == 255 || key.Scan == 0xff) return;
                bool extended = (key.Flags & 6) != 0;
                int vk = key.VKey;
                if (vk == 16) vk = key.Scan == 54 ? 0xa1 : 0xa0;
                else if (vk == 17) vk = extended ? 0xa3 : 0xa2;
                else if (vk == 18) vk = extended ? 0xa5 : 0xa4;
                long now = clock.ElapsedMilliseconds;
                input.Edge(new PhysicalKey { Device = header.Device.ToInt64(), Scan = key.Scan, Extended = extended }, vk, (key.Flags & 1) == 0, now);
                lastInput = now;
                rawEvents++;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0xff: ReadKeyboard(m.LParam); break;
                case 0xfe: if (m.WParam.ToInt64() == 2) { input.RemoveDevice(m.LParam.ToInt64()); mouseInput.RemoveDevice(m.LParam.ToInt64()); } break;
                case 0x2b1: input.Reset(); mouseInput.Reset(); break;
                case 0x218: input.Reset(); mouseInput.Reset(); break;
                case 0x201: Native.ReleaseCapture(); Native.SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); return;
                case 0x205: menu.Show(Cursor.Position); return;
                case 0x7e: ResizePet(petWidth, false); break;
            }
            base.WndProc(ref m);
        }

        void Tick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            if (smoke && smokeResizeStep == 0 && now > 1000) { ResizePet(280, false); smokeResizeStep = 1; }
            if (smoke && smokeResizeStep == 1 && now > 2000) { ResizePet(config.Width, false); smokeResizeStep = 2; }
            if (now - lastReconcile >= 120) { input.Reconcile(Native.Down, now); mouseInput.Reconcile(Native.Down, now); lastReconcile = now; }
            bool left = input.Active(Hand.Left, now), right = input.Active(Hand.Right, now);
            bool mouseLeft = mouseInput.Active(true, now), mouseRight = mouseInput.Active(false, now);
            int relative = Cursor.Position.X - (Left + (int)(Width * 0.25));
            int threshold = Math.Max(20, Width / 7);
            int gaze = relative < -threshold ? -1 : relative > threshold ? 1 : 0;
            if (smoke) gaze = (int)(now / 350) % 3 - 1;
            bool happy = !left && !right && now - lastInput > 5000;
            int state = (left ? 1 : 0) | (right ? 2 : 0) | ((gaze + 1) << 2) | (mouseLeft ? 16 : 0) | (happy ? 32 : 0) | (mouseRight ? 64 : 0);
            if (lastState != state)
            {
                using (Bitmap bitmap = renderer.Render(petWidth, left, right, gaze, mouseLeft, mouseRight, happy, config.GapRatio)) Present(bitmap);
                lastState = state;
            }
            if (smoke && now > 4000)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke.txt"),
                    "PASS: exclusive full-body Bubu poses cycled left/center/right; mirrored layered window rendered; keyboard and mouse Raw Input registered; live resize to 280 and back completed; clean exit requested.\r\nKeyboard event count: " + rawEvents + "\r\nPhysical key/button behavior requires manual acceptance.\r\n");
                Close();
            }
        }

        void Present(Bitmap bitmap)
        {
            IntPtr desktop = Native.GetDC(IntPtr.Zero);
            IntPtr memory = Native.CreateCompatibleDC(desktop);
            IntPtr dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                dib = bitmap.GetHbitmap(Color.FromArgb(0));
                previous = Native.SelectObject(memory, dib);
                var dest = new Native.Point(Left, Top);
                var size = new Native.Size(bitmap.Width, bitmap.Height);
                var source = new Native.Point(0, 0);
                var blend = new Native.Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                if (!Native.UpdateLayeredWindow(Handle, desktop, ref dest, ref size, memory, ref source, 0, ref blend, 2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Transparent window rendering failed");
            }
            finally
            {
                if (previous != IntPtr.Zero) Native.SelectObject(memory, previous);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                Native.DeleteDC(memory);
                Native.ReleaseDC(IntPtr.Zero, desktop);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (!stopped)
            {
                stopped = true;
                timer.Stop(); timer.Dispose(); input.Reset(); mouseInput.Reset();
                if (registered) Native.RegisterRawInputDevices(new[] { new Native.RawDevice { Page = 1, Usage = 6, Flags = 1, Target = IntPtr.Zero }, new Native.RawDevice { Page = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero } }, 2, (uint)Marshal.SizeOf(typeof(Native.RawDevice)));
                if (sessionRegistered) Native.WTSUnRegisterSessionNotification(Handle);
                tray.Visible = false; tray.Dispose(); menu.Dispose(); renderer.Dispose();
            }
            base.OnFormClosed(e);
        }
    }

    static class Program
    {
        static void ReplacePreviousInstance(string root)
        {
            int current = Process.GetCurrentProcess().Id;
            foreach (string name in new[] { "YierBubu", "YierBubu-v2", "YierBubu-v3", "YierBubu-v4" })
            foreach (Process process in Process.GetProcessesByName(name))
            using (process)
            {
                if (process.Id == current) continue;
                string path;
                try { path = process.MainModule.FileName; }
                catch (System.ComponentModel.Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                if (!string.Equals(Path.GetDirectoryName(path).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                int id = process.Id;
                Native.EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    uint owner; Native.GetWindowThreadProcessId(window, out owner);
                    if (owner == (uint)id) Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                if (!process.WaitForExit(3000)) throw new IOException("Please exit the previous pet from its tray menu, then launch again.");
            }
        }

        static void Assert(bool value, string message) { if (!value) throw new Exception("Test failed: " + message); }
        static PhysicalKey Key(ushort scan, long device) { return new PhysicalKey { Scan = scan, Device = device }; }

        static void SelfTest(string asset, string root)
        {
            var state = new InputState();
            Assert(InputState.Classify(30, false) == Hand.Left && InputState.Classify(36, false) == Hand.Right, "A/J physical banks");
            Assert(InputState.Classify(57, false) == Hand.Left && InputState.Classify(29, true) == Hand.Right, "space / right control");
            state.Edge(Key(30, 1), 65, true, 0);
            state.Edge(Key(31, 1), 83, true, 10);
            state.Edge(Key(30, 1), 65, false, 20);
            Assert(state.Active(Hand.Left, 200) && state.Count == 1, "same-hand chord release");
            state.Edge(Key(36, 1), 74, true, 200);
            Assert(state.Active(Hand.Left, 210) && state.Active(Hand.Right, 210), "both hands");
            state.Edge(Key(31, 1), 83, false, 220);
            Assert(!state.Active(Hand.Left, 230) && state.Active(Hand.Right, 230), "independent release");
            state.Reset();
            state.Edge(Key(30, 1), 65, true, 0); state.Edge(Key(30, 1), 65, false, 1);
            Assert(state.Active(Hand.Left, 50) && !state.Active(Hand.Left, 100), "short tap pulse expires");
            state.Edge(Key(30, 1), 65, true, 200); state.Edge(Key(30, 1), 65, true, 300);
            Assert(state.Count == 1, "auto-repeat deduplicated");
            state.Reconcile(delegate(int vk) { return false; }, 400);
            Assert(state.Count == 0 && !state.Active(Hand.Left, 400), "missed release reconciled");
            state.Edge(Key(30, 1), 65, true, 500); state.Edge(Key(30, 2), 65, true, 500);
            state.RemoveDevice(1); Assert(state.Count == 1 && state.Active(Hand.Left, 700), "device isolation");
            state.Reset(); Assert(state.Count == 0 && !state.Active(Hand.Left, 701), "session reset");
            using (var renderer = new SpriteRenderer(asset))
            {
                int h = SpriteRenderer.HeightFor(560);
                using (Bitmap atlas = new Bitmap(1120, h * 2))
                using (Graphics g = Graphics.FromImage(atlas))
                {
                    for (int i = 0; i < 4; i++)
                    using (Bitmap frame = renderer.Render(560, (i & 1) != 0, (i & 2) != 0, 0, false, false, false, 0))
                    {
                        Assert(frame.GetPixel(0, 0).A == 0, "transparent corner");
                        g.DrawImageUnscaled(frame, (i % 2) * 560, (i / 2) * h);
                    }
                    atlas.Save(Path.Combine(root, "state-preview.png"), ImageFormat.Png);
                }
                using (Bitmap small = renderer.Render(187, false, false, 0, false, false, true, 0))
                {
                    Assert(small.Width == 187 && small.Height == 111, "one-third default width with uncropped ears");
                    small.Save(Path.Combine(root, "default-size-preview.png"), ImageFormat.Png);
                }
                using (Bitmap buttons = new Bitmap(1120, h * 2))
                using (Graphics g = Graphics.FromImage(buttons))
                {
                    for (int i = 0; i < 4; i++)
                    using (Bitmap frame = renderer.Render(560, false, false, 0, (i & 1) != 0, (i & 2) != 0, false, 0))
                        g.DrawImageUnscaled(frame, (i % 2) * 560, (i / 2) * h);
                    buttons.Save(Path.Combine(root, "mouse-preview.png"), ImageFormat.Png);
                }
                // Regression: transparent pixels in a turned pose must never reveal an idle ear.
                // Compare the whole head region to one isolated complete-pose reference.
                foreach (int width in new[] { 187, 247, 560 })
                foreach (double gap in new[] { 0.0, 0.08 })
                for (int gaze = -1; gaze <= 1; gaze++)
                for (int buttons = 0; buttons < 4; buttons++)
                {
                    int expectedPose = buttons != 0 ? 3 : gaze < 0 ? 1 : gaze > 0 ? 2 : 0;
                    using (Bitmap actual = renderer.Render(width, false, false, gaze, (buttons & 1) != 0, (buttons & 2) != 0, false, gap))
                    using (Bitmap expected = renderer.BubuReference(width, expectedPose, gap))
                    {
                        for (int y = 0; y < actual.Height * 0.85; y++)
                        for (int x = 0; x < width / 2; x++)
                            Assert(actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(), "exclusive Bubu head/ears at width " + width + ", gaze " + gaze + ", buttons " + buttons);
                    }
                }
                // The actual blue mouse must remain on screen-left in every full pose.
                for (int pose = 0; pose < 4; pose++)
                using (Bitmap frame = renderer.BubuReference(560, pose, 0))
                {
                    long blueX = 0; int count = 0;
                    for (int y = frame.Height / 2; y < frame.Height; y++)
                    for (int x = 0; x < frame.Width / 2; x++)
                    {
                        Color pixel = frame.GetPixel(x, y);
                        if (pixel.A > 200 && pixel.B > pixel.R + 30 && pixel.G > 130) { blueX += x; count++; }
                    }
                    Assert(count > 100 && (double)blueX / count < frame.Width / 4.0, "mouse on outer screen-left paw in pose " + pose);
                }
                using (Bitmap looks = new Bitmap(2240, h * 3))
                using (Graphics g = Graphics.FromImage(looks))
                {
                    for (int gaze = -1; gaze <= 1; gaze++)
                    for (int buttons = 0; buttons < 4; buttons++)
                    using (Bitmap frame = renderer.Render(560, false, false, gaze, (buttons & 1) != 0, (buttons & 2) != 0, false, 0))
                        g.DrawImageUnscaled(frame, buttons * 560, (gaze + 1) * h);
                    looks.Save(Path.Combine(root, "look-preview.png"), ImageFormat.Png);
                }
            }
            var mouse = new MouseState();
            mouse.Edges(1, 1, 0); mouse.Edges(1, 4, 1);
            Assert(mouse.Active(true, 200) && mouse.Active(false, 200), "both mouse buttons held");
            mouse.Edges(1, 2, 210);
            Assert(!mouse.Active(true, 220) && mouse.Active(false, 220), "mouse buttons independent release");
            mouse.Reconcile(delegate(int vk) { return false; }, 400);
            Assert(!mouse.Active(false, 400), "mouse missed release reconciled");
            mouse.Edges(2, 1, 500); mouse.Edges(2, 2, 501);
            Assert(mouse.Active(true, 550) && !mouse.Active(true, 600), "mouse quick click pulse");
            mouse.Edges(3, 4, 700); mouse.RemoveDevice(3);
            Assert(!mouse.Active(false, 701), "mouse device removal");
            mouse.Edges(4, 1, 800); mouse.Reset();
            Assert(!mouse.Active(true, 801), "mouse session reset");
            Assert(SpriteRenderer.BubuPose(0, true, false) == 3 && SpriteRenderer.BubuPose(0, false, true) == 3 && SpriteRenderer.BubuPose(0, true, true) == 3, "one mouse paw for either button and chord");
            // The final global flip moves original-left button/key to screen-right.
            Assert(SpriteRenderer.KeyboardPose(true, false, false) == 1 && SpriteRenderer.KeyboardPose(false, true, false) == 2, "keyboard source poses remain physical before mirror");
            string configTest = Path.Combine(root, "config-test.ini");
            try
            {
                File.WriteAllText(configTest, "width=1\ngap_ratio=NaN\n");
                PetConfig parsed = PetConfig.Load(configTest);
                Assert(parsed.Width == 120 && parsed.GapRatio == 0, "config validation");
                parsed.Width = 238; parsed.GapRatio = 0.02; parsed.Save(configTest);
                PetConfig again = PetConfig.Load(configTest);
                Assert(again.Width == 238 && again.GapRatio == 0.02, "config atomic save and reload");
            }
            finally { if (File.Exists(configTest)) File.Delete(configTest); }
            File.WriteAllText(Path.Combine(root, "self-test.txt"), "PASS: outer screen-left mouse position in all four Bubu poses; keyboard and independent mouse state/recovery; 72 exclusive head/ear pixel regressions; corrected gaze selection; config atomic save/reload; transparent state/mouse/gaze atlases.\r\n");
        }

        [STAThread]
        static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string asset = Path.GetFullPath(Path.Combine(root, "..", "concept", "interaction-poses-v4.png"));
            bool testing = Array.IndexOf(args, "--self-test") >= 0;
            bool smoke = Array.IndexOf(args, "--smoke") >= 0;
            try
            {
                if (testing) { SelfTest(asset, root); return 0; }
                if (!smoke && Array.IndexOf(args, "--replace") >= 0) ReplacePreviousInstance(root);
                bool created;
                using (var mutex = new Mutex(true, smoke ? "Local\\YierBubuSpritePrototypeSmoke" : "Local\\YierBubuSpritePrototype", out created))
                {
                    if (!created) return 0;
                    Native.SetProcessDPIAware();
                    Application.EnableVisualStyles();
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var window = new PetWindow(asset, smoke)) Application.Run(window);
                }
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(root, testing ? "self-test.txt" : smoke ? "smoke.txt" : "error.txt"), "FAIL: " + ex.ToString());
                if (!testing && !smoke) MessageBox.Show(ex.Message, "YierBubu", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
