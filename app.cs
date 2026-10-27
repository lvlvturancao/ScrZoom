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
        public bool stolen;   // 应用被手机侧打开，虚拟屏已经空了
    }
    static readonly List<CastSession> Sessions = new List<CastSession>();
    static readonly object SessionLock = new object();
    CastSession lastSpawned;

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
        var r = await Restore(new Dictionary<string, object> { { "id", id } });
        var d = r as Dictionary<string, object>;
        if (d != null && d.ContainsKey("error")) Log("重连后补还原未完成: " + d["error"]);
        else { try { File.Delete(PendingFile); } catch { } Log("重连后补还原完成 " + id); }
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
    public const string AppVersion = "0.3.0";
    public const int AppVersionCode = 6;
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
        Log("recv " + action + " id=" + id);
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
            case "to-wireless": return await ToWireless(p);
            case "to-wired": return await ToWired(p);
            case "wifi-discover": return await WifiDiscover();
            case "wifi-disconnect": return await WifiDisconnect(p);
            case "wifi-connect": return await WifiConnect(p);
            case "wifi-forget": return WifiForget(p);
            case "sessions": return await SessionsList();
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
        // 每台设备的几条查询同时发，逐台串行会让首屏等上十几秒
        var probes = ids.Select(async id =>
        {
            // 无线设备：adb devices 里的条目可能是「假在线」——连接早断了但状态还写着 device，
            // 光看状态字段永远显示已连接。主动发一条短命令探活，探不通就判离线并 disconnect，
            // 让这条陈旧条目从 adb 里彻底消失，后面自动重连才不会一直对着死地址空试。
            if (id.Contains(":") && !id.StartsWith("emulator"))
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
            var name = RegexVal(r.text, "(?m)^" + System.Text.RegularExpressions.Regex.Escape(id) + @"\s.*?model:(\S+)") ?? id;
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
                { "via", id.Contains(":") ? "wireless" : "usb" },
                { "rot", RotVal(rot.text) }
            };
            foreach (var kv in wm) d[kv.Key] = kv.Value;
            RememberBaseline(id, wm);   // 顺手记下设备当前的生效分辨率/密度，还原时写回这个值
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
        return new Dictionary<string, object> { { "devices", list }, { "version", ver }, { "engine", engineDir ?? "" } };
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
            "--max-fps=" + Ps(p, "fps", "60"), "--keyboard=uhid" };
        if (Ps(p, "fullscreen") == "1") { a.Add("--fullscreen"); AddMonitorPos(a, p); }
        else AddWindowOpts(a, p);
        return Spawn(a, Ps(p, "id"),
            Ps(p, "fullscreen") == "1" ? "镜像 · 全屏" : "镜像",
            Ps(p, "size"), false, "");
    }

    async Task<object> LaunchWindow(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        // 记下启动前的虚拟屏集合，窗口起来后 diff 出本次新加的 display id
        var before = await ListDisplayIds(id);
        var a = new List<string> { "-s", id, "--new-display=" + Ps(p, "size", "1280x720/240"),
            "--video-bit-rate=" + Ps(p, "bitrate", "12M"), "--keyboard=uhid" };
        // 视频流保持虚拟屏原分辨率，电脑放不下时只缩窗口，不降清晰度
        AddWindowOpts(a, p);
        string app = Ps(p, "app");
        string appPkg = "";
        if (app.Length > 0)
        {
            bool isPkg = System.Text.RegularExpressions.Regex.IsMatch(app, "^[A-Za-z0-9_.]+$") && app.Contains(".");
            a.Add("--start-app=" + (isPkg ? app : "?" + app));
            if (!isPkg) Remember(app);
            else { appPkg = app; CountLaunch(app); }      // 应用列表按这个次数把常用的排到前面
        }
        var res = Spawn(a, id, "虚拟屏", Ps(p, "size", "1280x720/240").Split('/')[0], false, appPkg);
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

        SaveState(id, (bool)info["hadSizeOverride"] ? (string)info["currentSize"] : null,
                       (bool)info["hadDpiOverride"] ? Convert.ToString(info["currentDpi"]) : null);

        await Adb(id, "shell", "wm", "size", w + "x" + h);
        await Adb(id, "shell", "wm", "density", dpi.ToString());
        await Task.Delay(1200);
        var now = await ReadWm(id);
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
            "--max-fps=" + Ps(p, "fps", "60"), "--keyboard=uhid", "--fullscreen" };
        // 全屏档由 scrcpy 铺满「窗口所在那块屏」，所以先把窗口原点摆到用户选的目标显示器
        AddWindowOpts(a, p);
        var res = Spawn(a, id, "放大", w + "×" + h, true, "");
        var dict = res as Dictionary<string, object>;
        if (dict != null)
        {
            if (got.Length == 2)
                dict["applied"] = new Dictionary<string, object> {
                    { "w", int.Parse(got[0]) }, { "h", int.Parse(got[1]) }, { "dpi", now["currentDpi"] } };
            if (notice.Length > 0) dict["notice"] = notice;
        }
        return res;
    }

    object Spawn(List<string> a, string id, string mode, string size, bool scaled, string appPkg)
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
            var sess = new CastSession {
                pid = pr.Id, deviceId = id,
                deviceName = LookupName(LoadNames(), "", id),
                mode = mode, size = size, scaled = scaled,
                appPkg = appPkg ?? "",
                startedAt = DateTime.Now.ToString("HH:mm")
            };
            lock (SessionLock) Sessions.Add(sess);
            lastSpawned = sess;
            int n = 0;
            // 开头 40 行走日志；同时留一份环形缓冲，退出时把最后几行打出来——
            // 断连原因通常只在最后几行，不留在缓冲里就查不到
            var tail = new Queue<string>(40);
            var gate = new object();
            Action<string> take = line =>
            {
                if (line == null) return;
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
                await Task.Delay(400);   // 等管道读干净再取最后几行
                int code = -1;
                try { code = pr.ExitCode; } catch { }
                string[] last;
                lock (gate) { last = tail.ToArray(); }
                Log("scrcpy 退出 code=" + code);
                int from = Math.Max(0, last.Length - 12);
                for (int i = from; i < last.Length; i++) Log("  | " + last[i]);
                // 无论是谁关的这一路（程序、Alt+F4、任务栏），最后一路收尾时把设备参数还原
                lock (SessionLock) Sessions.Remove(sess);
                if (sess.scaled && RestoreOnClose() && !HasLiveSession(sess.deviceId))
                {
                    try
                    {
                        var rr = await Restore(new Dictionary<string, object> { { "id", sess.deviceId } });
                        var rd = rr as Dictionary<string, object>;
                        bool bad = rd != null && rd.ContainsKey("error");
                        if (bad) WriteJson(PendingFile, new Dictionary<string, object> { { "id", sess.deviceId } });
                        Log("关闭投屏后自动还原 " + sess.deviceId + (bad ? " 失败：" + rd["error"] : " 完成"));
                    }
                    catch (Exception ex2) { Log("自动还原异常: " + ex2.Message); }
                }
            };
            pr.BeginOutputReadLine();
            pr.BeginErrorReadLine();
            return new Dictionary<string, object> { { "ok", true }, { "cmd", "scrcpy " + string.Join(" ", a) } };
        }
        catch (Exception ex) { return Err(ex.Message); }
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
        var st = ReadJson(StateFile) as Dictionary<string, object>;
        string sid = Tv(st, "id");
        string shost = Tv(st, "host");
        // 端口轮换后完整地址会变，同 ip 也算同一台设备
        bool same = st != null && (sid == id || (!string.IsNullOrEmpty(shost) && shost == HostOf(id)));
        var bl = BaselineFor(id);
        string sizeArg = PickRestore(same ? Tv(st, "sizeOverride") : "", bl, "ovSize", "size", "physSize");
        string dpiArg = PickRestore(same ? Tv(st, "dpiOverride") : "", bl, "ovDpi", "dpi", "physDpi");
        Log("还原 " + id + " -> size=" + sizeArg + " density=" + dpiArg);
        var r1 = await Adb(id, "shell", "wm", "size", sizeArg);
        var r2 = await Adb(id, "shell", "wm", "density", dpiArg);
        if (!r1.ok || !r2.ok) return Err("设备没响应，没能还原设备分辨率与显示大小");
        if (same) { try { File.Delete(StateFile); } catch { } }   // 设备没响应时记录要留着，重连后再补
        return await ReadWm(id);
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
            if (p.Length >= 2 && p[1] == "device" && p[0].Contains(":")) list.Add(p[0]);
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

    class ProbeOutcome { public WifiEntry entry; public Dictionary<string, object> item; }

    Dictionary<string, object> KnownItem(WifiEntry e, string name, string state, string reason, string note, bool alive)
    {
        return new Dictionary<string, object> {
            { "addr", e.addr }, { "kind", "known" }, { "known", true }, { "connected", false },
            { "state", state }, { "reason", reason }, { "note", note }, { "name", name },
            { "mac", e.mac }, { "serial", e.serial }, { "mdnsId", e.mdnsId },
            { "alive", alive },
            { "hasIdentity", !string.IsNullOrEmpty(e.mac) || !string.IsNullOrEmpty(e.serial) || !string.IsNullOrEmpty(e.mdnsId) },
            { "canRecover", true } };
    }

    // 普通扫描：广播 → 地址簿探活 → ARP 找新 IP + 旧端口一次尝试。
    // 不自动扫端口；端口也变了就停在卡片，等用户点「找端口」。
    async Task<object> WifiDiscover()
    {
        var devices = new List<object>();
        var recovered = new List<object>();
        if (adbExe == null)
            return new Dictionary<string, object> { { "devices", devices }, { "recovered", recovered }, { "mdnsBlocked", false } };

        await CleanupOffline();
        var connected = await ConnectedWireless();
        var connIps = new HashSet<string>(connected.Select(c => c.Split(':')[0]), StringComparer.OrdinalIgnoreCase);
        var names = LoadNames();
        var entries = LoadWifiEntries();

        var mdns = await CollectMdns(3, 2500, 400);
        bool mdnsEmpty = mdns.devs.Count == 0;
        var seenIps = new HashSet<string>(connIps, StringComparer.OrdinalIgnoreCase);
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string last = LoadLastDevice();

        // 广播里同一台设备出现在新 IP：是上次设备就直接接回，其它设备给用户点
        foreach (var dev in mdns.devs)
        {
            if (string.IsNullOrEmpty(dev.ip) || seenIps.Contains(dev.ip)) continue;
            string addr = dev.connAddr ?? dev.pairAddr ?? dev.ip;
            var entry = entries.FirstOrDefault(e =>
                (!string.IsNullOrEmpty(dev.serial) &&
                 ((!string.IsNullOrEmpty(e.serial) && string.Equals(e.serial, dev.serial, StringComparison.OrdinalIgnoreCase)) ||
                  (!string.IsNullOrEmpty(e.mdnsId) && string.Equals(e.mdnsId, dev.serial, StringComparison.OrdinalIgnoreCase)))) ||
                e.addr.Split(':')[0] == dev.ip);
            string name = LookupName(names, dev.serial, addr);
            if (name.Length == 0 && entry != null) name = entry.name;
            if (entry != null && dev.connAddr != null && entry.addr != dev.connAddr)
            {
                bool isLast = string.Equals(entry.addr, last, StringComparison.OrdinalIgnoreCase);
                if (isLast)
                {
                    var cv = await ConnectVerified(dev.connAddr, 6000);
                    if ((bool)cv["ok"])
                    {
                        var id = await ReadDeviceIdentity(dev.connAddr);
                        bool same = string.IsNullOrEmpty(entry.serial) || string.IsNullOrEmpty(id.serial)
                                    || IdEq(entry.serial, id.serial);
                        if (same)
                        {
                            await LearnIdentity(dev.connAddr, dev.serial ?? entry.mdnsId, name.Length > 0 ? name : entry.name, entry.addr);
                            SaveLastDevice(dev.connAddr);
                            handled.Add(entry.addr);
                            recovered.Add(new Dictionary<string, object> { { "oldAddr", entry.addr }, { "addr", dev.connAddr }, { "name", name }, { "via", "mdns" } });
                            seenIps.Add(dev.ip);
                            Log("mDNS 换 IP 认领 " + entry.addr + " → " + dev.connAddr);
                            continue;
                        }
                        try { await Run(adbExe, new[] { "disconnect", dev.connAddr }, 3000); } catch { }
                        Log("mDNS 认领身份不符，不覆盖 " + dev.connAddr + " 原 serial=" + entry.serial + " 新 serial=" + id.serial);
                    }
                }
                devices.Add(new Dictionary<string, object> {
                    { "addr", dev.connAddr }, { "kind", "connect" }, { "connected", false },
                    { "name", name }, { "movedFrom", entry.addr },
                    { "note", "上次的设备换了 IP，点连接即可。" }, { "canRecover", true } });
                handled.Add(entry.addr);
                seenIps.Add(dev.ip);
                continue;
            }
            devices.Add(new Dictionary<string, object> {
                { "addr", addr }, { "kind", dev.connAddr != null ? "connect" : "pairing" },
                { "connected", false }, { "name", name } });
            seenIps.Add(dev.ip);
        }

        // 地址簿逐台探活（并发，最多 8 台）
        var pending = entries.Where(e => !handled.Contains(e.addr) && !seenIps.Contains(e.addr.Split(':')[0])).ToList();
        var probeTasks = pending.Select(e => ProbeEntry(e, names)).ToArray();
        await Task.WhenAll(probeTasks);
        var missing = new List<WifiEntry>();
        foreach (var t in probeTasks)
        {
            var o = t.Result;
            if (o == null) continue;
            if (o.item.ContainsKey("connected") && Convert.ToBoolean(o.item["connected"]))
            {
                seenIps.Add(o.entry.addr.Split(':')[0]);
                recovered.Add(new Dictionary<string, object> { { "oldAddr", o.entry.addr }, { "addr", o.entry.addr }, { "name", o.entry.name }, { "via", "same" } });
                continue;
            }
            devices.Add(o.item);
            missing.Add(o.entry);
            seenIps.Add(o.entry.addr.Split(':')[0]);
        }

        // 换 IP：ARP 按 MAC 找新地址，只试一次旧端口，不扫端口段。
        // 换 WiFi 后随机 MAC 可能变，找不到就留给「深度查找」。
        bool canArp = missing.Any(e => !string.IsNullOrEmpty(e.mac));
        if (canArp)
        {
            var lan = GetLanTarget(missing[0].addr.Split(':')[0]);
            var hosts = await ArpSweep(lan, null);
            var recoverTasks = new List<Task>();
            foreach (var e in missing)
            {
                if (string.IsNullOrEmpty(e.mac)) continue;
                string oldIp = e.addr.Split(':')[0];
                string oldPort = e.addr.Contains(":") ? e.addr.Split(':')[1] : "";
                if (oldPort.Length == 0) continue;
                // 代理 ARP 会让多个 IP 共用 MAC；有歧义就不自动认领
                var macHosts = hosts.Where(h => string.Equals(h.mac, e.mac, StringComparison.OrdinalIgnoreCase)).ToList();
                if (macHosts.Count != 1) continue;
                foreach (var h in macHosts)
                {
                    if (h.ip == oldIp) continue;
                    string target = h.ip + ":" + oldPort;
                    WifiEntry entry = e;
                    recoverTasks.Add(Task.Run(async () =>
                    {
                        var cv = await ConnectVerified(target, 6000);
                        if (!(bool)cv["ok"]) return;
                        var id = await ReadDeviceIdentity(target);
                        if (!IdentityMatches(entry, id))
                        {
                            try { await Run(adbExe, new[] { "disconnect", target }, 3000); } catch { }
                            Log("ARP 认领被身份校验拒绝 " + target + " 原 serial=" + entry.serial + " 新 serial=" + id.serial);
                            return;
                        }
                        await LearnIdentity(target, entry.mdnsId, entry.name, entry.addr);
                        lock (recovered) recovered.Add(new Dictionary<string, object> { { "oldAddr", entry.addr }, { "addr", target }, { "name", entry.name }, { "via", "arp" } });
                        if (LoadLastDevice() == entry.addr) SaveLastDevice(target);
                        Log("ARP 换 IP 认领 " + entry.addr + " → " + target);
                    }));
                }
            }
            await Task.WhenAll(recoverTasks);

            var recoveredOld = new HashSet<string>(recovered
                .OfType<Dictionary<string, object>>()
                .Select(x => Convert.ToString(x["oldAddr"])), StringComparer.OrdinalIgnoreCase);
            if (recoveredOld.Count > 0)
            {
                devices.RemoveAll(o =>
                {
                    var d = o as Dictionary<string, object>;
                    return d != null && recoveredOld.Contains(Convert.ToString(d["addr"]));
                });
                missing.RemoveAll(e => recoveredOld.Contains(e.addr));
            }
        }

        Log("wifi-discover 待连接 " + devices.Count + " 台（已连接 IP: " + string.Join(",", connIps) +
            "；广播条目 " + mdns.rawEntries + " 条，入列 " + mdns.devs.Count + " 台；地址簿 " + entries.Count +
            " 条，找回 " + recovered.Count + " 台）");
        if (mdnsEmpty)
            Log("wifi-discover 无广播，mdns 原始输出: " + (mdns.raw.Length > 0 ? mdns.raw.Replace("\r", "").Replace("\n", " | ") : "(空)"));

        return new Dictionary<string, object> {
            { "devices", devices }, { "recovered", recovered }, { "mdnsBlocked", mdnsEmpty } };
    }

    // 给只关心设备列表的内部调用用（手动连接的回退路径）
    async Task<List<object>> DiscoverDevices()
    {
        var obj = await WifiDiscover() as Dictionary<string, object>;
        if (obj == null) return new List<object>();
        var list = obj.ContainsKey("devices") ? obj["devices"] as List<object> : null;
        return list ?? new List<object>();
    }

    // 探一台地址簿设备：先确认机器在不在，再比对 MAC，最后连接并验证状态
    async Task<ProbeOutcome> ProbeEntry(WifiEntry e, Dictionary<string, string> names)
    {
        string ip = e.addr.Split(':')[0];
        var pg = await Run("ping", new[] { "-n", "1", "-w", "700", ip }, 5000);
        bool alive = pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
        string mac = alive ? GetMac(ip) : "";
        string name = e.name;
        if (name.Length == 0) name = LookupName(names, e.serial, e.addr);

        if (!alive)
            return new ProbeOutcome { entry = e, item = KnownItem(e, name, "gone", "offline",
                "这台机器不在线：可能换了 IP，或者 WiFi/息屏断开了。", false) };
        if (!string.IsNullOrEmpty(e.mac) && !string.IsNullOrEmpty(mac) && !string.Equals(e.mac, mac, StringComparison.OrdinalIgnoreCase))
            return new ProbeOutcome { entry = e, item = KnownItem(e, name, "gone", "moved",
                "这个 IP 现在是别的设备，手机可能换了 IP。", true) };

        var cv = await ConnectVerified(e.addr, 5000);
        if ((bool)cv["ok"])
        {
            var id = await ReadDeviceIdentity(e.addr);
            if (!IdentityMatches(e, id))
            {
                try { await Run(adbExe, new[] { "disconnect", e.addr }, 3000); } catch { }
                string other = id.model.Length > 0 ? id.model : e.addr;
                return new ProbeOutcome { entry = e, item = KnownItem(e, name, "gone", "moved",
                    "这个 IP 现在连到的是另一台设备（" + other + "），没有覆盖地址簿；手机可能换了 IP。", true) };
            }
            await LearnIdentity(e.addr, e.mdnsId, name, null);
            return new ProbeOutcome { entry = e, item = new Dictionary<string, object> {
                { "addr", e.addr }, { "name", name }, { "connected", true } } };
        }
        string st = Convert.ToString(cv["state"]);
        if (st == "unauthorized")
            return new ProbeOutcome { entry = e, item = KnownItem(e, name, "fail", "auth",
                "找到了无线调试，但需要授权：在手机上点允许，或重新配对。", true) };
        string note = st == "offline"
            ? "这个地址没有可用的无线调试：端口可能变了，点「找端口」重新扫描。"
            : FailNote("port", Convert.ToString(cv["text"]));
        return new ProbeOutcome { entry = e, item = KnownItem(e, name, "fail", "port", note, true) };
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
        await Run(adbExe, new[] { "kill-server" }, 5000);
        await Task.Delay(800);
        var s = await Run(adbExe, new[] { "start-server" }, 8000);
        return new Dictionary<string, object> { { "ok", s.ok }, { "text", s.ok ? "ok" : s.text } };
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
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            string gw = "";
            foreach (var g in props.GatewayAddresses)
                if (g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                { gw = g.Address.ToString(); break; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = ua.Address.ToString();
                if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                list.Add(new LanTarget { localIp = ip, prefix = ua.PrefixLength, gateway = gw });
            }
        }
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
                string mac = GetMac(ip);
                if (!string.IsNullOrEmpty(mac))
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
    async Task<Dictionary<string, object>> ConnectVerified(string addr, int waitMs)
    {
        var r = await Run(adbExe, new[] { "connect", addr }, 8000);
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
            if (p.Length >= 2 && p[1] == "offline" && p[0].Contains(":"))
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
        await CleanupOffline();
        var entries = LoadWifiEntries();
        if (entries.Count == 0)
            return new Dictionary<string, object> { { "ok", false }, { "text", "没有记录上次设备" }, { "addr", "" } };
        string last = LoadLastDevice();
        var e = entries.FirstOrDefault(x => string.Equals(x.addr, last, StringComparison.OrdinalIgnoreCase)) ?? entries[0];

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

        var owners = await MdnsOwners();
        bool adbHolds = owners.Any(o => ((Dictionary<string, object>)o)["name"].ToString().IndexOf("adb", StringComparison.OrdinalIgnoreCase) >= 0);
        string mdnsVerdict = owners.Count == 0 ? "free" : (adbHolds ? "ok" : "occupied");

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
                devs.Add(new Dictionary<string, object> { { "id", tk[0] }, { "via", tk[0].Contains(":") ? "wireless" : "usb" }, { "state", tk[1] } });
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
        if (id.Contains(":")) return Err("这台已经是无线连接了，换一台用数据线连着的");
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
        var pg = await Run("ping", new[] { "-n", "1", "-w", "700", ip }, 5000);
        return pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
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
                st.note = "这台设备已经不在原来的 IP 上，局域网里也没找到它的 MAC；请在手机上确认无线调试开着，或点「深度查找」。";
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
            st.note = "找到开放端口，但都不是无线调试：端口可能没开，或这台 IP 不是手机。";
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
            var disc = await DiscoverDevices();
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
            var fresh = await DiscoverDevices();
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
                steps.Add(st == "unauthorized" ? "找到了设备，但需要授权/重新配对" : "这个端口不是可用的无线调试");
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
            { "borderless", Ps(p, "borderless", "0") } });
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

