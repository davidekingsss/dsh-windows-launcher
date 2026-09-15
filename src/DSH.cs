// DSH — silent one-click launcher for DeepSeek Harness on Windows.
//
// Why an .exe instead of a .bat / .cmd?
//   A .bat/.cmd shortcut is hosted by conhost, which flashes a black window and
//   cannot be pinned to the taskbar as its own app. This file is compiled as a
//   WinExe (no console subsystem): there is never a console to show, and it owns
//   a real window handle + icon, which is exactly what a taskbar pin needs.
//
// The command it runs is exactly the user's:
//     npx @deepseek-ai/dsh web --no-open
// spawned via cmd.exe with CreateNoWindow, so npx resolves the newest published
// version on every start while nothing is ever displayed.
//
// Behaviour
//   * default (no args) -> HOST ONLY: boot the server silently, show the tray
//                          icon, never touch the browser
//   * --open / --app     -> additionally open the UI (an app window for --app)
//   * later launches     -> single instance; a relaunch just wakes the resident one
//   * tray menu          -> open, app window, copy link, restart, stop, logs,
//                           autostart, quit
//
// Authentication note (why "Open DSH" uses a tokenized URL):
//   The index route is fenced: a clean GET gets 401 (writeUnauthorized). A GET
//   carrying a valid `?token=<process launch token>` mints a signed,
//   authority-bound cookie and 303-redirects to the clean `/`. That cookie is
//   signed with a secret persisted in the Harness home and lives
//   `cookieMaxAgeDays` (default 30) days, so ONE tokenized visit is enough —
//   afterwards plain http://127.0.0.1:3080/ works with no token at all.
//   `--trusted-host` does NOT bypass this; it only widens the /api fence.
//
// TWO LOAD-BEARING RULES — breaking either reintroduces a shipped bug:
//
//   1. PUMP MESSAGES WITH Application.Run, NEVER A HAND-ROLLED LOOP.
//      A NotifyIcon context menu is tracked by Win32 TrackPopupMenu, which runs
//      its own modal message loop. A hand-rolled `while (..) { Application.DoEvents(); }`
//      pump does not satisfy it: the menu paints, then mouse tracking never
//      completes, so it cannot be hovered, clicked or dismissed — it just hangs
//      with a busy cursor. Everything now runs under Application.Run over a
//      hidden form, and the cross-process signal is turned into a form timer tick.
//
//   2. NEVER BLOCK ON I/O FROM A MENU HANDLER.
//      Probing the server, waiting for npx, opening the browser and killing
//      processes all happen on one worker thread. A menu handler may only enqueue
//      a request and return. (The first revision called an HTTP probe straight
//      from the handler; with the server down that stalled the UI thread.)
//
// `DSH.exe --selftest` runs a heartbeat that reports the worst UI-thread stall,
// which is how rule 1 is verified.
//
// Build: csc /target:winexe /optimize+ /win32icon:DSH.ico /out:DSH.exe DSH.cs

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Dsh
{
    const string AppName = "DSH";
    const int Port = 3080;
    const string NpxTarget = "@deepseek-ai/dsh";
    const string WebArg = "web";
    const string BaseUrl = "http://127.0.0.1:3080/";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "DSH";

    // The server's working directory becomes the default workspace root for a
    // freshly created session, so it must not be the install folder.
    static readonly string ServerCwd =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static readonly string Home = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSH");
    static readonly string StatePath = Path.Combine(Home, "state.txt");
    static readonly string VerbPath = Path.Combine(Home, "verb.txt");
    static readonly string AckPath = Path.Combine(Home, "ack.txt");
    static readonly string LogPath = Path.Combine(Home, "dsh-web.log");
    // Kept beside the executable rather than in %TEMP%: that directory is not
    // reliably writable for every account/token this launcher may run under,
    // and a silent failure is exactly when the trace is needed.
    static readonly string TracePath = Path.Combine(Home, "launcher.log");

    // One instance per user session: the mutex says "someone is resident",
    // the event is how a later launch (e.g. a taskbar click) pokes that instance.
    const string MutexName = @"Local\DSH.Launcher.Instance";
    const string SignalName = @"Local\DSH.Launcher.Signal";

    // ---- shared state ------------------------------------------------------
    static readonly object Gate = new object();
    static Form _pump;                    // hidden form; exists to own the message loop
    static System.Windows.Forms.Timer _watch;
    static NotifyIcon _tray;
    static Icon _icon;
    static ToolStripMenuItem _autostartItem;
    static EventWaitHandle _signal;
    static Process _server;
    static string _url = BaseUrl;
    static volatile bool _quitting;

    // ---- jobs the worker thread accepts ------------------------------------
    static readonly System.Collections.Generic.Queue<string> Jobs =
        new System.Collections.Generic.Queue<string>();
    static readonly AutoResetEvent JobReady = new AutoResetEvent(false);

    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Trace("FATAL: " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Trace("UI EXCEPTION: " + e.Exception);
        Trace("--- launch: " + string.Join(" ", args) + " ---");

        try { Directory.CreateDirectory(Home); } catch { }

        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "--host";
        // DEFAULT IS HOST ONLY: serve silently, leave a tray icon, do not touch
        // the browser. Opening a page is opt-in via --open / --app, so the
        // autostart entry and a desktop shortcut never steal focus.
        bool openWanted = verb == "--open" || verb == "--app";
        bool appWindow = verb == "--app";
        bool selfTest = verb == "--selftest";

        bool createdNew;
        using (var mutex = new Mutex(true, MutexName, out createdNew))
        {
            if (!createdNew)
            {
                // A resident instance exists: hand it the verb. Blocking verbs
                // wait for the acknowledgement so the caller can report the result.
                if (!openWanted && !selfTest) return;   // host-only relaunch: no-op
                bool blocking = verb == "--stop" || verb == "--restart"
                                || verb == "--kill-orphan" || verb == "--selftest";
                string reply = Send(verb.TrimStart('-'), blocking);
                Trace("sent verb '" + verb + "' -> " + reply);
                if (reply == "none") Info("DSH 未在运行。");
                else if (reply == "ok") Info(ReplyText(verb));
                else if (reply == "timeout") Info("DSH 没有及时响应。");
                return;
            }

            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try { _icon = new Icon(Path.Combine(AppDir(), "DSH.ico")); }
            catch { _icon = SystemIcons.Application; }

            // The hidden form is what Application.Run pumps messages for; it must
            // never be shown. Its handle is forced now so the worker can marshal
            // to the UI thread from the very first boot job.
            _pump = new Form
            {
                Text = "DSH",
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                FormBorderStyle = FormBorderStyle.None,
                Opacity = 0,
            };
            _pump.Handle.ToString();

            BuildTray();

            // RULE 1: this timer is the only thing that touches the wake-up event,
            // and it runs on the UI thread inside a real message loop.
            _watch = new System.Windows.Forms.Timer { Interval = 150 };
            _watch.Tick += (s, e) =>
            {
                if (_signal.WaitOne(0))
                {
                    string v = "open";
                    try
                    {
                        if (File.Exists(VerbPath))
                        {
                            v = File.ReadAllText(VerbPath, Encoding.UTF8).Trim();
                            File.Delete(VerbPath);
                        }
                    }
                    catch { }
                    Trace("received verb: " + v);
                    EnqueueJob(v);
                }
                if (_quitting) Application.Exit();
            };
            _watch.Start();

            if (selfTest) StartSelfTest();

            var worker = new Thread(WorkerLoop) { IsBackground = true };
            worker.Start();
            EnqueueJob("boot" + (openWanted ? "" : "-silent") + (appWindow ? "-app" : ""));

            Trace("pumping messages via Application.Run");
            Application.Run(_pump);

            _quitting = true;
            JobReady.Set();
            Cleanup();
        }
    }

    static string AppDir()
    {
        try { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
        catch { return Home; }
    }

    static string ReplyText(string verb)
    {
        switch (verb.TrimStart('-'))
        {
            case "restart": return "服务器已重启。";
            case "kill-orphan": return "残留服务器已清理。";
            case "selftest": return "自检进行中 — 结果写入启动器日志。";
            default: return "服务器已停止。";
        }
    }

    /// <summary>Append a line to launcher.log so failures are diagnosable.</summary>
    static void Trace(string message)
    {
        try
        {
            File.AppendAllText(TracePath,
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\r\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    // ---- UI-thread marshalling --------------------------------------------
    /// <summary>Run on the UI thread; safe to call from the worker.</summary>
    static void Ui(Action action)
    {
        try
        {
            if (_pump == null) { action(); return; }
            if (_pump.InvokeRequired) _pump.BeginInvoke(action);
            else action();
        }
        catch (Exception ex) { Trace("Ui marshal failed: " + ex.Message); }
    }

    // ---- boot feedback: the tray icon blinks while the server comes up -----
    static Icon _idleIcon;
    static System.Windows.Forms.Timer _blink;
    static System.Windows.Forms.Timer _ticker;
    static int _blinkPhase;
    static int _bootSeconds;
    static long _blinkStartedAt;

    /// <summary>Minimum time the startup blink stays on screen, in ms.</summary>
    const int BlinkVisibleMs = 1200;

    /// <summary>
    /// Counts elapsed seconds in the tray tooltip while the server starts.
    ///
    /// This is deliberately independent of the icon: it is the fallback that still
    /// works when the faint twin cannot be loaded, so the user always has some
    /// visible sign that something is happening. The slow part is npm resolving
    /// the newest version, whose progress we cannot know, so elapsed time is the
    /// honest signal.
    /// </summary>
    static void StartTicker()
    {
        try
        {
            _bootSeconds = 0;

            // 先闪一下再做计时。因为"服务器已在运行"这条分支不会真的启动服务器，
            // 若不加这段过渡，"正在启动"和"已就绪"都会是常亮图标，用户无法区分。
            if (PrepareBlinkIcon())
            {
                if (_blink == null)
                {
                    _blink = new System.Windows.Forms.Timer { Interval = 450 };
                    _blink.Tick += (s, e) =>
                    {
                        if (_tray == null || _idleIcon == null) return;
                        _blinkPhase++;
                        _tray.Icon = (_blinkPhase % 2 == 0) ? _icon : _idleIcon;
                    };
                }
                _blink.Start();
                _blinkStartedAt = Stopwatch.GetTimestamp();
                Trace("blink started (tray icon pulses during startup)");
            }

            SetTrayText("DSH 正在启动…");
            if (_ticker == null)
            {
                _ticker = new System.Windows.Forms.Timer { Interval = 1000 };
                _ticker.Tick += (s, e) =>
                {
                    _bootSeconds++;
                    SetTrayText("DSH 正在启动… 已用 " + _bootSeconds + " 秒" +
                                (_bootSeconds >= 10 ? "（npm 可能正在下载）" : ""));
                };
            }
            _ticker.Start();
        }
        catch (Exception ex) { Trace("StartTicker failed: " + ex.Message); }
    }

    /// <summary>Stop the elapsed-time counter and the blinking icon.</summary>
    static void StopBootFeedback()
    {
        try { if (_ticker != null && _ticker.Enabled) { _ticker.Stop(); Trace("ticker stopped at " + _bootSeconds + "s"); } }
        catch { }
        try { if (_blink != null && _blink.Enabled) { _blink.Stop(); Trace("blink stopped"); } }
        catch { }
        try { if (_tray != null) _tray.Icon = _icon; }
        catch { }
    }

    /// <summary>
    /// A faint twin of the tray icon, generated at build time by make-icon and
    /// loaded the only way that works everywhere: `new Icon(path)`.
    ///
    /// Two run-time attempts failed before this. Both composed the twin with GDI+
    /// and both threw `ArgumentOutOfRangeException: the requested range extends
    /// past the end of the array` — and not because of a coding mistake: on this
    /// machine EVERY path that decodes or rasterises image data throws, including
    /// new Bitmap(png), Graphics.DrawImage, Graphics.DrawIcon, Bitmap.GetHicon and
    /// System.Drawing's own Icon.ToBitmap(). Only vector drawing (GraphicsPath +
    /// FillPath) and Icon construction from a file work. The worse failure was
    /// silent: the fallback returned the ORIGINAL icon, so the blink alternated
    /// between two identical images and looked like nothing happened.
    /// </summary>
    static Icon LoadDimIcon()
    {
        try
        {
            string path = Path.Combine(AppDir(), "DSH-dim.ico");
            if (!File.Exists(path)) { Trace("dim icon missing: " + path); return null; }
            var icon = new Icon(path);
            Trace("blink twin loaded: " + path + " (" + icon.Size + ")");
            return icon;
        }
        catch (Exception ex)
        {
            string m; try { m = ex.GetType().Name + ": " + ex.Message; } catch { m = "unprintable"; }
            Trace("LoadDimIcon failed: " + m);
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Prepare the faint twin used by the blink, once. Call on the UI thread.
    /// Returns false when the twin is unavailable, in which case the caller keeps
    /// the tooltip ticker as the only progress signal.
    /// </summary>
    static bool PrepareBlinkIcon()
    {
        if (_idleIcon != null) return true;
        _idleIcon = LoadDimIcon();
        if (_idleIcon == null)
        {
            Balloon("DSH", "变暗图标不可用，托盘图标将保持常亮；启动进度请看托盘的悬停提示。");
            return false;
        }
        return true;
    }

    /// <summary>Stop blinking and restore the steady icon; call on the UI thread.</summary>
    static void StopBlink()
    {
        try { if (_blink != null && _blink.Enabled) { _blink.Stop(); Trace("blink stopped"); } }
        catch { }
        try { if (_tray != null) _tray.Icon = _icon; }
        catch { }
    }

    static void SetTrayText(string tip)
    {
        try
        {
            if (_tray == null) return;
            _tray.Text = tip.Length > 62 ? tip.Substring(0, 59) + "…" : tip;
        }
        catch { }
    }

    static void EnqueueJob(string job) { lock (Jobs) Jobs.Enqueue(job); JobReady.Set(); }

    // ---- worker thread -----------------------------------------------------
    static void WorkerLoop()
    {
        while (!_quitting)
        {
            string job = null;
            lock (Jobs) { if (Jobs.Count > 0) job = Jobs.Dequeue(); }
            if (job == null) { JobReady.WaitOne(200); continue; }

            try
            {
                switch (job)
                {
                    case "open": OpenBrowser(false); break;
                    case "app": OpenBrowser(true); break;
                    case "stop": StopServer(); Ack("ok"); Info("服务器已停止。"); break;
                    case "restart": RestartServer(); Ack("ok"); Info("服务器已重启。"); break;
                    case "kill-orphan": KillOrphan(); Ack("ok"); Info("残留服务器已清理。"); break;
                    case "install-market": InstallMarket(); Ack("ok"); break;
                    case "copy-link": CopyOpenLink(); Ack("ok"); break;
                    case "selftest": Ui(StartSelfTest); Ack("ok"); break;
                    case "boot": Boot(false, false); break;
                    case "boot-app": Boot(false, true); break;
                    case "boot-silent": Boot(true, false); break;
                    default: OpenBrowser(false); break;
                }
            }
            catch (Exception ex)
            {
                Trace("job '" + job + "' failed: " + ex);
                Ack("error");
            }
        }
    }

    static void Ack(string result)
    {
        try { File.WriteAllText(AckPath, result, new UTF8Encoding(false)); } catch { }
    }

    // ---- cross-process verbs ----------------------------------------------
    /// <summary>Ask the resident instance to do something. Returns its reply or "none".</summary>
    static string Send(string verb, bool blocking)
    {
        try
        {
            using (var ev = EventWaitHandle.OpenExisting(SignalName))
            {
                try { File.Delete(AckPath); } catch { }
                File.WriteAllText(VerbPath, verb, new UTF8Encoding(false));
                ev.Set();
                if (!blocking) return "ok";

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(25))
                {
                    if (File.Exists(AckPath))
                    {
                        string ack = "ok";
                        try { ack = File.ReadAllText(AckPath, Encoding.UTF8).Trim(); } catch { }
                        try { File.Delete(AckPath); } catch { }
                        return ack == "" ? "ok" : ack;
                    }
                    Thread.Sleep(120);
                }
                return "timeout";
            }
        }
        catch (Exception ex) { Trace("Send failed: " + ex.Message); return "none"; }
    }

    // ---- session (worker thread) ------------------------------------------
    static void Boot(bool silent, bool appWindow)
    {
        var sw = Stopwatch.StartNew();

        // 无论冷启动还是"服务器已在运行"，都先进入启动反馈（图标闪烁 + 悬停提示
        // 走秒）。否则两条路径收尾时都是常亮图标，用户看不出差别。
        Ui(StartTicker);

        if (ProbeReady())
        {
            int holder = PortOwnerPid();
            if (holder > 0 && !IsOurChild(holder))
                Trace("port " + Port + " is served by pid " + holder +
                      " which is not our child (orphaned server from an earlier run)");

            // Prefer the token the live server printed to our own log over the
            // cached one: the launch token is per-process, so a server restarted
            // outside this launcher invalidates anything we stored earlier.
            string fromLog = TokenFromLog();
            lock (Gate) { _url = fromLog ?? LoadUrl(); }
            Trace("server already up; using " + _url);
            Ui(MarkRunning);
            if (!silent) OpenBrowser(appWindow);
            return;
        }

        if (!SpawnServer())
        {
            Ui(() =>
            {
                StopBootFeedback();
                SetTrayText("DSH 启动失败 — 请查看日志");
                Balloon("DSH 无法启动", "无法拉起 npx。请在托盘菜单里查看启动器日志。");
            });
            return;
        }

        if (!WaitReady(TimeSpan.FromMinutes(6))) return;   // already reported + stopped the blink

        // Re-read the token from the log: the server prints it right about when it
        // becomes ready, and unlike state.txt this is written by the run we own.
        string fresh = TokenFromLog();
        lock (Gate) { _url = fresh ?? LoadUrl(); }
        Trace("server ready at " + _url + " (port owner pid " + PortOwnerPid() + ")");

        // Never open a page we cannot authenticate: that lands the user on a 401
        // and sends them hunting for a token. The fence can publish its URL a
        // moment after the port answers, so give it a short bounded grace period.
        if (!silent && _url == BaseUrl)
        {
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(400);
                string retry = TokenFromLog();
                if (retry != null) { lock (Gate) { _url = retry; } break; }
            }
            Trace("token after grace period: " + (_url == BaseUrl ? "still unavailable" : _url));
        }
        bool tokenized = _url != BaseUrl;
        long elapsed = sw.ElapsedMilliseconds / 1000;

        Ui(() =>
        {
            MarkRunning();
            // 每种结果只提示一次：连发的气泡会被 Windows 丢弃或合并，这正是
            // "通知有时不弹出"的原因。
            if (!silent && tokenized)
                Balloon("DSH 已就绪", "已打开界面（耗时 " + elapsed + " 秒）。");
            else if (!silent)
                Balloon("DSH 已就绪",
                    "服务器已启动，但没有拿到有效的登录链接。请用托盘菜单里的「复制访问链接」，"
                    + "或直接重启服务器。");
        });

        if (!silent && tokenized) OpenBrowser(appWindow);
        else if (!silent) Trace("skipped opening the browser: no authenticated URL available");
    }

    /// <summary>
    /// Start the server as `npx @deepseek-ai/dsh web`, with its output going to
    /// the log file and no console anywhere.
    ///
    /// HARDENING NOTE: this deliberately does NOT combine UseShellExecute=false
    /// with CreateNoWindow + WindowStyle.Hidden + redirected stdio handles. That
    /// combination is the signature of stealthily launching a hidden child
    /// process, and Windows Security flags it (observed: the endpoint-security
    /// heuristic terminated such a process tree). None of it is needed here:
    /// this launcher is a GUI process with no console, so a child gets no console
    /// to display either. A tiny .cmd file carries the command and does the
    /// redirection itself, which is exactly what a user would type by hand.
    /// </summary>
    static bool SpawnServer()
    {
        string bat = Path.Combine(Home, "start-dsh.cmd");
        try
        {
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "rem Written by DSH.exe. Runs the same command you would type:\r\n" +
                "rem     npx @deepseek-ai/dsh web --no-open\r\n" +
                "rem --no-open keeps the server from popping a browser on its own: the\r\n" +
                "rem launcher owns that decision (host-only by default).\r\n" +
                "cd /d \"" + ServerCwd + "\"\r\n" +
                "npx --yes " + NpxTarget + " " + WebArg + " --no-open > \"" + LogPath + "\" 2>&1\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Trace("could not write " + bat + ": " + ex.Message);
            return false;
        }

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c \"" + bat + "\"")
        {
            UseShellExecute = false,
            // Redirection lives inside the .cmd file, so no stdio pipe is needed.
            // CreateNoWindow is still set explicitly: it guarantees no console is
            // shown even when this launcher itself was started from a terminal and
            // would otherwise let cmd inherit that terminal's console.
            // (Measured: it does not create a conhost process here.)
            CreateNoWindow = true,
            WorkingDirectory = ServerCwd,
        };
        psi.EnvironmentVariables["npm_config_update_notifier"] = "false";

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Start();
            lock (Gate) { _server = proc; }
            Trace("spawned pid " + proc.Id + " via " + bat + " cwd=" + ServerCwd);
            return true;
        }
        catch (Exception ex)
        {
            Trace("spawn failed: " + ex);

            // Fallback: hand the batch file to the shell. ShellExecuteEx does not
            // inherit this process's (non-existent) console, so nothing appears.
            try
            {
                Process.Start(new ProcessStartInfo(bat)
                {
                    UseShellExecute = true,
                    WorkingDirectory = ServerCwd,
                    WindowStyle = ProcessWindowStyle.Minimized,
                });
                Trace("spawned via ShellExecute fallback");
                return true;
            }
            catch (Exception ex2)
            {
                Trace("fallback spawn failed: " + ex2);
                try { File.AppendAllText(LogPath, "spawn failed: " + ex2 + "\r\n"); } catch { }
                return false;
            }
        }
    }

    // npx writes a long "npm warn exec ..." banner right before the server's own
    // output, so this must not be anchored to the start of a line.
    static readonly Regex UrlLine =
        new Regex(@"dsh web:\s*(http://[^\s""]+)", RegexOptions.Compiled);

    /// <summary>Read whatever the server has appended so far and pick out its URL.</summary>
    static void PumpLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            string text;
            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();

            var m = UrlLine.Match(text);
            if (!m.Success) return;
            string url = m.Groups[1].Value.Trim();
            int lan = url.IndexOf(" (LAN:", StringComparison.Ordinal);
            if (lan > 0) url = url.Substring(0, lan);
            bool changed;
            int pid;
            lock (Gate) { changed = url != _url; _url = url; pid = _server != null ? _server.Id : 0; }
            if (changed)
            {
                WriteState(url, pid);
                Trace("captured url " + url);
            }
        }
        catch (Exception ex) { Trace("PumpLog failed: " + ex.Message); }
    }

    static bool WaitReady(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Process proc;
            lock (Gate) { proc = _server; }
            if (proc != null && proc.HasExited)
            {
                int code = -1;
                try { code = proc.ExitCode; } catch { }
                Trace("server process exited early, code " + code);
                string why = ReadFailureHint();
                Ui(() =>
                {
                    StopBootFeedback();
                    SetTrayText("DSH 启动失败 — 请查看日志");
                    Balloon("DSH 无法启动（退出码 " + code + "）", why);
                });
                return false;
            }
            PumpLog();
            if (ProbeReady()) return true;
            Thread.Sleep(350);
        }
        Ui(() =>
        {
            StopBootFeedback();
            SetTrayText("DSH 启动超时 — 请查看日志");
            Balloon("DSH 未能就绪", "端口 " + Port + " 在超时前没有响应。");
        });
        return false;
    }

    /// <summary>Surface the server's own last words instead of a generic message.</summary>
    static string ReadFailureHint()
    {
        try
        {
            if (!File.Exists(LogPath)) return "Open the log from the tray menu for details.";
            string[] lines = File.ReadAllLines(LogPath, Encoding.UTF8);
            for (int i = lines.Length - 1; i >= 0 && i >= lines.Length - 25; i--)
            {
                string t = lines[i].Trim();
                if (t.Length > 8 && !t.StartsWith("npm warn exec", StringComparison.OrdinalIgnoreCase))
                    return t.Length > 260 ? t.Substring(0, 257) + "…" : t;
            }
        }
        catch { }
        return "Open the log from the tray menu for details.";
    }

    /// <summary>
    /// The browser-trust fence answers 401 without a token and 200 with one, so
    /// any HTTP answer means the server is up; only a dead socket means "not yet".
    /// Worker thread only — this blocks.
    /// </summary>
    static bool ProbeReady()
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl);
            req.Method = "GET";
            req.Timeout = 1500;
            req.AllowAutoRedirect = false;
            req.Proxy = null;
            using (req.GetResponse()) { return true; }
        }
        catch (WebException we)
        {
            // The trust fence answers 401, which still proves the server is up:
            // any HTTP answer counts, a dead socket does not.
            return we.Response != null;
        }
        catch { return false; }
    }

    static bool IsOurChild(int pid)
    {
        Process proc;
        lock (Gate) { proc = _server; }
        if (proc == null) return false;
        try { return proc.Id == pid; } catch { return false; }
    }

    // ---- orphan / port ownership ------------------------------------------
    // When the launcher is killed rather than exited, `cmd -> npx -> node` can
    // survive as an orphan that still holds the port. That is exactly what makes
    // a later `npx @deepseek-ai/dsh web` fail with EADDRINUSE. `--kill-orphan`
    // (and "Clear orphaned server" in the tray) clears that case, and only it.
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
        int family, int cls, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;      // network byte order in the low 16 bits
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>PID currently listening on the DSH port, or 0.</summary>
    static int PortOwnerPid()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET,
                TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return 0;
            buffer = Marshal.AllocHGlobal(size);
            rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET,
                TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (rc != 0) return 0;

            int count = Marshal.ReadInt32(buffer);
            long row = buffer.ToInt64() + 4;
            int stride = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
            for (int i = 0; i < count; i++)
            {
                var entry = (MibTcpRowOwnerPid)Marshal.PtrToStructure(
                    new IntPtr(row + (long)i * stride), typeof(MibTcpRowOwnerPid));
                int port = ((int)(entry.LocalPort & 0xFF) << 8) | (int)((entry.LocalPort >> 8) & 0xFF);
                if (port == Port) return (int)entry.OwningPid;
            }
        }
        catch (Exception ex) { Trace("PortOwnerPid failed: " + ex.Message); }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        return 0;
    }

    static void KillOrphan()
    {
        try
        {
            int holder = PortOwnerPid();
            if (holder <= 0) { Trace("kill-orphan: nothing is listening on " + Port); return; }

            string name = "";
            try { using (var p = Process.GetProcessById(holder)) name = p.ProcessName; }
            catch { }
            Trace("kill-orphan: pid " + holder + " (" + name + ")");

            if (!name.Equals("node", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                Trace("kill-orphan: refusing to kill a non-DSH process");
                return;
            }
            Kill(holder);
            Thread.Sleep(700);
        }
        catch (Exception ex) { Trace("KillOrphan failed: " + ex.Message); }
    }

    // ---- url / state -------------------------------------------------------
    static void WriteState(string url, int pid)
    {
        try
        {
            File.WriteAllText(StatePath,
                "url=" + url + "\r\n" +
                "pid=" + pid + "\r\n" +
                "portOwner=" + PortOwnerPid() + "\r\n" +
                "time=" + DateTime.Now.ToString("s") + "\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex) { Trace("WriteState failed: " + ex.Message); }
    }

    /// <summary>Read one integer field out of state.txt, or 0.</summary>
    static int LoadStateInt(string key)
    {
        try
        {
            foreach (var line in File.ReadAllLines(StatePath, Encoding.UTF8))
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    int v;
                    if (int.TryParse(line.Substring(key.Length + 1).Trim(), out v)) return v;
                }
        }
        catch { }
        return 0;
    }

    static string LoadUrl()
    {
        try
        {
            foreach (var line in File.ReadAllLines(StatePath, Encoding.UTF8))
                if (line.StartsWith("url=", StringComparison.Ordinal))
                {
                    string u = line.Substring(4).Trim();
                    if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return u;
                }
        }
        catch { }
        return BaseUrl;
    }

    // ---- browser (worker thread) ------------------------------------------
    /// <summary>
    /// A tokenized URL is valid only for the server process that minted it: the
    /// launch token is per-process, so anything cached from an earlier boot must
    /// be re-checked. A valid one answers 303 (and mints the browser cookie);
    /// a stale one answers 401.
    /// </summary>
    static bool TokenStillValid(string url)
    {
        if (string.IsNullOrEmpty(url) || url == BaseUrl) return false;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0) return false;
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 1500;
            req.AllowAutoRedirect = false;   // a 303 IS the success signal
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
                return (int)resp.StatusCode == 303;
        }
        catch (WebException)
        {
            // 401 means the token is stale; anything else means "cannot tell".
            return false;
        }
        catch { return false; }
    }

    /// <summary>Resolve the URL to hand a browser, refreshing or dropping a stale token.</summary>
    static string ResolveOpenUrl()
    {
        string known;
        lock (Gate) { known = _url; }
        if (TokenStillValid(known)) return known;

        // The cached token is per-process, so a server restarted since it was
        // written makes it useless. The live token is usually still on disk:
        // this launcher redirects the server's output into dsh-web.log, and the
        // server prints its URL there on every boot.
        string fresh = TokenFromLog();
        if (fresh != null && TokenStillValid(fresh))
        {
            lock (Gate) { _url = fresh; }
            WriteState(fresh, _server != null ? _server.Id : 0);
            Trace("refreshed stale token from the server log");
            return fresh;
        }

        // Last chance: a state file written by a newer run.
        string fromFile = LoadUrl();
        if (fromFile != known && TokenStillValid(fromFile))
        {
            lock (Gate) { _url = fromFile; }
            return fromFile;
        }

        if (known != BaseUrl)
        {
            Trace("no valid token available; falling back to the clean URL");
            lock (Gate) { _url = BaseUrl; }
        }
        return BaseUrl;
    }

    /// <summary>
    /// The tokenized URL the server printed, if we can trust that it belongs to
    /// the server running now. Two guards, in order:
    ///   1. the URL must authenticate right now — a stale token from an earlier
    ///      server answers 401 and is skipped;
    ///   2. when both sides report a port owner, they must agree.
    /// Guard 2 alone is NOT sufficient and used to break this outright:
    /// state.txt can record portOwner=0 during a port handover, which made every
    /// later lookup bail out and fall back to a clean URL that always 401s.
    /// </summary>
    static string TokenFromLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return null;

            string text;
            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();

            var ms = UrlLine.Matches(text);
            if (ms.Count == 0) return null;

            // Newest first: the last URL the server printed is the current one.
            for (int i = ms.Count - 1; i >= 0; i--)
            {
                string url = ms[i].Groups[1].Value.Trim();
                int lan = url.IndexOf(" (LAN:", StringComparison.Ordinal);
                if (lan > 0) url = url.Substring(0, lan);
                if (url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!TokenStillValid(url)) continue;

                int owner = PortOwnerPid();
                int logged = LoadStateInt("portOwner");
                if (owner > 0 && logged > 0 && owner != logged)
                {
                    Trace("TokenFromLog: log names port owner " + logged +
                          " but " + owner + " holds the port; ignoring");
                    continue;
                }
                return url;
            }
        }
        catch (Exception ex) { Trace("TokenFromLog failed: " + ex.Message); }
        return null;
    }

    static void OpenBrowser(bool appWindow)
    {
        string url = ProbeReady() ? ResolveOpenUrl() : BaseUrl;
        bool tokenized = url != BaseUrl;

        Trace("opening browser: " + url + (appWindow ? " (app window)" : ""));

        if (appWindow)
        {
            string browser = FindChromium();
            if (browser != null)
            {
                try
                {
                    // UseShellExecute=true is deliberate. Launching a browser with
                    // UseShellExecute=false from a console-less process makes it
                    // inherit invalid stdio handles, and a hand-off to an already
                    // running browser fails with "cannot find the file specified".
                    // ShellExecuteEx goes through the shell's own activation path.
                    Process.Start(new ProcessStartInfo(browser)
                    {
                        Arguments = "--app=\"" + url + "\" --window-size=1400,900",
                        UseShellExecute = true,
                        WorkingDirectory = ServerCwd,
                    });
                    Trace("app window via " + Path.GetFileName(browser));
                    return;
                }
                catch (Exception ex) { Trace("app-window launch failed: " + ex.Message); }
            }
            else Trace("no Chromium found; falling back to the default browser");
        }

        // ShellExecute hands the URL to the default browser with no console.
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (!tokenized)
                Ui(() => Balloon("DSH 需要一次带令牌的访问",
                    "请从托盘菜单打开一次以完成本站登录，之后干净的地址就能直接使用。"));
            return;
        }
        catch (Exception ex) { Trace("ShellExecute failed: " + ex.Message); }

        // Fallback: let the shell's own file manager resolve the protocol.
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            Trace("explorer fallback failed: " + ex.Message);
            Ui(() => Balloon("无法打开浏览器", "请手动访问 " + url));
        }
    }

    /// <summary>
    /// Put the URL that opens the UI on the clipboard. The tokenized URL is used
    /// when known: it is what mints the persistent browser cookie (30 days by
    /// default) and then redirects to the clean `/`, so this is the link that
    /// works in a browser that has never authenticated before.
    /// </summary>
    static void CopyOpenLink()
    {
        string known = ProbeReady() ? ResolveOpenUrl() : BaseUrl;
        try
        {
            Ui(() =>
            {
                try
                {
                    Clipboard.SetText(known);
                    Balloon("已复制访问链接",
                        known != BaseUrl
                            ? "带令牌的链接已复制（对本次服务器运行有效）。"
                            : "干净链接已复制：http://127.0.0.1:3080/");
                }
                catch (Exception ex) { Trace("clipboard failed: " + ex.Message); }
            });
            Trace("copied open link: " + known);
        }
        catch (Exception ex) { Trace("CopyOpenLink failed: " + ex.Message); }
    }

    static string FindChromium()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    // ---- tray (UI thread only) --------------------------------------------
    // RULE 2: handlers only enqueue. No I/O, no waiting, no HTTP.
    // All user-facing text is Simplified Chinese: this launcher is written for a
    // Chinese-language Windows desktop, and log lines stay English so that a
    // support request can be pasted anywhere.
    static void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 DSH 界面", null, (s, e) => EnqueueJob("open"));
        menu.Items.Add("以应用窗口打开", null, (s, e) => EnqueueJob("app"));
        menu.Items.Add("复制访问链接", null, (s, e) => EnqueueJob("copy-link"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重启服务器", null, (s, e) => EnqueueJob("restart"));
        menu.Items.Add("停止服务器", null, (s, e) => EnqueueJob("stop"));
        menu.Items.Add("清理残留服务器", null, (s, e) => EnqueueJob("kill-orphan"));
        menu.Items.Add(new ToolStripSeparator());

        _autostartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled(),
        };
        _autostartItem.CheckedChanged += (s, e) =>
        {
            bool want = _autostartItem.Checked;
            SetAutostart(want);
            Balloon("DSH", want ? "已设置开机自动启动。" : "已取消开机自动启动。");
        };
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new ToolStripSeparator());

        // 插件管理。装完之后按钮就置灰，避免重复安装。
        _marketItem = new ToolStripMenuItem("安装 dshmarket 插件");
        _marketItem.Click += (s, e) => EnqueueJob("install-market");
        menu.Items.Add(_marketItem);
        RefreshMarketItem();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看服务器日志", null, (s, e) => OpenPath(LogPath));
        menu.Items.Add("查看启动器日志", null, (s, e) => OpenPath(TracePath));
        menu.Items.Add("打开安装目录", null, (s, e) => OpenPath(Home));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) => { _quitting = true; });

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "DSH",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (s, e) => EnqueueJob("open");
    }

    /// <summary>
    /// Restore the steady icon and the "running" tooltip, but never sooner than
    /// MIN_BLINK_VISIBLE_MS after the blink began.
    ///
    /// A warm start answers the readiness probe in a few milliseconds, so without
    /// this floor the blink lasted 28 ms and was invisible in practice — the
    /// feedback existed but the user never saw it.
    /// </summary>
    static void MarkRunning()
    {
        try
        {
            if (_blink != null && _blink.Enabled)
            {
                long elapsed = Stopwatch.GetTimestamp() - _blinkStartedAt;
                long ms = elapsed * 1000 / Stopwatch.Frequency;
                if (ms < BlinkVisibleMs)
                {
                    int delay = (int)(BlinkVisibleMs - ms);
                    Trace("holding startup feedback another " + delay + " ms so the blink is visible");
                    var once = new System.Windows.Forms.Timer { Interval = delay };
                    once.Tick += (s, e) =>
                    {
                        once.Stop();
                        once.Dispose();
                        StopBootFeedback();
                        SetTrayText(TipFor());
                    };
                    once.Start();
                    return;
                }
            }
        }
        catch (Exception ex) { Trace("MarkRunning delay failed: " + ex.Message); }

        StopBootFeedback();
        SetTrayText(TipFor());
    }

    static string TipFor() { return "DSH 正在运行 — 点击打开界面"; }

    static void SetTray(string state, string tip)
    {
        try
        {
            if (_tray == null) return;
            _tray.Text = tip.Length > 62 ? tip.Substring(0, 59) + "…" : tip;
        }
        catch { }
    }

    static void Balloon(string title, string body)
    {
        try { if (_tray != null) _tray.ShowBalloonTip(5000, title, body, ToolTipIcon.Info); }
        catch { }
    }

    static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Trace("OpenPath failed: " + ex.Message); }
    }

    // ---- UI-thread health --------------------------------------------------
    /// <summary>
    /// Heartbeat for `--selftest`: reports the worst gap between UI-thread ticks.
    /// A stalled message pump shows up as a multi-second gap, which is exactly
    /// the failure mode of a hand-rolled DoEvents loop.
    /// </summary>
    static void StartSelfTest()
    {
        long last = Stopwatch.GetTimestamp();
        long worst = 0;
        int ticks = 0;
        var beat = new System.Windows.Forms.Timer { Interval = 50 };
        beat.Tick += (s, e) =>
        {
            long now = Stopwatch.GetTimestamp();
            long ms = (now - last) * 1000 / Stopwatch.Frequency;
            last = now;
            if (ms > worst) worst = ms;
            ticks++;
            if (ticks == 100)          // ~5 s
            {
                beat.Stop();
                beat.Dispose();
                Trace("SELFTEST ticks=" + ticks + " worstStallMs=" + worst);
                Balloon("DSH 自检", "UI 线程心跳 " + ticks + " 次，最坏停顿 " + worst + " 毫秒");
                // Never sets _quitting: a diagnostic must not tear the launcher
                // down (doing so also stopped the server the user was using).
            }
        };
        beat.Start();
        Trace("selftest started");
    }

    // ---- profile plugin management ----------------------------------------
    // `dsh plugin --profile <name> add <pkg>` is a thin pnpm forwarder that then
    // reconciles dsh.profile.bundles against the installed state (the source of
    // @deepseek-ai/dsh/plugin): a dependency that resolves to a package declaring
    // `dsh.bundle.patch` is appended to the layer stack. So an install is only
    // really finished when all three of these hold, and the menu entry is enabled
    // only while they do not:
    //   1. the package is a profile dependency,
    //   2. it is listed in dsh.profile.bundles (otherwise nothing loads it),
    //   3. it is materialised under the profile's node_modules.
    const string MarketPackage = "dshmarket";
    const string WebProfileName = "web";
    static ToolStripMenuItem _marketItem;
    static bool _marketInstalling;

    static string WebProfileDir()
    {
        string home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(home, "profiles", WebProfileName);
    }

    /// <summary>True when the package is a declared, bundled and materialised plugin.</summary>
    static bool IsMarketInstalled()
    {
        try
        {
            string dir = WebProfileDir();
            string manifest = Path.Combine(dir, "package.json");
            if (!File.Exists(manifest)) return false;

            string json = File.ReadAllText(manifest);
            bool inDeps = Regex.IsMatch(json,
                "\"" + Regex.Escape(MarketPackage) + "\"\\s*:", RegexOptions.IgnoreCase);
            bool inBundles = json.IndexOf("\"" + MarketPackage + "\"", StringComparison.OrdinalIgnoreCase) >= 0
                             && Regex.IsMatch(json, "bundles\"?\\s*:\\s*\\[[^\\]]*" + Regex.Escape(MarketPackage),
                                 RegexOptions.IgnoreCase | RegexOptions.Singleline);
            bool materialised = File.Exists(Path.Combine(dir, "node_modules", MarketPackage, "package.json"));

            bool installed = inDeps && inBundles && materialised;
            if (!installed)
                Trace("market check: deps=" + inDeps + " bundles=" + inBundles + " files=" + materialised);
            return installed;
        }
        catch (Exception ex) { Trace("IsMarketInstalled failed: " + ex.Message); return false; }
    }

    static void RefreshMarketItem()
    {
        try
        {
            if (_marketItem == null) return;
            bool installed = IsMarketInstalled();
            _marketItem.Enabled = !installed && !_marketInstalling;
            _marketItem.Text = _marketInstalling
                ? "正在安装 dshmarket…"
                : (installed ? "dshmarket 已安装" : "安装 dshmarket 插件");
        }
        catch (Exception ex) { Trace("RefreshMarketItem failed: " + ex.Message); }
    }

    /// <summary>Run the install, then restart the server so the new bundle loads.</summary>
    static void InstallMarket()
    {
        if (IsMarketInstalled())
        {
            Ui(() => Balloon("DSH", "dshmarket 已经安装过了。"));
            return;
        }

        Ui(() =>
        {
            _marketInstalling = true;
            RefreshMarketItem();
            SetTrayText("DSH 正在安装 dshmarket…");
        });

        int code = RunNpx(new[]
        {
            // -w is required, not cosmetic: a profile directory carries a
            // pnpm-workspace.yaml (packages: [.]), so pnpm treats it as a
            // workspace root and refuses `add` with ERR_PNPM_ADDING_TO_ROOT
            // unless the caller opts in.
            "plugin", "--profile", WebProfileName, "add", "-w", MarketPackage,
        }, Path.Combine(Home, "dsh-plugin-install.log"), TimeSpan.FromMinutes(10));

        bool ok = code == 0 && IsMarketInstalled();
        Ui(() =>
        {
            _marketInstalling = false;
            RefreshMarketItem();
            SetTrayText(TipFor());
        });

        if (ok)
        {
            Trace("dshmarket installed; restarting the server to load the new bundle");
            Ui(() => Balloon("DSH", "dshmarket 安装完成，正在重启服务器以加载新插件。"));
            RestartServer();
        }
        else
        {
            string hint = code == 0
                ? "命令成功但未检测到插件生效，请查看日志。"
                : "安装失败（退出码 " + code + "）。";
            Trace("dshmarket install failed: exit " + code + ", detected=" + IsMarketInstalled());
            Ui(() => Balloon("dshmarket 安装失败",
                hint + "详情见 %LOCALAPPDATA%\\DSH\\dsh-plugin-install.log"));
        }
    }

    /// <summary>
    /// Run npx without any console window.
    ///
    /// This invokes `npx` itself — npx.cmd, the same entry point you would type —
    /// rather than a hardcoded node.exe plus npm's internal npx-cli.js. npx.cmd is
    /// a shell wrapper around that cli, so the semantics are identical; measured
    /// here as exit 0 with correct pass-through of a non-zero exit, and no conhost
    /// process created (a console-less WinExe plus CreateNoWindow means there is
    /// no window even though the wrapper needs cmd).
    ///
    /// Rejected alternative: launching a .cmd through ShellExecute. It cannot
    /// report an exit code, and without CreateNoWindow it flashes a console when
    /// the launcher itself was started from a terminal.
    /// </summary>
    static int RunNpx(string[] args, string logPath, TimeSpan timeout)
    {
        string nodeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");
        string npxCmd = Path.Combine(nodeDir, "npx.cmd");
        if (!File.Exists(npxCmd))
        {
            Trace("RunNpx: npx.cmd not found at " + npxCmd);
            return -1;
        }

        // Quoting. Exactly this, verified by controlled variation against both a
        // spaced and an unspaced cmd.exe path:
        //
        //     "/d /s /c ""<npx.cmd>" <pkg> <args...> > "<log>" 2>&1"
        //
        // cmd wants the batch path in doubled quotes after /c, and .NET itself
        // contributes the outermost pair (its BuildArguments always wraps the
        // argument string). Writing that outer pair by hand produces three quotes
        // and cmd then mis-parses the nesting: exit 1, no output, nothing in the
        // log. Measured, not reasoned — both cmd paths fail with three quotes and
        // both succeed with two.
        string command = "/d /s /c \"\"" + npxCmd + "\" " + NpxTarget;
        foreach (var a in args) command += " " + a.Replace("\"", "");
        command += " > \"" + logPath + "\" 2>&1\"";

        try
        {
            Trace("RunNpx: " + command);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ServerCwd,
            };
            psi.EnvironmentVariables["npm_config_update_notifier"] = "false";

            using (var p = Process.Start(psi))
            {
                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    Trace("RunNpx: timed out after " + timeout.TotalMinutes + " min");
                    try { p.Kill(); } catch { }
                    return -2;
                }
                Trace("RunNpx: exit " + p.ExitCode);
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Trace("RunNpx failed: " + ex.Message);
            return -1;
        }
    }

    // ---- autostart ---------------------------------------------------------
    static bool IsAutostartEnabled()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return key != null && key.GetValue(RunValue) != null;
        }
        catch (Exception ex) { Trace("IsAutostartEnabled failed: " + ex.Message); return false; }
    }

    static void SetAutostart(bool enabled)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enabled)
                    key.SetValue(RunValue, "\"" + ExePath() + "\" --silent", RegistryValueKind.String);
                else
                    key.DeleteValue(RunValue, false);
            }
            Trace("autostart set to " + enabled);
        }
        catch (Exception ex) { Trace("SetAutostart failed: " + ex.Message); }
    }

    static string ExePath()
    {
        try { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        catch { return Path.Combine(AppDir(), "DSH.exe"); }
    }

    // ---- lifecycle (worker thread) ----------------------------------------
    static void RestartServer()
    {
        StopServer();
        // Wait for the port to actually come free. A fixed sleep races the OS
        // releasing the listening socket, and losing that race makes the new
        // server die with EADDRINUSE.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (!ProbeReady()) break;
            Thread.Sleep(300);
        }
        Trace("restart: port free after " + sw.ElapsedMilliseconds + " ms");
        // Restarting is not a request to open a page: pass silent=true so the
        // server comes back host-only, exactly like a fresh launch.
        Boot(true, false);
    }

    static void StopServer()
    {
        try
        {
            Process proc;
            lock (Gate) { proc = _server; }
            // Kill the whole tree: cmd -> npx -> node. Killing only cmd would
            // orphan the node server, which then keeps the port and makes every
            // later `npx @deepseek-ai/dsh web` fail with EADDRINUSE.
            if (proc != null && !proc.HasExited) Kill(proc.Id);

            int holder = PortOwnerPid();
            if (holder > 0 && !IsOurChild(holder))
            {
                string name = "";
                try { using (var p = Process.GetProcessById(holder)) name = p.ProcessName; }
                catch { }
                if (name.Equals("node", StringComparison.OrdinalIgnoreCase)) Kill(holder);
            }

            lock (Gate) { _server = null; _url = BaseUrl; }
            Ui(() => SetTray("stopped", "DSH 已停止"));
            Trace("server stopped");
        }
        catch (Exception ex) { Trace("StopServer failed: " + ex.Message); }
    }

    static void Kill(int pid)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + pid + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                p.WaitForExit(8000);
            }
            Trace("taskkill /T /F pid " + pid);
        }
        catch (Exception ex) { Trace("Kill(" + pid + ") failed: " + ex.Message); }
    }

    static void Cleanup()
    {
        Trace("cleanup");
        try { StopServer(); } catch { }
        try { if (_watch != null) _watch.Stop(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        try { if (_pump != null && !_pump.IsDisposed) _pump.Dispose(); } catch { }
    }

    /// <summary>A short balloon for a process that owns no tray icon (control verbs).</summary>
    static void Info(string message)
    {
        try
        {
            using (var ni = new NotifyIcon { Icon = SystemIcons.Information, Visible = true })
            {
                ni.ShowBalloonTip(3500, AppName, message, ToolTipIcon.Info);
                Thread.Sleep(3600);
                ni.Visible = false;
            }
        }
        catch { }
    }
}
