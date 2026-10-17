# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/).

The code of all versions was written with Claude, an AI model by Anthropic.

## [Unreleased]

## [0.12.1] – 2026-10-17

### Added

- `MbLinks.sln` in the repository root, for opening the project in Visual Studio.
- README section "Repository layout".

### Changed

- The project now lives in the GitHub repository [proto-beep/MBsquare](https://github.com/proto-beep/MBsquare). Its address is set in `ProjectInfo.cs` (link in the About tab, contact in the User-Agent for MusicBrainz) and in the build badge.
- The source files moved to `src/MbLinks`, sorted into subfolders by function: `Core`, `Links`, `Net`, `MusicBrainz`, `Genres`, `Ratings` and `UI`. `WebService` and `Json` moved from `LinkService.cs` into their own files. The plugin behaves exactly as before.
- `MusicBeeInterface.cs` now belongs in `src/MbLinks`; the build workflow uses the new paths.

## [0.12.0] – 2026-10-07

### Added

- Automatic build on GitHub (`.github/workflows/build.yml`): every push and pull request is compiled on a Windows machine; the DLLs can be downloaded under "Artifacts".
- The build checks that `MusicBeeInterface.cs` is present and that the version in `MbLinks.csproj` appears in README and CHANGELOG; for tags, that the tag matches the version.
- Pushing a tag `vX.Y.Z` creates a GitHub release with a ZIP file and the matching CHANGELOG section as release notes.
- `THIRD-PARTY-NOTICES.md` with the licenses of TagLib# and the MusicBee API; included in every build.
- Build status badge in the README.

### Changed

- `MusicBeeInterface.cs` now belongs in the repository, because the automatic build needs it.
- README: building and publishing describe the automatic build.

## [0.11.0] – 2026-10-04

### Added

- Link "Show all MusicBrainz genres" (musicbrainz.org/genres) in the Genres tab, below the genre list settings.
- The confirmation for submitting genres names the address of the MusicBrainz genre list when names are skipped.

## [0.10.0] – 2026-10-04

### Added

- Setting "Update the genre list" in the Genres tab: every session (default), once a day, once a week, once a month, once a year, or never (keep the stored list).
- "Update now" button and an info line with the number of genres and the time the list was loaded.

### Changed

- The list of MusicBrainz genres is now stored in `MbLinks\genres.txt`, so it survives restarts. If loading it from MusicBrainz fails, the stored list is used.

## [0.9.0] – 2026-10-04

### Added

- Submitting your own genres to MusicBrainz, off by default and only on request: right-click → MusicBrainz → "Submit my genres to MB". The genres are added as your votes ("upvotes"); nothing is removed on MusicBrainz.
- Settings in the Genres tab: on/off (off by default); levels recording, release group (both on by default) and release; album levels get the genres shared by all selected tracks (default) or of any selected track; field to read from (MusicBee's genre field by default, or a custom tag); only names that MusicBrainz knows as genres (on by default).
- The list of all MusicBrainz genres is loaded once per session to check the names. The confirmation shows tracks, levels, entities, votes, skipped names, requests and estimated time.
- Every submitted genre vote is written to the log as EXPORT (or EXPORT FAILED).

### Changed

- The MusicBrainz login now also asks for the permission to submit tags. Logins from earlier versions keep working for ratings; for submitting genres the plugin asks you to log in again.
- The "Ratings" tab is now called "Ratings / Login".

## [0.8.0] – 2026-10-04

### Added

- Setting "Files in the log" (About tab): folder and file name (default), full path, or no file information. Paths inside error messages are shortened or removed accordingly.

### Changed

- The log no longer contains full paths by default, only the folder and the file name.
- The plugin works strictly with the MusicBrainz IDs stored in the files: album ratings and album genres now require a release group ID in the file; the release group is no longer looked up from the release ID. Track ratings and track genres require a recording ID, links a release or release group ID.
- In the context menu, the actions are disabled if a single selected track has no relevant MusicBrainz ID.
- The confirmation for submitting existing ratings states that only tracks and albums with the relevant IDs are submitted.

### Fixed

- Counts in log messages use the correct singular, e.g. "Submitting 1 rating" instead of "Submitting 1 ratings".

## [0.7.0] – 2026-10-03

### Added

- Genre import from MusicBrainz, only on request: right-click → MusicBrainz → "Get MB genres if available". A confirmation shows tracks, source, requests and estimated time before anything is written.
- New Genres tab in the settings: entry in the context menu on/off; source (recording by default, release group, or recording with release group as fallback); target field (MusicBee's genre field by default, or one of MusicBee's custom tags such as one named "MB Genres"); replace existing values (default) or keep them and add missing genres; optional capitalization.
- Genres are separated by "; " and sorted by the number of votes on MusicBrainz. Tracks without genres on MusicBrainz are left unchanged.
- Log level GENRES: every genre change with file, field, old and new value. The log viewer has a filter and a count for it.

## [0.6.0] – 2026-10-03

### Added

- Context menu → MusicBrainz → "Fetch links now": fetches the links of the selected albums immediately, also if they are already stored (to update them). Up to 10 albums without a question, larger selections after a confirmation with numbers and estimated time.
- The context menu shows when the links of the selected album were last checked.
- Log viewer inside MusicBee: one month at a time, filter for submissions, errors and events, summary counts, "Open in editor" and "Open folder".
- "Show log …" at the bottom left of the settings window (on every tab), in the About tab and in the Tools menu.
- Tools menu entry "MusicBrainz Links" is now a submenu with "Settings …", "Analyze tracks …" and "Show log …".
- Display tab: overview of all stored favicons with their website, "Refresh list" and "Open icon folder". "Reload icons" now asks for confirmation.

### Changed

- When an album is fetched again, the Wikipedia articles of all previously stored languages are looked up again as well.
- Progress texts of the analysis state that grouping uses MusicBee's database without requests and that the IDs are read from one file per album.
- README explains that each album needs exactly one request, regardless of the number of tracks.

## [0.5.0] – 2026-10-03

### Added

- Links are fetched in advance for the next 50 tracks in the playing list (can be switched off).
- "Analyze tracks …" in the Links tab: entire library, tracks shown in the main panel, selected tracks, or a playlist. A progress window determines tracks and albums first; a confirmation then shows tracks, albums, already stored albums, requests and the estimated time. The analysis runs in the background, can be stopped, and continues with the remaining albums when started again.
- "Stop the running job" button for analyses and checks of stored links.
- Display tab: the panel can show the playing album, the album selected in the library, or both (two rows, default); icon size 16, 20, 24 (default) or 32 px; links in the context menu on/off; "Reload icons".
- Context menu "MusicBrainz" for tracks and albums with the stored links of the selected album (with icons), "Fetch links for the selection …" and, if rating sync is on, "Submit ratings" and "Import ratings".
- Option "Only ratings changed from now on" for automatic submission, on by default. If switched off, the plugin counts the existing ratings and asks once, with numbers and estimated time, whether to submit them.
- Log file in `MbLinks\logs` (one file per month) with every submitted rating (date, time, MusicBrainz ID, value, trigger, file), failed submissions, all errors, and events such as logins and analyses. "Open log folder" in the About tab.
- Estimated remaining time in the status bar during analyses and checks.

### Changed

- Links are only fetched when an album is played, comes up in the playing list, or is analyzed on request.
- One MusicBrainz request per album instead of two (release and release group relationships in one lookup).
- The MusicBrainz icons are marked with a blue "RG" (release group) and an orange "R" (release) badge.
- The link store is now `links.jsonl` and is written by appending lines, which stays fast for large libraries; it is loaded in the background. An existing `links.json` is converted automatically.
- The rating commands in the context menu moved into the "MusicBrainz" submenu.
- The settings window has a fourth tab, "Display".

### Fixed

- `Library_QueryFilesEx` was called with `out` instead of `ref`, which would not have compiled against the MusicBee API.

## [0.4.0] – 2026-10-03

### Added

- Statistic below the "Check all stored links again …" button: number of stored links and albums, plus the number of links per service in two columns. Services that are switched off are listed too and marked "off". The statistic updates live while a check is running.

### Changed

- The line "Stored: links for N albums" above the button was replaced by the statistic below it.

## [0.3.0] – 2026-10-03

### Added

- Links are stored permanently in `links.json` and are only fetched again on request.
- Button "Check all stored links again …" in the Links tab. A confirmation first explains what happens and how long it will take; the check runs in the background, shows its progress in the status bar and can be stopped.
- MusicBrainz icons are only shown if the release or release group still exists in MusicBrainz.

### Changed

- All source code comments, user interface texts, status bar messages, README and CHANGELOG are now in English.
- Services enabled by default are now only Discogs, Genius, Rate Your Music and Wikipedia (plus the two MusicBrainz links). Jazz Music Archives, Metal Archives, Musik-Sammler and ProgArchives are now off by default.
- Services are listed in alphabetical order after the two MusicBrainz links, in the settings and in the panel.
- "Encyclopaedia Metallum (Metal Archives)" is now called "Metal Archives (Encyclopaedia Metallum)".
- A tag change of the playing track no longer fetches the links again; only changed MusicBrainz IDs lead to new requests.
- The Ratings tab states explicitly that rating sync is off by default and does nothing until it is switched on.

### Removed

- Spotify, Apple Music, Deezer, Tidal and Bandcamp, because MusicBrainz only stores these links per release.

## [0.2.0] – 2026-10-03

### Added

- Own settings window with the tabs Links, Ratings and About, available via the Tools menu and via "Configure" in MusicBee's plugin preferences.
- Links can be switched on or off as a whole, and every service individually.
- New services: Genius, Encyclopaedia Metallum (Metal Archives), ProgArchives, Jazz Music Archives, Metal Music Archives, AllMusic, Bandcamp, VGMdb, SecondHandSongs, Spotify, Apple Music, Deezer, Tidal.
- Rating sync with MusicBrainz, switchable as a whole: track rating ↔ recording, album rating ↔ release group, both selectable separately.
- Login to MusicBrainz via OAuth2 (authorization code with PKCE, the code is copied from the browser). Login data is stored encrypted with Windows DPAPI.
- Context menu entries "Submit ratings" and "Import ratings" for selected tracks.
- Optional automatic submission of changed ratings.
- Selectable rounding of half stars when submitting; by default an import only fills in unrated tracks, overwriting is configurable.
- Messages in MusicBee's status bar for progress and results of submissions and imports.
- Note about AI-generated code in README, CHANGELOG, all source files and the About tab.
- README with instructions, data storage and network access; this CHANGELOG; `.gitignore`.

### Changed

- The version number is maintained only in `MbLinks.csproj` and read at runtime.
- The User-Agent names the repository address from `ProjectInfo.cs` as contact.
- The Wikipedia language is set in the settings window instead of MusicBee's small plugin preferences area. Settings from 0.1.0 are kept.
- Wikidata is only queried if Wikipedia is switched on.

## [0.1.0] – 2026-10-03

### Added

- Dockable panel with clickable icons for the album that is playing; a left click opens the page in the default browser.
- Services: MusicBrainz (release group and release), Wikipedia in MusicBee's language with Wikidata as fallback, Discogs, Rate Your Music, Musik-Sammler.
- MusicBrainz IDs are read directly from the files (ID3v2, Vorbis comments, MP4, APEv2, WMA).
- Favicons are downloaded once and stored; otherwise an abbreviation is drawn.
- Requests to MusicBrainz at most about once per second, results per album kept in memory.
