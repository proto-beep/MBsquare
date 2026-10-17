// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    internal sealed class MbIds
    {
        public string ReleaseGroupId { get; set; }
        public string ReleaseId { get; set; }
        public string RecordingId { get; set; }

        public bool HasAlbumIds => ReleaseGroupId != null || ReleaseId != null;

        /// <summary>Key of the album in the link store: release ID and release group ID from the tags.</summary>
        public string CacheKey => (ReleaseId ?? "-") + "|" + (ReleaseGroupId ?? "-");
    }

    /// <summary>
    /// Reads MusicBrainz IDs directly from the file, because the MusicBee API does not provide these fields.
    /// Supports the Picard naming convention, which other taggers follow as well, plus common spelling variants.
    /// </summary>
    internal static class MbidReader
    {
        private static readonly Regex MbidPattern = new Regex(
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // normalised keys: lower case, letters and digits only, without the "musicbrainz" prefix
        private const string KeyReleaseGroup = "releasegroupid";
        private const string KeyRelease = "albumid";
        private const string KeyReleaseAlt = "releaseid";
        private const string KeyRecordingUfid = "#ufid";
        private const string KeyRecording = "recordingid";
        private const string KeyTrack = "trackid";   // in Picard's convention this is the recording ID (except ID3, which uses UFID)

        public static MbIds Read(string path, Action<string> log)
        {
            var ids = new MbIds();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ids;

            var values = new Dictionary<string, string>();
            try
            {
                ReadTags(path, values);
            }
            catch (Exception ex)
            {
                string where = PluginLog.FileRef(path);
                log?.Invoke("Could not read tags" + (where.Length > 0 ? " (" + where + ")" : "") + ": " + ex.Message);
                return ids;
            }

            ids.ReleaseGroupId = First(values, KeyReleaseGroup);
            ids.ReleaseId = First(values, KeyRelease, KeyReleaseAlt);
            ids.RecordingId = First(values, KeyRecordingUfid, KeyRecording, KeyTrack);
            return ids;
        }

        // separate method, so TagLib is only loaded here and a missing DLL can be caught above
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadTags(string path, Dictionary<string, string> values)
        {
            using (TagLib.File file = TagLib.File.Create(new ReadOnlyFileAbstraction(path), TagLib.ReadStyle.None))
            {
                // ID3v2 (MP3, AIFF, WAV, DSF …): all TXXX frames, plus UFID for the recording ID
                if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
                {
                    foreach (TagLib.Id3v2.UserTextInformationFrame frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                        Add(values, Normalize(frame.Description), frame.Text);

                    foreach (TagLib.Id3v2.UniqueFileIdentifierFrame frame in id3.GetFrames<TagLib.Id3v2.UniqueFileIdentifierFrame>())
                    {
                        if (frame.Owner != null && frame.Identifier != null &&
                            frame.Owner.IndexOf("musicbrainz.org", StringComparison.OrdinalIgnoreCase) >= 0)
                            Add(values, KeyRecordingUfid, Encoding.ASCII.GetString(frame.Identifier.Data));
                    }
                }

                // Vorbis comments (FLAC, Ogg Vorbis, Opus)
                if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
                {
                    Add(values, KeyReleaseGroup, xiph.GetField("MUSICBRAINZ_RELEASEGROUPID"));
                    Add(values, KeyRelease, xiph.GetField("MUSICBRAINZ_ALBUMID"));
                    Add(values, KeyTrack, xiph.GetField("MUSICBRAINZ_TRACKID"));
                }

                // APEv2 (Monkey's Audio, WavPack, Musepack)
                if (file.GetTag(TagLib.TagTypes.Ape, false) is TagLib.Ape.Tag ape)
                {
                    Add(values, KeyReleaseGroup, ape.GetItem("MUSICBRAINZ_RELEASEGROUPID")?.ToString());
                    Add(values, KeyRelease, ape.GetItem("MUSICBRAINZ_ALBUMID")?.ToString());
                    Add(values, KeyTrack, ape.GetItem("MUSICBRAINZ_TRACKID")?.ToString());
                }

                // MP4/M4A (iTunes freeform atoms)
                if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
                {
                    Add(values, KeyReleaseGroup, apple.GetDashBox("com.apple.iTunes", "MusicBrainz Release Group Id"));
                    Add(values, KeyRelease, apple.GetDashBox("com.apple.iTunes", "MusicBrainz Album Id"));
                    Add(values, KeyTrack, apple.GetDashBox("com.apple.iTunes", "MusicBrainz Track Id"));
                }

                // WMA/ASF
                if (file.GetTag(TagLib.TagTypes.Asf, false) is TagLib.Asf.Tag asf)
                {
                    Add(values, KeyReleaseGroup, asf.GetDescriptorString("MusicBrainz/Release Group Id"));
                    Add(values, KeyRelease, asf.GetDescriptorString("MusicBrainz/Album Id"));
                    Add(values, KeyTrack, asf.GetDescriptorString("MusicBrainz/Track Id"));
                }

                // fallback: TagLib's cross-format fields
                Add(values, KeyRelease, file.Tag.MusicBrainzReleaseId);
                Add(values, KeyTrack, file.Tag.MusicBrainzTrackId);
            }
        }

        /// <summary>"MusicBrainz Release Group Id" and "MUSICBRAINZ_RELEASEGROUPID" → "releasegroupid"</summary>
        private static string Normalize(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            var sb = new StringBuilder(description.Length);
            foreach (char c in description.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);

            string s = sb.ToString();
            const string prefix = "musicbrainz";
            return s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : null;
        }

        private static void Add(Dictionary<string, string> values, string key, params string[] candidates)
        {
            if (key == null || candidates == null || values.ContainsKey(key)) return;
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                Match match = MbidPattern.Match(candidate);   // with multiple values, the first valid ID wins
                if (match.Success)
                {
                    values[key] = match.Value.ToLowerInvariant();
                    return;
                }
            }
        }

        private static string First(Dictionary<string, string> values, params string[] keys)
        {
            foreach (string key in keys)
                if (values.TryGetValue(key, out string value)) return value;
            return null;
        }

        /// <summary>Opens the file read-only without blocking MusicBee.</summary>
        private sealed class ReadOnlyFileAbstraction : TagLib.File.IFileAbstraction
        {
            public ReadOnlyFileAbstraction(string path) { Name = path; }

            public string Name { get; }

            public Stream ReadStream =>
                new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            public Stream WriteStream => throw new NotSupportedException("Read-only access.");

            public void CloseStream(Stream stream) { stream?.Dispose(); }
        }
    }
}
