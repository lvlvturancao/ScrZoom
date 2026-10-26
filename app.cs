using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
    string LegacyWifiFile { get { return Path.Combine(DataDir, "wifi-last.json"); } }
    string NameBookFile { get { return Path.Combine(DataDir, "device-names.json"); } }
    string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
    string PendingFile { get { return Path.Combine(DataDir, "restore-pending.json"); } }
    string CapFile { get { return Path.Combine(DataDir, "device-capabilities.json"); } }

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
    }
    static readonly List<CastSession> Sessions = new List<CastSession>();
    static readonly object SessionLock = new object();

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

    object SessionsList()
    {
        var list = new List<object>();
        foreach (var s in LiveSessions())
            list.Add(new Dictionary<string, object> {
                { "pid", s.pid },
                { "deviceId", s.deviceId },
                { "deviceName", string.IsNullOrEmpty(s.deviceName) ? s.deviceId : s.deviceName },
                { "mode", s.mode },
                { "size", s.size },
                { "startedAt", s.startedAt },
                { "scaled", s.scaled } });
        return list;
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
        Width = 1180; Height = 780;
        MinimumSize = new Size(1020, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 247, 248);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        engineDir = FindEngine();
        if (engineDir != null) { scrcpyExe = Path.Combine(engineDir, "scrcpy.exe"); adbExe = Path.Combine(engineDir, "adb.exe"); }
        Log("启动 engine=" + (engineDir ?? "NULL") + " exe=" + AppDir());

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
    public const string AppVersion = "0.1.3";
    public const int AppVersionCode = 4;
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

    static string FindEngine()
    {
        var local = Path.Combine(AppDir(), "engine");
        if (File.Exists(Path.Combine(local, "scrcpy.exe"))) return local;

        // 先看程序自己的上一级目录：程序和引擎放在一起、整体搬到哪都能用，升级只换旁边那个 scrcpy 文件夹
        var beside = NewestEngine(new[] { Path.GetDirectoryName(AppDir()) });
        if (beside != null) return beside;

        // 兜底：下载目录
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return NewestEngine(new[] { downloads });
    }

    static string NewestEngine(string[] roots)
    {
        var candidates = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root).Where(d => Path.GetFileName(d).IndexOf("scrcpy", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(d => d))
                if (File.Exists(Path.Combine(d, "scrcpy.exe"))) candidates.Add(d);
        }
        return candidates.Count > 0 ? candidates[candidates.Count - 1] : null;
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

    async Task<object> Dispatch(string action, Dictionary<string, object> p)
    {
        switch (action)
        {
            case "monitor":
                var b = Screen.PrimaryScreen.Bounds;
                return new Dictionary<string, object> { { "w", b.Width }, { "h", b.Height } };
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
            case "sessions": return SessionsList();
            case "close-session": return await CloseSession(p);
            case "close-device": return await CloseDevice(p);
            case "settings": return GetSettings();
            case "set-restore": return SetRestore(p);
            case "wifi-restart-adb": return await RestartAdb();
            case "reconnect-last": return await ReconnectLast();
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
            Log("device " + id + " " + d["name"] + " " + d["currentSize"]);
            return (object)d;
        }).ToList();
        var list = new List<object>(await Task.WhenAll(probes));
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
        if (Ps(p, "fullscreen") == "1") a.Add("--fullscreen");
        else AddWindowOpts(a, p);
        return Spawn(a, Ps(p, "id"),
            Ps(p, "fullscreen") == "1" ? "镜像 · 全屏" : "镜像",
            Ps(p, "size"), false);
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
        if (app.Length > 0)
        {
            bool isPkg = System.Text.RegularExpressions.Regex.IsMatch(app, "^[A-Za-z0-9_.]+$") && app.Contains(".");
            a.Add("--start-app=" + (isPkg ? app : "?" + app));
            if (!isPkg) Remember(app);
        }
        var res = Spawn(a, id, "虚拟屏", Ps(p, "size", "1280x720/240").Split('/')[0], false);
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
            if (disp > 0) dict["displayId"] = disp;
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
        int dpi0 = (int)info["physicalDpi"];
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
        var res = Spawn(a, id, "放大", w + "×" + h, true);
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

    object Spawn(List<string> a, string id, string mode, string size, bool scaled)
    {
        if (scrcpyExe == null) return Err("没找到 scrcpy.exe");
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
                startedAt = DateTime.Now.ToString("HH:mm")
            };
            lock (SessionLock) Sessions.Add(sess);
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
        WriteJson(StateFile, new Dictionary<string, object> {
            { "id", id }, { "sizeOverride", sizeOverride }, { "dpiOverride", dpiOverride } });
    }

    async Task<object> Restore(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        var st = ReadJson(StateFile) as Dictionary<string, object>;
        if (st != null && Convert.ToString(st["id"]) == id)
        {
            string so = Convert.ToString(st["sizeOverride"]), dobj = Convert.ToString(st["dpiOverride"]);
            var r1 = await Adb(id, "shell", "wm", "size", string.IsNullOrEmpty(so) || so == "null" ? "reset" : so);
            var r2 = await Adb(id, "shell", "wm", "density", string.IsNullOrEmpty(dobj) || dobj == "null" ? "reset" : dobj);
            // 设备没响应就别删记录：留着，等重连回来再补还原
            if (r1.ok && r2.ok) { try { File.Delete(StateFile); } catch { } }
            else return Err("设备没响应，还原记录已保留，重连后会自动再试");
        }
        else
        {
            var r1 = await Adb(id, "shell", "wm", "size", "reset");
            var r2 = await Adb(id, "shell", "wm", "density", "reset");
            if (!r1.ok || !r2.ok) return Err("设备没响应，没能还原设备原始分辨率");
        }
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

    async Task<object> WifiDiscover()
    {
        var found = new List<object>();
        if (adbExe == null) return found;
        // 已连接的设备不再进扫描列表（下方设备列表已有），这里只拿它做排除依据
        var connected = await ConnectedWireless();
        var connIps = new HashSet<string>(connected.Select(c => c.Split(':')[0]), StringComparer.OrdinalIgnoreCase);
        var names = LoadNames();
        var byIp = new Dictionary<string, WifiDev>();
        // adb 的 mDNS 查询不稳定：一次查询经常漏设备，多轮查询合并提高发现率；
        // 全部为空时再补一轮（mDNS 后端可能短暂失聪，这里不重启 adb server，避免打断正在投屏的会话）
        bool mdnsEmpty = true;
        string rawSample = "";
        int rawEntries = 0;
        Func<Task> parseMdns = async () =>
        {
            var r = await Run(adbExe, new[] { "mdns", "services" }, 7000);
            if (rawSample.Length == 0 && r.text.Trim().Length > 0) rawSample = r.text;
            foreach (var line in r.text.Split('\n'))
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
                rawEntries++;
                name = string.Join(" ", tokens.Where(t => t != addr)
                    .Select(t => t.Replace("._adb-tls-connect._tcp", "").Replace("._adb-tls-pairing._tcp", ""))
                    .Where(t => t.Length > 0));
                var ip = addr.Split(':')[0];
                if (connIps.Contains(ip)) continue;   // 这台已经连着，扫描列表不再重复
                bool pairing = l.IndexOf("pairing", StringComparison.OrdinalIgnoreCase) >= 0;
                var ser = System.Text.RegularExpressions.Regex.Match(name, "adb-([0-9A-Za-z]+)-");
                WifiDev dev;
                if (!byIp.TryGetValue(ip, out dev)) { dev = new WifiDev { ip = ip }; byIp[ip] = dev; }
                if (string.IsNullOrEmpty(dev.name)) dev.name = name;
                if (string.IsNullOrEmpty(dev.serial) && ser.Success) dev.serial = ser.Groups[1].Value;
                if (pairing) { if (dev.pairAddr == null) dev.pairAddr = addr; }
                else if (dev.connAddr == null) dev.connAddr = addr;
                mdnsEmpty = false;
            }
        };
        // 设备的定时广播会间歇性断档（息屏时尤其明显）：先保证 3 轮用于多设备召回，
        // 之后一旦有结果就停；一直空就等满约 9 秒，别让一次扫描白跑
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await parseMdns();
            if (attempt >= 2 && byIp.Count > 0) break;
            if (attempt < 9) await Task.Delay(900);
        }
        if (mdnsEmpty)
        {
            await Task.Delay(1200);
            await parseMdns();
        }
        foreach (var dev in byIp.Values)
            found.Add(new Dictionary<string, object> {
                { "addr", dev.connAddr ?? dev.pairAddr ?? dev.ip },
                { "kind", dev.connAddr != null ? "connect" : "pairing" },
                { "connected", false },
                { "name", LookupName(names, dev.serial, dev.connAddr ?? dev.ip) } });

        // 有些设备从不发送发现信号（系统没做这个广播），只扫广播就永远列不出它。
        // 这里补一路：把常用地址簿里、本轮没出现的地址直接探一次。
        var seenIps = new HashSet<string>(byIp.Keys, StringComparer.OrdinalIgnoreCase);
        var todo = WifiBook().Where(a => { var ip = a.Split(':')[0]; return !seenIps.Contains(ip) && !connIps.Contains(ip); }).ToList();
        if (todo.Count > 0)
        {
            var tasks = todo.Select(a => ProbeKnown(a, names)).ToArray();
            await Task.WhenAll(tasks);
            foreach (var t in tasks)
            {
                var item = t.Result;
                if (item != null) found.Add(item);
            }
        }

        Log("wifi-discover 待连接 " + found.Count + " 台（已连接 IP: " + string.Join(",", connIps) + "；广播条目 " + rawEntries + " 条，入列 " + byIp.Count + " 台；常用地址探了 " + todo.Count + " 个）");
        if (byIp.Count == 0)
            Log("wifi-discover 无广播，mdns 原始输出: " + (rawSample.Length > 0 ? rawSample.Replace("\r", "").Replace("\n", " | ") : "(空)"));
        return found;
    }

    // 探一个常用地址：先确认机器在不在这个网，在才试连，避免干等超时
    async Task<Dictionary<string, object>> ProbeKnown(string addr, Dictionary<string, string> names)
    {
        string ip = addr.Split(':')[0];
        var pg = await Run("ping", new[] { "-n", "1", "-w", "700", ip }, 5000);
        bool alive = pg.text.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
        string state = "gone", note = "这台机器现在不在线，WiFi 可能断了或者地址变了";
        if (alive)
        {
            var cr = await Run(adbExe, new[] { "connect", addr }, 6000);
            string t = cr.text.Trim();
            if (t.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("already connected", StringComparison.OrdinalIgnoreCase) >= 0)
            { state = "on"; note = ""; }
            else state = "fail";
            if (state == "fail") note = KnownFailHint(t);
        }
        if (state == "on") return null;   // 已经连上了，交给下方设备列表，扫描列表不再重复
        return new Dictionary<string, object> {
            { "addr", addr }, { "kind", "known" }, { "known", true },
            { "connected", false }, { "state", state },
            { "note", note }, { "name", LookupName(names, "", addr) } };
    }

    static string KnownFailHint(string raw)
    {
        string t = raw ?? "";
        if (t.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0)
            return "端口不接受连接：无线调试可能已关掉，或端口换了，去手机页面上看新端口";
        if (t.IndexOf("authenticat", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
            || t.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0)
            return "要重新配对：手机上无线调试页填配对码再连一次";
        if (t.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || t.Length == 0)
            return "这台机器没回应：多半是端口变了，去手机无线调试页面上看当前端口";
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

    // 自动重连：先试上次那台，不行再依次试常用地址簿里的其它台
    async Task<object> ReconnectLast()
    {
        if (adbExe == null) return Err("没找到 adb");
        var cand = new List<string>();
        string last = LoadLastDevice();
        if (last.Length > 0) cand.Add(last);
        foreach (var a in WifiBook()) if (!cand.Contains(a)) cand.Add(a);
        if (cand.Count == 0)
            return new Dictionary<string, object> { { "ok", false }, { "text", "没有记录上次设备" }, { "addr", "" } };

        var online = await ConnectedWireless();
        foreach (var addr in cand.Take(5))
        {
            if (online.Any(c => c == addr || c.Split(':')[0] == addr.Split(':')[0]))
            {
                SaveLastDevice(addr); RememberWifi(addr);
                await TryRestorePending();
                return new Dictionary<string, object> { { "ok", true }, { "text", "已经在线" }, { "addr", addr } };
            }
            var r = await Run(adbExe, new[] { "connect", addr }, 7000);
            bool ok = r.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                      || r.text.IndexOf("already connected", StringComparison.OrdinalIgnoreCase) >= 0;
            ok = ok && r.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0;
            if (ok)
            {
                SaveLastDevice(addr); RememberWifi(addr);
                await TryRestorePending();
                return new Dictionary<string, object> { { "ok", true }, { "text", r.text.Trim() }, { "addr", addr } };
            }
        }
        return new Dictionary<string, object> { { "ok", false }, { "text", "常用地址都连不上，重新扫描或手动填一次" }, { "addr", cand[0] } };
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

    // 常用无线地址簿：有些设备不发送发现信号，只能靠这里记下的地址找回
    List<string> WifiBook()
    {
        var outList = new List<string>();
        var raw = ReadJson(WifiBookFile) as System.Collections.IEnumerable;
        if (raw != null)
            foreach (var o in raw)
            {
                var d = o as Dictionary<string, object>;
                string a = d != null ? Convert.ToString(d["addr"]) : Convert.ToString(o);
                if (!string.IsNullOrEmpty(a) && a.Contains(":") && !outList.Contains(a)) outList.Add(a);
            }
        // 早期版本只存一个地址，这里一并认账
        var legacy = ReadJson(LegacyWifiFile) as Dictionary<string, object>;
        if (legacy != null)
        {
            string ip = Convert.ToString(legacy["ip"]) ?? "", port = Convert.ToString(legacy["port"]) ?? "";
            if (ip.Length > 0)
            {
                string a = port.Length > 0 ? ip + ":" + port : ip;
                if (!outList.Contains(a)) outList.Add(a);
            }
        }
        var last = LoadLastDevice();
        if (last.Length > 0 && last.Contains(":") && !outList.Contains(last)) outList.Insert(0, last);
        // 同一台设备的端口会变，只留最近的那一条，否则列表里会堆一串连不上的旧端口
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedup = new List<string>();
        foreach (var a in outList) { if (!a.Contains(":")) continue; if (seenIps.Add(a.Split(':')[0])) dedup.Add(a); }
        return dedup.Take(8).ToList();
    }

    void RememberWifi(string addr)
    {
        if (string.IsNullOrEmpty(addr) || !addr.Contains(":")) return;
        var list = WifiBook();
        string ip = addr.Split(':')[0];
        list.RemoveAll(a => a.Split(':')[0] == ip);   // 同 IP 的旧端口直接顶掉
        list.Insert(0, addr);
        WriteJson(WifiBookFile, list.Take(8).Select(a => new Dictionary<string, object> { { "addr", a } }).ToList());
    }

    object WifiForget(Dictionary<string, object> p)
    {
        string addr = Ps(p, "addr").Trim();
        var list = WifiBook();
        bool hit = list.Remove(addr);
        WriteJson(WifiBookFile, list.Select(a => new Dictionary<string, object> { { "addr", a } }).ToList());
        if (hit && LoadLastDevice() == addr) SaveLastDevice("");
        return new Dictionary<string, object> { { "ok", hit }, { "text", hit ? "已忘记 " + addr : "常用地址里没有这条" } };
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
            var disc = await WifiDiscover() as List<object>;
            if (disc != null)
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

        var cr = await Run(adbExe, new[] { "connect", target }, 20000);
        bool ok = cr.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                  && cr.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0;
        if (!ok && target != addr)
        {
            var back = await Run(adbExe, new[] { "connect", addr }, 15000);
            if (back.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                && back.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0)
            { ok = true; target = addr; }
        }
        // 常用地址里的端口可能已经失效（设备重开无线调试会换端口）：
        // 现场再发现一次，同 IP 的其它端口直接拿来重试
        if (!ok)
        {
            var fresh = await WifiDiscover() as List<object>;
            if (fresh != null)
            {
                var cand = fresh.Select(o => o as Dictionary<string, object>)
                                .FirstOrDefault(o => o != null
                                    && Convert.ToString(o["kind"]) == "connect"
                                    && Convert.ToString(o["addr"]).Split(':')[0] == ip
                                    && Convert.ToString(o["addr"]) != target);
                if (cand != null)
                {
                    string na = Convert.ToString(cand["addr"]);
                    var r2 = await Run(adbExe, new[] { "connect", na }, 20000);
                    if (r2.text.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
                        && r2.text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        ok = true; target = na;
                        steps.Add("端口已变，改连 " + na);
                    }
                }
            }
        }
        steps.Add(ok ? "已连上 " + target : "连接失败：" + (cr.text.Trim().Length > 0 ? cr.text.Trim() : "没有回应"));
        Log("wifi-connect " + target + " ok=" + ok);
        if (ok) { SaveLastDevice(target); RememberWifi(target); } // 记住本次连接：下次自动重连、以及靠它找回不发信号的设备
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

        var apps = pkgs.Select(pkg => (object)new Dictionary<string, object>
        {
            { "pkg", pkg }, { "activity", cache.ContainsKey(pkg) ? cache[pkg] : "" }, { "sys", sysPkgs.Contains(pkg) }
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

        // 已有虚拟屏：先试原地追加启动；很多系统的 am 不支持 --display-id，失败就退回新开窗口
        if (disp != 0)
        {
            string comp = pkg + activity;
            if (comp != pkg && comp.Length > 0)
            {
                var r = await Adb(id, "shell", "am", "start", "--display-id=" + disp, "-n", comp);
                bool ok = r.ok && r.text.IndexOf("Unknown option", StringComparison.OrdinalIgnoreCase) < 0
                              && r.text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0;
                if (ok) return new Dictionary<string, object> { { "ok", true }, { "displayId", disp }, { "created", false } };
            }
        }

        // 新开虚拟屏窗口 + scrcpy 原生 --start-app（把应用启动到该虚拟屏，兼容性最好）
        var lw = await LaunchWindow(new Dictionary<string, object> {
            { "id", id }, { "size", size }, { "app", pkg },
            { "bitrate", Ps(p, "bitrate", "12M") },
            { "windowWidth", Ps(p, "windowWidth", "0") } });
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

    // ── 应用图标：从设备 APK 提取（toybox unzip），本地缓存 ──
    async Task<object> AppIcon(Dictionary<string, object> p)
    {
        string id = Ps(p, "id"), pkg = Ps(p, "pkg");
        if (id.Length == 0 || pkg.Length == 0) return Err("缺参数");
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
