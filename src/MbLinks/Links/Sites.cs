// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicBeePlugin
{
    internal sealed class SiteDefinition
    {
        public SiteDefinition(string key, string name, string fallbackText, bool defaultEnabled, params string[] domains)
        {
            Key = key;
            Name = name;
            FallbackText = fallbackText;
            DefaultEnabled = defaultEnabled;
            Domains = domains ?? new string[0];
        }

        public string Key { get; }
        public string Name { get; }
        public string FallbackText { get; }    // abbreviation drawn when no favicon is available
        public bool DefaultEnabled { get; }
        public string[] Domains { get; }

        public bool Matches(Uri uri)
        {
            string host = uri.Host.ToLowerInvariant();
            foreach (string domain in Domains)
                if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)) return true;
            return false;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// All services in panel order: the two MusicBrainz links first, then all other services alphabetically.
    /// External links come from the URL relationships in MusicBrainz and only appear if someone has added them there.
    /// To add a service, add one line to the list below (key, name, abbreviation, enabled by default, domain).
    /// </summary>
    internal static class Sites
    {
        public const string MusicBrainzReleaseGroup = "musicbrainz-releasegroup";
        public const string MusicBrainzRelease = "musicbrainz-release";
        public const string Wikipedia = "wikipedia";
        public const string Discogs = "discogs";

        public static readonly IReadOnlyList<SiteDefinition> All = Build();

        public static SiteDefinition Find(string key) =>
            All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<SiteDefinition> Build()
        {
            var musicBrainz = new[]
            {
                new SiteDefinition(MusicBrainzReleaseGroup, "MusicBrainz – Release Group", "MB", true),
                new SiteDefinition(MusicBrainzRelease, "MusicBrainz – Release", "MB", true),
            };

            // Enabled by default: Discogs, Genius, Rate Your Music, Wikipedia. Everything else only on request.
            var others = new[]
            {
                new SiteDefinition("allmusic", "AllMusic", "AM", false, "allmusic.com"),
                new SiteDefinition(Discogs, "Discogs", "D", true, "discogs.com"),
                new SiteDefinition("genius", "Genius", "G", true, "genius.com"),
                new SiteDefinition("jazzmusicarchives", "Jazz Music Archives", "JMA", false, "jazzmusicarchives.com"),
                new SiteDefinition("metalarchives", "Metal Archives (Encyclopaedia Metallum)", "MA", false, "metal-archives.com"),
                new SiteDefinition("metalmusicarchives", "Metal Music Archives", "MMA", false, "metalmusicarchives.com"),
                new SiteDefinition("musiksammler", "Musik-Sammler", "MS", false, "musik-sammler.de"),
                new SiteDefinition("progarchives", "ProgArchives", "PA", false, "progarchives.com"),
                new SiteDefinition("rateyourmusic", "Rate Your Music", "RYM", true, "rateyourmusic.com"),
                new SiteDefinition("secondhandsongs", "SecondHandSongs", "SHS", false, "secondhandsongs.com"),
                new SiteDefinition("vgmdb", "VGMdb", "VG", false, "vgmdb.net"),
                new SiteDefinition(Wikipedia, "Wikipedia (or Wikidata)", "W", true),
            };

            return musicBrainz
                .Concat(others.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
