// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>What the settings window can ask the plugin to do.</summary>
    internal sealed class PluginActions
    {
        public Func<int> StoredAlbums { get; set; }
        public Func<string, LinkStatistics> Statistics { get; set; }
        public Func<bool> IsLinkJobRunning { get; set; }
        public Action StartRefresh { get; set; }
        public Action StopLinkJob { get; set; }
        public Action<IWin32Window> ShowAnalysis { get; set; }
        public Action ReloadIcons { get; set; }
        public Func<IList<KeyValuePair<string, byte[]>>> StoredIcons { get; set; }
        public Action OpenIconFolder { get; set; }
        public Action<IWin32Window> ShowLog { get; set; }
        public Func<string, string> FieldName { get; set; }
        public Func<string> GenreListInfo { get; set; }
        public Func<Task<string>> RefreshGenreList { get; set; }
        public Action OpenLogFolder { get; set; }
    }

    /// <summary>Asks which tracks should be analyzed.</summary>
    internal sealed class AnalysisScopeDialog : Form
    {
        private readonly RadioButton libraryOption;
        private readonly RadioButton displayedOption;
        private readonly RadioButton selectedOption;
        private readonly RadioButton playlistOption;
        private readonly ComboBox playlistBox;

        public AnalysisScopeDialog(IList<KeyValuePair<string, string>> playlists, AnalysisScope initial)
        {
            Text = ProjectInfo.Name + " – analyze tracks";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(12) };
            flow.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                Margin = new Padding(3, 3, 3, 10),
                Text = "Which tracks should be analyzed? The plugin fetches the links of their albums from MusicBrainz and stores them. " +
                       "Albums whose links are already stored are skipped. Before anything is fetched, you will see how many albums " +
                       "and requests it involves and how long it will take."
            });

            libraryOption = NewRadio("Entire library (the library currently open in MusicBee)");
            displayedOption = NewRadio("Tracks currently shown in the main panel (current view, filter or search)");
            selectedOption = NewRadio("Selected tracks");
            playlistOption = NewRadio("Playlist:");
            playlistBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 400, Margin = new Padding(24, 0, 3, 3) };
            foreach (KeyValuePair<string, string> playlist in playlists)
                playlistBox.Items.Add(new PlaylistItem(playlist.Key, playlist.Value));

            flow.Controls.Add(libraryOption);
            flow.Controls.Add(displayedOption);
            flow.Controls.Add(selectedOption);
            flow.Controls.Add(playlistOption);
            flow.Controls.Add(playlistBox);

            if (playlistBox.Items.Count > 0)
            {
                playlistBox.SelectedIndex = 0;
            }
            else
            {
                playlistOption.Enabled = false;
                playlistBox.Enabled = false;
            }
            playlistBox.SelectedIndexChanged += (s, e) => playlistOption.Checked = true;

            switch (initial)
            {
                case AnalysisScope.Displayed: displayedOption.Checked = true; break;
                case AnalysisScope.Selected: selectedOption.Checked = true; break;
                case AnalysisScope.Playlist when playlistOption.Enabled: playlistOption.Checked = true; break;
                default: libraryOption.Checked = true; break;
            }

            var ok = new Button { Text = "Continue …", AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += (s, e) => Accept();
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            flow.Controls.Add(buttons);

            Controls.Add(flow);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public AnalysisScope Scope { get; private set; }
        public string PlaylistUrl { get; private set; }
        public string ScopeName { get; private set; }

        private void Accept()
        {
            if (playlistOption.Checked)
            {
                var item = playlistBox.SelectedItem as PlaylistItem;
                if (item == null) return;
                Scope = AnalysisScope.Playlist;
                PlaylistUrl = item.Url;
                ScopeName = AnalysisScopes.Describe(Scope, item.Name);
            }
            else
            {
                Scope = libraryOption.Checked ? AnalysisScope.Library
                      : displayedOption.Checked ? AnalysisScope.Displayed
                      : AnalysisScope.Selected;
                ScopeName = AnalysisScopes.Describe(Scope, null);
            }
            DialogResult = DialogResult.OK;
        }

        private static RadioButton NewRadio(string text) =>
            new RadioButton { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };

        private sealed class PlaylistItem
        {
            public PlaylistItem(string url, string name)
            {
                Url = url;
                Name = name;
            }

            public string Url { get; }
            public string Name { get; }
            public override string ToString() => Name;
        }
    }

    /// <summary>Runs work in the background while showing a progress window with a cancel button.</summary>
    internal sealed class ProgressDialog : Form
    {
        private readonly Label status;
        private readonly Button cancelButton;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool running;

        private ProgressDialog(string title)
        {
            Text = title;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            status = new Label { AutoSize = false, Size = new Size(400, 40), Text = "Please wait …" };
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 400 };
            cancelButton = new Button { Text = "Cancel", AutoSize = true };
            cancelButton.Click += (s, e) => RequestCancel();

            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(12) };
            flow.Controls.Add(status);
            flow.Controls.Add(bar);
            flow.Controls.Add(cancelButton);
            Controls.Add(flow);
        }

        /// <summary>Returns the result, or null if the user cancelled or an error occurred.</summary>
        public static T Run<T>(IWin32Window owner, string title, Func<IProgress<string>, CancellationToken, T> work) where T : class
        {
            using (var dialog = new ProgressDialog(title))
            {
                T result = null;
                dialog.Shown += async (s, e) =>
                {
                    dialog.running = true;
                    var progress = new Progress<string>(message =>
                    {
                        if (!dialog.IsDisposed && !dialog.cts.IsCancellationRequested) dialog.status.Text = message;
                    });

                    try
                    {
                        result = await Task.Run(() => work(progress, dialog.cts.Token));
                        dialog.running = false;
                        dialog.DialogResult = dialog.cts.IsCancellationRequested ? DialogResult.Cancel : DialogResult.OK;
                    }
                    catch (OperationCanceledException)
                    {
                        dialog.running = false;
                        dialog.DialogResult = DialogResult.Cancel;
                    }
                    catch (Exception ex)
                    {
                        dialog.running = false;
                        PluginLog.Error(title + ": " + ex);
                        MessageBox.Show(dialog, "Something went wrong: " + ex.Message, ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        dialog.DialogResult = DialogResult.Cancel;
                    }
                };

                DialogResult answer = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                return answer == DialogResult.OK ? result : null;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // the window closes as soon as the work has stopped
                RequestCancel();
            }
            base.OnFormClosing(e);
        }

        private void RequestCancel()
        {
            cts.Cancel();
            cancelButton.Enabled = false;
            status.Text = "Cancelling …";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cts.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Small dialog for pasting the code that MusicBrainz shows after access has been granted.</summary>
    internal static class CodePrompt
    {
        public static string Ask(IWin32Window owner)
        {
            using (var form = new Form())
            {
                form.Text = "MusicBrainz login";
                form.Font = SystemFonts.MessageBoxFont;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.AutoSize = true;
                form.AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var label = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(400, 0),
                    Text = "MusicBrainz has opened in your browser. After you allow access, MusicBrainz shows a code. Paste it here:"
                };
                var box = new TextBox { Width = 400 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);

                var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(12) };
                flow.Controls.Add(label);
                flow.Controls.Add(box);
                flow.Controls.Add(buttons);

                form.Controls.Add(flow);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
