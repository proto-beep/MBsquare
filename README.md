# MBsquare – MusicBrainz Links for MusicBee

> [!NOTE]
> **This code was written by an AI.** The entire source code of this plugin, including the build workflow, was created with Claude, an AI model by Anthropic. The code is compiled automatically on GitHub, but there are no automated tests. Please back up your MusicBee library before importing ratings for many tracks at once.

**Current version: 0.12.2** · Changes: see [CHANGELOG.md](CHANGELOG.md)

[![Build](https://github.com/proto-beep/MBsquare/actions/workflows/build.yml/badge.svg)](https://github.com/proto-beep/MBsquare/actions/workflows/build.yml)

A plugin for [MusicBee](https://getmusicbee.com), developed in the repository MBsquare. In MusicBee it appears as "MusicBrainz Links" (file `mb_MbLinks.dll`). It does four things:

- It shows clickable icons for MusicBrainz, Wikipedia and other music databases: in a panel for the playing album and the album selected in the library, and in the context menu of tracks and albums. A left click opens the page in your default browser.
- On request, it writes the genres from MusicBrainz into the tags of selected tracks.
- Optionally and only on request, it submits your own genres to MusicBrainz as your votes. This is **off by default**.
- Optionally, it syncs your own ratings between MusicBee and MusicBrainz in both directions. This is **off by default** and only active once you switch it on.

## Only files with MusicBrainz IDs

Everything the plugin does is based solely on the MusicBrainz IDs stored in the files. It never searches by artist, album or title, and it never derives a missing ID for writing operations.

| Function | Needed in the file |
| --- | --- |
| Links | release ID or release group ID |
| Track rating, track genres, submitting genres for recordings | recording ID |
| Album rating, album genres, submitting genres for release groups | release group ID |
| Submitting genres for releases | release ID |

Tracks without the relevant ID are skipped and counted as such in the result. If a single selected track has no relevant ID, the corresponding entries in the context menu are disabled.

## Features

### Links

An icon only appears if the link actually exists. Without MusicBrainz IDs in the file, or without any links, nothing is shown.

The two MusicBrainz icons lead to the release group and to the release. They are marked with a blue **RG** and an orange **R** badge. Wikipedia appears in MusicBee's language; if there is no article in that language, Wikidata is linked instead.

Links to other services come from the URL relationships in MusicBrainz. An icon therefore only appears if someone has added the link to the album there. Links at release group level take precedence over links at release level.

### When links are fetched

Links are only fetched from MusicBrainz:

- when an album is played,
- when it comes up in the playing list (the next 50 tracks; can be switched off),
- when you right-click tracks or albums → MusicBrainz → "Fetch links now" (this also updates albums that are already stored),
- when you explicitly analyze tracks (entire library, the tracks shown in the main panel, the selected tracks, or a playlist).

Each album needs exactly one request to MusicBrainz, no matter how many tracks it has. The plugin reads the release ID from one file of the album and gets the release, its release group and the links of both in that single request. The only step that looks at every track is grouping tracks into albums before an analysis; it uses MusicBee's own database, reads no files and sends no requests. The links are then stored permanently and are only fetched again when you fetch them manually or check all stored links in the settings.

Before an analysis starts, the plugin determines the tracks and albums involved and shows a confirmation with the number of tracks, albums, already stored albums, requests and the estimated time. Nothing is fetched before you confirm. The analysis runs in the background, shows its progress in the status bar, and can be stopped; running it again continues with the remaining albums.

### Where links are shown

| Place | Default | Notes |
| --- | --- | --- |
| Panel | playing and selected album | Placed via MusicBee's Arrange Panels, e.g. directly below the Now Playing panel. Two rows: ▶ playing album, ≡ album selected in the library. Icons run from left to right. Content and icon size (16–32 px) are configurable. |
| Context menu | on | Right-click tracks or albums in any view → MusicBrainz → links of the selected album, the date they were checked, "Fetch links now" and, if enabled, the rating commands. |

MusicBee does not allow plugins to place controls inside its own views, such as the playing track display or the album view. The panel and the context menu are the places MusicBee offers to plugins. In the right sidebar, the panel can be placed above the Playing Tracks list or between Playing Tracks and Track Information. For the selected album, only stored links are shown; nothing is fetched just by selecting.

"Fetch links now" in the context menu works immediately for up to 10 albums. For larger selections a confirmation shows the numbers and the estimated time first.

### Genres from MusicBrainz (only on request)

Select tracks or albums, right-click → MusicBrainz → **Get MB genres if available**. Genres are never fetched or written in any other way.

Before anything is written, a confirmation shows the number of tracks (with and without MusicBrainz IDs), the source, the number of requests and the estimated time. The genres are then written in the background; the result appears in the status bar, and every change is written to the log file with the old and the new value.

| Setting | Default | Options |
| --- | --- | --- |
| Source | recording (each track's own genres) | release group (the album's genres for all its tracks), or recording with the release group as fallback |
| Field | MusicBee's genre field | one of MusicBee's custom tags, e.g. one you name "MB Genres" |
| Existing values | replaced | keep and add missing genres |
| Capitalization | as on MusicBrainz (lower case) | capitalize each word |

Several genres are separated by `; ` and sorted by the number of votes on MusicBrainz. If MusicBrainz has no genres for a track, its field is left unchanged. MusicBrainz has noticeably more genres on release groups than on recordings; if many tracks stay unchanged, the fallback source helps.

Requests: one per album for track genres (up to 100 tracks), one per release group for album genres.

For an own field "MB Genres": in MusicBee's preferences under Tags, give one of the custom tags the name "MB Genres", then choose it in the plugin's Genres tab.

Genres from Rate Your Music are not supported: Rate Your Music has no public API and its terms of service forbid automated access.

### Submitting your genres to MusicBrainz (optional, only on request)

Off by default; switch it on in the Genres tab. Then select tracks or albums, right-click → MusicBrainz → **Submit my genres to MB**.

MusicBrainz stores genres as tags that users vote on. The plugin adds your genres as your own votes ("upvotes") on the levels you choose. It never removes votes, neither yours nor other people's.

| Setting | Default | Options |
| --- | --- | --- |
| Levels | recording and release group | release (the specific edition) in addition, or any combination |
| Album levels get | genres shared by all selected tracks of the album | genres of any selected track of the album |
| Read from | MusicBee's genre field | one of MusicBee's custom tags |
| Only names MusicBrainz knows as genres | on | off: other names are submitted as plain tags |
| Update the genre list | every session | once a day, week, month or year, or never (keep the stored list) |

The genres in the field are split at `;`. To check the names, the plugin uses the list of all MusicBrainz genres. It is stored in `MbLinks\genres.txt` and loaded again from MusicBrainz according to the setting; "Update now" in the Genres tab loads it immediately, and the tab shows how many genres the list contains and when it was loaded. With "never", the list is only loaded if there is none yet or when you click "Update now". If loading fails, the stored list is used. The Genres tab also links to the [list of all MusicBrainz genres](https://musicbrainz.org/genres). Before anything is sent, a confirmation shows the tracks, levels, entities, number of votes, the names that are skipped, the requests and the estimated time. Up to 100 entities are submitted per request. Every submitted genre is written to the log file.

Submitting genres needs the MusicBrainz login with the permission to submit tags. Logins made with plugin versions before 0.9.0 do not include it; the plugin then asks you to log in again.

### Rating sync (optional)

| MusicBee | MusicBrainz |
| --- | --- |
| Track rating | Recording |
| Album rating | Release group |

Rating sync is off by default. As long as it is off, the plugin neither reads nor changes any ratings and the rating commands are hidden. Once switched on, you submit and import ratings via the context menu of selected tracks.

Automatic submission of changed ratings is a separate option, also off by default. When you switch it on, it applies only to ratings changed from then on. If you untick "only ratings changed from now on", the plugin counts all existing ratings and asks once, with numbers and an estimated time, whether to submit them.

The result of every submission and import is shown in MusicBee's status bar at the bottom, for larger amounts also the progress. Every submitted rating is written to the log file.

MusicBrainz only supports whole stars. Half stars are rounded up or down when submitting, depending on the setting. MusicBrainz has no ratings for individual releases, so the album rating is mapped to the release group.

## Supported services

The two MusicBrainz links come first, all other services follow in alphabetical order.

| Service | Default |
| --- | --- |
| MusicBrainz – Release Group | on |
| MusicBrainz – Release | on |
| AllMusic | off |
| Discogs (master before release) | on |
| Genius | on |
| Jazz Music Archives | off |
| Metal Archives (Encyclopaedia Metallum) | off |
| Metal Music Archives | off |
| Musik-Sammler | off |
| ProgArchives | off |
| Rate Your Music | on |
| SecondHandSongs | off |
| VGMdb | off |
| Wikipedia (or Wikidata) | on |

Every service can be switched on or off individually in the settings. Adding a service takes one line in `src/MbLinks/Links/Sites.cs`.

## Large libraries

MusicBrainz allows about one request per second, and the plugin needs one request per album. Typical libraries have about 10 to 12 tracks per album.

| Library | Albums (approx.) | Time for a full analysis |
| --- | --- | --- |
| 100,000 tracks | 8,000–10,000 | about 2.5–3 hours |
| 500,000 tracks | 40,000–50,000 | about 12–15 hours |
| 1,000,000 tracks | 80,000–100,000 | about 1–1.3 days |
| 100,000 albums | 100,000 | about 1.3 days |
| 500,000 albums | 500,000 | about 6.4 days |
| 1,000,000 albums | 1,000,000 | about 12.7 days |

Before the requests, the plugin reads the MusicBrainz IDs from one file per album, which takes roughly 10 ms per album. The analysis can be stopped and resumed at any time, so even very large libraries can be analyzed in several sessions. The link store is written by appending lines, so it stays fast with many albums. Note that MusicBee runs as a 32-bit program; with several hundred thousand stored albums the memory use of the link store becomes noticeable.

## Installation

Requires MusicBee 3.5 or newer.

### Ready-made version

1. Download the ZIP file of the desired release from the GitHub releases page. The latest development build is available on the Actions page: open the newest successful run of "Build" and download the file under "Artifacts".
2. Close MusicBee.
3. Copy `mb_MbLinks.dll` and `TagLibSharp.dll` into MusicBee's Plugins folder. For a normal installation this is usually `C:\Program Files (x86)\MusicBee\Plugins`, for the Microsoft Store version `C:\Users\<name>\AppData\Local\Packages\50072StevenMayall.MusicBee_kcr266et74avj\LocalCache\Roaming\MusicBee\Plugins`.
4. Start MusicBee and check under Preferences → Plugins that "MusicBrainz Links" is enabled.
5. Place the panel where you want it via MusicBee's panel layout (Arrange Panels).

### Building from source

#### Automatic build on GitHub

Every push and every pull request is built automatically on a Windows machine provided by GitHub (`.github/workflows/build.yml`). The workflow:

- checks that `MusicBeeInterface.cs` is in `src/MbLinks`,
- checks that the version in `src/MbLinks/MbLinks.csproj` is stated at the top of this README and has a section in `CHANGELOG.md`,
- builds the plugin and offers `mb_MbLinks.dll`, `TagLibSharp.dll`, README, CHANGELOG and the third-party notices for download under "Artifacts" on the run page,
- creates a GitHub release with a ZIP file and the matching CHANGELOG section when a tag `vX.Y.Z` is pushed; the tag must match the version in `MbLinks.csproj`.

If something fails, the run page shows the reason, including all compiler errors.

The workflow needs `MusicBeeInterface.cs` in the folder `src/MbLinks`. Add the file from the official MusicBee API package (on getmusicbee.com or in the API thread of the MusicBee forum) once, and replace it when a newer API version is needed.

#### Building on your own computer

You need Visual Studio 2022 with the ".NET desktop development" workload or the .NET SDK, plus `MusicBeeInterface.cs` (see above).

1. Make sure `MusicBeeInterface.cs` is in `src/MbLinks`.
2. Build: open `MbLinks.sln` in Visual Studio and build the Release configuration, or run in the repository root:

   ```
   dotnet build src/MbLinks/MbLinks.csproj -c Release
   ```

3. Both DLLs are then in `src\MbLinks\bin\Release\net48\`.

If you build often, enter the Plugins folder in `MusicBeePluginsDir` in `src/MbLinks/MbLinks.csproj`; the plugin is then copied there after every build, provided MusicBee is closed.

If you publish a fork, set `RepositoryUrl` in `src/MbLinks/ProjectInfo.cs` to your repository address. MusicBrainz requires a contact in the User-Agent and this address is used for it.

## Settings

The settings open via Tools → MusicBrainz Links → Settings … or via "Configure" next to the plugin in MusicBee's preferences. The same Tools submenu also offers "Analyze tracks …" and "Show log …".

| Tab | Contents |
| --- | --- |
| Links | links on/off, services, Wikipedia language, fetching in advance for the playing list, "Analyze tracks …", "Check all stored links again …", stopping a running job, statistics of the stored links |
| Display | what the panel shows, icon size, links in the context menu, overview of all stored favicons, "Reload icons", "Open icon folder" |
| Genres | "Get MB genres if available": on/off, source, target field, replace or add, capitalization. "Submit my genres to MB": on/off (off by default), levels, album rule, source field, only known genres |
| Ratings / Login | MusicBrainz login, rating sync on/off, track and album ratings, automatic submission (only future ratings by default), rounding, overwriting on import |
| About | version, AI notice, "Show log …", "Open log folder" |

The "Show log …" button at the bottom left of the settings window is available on every tab.

If the Wikipedia language is left empty, the plugin tries to detect it (see [Known limitations](#known-limitations)); the detected language is shown in the window.

### Setting up the MusicBrainz login

Ratings and submitting genres need a login to MusicBrainz via OAuth2. The plugin never sees your password. It asks for the permissions to read your profile, to submit ratings and to submit tags (genres).

1. Log in to MusicBrainz and register a new application under "Applications" in your account settings. Type: "Installed application", callback URL: `urn:ietf:wg:oauth:2.0:oob`.
2. Enter the client ID and client secret shown there in the "Ratings / Login" tab.
3. Click "Log in …". MusicBrainz opens in your browser; after you allow access, MusicBrainz shows a code that you copy into the plugin window.

If you publish the plugin, you can instead enter the credentials of your own registered application in `src/MbLinks/MusicBrainz/MusicBrainzAuth.cs` under `DefaultClientId` and `DefaultClientSecret`; steps 1 and 2 are then not needed for anyone. For desktop applications the client secret is not considered confidential; the plugin also uses PKCE.

## How is data stored?

All files are in the `MbLinks` subfolder of MusicBee's data folder.

| Data | Location | Kept until |
| --- | --- | --- |
| Links per album (MusicBrainz IDs, URL relationships, Wikipedia articles per language) | `MbLinks\links.jsonl` | you check them again in the settings |
| Favicons of the services | `MbLinks\icons\*.png` | you click "Reload icons" |
| Settings | `MbLinks\settings.xml` | permanently |
| List of MusicBrainz genres (for submitting genres) | `MbLinks\genres.txt` | updated according to the setting |
| Login (refresh token, client credentials, user name) | `MbLinks\auth.dat`, encrypted with Windows DPAPI, readable only by your own Windows account | you log out |
| Log file | `MbLinks\logs\mblinks-YYYY-MM.log` | permanently (one file per month) |
| Ratings | no copy of their own; only in memory, which values have already been submitted in this session | MusicBee is closed |

Switching services on or off or changing the Wikipedia language needs no new MusicBrainz requests; a different Wikipedia language only causes one Wikidata request per album when it is shown.

Uninstalling the plugin via MusicBee deletes the `MbLinks` folder.

## Log file

How files appear in the log is set in the About tab:

| Setting | Example |
| --- | --- |
| Folder and file name (default) | `Example Album (2004)\01 First Song.flac` |
| Full path | `D:\Music\Example Artist\Example Album (2004)\01 First Song.flac` |
| No file information | no file column; file names inside error messages are replaced by `[file]` |

Paths in error messages from Windows are shortened in the same way. The setting applies to new entries; existing log files are not changed.

The log can be viewed inside MusicBee via "Show log …" (settings window or Tools menu). The viewer shows one month at a time, filters by submissions, errors or events, and can open the file in your editor. Each line has date and time:

- **EXPORT**: every rating and every genre vote accepted by MusicBrainz, with entity type, MusicBrainz ID, value or genres, trigger (manual, automatic, initial) and file,
- **EXPORT FAILED**: ratings that could not be submitted, with the reason,
- **GENRES**: every genre change in the tags, with file, field, old and new value,
- **ERROR**: all errors, e.g. failed requests or unreadable files,
- **INFO**: logins, logouts, start and end of analyses and checks, settings changes.

## Network access

| Destination | Purpose |
| --- | --- |
| musicbrainz.org | links, ratings, login; at most about one request per second |
| wikidata.org | Wikipedia article in the requested language |
| websites of the services | loading the favicon once |
| icons.duckduckgo.com | fallback source for favicons |

## Known limitations

- The MusicBee API does not provide MusicBrainz fields. The plugin therefore reads the IDs directly from the files with TagLib#. Supported are ID3v2, Vorbis comments (FLAC, Ogg, Opus), MP4/M4A, APEv2 and WMA in Picard's naming convention, which other taggers follow as well.
- The MusicBee API does not report the user interface language. The plugin looks for it in MusicBee's settings file and otherwise uses the Windows language; if in doubt, enter the language in the settings.
- The panel checks the selection once per second. The notification for selection changes that newer MusicBee versions offer is not used yet.
- For an analysis, tracks are grouped into albums by folder and album name. Different releases with the same album name in the same folder count as one album; their other releases are fetched when played.
- Links are album-level only. Genius pages for individual songs are usually attached to works in MusicBrainz and are not shown yet.
- Genres from Rate Your Music are not read: Rate Your Music has no public API and its terms of service forbid automated access.
- The genre import writes to the files. There is no automatic undo; the log file contains the old values.
- Streaming services are intentionally not supported, because MusicBrainz only stores them per release.
- Ratings removed in MusicBee are not deleted on MusicBrainz.
- It has not been verified whether MusicBee reports a changed album rating as a rating change. If automatic submission misses it, submit via the context menu.

## Repository layout

| Path | Contents |
| --- | --- |
| `MbLinks.sln` | solution for Visual Studio |
| `src/MbLinks/MbLinks.csproj` | project file with the version number |
| `src/MbLinks/Plugin.cs` | entry point for MusicBee: notifications, menus, background jobs |
| `src/MbLinks/ProjectInfo.cs` | name, repository address, version, User-Agent |
| `src/MbLinks/MusicBeeInterface.cs` | MusicBee API (from the official MusicBee API package, see above) |
| `src/MbLinks/Core/` | settings, log, access to MusicBee's library, reading MusicBrainz IDs from files, analysis planning |
| `src/MbLinks/Links/` | list of services, link store, link lookup, favicons |
| `src/MbLinks/Net/` | web access with the MusicBrainz request limit, JSON helpers |
| `src/MbLinks/MusicBrainz/` | OAuth2 login |
| `src/MbLinks/Genres/` | genre import from MusicBrainz, submitting your own genres |
| `src/MbLinks/Ratings/` | rating sync |
| `src/MbLinks/UI/` | settings window, dialogs, panel, icons, log viewer |
| `.github/workflows/build.yml` | automatic build and releases on GitHub |

All `.cs` files below `src/MbLinks` are compiled automatically; new files only need to be placed in the right folder.

## Versioning

The project follows [Semantic Versioning](https://semver.org/). The version number is maintained only in `src/MbLinks/MbLinks.csproj`; the plugin reads it at runtime (MusicBee's plugin list, About tab, User-Agent). All changes are listed in [CHANGELOG.md](CHANGELOG.md).

### Publishing a new version

1. Increase `<Version>` in `src/MbLinks/MbLinks.csproj`.
2. In `CHANGELOG.md`, move the entries under "Unreleased" into a new section with version number and date.
3. Update the version at the top of this README.
4. Commit and push. The automatic build checks steps 1 to 3 and fails if one is missing.
5. Create the tag and push it, e.g. `git tag v0.12.2` and `git push origin v0.12.2`. The workflow then builds the plugin and creates the GitHub release with the ZIP file and the CHANGELOG section as release notes.

## License

Not decided yet. As long as there is no license file in the repository, others may view the code but cannot safely reuse it.

TagLibSharp.dll, which is shipped with the plugin, is licensed under the LGPL-2.1. Details on all third-party components are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Acknowledgements

- [MusicBrainz](https://musicbrainz.org) and [Wikidata](https://www.wikidata.org) for their open data
- [TagLib#](https://github.com/mono/taglib-sharp) for reading tags
- MusicBee by Steven Mayall
