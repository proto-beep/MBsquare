// MusicBrainz Links – MusicBee plugin
// Note: this code was written with Claude, an AI model by Anthropic. See README.md.

using System;

namespace MusicBeePlugin
{
    /// <summary>Central project information. The version number is maintained only in MbLinks.csproj.</summary>
    internal static class ProjectInfo
    {
        public const string Name = "MusicBrainz Links";
        public const string ShortName = "MbLinks";

        // GitHub repository of the plugin. MusicBrainz also uses it as the contact in the User-Agent;
        // whoever publishes a fork sets their own repository address here.
        public const string RepositoryUrl = "https://github.com/proto-beep/MBsquare";

        public const string AiNotice =
            "The code of this plugin was written with Claude, an AI model by Anthropic.";

        public static bool IsRepositoryConfigured => RepositoryUrl.IndexOf("YOUR-NAME", StringComparison.Ordinal) < 0;

        /// <summary>Version as Major.Minor.Patch, read from the assembly (source: MbLinks.csproj).</summary>
        public static string Version
        {
            get
            {
                System.Version v = typeof(ProjectInfo).Assembly.GetName().Version;
                return v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            }
        }

        /// <summary>User-Agent for all web requests (MusicBrainz requires name, version and contact).</summary>
        public static string UserAgent => ShortName + "/" + Version + " ( " + RepositoryUrl + " )";

        /// <summary>Value of the "client" parameter for submissions to MusicBrainz.</summary>
        public static string SubmissionClient => ShortName + "-" + Version;
    }
}
