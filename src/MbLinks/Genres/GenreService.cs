// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    /// <summary>Where the genres come from.</summary>
    internal enum GenreSource
    {
        Recording = 0,                  // each track's own genres
        ReleaseGroup = 1,               // the album's genres for all its tracks
        RecordingThenReleaseGroup = 2   // the track's genres, otherwise the album's
    }

    /// <summary>The tag field the genres are written to: MusicBee's genre field or one of its custom tags.</summary>
    internal static class GenreFields
    {
        public const string GenreKey = "Genre";
        public const string Separator = "; ";

        /// <summary>The page on MusicBrainz that lists all genres.</summary>
        public const string MusicBrainzGenresUrl = "https://musicbrainz.org/genres";

        public static Plugin.MetaDataType Resolve(string key)
        {
            if (!string.IsNullOrEmpty(key) && key.StartsWith("Custom", StringComparison.Ordinal) &&
                Enum.IsDefined(typeof(Plugin.MetaDataType), key) &&
                Enum.TryParse(key, out Plugin.MetaDataType field))
                return field;
            return Plugin.MetaDataType.Genre;
        }

        /// <summary>"Custom1" … "Custom16", as far as the MusicBee API in use knows them.</summary>
        public static IEnumerable<string> CustomKeys()
        {
            for (int i = 1; i <= 16; i++)
            {
                string key = "Custom" + i;
                if (Enum.IsDefined(typeof(Plugin.MetaDataType), key)) yield return key;
            }
        }

        public static string Normalize(string key)
        {
            if (key == GenreKey) return key;
            return CustomKeys().Contains(key) ? key : GenreKey;
        }
    }

    /// <summary>What a genre import would do; shown in the confirmation.</summary>
    internal sealed class GenrePlan
    {
        public int Tracks { get; set; }
        public int WithoutIds { get; set; }
        public List<KeyValuePair<string, MbIds>> Items { get; } = new List<KeyValuePair<string, MbIds>>();
        public int ReleaseBrowses { get; set; }      // track genres, one request per album (up to 100 tracks)
        public int RecordingLookups { get; set; }    // tracks without release ID, one request each
        public int GroupLookups { get; set; }        // album genres, one request per release group
        public GenreSource Source { get; set; }

        public int Requests => ReleaseBrowses + RecordingLookups + GroupLookups;

        public TimeSpan EstimatedDuration => TimeSpan.FromSeconds(Requests * WebService.SecondsPerMusicBrainzRequest);

        public string Describe(PluginSettings s, string fieldName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Get the MusicBrainz genres for the selected tracks?");
            sb.AppendLine();
            sb.AppendLine("Tracks: " + Fmt.Number(Tracks));
            sb.AppendLine("   with MusicBrainz IDs: " + Fmt.Number(Items.Count));
            if (WithoutIds > 0) sb.AppendLine("   without MusicBrainz IDs (skipped): " + Fmt.Number(WithoutIds));
            sb.AppendLine("Source: " + DescribeSource(Source));
            sb.AppendLine("Requests to MusicBrainz: " + (Source == GenreSource.RecordingThenReleaseGroup ? "up to " : "about ") +
                          Fmt.Number(Requests) + " (at most about one per second)");
            sb.AppendLine("Estimated time: " + Fmt.Duration(EstimatedDuration));
            sb.AppendLine();
            sb.AppendLine("The genres are written to the field \"" + fieldName + "\", separated by \"" + GenreFields.Separator.Trim() + "\". " +
                          (s.GenreReplace ? "Existing values in this field are replaced. " : "Existing values are kept; missing genres are added. ") +
                          "Tracks for which MusicBrainz has no genres are left unchanged. " +
                          "Every change, with the old and the new value, is written to the log file.");
            sb.AppendLine();
            sb.Append("Do you really want to write the genres now?");
            return sb.ToString();
        }

        public static string DescribeSource(GenreSource source)
        {
            switch (source)
            {
                case GenreSource.ReleaseGroup: return "release group (the album's genres for all its tracks)";
                case GenreSource.RecordingThenReleaseGroup: return "recording, otherwise release group";
                default: return "recording (each track's own genres)";
            }
        }
    }

    internal sealed class GenreResult
    {
        public int Written;
        public int Unchanged;
        public int NoGenres;
        public int WriteFailed;
        public int Failed;

        public string Describe()
        {
            string text = "genres written for " + Fmt.Number(Written) + (Written == 1 ? " track" : " tracks");
            if (Unchanged > 0) text += ", " + Fmt.Number(Unchanged) + " already up to date";
            if (NoGenres > 0) text += ", " + Fmt.Number(NoGenres) + " without genres on MusicBrainz (unchanged)";
            if (WriteFailed > 0) text += ", " + Fmt.Number(WriteFailed) + " could not be written";
            if (Failed > 0) text += ", " + Fmt.Number(Failed) + (Failed == 1 ? " request" : " requests") + " failed (see the log)";
            return text + ".";
        }
    }

    /// <summary>
    /// Writes the genres from MusicBrainz into the tags of the selected tracks. Only ever started manually
    /// (right-click → MusicBrainz → Get MB genres if available) and only after a confirmation.
    /// </summary>
    internal sealed class GenreService
    {
        private readonly WebService web;
        private readonly MusicBeeLibrary library;

        public GenreService(WebService web, MusicBeeLibrary library)
        {
            this.web = web;
            this.library = library;
        }

        /// <summary>Reads the MusicBrainz IDs of the tracks and counts the requests that will be needed.</summary>
        public GenrePlan Prepare(string[] files, PluginSettings s, IProgress<string> progress, CancellationToken ct)
        {
            var plan = new GenrePlan { Tracks = files.Length, Source = s.GenreSource };
            bool needRecording = s.GenreSource != GenreSource.ReleaseGroup;
            bool needGroup = s.GenreSource != GenreSource.Recording;

            var releases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loose = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                if (i % 50 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Reading MusicBrainz IDs … " + Fmt.Number(i) + " / " + Fmt.Number(files.Length) + " tracks");
                }

                string file = files[i];
                if (string.IsNullOrEmpty(file) || file.Contains("://")) { plan.WithoutIds++; continue; }

                MbIds ids = MbidReader.Read(file, PluginLog.Error);
                // only the IDs stored in the file count: recording ID for track genres, release group ID for album genres
                bool usable = (needRecording && ids.RecordingId != null) || (needGroup && ids.ReleaseGroupId != null);
                if (!usable) { plan.WithoutIds++; continue; }
                plan.Items.Add(new KeyValuePair<string, MbIds>(file, ids));

                if (needRecording && ids.RecordingId != null)
                {
                    if (ids.ReleaseId != null) releases.Add(ids.ReleaseId);
                    else loose.Add(ids.RecordingId);
                }
                if (needGroup && ids.ReleaseGroupId != null) groups.Add(ids.ReleaseGroupId);
            }

            plan.ReleaseBrowses = releases.Count;
            plan.RecordingLookups = loose.Count;
            plan.GroupLookups = groups.Count;
            return plan;
        }

        public async Task<GenreResult> ApplyAsync(GenrePlan plan, PluginSettings s, IProgress<string> progress, CancellationToken ct)
        {
            var result = new GenreResult();
            Plugin.MetaDataType field = GenreFields.Resolve(s.GenreTargetField);
            string fieldName = s.GenreTargetField;
            bool needRecording = s.GenreSource != GenreSource.ReleaseGroup;
            bool needGroup = s.GenreSource != GenreSource.Recording;

            // track genres: one request per album, tracks without release ID one by one
            var recordingGenres = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (needRecording)
            {
                var wanted = new HashSet<string>(
                    plan.Items.Where(x => x.Value.RecordingId != null).Select(x => x.Value.RecordingId),
                    StringComparer.OrdinalIgnoreCase);

                List<string> releases = plan.Items
                    .Where(x => x.Value.RecordingId != null && x.Value.ReleaseId != null)
                    .Select(x => x.Value.ReleaseId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = 0; i < releases.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("fetching track genres … album " + Fmt.Number(i + 1) + "/" + Fmt.Number(releases.Count));
                    await BrowseRecordingGenresAsync(releases[i], wanted, recordingGenres, result, ct).ConfigureAwait(false);
                }

                List<string> loose = plan.Items
                    .Where(x => x.Value.RecordingId != null && x.Value.ReleaseId == null)
                    .Select(x => x.Value.RecordingId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = 0; i < loose.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("fetching track genres … track " + Fmt.Number(i + 1) + "/" + Fmt.Number(loose.Count));
                    var response = await GetAsync("recording/" + loose[i] + "?inc=genres&fmt=json", result, ct).ConfigureAwait(false);
                    if (response != null) recordingGenres[loose[i]] = ParseGenres(response);
                }
            }

            // album genres: only where needed (always for "release group", as fallback otherwise)
            var groupOfFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groupGenres = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (needGroup)
            {
                foreach (KeyValuePair<string, MbIds> item in plan.Items)
                {
                    MbIds ids = item.Value;
                    bool hasTrackGenres = ids.RecordingId != null &&
                                          recordingGenres.TryGetValue(ids.RecordingId, out List<string> trackGenres) &&
                                          trackGenres.Count > 0;
                    if (s.GenreSource == GenreSource.RecordingThenReleaseGroup && hasTrackGenres) continue;

                    if (ids.ReleaseGroupId != null) groupOfFile[item.Key] = ids.ReleaseGroupId;
                }

                List<string> groupIds = groupOfFile.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < groupIds.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("fetching album genres … " + Fmt.Number(i + 1) + "/" + Fmt.Number(groupIds.Count));
                    var response = await GetAsync("release-group/" + groupIds[i] + "?inc=genres&fmt=json", result, ct).ConfigureAwait(false);
                    if (response != null) groupGenres[groupIds[i]] = ParseGenres(response);
                }
            }

            // write into the tags
            var changes = new List<string>();
            for (int i = 0; i < plan.Items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 20 == 0) progress?.Report("writing genres … " + Fmt.Number(i + 1) + "/" + Fmt.Number(plan.Items.Count));

                string file = plan.Items[i].Key;
                MbIds ids = plan.Items[i].Value;

                List<string> genres = null;
                if (s.GenreSource != GenreSource.ReleaseGroup && ids.RecordingId != null &&
                    recordingGenres.TryGetValue(ids.RecordingId, out List<string> fromTrack) && fromTrack.Count > 0)
                    genres = fromTrack;
                if (genres == null && s.GenreSource != GenreSource.Recording &&
                    groupOfFile.TryGetValue(file, out string groupId) &&
                    groupGenres.TryGetValue(groupId, out List<string> fromAlbum) && fromAlbum.Count > 0)
                    genres = fromAlbum;

                if (genres == null)
                {
                    result.NoGenres++;
                    continue;
                }

                if (s.GenreCapitalize) genres = genres.Select(Capitalize).ToList();

                string current = library.GetTag(file, field) ?? "";
                string value = s.GenreReplace ? string.Join(GenreFields.Separator, genres) : Merge(current, genres);
                if (NormalizeList(value) == NormalizeList(current))
                {
                    result.Unchanged++;
                    continue;
                }

                if (library.SetTag(file, field, value) && library.Commit(file))
                {
                    result.Written++;
                    string where = PluginLog.FileRef(file);
                    changes.Add((where.Length > 0 ? where + " | " : "") + fieldName + " | \"" + current + "\" → \"" + value + "\"");
                    if (changes.Count >= 100)
                    {
                        PluginLog.Genres(changes);
                        changes.Clear();
                    }
                }
                else
                {
                    result.WriteFailed++;
                    string where = PluginLog.FileRef(file);
                    PluginLog.Error("Could not write the genres" + (where.Length > 0 ? " to " + where : "") + ".");
                }
            }

            PluginLog.Genres(changes);
            library.RefreshPanels();
            PluginLog.Info("Genre import: " + result.Describe());
            return result;
        }

        // ---------- helpers ----------

        /// <summary>Genre names sorted by votes (most voted first).</summary>
        private static List<string> ParseGenres(Dictionary<string, object> entity)
        {
            return Json.Arr(entity, "genres")
                .OfType<Dictionary<string, object>>()
                .Select(g => new { Name = Json.Str(g, "name"), Votes = Json.Number(g, "count") ?? 0 })
                .Where(g => !string.IsNullOrWhiteSpace(g.Name))
                .OrderByDescending(g => g.Votes)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Capitalize(string genre) =>
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(genre);

        private static List<string> SplitList(string value) =>
            (value ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static string NormalizeList(string value) => string.Join(GenreFields.Separator, SplitList(value));

        /// <summary>Keeps the existing genres and adds the missing ones.</summary>
        private static string Merge(string current, List<string> genres)
        {
            List<string> list = SplitList(current);
            foreach (string genre in genres)
                if (!list.Contains(genre, StringComparer.OrdinalIgnoreCase)) list.Add(genre);
            return string.Join(GenreFields.Separator, list);
        }

        private async Task BrowseRecordingGenresAsync(string releaseId, HashSet<string> wanted, Dictionary<string, List<string>> target,
                                                      GenreResult result, CancellationToken ct)
        {
            int offset = 0;
            while (true)
            {
                var data = await GetAsync("recording?release=" + releaseId + "&inc=genres&limit=100&offset=" + offset + "&fmt=json", result, ct).ConfigureAwait(false);
                if (data == null) return;

                int count = 0;
                foreach (object item in Json.Arr(data, "recordings"))
                {
                    count++;
                    var recording = item as Dictionary<string, object>;
                    string id = Json.Str(recording, "id");
                    if (id != null && wanted.Contains(id)) target[id] = ParseGenres(recording);
                }

                offset += count;
                double total = Json.Number(data, "recording-count") ?? 0;
                if (count == 0 || offset >= total) return;
            }
        }

        private async Task<Dictionary<string, object>> GetAsync(string pathAndQuery, GenreResult result, CancellationToken ct)
        {
            var response = await web.GetMusicBrainzAsync(pathAndQuery, ct).ConfigureAwait(false);
            if (!response.Ok)
            {
                result.Failed++;
                PluginLog.Error("MusicBrainz request " + pathAndQuery + " failed (" + WebService.DescribeStatus(response.Status) + ").");
            }
            return response.Data;
        }
    }
}
