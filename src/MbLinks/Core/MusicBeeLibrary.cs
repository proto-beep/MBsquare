// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicBeePlugin
{
    /// <summary>Access to MusicBee's library, playlists, playing list, rating fields and status bar.</summary>
    internal sealed class MusicBeeLibrary
    {
        private static readonly Plugin.MetaDataType[] RatingFields =
        {
            Plugin.MetaDataType.Rating,
            Plugin.MetaDataType.RatingAlbum,
            Plugin.MetaDataType.Album
        };

        private readonly Plugin.MusicBeeApiInterface api;

        public MusicBeeLibrary(Plugin.MusicBeeApiInterface api)
        {
            this.api = api;
        }

        // ---------- ratings ----------

        public string GetTrackRating(string file) => Safe(() => api.Library_GetFileTag(file, Plugin.MetaDataType.Rating));
        public string GetAlbumRating(string file) => Safe(() => api.Library_GetFileTag(file, Plugin.MetaDataType.RatingAlbum));
        public string GetAlbum(string file) => Safe(() => api.Library_GetFileTag(file, Plugin.MetaDataType.Album)) ?? "";

        public bool SetTrackRating(string file, string value) => Safe(() => api.Library_SetFileTag(file, Plugin.MetaDataType.Rating, value));
        public bool SetAlbumRating(string file, string value) => Safe(() => api.Library_SetFileTag(file, Plugin.MetaDataType.RatingAlbum, value));
        public bool Commit(string file) => Safe(() => api.Library_CommitTagsToFile(file));

        public string GetTag(string file, Plugin.MetaDataType field) => Safe(() => api.Library_GetFileTag(file, field));
        public bool SetTag(string file, Plugin.MetaDataType field, string value) => Safe(() => api.Library_SetFileTag(file, field, value));

        /// <summary>The name MusicBee shows for a field, e.g. the name the user gave a custom tag.</summary>
        public string GetFieldName(Plugin.MetaDataType field) => Safe(() => api.Setting_GetFieldName(field));

        /// <summary>Track rating, album rating and album name in one call (falls back to single calls).</summary>
        public string[] GetRatingTags(string file)
        {
            try
            {
                if (api.Library_GetFileTags(file, RatingFields, out string[] results) && results != null && results.Length >= 3)
                    return results;
            }
            catch { }
            return new[] { GetTrackRating(file), GetAlbumRating(file), GetAlbum(file) };
        }

        // ---------- track lists ----------

        public string[] Query(string query)
        {
            try
            {
                return api.Library_QueryFilesEx(query, out string[] files) && files != null ? files : new string[0];
            }
            catch
            {
                return new string[0];
            }
        }

        /// <summary>The tracks currently selected in MusicBee.</summary>
        public string[] GetSelectedFiles() => Query("domain=SelectedFiles");

        /// <summary>The tracks currently shown in the main panel (current view, filter or search).</summary>
        public string[] GetDisplayedFiles() => Query("domain=DisplayedFiles");

        /// <summary>All tracks of the library currently open in MusicBee.</summary>
        public string[] GetLibraryFiles()
        {
            string[] queries =
            {
                null,
                "domain=Library",
                "<Source Type=\"1\"><Conditions CombineMethod=\"All\" /></Source>"
            };
            foreach (string query in queries)
            {
                string[] files = Query(query);
                if (files.Length > 0) return files;
            }
            return new string[0];
        }

        /// <summary>All playlists as (address, name), sorted by name.</summary>
        public IList<KeyValuePair<string, string>> GetPlaylists()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                if (!api.Playlist_QueryPlaylists()) return list;
                for (int guard = 0; guard < 100000; guard++)
                {
                    string url = api.Playlist_QueryGetNextPlaylist();
                    if (string.IsNullOrEmpty(url)) break;
                    string name = Safe(() => api.Playlist_GetName(url));
                    if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(url);
                    list.Add(new KeyValuePair<string, string>(url, name));
                }
            }
            catch { }
            return list.OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public string[] GetPlaylistFiles(string playlistUrl)
        {
            try
            {
                return api.Playlist_QueryFilesEx(playlistUrl, out string[] files) && files != null ? files : new string[0];
            }
            catch
            {
                return new string[0];
            }
        }

        /// <summary>The next tracks in the playing list, in play order (respects shuffle).</summary>
        public List<string> GetUpcomingFiles(int count)
        {
            var files = new List<string>();
            try
            {
                for (int offset = 1; offset <= count; offset++)
                {
                    int index = api.NowPlayingList_GetNextIndex(offset);
                    if (index < 0) break;
                    string file = api.NowPlayingList_GetListFileUrl(index);
                    if (string.IsNullOrEmpty(file)) break;
                    files.Add(file);
                }
            }
            catch { }
            return files;
        }

        // ---------- MusicBee window ----------

        /// <summary>Text in MusicBee's status bar at the bottom; an empty text clears the message.</summary>
        public void SetStatus(string message)
        {
            try { api.MB_SetBackgroundTaskMessage(message ?? ""); } catch { }
        }

        public void RefreshPanels()
        {
            try { api.MB_RefreshPanels(); } catch { }
        }

        private static T Safe<T>(Func<T> call)
        {
            try { return call(); } catch { return default(T); }
        }
    }
}
