using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ClassroomBreakLock.Interop;

/// <summary>
/// Win11 / ClassIsland 风格的托盘右键菜单渲染器，替换 WinForms 默认那套灰色方框菜单。
/// 观感：浅色面 + 8px 圆角 + 1px 细边 + 悬停淡蓝圆角高亮 + 细分隔线。
/// </summary>
internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color SurfaceColor = Color.FromArgb(0xFB, 0xFB, 0xFB);
    private static readonly Color BorderColor = Color.FromArgb(0xE3, 0xE3, 0xE3);
    private static readonly Color HoverColor = Color.FromArgb(0xEF, 0xF6, 0xFC);
    private static readonly Color PressedColor = Color.FromArgb(0xDF, 0xEC, 0xF9);
    private static readonly Color SeparatorColor = Color.FromArgb(0xEC, 0xEC, 0xEC);

    public FluentMenuRenderer() : base(new FluentColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using GraphicsPath path = RoundedRect(r, 8);
        using SolidBrush brush = new SolidBrush(SurfaceColor);
        g.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using GraphicsPath path = RoundedRect(r, 8);
        using Pen pen = new Pen(BorderColor);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
        {
            return;
        }

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
        using GraphicsPath path = RoundedRect(r, 6);
        using SolidBrush brush = new SolidBrush(e.Item.Pressed ? PressedColor : HoverColor);
        g.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        int y = e.Item.Height / 2;
        using Pen pen = new Pen(SeparatorColor);
        g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    /// <summary>不要左侧那条图标/勾选竖条——ClassIsland 的菜单是干净的。</summary>
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>配套配色表：把 WinForms 默认的蓝渐变、灰边统统换掉。</summary>
internal sealed class FluentColorTable : ProfessionalColorTable
{
    private static readonly Color SurfaceColor = Color.FromArgb(0xFB, 0xFB, 0xFB);
    private static readonly Color HoverColor = Color.FromArgb(0xEF, 0xF6, 0xFC);
    private static readonly Color BorderColor = Color.FromArgb(0xE3, 0xE3, 0xE3);
    private static readonly Color Sep = Color.FromArgb(0xEC, 0xEC, 0xEC);

    public override Color ToolStripDropDownBackground => SurfaceColor;
    public override Color ImageMarginGradientBegin => SurfaceColor;
    public override Color ImageMarginGradientMiddle => SurfaceColor;
    public override Color ImageMarginGradientEnd => SurfaceColor;
    public override Color MenuBorder => BorderColor;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => HoverColor;
    public override Color MenuItemSelectedGradientBegin => HoverColor;
    public override Color MenuItemSelectedGradientEnd => HoverColor;
    public override Color MenuItemPressedGradientBegin => SurfaceColor;
    public override Color MenuItemPressedGradientEnd => SurfaceColor;
    public override Color SeparatorDark => Sep;
    public override Color SeparatorLight => Sep;
}
