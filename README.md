# optima save

Right-click a video, save it. YouTube, TikTok, Instagram, X and most other sites, as an mp4 (sharpest up
to 1080p, ready for Premiere) or an mp3, with an optional trim window to keep only the part you want.

Free, no account, no ads, no telemetry. Part of [optima machina](https://optimamachina.world): small tools
that want nothing from you.

**[Download the latest version](https://github.com/optimamachina/optima-save/releases/latest)** (Windows 10 / 11, 64-bit)

Windows may say "Windows protected your PC" because the installer isn't code-signed yet. Click **More info**,
then **Run anyway**.

## install

1. Download `Optima-Save-Setup.exe` and open it. It installs itself for your user (no admin needed) and
   downloads the tools it uses, once: yt-dlp, ffmpeg and deno, about 150 MB, each checked against the
   checksum its project publishes.
2. The window lists the browsers on your PC. Click **add to** next to each one you use:
   - **Firefox, Zen, Floorp, LibreWolf, Waterfox**: the browser opens the add-on (signed by Mozilla).
     Click **continue to installation**, then **add**.
   - **Chrome, Brave, Edge**: their stores don't accept video downloaders, so it goes in unpacked. The
     extensions page opens and the folder is copied for you: turn on **Developer mode**, click
     **Load unpacked**, paste the folder and pick it.
3. For private windows: Firefox-family browsers, the add-on's page in `about:addons` → **Run in Private
   Windows** → Allow. Chrome-family, the extension's **Details** → **Allow in Incognito**.

## using it

- Right-click a video, a thumbnail or a video link → **save video as…** (or press **Alt+Shift+S**).
- The normal Windows save dialog opens. Pick **mp4** or **mp3** in its file type box and a folder.
  **Trim before saving** is ticked: leave it for the trim window, untick it to save the whole thing.
- **The trim window**: drag the two handles, or play and press **I** and **O** at the moments you want.
  Space plays, the arrow keys step a second (a frame with Shift), Enter saves, Esc cancels. Only the
  part you chose is downloaded, cut on the exact frames.
- A small pill in the bottom-right corner shows the progress. Click its **×** to stop a save; click it
  when it says **saved** to see the file.

On YouTube's own player the right-click menu is YouTube's: right-click just below the video, a thumbnail
or a title instead, or press Alt+Shift+S.

## what you get

- **mp4**: the sharpest version up to 1080p (1920×1080, 1080×1920 for vertical videos), h264 + aac so it
  drops straight into Premiere. The rare video that only comes in other codecs is converted.
- **mp3**: 320 kbps, with the title and the cover art.
- A failed or cancelled save never touches the file you chose to overwrite, and leaves nothing behind.

## privacy and security

- Nothing is ever sent to optima machina. There is no server of ours.
- The extension can't see any site until you click it, and then only that one tab.
- Instagram (and, only when a site refuses a logged-out download, TikTok, X, Facebook, Threads or Reddit)
  gets your login from your Firefox-family browser, read on your PC and sent only to that same site.
  YouTube never does: it can flag accounts that download.
- Saves from private windows leave nothing on your PC but the file.
- Once a day, after a save, the helper asks GitHub whether there is a new version. It installs one only
  if it carries this project's update signature, and checks its checksum too.
- A small log (`%LOCALAPPDATA%\optima save\log.txt`) records steps and errors, never addresses or titles.

## uninstall

Windows Settings → Apps → **optima save** → Uninstall (or open the installer again and click **remove**).
Then remove the extension from your browsers.

## build it yourself

Windows 10 / 11 only. No SDK needed: `build.bat` uses the C# compiler that ships with Windows.

```
build.bat          the helper (optima-save.exe), the Firefox add-on (optima-save.xpi) and chrome\
release.bat        a full release: build, signed update files, the file for Mozilla to sign
```

See [AGENTS.md](AGENTS.md) for how it fits together.

## ingredients

The code was written with Claude. The idea, every design decision and the testing are human. It stands on
[yt-dlp](https://github.com/yt-dlp/yt-dlp), [ffmpeg](https://ffmpeg.org), [deno](https://deno.com) and
Microsoft's WebView2, each under its own license.

## license

[MIT](LICENSE)
