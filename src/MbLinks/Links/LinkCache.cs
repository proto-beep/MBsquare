// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    /// <summary>One URL relationship from MusicBrainz.</summary>
    public sealed class StoredUrl
    {
        public string Url { get; set; }
        public bool ReleaseGroupLevel { get; set; }
    }

    /// <summary>Everything the plugin knows about one album. One line in links.jsonl.</summary>
    public sealed class AlbumLinks
    {
        public string Key { get; set; }                 // release ID and release group ID from the tags
        public string ReleaseId { get; set; }           // from the tags
        public string ReleaseGroupId { get; set; }      // current release group according to MusicBrainz (or from the tags)
        public bool ReleaseExists { get; set; }
        public bool ReleaseGroupExists { get; set; }
        public List<StoredUrl> Urls { get; set; } = new List<StoredUrl>();
        public Dictionary<string, string> Wikipedia { get; set; } = new Dictionary<string, string>();   // language → article URL, "" = no article
        public DateTime CheckedUtc { get; set; }
    }

    /// <summary>
    /// Keeps the links of every album permanently, until the user checks them again.
    /// Storage: links.jsonl with one JSON line per change. New lines are appended (cheap even for very large
    /// libraries); when loading, the last line per album wins. The file is compacted when it contains many
    /// outdated lines. Loading runs in the background so MusicBee starts without delay.
    /// </summary>
    internal sealed class LinkCache : IDisposable
    {
        private readonly string path;
        private readonly string legacyPath;
        private readonly object sync = new object();
        private readonly object writeLock = new object();
        private readonly Dictionary<string, AlbumLinks> entries = new Dictionary<string, AlbumLinks>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> pendingLines = new List<string>();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly Task loadTask;
        private Timer flushTimer;
        private int fileLines;

        public LinkCache(string folder)
        {
            path = Path.Combine(folder, "links.jsonl");
            legacyPath = Path.Combine(folder, "links.json");   // format of versions 0.3 and 0.4
            loadTask = Task.Run(() => Load());
        }

        public bool IsLoaded => loadTask.IsCompleted;
        public Task WhenLoaded => loadTask;

        public int Count
        {
            get
            {
                if (!IsLoaded) return 0;
                lock (sync) return entries.Count;
            }
        }

        /// <summary>The stored album, or null (also while the store is still loading).</summary>
        public AlbumLinks Get(string key)
        {
            if (!IsLoaded) return null;
            lock (sync) return entries.TryGetValue(key, out AlbumLinks entry) ? entry : null;
        }

        public List<string> Keys()
        {
            loadTask.Wait();
            lock (sync) return entries.Keys.ToList();
        }

        /// <summary>All stored albums, or null while the store is still loading.</summary>
        public List<AlbumLinks> Snapshot()
        {
            if (!IsLoaded) return null;
            lock (sync) return entries.Values.ToList();
        }

        public void Put(AlbumLinks entry)
        {
            loadTask.Wait();
            lock (sync)
            {
                entries[entry.Key] = entry;
                pendingLines.Add(serializer.Serialize(entry));
                ScheduleFlush();
            }
        }

        /// <summary>Stored Wikipedia URL for a language: null = not looked up yet, "" = no article.</summary>
        public string GetWikipedia(AlbumLinks entry, string language)
        {
            lock (sync)
                return entry.Wikipedia != null && entry.Wikipedia.TryGetValue(language, out string url) ? url : null;
        }

        public void SetWikipedia(AlbumLinks entry, string language, string url)
        {
            loadTask.Wait();
            lock (sync)
            {
                if (entry.Wikipedia == null) entry.Wikipedia = new Dictionary<string, string>();
                entry.Wikipedia[language] = url ?? "";
                if (entries.TryGetValue(entry.Key, out AlbumLinks stored) && ReferenceEquals(stored, entry))
                {
                    pendingLines.Add(serializer.Serialize(entry));
                    ScheduleFlush();
                }
            }
        }

        public Dictionary<string, string> WikipediaSnapshot(AlbumLinks entry)
        {
            lock (sync)
                return entry.Wikipedia == null ? new Dictionary<string, string>() : new Dictionary<string, string>(entry.Wikipedia);
        }

        /// <summary>Appends pending changes to the file.</summary>
        public void Flush()
        {
            List<string> lines;
            lock (sync)
            {
                flushTimer?.Dispose();
                flushTimer = null;
                if (pendingLines.Count == 0) return;
                lines = new List<string>(pendingLines);
                pendingLines.Clear();
            }

            lock (writeLock)
            {
                try
                {
                    File.AppendAllLines(path, lines, new UTF8Encoding(false));
                    fileLines += lines.Count;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Could not write the link store: " + ex.Message);
                }
            }
        }

        /// <summary>Rewrites the file with exactly one line per album, if it contains many outdated lines.</summary>
        public void CompactIfNeeded()
        {
            loadTask.Wait();
            bool needed;
            lock (sync) needed = fileLines > entries.Count * 3 / 2 + 100;
            if (needed) Compact();
        }

        public void Dispose()
        {
            Flush();
        }

        // called while holding sync
        private void ScheduleFlush()
        {
            if (flushTimer == null)
                flushTimer = new Timer(_ => Flush(), null, 3000, Timeout.Infinite);
        }

        private void Compact()
        {
            List<string> lines;
            lock (sync)
            {
                lines = entries.Values.Select(e => serializer.Serialize(e)).ToList();
                pendingLines.Clear();   // all pending changes are part of the snapshot
                flushTimer?.Dispose();
                flushTimer = null;
            }

            lock (writeLock)
            {
                try
                {
                    string temp = path + ".tmp";
                    File.WriteAllLines(temp, lines, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    fileLines = lines.Count;
                }
                catch (Exception ex)
                {
                    PluginLog.Error("Could not compact the link store: " + ex.Message);
                }
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(path))
                {
                    int damaged = 0;
                    foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        fileLines++;
                        AlbumLinks entry = null;
                        try { entry = serializer.Deserialize<AlbumLinks>(line); }
                        catch { damaged++; }
                        if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;
                        Normalize(entry);
                        lock (sync) entries[entry.Key] = entry;
                    }
                    if (damaged > 0) PluginLog.Error("The link store contained " + damaged + " damaged lines; they were skipped.");
                    if (fileLines > entries.Count * 3 / 2 + 100) Compact();
                }
                else if (File.Exists(legacyPath))
                {
                    var list = serializer.Deserialize<List<AlbumLinks>>(File.ReadAllText(legacyPath, Encoding.UTF8));
                    if (list != null)
                    {
                        foreach (AlbumLinks entry in list)
                        {
                            if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;
                            Normalize(entry);
                            lock (sync) entries[entry.Key] = entry;
                        }
                    }
                    Compact();
                    File.Delete(legacyPath);
                    PluginLog.Info("Converted links.json to links.jsonl (" + entries.Count + " albums).");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Could not load the link store: " + ex.Message);
            }
        }

        private static void Normalize(AlbumLinks entry)
        {
            if (entry.Urls == null) entry.Urls = new List<StoredUrl>();
            if (entry.Wikipedia == null) entry.Wikipedia = new Dictionary<string, string>();
        }
    }
}
