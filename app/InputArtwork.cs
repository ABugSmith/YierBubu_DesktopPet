using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace YierBubu
{
    sealed class KeyCap
    {
        public int Code;
        public string Label;
        public float X, Y, W, H;
    }

    static class KeyboardLayout
    {
        public static readonly List<KeyCap> Keys = Create();
        static void Add(List<KeyCap> keys, int code, string label, float x, float y, float w, float h)
        { keys.Add(new KeyCap { Code = code, Label = label, X = x, Y = y, W = w, H = h }); }
        static void Row(List<KeyCap> keys, int[] codes, string[] labels, float x, float y)
        { for (int i = 0; i < codes.Length; i++) Add(keys, codes[i], labels[i], x + i, y, 1, 1); }
        static List<KeyCap> Create()
        {
            var k = new List<KeyCap>();
            Add(k, 1, "Esc", 0, 0, 1, 1);
            for (int i = 0; i < 12; i++) Add(k, i < 10 ? 0x3b + i : 0x57 + i - 10, "F" + (i + 1), 2 + i + (i >= 4 ? 0.5f : 0) + (i >= 8 ? 0.5f : 0), 0, 1, 1);
            Row(k, new[] { 0x137, 0x46, 0x245 }, new[] { "Prt", "Scr", "Pau" }, 15.6f, 0);
            Row(k, new[] { 0x29, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0x0c, 0x0d }, new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=" }, 0, 1);
            Add(k, 0x0e, "Back", 13, 1, 2, 1);
            Add(k, 0x0f, "Tab", 0, 2, 1.5f, 1);
            Row(k, new[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b }, new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]" }, 1.5f, 2);
            Add(k, 0x2b, "\\", 13.5f, 2, 1.5f, 1);
            Add(k, 0x3a, "Caps", 0, 3, 1.75f, 1);
            Row(k, new[] { 0x1e, 0x1f, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28 }, new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'" }, 1.75f, 3);
            Add(k, 0x1c, "Enter", 12.75f, 3, 2.25f, 1);
            Add(k, 0x2a, "Shift", 0, 4, 2.25f, 1);
            Row(k, new[] { 0x2c, 0x2d, 0x2e, 0x2f, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35 }, new[] { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" }, 2.25f, 4);
            Add(k, 0x36, "Shift", 12.25f, 4, 2.75f, 1);
            Add(k, 0x1d, "Ctrl", 0, 5, 1.25f, 1); Add(k, 0x15b, "Win", 1.25f, 5, 1.25f, 1); Add(k, 0x38, "Alt", 2.5f, 5, 1.25f, 1);
            Add(k, 0x39, "Space", 3.75f, 5, 6.25f, 1);
            Add(k, 0x138, "Alt", 10, 5, 1.25f, 1); Add(k, 0x15c, "Win", 11.25f, 5, 1.25f, 1); Add(k, 0x15d, "Menu", 12.5f, 5, 1.25f, 1); Add(k, 0x11d, "Ctrl", 13.75f, 5, 1.25f, 1);
            Row(k, new[] { 0x152, 0x147, 0x149 }, new[] { "Ins", "Home", "PgU" }, 15.6f, 1);
            Row(k, new[] { 0x153, 0x14f, 0x151 }, new[] { "Del", "End", "PgD" }, 15.6f, 2);
            Add(k, 0x148, "Up", 16.6f, 4, 1, 1);
            Row(k, new[] { 0x14b, 0x150, 0x14d }, new[] { "Left", "Down", "Right" }, 15.6f, 5);
            Row(k, new[] { 0x45, 0x135, 0x37, 0x4a }, new[] { "Num", "/", "*", "-" }, 19.2f, 1);
            Row(k, new[] { 0x47, 0x48, 0x49 }, new[] { "7", "8", "9" }, 19.2f, 2);
            Add(k, 0x4e, "+", 22.2f, 2, 1, 2);
            Row(k, new[] { 0x4b, 0x4c, 0x4d }, new[] { "4", "5", "6" }, 19.2f, 3);
            Row(k, new[] { 0x4f, 0x50, 0x51 }, new[] { "1", "2", "3" }, 19.2f, 4);
            Add(k, 0x11c, "Ent", 22.2f, 4, 1, 2);
            Add(k, 0x52, "0", 19.2f, 5, 2, 1); Add(k, 0x53, ".", 21.2f, 5, 1, 1);
            return k;
        }
        public const float Scale = 0.90f;
        // Standard equal-width units for letters, navigation and numpad.
        public static float LayoutX(float x) { return x; }
        public static RectangleF FullSizeBounds(RectangleF board)
        {
            float fullWidth = board.Width / Scale;
            return new RectangleF(board.X - (fullWidth - board.Width) / 2,
                board.Y, fullWidth, board.Height / Scale);
        }
        public static RectangleF Bounds(RectangleF board, KeyCap key)
        {
            float u = board.Width / (LayoutX(23.2f) + 0.4f), v = board.Height / 6.3f;
            float a = LayoutX(key.X), b = LayoutX(key.X + key.W);
            float pad = Math.Max(0.18f, Math.Min(u * 0.09f, (b - a) * u * 0.14f));
            return new RectangleF(board.Left + (a + 0.2f) * u + pad,
                board.Bottom - (key.Y + key.H + 0.15f) * v + pad, (b - a) * u - 2 * pad, key.H * v - 2 * pad);
        }
        public static string Name(int code)
        {
            if (code == 0x1d) return "L Ctrl"; if (code == 0x11d) return "R Ctrl";
            if (code == 0x2a) return "L Shift"; if (code == 0x36) return "R Shift";
            if (code == 0x38) return "L Alt"; if (code == 0x138) return "R Alt";
            if (code == 0x15b) return "L Win"; if (code == 0x15c) return "R Win";
            if (code == 0x137) return "PrintScreen"; if (code == 0x245) return "Pause";
            if (code == 0x11c) return "Num Enter"; if (code == 0x135) return "Num /";
            foreach (KeyCap key in Keys)
                if (key.Code == code) return key.X >= 19.2f && key.Code != 0x45 ? "Num " + key.Label : key.Label;
            return code == 0x56 ? "ISO" : "Key " + code.ToString("X");
        }
        public static bool IsModifier(int code)
        {
            foreach (int modifier in new[] { 0x1d, 0x11d, 0x2a, 0x36, 0x38, 0x138, 0x15b, 0x15c })
                if (code == modifier) return true;
            return false;
        }
        public static int? FromSet(ISet<int> pressed, Hand side)
        {
            int? result = null; int best = -1;
            if (pressed == null) return null;
            foreach (int code in new SortedSet<int>(pressed))
            {
                if (InputState.Classify((ushort)(code & 0xff), (code & 0x100) != 0) != side) continue;
                int rank = IsModifier(code) ? 0 : 1;
                if (rank >= best) { result = code; best = rank; }
            }
            return result;
        }
    }

    static class InputArtwork
    {
        public static readonly Color Pressed = Color.FromArgb(255, 199, 78);
        static void Rounded(Graphics g, Brush brush, RectangleF r, float radius)
        {
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            using (var p = new GraphicsPath())
            {
                float d = radius * 2;
                p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
                g.FillPath(brush, p);
            }
        }
        public static void Keyboard(Graphics g, RectangleF board, ISet<int> pressed)
        {
            using (var frame = new SolidBrush(Color.FromArgb(93, 62, 123))) Rounded(g, frame, board, board.Height * 0.12f);
            using (var normal = new SolidBrush(Color.FromArgb(222, 204, 243)))
            using (var down = new SolidBrush(Pressed))
            using (var ink = new SolidBrush(Color.FromArgb(56, 36, 67)))
            using (var font = new Font("Consolas", Math.Max(2.2f, board.Width / 23.6f * 0.52f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            foreach (KeyCap key in KeyboardLayout.Keys)
            {
                RectangleF r = KeyboardLayout.Bounds(board, key);
                Rounded(g, pressed != null && pressed.Contains(key.Code) ? down : normal, r, Math.Max(0.2f, r.Height * 0.12f));
                // Flip the complete keyboard image on both axes, including labels.
                GraphicsState labelState = g.Save();
                g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
                g.RotateTransform(180);
                g.DrawString(key.Label, font, ink, new RectangleF(-r.Width / 2, -r.Height / 2, r.Width, r.Height), format);
                g.Restore(labelState);
            }
        }
        public static void PressedMarkers(Graphics g, RectangleF board, ISet<int> pressed)
        {
            if (pressed == null || pressed.Count == 0) return;
            // A short underline remains visible if a reaching arm covers a held
            // modifier in the newly rotated top row. Keep it inside its keycap.
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.None;
            using (var ink = new SolidBrush(Pressed))
            foreach (KeyCap key in KeyboardLayout.Keys)
            {
                if (!pressed.Contains(key.Code)) continue;
                RectangleF r = KeyboardLayout.Bounds(board, key);
                float inset = Math.Min(r.Width * 0.15f, 1.5f);
                float h = Math.Min(r.Height * 0.32f, Math.Max(1.2f, board.Height * 0.025f));
                g.FillRectangle(ink, r.X + inset, r.Bottom - h, r.Width - 2 * inset, h);
            }
            g.Restore(state);
        }
        public static void Mouse(Graphics g, RectangleF mouse, bool left, bool right, bool middle)
        {
            // Final screen orientation follows v1: right button is on viewer-left.
            using (var clip = new GraphicsPath())
            {
                clip.AddEllipse(mouse);
                GraphicsState state = g.Save(); g.SetClip(clip);
                using (var on = new SolidBrush(Color.FromArgb(235, Pressed)))
                {
                    if (right) g.FillRectangle(on, mouse.X, mouse.Y, mouse.Width / 2, mouse.Height);
                    if (left) g.FillRectangle(on, mouse.X + mouse.Width / 2, mouse.Y, mouse.Width / 2, mouse.Height);
                }
                using (var divider = new Pen(Color.FromArgb(180, 54, 77, 107), Math.Max(0.7f, mouse.Width * 0.025f)))
                    g.DrawLine(divider, mouse.X + mouse.Width / 2, mouse.Y, mouse.X + mouse.Width / 2, mouse.Bottom);
                using (var wheel = new SolidBrush(middle ? Color.FromArgb(116, 245, 160) : Color.FromArgb(81, 121, 158)))
                {
                    Rounded(g, wheel, new RectangleF(mouse.X + mouse.Width * 0.45f, mouse.Y + mouse.Height * 0.12f, mouse.Width * 0.1f, mouse.Height * 0.55f), mouse.Width * 0.04f);
                }
                g.Restore(state);
            }
        }
        public static void Arm(Graphics g, PointF shoulder, PointF contact, float pet)
        {
            // One continuous mitten silhouette: a broad arm with a round end,
            // without a narrow wrist or a separate palm seam.
            float rootRadius = pet * 0.100f, tipRadius = pet * 0.103f;
            var end = new PointF(contact.X, contact.Y - tipRadius);
            float dx = end.X - shoulder.X, dy = end.Y - shoulder.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < pet * 0.025f)
            {
                // At rest this is a true round mitten, with no capsule waist.
                float cx = (shoulder.X + end.X) * 0.5f, cy = (shoulder.Y + end.Y) * 0.5f;
                using (var white = new SolidBrush(Color.White))
                using (var edge = new Pen(Color.FromArgb(65, 24, 8), Math.Max(0.9f, pet * 0.017f)))
                {
                    var circle = new RectangleF(cx - tipRadius, cy - tipRadius, tipRadius * 2, tipRadius * 2);
                    g.FillEllipse(white, circle); g.DrawEllipse(edge, circle);
                }
                return;
            }
            float ux = length > 0.001f ? dx / length : 0;
            float uy = length > 0.001f ? dy / length : 1;
            Func<float, float, PointF> at = (along, across) =>
                new PointF(shoulder.X + ux * along - uy * across,
                           shoulder.Y + uy * along + ux * across);
            const float k = 0.55228475f;
            using (var shape = new GraphicsPath())
            using (var white = new SolidBrush(Color.White))
            using (var edge = new Pen(Color.FromArgb(65, 24, 8), Math.Max(0.9f, pet * 0.017f)))
            {
                edge.LineJoin = LineJoin.Round;
                shape.AddBezier(at(0, rootRadius), at(length * 0.32f, rootRadius),
                    at(length * 0.70f, tipRadius), at(length, tipRadius));
                shape.AddBezier(at(length, tipRadius), at(length + k * tipRadius, tipRadius),
                    at(length + tipRadius, k * tipRadius), at(length + tipRadius, 0));
                shape.AddBezier(at(length + tipRadius, 0), at(length + tipRadius, -k * tipRadius),
                    at(length + k * tipRadius, -tipRadius), at(length, -tipRadius));
                shape.AddBezier(at(length, -tipRadius), at(length * 0.70f, -tipRadius),
                    at(length * 0.32f, -rootRadius), at(0, -rootRadius));
                shape.AddBezier(at(0, -rootRadius), at(-k * rootRadius, -rootRadius),
                    at(-rootRadius, -k * rootRadius), at(-rootRadius, 0));
                shape.AddBezier(at(-rootRadius, 0), at(-rootRadius, k * rootRadius),
                    at(-k * rootRadius, rootRadius), at(0, rootRadius));
                shape.CloseFigure();
                g.FillPath(white, shape);
                g.DrawPath(edge, shape);
            }
        }
    }

    sealed class PawFrame
    {
        public readonly PointF Left, Right;
        public readonly bool Moving;
        public PawFrame(PointF left, PointF right, bool moving) { Left = left; Right = right; Moving = moving; }
    }

    sealed class HandAnimator
    {
        PointF left, right;
        bool initialized;
        long last;
        public static PointF Shoulder(RectangleF board, Hand side)
        {
            // Anchor to the character, not the asymmetric keyboard sections.
            RectangleF full = KeyboardLayout.FullSizeBounds(board);
            float pet = full.Width / 0.97f;
            float bodyLeft = full.X - pet * 0.015f;
            return new PointF(bodyLeft + pet * (side == Hand.Left ? 0.25f : 0.75f),
                full.Top - full.Height * 0.22f);
        }
        public static PointF Target(RectangleF board, int? code, Hand side)
        {
            if (code.HasValue)
                foreach (KeyCap cap in KeyboardLayout.Keys)
                    if (cap.Code == code.Value) { RectangleF key = KeyboardLayout.Bounds(board, cap); return new PointF(key.X + key.Width / 2, key.Y + key.Height / 2); }
            PointF shoulder = Shoulder(board, side);
            return new PointF(shoulder.X, shoulder.Y + KeyboardLayout.FullSizeBounds(board).Width / 0.97f * 0.103f);
        }
        public static double Distance(PointF a, PointF b) { return Math.Sqrt((a.X-b.X)*(a.X-b.X) + (a.Y-b.Y)*(a.Y-b.Y)); }
        static PointF Move(PointF current, PointF target, double alpha)
        {
            if (Distance(current, target) < 0.35) return target;
            return new PointF((float)(current.X + (target.X-current.X)*alpha), (float)(current.Y + (target.Y-current.Y)*alpha));
        }
        public PawFrame Step(RectangleF board, int? leftCode, int? rightCode, long now)
        {
            if (!initialized) { left = Target(board, null, Hand.Left); right = Target(board, null, Hand.Right); initialized = true; last = now - 16; }
            double dt = Math.Max(1, Math.Min(64, now - last)); last = now;
            PointF lt = Target(board, leftCode, Hand.Left), rt = Target(board, rightCode, Hand.Right);
            double alpha = 1 - Math.Exp(-dt / 28.0);
            left = Move(left, lt, alpha); right = Move(right, rt, alpha);
            return new PawFrame(left, right, Distance(left, lt) > 0.05 || Distance(right, rt) > 0.05);
        }
        public void Reset() { initialized = false; }
    }
}
