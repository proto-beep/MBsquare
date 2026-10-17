// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MusicBeePlugin
{
    internal enum AnalysisScope
    {
        Library,
        Displayed,
        Selected,
        Playlist
    }

    internal static class AnalysisScopes
    {
        public static string Describe(AnalysisScope scope, string playlistName)
        {
            switch (scope)
            {
                case AnalysisScope.Displayed: return "the tracks shown in the main panel";
                case AnalysisScope.Selected: return "the selected tracks";
                case AnalysisScope.Playlist: return "the playlist \"" + playlistName + "\"";
                default: return "the entire library";
            }
        }
    }

    /// <summary>Number and duration formatting for messages.</summary>
    internal static class Fmt
    {
        public static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>"1 album", "2 albums".</summary>
        public static string Count(int n, string one, string many) => Number(n) + " " + (n == 1 ? one : many);

        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "less than a minute";
            if (t.TotalHours < 1)
            {
                int minutes = (int)Math.Ceiling(t.TotalMinutes);
                return "about " + minutes + (minutes == 1 ? " minute" : " minutes");
            }
            if (t.TotalDays < 1)
                return "about " + t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " hours";

            int days = (int)t.TotalDays;
            int hours = (int)Math.Round(t.TotalHours - days * 24);
            if (hours == 24) { days++; hours = 0; }
            return "about " + days + (days == 1 ? " day" : " days")
                 + (hours > 0 ? " and " + hours + (hours == 1 ? " hour" : " hours") : "");
        }

        /// <summary>Remaining time for a number of MusicBrainz requests, e.g. "about 5 minutes left".</summary>
        public static string Remaining(int requests) =>
            Duration(TimeSpan.FromSeconds(Math.Max(0, requests) * WebService.SecondsPerMusicBrainzRequest)) + " left";
    }

    /// <summary>Result of preparing an analysis: what is in scope and what would be fetched.</summary>
    internal sealed class AnalysisPlan
    {
        public string ScopeName { get; set; }
        public int Tracks { get; set; }
        public int AlbumFolders { get; set; }
        public int FoldersWithoutIds { get; set; }
        public int Albums { get; set; }
        public int AlreadyStored { get; set; }
        public bool IncludesWikipedia { get; set; }
        public bool IncludesStored { get; set; }   // stored albums are fetched again (manual fetch from the context menu)
        public List<MbIds> ToFetch { get; } = new List<MbIds>();

        public TimeSpan EstimatedDuration =>
            TimeSpan.FromSeconds(ToFetch.Count * WebService.SecondsPerMusicBrainzRequest);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine(IncludesStored ? "Fetch the links for " + ScopeName + " now?" : "Analyze " + ScopeName + "?");
            sb.AppendLine();
            AppendCounts(sb);
            sb.AppendLine();
            sb.AppendLine("Requests to MusicBrainz: about " + Fmt.Number(ToFetch.Count) + " (one per album, at most about one per second)");
            if (IncludesWikipedia)
                sb.AppendLine("Requests to Wikidata: up to " + Fmt.Number(ToFetch.Count) + " (only for albums with a Wikidata item; they run in the waiting time between MusicBrainz requests)");
            sb.AppendLine("Estimated time: " + Fmt.Duration(EstimatedDuration));
            sb.AppendLine();
            sb.AppendLine("The analysis runs in the background and you can keep using MusicBee. Progress and the result are shown in the status bar; " +
                          "the analysis can be stopped in the settings. Everything fetched up to then stays stored. If it is stopped or MusicBee is closed, " +
                          "running the analysis again continues with the remaining albums.");
            sb.AppendLine();
            sb.Append("Do you really want to start the analysis now?");
            return sb.ToString();
        }

        public string DescribeNothingToDo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("There is nothing to fetch for " + ScopeName + ".");
            sb.AppendLine();
            AppendCounts(sb);
            return sb.ToString();
        }

        private void AppendCounts(StringBuilder sb)
        {
            sb.AppendLine("Tracks: " + Fmt.Number(Tracks));
            sb.AppendLine("Albums with MusicBrainz IDs: " + Fmt.Number(Albums));
            sb.AppendLine(IncludesStored
                ? "   already stored (fetched again to update them): " + Fmt.Number(AlreadyStored)
                : "   already stored (skipped): " + Fmt.Number(AlreadyStored));
            sb.AppendLine("   to fetch: " + Fmt.Number(ToFetch.Count));
            if (FoldersWithoutIds > 0)
                sb.AppendLine("Albums without MusicBrainz IDs (skipped): " + Fmt.Number(FoldersWithoutIds));
        }
    }

    internal sealed class AnalysisResult
    {
        public int Total;
        public int Fetched;
        public int Updated;
        public int Failed;
        public int AlreadyStored;

        public string Describe()
        {
            string text = "links fetched for " + Fmt.Number(Fetched) + (Fetched == 1 ? " album" : " albums");
            if (Updated > 0) text += ", updated for " + Fmt.Number(Updated) + (Updated == 1 ? " album" : " albums");
            if (Failed > 0) text += ", " + Fmt.Number(Failed) + " failed (see the log; they are tried again next time)";
            if (AlreadyStored > 0) text += ", " + Fmt.Number(AlreadyStored) + " had been stored in the meantime";
            return text + ".";
        }
    }

    /// <summary>Existing ratings that would be submitted when automatic submission includes existing ratings.</summary>
    internal sealed class RatingSummary
    {
        public string[] Files { get; set; } = new string[0];
        public int RatedTracks { get; set; }
        public int RatedAlbums { get; set; }

        public int Submissions => (int)Math.Ceiling((RatedTracks + RatedAlbums) / 100.0);

        // reading the IDs from the files (about 10 ms per file) plus the submissions
        public TimeSpan EstimatedDuration =>
            TimeSpan.FromSeconds(Files.Length * 0.01 + Submissions * WebService.SecondsPerMusicBrainzRequest);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Submit all existing ratings of the library to MusicBrainz now?");
            sb.AppendLine();
            sb.AppendLine("Rated tracks: " + Fmt.Number(RatedTracks));
            sb.AppendLine("Rated albums: " + Fmt.Number(RatedAlbums));
            sb.AppendLine("Files to read (for the MusicBrainz IDs): " + Fmt.Number(Files.Length));
            sb.AppendLine("Only tracks with a MusicBrainz recording ID and albums with a release group ID in the files are submitted; all others are skipped.");
            sb.AppendLine("Requests to MusicBrainz: at most " + Fmt.Number(Submissions) + " (up to 100 ratings each)");
            sb.AppendLine("Estimated time: " + Fmt.Duration(EstimatedDuration));
            sb.AppendLine();
            sb.AppendLine("Existing ratings on MusicBrainz are overwritten with the values from MusicBee. Every submitted rating is written to the log file. " +
                          "If you choose No, automatic submission stays on for future ratings only.");
            sb.AppendLine();
            sb.Append("Do you really want to submit them now?");
            return sb.ToString();
        }
    }

    internal static class AnalysisPlanner
    {
        /// <summary>
        /// Groups the tracks into albums (folder + album name, from MusicBee's database, fast), reads the MusicBrainz IDs
        /// from one file per album and compares them with the link store.
        /// </summary>
        /// <param name="includeStored">true: stored albums are fetched again (to update them).</param>
        public static AnalysisPlan Prepare(string[] files, MusicBeeLibrary library, LinkService links, bool includeWikipedia,
                                           bool includeStored, IProgress<string> progress, CancellationToken ct)
        {
            var plan = new AnalysisPlan { Tracks = files.Length, IncludesWikipedia = includeWikipedia, IncludesStored = includeStored };

            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                if (i % 2000 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Grouping tracks into albums (from MusicBee's database, no requests) … " +
                                     Fmt.Number(i) + " / " + Fmt.Number(files.Length) + " tracks");
                }
                string file = files[i];
                if (string.IsNullOrEmpty(file) || file.Contains("://")) continue;   // streams have no tags to read
                string key = GroupKey(file, library.GetAlbum(file));
                if (!groups.ContainsKey(key)) groups[key] = file;
            }
            plan.AlbumFolders = groups.Count;

            progress?.Report("Loading the stored links …");
            links.WaitUntilLoaded();

            var albums = new Dictionary<string, MbIds>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (string file in groups.Values)
            {
                if (n % 50 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Reading the MusicBrainz IDs from one file per album … " + Fmt.Number(n) + " / " + Fmt.Number(groups.Count) + " albums");
                }
                n++;

                MbIds ids = MbidReader.Read(file, null);
                if (!ids.HasAlbumIds)
                {
                    plan.FoldersWithoutIds++;
                    continue;
                }
                if (!albums.ContainsKey(ids.CacheKey)) albums[ids.CacheKey] = ids;
            }

            plan.Albums = albums.Count;
            foreach (MbIds ids in albums.Values)
            {
                bool stored = links.IsStored(ids.CacheKey);
                if (stored) plan.AlreadyStored++;
                if (!stored || includeStored) plan.ToFetch.Add(ids);
            }
            return plan;
        }

        /// <summary>Finds all rated tracks and albums (fast, from MusicBee's database).</summary>
        public static RatingSummary CountRatings(string[] files, MusicBeeLibrary library, PluginSettings s,
                                                 IProgress<string> progress, CancellationToken ct)
        {
            var summary = new RatingSummary();
            var selected = new List<string>();
            var ratedAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                if (i % 2000 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Counting ratings … " + Fmt.Number(i) + " / " + Fmt.Number(files.Length) + " tracks");
                }
                string file = files[i];
                if (string.IsNullOrEmpty(file) || file.Contains("://")) continue;

                string[] tags = library.GetRatingTags(file);   // track rating, album rating, album name
                bool trackRated = s.RatingsTracks && RatingService.ToMusicBrainz(RatingService.ParseStars(tags[0]), s.RoundHalfUp) > 0;
                bool albumRated = s.RatingsAlbums && RatingService.ToMusicBrainz(RatingService.ParseStars(tags[1]), s.RoundHalfUp) > 0;
                bool newAlbum = albumRated && ratedAlbums.Add(GroupKey(file, tags[2]));

                if (trackRated) summary.RatedTracks++;
                if (newAlbum) summary.RatedAlbums++;
                if (trackRated || newAlbum) selected.Add(file);
            }

            summary.Files = selected.ToArray();
            return summary;
        }

        public static string GroupKey(string file, string album)
        {
            string folder = "";
            try { folder = Path.GetDirectoryName(file) ?? ""; } catch { }
            return folder + "|" + (album ?? "");
        }
    }
}
