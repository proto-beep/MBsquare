// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    /// <summary>How file paths appear in the log.</summary>
    internal enum LogPathMode
    {
        FolderAndFile = 0,   // "Example Album (2004)\01 First Song.flac" (default)
        FullPath = 1,        // "D:\Music\Example Artist\Example Album (2004)\01 First Song.flac"
        None = 2             // no file information at all
    }

    /// <summary>
    /// Log file in MbLinks\logs, one file per month (mblinks-YYYY-MM.log).
    /// Records every rating submitted to MusicBrainz, every genre change in the tags, all errors,
    /// and events such as logins and link jobs.
    /// </summary>
    internal static class PluginLog
    {
        private static readonly object sync = new object();
        private static string folder;
        private static Action<string> trace;

        // drive or network path, followed by one or more "\segment" (characters that are not allowed in Windows names end it)
        private static readonly Regex PathPattern = new Regex(
            @"(?:[A-Za-z]:|\\\\[^\\/:*?""<>|\r\n]+)((?:\\[^\\/:*?""<>|\r\n]+)+)", RegexOptions.Compiled);

        private static readonly Regex AudioFileName = new Regex(
            @"^.*?\.(?:flac|mp3|m4a|m4b|mp4|aac|ogg|oga|opus|wma|wav|aif|aiff|ape|wv|mpc|dsf|dff|tta)(?=$|[^A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Folder => folder;

        /// <summary>How file paths are written; set from the plugin settings.</summary>
        public static LogPathMode PathMode { get; set; } = LogPathMode.FolderAndFile;

        /// <summary>A file as it should appear in the log: folder and file name, full path, or "" if switched off.</summary>
        public static string FileRef(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            switch (PathMode)
            {
                case LogPathMode.FullPath:
                    return path;
                case LogPathMode.None:
                    return "";
                default:
                    try
                    {
                        string name = Path.GetFileName(path);
                        string parent = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
                        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
                    }
                    catch
                    {
                        return "";
                    }
            }
        }

        /// <summary>" | file" for appending to a log line, or "" if file information is switched off.</summary>
        public static string FileColumn(string path)
        {
            string reference = FileRef(path);
            return reference.Length == 0 ? "" : " | " + reference;
        }

        public static void Init(string dataFolder, Action<string> mbTrace)
        {
            folder = Path.Combine(dataFolder, "logs");
            trace = mbTrace;
            try { Directory.CreateDirectory(folder); } catch { }
        }

        public static void Info(string message) => Write("INFO", new[] { message });

        /// <summary>Ratings that MusicBrainz accepted.</summary>
        public static void Export(IEnumerable<string> lines) => Write("EXPORT", lines);

        /// <summary>Genre changes in the tags (file, field, old and new value).</summary>
        public static void Genres(IEnumerable<string> lines) => Write("GENRES", lines);

        /// <summary>Ratings that could not be submitted.</summary>
        public static void ExportFailed(IEnumerable<string> lines) => Write("EXPORT FAILED", lines);

        public static void Error(string message)
        {
            Write("ERROR", new[] { message });
            try { trace?.Invoke(ProjectInfo.Name + ": " + message); } catch { }
        }

        private static void Write(string level, IEnumerable<string> messages)
        {
            if (folder == null || messages == null) return;

            DateTime now = DateTime.Now;
            string stamp = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            foreach (string message in messages)
            {
                sb.Append(stamp).Append(" [").Append(level).Append("] ")
                  .Append(ShortenPaths((message ?? "").Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ')))
                  .AppendLine();
            }
            if (sb.Length == 0) return;

            string file = Path.Combine(folder, "mblinks-" + now.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".log");
            lock (sync)
            {
                try { File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }

        /// <summary>
        /// Safety net for paths inside free text, e.g. in error messages from Windows:
        /// shortened to folder and file name, or removed, according to the setting.
        /// </summary>
        private static string ShortenPaths(string message)
        {
            if (PathMode == LogPathMode.FullPath || string.IsNullOrEmpty(message)) return message;
            return PathPattern.Replace(message, match =>
            {
                string[] segments = match.Groups[1].Value.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0) return "";
                string last = segments[segments.Length - 1];
                if (PathMode == LogPathMode.None) return AudioFileName.Replace(last, "[file]", 1);
                return segments.Length == 1 ? last : segments[segments.Length - 2] + "\\" + last;
            });
        }
    }
}
