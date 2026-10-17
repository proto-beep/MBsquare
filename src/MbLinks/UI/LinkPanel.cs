// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// The dockable panel: one row of icons for the playing album and/or one for the album selected in the library.
    /// Icons run from left to right. Without links a row stays empty.
    /// </summary>
    internal sealed class LinkPanel : TableLayoutPanel
    {
        private readonly LinkRow playingRow;
        private readonly LinkRow selectedRow;
        private readonly int rowHeight;
        private readonly int rows;

        public LinkPanel(float dpiScale, int iconSize, PanelContent content, IconBitmaps bitmaps, Action<string> openUrl)
        {
            int size = Math.Max(12, (int)Math.Round(iconSize * dpiScale));
            int gap = Math.Max(2, (int)Math.Round(3 * dpiScale));
            rowHeight = size + 2 * gap;

            bool both = content == PanelContent.PlayingAndSelected;
            if (content != PanelContent.Selected)
                playingRow = new LinkRow(size, gap, bitmaps, openUrl, both ? "▶" : null, "Playing album");
            if (content != PanelContent.Playing)
                selectedRow = new LinkRow(size, gap, bitmaps, openUrl, both ? "≡" : null, "Album selected in the library");

            rows = (playingRow != null ? 1 : 0) + (selectedRow != null ? 1 : 0);
            ColumnCount = 1;
            RowCount = rows;
            Padding = new Padding(gap, 0, gap, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows; i++) RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));

            int row = 0;
            if (playingRow != null) Controls.Add(playingRow, 0, row++);
            if (selectedRow != null) Controls.Add(selectedRow, 0, row);
        }

        public int PreferredPanelHeight => rows * rowHeight;

        public bool ShowsSelected => selectedRow != null;

        public void SetPlaying(IList<SiteLink> links) => playingRow?.SetLinks(links);

        public void SetSelected(IList<SiteLink> links) => selectedRow?.SetLinks(links);
    }

    /// <summary>One row of icons, left to right.</summary>
    internal sealed class LinkRow : FlowLayoutPanel
    {
        private readonly int size;
        private readonly int gap;
        private readonly IconBitmaps bitmaps;
        private readonly Action<string> openUrl;
        private readonly string glyph;
        private readonly string glyphTooltip;
        private readonly ToolTip toolTip = new ToolTip();

        public LinkRow(int size, int gap, IconBitmaps bitmaps, Action<string> openUrl, string glyph, string glyphTooltip)
        {
            this.size = size;
            this.gap = gap;
            this.bitmaps = bitmaps;
            this.openUrl = openUrl;
            this.glyph = glyph;
            this.glyphTooltip = glyphTooltip;

            Dock = DockStyle.Fill;
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = false;
            AutoScroll = false;
            Margin = new Padding(0);
            Padding = new Padding(0);
            DoubleBuffered = true;
        }

        public void SetLinks(IList<SiteLink> links)
        {
            if (IsDisposed) return;
            SuspendLayout();
            try
            {
                toolTip.RemoveAll();
                while (Controls.Count > 0) Controls[0].Dispose();   // the bitmaps belong to IconBitmaps, not to the picture boxes
                if (links == null || links.Count == 0) return;

                if (glyph != null)
                {
                    var label = new Label
                    {
                        Text = glyph,
                        AutoSize = false,
                        Size = new Size(Math.Max(10, size * 2 / 3), size),
                        TextAlign = ContentAlignment.MiddleCenter,
                        ForeColor = Color.Gray,
                        Margin = new Padding(0, gap, gap, 0)
                    };
                    toolTip.SetToolTip(label, glyphTooltip);
                    Controls.Add(label);
                }

                foreach (SiteLink link in links)
                {
                    var box = new PictureBox
                    {
                        Size = new Size(size, size),
                        SizeMode = PictureBoxSizeMode.Normal,
                        Image = bitmaps.Get(link, size),
                        Cursor = Cursors.Hand,
                        Margin = new Padding(Math.Max(1, gap / 2), gap, Math.Max(1, gap / 2), 0),
                        TabStop = false
                    };
                    string url = link.Url;
                    box.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) openUrl(url); };
                    toolTip.SetToolTip(box, link.Tooltip);
                    Controls.Add(box);
                }
            }
            finally
            {
                ResumeLayout();
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) toolTip.Dispose();
        }
    }
}
