# AGENTS.md

Technical notes for **optima save**: a browser extension (Firefox family + Chrome family) and a Windows
helper that does the work. User-facing docs are in `README.md`; the look follows the optima machina design
language (plastic, one blue, lowercase words, Inter).

## layout

```text
optima-save.cs        the helper, one C# 5 file (built by Windows' own csc, see build.bat):
                        native messaging host, the save dialog (IFileSaveDialog + a "trim" checkbox),
                        yt-dlp runs, the progress pill, the trim and setup windows (WebView2),
                        install / uninstall / self-update
trim.html             the trim window's page (embedded in the exe)
setup.html            the setup window's page (embedded in the exe)
extension/            the Firefox-family add-on (MV2): background.js, manifest.json, icons
chrome-manifest.json  the Chrome-family manifest (MV3); build.bat assembles chrome\ from it + background.js
updates.json          Firefox's update manifest (the add-on's update_url points here, on the main branch)
icons/                app icons (blue card into a slot); icon.ico goes into the exe
vendor/               WebView2 1.0.4258.31 (Microsoft, BSD-style license), Inter (OFL), the logo images,
                      the done sound. All embedded in the exe at build time.
build.bat             builds optima-save.exe, optima-save.xpi and chrome\; "build.bat test <out>" = test build
release.js            release steps: build + signed latest.json, add the Mozilla-signed xpi, fill _publish
```

## how a save flows

1. The extension (`background.js`, one file for both browsers) works out the URL: the right-clicked link,
   the page, or (Firefox, with page access) the one video in a feed box. Zen often hands menu clicks over
   with no tab, so everything falls back to the URLs the menu event carries.
2. `connectNative('optima_save')` starts the helper. The helper checks the caller (extension id / origin),
   caps messages at 64 KB, takes known actions only and http(s) URLs only.
3. The save dialog opens (the title comes from the page, else the site's oEmbed, else a dated name). With
   "trim before saving" ticked, the trim window downloads a 360p preview to a temp folder and plays it
   locally; the chosen part comes down with `--download-sections` + `--force-keyframes-at-cuts`.
4. yt-dlp writes to `<name>.optima-save.*`; only a finished file replaces the target. mp4s that aren't
   h264 + aac are converted (Premiere).

## security model (keep it)

- The extension: menus/contextMenus, nativeMessaging, activeTab, scripting. No host permissions, no
  content scripts, no storage, no network. Injection only via `scripting.executeScript({ func, args })`.
- The helper trusts nothing it receives. "show in folder" only for files it saved. Child processes get a
  closed stdin. `--` before every URL handed to yt-dlp. Browser cookies only for `LoginSites`, never YouTube.
- Tools: HTTPS only, SHA-256 checked against each project's published sums, zip entries as plain names.
- WebView2 windows: InPrivate, own throwaway profile, devtools off, navigation locked to the page's own
  virtual host, no new windows / downloads / permissions; page messages are validated in C#.
- Updates: the exact bytes of `latest.json` must verify against the RSA-3072 key baked into the exe
  (`UpdateKeyModulus`) before anything in them is read; only newer versions; the download must come from
  this repo's releases, end on GitHub's own hosts and match the signed SHA-256. The new exe runs once
  with `--warm` (Windows' first-run scan happens then, not on the next click) and must exit 0, or the old
  exe is put back and that version is skipped: a broken release can't strand anyone.
- The private key lives outside the repo (`%USERPROFILE%\.optima-signing`), encrypted with a passphrase
  (AES-256); `release.js` refuses to sign with an unprotected key. If the key is ever stolen with its
  passphrase, every installed copy would trust that thief's updates: keep both offline and separate.
- `TESTMODE` builds (tests only, never shipped) add a WebView2 debug port, a dialog-free path and a local
  release server; `release.js` checks the shipped exe is clean of personal data before publishing.

## releasing

1. Bump the version in four places: `AppVersion` + `AssemblyVersion` in optima-save.cs, both manifests.
2. `release.bat` → upload `optima-save-for-mozilla.xpi` to addons.mozilla.org (On your own; source code: No).
3. Drag the signed xpi onto `add signed xpi.bat` (updates updates.json and fills `_publish`).
4. GitHub: commit the code (with updates.json), then a release `v<version>` with Optima-Save-Setup.exe,
   optima-save.xpi, latest.json and latest.json.sig. Installed helpers pick it up within a day.
