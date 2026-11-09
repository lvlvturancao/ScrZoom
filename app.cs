using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    // 显式声明应用标识：任务栏按钮因此直接取本程序的图标，不再经过快捷方式与缓存转手
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    static Mutex InstanceLock;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    static void Main()
    {
        bool created;
        InstanceLock = new Mutex(true, "Shuiyue.ScreenZoom.Instance", out created);
        if (!created)
        {
            // 已有实例在跑：把它叫到前台，本进程直接退出。
            // 两份实例各自会应用设备分辨率，一次配置变更就能把另一份的投屏打掉。
            try
            {
                int me = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
                {
                    if (p.Id == me) continue;
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    ShowWindow(h, 9);              // SW_RESTORE
                    SetForegroundWindow(h);
                    break;
                }
            }
            catch { }
            return;
        }
        // .NET 4.0 默认只启用 TLS 1.0，Cloudflare 只接受 1.2 及以上；不设这句「检查更新」必然失败
        try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; } catch { }
        try { SetCurrentProcessExplicitAppUserModelID("Shuiyue.ScreenZoom"); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

public class MainForm : Form
{
    WebView2 wv;
    string engineDir, scrcpyExe, adbExe;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "投屏工作台"); } }
    string StateFile { get { return Path.Combine(DataDir, "last-scale.json"); } }
    string RecentFile { get { return Path.Combine(DataDir, "recent-apps.json"); } }
    string TemplatesFile { get { return Path.Combine(DataDir, "vd-templates.json"); } }
    string AppsCacheFile { get { return Path.Combine(DataDir, "apps-cache.json"); } }
    string LastDevFile { get { return Path.Combine(DataDir, "last-device.json"); } }
    string WifiBookFile { get { return Path.Combine(DataDir, "wifi-addresses.json"); } }
    string UsageFile { get { return Path.Combine(DataDir, "app-usage.json"); } }
    string LegacyWifiFile { get { return Path.Combine(DataDir, "wifi-last.json"); } }
    string NameBookFile { get { return Path.Combine(DataDir, "device-names.json"); } }
    string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
    string PendingFile { get { return Path.Combine(DataDir, "restore-pending.json"); } }
    string CapFile { get { return Path.Combine(DataDir, "device-capabilities.json"); } }
    string BaselineFile { get { return Path.Combine(DataDir, "dpi-baselines.json"); } }
    string AppliedFile { get { return Path.Combine(DataDir, "applied-display.json"); } }

    // ── 高 DPI 换算 ──
    // 进程声明了 PerMonitorV2（见 app.manifest）之后，WinForms 里的数字就是物理像素。
    // 构造函数那组尺寸是按 96dpi 设计的，不乘回比例，150% 缩放下窗口会凭空小一号。
    // 注：这里用系统 DPI 而不是 AutoScaleMode.Dpi —— 实测后者在 .NET Framework 上
    // 只会把 AutoScaleDimensions 记成当前 DPI、并不会真的放大窗体。
    const int DesignWidth = 1180, DesignHeight = 780, DesignMinWidth = 1020, DesignMinHeight = 640;
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    // 同网段主机查 MAC：比解析中文 arp -a 稳，也不受字段顺序影响
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref uint phyAddrLen);
    // PER_MONITOR_AWARE_V2 的上下文句柄是 -4；只有拿到它才说明 app.manifest 真的嵌进 exe 了。
    static string AwarenessName(IntPtr h)
    {
        try
        {
            IntPtr ctx = GetWindowDpiAwarenessContext(h);
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4))) return "PerMonitorV2";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-3))) return "PerMonitor";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-2))) return "System";
            if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-1))) return "Unaware(清单失效!)";
            return "ctx=" + ctx.ToInt64();
        }
        catch { return "未知"; }
    }
    static double ScaleOf(uint dpi) { return dpi >= 96 ? dpi / 96.0 : 1.0; }
    // 优先用窗口所在显示器的 DPI（PerMonitorV2 下跨屏拖动会变），拿不到再退回系统 DPI。
    static double DpiScale() { return DpiScaleFor(IntPtr.Zero); }
    static double DpiScaleFor(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                uint d = GetDpiForWindow(hwnd);
                if (d >= 96) return ScaleOf(d);
            }
            catch { }
        }
        try
        {
            uint d = GetDpiForSystem();
            if (d >= 96) return ScaleOf(d);
        }
        catch { /* Win10 1607 以前没有这个 API，退回 GDI 取系统 DPI */ }
        try
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float d = g.DpiX;
                if (d >= 96) return ScaleOf((uint)d);
            }
        }
        catch { }
        return 1.0;
    }

    // 设备能改到多大没有系统接口可查，只能设一次再读回实际值。
    // 第一次用放大档就是这个过程，顺手记下来，之后直接显示，不用再试探。
    void RememberCapability(string key, int maxW, int maxH, bool supported, string note)
    {
        if (string.IsNullOrEmpty(key)) return;
        var map = ReadJson(CapFile) as Dictionary<string, object>;
        if (map == null) map = new Dictionary<string, object>();
        map[key] = new Dictionary<string, object> {
            { "maxW", maxW }, { "maxH", maxH }, { "supported", supported },
            { "note", note ?? "" }, { "at", DateTime.Now.ToString("yyyy-MM-dd") } };
        WriteJson(CapFile, map);
    }

    // 正在投屏的每一路：只靠窗口标题分不清（同一台设备的多路标题相同），启动时登记身份
    class CastSession
    {
        public int pid;
        public string deviceId, deviceName, mode, size, startedAt;
        public bool scaled;   // 这一路启动前改过设备分辨率，收尾时要还原
        public int displayId; // 虚拟屏这一路占用的 display id，拉回时要用
        public string appPkg = "";
        public string kbd = "sys";  // 这一路的键盘通道：sys 系统注入 / hw 物理键盘 / off 关闭
        public bool stolen;   // 应用被手机侧打开，虚拟屏已经空了
        public bool intentional;                 // 用户主动关的：不触发自动重连
        public DateTime startedAtDt;             // 用来判断这一路活了多久
        public int retryGen;                     // 连续自动重连次数（活久了会归零）
        public Func<Task<object>> retry;         // 异常退出时用原参数再来一次
        public List<string> argv;                // 启动时用的完整参数：重连照着原样再起一次，不重算档位
        // 掉线追踪：投屏期间在本机一侧持续记心跳，掉线时直接取最后几分钟，不用等设备回来
        public List<string> netTrace = new List<string>();
        public List<string> pingTrace = new List<string>();
        public object traceLock = new object();
        public string lastNetSig = "";
        public CancellationTokenSource traceCts;
        // 诊断面板：配置从启动参数解析，运行数据由 scrcpy 输出和本机心跳填充
        public string cfgBitrate = "", cfgMaxFps = "", cfgMaxSize = "", cfgVideoBuffer = "0", cfgAudio = "on", cfgAudioCodec = "", cfgAudioKbps = "", cfgAudioBuffer = "";
        public string videoCodec = "", audioCodec = "";
        public string streamSize = "";   // 引擎实际编码/解码的画面尺寸，从 Texture 日志读
        public int fps; public DateTime fpsAt; public int skippedLast;
        public List<int> skipRing = new List<int>();
        public List<PingSample> pingSamples = new List<PingSample>();
        public int tcpConns; public string tcpStates = ""; public List<int> tcpPorts = new List<int>();
        public string resolvedIp = "";
        public List<string> events = new List<string>();
        public double cpuPct; public DateTime cpuAt;
    }

    class PingSample { public DateTime t; public int ms; public bool ok; }
    class DevSample { public DateTime t; public double txMbps, rxMbps, retriedPerSec, lostPerSec; }

    // 设备侧采样结果：同一台设备的几路投屏共用一份（adb 隧道只有一条）
    class DevTelemetry
    {
        public DateTime at;
        public string adbId = "", err = "";
        public bool ok, wifiViaDumpsys, ssOk;
        public string ip = "", ssid = "", bssid = "", band = "";
        public int rssiDbm = -127, linkMbps, txLinkMbps, rxLinkMbps, freqMhz, nproc;
        public long txBytes, rxBytes, txPackets, rxPackets, successTx, retriedTotal, lostTotal;
        public long prevTxBytes = -1, prevRxBytes = -1, prevRetriedTotal = -1, prevLostTotal = -1; public DateTime prevAt;
        public double txMbps, rxMbps, retriedPerSec, lostPerSec;
        public double load1, load5, load15;
        public double cpuPctDev; public long prevCpuTotal, prevCpuIdle;
        public long memTotalKb, memFreeKb, memAvailKb, swapTotalKb, swapFreeKb, swapUsedKb;
        public string rto = "", cwnd = "", ssthresh = "", retrans = "";
        public List<string> ssLines = new List<string>();
        public List<DevSample> hist = new List<DevSample>();
        public string rawWifi = "", rawSs = "";
    }

    class EndedSession
    {
        public DateTime at; public int pid, code, retryGen;
        public string deviceId = "", deviceName = "", mode = "", size = "";
        public bool abnormal, intentional;
        public List<string> events = new List<string>(), tail = new List<string>();
    }
    // 给界面的一次性提示（自动重连这类后台事件没地方推，扫描时捎带回去）
    static string uiNotice = "";
    static readonly List<CastSession> Sessions = new List<CastSession>();
    static readonly object SessionLock = new object();
    CastSession lastSpawned;

    // 诊断面板：设备快照、观察窗口和刚结束的会话
    readonly Dictionary<string, DevTelemetry> devStats = new Dictionary<string, DevTelemetry>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> devAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> devFailStreak = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> devWifiCmdBad = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, DateTime> statWatch = new Dictionary<int, DateTime>();
    readonly Dictionary<int, EndedSession> endedSessions = new Dictionary<int, EndedSession>();
    readonly object devStatsLock = new object();
    readonly object statCtlLock = new object();
    readonly HashSet<string> devSampling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool statsLoopRunning;

    static string DevKey(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (IsWirelessId(id) && id.Contains(":")) return HostOf(id);
        return id;
    }

    static void AddEvent(CastSession s, string text)
    {
        if (s == null) return;
        lock (s.traceLock)
        {
            s.events.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            while (s.events.Count > 40) s.events.RemoveAt(0);
        }
    }

    static string ArgVal(List<string> a, string key)
    {
        if (a == null) return "";
        string pref = key + "=";
        foreach (var x in a)
            if (x != null && x.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) return x.Substring(pref.Length);
        return "";
    }

    // 设备信息短缓存：投屏期间不再对同一台设备并发跑 shell 查询，列表靠上一次的结果撑住
    readonly Dictionary<string, Dictionary<string, object>> devInfoCache = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> devInfoCacheAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    readonly object devInfoCacheLock = new object();

    Dictionary<string, object> DevInfoCached(string id)
    {
        lock (devInfoCacheLock) { Dictionary<string, object> d; return devInfoCache.TryGetValue(id, out d) ? d : null; }
    }

    bool DevInfoFresh(string id, int seconds)
    {
        lock (devInfoCacheLock)
        {
            DateTime t;
            return devInfoCacheAt.TryGetValue(id, out t) && (DateTime.Now - t).TotalSeconds < seconds;
        }
    }

    void DevInfoStore(string id, Dictionary<string, object> d)
    {
        lock (devInfoCacheLock) { devInfoCache[id] = d; devInfoCacheAt[id] = DateTime.Now; }
    }

    // 刚启动就投屏、还没扫过这台：用之前记下的分辨率快照把列表项补齐，别为了显示信息去挤通道
    Dictionary<string, object> CachedFromBaseline(string id, string modelName)
    {
        var bl = BaselineFor(id);
        string size = "", dpi = "", phys = "", physDpi = "", ov = "", ovDpi = "";
        if (bl != null)
        {
            size = Tv(bl, "size"); dpi = Tv(bl, "dpi"); phys = Tv(bl, "physSize"); physDpi = Tv(bl, "physDpi");
            ov = Tv(bl, "ovSize"); ovDpi = Tv(bl, "ovDpi");
        }
        string shown = LookupName(LoadNames(), "", id);
        if (shown.Length == 0) shown = modelName;
        return new Dictionary<string, object> {
            { "id", id }, { "name", shown }, { "android", "" },
            { "via", IsWirelessId(id) ? "wireless" : "usb" }, { "rot", "0" },
            { "physicalSize", phys }, { "physicalDpi", physDpi.Length > 0 ? physDpi : "0" },
            { "currentSize", ov.Length > 0 ? ov : size }, { "currentDpi", ovDpi.Length > 0 ? ovDpi : dpi },
            { "hadSizeOverride", ov.Length > 0 }, { "hadDpiOverride", ovDpi.Length > 0 }
        };
    }

    bool RestoreOnClose()
    {
        var j = ReadJson(SettingsFile) as Dictionary<string, object>;
        if (j == null || !j.ContainsKey("restoreOnClose")) return true;
        try { return Convert.ToBoolean(j["restoreOnClose"]); } catch { return true; }
    }

    void SaveRestoreOnClose(bool on)
    {
        WriteJson(SettingsFile, new Dictionary<string, object> { { "restoreOnClose", on } });
    }

    List<CastSession> LiveSessions()
    {
        var live = new List<CastSession>();
        lock (SessionLock)
        {
            var dead = new List<CastSession>();
            foreach (var s in Sessions)
            {
                bool alive = false;
                try { alive = !Process.GetProcessById(s.pid).HasExited; } catch { }
                if (alive) live.Add(s); else dead.Add(s);
            }
            foreach (var d in dead) Sessions.Remove(d);
        }
        return live;
    }

    bool HasLiveSession(string deviceId)
    {
        foreach (var s in LiveSessions()) if (s.deviceId == deviceId) return true;
        return false;
    }

    // 程序重启前开着的投屏窗口仍在跑，但登记表是内存里的，会丢。
    // 启动时把它们接管过来：能看见、能关，设备身份未知就标成"已在运行"。
    void AdoptRunningWindows()
    {
        lock (SessionLock)
        {
            foreach (var pr in Process.GetProcessesByName("scrcpy"))
            {
                if (Sessions.Any(x => x.pid == pr.Id)) continue;
                string title = "";
                try { title = pr.MainWindowTitle; } catch { }
                Sessions.Add(new CastSession {
                    pid = pr.Id, deviceId = "",
                    deviceName = string.IsNullOrEmpty(title) ? "投屏窗口" : title,
                    mode = "已在运行", size = "—", scaled = false,
                    startedAt = "—"
                });
            }
        }
    }

    async Task<object> SessionsList()
    {
        var list = new List<object>();
        var live = LiveSessions();
        await DetectStolenApps(live);
        foreach (var s in live)
            list.Add(new Dictionary<string, object> {
                { "pid", s.pid },
                { "deviceId", s.deviceId },
                { "deviceName", string.IsNullOrEmpty(s.deviceName) ? s.deviceId : s.deviceName },
                { "mode", s.mode },
                { "size", s.size },
                { "startedAt", s.startedAt },
                { "scaled", s.scaled },
                { "displayId", s.displayId },
                { "appPkg", s.appPkg },
                { "appName", AppNote(s.appPkg) },
                { "kbd", s.kbd },
                { "stolen", s.stolen } });
        return list;
    }

    // 手机侧点开同一个应用时，系统会把那个 task 挪到主屏，虚拟屏就只剩壁纸。
    // 判据要收紧：我们这块屏上确实没应用了，而且这个应用正主屏在前台，才算被抢走；
    // 否则用户在虚拟屏上换了别的应用，会被误报。
    async Task DetectStolenApps(List<CastSession> live)
    {
        var watch = live.Where(s => s.displayId > 0 && s.appPkg.Length > 0).ToList();
        if (watch.Count == 0) return;
        foreach (var grp in watch.GroupBy(s => s.deviceId))
        {
            var focus = await DisplayFocusMap(grp.Key);
            if (focus == null) continue;
            string mainPkg;
            focus.TryGetValue(0, out mainPkg);
            foreach (var s in grp)
            {
                string ours;
                focus.TryGetValue(s.displayId, out ours);
                bool empty = string.IsNullOrEmpty(ours) || ours.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0;
                s.stolen = empty && string.Equals(mainPkg, s.appPkg, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // 每块屏当前在前台的应用包名
    async Task<Dictionary<int, string>> DisplayFocusMap(string id)
    {
        var r = await Adb(id, "shell", "dumpsys", "window", "displays");
        if (!r.ok) return null;
        var map = new Dictionary<int, string>();
        int cur = -1;
        foreach (var line in r.text.Split('\n'))
        {
            var d = System.Text.RegularExpressions.Regex.Match(line, "mDisplayId=([0-9]+)");
            if (d.Success) { int v; if (int.TryParse(d.Groups[1].Value, out v)) cur = v; continue; }
            if (cur < 0) continue;
            var f = System.Text.RegularExpressions.Regex.Match(line, @"mFocusedApp=ActivityRecord\{[0-9a-fA-F]+ u\d+ ([A-Za-z0-9_.]+)/");
            if (f.Success) map[cur] = f.Groups[1].Value;
            else if (line.IndexOf("mFocusedApp=null", StringComparison.OrdinalIgnoreCase) >= 0) map[cur] = "";
        }
        return map;
    }

    // am 的写法各家系统不一致：这台实测只认 "--display <id>"，别家可能只认 "--display-id=<id>"，两种都试
    async Task<Sh> StartOnDisplay(string id, int disp, string comp)
    {
        var r = await Adb(id, "shell", "am", "start", "--display", disp.ToString(), "-n", comp);
        if (r.text.IndexOf("Unknown option", StringComparison.OrdinalIgnoreCase) >= 0)
            r = await Adb(id, "shell", "am", "start", "--display-id=" + disp, "-n", comp);
        return r;
    }

    async Task<object> PullBackApp(Dictionary<string, object> p)
    {
        string id = Ps(p, "id"), pkg = Ps(p, "pkg");
        int disp;
        if (!int.TryParse(Ps(p, "displayId"), out disp) || disp <= 0) return Err("没指定要拉回哪块虚拟屏");
        if (pkg.Length == 0) return Err("没指定要拉回哪个应用");
        string act;
        var cache = LoadAppsCache();
        if (!cache.TryGetValue(pkg, out act) || act.Length == 0) act = await ResolveActivity(id, pkg);
        if (act.Length == 0) return Err("没解析到「" + AppNote(pkg) + pkg + "」的启动入口，回设备应用列表点「刷新列表」再试");
        var r = await StartOnDisplay(id, disp, pkg + act);
        string t = (r.text ?? "").Trim();
        if (!r.ok || t.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                   || t.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                   || t.IndexOf("Unknown option", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log("拉回失败 " + pkg + " → 屏 " + disp + ": " + t.Replace("\r", " ").Replace("\n", " "));
            return Err("拉回失败：" + (t.Length > 160 ? t.Substring(0, 160) : t));
        }
        foreach (var s in LiveSessions())
            if (s.deviceId == id && s.displayId == disp) { s.stolen = false; break; }
        Log("拉回 " + pkg + " → 屏 " + disp);
        return new Dictionary<string, object> { { "ok", true }, { "text", t } };
    }

    async Task<object> CloseSession(Dictionary<string, object> p)
    {
        int pid;
        if (!int.TryParse(Ps(p, "pid"), out pid) || pid <= 0) return Err("没指定要关哪一路");
        bool ok = false;
        MarkIntentional(pid);   // 用户主动关的，别触发自动重连
        try
        {
            var pr = Process.GetProcessById(pid);
            ok = pr.CloseMainWindow();
        }
        catch { }
        if (!ok)
        {
            try { Process.GetProcessById(pid).Kill(); ok = true; } catch { }
        }
        await Task.Delay(700);
        return new Dictionary<string, object> {
            { "ok", ok }, { "text", ok ? "已关闭这一路投屏" : "这一路可能已经关了" } };
    }

    async Task<object> CloseDevice(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("没指定设备");
        int n = 0;
        foreach (var s in LiveSessions())
        {
            if (s.deviceId != id) continue;
            MarkIntentional(s.pid);   // 这是用户点「结束投屏」，不算异常掉线
            bool closed = false;
            try { closed = Process.GetProcessById(s.pid).CloseMainWindow(); } catch { }
            if (closed) n++;
        }
        // 等窗口真正退出，再做一次还原兜底（Exited 里可能已经做过，重复一次无害）
        for (int i = 0; i < 12 && HasLiveSession(id); i++) await Task.Delay(250);
        if (n > 0 && RestoreOnClose() && !HasLiveSession(id))
        {
            try { await Restore(new Dictionary<string, object> { { "id", id } }); } catch { }
        }
        return new Dictionary<string, object> {
            { "ok", n > 0 }, { "count", n },
            { "text", n > 0 ? "已结束 " + n + " 路投屏" : "这台设备当前没有投屏" } };
    }

    object GetSettings()
    {
        return new Dictionary<string, object> { { "restoreOnClose", RestoreOnClose() } };
    }

    object SetRestore(Dictionary<string, object> p)
    {
        string raw = Ps(p, "on");
        bool on = raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        SaveRestoreOnClose(on);
        return new Dictionary<string, object> { { "ok", true }, { "restoreOnClose", on } };
    }

    // 投屏意外断开时还原可能失败（设备当时不可达）。标记下来，等重连成功后补一次；
    // 不能只看有没有未还原记录：正常重启程序时那条记录本来就应该留着，不能擅自还原。
    async Task TryRestorePending()
    {
        var pend = ReadJson(PendingFile) as Dictionary<string, object>;
        if (pend == null) return;
        string id = Convert.ToString(pend["id"]);
        if (string.IsNullOrEmpty(id)) { try { File.Delete(PendingFile); } catch { } return; }
        if (!RestoreOnClose()) return;
        // 记录里存的是断开那一刻的完整地址，无线调试一开关端口就变，照原样还原必然失败。
        // 所以先看它现在在不在，不在就按同一台机器的 IP 找它当前连着的地址。
        string target = id;
        if (await DeviceState(id) != "device")
        {
            string host = Tv(pend, "host");
            if (string.IsNullOrEmpty(host)) host = HostOf(id);
            var conn = await ConnectedWireless();
            string hit = conn.FirstOrDefault(c => string.Equals(HostOf(c), host, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(hit)) return;   // 这台还没回来，继续等
            target = hit;
            Log("待还原的设备换了地址：" + id + " → " + target);
        }
        // 正在投屏就先别动：等这一路关了再补，别把正在跑的应用的分辨率拽来拽去
        if (HasLiveSession(target)) return;
        var r = await Restore(new Dictionary<string, object> { { "id", target } });
        var d = r as Dictionary<string, object>;
        if (d != null && d.ContainsKey("error")) Log("重连后补还原未完成: " + d["error"]);
        else { try { File.Delete(PendingFile); } catch { } Log("重连后补还原完成 " + target); }
    }
    static readonly object NameLock = new object();

    // 设备名字簿：扫描到的设备只有地址，靠这张表把地址/序列号还原成型号名
    Dictionary<string, string> LoadNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var j = ReadJson(NameBookFile) as Dictionary<string, object>;
        if (j != null)
            foreach (var kv in j)
            {
                var v = Convert.ToString(kv.Value);
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(v)) map[kv.Key] = v;
            }
        return map;
    }

    void RememberName(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return;
        lock (NameLock)
        {
            var map = LoadNames();
            map[id] = name;
            if (id.Contains(":")) map[id.Split(':')[0]] = name;   // 端口会变，IP 也要能查到
            WriteJson(NameBookFile, map);
        }
    }

    string LookupName(Dictionary<string, string> map, string serial, string addr)
    {
        string n;
        if (!string.IsNullOrEmpty(serial) && map.TryGetValue(serial, out n) && n.Length > 0) return n;
        if (!string.IsNullOrEmpty(addr))
        {
            if (map.TryGetValue(addr, out n) && n.Length > 0) return n;
            var ip = addr.Split(':')[0];
            if (map.TryGetValue(ip, out n) && n.Length > 0) return n;
        }
        return "";
    }

    public MainForm()
    {
        Text = "水月清影 ScreenZoom";
        // 界面只有一个 Dock=Fill 的 WebView2，页面内缩放由 Chromium 按当前 DPI 处理；
        // 关掉 WinForms 自动缩放（见上），改成下面手动乘 DPI 比例。
        AutoScaleMode = AutoScaleMode.None;
        double dpiScale = DpiScale();
        startupScale = dpiScale;
        ApplyScale(dpiScale);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 247, 248);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        engineDir = FindEngine();
        if (engineDir != null) { scrcpyExe = Path.Combine(engineDir, "scrcpy.exe"); adbExe = Path.Combine(engineDir, "adb.exe"); }
        Log("启动 engine=" + (engineDir ?? "NULL") + " exe=" + AppDir() + " dpi=" + dpiScale);

        wv = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(wv);
        AdoptRunningWindows();
        Load += async (s, e) => await Init();
    }

    async Task Init()
    {
        Directory.CreateDirectory(DataDir);
        EnsureOwnAdbServer();
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir, null);
            await wv.EnsureCoreWebView2Async(env);
            wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            wv.CoreWebView2.Settings.IsStatusBarEnabled = false;
            wv.CoreWebView2.WebMessageReceived += OnMessage;
            wv.CoreWebView2.Navigate(new Uri(Path.Combine(AppDir(), "index.html")).AbsoluteUri);

            // 启动自检：确认程序进程里跑 adb mdns 能不能拿到广播（排查扫描不到新端口的依据）
            try
            {
                var md = await Run(adbExe, new[] { "mdns", "services" }, 7000);
                string flat = md.text.Replace("\r", " ").Replace("\n", " | ");
                Log("自检 mdns 端口变量=" + (Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT") ?? "默认") + " 输出: " + flat.Trim());
            }
            catch (Exception mdEx) { Log("自检 mdns 失败: " + mdEx.Message); }

            // 一次性自检：带上 SY_SELFTEST_DISCOVER=1 启动时，跑一遍扫描并把结果写进日志
            if (Environment.GetEnvironmentVariable("SY_SELFTEST_DISCOVER") == "1")
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = await WifiDiscover() as Dictionary<string, object>;
                    sw.Stop();
                    var on = (r["devices"] as List<object>) ?? new List<object>();
                    var off = (r["offline"] as List<object>) ?? new List<object>();
                    Log("自检扫描 用时=" + sw.ElapsedMilliseconds + "ms lanOk=" + r["lanOk"] + " 在线=" + on.Count + " 上次用过=" + off.Count);
                    foreach (var o in on.Concat(off))
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        Log("自检扫描行 kind=" + d["kind"] + " addr=" + d["addr"] + " name=" + d["name"] +
                            " state=" + (d.ContainsKey("state") ? d["state"] : "-") +
                            " reason=" + (d.ContainsKey("reason") ? d["reason"] : "-") +
                            " unknown=" + (d.ContainsKey("unknown") ? d["unknown"] : "-") +
                            " hintIp=" + (d.ContainsKey("hintIp") ? d["hintIp"] : "-"));
                    }
                }
                catch (Exception sx) { Log("自检扫描失败: " + sx.Message); }
            }

        }
        catch (Exception ex)
        {
            Log("界面初始化失败: " + ex.Message);
            if (IsWebView2Missing(ex)) ShowWebView2Missing();
            else MessageBox.Show("界面初始化失败：\n\n" + ex.Message, "水月清影", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 缺组件时别只留一个白窗口：说清是什么、去哪装
    static bool IsWebView2Missing(Exception ex)
    {
        string name = ex.GetType().Name ?? "";
        string msg = (ex.Message ?? "") + " " + (ex.InnerException != null ? ex.InnerException.Message : "");
        if (name.IndexOf("WebView2RuntimeNot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        bool mentions = msg.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0;
        bool missing = msg.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("未安装", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("找不到", StringComparison.OrdinalIgnoreCase) >= 0;
        return mentions && missing;
    }

    void ShowWebView2Missing()
    {
        var r = MessageBox.Show(
            "这台电脑缺少微软的 WebView2 组件，程序的界面需要它才能显示。\n\n" +
            "Windows 11 自带；Windows 10 多数已随 Edge 装过。\n" +
            "点「是」打开微软官方下载页，装一次以后就不用管了。",
            "水月清影 · 缺少界面组件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
        {
            try { Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/"); } catch { }
        }
        Close();
    }

    // 版本号：发布脚本会核对这里的值，改版本时两个都要动
    public const string AppVersion = "0.5.1";
    public const int AppVersionCode = 9;
    const string UpdateUrl = "https://shuiyue-30d.pages.dev/update.json";

    static string AppDir() { return Path.GetDirectoryName(Application.ExecutablePath); }

    void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    // 引擎只认 exe 同目录的 engine 文件夹，整个目录搬到哪都能用
    static string FindEngine()
    {
        var local = Path.Combine(AppDir(), "engine");
        return File.Exists(Path.Combine(local, "scrcpy.exe")) ? local : null;
    }

    async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Dictionary<string, object> msg;
        string raw;
        // 界面传的是对象，只能按 JSON 取；早先用"当字符串取"会直接抛异常，把每条请求静默丢掉
        try { raw = e.WebMessageAsJson; }
        catch (Exception ex) { Log("取消息失败: " + ex.Message); return; }
        try { msg = Json.DeserializeObject(raw) as Dictionary<string, object>; }
        catch (Exception ex) { Log("解析失败: " + ex.Message + " / " + (raw.Length > 200 ? raw.Substring(0, 200) : raw)); return; }
        if (msg == null) { Log("解析结果为空: " + raw); return; }
        string id = Get(msg, "id");
        string action = Get(msg, "action");
        // 诊断面板 1 秒一次，不能每次都写日志，否则 debug.log 会被刷满
        if (action != "session-stats") Log("recv " + action + " id=" + id);
        var payload = msg.ContainsKey("payload") ? msg["payload"] as Dictionary<string, object> : new Dictionary<string, object>();
        if (action == "__jserror") { Log("JS错误: " + Get(payload, "msg") + " @行" + Get(payload, "line")); return; }
        object result;
        try { result = await Dispatch(action, payload ?? new Dictionary<string, object>()); }
        catch (Exception ex) { Log("dispatch error " + action + ": " + ex.Message); result = new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } }; }
        var script = "window.__cb && window.__cb(" + Json.Serialize(id) + "," + Json.Serialize(result) + ");";
        try { wv.CoreWebView2.ExecuteScriptAsync(script); } catch (Exception ex) { Log("exec error " + ex.Message); }
    }

    static string Get(Dictionary<string, object> d, string k)
    {
        object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
    }
    static string Ps(Dictionary<string, object> d, string k, string def = "")
    {
        object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : def;
    }

    // 按 DPI 比例把 96dpi 设计尺寸换算成物理像素
    void ApplyScale(double scale)
    {
        Width = (int)Math.Round(DesignWidth * scale);
        Height = (int)Math.Round(DesignHeight * scale);
        MinimumSize = new Size((int)Math.Round(DesignMinWidth * scale), (int)Math.Round(DesignMinHeight * scale));
    }

    // 跨屏时只重算下限：系统给的建议矩形已经按窗口当前尺寸换算过，
    // 若在这里再 ApplyScale 会把用户手动拖大的窗口打回设计尺寸。
    void ApplyMinSize(double scale)
    {
        MinimumSize = new Size((int)Math.Round(DesignMinWidth * scale), (int)Math.Round(DesignMinHeight * scale));
    }

    double startupScale = 1.0;
    // 构造时只能拿到系统 DPI，而窗口真正落在哪块屏要到显示后才定（多屏不同缩放时
    // 可能落在非主屏）。首次显示后按窗口实际 DPI 校正一次尺寸并在这块屏上居中，
    // 否则副屏缩放更大时窗口会凭空小一号。只在没被 WM_DPICHANGED 处理过时校正。
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            double s = DpiScaleFor(Handle);
            // 常驻自检：界面发糊几乎只有两种原因——清单没生效（Unaware）或尺寸没按所在屏缩放。
            // 这行日志把两者一次性写清，换屏/改缩放/别人机器报糊时看一眼就能定位，不必再编译探针。
            var scr = Screen.FromHandle(Handle);
            Log("UI 自检 感知=" + AwarenessName(Handle) + " 窗口dpi=" + (int)(s * 96)
                + " 尺寸=" + Width + "x" + Height + " 期望=" + (int)Math.Round(DesignWidth * s) + "x" + (int)Math.Round(DesignHeight * s)
                + " 屏=" + scr.DeviceName + " " + scr.Bounds.Width + "x" + scr.Bounds.Height
                + (scr.Primary ? "(主)" : "(副)"));
            if (Math.Abs(s - startupScale) < 0.01) return;
            startupScale = s;
            var b = scr.Bounds;
            SuspendLayout();
            ApplyScale(s);
            Left = b.X + Math.Max(0, (b.Width - Width) / 2);
            Top = b.Y + Math.Max(0, (b.Height - Height) / 2);
            Log("UI 按所在屏缩放校正 dpi=" + s);
        }
        catch (Exception ex) { Log("OnShown 缩放校正失败: " + ex.Message); }
        finally { ResumeLayout(true); }
    }

    const int WM_DPICHANGED = 0x02E0;
    // PerMonitorV2 下窗口拖到不同缩放的显示器会收到这条消息。
    // 不处理的话 MinimumSize 停在旧 DPI，窗口在新屏上还是原来的物理尺寸，看着突然大一号（或小一号）。
    // 顺序必须是先按新 DPI 重设下限、再采纳 LParam 的建议矩形：建议矩形本身也会被
    // MinimumSize 截断，若先设矩形就设下限，从大缩放屏移回小缩放屏时窗口会被旧下限顶住缩不回去。
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            double scale = ScaleOf((uint)(m.WParam.ToInt32() & 0xFFFF));
            startupScale = scale;   // 已按所在屏处理过，OnShown 不必再校正
            try
            {
                var sug = (WinRect)Marshal.PtrToStructure(m.LParam, typeof(WinRect));
                SuspendLayout();
                ApplyMinSize(scale);
                DesktopBounds = new Rectangle(sug.left, sug.top, sug.right - sug.left, sug.bottom - sug.top);
                Log("UI 跨屏 dpi=" + (int)(scale * 96) + " 尺寸=" + Width + "x" + Height);
            }
            catch (Exception ex) { Log("DPICHANGED 失败: " + ex.Message); }
            finally { ResumeLayout(true); }
        }
        base.WndProc(ref m);
    }

    // ── 显示器枚举：拿「物理像素」的显示器几何 ──
    // 进程已是 PerMonitorV2（见 app.manifest），此时 GetMonitorInfo 直接返回真实物理几何。
    // 仍走 Win32 而不用 Screen.Bounds，是因为要顺带取到显示器名与设备路径给前端下拉框用；
    // 枚举期间临时把线程切到 PER_MONITOR_AWARE_V2，是为了万一清单没生效（比如被外部方式启动的
    // 旧 exe）也能读到正确值，读完切回，不影响主窗口的 DPI 行为。
    [StructLayout(LayoutKind.Sequential)] struct WinRect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int cbSize;
        public WinRect rcMonitor; public WinRect rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 50)] public string DeviceKey;
    }
    delegate bool EnumMonProc(IntPtr h, IntPtr hdc, ref WinRect r, IntPtr data);

    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumMonProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfoEx mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, int i, ref DisplayDevice dd, int flags);

    class MonitorInfo
    {
        public int index;
        public string device = "";
        public string name = "";
        public int x, y, w, h, workX, workY, workW, workH;
        public bool primary;
        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object> {
                { "index", index }, { "device", device }, { "name", name },
                { "x", x }, { "y", y }, { "w", w }, { "h", h },
                { "workX", workX }, { "workY", workY }, { "workW", workW }, { "workH", workH },
                { "primary", primary } };
        }
    }

    // Win10 1703+ 才有；老系统返回 IntPtr.Zero 表示不支持，调用方退回 WinForms 逻辑值
    static bool TrySetThreadDpiAware(out IntPtr prev, IntPtr want)
    {
        prev = IntPtr.Zero;
        try
        {
            prev = SetThreadDpiAwarenessContext(want);
            return true;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }

    static List<MonitorInfo> EnumMonitors()
    {
        var list = new List<MonitorInfo>();
        IntPtr prev;
        // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        bool switched = TrySetThreadDpiAware(out prev, new IntPtr(-4));
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hm, IntPtr hdc, ref WinRect r, IntPtr d) =>
            {
                var mi = new MonitorInfoEx();
                mi.cbSize = Marshal.SizeOf(typeof(MonitorInfoEx));
                mi.szDevice = new string('\0', 32);
                if (!GetMonitorInfo(hm, ref mi)) return true;
                var m = new MonitorInfo
                {
                    device = (mi.szDevice ?? "").TrimEnd('\0'),
                    x = mi.rcMonitor.left, y = mi.rcMonitor.top,
                    w = mi.rcMonitor.right - mi.rcMonitor.left,
                    h = mi.rcMonitor.bottom - mi.rcMonitor.top,
                    workX = mi.rcWork.left, workY = mi.rcWork.top,
                    workW = mi.rcWork.right - mi.rcWork.left,
                    workH = mi.rcWork.bottom - mi.rcWork.top,
                    primary = (mi.dwFlags & 1) != 0
                };
                var dd = new DisplayDevice(); dd.cb = Marshal.SizeOf(typeof(DisplayDevice));
                if (EnumDisplayDevices(m.device, 0, ref dd, 0)) m.name = dd.DeviceString ?? "";
                list.Add(m);
                return true;
            }, IntPtr.Zero);
        }
        catch { list.Clear(); }
        finally { if (switched && prev != IntPtr.Zero) { try { SetThreadDpiAwarenessContext(prev); } catch { } } }

        // 按桌面排布顺序（先左后右、再上后下）编号，前端下拉框与「副屏」说法才对得上
        list = list.OrderBy(m => m.x).ThenBy(m => m.y).ToList();
        for (int i = 0; i < list.Count; i++) list[i].index = i;
        return list;
    }

    Dictionary<string, object> MonitorJson()
    {
        var list = EnumMonitors();
        var chosen = list.FirstOrDefault(m => m.primary) ?? list.FirstOrDefault();
        if (chosen == null)
        {
            var b = Screen.PrimaryScreen.Bounds;
            chosen = new MonitorInfo { index = 0, w = b.Width, h = b.Height, x = b.X, y = b.Y, primary = true, name = "主显示器" };
        }
        return new Dictionary<string, object> {
            { "w", chosen.w }, { "h", chosen.h }, { "x", chosen.x }, { "y", chosen.y },
            { "index", chosen.index }, { "name", chosen.name }, { "device", chosen.device },
            { "count", list.Count },
            { "monitors", list.Select(m => m.ToJson()).ToList() } };
    }

    async Task<object> Dispatch(string action, Dictionary<string, object> p)
    {
        switch (action)
        {
            case "monitor": return MonitorJson();
            case "scan": return await Scan();
            case "state": return ReadJson(StateFile);
            case "recent-apps": return ReadJson(RecentFile) ?? new List<object>();
            case "windows": return await ListWindows();
            case "close-windows": return await CloseWindows();
            case "launch-mirror": return LaunchMirror(p);
            case "launch-window": return await LaunchWindow(p);
            case "launch-scaled": return await LaunchScaled(p);
            case "vd-list": return await VirtualDisplays(p);
            case "vd-cleanup": return await CleanupVirtualDisplays(p);
            case "vd-templates": return VdTemplates();
            case "vd-template-save": return VdTemplateSave(p);
            case "vd-template-delete": return VdTemplateDelete(p);
            case "apps-list": return await AppsList(p);
            case "apps-launch": return await LaunchApp(p);
            case "app-icon": return await AppIcon(p);
            case "about": return About();
            case "check-update": return await CheckUpdate();
            case "save-reward-qr": return SaveRewardQr();
            case "open-url": return OpenUrl(p);
            case "restore": return await Restore(p);
            case "restore-default": return await RestoreDefault(p);
            case "to-wireless": return await ToWireless(p);
            case "to-wired": return await ToWired(p);
            case "wifi-discover": return await WifiDiscover();
            case "wifi-disconnect": return await WifiDisconnect(p);
            case "wifi-connect": return await WifiConnect(p);
            case "wifi-forget": return WifiForget(p);
            case "sessions": return await SessionsList();
            case "session-stats": return SessionStats(p);
            case "copy-text": return CopyText(p);
            case "vd-pullback": return await PullBackApp(p);
            case "conn-diag": return await ConnectionDiag(p);
            case "usb-to-wireless": return await UsbToWireless(p);
            case "close-session": return await CloseSession(p);
            case "close-device": return await CloseDevice(p);
            case "settings": return GetSettings();
            case "set-restore": return SetRestore(p);
            case "wifi-restart-adb": return await RestartAdb();
            case "reconnect-last": return await ReconnectLast();
            case "wifi-relocate": return await RelocateStart(p);
            case "wifi-relocate-poll": return RelocatePoll(p);
            case "wifi-deep-scan": return await DeepScanStart(p);
            case "wifi-find-cancel": return CancelWifiJob();
            case "kbd-probe": return await KbdProbe(p);
            case "kbd-hardkb-settings": return await KbdHardKeySettings(p);
        }
        return Err("未知操作 " + action);
    }

    static Dictionary<string, object> Err(string m) { return new Dictionary<string, object> { { "ok", false }, { "error", m } }; }

    async Task<Sh> Adb(string id, params string[] args)
    {
        var a = new List<string>();
        if (adbExe == null) return new Sh { ok = false, text = "没找到 adb" };
        if (!string.IsNullOrEmpty(id)) { a.Add("-s"); a.Add(id); }
        a.AddRange(args);
        return await Run(adbExe, a);
    }

    // 带自定义超时的 adb 调用：探活这类短命令不能沿用默认 15 秒，否则一次扫描会拖很久
    async Task<Sh> AdbT(int ms, string id, params string[] args)
    {
        var a = new List<string>();
        if (adbExe == null) return new Sh { ok = false, text = "没找到 adb" };
        if (!string.IsNullOrEmpty(id)) { a.Add("-s"); a.Add(id); }
        a.AddRange(args);
        return await Run(adbExe, a, ms);
    }

    async Task<byte[]> AdbRaw(string id, params string[] args)
    {
        if (adbExe == null) return null;
        var a = new List<string>();
        if (!string.IsNullOrEmpty(id)) { a.Add("-s"); a.Add(id); }
        a.AddRange(args);
        return await Task.Run(() => RunRaw(adbExe, a));
    }

    // 5037 上的 adb 服务常被别的程序占着（例如手机助手常驻的 adb daemon）：
    // 那种服务版本旧，收不到 mDNS 广播，连 Android 11+ 的 TLS 无线调试也会报 failed to connect。
    // 启动时发现占用者不是自带的 adb，就把它换掉，换成随程序分发的版本。
    void EnsureOwnAdbServer()
    {
        if (adbExe == null) return;
        int pid = Port5037Pid();
        if (pid <= 0 && ServerWireVersion() <= 0)
        {
            // 没有服务就先起好，顺手提一下优先级，别等第一次查询时才起
            RunRaw(adbExe, new[] { "start-server" }, 15000);
            BoostServer(Port5037Pid());
            ForgetForeignCache();
            return;
        }
        string foreign = ForeignAdbOwner(true);
        if (foreign.Length == 0) { BoostServer(pid); return; }
        Log("adb 服务被别的程序占着（" + foreign + "），换成自带的");
        RunRaw(adbExe, new[] { "kill-server" }, 8000);
        RunRaw(adbExe, new[] { "start-server" }, 15000);
        BoostServer(Port5037Pid());
        ForgetForeignCache();
        string after = ForeignAdbOwner(true);
        Log("adb 服务已切换，现在占用者=" + (after.Length > 0 ? after : "自带 adb"));
    }

    // 本机的 adb 服务和投屏进程都往上提半档优先级：它们要持续从手机读数据，
    // 本机一忙、读得慢一拍，手机的发送缓冲就会憋满，设备端写失败后整条连接会被关掉。
    // 只提 AboveNormal，不碰实时优先级，避免反过来抢系统资源。
    static void BoostServer(int pid)
    {
        if (pid <= 0) return;
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.PriorityClass != ProcessPriorityClass.AboveNormal)
                p.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch { }
    }

    // 5037 是本机全局的：谁先把 adb 服务起起来，后面所有 adb 命令都走它。
    // 手机助手类程序自带的 adb 版本旧，认不了新版无线调试（TLS），会让每个端口都"不是无线调试"。
    // 认出被占用有两层办法：能拿到监听进程的 pid 就比路径；拿不到就问服务本身要协议版本。
    // 后者是必需的——adb 的服务是 fork 出来的，套接字继承自已经退出的父进程，
    // netstat 在这种端口上只能给出 pid 0，光比路径永远查不出来。
    bool foreignChecked = false;
    DateTime foreignAt = DateTime.MinValue;
    int foreignTtl = 0;
    string foreignName = "";

    // 返回占用 5037 的外部程序名；用的是自带 adb（或没有服务）就返回空，认不出名字给"旧版 adb"。
    // 结果留几秒：扫描每轮都要问一次，别每次都开进程、连端口；报"被占用"时缓存得短，换完服务要立刻变。
    string ForeignAdbOwner(bool refresh = false)
    {
        if (adbExe == null) return "";
        if (!refresh && foreignChecked && (DateTime.Now - foreignAt).TotalSeconds < foreignTtl) return foreignName;
        foreignChecked = true;
        foreignAt = DateTime.Now;
        foreignName = ForeignAdbOwnerNow();
        foreignTtl = foreignName.Length > 0 ? 4 : 20;
        return foreignName;
    }

    void ForgetForeignCache() { foreignChecked = false; }

    string ForeignAdbOwnerNow()
    {
        int pid = Port5037Pid();
        if (pid > 0)
        {
            string owner = "";
            try { owner = Process.GetProcessById(pid).MainModule.FileName; } catch { }
            if (owner.Length == 0) return "";
            return string.Equals(owner, adbExe, StringComparison.OrdinalIgnoreCase) ? "" : AdbOwnerName(owner);
        }
        if (Port5037Open()) return ServerIsOldAdb() ? OtherAdbName() : "";
        return "";
    }

    // 端口上没人听就没有"占用"这回事，别把版本问不出来当成旧版
    static bool Port5037Open()
    {
        try
        {
            var sk = new System.Net.Sockets.TcpClient();
            try
            {
                var ar = sk.BeginConnect(System.Net.IPAddress.Loopback, 5037, null, null);
                return ar.AsyncWaitHandle.WaitOne(400) && sk.Connected;
            }
            finally { sk.Close(); }
        }
        catch { }
        return false;
    }

    // 服务答的协议版本比自带的低 → 是旧版 adb 占着端口。持平或更高不算占用：
    // 新版服务认得了无线调试，硬说它有问题只会把人引偏。
    bool ServerIsOldAdb()
    {
        int srv = ServerWireVersion();
        int mine = OwnWireVersion();
        return srv > 0 && mine > 0 && srv < mine;
    }

    static int ownWireVer = 0;
    static bool ownWireRead = false;

    // 自带 adb 的协议版本：`adb version` 里的 "1.0.41" 取 41，和服务端 host:version 同一个数
    int OwnWireVersion()
    {
        if (ownWireRead) return ownWireVer;
        ownWireRead = true;
        if (adbExe == null) return 0;
        try
        {
            var psi = new ProcessStartInfo(adbExe, "version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using (var pr = Process.Start(psi))
            {
                string tx = pr.StandardOutput.ReadToEnd();
                if (!pr.WaitForExit(4000)) { try { pr.Kill(); } catch { } }
                var m = System.Text.RegularExpressions.Regex.Match(tx ?? "", @"1\.0\.(\d+)");
                int v;
                if (m.Success && int.TryParse(m.Groups[1].Value, out v)) ownWireVer = v;
            }
        }
        catch { }
        return ownWireVer;
    }

    // 直接问 5037 上的服务要版本（adb 的 host:version），拿不到就回 -1
    static int ServerWireVersion()
    {
        try
        {
            using (var sk = new System.Net.Sockets.TcpClient())
            {
                sk.ReceiveTimeout = 800;
                sk.SendTimeout = 500;
                var ar = sk.BeginConnect(System.Net.IPAddress.Loopback, 5037, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(500)) return -1;
                sk.EndConnect(ar);
                var st = sk.GetStream();
                byte[] req = System.Text.Encoding.ASCII.GetBytes("000chost:version");
                st.Write(req, 0, req.Length);
                st.Flush();
                string head = ReadAscii(st, 8);
                if (head == null || !head.StartsWith("OKAY")) return -1;
                int len;
                if (!int.TryParse(head.Substring(4, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out len) || len <= 0 || len > 64) return -1;
                string body = ReadAscii(st, len);
                if (body == null) return -1;
                int comma = body.IndexOf(',');
                if (comma > 0) body = body.Substring(0, comma);
                int v;
                if (int.TryParse(body, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            }
        }
        catch { }
        return -1;
    }

    static string ReadAscii(System.IO.Stream st, int n)
    {
        byte[] buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r;
            try { r = st.Read(buf, got, n - got); } catch { return null; }
            if (r <= 0) return null;
            got += r;
        }
        return System.Text.Encoding.ASCII.GetString(buf, 0, n);
    }

    // 版本对不上但说不出是谁：从进程表里找个不是自带的 adb，报它的路径；找不到就算了
    string OtherAdbOwnerPath()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("adb"))
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule.FileName; } catch { }
                    if (path.Length > 0 && !string.Equals(path, adbExe, StringComparison.OrdinalIgnoreCase)) return path;
                }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return "";
    }

    string OtherAdbName()
    {
        string path = OtherAdbOwnerPath();
        return path.Length > 0 ? AdbOwnerName(path) : "旧版 adb";
    }

    // 报占用者要用人看得懂的名字：所有这类程序的可执行文件都叫 adb.exe，说文件名等于没说
    static string AdbOwnerName(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            var parts = dir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string seg = parts[i];
                if (seg.IndexOf(':') >= 0) break;
                string low = seg.ToLowerInvariant();
                if (low == "platform-tools" || low == "platform_tools" || low == "adb" || low == "bin" ||
                    low == "engine" || low == "tools" || low == "x86" || low == "common files" ||
                    low == "program files" || low == "appdata" || low == "local" || low == "roaming" ||
                    low == "plugins" || low == "resources" || low == "files") continue;
                return seg;
            }
        }
        catch { }
        return "旧版 adb";
    }

    static int Port5037Pid()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using (var pr = Process.Start(psi))
            {
                string tx = pr.StandardOutput.ReadToEnd();
                if (!pr.WaitForExit(5000)) { try { pr.Kill(); } catch { } }
                foreach (var line in (tx ?? "").Split('\n'))
                {
                    var t = line.Trim();
                    if (t.IndexOf(":5037", StringComparison.Ordinal) < 0) continue;
                    if (t.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var cols = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int pid;
                    if (cols.Length > 0 && int.TryParse(cols[cols.Length - 1], out pid)) return pid;
                }
            }
        }
        catch { }
        return 0;
    }
    static byte[] RunRaw(string exe, IEnumerable<string> args, int ms = 40000)
    {
        var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(EscapeArg)))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        try
        {
            using (var pr = Process.Start(psi))
            {
                using (var msOut = new MemoryStream())
                {
                    pr.StandardOutput.BaseStream.CopyTo(msOut);
                    if (!pr.WaitForExit(ms)) { try { pr.Kill(); } catch { } return null; }
                    return msOut.ToArray();
                }
            }
        }
        catch { return null; }
    }

    class Sh { public bool ok; public string text; }

    static Task<Sh> Run(string exe, IEnumerable<string> args, int ms = 15000)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo(exe, string.Join(" ", args.Select(EscapeArg)))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using (var pr = Process.Start(psi))
                {
                    var so = new StringBuilder(); var se = new StringBuilder();
                    pr.OutputDataReceived += (s, ev) => { if (ev.Data != null) lock (so) so.AppendLine(ev.Data); };
                    pr.ErrorDataReceived += (s, ev) => { if (ev.Data != null) lock (se) se.AppendLine(ev.Data); };
                    pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
                    if (!pr.WaitForExit(ms))
                    {
                        // adb 拉起的常驻服务会继承输出管道，等不到退出就先收掉，别把界面卡死
                        try { pr.Kill(); } catch { }
                        return new Sh { ok = false, text = so.ToString() };
                    }
                    pr.WaitForExit();
                    // 合并两路输出：adb 有些结果（比如配对成功）只写在错误流里
                    return new Sh { ok = pr.ExitCode == 0, text = so.ToString() + se.ToString() };
                }
            }
            catch (Exception ex) { return new Sh { ok = false, text = ex.Message }; }
        });
    }

    // 投屏异常退出时自动重连：把这次启动的入参留一份，带 retryGen 标记再调同一个启动函数。
    // 次数在真正掉线那一刻判断——这一路活过 90 秒说明链路本来能用，次数归零重计；
    // 连续三次都没撑住就停下，交给用户判断是网络问题还是该换数据线。
    void ArmRetry(Dictionary<string, object> p, Func<Dictionary<string, object>, Task<object>> launcher)
    {
        var sess = lastSpawned;
        if (sess == null) return;
        sess.retry = () =>
        {
            int gen = sess.retryGen;
            if ((DateTime.Now - sess.startedAtDt).TotalSeconds >= 90) gen = 0;
            if (gen >= 3)
            {
                uiNotice = "这台设备反复掉线（已自动重连 3 次），先停下来，检查网络或改用数据线；设备分辨率保持原样，需要还原到「显示还原」页";
                Log("自动重连次数用尽 " + sess.deviceId);
                return Task.FromResult<object>(Err("自动重连次数用尽"));
            }
            var q = new Dictionary<string, object>(p);
            q["retryGen"] = (gen + 1).ToString();
            return launcher(q);
        };
    }

    // 改过分辨率的那一路（放大）重连时不能再走一遍启动流程：
    // 那会先把设备分辨率还原、再设回目标值，正在跑的游戏会被迫重新布局，严重的直接崩。
    // 这里照抄启动时的完整参数再起一个 scrcpy，设备侧保持掉线前的样子。
    void ArmRespawn(CastSession sess)
    {
        if (sess == null) return;
        sess.retry = () =>
        {
            int gen = sess.retryGen;
            if ((DateTime.Now - sess.startedAtDt).TotalSeconds >= 90) gen = 0;
            if (gen >= 3)
            {
                uiNotice = "这台设备反复掉线（已自动重连 3 次），先停下来，检查网络或改用数据线；设备分辨率保持原样，需要还原到「显示还原」页";
                Log("自动重连次数用尽 " + sess.deviceId);
                return Task.FromResult<object>(Err("自动重连次数用尽"));
            }
            return Task.FromResult<object>(RespawnExact(sess, gen + 1));
        };
    }

    object RespawnExact(CastSession old, int gen)
    {
        if (old.argv == null || old.argv.Count == 0) return Err("没有可用的启动参数，请手动重新投屏");
        var res = Spawn(new List<string>(old.argv), old.deviceId, old.mode, old.size, old.scaled, old.appPkg, gen);
        var d = res as Dictionary<string, object>;
        if (d != null && !d.ContainsKey("error"))
        {
            var ns = lastSpawned;
            if (ns != null)
            {
                ns.deviceName = old.deviceName;
                AddEvent(ns, "自动重连（第 " + gen + " 次）");
                Log("自动重连：沿用原参数重启 " + old.deviceId + "（第 " + gen + " 次）");
                ArmRespawn(ns);
            }
        }
        return res;
    }

    // 无线投屏最容易死在一个地方：设备自己息屏休眠，Wi-Fi 跟着掉，连接就断。
    // 默认在投屏期间让设备保持活跃（引擎的 --keep-active，靠模拟轻量活动保屏幕常亮）；
    // 想省电可以在「更多参数」里关掉。默认值走 "1"，没传也当开。
    static void AddAwakeOpts(List<string> a, Dictionary<string, object> p)
    {
        // 投屏时关闭手机屏幕：画面继续，设备保持运行；不能再加 --keep-active，否则会把屏幕又点亮
        if (Ps(p, "screenOff", "0") == "1")
        {
            a.Add("--turn-screen-off");
            a.Add("--stay-awake");
            a.Add("--power-off-on-close");   // 结束时保持黑屏，按电源键唤醒后回到手机正常锁屏
            return;
        }
        if (Ps(p, "keepActive", "1") != "0") a.Add("--keep-active");
    }

    // 每秒一行 FPS 输出，诊断面板据此显示实际帧率和跳帧。
    // 输出行在 Spawn 里解析后会从掉线 tail 中剔除，不会挤掉真正的断开原因。
    static void AddStatsOpts(List<string> a)
    {
        a.Add("--print-fps");
    }

    // 网络抖动大、偶发卡顿时加一点视频缓冲，代价是延迟；默认 0
    static void AddBufferOpts(List<string> a, Dictionary<string, object> p)
    {
        int ms = 0;
        if (int.TryParse(Ps(p, "buffer", "0"), out ms) && ms > 0) a.Add("--video-buffer=" + ms);
    }

    // 键盘通道：sys=系统注入（默认，不会顶掉手机软键盘，电脑能直接打中文）
    //           hw=物理键盘（相当于给设备插了把实体键盘，游戏按键手感，但软键盘会被收起）
    //           off=不转发键盘
    // --display-ime-policy 只在虚拟屏那一路传：引擎实测「只支持第二块屏」，主屏传了会直接报错退出。
    // 不加 --prefer-text：它把按键转成文本事件，会打坏依赖键码的快捷键和游戏。
    static void AddKeyboardOpts(List<string> a, Dictionary<string, object> p, bool virtualDisplay)
    {
        string k = Ps(p, "kbd", "sys");
        a.Add(k == "hw" ? "--keyboard=uhid" : (k == "off" ? "--keyboard=disabled" : "--keyboard=sdk"));
        if (virtualDisplay && k != "off") a.Add("--display-ime-policy=local");
        if (Ps(p, "legacyPaste", "0") == "1") a.Add("--legacy-paste");
    }

    // 探测这台设备有没有物理键盘通道：/dev/uhid 读不到，选「物理键盘」这一路多半没反应。
    // 只用来给界面上的选项做提示，不参与启动决策（不静默降级，让用户自己看到）。
    async Task<object> KbdProbe(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("没指定设备");
        var uh = await AdbT(3000, id, "shell", "ls", "/dev/uhid");
        var ap = await AdbT(2000, id, "shell", "getprop", "ro.build.version.sdk");
        int api;
        int.TryParse((ap.text ?? "").Trim(), out api);
        bool uhid = (uh.text ?? "").IndexOf("uhid", StringComparison.OrdinalIgnoreCase) >= 0
                    && (uh.text ?? "").IndexOf("No such file", StringComparison.OrdinalIgnoreCase) < 0;
        return new Dictionary<string, object> {
            { "ok", true }, { "uhid", uhid }, { "api", api },
            { "text", uhid ? "" : "这台设备没有物理键盘通道，选「物理键盘」时按键可能没反应；打字不受影响。" } };
    }

    // 物理键盘要在手机侧的「实体键盘」设置里配一次，替用户把那页打开
    async Task<object> KbdHardKeySettings(Dictionary<string, object> p)
    {
        var r = await Adb(Ps(p, "id"), "shell", "am", "start", "-a", "android.settings.HARD_KEYBOARD_SETTINGS");
        string t = (r.text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        bool ok = r.ok && t.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0
                      && t.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) < 0;
        return new Dictionary<string, object> {
            { "ok", ok }, { "text", ok ? "已在手机上打开实体键盘设置" : "这台设备没有这个设置页：" + (t.Length > 90 ? t.Substring(0, 90) : t) } };
    }

    // 置顶 / 无边框 / 指定窗口宽度，都是 scrcpy 原生参数；视频流尺寸不受影响
    static void AddWindowOpts(List<string> a, Dictionary<string, object> p)
    {
        if (Ps(p, "alwaysOnTop") == "1") a.Add("--always-on-top");
        if (Ps(p, "borderless") == "1") a.Add("--window-borderless");
        int ww;
        if (int.TryParse(Ps(p, "windowWidth"), out ww) && ww > 0) a.Add("--window-width=" + ww);
        AddMonitorPos(a, p);
    }

    // 投到指定那块屏：scrcpy 全屏是跟着「窗口所在那块屏」走的，所以先把窗口原点摆到目标显示器上。
    // 坐标用 EnumMonitors 的物理像素 —— scrcpy 自身是 PerMonitorV2，只认物理坐标。
    static void AddMonitorPos(List<string> a, Dictionary<string, object> p)
    {
        int mi;
        if (!int.TryParse(Ps(p, "monIndex", "-1"), out mi) || mi < 0) return;
        var target = EnumMonitors().FirstOrDefault(m => m.index == mi);
        if (target == null) return;
        // SDL 收的是「客户区」原点，系统把边框和标题栏算在外面，实测窗口外框会往左上偏出十几到几十像素。
        // 预留这点余量，免得带负 Y 的副屏把标题栏顶出屏幕点不到，也免得窗口左侧漏到隔壁屏上。
        // scrcpy 内部用 int16 存这个坐标，超出范围会绕回，所以夹一下（超大虚拟桌面才会碰到）。
        a.Add("--window-x=" + Clamp16(target.x + 30));
        a.Add("--window-y=" + Clamp16(target.y + 70));
    }

    static int Clamp16(int v)
    {
        return v < -32767 ? -32767 : (v > 32767 ? 32767 : v);
    }

    static string EscapeArg(string s)
    {
        // 参数里带引号时必须转义，否则整条命令被外壳拆开，报错文本会被当成结果解析
        if (s.Length == 0) return "\"\"";
        if (!s.Contains(" ") && !s.Contains("\t") && !s.Contains("\"")) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    static int RotVal(string t)
    {
        // mCurrentOrientation: 0=竖屏 1=横屏(90°) 2=倒竖 3=横屏(270°)；读不到按竖屏
        var m = System.Text.RegularExpressions.Regex.Match(t ?? "", "mCurrentOrientation=(\\d)");
        int v;
        return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : 0;
    }

    async Task<object> Scan()
    {
        if (adbExe == null) return Err("没找到 scrcpy 目录，请把程序放在 scrcpy 旁边");
        await Run(adbExe, new[] { "start-server" }, 4000);
        var r = await Run(adbExe, new[] { "devices", "-l" });
        var ids = new List<string>();
        foreach (var line in r.text.Split('\n'))
        {
            var l = line.Trim();
            // 带 -l 时行尾还有 product/model 等信息，只能按第二个字段判在线
            var parts = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device") continue;
            ids.Add(parts[0]);
        }
        // 同一台设备可能同时挂着 IP:端口 和 mDNS 服务名两条传输（adb 自动连会这样）。
        // 两条都列出来会像多了台一模一样的设备，这里只留 IP 那条；没有 IP 那条时保留 mDNS。
        if (ids.Any(IsWirelessId) && ids.Any(x => x.IndexOf("_adb-tls-", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            var mdnsAddr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ms = await Run(adbExe, new[] { "mdns", "services" }, 3000);
            foreach (var line in (ms.text ?? "").Split('\n'))
            {
                var tk = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 3) continue;
                string ep = tk[tk.Length - 1];
                string[] hp = ep.Split(':');
                if (hp.Length != 2 || hp[1].Length == 0 || !hp[1].All(char.IsDigit)) continue;
                mdnsAddr[tk[0]] = ep;
            }
            var keep = new List<string>();
            foreach (var id0 in ids)
            {
                if (id0.IndexOf("_adb-tls-", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string ep0;
                    if (mdnsAddr.TryGetValue(id0.Split('.')[0], out ep0) && ids.Contains(ep0))
                    {
                        Log("同一台设备有两条无线传输，只留 " + ep0 + "，跳过 " + id0);
                        continue;
                    }
                }
                keep.Add(id0);
            }
            ids = keep;
        }
        // 每台设备的几条查询同时发，逐台串行会让首屏等上十几秒
        var probes = ids.Select(async id =>
        {
            string modelName = RegexVal(r.text, "(?m)^" + System.Text.RegularExpressions.Regex.Escape(id) + @"\s.*?model:(\S+)") ?? id;
            bool live = LiveSessions().Any(x => string.Equals(x.deviceId, id, StringComparison.OrdinalIgnoreCase));
            var cached = DevInfoCached(id);
            // 正在投屏的这台，不在它同一条 adb 通道上再并发 shell 查询：
            // 视频流已经占满了这条连接，每 6 秒再叠几条 getprop / wm 会让 adbd 的写路径排队，
            // 严重时整条连接会被掐断（设备端就是 SSL_write failed）。列表信息沿用上一次读到的。
            if (live)
            {
                if (cached != null)
                {
                    var dc = new Dictionary<string, object>(cached);
                    dc["id"] = id;
                    dc["via"] = IsWirelessId(id) ? "wireless" : "usb";
                    string nm0 = Convert.ToString(dc["name"]);
                    if (nm0.Length == 0 || nm0 == id) dc["name"] = modelName;
                    return (object)dc;
                }
                return (object)CachedFromBaseline(id, modelName);
            }
            // 无线设备：adb devices 里的条目可能是「假在线」——连接早断了但状态还写着 device，
            // 光看状态字段永远显示已连接。主动发一条短命令探活，探不通就判离线并 disconnect，
            // 让这条陈旧条目从 adb 里彻底消失，后面自动重连才不会一直对着死地址空试。
            if (IsWirelessId(id) && !id.StartsWith("emulator"))
            {
                var alive = await AdbT(3000, id, "shell", "echo", "ok");
                // 第一次探不通再补一次：正在投屏时 adb 可能短暂排队，一次超时不能直接判死
                if (alive == null || !Clean(alive.text).Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(400);
                    alive = await AdbT(3000, id, "shell", "echo", "ok");
                }
                if (alive == null || !Clean(alive.text).Equals("ok", StringComparison.OrdinalIgnoreCase))
                {
                    Log("device " + id + " 探活失败判离线: " + Clean(alive == null ? "" : alive.text));
                    try { await Run(adbExe, new[] { "disconnect", id }, 4000); } catch { }
                    return null;
                }
            }
            // 名字、系统版本、分辨率不用每 6 秒重读一遍：20 秒内直接沿用上次结果
            if (cached != null && DevInfoFresh(id, 20))
            {
                var dc = new Dictionary<string, object>(cached);
                dc["id"] = id;
                dc["via"] = IsWirelessId(id) ? "wireless" : "usb";
                if (modelName != id) dc["name"] = modelName;
                return (object)dc;
            }
            var name = modelName;
            var tModel = Adb(id, "shell", "getprop", "ro.product.model");
            var tRel = Adb(id, "shell", "getprop", "ro.build.version.release");
            var tWm = ReadWm(id);
            var tRot = Adb(id, "shell", "dumpsys display | grep mCurrentOrientation");
            await Task.WhenAll(tModel, tRel, tWm, tRot);
            var m = await tModel; var v = await tRel; var wm = await tWm; var rot = await tRot;
            var shown = Clean(m.text).Length > 0 ? Clean(m.text) : name;
            if (!string.IsNullOrEmpty(shown)) RememberName(id, shown);   // 认一次脸，之后扫描时能报出名字
            var d = new Dictionary<string, object> {
                { "id", id },
                { "name", shown },
                { "android", Clean(v.text) },
                { "via", IsWirelessId(id) ? "wireless" : "usb" },
                { "rot", RotVal(rot.text) }
            };
            foreach (var kv in wm) d[kv.Key] = kv.Value;
            RememberBaseline(id, wm);   // 顺手记下设备当前的生效分辨率/密度，还原时写回这个值
            DevInfoStore(id, d);
            Log("device " + id + " " + d["name"] + " " + d["currentSize"]);
            return (object)d;
        }).ToList();
        // 探活失败的无线条目返回 null，这里过滤掉，不进设备列表
        var list = new List<object>((await Task.WhenAll(probes)).Where(x => x != null));
        string ver = "";
        if (scrcpyExe != null)
        {
            var rv = await Run(scrcpyExe, new[] { "--version" });
            ver = RegexVal(rv.text, "scrcpy\\s+([0-9.]+)") ?? "";
        }
        Log("scan done: devices=" + list.Count + " ver=" + ver + " engine=" + (engineDir ?? "NULL"));
        // 有没还原成功的记录：设备一回来就补还原，顺便把这件事告诉界面
        string pendingHost = "";
        if (File.Exists(PendingFile))
        {
            await TryRestorePending();
            if (File.Exists(PendingFile))
            {
                var pd = ReadJson(PendingFile) as Dictionary<string, object>;
                if (pd != null)
                {
                    pendingHost = Tv(pd, "host");
                    if (string.IsNullOrEmpty(pendingHost)) pendingHost = Tv(pd, "id");
                }
            }
        }
        string noticeNow = uiNotice;
        uiNotice = "";
        return new Dictionary<string, object> {
            { "devices", list }, { "version", ver }, { "engine", engineDir ?? "" },
            { "restorePending", pendingHost }, { "notice", noticeNow } };
    }

    static string RegexVal(string s, string pat)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, pat);
        return m.Success ? m.Groups[1].Value : null;
    }
    static string Clean(string s) { return (s ?? "").Trim(); }

    async Task<Dictionary<string, object>> ReadWm(string id)
    {
        var tSize = Adb(id, "shell", "wm", "size");
        var tDpi = Adb(id, "shell", "wm", "density");
        await Task.WhenAll(tSize, tDpi);
        var s = await tSize; var d = await tDpi;
        string phys = RegexVal(s.text, "Physical size:\\s*([0-9x]+)") ?? "";
        string over = RegexVal(s.text, "Override size:\\s*([0-9x]+)") ?? "";
        int dphys, doverN = 0;
        if (!int.TryParse(RegexVal(d.text, "Physical density:\\s*([0-9]+)") ?? "0", out dphys)) dphys = 0;
        string dover = RegexVal(d.text, "Override density:\\s*([0-9]+)") ?? "";
        if (dover.Length > 0 && !int.TryParse(dover, out doverN)) dover = "";
        return new Dictionary<string, object> {
            { "physicalSize", phys },
            { "currentSize", over.Length > 0 ? over : phys },
            { "hadSizeOverride", over.Length > 0 },
            { "physicalDpi", dphys },
            { "currentDpi", dover.Length > 0 ? doverN : dphys },
            { "hadDpiOverride", dover.Length > 0 }
        };
    }

    object LaunchMirror(Dictionary<string, object> p)
    {
        var a = new List<string> { "-s", Ps(p, "id"), "--video-bit-rate=" + Ps(p, "bitrate", "12M"),
            "--max-fps=" + Ps(p, "fps", "60") };
        // 镜像只按窗口能显示的尺寸编码：手机原生长边常是 3168/2400，不下发上限时
        // 引擎会按原生尺寸编码，手机编码器被拖满，画面掉帧；窗口再大也看不见多出来的像素。
        string szTxt = Ps(p, "size").Replace('×', 'x');
        string[] wh = szTxt.Split('x');
        int mw0, mh0;
        if (wh.Length == 2 && int.TryParse(wh[0], out mw0) && int.TryParse(wh[1], out mh0))
        {
            int maxSide = Math.Max(mw0, mh0);
            if (maxSide > 0) a.Add("--max-size=" + maxSide);
        }
        if (Ps(p, "fullscreen") == "1") { a.Add("--fullscreen"); AddMonitorPos(a, p); }
        else AddWindowOpts(a, p);
        AddKeyboardOpts(a, p, false);
        AddAwakeOpts(a, p);
        AddStatsOpts(a);
        AddBufferOpts(a, p);
        int gen0 = 0; int.TryParse(Ps(p, "retryGen", "0"), out gen0);
        var res = Spawn(a, Ps(p, "id"),
            Ps(p, "fullscreen") == "1" ? "镜像 · 全屏" : "镜像",
            Ps(p, "size"), false, "", gen0);
        var d = res as Dictionary<string, object>;
        if (d != null && !d.ContainsKey("error")) ArmRetry(p, q => Task.FromResult(LaunchMirror(q)));
        return res;
    }

    async Task<object> LaunchWindow(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        // 记下启动前的虚拟屏集合，窗口起来后 diff 出本次新加的 display id
        var before = await ListDisplayIds(id);
        var a = new List<string> { "-s", id, "--new-display=" + Ps(p, "size", "1280x720/240"),
            "--video-bit-rate=" + Ps(p, "bitrate", "12M") };
        // 视频流保持虚拟屏原分辨率，电脑放不下时只缩窗口，不降清晰度
        AddWindowOpts(a, p);
        AddKeyboardOpts(a, p, true);
        AddAwakeOpts(a, p);
        AddStatsOpts(a);
        AddBufferOpts(a, p);
        string app = Ps(p, "app");
        string appPkg = "";
        if (app.Length > 0)
        {
            bool isPkg = System.Text.RegularExpressions.Regex.IsMatch(app, "^[A-Za-z0-9_.]+$") && app.Contains(".");
            a.Add("--start-app=" + (isPkg ? app : "?" + app));
            if (!isPkg) Remember(app);
            else { appPkg = app; CountLaunch(app); }      // 应用列表按这个次数把常用的排到前面
        }
        int genW = 0; int.TryParse(Ps(p, "retryGen", "0"), out genW);
        var res = Spawn(a, id, "虚拟屏", Ps(p, "size", "1280x720/240").Split('/')[0], false, appPkg, genW);
        var dict = res as Dictionary<string, object>;
        if (dict != null && dict.ContainsKey("ok") && Convert.ToBoolean(dict["ok"]))
        {
            // scrcpy 起虚拟屏要一两秒，轮询等它出现在 dumpsys 里
            int disp = 0;
            for (int i = 0; i < 10 && disp == 0; i++)
            {
                await Task.Delay(700);
                var after = await ListDisplayIds(id);
                var diff = after.Except(before).ToList();
                if (diff.Count > 0) disp = diff.Max();
            }
            if (disp > 0)
            {
                dict["displayId"] = disp;
                if (lastSpawned != null) lastSpawned.displayId = disp;   // 记住归属，掉屏时才知道该往哪块屏拉回
            }
        }
        if (dict != null && !dict.ContainsKey("error")) ArmRetry(p, q => LaunchWindow(q));
        return res;
    }

    async Task<object> LaunchScaled(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        var info = await ReadWm(id);
        string phys = (string)info["physicalSize"];
        var wh = phys.Split('x');
        if (wh.Length != 2) return Err("读不到设备原始分辨率");
        int pw = int.Parse(wh[0]), ph = int.Parse(wh[1]);
        // 基准用「当前生效密度」而不是出厂密度：用户在系统设置里选的显示大小挡位
        // 在多数 ROM 上就是 wm density 覆盖值，用它缩放，放大倍数才跟用户看到的界面大小一致
        int dpi0 = (int)info["currentDpi"];
        if (dpi0 <= 0) dpi0 = (int)info["physicalDpi"];
        if (dpi0 <= 0) return Err("读不到设备显示密度");
        RememberBaseline(id, info);   // 必须在改任何东西之前存快照
        int monW = int.Parse(Ps(p, "monW", "1920")), monH = int.Parse(Ps(p, "monH", "1080"));
        string kind = Ps(p, "kind", "factor");
        string devKey = LookupName(LoadNames(), "", id);
        if (string.IsNullOrEmpty(devKey)) devKey = id;
        string beforeSize = Convert.ToString(info["currentSize"]);
        string notice = "";
        int w, h, dpi;

        if (kind == "fill")
        {
            await Adb(id, "shell", "wm", "size", monW + "x" + monH);
            await Task.Delay(900);
            var back = await ReadWm(id);
            var aw = ((string)back["currentSize"]).Split('x');
            if (aw.Length != 2) return Err("设备不接受该分辨率");
            if (aw[0] == pw.ToString() && aw[1] == ph.ToString() && (monW != pw || monH != ph))
            {
                RememberCapability(devKey, 0, 0, false, "系统拒绝 adb 修改分辨率");
                return Err("这台设备不允许 adb 修改分辨率（系统限制了这项权限），放大档位用不了");
            }
            RememberCapability(devKey, int.Parse(aw[0]), int.Parse(aw[1]), true,
                "目标 " + monW + "×" + monH + "，设备实际接受 " + aw[0] + "×" + aw[1]);
            double k = Math.Min(int.Parse(aw[0]) / (double)monW, int.Parse(aw[1]) / (double)monH);
            w = (int)Math.Round(monW * k); h = (int)Math.Round(monH * k);
            dpi = (int)Math.Round(dpi0 * (h / (double)ph));
            if (int.Parse(aw[0]) != monW || int.Parse(aw[1]) != monH)
                notice = "这台设备最多只能到 " + aw[0] + "×" + aw[1] + "，已按 " + w + "×" + h + " 投屏";
        }
        else
        {
            double k = kind == "fit" ? Math.Min(monW / (double)pw, monH / (double)ph)
                                      : double.Parse(Ps(p, "factor", "1.5"));
            w = (int)Math.Round(pw * k); h = (int)Math.Round(ph * k);
            dpi = (int)Math.Round(dpi0 * k);
        }

        // 上一份还原记录还没结清（掉线后没重连上、或用户还没去还原）就别覆盖：
        // 此刻读到的"当前尺寸"已经是放大后的值，存进去会把原尺寸弄丢，之后再也还原不回去
        bool keepOldState = false;
        try
        {
            var st0 = ReadJson(StateFile) as Dictionary<string, object>;
            if (st0 != null && string.Equals(Tv(st0, "host"), HostOf(id), StringComparison.OrdinalIgnoreCase))
                keepOldState = true;
        }
        catch { }
        if (!keepOldState)
            SaveState(id, (bool)info["hadSizeOverride"] ? (string)info["currentSize"] : null,
                           (bool)info["hadDpiOverride"] ? Convert.ToString(info["currentDpi"]) : null);

        await Adb(id, "shell", "wm", "size", w + "x" + h);
        await Adb(id, "shell", "wm", "density", dpi.ToString());
        await Task.Delay(1200);
        var now = await ReadWm(id);
        // 记设备实际接受的值：它可能把尺寸卡在上限，记实际值下次才认得出这是我们的缩放
        SaveApplied(id, Convert.ToString(now["currentSize"]), Convert.ToString(now["currentDpi"]));
        var got = ((string)now["currentSize"]).Split('x');

        // 设了但没变 = 设备拒绝了（常见于 realme/OPPO 这类收紧了 shell 权限的 ROM）。
        // 这时不能装作成功继续投，否则用户看到的是"标着放大、其实没放大"。
        if (kind != "fill" && got.Length == 2)
        {
            var before = beforeSize.Split('x');
            bool untouched = before.Length == 2 && got[0] == before[0] && got[1] == before[1];
            bool exact = got[0] == w.ToString() && got[1] == h.ToString();
            if (untouched && !exact)
            {
                RememberCapability(devKey, 0, 0, false, "系统拒绝 adb 修改分辨率");
                return Err("这台设备不允许 adb 修改分辨率（系统限制了这项权限），放大档位用不了");
            }
            if (exact) RememberCapability(devKey, w, h, true, "按目标尺寸生效");
            else
            {
                RememberCapability(devKey, int.Parse(got[0]), int.Parse(got[1]), true,
                    "目标 " + w + "×" + h + "，设备实际接受 " + got[0] + "×" + got[1]);
                notice = "设备最多只接受 " + got[0] + "×" + got[1] + "，已按这个尺寸投屏";
            }
        }

        var a = new List<string> { "-s", id, "--video-bit-rate=" + Ps(p, "bitrate", "12M"),
            "--max-fps=" + Ps(p, "fps", "60"), "--fullscreen" };
        // 全屏档由 scrcpy 铺满「窗口所在那块屏」，所以先把窗口原点摆到用户选的目标显示器
        AddWindowOpts(a, p);
        AddKeyboardOpts(a, p, false);
        AddAwakeOpts(a, p);
        AddStatsOpts(a);
        AddBufferOpts(a, p);
        int genS = 0; int.TryParse(Ps(p, "retryGen", "0"), out genS);
        var res = Spawn(a, id, "放大", w + "×" + h, true, "", genS);
        var dict = res as Dictionary<string, object>;
        if (dict != null)
        {
            if (got.Length == 2)
                dict["applied"] = new Dictionary<string, object> {
                    { "w", int.Parse(got[0]) }, { "h", int.Parse(got[1]) }, { "dpi", now["currentDpi"] } };
            if (notice.Length > 0) dict["notice"] = notice;
            // 放大这一路重连必须原样重启，不能重跑「改分辨率 + 启动」这套流程
            if (!dict.ContainsKey("error")) ArmRespawn(lastSpawned);
        }
        return res;
    }

    object Spawn(List<string> a, string id, string mode, string size, bool scaled, string appPkg, int retryGen = 0)
    {
        if (scrcpyExe == null) return Err("没找到 scrcpy.exe");
        Log("spawn " + mode + " " + id + " :: " + string.Join(" ", a));
        try
        {
            var psi = new ProcessStartInfo(scrcpyExe, string.Join(" ", a.Select(EscapeArg)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // 不留黑色控制台窗口
                RedirectStandardOutput = true,  // 但管道必须有人读，否则 scrcpy 会被憋住
                RedirectStandardError = true,
                WorkingDirectory = engineDir
            };
            var pr = Process.Start(psi);
            try { pr.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            string kbdMode = a.Contains("--keyboard=uhid") ? "hw"
                : (a.Contains("--keyboard=disabled") ? "off" : "sys");
            var sess = new CastSession {
                pid = pr.Id, deviceId = id,
                deviceName = LookupName(LoadNames(), "", id),
                mode = mode, size = size, scaled = scaled,
                appPkg = appPkg ?? "",
                kbd = kbdMode,
                startedAt = DateTime.Now.ToString("HH:mm"),
                startedAtDt = DateTime.Now,
                retryGen = retryGen,
                argv = new List<string>(a)
            };
            // 诊断面板的静态配置：从这次实际启动参数解析，重连回放 argv 时自然保留
            sess.cfgBitrate = ArgVal(a, "--video-bit-rate");
            sess.cfgMaxFps = ArgVal(a, "--max-fps");
            sess.cfgMaxSize = ArgVal(a, "--max-size");
            sess.cfgVideoBuffer = ArgVal(a, "--video-buffer");
            if (sess.cfgVideoBuffer.Length == 0) sess.cfgVideoBuffer = "0";
            sess.cfgAudio = a.Contains("--no-audio") ? "off" : "on";
            sess.cfgAudioCodec = ArgVal(a, "--audio-codec");
            sess.cfgAudioKbps = ArgVal(a, "--audio-bit-rate");
            sess.cfgAudioBuffer = ArgVal(a, "--audio-buffer");
            sess.videoCodec = ArgVal(a, "--video-codec");
            if (sess.videoCodec.Length == 0) sess.videoCodec = "h264";   // 引擎默认值，日志里没有时也能显示
            if (sess.cfgAudio == "on")
            {
                if (sess.cfgAudioCodec.Length == 0) sess.cfgAudioCodec = "opus";
                if (sess.cfgAudioKbps.Length == 0) sess.cfgAudioKbps = "128K";
                if (sess.cfgAudioBuffer.Length == 0) sess.cfgAudioBuffer = "50";
            }
            sess.resolvedIp = (IsWirelessId(id) && id.Contains(":")) ? HostOf(id) : "";
            lock (SessionLock) Sessions.Add(sess);
            lastSpawned = sess;
            AddEvent(sess, "启动 " + mode + (string.IsNullOrEmpty(size) ? "" : " · " + size));
            StartTrace(sess);   // 本机侧心跳：掉线时能回答是本地先断还是网络先抖
            // 物理键盘通道同一台设备只能有一路占着，第二路常常打不开
            bool kbdClash = kbdMode == "hw" && LiveSessions().Any(x => x.pid != pr.Id
                && string.Equals(x.deviceId, id, StringComparison.OrdinalIgnoreCase) && x.kbd == "hw");
            int n = 0;
            // 开头 40 行走日志；同时留一份环形缓冲，退出时把最后几行打出来——
            // 断连原因通常只在最后几行，不留在缓冲里就查不到
            var tail = new Queue<string>(40);
            var gate = new object();
            Action<string> take = line =>
            {
                if (line == null) return;
                // --print-fps 每秒一行，只更新面板数据；不能进 tail，否则掉线日志最后几行全是帧率
                if (ParseFpsLine(sess, line)) return;
                ParseSessionLogLine(sess, line);
                lock (gate)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 40) tail.Dequeue();
                    if (n < 40) { n++; Log("[scrcpy] " + line); }
                }
            };
            pr.OutputDataReceived += (s, e) => take(e.Data);
            pr.ErrorDataReceived += (s, e) => take(e.Data);
            pr.EnableRaisingEvents = true;
            pr.Exited += async (s, e) =>
            {
                // 掉线瞬间的本机现场先抓下来：等管道读完再抓，连接状态已经变了
                string hostSnap = HostSocketSnapshot(sess.deviceId);
                await Task.Delay(400);   // 等管道读干净再取最后几行
                int code = -1;
                try { code = pr.ExitCode; } catch { }
                string[] last;
                lock (gate) { last = tail.ToArray(); }
                string traceText = TraceDump(sess);
                try { if (sess.traceCts != null) sess.traceCts.Cancel(); } catch { }
                Log("scrcpy 退出 code=" + code);
                int from = Math.Max(0, last.Length - 12);
                for (int i = from; i < last.Length; i++) Log("  | " + last[i]);
                bool abnormal = code != 0 && !sess.intentional;
                AddEvent(sess, "退出 code=" + code + (abnormal ? "（异常）" : ""));
                lock (SessionLock) Sessions.Remove(sess);
                lock (statCtlLock) statWatch.Remove(sess.pid);
                // 留一份结束快照，让已经打开的诊断面板能看到退出码、末尾日志和事件
                try
                {
                    var ended = new EndedSession {
                        at = DateTime.Now, pid = sess.pid, code = code, retryGen = sess.retryGen,
                        deviceId = sess.deviceId, deviceName = sess.deviceName, mode = sess.mode, size = sess.size,
                        abnormal = abnormal, intentional = sess.intentional
                    };
                    lock (sess.traceLock) ended.events = new List<string>(sess.events);
                    ended.tail = new List<string>();
                    for (int i = from; i < last.Length; i++) ended.tail.Add(last[i]);
                    lock (statCtlLock)
                    {
                        endedSessions[sess.pid] = ended;
                        var old = new List<int>();
                        foreach (var kv in endedSessions)
                            if ((DateTime.Now - kv.Value.at).TotalSeconds > 60) old.Add(kv.Key);
                        foreach (var k in old) endedSessions.Remove(k);
                        while (endedSessions.Count > 10)
                        {
                            int oldestK = 0; DateTime oldestT = DateTime.MaxValue;
                            foreach (var kv in endedSessions) if (kv.Value.at < oldestT) { oldestT = kv.Value.at; oldestK = kv.Key; }
                            if (oldestK == 0) break;
                            endedSessions.Remove(oldestK);
                        }
                    }
                }
                catch { }
                // 用户主动关的这一路，最后收尾时把设备参数还原。
                // 异常断开（网络掉线这类）不走还原：中途把分辨率改回去、重连时再改回来，
                // 正在跑的游戏会被迫重新布局甚至崩掉；重连直接沿用原参数，设备那边一点都不动。
                if (sess.scaled && RestoreOnClose() && !abnormal && !HasLiveSession(sess.deviceId))
                {
                    try
                    {
                        var rr = await Restore(new Dictionary<string, object> { { "id", sess.deviceId } });
                        var rd = rr as Dictionary<string, object>;
                        bool bad = rd != null && rd.ContainsKey("error");
                        if (bad) WriteJson(PendingFile, new Dictionary<string, object> {
                            { "id", sess.deviceId }, { "host", HostOf(sess.deviceId) } });
                        Log("关闭投屏后自动还原 " + sess.deviceId + (bad ? " 失败：" + rd["error"] : " 完成"));
                    }
                    catch (Exception ex2) { Log("自动还原异常: " + ex2.Message); }
                }
                // 不是用户关的、也不是正常退出：先留一份设备端现场，再等设备回来用原参数重连
                if (abnormal)
                {
                    string tailText = string.Join("\n", last);
                    var _c = Task.Run(() => CaptureDropLog(sess.deviceId, tailText, traceText, hostSnap));
                    if (sess.retry != null)
                    {
                        var again = sess.retry;
                        var _r = Task.Run(() => AutoReconnect(sess, again));
                    }
                }
            };
            pr.BeginOutputReadLine();
            pr.BeginErrorReadLine();
            var res = new Dictionary<string, object> { { "ok", true }, { "cmd", "scrcpy " + string.Join(" ", a) } };
            if (kbdClash)
            {
                Log("同一台设备已有物理键盘那一路，这一路可能抢不到键盘通道");
                res["notice"] = "这台设备已经有一路在用物理键盘，这一路的键盘可能没反应。把先开的那一路改成「系统注入」就好。";
            }
            return res;
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // scrcpy --print-fps 的输出：每秒一行「60 fps」或「60 fps (+2 frames skipped)」。
    // 只更新面板状态，返回 true 让 take 不再把它塞进掉线日志的 tail。
    static bool ParseFpsLine(CastSession s, string line)
    {
        string t = (line ?? "").Trim();
        if (t.Length == 0 || t.IndexOf(" fps", StringComparison.Ordinal) < 0) return false;
        // scrcpy 实际输出带日志前缀（INFO: 11 fps），不能只匹配裸行
        var m = System.Text.RegularExpressions.Regex.Match(t, @"(\d+) fps(?: \(\+(\d+) frames skipped\))?\s*$");
        if (!m.Success) return false;
        int fps = 0, skip = 0;
        int.TryParse(m.Groups[1].Value, out fps);
        if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out skip);
        lock (s.traceLock)
        {
            s.fps = fps;
            s.skippedLast = skip;
            s.fpsAt = DateTime.Now;
            s.skipRing.Add(skip);
            while (s.skipRing.Count > 120) s.skipRing.RemoveAt(0);
        }
        return true;
    }

    static void ParseSessionLogLine(CastSession s, string line)
    {
        if (s == null || line == null) return;
        string t = line.Trim();
        if (t.Length == 0) return;
        if (t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var dim = System.Text.RegularExpressions.Regex.Match(t, @"(\d+x\d+)");
            if (dim.Success) lock (s.traceLock) s.streamSize = dim.Groups[1].Value;
        }
        else if (t.IndexOf("Video codec:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string v = AfterColon(t);
            if (v.Length > 0) lock (s.traceLock) s.videoCodec = v;
        }
        else if (t.IndexOf("Audio codec:", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string v = AfterColon(t);
            if (v.Length > 0) lock (s.traceLock) s.audioCodec = v;
        }
        else if (t.IndexOf("Audio stream error", StringComparison.OrdinalIgnoreCase) >= 0
              || t.IndexOf("audio disabled", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            AddEvent(s, "音频：" + t);
        }
    }

    static string AfterColon(string t)
    {
        int i = t.IndexOf(':');
        return i >= 0 && i + 1 < t.Length ? t.Substring(i + 1).Trim() : "";
    }

    void MarkIntentional(int pid)
    {
        lock (SessionLock) { foreach (var s in Sessions) if (s.pid == pid) s.intentional = true; }
    }

    // 掉线追踪：每 0.5 秒看一次本机到设备的 TCP 连接状态，每 2 秒 ping 一次。
    // 全在本机做，不给设备加任何 adb 命令；掉线时这两条线能回答"是本机先断的、还是网络先抖的"。
    void StartTrace(CastSession sess)
    {
        string ip = HostOf(sess.deviceId);
        IPAddress ipa;
        bool hasIp = IPAddress.TryParse(ip, out ipa);
        var cts = new CancellationTokenSource();
        sess.traceCts = cts;
        var ct = cts.Token;
        Task.Run(async () =>
        {
            Process proc = null;
            try { proc = Process.GetProcessById(sess.pid); } catch { }
            TimeSpan lastCpu = TimeSpan.Zero;
            DateTime lastCpuAt = DateTime.MinValue;
            int n = 0;
            while (!ct.IsCancellationRequested)
            {
                if (hasIp)
                {
                    try
                    {
                        var conns = IPGlobalProperties.GetIPGlobalProperties()
                            .GetActiveTcpConnections()
                            .Where(c => c.RemoteEndPoint.Address.ToString() == ip)
                            .OrderBy(c => c.LocalEndPoint.Port)
                            .ToList();
                        string sig = string.Join(", ", conns.Select(c =>
                            c.LocalEndPoint.Port + ">" + c.RemoteEndPoint.Port + " " + c.State).ToArray());
                        lock (sess.traceLock)
                        {
                            sess.tcpConns = conns.Count;
                            sess.tcpStates = string.Join(", ", conns.Select(c => c.State.ToString()).Distinct().ToArray());
                            sess.tcpPorts.Clear();
                            foreach (var c in conns)
                            {
                                int rp = c.RemoteEndPoint.Port;
                                if (!sess.tcpPorts.Contains(rp)) sess.tcpPorts.Add(rp);
                            }
                        }
                        if (sig != sess.lastNetSig)
                        {
                            sess.lastNetSig = sig;
                            AddTrace(sess, sess.netTrace, DateTime.Now.ToString("HH:mm:ss.fff") + " " +
                                (sig.Length > 0 ? sig : "(没有到设备的 TCP 连接)"));
                        }
                    }
                    catch { }
                    if (n % 4 == 0)
                    {
                        string line;
                        int rtt = 0; bool ok = false;
                        try
                        {
                            using (var pg = new Ping())
                            {
                                var rep = pg.Send(ipa, 1200);
                                ok = rep.Status == IPStatus.Success;
                                if (ok) rtt = (int)rep.RoundtripTime;
                                line = ok ? ("通 " + rtt + "ms") : rep.Status.ToString();
                            }
                        }
                        catch (Exception ex) { line = "失败 " + ex.Message; }
                        AddTrace(sess, sess.pingTrace, DateTime.Now.ToString("HH:mm:ss") + " " + line);
                        lock (sess.traceLock)
                        {
                            sess.pingSamples.Add(new PingSample { t = DateTime.Now, ms = rtt, ok = ok });
                            while (sess.pingSamples.Count > 180) sess.pingSamples.RemoveAt(0);
                        }
                    }
                }
                // 电脑端投屏进程 CPU：1 秒一次，判断读取/渲染有没有被本机拖慢
                if (n % 2 == 0 && proc != null)
                {
                    try
                    {
                        DateTime now = DateTime.Now;
                        TimeSpan cpu = proc.TotalProcessorTime;
                        if (lastCpuAt != DateTime.MinValue)
                        {
                            double secs = (now - lastCpuAt).TotalSeconds;
                            if (secs > 0.2)
                            {
                                double pct = (cpu - lastCpu).TotalSeconds / secs / Environment.ProcessorCount * 100.0;
                                lock (sess.traceLock) { sess.cpuPct = pct; sess.cpuAt = now; }
                            }
                        }
                        lastCpu = cpu; lastCpuAt = now;
                    }
                    catch { }
                }
                n++;
                try { await Task.Delay(500, ct); } catch { break; }
            }
        });
    }

    static void AddTrace(CastSession sess, List<string> list, string line)
    {
        lock (sess.traceLock)
        {
            list.Add(line);
            while (list.Count > 300) list.RemoveAt(0);
        }
    }

    string TraceDump(CastSession sess)
    {
        var sb = new StringBuilder();
        lock (sess.traceLock)
        {
            sb.AppendLine("-- 本机到设备的网络心跳 --");
            sb.AppendLine("[TCP 连接状态，只在变化时记]");
            foreach (var l in sess.netTrace) sb.AppendLine("  " + l);
            sb.AppendLine("[Ping，每 2 秒一次]");
            foreach (var l in sess.pingTrace) sb.AppendLine("  " + l);
            sb.AppendLine("[最近 60 秒摘要]");
            sb.AppendLine("  " + HostSummary(sess));
            sb.AppendLine("[最近事件]");
            foreach (var e in sess.events) sb.AppendLine("  " + e);
        }
        return sb.ToString();
    }

    // 诊断面板和掉线日志共用的一段短摘要
    static string HostSummary(CastSession s)
    {
        int n = 0, bad = 0, maxMs = 0, sumMs = 0, jitterSum = 0, prev = -1, jitterN = 0;
        DateTime since = DateTime.Now.AddSeconds(-60);
        foreach (var p in s.pingSamples)
        {
            if (p.t < since) continue;
            n++;
            if (!p.ok) { bad++; continue; }
            sumMs += p.ms;
            if (p.ms > maxMs) maxMs = p.ms;
            if (prev >= 0) { jitterSum += Math.Abs(p.ms - prev); jitterN++; }
            prev = p.ms;
        }
        int okN = n - bad;
        double avg = okN > 0 ? sumMs / (double)okN : 0;
        double jitter = jitterN > 0 ? jitterSum / (double)jitterN : 0;
        double loss = n > 0 ? bad * 100.0 / n : 0;
        return string.Format("Ping 平均 {0:0.0}ms 最大 {1}ms 抖动 {2:0.0}ms 丢包 {3:0.0}%（{4} 个样本） · 投屏进程 CPU {5:0.0}%",
            avg, maxMs, jitter, loss, n, s.cpuPct);
    }

    // 掉线那一瞬间从本机抓一次；等设备回来再抓，这个现场就没了
    string HostSocketSnapshot(string id)
    {
        string ip = HostOf(id);
        try
        {
            var list = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Where(c => c.RemoteEndPoint.Address.ToString() == ip)
                .Select(c => c.LocalEndPoint + " -> " + c.RemoteEndPoint + " " + c.State)
                .ToArray();
            return list.Length > 0 ? string.Join("\n", list) : "(掉线瞬间本机已没有到设备的 TCP 连接)";
        }
        catch (Exception ex) { return "(读取失败: " + ex.Message + ")"; }
    }

    // 掉线前后本机系统日志：网卡、驱动、电源、程序崩溃都会留在这里
    string RecentEvents(string logName, int minutes)
    {
        var sb = new StringBuilder();
        try
        {
            string xp = "*[System[TimeCreated[timediff(@SystemTime) <= " + (minutes * 60000) + "]]]";
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery(logName,
                System.Diagnostics.Eventing.Reader.PathType.LogName, xp);
            using (var rd = new System.Diagnostics.Eventing.Reader.EventLogReader(q))
            {
                int n = 0;
                for (var rec = rd.ReadEvent(); rec != null && n < 60; rec = rd.ReadEvent(), n++)
                {
                    string msg = "";
                    try { msg = (rec.FormatDescription() ?? "").Replace("\r", " ").Replace("\n", " "); } catch { }
                    if (msg.Length > 240) msg = msg.Substring(0, 240) + "…";
                    sb.AppendLine((rec.TimeCreated.HasValue ? rec.TimeCreated.Value.ToString("yyyy-MM-dd HH:mm:ss") : "?")
                        + "  " + rec.ProviderName + " id=" + rec.Id + " " + (rec.LevelDisplayName ?? ""));
                    if (msg.Length > 0) sb.AppendLine("    " + msg);
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("(读取失败: " + ex.Message + ")"); }
        return sb.ToString();
    }

    // 掉线现场：设备回来后把它的 Wi-Fi 状态、投屏进程最后的输出、最近几分钟 logcat 存成一份文件。
    // 掉线当时设备往往已经不可达，所以这里先等它回来（最多一分钟），拿到就存。
    async Task CaptureDropLog(string id, string sessionTail, string traceText, string hostSnap)
    {
        try
        {
            for (int i = 0; i < 30; i++)
            {
                if (await DeviceState(id) == "device") break;
                await Task.Delay(2000);
            }
            if (await DeviceState(id) != "device") { Log("掉线现场没抓到：设备一直没回来 " + id); return; }
            // logcat 里编解码器每帧都刷几行，不滤掉的话关键的 adbd / Wi-Fi / 内存事件会被冲没
            var lg = await AdbT(20000, id, "shell",
                "logcat -d -b all -v time -t 30000 | grep -vE 'C2Unisoc|SimpleUnisoc|gralloc4|VSP |BufferPool|GraphicBufferSource'");
            var wf = await AdbT(8000, id, "shell", "dumpsys wifi | grep -E 'mWifiInfo|StatsCurr|Detailed state'");
            var sy = await AdbT(12000, id, "shell",
                "cat /proc/meminfo | head -8; echo ---; uptime; echo ---; dumpsys meminfo | head -18; echo ---; ps -A -o PID,PPID,RSS,NAME | grep -E 'app_process|adbd'");
            var lb = await AdbT(6000, id, "shell", "logcat -g");
            var dm = await AdbT(8000, id, "shell", "dmesg | tail -150");
            string dir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(dir);
            string safe = id.Replace(':', '_').Replace('.', '_');
            string file = Path.Combine(dir, "drop-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== 掉线现场 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            sb.AppendLine("设备: " + id);
            sb.AppendLine();
            sb.AppendLine("-- 投屏进程最后的输出 --");
            sb.AppendLine(sessionTail ?? "");
            sb.AppendLine("-- 掉线瞬间本机的 TCP 连接 --");
            sb.AppendLine(hostSnap ?? "");
            sb.AppendLine(traceText ?? "");
            sb.AppendLine("-- 本机 adb 服务 --");
            int ap = Port5037Pid();
            if (ap > 0)
            {
                try
                {
                    var p5 = Process.GetProcessById(ap);
                    sb.AppendLine(p5.ProcessName + " pid=" + ap + " 启动于 " + p5.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch { sb.AppendLine("pid=" + ap); }
            }
            sb.AppendLine("-- 本机到设备的 TCP 连接（设备回来之后） --");
            try
            {
                var ns = await Run("netstat", new[] { "-ano", "-p", "TCP" }, 5000);
                string hip = HostOf(id) + ":";
                var conns = (ns.text ?? "").Split('\n').Where(x => x.IndexOf(hip, StringComparison.Ordinal) >= 0).ToArray();
                sb.AppendLine(conns.Length > 0 ? string.Join("\n", conns) : "(没有到这台设备的 TCP 连接)");
            }
            catch { }
            sb.AppendLine("-- 本机系统事件（掉线前后 15 分钟） --");
            sb.AppendLine(RecentEvents("System", 15));
            sb.AppendLine("-- 本机应用事件（掉线前后 15 分钟） --");
            sb.AppendLine(RecentEvents("Application", 15));
            sb.AppendLine("-- 设备 Wi-Fi 状态 --");
            sb.AppendLine(wf == null ? "" : (wf.text ?? ""));
            sb.AppendLine("-- 设备内存与负载 --");
            sb.AppendLine(sy == null ? "" : (sy.text ?? ""));
            sb.AppendLine("-- 设备日志缓冲区大小 --");
            sb.AppendLine(lb == null ? "" : (lb.text ?? ""));
            sb.AppendLine("-- 内核日志（可能因权限读不到） --");
            sb.AppendLine(dm == null ? "" : (dm.text ?? ""));
            sb.AppendLine("-- logcat（最后 30000 行，已滤掉编解码器刷屏） --");
            sb.AppendLine(lg == null ? "" : (lg.text ?? ""));
            File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(false));
            uiNotice = "掉线现场已保存：" + file;
            Log("掉线现场已保存 " + file);
        }
        catch (Exception ex) { Log("保存掉线现场失败: " + ex.Message); }
    }

    // ---------- 诊断面板：会话快照与按需设备采样 ----------

    object SessionStats(Dictionary<string, object> p)
    {
        int pid = 0; int.TryParse(Ps(p, "pid"), out pid);
        bool watch = Ps(p, "watch") == "1";
        CastSession s = null;
        lock (SessionLock) { foreach (var x in Sessions) if (x.pid == pid) { s = x; break; } }
        if (watch && s != null)
        {
            lock (statCtlLock) statWatch[pid] = DateTime.Now;
            EnsureStatsLoop();
        }
        else if (!watch)
        {
            lock (statCtlLock) statWatch.Remove(pid);
        }
        if (s != null) return BuildStatsJson(s);
        EndedSession e = null;
        lock (statCtlLock) endedSessions.TryGetValue(pid, out e);
        if (e != null && (DateTime.Now - e.at).TotalSeconds <= 60) return EndedJson(e);
        return new Dictionary<string, object> { { "ok", true }, { "pid", pid }, { "found", false } };
    }

    // 面板打开时每设备约 5 秒采一次；关闭后 15 秒不再续期就停，空闲 30 秒后整个循环退出
    void EnsureStatsLoop()
    {
        lock (statCtlLock)
        {
            if (statsLoopRunning) return;
            statsLoopRunning = true;
        }
        Task.Run(async () =>
        {
            int idle = 0;
            while (true)
            {
                await Task.Delay(1000);
                List<int> pids = new List<int>();
                lock (statCtlLock)
                {
                    var exp = new List<int>();
                    foreach (var kv in statWatch) if ((DateTime.Now - kv.Value).TotalSeconds > 15) exp.Add(kv.Key);
                    foreach (var x in exp) statWatch.Remove(x);
                    foreach (var kv in statWatch) pids.Add(kv.Key);
                    if (pids.Count == 0)
                    {
                        idle++;
                        if (idle >= 30) { statsLoopRunning = false; return; }
                        continue;
                    }
                    idle = 0;
                }
                var targets = new List<CastSession>();
                lock (SessionLock) { foreach (var x in Sessions) if (pids.Contains(x.pid)) targets.Add(x); }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in targets)
                {
                    string key = DevKey(t.deviceId);
                    if (key.Length == 0 || seen.Contains(key)) continue;
                    seen.Add(key);
                    bool go;
                    lock (devStatsLock)
                    {
                        if (devSampling.Contains(key)) continue;
                        DateTime next;
                        go = !devAttempt.TryGetValue(key, out next) || next <= DateTime.Now;
                    }
                    if (!go) continue;
                    string k = key, adbId = t.deviceId;
                    lock (devStatsLock)
                    {
                        if (devSampling.Count >= 2 || devSampling.Contains(k)) continue;
                        devSampling.Add(k);
                    }
                    Task.Run(async () =>
                    {
                        try { await SampleDevice(k, adbId); }
                        catch (Exception ex) { MarkDeviceFail(k, ex.Message); }
                        finally { lock (devStatsLock) devSampling.Remove(k); }
                    });
                }
            }
        });
    }

    async Task SampleDevice(string key, string adbId)
    {
        string cmd = "echo @@W; cmd wifi status 2>/dev/null; echo @@N; cat /proc/net/dev 2>/dev/null; "
            + "echo @@L; cat /proc/loadavg 2>/dev/null; "
            + "echo @@M; grep -E '^(MemTotal|MemFree|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo 2>/dev/null; "
            + "echo @@C; nproc 2>/dev/null; "
            + "echo @@P; head -1 /proc/stat 2>/dev/null; "
            + "echo @@I; ip -o -4 addr show wlan0 2>/dev/null; "
            + "echo @@S; ss -tin 2>/dev/null | head -80";
        var r = await AdbT(6000, adbId, "shell", cmd);
        string text = r == null ? "" : (r.text ?? "");
        if (text.IndexOf("@@W", StringComparison.Ordinal) < 0)
        {
            MarkDeviceFail(key, text.Trim().Length > 0 ? Clip(text.Trim(), 120) : "设备没响应");
            return;
        }

        string[] marks = new string[] { "@@W", "@@N", "@@L", "@@M", "@@C", "@@P", "@@I", "@@S" };
        string wifi = Section(text, "@@W", marks);
        string net = Section(text, "@@N", marks);
        string load = Section(text, "@@L", marks);
        string mem = Section(text, "@@M", marks);
        string nprocS = Section(text, "@@C", marks);
        string statSec = Section(text, "@@P", marks);
        string ipSec = Section(text, "@@I", marks);
        string ssSec = Section(text, "@@S", marks);

        bool viaDumpsys = false;
        bool wifiBad;
        lock (devStatsLock) devWifiCmdBad.TryGetValue(key, out wifiBad);
        if (wifi.IndexOf("WifiInfo", StringComparison.OrdinalIgnoreCase) < 0 && !wifiBad)
        {
            var wf = await AdbT(5000, adbId, "shell",
                "dumpsys wifi | grep -E 'mWifiInfo|Wi-Fi is|Link speed|RSSI|Frequency'");
            string wt = wf == null ? "" : (wf.text ?? "");
            if (wt.IndexOf("mWifiInfo", StringComparison.OrdinalIgnoreCase) >= 0) { wifi = wt; viaDumpsys = true; }
            else lock (devStatsLock) devWifiCmdBad[key] = true;
        }

        bool hasWifi = wifi.Trim().Length > 0;
        string ssid = Rgx(wifi, "SSID:\\s*\"([^\"]*)\"");
        string bssid = Rgx(wifi, "BSSID:\\s*([0-9a-fA-F:]{17})");
        int rssi = (int)Lng(Rgx(wifi, "RSSI:\\s*(-?\\d+)"));
        int link = (int)Lng(Rgx(wifi, "Link speed:\\s*(\\d+)"));
        int txLink = (int)Lng(Rgx(wifi, "Tx Link speed:\\s*(\\d+)"));
        int rxLink = (int)Lng(Rgx(wifi, "Rx Link speed:\\s*(\\d+)"));
        int freq = (int)Lng(Rgx(wifi, "Frequency:\\s*(\\d+)"));
        long successTx = Lng(Rgx(wifi, "successfulTxPackets:\\s*(\\d+)"));
        long retriedTotal = Lng(Rgx(wifi, "retriedTxPackets:\\s*(\\d+)"));
        long lostTotal = Lng(Rgx(wifi, "lostTxPackets:\\s*(\\d+)"));
        double retriedPs = Dbl(Rgx(wifi, "retriedTxPacketsPerSecond:\\s*([\\d.]+)"));
        double lostPs = Dbl(Rgx(wifi, "lostTxPacketsPerSecond:\\s*([\\d.]+)"));

        long rxBytes = 0, rxPkts = 0, txBytes = 0, txPkts = 0;
        bool hasNet = false;
        foreach (var l in net.Split('\n'))
        {
            if (l.IndexOf("wlan0:", StringComparison.Ordinal) < 0) continue;
            var f = l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 11)
            {
                rxBytes = Lng(f[1]); rxPkts = Lng(f[2]);
                txBytes = Lng(f[9]); txPkts = Lng(f[10]);
                hasNet = true;
            }
            break;
        }

        bool hasLoad = false;
        double l1 = 0, l5 = 0, l15 = 0;
        var la = load.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (la.Length >= 3) { l1 = Dbl(la[0]); l5 = Dbl(la[1]); l15 = Dbl(la[2]); hasLoad = true; }

        long memTotal = Lng(Rgx(mem, "MemTotal:\\s*(\\d+)"));
        long memFree = Lng(Rgx(mem, "MemFree:\\s*(\\d+)"));
        long memAvail = Lng(Rgx(mem, "MemAvailable:\\s*(\\d+)"));
        long swapTotal = Lng(Rgx(mem, "SwapTotal:\\s*(\\d+)"));
        long swapFree = Lng(Rgx(mem, "SwapFree:\\s*(\\d+)"));

        // /proc/stat 第一行：cpu user nice system idle iowait irq softirq steal ...
        long cpuTotal = 0, cpuIdle = 0;
        bool hasCpu = false;
        var cf = statSec.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (cf.Length >= 9 && cf[0] == "cpu")
        {
            for (int i = 1; i <= 8; i++) cpuTotal += Lng(cf[i]);
            cpuIdle = Lng(cf[4]) + Lng(cf[5]);
            hasCpu = true;
        }
        int nproc = (int)Lng(nprocS);
        string ip = Rgx(ipSec, "inet\\s+([0-9.]+)");

        var ssLines = new List<string>();
        foreach (var l in ssSec.Split('\n'))
        {
            string t = l.Trim();
            if (t.Length == 0) continue;
            ssLines.Add(t);
            if (ssLines.Count >= 80) break;
        }

        DateTime now = DateTime.Now;
        double txMbps = 0, rxMbps = 0, retriedUse = retriedPs, lostUse = lostPs;
        lock (devStatsLock)
        {
            DevTelemetry d;
            if (!devStats.TryGetValue(key, out d)) { d = new DevTelemetry(); devStats[key] = d; }
            DateTime prevT = d.prevAt;
            long prevRetried = d.prevRetriedTotal, prevLost = d.prevLostTotal;
            d.adbId = adbId; d.at = now; d.ok = true; d.err = ""; d.wifiViaDumpsys = viaDumpsys;
            if (hasWifi)
            {
                if (ssid.Length > 0) d.ssid = ssid;
                if (bssid.Length > 0) d.bssid = bssid;
                if (rssi != 0) d.rssiDbm = rssi;
                if (link > 0) d.linkMbps = link;
                if (txLink > 0) d.txLinkMbps = txLink;
                if (rxLink > 0) d.rxLinkMbps = rxLink;
                if (freq > 0) { d.freqMhz = freq; d.band = freq >= 5925 ? "6GHz" : (freq >= 4900 ? "5GHz" : "2.4GHz"); }
                if (successTx > 0) d.successTx = successTx;
                if (retriedTotal > 0) d.retriedTotal = retriedTotal;
                if (lostTotal > 0) d.lostTotal = lostTotal;
                if (retriedPs > 0) d.retriedPerSec = retriedPs;
                if (lostPs > 0) d.lostPerSec = lostPs;
                d.rawWifi = Clip(wifi, 1200);
            }
            if (ip.Length > 0) d.ip = ip;
            if (hasNet)
            {
                d.rxBytes = rxBytes; d.txBytes = txBytes; d.rxPackets = rxPkts; d.txPackets = txPkts;
                if (prevT != DateTime.MinValue && d.prevTxBytes >= 0)
                {
                    double dt = (now - prevT).TotalSeconds;
                    if (dt > 0.2)
                    {
                        txMbps = Math.Max(0, (txBytes - d.prevTxBytes) * 8.0 / dt / 1e6);
                        rxMbps = Math.Max(0, (rxBytes - d.prevRxBytes) * 8.0 / dt / 1e6);
                        d.txMbps = txMbps; d.rxMbps = rxMbps;
                    }
                }
                d.prevTxBytes = txBytes; d.prevRxBytes = rxBytes;
            }
            // 没有 PerSecond 字段的 ROM：用累计计数差自己算
            if (prevT != DateTime.MinValue)
            {
                double dt = (now - prevT).TotalSeconds;
                if (dt > 0.2)
                {
                    if (retriedPs <= 0 && prevRetried >= 0 && retriedTotal >= prevRetried)
                        d.retriedPerSec = Math.Max(0, (retriedTotal - prevRetried) / dt);
                    if (lostPs <= 0 && prevLost >= 0 && lostTotal >= prevLost)
                        d.lostPerSec = Math.Max(0, (lostTotal - prevLost) / dt);
                }
            }
            if (retriedTotal >= 0) d.prevRetriedTotal = retriedTotal;
            if (lostTotal >= 0) d.prevLostTotal = lostTotal;
            d.prevAt = now;
            if (hasLoad) { d.load1 = l1; d.load5 = l5; d.load15 = l15; }
            if (memTotal > 0) d.memTotalKb = memTotal;
            if (memFree > 0) d.memFreeKb = memFree;
            if (memAvail > 0) d.memAvailKb = memAvail;
            if (swapFree > 0) d.swapFreeKb = swapFree;
            if (swapTotal > 0) { d.swapTotalKb = swapTotal; d.swapUsedKb = Math.Max(0, swapTotal - swapFree); }
            if (hasCpu)
            {
                if (d.prevCpuTotal > 0 && cpuTotal > d.prevCpuTotal)
                {
                    long dt = cpuTotal - d.prevCpuTotal;
                    long di = cpuIdle - d.prevCpuIdle;
                    if (dt > 0) d.cpuPctDev = Math.Max(0, Math.Min(100, (1 - di / (double)dt) * 100.0));
                }
                d.prevCpuTotal = cpuTotal;
                d.prevCpuIdle = cpuIdle;
            }
            if (nproc > 0) d.nproc = nproc;
            d.ssLines = ssLines;
            d.ssOk = ssLines.Count > 0;
            d.rawSs = Clip(ssSec, 1200);
            d.hist.Add(new DevSample {
                t = now, txMbps = d.txMbps, rxMbps = d.rxMbps,
                retriedPerSec = d.retriedPerSec, lostPerSec = d.lostPerSec });
            while (d.hist.Count > 60) d.hist.RemoveAt(0);
            devFailStreak[key] = 0;
            devAttempt[key] = now.AddSeconds(5);
            Log("stats sample " + key + " rssi=" + d.rssiDbm + " tx=" + d.txMbps.ToString("0.0")
                + "Mbps retry=" + d.retriedPerSec.ToString("0.0") + "/s ss=" + ssLines.Count);
        }
        if (ip.Length > 0) ApplyResolvedIp(key, ip);
    }

    void MarkDeviceFail(string key, string err)
    {
        lock (devStatsLock)
        {
            DevTelemetry d;
            if (!devStats.TryGetValue(key, out d)) { d = new DevTelemetry(); devStats[key] = d; }
            d.err = err ?? "采样失败";
            d.ok = false;
            int streak;
            devFailStreak.TryGetValue(key, out streak);
            streak++;
            devFailStreak[key] = streak;
            devAttempt[key] = DateTime.Now.AddSeconds(streak >= 3 ? 15 : 5);
            Log("stats sample fail " + key + " " + d.err + (streak >= 3 ? "（退避 15s）" : ""));
        }
    }

    // mDNS 连接的设备没有 IP 可 ping；采样拿到 wlan0 地址后补上 RTT 和本机 TCP 端口
    void ApplyResolvedIp(string key, string ip)
    {
        IPAddress ipa;
        if (!IPAddress.TryParse(ip, out ipa)) return;
        var list = new List<CastSession>();
        lock (SessionLock) { foreach (var s in Sessions) if (DevKey(s.deviceId) == key) list.Add(s); }
        foreach (var s in list)
        {
            if (!IsWirelessId(s.deviceId) || s.deviceId.Contains(":")) continue;
            lock (s.traceLock) { if (string.IsNullOrEmpty(s.resolvedIp)) s.resolvedIp = ip; }
            try
            {
                using (var pg = new Ping())
                {
                    var rep = pg.Send(ipa, 1000);
                    bool ok = rep.Status == IPStatus.Success;
                    lock (s.traceLock)
                    {
                        s.pingSamples.Add(new PingSample { t = DateTime.Now, ms = ok ? (int)rep.RoundtripTime : 0, ok = ok });
                        while (s.pingSamples.Count > 180) s.pingSamples.RemoveAt(0);
                    }
                }
                var conns = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpConnections().Where(c => c.RemoteEndPoint.Address.ToString() == ip).ToList();
                lock (s.traceLock)
                {
                    s.tcpConns = conns.Count;
                    s.tcpStates = string.Join(", ", conns.Select(c => c.State.ToString()).Distinct().ToArray());
                    s.tcpPorts.Clear();
                    foreach (var c in conns) if (!s.tcpPorts.Contains(c.RemoteEndPoint.Port)) s.tcpPorts.Add(c.RemoteEndPoint.Port);
                }
            }
            catch { }
        }
    }

    Dictionary<string, object> BuildStatsJson(CastSession s)
    {
        string deviceId, deviceName, mode, size, cfgBitrate, cfgMaxFps, cfgMaxSize, cfgVideoBuffer, cfgAudio, cfgAudioCodec, cfgAudioKbps, cfgAudioBuffer;
        string videoCodec, audioCodec, tcpStates, streamSize;
        int fps, skippedLast, tcpConns, retryGen, displayId;
        DateTime fpsAt, started;
        double cpuPct;
        List<int> skipRing, tcpPorts;
        List<PingSample> pings;
        List<string> events;
        lock (s.traceLock)
        {
            deviceId = s.deviceId; deviceName = s.deviceName; mode = s.mode; size = s.size;
            cfgBitrate = s.cfgBitrate; cfgMaxFps = s.cfgMaxFps; cfgMaxSize = s.cfgMaxSize; cfgVideoBuffer = s.cfgVideoBuffer; cfgAudio = s.cfgAudio;
            cfgAudioCodec = s.cfgAudioCodec; cfgAudioKbps = s.cfgAudioKbps; cfgAudioBuffer = s.cfgAudioBuffer;
            videoCodec = s.videoCodec; audioCodec = s.audioCodec; streamSize = s.streamSize;
            fps = s.fps; fpsAt = s.fpsAt; skippedLast = s.skippedLast;
            skipRing = new List<int>(s.skipRing);
            pings = new List<PingSample>(s.pingSamples);
            tcpConns = s.tcpConns; tcpStates = s.tcpStates; tcpPorts = new List<int>(s.tcpPorts);
            events = new List<string>(s.events);
            cpuPct = s.cpuPct;
        }
        started = s.startedAtDt; retryGen = s.retryGen; displayId = s.displayId;

        DateTime since = DateTime.Now.AddSeconds(-60);
        int n = 0, bad = 0, sum = 0, maxMs = 0, jitterSum = 0, jitterN = 0, prevMs = -1, cur = -1;
        var pingSeries = new List<Dictionary<string, object>>();
        foreach (var p in pings)
        {
            if (p.t < since) continue;
            n++;
            if (!p.ok) { bad++; pingSeries.Add(new Dictionary<string, object> { { "t", EpochMs(p.t) }, { "ms", null } }); continue; }
            cur = p.ms; sum += p.ms; if (p.ms > maxMs) maxMs = p.ms;
            if (prevMs >= 0) { jitterSum += Math.Abs(p.ms - prevMs); jitterN++; }
            prevMs = p.ms;
            pingSeries.Add(new Dictionary<string, object> { { "t", EpochMs(p.t) }, { "ms", p.ms } });
        }
        int okN = n - bad;
        double avg = okN > 0 ? sum / (double)okN : 0;
        double jitter = jitterN > 0 ? jitterSum / (double)jitterN : 0;
        double loss = n > 0 ? bad * 100.0 / n : 0;

        int skipPerMin = 0;
        for (int i = Math.Max(0, skipRing.Count - 60); i < skipRing.Count; i++) skipPerMin += skipRing[i];
        bool fpsOk = fpsAt != DateTime.MinValue && (DateTime.Now - fpsAt).TotalMilliseconds < 5000;

        var txSeries = new List<Dictionary<string, object>>();
        var retrySeries = new List<Dictionary<string, object>>();
        Dictionary<string, object> devJson = null;
        string key = DevKey(deviceId);
        lock (devStatsLock)
        {
            DevTelemetry d;
            if (devStats.TryGetValue(key, out d))
            {
                double age = d.at == DateTime.MinValue ? -1 : (DateTime.Now - d.at).TotalMilliseconds;
                string rto = "", cwnd = "", ssthresh = "", retrans = "";
                foreach (var line in d.ssLines)
                {
                    if (line.IndexOf("ESTAB", StringComparison.Ordinal) < 0) continue;
                    bool hit = false;
                    foreach (var pt in tcpPorts)
                        if (line.IndexOf(":" + pt + " ", StringComparison.Ordinal) >= 0) { hit = true; break; }
                    if (!hit) continue;
                    rto = Rgx(line, "rto:([0-9.]+)");
                    cwnd = Rgx(line, "cwnd:(\\d+)");
                    ssthresh = Rgx(line, "ssthresh:(\\d+)");
                    retrans = Rgx(line, "retrans:(\\d+/\\d+)");
                    break;
                }
                double ratio = 0;
                if (d.successTx + d.retriedTotal > 0) ratio = d.retriedTotal * 100.0 / (d.successTx + d.retriedTotal);
                devJson = new Dictionary<string, object> {
                    { "available", d.at != DateTime.MinValue },
                    { "ok", d.ok },
                    { "pending", d.prevTxBytes < 0 },
                    { "stale", d.at != DateTime.MinValue && age > 12000 },
                    { "ageMs", age < 0 ? 0 : (long)age },
                    { "err", d.err ?? "" },
                    { "ip", d.ip ?? "" },
                    { "wifi", new Dictionary<string, object> {
                        { "ssid", d.ssid }, { "bssid", d.bssid }, { "rssiDbm", d.rssiDbm },
                        { "linkMbps", d.linkMbps }, { "txLinkMbps", d.txLinkMbps }, { "rxLinkMbps", d.rxLinkMbps },
                        { "freqMhz", d.freqMhz }, { "band", d.band }, { "source", d.wifiViaDumpsys ? "dumpsys" : "cmd-wifi" } } },
                    { "phy", new Dictionary<string, object> {
                        { "txMbps", R1(d.txMbps) }, { "rxMbps", R1(d.rxMbps) },
                        { "retriedPerSec", R1(d.retriedPerSec) }, { "lostPerSec", R1(d.lostPerSec) },
                        { "retriedTotal", d.retriedTotal }, { "lostTotal", d.lostTotal },
                        { "retryRatioPct", R1(ratio) } } },
                    { "load", new Dictionary<string, object> {
                        { "l1", R2(d.load1) }, { "l5", R2(d.load5) }, { "l15", R2(d.load15) }, { "nproc", d.nproc },
                        { "cpuPct", R1(d.cpuPctDev) } } },
                    { "mem", new Dictionary<string, object> {
                        { "totalMb", d.memTotalKb / 1024 }, { "freeMb", d.memFreeKb / 1024 },
                        { "availMb", d.memAvailKb / 1024 }, { "swapFreeMb", d.swapFreeKb / 1024 },
                        { "swapTotalMb", d.swapTotalKb / 1024 }, { "swapUsedMb", d.swapUsedKb / 1024 } } },
                    { "tcp", new Dictionary<string, object> {
                        { "rto", rto }, { "cwnd", cwnd }, { "ssthresh", ssthresh }, { "retrans", retrans } } },
                    { "raw", new Dictionary<string, object> { { "wifi", d.rawWifi ?? "" }, { "ss", d.rawSs ?? "" } } }
                };
                foreach (var h in d.hist)
                {
                    if (h.t < since) continue;
                    txSeries.Add(new Dictionary<string, object> { { "t", EpochMs(h.t) }, { "mbps", R1(h.txMbps) } });
                    retrySeries.Add(new Dictionary<string, object> { { "t", EpochMs(h.t) }, { "n", R1(h.retriedPerSec) } });
                }
            }
        }
        if (devJson == null)
            devJson = new Dictionary<string, object> { { "available", false }, { "err", "还没有设备侧数据" } };

        var result = new Dictionary<string, object> {
            { "ok", true }, { "found", true }, { "pid", s.pid }, { "nowMs", EpochMs(DateTime.Now) },
            { "deviceId", deviceId }, { "deviceName", string.IsNullOrEmpty(deviceName) ? deviceId : deviceName },
            { "link", IsWirelessId(deviceId) ? "wireless" : "usb" },
            { "mode", mode }, { "size", size }, { "streamSize", streamSize }, { "displayId", displayId },
            { "uptimeSec", started == DateTime.MinValue ? 0 : (long)(DateTime.Now - started).TotalSeconds },
            { "retryGen", retryGen },
            { "cfg", new Dictionary<string, object> {
                { "bitrateLabel", cfgBitrate }, { "bitrateMbps", R1(BitrateMbps(cfgBitrate)) },
                { "maxFps", cfgMaxFps.Length > 0 ? cfgMaxFps : "不限" },
                { "maxSize", cfgMaxSize }, { "videoBuffer", cfgVideoBuffer },
                { "audio", cfgAudio }, { "audioCodec", cfgAudioCodec }, { "audioKbps", cfgAudioKbps },
                { "audioBuffer", cfgAudioBuffer }, { "videoCodec", videoCodec }, { "audioCodecActual", audioCodec } } },
            { "fps", new Dictionary<string, object> {
                { "cur", fps }, { "skippedLast", skippedLast }, { "skippedPerMin", skipPerMin },
                { "ageMs", fpsAt == DateTime.MinValue ? -1 : (long)(DateTime.Now - fpsAt).TotalMilliseconds },
                { "available", fpsOk } } },
            { "host", new Dictionary<string, object> {
                { "available", IsWirelessId(deviceId) }, { "tcpConns", tcpConns }, { "tcpStates", tcpStates },
                { "pingCurMs", cur }, { "pingAvgMs", R1(avg) }, { "pingMaxMs", maxMs },
                { "jitterMs", R1(jitter) }, { "lossPct", R1(loss) }, { "windowSec", 60 }, { "samples", n },
                { "cpuPct", R1(cpuPct) } } },
            { "dev", devJson },
            { "series", new Dictionary<string, object> { { "ping", pingSeries }, { "tx", txSeries }, { "retry", retrySeries } } },
            { "events", events },
            { "notes", new List<string> {
                "吞吐取自设备 wlan0 计数，含重传、ACK 和其它应用流量，是近似值",
                "Ping 是本机到设备的链路往返，不是画面端到端延迟",
                "单连接 TCP 字节/RTT 需要管理员权限，当前用无线重试、丢包和套接字 RTO 代替" } }
        };
        return result;
    }

    Dictionary<string, object> EndedJson(EndedSession e)
    {
        int repl = 0;
        lock (SessionLock)
            foreach (var s in Sessions)
                if (s.pid != e.pid && string.Equals(s.deviceId, e.deviceId, StringComparison.OrdinalIgnoreCase)) { repl = s.pid; break; }
        return new Dictionary<string, object> {
            { "ok", true }, { "pid", e.pid }, { "found", false },
            { "ended", new Dictionary<string, object> {
                { "atMs", EpochMs(e.at) }, { "code", e.code }, { "abnormal", e.abnormal },
                { "intentional", e.intentional }, { "retryGen", e.retryGen },
                { "deviceId", e.deviceId }, { "deviceName", e.deviceName },
                { "mode", e.mode }, { "size", e.size },
                { "tail", e.tail }, { "events", e.events }, { "replacementPid", repl } } }
        };
    }

    object CopyText(Dictionary<string, object> p)
    {
        string text = Ps(p, "text");
        if (text.Length == 0) return Err("没有可复制的内容");
        if (text.Length > 65536) text = text.Substring(0, 65536);
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return new Dictionary<string, object> { { "ok", true } }; }
            catch (Exception ex)
            {
                if (i == 2) return Err("剪贴板被占用：" + ex.Message);
                Thread.Sleep(60);
            }
        }
        return Err("剪贴板被占用");
    }

    static string Section(string text, string start, string[] all)
    {
        int i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return "";
        i += start.Length;
        int end = text.Length;
        foreach (var m in all)
        {
            int j = text.IndexOf(m, i, StringComparison.Ordinal);
            if (j >= 0 && j < end) end = j;
        }
        return text.Substring(i, end - i);
    }

    static string Rgx(string text, string pat)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", pat);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : "";
    }

    static long Lng(string s) { long v; long.TryParse((s ?? "").Trim(), out v); return v; }

    static double Dbl(string s)
    {
        double v;
        double.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v);
        return v;
    }

    static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    static double R1(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 1); }
    static double R2(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Round(v, 2); }
    static long EpochMs(DateTime t) { return (long)(t.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

    static double BitrateMbps(string v)
    {
        string t = (v ?? "").Trim().ToUpperInvariant();
        double n = 0;
        if (t.EndsWith("M")) { double.TryParse(t.Substring(0, t.Length - 1), out n); return n; }
        if (t.EndsWith("K")) { double.TryParse(t.Substring(0, t.Length - 1), out n); return n / 1000.0; }
        if (double.TryParse(t, out n)) return n / 1e6;
        return 0;
    }

    // 投屏异常断开：等设备回到 adb（最多一分钟），用原参数重连一次。
    // 只兜"同一地址回来"；发现它换了端口就停手，提示用户到无线页确认，别自作主张换地址连。
    async Task AutoReconnect(CastSession old, Func<Task<object>> again)
    {
        string id = old.deviceId, ip = HostOf(id);
        string nm = string.IsNullOrEmpty(old.deviceName) ? id : old.deviceName;
        int gen = old.retryGen;
        uiNotice = nm + " 掉线了，正在自动重连" + (gen > 1 ? "（第 " + gen + " 次）" : "") + "…";
        Log("自动重连开始 " + id);
        for (int i = 0; i < 30; i++)
        {
            if (await DeviceState(id) == "device")
            {
                var r = await again();
                var d = r as Dictionary<string, object>;
                bool ok = d != null && !d.ContainsKey("error");
                uiNotice = ok ? (nm + " 掉线后已自动重连")
                    : (nm + " 掉线了，自动重连没成功，请手动连接；设备分辨率保持原样，需要还原到「显示还原」页");
                Log("自动重连" + (ok ? "完成 " : "失败 ") + id);
                return;
            }
            if (i == 3 || i == 10 || i == 20)
            {
                string md = await FindMdnsAddr(ip, 1);
                if (!string.IsNullOrEmpty(md) && !string.Equals(md, id, StringComparison.OrdinalIgnoreCase))
                {
                    uiNotice = nm + " 换了端口（" + md + "），没有自动换地址去连，请到无线连接页点「连接」";
                    Log("自动重连发现新地址 " + md + "，交给用户确认");
                    return;
                }
            }
            await Task.Delay(2000);
        }
        uiNotice = nm + " 掉线了，一直没回到网络；自动重连已停止。设备分辨率保持原样，回来后可到「显示还原」页还原";
        Log("自动重连放弃 " + id);
    }

    Task<object> ListWindows()
    {
        // 直接枚举本机进程，不绕外部命令，省得再被命令行转义坑一次
        var list = new List<object>();
        foreach (var pr in Process.GetProcessesByName("scrcpy"))
        {
            string title = "";
            try { title = pr.MainWindowTitle; } catch { }
            list.Add(new Dictionary<string, object> { { "pid", pr.Id }, { "title", title } });
        }
        return Task.FromResult<object>(list);
    }

    async Task<object> CloseWindows()
    {
        lock (SessionLock) { foreach (var s in Sessions) s.intentional = true; }
        foreach (var pr in Process.GetProcessesByName("scrcpy")) { try { pr.CloseMainWindow(); } catch { } }
        await Task.Delay(1200);
        return await ListWindows();
    }

    void SaveState(string id, string sizeOverride, string dpiOverride)
    {
        // 无线端口每次重连都会变，只记 ip 才能端口换了还认得出是同一台机器
        WriteJson(StateFile, new Dictionary<string, object> {
            { "id", id }, { "host", HostOf(id) },
            { "sizeOverride", sizeOverride }, { "dpiOverride", dpiOverride } });
    }

    static string HostOf(string id)
    {
        int c = (id ?? "").LastIndexOf(':');
        return c > 0 ? id.Substring(0, c) : (id ?? "");
    }

    // 无线设备有两种编号：IP:端口，或 adb 用 mDNS 自动连上来的服务名
    // （形如 adb-<序列号>-<后缀>._adb-tls-connect._tcp）。后者没有冒号，不能当成 USB。
    static bool IsWirelessId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        return id.Contains(":") || id.IndexOf("_adb-tls-", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string BaselineKey(string id)
    {
        return (id ?? "").Contains(":") ? HostOf(id) : (id ?? "");
    }

    Dictionary<string, object> LoadBaselines()
    {
        return ReadJson(BaselineFile) as Dictionary<string, object> ?? new Dictionary<string, object>();
    }

    // 通用做法：在动手改设备参数之前，先把「设备当时实际生效的分辨率/密度」存一份快照。
    // 还原时写回这个快照，而不是 wm density reset —— reset 的语义是回到出厂默认，
    // 永远不等于回到用户在系统设置里选的挡位（各厂商 ROM 存挡位的方式都不一样，
    // 靠猜厂商私有属性既不通用也不可靠，快照法只问设备本身，任何 ROM 都成立）。
    void RememberBaseline(string id, Dictionary<string, object> wm)
    {
        if (string.IsNullOrEmpty(id) || wm == null) return;
        // 已经有未还原的改动时不能覆盖：那时读到的可能是我们自己设的值
        if (File.Exists(StateFile) || File.Exists(PendingFile)) return;
        try
        {
            var entry = new Dictionary<string, object> {
                { "size", Convert.ToString(wm["currentSize"]) },
                { "dpi", Convert.ToString(wm["currentDpi"]) },
                { "physSize", Convert.ToString(wm["physicalSize"]) },
                { "physDpi", Convert.ToString(wm["physicalDpi"]) },
                { "ovSize", (bool)wm["hadSizeOverride"] ? Convert.ToString(wm["currentSize"]) : "" },
                { "ovDpi", (bool)wm["hadDpiOverride"] ? Convert.ToString(wm["currentDpi"]) : "" }
            };
            var all = LoadBaselines();
            object oldO;
            // 首次没有这张表，取值会抛 KeyNotFoundException，必须先试取
            all.TryGetValue(BaselineKey(id), out oldO);
            var old = oldO as Dictionary<string, object>;
            // 设备当前这个覆盖值如果正是我们上次设的，就不能当"原始状态"记下来；
            // 否则还原只会写回同一个值，表现就是"点了还原没反应"。
            var ap = AppliedFor(id);
            bool sizeMine = ap != null && (bool)wm["hadSizeOverride"]
                && Convert.ToString(wm["currentSize"]) == Tv(ap, "size");
            bool dpiMine = ap != null && (bool)wm["hadDpiOverride"]
                && Convert.ToString(wm["currentDpi"]) == Tv(ap, "dpi");
            if (sizeMine || dpiMine)
            {
                // 已经有干净基准就保留，别让我们的缩放把它覆盖掉
                if (old != null && Tv(old, "ovSize").Length == 0 && Tv(old, "ovDpi").Length == 0) return;
                // 没有干净基准时按物理分辨率记，至少给还原留一条回得去的路
                entry["size"] = entry["physSize"];
                entry["dpi"] = entry["physDpi"];
                entry["ovSize"] = "";
                entry["ovDpi"] = "";
            }
            else if (ap != null) ClearApplied(id);   // 和设备现状对不上，说明已经不是我们设的值了
            // 和上次记的一样就不动盘：设备列表每几秒扫一次，没必要跟着一直写文件
            if (old != null && Convert.ToString(old["size"]) == Convert.ToString(entry["size"])
                && Convert.ToString(old["dpi"]) == Convert.ToString(entry["dpi"])
                && Convert.ToString(old["physSize"]) == Convert.ToString(entry["physSize"])
                && Convert.ToString(old["physDpi"]) == Convert.ToString(entry["physDpi"])
                && Convert.ToString(old["ovSize"]) == Convert.ToString(entry["ovSize"])
                && Convert.ToString(old["ovDpi"]) == Convert.ToString(entry["ovDpi"])) return;
            entry["at"] = DateTime.Now.ToString("HH:mm:ss");
            all[BaselineKey(id)] = entry;
            WriteJson(BaselineFile, all);
        }
        catch (Exception ex) { Log("记录显示基准失败: " + ex.Message); }
    }

    Dictionary<string, object> BaselineFor(string id)
    {
        var all = LoadBaselines();
        object o;
        all.TryGetValue(BaselineKey(id), out o);
        return o as Dictionary<string, object>;
    }

    // 记下我们最后一次给这台设备设过的分辨率和显示密度。
    // 用来识别"设备现在这个覆盖值是不是我们自己留下的"——不能把自己的放大值当成用户的原始状态。
    void SaveApplied(string id, string size, string dpi)
    {
        try
        {
            var all = ReadJson(AppliedFile) as Dictionary<string, object> ?? new Dictionary<string, object>();
            all[BaselineKey(id)] = new Dictionary<string, object> {
                { "size", size ?? "" }, { "dpi", dpi ?? "" }, { "at", DateTime.Now.ToString("HH:mm:ss") }
            };
            WriteJson(AppliedFile, all);
        }
        catch { }
    }

    Dictionary<string, object> AppliedFor(string id)
    {
        try
        {
            var all = ReadJson(AppliedFile) as Dictionary<string, object>;
            object o;
            return all != null && all.TryGetValue(BaselineKey(id), out o) ? o as Dictionary<string, object> : null;
        }
        catch { return null; }
    }

    void ClearApplied(string id)
    {
        try
        {
            var all = ReadJson(AppliedFile) as Dictionary<string, object>;
            if (all != null && all.Remove(BaselineKey(id))) WriteJson(AppliedFile, all);
        }
        catch { }
    }

    void RemoveBaseline(string id)
    {
        try
        {
            var all = LoadBaselines();
            if (all.Remove(BaselineKey(id))) WriteJson(BaselineFile, all);
        }
        catch { }
    }

    static string Tv(Dictionary<string, object> d, string k)
    {
        object v;
        return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
    }

    // 还原目标：本次记录 > 历史快照里的覆盖值 > 历史快照里的生效值 > reset
    static string PickRestore(string recorded, Dictionary<string, object> bl, string ovKey, string curKey, string physKey)
    {
        if (!string.IsNullOrEmpty(recorded) && recorded != "null") return recorded;
        if (bl != null)
        {
            string ov = Tv(bl, ovKey);
            if (!string.IsNullOrEmpty(ov) && ov != "null") return ov;
            string cur = Tv(bl, curKey), phys = Tv(bl, physKey);
            if (!string.IsNullOrEmpty(cur) && cur != "null" && cur != phys) return cur;
        }
        return "reset";
    }

    async Task<object> Restore(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (HasLiveSession(id)) return Err("这台设备正在投屏，先关掉这一路再还原——中途改分辨率会把正在跑的应用打乱");
        var st = ReadJson(StateFile) as Dictionary<string, object>;
        string sid = Tv(st, "id");
        string shost = Tv(st, "host");
        // 端口轮换后完整地址会变，同 ip 也算同一台设备
        bool same = st != null && (sid == id || (!string.IsNullOrEmpty(shost) && shost == HostOf(id)));
        var bl = BaselineFor(id);
        string sizeArg = PickRestore(same ? Tv(st, "sizeOverride") : "", bl, "ovSize", "size", "physSize");
        string dpiArg = PickRestore(same ? Tv(st, "dpiOverride") : "", bl, "ovDpi", "dpi", "physDpi");
        Log("还原 " + id + " -> size=" + sizeArg + " density=" + dpiArg);
        var before = await ReadWm(id);
        var r1 = await Adb(id, "shell", "wm", "size", sizeArg);
        var r2 = await Adb(id, "shell", "wm", "density", dpiArg);
        if (!r1.ok || !r2.ok) return Err("设备没响应，没能还原设备分辨率与显示大小");
        if (same) { try { File.Delete(StateFile); } catch { } }   // 设备没响应时记录要留着，重连后再补
        ClearApplied(id);
        var after = await ReadWm(id);
        // 目标值本来就是设备现在的值，写进去不会有任何变化：明确告诉界面，
        // 别让人以为"点了还原没生效"——要继续回设备本身的分辨率得用「恢复默认」
        bool noop = Convert.ToString(before["currentSize"]) == Convert.ToString(after["currentSize"])
                 && Convert.ToString(before["currentDpi"]) == Convert.ToString(after["currentDpi"]);
        after["ok"] = true;
        after["noop"] = noop;
        after["targetSize"] = sizeArg;
        after["targetDpi"] = dpiArg;
        return after;
    }

    // 兜底：直接清掉分辨率与显示密度的覆盖值，回到设备自己的物理默认。
    // 只该在「还原」写回的原状态本身就不对时用（比如基准被上一次放大污染），界面上要二次确认。
    async Task<object> RestoreDefault(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (string.IsNullOrEmpty(id)) return Err("请先选中一台设备");
        if (HasLiveSession(id)) return Err("这台设备正在投屏，先关掉这一路再恢复默认——中途改分辨率会把正在跑的应用打乱");
        Log("恢复默认显示参数 " + id);
        var r1 = await Adb(id, "shell", "wm", "size", "reset");
        var r2 = await Adb(id, "shell", "wm", "density", "reset");
        if (!r1.ok || !r2.ok) return Err("设备没响应，没能恢复默认显示");
        // 这台设备的未还原记录、待补还原和"我们设过的值"一起清掉，免得下次又按旧记录写回去
        try
        {
            var st = ReadJson(StateFile) as Dictionary<string, object>;
            if (st != null && (Tv(st, "id") == id || Tv(st, "host") == HostOf(id))) File.Delete(StateFile);
        }
        catch { }
        try
        {
            var pd = ReadJson(PendingFile) as Dictionary<string, object>;
            if (pd != null && (Tv(pd, "id") == id || Tv(pd, "host") == HostOf(id))) File.Delete(PendingFile);
        }
        catch { }
        ClearApplied(id);
        var now = await ReadWm(id);
        RemoveBaseline(id);
        RememberBaseline(id, now);   // 现在是物理默认，重新记一份干净基准
        now["ok"] = true;
        return now;
    }

    async Task<object> ToWireless(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        var ip = await Adb(id, "shell", "ip -4 addr show wlan0 | grep -oE 'inet [0-9.]+' | cut -d' ' -f2");
        string addr = Clean(ip.text).Split('\n')[0];
        if (addr.Length == 0) return Err("读不到手机 WiFi 地址，确认手机已连 WiFi");
        await Adb(id, "tcpip", "5555");
        await Task.Delay(2500);
        var c = await Run(adbExe, new[] { "connect", addr + ":5555" });
        bool ok = c.text.Contains("connected");
        return new Dictionary<string, object> { { "ok", ok }, { "addr", addr }, { "error", ok ? "" : c.text.Trim() } };
    }

    async Task<object> ToWired(Dictionary<string, object> p)
    {
        var r = await Adb(Ps(p, "id"), "usb");
        return new Dictionary<string, object> { { "ok", r.ok } };
    }

    const int AdbMaxDevices = 16;

    async Task<List<string>> ConnectedWireless()
    {
        var r = await Run(adbExe, new[] { "devices" });
        var list = new List<string>();
        foreach (var line in r.text.Split('\n'))
        {
            // 必须先 Trim：Windows 下行尾带回车，不去掉会让状态值变成「device\r」，一台都判不出来
            var p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[1] == "device" && IsWirelessId(p[0])) list.Add(p[0]);
        }
        return list;
    }

    class MdnsScan { public List<WifiDev> devs = new List<WifiDev>(); public int rawEntries; public string raw = ""; }

    // 广播只做发现：不在这里连设备，也不在这里扫端口
    async Task<MdnsScan> CollectMdns(int rounds, int perCallMs, int gapMs)
    {
        var res = new MdnsScan();
        var byIp = new Dictionary<string, WifiDev>();
        for (int attempt = 0; attempt < rounds; attempt++)
        {
            var r = await Run(adbExe, new[] { "mdns", "services" }, perCallMs);
            string text = r.text ?? "";
            if (res.raw.Length == 0 && text.Trim().Length > 0) res.raw = text;
            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("List of")) continue;
                var tokens = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string addr = null, name = "";
                foreach (var x in tokens)
                {
                    var s = x.Split(':');
                    if (s.Length == 2 && s[1].Length > 0 && s[1].All(char.IsDigit)) { addr = x; break; }
                }
                if (addr == null) continue;
                res.rawEntries++;
                name = string.Join(" ", tokens.Where(t => t != addr)
                    .Select(t => t.Replace("._adb-tls-connect._tcp", "").Replace("._adb-tls-pairing._tcp", ""))
                    .Where(t => t.Length > 0));
                var ip = addr.Split(':')[0];
                bool pairing = l.IndexOf("pairing", StringComparison.OrdinalIgnoreCase) >= 0;
                var ser = System.Text.RegularExpressions.Regex.Match(name, "adb-([0-9A-Za-z]+)-");
                WifiDev dev;
                if (!byIp.TryGetValue(ip, out dev)) { dev = new WifiDev { ip = ip }; byIp[ip] = dev; }
                if (string.IsNullOrEmpty(dev.name)) dev.name = name;
                if (string.IsNullOrEmpty(dev.serial) && ser.Success) dev.serial = ser.Groups[1].Value;
                if (pairing) { if (dev.pairAddr == null) dev.pairAddr = addr; }
                else if (dev.connAddr == null) dev.connAddr = addr;
            }
            if (attempt >= 1 && byIp.Count > 0) break;
            if (attempt + 1 < rounds) await Task.Delay(gapMs);
        }
        res.devs = byIp.Values.ToList();
        return res;
    }

    Dictionary<string, object> KnownItem(WifiEntry e, string name, string state, string reason, string note, bool alive)
    {
        return new Dictionary<string, object> {
            { "addr", e.addr }, { "kind", "known" }, { "known", true }, { "connected", false },
            { "state", state }, { "reason", reason }, { "note", note }, { "name", name },
            { "mac", e.mac }, { "serial", e.serial }, { "mdnsId", e.mdnsId },
            { "alive", alive }, { "bookAddr", e.addr },
            { "hasIdentity", !string.IsNullOrEmpty(e.mac) || !string.IsNullOrEmpty(e.serial) || !string.IsNullOrEmpty(e.mdnsId) },
            { "canRecover", true } };
    }

    // 普通扫描：以「网段探活」为准 —— IP 能通就算扫到，端口只对认得的设备探一次。
    // 端口不对就停在卡片上，等用户点「找端口」单独扫这一台；扫描本身不自动连、不扫端口段。
    // light=true 时只做广播 + 地址簿（配对成功后重新发现走这条路，避免多等一次网段扫描）。
    async Task<object> WifiDiscover(bool light = false)
    {
        var devices = new List<object>();
        var offline = new List<object>();
        var recovered = new List<object>();
        if (adbExe == null)
            return new Dictionary<string, object> {
                { "devices", devices }, { "offline", offline }, { "recovered", recovered },
                { "mdnsBlocked", false }, { "lanOk", false } };

        var names = LoadNames();
        var entries = LoadWifiEntries();
        RelocateState busy;
        bool finding = RelocBusy(out busy);
        if (!finding) await CleanupOffline();   // 查找中别去动 adb，免得打断正在验证的连接

        // 手机助手类程序常把 5037 抢去跑自己的 adb；它不支持新版无线调试，会让所有端口都"不是无线调试"。
        // 普通扫描且没有投屏在跑时，先换回自带 adb，再开始发现和验证。
        if (!finding && !light && LiveSessions().Count == 0 && ForeignAdbOwner().Length > 0)
        {
            EnsureOwnAdbServer();
            uiNotice = "检测到 5037 被其它程序的 adb 服务占用，已换回自带 adb。";
        }

        var connected = await ConnectedWireless();
        var connIps = new HashSet<string>(connected.Select(c => c.Split(':')[0]), StringComparer.OrdinalIgnoreCase);

        // 广播和网段探活同时跑：广播负责认名字和配对端口，探活负责"IP 通就算在线"
        LanTarget lan = GetLanTarget(entries.Count > 0 ? entries[0].addr.Split(':')[0] : null);
        bool lanOk = lan != null && !finding && !light;
        // 广播全无时每轮都要等满超时，普通扫描压到两轮；配对后那次更要准，保留三轮
        var mdnsTask = CollectMdns(light ? 3 : 2, light ? 2500 : 2200, 300);
        Task<List<LanHost>> sweepTask = lanOk ? ArpSweep(lan, null) : Task.FromResult(new List<LanHost>());
        await Task.WhenAll(mdnsTask, sweepTask);
        var mdns = mdnsTask.Result;
        var scanHosts = sweepTask.Result;
        bool mdnsEmpty = mdns.devs.Count == 0;

        var hostByIp = new Dictionary<string, LanHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in scanHosts) if (!hostByIp.ContainsKey(h.ip)) hostByIp[h.ip] = h;
        // 代理 ARP 会让多个 IP 共用一个 MAC：有歧义的 MAC 不参与"换 IP"匹配
        var macOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in scanHosts.Where(h => !string.IsNullOrEmpty(h.mac)).GroupBy(h => h.mac, StringComparer.OrdinalIgnoreCase))
            if (g.Count() == 1) macOwner[g.Key] = g.First().ip;

        var claimed = new HashSet<string>(connIps, StringComparer.OrdinalIgnoreCase);  // 已经出过行的 IP
        var usedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 已经认领的地址簿条目
        // 已经连着的设备，地址簿里同 IP 的条目也算「用过」。不提前认领的话，它会落到下面的
        // 离线复核里再探一次端口；探针结束要 adb disconnect，那一下会把用户正连着的连接掐断，
        // 表现就是「什么都没干，设备自己掉线了」。
        foreach (var e0 in entries)
        {
            string eip = e0.addr.Split(':')[0];
            if (claimed.Contains(eip)) usedEntries.Add(e0.addr);
        }

        // 1) 广播里的设备：端口现成，最先列
        foreach (var dev in mdns.devs)
        {
            if (string.IsNullOrEmpty(dev.ip) || claimed.Contains(dev.ip)) continue;
            string addr = dev.connAddr ?? dev.pairAddr ?? dev.ip;
            WifiEntry entry = entries.FirstOrDefault(e =>
                (!string.IsNullOrEmpty(dev.serial) &&
                 (IdEq(e.serial, dev.serial) || IdEq(e.mdnsId, dev.serial))) ||
                e.addr.Split(':')[0] == dev.ip);
            // 插着数据线也照样列无线地址：那正是"能搜到、端口是新的"那条可用路径；
            // 地址簿里同一 IP 的旧端口在下面会被 usedEntries 按掉，不会留假信息
            string name = LookupName(names, dev.serial, addr);
            if (name.Length == 0 && entry != null) name = entry.name;
            if (entry != null)
            {
                usedEntries.Add(entry.addr);
                if (dev.connAddr != null && entry.addr != dev.connAddr)
                {
                    devices.Add(new Dictionary<string, object> {
                        { "addr", dev.connAddr }, { "kind", "connect" }, { "connected", false },
                        { "name", name }, { "movedFrom", entry.addr }, { "bookAddr", entry.addr },
                        { "note", "这台设备换了 IP，点「连接」即可；扫描不会自动连接。" }, { "canRecover", true } });
                    claimed.Add(dev.ip);
                    continue;
                }
            }
            devices.Add(new Dictionary<string, object> {
                { "addr", addr }, { "kind", dev.connAddr != null ? "connect" : "pairing" },
                { "connected", false }, { "name", name } });
            claimed.Add(dev.ip);
        }

        // 2) 认得的设备：IP 对得上 → 探一次旧端口；MAC 唯一对得上 → 换了 IP
        var probeEntries = new List<WifiEntry>();
        var probeIps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (usedEntries.Contains(e.addr)) continue;
            string ip = e.addr.Split(':')[0];
            if (claimed.Contains(ip)) continue;                 // 广播已经占了这个地址
            string hostIp = null;
            if (hostByIp.ContainsKey(ip)) hostIp = ip;
            else if (!string.IsNullOrEmpty(e.mac) && macOwner.ContainsKey(e.mac)) hostIp = macOwner[e.mac];
            if (hostIp == null) continue;                       // 网段里没它，下面按离线处理
            LanHost h = hostByIp.ContainsKey(hostIp) ? hostByIp[hostIp] : null;
            if (hostIp == ip && h != null && !string.IsNullOrEmpty(e.mac) && !string.IsNullOrEmpty(h.mac)
                && !string.Equals(e.mac, h.mac, StringComparison.OrdinalIgnoreCase))
                continue;                                       // 这个 IP 现在是别的设备，不冒认
            if (claimed.Contains(hostIp)) continue;
            usedEntries.Add(e.addr);
            claimed.Add(hostIp);
            probeEntries.Add(e);
            probeIps[e.addr] = hostIp;
        }

        var probeOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var probeTasks = new List<Task>();
        foreach (var e in probeEntries)
        {
            WifiEntry en = e;
            string hip = probeIps[en.addr];
            probeTasks.Add(Task.Run(async () =>
            {
                int port = 0;
                string ps = en.addr.Contains(":") ? en.addr.Split(':')[1] : "";
                int.TryParse(ps, out port);
                string verdict = await ProbeAdbPort(hip, port);
                lock (probeOut) probeOut[en.addr] = verdict;
            }));
        }
        // 3) 剩下的地址簿条目：补 ping 复核（漏一包不算掉线），真不通才进"上次用过的"
        var restTasks = new List<Task<Dictionary<string, object>>>();
        foreach (var e in entries)
        {
            if (usedEntries.Contains(e.addr)) continue;
            if (claimed.Contains(e.addr.Split(':')[0])) continue;   // 已经连着的不再复核
            WifiEntry en = e;
            restTasks.Add(Task.Run(async () =>
            {
                string ip = en.addr.Split(':')[0];
                string nm = en.name.Length > 0 ? en.name : LookupName(names, en.serial, en.addr);
                // 网段扫描已经 ping 过一轮的，只补两包复核；不在被扫网段的才做完整复核，否则一次扫描要多等三秒
                bool inLan = lan != null && SameSubnet(lan.localIp, lan.prefix, ip);
                if (await (inLan ? PingQuick(ip) : PingAlive(ip)))
                {
                    // IP 通但没进被扫网段（多网卡/VPN）：照样算在线，端口同样要真验一次
                    int port = 0;
                    string ps = en.addr.Contains(":") ? en.addr.Split(':')[1] : "";
                    int.TryParse(ps, out port);
                    string verdict = await ProbeAdbPort(ip, port);
                    return KnownItem(en, nm, verdict == "adb" ? "idle" : "fail", PortReason(verdict, false),
                        PortNote(verdict, false, ip), true);
                }
                LanHost h = hostByIp.ContainsKey(ip) ? hostByIp[ip] : null;
                if (h != null)
                    return KnownItem(en, nm, "gone", "ipUsed",
                        "这个地址现在是别的设备：手机可能换了 IP，点「找回这台」按身份再找一次。", false);
                return KnownItem(en, nm, "gone", "offline",
                    "没回应：可能息屏后 Wi-Fi 断了，也可能换了 IP。点亮屏幕再点「重新扫描」试试。", false);
            }));
        }

        // 端口验证和离线复核互不依赖，一起等；分成两段串行会把一次扫描拖到十几秒
        await Task.WhenAll(probeTasks.Cast<Task>().Concat(restTasks));

        foreach (var e in probeEntries)
        {
            string hip = probeIps[e.addr];
            string verdict;
            probeOut.TryGetValue(e.addr, out verdict);
            if (string.IsNullOrEmpty(verdict)) verdict = "closed";
            bool moved = !string.Equals(hip, e.addr.Split(':')[0], StringComparison.OrdinalIgnoreCase);
            string name = e.name.Length > 0 ? e.name : LookupName(names, e.serial, e.addr);
            var item = KnownItem(e, name, verdict == "adb" ? "idle" : "fail", PortReason(verdict, moved), PortNote(verdict, moved, hip), true);
            item["addr"] = hip + ":" + (e.addr.Contains(":") ? e.addr.Split(':')[1] : "0");
            item["bookAddr"] = e.addr;
            if (moved) item["hintIp"] = hip;
            devices.Add(item);
        }
        foreach (var t in restTasks)
        {
            var it = t.Result;
            if (it == null) continue;
            if (Convert.ToString(it["state"]) == "gone") offline.Add(it); else devices.Add(it);
        }

        Log("wifi-discover 在线 " + devices.Count + " 台、上次用过 " + offline.Count + " 台（已连接 IP: " + string.Join(",", connIps) +
            "；广播条目 " + mdns.rawEntries + " 条，入列 " + mdns.devs.Count + " 台；网段在线主机 " + scanHosts.Count +
            " 台，lanOk=" + lanOk + "；地址簿 " + entries.Count + " 条）");
        if (mdnsEmpty)
            Log("wifi-discover 无广播，mdns 原始输出: " + (mdns.raw.Length > 0 ? mdns.raw.Replace("\r", "").Replace("\n", " | ") : "(空)"));

        return new Dictionary<string, object> {
            { "devices", devices }, { "offline", offline }, { "recovered", recovered },
            { "mdnsBlocked", mdnsEmpty }, { "lanOk", lanOk } };
    }

    // 给只关心设备列表的内部调用用（手动连接、配对后的回退路径）：轻量模式不扫网段
    async Task<List<object>> DiscoverDevices(bool light = false)
    {
        var obj = await WifiDiscover(light) as Dictionary<string, object>;
        if (obj == null) return new List<object>();
        var list = obj.ContainsKey("devices") ? obj["devices"] as List<object> : null;
        return list ?? new List<object>();
    }

    // 单发 ping 会误判：设备息屏后 Wi-Fi 进省电，第一包常被丢，一次不通不等于不在线。
    // 先按老办法单包探（快），漏了再补三包（约 2 秒），有一包回来就算活着。
    async Task<bool> PingAlive(string ip)
    {
        var pg = await Run("ping", new[] { "-n", "1", "-w", "700", ip }, 5000);
        if (pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        pg = await Run("ping", new[] { "-n", "3", "-w", "700", ip }, 6000);
        return pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // 端口探测的并发上限：验证要真连一次再断开，别几台一起把 adb 问爆
    static readonly SemaphoreSlim PortGate = new SemaphoreSlim(3);

    // 只看 TCP 通不通会被骗：厂商自己的服务也占这种端口（实测 realme 的 46888 能连上但不是 adb，
    // 真端口是当时广播里那个）。所以要真连一次、看设备状态，验完立刻断开——扫描不该把设备悄悄连上。
    // 返回：adb=确认是无线调试 / auth=找到了但要重新配对 / notadb=端口开着但不是无线调试 / closed=不通
    // 假 adb 端口会让 adb connect 卡满十秒（实测 realme 的 46888 能连上但不是 adb）。
    // 验过的结论落盘，重启程序也不用重验；十分钟过期，避免设备换了用途还按旧结论显示。
    string PortCacheFile { get { return Path.Combine(DataDir, "port-probe-cache.json"); } }

    bool PortCacheTryGet(string addr, out string verdict)
    {
        verdict = "";
        var map = ReadJson(PortCacheFile) as Dictionary<string, object>;
        if (map == null) return false;
        object o;
        if (!map.TryGetValue(addr, out o)) return false;
        var d = o as Dictionary<string, object>;
        if (d == null) return false;
        string v = Convert.ToString(d["verdict"]);
        DateTime at;
        if (!DateTime.TryParse(Convert.ToString(d["at"]), out at)) return false;
        if ((DateTime.Now - at).TotalMinutes >= 10) return false;
        if (v != "adb" && v != "notadb") return false;
        verdict = v;
        return true;
    }

    void PortCachePut(string addr, string verdict)
    {
        var map = ReadJson(PortCacheFile) as Dictionary<string, object>;
        if (map == null) map = new Dictionary<string, object>();
        map[addr] = new Dictionary<string, object> {
            { "verdict", verdict }, { "at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") } };
        WriteJson(PortCacheFile, map);
    }

    async Task<string> ProbeAdbPort(string ip, int port)
    {
        if (port <= 0) return "closed";
        string addr = ip + ":" + port;
        string cached;
        if (PortCacheTryGet(addr, out cached)) return cached;
        // 这条地址已经连着：扫描不探也不断。探针结束会执行 adb disconnect，
        // 那一下会把用户正在用的连接掐掉（曾经因此表现为"什么都没干也掉线"）。
        try
        {
            var live = await ConnectedWireless();
            if (live.Any(x => string.Equals(x, addr, StringComparison.OrdinalIgnoreCase)))
            {
                PortCachePut(addr, "adb");
                return "adb";
            }
        }
        catch { }
        if (!await PortProbe(ip, port, 700)) return "closed";
        await PortGate.WaitAsync();
        string verdict = "notadb";
        try
        {
            // 扫描里的验证要克制：连接最多等 2.5 秒，判定最多再等 2 秒
            var cv = await ConnectVerified(addr, 2000, 2500);
            verdict = (bool)cv["ok"] ? "adb"
                : ("unauthorized".Equals(Convert.ToString(cv["state"])) ? "auth" : "notadb");
        }
        catch { verdict = "notadb"; }
        PortGate.Release();
        // 扫描不许把设备悄悄连上：验完就断开（客户端超时被杀时，服务端可能还留着这条）
        try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }

        // 端口开着但 adb 连不上，不一定是厂商服务占端口；也可能是 5037 被手机助手的 adb 抢走。
        // 没有投屏在跑时先换回自带 adb 再试一次；有投屏就不动，只报"adb 服务被占用"。
        if (verdict == "notadb")
        {
            string foreign = ForeignAdbOwner();
            if (foreign.Length > 0)
            {
                if (LiveSessions().Count == 0)
                {
                    EnsureOwnAdbServer();
                    await Task.Delay(500);
                    await PortGate.WaitAsync();
                    try
                    {
                        var cv2 = await ConnectVerified(addr, 2500, 3000);
                        verdict = (bool)cv2["ok"] ? "adb"
                            : ("unauthorized".Equals(Convert.ToString(cv2["state"])) ? "auth" : "notadb");
                    }
                    catch { verdict = "notadb"; }
                    PortGate.Release();
                    try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }
                    if (verdict == "notadb" && ForeignAdbOwner().Length > 0) verdict = "foreignadb";
                }
                else verdict = "foreignadb";
            }
        }

        // 外部 adb 占用是临时状态，不写进 10 分钟缓存，否则换回自带 adb 后还会显示旧结论。
        if (verdict != "foreignadb") PortCachePut(addr, verdict);
        return verdict;
    }

    static string PortReason(string verdict, bool moved)
    {
        if (moved) return "moved";
        if (verdict == "adb") return "manual";
        if (verdict == "auth") return "auth";
        if (verdict == "foreignadb") return "foreign";
        return verdict == "notadb" ? "notadb" : "port";
    }

    string PortNote(string verdict, bool moved, string hip)
    {
        if (verdict == "auth") return "找到了这台，但要重新配对：在手机无线调试页用配对码再配一次。";
        if (verdict == "foreignadb")
        {
            string who = ForeignAdbOwner();
            if (who.Length == 0) who = "旧版 adb";
            return "端口能连，问题在本机：5037 上的 adb 服务是「" + who + "」起的旧版本，认不了新版无线调试。点「换回自带 adb」后重新扫描；正在投屏时先结束这一路。";
        }
        if (verdict == "notadb") return moved
            ? ("它的 IP 是 " + hip + "，但这个端口不是无线调试；点「找端口」找回真正的端口。")
            : "这个端口开着，但不是无线调试（厂商自带的服务也占这种端口）；点「找端口」找回真正的端口。";
        if (verdict == "adb") return moved
            ? ("已经换了 IP（" + hip + "），端口验证过是无线调试；点「连接」建立连接。")
            : "端口验证过是无线调试；点「连接」建立连接，扫描不会自动连。";
        // 端口不通不一定是端口变了：也可能手机上把无线调试关掉了，两种从网络上是分不出来的，
        // 所以文案只陈述"连不上"，把两种可能都摆出来，别断言成"端口变了"。
        return moved ? ("已经换了 IP（" + hip + "），但这个端口连不上：可能端口变了，也可能无线调试没开。点「找端口」重扫这一台。")
                     : "这个端口连不上：手机可能换了端口，也可能无线调试被关掉了。先看一眼手机上的「无线调试」是不是开着，再点「找端口」重扫这一台。";
    }

    // 网段扫描已经 ping 过一轮时的轻量复核：连发两包，约 1.2 秒，不再走"单包 + 三包"那套
    async Task<bool> PingQuick(string ip)
    {
        var pg = await Run("ping", new[] { "-n", "2", "-w", "600", ip }, 5000);
        return pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    async Task<bool> PortProbe(string ip, int port, int timeoutMs)
    {
        IPAddress a;
        if (port <= 0 || port > 65535 || !IPAddress.TryParse(ip, out a)) return false;
        return await ConnectProbe(a, port, timeoutMs);
    }

    // adb connect 的报错跟着系统语言变：英文 refused、中文「积极拒绝 (10061)」都要认。
    // 分类给前端用：port 类才值得去扫端口，auth 类得重新配对，其余保留原始报错。
    static string FailReason(string raw)
    {
        string t = raw ?? "";
        if (t.IndexOf("10061", StringComparison.Ordinal) >= 0
            || t.IndexOf("积极拒绝", StringComparison.Ordinal) >= 0
            || t.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("cannot connect", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("No connection could be made", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
            || t.Trim().Length == 0)
            return "port";
        if (t.IndexOf("authenticat", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0)
            return "auth";
        return "other";
    }

    static string FailNote(string reason, string raw)
    {
        if (reason == "port")
            return "端口可能变了：手机上的「无线调试」每次开关都会换一个端口，点「找端口」自动找回。";
        if (reason == "auth")
            return "需要重新配对：在手机无线调试页用配对码再配一次，配好后不用再输。";
        string t = (raw ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        if (t.Length > 80) t = t.Substring(0, 80) + "…";
        return "连不上：" + t;
    }

    // mDNS 后端偶尔会失聪导致扫不到设备：手动重启 adb 服务兜底（会断开当前连接）
    async Task<object> RestartAdb()
    {
        if (adbExe == null) return Err("没找到 adb");
        if (LiveSessions().Count > 0) return Err("正在投屏，先结束这一路再换 adb 服务，避免把当前连接打断");
        string foreign = ForeignAdbOwner();
        await Run(adbExe, new[] { "kill-server" }, 5000);
        await Task.Delay(800);
        ForgetForeignCache();
        var s = await Run(adbExe, new[] { "start-server" }, 8000);
        BoostServer(Port5037Pid());
        ForgetForeignCache();
        string now = ForeignAdbOwner(true);
        string text = s.ok
            ? (foreign.Length > 0
                ? "已换回自带 adb（原来被「" + foreign + "」占用）"
                : "已重启自带 adb")
            : s.text;
        if (s.ok && now.Length > 0)
            text = "adb 服务仍是「" + now + "」的旧版本，它一退出就会再来抢：把那个程序连同后台服务关掉，再点一次";
        return new Dictionary<string, object> { { "ok", s.ok && now.Length == 0 }, { "text", text } };
    }

    // ── 无线身份：同网段 MAC / 网段 / 连接验证 ──

    static string GetMac(string ip)
    {
        try
        {
            var bytes = IPAddress.Parse(ip).GetAddressBytes();
            if (bytes.Length != 4) return "";
            int dest = BitConverter.ToInt32(bytes, 0);
            byte[] mac = new byte[6];
            uint len = 6;
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return "";
            var parts = new string[len];
            bool allZero = true;
            for (int i = 0; i < len; i++) { parts[i] = mac[i].ToString("x2"); if (mac[i] != 0) allZero = false; }
            return allZero ? "" : string.Join("-", parts);
        }
        catch { return ""; }
    }

    class LanTarget { public string localIp = "", gateway = ""; public int prefix; }
    class LanHost { public string ip = "", mac = ""; public bool gateway; }

    static uint IpToInt(string ip)
    {
        var b = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
    static string IntToIp(uint v) { return (v >> 24) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255); }

    static bool SameSubnet(string a, int prefix, string b)
    {
        try
        {
            if (prefix < 0 || prefix > 32) return false;
            uint mask = prefix == 0 ? 0 : 0xffffffffu << (32 - prefix);
            return (IpToInt(a) & mask) == (IpToInt(b) & mask);
        }
        catch { return false; }
    }

    LanTarget GetLanTarget(string preferIp)
    {
        var list = new List<LanTarget>();
        var all = new List<LanTarget>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            string gw = "";
            foreach (var g in props.GatewayAddresses)
                if (g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                { gw = g.Address.ToString(); break; }
            // VPN / 虚拟网卡常排在前面，会把网段扫错，先剔掉；剔完一个不剩再退回原来的全量
            string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool virtualNic = desc.Contains("vethernet") || desc.Contains("vmware") || desc.Contains("virtualbox")
                || desc.Contains("hyper-v") || desc.Contains("wsl") || desc.Contains("tap-")
                || desc.Contains("tailscale") || desc.Contains("zerotier") || desc.Contains("wireguard")
                || desc.Contains("openvpn");
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = ua.Address.ToString();
                if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                var t = new LanTarget { localIp = ip, prefix = ua.PrefixLength, gateway = gw };
                all.Add(t);
                if (!virtualNic) list.Add(t);
            }
        }
        if (list.Count == 0) list = all;
        if (list.Count == 0) return null;
        if (!string.IsNullOrEmpty(preferIp))
            foreach (var t in list) if (SameSubnet(t.localIp, t.prefix, preferIp)) return t;
        var withGw = list.FirstOrDefault(t => t.gateway.Length > 0);
        return withGw != null ? withGw : list[0];
    }

    // 先用 ping 找在线主机（254 个地址约 1-2 秒），再只对这些主机查 MAC。
    // 直接对全段 SendARP 在离线地址上会一个个等超时，实测要 40 多秒。
    async Task<List<LanHost>> ArpSweep(LanTarget lan, RelocateState st)
    {
        var hosts = new List<LanHost>();
        if (lan == null) return hosts;
        int prefix = lan.prefix;
        if (prefix < 24) prefix = 24;
        if (prefix > 30) return hosts;
        uint mask = 0xffffffffu << (32 - prefix);
        uint net = IpToInt(lan.localIp) & mask;
        uint broadcast = net | ~mask;
        uint start = net + 1, end = broadcast - 1;
        var ips = new List<string>();
        for (uint v = start; v <= end; v++)
        {
            string hostIp = IntToIp(v);
            if (hostIp != lan.localIp) ips.Add(hostIp);
        }

        var live = new List<string>();
        var sem = new SemaphoreSlim(64);
        var tasks = new List<Task>();
        foreach (var hostIp in ips)
        {
            if (st != null && st.cancel) break;
            string ip = hostIp;
            await sem.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        var reply = await ping.SendPingAsync(ip, 350);
                        if (reply.Status == IPStatus.Success) lock (live) live.Add(ip);
                    }
                }
                catch { }
                finally { sem.Release(); }
            }));
        }
        await Task.WhenAll(tasks);

        var macTasks = new List<Task>();
        foreach (var ip0 in live.ToList())
        {
            if (st != null && st.cancel) break;
            string ip = ip0;
            macTasks.Add(Task.Run(() =>
            {
                // MAC 只用来认身份：查不到也留着这台主机，否则"IP 通"的设备反而列不出来
                string mac = GetMac(ip);
                lock (hosts) hosts.Add(new LanHost { ip = ip, mac = mac, gateway = ip == lan.gateway });
            }));
        }
        await Task.WhenAll(macTasks);
        return hosts.OrderBy(h => IpToInt(h.ip)).ToList();
    }

    async Task<string> DeviceState(string addr)
    {
        var r = await Run(adbExe, new[] { "devices" }, 6000);
        foreach (var line in (r.text ?? "").Split('\n'))
        {
            var p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && string.Equals(p[0], addr, StringComparison.OrdinalIgnoreCase)) return p[1];
        }
        return "";
    }

    // adb 的 "connected to" 不代表可用：OPPO 等设备的专有端口也会先连上再变 offline。
    // 只有 adb devices 显示 device，并且 shell 能回声，才算真正连上。
    async Task<Dictionary<string, object>> ConnectVerified(string addr, int waitMs, int connectMs = 8000)
    {
        var r = await Run(adbExe, new[] { "connect", addr }, connectMs);
        string text = (r.text ?? "").Trim();
        string low = text.ToLowerInvariant();
        // 不在这里凭提示语判授权：OPPO 这类专有服务也会回 failed to authenticate。
        // 以 adb devices 的最终状态为准，避免留下 offline 记录。
        if (low.Contains("cannot connect") || low.Contains("refused") || low.Contains("failed to connect")
            || low.Contains("failed to get feature set"))
        {
            // 失败的连接不要留在 adb devices 里，否则下次扫描会把它当 offline 设备
            try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }
            return new Dictionary<string, object> { { "ok", false }, { "state", "closed" }, { "text", text } };
        }

        var sw = Stopwatch.StartNew();
        string st = "";
        while (sw.ElapsedMilliseconds < waitMs)
        {
            st = await DeviceState(addr);
            if (st == "device")
            {
                var echo = await AdbT(3000, addr, "shell", "echo", "ok");
                if ((echo.text ?? "").IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0)
                    return new Dictionary<string, object> { { "ok", true }, { "state", "device" }, { "text", text } };
            }
            else if (st == "unauthorized")
                return new Dictionary<string, object> { { "ok", false }, { "state", "unauthorized" }, { "text", text } };
            await Task.Delay(350);
        }
        try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }
        return new Dictionary<string, object> { { "ok", false }, { "state", st.Length > 0 ? st : "offline" }, { "text", text } };
    }

    async Task CleanupOffline()
    {
        var r = await Run(adbExe, new[] { "devices" }, 6000);
        foreach (var line in (r.text ?? "").Split('\n'))
        {
            var p = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[1] == "offline" && IsWirelessId(p[0]))
            {
                await Run(adbExe, new[] { "disconnect", p[0] }, 3000);
                Log("清理 offline 无线记录 " + p[0]);
            }
        }
    }

    // 只读身份：不写地址簿，供「先确认是不是同一台」用
    async Task<DeviceIdentity> ReadDeviceIdentity(string addr)
    {
        var id = new DeviceIdentity();
        id.mac = GetMac(addr.Split(':')[0]);
        var s = await AdbT(3000, addr, "shell", "getprop", "ro.serialno");
        string serial = (s.text ?? "").Trim();
        if (serial.Length == 0 || serial.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var a = await AdbT(3000, addr, "shell", "settings", "get", "secure", "android_id");
            string aid = (a.text ?? "").Trim();
            serial = aid.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ? "" : aid;
        }
        var m = await AdbT(3000, addr, "shell", "getprop", "ro.product.model");
        string model = (m.text ?? "").Trim();
        if (model.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) model = "";
        id.serial = serial;
        id.model = model;
        return id;
    }

    // 连接成功后才学身份：MAC 用于同网段找新 IP，serial/名称用于跨地址认人
    async Task LearnIdentity(string addr, string mdnsId, string fallbackName, string oldAddr = null)
    {
        var id = await ReadDeviceIdentity(addr);
        string model = id.model.Length > 0 ? id.model : (fallbackName ?? "");
        if (!string.IsNullOrEmpty(model))
        {
            RememberName(addr, model);
            if (!string.IsNullOrEmpty(id.serial)) RememberName(id.serial, model);
            if (!string.IsNullOrEmpty(mdnsId)) RememberName(mdnsId, model);
        }
        RememberWifi(addr, id.mac, id.serial, mdnsId, model, oldAddr);
        Log("identity " + addr + " mac=" + id.mac + " serial=" + id.serial + " name=" + model);
    }

    // 自动重连：只走快路径（旧地址 → ARP 换 IP → 旧端口），不自动扫端口。
    // 端口也变了就停下，由用户点「找端口」，避免启动时莫名等 30 秒。
    async Task<object> ReconnectLast()
    {
        if (adbExe == null) return Err("没找到 adb");
        string last = LoadLastDevice();
        if (last.Length == 0)
            return new Dictionary<string, object> { { "ok", false }, { "text", "没有记录上次设备" }, { "addr", "" } };
        await CleanupOffline();
        var entries = LoadWifiEntries();
        var e = entries.FirstOrDefault(x => string.Equals(x.addr, last, StringComparison.OrdinalIgnoreCase));
        if (e == null)
            return new Dictionary<string, object> { { "ok", false }, { "text", "没有记录上次设备" }, { "addr", "" } };

        var online = await ConnectedWireless();
        var hit = online.FirstOrDefault(c => c == e.addr || c.Split(':')[0] == e.addr.Split(':')[0]);
        if (hit != null)
        {
            SaveLastDevice(hit); RememberWifi(hit);
            await TryRestorePending();
            return new Dictionary<string, object> { { "ok", true }, { "text", "已经在线" }, { "addr", hit } };
        }

        var cv = await ConnectVerified(e.addr, 6000);
        if ((bool)cv["ok"])
        {
            await LearnIdentity(e.addr, e.mdnsId, e.name, null);
            SaveLastDevice(e.addr);
            await TryRestorePending();
            return new Dictionary<string, object> { { "ok", true }, { "text", "已连上" }, { "addr", e.addr } };
        }

        // 换 IP：按 MAC 找新地址，只试旧端口
        if (!string.IsNullOrEmpty(e.mac))
        {
            var lan = GetLanTarget(e.addr.Split(':')[0]);
            var hosts = await ArpSweep(lan, null);
            string oldPort = e.addr.Contains(":") ? e.addr.Split(':')[1] : "";
            // 代理 ARP 会让多个 IP 共用 MAC；有歧义就不自动认领
            var macHosts = hosts.Where(h => string.Equals(h.mac, e.mac, StringComparison.OrdinalIgnoreCase)).ToList();
            if (macHosts.Count == 1)
            {
                var h = macHosts[0];
                if (h.ip != e.addr.Split(':')[0] && oldPort.Length > 0)
                {
                    string addr = h.ip + ":" + oldPort;
                    var cv2 = await ConnectVerified(addr, 6000);
                    if ((bool)cv2["ok"])
                    {
                        var id = await ReadDeviceIdentity(addr);
                        if (!IdentityMatches(e, id))
                        {
                            try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }
                            Log("重连 ARP 认领被身份校验拒绝 " + addr + " 原 serial=" + e.serial + " 新 serial=" + id.serial);
                            return new Dictionary<string, object> { { "ok", false }, { "text", "找到了同一网段的设备，但身份对不上；打开无线连接重新找一台" }, { "addr", e.addr } };
                        }
                        await LearnIdentity(addr, e.mdnsId, e.name, e.addr);
                        SaveLastDevice(addr);
                        await TryRestorePending();
                        Log("重连：ARP 换 IP 认领 " + e.addr + " → " + addr);
                        return new Dictionary<string, object> { { "ok", true }, { "text", "换了 IP，已重新连上" }, { "addr", addr } };
                    }
                }
            }
        }

        string st = Convert.ToString(cv["state"]);
        string msg = st == "unauthorized"
            ? "找到了上次设备，但需要授权：打开无线连接重新配对"
            : "IP 或端口可能变了：打开无线连接，点「找端口」";
        return new Dictionary<string, object> { { "ok", false }, { "text", msg }, { "addr", e.addr } };
    }

    string LoadLastDevice()
    {
        var j = ReadJson(LastDevFile) as Dictionary<string, object>;
        return j != null && j.ContainsKey("addr") ? Convert.ToString(j["addr"]) ?? "" : "";
    }

    void SaveLastDevice(string addr)
    {
        WriteJson(LastDevFile, new Dictionary<string, object> { { "addr", addr ?? "" } });
    }

    // 常用无线地址簿：地址只是最近一次的位置；MAC/serial 才是设备身份。
    // 只用于同网段找回，不做云同步。
    class WifiEntry
    {
        public string addr = "", mac = "", serial = "", mdnsId = "", name = "";
        public DateTime lastSeen;
    }
    class DeviceIdentity { public string serial = "", model = "", mac = ""; }
    static readonly object WifiLock = new object();

    static bool IdEq(string a, string b)
    {
        return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
    static bool HasIdentity(WifiEntry e) { return !string.IsNullOrEmpty(e.serial) || !string.IsNullOrEmpty(e.mdnsId); }
    static bool SameIdentity(WifiEntry a, WifiEntry b)
    {
        return IdEq(a.serial, b.serial) || IdEq(a.mdnsId, b.mdnsId) || IdEq(a.serial, b.mdnsId) || IdEq(a.mdnsId, b.serial);
    }
    // 条目是否就是这次连接到的设备：serial/mDNS 优先，MAC 只在双方都没有更强身份时才用
    static bool EntryIdMatches(WifiEntry e, string serial, string mdnsId, string mac)
    {
        if (IdEq(e.serial, serial) || IdEq(e.mdnsId, mdnsId) || IdEq(e.serial, mdnsId) || IdEq(e.mdnsId, serial)) return true;
        if (string.IsNullOrEmpty(e.serial) && string.IsNullOrEmpty(e.mdnsId) && IdEq(e.mac, mac)) return true;
        return false;
    }
    static bool IdentityMatches(WifiEntry e, DeviceIdentity id)
    {
        if (!string.IsNullOrEmpty(e.serial) && !string.IsNullOrEmpty(id.serial))
            return string.Equals(e.serial, id.serial, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(e.mac) && !string.IsNullOrEmpty(id.mac))
            return string.Equals(e.mac, id.mac, StringComparison.OrdinalIgnoreCase);
        return true;   // 没有足够信息区分，按原来的地址处理
    }

    List<WifiEntry> LoadWifiEntries()
    {
        var list = new List<WifiEntry>();
        var raw = ReadJson(WifiBookFile) as System.Collections.IEnumerable;
        if (raw != null)
            foreach (var o in raw)
            {
                var d = o as Dictionary<string, object>;
                var e = new WifiEntry();
                if (d != null)
                {
                    e.addr = d.ContainsKey("addr") ? (Convert.ToString(d["addr"]) ?? "") : "";
                    e.mac = d.ContainsKey("mac") ? (Convert.ToString(d["mac"]) ?? "") : "";
                    e.serial = d.ContainsKey("serial") ? (Convert.ToString(d["serial"]) ?? "") : "";
                    e.mdnsId = d.ContainsKey("mdnsId") ? (Convert.ToString(d["mdnsId"]) ?? "") : "";
                    e.name = d.ContainsKey("name") ? (Convert.ToString(d["name"]) ?? "") : "";
                    DateTime t;
                    if (d.ContainsKey("lastSeen") && DateTime.TryParse(Convert.ToString(d["lastSeen"]), out t)) e.lastSeen = t;
                }
                else e.addr = Convert.ToString(o) ?? "";
                if (!string.IsNullOrEmpty(e.addr) && e.addr.Contains(":")) list.Add(e);
            }
        // 早期版本只存一个地址，这里一并认账
        var legacy = ReadJson(LegacyWifiFile) as Dictionary<string, object>;
        if (legacy != null)
        {
            string ip = legacy.ContainsKey("ip") ? (Convert.ToString(legacy["ip"]) ?? "") : "";
            string port = legacy.ContainsKey("port") ? (Convert.ToString(legacy["port"]) ?? "") : "";
            if (ip.Length > 0)
            {
                string a = port.Length > 0 ? ip + ":" + port : ip + ":5555";
                if (!list.Any(e => e.addr == a)) list.Add(new WifiEntry { addr = a });
            }
        }
        var last = LoadLastDevice();
        if (last.Length > 0 && last.Contains(":") && !list.Any(e => e.addr == last))
            list.Insert(0, new WifiEntry { addr = last });
        // 同一 IP 下只有身份兼容才合并；身份冲突时整条保留较新的，绝不混合字段
        var byIp = new Dictionary<string, WifiEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<WifiEntry>();
        foreach (var e in list)
        {
            string ip = e.addr.Split(':')[0];
            WifiEntry cur;
            if (!byIp.TryGetValue(ip, out cur)) { byIp[ip] = e; order.Add(e); continue; }
            bool curHas = HasIdentity(cur), eHas = HasIdentity(e);
            if (!curHas && eHas)
            {
                // 空壳记录让位给有身份的记录：整组身份一起换，不能只补一半
                cur.serial = e.serial; cur.mdnsId = e.mdnsId; cur.mac = e.mac;
                if (string.IsNullOrEmpty(cur.name)) cur.name = e.name;
                if (e.lastSeen > cur.lastSeen) cur.lastSeen = e.lastSeen;
            }
            else if (curHas && !eHas)
            {
                if (string.IsNullOrEmpty(cur.mac)) cur.mac = e.mac;
                if (string.IsNullOrEmpty(cur.name)) cur.name = e.name;
                if (e.lastSeen > cur.lastSeen) cur.lastSeen = e.lastSeen;
            }
            else if (!curHas && !eHas)
            {
                if (string.IsNullOrEmpty(cur.name)) cur.name = e.name;
                if (e.lastSeen > cur.lastSeen) cur.lastSeen = e.lastSeen;
            }
            else if (SameIdentity(cur, e))
            {
                if (e.lastSeen >= cur.lastSeen)
                {
                    if (!string.IsNullOrEmpty(e.mac)) cur.mac = e.mac;
                    if (!string.IsNullOrEmpty(e.name)) cur.name = e.name;
                    cur.lastSeen = e.lastSeen;
                }
                else if (string.IsNullOrEmpty(cur.mac)) cur.mac = e.mac;
            }
            else if (e.lastSeen > cur.lastSeen)
            {
                // 同一 IP 换了一台设备：保留较新记录，旧身份整条丢弃，不复制任何字段
                int idx = order.IndexOf(cur);
                if (idx >= 0) order[idx] = e;
                byIp[ip] = e;
                Log("地址簿同一 IP 身份冲突，保留较新记录 " + ip + "（旧 " + cur.serial + " / 新 " + e.serial + "）");
            }
        }

        // 跨 IP 按 serial/mdnsId 去重：同一台设备只留较新地址
        var dedup = new List<WifiEntry>();
        var idIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in order)
        {
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(e.serial)) keys.Add("s:" + e.serial);
            if (!string.IsNullOrEmpty(e.mdnsId)) keys.Add("m:" + e.mdnsId);
            int idx = -1;
            foreach (var k in keys)
            {
                int at;
                if (idIndex.TryGetValue(k, out at)) { idx = at; break; }
            }
            if (idx >= 0)
            {
                var cur = dedup[idx];
                if (e.lastSeen > cur.lastSeen || (e.addr == last && cur.addr != last))
                    dedup[idx] = e;
                foreach (var k in keys) idIndex[k] = idx;
                continue;
            }
            int pos = dedup.Count;
            dedup.Add(e);
            foreach (var k in keys) idIndex[k] = pos;
        }
        return dedup.Take(8).ToList();
    }

    void SaveWifiEntries(List<WifiEntry> list)
    {
        var arr = new List<object>();
        foreach (var e in list.Take(8))
        {
            if (string.IsNullOrEmpty(e.addr) || !e.addr.Contains(":")) continue;
            var d = new Dictionary<string, object> { { "addr", e.addr } };
            if (!string.IsNullOrEmpty(e.mac)) d["mac"] = e.mac;
            if (!string.IsNullOrEmpty(e.serial)) d["serial"] = e.serial;
            if (!string.IsNullOrEmpty(e.mdnsId)) d["mdnsId"] = e.mdnsId;
            if (!string.IsNullOrEmpty(e.name)) d["name"] = e.name;
            if (e.lastSeen != default(DateTime)) d["lastSeen"] = e.lastSeen.ToString("s");
            arr.Add(d);
        }
        WriteJson(WifiBookFile, arr);
    }

    // 常用无线地址簿：有些设备不发送发现信号，只能靠这里记下的地址找回
    List<string> WifiBook()
    {
        return LoadWifiEntries()
            .Select(e => e.addr)
            .Where(a => !string.IsNullOrEmpty(a) && a.Contains(":"))
            .Distinct()
            .Take(8)
            .ToList();
    }

    // 兼容旧调用：只记地址；学到身份时用下面的完整版本
    void RememberWifi(string addr)
    {
        RememberWifi(addr, "", "", "", "", null);
    }

    void RememberWifi(string addr, string mac, string serial, string mdnsId, string name, string oldAddr = null)
    {
        if (string.IsNullOrEmpty(addr) || !addr.Contains(":")) return;
        lock (WifiLock)
        {
            var list = LoadWifiEntries();
            WifiEntry hit = null;
            if (!string.IsNullOrEmpty(serial))
                hit = list.FirstOrDefault(e => IdEq(e.serial, serial) || IdEq(e.mdnsId, serial));
            if (hit == null && !string.IsNullOrEmpty(mdnsId))
                hit = list.FirstOrDefault(e => IdEq(e.mdnsId, mdnsId) || IdEq(e.serial, mdnsId));
            if (hit == null)
            {
                var atAddr = list.FirstOrDefault(e => e.addr == addr);
                if (atAddr != null)
                {
                    if (!HasIdentity(atAddr) || EntryIdMatches(atAddr, serial, mdnsId, mac))
                        hit = atAddr;
                    else
                    {
                        // 这个 IP 已经属于另一台设备：清空旧身份再承载新设备，避免混合
                        Log("地址簿替换同 IP 的另一台设备 " + addr + "（旧 serial=" + atAddr.serial + "）");
                        atAddr.serial = ""; atAddr.mdnsId = ""; atAddr.mac = ""; atAddr.name = "";
                        hit = atAddr;
                    }
                }
            }
            if (hit == null) { hit = new WifiEntry(); list.Insert(0, hit); }

            hit.addr = addr;
            if (!string.IsNullOrEmpty(mac)) hit.mac = mac;
            if (!string.IsNullOrEmpty(serial)) hit.serial = serial;
            if (!string.IsNullOrEmpty(mdnsId)) hit.mdnsId = mdnsId;
            if (!string.IsNullOrEmpty(name)) hit.name = name;
            hit.lastSeen = DateTime.Now;

            // 新地址如果已被另一台设备占用，替换那条记录，但不继承它的身份
            var otherAtAddr = list.FirstOrDefault(e => e != hit && e.addr == addr);
            if (otherAtAddr != null && HasIdentity(otherAtAddr) && !EntryIdMatches(otherAtAddr, serial, mdnsId, mac))
            {
                Log("地址簿清理新地址上的旧设备 " + addr + "（旧 serial=" + otherAtAddr.serial + "）");
                list.Remove(otherAtAddr);
            }
            // 同一身份、同地址空壳、换 IP 前的同一身份记录都只留这一条。
            // 旧地址若已属于别的设备，绝不动它。
            list.RemoveAll(e => e != hit && (
                EntryIdMatches(e, serial, mdnsId, mac) ||
                (e.addr == addr && !HasIdentity(e)) ||
                (!string.IsNullOrEmpty(oldAddr) && e.addr == oldAddr &&
                 (!HasIdentity(e) || EntryIdMatches(e, serial, mdnsId, mac)))));
            list.Remove(hit);
            list.Insert(0, hit);
            SaveWifiEntries(list);
        }
    }

    void RemoveWifiEntry(string addr)
    {
        lock (WifiLock)
        {
            var list = LoadWifiEntries();
            if (list.RemoveAll(e => e.addr == addr) > 0) SaveWifiEntries(list);
        }
    }

    // ── 连接诊断 ──

    static string ProcName(int pid)
    {
        if (pid <= 0) return "";
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "pid " + pid; }
    }

    // 谁占着 mDNS 端口：adb 收不到设备广播最常见的原因，而且它自己不说
    static async Task<List<object>> MdnsOwners()
    {
        var owners = new List<object>();
        var ns = await Run("netstat", new[] { "-ano", "-p", "udp" }, 9000);
        foreach (var line in (ns.text ?? "").Split('\n'))
        {
            var tk = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tk.Length < 3 || tk[0].ToUpperInvariant() != "UDP") continue;
            if (!tk[1].EndsWith(":5353")) continue;
            int pid;
            int.TryParse(tk[tk.Length - 1], out pid);
            string nm = ProcName(pid);
            if (nm.Length == 0) continue;
            owners.Add(new Dictionary<string, object> { { "addr", tk[1] }, { "name", nm }, { "pid", pid } });
        }
        return owners;
    }

    async Task<object> ConnectionDiag(Dictionary<string, object> p)
    {
        var ips = new List<object>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string a = ua.Address.ToString();
                if (a.StartsWith("169.254.")) continue;     // 没拿到 DHCP 地址，不参与局域网发现
                ips.Add(new Dictionary<string, object> { { "ip", a }, { "nic", ni.Name } });
            }
        }

        // 先跑一次 mdns 查询：adb 的 mDNS socket 往往到这时才绑定，
