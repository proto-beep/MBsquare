// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>Settings window with the tabs Links, Display, Ratings and About.</summary>
    internal sealed class SettingsForm : Form
    {
        private const int ContentWidth = 470;

        private readonly PluginSettings settings;
        private readonly MusicBrainzAuth auth;
        private readonly PluginActions actions;
        private readonly Action<string> openUrl;
        private readonly string detectedLanguage;
        private readonly System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer { Interval = 2000 };

        // Links
        private CheckBox linksEnabled;
        private CheckedListBox siteList;
        private TextBox languageBox;
        private CheckBox prefetchUpcoming;
        private Button analyzeButton;
        private Button refreshButton;
        private Button stopButton;
        private Label statsHeader;
        private TableLayoutPanel statsTable;
        private LinkStatistics lastStats;
        private string statsSignature;
        private bool wasRunning;
        private bool statsBusy;

        // Display
        private ComboBox panelContentBox;
        private ComboBox iconSizeBox;
        private CheckBox contextMenuLinks;
        private FlowLayoutPanel iconGallery;
        private readonly List<Image> galleryImages = new List<Image>();

        // Genres
        private CheckBox genresEnabled;
        private ComboBox genreSourceBox;
        private ComboBox genreFieldBox;
        private RadioButton genreReplace;
        private RadioButton genreAdd;
        private CheckBox genreCapitalize;
        private CheckBox genreSubmitEnabled;
        private CheckBox genreSubmitRecording;
        private CheckBox genreSubmitReleaseGroup;
        private CheckBox genreSubmitRelease;
        private ComboBox genreSubmitAlbumBox;
        private ComboBox genreSubmitFieldBox;
        private CheckBox genreSubmitOnlyKnown;
        private ComboBox genreListRefreshBox;
        private Label genreListInfo;
        private Button genreListButton;

        // Log
        private ComboBox logPathsBox;

        // Ratings
        private CheckBox ratingsEnabled;
        private CheckBox ratingsTracks;
        private CheckBox ratingsAlbums;
        private CheckBox autoUpload;
        private CheckBox futureOnly;
        private CheckBox importOverwrite;
        private RadioButton roundUp;
        private RadioButton roundDown;
        private Label loginStatus;
        private Button loginButton;
        private Button logoutButton;
        private TextBox clientIdBox;
        private TextBox clientSecretBox;

        public SettingsForm(PluginSettings current, MusicBrainzAuth auth, PluginActions actions, string detectedLanguage, Action<string> openUrl)
        {
            settings = current.Clone();
            this.auth = auth;
            this.actions = actions;
            this.openUrl = openUrl;
            this.detectedLanguage = detectedLanguage;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = ProjectInfo.Name + " " + ProjectInfo.Version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(540, 660);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildLinksPage());
            tabs.TabPages.Add(BuildDisplayPage());
            tabs.TabPages.Add(BuildGenresPage());
            tabs.TabPages.Add(BuildRatingsPage());
            tabs.TabPages.Add(BuildAboutPage());

            var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            okButton.Click += (s, e) => ApplyToSettings();
            AcceptButton = okButton;
            CancelButton = cancelButton;

            var dialogButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
            dialogButtons.Controls.Add(cancelButton);
            dialogButtons.Controls.Add(okButton);

            var logButton = new Button { Text = "Show log …", AutoSize = true, Anchor = AnchorStyles.Left };
            logButton.Click += (s, e) => actions.ShowLog(this);

            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(6) };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons.Controls.Add(logButton, 0, 0);
            buttons.Controls.Add(dialogButtons, 1, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            auth.StateChanged += OnAuthStateChanged;
            UpdateLoginState();
            UpdateJobButtons(actions.IsLinkJobRunning());
            UpdateEnabledStates();

            // statistics: computed in the background; live while a job is running and once more when it ends
            statsTimer.Tick += (s, e) =>
            {
                bool running = actions.IsLinkJobRunning();
                UpdateJobButtons(running);
                if (running || wasRunning || lastStats == null) RefreshStatistics();
                wasRunning = running;
            };
            statsTimer.Start();

            ResumeLayout(false);
            PerformLayout();
            RefreshStatistics();
        }

        /// <summary>The changed settings (after OK).</summary>
        public PluginSettings Result => settings;

        // ---------- tabs ----------

        private TabPage BuildLinksPage()
        {
            var page = new TabPage("Links");
            FlowLayoutPanel flow = NewPageFlow();

            linksEnabled = NewCheckBox("Show links", settings.LinksEnabled);
            linksEnabled.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(linksEnabled);

            flow.Controls.Add(NewLabel("Services (MusicBrainz first, then alphabetical):", 10));
            siteList = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Width = ContentWidth, Height = 210 };
            foreach (SiteDefinition site in Sites.All)
                siteList.Items.Add(site, settings.IsSiteEnabled(site.Key));
            // ItemCheck fires before the change, so the statistics are redrawn afterwards
            siteList.ItemCheck += (s, e) => BeginInvoke(new Action(ShowStatistics));
            flow.Controls.Add(siteList);

            flow.Controls.Add(NewLabel(
                "An icon only appears if the link exists. Links to other services are taken from MusicBrainz; " +
                "missing ones can be added to the album there.", 3));

            languageBox = new TextBox { Width = 60, Text = settings.WikipediaLanguage };
            languageBox.Leave += (s, e) => RefreshStatistics();
            flow.Controls.Add(Row(FieldLabel("Wikipedia language:", 130), languageBox));
            flow.Controls.Add(NewLabel(
                "Leave empty for automatic (detected: " + detectedLanguage + "). Otherwise enter a language code " +
                "such as de, en or fr. If there is no article in that language, Wikidata is linked instead.", 3));

            flow.Controls.Add(NewLabel(
                "Links are fetched when an album is played, when it comes up in the playing list, or when you analyze " +
                "tracks here. They are stored permanently until you check them again.", 12));
            prefetchUpcoming = NewCheckBox("Fetch links in advance for the next 50 tracks in the playing list", settings.PrefetchUpcoming);
            flow.Controls.Add(prefetchUpcoming);

            analyzeButton = new Button { Text = "Analyze tracks …", AutoSize = true };
            analyzeButton.Click += OnAnalyzeClick;
            refreshButton = new Button { Text = "Check all stored links again …", AutoSize = true };
            refreshButton.Click += OnRefreshClick;
            stopButton = new Button { Text = "Stop the running job", AutoSize = true };
            stopButton.Click += OnStopClick;
            flow.Controls.Add(Row(analyzeButton, refreshButton, stopButton));

            statsHeader = NewLabel("", 8);
            flow.Controls.Add(statsHeader);
            statsTable = new TableLayoutPanel
            {
                ColumnCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(3, 0, 3, 3)
            };
            for (int i = 0; i < 4; i++) statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            flow.Controls.Add(statsTable);

            page.Controls.Add(flow);
            return page;
        }

        private TabPage BuildDisplayPage()
        {
            var page = new TabPage("Display");
            FlowLayoutPanel flow = NewPageFlow();

            flow.Controls.Add(NewLabel("Panel", 3, bold: true));
            panelContentBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            panelContentBox.Items.AddRange(new object[] { "Playing album", "Album selected in the library", "Playing and selected album" });
            panelContentBox.SelectedIndex = (int)settings.PanelContent;
            flow.Controls.Add(Row(FieldLabel("Shows links for:", 130), panelContentBox));

            iconSizeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
            foreach (int size in PluginSettings.IconSizes) iconSizeBox.Items.Add(size + " px");
            iconSizeBox.SelectedIndex = Math.Max(0, Array.IndexOf(PluginSettings.IconSizes, settings.IconSize));
            flow.Controls.Add(Row(FieldLabel("Icon size:", 130), iconSizeBox));

            flow.Controls.Add(NewLabel(
                "The icons run from left to right. With \"Playing and selected album\" the panel has two rows: " +
                "▶ for the playing album and ≡ for the album selected in the library (only stored links, nothing is fetched). " +
                "The second row stays empty while the selected album is the playing one.", 6));
            flow.Controls.Add(NewLabel(
                "Place the panel via MusicBee's View → Arrange Panels, for example directly below the Now Playing panel. " +
                "Changes to the panel take effect after restarting MusicBee. MusicBee does not allow plugins to place " +
                "icons inside its own views (such as the playing track display or the album view); the panel and the " +
                "context menu are the places available to plugins.", 6));

            flow.Controls.Add(NewLabel("Context menu", 14, bold: true));
            contextMenuLinks = NewCheckBox("Show the links of the selected album when right-clicking tracks or albums (→ MusicBrainz)", settings.ContextMenuLinks);
            flow.Controls.Add(contextMenuLinks);

            flow.Controls.Add(NewLabel("Icons", 14, bold: true));
            flow.Controls.Add(NewLabel(
                "MusicBrainz release group and release are marked with a blue \"RG\" and an orange \"R\" badge. " +
                "The other icons are the favicons of the websites; they are downloaded once and stored.", 3));
            flow.Controls.Add(NewLabel("Stored favicons:", 8));
            iconGallery = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MaximumSize = new Size(ContentWidth, 0),
                Margin = new Padding(3, 3, 3, 3)
            };
            flow.Controls.Add(iconGallery);
            FillIconGallery();

            var reloadButton = new Button { Text = "Reload icons", AutoSize = true };
            reloadButton.Click += (s, e) =>
            {
                DialogResult answer = MessageBox.Show(this,
                    "Delete all stored favicons? They are downloaded again from the websites the next time they are shown.",
                    ProjectInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
                actions.ReloadIcons();
                FillIconGallery();
            };
            var refreshGalleryButton = new Button { Text = "Refresh list", AutoSize = true };
            refreshGalleryButton.Click += (s, e) => FillIconGallery();
            var iconFolderButton = new Button { Text = "Open icon folder", AutoSize = true };
            iconFolderButton.Click += (s, e) => actions.OpenIconFolder();
            flow.Controls.Add(Row(reloadButton, refreshGalleryButton, iconFolderButton));

            page.Controls.Add(flow);
            return page;
        }

        private TabPage BuildGenresPage()
        {
            var page = new TabPage("Genres");
            FlowLayoutPanel flow = NewPageFlow();

            genresEnabled = NewCheckBox("Offer \"Get MB genres if available\" in the context menu", settings.GenresEnabled);
            genresEnabled.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(genresEnabled);
            flow.Controls.Add(NewLabel(
                "Genres are only fetched when you ask for them: select tracks or albums, right-click → MusicBrainz → " +
                "\"Get MB genres if available\". Before anything is written, a confirmation shows the number of tracks " +
                "and requests and the estimated time.", 3));

            genreSourceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            genreSourceBox.Items.AddRange(new object[]
            {
                "Recording (each track's own genres)",
                "Release group (the album's genres for all its tracks)",
                "Recording, otherwise release group"
            });
            genreSourceBox.SelectedIndex = (int)settings.GenreSource;
            flow.Controls.Add(Row(FieldLabel("Source:", 110), genreSourceBox));

            genreFieldBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            foreach (FieldChoice choice in FieldChoices()) genreFieldBox.Items.Add(choice);
            genreFieldBox.SelectedIndex = 0;
            for (int i = 0; i < genreFieldBox.Items.Count; i++)
                if (((FieldChoice)genreFieldBox.Items[i]).Key == settings.GenreTargetField) genreFieldBox.SelectedIndex = i;
            flow.Controls.Add(Row(FieldLabel("Write to:", 110), genreFieldBox));
            flow.Controls.Add(NewLabel(
                "Default: MusicBee's genre field. For an own field \"MB Genres\", give one of MusicBee's custom tags " +
                "the name \"MB Genres\" in MusicBee's preferences (Tags), then choose it here; its name then appears in this list.", 3));

            flow.Controls.Add(NewLabel("Existing values in the field:", 10));
            genreReplace = new RadioButton { Text = "replace", AutoSize = true, Checked = settings.GenreReplace };
            genreAdd = new RadioButton { Text = "keep and add missing genres", AutoSize = true, Checked = !settings.GenreReplace };
            flow.Controls.Add(Row(genreReplace, genreAdd));

            genreCapitalize = NewCheckBox("Capitalize genre names (alternative rock → Alternative Rock)", settings.GenreCapitalize, 10);
            flow.Controls.Add(genreCapitalize);

            flow.Controls.Add(NewLabel(
                "Several genres are separated by \"; \" and sorted by the number of votes on MusicBrainz. " +
                "If MusicBrainz has no genres for a track, its field is left unchanged. Every change is written " +
                "to the log file with the old and the new value, so it can be undone by hand if needed.", 10));

            // submitting the user's own genres
            flow.Controls.Add(NewLabel("Submit my genres to MusicBrainz", 18, bold: true));
            genreSubmitEnabled = NewCheckBox("Offer \"Submit my genres to MB\" in the context menu", settings.GenreSubmitEnabled);
            genreSubmitEnabled.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(genreSubmitEnabled);
            flow.Controls.Add(NewLabel(
                "Off by default. Sends the genres from your tags to MusicBrainz as your own votes, only when you ask for it: " +
                "select tracks or albums, right-click → MusicBrainz → \"Submit my genres to MB\". A confirmation shows " +
                "the numbers first. Requires the MusicBrainz login (tab \"Ratings / Login\").", 3));

            flow.Controls.Add(NewLabel("Levels on MusicBrainz:", 8));
            genreSubmitRecording = NewCheckBox("Recording (each track's genres)", settings.GenreSubmitRecording);
            genreSubmitReleaseGroup = NewCheckBox("Release group (the album)", settings.GenreSubmitReleaseGroup);
            genreSubmitRelease = NewCheckBox("Release (the specific edition)", settings.GenreSubmitRelease);
            genreSubmitReleaseGroup.CheckedChanged += (s, e) => UpdateEnabledStates();
            genreSubmitRelease.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(Row(genreSubmitRecording, genreSubmitReleaseGroup, genreSubmitRelease));

            genreSubmitAlbumBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            genreSubmitAlbumBox.Items.AddRange(new object[]
            {
                "Genres shared by all selected tracks of the album",
                "Genres of any selected track of the album"
            });
            genreSubmitAlbumBox.SelectedIndex = settings.GenreSubmitAlbumShared ? 0 : 1;
            flow.Controls.Add(Row(FieldLabel("Album levels get:", 110), genreSubmitAlbumBox));

            genreSubmitFieldBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            foreach (FieldChoice choice in FieldChoices()) genreSubmitFieldBox.Items.Add(choice);
            genreSubmitFieldBox.SelectedIndex = 0;
            for (int i = 0; i < genreSubmitFieldBox.Items.Count; i++)
                if (((FieldChoice)genreSubmitFieldBox.Items[i]).Key == settings.GenreSubmitField) genreSubmitFieldBox.SelectedIndex = i;
            flow.Controls.Add(Row(FieldLabel("Read from:", 110), genreSubmitFieldBox));

            genreSubmitOnlyKnown = NewCheckBox("Only submit names that MusicBrainz knows as genres", settings.GenreSubmitOnlyKnown, 6);
            genreSubmitOnlyKnown.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(genreSubmitOnlyKnown);

            genreListRefreshBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            genreListRefreshBox.Items.AddRange(new object[]
            {
                "Every session",
                "Once a day",
                "Once a week",
                "Once a month",
                "Once a year",
                "Never (keep the stored list)"
            });
            genreListRefreshBox.SelectedIndex = (int)settings.GenreListRefresh;
            flow.Controls.Add(Row(FieldLabel("Update the genre list:", 150), genreListRefreshBox));

            genreListButton = new Button { Text = "Update now", AutoSize = true };
            genreListButton.Click += OnGenreListUpdateClick;
            genreListInfo = NewLabel(SafeGenreListInfo(), 7);
            flow.Controls.Add(Row(genreListButton, genreListInfo));

            var genresLink = new LinkLabel
            {
                Text = "Show all MusicBrainz genres (" + GenreFields.MusicBrainzGenresUrl.Replace("https://", "") + ")",
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3)
            };
            genresLink.LinkClicked += (s, e) => openUrl(GenreFields.MusicBrainzGenresUrl);
            flow.Controls.Add(genresLink);
            flow.Controls.Add(NewLabel(
                "Recommended: other names would become plain tags on MusicBrainz instead of genres. The confirmation lists " +
                "the names that are skipped. Genres are only added as your votes; nothing is removed on MusicBrainz. " +
                "Every submitted genre is written to the log file.", 3));

            page.Controls.Add(flow);
            return page;
        }

        private string SafeGenreListInfo()
        {
            try { return actions.GenreListInfo(); }
            catch { return ""; }
        }

        private async void OnGenreListUpdateClick(object sender, EventArgs e)
        {
            genreListButton.Enabled = false;
            genreListInfo.Text = "Loading the genre list from MusicBrainz …";
            string text;
            try
            {
                text = await actions.RefreshGenreList();
            }
            catch (Exception ex)
            {
                text = "The genre list could not be loaded: " + ex.Message;
                PluginLog.Error("Updating the genre list: " + ex.Message);
            }
            if (IsDisposed) return;
            genreListInfo.Text = text;
            UpdateEnabledStates();
        }

        /// <summary>MusicBee's genre field and the custom tags, with the names given to them in MusicBee.</summary>
        private IEnumerable<FieldChoice> FieldChoices()
        {
            yield return new FieldChoice(GenreFields.GenreKey, "Genre (MusicBee's genre field)");
            foreach (string key in GenreFields.CustomKeys())
            {
                string name = null;
                try { name = actions.FieldName(key); } catch { }
                string number = key.Substring("Custom".Length);
                bool named = !string.IsNullOrEmpty(name) && !string.Equals(name.Replace(" ", ""), key, StringComparison.OrdinalIgnoreCase);
                yield return new FieldChoice(key, "Custom tag " + number + (named ? " – " + name : ""));
            }
        }

        private TabPage BuildRatingsPage()
        {
            var page = new TabPage("Ratings / Login");
            FlowLayoutPanel flow = NewPageFlow();

            ratingsEnabled = NewCheckBox("Sync ratings with MusicBrainz", settings.RatingsEnabled);
            ratingsEnabled.CheckedChanged += (s, e) => UpdateEnabledStates();
            flow.Controls.Add(ratingsEnabled);
            flow.Controls.Add(NewLabel(
                "Off by default. As long as this is off, the plugin neither reads nor changes any ratings " +
                "and the rating entries in the context menu are hidden.", 3));

            // login
            loginStatus = NewLabel("", 3);
            loginButton = new Button { Text = "Log in …", AutoSize = true };
            logoutButton = new Button { Text = "Log out", AutoSize = true };
            loginButton.Click += OnLoginClick;
            logoutButton.Click += OnLogoutClick;
            clientIdBox = new TextBox { Width = 330, Text = auth.StoredClientId ?? MusicBrainzAuth.DefaultClientId };
            clientSecretBox = new TextBox { Width = 330, UseSystemPasswordChar = true, Text = auth.StoredClientSecret ?? MusicBrainzAuth.DefaultClientSecret };

            var loginFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
            loginFlow.Controls.Add(loginStatus);
            loginFlow.Controls.Add(Row(loginButton, logoutButton));
            loginFlow.Controls.Add(NewLabel("OAuth application credentials (see \"Setting up ratings\" in the README):", 8));
            loginFlow.Controls.Add(Row(FieldLabel("Client ID:", 80), clientIdBox));
            loginFlow.Controls.Add(Row(FieldLabel("Secret:", 80), clientSecretBox));

            var loginGroup = new GroupBox
            {
                Text = "MusicBrainz login",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(ContentWidth, 0),
                Padding = new Padding(8),
                Margin = new Padding(3, 8, 3, 3)
            };
            loginGroup.Controls.Add(loginFlow);
            flow.Controls.Add(loginGroup);

            // options
            ratingsTracks = NewCheckBox("Track ratings ↔ MusicBrainz recordings", settings.RatingsTracks, 10);
            ratingsAlbums = NewCheckBox("Album ratings ↔ MusicBrainz release groups", settings.RatingsAlbums);
            autoUpload = NewCheckBox("Submit changed ratings automatically", settings.AutoUpload);
            autoUpload.CheckedChanged += (s, e) => UpdateEnabledStates();
            futureOnly = NewCheckBox("Only ratings changed from now on (existing ratings are not submitted)", settings.AutoUploadFutureOnly);
            futureOnly.Margin = new Padding(24, 0, 3, 3);
            flow.Controls.Add(ratingsTracks);
            flow.Controls.Add(ratingsAlbums);
            flow.Controls.Add(autoUpload);
            flow.Controls.Add(futureOnly);
            flow.Controls.Add(NewLabel(
                "If you untick \"only ratings changed from now on\", the plugin counts the existing ratings after you click OK " +
                "and asks once whether to submit all of them.", 0, indent: 24));

            flow.Controls.Add(NewLabel("Half stars when submitting (MusicBrainz only supports whole stars):", 10));
            roundUp = new RadioButton { Text = "round up", AutoSize = true, Checked = settings.RoundHalfUp };
            roundDown = new RadioButton { Text = "round down", AutoSize = true, Checked = !settings.RoundHalfUp };
            flow.Controls.Add(Row(roundUp, roundDown));

            importOverwrite = NewCheckBox("Overwrite existing ratings in MusicBee when importing", settings.ImportOverwrite, 10);
            flow.Controls.Add(importOverwrite);

            flow.Controls.Add(NewLabel(
                "To submit or import: select tracks or albums, right-click → MusicBrainz → \"Submit ratings\" or " +
                "\"Import ratings\". The result appears in MusicBee's status bar; every submitted rating is written to the log file.", 10));

            page.Controls.Add(flow);
            return page;
        }

        private TabPage BuildAboutPage()
        {
            var page = new TabPage("About");
            FlowLayoutPanel flow = NewPageFlow();

            flow.Controls.Add(NewLabel(ProjectInfo.Name + ", version " + ProjectInfo.Version, 3, bold: true));
            flow.Controls.Add(NewLabel(ProjectInfo.AiNotice, 10));
            flow.Controls.Add(NewLabel(
                "Data from MusicBrainz and Wikidata. The icons are the favicons of the respective websites.", 10));

            if (ProjectInfo.IsRepositoryConfigured)
            {
                var link = new LinkLabel { Text = ProjectInfo.RepositoryUrl, AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
                link.LinkClicked += (s, e) => openUrl(ProjectInfo.RepositoryUrl);
                flow.Controls.Add(link);
            }

            flow.Controls.Add(NewLabel("Log file", 16, bold: true));
            flow.Controls.Add(NewLabel(
                "Every rating submitted to MusicBrainz (with date, time, MusicBrainz ID, value and trigger), every genre change, " +
                "all errors, and events such as logins, analyses and checks are written to a log file " +
                "(one file per month) in the plugin's data folder.", 3));

            logPathsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            logPathsBox.Items.AddRange(new object[]
            {
                "Folder and file name (default)",
                "Full path",
                "No file information"
            });
            logPathsBox.SelectedIndex = (int)settings.LogPaths;
            flow.Controls.Add(Row(FieldLabel("Files in the log:", 120), logPathsBox));
            flow.Controls.Add(NewLabel(
                "Example: \"Example Album (2004)\\01 First Song.flac\" instead of the full path with drive and all folders. " +
                "With \"No file information\" the log contains no file names at all. The setting applies to new entries; " +
                "existing log files are not changed.", 3));
            var showLogButton = new Button { Text = "Show log …", AutoSize = true };
            showLogButton.Click += (s, e) => actions.ShowLog(this);
            var logFolderButton = new Button { Text = "Open log folder", AutoSize = true };
            logFolderButton.Click += (s, e) => actions.OpenLogFolder();
            flow.Controls.Add(Row(showLogButton, logFolderButton));

            page.Controls.Add(flow);
            return page;
        }

        // ---------- analysis and checking ----------

        private void OnAnalyzeClick(object sender, EventArgs e)
        {
            actions.ShowAnalysis(this);
            UpdateJobButtons(actions.IsLinkJobRunning());
        }

        private void OnRefreshClick(object sender, EventArgs e)
        {
            int albums = actions.StoredAlbums();
            if (albums == 0)
            {
                MessageBox.Show(this,
                    "No links are stored yet. Links are stored when an album is played, when it comes up in the playing list, or when you analyze tracks.",
                    ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TimeSpan duration = TimeSpan.FromSeconds(albums * WebService.SecondsPerMusicBrainzRequest);
            DialogResult answer = MessageBox.Show(this,
                "Check all stored links again?\n\n" +
                "Links are currently stored for " + Fmt.Number(albums) + (albums == 1 ? " album" : " albums") + ". " +
                "For each of them the plugin asks MusicBrainz again (and Wikidata, if a Wikipedia article was stored) " +
                "and replaces the stored links with the current ones. Links that no longer exist in MusicBrainz are removed, " +
                "new ones are added.\n\n" +
                "Requests to MusicBrainz: about " + Fmt.Number(albums) + " (one per album, at most about one per second)\n" +
                "Estimated time: " + Fmt.Duration(duration) + "\n\n" +
                "The check runs in the background and you can keep using MusicBee. Progress and the result are shown in the " +
                "status bar. If MusicBee is closed or the check is stopped, albums that have not been checked yet keep their " +
                "previous links.\n\n" +
                "Do you really want to start the check now?",
                ProjectInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;
            actions.StartRefresh();
            UpdateJobButtons(true);
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            DialogResult answer = MessageBox.Show(this,
                "An analysis or a check of the stored links is running.\n\n" +
                "Do you want to stop it? Everything fetched so far stays stored; all other albums keep their previous state.",
                ProjectInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) actions.StopLinkJob();
        }

        private void UpdateJobButtons(bool running)
        {
            analyzeButton.Enabled = !running;
            refreshButton.Enabled = !running;
            stopButton.Visible = running;
        }

        // ---------- statistics ----------

        private string CurrentLanguage()
        {
            string language = PluginSettings.NormalizeLanguage(languageBox.Text);
            return language.Length > 0 ? language : detectedLanguage;
        }

        private async void RefreshStatistics()
        {
            if (statsBusy || IsDisposed) return;
            statsBusy = true;
            try
            {
                string language = CurrentLanguage();
                LinkStatistics stats = await Task.Run(() => actions.Statistics(language));
                if (IsDisposed) return;
                lastStats = stats;
                ShowStatistics();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Statistics: " + ex.Message);
            }
            finally
            {
                statsBusy = false;
            }
        }

        /// <summary>Total, then one line per service with stored links (two columns).</summary>
        private void ShowStatistics()
        {
            if (IsDisposed) return;
            bool running = actions.IsLinkJobRunning();

            if (lastStats == null)
            {
                statsHeader.Text = "Statistics: loading the stored links …";
                return;
            }

            statsHeader.Text = "Statistics: " + Plural(lastStats.TotalLinks, "link", "links") + " stored for "
                             + Plural(lastStats.Albums, "album", "albums") + (running ? " (job running)" : "") + ".";

            List<ServiceCount> rows = lastStats.Services.Where(c => c.Count > 0).ToList();
            string signature = string.Join("|", rows.Select(c => c.Name + "=" + c.Count + (IsChecked(c.SiteKey) ? "" : "-")));
            if (signature == statsSignature) return;
            statsSignature = signature;

            statsTable.SuspendLayout();
            while (statsTable.Controls.Count > 0) statsTable.Controls[0].Dispose();
            statsTable.RowStyles.Clear();
            statsTable.RowCount = Math.Max(1, (rows.Count + 1) / 2);
            for (int i = 0; i < rows.Count; i++)
            {
                int column = (i % 2) * 2;
                int row = i / 2;
                string name = rows[i].Name + (IsChecked(rows[i].SiteKey) ? "" : " (off)");
                statsTable.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(3, 1, 3, 1) }, column, row);
                statsTable.Controls.Add(new Label
                {
                    Text = Fmt.Number(rows[i].Count),
                    AutoSize = true,
                    Anchor = AnchorStyles.Right,
                    Margin = new Padding(3, 1, 20, 1)
                }, column + 1, row);
            }
            statsTable.ResumeLayout();
        }

        private bool IsChecked(string siteKey)
        {
            for (int i = 0; i < siteList.Items.Count; i++)
                if (((SiteDefinition)siteList.Items[i]).Key == siteKey) return siteList.GetItemChecked(i);
            return false;
        }

        private static string Plural(int n, string one, string many) => Fmt.Number(n) + " " + (n == 1 ? one : many);

        // ---------- login ----------

        private async void OnLoginClick(object sender, EventArgs e)
        {
            string id = clientIdBox.Text.Trim();
            string secret = clientSecretBox.Text.Trim();
            if (id.Length == 0)
            {
                MessageBox.Show(this,
                    "Logging in requires an OAuth application registered with MusicBrainz. " +
                    "See \"Setting up ratings\" in the README.",
                    ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            openUrl(auth.BeginLogin(id));
            string code = CodePrompt.Ask(this);
            if (string.IsNullOrWhiteSpace(code)) return;

            loginButton.Enabled = false;
            loginStatus.Text = "Logging in …";
            string error;
            try
            {
                error = await auth.CompleteLoginAsync(code, id, secret, CancellationToken.None);
            }
            catch (Exception ex)
            {
                error = "Login failed: " + ex.Message;
                PluginLog.Error(error);
            }

            if (IsDisposed) return;
            UpdateLoginState();
            if (error != null) MessageBox.Show(this, error, ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private async void OnLogoutClick(object sender, EventArgs e)
        {
            logoutButton.Enabled = false;
            try { await auth.LogoutAsync(CancellationToken.None); }
            catch (Exception ex) { PluginLog.Error("Logout: " + ex.Message); }
            if (!IsDisposed) UpdateLoginState();
        }

        private void OnAuthStateChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(UpdateLoginState)); }
            catch (InvalidOperationException) { }
        }

        private void UpdateLoginState()
        {
            bool loggedIn = auth.IsLoggedIn;
            string name = auth.UserName;
            loginStatus.Text = loggedIn
                ? "Logged in" + (string.IsNullOrEmpty(name) ? "." : " as " + name + ".")
                : "Not logged in.";
            loginButton.Text = loggedIn ? "Log in again …" : "Log in …";
            loginButton.Enabled = true;
            logoutButton.Enabled = loggedIn;
        }

        // ---------- applying ----------

        private void UpdateEnabledStates()
        {
            siteList.Enabled = linksEnabled.Checked;
            languageBox.Enabled = linksEnabled.Checked;
            prefetchUpcoming.Enabled = linksEnabled.Checked;

            bool genresOn = genresEnabled.Checked;
            foreach (Control c in new Control[] { genreSourceBox, genreFieldBox, genreReplace, genreAdd, genreCapitalize })
                c.Enabled = genresOn;

            bool submitOn = genreSubmitEnabled.Checked;
            foreach (Control c in new Control[] { genreSubmitRecording, genreSubmitReleaseGroup, genreSubmitRelease, genreSubmitFieldBox, genreSubmitOnlyKnown })
                c.Enabled = submitOn;
            genreSubmitAlbumBox.Enabled = submitOn && (genreSubmitReleaseGroup.Checked || genreSubmitRelease.Checked);
            genreListRefreshBox.Enabled = submitOn && genreSubmitOnlyKnown.Checked;
            genreListButton.Enabled = submitOn && genreSubmitOnlyKnown.Checked;

            bool ratingsOn = ratingsEnabled.Checked;
            foreach (Control c in new Control[] { ratingsTracks, ratingsAlbums, autoUpload, roundUp, roundDown, importOverwrite })
                c.Enabled = ratingsOn;
            futureOnly.Enabled = ratingsOn && autoUpload.Checked;
        }

        private void ApplyToSettings()
        {
            settings.LinksEnabled = linksEnabled.Checked;
            settings.WikipediaLanguage = PluginSettings.NormalizeLanguage(languageBox.Text);
            settings.PrefetchUpcoming = prefetchUpcoming.Checked;
            for (int i = 0; i < siteList.Items.Count; i++)
                settings.SetSiteEnabled(((SiteDefinition)siteList.Items[i]).Key, siteList.GetItemChecked(i));

            settings.PanelContent = (PanelContent)Math.Max(0, panelContentBox.SelectedIndex);
            settings.IconSize = PluginSettings.IconSizes[Math.Max(0, iconSizeBox.SelectedIndex)];
            settings.ContextMenuLinks = contextMenuLinks.Checked;

            settings.GenresEnabled = genresEnabled.Checked;
            settings.GenreSource = (GenreSource)Math.Max(0, genreSourceBox.SelectedIndex);
            settings.GenreTargetField = (genreFieldBox.SelectedItem as FieldChoice)?.Key ?? GenreFields.GenreKey;
            settings.GenreReplace = genreReplace.Checked;
            settings.GenreCapitalize = genreCapitalize.Checked;

            settings.GenreSubmitEnabled = genreSubmitEnabled.Checked;
            settings.GenreSubmitRecording = genreSubmitRecording.Checked;
            settings.GenreSubmitReleaseGroup = genreSubmitReleaseGroup.Checked;
            settings.GenreSubmitRelease = genreSubmitRelease.Checked;
            settings.GenreSubmitAlbumShared = genreSubmitAlbumBox.SelectedIndex != 1;
            settings.GenreSubmitField = (genreSubmitFieldBox.SelectedItem as FieldChoice)?.Key ?? GenreFields.GenreKey;
            settings.GenreSubmitOnlyKnown = genreSubmitOnlyKnown.Checked;
            settings.GenreListRefresh = (GenreListRefresh)Math.Max(0, genreListRefreshBox.SelectedIndex);

            settings.LogPaths = (LogPathMode)Math.Max(0, logPathsBox.SelectedIndex);

            settings.RatingsEnabled = ratingsEnabled.Checked;
            settings.RatingsTracks = ratingsTracks.Checked;
            settings.RatingsAlbums = ratingsAlbums.Checked;
            settings.AutoUpload = autoUpload.Checked;
            settings.AutoUploadFutureOnly = futureOnly.Checked;
            settings.RoundHalfUp = roundUp.Checked;
            settings.ImportOverwrite = importOverwrite.Checked;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            statsTimer.Stop();
            statsTimer.Dispose();
            auth.StateChanged -= OnAuthStateChanged;
            ClearIconGallery();
            base.OnFormClosed(e);
        }

        // ---------- favicon overview ----------

        /// <summary>Shows every stored favicon with the website it belongs to.</summary>
        private void FillIconGallery()
        {
            iconGallery.SuspendLayout();
            ClearIconGallery();

            IList<KeyValuePair<string, byte[]>> icons = actions.StoredIcons();
            if (icons.Count == 0)
            {
                iconGallery.Controls.Add(new Label { Text = "No favicons stored yet. They are loaded when links are shown.", AutoSize = true });
            }

            foreach (KeyValuePair<string, byte[]> icon in icons)
            {
                Image image = LoadIcon(icon.Value, 32);
                if (image != null) galleryImages.Add(image);
                iconGallery.Controls.Add(new Label
                {
                    Image = image,
                    ImageAlign = ContentAlignment.TopCenter,
                    Text = icon.Key,
                    TextAlign = ContentAlignment.BottomCenter,
                    AutoEllipsis = true,
                    Size = new Size(110, 58),
                    Margin = new Padding(2)
                });
            }
            iconGallery.ResumeLayout();
        }

        private void ClearIconGallery()
        {
            while (iconGallery.Controls.Count > 0) iconGallery.Controls[0].Dispose();
            foreach (Image image in galleryImages) image.Dispose();
            galleryImages.Clear();
        }

        private static Image LoadIcon(byte[] png, int size)
        {
            try
            {
                using (var ms = new System.IO.MemoryStream(png))
                using (Image source = Image.FromStream(ms))
                {
                    var bitmap = new Bitmap(size, size);
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(source, new Rectangle(0, 0, size, size));
                    }
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------- building blocks ----------

        private sealed class FieldChoice
        {
            public FieldChoice(string key, string text)
            {
                Key = key;
                Text = text;
            }

            public string Key { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        private static FlowLayoutPanel NewPageFlow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            row.Controls.AddRange(controls);
            return row;
        }

        private static Label NewLabel(string text, int topMargin, bool bold = false, int indent = 3)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(ContentWidth - indent, 0),
                Margin = new Padding(indent, topMargin, 3, 3)
            };
            if (bold) label.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            return label;
        }

        private static Label FieldLabel(string text, int width)
        {
            return new Label { Text = text, AutoSize = false, Size = new Size(width, 23), TextAlign = ContentAlignment.MiddleLeft };
        }

        private static CheckBox NewCheckBox(string text, bool isChecked, int topMargin = 3)
        {
            return new CheckBox { Text = text, Checked = isChecked, AutoSize = true, Margin = new Padding(3, topMargin, 3, 3) };
        }
    }
}
