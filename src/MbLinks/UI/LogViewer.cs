// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>Shows the log files (one per month) inside MusicBee, with a filter for exports, errors and events.</summary>
    internal sealed class LogViewer : Form
    {
        private const int MaxLines = 20000;

        private readonly ComboBox fileBox;
        private readonly ComboBox filterBox;
        private readonly TextBox textBox;
        private readonly Label infoLabel;

        public static void ShowLog(IWin32Window owner)
        {
            using (var viewer = new LogViewer())
            {
                if (owner != null) viewer.ShowDialog(owner);
                else viewer.ShowDialog();
            }
        }

        private LogViewer()
        {
            Text = ProjectInfo.Name + " – log";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Size = new Size(900, 600);
            MinimumSize = new Size(500, 300);

            fileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            filterBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            filterBox.Items.AddRange(new object[]
            {
                "All entries",
                "Submissions to MusicBrainz (EXPORT)",
                "Errors (ERROR, EXPORT FAILED)",
                "Genre changes (GENRES)",
                "Events (INFO)"
            });
            filterBox.SelectedIndex = 0;

            var refreshButton = new Button { Text = "Refresh", AutoSize = true };
            var editorButton = new Button { Text = "Open in editor", AutoSize = true };
            var folderButton = new Button { Text = "Open folder", AutoSize = true };
            refreshButton.Click += (s, e) => LoadFiles();
            editorButton.Click += (s, e) => OpenWithShell(SelectedFile());
            folderButton.Click += (s, e) => OpenWithShell(PluginLog.Folder);

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
            top.Controls.Add(Caption("Month:"));
            top.Controls.Add(fileBox);
            top.Controls.Add(Caption("Show:"));
            top.Controls.Add(filterBox);
            top.Controls.Add(refreshButton);
            top.Controls.Add(editorButton);
            top.Controls.Add(folderButton);

            textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window
            };

            infoLabel = new Label { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 3, 6, 6) };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(top, 0, 0);
            root.Controls.Add(textBox, 0, 1);
            root.Controls.Add(infoLabel, 0, 2);
            Controls.Add(root);

            fileBox.SelectedIndexChanged += (s, e) => ShowSelected();
            filterBox.SelectedIndexChanged += (s, e) => ShowSelected();
            LoadFiles();
        }

        private void LoadFiles()
        {
            string previous = SelectedFile();
            fileBox.Items.Clear();

            string folder = PluginLog.Folder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (string file in Directory.GetFiles(folder, "mblinks-*.log").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                    fileBox.Items.Add(new LogFile(file));
            }

            if (fileBox.Items.Count == 0)
            {
                textBox.Text = "";
                infoLabel.Text = "No log entries yet.";
                return;
            }

            int index = 0;
            for (int i = 0; i < fileBox.Items.Count; i++)
                if (string.Equals(((LogFile)fileBox.Items[i]).Path, previous, StringComparison.OrdinalIgnoreCase)) index = i;
            fileBox.SelectedIndex = index;
            ShowSelected();
        }

        private void ShowSelected()
        {
            string file = SelectedFile();
            if (file == null) return;

            List<string> lines;
            try
            {
                lines = ReadShared(file);
            }
            catch (Exception ex)
            {
                textBox.Text = "";
                infoLabel.Text = "Could not read the log file: " + ex.Message;
                return;
            }

            List<string> shown = lines.Where(Matches).ToList();
            bool truncated = shown.Count > MaxLines;
            if (truncated) shown = shown.Skip(shown.Count - MaxLines).ToList();

            textBox.Text = string.Join(Environment.NewLine, shown);
            textBox.SelectionStart = textBox.TextLength;
            textBox.ScrollToCaret();

            int exports = lines.Count(l => l.Contains("[EXPORT]"));
            int failed = lines.Count(l => l.Contains("[EXPORT FAILED]"));
            int errors = lines.Count(l => l.Contains("[ERROR]"));
            int genres = lines.Count(l => l.Contains("[GENRES]"));
            infoLabel.Text = Fmt.Number(lines.Count) + " entries in this month: " +
                             Fmt.Number(exports) + " ratings submitted, " +
                             Fmt.Number(failed) + " submissions failed, " +
                             Fmt.Number(genres) + " genre changes, " +
                             Fmt.Number(errors) + " errors." +
                             (truncated ? " Showing the last " + Fmt.Number(MaxLines) + " matching entries." : "") +
                             "  " + file;
        }

        private bool Matches(string line)
        {
            switch (filterBox.SelectedIndex)
            {
                case 1: return line.Contains("[EXPORT]");
                case 2: return line.Contains("[ERROR]") || line.Contains("[EXPORT FAILED]");
                case 3: return line.Contains("[GENRES]");
                case 4: return line.Contains("[INFO]");
                default: return true;
            }
        }

        private string SelectedFile() => (fileBox.SelectedItem as LogFile)?.Path;

        /// <summary>Reads the file even while the plugin is appending to it.</summary>
        private static List<string> ReadShared(string file)
        {
            var lines = new List<string>();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (line.Length > 0) lines.Add(line);
            }
            return lines;
        }

        private static void OpenWithShell(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not open " + path + ": " + ex.Message);
            }
        }

        private static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(6, 7, 3, 3) };

        private sealed class LogFile
        {
            public LogFile(string path)
            {
                Path = path;
            }

            public string Path { get; }

            // "mblinks-2026-10.log" → "2026-10"
            public override string ToString() =>
                System.IO.Path.GetFileNameWithoutExtension(Path).Replace("mblinks-", "");
        }
    }
}
