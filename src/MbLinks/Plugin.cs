// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Entry point for MusicBee.
    /// Links are fetched only when an album is played, when it comes up in the playing list, or when the user
    /// explicitly analyzes tracks. They are shown in the dockable panel and in the context menu.
    /// Ratings (off by default): sync with MusicBrainz via the context menu or automatically on changes.
    /// </summary>
    public partial class Plugin
    {
        private const int UpcomingTracks = 50;
        private const int MenuIconSize = 16;

        private MusicBeeApiInterface mbApi;
        private readonly PluginInfo about = new PluginInfo();

        private string dataFolder;
        private PluginSettings settings;
        private int settingsRevision;
        private string detectedLanguage = "en";

        private WebService web;
        private LinkCache linkCache;
        private IconStore iconStore;
        private LinkService linkService;
        private MusicBrainzAuth auth;
        private RatingService ratings;
        private GenreService genreService;
        private GenreSubmitService genreSubmitService;
        private MusicBeeLibrary library;

        // user interface: only touched on MusicBee's UI thread
        private readonly IconBitmaps bitmaps = new IconBitmaps();
        private LinkPanel linkPanel;
        private System.Windows.Forms.Timer selectionTimer;
        private string lastSelectedFile;
        private ToolStripMenuItem contextMenuRoot;
        private ToolStripItem uploadMenuItem;   // fallback if MusicBee does not allow a submenu
        private ToolStripItem importMenuItem;

        private readonly object sync = new object();
        private CancellationTokenSource linksCts;
        private int linksSerial;
        private int selectedSerial;
        private string displayedKey;
        private string playingAlbumKey;
        private IList<SiteLink> currentLinks = new List<SiteLink>();

        private readonly ConcurrentQueue<string> prefetchQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<string, byte> prefetchSeen =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private int prefetchRunning;

        private CancellationTokenSource shutdownCts = new CancellationTokenSource();
        private CancellationTokenSource linkJobCts;
        private int linkJobRunning;
        private int ratingJobRunning;
        private int genreJobRunning;
        private int genreSubmitRunning;
        private readonly ConcurrentDictionary<string, byte> pendingAutoUpload =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> suppressAutoUpload =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer autoUploadTimer;
        private System.Threading.Timer statusTimer;
        private string lastStatus;

        private PluginSettings Settings => Volatile.Read(ref settings);

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApi = new MusicBeeApiInterface();
            mbApi.Initialise(apiInterfacePtr);

            System.Version version = typeof(Plugin).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = ProjectInfo.Name;
            about.Description = "Links to MusicBrainz and other services, optional rating sync with MusicBrainz (code written by an AI)";
            about.Author = "";
            about.TargetApplication = ProjectInfo.Name;   // panel header; "" = no header
            about.Type = PluginType.General;                // the panel is created through OnDockablePanelCreated
            about.VersionMajor = (short)version.Major;
            about.VersionMinor = (short)version.Minor;
            about.Revision = (short)Math.Max(0, version.Build);
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents;
            about.ConfigurationPanelHeight = 0;             // 0 = own settings window via Configure()

            string storagePath = mbApi.Setting_GetPersistentStoragePath();
            AssemblyLoader.Register(storagePath);

            dataFolder = Path.Combine(storagePath, "MbLinks");
            Directory.CreateDirectory(dataFolder);
            PluginLog.Init(dataFolder, message => { try { mbApi.MB_Trace(message); } catch { } });

            settings = PluginSettings.Load(dataFolder);
            PluginLog.PathMode = settings.LogPaths;
            detectedLanguage = LanguageDetector.Detect(storagePath, CultureInfo.CurrentUICulture);

            shutdownCts = new CancellationTokenSource();
            library = new MusicBeeLibrary(mbApi);
            web = new WebService();
            auth = new MusicBrainzAuth(web, dataFolder);
            linkCache = new LinkCache(dataFolder);
            iconStore = new IconStore(web, Path.Combine(dataFolder, "icons"));
            linkService = new LinkService(web, iconStore, linkCache);
            ratings = new RatingService(web, auth, library);
            genreService = new GenreService(web, library);
            genreSubmitService = new GenreSubmitService(web, auth, library, dataFolder);

            AddMenuItems();
            PluginLog.Info(ProjectInfo.Name + " " + ProjectInfo.Version + " started.");
            return about;
        }

        // ---------- MusicBee interface ----------

        // click on "Configure" in MusicBee's plugin preferences
        public bool Configure(IntPtr panelHandle)
        {
            ShowSettings();
            return true;
        }

        // settings are saved in the plugin's own window
        public void SaveSettings()
        {
        }

        public void Close(PluginCloseReason reason)
        {
            shutdownCts.Cancel();
            lock (sync)
            {
                linksCts?.Cancel();
                autoUploadTimer?.Dispose();
                statusTimer?.Dispose();
            }
            try { selectionTimer?.Dispose(); } catch { }
            linkCache?.Dispose();   // writes pending link changes
            web?.Dispose();
            PluginLog.Info(ProjectInfo.Name + " closed (" + reason + ").");
        }

        public void Uninstall()
        {
            try
            {
                if (!string.IsNullOrEmpty(dataFolder) && Directory.Exists(dataFolder))
                    Directory.Delete(dataFolder, true);
            }
            catch { }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                case NotificationType.TrackChanged:
                    UpdateForNowPlaying();
                    QueueUpcoming();
                    break;

                case NotificationType.NowPlayingListChanged:
                    QueueUpcoming();
                    break;

                case NotificationType.TagsChanged:
                    // only matters if the MusicBrainz IDs changed; stored links are not fetched again
                    if (IsNowPlaying(sourceFileUrl)) UpdateForNowPlaying();
                    break;

                case NotificationType.RatingChanged:
                    QueueAutoUpload(sourceFileUrl);
                    break;
            }
        }

        // The presence of this method tells MusicBee that the plugin has a dockable panel.
        public int OnDockablePanelCreated(Control panel)
        {
            float scale;
            using (Graphics g = panel.CreateGraphics())
                scale = g.DpiY / 96f;

            PluginSettings s = Settings;
            var lp = new LinkPanel(scale, s.IconSize, s.PanelContent, bitmaps, OpenUrl) { Dock = DockStyle.Fill };
            lp.HandleCreated += (o, e) => lp.SetPlaying(Volatile.Read(ref currentLinks));
            panel.Controls.Add(lp);
            if (lp.IsHandleCreated) lp.SetPlaying(Volatile.Read(ref currentLinks));

            linkPanel = lp;
            if (lp.ShowsSelected) StartSelectionTracking();
            return lp.PreferredPanelHeight;
        }

        // ---------- settings and menus ----------

        private void AddMenuItems()
        {
            try
            {
                // Tools → MusicBrainz Links → Settings / Analyze tracks / Show log
                var toolsRoot = mbApi.MB_AddMenuItem("mnuTools/" + ProjectInfo.Name, null, (o, e) => { }) as ToolStripMenuItem;
                if (toolsRoot != null)
                {
                    toolsRoot.DropDownItems.Add(new ToolStripMenuItem("Settings …", null, (o, e) => ShowSettings()));
                    toolsRoot.DropDownItems.Add(new ToolStripMenuItem("Analyze tracks …", null,
                        (o, e) => RunAnalysisFlow(MainWindow(), AnalysisScope.Library, true)));
                    toolsRoot.DropDownItems.Add(new ToolStripMenuItem("Show log …", null, (o, e) => LogViewer.ShowLog(MainWindow())));
                }
                else
                {
                    mbApi.MB_AddMenuItem("mnuTools/" + ProjectInfo.Name, null, (o, e) => ShowSettings());
                }

                contextMenuRoot = mbApi.MB_AddMenuItem("context.Main/MusicBrainz", null, (o, e) => { }) as ToolStripMenuItem;
                if (contextMenuRoot != null)
                {
                    // placeholder, so the submenu can open; the entries are built when it opens
                    contextMenuRoot.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
                    contextMenuRoot.DropDownOpening += (o, e) => BuildContextMenu();
                }
                else
                {
                    uploadMenuItem = mbApi.MB_AddMenuItem("context.Main/MusicBrainz: Submit ratings", null,
                        (o, e) => StartRatingJob(false, library.GetSelectedFiles(), false, "manual"));
                    importMenuItem = mbApi.MB_AddMenuItem("context.Main/MusicBrainz: Import ratings", null,
                        (o, e) => StartRatingJob(true, library.GetSelectedFiles(), false, "manual"));
                }
                UpdateMenuVisibility();
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not add menu items: " + ex.Message);
            }
        }

        private void UpdateMenuVisibility()
        {
            PluginSettings s = Settings;
            if (contextMenuRoot != null)
                contextMenuRoot.Visible = (s.LinksEnabled && s.ContextMenuLinks) || s.GenresEnabled || s.GenreSubmitEnabled || s.RatingsEnabled;
            if (uploadMenuItem != null) uploadMenuItem.Visible = s.RatingsEnabled;
            if (importMenuItem != null) importMenuItem.Visible = s.RatingsEnabled;
        }

        /// <summary>
        /// Right-click → MusicBrainz: stored links of the selected album, fetching, genres, ratings.
        /// Everything works only with the MusicBrainz IDs stored in the files; if a single selected track has none,
        /// the actions are disabled. With several selected tracks, those without the relevant ID are skipped.
        /// </summary>
        private void BuildContextMenu()
        {
            ToolStripMenuItem root = contextMenuRoot;
            if (root == null) return;

            var items = new List<ToolStripItem>();
            PluginSettings s = Settings;

            try
            {
                string[] files = library.GetSelectedFiles();
                string first = files.Length > 0 ? files[0] : null;
                MbIds ids = first == null ? new MbIds() : MbidReader.Read(first, null);
                bool single = files.Length == 1;

                if (first == null)
                    items.Add(Disabled("No track selected"));
                else if (single && !ids.HasAlbumIds && ids.RecordingId == null)
                    items.Add(Disabled("No MusicBrainz IDs in the selected track"));

                if (s.LinksEnabled && s.ContextMenuLinks && ids.HasAlbumIds)
                {
                    if (!linkService.IsLoaded)
                    {
                        items.Add(Disabled("Stored links are still loading …"));
                    }
                    else
                    {
                        List<SiteLink> links = linkService.GetStoredLinks(ids, Language(s), s.IsSiteEnabled(Sites.Wikipedia))
                            .Where(l => s.IsSiteEnabled(l.SiteKey))
                            .ToList();
                        DateTime? lastChecked = linkService.GetCheckedUtc(ids.CacheKey);

                        if (links.Count == 0)
                            items.Add(Disabled(lastChecked.HasValue ? "No links for this album" : "Links not fetched yet"));

                        foreach (SiteLink link in links)
                        {
                            string url = link.Url;
                            items.Add(new ToolStripMenuItem(link.Title, new Bitmap(bitmaps.Get(link, MenuIconSize)), (o, e) => OpenUrl(url))
                            {
                                ToolTipText = url
                            });
                        }

                        if (links.Any(l => l.IconData == null))
                        {
                            CancellationToken token = shutdownCts.Token;
                            Task.Run(async () =>
                            {
                                try { await linkService.EnsureIconsAsync(links, token).ConfigureAwait(false); }
                                catch { }
                            });
                        }

                        if (lastChecked.HasValue)
                            items.Add(Disabled("Links checked on " + lastChecked.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                    }
                }

                if (s.LinksEnabled && s.ContextMenuLinks)
                {
                    if (items.Count > 0) items.Add(new ToolStripSeparator());
                    items.Add(MenuAction("Fetch links now (selected albums)", first != null && (!single || ids.HasAlbumIds), FetchSelectionNow));
                }

                if (s.GenresEnabled)
                {
                    bool hasGenreId = s.GenreSource == GenreSource.Recording ? ids.RecordingId != null
                                    : s.GenreSource == GenreSource.ReleaseGroup ? ids.ReleaseGroupId != null
                                    : ids.RecordingId != null || ids.ReleaseGroupId != null;
                    if (items.Count > 0) items.Add(new ToolStripSeparator());
                    items.Add(MenuAction("Get MB genres if available", first != null && (!single || hasGenreId), RunGenreFlow));
                }

                if (s.GenreSubmitEnabled)
                {
                    bool hasSubmitId = (s.GenreSubmitRecording && ids.RecordingId != null) ||
                                       (s.GenreSubmitReleaseGroup && ids.ReleaseGroupId != null) ||
                                       (s.GenreSubmitRelease && ids.ReleaseId != null);
                    if (!s.GenresEnabled && items.Count > 0) items.Add(new ToolStripSeparator());
                    items.Add(MenuAction("Submit my genres to MB", first != null && (!single || hasSubmitId), RunGenreSubmitFlow));
                }

                if (s.RatingsEnabled)
                {
                    bool hasRatingId = (s.RatingsTracks && ids.RecordingId != null) || (s.RatingsAlbums && ids.ReleaseGroupId != null);
                    bool enabled = first != null && (!single || hasRatingId);
                    if (items.Count > 0) items.Add(new ToolStripSeparator());
                    items.Add(MenuAction("Submit ratings", enabled, () => StartRatingJob(false, library.GetSelectedFiles(), false, "manual")));
                    items.Add(MenuAction("Import ratings", enabled, () => StartRatingJob(true, library.GetSelectedFiles(), false, "manual")));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Context menu: " + ex.Message);
            }

            if (items.Count == 0) items.Add(Disabled("Nothing to show, see the plugin settings"));

            List<ToolStripItem> old = root.DropDownItems.Cast<ToolStripItem>().ToList();
            root.DropDownItems.Clear();
            foreach (ToolStripItem oldItem in old) oldItem.Dispose();
            root.DropDownItems.AddRange(items.ToArray());
        }

        private static ToolStripMenuItem MenuAction(string text, bool enabled, Action handler) =>
            new ToolStripMenuItem(text, null, (o, e) => handler()) { Enabled = enabled };

        private static ToolStripMenuItem Disabled(string text) => new ToolStripMenuItem(text) { Enabled = false };

        private void ShowSettings()
        {
            try
            {
                PluginSettings before = Settings;
                IWin32Window owner = MainWindow();
                using (var form = new SettingsForm(before, auth, CreateActions(), detectedLanguage, OpenUrl))
                {
                    DialogResult result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                    if (result == DialogResult.OK) ApplySettings(before, form.Result, owner);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Settings window: " + ex);
            }
        }

        private PluginActions CreateActions()
        {
            return new PluginActions
            {
                StoredAlbums = () => linkService.StoredAlbums,
                Statistics = language => linkService.GetStatistics(language),
                IsLinkJobRunning = () => Volatile.Read(ref linkJobRunning) != 0,
                StartRefresh = StartLinkRefresh,
                StopLinkJob = StopRunningLinkJob,
                ShowAnalysis = owner => RunAnalysisFlow(owner, AnalysisScope.Library, true),
                ReloadIcons = ReloadAllIcons,
                StoredIcons = () => iconStore.ListStored(),
                OpenIconFolder = () => OpenFolder(iconStore.Folder),
                ShowLog = owner => LogViewer.ShowLog(owner),
                FieldName = key => library.GetFieldName(GenreFields.Resolve(key)),
                GenreListInfo = () => genreSubmitService.DescribeStoredList(),
                RefreshGenreList = () => genreSubmitService.RefreshNowAsync(shutdownCts.Token),
                OpenLogFolder = () => OpenFolder(PluginLog.Folder)
            };
        }

        private void ApplySettings(PluginSettings before, PluginSettings after, IWin32Window owner)
        {
            after.Save();
            Volatile.Write(ref settings, after);
            PluginLog.PathMode = after.LogPaths;
            Interlocked.Increment(ref settingsRevision);
            UpdateMenuVisibility();
            lastSelectedFile = null;
            UpdateForNowPlaying();
            QueueUpcoming();
            PluginLog.Info("Settings saved.");

            if (after.PanelContent != before.PanelContent || after.IconSize != before.IconSize)
            {
                MessageBox.Show(owner, "The panel uses the new layout after MusicBee has been restarted.",
                    ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            bool includeExistingBefore = before.RatingsEnabled && before.AutoUpload && !before.AutoUploadFutureOnly;
            bool includeExistingAfter = after.RatingsEnabled && after.AutoUpload && !after.AutoUploadFutureOnly;
            if (includeExistingAfter && !includeExistingBefore) OfferInitialUpload(owner);
        }

        private IWin32Window MainWindow()
        {
            try
            {
                IntPtr handle = mbApi.MB_GetWindowHandle();
                return handle != IntPtr.Zero ? new WindowHandle(handle) : null;
            }
            catch
            {
                return null;
            }
        }

        private string Language(PluginSettings s) =>
            string.IsNullOrEmpty(s.WikipediaLanguage) ? detectedLanguage : s.WikipediaLanguage;

        // ---------- links for the playing album ----------

        private void UpdateForNowPlaying()
        {
            string file = null;
            try { file = mbApi.NowPlaying_GetFileUrl(); } catch { }

            PluginSettings s = Settings;
            string language = Language(s);
            int revision = Volatile.Read(ref settingsRevision);
            int serial = Interlocked.Increment(ref linksSerial);

            var cts = new CancellationTokenSource();
            lock (sync)
            {
                linksCts?.Cancel();
                linksCts = cts;
            }
            CancellationToken token = cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    if (!s.LinksEnabled)
                    {
                        Publish(serial, "off|" + revision, null, new List<SiteLink>());
                        return;
                    }

                    MbIds ids = MbidReader.Read(file, PluginLog.Error);
                    string key = ids.CacheKey + "|" + language + "|" + revision;
                    if (key == Volatile.Read(ref displayedKey)) return;   // same album, already shown

                    IList<SiteLink> visible = new List<SiteLink>();   // no IDs → no icons
                    if (ids.HasAlbumIds)
                    {
                        IList<SiteLink> all = await linkService
                            .GetLinksAsync(ids, language, s.IsSiteEnabled(Sites.Wikipedia), token)
                            .ConfigureAwait(false);
                        visible = all.Where(l => s.IsSiteEnabled(l.SiteKey)).ToList();
                        await linkService.EnsureIconsAsync(visible, token).ConfigureAwait(false);
                    }
                    Publish(serial, key, ids.HasAlbumIds ? ids.CacheKey : null, visible);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PluginLog.Error("Links for the playing track: " + ex); }
            });
        }

        private void Publish(int serial, string key, string albumKey, IList<SiteLink> links)
        {
            if (serial != Volatile.Read(ref linksSerial)) return;   // a newer track is already being handled

            Volatile.Write(ref displayedKey, key);
            Volatile.Write(ref playingAlbumKey, albumKey);
            Volatile.Write(ref currentLinks, links);
            PostToPanel(panel =>
            {
                lastSelectedFile = null;   // the selected row depends on the playing album
                panel.SetPlaying(links);
            });
        }

        private void PostToPanel(Action<LinkPanel> action)
        {
            LinkPanel panel = linkPanel;
            if (panel == null || panel.IsDisposed || !panel.IsHandleCreated) return;
            try
            {
                panel.BeginInvoke(new Action(() =>
                {
                    if (!panel.IsDisposed) action(panel);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private bool IsNowPlaying(string file)
        {
            string now = null;
            try { now = mbApi.NowPlaying_GetFileUrl(); } catch { }
            return !string.IsNullOrEmpty(now) && string.Equals(now, file, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- links for the album selected in the library (stored links only) ----------

        // MusicBee has no notification for selection changes, so the selection is checked once per second.
        private void StartSelectionTracking()
        {
            if (selectionTimer != null) return;
            selectionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            selectionTimer.Tick += (o, e) => CheckSelection();
            selectionTimer.Start();
        }

        private void CheckSelection()
        {
            LinkPanel panel = linkPanel;
            if (panel == null || panel.IsDisposed || !panel.Visible) return;

            string[] files = library.GetSelectedFiles();
            string first = files.Length > 0 ? files[0] : null;
            if (string.Equals(first, lastSelectedFile, StringComparison.OrdinalIgnoreCase)) return;
            lastSelectedFile = first;

            PluginSettings s = Settings;
            string language = Language(s);
            int serial = Interlocked.Increment(ref selectedSerial);
            CancellationToken token = shutdownCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    IList<SiteLink> links = new List<SiteLink>();
                    if (s.LinksEnabled && first != null)
                    {
                        MbIds ids = MbidReader.Read(first, null);
                        bool sameAsPlaying = s.PanelContent == PanelContent.PlayingAndSelected
                                             && ids.CacheKey == Volatile.Read(ref playingAlbumKey);
                        if (ids.HasAlbumIds && !sameAsPlaying)
                        {
                            links = linkService.GetStoredLinks(ids, language, s.IsSiteEnabled(Sites.Wikipedia))
                                .Where(l => s.IsSiteEnabled(l.SiteKey))
                                .ToList();
                            await linkService.EnsureIconsAsync(links, token).ConfigureAwait(false);
                        }
                    }
                    if (serial != Volatile.Read(ref selectedSerial)) return;
                    PostToPanel(p => p.SetSelected(links));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PluginLog.Error("Links for the selected album: " + ex.Message); }
            });
        }

        // ---------- prefetch for the next tracks in the playing list ----------

        private void QueueUpcoming()
        {
            PluginSettings s = Settings;
            if (!s.LinksEnabled || !s.PrefetchUpcoming || library == null) return;

            foreach (string file in library.GetUpcomingFiles(UpcomingTracks))
            {
                if (file.Contains("://")) continue;   // streams
                string album = AnalysisPlanner.GroupKey(file, library.GetAlbum(file));
                if (prefetchSeen.TryAdd(album, 0)) prefetchQueue.Enqueue(file);
            }

            if (!prefetchQueue.IsEmpty && Interlocked.CompareExchange(ref prefetchRunning, 1, 0) == 0)
                Task.Run(() => PrefetchWorkerAsync());
        }

        private async Task PrefetchWorkerAsync()
        {
            try
            {
                while (prefetchQueue.TryDequeue(out string file))
                {
                    if (shutdownCts.IsCancellationRequested) return;

                    PluginSettings s = Settings;
                    if (!s.LinksEnabled || !s.PrefetchUpcoming) continue;

                    MbIds ids = MbidReader.Read(file, PluginLog.Error);
                    if (!ids.HasAlbumIds) continue;

                    try
                    {
                        IList<SiteLink> links = await linkService
                            .GetLinksAsync(ids, Language(s), s.IsSiteEnabled(Sites.Wikipedia), shutdownCts.Token)
                            .ConfigureAwait(false);
                        await linkService.EnsureIconsAsync(links.Where(l => s.IsSiteEnabled(l.SiteKey)), shutdownCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { PluginLog.Error("Fetching links in advance: " + ex.Message); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref prefetchRunning, 0);
                if (!prefetchQueue.IsEmpty && !shutdownCts.IsCancellationRequested &&
                    Interlocked.CompareExchange(ref prefetchRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() => PrefetchWorkerAsync());
                }
            }
        }

        // ---------- analysis and checking of stored links ----------

        /// <summary>Choose tracks → prepare (with progress) → confirmation with numbers → background job.</summary>
        private void RunAnalysisFlow(IWin32Window owner, AnalysisScope scope, bool askScope)
        {
            try
            {
                if (Volatile.Read(ref linkJobRunning) != 0)
                {
                    MessageBox.Show(owner,
                        "An analysis or a check of the stored links is already running. Please wait until it has finished, or stop it in the settings.",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string playlistUrl = null;
                string scopeName = AnalysisScopes.Describe(scope, null);
                if (askScope)
                {
                    using (var dialog = new AnalysisScopeDialog(library.GetPlaylists(), scope))
                    {
                        DialogResult choice = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                        if (choice != DialogResult.OK) return;
                        scope = dialog.Scope;
                        playlistUrl = dialog.PlaylistUrl;
                        scopeName = dialog.ScopeName;
                    }
                }

                string[] files = GetFiles(scope, playlistUrl);
                if (files.Length == 0)
                {
                    MessageBox.Show(owner, "There are no tracks in " + scopeName + ".", ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                PluginSettings s = Settings;
                bool includeWikipedia = s.IsSiteEnabled(Sites.Wikipedia);
                AnalysisPlan plan = ProgressDialog.Run(owner, ProjectInfo.Name + " – preparing the analysis",
                    (progress, ct) => AnalysisPlanner.Prepare(files, library, linkService, includeWikipedia, false, progress, ct));
                if (plan == null) return;   // cancelled
                plan.ScopeName = scopeName;

                if (plan.ToFetch.Count == 0)
                {
                    MessageBox.Show(owner, plan.DescribeNothingToDo(), ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DialogResult answer = MessageBox.Show(owner, plan.Describe(), ProjectInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;

                string language = Language(s);
                PluginLog.Info("Analysis of " + scopeName + " started: " + Fmt.Count(plan.ToFetch.Count, "album", "albums") + " to fetch, " +
                               Fmt.Number(plan.AlreadyStored) + " already stored, " + Fmt.Count(plan.Tracks, "track", "tracks") + ".");
                StartLinkJob("analysis of " + scopeName, async (progress, ct) =>
                    (await linkService.AnalyzeAsync(plan.ToFetch, language, includeWikipedia, false, progress, ct).ConfigureAwait(false)).Describe());
            }
            catch (Exception ex)
            {
                PluginLog.Error("Analysis: " + ex);
            }
        }

        /// <summary>
        /// Right-click → MusicBrainz → Fetch links now: fetches the links of the selected albums immediately,
        /// also if they are already stored (to update them). Up to 10 albums without asking, more only after a confirmation.
        /// </summary>
        private void FetchSelectionNow()
        {
            IWin32Window owner = MainWindow();
            try
            {
                if (Volatile.Read(ref linkJobRunning) != 0)
                {
                    MessageBox.Show(owner,
                        "An analysis or a check of the stored links is already running. Please wait until it has finished, or stop it in the settings.",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string[] files = library.GetSelectedFiles();
                if (files.Length == 0)
                {
                    ShowStatus("no tracks selected.");
                    return;
                }

                PluginSettings s = Settings;
                bool includeWikipedia = s.IsSiteEnabled(Sites.Wikipedia);
                string language = Language(s);

                // small selections are prepared directly, larger ones with a progress window
                AnalysisPlan plan = files.Length <= 300
                    ? AnalysisPlanner.Prepare(files, library, linkService, includeWikipedia, true, null, CancellationToken.None)
                    : ProgressDialog.Run(owner, ProjectInfo.Name + " – preparing",
                        (progress, ct) => AnalysisPlanner.Prepare(files, library, linkService, includeWikipedia, true, progress, ct));
                if (plan == null) return;
                plan.ScopeName = AnalysisScopes.Describe(AnalysisScope.Selected, null);

                if (plan.ToFetch.Count == 0)
                {
                    ShowStatus("the selected tracks contain no MusicBrainz IDs.");
                    return;
                }

                if (plan.ToFetch.Count > 10)
                {
                    DialogResult answer = MessageBox.Show(owner, plan.Describe(), ProjectInfo.Name,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                }

                int count = plan.ToFetch.Count;
                PluginLog.Info("Manual fetch for " + Fmt.Count(count, "album", "albums") + " started from the context menu.");
                StartLinkJob("fetching links for " + Fmt.Count(count, "album", "albums"), async (progress, ct) =>
                    (await linkService.AnalyzeAsync(plan.ToFetch, language, includeWikipedia, true, progress, ct).ConfigureAwait(false)).Describe());
            }
            catch (Exception ex)
            {
                PluginLog.Error("Fetching links for the selection: " + ex);
            }
        }

        private string[] GetFiles(AnalysisScope scope, string playlistUrl)
        {
            switch (scope)
            {
                case AnalysisScope.Displayed: return library.GetDisplayedFiles();
                case AnalysisScope.Selected: return library.GetSelectedFiles();
                case AnalysisScope.Playlist: return library.GetPlaylistFiles(playlistUrl);
                default: return library.GetLibraryFiles();
            }
        }

        private void StartLinkRefresh()
        {
            StartLinkJob("check of stored links", async (progress, ct) =>
                (await linkService.RefreshAllAsync(progress, ct).ConfigureAwait(false)).Describe());
        }

        /// <summary>Runs an analysis or a check of stored links in the background; only one at a time.</summary>
        private bool StartLinkJob(string name, Func<IProgress<string>, CancellationToken, Task<string>> work)
        {
            if (Interlocked.CompareExchange(ref linkJobRunning, 1, 0) != 0)
            {
                ShowStatus("an analysis or check of stored links is already running.");
                return false;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
            lock (sync) linkJobCts = cts;
            var progress = new StatusProgress(message => ShowStatus(message, true));
            PluginLog.Info("Started: " + name + ".");

            Task.Run(async () =>
            {
                try
                {
                    string summary = await work(progress, cts.Token).ConfigureAwait(false);
                    ShowStatus(summary);
                    PluginLog.Info("Finished: " + name + ": " + summary);
                }
                catch (OperationCanceledException)
                {
                    if (!shutdownCts.IsCancellationRequested)
                    {
                        ShowStatus(name + " was stopped. Everything fetched so far stays stored.");
                        PluginLog.Info("Stopped by the user: " + name + ".");
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error(name + ": " + ex);
                    ShowStatus("error during " + name + ", see the log file.");
                }
                finally
                {
                    lock (sync) linkJobCts = null;
                    Interlocked.Exchange(ref linkJobRunning, 0);
                    if (!shutdownCts.IsCancellationRequested)
                    {
                        Volatile.Write(ref displayedKey, null);   // show the new links of the current album
                        UpdateForNowPlaying();
                    }
                }
            });
            return true;
        }

        private void StopRunningLinkJob()
        {
            lock (sync) linkJobCts?.Cancel();
        }

        private void ReloadAllIcons()
        {
            // first remove the icons from the panel, then dispose the bitmaps
            linkPanel?.SetPlaying(null);
            linkPanel?.SetSelected(null);
            bitmaps.Clear();
            iconStore.Clear();
            lastSelectedFile = null;
            Volatile.Write(ref displayedKey, null);
            UpdateForNowPlaying();
            PluginLog.Info("Icons cleared; they are downloaded again.");
        }

        private static void OpenFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return;
                Directory.CreateDirectory(folder);
                using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not open the folder " + folder + ": " + ex.Message);
            }
        }

        // ---------- genres (only on request) ----------

        /// <summary>Right-click → MusicBrainz → Get MB genres if available: prepare, confirm, then write in the background.</summary>
        private void RunGenreFlow()
        {
            IWin32Window owner = MainWindow();
            try
            {
                if (Volatile.Read(ref genreJobRunning) != 0)
                {
                    MessageBox.Show(owner, "A genre import is already running. Please wait until it has finished.",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string[] files = library.GetSelectedFiles();
                if (files.Length == 0)
                {
                    ShowStatus("no tracks selected.");
                    return;
                }

                PluginSettings s = Settings;
                GenrePlan plan = files.Length <= 100
                    ? genreService.Prepare(files, s, null, CancellationToken.None)
                    : ProgressDialog.Run(owner, ProjectInfo.Name + " – preparing the genre import",
                        (progress, ct) => genreService.Prepare(files, s, progress, ct));
                if (plan == null) return;

                if (plan.Items.Count == 0)
                {
                    MessageBox.Show(owner, "The selected tracks contain no MusicBrainz IDs, so no genres can be fetched.",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DialogResult answer = MessageBox.Show(owner, plan.Describe(s, DescribeField(s.GenreTargetField)), ProjectInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;

                StartGenreJob(plan, s);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Genre import: " + ex);
            }
        }

        private string DescribeField(string key)
        {
            if (key == GenreFields.GenreKey) return "Genre";
            string name = library.GetFieldName(GenreFields.Resolve(key));
            return string.IsNullOrEmpty(name) ? key : name;
        }

        private void StartGenreJob(GenrePlan plan, PluginSettings s)
        {
            if (Interlocked.CompareExchange(ref genreJobRunning, 1, 0) != 0) return;

            var progress = new StatusProgress(message => ShowStatus(message, true));
            CancellationToken token = shutdownCts.Token;
            PluginLog.Info("Genre import started: " + Fmt.Count(plan.Items.Count, "track", "tracks") + ", source " + plan.Source +
                           ", field " + DescribeField(s.GenreTargetField) + (s.GenreReplace ? ", replace" : ", add") + ".");

            Task.Run(async () =>
            {
                try
                {
                    GenreResult result = await genreService.ApplyAsync(plan, s, progress, token).ConfigureAwait(false);
                    ShowStatus(result.Describe());
                }
                catch (OperationCanceledException)
                {
                    library.SetStatus("");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Genre import: " + ex);
                    ShowStatus("error during the genre import, see the log file.");
                }
                finally
                {
                    Interlocked.Exchange(ref genreJobRunning, 0);
                }
            });
        }

        // ---------- submitting the user's genres (only on request) ----------

        /// <summary>Right-click → MusicBrainz → Submit my genres to MB: check login, prepare, confirm, submit in the background.</summary>
        private void RunGenreSubmitFlow()
        {
            IWin32Window owner = MainWindow();
            try
            {
                PluginSettings s = Settings;
                if (!s.GenreSubmitEnabled) return;

                if (!s.GenreSubmitRecording && !s.GenreSubmitReleaseGroup && !s.GenreSubmitRelease)
                {
                    MessageBox.Show(owner, "No level is selected for submitting genres (Genres tab in the plugin settings).",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!auth.IsLoggedIn)
                {
                    MessageBox.Show(owner, "Submitting genres requires a login to MusicBrainz (tab \"Ratings / Login\" in the plugin settings).",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!auth.CanSubmitTags)
                {
                    MessageBox.Show(owner,
                        "Your current MusicBrainz login does not include the permission to submit genres (it was made with an older version of the plugin). " +
                        "Please log in again in the plugin settings (tab \"Ratings / Login\").",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (Volatile.Read(ref genreSubmitRunning) != 0)
                {
                    MessageBox.Show(owner, "A genre submission is already running. Please wait until it has finished.",
                        ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string[] files = library.GetSelectedFiles();
                if (files.Length == 0)
                {
                    ShowStatus("no tracks selected.");
                    return;
                }

                string fieldName = DescribeField(s.GenreSubmitField);
                GenreSubmitPlan plan = ProgressDialog.Run(owner, ProjectInfo.Name + " – preparing the genre submission",
                    (progress, ct) => genreSubmitService.Prepare(files, s, fieldName, progress, ct));
                if (plan == null) return;

                if (plan.Entities.Count == 0)
                {
                    MessageBox.Show(owner, plan.DescribeNothingToDo(), ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DialogResult answer = MessageBox.Show(owner, plan.Describe(), ProjectInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;

                StartGenreSubmitJob(plan);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Genre submission: " + ex);
            }
        }

        private void StartGenreSubmitJob(GenreSubmitPlan plan)
        {
            if (Interlocked.CompareExchange(ref genreSubmitRunning, 1, 0) != 0) return;

            var progress = new StatusProgress(message => ShowStatus(message, true));
            CancellationToken token = shutdownCts.Token;
            PluginLog.Info("Genre submission started: " + Fmt.Count(plan.Votes, "genre vote", "genre votes") + " for " +
                           Fmt.Count(plan.Entities.Count, "entity", "entities") + "; levels: " + plan.Levels + ".");

            Task.Run(async () =>
            {
                try
                {
                    GenreSubmitResult result = await genreSubmitService.SubmitAsync(plan, progress, token).ConfigureAwait(false);
                    ShowStatus(result.Describe());
                }
                catch (OperationCanceledException)
                {
                    library.SetStatus("");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Genre submission: " + ex);
                    ShowStatus("error during the genre submission, see the log file.");
                }
                finally
                {
                    Interlocked.Exchange(ref genreSubmitRunning, 0);
                }
            });
        }

        // ---------- ratings ----------

        /// <summary>When automatic submission should include existing ratings: count them, ask, then submit once.</summary>
        private void OfferInitialUpload(IWin32Window owner)
        {
            if (!auth.IsLoggedIn)
            {
                MessageBox.Show(owner,
                    "To submit existing ratings, please log in to MusicBrainz first (tab \"Ratings / Login\"). " +
                    "Until then, automatic submission is set back to future ratings only.",
                    ProjectInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                RevertToFutureOnly();
                return;
            }

            string[] files = library.GetLibraryFiles();
            PluginSettings s = Settings;
            RatingSummary summary = ProgressDialog.Run(owner, ProjectInfo.Name + " – counting ratings",
                (progress, ct) => AnalysisPlanner.CountRatings(files, library, s, progress, ct));

            if (summary == null)
            {
                RevertToFutureOnly();
                return;
            }
            if (summary.Files.Length == 0)
            {
                MessageBox.Show(owner, "The library contains no ratings that could be submitted.", ProjectInfo.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult answer = MessageBox.Show(owner, summary.Describe(), ProjectInfo.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes)
            {
                PluginLog.Info("Submission of all existing ratings confirmed: " + Fmt.Count(summary.RatedTracks, "track", "tracks") + ", " +
                               Fmt.Count(summary.RatedAlbums, "album", "albums") + ".");
                StartRatingJob(false, summary.Files, false, "initial");
            }
            else
            {
                RevertToFutureOnly();
            }
        }

        private void RevertToFutureOnly()
        {
            PluginSettings copy = Settings.Clone();
            copy.AutoUploadFutureOnly = true;
            copy.Save();
            Volatile.Write(ref settings, copy);
        }

        /// <summary>Starts submission or import. Returns false if a sync is already running (for a later retry).</summary>
        private bool StartRatingJob(bool import, string[] files, bool onlyChanged, string trigger)
        {
            PluginSettings s = Settings;
            if (!s.RatingsEnabled) { ShowStatus("rating sync is switched off in the settings."); return true; }
            if (!s.RatingsTracks && !s.RatingsAlbums) { ShowStatus("neither track nor album ratings are selected in the settings."); return true; }
            if (!auth.IsLoggedIn) { ShowStatus("not logged in. Log in via the \"" + ProjectInfo.Name + "\" menu item."); return true; }
            if (files == null || files.Length == 0) { ShowStatus("no tracks selected."); return true; }

            if (Interlocked.CompareExchange(ref ratingJobRunning, 1, 0) != 0)
            {
                if (!onlyChanged) ShowStatus("a rating sync is already running.");
                return false;
            }

            var progress = new StatusProgress(message => ShowStatus(message, true));
            CancellationToken token = shutdownCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    RatingResult result = import
                        ? await ratings.ImportAsync(files, s, MarkImported, progress, token).ConfigureAwait(false)
                        : await ratings.UploadAsync(files, s, onlyChanged, trigger, progress, token).ConfigureAwait(false);
                    ShowStatus(result.Describe(import));
                }
                catch (OperationCanceledException)
                {
                    library.SetStatus("");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Rating sync: " + ex);
                    ShowStatus("error during rating sync, see the log file.");
                }
                finally
                {
                    Interlocked.Exchange(ref ratingJobRunning, 0);
                }
            });
            return true;
        }

        private void MarkImported(string file)
        {
            suppressAutoUpload[file] = DateTime.UtcNow.AddSeconds(30);
        }

        private void QueueAutoUpload(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (suppressAutoUpload.TryGetValue(file, out DateTime until))
            {
                if (DateTime.UtcNow < until) return;   // the change came from our own import
                suppressAutoUpload.TryRemove(file, out _);
            }

            PluginSettings s = Settings;
            if (!s.RatingsEnabled || !s.AutoUpload || auth == null || !auth.IsLoggedIn) return;

            pendingAutoUpload[file] = 0;
            ScheduleAutoUpload(3000);   // collect briefly, in case several ratings are changed in a row
        }

        private void ScheduleAutoUpload(int delayMs)
        {
            lock (sync)
            {
                autoUploadTimer?.Dispose();
                autoUploadTimer = new System.Threading.Timer(_ => FlushAutoUpload(), null, delayMs, Timeout.Infinite);
            }
        }

        private void FlushAutoUpload()
        {
            string[] files = pendingAutoUpload.Keys.ToArray();
            if (files.Length == 0) return;
            foreach (string f in files) pendingAutoUpload.TryRemove(f, out _);

            if (!StartRatingJob(false, files, true, "automatic"))
            {
                foreach (string f in files) pendingAutoUpload[f] = 0;   // something is still running: try again later
                ScheduleAutoUpload(5000);
            }
        }

        // ---------- status bar, opening links ----------

        /// <summary>Message in MusicBee's status bar; without keep it is cleared again after 10 seconds.</summary>
        private void ShowStatus(string message, bool keep = false)
        {
            string text = "MusicBrainz: " + message;
            lastStatus = text;
            library?.SetStatus(text);

            lock (sync)
            {
                statusTimer?.Dispose();
                statusTimer = keep
                    ? null
                    : new System.Threading.Timer(_ => { if (lastStatus == text) library.SetStatus(""); }, null, 10000, Timeout.Infinite);
            }
        }

        private void OpenUrl(string url)
        {
            if (!WebService.IsWebUrl(url)) return;
            try
            {
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not open link: " + ex.Message);
            }
        }
    }

    /// <summary>MusicBee's main window as the owner of dialogs.</summary>
    internal sealed class WindowHandle : IWin32Window
    {
        public WindowHandle(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }

    /// <summary>Loads TagLibSharp.dll from the Plugins folder if .NET does not find it there by itself.</summary>
    internal static class AssemblyLoader
    {
        private static bool registered;

        public static void Register(string musicBeeStoragePath)
        {
            if (registered) return;
            registered = true;

            var folders = new List<string>();
            try
            {
                string location = typeof(AssemblyLoader).Assembly.Location;
                if (!string.IsNullOrEmpty(location)) folders.Add(Path.GetDirectoryName(location));
            }
            catch { }
            folders.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"));
            if (!string.IsNullOrEmpty(musicBeeStoragePath)) folders.Add(Path.Combine(musicBeeStoragePath, "Plugins"));

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (!name.StartsWith("TagLib", StringComparison.OrdinalIgnoreCase)) return null;

                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (loaded != null) return loaded;

                foreach (string folder in folders)
                {
                    string candidate = Path.Combine(folder, name + ".dll");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                return null;
            };
        }
    }
}
