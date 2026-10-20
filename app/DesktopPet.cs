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

[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyProduct("YierBubu")]

namespace YierBubu
{
    enum Hand { None, Left, Right }

    struct PhysicalKey : IEquatable<PhysicalKey>
    {
        public long Device;
        public ushort Scan;
        public bool Extended;
        public bool E1;
        public int Code { get { return Scan | (Extended ? 0x100 : 0) | (E1 ? 0x200 : 0); } }
        public bool Equals(PhysicalKey other) { return Device == other.Device && Scan == other.Scan && Extended == other.Extended && E1 == other.E1; }
        public override bool Equals(object obj) { return obj is PhysicalKey && Equals((PhysicalKey)obj); }
        public override int GetHashCode() { return Device.GetHashCode() ^ (Code << 1); }
        public static PhysicalKey FromRaw(long device, ushort scan, ushort flags, ushort vk)
        {
            var key = new PhysicalKey { Device = device, Scan = scan, Extended = (flags & 2) != 0, E1 = (flags & 4) != 0 };
            if (vk == 0x13) { key.Scan = 0x45; key.Extended = false; key.E1 = true; }
            if (vk == 0x90) { key.Scan = 0x45; key.Extended = false; key.E1 = false; }
            if (vk == 0x2c) { key.Scan = 0x37; key.Extended = true; key.E1 = false; }
            return key;
        }
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
        readonly Dictionary<PhysicalKey, long> pulses = new Dictionary<PhysicalKey, long>();
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
            pulses[key] = now + 140;
            if (hand == Hand.Left) leftUntil = now + 90;
            else rightUntil = now + 90;
        }

        public bool Active(Hand side, long now)
        {
            foreach (HeldKey key in held.Values) if (key.Hand == side) return true;
            return now < (side == Hand.Left ? leftUntil : rightUntil);
        }

        public SortedSet<int> VisibleKeys(long now)
        {
            var result = new SortedSet<int>();
            foreach (PhysicalKey key in held.Keys) result.Add(key.Code);
            foreach (PhysicalKey key in new List<PhysicalKey>(pulses.Keys))
                if (now < pulses[key]) result.Add(key.Code); else pulses.Remove(key);
            return result;
        }
        public int? LatestKey(Hand side, long now)
        {
            int? selected = null; int rank = -1; long time = long.MinValue;
            foreach (var pair in held)
            {
                if (pair.Value.Hand != side) continue;
                int r = KeyboardLayout.IsModifier(pair.Key.Code) ? 0 : 1;
                if (r > rank || (r == rank && pair.Value.Since >= time)) { selected = pair.Key.Code; rank = r; time = pair.Value.Since; }
            }
            if (selected.HasValue) return selected;
            foreach (var pair in pulses)
            {
                if (now >= pair.Value || Classify(pair.Key.Scan, pair.Key.Extended) != side) continue;
                int r = KeyboardLayout.IsModifier(pair.Key.Code) ? 0 : 1;
                if (r > rank || (r == rank && pair.Value >= time)) { selected = pair.Key.Code; rank = r; time = pair.Value; }
            }
            return selected;
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
            foreach (PhysicalKey key in new List<PhysicalKey>(pulses.Keys)) if (key.Device == device) pulses.Remove(key);
            leftUntil = rightUntil = 0;
        }

