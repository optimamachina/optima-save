// optima save · the helper.
// Zen / Firefox starts it once per save (native messaging). It asks where to save with the normal
// Windows dialog, runs yt-dlp, and streams progress back to the extension. Double-click it to install:
// it copies itself to %LOCALAPPDATA%\optima save, registers with the browser and fetches yt-dlp,
// deno and ffmpeg from their official GitHub releases, each checked against its published SHA-256.
// "--uninstall" removes all of that again. Built with the C# 5 compiler that ships with Windows
// (see build.bat), so there is no runtime to install.
//
// Security: this is the trusted side. It treats every message as untrusted, even from its own
// extension: known actions only, http(s) urls only, the person picks every path in the dialog,
// "show in folder" works only for files it saved, and it never trusts a path yt-dlp prints back.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

[assembly: AssemblyTitle("optima save")]
[assembly: AssemblyProduct("optima save")]
[assembly: AssemblyDescription("right-click a video, save it as mp4 or mp3")]
[assembly: AssemblyCompany("optima machina")]
[assembly: AssemblyCopyright("MIT license, optima machina")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

static class OptimaSave
{
    const string AppVersion = "1.3.0";
    const string Repo = "optimamachina/optima-save";
#if TESTMODE
    // tests: a fake release server on this PC and a stand-in exe, never the real install
    static readonly string ReleaseBase = Environment.GetEnvironmentVariable("OPTIMA_SAVE_TEST_RELEASES") ?? "https://github.com/" + Repo + "/releases/";
    static readonly string XpiUrl = ReleaseBase + "latest/download/optima-save.xpi";
    static readonly string LatestUrl = ReleaseBase + "latest/download/latest.json";
#else
    const string ReleaseBase = "https://github.com/" + Repo + "/releases/";
    const string XpiUrl = ReleaseBase + "latest/download/optima-save.xpi";      // signed by Mozilla
    const string LatestUrl = ReleaseBase + "latest/download/latest.json";       // signed by us
#endif
    // The public half of the update-signing key. The private half never leaves the maker's PC; an update
    // that isn't signed with it is refused, even if it comes from the real GitHub repo.
    const string UpdateKeyModulus = "0xu8kgr1BJhbQDT4JLA9IflVf/Nbyo83Ep+ZorwPAR8hrtLYsNB9oqN0eVGzxCK13fs2CIAGD0dHQ9q88RsftYrGTNxZ9+8+smHktOtsS8fYWqVC9NQ8upcdcAEB3bxIF/NNW9dsk2fmNCrmpR9aedJ0Bv/rWd0mWOp8mmX9RC23R6UhOm2dqEMbpa9D6iHYvkxLaI4pnLhxM4zJswgt7Cc+OI0BPeQlhJQLD0NJr2gHbMfQdNU/4MviGvBRcEW9NBRx4kaRXMCS5+fSWb3MGkWcXkmexlX6FDR0YO0zfsmQlR62xYmh9CN/Rzy997i8unzUKxLv1UnN31R3QLRozQS2uyif4Uc+F9nOquzQ/Y+mAWBDHM1ly/pvE8hGJ8FRexjdPQiz7n9G1i3Ko5H8e3/hHGBOpXcACEs4CC/sI1CJHbFKHY3nC5ws9k1O6nQs2O4ch4RKz0XxFBLgSF3mrZsB9GZ5a5ibuslST+X8hHkiCmOVVrErVsVlmb3GOfR/";
    const string UpdateKeyExponent = "AQAB";

    const string HostName = "optima_save";
    const string ExtensionId = "optima-save@optimamachina.world";                         // Zen / Firefox
    const string ChromeOrigin = "chrome-extension://baglneblpfdhldmgjldafmggmhcihfmg/";    // Chrome, Brave, Edge
    static readonly string[] ChromiumKeys = {
        @"Software\Google\Chrome\NativeMessagingHosts\", @"Software\Chromium\NativeMessagingHosts\",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\", @"Software\Microsoft\Edge\NativeMessagingHosts\" };
    const string RegKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;
    const string TempMark = ".optima-save.";  // every file yt-dlp writes carries this until the save succeeds
    const int MaxMessage = 64 * 1024;

    static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "optima save");
    static readonly string Bin = Path.Combine(Home, "bin");
#if TESTMODE
    static readonly string SettingsFile = Path.Combine(Home, "settings.test.json");
    static readonly string LogFile = Path.Combine(Home, "log.test.txt");
#else
    static readonly string SettingsFile = Path.Combine(Home, "settings.json");
    static readonly string LogFile = Path.Combine(Home, "log.txt");
#endif
#if TESTMODE
    static readonly string InstalledExe = Environment.GetEnvironmentVariable("OPTIMA_SAVE_TEST_EXE") ?? Path.Combine(Home, "optima-save-test-install.exe");
#else
    static readonly string InstalledExe = Path.Combine(Home, "optima-save.exe");
#endif
    static readonly string ChromeDir = Path.Combine(Home, "chrome");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\optima-save";
    static string Tool(string exe) { return Path.Combine(Bin, exe); }

    // Sites where a login is often needed to see a video. Only these ever get the browser's cookies;
    // never youtube (it flags accounts that download) and never an unknown site.
    static readonly string[] LoginSites = { "instagram", "tiktok", "x", "facebook", "threads", "reddit" };

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer(); // no type resolver: plain data only
    static Stream output;
    static readonly object outputLock = new object();
    static Process running;
    static volatile bool cancelled;
    static bool noTrace; // a save from a private window: nothing about it is written to disk but the file
    static volatile bool userCancelled; // the × on the pill

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);

    // Windows only lets the app you're using pull focus. Borrow the browser's input queue for a
    // moment so the save dialog lands in front of it, not behind.
    static void ToFront(IntPtr hWnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint theirs = GetWindowThreadProcessId(fg, IntPtr.Zero), mine = GetCurrentThreadId();
        bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);
        if (attached) AttachThreadInput(mine, theirs, false);
    }

    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded; // WebView2 travels inside this exe
        SetProcessDPIAware(); // crisp dialog and pill on scaled screens (125%, 150%...)
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("crash: " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Log("ui crash: " + e.Exception);
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2, GitHub needs it
        try { File.Delete(Path.Combine(Home, "optima-save.old.exe")); } catch { } // left by the last update
        if (Array.IndexOf(args, "--uninstall") >= 0) return Uninstall();
        if (Array.IndexOf(args, "--update") >= 0) return UpdateSelf();
#if TESTMODE
        if (Array.IndexOf(args, "--setup-only") >= 0) return SetupWindow();
        if (Array.IndexOf(args, "--warm") >= 0) { Log("warm run " + AppVersion); return 0; }
#else
        if (Array.IndexOf(args, "--warm") >= 0) { try { Refresh(); return 0; } catch (Exception e) { Log("warm-up failed: " + e.Message); return 2; } }
#endif
        // The browser passes the manifest path and the extension id. Anything else is a person.
        bool fromBrowser = Array.Exists(args, a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || a.Contains("@") || a.StartsWith("chrome-extension://"));
        if (!fromBrowser) return Install(Array.IndexOf(args, "--quiet") >= 0);
        Log("started by the browser, " + args.Length + " args");
        bool ours = Array.IndexOf(args, ExtensionId) >= 0 || Array.IndexOf(args, ChromeOrigin) >= 0;
        if (!ours) { Log("refused: not our extension"); return 1; } // the browser checks this too; so do we

        output = Console.OpenStandardOutput();
        Stream input = Console.OpenStandardInput();
        Dictionary<string, object> msg = Receive(input);
        if (msg == null) { Log("no message"); return 0; }
        object priv;
        noTrace = msg.TryGetValue("private", out priv) && priv is bool && (bool)priv;
        Log("request: " + Str(msg, "action"));

        // The extension closing the connection (middle-click on the button, browser closing) cancels.
        var watch = new Thread(() =>
        {
            try { while (Receive(input) != null) { } } catch { }
            cancelled = true;
            KillRunning();
        });
        watch.IsBackground = true;
        watch.Start();

        try
        {
            string action = Str(msg, "action");
            if (action == "save") Save(msg);
            else if (action == "reveal") { Reveal(Str(msg, "path")); Send(new { type = "ok" }); }
            else Send(new { type = "error", message = "unknown request" });
        }
        catch (Exception e)
        {
            Log("failed: " + e);
            if (!cancelled) { Send(new { type = "error", message = e.Message }); PillEnd("error", "couldn't save · " + e.Message, null, 7000); }
        }
        if (userCancelled) { Log("cancelled from the pill"); Send(new { type = "cancelled" }); }
        if (pill != null) PillEnd("error", "cancelled", null, 1500); // a pill still open here was cut short
        Log("finished" + (cancelled ? " (cancelled)" : ""));
        StartUpdateCheck();
        return 0;
    }

    // A small local diary for when something goes wrong: steps and errors only, never urls or titles.
    static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Home);
            if (noTrace) return;
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 200 * 1024) File.Delete(LogFile);
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + Process.GetCurrentProcess().Id + "] " + line + Environment.NewLine);
        }
        catch { }
    }

    // ------------------------------------------------------------------ one save

    static void Save(Dictionary<string, object> msg)
    {
        string url = Str(msg, "url");
        if (!SafeUrl(url)) { Send(new { type = "error", message = "there's no video on this page to save" }); return; }
        string site = Site(url);
        Log("save from " + site);
        var settings = LoadSettings();

        string path, type;
        bool trim = false;
        string title = Str(msg, "title"); // the page title, else the site's own answer, else a dated name
        if (string.IsNullOrWhiteSpace(title)) title = LookupTitle(url, site);
        if (string.IsNullOrWhiteSpace(title)) title = Str(msg, "hint");
#if TESTMODE
        // Test builds only: a path in the message skips the dialog. Never compiled into the real helper.
        string given = Str(msg, "path");
        if (given != null) { path = given; type = given.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "mp4"; object ui; trim = msg.TryGetValue("trimUI", out ui) && ui is bool && (bool)ui; }
        else
#endif
        if (!AskWhere(CleanName(title), settings, out path, out type, out trim))
        {
            Log("dialog closed without saving");
            Send(new { type = "cancelled" });
            return;
        }
        Log("saving as " + type + (trim ? ", trimmed" : ""));

        // "trim before saving" was ticked: pick the part first. The tools must exist for the preview.
        double[] section = null;
        if (trim)
        {
            Send(new { type = "trimming" });
            EnsureTools();
            if (cancelled) return;
            string trimError;
            TrimChoice choice = AskTrim(url, site, title, out trimError);
            if (trimError != null) { Send(new { type = "error", message = trimError }); PillEnd("error", "couldn't trim · " + trimError, null, 7000); return; }
            if (choice == null) { Log("trim window closed"); Send(new { type = "cancelled" }); return; }
            if (!choice.Whole) section = new[] { choice.Start, choice.End };
        }
        if (!noTrace)
        {
            settings[type == "mp3" ? "audioDir" : "videoDir"] = Path.GetDirectoryName(path);
            settings["lastType"] = type;
            SaveSettings(settings);
        }
        Send(new { type = "start", name = Path.GetFileNameWithoutExtension(path), kind = type });
        PillShow(Path.GetFileNameWithoutExtension(path));

        EnsureTools();
        if (cancelled) return;
        Update(settings, false);

        bool canLogin = Array.IndexOf(LoginSites, site) >= 0;
        bool cookies = site == "instagram"; // instagram refuses most logged-out downloads
        string temp;
        string err = Download(url, path, type, cookies, section, out temp);
        // YouTube and others hand out the odd one-off 403 or dropped connection; a second try usually passes.
        if (err != null && !cancelled && Regex.IsMatch(err, @"HTTP Error (403|5\d\d)|timed out|reset by peer|IncompleteRead|Connection aborted", RegexOptions.IgnoreCase))
        {
            Thread.Sleep(1500);
            err = Download(url, path, type, cookies, section, out temp);
        }
        if (err != null && !cancelled && canLogin && !cookies && Regex.IsMatch(err, "log ?in|sign in|cookie|private|rate|empty media|403|unable to extract", RegexOptions.IgnoreCase))
        {
            cookies = true;
            err = Download(url, path, type, true, section, out temp);
        }
        // Sites change every few weeks and yt-dlp follows. A fresh copy fixes most other breakage.
        if (err != null && !cancelled && Update(settings, true)) err = Download(url, path, type, cookies, section, out temp);

        if (cancelled || err != null)
        {
            CleanUp(path);
            if (!cancelled)
            {
                Log("yt-dlp said: " + err.Split(new[] { (char)10 })[0]);
                Send(new { type = "error", message = Friendly(err, site) });
                PillEnd("error", "couldn't save · " + Friendly(err, site), null, 7000);
            }
            return;
        }
        if (type == "mp4") MakeEditable(temp);
        if (cancelled) { CleanUp(path); return; }

        // Only now, with a finished file in hand, replace what's at the chosen path (the dialog already
        // asked about overwriting). A failed save never costs you the old file.
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
        CleanUp(path);
        if (!noTrace) Remember(settings, path);
        Send(new { type = "done", path = path });
        PlayDone();
        PillEnd("done", "saved · " + Path.GetFileName(path), path, 4500);
    }

    static string Download(string url, string path, string type, bool cookies, double[] section, out string temp)
    {
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path);
        string expected = Path.Combine(dir, stem + TempMark + type);
        temp = expected;
        var a = new List<string> {
            "--ignore-config", "--no-playlist", "-I", "1", "--newline", "--no-colors", "--encoding", "utf-8",
            "--force-overwrites", "--no-mtime", "--cache-dir", Path.Combine(Home, "cache"),
            "--ffmpeg-location", Bin, "--js-runtimes", "deno:" + Tool("deno.exe"),
            "--no-simulate", "--progress",
            "--print", "before_dl:@@F %(format_id)s",
            "--progress-template", "download:@@P %(progress._percent_str)s",
            "-P", dir, "-o", stem.Replace("%", "%%") + TempMark + "%(ext)s",
        };
        if (type == "mp3")
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K", "--embed-metadata", "--embed-thumbnail" });
        else // the sharpest that fits 1080p either way up (1920x1080, 1080x1920, a wide film's 1920x804), h264 + aac first
            a.AddRange(new[] {
                "-f", "bv*[width<=?1920][height<=?1920]+ba/b[width<=?1920][height<=?1920]/bv*+ba/b",
                "-S", "res:1080,vcodec:h264,acodec:aac", "--merge-output-format", "mp4", "--remux-video", "mp4" });
        if (cookies)
        {
            string profile = BrowserProfile();
            if (profile != null) { a.Add("--cookies-from-browser"); a.Add("firefox:" + profile); }
        }
        if (section != null) // only the chosen part comes down; cut on the exact frames (re-encodes just the part)
        {
            a.Add("--download-sections");
            a.Add("*" + section[0].ToString("0.###", CultureInfo.InvariantCulture) + "-" + section[1].ToString("0.###", CultureInfo.InvariantCulture));
            a.Add("--force-keyframes-at-cuts");
            Send(new { type = "progress", percent = -1, status = "saving the part" });
        }
        a.Add("--"); // everything after this is a url, never an option
        a.Add(url);

        int parts = 1, part = 0;
        double last = -1, lastSent = -1;
        var errors = new StringBuilder();
        var gate = new object();
        // --print goes to stdout and, since --print makes yt-dlp quiet, progress goes to stderr: read both.
        Action<string> handle = line =>
        {
            lock (gate)
            {
                if (line.StartsWith("ERROR")) { if (errors.Length < 4000) errors.AppendLine(line); }
                else if (line.StartsWith("@@F ")) { parts = Math.Min(2, line.Split('+').Length); part = 0; last = -1; }
                else if (line.StartsWith("@@P "))
                {
                    Match m = Regex.Match(line, @"([\d.]+)%");
                    double p;
                    if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out p)) return;
                    if (p < last - 50 && part < parts - 1) part++; // the second stream (audio) started
                    last = p;
                    if (part == parts - 1 && p >= 100) { Send(new { type = "progress", percent = 98, status = "finishing" }); return; }
                    // video carries most of the bytes: give it 85 of the 97, audio the rest
                    double overall = parts == 1 ? p * 0.97 : (part == 0 ? p * 0.85 : 85 + p * 0.12);
                    if (overall - lastSent >= 1) { lastSent = overall; Send(new { type = "progress", percent = Math.Round(overall) }); }
                }
            }
        };
        int code = Run(Tool("yt-dlp.exe"), a, handle, handle);

        // Only the exact file we asked for counts. Nothing yt-dlp prints is used as a path.
        if (code == 0 && File.Exists(expected)) return null;
        string e = errors.ToString().Trim();
        return e.Length > 0 ? e : "yt-dlp stopped (code " + code + ")";
    }

    // Premiere chokes on VP9, AV1 and Opus. Almost every site serves h264 + aac, which passes straight
    // through; the rare other file gets converted, copying whatever is already fine.
    static void MakeEditable(string file)
    {
        string v = null, au = null;
        var probe = new List<string> { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", "--", file };
        Run(Tool("ffprobe.exe"), probe, line =>
        {
            string l = line.Trim();
            if (l.EndsWith(",video") || l.StartsWith("video,")) v = l.Replace(",video", "").Replace("video,", "");
            if (l.EndsWith(",audio") || l.StartsWith("audio,")) au = l.Replace(",audio", "").Replace("audio,", "");
        }, null);
        bool vOk = v == null || v == "h264", aOk = au == null || au == "aac";
        if (vOk && aOk) return;

        double duration = Duration(file);
        string converted = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".converting.mp4");
        var a = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", file, "-map", "0:v:0?", "-map", "0:a:0?" };
        a.AddRange(vOk ? new[] { "-c:v", "copy" } : new[] { "-c:v", "libx264", "-crf", "17", "-preset", "fast", "-pix_fmt", "yuv420p" });
        a.AddRange(aOk ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", "256k" });
        a.AddRange(new[] { "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", converted });
        Send(new { type = "progress", percent = 98, status = "converting for premiere" });
        double lastSent = -1;
        int code = Run(Tool("ffmpeg.exe"), a, line =>
        {
            if (!line.StartsWith("out_time_us=") || duration <= 0) return;
            double us;
            if (!double.TryParse(line.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out us)) return;
            double p = Math.Min(99, us / 1e6 / duration * 100);
            if (p - lastSent >= 1) { lastSent = p; Send(new { type = "progress", percent = Math.Round(p), status = "converting for premiere" }); }
        }, null);
        if (code == 0 && File.Exists(converted) && !cancelled) { File.Delete(file); File.Move(converted, file); }
        else if (File.Exists(converted)) File.Delete(converted);
    }

    static double Duration(string file)
    {
        double d = 0;
        Run(Tool("ffprobe.exe"), new List<string> { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", "--", file }, line =>
        {
            double x;
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) d = x;
        }, null);
        return d;
    }

    // Removes only this save's own temp files ("<name>.optima-save.*"), so it can never touch yours.
    static void CleanUp(string path)
    {
        try
        {
            string prefix = Path.GetFileNameWithoutExtension(path) + TempMark;
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(path)))
                if (Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    static string Friendly(string err, string site)
    {
        string l = err.ToLowerInvariant();
        if (l.Contains("unsupported url")) return "no video here. open the video itself, or right-click it";
        if (l.Contains("confirm your age") || l.Contains("age-restricted") || l.Contains("inappropriate")) return "youtube wants a login for this one (age limit)";
        if (l.Contains("private")) return "this video is private";
        if (l.Contains("log in") || l.Contains("login") || l.Contains("sign in") || l.Contains("cookie")) return site + " wants you logged in. log in, in your browser, and try again";
        if (l.Contains("429") || l.Contains("rate-limit") || l.Contains("rate limit")) return site + " is limiting downloads. try again in a few minutes";
        if (l.Contains("403")) return "the site blocked the download. try again in a minute";
        if (l.Contains("no space") || l.Contains("errno 28")) return "the drive is full";
        if (l.Contains("getaddrinfo") || l.Contains("timed out") || l.Contains("unable to connect")) return "no internet connection";
        string first = err.Split('\n')[0];
        first = Regex.Replace(first, @"^ERROR:\s*(\[[^\]]+\]\s*)?([\w-]+:\s*)?", "");
        return first.Length > 160 ? first.Substring(0, 160) + "…" : first;
    }

    // ------------------------------------------------------------------ the dialog

    // The Windows save dialog itself (the same one every app uses), plus one checkbox: "trim before
    // saving". Built on IFileSaveDialog because the .NET wrapper can't add controls.
    static bool AskWhere(string name, Dictionary<string, object> s, out string path, out string type, out bool trim)
    {
        type = Str(s, "lastType") == "mp3" ? "mp3" : "mp4";
        path = null;
        trim = false;
        string dir = Str(s, type == "mp3" ? "audioDir" : "videoDir");
        if (dir == null || !Directory.Exists(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        // An invisible owner window in front of everything, so the dialog opens on top of the browser.
        using (var owner = new Form())
        {
            Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            owner.FormBorderStyle = FormBorderStyle.None;
            owner.ShowInTaskbar = false;
            owner.Opacity = 0;
            owner.TopMost = true;
            owner.StartPosition = FormStartPosition.Manual;
            owner.Bounds = new Rectangle(screen.X + screen.Width / 2, screen.Y + screen.Height / 2, 1, 1);
            owner.Show();
            owner.Activate();
            ToFront(owner.Handle);

            var dlg = (IFileDialog)new FileSaveDialogCoClass();
            try
            {
                dlg.SetTitle("save as");
                dlg.SetFileTypes(2, new[] { new FilterSpec { Name = "video · mp4", Spec = "*.mp4" }, new FilterSpec { Name = "audio · mp3", Spec = "*.mp3" } });
                dlg.SetFileTypeIndex(type == "mp3" ? 2u : 1u);
                dlg.SetDefaultExtension(type);
                uint options;
                dlg.GetOptions(out options);
                // overwrite prompt, no cwd change, real files only, folder must exist, no read-only; private saves stay out of Recent
                dlg.SetOptions(options | 0x2 | 0x8 | 0x40 | 0x800 | 0x8000 | (noTrace ? 0x2000000u : 0u));
                try
                {
                    IShellItem folder;
                    SHCreateItemFromParsingName(dir, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                    dlg.SetFolder(folder);
                }
                catch { }
                dlg.SetFileName(name + "." + type);
                var custom = (IFileDialogCustomize)dlg;
                custom.AddCheckButton(1, "trim before saving", true);

                if (dlg.Show(owner.Handle) != 0) return false; // cancelled

                IShellItem result;
                dlg.GetResult(out result);
                string file;
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out file);
                uint index;
                dlg.GetFileTypeIndex(out index);
                custom.GetCheckButtonState(1, out trim);

                path = Path.GetFullPath(file);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mp3" || ext == ".mp4") type = ext.Substring(1); // a typed .mp3 / .mp4 wins
                else { type = index == 2 ? "mp3" : "mp4"; path += "." + type; } // anything else can't stick
                return true;
            }
            finally { Marshal.FinalReleaseComObject(dlg); }
        }
    }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")] class FileSaveDialogCoClass { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec { [MarshalAs(UnmanagedType.LPWStr)] public string Name; [MarshalAs(UnmanagedType.LPWStr)] public string Spec; }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int where);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialogCustomize
    {
        void EnableOpenDropDown(int id);
        void AddMenu(int id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(int id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddComboBox(int id);
        void AddRadioButtonList(int id);
        void AddCheckButton(int id, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddEditBox(int id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void AddSeparator(int id);
        void AddText(int id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetControlLabel(int id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetControlState(int id, out int state);
        void SetControlState(int id, int state);
        void GetEditBoxText(int id, out IntPtr text);
        void SetEditBoxText(int id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetCheckButtonState(int id, [MarshalAs(UnmanagedType.Bool)] out bool isChecked);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    // The suggested name comes from the web page, so it's untrusted: no path characters, no control or
    // invisible formatting characters (a right-to-left override can disguise an extension), no device names.
    static string CleanName(string title)
    {
        var sb = new StringBuilder();
        foreach (char c in title ?? "")
        {
            UnicodeCategory cat = char.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format) continue;
            sb.Append("\\/:*?\"<>|".IndexOf(c) >= 0 ? ' ' : c);
        }
        string t = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (t.Length > 120) t = t.Substring(0, 120).Trim();
        if (Regex.IsMatch(t, @"^(con|prn|aux|nul|com\d|lpt\d)(\.|$)", RegexOptions.IgnoreCase)) t = "_" + t;
        return t.Length > 0 ? t : "video";
    }

    static bool SafeUrl(string u)
    {
        Uri x;
        return u != null && u.Length <= 4096 && !Regex.IsMatch(u, @"[\s\x00-\x1f\x7f]")
            && Uri.TryCreate(u, UriKind.Absolute, out x) && (x.Scheme == Uri.UriSchemeHttps || x.Scheme == Uri.UriSchemeHttp);
    }

    // "Show in folder" only for files this helper saved, never an arbitrary path.
    static void Reveal(string path)
    {
        if (string.IsNullOrEmpty(path) || !Recent(LoadSettings()).Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return;
        if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
    }

    static List<string> Recent(Dictionary<string, object> s)
    {
        var list = new List<string>();
        object o;
        if (s.TryGetValue("recent", out o) && o is IEnumerable && !(o is string))
            foreach (object x in (IEnumerable)o) if (x is string) list.Add((string)x);
        return list;
    }

    static void Remember(Dictionary<string, object> settings, string path)
    {
        var s = LoadSettings(); // re-read: another save may have finished meanwhile
        foreach (var kv in settings) if (kv.Key != "recent") s[kv.Key] = kv.Value;
        List<string> list = Recent(s);
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 20) list.RemoveRange(20, list.Count - 20);
        s["recent"] = list;
        SaveSettings(s);
    }

    // ------------------------------------------------------------------ tools: yt-dlp, deno, ffmpeg

    static bool ToolsReady()
    {
        return File.Exists(Tool("yt-dlp.exe")) && File.Exists(Tool("deno.exe")) && File.Exists(Tool("ffmpeg.exe")) && File.Exists(Tool("ffprobe.exe"));
    }

    // First run only, about 150 MB. Official release builds straight from GitHub, over https, each one
    // checked against the SHA-256 its project publishes before anything is unpacked or run.
    static void EnsureTools() { EnsureTools(null); }
    static void EnsureTools(Action<string, double> report)
    {
        if (ToolsReady()) return;
        using (var lockAll = new Mutex(false, "optima_save_setup"))
        {
            lockAll.WaitOne();
            try
            {
                Directory.CreateDirectory(Bin);
                Action<string, double, double, double> step = (what, from, span, p) =>
                {
                    double overall = from + span * p;
                    if (report != null) report(what, overall);
                    else Send(new { type = "setup", percent = Math.Round(overall * 100), status = "first-time setup: " + what });
                };
                if (!File.Exists(Tool("yt-dlp.exe")))
                {
                    const string rel = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
                    string f = Fetch(rel + "yt-dlp.exe", p => step("yt-dlp", 0, 0.12, p));
                    Verify(f, rel + "SHA2-256SUMS", "yt-dlp.exe");
                    Place(f, Tool("yt-dlp.exe"));
                }
                if (!File.Exists(Tool("deno.exe")))
                {
                    const string rel = "https://github.com/denoland/deno/releases/latest/download/", zip = "deno-x86_64-pc-windows-msvc.zip";
                    string f = Fetch(rel + zip, p => step("deno", 0.12, 0.28, p));
                    Verify(f, rel + zip + ".sha256sum", zip);
                    Unzip(f, n => n == "deno.exe");
                }
                if (!File.Exists(Tool("ffmpeg.exe")) || !File.Exists(Tool("ffprobe.exe")))
                {
                    const string rel = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/", zip = "ffmpeg-master-latest-win64-gpl-shared.zip";
                    string f = Fetch(rel + zip, p => step("ffmpeg", 0.40, 0.60, p));
                    Verify(f, rel + "checksums.sha256", zip);
                    Unzip(f, n => n != "ffplay.exe" && (n.EndsWith(".exe") || n.EndsWith(".dll")));
                }
                step("done", 1, 0, 0);
            }
            finally { lockAll.ReleaseMutex(); }
        }
    }

    // Set while the self-updater runs: its requests may only end up on GitHub's own hosts.
    static string[] onlyHosts;
    static readonly string[] GitHubHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    static HttpWebResponse Get(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "optima-save";
        var res = (HttpWebResponse)req.GetResponse();
#if TESTMODE
        bool local = res.ResponseUri.Host == "127.0.0.1";
#else
        bool local = false;
#endif
        if (res.ResponseUri.Scheme != Uri.UriSchemeHttps && !local) { res.Close(); throw new Exception("a download was redirected off https, so it was stopped"); }
        if (onlyHosts != null && !local && Array.IndexOf(onlyHosts, res.ResponseUri.Host.ToLowerInvariant()) < 0)
        { res.Close(); throw new Exception("an update download went somewhere other than github, so it was stopped"); }
        return res;
    }

    // Downloads into the tools folder under a temp name and returns that path.
    static string Fetch(string url, Action<double> progress)
    {
        string part = Path.Combine(Home, Path.GetFileName(new Uri(url).AbsolutePath) + ".download");
        using (HttpWebResponse res = Get(url))
        using (Stream src = res.GetResponseStream())
        using (var dst = File.Create(part))
        {
            long total = res.ContentLength, got = 0;
            if (total > 600L * 1024 * 1024) throw new Exception("a download was unexpectedly large, so it was stopped");
            var buf = new byte[1 << 16];
            int n;
            double lastP = -1;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (cancelled) throw new OperationCanceledException();
                dst.Write(buf, 0, n);
                got += n;
                double p = total > 0 ? (double)got / total : 0;
                if (p - lastP >= 0.01) { lastP = p; progress(p); }
            }
        }
        return part;
    }

    static void Verify(string file, string sumsUrl, string asset)
    {
        string sums;
        using (HttpWebResponse res = Get(sumsUrl))
        using (var r = new StreamReader(res.GetResponseStream()))
        {
            var buf = new char[64 * 1024];
            sums = new string(buf, 0, r.ReadBlock(buf, 0, buf.Length));
        }
        // "<hash>  <file>" lines (yt-dlp, ffmpeg) or PowerShell's "Hash : <hash>" block (deno)
        Match m = Regex.Match(sums, @"(?im)^([0-9a-f]{64})\s+\*?" + Regex.Escape(asset) + @"\s*$");
        if (!m.Success && sums.IndexOf(asset, StringComparison.OrdinalIgnoreCase) >= 0) m = Regex.Match(sums, @"(?i)\bHash\s*:\s*([0-9a-f]{64})\b");
        string got;
        using (var sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(file))
            got = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        if (!m.Success || !string.Equals(got, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new Exception(asset + " didn't match its published checksum, so it wasn't installed. try again later");
        }
    }

    static void Place(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }

    // Unpacks only plain file names into the tools folder: an entry can't climb out with "..\" paths.
    static void Unzip(string zip, Func<string, bool> keep)
    {
        using (ZipArchive z = ZipFile.OpenRead(zip))
            foreach (ZipArchiveEntry e in z.Entries)
            {
                string n = e.FullName.Replace('\\', '/');
                string name = n.Substring(n.LastIndexOf('/') + 1);
                if (name.Length == 0 || name.Contains("..") || name.IndexOf(':') >= 0 || !keep(name)) continue;
                e.ExtractToFile(Tool(name), true);
            }
        File.Delete(zip);
    }

    // yt-dlp updates itself weekly, and once more right away when a save fails. Its updater checks
    // the release's checksums itself.
    static bool Update(Dictionary<string, object> s, bool afterFailure)
    {
        long now = DateTime.UtcNow.Ticks, last = 0;
        long.TryParse(Str(s, "updatedAt"), out last);
        TimeSpan age = TimeSpan.FromTicks(now - last);
        if (afterFailure ? age < TimeSpan.FromHours(1) : age < TimeSpan.FromDays(7)) return false;
        Send(new { type = "progress", percent = 0, status = "updating yt-dlp" });
        using (var m = new Mutex(false, "optima_save_update"))
        {
            m.WaitOne();
            try { Run(Tool("yt-dlp.exe"), new List<string> { "-U" }, null, null, 90000); } // never hold a save up for long
            finally { m.ReleaseMutex(); }
        }
        s["updatedAt"] = now.ToString();
        SaveSettings(s);
        return true;
    }

    // The login for LoginSites comes from the Firefox-family profile used most recently. yt-dlp reads it
    // locally and only sends each site its own cookies, the same as the browser would.
    static string BrowserProfile()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string root in new[] { @"zen\Profiles", @"Mozilla\Firefox\Profiles", @"Floorp\Profiles", @"librewolf\Profiles", @"Waterfox\Profiles" })
        {
            string dir = Path.Combine(roaming, root);
            if (!Directory.Exists(dir)) continue;
            foreach (string p in Directory.GetDirectories(dir))
                foreach (string f in new[] { "cookies.sqlite", "cookies.sqlite-wal" })
                {
                    string c = Path.Combine(p, f);
                    if (File.Exists(c) && File.GetLastWriteTimeUtc(c) > bestTime) { bestTime = File.GetLastWriteTimeUtc(c); best = p; }
                }
        }
        return best;
    }

    static string Site(string url)
    {
        string h = "";
        try { h = new Uri(url).Host.ToLowerInvariant(); } catch { }
        Func<string, bool> on = d => h == d || h.EndsWith("." + d);
        if (on("youtube.com") || on("youtu.be") || on("youtube-nocookie.com")) return "youtube";
        if (on("instagram.com")) return "instagram";
        if (on("tiktok.com")) return "tiktok";
        if (on("x.com") || on("twitter.com")) return "x";
        if (on("facebook.com") || on("fb.watch")) return "facebook";
        if (on("threads.net") || on("threads.com")) return "threads";
        if (on("reddit.com") || on("redd.it")) return "reddit";
        return h.StartsWith("www.") ? h.Substring(4) : (h.Length > 0 ? h : "the site");
    }

    // ------------------------------------------------------------------ install

    // Double-clicking the exe: put it in %LOCALAPPDATA%\optima save, connect it to every browser, then the
    // setup window (tools + "connect your browsers"). --quiet does the same without a window.
    static int Install(bool quiet)
    {
        Directory.CreateDirectory(Home);
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            // a save may be running from the old copy: a running exe can be renamed, never overwritten
            string old = Path.Combine(Home, "optima-save.old.exe");
            try { File.Delete(old); } catch { }
            if (File.Exists(InstalledExe)) try { File.Move(InstalledExe, old); } catch { }
            File.Copy(self, InstalledExe, true);
        }
        Refresh();
        Log("installed " + AppVersion);

        if (quiet)
        {
            EnsureTools((what, p) => Console.WriteLine("setup " + what + " " + Math.Round(p * 100) + "%"));
            Console.WriteLine("installed: " + InstalledExe);
            return 0;
        }
        return SetupWindow();
    }

    // Everything an install (or an update's warm-up run) keeps in shape: the browser connections, the
    // Apps & features entry, the Chrome extension folder and WebView2's loader.
    static void Refresh()
    {
        Directory.CreateDirectory(Home);
        string manifest = Path.Combine(Home, HostName + ".json");
        File.WriteAllText(manifest, Json.Serialize(new Dictionary<string, object> {
            { "name", HostName }, { "description", "optima save helper" }, { "path", InstalledExe }, { "type", "stdio" },
            { "allowed_extensions", new[] { ExtensionId } },
        }));
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("", manifest);
        // Chrome, Chromium, Brave and Edge read a manifest with allowed_origins instead.
        string chromium = Path.Combine(Home, HostName + "_chromium.json");
        File.WriteAllText(chromium, Json.Serialize(new Dictionary<string, object> {
            { "name", HostName }, { "description", "optima save helper" }, { "path", InstalledExe }, { "type", "stdio" },
            { "allowed_origins", new[] { ChromeOrigin } },
        }));
        foreach (string key in ChromiumKeys)
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key + HostName)) k.SetValue("", chromium);

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "optima save");
            k.SetValue("DisplayVersion", AppVersion);
            k.SetValue("Publisher", "optima machina");
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("InstallLocation", Home);
            k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
            k.SetValue("URLInfoAbout", "https://optimamachina.world");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", 160 * 1024, RegistryValueKind.DWord); // KB, with the tools
        }

        // The Chrome / Brave / Edge extension lives in this folder (loaded unpacked, the stores don't take
        // downloaders), so updates of the helper update it too.
        Directory.CreateDirectory(ChromeDir);
        foreach (string name in new[] { "manifest.json", "background.js", "icon16.png", "icon32.png", "icon48.png", "icon128.png" })
        {
            byte[] b = Resource("chrome/" + name);
            if (b != null) File.WriteAllBytes(Path.Combine(ChromeDir, name), b);
        }
        EnsureLoader();
    }

    static int Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(RegKey, false);
        foreach (string key in ChromiumKeys) Registry.CurrentUser.DeleteSubKeyTree(key + HostName, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        foreach (string d in new[] { Bin, Path.Combine(Home, "cache"), Path.Combine(Home, "webview"), ChromeDir })
            try { Directory.Delete(d, true); } catch { }
        foreach (string f in Directory.Exists(Home) ? Directory.GetFiles(Home) : new string[0])
            try { File.Delete(f); } catch { }
        // the running exe can't delete itself: a hidden command removes the folder a moment after it exits
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Home + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
        MessageBox.Show("optima save is removed from this PC.\n\nTo finish, remove the extension in your browser too (its add-ons or extensions page).", "optima save");
        return 0;
    }

    // ------------------------------------------------------------------ updates
    // Once a day, after a save (never during one), a separate quiet run asks GitHub for latest.json. It
    // must carry a valid signature from the update key; then the new exe is downloaded, its SHA-256
    // checked, swapped in (the old one renamed, since it may be running), and run once with --warm so
    // Windows' first-run scan happens now, not on your next click. The Firefox-family extension updates
    // through the browser itself; the Chrome folder is rewritten by that warm-up run.

    static void StartUpdateCheck()
    {
#if !TESTMODE
        try
        {
            long last;
            long.TryParse(Str(LoadSettings(), "selfCheckedAt"), out last);
            if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last) < TimeSpan.FromHours(20) || !File.Exists(InstalledExe)) return;
            // through the shell, so the browser's pipes are not inherited and the browser isn't kept waiting
            Process.Start(new ProcessStartInfo(InstalledExe, "--update") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
#endif
    }

    static int UpdateSelf()
    {
        using (var m = new Mutex(false, "optima_save_self_update"))
        {
            if (!m.WaitOne(0)) return 0;
            try
            {
                var s = LoadSettings();
                s["selfCheckedAt"] = DateTime.UtcNow.Ticks.ToString();
                SaveSettings(s);
#if !TESTMODE
                onlyHosts = GitHubHosts;
#endif
                // the signature is checked on the exact bytes served, before anything in them is read
                byte[] raw = FetchBytes(LatestUrl, 16 * 1024);
                byte[] signature = Convert.FromBase64String(Encoding.ASCII.GetString(FetchBytes(LatestUrl + ".sig", 4096)).Trim());
                if (!SignedByUs(raw, signature)) { Log("update refused: bad signature"); return 1; }
                var info = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
                string version = Str(info, "version"), url = Str(info, "url"), sha = Str(info, "sha256");
                System.Version theirs, ours = new System.Version(AppVersion);
                if (version == null || !System.Version.TryParse(version, out theirs) || theirs <= ours) return 0;
                if (version == Str(s, "skipVersion")) return 0; // it failed its start-up check here before
                if (url == null || !url.StartsWith(ReleaseBase + "download/", StringComparison.Ordinal) || sha == null || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$"))
                { Log("update refused: odd release info"); return 1; }

                string tmp = Fetch(url, p => { });
                if (!string.Equals(Sha256(tmp), sha, StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); Log("update refused: checksum"); return 1; }
                string old = Path.Combine(Path.GetDirectoryName(InstalledExe), "optima-save.old.exe");
                try { File.Delete(old); } catch { }
                File.Move(InstalledExe, old);
                File.Move(tmp, InstalledExe);
                // The new version must start and finish its warm-up cleanly, or the old one goes back in place
                // (and this version is skipped from now on): a broken update can never strand the browser.
                bool healthy = false;
                try
                {
                    using (Process warm = Process.Start(new ProcessStartInfo(InstalledExe, "--warm") { UseShellExecute = false, CreateNoWindow = true }))
                    {
                        healthy = warm.WaitForExit(120000) && warm.ExitCode == 0;
                        if (!warm.HasExited) try { warm.Kill(); warm.WaitForExit(5000); } catch { }
                    }
                }
                catch { }
                if (!healthy)
                {
                    try { File.Delete(InstalledExe); } catch { }
                    File.Move(old, InstalledExe);
                    s = LoadSettings();
                    s["skipVersion"] = version;
                    SaveSettings(s);
                    Log("update to " + version + " didn't start cleanly; rolled back to " + AppVersion);
                    return 1;
                }
                Log("updated " + AppVersion + " → " + version);
                return 0;
            }
            catch (Exception e) { Log("update check: " + e.Message); return 1; }
            finally { m.ReleaseMutex(); }
        }
    }

    static bool SignedByUs(byte[] data, byte[] signature)
    {
        using (var rsa = new RSACng())
        {
            rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(UpdateKeyModulus), Exponent = Convert.FromBase64String(UpdateKeyExponent) });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }

    static byte[] FetchBytes(string url, int max)
    {
        using (HttpWebResponse res = Get(url))
        using (Stream src = res.GetResponseStream())
        using (var mem = new MemoryStream())
        {
            var buf = new byte[8192];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                mem.Write(buf, 0, n);
                if (mem.Length > max) throw new Exception("too long");
            }
            return mem.ToArray();
        }
    }

    static string FetchText(string url, int max)
    {
        using (HttpWebResponse res = Get(url))
        using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
        {
            var buf = new char[max];
            int n = r.ReadBlock(buf, 0, max);
            if (n >= max) throw new Exception("too long");
            return new string(buf, 0, n);
        }
    }

    static string Sha256(string file)
    {
        using (var sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
    }

    // When the browser couldn't tell us the page title (Zen often can't), ask the site's own public
    // oEmbed endpoint: one small request to the same site the video comes from, 2.5 s at most.
    static string LookupTitle(string url, string site)
    {
        string api = site == "youtube" ? "https://www.youtube.com/oembed?format=json&url="
            : site == "tiktok" ? "https://www.tiktok.com/oembed?url="
            : site == "vimeo.com" ? "https://vimeo.com/api/oembed.json?url=" : null;
        if (api == null) return null;
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(api + Uri.EscapeDataString(url));
            req.UserAgent = "optima-save";
            req.Timeout = req.ReadWriteTimeout = 2500;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                if (res.ResponseUri.Scheme != Uri.UriSchemeHttps) return null;
                var buf = new char[64 * 1024];
                var o = Json.Deserialize<Dictionary<string, object>>(new string(buf, 0, r.ReadBlock(buf, 0, buf.Length)));
                string t = Str(o, "title");
                return t == null ? null : Regex.Replace(t, @"#[\p{L}\p{N}_]+", "").Trim();
            }
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ the trim window
    // An optima window with the video, a timeline and two handles. Lives in WebView2 (Edge's engine, part
    // of Windows 11) in InPrivate mode: no history, no cookies kept, no devtools, can't navigate away,
    // can't open windows or downloads, denies every permission. The page is ours (trim.html, inside this
    // exe); the site only supplies data: a title shown as text and a video address used as the video's src.

    sealed class TrimChoice { public bool Whole; public double Start, End; }

    static TrimChoice AskTrim(string url, string site, string title, out string error)
    {
        error = null;
        string dir = Path.Combine(Path.GetTempPath(), "optima-save-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "trim.html"), TrimPage(title), new UTF8Encoding(false));
            using (var form = new TrimForm(url, site, dir))
            {
                form.ShowDialog();
                error = form.Error;
                return form.Choice;
            }
        }
        finally { RemoveSoon(dir); } // the engine lets go of its profile a moment after the window closes
    }

    // trim.html with the font and the data filled in. The data is JSON with every character that could
    // end the script tag escaped, so a hostile title stays a string.
    static string TrimPage(string title)
    {
        string page = Encoding.UTF8.GetString(Resource("trim.html"));
        string json = Json.Serialize(new Dictionary<string, object> { { "title", title ?? "" } })
            .Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        return page.Replace("/*FONT*/", "data:font/woff2;base64," + Convert.ToBase64String(Resource("inter.woff2")))
                   .Replace("/*LOGO*/", "data:image/png;base64," + Convert.ToBase64String(Resource("logo-small.png")))
                   .Replace("/*DATA*/null", json);
    }

    sealed class TrimForm : Form
    {
        public TrimChoice Choice;
        public string Error;
        readonly WebView2 web = new WebView2();
        readonly string url, site, dir;
        volatile bool previewing, closing;
        double duration;

        public TrimForm(string url, string site, string dir)
        {
            this.url = url; this.site = site; this.dir = dir;
            Text = "optima save · trim";
            BackColor = ColorTranslator.FromHtml("#e7e3dd");
            StartPosition = FormStartPosition.CenterScreen;
            float k;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size((int)(960 * k), (int)(640 * k));
            MinimumSize = new Size((int)(760 * k), (int)(500 * k));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            web.CreationProperties = EngineProfile(dir); // our own page, no ads: the preview may play sound without a click
            Controls.Add(web);
            Load += async (s, e) => await Begin();
            Shown += (s, e) => { Activate(); ToFront(Handle); };
            FormClosing += (s, e) => { closing = true; if (previewing) KillRunning(); };
            var watch = new System.Windows.Forms.Timer { Interval = 300 };
            watch.Tick += (s, e) => { if (cancelled) Close(); }; // middle-click / browser closed
            watch.Start();
        }

        async Task Begin()
        {
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(EnsureLoader());
                await web.EnsureCoreWebView2Async(null);
            }
            catch (Exception ex)
            {
                Log("trim window: " + ex.Message);
                Error = "the trim window needs Microsoft Edge WebView2, which isn't working on this pc";
                Close();
                return;
            }
            CoreWebView2 c = web.CoreWebView2;
            LockDown(c, "optima-save.trim", dir);
            c.WebMessageReceived += OnMessage;
            c.Navigate("https://optima-save.trim/trim.html");
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://optima-save.trim/", StringComparison.Ordinal)) return;
            Dictionary<string, object> m;
            try { m = Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
            if (m == null) return;
            string type = Str(m, "type");
            if (type == "ready") Prepare();
            else if (type == "cancel") Close();
            else if (type == "whole") { Choice = new TrimChoice { Whole = true }; Close(); }
            else if (type == "save")
            {
                double a = Num(m, "start"), b = Num(m, "end"), max = duration > 0 ? duration + 1 : 1e7;
                if (double.IsNaN(a) || double.IsNaN(b) || a < 0 || b > max || b - a < 0.05) return;
                Choice = new TrimChoice { Start = a, End = b };
                Close();
            }
            else if (type == "preview-failed" && !previewing) StartPreviewFile();
        }

        static double Num(Dictionary<string, object> m, string key)
        {
            object o;
            if (!m.TryGetValue(key, out o) || o == null || o is string || o is bool) return double.NaN;
            try { double d = Convert.ToDouble(o, CultureInfo.InvariantCulture); return double.IsInfinity(d) ? double.NaN : d; }
            catch { return double.NaN; }
        }

        void Post(object o)
        {
            string json = Json.Serialize(o);
            try { BeginInvoke((Action)(() => { if (!closing && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); })); } catch { }
        }

        // The preview is always a small local copy (360p, picture and sound in one file): playback and
        // scrubbing are instant, where streaming straight from YouTube gets throttled into stutters.
        void Prepare()
        {
            var t = new Thread(PreviewFile);
            t.IsBackground = true;
            t.Start();
        }

        void StartPreviewFile()
        {
            var t = new Thread(PreviewFile);
            t.IsBackground = true;
            t.Start();
        }

        void PreviewFile()
        {
            previewing = true;
            Post(new { type = "status", text = "loading the preview" });
            var a = BaseArgs();
            a.AddRange(new[] {
                "-f", "bv*[height<=360][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=360]+ba/b[height<=480]/bv*[height<=480]+ba/b/bv*+ba",
                "-S", "res:360,vcodec:h264,acodec:aac", "-N", "4", "--merge-output-format", "mp4", "--remux-video", "mp4",
                "--no-simulate", "--print", "before_dl:@@D %(duration)s", "--print", "before_dl:@@F %(format_id)s",
                "--newline", "--progress", "--progress-template", "download:@@P %(progress._percent_str)s", "-P", dir, "-o", "preview.%(ext)s" });
            if (site == "instagram") { string p = BrowserProfile(); if (p != null) { a.Add("--cookies-from-browser"); a.Add("firefox:" + p); } }
            a.Add("--");
            a.Add(url);
            double last = -10, prev = -1;
            int parts = 1, part = 0;
            var gate = new object();
            Action<string> progress = line =>
            {
                lock (gate)
                {
                    double p;
                    if (line.StartsWith("@@D "))
                    {
                        if (double.TryParse(line.Substring(4), NumberStyles.Float, CultureInfo.InvariantCulture, out p) && p > 0)
                        { duration = p; Post(new { type = "info", duration = p }); }
                        return;
                    }
                    if (line.StartsWith("@@F ")) { parts = Math.Min(2, line.Split('+').Length); return; }
                    Match m = Regex.Match(line, @"^@@P\s+([\d.]+)%");
                    if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out p)) return;
                    if (p < prev - 50 && part < parts - 1) part++; // the sound started after the picture
                    prev = p;
                    double overall = parts == 1 ? p : (part == 0 ? p * 0.85 : 85 + p * 0.15);
                    if (overall - last >= 2) { last = overall; Post(new { type = "status", text = "loading the preview · " + Math.Round(overall) + "%" }); }
                }
            };
            Run(Tool("yt-dlp.exe"), a, progress, progress);
            previewing = false;
            if (closing) return;
            if (File.Exists(Path.Combine(dir, "preview.mp4"))) Post(new { type = "src", src = "https://optima-save.trim/preview.mp4" });
            else Post(new { type = "status", text = "no preview for this one. you can still save the whole video" });
        }
    }

    static List<string> BaseArgs()
    {
        return new List<string> { "--ignore-config", "--no-playlist", "-I", "1", "--no-warnings", "--encoding", "utf-8",
            "--cache-dir", Path.Combine(Home, "cache"), "--ffmpeg-location", Bin, "--js-runtimes", "deno:" + Tool("deno.exe") };
    }

    static void PlayDone()
    {
#if !TESTMODE
        try { new SoundPlayer(new MemoryStream(Resource("done.wav"))).Play(); } catch { }
#endif
    }

    // WebView2's two managed libraries ride inside this exe; this hands them to .NET when asked.
    static readonly Dictionary<string, Assembly> embedded = new Dictionary<string, Assembly>();
    static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
        lock (embedded)
        {
            Assembly a;
            if (!embedded.TryGetValue(name, out a))
            {
                byte[] b = Resource(name + ".dll");
                if (b == null) return null;
                embedded[name] = a = Assembly.Load(b);
            }
            return a;
        }
    }

    // The native loader has to be a real file; it's unpacked next to the tools when missing or changed.
    static string EnsureLoader()
    {
        byte[] want = Resource("WebView2Loader.dll");
        string file = Path.Combine(Bin, "WebView2Loader.dll");
        bool same = File.Exists(file);
        if (same)
        {
            byte[] have = File.ReadAllBytes(file);
            same = have.Length == want.Length;
            for (int i = 0; same && i < have.Length; i++) same = have[i] == want[i];
        }
        if (!same) { Directory.CreateDirectory(Bin); File.WriteAllBytes(file, want); }
        return Bin;
    }

    static byte[] Resource(string name)
    {
        using (Stream r = typeof(OptimaSave).Assembly.GetManifestResourceStream(name))
        {
            if (r == null) return null;
            var b = new byte[r.Length];
            ReadAll(r, b);
            return b;
        }
    }

    // Both optima windows (trim, setup) run our own page in WebView2 under the same rules: InPrivate, no
    // devtools, no context menu, no script dialogs, no browser shortcuts, no autofill, no host objects,
    // no navigation outside the page's own folder, no new windows, no downloads, every permission denied.
    static void LockDown(CoreWebView2 c, string host, string dir)
    {
        CoreWebView2Settings st = c.Settings;
#if TESTMODE
        st.AreDevToolsEnabled = true;
#else
        st.AreDevToolsEnabled = false;
#endif
        st.AreDefaultContextMenusEnabled = false;
        st.AreDefaultScriptDialogsEnabled = false;
        st.AreBrowserAcceleratorKeysEnabled = false;
        st.AreHostObjectsAllowed = false;
        st.IsStatusBarEnabled = false;
        st.IsZoomControlEnabled = false;
        st.IsPasswordAutosaveEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.IsWebMessageEnabled = true;
        c.SetVirtualHostNameToFolderMapping(host, dir, CoreWebView2HostResourceAccessKind.DenyCors);
        string origin = "https://" + host + "/";
        c.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith(origin, StringComparison.Ordinal)) e.Cancel = true; };
        c.NewWindowRequested += (s, e) => e.Handled = true;
        c.DownloadStarting += (s, e) => e.Cancel = true;
        c.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    static CoreWebView2CreationProperties EngineProfile(string dir)
    {
        return new CoreWebView2CreationProperties
        {
            UserDataFolder = dir + "-engine", // its own throwaway profile: two windows can't collide, nothing outlives it
            IsInPrivateModeEnabled = true,
#if TESTMODE
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --remote-debugging-port=9333", // tests drive the page
#else
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
#endif
        };
    }

    static void RemoveSoon(string dir)
    {
        foreach (string d in new[] { dir, dir + "-engine" })
            for (int i = 0; i < 20 && Directory.Exists(d); i++) { try { Directory.Delete(d, true); } catch { Thread.Sleep(300); } }
    }

    // ------------------------------------------------------------------ the setup window
    // Double-click the exe: the helper installs itself, downloads the tools, then lists the browsers it
    // finds with one button each. Firefox-family browsers get the Mozilla-signed add-on from GitHub (they
    // ask before installing); Chrome-family browsers open their extensions page, with the folder to load
    // copied to the clipboard (their stores don't accept video downloaders, so it goes in unpacked).

    sealed class BrowserInfo { public string Name, Exe, Family; }

    static List<BrowserInfo> FindBrowsers()
    {
        var list = new List<BrowserInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] firefox = { "firefox.exe", "zen.exe", "floorp.exe", "librewolf.exe", "waterfox.exe" };
        string[] chromium = { "chrome.exe", "brave.exe", "msedge.exe", "vivaldi.exe", "chromium.exe" };
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            using (RegistryKey clients = hive.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet"))
            {
                if (clients == null) continue;
                foreach (string sub in clients.GetSubKeyNames())
                    using (RegistryKey b = clients.OpenSubKey(sub))
                    using (RegistryKey cmd = b == null ? null : b.OpenSubKey(@"shell\open\command"))
                    {
                        if (cmd == null) continue;
                        string exe = ExeFromCommand(Convert.ToString(cmd.GetValue("")));
                        if (exe == null || !File.Exists(exe) || !seen.Add(exe)) continue;
                        string file = Path.GetFileName(exe).ToLowerInvariant();
                        string family = Array.IndexOf(firefox, file) >= 0 ? "firefox" : Array.IndexOf(chromium, file) >= 0 ? "chromium" : null;
                        if (family == null) continue;
                        string name = Convert.ToString(b.GetValue(""));
                        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) name = Path.GetFileNameWithoutExtension(exe);
                        list.Add(new BrowserInfo { Name = name.Trim(), Exe = exe, Family = family });
                    }
            }
        return list;
    }

    static string ExeFromCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd.Substring(1, end - 1) : null; }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? cmd.Substring(0, exe + 4) : null;
    }

    static int SetupWindow()
    {
        Application.EnableVisualStyles();
        string dir = Path.Combine(Path.GetTempPath(), "optima-save-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string page = Encoding.UTF8.GetString(Resource("setup.html"))
                .Replace("/*FONT*/", "data:font/woff2;base64," + Convert.ToBase64String(Resource("inter.woff2")));
            File.WriteAllText(Path.Combine(dir, "setup.html"), page, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(dir, "logo.png"), Resource("logo-320.png"));
            using (var form = new SetupForm(dir))
            {
                Application.Run(form);
                if (form.RemoveAfter) return Uninstall();
                return 0;
            }
        }
        finally { RemoveSoon(dir); }
    }

    sealed class SetupForm : Form
    {
        public bool RemoveAfter;
        readonly WebView2 web = new WebView2();
        readonly string dir;
        List<BrowserInfo> browsers = new List<BrowserInfo>();
        bool toolsStarted;

        public SetupForm(string dir)
        {
            this.dir = dir;
            Text = "optima save";
            BackColor = ColorTranslator.FromHtml("#e7e3dd");
            StartPosition = FormStartPosition.CenterScreen;
            float k;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size((int)(560 * k), (int)(720 * k));
            MinimumSize = new Size((int)(460 * k), (int)(560 * k));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            web.CreationProperties = EngineProfile(dir);
            Controls.Add(web);
            Load += async (s, e) => await Begin();
            Shown += (s, e) => { Activate(); ToFront(Handle); };
            FormClosing += (s, e) => cancelled = true; // stops a tools download that's still running
        }

        async Task Begin()
        {
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(EnsureLoader());
                await web.EnsureCoreWebView2Async(null);
            }
            catch (Exception ex)
            {
                // no WebView2: the install itself is done; tell it plainly and fetch the tools anyway
                Log("setup window: " + ex.Message);
                Hide();
                try { EnsureTools((w, p) => { }); } catch { }
                MessageBox.Show("optima save is installed.\n\nRight-click any video in your browser and choose \"save video as…\". If the browser doesn't have the optima save extension yet, get it from github.com/" + Repo + ".", "optima save");
                Close();
                return;
            }
            LockDown(web.CoreWebView2, "optima-save.setup", dir);
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.Navigate("https://optima-save.setup/setup.html");
        }

        void Post(object o)
        {
            string json = Json.Serialize(o);
            try { BeginInvoke((Action)(() => { if (!IsDisposed && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); })); } catch { }
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith("https://optima-save.setup/", StringComparison.Ordinal)) return;
            Dictionary<string, object> m;
            try { m = Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
            if (m == null) return;
            string type = Str(m, "type");
            if (type == "ready")
            {
                browsers = FindBrowsers();
                var shown = new List<object>();
                for (int i = 0; i < browsers.Count; i++) shown.Add(new { i = i, name = browsers[i].Name, family = browsers[i].Family });
                Post(new { type = "state", version = AppVersion, browsers = shown, folder = ChromeDir });
                if (!toolsStarted) { toolsStarted = true; StartTools(); }
            }
            else if (type == "connect")
            {
                object o;
                int i = m.TryGetValue("i", out o) && o is int ? (int)o : -1;
                if (i < 0 || i >= browsers.Count) return; // only a browser this window found itself
                Connect(browsers[i]);
            }
            else if (type == "folder") { if (Directory.Exists(ChromeDir)) Process.Start("explorer.exe", "\"" + ChromeDir + "\""); }
            else if (type == "uninstall") { RemoveAfter = true; Close(); }
            else if (type == "close") Close();
        }

        void Connect(BrowserInfo b)
        {
            try
            {
                if (b.Family == "firefox")
                    Process.Start(new ProcessStartInfo(b.Exe, "\"" + XpiUrl + "\"") { UseShellExecute = false });
                else
                {
                    Clipboard.SetText(ChromeDir);
                    Process.Start(new ProcessStartInfo(b.Exe, "chrome://extensions") { UseShellExecute = false });
                }
                Log("setup: opened " + b.Family + " browser");
            }
            catch (Exception ex) { Log("setup: couldn't open the browser: " + ex.Message); }
        }

        void StartTools()
        {
            var t = new Thread(() =>
            {
                try
                {
                    if (ToolsReady()) { Post(new { type = "tools-done", ok = true, text = "ready. yt-dlp, deno and ffmpeg are in place and keep themselves current." }); return; }
                    EnsureTools((what, p) =>
                    {
                        if (what != "done") Post(new { type = "tools", pct = Math.Round(p * 100), text = "downloading " + what + " · " + Math.Round(p * 100) + "%" });
                    });
                    Post(new { type = "tools-done", ok = true, text = "ready. every download was checked against its published checksum." });
                }
                catch (Exception ex)
                {
                    if (!cancelled) Post(new { type = "tools-done", ok = false, text = "the download didn't finish: " + ex.Message + ". open optima save again to retry." });
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    // ------------------------------------------------------------------ the status pill
    // Zen tucks extension buttons away, so the helper shows its own state: a small plastic pill in the
    // bottom-right corner with the progress ring, then "saved" (click: show the file) or the error.
    // It never takes focus and asks nothing. Several saves stack upwards.

    static Pill pill;
    static string pillName = "";
    static readonly ManualResetEvent pillClosed = new ManualResetEvent(true);

    static void PillShow(string name)
    {
        if (pill != null) return;
        pillName = name;
        pillClosed.Reset();
        var ready = new ManualResetEvent(false);
        var t = new Thread(() =>
        {
            Semaphore slotLock = null;
            int slot = 0;
            for (; slot < 4; slot++)
            {
                var sem = new Semaphore(1, 1, "optima_save_pill_" + slot);
                if (sem.WaitOne(0)) { slotLock = sem; break; }
                sem.Dispose();
            }
            if (slotLock == null) slot = 0;
            Application.EnableVisualStyles();
            var p = new Pill(slot);
            p.Click += (s, e) => { if (p.File != null && File.Exists(p.File)) { Process.Start("explorer.exe", "/select,\"" + p.File + "\""); p.Close(); } };
            // the × stops the save: yt-dlp / ffmpeg are killed and the half-done temp files removed
            p.OnCancel = () => { userCancelled = true; cancelled = true; KillRunning(); };
            p.Shown += (s, e) => ready.Set();
            p.FormClosed += (s, e) => { if (slotLock != null) slotLock.Release(); pillClosed.Set(); };
            pill = p;
            Application.Run(p);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        ready.WaitOne(3000);
    }

    static void PillSet(string state, string text, double pct, string value)
    {
        Pill p = pill;
        if (p == null) return;
        try { p.BeginInvoke((Action)(() => p.Set(state, text, pct, value))); } catch { }
    }

    // Shows the last word for a few seconds, then closes; waits so the process doesn't end under it.
    static void PillEnd(string state, string text, string file, int ms)
    {
        if (pill == null && state != "error") return;
        if (pill == null) PillShow("");
        Pill p = pill;
        if (p == null) return;
        try
        {
            p.BeginInvoke((Action)(() =>
            {
                p.File = file;
                p.Set(state, text, -1, "");
                var timer = new System.Windows.Forms.Timer { Interval = ms };
                timer.Tick += (s, e) => { timer.Stop(); p.Close(); };
                timer.Start();
            }));
        }
        catch { return; }
        pillClosed.WaitOne(ms + 2000);
        pill = null;
    }

    static void Mirror(object o)
    {
        if (pill == null) return;
        Func<string, object> get = n => { var prop = o.GetType().GetProperty(n); return prop == null ? null : prop.GetValue(o, null); };
        string type = get("type") as string;
        if (type != "progress" && type != "setup") return;
        double pct = Convert.ToDouble(get("percent") ?? 0.0, CultureInfo.InvariantCulture);
        string status = get("status") as string;
        string doing = status == null || status == "finishing" ? "saving" : status;
        PillSet("busy", doing + " · " + pillName, pct, pct >= 0 ? Math.Round(pct) + "%" : "");
    }

    sealed class Pill : Form
    {
        readonly int W, H;
        readonly float k; // screen scale: 1 at 100%, 1.25 at 125%...
        static readonly Color Hi = ColorTranslator.FromHtml("#efebe6"), Lo = ColorTranslator.FromHtml("#dcd7cf"),
            Ink = ColorTranslator.FromHtml("#2b2926"), RingInk = ColorTranslator.FromHtml("#34312d"),
            Blue = ColorTranslator.FromHtml("#2a47c4"), Amber = ColorTranslator.FromHtml("#d98a2b");
        string state = "busy", text = "", value = "";
        double pct = 0;
        public string File;
        public Action OnCancel;
        RectangleF cancelBox;
        bool overCancel;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = state == "busy" && cancelBox.Contains(e.Location);
            if (over != overCancel) { overCancel = over; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (overCancel) { overCancel = false; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (state == "busy" && cancelBox.Contains(e.Location) && OnCancel != null)
            {
                Set("busy", "cancelling · " + text.Replace("saving · ", ""), pct, "");
                OnCancel();
                OnCancel = null;
                return;
            }
            base.OnMouseClick(e);
        }

        public Pill(int slot)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = ColorTranslator.FromHtml("#e7e3dd");
            using (Graphics probe = CreateGraphics()) k = probe.DpiX / 96f;
            W = (int)(330 * k); H = (int)(46 * k);
            Size = new Size(W, H);
            Rectangle wa = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            int gap = (int)(24 * k);
            Location = new Point(wa.Right - W - gap, wa.Bottom - H - gap - slot * (H + (int)(10 * k)));
            using (GraphicsPath shape = Shape(new RectangleF(0, 0, W, H))) Region = new Region(shape);
            Cursor = Cursors.Hand;
            text = "saving";
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* no activate */ | 0x80 /* tool window */ | 0x8 /* topmost */;
                return cp;
            }
        }

        static GraphicsPath Shape(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        float spin;
        System.Windows.Forms.Timer spinner;

        public void Set(string state, string text, double pct, string value)
        {
            this.state = state; this.text = text; this.pct = pct; this.value = value;
            bool unknown = state == "busy" && pct < 0;
            if (unknown && spinner == null)
            {
                spinner = new System.Windows.Forms.Timer { Interval = 40 };
                spinner.Tick += (s, e) => { spin = (spin + 14) % 360; Invalidate(); };
            }
            if (spinner != null) { if (unknown) spinner.Start(); else spinner.Stop(); }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Hi, Lo, 145f))
            using (GraphicsPath all = Shape(new RectangleF(0, 0, W, H)))
                g.FillPath(bg, all);
            using (var pen = new Pen(RingInk, 1.4f * k))
            using (GraphicsPath edge = Shape(new RectangleF(0.7f * k, 0.7f * k, W - 1.4f * k, H - 1.4f * k)))
                g.DrawPath(pen, edge);

            var c = new RectangleF(16 * k, (H - 18 * k) / 2f, 18 * k, 18 * k);
            if (state == "done")
                using (var pen = new Pen(Blue, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[] { new PointF(c.X + 2 * k, c.Y + 9.5f * k), new PointF(c.X + 7 * k, c.Y + 14 * k), new PointF(c.X + 16 * k, c.Y + 4 * k) });
            else if (state == "error")
                using (var b = new SolidBrush(Amber)) g.FillEllipse(b, c.X + 5 * k, c.Y + 5 * k, 8 * k, 8 * k);
            else
            {
                using (var track = new Pen(Color.FromArgb(60, Ink), 2.2f * k)) g.DrawEllipse(track, c);
                if (pct > 0 || pct < 0)
                    using (var arc = new Pen(Blue, 2.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        if (pct < 0) g.DrawArc(arc, c, spin - 90, 100);
                        else g.DrawArc(arc, c, -90, (float)(360 * Math.Min(pct, 100) / 100));
            }

            using (var font = new Font("Segoe UI", 9.25f))
            using (var ink = new SolidBrush(Ink))
            using (var blue = new SolidBrush(Blue))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                // while saving, a × sits at the right end; the percentage moves left of it
                bool canCancel = state == "busy" && OnCancel != null;
                float right = canCancel ? 44 * k : 18 * k;
                cancelBox = canCancel ? new RectangleF(W - 40 * k, 0, 40 * k, H) : RectangleF.Empty;
                if (canCancel)
                {
                    float cx = W - 22 * k, cy = H / 2f, r = 4.2f * k;
                    using (var pen = new Pen(overCancel ? Blue : Color.FromArgb(150, Ink), 1.8f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                        g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                    }
                }
                float vw = string.IsNullOrEmpty(value) ? 0 : g.MeasureString(value, font).Width;
                g.DrawString(text, font, ink, new RectangleF(46 * k, 0, W - 46 * k - right - vw - 6 * k, H), sf);
                if (vw > 0) g.DrawString(value, font, blue, new RectangleF(W - right - vw, 0, vw + 2, H), sf);
            }
        }
    }

    // ------------------------------------------------------------------ plumbing

    static Dictionary<string, object> LoadSettings()
    {
        try { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsFile)) ?? new Dictionary<string, object>(); }
        catch { return new Dictionary<string, object>(); }
    }

    static void SaveSettings(Dictionary<string, object> s)
    {
        try { Directory.CreateDirectory(Home); File.WriteAllText(SettingsFile, Json.Serialize(s)); } catch { }
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object o;
        return d != null && d.TryGetValue(key, out o) && o is string ? (string)o : null;
    }

    // Native messaging: a 4-byte length, then UTF-8 JSON, both ways. Oversized messages are refused.
    static Dictionary<string, object> Receive(Stream input)
    {
        var len = new byte[4];
        if (ReadAll(input, len) < 4) return null;
        int n = BitConverter.ToInt32(len, 0);
        if (n <= 0 || n > MaxMessage) return null;
        var buf = new byte[n];
        if (ReadAll(input, buf) < n) return null;
        try { return Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(buf)); }
        catch { return null; }
    }

    static int ReadAll(Stream s, byte[] buf)
    {
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        return got;
    }

    static void Send(object o)
    {
        if (output == null) return;
        byte[] b = Encoding.UTF8.GetBytes(Json.Serialize(o));
        lock (outputLock)
        {
            try { output.Write(BitConverter.GetBytes(b.Length), 0, 4); output.Write(b, 0, b.Length); output.Flush(); }
            catch { cancelled = true; }
        }
        Mirror(o);
    }

    // Children get their own empty input: they must never see (or block on) the browser's message pipe.
    static int Run(string exe, List<string> args, Action<string> onOut, Action<string> onErr, int timeoutMs = -1)
    {
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.ConvertAll(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PATH"] = Bin + ";" + Environment.GetEnvironmentVariable("PATH");
        using (var p = new Process { StartInfo = psi })
        {
            p.OutputDataReceived += (s, e) => { if (e.Data != null && onOut != null) onOut(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null && onErr != null) onErr(e.Data); };
            if (cancelled) return -1;
            p.Start();
            running = p;
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { KillRunning(); p.WaitForExit(); }
            else p.WaitForExit(); // drains the last output lines
            running = null;
            return p.ExitCode;
        }
    }

    static void KillRunning()
    {
        Process p = running;
        if (p == null) return;
        try { Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(); } catch { }
    }

    // Windows command-line quoting, the rules programs use to split their arguments.
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