// 先查 5353 持有者会把"adb 还没绑"误判成"端口被别家独占"。
        string mdnsRaw = "";
        int mdnsCount = 0;
        if (adbExe != null)
        {
            var md = await Run(adbExe, new[] { "mdns", "services" }, 7000);
            foreach (var line in (md.text ?? "").Split('\n'))
            {
                var l = line.Trim();
                if (l.Length == 0 || l.StartsWith("List of")) continue;
                mdnsCount++;
                mdnsRaw += l + "\n";
            }
        }

        var owners = await MdnsOwners();
        bool adbHolds = owners.Any(o => ((Dictionary<string, object>)o)["name"].ToString().IndexOf("adb", StringComparison.OrdinalIgnoreCase) >= 0);
        // 5353 是共享端口：Windows 的 DNS 服务、浏览器、Electron 应用都会绑。
        // 只要 adb 也在持有者里就算正常；adb 不在也不代表被独占，标成 shared 提醒即可。
        string mdnsVerdict = owners.Count == 0 ? "free" : (adbHolds ? "ok" : "shared");
        // adb 占着 5353 却一条广播都收不到：多半是这个网络（公司/访客 WiFi）不转发 mDNS 组播，
        // 光报 "ok" 会把人误导成"广播正常、是手机的问题"。这里单独给一个 silent 结论。
        if (mdnsVerdict == "ok" && mdnsCount == 0) mdnsVerdict = "silent";

        var devs = new List<object>();
        if (adbExe != null)
        {
            var dv = await Run(adbExe, new[] { "devices" }, 8000);
            foreach (var line in (dv.text ?? "").Split('\n'))
            {
                var tk = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 2 || tk[0] == "List" || tk[0].Contains("of")) continue;
                if (tk[1] != "device" && tk[1] != "offline" && tk[1] != "unauthorized") continue;
                devs.Add(new Dictionary<string, object> { { "id", tk[0] }, { "via", IsWirelessId(tk[0]) ? "wireless" : "usb" }, { "state", tk[1] } });
            }
        }

        // 广播一条都没有时先别看网络：数据线连着的设备如果根本没在监听无线端口，
        // 那是手机上的「无线调试」没开——广播自然也不会有，说成"网络不转发"会把人带偏。
        if (mdnsVerdict == "silent")
        {
            foreach (var o in devs)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Convert.ToString(d["via"]) != "usb") continue;
                var tcp = await AdbT(3000, Convert.ToString(d["id"]), "shell", "cat /proc/net/tcp /proc/net/tcp6");
                bool listening = false;
                foreach (var row in (tcp.text ?? "").Split('\n'))
                {
                    var tk = row.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length < 4 || tk[3] != "0A") continue;           // 0A = LISTEN
                    var seg = tk[1].Split(':');
                    if (seg.Length != 2) continue;
                    int port;
                    if (!int.TryParse(seg[1], System.Globalization.NumberStyles.HexNumber, null, out port)) continue;
                    if (port >= 32768 || port == 5555) { listening = true; break; }
                }
                if (!listening) { mdnsVerdict = "wifiOff"; break; }
            }
        }

        Log("诊断 5353=" + mdnsVerdict + " 占用=" + string.Join(",", owners.Select(o => ((Dictionary<string, object>)o)["name"].ToString())) +
            " mdns条目=" + mdnsCount + " 本机IP=" + ips.Count);

        return new Dictionary<string, object> {
            { "ok", true },
            { "localIps", ips },
            { "mdnsOwners", owners },
            { "mdnsVerdict", mdnsVerdict },
            { "mdnsCount", mdnsCount },
            { "mdnsSample", mdnsRaw.Trim() },
            { "devices", devs },
            { "book", WifiBook() },
            { "hasAdb", adbExe != null }
        };
    }

    // 插线一次转无线：USB 还连着的时候，用 USB 读出手机的局域网 IP 和监听端口，
    // 逐个试 adb connect，命中就记进常用地址簿。完全不依赖 mDNS。
    async Task<object> UsbToWireless(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("先在「投屏」页选一台设备");
        if (IsWirelessId(id)) return Err("这台已经是无线连接了，换一台用数据线连着的");
        if (adbExe == null) return Err("没找到 adb");

        string ip = "";
        var w = await Adb(id, "shell", "ip", "-4", "addr", "show", "wlan0");
        var m = System.Text.RegularExpressions.Regex.Match(w.text ?? "", @"inet ([0-9.]+)/");
        if (m.Success) ip = m.Groups[1].Value;
        if (ip.Length == 0)
        {
            var all = await Adb(id, "shell", "ip", "-4", "addr");
            foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(all.text ?? "", @"inet ([0-9.]+)/"))
            {
                string c = mm.Groups[1].Value;
                if (!c.StartsWith("127.") && !c.StartsWith("169.254.")) { ip = c; break; }
            }
        }
        if (ip.Length == 0) return Err("没读到手机的局域网 IP：先确认手机连着 WiFi，且和电脑在同一个网络");

        var ss = await Adb(id, "shell", "ss", "-ltn");
        if (ss.text.IndexOf("LISTEN", StringComparison.OrdinalIgnoreCase) < 0) ss = await Adb(id, "shell", "netstat", "-ltn");
        var ports = new List<int>();
        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(ss.text ?? @"", @"LISTEN\S*\s+\d+\s+\d+\s+(?:\*|\[::\]|0\.0\.0\.0):(\d+)"))
        {
            int v;
            if (int.TryParse(mm.Groups[1].Value, out v) && v >= 1024 && v != 5353 && !ports.Contains(v)) ports.Add(v);
        }
        // adbd 的无线端口是系统随机分配的高位端口，从大到小试命中最快
        ports = ports.OrderByDescending(x => x).Take(12).ToList();
        if (ports.Count == 0) return Err("手机上没找到可试的监听端口：开发者选项里的「无线调试」可能没开");

        var tried = new List<string>();
        foreach (int port in ports)
        {
            string addr = ip + ":" + port;
            tried.Add(addr);
            var cv = await ConnectVerified(addr, 5000);
            if ((bool)cv["ok"])
            {
                await LearnIdentity(addr, id, "");
                SaveLastDevice(addr);
                Log("转无线成功 " + id + " → " + addr + "（试了 " + tried.Count + " 个端口）");
                return new Dictionary<string, object> {
                    { "ok", true }, { "addr", addr }, { "tried", tried.Count }, { "ports", tried } };
            }
        }
        Log("转无线失败 " + id + " " + ip + " 试了 " + tried.Count + " 个端口");
        return new Dictionary<string, object> {
            { "ok", false },
            { "error", "手机 " + ip + " 上试了 " + tried.Count + " 个端口都没连上。最常见的原因是「无线调试」没开：开发者选项 → 无线调试 → 打开。" },
            { "tried", tried.Count }, { "ports", tried } };
    }

    // ── 无线查找任务：单台找回 / 全网段深扫，共用进度和取消 ──
    // Android 无线调试端口是随机高位端口；广播不可用时只能靠 TCP 扫，但必须验证 adb 状态，
    // 否则 OPPO 这类专有服务也会被当成"连上了"。
    const int RelocBandStart = 32768;
    const int RelocBandEnd = 65535;
    const int RelocConcurrency = 1000;
    const int RelocProbeTimeoutMs = 1000;

    class RelocateState
    {
        public volatile string kind = "recover";   // recover | deep
        public volatile string ip = "";
        public volatile string targetName = "";
        public volatile bool running;
        public volatile bool cancel;
        public int scanned;
        public int total;
        public int hostsTotal;
        public volatile string result = "";   // "" | found | none
        public volatile string addr = "";
        public volatile string note = "";
        public List<object> found = new List<object>();
    }
    RelocateState reloc;

    // 单次非阻塞试连：BeginConnect + 超时定时器，不占用线程池线程，便于上千并发
    static Task<bool> ConnectProbe(IPAddress ip, int port, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        Socket sock = null;
        System.Threading.Timer timer = null;
        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            timer = new System.Threading.Timer(_ => tcs.TrySetResult(false), null, timeoutMs, Timeout.Infinite);
            sock.BeginConnect(new IPEndPoint(ip, port), ar =>
            {
                bool ok = false;
                try { sock.EndConnect(ar); ok = sock.Connected; } catch { ok = false; }
                tcs.TrySetResult(ok);
            }, null);
        }
        catch { tcs.TrySetResult(false); }
        var s = sock; var tm = timer;
        return tcs.Task.ContinueWith(t =>
        {
            if (tm != null) tm.Dispose();
            if (s != null) { try { s.Close(); } catch { } }
            bool r = false;
            try { r = t.Result; } catch { r = false; }
            return r;
        });
    }

    // 固定 worker 数从游标取任务：可取消、不会一次排几万个 Task。
    // 返回找到的开放端口地址（ip:port），跨主机不会混淆。
    static async Task<List<string>> SweepHosts(List<string> ips, int start, int end, int concurrency, int timeoutMs, RelocateState st)
    {
        var open = new List<string>();
        var addrs = new List<IPAddress>();
        var names = new List<string>();
        foreach (var ip in ips ?? new List<string>())
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) continue;
            addrs.Add(a); names.Add(ip);
        }
        if (addrs.Count == 0) return open;
        int portCount = end - start + 1;
        int total = addrs.Count * portCount;
        st.total = total;
        st.scanned = 0;
        st.hostsTotal = addrs.Count;
        int cursor = -1;
        var workers = new List<Task>();
        for (int w = 0; w < concurrency; w++)
        {
            workers.Add(Task.Run(async () =>
            {
                while (true)
                {
                    if (st.cancel) return;
                    int idx = Interlocked.Increment(ref cursor);
                    if (idx >= total) return;
                    int hostIdx = idx / portCount;
                    int port = start + (idx % portCount);
                    bool ok = await ConnectProbe(addrs[hostIdx], port, timeoutMs);
                    if (ok) lock (open) open.Add(names[hostIdx] + ":" + port);
                    Interlocked.Increment(ref st.scanned);
                }
            }));
        }
        await Task.WhenAll(workers);
        return open;
    }

    bool RelocBusy(out RelocateState cur)
    {
        cur = reloc;
        return cur != null && cur.running;
    }

    Task<object> RelocateStart(Dictionary<string, object> p)
    {
        if (adbExe == null) return Task.FromResult<object>(Err("没找到 adb"));
        string addr = Ps(p, "addr").Trim();
        string ip = Ps(p, "ip").Trim();
        if (ip.Length == 0 && addr.Contains(":")) ip = addr.Split(':')[0];
        if (ip.Length == 0) return Task.FromResult<object>(Err("缺少要找回的手机 IP"));
        RelocateState cur;
        if (RelocBusy(out cur))
        {
            if (cur.ip == ip)
                return Task.FromResult<object>(new Dictionary<string, object> { { "ok", true }, { "started", true }, { "text", "正在找端口中" } });
            return Task.FromResult<object>(new Dictionary<string, object> {
                { "ok", false }, { "busy", true }, { "ip", cur.ip },
                { "error", "正在为 " + (cur.targetName.Length > 0 ? cur.targetName : cur.ip) + " 查找，等这次结束再试另一台。" } });
        }

        var entries = LoadWifiEntries();
        var entry = entries.FirstOrDefault(e => e.addr == addr) ?? entries.FirstOrDefault(e => e.addr.Split(':')[0] == ip);
        if (entry == null) entry = new WifiEntry { addr = addr.Contains(":") ? addr : ip + ":0" };
        string name = entry.name.Length > 0 ? entry.name : ip;
        var st = new RelocateState { kind = "recover", ip = ip, targetName = name, running = true, note = "正在定位设备…" };
        reloc = st;
        var _ = RecoverLoop(st, entry);
        Log("recover 开始 " + (addr.Length > 0 ? addr : ip));
        return Task.FromResult<object>(new Dictionary<string, object> { { "ok", true }, { "started", true } });
    }

    Task<object> DeepScanStart(Dictionary<string, object> p)
    {
        if (adbExe == null) return Task.FromResult<object>(Err("没找到 adb"));
        RelocateState cur;
        if (RelocBusy(out cur))
            return Task.FromResult<object>(new Dictionary<string, object> {
                { "ok", false }, { "busy", true }, { "ip", cur.ip },
                { "error", "正在为 " + (cur.targetName.Length > 0 ? cur.targetName : cur.ip) + " 查找，等这次结束再试。" } });
        var st = new RelocateState { kind = "deep", running = true, note = "正在列出在线设备…" };
        reloc = st;
        var _ = DeepScanLoop(st);
        Log("deep 开始扫描本网段");
        return Task.FromResult<object>(new Dictionary<string, object> { { "ok", true }, { "started", true } });
    }

    object CancelWifiJob()
    {
        var st = reloc;
        if (st == null || !st.running) return new Dictionary<string, object> { { "ok", false }, { "text", "没有正在进行的查找" } };
        st.cancel = true;
        st.note = "正在停止…";
        return new Dictionary<string, object> { { "ok", true }, { "text", "已请求停止" } };
    }

    async Task<bool> PingIp(string ip)
    {
        return await PingAlive(ip);
    }

    // 广播会间歇断档：点「找端口」时先廉价重查，命中就不用扫端口段
    async Task<string> FindMdnsAddr(string ip, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            var r = await Run(adbExe, new[] { "mdns", "services" }, 3000);
            foreach (var line in (r.text ?? "").Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("List of")) continue;
                if (l.IndexOf("_adb-tls-connect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var x in l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!x.StartsWith(ip + ":", StringComparison.OrdinalIgnoreCase)) continue;
                    string port = x.Substring(ip.Length + 1);
                    if (port.Length > 0 && port.All(char.IsDigit)) return x;
                }
            }
            if (i + 1 < rounds) await Task.Delay(500);
        }
        return null;
    }

    async Task FinishRecover(RelocateState st, WifiEntry entry, string addr)
    {
        var id = await ReadDeviceIdentity(addr);
        if (!IdentityMatches(entry, id))
        {
            st.result = "none";
            st.note = "这个地址现在连到的是另一台设备（" + (id.model.Length > 0 ? id.model : addr) + "），没有覆盖地址簿。";
            try { await Run(adbExe, new[] { "disconnect", addr }, 3000); } catch { }
            Log("recover 身份不符，拒绝写入 " + addr + " 原 serial=" + entry.serial + " 新 serial=" + id.serial);
            return;
        }
        await LearnIdentity(addr, entry.mdnsId, entry.name, entry.addr);
        if (LoadLastDevice() == entry.addr) SaveLastDevice(addr);
        st.addr = addr;
        st.result = "found";
        st.note = "已找回并连上：" + addr;
        Log("recover 命中 " + addr + "（原 " + entry.addr + "）");
    }

    async Task RecoverLoop(RelocateState st, WifiEntry entry)
    {
        try
        {
            string oldIp = entry.addr.Split(':')[0];
            var lan = GetLanTarget(oldIp);

            // 只按设备身份定位：有 MAC 就按 MAC，没有才用旧 IP（且 MAC 要对得上）
            var candidates = new List<string>();
            if (lan != null && !string.IsNullOrEmpty(entry.mac))
            {
                st.note = "正在查找这台设备的新 IP…";
                var hosts = await ArpSweep(lan, st);
                foreach (var h in hosts.Where(h => string.Equals(h.mac, entry.mac, StringComparison.OrdinalIgnoreCase)))
                    if (!candidates.Contains(h.ip)) candidates.Add(h.ip);
            }
            if (candidates.Count == 0)
            {
                if (await PingIp(oldIp))
                {
                    string mac = GetMac(oldIp);
                    if (entry.mac.Length == 0 || string.Equals(mac, entry.mac, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(oldIp);
                }
            }
            if (candidates.Count == 0)
            {
                st.result = "none";
                st.note = "这台设备已经不在原来的 IP 上，局域网里也没找到它的 MAC；请在手机上确认「无线调试」开着，仍找不到就在无线页对这台的 IP 点「找端口」。";
                return;
            }

            // 广播里有旧 IP 的新端口：直接试
            string mdnsAddr = await FindMdnsAddr(oldIp, 1);
            if (!string.IsNullOrEmpty(mdnsAddr))
            {
                st.note = "广播里发现 " + mdnsAddr + "，正在试连…";
                var cv0 = await ConnectVerified(mdnsAddr, 5000);
                if ((bool)cv0["ok"]) { await FinishRecover(st, entry, mdnsAddr); return; }
            }

            // 先试旧端口和 5555，不扫端口段
            string oldPort = entry.addr.Contains(":") ? entry.addr.Split(':')[1] : "";
            var quick = new List<int>();
            int op;
            if (int.TryParse(oldPort, out op) && op > 0 && op <= 65535) quick.Add(op);
            if (!quick.Contains(5555)) quick.Add(5555);
            foreach (var ip in candidates)
            {
                foreach (int port in quick)
                {
                    if (st.cancel) { st.result = "none"; st.note = "已停止查找。"; return; }
                    string addr = ip + ":" + port;
                    st.note = "试连 " + addr + "…";
                    var cv = await ConnectVerified(addr, 5000);
                    if ((bool)cv["ok"]) { await FinishRecover(st, entry, addr); return; }
                    if ("unauthorized".Equals(Convert.ToString(cv["state"])))
                    {
                        st.result = "none";
                        st.note = "找到了设备，但需要授权：在手机无线调试页重新配对。";
                        return;
                    }
                }
            }

            st.note = "旧端口不通，开始扫描这台设备的端口…";
            var open = await SweepHosts(candidates, RelocBandStart, RelocBandEnd, RelocConcurrency, RelocProbeTimeoutMs, st);
            if (st.cancel) { st.result = "none"; st.note = "已停止查找。"; return; }
            if (open.Count == 0)
            {
                st.result = "none";
                st.note = "没找到这台设备的无线调试端口：确认「无线调试」开着、手机和电脑在同一网络。";
                return;
            }
            foreach (var addr in open.OrderByDescending(a => int.Parse(a.Split(':')[1])))
            {
                if (st.cancel) { st.result = "none"; st.note = "已停止查找。"; return; }
                st.note = "发现开放端口，正在验证 " + addr + "…";
                var cv = await ConnectVerified(addr, 6000);
                if ((bool)cv["ok"]) { await FinishRecover(st, entry, addr); return; }
                if ("unauthorized".Equals(Convert.ToString(cv["state"])))
                {
                    st.result = "none";
                    st.note = "找到了设备，但需要授权：在手机无线调试页重新配对。";
                    return;
                }
            }
            st.result = "none";
            st.note = "找到开放端口，但都不是无线调试：端口可能没开，或这台 IP 不是安卓设备。";
        }
        catch (Exception e) { st.result = "none"; st.note = "查找出错：" + e.Message; }
        finally { st.running = false; }
    }

    async Task DeepScanLoop(RelocateState st)
    {
        try
        {
            var lan = GetLanTarget(null);
            if (lan == null)
            {
                st.result = "none";
                st.note = "没找到本机局域网地址：确认电脑连着 WiFi 或网线。";
                return;
            }
            st.note = "正在扫描本网段的在线设备…";
            var hosts = await ArpSweep(lan, st);
            var ips = hosts.Where(h => !h.gateway).Select(h => h.ip).ToList();
            if (ips.Count == 0)
            {
                st.result = "none";
                st.note = "本网段没找到在线设备：确认手机和电脑在同一个网络。";
                return;
            }
            st.note = "正在扫描 " + ips.Count + " 台在线设备的端口…";
            var open = await SweepHosts(ips, RelocBandStart, RelocBandEnd, RelocConcurrency, RelocProbeTimeoutMs, st);
            if (st.cancel) { st.result = "none"; st.note = "已停止查找。"; return; }
            if (open.Count == 0)
            {
                st.result = "none";
                st.note = "没找到无线调试端口：确认手机的「无线调试」开着，并且和电脑在同一网络。";
                return;
            }

            var entries = LoadWifiEntries();
            var savedIps = new HashSet<string>(entries.Select(e => e.addr.Split(':')[0]), StringComparer.OrdinalIgnoreCase);
            var found = new List<object>();
            var ordered = open
                .OrderByDescending(a => savedIps.Contains(a.Split(':')[0]))
                .ThenByDescending(a => { int p; int.TryParse(a.Split(':')[1], out p); return p; })
                .Take(16).ToList();
            foreach (var addr in ordered)
            {
                if (st.cancel) break;
                st.note = "正在验证 " + addr + "…";
                var cv = await ConnectVerified(addr, 6000);
                if ((bool)cv["ok"])
                {
                    string ip = addr.Split(':')[0];
                    var entry = entries.FirstOrDefault(e => e.addr.Split(':')[0] == ip);
                    var id = await ReadDeviceIdentity(addr);
                    bool same = entry == null || IdentityMatches(entry, id);
                    if (same)
                    {
                        await LearnIdentity(addr, entry != null ? entry.mdnsId : "", entry != null ? entry.name : "");
                        if (entry != null && LoadLastDevice() == entry.addr) SaveLastDevice(addr);
                    }
                    else
                    {
                        Log("deep 命中的设备与地址簿身份不符，不覆盖 " + addr + " 原 serial=" + entry.serial + " 新 serial=" + id.serial);
                    }
                    found.Add(new Dictionary<string, object> {
                        { "addr", addr }, { "name", same ? (entry != null ? entry.name : "") : id.model } });
                    st.addr = addr;
                    st.result = "found";
                    st.note = same ? ("已找到并连上 " + addr) : ("已找到并连上 " + addr + "；地址簿原来的记录不是这台，未覆盖");
                    break;
                }
                if ("unauthorized".Equals(Convert.ToString(cv["state"])))
                {
                    found.Add(new Dictionary<string, object> { { "addr", addr }, { "unauthorized", true } });
                    st.addr = addr;
                    st.result = "found";
                    st.note = "找到无线调试 " + addr + "，但需要授权：在手机上点允许或重新配对。";
                    break;
                }
            }
            st.found = found;
            if (found.Count == 0)
            {
                st.result = "none";
                st.note = "找到开放端口，但都不是无线调试：确认手机的「无线调试」开着。";
            }
        }
        catch (Exception e) { st.result = "none"; st.note = "深度查找出错：" + e.Message; }
        finally { st.running = false; }
    }

    object RelocatePoll(Dictionary<string, object> p)
    {
        var st = reloc;
        if (st == null) return new Dictionary<string, object> { { "ok", false }, { "phase", "idle" } };
        // 先读 result：扫描线程是「先写结果、最后置 running=false」，反过来会多显示一帧 running
        string res = st.result;
        string phase = res.Length > 0 ? res : (st.running ? "running" : "idle");
        return new Dictionary<string, object> {
            { "ok", true }, { "phase", phase }, { "kind", st.kind },
            { "ip", st.ip }, { "targetName", st.targetName },
            { "scanned", st.scanned }, { "total", st.total }, { "hostsTotal", st.hostsTotal },
            { "result", res }, { "addr", st.addr }, { "note", st.note }, { "found", st.found } };
    }

    object WifiForget(Dictionary<string, object> p)
    {
        string addr = Ps(p, "addr").Trim();
        lock (WifiLock)
        {
            var list = LoadWifiEntries();
            bool hit = list.RemoveAll(e => e.addr == addr) > 0;
            if (hit) SaveWifiEntries(list);
            if (hit && LoadLastDevice() == addr) SaveLastDevice("");
            return new Dictionary<string, object> { { "ok", hit }, { "text", hit ? "已忘记 " + addr : "常用地址里没有这条" } };
        }
    }

    async Task<object> WifiDisconnect(Dictionary<string, object> p)
    {
        string addr = Ps(p, "addr").Trim();
        // mDNS 自动连上的设备编号是一长串服务名，adb disconnect 不认；先查出它当前地址再断
        if (addr.IndexOf("_adb-tls-", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string svc = addr.Split('.')[0];
            try
            {
                var md = await Run(adbExe, new[] { "mdns", "services" }, 4000);
                foreach (var line in (md.text ?? "").Split('\n'))
                {
                    if (line.IndexOf(svc, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var hp = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(x => x.Count(ch => ch == ':') == 1 && x.Split(':')[1].Length > 0 && x.Split(':')[1].All(char.IsDigit));
                    if (!string.IsNullOrEmpty(hp)) { addr = hp; Log("断开 mDNS 设备 " + svc + " → " + addr); break; }
                }
            }
            catch { }
        }
        var a = new List<string> { "disconnect" };
        if (addr.Length > 0) a.Add(addr);
        var r = await Run(adbExe, a, 10000);
        string t = r.text.Trim();
        bool ok = t.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0 || (r.ok && t.Length == 0);
        if (ok) SaveLastDevice(""); // 用户主动断开 = 不想自动连，清空记录
        return new Dictionary<string, object> { { "ok", ok }, { "text", t.Length > 0 ? t : (addr.Length > 0 ? "已断开 " + addr : "已断开全部无线连接") } };
    }

    class WifiDev { public string ip, name, connAddr, pairAddr; public string serial = ""; }

    async Task<object> WifiConnect(Dictionary<string, object> p)
    {
        if (adbExe == null) return Err("没找到 adb");
        string ip = Ps(p, "ip").Trim(), port = Ps(p, "port").Trim(), code = Ps(p, "code").Trim();
        // 有人习惯把整串地址粘进 IP 框，这里兜住
        if (ip.Contains(":"))
        {
            var seg = ip.Split(':');
            ip = seg[0];
            if (port.Length == 0 && seg.Length > 1) port = seg[1];
        }
        if (ip.Length == 0) return Err("先填手机无线调试页上的 IP 地址");
        if (port.Length == 0) return Err("还要填端口，就是手机页面上 IP 后面那个数字");
        string addr = ip + ":" + port;
        var steps = new List<string>();

        // 别的程序可能又起了自己的 adb 服务把 5037 抢走（手机助手常干这事），
        // 那种服务连不上无线调试。有投屏在跑就先不动，免得把正在用的连接断掉。
        int own5037 = Port5037Pid();
        string owner5037 = "";
        if (own5037 > 0) { try { owner5037 = Process.GetProcessById(own5037).MainModule.FileName; } catch { } }
        if (owner5037.Length > 0 && !string.Equals(owner5037, adbExe, StringComparison.OrdinalIgnoreCase)
            && LiveSessions().Count == 0)
        {
            EnsureOwnAdbServer();
            steps.Add("adb 服务被别的程序占着，已换成自带的");
        }

        if (code.Length > 0)
        {
            var pr = await Run(adbExe, new[] { "pair", addr, code }, 25000);
            bool paired = pr.text.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!paired) return new Dictionary<string, object> { { "ok", false }, { "text", "配对没成功：" + pr.text.Trim() } };
            steps.Add("配对成功");
        }

        // 配对端口和投屏端口不是同一个，配对完要重新发现真正能连的那个。
        // 只认地址相同的那条广播：之前这里取"任意一条"，会把连接目标换成另一台设备。
        string target = addr;
        if (code.Length > 0)
        {
            var disc = await DiscoverDevices(true);
            {
                var conn = disc.Select(o => o as Dictionary<string, object>)
                               .FirstOrDefault(o => o != null
                                   && Convert.ToString(o["kind"]) == "connect"
                                   && Convert.ToString(o["addr"]).Split(':')[0] == ip);
                if (conn != null)
                {
                    var a = Convert.ToString(conn["addr"]);
                    if (!string.IsNullOrEmpty(a)) target = a;
                }
            }
        }

        // adb 同时最多 AdbMaxDevices 台，连新设备前先看有没有位置
        var connectedNow = await ConnectedWireless();
        bool targetIpOnline = connectedNow.Any(c => c.Split(':')[0] == ip);
        if (!targetIpOnline)
        {
            int total = (await Run(adbExe, new[] { "devices" })).text.Split('\n')
                .Count(l => { var s = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); return s.Length >= 2 && s[1] == "device"; });
            if (total >= AdbMaxDevices)
                return new Dictionary<string, object> { { "ok", false }, { "text", "已达连接上限（" + AdbMaxDevices + " 台），先断开一台设备再连。" } };
        }

        var cr = await Run(adbExe, new[] { "connect", target }, 15000);
        bool ok = cr.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                  && cr.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0;
        if (!ok && target != addr)
        {
            var back = await Run(adbExe, new[] { "connect", addr }, 12000);
            if (back.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                && back.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0)
            { ok = true; target = addr; }
        }
        // 常用地址里的端口可能已经失效（设备重开无线调试会换端口）：
        // 现场再发现一次，同 IP 的其它端口直接拿来重试
        if (!ok)
        {
            var fresh = await DiscoverDevices(true);
            var cand = fresh.Select(o => o as Dictionary<string, object>)
                            .FirstOrDefault(o => o != null
                                && Convert.ToString(o["kind"]) == "connect"
                                && Convert.ToString(o["addr"]).Split(':')[0] == ip
                                && Convert.ToString(o["addr"]) != target);
            if (cand != null)
            {
                string na = Convert.ToString(cand["addr"]);
                var cv = await ConnectVerified(na, 6000);
                if ((bool)cv["ok"])
                {
                    ok = true; target = na;
                    steps.Add("端口已变，改连 " + na);
                }
            }
        }
        // 最后必须确认真的可用：OPPO 等专有端口也会先报 connected，随后 offline
        if (ok)
        {
            var verify = await ConnectVerified(target, 6000);
            ok = (bool)verify["ok"];
            if (!ok)
            {
                string st = Convert.ToString(verify["state"]);
                steps.Add(st == "unauthorized"
                    ? "找到了设备，但需要授权/重新配对"
                    : (ForeignAdbOwner().Length > 0
                        ? "本机 5037 仍被其它程序的 adb 占用，这个端口连不上"
                        : "这个端口不是可用的无线调试"));
            }
        }
        steps.Add(ok ? "已连上 " + target : "连接失败：" + (cr.text.Trim().Length > 0 ? cr.text.Trim() : "没有回应"));
        Log("wifi-connect " + target + " ok=" + ok);
        if (ok)
        {
            SaveLastDevice(target);
            await LearnIdentity(target, "", "");
        }
        return new Dictionary<string, object> { { "ok", ok }, { "text", string.Join("\n", steps) } };
    }

    object ReadJson(string file)
    {
        try { if (!File.Exists(file)) return null; return Json.DeserializeObject(File.ReadAllText(file)); }
        catch { return null; }
    }
    void Remember(string name)
    {
        var list = new List<object>();
        var raw = ReadJson(RecentFile) as System.Collections.IEnumerable;
        if (raw != null) foreach (var o in raw) list.Add(o);
        list.Insert(0, name);
        var seen = new List<object>();
        foreach (var o in list) if (!seen.Contains(o)) seen.Add(o);
        WriteJson(RecentFile, seen.Take(8).ToList());
    }

    Dictionary<string, int> LoadUsage()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var raw = ReadJson(UsageFile) as Dictionary<string, object>;
        if (raw != null)
            foreach (var kv in raw)
            {
                int n;
                if (int.TryParse(Convert.ToString(kv.Value), out n) && n > 0) d[kv.Key] = n;
            }
        return d;
    }

    void CountLaunch(string pkg)
    {
        if (pkg.Length == 0) return;
        var d = LoadUsage();
        int n;
        d.TryGetValue(pkg, out n);
        d[pkg] = n + 1;
        WriteJson(UsageFile, d);
    }

    void WriteJson(string file, object obj)
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(file, Json.Serialize(obj), Encoding.UTF8); } catch { }
    }

    // ── P0：虚拟屏生命周期管理 ──
    async Task<List<int>> ListDisplayIds(string id)
    {
        var r = await Adb(id, "shell", "dumpsys", "display");
        var ids = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r.text, "mDisplayId=([0-9]+)"))
        {
            int v;
            if (int.TryParse(m.Groups[1].Value, out v) && v != 0) ids.Add(v); // 0 是物理主屏，永不清理
        }
        return ids.ToList();
    }

    async Task<object> VirtualDisplays(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("先选设备");
        var ids = await ListDisplayIds(id);
        var list = ids.Select(d => (object)new Dictionary<string, object> { { "id", d } }).ToList();
        return new Dictionary<string, object> { { "ok", true }, { "displays", list } };
    }

    async Task<object> CleanupVirtualDisplays(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("先选设备");
        var ids = await ListDisplayIds(id);
        var results = new List<object>();
        foreach (var d in ids)
        {
            var r = await Adb(id, "shell", "display", "remove", d.ToString());
            results.Add(new Dictionary<string, object> { { "id", d }, { "ok", r.ok }, { "text", Clean(r.text) } });
        }
        return new Dictionary<string, object> { { "ok", true }, { "removed", results } };
    }

    // ── P0：虚拟屏模板 ──
    object VdTemplates()
    {
        var presets = new List<object> {
            new Dictionary<string, object>{ { "name", "720P 轻量" }, { "size", "1280x720/240" } },
            new Dictionary<string, object>{ { "name", "1080P 办公" }, { "size", "1920x1080/320" } },
            new Dictionary<string, object>{ { "name", "2K 高清" }, { "size", "2560x1440/480" } },
            new Dictionary<string, object>{ { "name", "720P 竖屏" }, { "size", "720x1280/240" } },
            new Dictionary<string, object>{ { "name", "1080P 竖屏" }, { "size", "1080x1920/320" } },
            new Dictionary<string, object>{ { "name", "2K 竖屏" }, { "size", "1440x2560/480" } }
        };
        var custom = ReadJson(TemplatesFile) as System.Collections.IEnumerable;
        var cl = new List<object>();
        if (custom != null) foreach (var o in custom) cl.Add(o);
        return new Dictionary<string, object> { { "ok", true }, { "presets", presets }, { "custom", cl } };
    }

    object VdTemplateSave(Dictionary<string, object> p)
    {
        string name = Ps(p, "name").Trim(), size = Ps(p, "size").Trim();
        if (name.Length == 0 || size.Length == 0) return Err("模板名称和分辨率都要填");
        if (!System.Text.RegularExpressions.Regex.IsMatch(size, @"^\d+x\d+(/\d+)?$")) return Err("尺寸格式要像 1920x1080/320");
        var list = new List<object>();
        var raw = ReadJson(TemplatesFile) as System.Collections.IEnumerable;
        if (raw != null) foreach (var o in raw) list.Add(o);
        list.RemoveAll(o => { var d = o as Dictionary<string, object>; return d != null && Convert.ToString(d["name"]) == name; });
        list.Add(new Dictionary<string, object> { { "name", name }, { "size", size } });
        WriteJson(TemplatesFile, list);
        return new Dictionary<string, object> { { "ok", true }, { "custom", list } };
    }

    object VdTemplateDelete(Dictionary<string, object> p)
    {
        string name = Ps(p, "name").Trim();
        var list = new List<object>();
        var raw = ReadJson(TemplatesFile) as System.Collections.IEnumerable;
        if (raw != null) foreach (var o in raw) list.Add(o);
        list.RemoveAll(o => { var d = o as Dictionary<string, object>; return d != null && Convert.ToString(d["name"]) == name; });
        WriteJson(TemplatesFile, list);
        return new Dictionary<string, object> { { "ok", true }, { "custom", list } };
    }

    // ── P0：读取设备应用列表（包名 + 启动入口，本地缓存增量查询） ──
    async Task<object> AppsList(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        if (id.Length == 0) return Err("先选设备");
        var r = await Adb(id, "shell", "pm", "list", "packages", "-f");
        var pkgs = new List<string>();
        foreach (var line in r.text.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith("package:")) continue;
            int eq = l.LastIndexOf('=');
            if (eq >= 0) { var nm = l.Substring(eq + 1).Trim(); if (nm.Length > 0) pkgs.Add(nm); }
        }
        pkgs = pkgs.Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // 系统应用集合：系统应用不解析启动入口（打开时直接用包名），仅第三方参与缓存解析
        var sysPkgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sr = await Adb(id, "shell", "pm", "list", "packages", "-s");
        foreach (var line in sr.text.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("package:")) { var nm = l.Substring(8).Trim(); if (nm.Length > 0) sysPkgs.Add(nm); }
        }

        var cache = LoadAppsCache();
        var need = pkgs.Where(x => !sysPkgs.Contains(x) && !cache.ContainsKey(x)).ToList();
        if (need.Count > 0)
        {
            var sem = new SemaphoreSlim(6);
            var tasks = need.Select(pkg => Task.Run(async () =>
            {
                string act = "";
                await sem.WaitAsync();
                try { act = await ResolveActivity(id, pkg); }
                finally { sem.Release(); }
                lock (cache) cache[pkg] = act;
            })).ToArray();
            await Task.WhenAll(tasks);
            SaveAppsCache(cache);
        }

        // 常用的排前面：打开过一次就往上走，同次数按包名字典序（pkgs 已是字典序，OrderBy 稳定）
        var usage = LoadUsage();
        pkgs = pkgs.OrderByDescending(x => { int n; return usage.TryGetValue(x, out n) ? n : 0; }).ToList();

        var apps = pkgs.Select(pkg =>
        {
            int n; usage.TryGetValue(pkg, out n);
            return (object)new Dictionary<string, object>
            {
                { "pkg", pkg },
                { "activity", cache.ContainsKey(pkg) ? cache[pkg] : "" },
                { "sys", sysPkgs.Contains(pkg) },
                { "used", n },
                { "name", AppNote(pkg) }
            };
        }).ToList();
        return new Dictionary<string, object> { { "ok", true }, { "apps", apps }, { "total", pkgs.Count }, { "fresh", need.Count } };
    }

    async Task<string> ResolveActivity(string id, string pkg)
    {
        var r = await Adb(id, "shell", "pm", "resolve-activity", "--brief", pkg);
        var m = System.Text.RegularExpressions.Regex.Match(r.text, @"([A-Za-z0-9_.]+)(/[A-Za-z0-9_.]+)");
        return m.Success ? m.Groups[2].Value : "";
    }

    Dictionary<string, string> LoadAppsCache()
    {
        var d = new Dictionary<string, string>();
        var raw = ReadJson(AppsCacheFile) as Dictionary<string, object>;
        if (raw != null)
            foreach (var kv in raw)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null && kv.Key != "total")
                    d[kv.Key] = Convert.ToString(kv.Value);
        return d;
    }

    void SaveAppsCache(Dictionary<string, string> d)
    {
        try
        {
            var obj = new Dictionary<string, object>();
            foreach (var kv in d) obj[kv.Key] = kv.Value;
            WriteJson(AppsCacheFile, obj);
        }
        catch { }
    }

    // ── P0：在虚拟屏里启动应用 ──
    async Task<object> LaunchApp(Dictionary<string, object> p)
    {
        string id = Ps(p, "id"), pkg = Ps(p, "pkg"), activity = Ps(p, "activity"), size = Ps(p, "size", "1920x1080/320");
        if (id.Length == 0) return Err("先选设备");
        if (pkg.Length == 0) return Err("缺包名");
        int disp = await CurrentVirtualDisplay(id);

        // 已有虚拟屏：先试原地追加启动；am 的写法各家系统不一致，StartOnDisplay 里两种都试
        if (disp != 0)
        {
            string act = activity;
            if (act.Length == 0)
            {
                var cache = LoadAppsCache();
                if (!cache.TryGetValue(pkg, out act) || act.Length == 0) act = await ResolveActivity(id, pkg);
            }
            string comp = pkg + act;
            if (comp != pkg && comp.Length > 0)
            {
                var r = await StartOnDisplay(id, disp, comp);
                bool ok = r.ok && r.text.IndexOf("Unknown option", StringComparison.OrdinalIgnoreCase) < 0
                              && r.text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0;
                if (ok) { CountLaunch(pkg); return new Dictionary<string, object> { { "ok", true }, { "displayId", disp }, { "created", false } }; }
            }
        }

        // 新开虚拟屏窗口 + scrcpy 原生 --start-app（把应用启动到该虚拟屏，兼容性最好）
        var lw = await LaunchWindow(new Dictionary<string, object> {
            { "id", id }, { "size", size }, { "app", pkg },
            { "bitrate", Ps(p, "bitrate", "12M") },
            { "windowWidth", Ps(p, "windowWidth", "0") },
            // 投屏目标要跟着透传，否则从应用列表开的窗口会掉回默认屏
            { "monIndex", Ps(p, "monIndex", "-1") },
            { "alwaysOnTop", Ps(p, "alwaysOnTop", "0") },
            { "borderless", Ps(p, "borderless", "0") },
            // 键盘通道和粘贴方式也要透传，否则从应用列表开的窗口会掉回默认设置
            { "kbd", Ps(p, "kbd", "sys") },
            { "legacyPaste", Ps(p, "legacyPaste", "0") },
            { "keepActive", Ps(p, "keepActive", "1") } });
        var dict = lw as Dictionary<string, object>;
        object dv;
        if (dict == null || !dict.TryGetValue("displayId", out dv))
            return new Dictionary<string, object> { { "ok", false }, { "error", "虚拟屏没起来：窗口启动失败" } };
        int nd;
        if (!int.TryParse(Convert.ToString(dv), out nd) || nd <= 0)
            return new Dictionary<string, object> { { "ok", false }, { "error", "虚拟屏没起来：没检测到新显示器" } };
        return new Dictionary<string, object> { { "ok", true }, { "displayId", nd }, { "created", true } };
    }

    async Task<int> CurrentVirtualDisplay(string id)
    {
        var ids = await ListDisplayIds(id);
        return ids.Count > 0 ? ids.Max() : 0;
    }

    // ── 应用图标：优先程序自带的兜底图标，其次从设备 APK 提取（toybox unzip）──

    // 微信这类 APK 把资源改名成 r/xx/yy 且图标是 webp/自适应图标，按名字在包里挑不到，
    // 所以这类应用改用内嵌进 exe 的图标：往 assets\icons\ 放一个 <包名>.png 就生效。
    static readonly Dictionary<string, string> AppNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "com.tencent.mm", "微信" },
    };
    static Dictionary<string, string> builtinIcons;

    static Dictionary<string, string> BuiltinIcons()
    {
        if (builtinIcons != null) return builtinIcons;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var nm in asm.GetManifestResourceNames())
            {
                int at = nm.IndexOf("icon.", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || !nm.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                string pkg = nm.Substring(at + 5, nm.Length - at - 5 - 4);
                if (pkg.Length == 0) continue;
                using (var st = asm.GetManifestResourceStream(nm))
                {
                    if (st == null) continue;
                    var ms = new MemoryStream();
                    st.CopyTo(ms);
                    d[pkg] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        catch { }
        builtinIcons = d;
        return d;
    }

    static string AppNote(string pkg)
    {
        string n;
        return AppNotes.TryGetValue(pkg, out n) ? n : "";
    }

    async Task<object> AppIcon(Dictionary<string, object> p)
    {
        string id = Ps(p, "id"), pkg = Ps(p, "pkg");
        if (pkg.Length == 0) return Err("缺参数");
        string builtin;
        if (BuiltinIcons().TryGetValue(pkg, out builtin))
            return new Dictionary<string, object> { { "ok", true }, { "png", builtin } };
        if (id.Length == 0) return Err("缺参数");
        string dir = Path.Combine(DataDir, "app-icons");
        try { Directory.CreateDirectory(dir); } catch { }
        string cachePath = Path.Combine(dir, San(pkg) + ".png");
        if (File.Exists(cachePath))
        {
            try { return PngResult(cachePath); } catch { }
        }
        var r = await Adb(id, "shell", "pm", "path", pkg);
        string apk = "";
        foreach (var line in r.text.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("package:")) { apk = l.Substring(8).Trim(); if (apk.Length > 0) break; }
        }
        if (apk.Length == 0) return new Dictionary<string, object> { { "ok", false }, { "icon", false } };
        var ls = await Adb(id, "shell", "unzip", "-l", apk);
        string pick = null; int pickRank = int.MaxValue;
        foreach (var line in ls.text.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"([\w./-]+\.png)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || m.Value.IndexOf("ic_launcher", StringComparison.OrdinalIgnoreCase) < 0) continue;
            string f = m.Groups[1].Value;
            int rank = IconRank(f);
            if (rank < pickRank) { pickRank = rank; pick = f; }
        }
        if (pick == null) return new Dictionary<string, object> { { "ok", false }, { "icon", false } };
        var bytes = await AdbRaw(id, "exec-out", "unzip", "-p", apk, pick);
        if (bytes == null || bytes.Length < 100) return new Dictionary<string, object> { { "ok", false }, { "icon", false } };
        try { File.WriteAllBytes(cachePath, bytes); } catch { }
        try { return PngResult(cachePath); } catch { }
        return new Dictionary<string, object> { { "ok", false }, { "icon", false } };
    }
    static object PngResult(string path)
    {
        var b = File.ReadAllBytes(path);
        return new Dictionary<string, object> { { "ok", true }, { "png", "data:image/png;base64," + Convert.ToBase64String(b) } };
    }
    static int IconRank(string f)
    {
        var l = f.ToLowerInvariant();
        if (l.Contains("xxhdpi")) return 1;
        if (l.Contains("xhdpi")) return 2;
        if (l.Contains("hdpi")) return 3;
        if (l.Contains("mdpi")) return 4;
        if (l.Contains("xxxhdpi")) return 5;
        if (l.Contains("drawable")) return 6;
        return 7;
    }
    static string San(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    object OpenUrl(Dictionary<string, object> p)
    {
        string u = Ps(p, "url");
        if (u.StartsWith("https://") || u.StartsWith("http://"))
        {
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
            catch (Exception ex) { return Err("打开失败：" + ex.Message); }
        }
        return new Dictionary<string, object> { { "ok", true } };
    }

    // ── 关于与打赏 ──
    object About()
    {
        return new Dictionary<string, object> {
            { "ok", true },
            { "app", "水月清影" },
            { "version", AppVersion },
            { "versionCode", AppVersionCode },
            { "license", "GPL-3.0" }
        };
    }

    async Task<object> CheckUpdate()
    {
        string text;
        try
        {
            text = await Task.Run(() =>
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(UpdateUrl + "?v=" + AppVersionCode);
                req.Timeout = 8000; req.ReadWriteTimeout = 8000;
                req.UserAgent = "shuiyue/" + AppVersion;
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            });
        }
        catch (Exception ex)
        {
            Log("检查更新失败：" + ex.Message);
            return new Dictionary<string, object> { { "ok", false }, { "error", "连不上更新服务器（" + ex.Message + "）" }, { "local", AppVersion } };
        }

        var doc = Json.DeserializeObject(text) as Dictionary<string, object>;
        if (doc == null) return new Dictionary<string, object> { { "ok", false }, { "error", "更新清单格式不对" }, { "local", AppVersion } };

        int code = 0; int.TryParse(Ps(doc, "versionCode"), out code);
        string size = Ps(doc, "size");
        long bytes = 0; long.TryParse(size, out bytes);
        var remote = new Dictionary<string, object> {
            { "versionName", Ps(doc, "versionName") },
            { "versionCode", code },
            { "notes", Ps(doc, "notes") },
            { "url", Ps(doc, "url") },
            { "size", bytes },
            { "published", Ps(doc, "published") },
            { "force", Ps(doc, "force") == "True" || Ps(doc, "force") == "true" }
        };
        bool has = code > AppVersionCode;
        Log("检查更新：本地 " + AppVersionCode + " 远端 " + code + (has ? " → 有新版" : " → 已最新"));
        return new Dictionary<string, object> { { "ok", true }, { "local", AppVersion }, { "localCode", AppVersionCode }, { "hasUpdate", has }, { "remote", remote } };
    }

    object SaveRewardQr()
    {
        var src = Path.Combine(AppDir(), "alipay_reward_qr.png");
        if (!File.Exists(src)) return Err("打赏二维码图片缺失（alipay_reward_qr.png）");
        try
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dest = Path.Combine(desk, "水月清影-打赏二维码.png");
            File.Copy(src, dest, true);
            return new Dictionary<string, object> { { "ok", true }, { "path", dest } };
        }
        catch (Exception ex) { return Err("保存失败：" + ex.Message); }
    }
}

