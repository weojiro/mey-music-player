using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;
using static TaskbarPlayerProto.NativeMethods;

namespace TaskbarPlayerProto;

// Dark tray menu in the style of Claude's: #1F1F1F, 28 px rows, full-width separators,
// rounded corners on Windows 11 (square on Windows 10).
static class TrayMenu
{
    static readonly Color Back = Color.FromArgb(0x1F, 0x1F, 0x1F);
    static readonly Color Hover = Color.FromArgb(0x33, 0x33, 0x33);
    static readonly Color Text = Color.FromArgb(0xE3, 0xE3, 0xE3);
    static readonly Color Dim = Color.FromArgb(0x9A, 0xA0, 0xA6);
    static readonly Color Line = Color.FromArgb(0x5E, 0x5E, 0x5E);

    const int TextLeft = 21;        // text starts here; the check mark sits in front of it
    const int ArrowRoom = 28;       // right side, for the submenu arrow
    const int ItemHeight = 28;
    const int SeparatorHeight = 15;
    const int PadTop = 12, PadBottom = 8;

    static readonly Forms.ToolStripRenderer Renderer = new DarkRenderer();

    // Applies the look to a menu and its submenus. Call after the items are added.
    public static void Style(Forms.ToolStripDropDownMenu menu)
    {
        menu.Renderer = Renderer;
        menu.BackColor = Back;
        menu.ForeColor = Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.DropShadowEnabled = false;      // a square shadow would show around the rounded corners
        menu.Font = new Font("Segoe UI", 9f);
        menu.HandleCreated += (_, _) => Round(menu.Handle);

        var items = menu.Items.Cast<Forms.ToolStripItem>().ToList();
        foreach (var item in items)
        {
            if (item is Forms.ToolStripSeparator sep)
            {
                sep.AutoSize = false;
                sep.Height = SeparatorHeight;
                continue;
            }
            // rows get their height from padding, so the width stays automatic
            item.Padding = new Forms.Padding(TextLeft, 0, ArrowRoom, 0);
            int extra = Math.Max(0, ItemHeight - item.GetPreferredSize(Size.Empty).Height);
            item.Padding = new Forms.Padding(TextLeft, extra / 2, ArrowRoom, extra - extra / 2);
            if (item is Forms.ToolStripMenuItem mi && mi.DropDown is Forms.ToolStripDropDownMenu sub) Style(sub);
        }
        // the drop-down ignores its own Padding: the space above and below comes from the first and last row
        if (items.Count > 0)
        {
            items[0].Margin = new Forms.Padding(0, PadTop, 0, 0);
            items[^1].Margin = new Forms.Padding(0, 0, 0, PadBottom);
        }
        // the drop-down does not count the rows' padding in its width: give it the widest row (texts change with the language)
        menu.Opening += (_, _) => menu.MinimumSize = new Size(
            items.Where(i => i is not Forms.ToolStripSeparator).Select(i => i.GetPreferredSize(Size.Empty).Width).DefaultIfEmpty(0).Max(), 0);
    }

    static void Round(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22000) return;
        int corner = DWMWCP_ROUND, border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    sealed class DarkRenderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Back);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
            // separators drawn here so they run edge to edge (the items are inset by 2 px)
            using var p = new Pen(Line);
            foreach (Forms.ToolStripItem item in e.ToolStrip.Items)
                if (item is Forms.ToolStripSeparator && item.Available)
                {
                    int y = item.Bounds.Top + item.Height / 2;
                    e.Graphics.DrawLine(p, 0, y, e.ToolStrip.Width, y);
                }
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(4, 0, e.Item.Width - 8, e.Item.Height);
            using var path = new GraphicsPath();
            const int d = 8;    // corner diameter of the hover highlight
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var b = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            bool rtl = e.Item.RightToLeft == Forms.RightToLeft.Yes;
            var color = e.Item.Enabled ? Text : Dim;
            e.TextColor = color;
            // the layout's text box sits at the top of the padded row: center the text over the whole row instead
            e.TextRectangle = rtl
                ? new Rectangle(ArrowRoom, 0, e.Item.Width - TextLeft - ArrowRoom, e.Item.Height)
                : new Rectangle(TextLeft, 0, e.Item.Width - TextLeft - ArrowRoom, e.Item.Height);
            e.TextFormat |= Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine;
            base.OnRenderItemText(e);

            if (e.Item is Forms.ToolStripMenuItem { Checked: true })
            {
                // a drawn check mark, centered in the space before the text (fonts differ between Windows 10 and 11)
                float cx = rtl ? e.Item.Width - TextLeft / 2f : TextLeft / 2f, cy = e.Item.Height / 2f;
                using var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4, cy), new PointF(cx - 1.3f, cy + 2.8f), new PointF(cx + 4, cy - 3) });
            }
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? Dim : Text;
            base.OnRenderArrow(e);
        }
    }
}
