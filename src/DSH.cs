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
                if (reply == "none") Info("DSH is not running.");
                else if (reply == "ok") Info(ReplyText(verb));
                else if (reply == "timeout") Info("DSH did not respond in time.");
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
            case "restart": return "DSH server restarted.";
            case "kill-orphan": return "Orphaned DSH server cleared.";
            case "selftest": return "Self-test is running — see the launcher log.";
            default: return "DSH server stopped.";
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
                    case "stop": StopServer(); Ack("ok"); Info("DSH server stopped."); break;
                    case "restart": RestartServer(); Ack("ok"); Info("DSH server restarted."); break;
                    case "kill-orphan": KillOrphan(); Ack("ok"); Info("Orphaned DSH server cleared."); break;
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
            Ui(() => SetTray("running", TipFor()));
            if (!silent) OpenBrowser(appWindow);
            return;
        }

        Ui(() => SetTray("starting", "DSH is starting — resolving the newest version via npx…"));
        if (!SpawnServer())
        {
            Ui(() =>
            {
                SetTray("error", "DSH could not start — see the log");
                Balloon("DSH could not start", "npx could not be launched. Open the log from the tray menu.");
            });
            return;
        }

        if (!WaitReady(TimeSpan.FromMinutes(6))) return;   // already reported

        lock (Gate) { _url = LoadUrl(); }
        Trace("server ready at " + _url + " (port owner pid " + PortOwnerPid() + ")");
        Ui(() =>
        {
            SetTray("running", TipFor());
            if (!silent) Balloon("DSH is ready", "Opening the DeepSeek Harness window.");
        });
        if (!silent) OpenBrowser(appWindow);
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
                    SetTray("error", "DSH could not start — see the log");
                    Balloon("DSH could not start (exit " + code + ")", why);
                });
                return false;
            }
            PumpLog();
            if (ProbeReady()) return true;
            Thread.Sleep(350);
        }
        Ui(() =>
        {
            SetTray("error", "DSH did not become ready — see the log");
            Balloon("DSH did not start", "Nothing answered on port " + Port + " in time.");
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
    /// The tokenized URL the server printed, if the log on disk belongs to the
    /// process that currently owns the port. Matching the port owner is what
    /// makes this safe: a log left over from an earlier server would otherwise
    /// hand back a token that no longer authenticates.
    /// </summary>
    static string TokenFromLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return null;
            int owner = PortOwnerPid();
            int logged = LoadStateInt("portOwner");
            if (owner <= 0) return null;
            if (logged != 0 && logged != owner) return null;   // log predates this server

            string text;
            using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                text = sr.ReadToEnd();

            var ms = UrlLine.Matches(text);
            if (ms.Count == 0) return null;
            string url = ms[ms.Count - 1].Groups[1].Value.Trim();
            int lan = url.IndexOf(" (LAN:", StringComparison.Ordinal);
            if (lan > 0) url = url.Substring(0, lan);
            return url;
        }
        catch (Exception ex) { Trace("TokenFromLog failed: " + ex.Message); return null; }
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
                    Process.Start(new ProcessStartInfo(browser)
                    {
                        Arguments = "--app=" + url + " --window-size=1400,900",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    return;
                }
                catch (Exception ex) { Trace("app-window launch failed: " + ex.Message); }
            }
        }

        // ShellExecute hands the URL to the default browser with no console.
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (!tokenized)
                Ui(() => Balloon("DSH needs one tokenized visit",
                    "Open DSH from the tray to sign this browser in once; after that the clean URL works."));
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
            Ui(() => Balloon("Could not open the browser", "Open " + url + " manually."));
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
                    Balloon("DSH link copied",
                        known != BaseUrl
                            ? "Tokenized link copied (valid for this server run)."
                            : "Clean link copied: http://127.0.0.1:3080/");
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
    static void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open DSH", null, (s, e) => EnqueueJob("open"));
        menu.Items.Add("Open as app window", null, (s, e) => EnqueueJob("app"));
        menu.Items.Add("Copy open link", null, (s, e) => EnqueueJob("copy-link"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart DSH server", null, (s, e) => EnqueueJob("restart"));
        menu.Items.Add("Stop DSH server", null, (s, e) => EnqueueJob("stop"));
        menu.Items.Add("Clear orphaned server", null, (s, e) => EnqueueJob("kill-orphan"));
        menu.Items.Add(new ToolStripSeparator());

        _autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled(),
        };
        _autostartItem.CheckedChanged += (s, e) =>
        {
            bool want = _autostartItem.Checked;
            SetAutostart(want);
            Balloon("DSH", want ? "DSH will start with Windows." : "DSH will no longer start with Windows.");
        };
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open server log", null, (s, e) => OpenPath(LogPath));
        menu.Items.Add("Open launcher log", null, (s, e) => OpenPath(TracePath));
        menu.Items.Add("Open install folder", null, (s, e) => OpenPath(Home));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (s, e) => { _quitting = true; });

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "DSH",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (s, e) => EnqueueJob("open");
    }

    static string TipFor() { return "DSH is running — click to open"; }

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
                Balloon("DSH self-test", "UI thread ticks: " + ticks + ", worst stall: " + worst + " ms");
                // Never sets _quitting: a diagnostic must not tear the launcher
                // down (doing so also stopped the server the user was using).
            }
        };
        beat.Start();
        Trace("selftest started");
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
        Thread.Sleep(900);
        Boot(false, false);
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
            Ui(() => SetTray("stopped", "DSH is stopped"));
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
