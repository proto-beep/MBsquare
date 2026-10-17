// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MusicBeePlugin
{
    internal sealed class RatingResult
    {
        public int TracksSent;
        public int AlbumsSent;
        public int TracksImported;
        public int AlbumsImported;
        public int Skipped;      // tracks without a matching MusicBrainz ID
        public int Failed;       // failed requests during import
        public string Error;     // reason for stopping

        /// <summary>Text for the status bar.</summary>
        public string Describe(bool import)
        {
            int tracks = import ? TracksImported : TracksSent;
            int albums = import ? AlbumsImported : AlbumsSent;
            int total = tracks + albums;
            string verb = import ? " imported" : " submitted";

            string text;
            if (Error != null)
            {
                text = (import ? "import" : "submission") + " stopped (" + Error + ")";
                if (total > 0) text += ", " + Count(total, "rating", "ratings") + verb + " before that";
            }
            else if (total == 0)
            {
                text = import ? "no ratings found to import" : "no new ratings to submit";
            }
            else
            {
                text = Count(total, "rating", "ratings") + verb
                     + " (" + Count(tracks, "track", "tracks") + ", " + Count(albums, "album", "albums") + ")";
            }

            if (Skipped > 0) text += ", " + Count(Skipped, "track", "tracks") + " without MusicBrainz ID skipped";
            if (Failed > 0) text += ", " + Count(Failed, "request", "requests") + " failed";
            return text + ".";
        }

        private static string Count(int n, string one, string many) => Fmt.Number(n) + " " + (n == 1 ? one : many);
    }

    /// <summary>
    /// Syncs MusicBee's ratings with the user's own ratings on MusicBrainz:
    /// track rating ↔ recording, album rating ↔ release group. MusicBrainz only knows whole stars (20–100).
    /// Every submitted rating is written to the log file.
    /// </summary>
    internal sealed class RatingService
    {
        private const int BatchSize = 100;
        private static readonly XNamespace Mmd = "http://musicbrainz.org/ns/mmd-2.0#";

        private readonly WebService web;
        private readonly MusicBrainzAuth auth;
        private readonly MusicBeeLibrary library;

        // values submitted or imported in this session, so automatic submission does not send anything twice
        private readonly ConcurrentDictionary<string, int> lastKnown = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public RatingService(WebService web, MusicBrainzAuth auth, MusicBeeLibrary library)
        {
            this.web = web;
            this.auth = auth;
            this.library = library;
        }

        // ---------- submitting ----------

        /// <param name="trigger">How the submission was started (manual, automatic, initial); written to the log.</param>
        public async Task<RatingResult> UploadAsync(IList<string> files, PluginSettings s, bool onlyChanged, string trigger,
                                                    IProgress<string> progress, CancellationToken ct)
        {
            var result = new RatingResult();
            if (await auth.GetAccessTokenAsync(ct).ConfigureAwait(false) == null)
            {
                result.Error = "not logged in or login expired";
                PluginLog.Error("Submitting ratings: not logged in or login expired.");
                return result;
            }

            var recordings = new Dictionary<string, RatingEntry>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, RatingEntry>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("reading ratings … " + Fmt.Number(i + 1) + "/" + Fmt.Number(files.Count));
                string file = files[i];

                int trackValue = s.RatingsTracks ? ToMusicBrainz(ParseStars(library.GetTrackRating(file)), s.RoundHalfUp) : 0;
                int albumValue = s.RatingsAlbums ? ToMusicBrainz(ParseStars(library.GetAlbumRating(file)), s.RoundHalfUp) : 0;
                if (trackValue == 0 && albumValue == 0) continue;   // unrated: nothing to submit

                MbIds ids = MbidReader.Read(file, PluginLog.Error);
                bool skipped = false;

                if (trackValue > 0)
                {
                    if (ids.RecordingId != null) recordings[ids.RecordingId] = new RatingEntry("recording", ids.RecordingId, trackValue, file);
                    else skipped = true;
                }
                if (albumValue > 0)
                {
                    // only the release group ID stored in the file counts; nothing is looked up
                    if (ids.ReleaseGroupId != null) groups[ids.ReleaseGroupId] = new RatingEntry("release-group", ids.ReleaseGroupId, albumValue, file);
                    else skipped = true;
                }
                if (skipped) result.Skipped++;
            }

            List<RatingEntry> entries = recordings.Values.Concat(groups.Values)
                .Where(e => !onlyChanged || !IsKnown(e))
                .ToList();

            try
            {
                for (int start = 0; start < entries.Count; start += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    List<RatingEntry> batch = entries.Skip(start).Take(BatchSize).ToList();
                    progress?.Report("submitting ratings … " + Fmt.Number(start + batch.Count) + "/" + Fmt.Number(entries.Count));

                    int status = await AuthorizedPostAsync(BuildXml(batch), ct).ConfigureAwait(false);
                    List<string> lines = batch.Select(e => DescribeEntry(e, trigger) + " | " + WebService.DescribeStatus(status)).ToList();

                    if (status < 200 || status >= 300)
                    {
                        PluginLog.ExportFailed(lines);
                        result.Error = WebService.DescribeStatus(status);
                        PluginLog.Error("Submitting " + Fmt.Count(batch.Count, "rating", "ratings") + " to MusicBrainz failed (" + result.Error + ").");
                        break;
                    }

                    PluginLog.Export(lines);
                    foreach (RatingEntry entry in batch)
                    {
                        lastKnown[entry.Kind + ":" + entry.Id] = entry.Value;
                        if (entry.Kind == "recording") result.TracksSent++;
                        else result.AlbumsSent++;
                    }
                }
            }
            catch (NotLoggedInException)
            {
                result.Error = "not logged in or login expired";
                PluginLog.Error("Submitting ratings: not logged in or login expired.");
            }
            return result;
        }

        // ---------- importing ----------

        public async Task<RatingResult> ImportAsync(IList<string> files, PluginSettings s, Action<string> beforeFileChange,
                                                    IProgress<string> progress, CancellationToken ct)
        {
            var result = new RatingResult();
            if (await auth.GetAccessTokenAsync(ct).ConfigureAwait(false) == null)
            {
                result.Error = "not logged in or login expired";
                PluginLog.Error("Importing ratings: not logged in or login expired.");
                return result;
            }

            var infos = new List<FileIds>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("reading MusicBrainz IDs … " + Fmt.Number(i + 1) + "/" + Fmt.Number(files.Count));
                MbIds ids = MbidReader.Read(files[i], PluginLog.Error);
                bool usable = (s.RatingsTracks && ids.RecordingId != null) || (s.RatingsAlbums && ids.ReleaseGroupId != null);
                if (!usable) { result.Skipped++; continue; }
                infos.Add(new FileIds(files[i], ids));
            }

            try
            {
                // track ratings: requested per release, tracks without release ID one by one
                var trackStars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (s.RatingsTracks)
                {
                    var wanted = new HashSet<string>(
                        infos.Where(f => f.Ids.RecordingId != null).Select(f => f.Ids.RecordingId),
                        StringComparer.OrdinalIgnoreCase);

                    List<string> releases = infos
                        .Where(f => f.Ids.RecordingId != null && f.Ids.ReleaseId != null)
                        .Select(f => f.Ids.ReleaseId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    for (int i = 0; i < releases.Count; i++)
                    {
                        progress?.Report("fetching track ratings … album " + Fmt.Number(i + 1) + "/" + Fmt.Number(releases.Count));
                        await BrowseRecordingRatingsAsync(releases[i], wanted, trackStars, result, ct).ConfigureAwait(false);
                    }

                    List<string> loose = infos
                        .Where(f => f.Ids.RecordingId != null && f.Ids.ReleaseId == null)
                        .Select(f => f.Ids.RecordingId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    for (int i = 0; i < loose.Count; i++)
                    {
                        progress?.Report("fetching track ratings … track " + Fmt.Number(i + 1) + "/" + Fmt.Number(loose.Count));
                        var data = await AuthorizedGetAsync("recording/" + loose[i] + "?inc=user-ratings&fmt=json", result, ct).ConfigureAwait(false);
                        int stars = ParseUserRating(data);
                        if (stars > 0) trackStars[loose[i]] = stars;
                    }
                }

                // album ratings: per release group
                var groupOfFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var groupStars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (s.RatingsAlbums)
                {
                    // only the release group ID stored in the file counts; nothing is looked up
                    foreach (FileIds f in infos)
                        if (f.Ids.ReleaseGroupId != null) groupOfFile[f.File] = f.Ids.ReleaseGroupId;

                    List<string> groupIds = groupOfFile.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    for (int i = 0; i < groupIds.Count; i++)
                    {
                        progress?.Report("fetching album ratings … " + Fmt.Number(i + 1) + "/" + Fmt.Number(groupIds.Count));
                        var data = await AuthorizedGetAsync("release-group/" + groupIds[i] + "?inc=user-ratings&fmt=json", result, ct).ConfigureAwait(false);
                        int stars = ParseUserRating(data);
                        if (stars > 0) groupStars[groupIds[i]] = stars;
                    }
                }

                // apply in MusicBee (announce first, so automatic submission ignores these changes)
                foreach (FileIds f in infos) beforeFileChange?.Invoke(f.File);

                var countedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileIds f in infos)
                {
                    ct.ThrowIfCancellationRequested();
                    bool changed = false;

                    if (f.Ids.RecordingId != null &&
                        trackStars.TryGetValue(f.Ids.RecordingId, out int trackRating) &&
                        ApplyRating(f.File, false, trackRating, s.ImportOverwrite))
                    {
                        changed = true;
                        result.TracksImported++;
                        lastKnown["recording:" + f.Ids.RecordingId] = trackRating * 20;
                    }

                    if (groupOfFile.TryGetValue(f.File, out string groupId) &&
                        groupStars.TryGetValue(groupId, out int albumRating) &&
                        ApplyRating(f.File, true, albumRating, s.ImportOverwrite))
                    {
                        changed = true;
                        if (countedGroups.Add(groupId)) result.AlbumsImported++;
                        lastKnown["release-group:" + groupId] = albumRating * 20;
                    }

                    if (changed) library.Commit(f.File);
                }
            }
            catch (NotLoggedInException)
            {
                result.Error = "not logged in or login expired";
                PluginLog.Error("Importing ratings: not logged in or login expired.");
            }

            library.RefreshPanels();
            PluginLog.Info("Rating import: " + result.Describe(true));
            return result;
        }

        // ---------- conversion ----------

        /// <summary>MusicBee rating (0–5, half stars allowed) as a number; 0 = unrated.</summary>
        internal static double ParseStars(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return 0;
            if (value > 5) value /= 20.0;   // in case the rating comes as 0–100
            return Math.Max(0, Math.Min(5, value));
        }

        /// <summary>Stars → MusicBrainz value (20, 40 … 100); 0 = do not submit.</summary>
        internal static int ToMusicBrainz(double stars, bool roundHalfUp)
        {
            if (stars <= 0) return 0;
            double whole = roundHalfUp ? Math.Ceiling(stars - 1e-9) : Math.Floor(stars + 1e-9);
            if (whole < 1) return 0;
            return (int)Math.Min(5, whole) * 20;
        }

        /// <summary>The user's own rating from a MusicBrainz response as whole stars (1–5), 0 = none.</summary>
        private static int ParseUserRating(Dictionary<string, object> entity)
        {
            if (entity == null || !entity.TryGetValue("user-rating", out object raw) || raw == null) return 0;
            double? value = raw is Dictionary<string, object> d ? Json.Number(d, "value") : Json.ToDouble(raw);
            if (value == null || value.Value <= 0) return 0;
            double stars = value.Value > 5 ? value.Value / 20.0 : value.Value;
            return (int)Math.Max(1, Math.Min(5, Math.Round(stars, MidpointRounding.AwayFromZero)));
        }

        // ---------- helpers ----------

        private static string DescribeEntry(RatingEntry e, string trigger) =>
            e.Kind + " " + e.Id + " | rating " + e.Value + " (" + Fmt.Count(e.Value / 20, "star", "stars") + ") | " + trigger + PluginLog.FileColumn(e.File);

        private bool ApplyRating(string file, bool album, int stars, bool overwrite)
        {
            double current = ParseStars(album ? library.GetAlbumRating(file) : library.GetTrackRating(file));
            if (current > 0 && !overwrite) return false;
            if (Math.Abs(current - stars) < 0.01) return false;

            string value = stars.ToString(CultureInfo.InvariantCulture);
            return album ? library.SetAlbumRating(file, value) : library.SetTrackRating(file, value);
        }

        private bool IsKnown(RatingEntry entry) =>
            lastKnown.TryGetValue(entry.Kind + ":" + entry.Id, out int previous) && previous == entry.Value;

        private async Task BrowseRecordingRatingsAsync(string releaseId, HashSet<string> wanted, Dictionary<string, int> target, RatingResult result, CancellationToken ct)
        {
            int offset = 0;
            while (true)
            {
                var data = await AuthorizedGetAsync(
                    "recording?release=" + releaseId + "&inc=user-ratings&limit=100&offset=" + offset + "&fmt=json",
                    result, ct).ConfigureAwait(false);
                if (data == null) return;

                int count = 0;
                foreach (object item in Json.Arr(data, "recordings"))
                {
                    count++;
                    var recording = item as Dictionary<string, object>;
                    string id = Json.Str(recording, "id");
                    int stars = ParseUserRating(recording);
                    if (id != null && stars > 0 && wanted.Contains(id)) target[id] = stars;
                }

                offset += count;
                double total = Json.Number(data, "recording-count") ?? 0;
                if (count == 0 || offset >= total) return;
            }
        }

        private async Task<Dictionary<string, object>> AuthorizedGetAsync(string pathAndQuery, RatingResult result, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                if (token == null) throw new NotLoggedInException();

                var response = await web.GetMusicBrainzAsync(pathAndQuery, ct, token).ConfigureAwait(false);
                if (response.Status == 401)
                {
                    auth.InvalidateAccessToken();
                    continue;
                }
                if (!response.Ok)
                {
                    result.Failed++;
                    PluginLog.Error("MusicBrainz request " + pathAndQuery + " failed (" + WebService.DescribeStatus(response.Status) + ").");
                }
                return response.Data;
            }
            throw new NotLoggedInException();
        }

        private async Task<int> AuthorizedPostAsync(string xml, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
                if (token == null) throw new NotLoggedInException();

                int status = await web.PostRatingsAsync(xml, token, ct).ConfigureAwait(false);
                if (status != 401) return status;
                auth.InvalidateAccessToken();
            }
            throw new NotLoggedInException();
        }

        private static string BuildXml(List<RatingEntry> batch)
        {
            var root = new XElement(Mmd + "metadata");

            List<RatingEntry> recordings = batch.Where(e => e.Kind == "recording").ToList();
            if (recordings.Count > 0)
                root.Add(new XElement(Mmd + "recording-list", recordings.Select(e =>
                    new XElement(Mmd + "recording", new XAttribute("id", e.Id), new XElement(Mmd + "user-rating", e.Value)))));

            List<RatingEntry> groups = batch.Where(e => e.Kind == "release-group").ToList();
            if (groups.Count > 0)
                root.Add(new XElement(Mmd + "release-group-list", groups.Select(e =>
                    new XElement(Mmd + "release-group", new XAttribute("id", e.Id), new XElement(Mmd + "user-rating", e.Value)))));

            return root.ToString(SaveOptions.DisableFormatting);
        }

        private sealed class RatingEntry
        {
            public RatingEntry(string kind, string id, int value, string file)
            {
                Kind = kind;
                Id = id;
                Value = value;
                File = file;
            }

            public string Kind { get; }
            public string Id { get; }
            public int Value { get; }
            public string File { get; }
        }

        private sealed class FileIds
        {
            public FileIds(string file, MbIds ids)
            {
                File = file;
                Ids = ids;
            }

            public string File { get; }
            public MbIds Ids { get; }
        }

        private sealed class NotLoggedInException : Exception
        {
        }
    }

    /// <summary>Progress for the status bar, updated at most twice per second.</summary>
    internal sealed class StatusProgress : IProgress<string>
    {
        private readonly Action<string> report;
        private DateTime last = DateTime.MinValue;

        public StatusProgress(Action<string> report)
        {
            this.report = report;
        }

        public void Report(string value)
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 500) return;
            last = DateTime.UtcNow;
            report(value);
        }
    }
}
