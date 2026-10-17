// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MusicBeePlugin
{
    /// <summary>What the panel shows.</summary>
    internal enum PanelContent
    {
        Playing = 0,
        Selected = 1,
        PlayingAndSelected = 2
    }

    /// <summary>
    /// Plugin settings, stored in MbLinks\settings.xml.
    /// Changes always go through a copy (Clone), so background threads never see half-updated values.
    /// </summary>
    internal sealed class PluginSettings
    {
        public static readonly int[] IconSizes = { 16, 20, 24, 32 };

        private readonly string path;
        private Dictionary<string, bool> siteStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Links
        public bool LinksEnabled { get; set; } = true;
        public string WikipediaLanguage { get; set; } = "";   // empty = automatic
        public bool PrefetchUpcoming { get; set; } = true;    // fetch links for the next tracks in the playing list

        // Display
        public PanelContent PanelContent { get; set; } = PanelContent.PlayingAndSelected;
        public int IconSize { get; set; } = 24;
        public bool ContextMenuLinks { get; set; } = true;

        // Genres: only fetched manually via the context menu
        public bool GenresEnabled { get; set; } = true;                      // offer "Get MB genres if available"
        public GenreSource GenreSource { get; set; } = GenreSource.Recording;
        public string GenreTargetField { get; set; } = GenreFields.GenreKey;   // "Genre" or "Custom1" … "Custom16"
        public bool GenreReplace { get; set; } = true;                       // false: keep existing genres, add missing ones
        public bool GenreCapitalize { get; set; } = false;

        // Submitting the user's genres to MusicBrainz: off by default, only manually
        public bool GenreSubmitEnabled { get; set; } = false;
        public bool GenreSubmitRecording { get; set; } = true;
        public bool GenreSubmitReleaseGroup { get; set; } = true;
        public bool GenreSubmitRelease { get; set; } = false;
        public bool GenreSubmitAlbumShared { get; set; } = true;            // albums: only genres shared by all selected tracks
        public string GenreSubmitField { get; set; } = GenreFields.GenreKey;
        public bool GenreSubmitOnlyKnown { get; set; } = true;              // only names MusicBrainz knows as genres
        public GenreListRefresh GenreListRefresh { get; set; } = GenreListRefresh.EverySession;

        // Log
        public LogPathMode LogPaths { get; set; } = LogPathMode.FolderAndFile;

        // Ratings: off until the user switches them on
        public bool RatingsEnabled { get; set; } = false;
        public bool RatingsTracks { get; set; } = true;       // track rating ↔ recording
        public bool RatingsAlbums { get; set; } = true;       // album rating ↔ release group
        public bool AutoUpload { get; set; } = false;
        public bool AutoUploadFutureOnly { get; set; } = true; // automatic submission ignores ratings that already existed
        public bool RoundHalfUp { get; set; } = true;
        public bool ImportOverwrite { get; set; } = false;

        private PluginSettings(string path)
        {
            this.path = path;
        }

        public bool IsSiteEnabled(string key)
        {
            if (siteStates.TryGetValue(key, out bool enabled)) return enabled;
            SiteDefinition site = Sites.Find(key);
            return site != null && site.DefaultEnabled;
        }

        public void SetSiteEnabled(string key, bool enabled)
        {
            siteStates[key] = enabled;
        }

        public PluginSettings Clone()
        {
            var copy = (PluginSettings)MemberwiseClone();
            copy.siteStates = new Dictionary<string, bool>(siteStates, StringComparer.OrdinalIgnoreCase);
            return copy;
        }

        public static PluginSettings Load(string folder)
        {
            var s = new PluginSettings(Path.Combine(folder, "settings.xml"));
            try
            {
                if (!File.Exists(s.path)) return s;
                XElement root = XDocument.Load(s.path).Root;
                if (root == null) return s;

                s.LinksEnabled = ReadBool(root, "LinksEnabled", s.LinksEnabled);
                s.WikipediaLanguage = NormalizeLanguage((string)root.Element("WikipediaLanguage"));
                s.PrefetchUpcoming = ReadBool(root, "PrefetchUpcoming", s.PrefetchUpcoming);

                XElement sites = root.Element("Sites");
                if (sites != null)
                {
                    foreach (XElement site in sites.Elements("Site"))
                    {
                        string key = (string)site.Attribute("key");
                        bool? enabled = ParseBool((string)site.Attribute("enabled"));
                        if (!string.IsNullOrEmpty(key) && enabled.HasValue && Sites.Find(key) != null)
                            s.siteStates[key] = enabled.Value;
                    }
                }

                s.PanelContent = ReadEnum(root, "PanelContent", s.PanelContent);
                s.IconSize = NormalizeIconSize(ReadInt(root, "IconSize", s.IconSize));
                s.ContextMenuLinks = ReadBool(root, "ContextMenuLinks", s.ContextMenuLinks);

                s.GenresEnabled = ReadBool(root, "GenresEnabled", s.GenresEnabled);
                s.GenreSource = ReadEnum(root, "GenreSource", s.GenreSource);
                s.GenreTargetField = GenreFields.Normalize((string)root.Element("GenreTargetField") ?? s.GenreTargetField);
                s.GenreReplace = ReadBool(root, "GenreReplace", s.GenreReplace);
                s.GenreCapitalize = ReadBool(root, "GenreCapitalize", s.GenreCapitalize);

                s.GenreSubmitEnabled = ReadBool(root, "GenreSubmitEnabled", s.GenreSubmitEnabled);
                s.GenreSubmitRecording = ReadBool(root, "GenreSubmitRecording", s.GenreSubmitRecording);
                s.GenreSubmitReleaseGroup = ReadBool(root, "GenreSubmitReleaseGroup", s.GenreSubmitReleaseGroup);
                s.GenreSubmitRelease = ReadBool(root, "GenreSubmitRelease", s.GenreSubmitRelease);
                s.GenreSubmitAlbumShared = ReadBool(root, "GenreSubmitAlbumShared", s.GenreSubmitAlbumShared);
                s.GenreSubmitField = GenreFields.Normalize((string)root.Element("GenreSubmitField") ?? s.GenreSubmitField);
                s.GenreSubmitOnlyKnown = ReadBool(root, "GenreSubmitOnlyKnown", s.GenreSubmitOnlyKnown);
                s.GenreListRefresh = ReadEnum(root, "GenreListRefresh", s.GenreListRefresh);

                s.LogPaths = ReadEnum(root, "LogPaths", s.LogPaths);

                s.RatingsEnabled = ReadBool(root, "RatingsEnabled", s.RatingsEnabled);
                s.RatingsTracks = ReadBool(root, "RatingsTracks", s.RatingsTracks);
                s.RatingsAlbums = ReadBool(root, "RatingsAlbums", s.RatingsAlbums);
                s.AutoUpload = ReadBool(root, "AutoUpload", s.AutoUpload);
                s.AutoUploadFutureOnly = ReadBool(root, "AutoUploadFutureOnly", s.AutoUploadFutureOnly);
                s.RoundHalfUp = ReadBool(root, "RoundHalfUp", s.RoundHalfUp);
                s.ImportOverwrite = ReadBool(root, "ImportOverwrite", s.ImportOverwrite);
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var root = new XElement("MbLinks",
                    new XAttribute("version", ProjectInfo.Version),
                    new XElement("LinksEnabled", LinksEnabled),
                    new XElement("WikipediaLanguage", WikipediaLanguage ?? ""),
                    new XElement("PrefetchUpcoming", PrefetchUpcoming),
                    new XElement("Sites", siteStates.Select(kv =>
                        new XElement("Site", new XAttribute("key", kv.Key), new XAttribute("enabled", kv.Value)))),
                    new XElement("PanelContent", PanelContent.ToString()),
                    new XElement("IconSize", IconSize),
                    new XElement("ContextMenuLinks", ContextMenuLinks),
                    new XElement("GenresEnabled", GenresEnabled),
                    new XElement("GenreSource", GenreSource.ToString()),
                    new XElement("GenreTargetField", GenreTargetField ?? GenreFields.GenreKey),
                    new XElement("GenreReplace", GenreReplace),
                    new XElement("GenreCapitalize", GenreCapitalize),
                    new XElement("GenreSubmitEnabled", GenreSubmitEnabled),
                    new XElement("GenreSubmitRecording", GenreSubmitRecording),
                    new XElement("GenreSubmitReleaseGroup", GenreSubmitReleaseGroup),
                    new XElement("GenreSubmitRelease", GenreSubmitRelease),
                    new XElement("GenreSubmitAlbumShared", GenreSubmitAlbumShared),
                    new XElement("GenreSubmitField", GenreSubmitField ?? GenreFields.GenreKey),
                    new XElement("GenreSubmitOnlyKnown", GenreSubmitOnlyKnown),
                    new XElement("GenreListRefresh", GenreListRefresh.ToString()),
                    new XElement("LogPaths", LogPaths.ToString()),
                    new XElement("RatingsEnabled", RatingsEnabled),
                    new XElement("RatingsTracks", RatingsTracks),
                    new XElement("RatingsAlbums", RatingsAlbums),
                    new XElement("AutoUpload", AutoUpload),
                    new XElement("AutoUploadFutureOnly", AutoUploadFutureOnly),
                    new XElement("RoundHalfUp", RoundHalfUp),
                    new XElement("ImportOverwrite", ImportOverwrite));
                new XDocument(root).Save(path);
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not save settings: " + ex.Message);
            }
        }

        public static string NormalizeLanguage(string input)
        {
            string value = (input ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(value, "^[a-z]{2,8}(-[a-z]{2,8})*$") ? value : "";
        }

        public static int NormalizeIconSize(int size) => IconSizes.Contains(size) ? size : 24;

        private static bool ReadBool(XElement root, string name, bool fallback) =>
            ParseBool((string)root.Element(name)) ?? fallback;

        private static int ReadInt(XElement root, string name, int fallback) =>
            int.TryParse((string)root.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        private static T ReadEnum<T>(XElement root, string name, T fallback) where T : struct =>
            Enum.TryParse((string)root.Element(name), out T value) && Enum.IsDefined(typeof(T), value) ? value : fallback;

        private static bool? ParseBool(string text) =>
            bool.TryParse(text, out bool value) ? value : (bool?)null;
    }

    /// <summary>
    /// The MusicBee API cannot report the user interface language.
    /// So: look for it in MusicBee's settings file first, otherwise use the Windows language.
    /// A language entered in the plugin settings takes precedence.
    /// </summary>
    internal static class LanguageDetector
    {
        public static string Detect(string musicBeeStoragePath, CultureInfo fallbackCulture)
        {
            CultureInfo culture = FromMusicBeeSettings(musicBeeStoragePath) ?? fallbackCulture;
            return ToWikiCode(culture);
        }

        public static string ToWikiCode(CultureInfo culture)
        {
            if (culture == null || string.IsNullOrEmpty(culture.Name)) return "en";
            string code = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            switch (code)
            {
                case "nb": return "no";   // Norwegian (Bokmål) → no.wikipedia.org
                case "iv": return "en";
                default: return code;
            }
        }

        private static CultureInfo FromMusicBeeSettings(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            foreach (string fileName in new[] { "MusicBee3Settings.ini", "MusicBeeSettings.ini" })
            {
                try
                {
                    string path = Path.Combine(folder, fileName);
                    if (!File.Exists(path)) continue;

                    XElement root = XDocument.Load(path).Root;
                    if (root == null) continue;

                    foreach (XElement element in root.Elements())
                    {
                        if (element.HasElements) continue;
                        if (element.Name.LocalName.IndexOf("Language", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        CultureInfo culture = ParseCulture(element.Value);
                        if (culture != null) return culture;
                    }
                }
                catch { }
            }
            return null;
        }

        private static CultureInfo ParseCulture(string value)
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0 || v.Length > 100) return null;

            // "Languages\German.xml" → "German"
            int slash = v.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) v = v.Substring(slash + 1);
            if (v.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 4);

            // language code such as "de" or "de-DE"
            if (Regex.IsMatch(v, "^[A-Za-z]{2,3}([-_][A-Za-z0-9]{2,8})*$"))
            {
                try
                {
                    CultureInfo c = CultureInfo.GetCultureInfo(v.Replace('_', '-'));
                    if (c.LCID != 4096 && !string.IsNullOrEmpty(c.Name)) return c;   // 4096 = unknown culture
                }
                catch (CultureNotFoundException) { }
            }

            // language name such as "German", "Deutsch" or "English (UK)"
            string baseName = v.Split('(')[0].Trim();
            foreach (CultureInfo c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (string.IsNullOrEmpty(c.Name)) continue;
                if (string.Equals(c.EnglishName, baseName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.NativeName, baseName, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }
    }
}