        public void Reset() { held.Clear(); pulses.Clear(); leftUntil = rightUntil = 0; }
    }

    sealed class MouseState
    {
        readonly Dictionary<long, int> devices = new Dictionary<long, int>();
        long leftUntil, rightUntil, middleUntil;
        public void Edges(long device, ushort flags, long now)
        {
            int bits; devices.TryGetValue(device, out bits);
            if ((flags & 1) != 0) { bits |= 1; leftUntil = now + 90; }
            if ((flags & 2) != 0) bits &= ~1;
            if ((flags & 4) != 0) { bits |= 2; rightUntil = now + 90; }
            if ((flags & 8) != 0) bits &= ~2;
            if ((flags & 16) != 0) { bits |= 4; middleUntil = now + 140; }
            if ((flags & 32) != 0) bits &= ~4;
            if (bits == 0) devices.Remove(device); else devices[device] = bits;
        }
        public bool Active(bool left, long now)
        {
            int mask = left ? 1 : 2;
            foreach (int bits in devices.Values) if ((bits & mask) != 0) return true;
            return now < (left ? leftUntil : rightUntil);
        }
        public bool Middle(long now)
        {
            foreach (int bits in devices.Values) if ((bits & 4) != 0) return true;
            return now < middleUntil;
        }
        public void Reconcile(Func<int, bool> down, long now)
        {
            int mask = (!down(1) && now >= leftUntil ? 1 : 0) | (!down(2) && now >= rightUntil ? 2 : 0) | (!down(4) && now >= middleUntil ? 4 : 0);
            foreach (long device in new List<long>(devices.Keys))
            {
                int bits = devices[device] & ~mask;
                if (bits == 0) devices.Remove(device); else devices[device] = bits;
            }
        }
        public void RemoveDevice(long device) { devices.Remove(device); leftUntil = rightUntil = middleUntil = 0; }
        public void Reset() { devices.Clear(); leftUntil = rightUntil = middleUntil = 0; }
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
        readonly Rectangle[] mouseSources = new Rectangle[4];
        int bubuWidth, bubuHeight;
        public SpriteRenderer(string path)
        {
            sheet = new Bitmap(Path.Combine(Path.GetDirectoryName(path), "yier-body-underlay-v1.png"));
            if (sheet.Width < 800 || sheet.Height < 400) throw new InvalidDataException("Sprite sheet is too small.");
            // Runtime palette correction restricted to the bow and neckband.
            // Keep the source PNG intact and all face/ear colors unchanged.
            float cell = sheet.Width / 4f, sy = sheet.Height / 887f;
            for (int pose = 0; pose < 4; pose++)
            for (int y = (int)(290 * sy); y < (int)(335 * sy); y++)
            for (int x = (int)(pose * cell + 125 * cell / 443.5f); x < (int)(pose * cell + 320 * cell / 443.5f); x++)
            {
                Color c = sheet.GetPixel(x, y);
                float localX = (x - pose * cell) * 443.5f / cell, localY = y / sy;
                bool bow = localX >= 183 && localX <= 263;
                bool band = localY >= 306 && localY <= 324;
                if ((bow || band) && c.A > 0 && Math.Max(c.R, Math.Max(c.G, c.B)) < 170)
                    sheet.SetPixel(x, y, Color.FromArgb(c.A, 0, 0, 0));
            }
            bubuSheet = new Bitmap(Path.Combine(Path.GetDirectoryName(path), "bubu-single-paw-v1.png"));
            if (bubuSheet.Width < 400 || bubuSheet.Height < 400) throw new InvalidDataException("Bubu sprite sheet is too small.");
            for (int pose = 0; pose < 4; pose++)
            {
                int x0 = (pose % 2) * bubuSheet.Width / 2, x1 = (pose % 2 + 1) * bubuSheet.Width / 2;
                int y0 = (pose / 2) * bubuSheet.Height / 2, y1 = (pose / 2 + 1) * bubuSheet.Height / 2;
                int left = x1, top = y1, right = x0, bottom = y0;
                int ml = x1, mt = y1, mr = x0, mb = y0;
                // Cached source bounds only: the PNG is never rewritten or modified.
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color pixel = bubuSheet.GetPixel(x, y);
                    if (pixel.A > 24) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
                    if (pixel.A > 128 && pixel.B > pixel.R + 30 && pixel.G > 130) { ml = Math.Min(ml, x); mt = Math.Min(mt, y); mr = Math.Max(mr, x + 1); mb = Math.Max(mb, y + 1); }
                }
                if (left >= right || top >= bottom) throw new InvalidDataException("A Bubu pose is empty.");
                bubuSources[pose] = Rectangle.FromLTRB(Math.Max(x0, left - 2), Math.Max(y0, top - 2), Math.Min(x1, right + 2), Math.Min(y1, bottom + 2));
                bubuWidth = Math.Max(bubuWidth, bubuSources[pose].Width);
                bubuHeight = Math.Max(bubuHeight, bubuSources[pose].Height);
                if (ml >= mr || mt >= mb) throw new InvalidDataException("A Bubu pose has no blue mouse.");
                mouseSources[pose] = Rectangle.FromLTRB(ml, mt, mr, mb);
            }
        }

        void Bubu(Graphics g, int pose, RectangleF target, bool feedback = false, bool mouseLeft = false, bool mouseRight = false, bool mouseMiddle = false)
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
            if (feedback)
            {
                Rectangle blue = mouseSources[pose];
                var mouse = new RectangleF(dest.X + (blue.X - source.X) * scale, dest.Y + (blue.Y - source.Y) * scale, blue.Width * scale, blue.Height * scale);
                GraphicsState saved = g.Save();
                g.TranslateTransform(dest.Left + dest.Right, 0); g.ScaleTransform(-1, 1);
                InputArtwork.Mouse(g, mouse, mouseLeft, mouseRight, mouseMiddle);
                g.Restore(saved);
            }
        }

        void Sprite(Graphics g, int row, int column, RectangleF target)
        {
            float cellW = sheet.Width / 4f, cellH = sheet.Height / 2f;
            // Common source rectangle and baseline keep all frames on the same anchors.
            var source = new RectangleF(column * cellW + 45 * cellW / 443.5f,
                row * cellH + 15 * cellH / 443.5f, 355 * cellW / 443.5f, 410 * cellH / 443.5f);
            g.DrawImage(sheet, target, source, GraphicsUnit.Pixel);
        }

        public static int HeightFor(int width)
        {
            return (int)Math.Ceiling(width * 0.608f + 6 + Math.Max(2, width * 0.009f));
        }
        public static RectangleF KeyboardBounds(int width, double gapRatio)
        {
            float pet = width * (1f - (float)gapRatio) / 2;
            float scale = KeyboardLayout.Scale;
            return new RectangleF(width - pet + pet * (0.015f + 0.97f * (1 - scale) / 2),
                pet * 410f / 355f * 0.80f, pet * 0.97f * scale, pet * 0.29f * scale);
        }
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

        public Bitmap Render(int width, bool left, bool right, int gaze, bool mouseLeft, bool mouseRight, bool happy, double gapRatio, ISet<int> pressed = null, bool mouseMiddle = false, PawFrame motion = null, bool showHands = true)
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
                var bubuTarget = new RectangleF(width - pet, 0, pet, bodyHeight);
                Bubu(g, BubuPose(gaze, mouseLeft || mouseMiddle, mouseRight), bubuTarget, true, mouseLeft, mouseRight, mouseMiddle);
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
                g.ResetTransform();
                RectangleF board = KeyboardBounds(width, gapRatio);
                InputArtwork.Keyboard(g, board, pressed);
                if (showHands)
                {
                    if (motion == null) motion = new PawFrame(HandAnimator.Target(board, KeyboardLayout.FromSet(pressed, Hand.Left), Hand.Left), HandAnimator.Target(board, KeyboardLayout.FromSet(pressed, Hand.Right), Hand.Right), false);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    InputArtwork.Arm(g, HandAnimator.Shoulder(board, Hand.Right), motion.Right, pet);
                    InputArtwork.Arm(g, HandAnimator.Shoulder(board, Hand.Left), motion.Left, pet);
                }
                InputArtwork.PressedMarkers(g, board, pressed);
            }
            return frame;
        }

        public void Dispose() { bubuSheet.Dispose(); sheet.Dispose(); }
    }

    sealed class PetWindow : Form
    {
        readonly InputState input = new InputState();
        readonly MouseState mouseInput = new MouseState();
        readonly HandAnimator arms = new HandAnimator();
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
        string lastKeySignature;
        bool lastArmsMoving;
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
            arms.Reset();
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
                PhysicalKey physical = PhysicalKey.FromRaw(header.Device.ToInt64(), key.Scan, key.Flags, key.VKey);
                bool extended = physical.Extended;
                int vk = key.VKey;
                if (vk == 16) vk = key.Scan == 54 ? 0xa1 : 0xa0;
                else if (vk == 17) vk = extended ? 0xa3 : 0xa2;
                else if (vk == 18) vk = extended ? 0xa5 : 0xa4;
                long now = clock.ElapsedMilliseconds;
                input.Edge(physical, vk, (key.Flags & 1) == 0, now);
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
            bool mouseMiddle = mouseInput.Middle(now);
            SortedSet<int> visibleKeys = input.VisibleKeys(now);
            int relative = Cursor.Position.X - (Left + (int)(Width * 0.25));
            int threshold = Math.Max(20, Width / 7);
            int gaze = relative < -threshold ? -1 : relative > threshold ? 1 : 0;
            if (smoke) gaze = (int)(now / 350) % 3 - 1;
            if (smoke)
            {
                int phase = (int)(now / 650) % 4;
                visibleKeys = new SortedSet<int>(phase == 0 ? new[] { 0x1e } : phase == 1 ? new[] { 0x1f } : phase == 2 ? new[] { 0x1e, 0x26 } : new[] { 0x11c });
                left = phase != 3; right = phase >= 2;
                mouseLeft = phase == 1; mouseRight = phase == 2; mouseMiddle = phase == 3;
            }
            bool happy = !left && !right && now - lastInput > 5000;
            int? leftTarget = smoke ? KeyboardLayout.FromSet(visibleKeys, Hand.Left) : input.LatestKey(Hand.Left, now);
            int? rightTarget = smoke ? KeyboardLayout.FromSet(visibleKeys, Hand.Right) : input.LatestKey(Hand.Right, now);
            PawFrame handFrame = arms.Step(SpriteRenderer.KeyboardBounds(petWidth, config.GapRatio), leftTarget, rightTarget, now);
            int state = (left ? 1 : 0) | (right ? 2 : 0) | ((gaze + 1) << 2) | (mouseLeft ? 16 : 0) | (happy ? 32 : 0) | (mouseRight ? 64 : 0) | (mouseMiddle ? 128 : 0);
            string signature = string.Join(",", visibleKeys);
            if (lastState != state || lastKeySignature != signature || handFrame.Moving || lastArmsMoving)
            {
                using (Bitmap bitmap = renderer.Render(petWidth, left, right, gaze, mouseLeft, mouseRight, happy, config.GapRatio, visibleKeys, mouseMiddle, handFrame)) Present(bitmap);
                lastState = state;
                lastKeySignature = signature;
            }
            lastArmsMoving = handFrame.Moving;
            if (smoke && now > 4000)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke.txt"),
                    "PASS: animated A/S/two-hand/numpad reach; mouse feedback without letters; no caption strip; gaze and Raw Input; resize/reset; clean exit.\r\nKeyboard event count: " + rawEvents + "\r\nSmoke uses in-app snapshots, not synthesized OS input. Physical key/button behavior requires manual acceptance.\r\n");
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
            foreach (string name in new[] { "YierBubu", "YierBubu-v2", "YierBubu-v3", "YierBubu-v4", "YierBubu-1.0.1", "YierBubu-1.0.1-r2", "YierBubu-1.0.1-r3", "YierBubu-1.0.1-r4", "YierBubu-1.0.1-r5", "YierBubu-1.0.1-r6", "YierBubu-1.0.1-r7" })
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

        static int GoldPixels(Bitmap frame, RectangleF bounds)
        {
            int count = 0;
            Rectangle r = Rectangle.Intersect(Rectangle.Ceiling(bounds), new Rectangle(0, 0, frame.Width, frame.Height));
            for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            { Color c = frame.GetPixel(x, y); if (c.A > 150 && c.R > 235 && c.G > 185 && c.G < 220 && c.B < 105) count++; }
            return count;
        }

        static void SelfTest(string asset, string root)
        {
            using (var arm = new Bitmap(160, 160))
            using (Graphics g = Graphics.FromImage(arm))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                InputArtwork.Arm(g, new PointF(70, 30), new PointF(70, 130), 200);
                int whiteWidth = 0;
                for (int x = 0; x < arm.Width; x++)
                { Color c = arm.GetPixel(x, 70); if (c.A > 240 && c.R > 245 && c.G > 245 && c.B > 245) whiteWidth++; }
                Assert(whiteWidth >= 30, "broad rounded arm retains a thick white core");
                for (int y = 35; y < 122; y++)
                { Color c = arm.GetPixel(70, y); Assert(c.A > 240 && c.R > 245 && c.G > 245 && c.B > 245, "continuous mitten without wrist seam"); }
            }
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
                    Assert(small.Width == 187 && small.Height == 122, "compact width without caption bar");
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
                        for (int y = 0; y < width * 0.59 * 0.70; y++)
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
                var codes = new HashSet<int>();
                Assert(KeyboardLayout.Keys.Count == 104, "104-key ANSI layout");
                foreach (KeyCap cap in KeyboardLayout.Keys)
                {
                    Assert(codes.Add(cap.Code), "no duplicate physical key codes");
                    using (Bitmap frame = renderer.Render(720, false, false, 0, false, false, false, 0, new SortedSet<int> { cap.Code }))
                        Assert(GoldPixels(frame, KeyboardLayout.Bounds(SpriteRenderer.KeyboardBounds(720, 0), cap)) > 0, "specific key highlight: " + KeyboardLayout.Name(cap.Code));
                }
                using (Bitmap chord = renderer.Render(560, true, false, 0, false, false, false, 0, new SortedSet<int> { 0x1d, 0x1e }))
                {
                    foreach (KeyCap cap in KeyboardLayout.Keys)
                        if (cap.Code == 0x1d || cap.Code == 0x1e) Assert(GoldPixels(chord, KeyboardLayout.Bounds(SpriteRenderer.KeyboardBounds(560, 0), cap)) > 0, "chord highlights both keys");
                    chord.Save(Path.Combine(root, "exact-key-preview.png"), ImageFormat.Png);
                }
                using (Bitmap none = renderer.Render(560, false, false, 0, false, false, false, 0))
                using (Bitmap onlyA = renderer.Render(560, true, false, 0, false, false, false, 0, new SortedSet<int> { 0x1e }))
                {
                    foreach (KeyCap cap in KeyboardLayout.Keys)
                        if (cap.Code == 0x1e || cap.Code == 0x1f)
                        {
                            RectangleF r = KeyboardLayout.Bounds(SpriteRenderer.KeyboardBounds(560, 0), cap);
                            Assert(GoldPixels(none, r) == 0, "key highlight cleared");
                            if (cap.Code == 0x1f) Assert(GoldPixels(onlyA, r) == 0, "neighbor S is not highlighted by A");
                        }
                }
                using (Bitmap preview = new Bitmap(1120, SpriteRenderer.HeightFor(560) * 2))
                using (Graphics g = Graphics.FromImage(preview))
                {
                    int[][] combos = { new[] { 0x1e }, new[] { 0x1f }, new[] { 0x1d, 0x2a, 0x1e }, new[] { 0x11c, 0x148, 0x3b } };
                    for (int i = 0; i < 4; i++)
                    using (Bitmap frame = renderer.Render(560, i != 3, i == 3, 0, i == 0, i == 1, false, 0, new SortedSet<int>(combos[i]), i == 2))
                        g.DrawImageUnscaled(frame, i % 2 * 560, i / 2 * SpriteRenderer.HeightFor(560));
                    preview.Save(Path.Combine(root, "input-preview.png"), ImageFormat.Png);
                }
                using (Bitmap leftClick = renderer.Render(560, false, false, 0, true, false, false, 0))
                using (Bitmap rightClick = renderer.Render(560, false, false, 0, false, true, false, 0))
                using (Bitmap idleClick = renderer.Render(560, false, false, 0, false, false, false, 0))
                {
                    var bubuArea = new RectangleF(0, 0, 280, SpriteRenderer.HeightFor(560));
                    Assert(GoldPixels(leftClick, bubuArea) > 20 && GoldPixels(rightClick, bubuArea) > 20, "left/right mouse visible highlights");
                    Assert(GoldPixels(idleClick, bubuArea) == 0, "mouse highlight clears on release");
                    int changed = 0;
                    for (int y = 0; y < leftClick.Height; y++) for (int x = 0; x < 280; x++)
                        if (leftClick.GetPixel(x, y) != rightClick.GetPixel(x, y)) changed++;
                    Assert(changed > 20, "left/right mouse states render differently");
                }
                using (Bitmap middleClick = renderer.Render(560, false, false, 0, false, false, false, 0, null, true))
                {
                    int green = 0;
                    for (int y = 0; y < middleClick.Height; y++) for (int x = 0; x < 280; x++)
                    { Color c = middleClick.GetPixel(x, y); if (c.A > 200 && c.G > 210 && c.R < 160 && c.B < 200) green++; }
                    Assert(green > 10, "middle button wheel visibly green");
                }
                RectangleF keyBoard = SpriteRenderer.KeyboardBounds(560, 0);
                KeyCap mainOne = KeyboardLayout.Keys.Find(k => k.Code == 2);
                KeyCap numOne = KeyboardLayout.Keys.Find(k => k.Code == 0x4f);
                Assert(Math.Abs(KeyboardLayout.Bounds(keyBoard, numOne).Width - KeyboardLayout.Bounds(keyBoard, mainOne).Width) < 0.001,
                    "standard equal-width numpad and letter keys");
                Assert(Math.Abs(keyBoard.Width - 280 * 0.97f * 0.90f) < 0.001 && Math.Abs(keyBoard.Height - 280 * 0.29f * 0.90f) < 0.001,
                    "approved B keyboard is 90 percent in both dimensions");
                RectangleF spaceCap = KeyboardLayout.Bounds(keyBoard, KeyboardLayout.Keys.Find(k => k.Code == 0x39));
                RectangleF numberCap = KeyboardLayout.Bounds(keyBoard, mainOne);
                RectangleF numpadCap = KeyboardLayout.Bounds(keyBoard, numOne);
                Assert(spaceCap.Top < numberCap.Top && numpadCap.Left > numberCap.Right,
                    "rotated keyboard puts space near chin and numpad on screen right");
                Assert(HandAnimator.Shoulder(keyBoard, Hand.Left).X < HandAnimator.Shoulder(keyBoard, Hand.Right).X,
                    "physical left hand follows screen-left keys after rotation");
                foreach (KeyCap a in KeyboardLayout.Keys)
                {
                    RectangleF ra = KeyboardLayout.Bounds(keyBoard, a);
                    Assert(ra.Width > 0 && ra.Height > 0 && keyBoard.Contains(ra), "key stays in board");
                    foreach (KeyCap b in KeyboardLayout.Keys)
                        if (a.Code != b.Code) Assert(!ra.IntersectsWith(KeyboardLayout.Bounds(keyBoard, b)), "keys remain separate");
                }
                foreach (int idleWidth in new[] { 187, 319, 560 })
                foreach (double idleGap in new[] { 0.0, 0.08 })
                {
                    RectangleF idleBoard = SpriteRenderer.KeyboardBounds(idleWidth, idleGap);
                    float idlePet = idleWidth * (1f - (float)idleGap) / 2;
                    PointF lh = HandAnimator.Target(idleBoard, null, Hand.Left);
                    PointF rh = HandAnimator.Target(idleBoard, null, Hand.Right);
                    float bodyCenter = idleWidth - idlePet / 2;
                    Assert(Math.Abs(lh.X + rh.X - 2 * bodyCenter) < 0.001 && Math.Abs(lh.Y - rh.Y) < 0.001,
                        "idle hands symmetric about character center at every tested size/gap");
                    for (int pose = 0; pose < 4; pose++)
                    using (Bitmap idle = renderer.Render(idleWidth, pose == 1, pose == 2, 0, false, false, pose == 3, idleGap))
                    using (Bitmap bare = renderer.Render(idleWidth, pose == 1, pose == 2, 0, false, false, pose == 3, idleGap, null, false, null, false))
                    {
                        // Compare the entire bow rectangle, including its outline,
                        // against a render with all hand layers disabled.
                        int x0 = (int)Math.Floor(idleWidth - (264 - 45) * idlePet / 355);
                        int x1 = (int)Math.Ceiling(idleWidth - (182 - 45) * idlePet / 355);
                        int y0 = (int)Math.Floor((290 - 15) * idlePet / 355);
                        int y1 = (int)Math.Ceiling((335 - 15) * idlePet / 355);
                        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
                            Assert(idle.GetPixel(x, y).ToArgb() == bare.GetPixel(x, y).ToArgb(), "whole bow unobscured by idle hands");
                    }
                }
                foreach (Hand side in new[] { Hand.Left, Hand.Right })
                using (var idleArm = new Bitmap(560, SpriteRenderer.HeightFor(560)))
                using (Graphics g = Graphics.FromImage(idleArm))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    InputArtwork.Arm(g, HandAnimator.Shoulder(keyBoard, side), HandAnimator.Target(keyBoard, null, side), 280);
                    int minX = 560, maxX = 0, minY = idleArm.Height, maxY = 0;
                    for (int y = 0; y < idleArm.Height; y++) for (int x = 0; x < idleArm.Width; x++)
                        if (idleArm.GetPixel(x, y).A > 128) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                    Assert(Math.Abs((maxX - minX) - (maxY - minY)) <= 1 && maxX - minX > 50, "idle mitten is round and full");
                }
                for (int pose = 0; pose < 4; pose++)
                using (Bitmap bowFrame = renderer.Render(560, pose == 1, pose == 2, 0, false, false, pose == 3, 0, null, false, null, false))
                {
                    foreach (int sx in new[] { 199, 222, 245 })
                    {
                        Color c = bowFrame.GetPixel((int)(560 - (sx - 45) * 280f / 355), (int)((310 - 15) * 280f / 355));
                        Assert(c.A > 250 && c.R == 0 && c.G == 0 && c.B == 0, "bow remains pure black in every expression");
                    }
                }
                foreach (KeyCap cap in KeyboardLayout.Keys)
                {
                    if (cap.Label.Length != 1 || !char.IsLetter(cap.Label[0])) continue;
                    Hand side = InputState.Classify((ushort)(cap.Code & 0xff), (cap.Code & 0x100) != 0);
                    RectangleF key = KeyboardLayout.Bounds(keyBoard, cap);
                    PointF target = HandAnimator.Target(keyBoard, cap.Code, side);
                    Assert(HandAnimator.Distance(target, new PointF(key.X + key.Width/2, key.Y + key.Height/2)) < 0.001, "letter contact exactly on key center");
                    using (Bitmap withHands = renderer.Render(560, side == Hand.Left, side == Hand.Right, 0, false, false, false, 0, new SortedSet<int> { cap.Code }))
                    using (Bitmap noHands = renderer.Render(560, side == Hand.Left, side == Hand.Right, 0, false, false, false, 0, new SortedSet<int> { cap.Code }, false, null, false))
                    {
                        int white = 0;
                        for (int y = (int)target.Y - 28; y < (int)target.Y - 10; y++)
                        for (int x = (int)target.X - 8; x < (int)target.X + 8; x++)
                        {
                            Color a = withHands.GetPixel(x, y), b = noHands.GetPixel(x, y);
                            if (a.A > 240 && a.R > 245 && a.G > 245 && a.B > 245 && a.ToArgb() != b.ToArgb()) white++;
                        }
                        Assert(white > 10, "visible moving paw at letter " + cap.Label);
                        int dot = 6;
                        for (int y = (int)Math.Ceiling(keyBoard.Bottom) + 1; y < withHands.Height - dot - 1; y++)
                        for (int x = 0; x < withHands.Width; x++)
                            Assert(withHands.GetPixel(x, y).A == 0, "no gray input preview strip");
                    }
                }
                int w = 440, fh = SpriteRenderer.HeightFor(w);
                using (Bitmap storyboard = new Bitmap(w * 4, fh * 2))
                using (Graphics g = Graphics.FromImage(storyboard))
                {
                    var demo = new HandAnimator();
                    long[] times = { 0, 32, 64, 128, 192, 256, 320, 384 };
                    for (int i = 0; i < times.Length; i++)
                    {
                        int? l = i > 0 && i < 6 ? (int?)0x1e : null;
                        int? r = i == 4 || i == 5 ? (int?)0x26 : null;
                        var active = new SortedSet<int>(); if (l.HasValue) active.Add(l.Value); if (r.HasValue) active.Add(r.Value);
                        PawFrame pose = demo.Step(SpriteRenderer.KeyboardBounds(w, 0), l, r, times[i]);
                        using (Bitmap frame = renderer.Render(w, l.HasValue, r.HasValue, 0, false, false, false, 0, active, false, pose))
                            g.DrawImageUnscaled(frame, (i % 4) * w, (i / 4) * fh);
                    }
                    storyboard.Save(Path.Combine(root, "reach-preview.png"), ImageFormat.Png);
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
            mouse.Edges(5, 16, 900); Assert(mouse.Middle(1000) && !mouse.Active(true, 1000), "middle button independent");
            mouse.Edges(5, 32, 1001); Assert(mouse.Middle(1020) && !mouse.Middle(1100), "middle click pulse expires");
            mouse.Reset(); Assert(!mouse.Middle(1101), "middle reset");
            var exact = new InputState();
            exact.Edge(Key(0x1e, 1), 65, true, 0); string aStamp = string.Join(",", exact.VisibleKeys(10));
            exact.Edge(Key(0x1e, 1), 65, false, 20); exact.Edge(Key(0x1f, 1), 83, true, 200);
            Assert(aStamp != string.Join(",", exact.VisibleKeys(210)), "same-hand key changes invalidate snapshot");
            exact.Edge(Key(0x1f, 2), 83, true, 220); exact.Edge(Key(0x1f, 1), 83, false, 230);
            Assert(exact.VisibleKeys(400).Contains(0x1f), "same key on second keyboard remains visible");
            exact.RemoveDevice(2); Assert(!exact.VisibleKeys(401).Contains(0x1f), "exact key device removal clears");
            exact.Edge(Key(0x20, 1), 68, true, 500); exact.Edge(Key(0x20, 1), 68, false, 501);
            Assert(exact.VisibleKeys(550).Contains(0x20) && !exact.VisibleKeys(650).Contains(0x20), "exact-key short tap pulse");
            exact.Reset(); Assert(exact.VisibleKeys(651).Count == 0, "exact reset clears pulses");
            var reaching = new InputState();
            reaching.Edge(Key(0x1e, 1), 65, true, 0);
            reaching.Edge(Key(0x1f, 1), 83, true, 10);
            Assert(reaching.LatestKey(Hand.Left, 20) == 0x1f, "last pressed same-hand letter wins");
            reaching.Edge(Key(0x1d, 1), 0xa2, true, 30);
            Assert(reaching.LatestKey(Hand.Left, 40) == 0x1f, "modifier does not steal letter target");
            reaching.Edge(Key(0x1f, 1), 83, false, 50);
            Assert(reaching.LatestKey(Hand.Left, 60) == 0x1e, "release restores remaining held letter");
            reaching.Edge(Key(0x24, 1), 74, true, 70);
            Assert(reaching.LatestKey(Hand.Right, 80) == 0x24 && reaching.LatestKey(Hand.Left, 80) == 0x1e, "two hand targets independent");
            reaching.Reset(); Assert(!reaching.LatestKey(Hand.Left, 81).HasValue, "reset clears hand targets");
            var animator = new HandAnimator();
            RectangleF keyboard = SpriteRenderer.KeyboardBounds(560, 0);
            PointF goal = HandAnimator.Target(keyboard, 0x1e, Hand.Left);
            PawFrame frame0 = animator.Step(keyboard, null, null, 0);
            PawFrame moving = animator.Step(keyboard, 0x1e, null, 16);
            Assert(moving.Moving && HandAnimator.Distance(moving.Left, goal) < HandAnimator.Distance(frame0.Left, goal) && HandAnimator.Distance(moving.Left, goal) > 0.1, "smooth reach without instant jump");
            PawFrame arrived = moving;
            for (int t = 32; t <= 256; t += 16) arrived = animator.Step(keyboard, 0x1e, null, t);
            Assert(HandAnimator.Distance(arrived.Left, goal) < 0.4, "paw reaches exact A center");
            for (int t = 272; t <= 528; t += 16) arrived = animator.Step(keyboard, null, null, t);
            Assert(HandAnimator.Distance(arrived.Left, frame0.Left) < 0.4, "paw returns home on release");
            animator.Reset();
            RectangleF smallBoard = SpriteRenderer.KeyboardBounds(187, 0);
            Assert(HandAnimator.Distance(animator.Step(smallBoard, null, null, 600).Left, HandAnimator.Target(smallBoard, null, Hand.Left)) < 0.01, "resize resets old-scale coordinates");
            using (Bitmap mouseCanvas = new Bitmap(100, 50))
            using (Graphics g = Graphics.FromImage(mouseCanvas))
            {
                Color blue = Color.FromArgb(140, 190, 235); g.Clear(blue);
                InputArtwork.Mouse(g, new RectangleF(0, 0, 100, 50), false, false, false);
                for (int y = 15; y < 30; y++) for (int x = 15; x < 32; x++)
                {
                    Assert(mouseCanvas.GetPixel(x, y).ToArgb() == blue.ToArgb(), "mouse right label removed");
                    Assert(mouseCanvas.GetPixel(x + 50, y).ToArgb() == blue.ToArgb(), "mouse left label removed");
                }
            }
            Assert(PhysicalKey.FromRaw(1, 0x45, 4, 0x13).Code == 0x245, "Pause E1 identity");
            Assert(PhysicalKey.FromRaw(1, 0x45, 2, 0x90).Code == 0x45, "NumLock is not Pause");
            Assert(PhysicalKey.FromRaw(1, 0x54, 0, 0x2c).Code == 0x137, "PrintScreen normalization");
            Assert(PhysicalKey.FromRaw(1, 0x1c, 2, 0x0d).Code == 0x11c, "numpad Enter independent");
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
            File.WriteAllText(Path.Combine(root, "self-test.txt"), "PASS: round idle mittens; pure-black bow; space near chin and numpad on screen right; standard numpad proportions; 90-percent keyboard with separate keys; symmetric idle hands and unobscured bow across sizes/expressions; 104 exact key highlights; animated exact-key reach and release/resize recovery; latest-letter selection and independent hands; mouse glyphs removed; no caption strip; keyboard and mouse recovery; 72 head/ear regressions; config save/reload.\r\n");
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
