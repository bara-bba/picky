using System.Drawing;
using System.Windows.Forms;

namespace ScreenSnap;

/// <summary>PowerToys-dark palette for the tray context menu.</summary>
internal static class TrayIcon
{
    public static ContextMenuStrip CreateDarkMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()) { RoundedEdges = false },
            BackColor = ColorTranslator.FromHtml("#2B2B2B"),
            ForeColor = Color.White,
            ShowImageMargin = false,
        };
        return menu;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        // Subtle neutral hover fill; the border matches the fill so there is no boxed outline.
        private static readonly Color Hover = ColorTranslator.FromHtml("#3D3D3D");

        public override Color ToolStripDropDownBackground => ColorTranslator.FromHtml("#2B2B2B");
        public override Color ImageMarginGradientBegin => ColorTranslator.FromHtml("#2B2B2B");
        public override Color ImageMarginGradientMiddle => ColorTranslator.FromHtml("#2B2B2B");
        public override Color ImageMarginGradientEnd => ColorTranslator.FromHtml("#2B2B2B");
        public override Color MenuBorder => ColorTranslator.FromHtml("#3D3D3D");
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => ColorTranslator.FromHtml("#3D3D3D");
        public override Color SeparatorLight => ColorTranslator.FromHtml("#3D3D3D");
    }
}
