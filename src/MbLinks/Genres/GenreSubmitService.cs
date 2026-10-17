// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MusicBeePlugin
{
    /// <summary>How often the list of MusicBrainz genres is loaded again.</summary>
    internal enum GenreListRefresh
    {
        EverySession = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3,
        Yearly = 4,
        Never = 5   // keep the stored list; loaded only if there is none yet, or with "Update now"
    }

    /// <summary>One MusicBrainz entity and the genres the user votes for.</summary>
    internal sealed class GenreSubmitEntity
    {
        public GenreSubmitEntity(string kind, string id, string file)
        {
            Kind = kind;
            Id = id;
            File = file;
        }

        public string Kind { get; }          // "recording", "release-group" or "release"
        public string Id { get; }
        public string File { get; }          // first contributing file, for the log
        public List<string> Genres { get; } = new List<string>();
    }

    /// <summary>What a genre submission would do; shown in the confirmation.</summary>
    internal sealed class GenreSubmitPlan
    {
        public int Tracks { get; set; }
        public int WithGenres { get; set; }
        public int WithoutGenres { get; set; }
        public int OnlyUnknownGenres { get; set; }
        public int WithoutIds { get; set; }
        public bool OnlyKnown { get; set; }
        public string FieldName { get; set; }
        public string Levels { get; set; }
        public List<GenreSubmitEntity> Entities { get; } = new List<GenreSubmitEntity>();
        public List<string> UnknownNames { get; } = new List<string>();

        public int Votes => Entities.Sum(e => e.Genres.Count);
        public int Requests => (Entities.Count + GenreSubmitService.BatchSize - 1) / GenreSubmitService.BatchSize;
        public TimeSpan EstimatedDuration => TimeSpan.FromSeconds(Requests * WebService.SecondsPerMusicBrainzRequest);

        private int CountOf(string kind) => Entities.Count(e => e.Kind == kind);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Submit your genres to MusicBrainz?");
            sb.AppendLine();
            AppendCounts(sb);
            sb.AppendLine();
            sb.AppendLine("Genre votes to submit: " + Fmt.Number(Votes));
            sb.AppendLine("Requests to MusicBrainz: about " + Fmt.Number(Requests) + " (up to " + GenreSubmitService.BatchSize + " entities each)");
            sb.AppendLine("Estimated time: " + Fmt.Duration(EstimatedDuration));
            sb.AppendLine();
            sb.AppendLine("Your genres are added as your own votes (\"upvotes\") on MusicBrainz. Existing votes, yours and other people's, " +
                          "are not removed. Every submitted genre is written to the log file.");
            sb.AppendLine();
            sb.Append("Do you really want to submit them now?");
            return sb.ToString();
        }

        public string DescribeNothingToDo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("There are no genres to submit for the selected tracks.");
            sb.AppendLine();
            AppendCounts(sb);
            return sb.ToString();
        }

        private void AppendCounts(StringBuilder sb)
        {
            sb.AppendLine("Tracks: " + Fmt.Number(Tracks));
            sb.AppendLine("   with genres and the needed MusicBrainz IDs: " + Fmt.Number(WithGenres));
            if (WithoutGenres > 0) sb.AppendLine("   without genres in the field \"" + FieldName + "\" (skipped): " + Fmt.Number(WithoutGenres));
            if (OnlyUnknownGenres > 0) sb.AppendLine("   only with names that are not MusicBrainz genres (skipped): " + Fmt.Number(OnlyUnknownGenres));
            if (WithoutIds > 0) sb.AppendLine("   without the needed MusicBrainz IDs (skipped): " + Fmt.Number(WithoutIds));
            sb.AppendLine("Levels: " + Levels);
            sb.AppendLine("Entities: " + Fmt.Count(CountOf("recording"), "recording", "recordings") + ", " +
                          Fmt.Count(CountOf("release-group"), "release group", "release groups") + ", " +
                          Fmt.Count(CountOf("release"), "release", "releases"));
            if (UnknownNames.Count > 0)
            {
                IEnumerable<string> examples = UnknownNames.Take(8).Select(n => "\"" + n + "\"");
                sb.AppendLine("Names that are not MusicBrainz genres (not submitted): " + string.Join(", ", examples) +
                              (UnknownNames.Count > 8 ? " and " + Fmt.Number(UnknownNames.Count - 8) + " more" : ""));
                sb.AppendLine("All MusicBrainz genres: " + GenreFields.MusicBrainzGenresUrl);
            }
        }
    }

    internal sealed class GenreSubmitResult
    {
        public int Recordings;
        public int ReleaseGroups;
        public int Releases;
        public int Votes;
        public string Error;

        public void Add(GenreSubmitEntity entity)
        {
            Votes += entity.Genres.Count;
            if (entity.Kind == "recording") Recordings++;
            else if (entity.Kind == "release-group") ReleaseGroups++;
            else Releases++;
        }

        public string Describe()
        {
            string counts = Fmt.Count(Votes, "genre vote", "genre votes") + " submitted (" +
                            Fmt.Count(Recordings, "recording", "recordings") + ", " +
                            Fmt.Count(ReleaseGroups, "release group", "release groups") + ", " +
                            Fmt.Count(Releases, "release", "releases") + ")";
            if (Error == null) return counts + ".";
            return "genre submission stopped (" + Error + ")" + (Votes > 0 ? ", " + counts + " before that" : "") + ".";
        }
    }

    /// <summary>
    /// Submits the genres from the user's tags to MusicBrainz as the user's own tag votes, on the levels
    /// recording, release group and release. Only ever started manually and only after a confirmation.
    /// Only the IDs stored in the files are used; votes are only added, never removed.
    /// </summary>
    internal sealed class GenreSubmitService
    {
        public const int BatchSize = 100;
        private static readonly XNamespace Mmd = "http://musicbrainz.org/ns/mmd-2.0#";

        private readonly WebService web;
        private readonly MusicBrainzAuth auth;
        private readonly MusicBeeLibrary library;
        private readonly string genreFile;
        private readonly object knownLock = new object();
        private Dictionary<string, string> knownGenres;   // name → MusicBrainz genre name in lower case
        private DateTime knownLoadedUtc;
        private bool knownFromThisSession;

        public GenreSubmitService(WebService web, MusicBrainzAuth auth, MusicBeeLibrary library, string dataFolder)
        {
            this.web = web;
            this.auth = auth;
            this.library = library;
            genreFile = Path.Combine(dataFolder, "genres.txt");
        }

        /// <summary>Reads the genres and IDs of the tracks and builds the votes per entity. Runs in the background.</summary>
        public GenreSubmitPlan Prepare(string[] files, PluginSettings s, string fieldName, IProgress<string> progress, CancellationToken ct)
        {
            var plan = new GenreSubmitPlan
            {
                Tracks = files.Length,
                OnlyKnown = s.GenreSubmitOnlyKnown,
                FieldName = fieldName,
                Levels = DescribeLevels(s)
            };
            Plugin.MetaDataType field = GenreFields.Resolve(s.GenreSubmitField);

            Dictionary<string, string> known = null;
            if (s.GenreSubmitOnlyKnown)
            {
                progress?.Report("Loading the list of MusicBrainz genres …");
                known = LoadKnownGenres(s.GenreListRefresh, ct);
                if (known == null)
                    throw new InvalidOperationException("The list of MusicBrainz genres could not be loaded, so nothing can be checked or submitted. Details are in the log.");
            }

            var recordings = new Dictionary<string, GenreSubmitEntity>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, AlbumTracks>(StringComparer.OrdinalIgnoreCase);
            var releases = new Dictionary<string, AlbumTracks>(StringComparer.OrdinalIgnoreCase);
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                if (i % 50 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Reading genres and MusicBrainz IDs … " + Fmt.Number(i) + " / " + Fmt.Number(files.Length) + " tracks");
                }

                string file = files[i];
                if (string.IsNullOrEmpty(file) || file.Contains("://")) { plan.WithoutIds++; continue; }

                List<string> names = SplitGenres(library.GetTag(file, field));
                if (names.Count == 0) { plan.WithoutGenres++; continue; }

                var genres = new List<string>();
                foreach (string name in names)
                {
                    if (known == null) genres.Add(name.ToLowerInvariant());
                    else if (known.TryGetValue(name, out string genre)) genres.Add(genre);
                    else unknown.Add(name);
                }
                genres = genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (genres.Count == 0) { plan.OnlyUnknownGenres++; continue; }

                MbIds ids = MbidReader.Read(file, PluginLog.Error);
                bool used = false;

                if (s.GenreSubmitRecording && ids.RecordingId != null)
                {
                    if (!recordings.TryGetValue(ids.RecordingId, out GenreSubmitEntity entity))
                    {
                        entity = new GenreSubmitEntity("recording", ids.RecordingId, file);
                        recordings[ids.RecordingId] = entity;
                    }
                    foreach (string genre in genres)
                        if (!entity.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)) entity.Genres.Add(genre);
                    used = true;
                }
                if (s.GenreSubmitReleaseGroup && ids.ReleaseGroupId != null)
                {
                    AddTrack(groups, ids.ReleaseGroupId, genres, file);
                    used = true;
                }
                if (s.GenreSubmitRelease && ids.ReleaseId != null)
                {
                    AddTrack(releases, ids.ReleaseId, genres, file);
                    used = true;
                }

                if (used) plan.WithGenres++;
                else plan.WithoutIds++;
            }

            plan.Entities.AddRange(recordings.Values);
            plan.Entities.AddRange(AlbumEntities("release-group", groups, s.GenreSubmitAlbumShared));
            plan.Entities.AddRange(AlbumEntities("release", releases, s.GenreSubmitAlbumShared));
            plan.Entities.RemoveAll(e => e.Genres.Count == 0);   // e.g. no genre shared by all tracks of an album
            plan.UnknownNames.AddRange(unknown.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return plan;
        }

        public async Task<GenreSubmitResult> SubmitAsync(GenreSubmitPlan plan, IProgress<string> progress, CancellationToken ct)
        {
            var result = new GenreSubmitResult();
            try
            {
                for (int start = 0; start < plan.Entities.Count; start += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    List<GenreSubmitEntity> batch = plan.Entities.Skip(start).Take(BatchSize).ToList();
                    progress?.Report("submitting genres … " + Fmt.Number(start + batch.Count) + "/" + Fmt.Number(plan.Entities.Count));

                    int status = await AuthorizedPostAsync(BuildXml(batch), ct).ConfigureAwait(false);
                    List<string> lines = batch.Select(e => DescribeEntity(e) + " | " + WebService.DescribeStatus(status)).ToList();

                    if (status < 200 || status >= 300)
                    {
                        PluginLog.ExportFailed(lines);
                        result.Error = status == 401 || status == 403
                            ? "missing permission; please log in again in the plugin settings"
                            : WebService.DescribeStatus(status);
                        PluginLog.Error("Submitting genres for " + Fmt.Count(batch.Count, "entity", "entities") + " failed (" + result.Error + ").");
                        break;
                    }

                    PluginLog.Export(lines);
                    foreach (GenreSubmitEntity entity in batch) result.Add(entity);
                }
            }
            catch (NotLoggedInException)
            {
                result.Error = "not logged in or login expired";
                PluginLog.Error("Submitting genres: not logged in or login expired.");
            }

            PluginLog.Info("Genre submission: " + result.Describe());
            return result;
        }

        // ---------- helpers ----------

        public static string DescribeLevels(PluginSettings s)
        {
            var levels = new List<string>();
            if (s.GenreSubmitRecording) levels.Add("recording (track)");
            if (s.GenreSubmitReleaseGroup) levels.Add("release group (album)");
            if (s.GenreSubmitRelease) levels.Add("release (edition)");
            string text = levels.Count == 0 ? "none" : string.Join(", ", levels);
            if (s.GenreSubmitReleaseGroup || s.GenreSubmitRelease)
                text += s.GenreSubmitAlbumShared ? "; albums get the genres shared by all selected tracks" : "; albums get the genres of any selected track";
            return text;
        }

        private static List<string> SplitGenres(string value) =>
            (value ?? "").Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static void AddTrack(Dictionary<string, AlbumTracks> albums, string id, List<string> genres, string file)
        {
            if (!albums.TryGetValue(id, out AlbumTracks album))
            {
                album = new AlbumTracks(file);
                albums[id] = album;
            }
            album.TrackGenres.Add(genres);
        }

        private static IEnumerable<GenreSubmitEntity> AlbumEntities(string kind, Dictionary<string, AlbumTracks> albums, bool sharedOnly)
        {
            foreach (KeyValuePair<string, AlbumTracks> album in albums)
            {
                var entity = new GenreSubmitEntity(kind, album.Key, album.Value.File);
                List<List<string>> tracks = album.Value.TrackGenres;
                IEnumerable<string> genres = sharedOnly
                    ? tracks[0].Where(g => tracks.All(t => t.Contains(g, StringComparer.OrdinalIgnoreCase)))
                    : tracks.SelectMany(t => t);
                entity.Genres.AddRange(genres.Distinct(StringComparer.OrdinalIgnoreCase));
                yield return entity;
            }
        }

        private static string DescribeEntity(GenreSubmitEntity e) =>
            "tags " + e.Kind + " " + e.Id + " | upvote: " + string.Join(GenreFields.Separator, e.Genres) + " | manual" + PluginLog.FileColumn(e.File);

        private static string BuildXml(List<GenreSubmitEntity> batch)
        {
            var root = new XElement(Mmd + "metadata");
            foreach (string kind in new[] { "recording", "release-group", "release" })
            {
                List<GenreSubmitEntity> ofKind = batch.Where(e => e.Kind == kind).ToList();
                if (ofKind.Count == 0) continue;
                root.Add(new XElement(Mmd + (kind + "-list"), ofKind.Select(e =>
                    new XElement(Mmd + kind,
                        new XAttribute("id", e.Id),
                        new XElement(Mmd + "user-tag-list", e.Genres.Select(genre =>
                            new XElement(Mmd + "user-tag",
                                new XAttribute("vote", "upvote"),
                                new XElement(Mmd + "name", genre))))))));
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private async Task<int> AuthorizedPostAsync(string xml, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                if (token == null) throw new NotLoggedInException();

                int status = await web.PostSubmissionAsync("tag", xml, token, ct).ConfigureAwait(false);
                if (status != 401) return status;
                auth.InvalidateAccessToken();
            }
            return 401;
        }

        // ---------- list of MusicBrainz genres ----------

        /// <summary>
        /// All genre names MusicBrainz knows. Kept in memory and in MbLinks\genres.txt, and loaded again from
        /// MusicBrainz according to the setting. If loading fails, the stored list is used.
        /// </summary>
        private Dictionary<string, string> LoadKnownGenres(GenreListRefresh refresh, CancellationToken ct)
        {
            lock (knownLock)
                if (knownGenres != null && !IsDue(refresh, knownLoadedUtc, knownFromThisSession)) return knownGenres;

            StoredGenreList stored = ReadStoredList();
            if (stored != null && !IsDue(refresh, stored.LoadedUtc, false))
            {
                SetCache(stored.Genres, stored.LoadedUtc, false);
                PluginLog.Info("Using the stored list of MusicBrainz genres from " + LocalDate(stored.LoadedUtc) +
                               " (" + Fmt.Count(stored.Genres.Count, "genre", "genres") + ").");
                return stored.Genres;
            }

            Dictionary<string, string> downloaded = DownloadKnownGenresAsync(ct).GetAwaiter().GetResult();
            if (downloaded != null)
            {
                Store(downloaded);
                return downloaded;
            }

            if (stored != null)
            {
                PluginLog.Error("Could not update the list of MusicBrainz genres; using the stored list from " + LocalDate(stored.LoadedUtc) + ".");
                SetCache(stored.Genres, stored.LoadedUtc, false);
                return stored.Genres;
            }
            return null;
        }

        /// <summary>"Update now" in the settings: loads the list from MusicBrainz regardless of the interval.</summary>
        public async Task<string> RefreshNowAsync(CancellationToken ct)
        {
            Dictionary<string, string> downloaded = await DownloadKnownGenresAsync(ct).ConfigureAwait(false);
            if (downloaded == null) return "The genre list could not be loaded from MusicBrainz; details are in the log.";
            Store(downloaded);
            return DescribeStoredList();
        }

        /// <summary>Short description of the list in use, for the settings window.</summary>
        public string DescribeStoredList()
        {
            int count;
            DateTime loadedUtc;
            lock (knownLock)
            {
                count = knownGenres?.Count ?? 0;
                loadedUtc = knownLoadedUtc;
            }

            if (count == 0)
            {
                StoredGenreList stored = ReadStoredList();
                if (stored == null) return "Genre list: not loaded yet.";
                count = stored.Genres.Count;
                loadedUtc = stored.LoadedUtc;
            }
            return "Genre list: " + Fmt.Count(count, "genre", "genres") + ", loaded on " + LocalDate(loadedUtc) + ".";
        }

        private static bool IsDue(GenreListRefresh refresh, DateTime loadedUtc, bool fromThisSession)
        {
            DateTime now = DateTime.UtcNow;
            switch (refresh)
            {
                case GenreListRefresh.EverySession: return !fromThisSession;
                case GenreListRefresh.Daily: return loadedUtc.AddDays(1) <= now;
                case GenreListRefresh.Weekly: return loadedUtc.AddDays(7) <= now;
                case GenreListRefresh.Monthly: return loadedUtc.AddMonths(1) <= now;
                case GenreListRefresh.Yearly: return loadedUtc.AddYears(1) <= now;
                default: return false;   // never
            }
        }

        private void Store(Dictionary<string, string> genres)
        {
            DateTime now = DateTime.UtcNow;
            SetCache(genres, now, true);
            try
            {
                var lines = new List<string> { "# " + now.ToString("o", CultureInfo.InvariantCulture) };
                lines.AddRange(genres.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                File.WriteAllLines(genreFile, lines, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not save the list of MusicBrainz genres: " + ex.Message);
            }
            PluginLog.Info("List of MusicBrainz genres loaded: " + Fmt.Count(genres.Count, "genre", "genres") + ".");
        }

        private void SetCache(Dictionary<string, string> genres, DateTime loadedUtc, bool fromThisSession)
        {
            lock (knownLock)
            {
                knownGenres = genres;
                knownLoadedUtc = loadedUtc;
                knownFromThisSession = fromThisSession;
            }
        }

        /// <summary>genres.txt: first line "# " and the load time (UTC), then one genre per line.</summary>
        private StoredGenreList ReadStoredList()
        {
            try
            {
                if (!File.Exists(genreFile)) return null;
                string[] lines = File.ReadAllLines(genreFile, Encoding.UTF8);
                if (lines.Length < 2 || !lines[0].StartsWith("#")) return null;
                if (!DateTime.TryParse(lines[0].TrimStart('#').Trim(), CultureInfo.InvariantCulture,
                                       DateTimeStyles.RoundtripKind, out DateTime loaded)) return null;

                var genres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1))
                {
                    string name = line.Trim();
                    if (name.Length > 0) genres[name] = name.ToLowerInvariant();
                }
                return genres.Count > 0 ? new StoredGenreList(genres, loaded.ToUniversalTime()) : null;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not read the stored list of MusicBrainz genres: " + ex.Message);
                return null;
            }
        }

        private static string LocalDate(DateTime utc) =>
            utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        private sealed class StoredGenreList
        {
            public StoredGenreList(Dictionary<string, string> genres, DateTime loadedUtc)
            {
                Genres = genres;
                LoadedUtc = loadedUtc;
            }

            public Dictionary<string, string> Genres { get; }
            public DateTime LoadedUtc { get; }
        }

        private async Task<Dictionary<string, string>> DownloadKnownGenresAsync(CancellationToken ct)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // plain text list, one genre per line
            var text = await web.GetMusicBrainzTextAsync("genre/all?fmt=txt", ct).ConfigureAwait(false);
            if (text.Ok)
            {
                string[] lines = text.Body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool plausible = lines.Length > 100 && !lines.Any(l => l.TrimStart().StartsWith("<") || l.TrimStart().StartsWith("{"));
                if (plausible)
                {
                    foreach (string line in lines)
                    {
                        string name = line.Trim();
                        if (name.Length > 0) map[name] = name.ToLowerInvariant();
                    }
                    return map;
                }
            }

            // fallback: the same list page by page as JSON
            int offset = 0;
            while (true)
            {
                var page = await web.GetMusicBrainzAsync("genre/all?limit=100&offset=" + offset + "&fmt=json", ct).ConfigureAwait(false);
                if (!page.Ok || page.Data == null)
                {
                    PluginLog.Error("Could not load the list of MusicBrainz genres (" + WebService.DescribeStatus(page.Status) + ").");
                    return null;
                }

                int count = 0;
                foreach (object item in Json.Arr(page.Data, "genres"))
                {
                    count++;
                    string name = Json.Str(item as Dictionary<string, object>, "name");
                    if (!string.IsNullOrWhiteSpace(name)) map[name.Trim()] = name.Trim().ToLowerInvariant();
                }

                offset += count;
                double total = Json.Number(page.Data, "genre-count") ?? 0;
                if (count == 0 || offset >= total) break;
            }
            return map.Count > 0 ? map : null;
        }

        private sealed class AlbumTracks
        {
            public AlbumTracks(string file)
            {
                File = file;
            }

            public string File { get; }
            public List<List<string>> TrackGenres { get; } = new List<List<string>>();
        }

        private sealed class NotLoggedInException : Exception
        {
        }
    }
}
