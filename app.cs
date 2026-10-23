using System;
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
    [STAThread]
    static void Main()
    {
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

    public MainForm()
    {
        Text = "水月清影 ScreenZoom";
        Width = 1180; Height = 780;
        MinimumSize = new Size(1020, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 247, 248);

        engineDir = FindEngine();
        if (engineDir != null) { scrcpyExe = Path.Combine(engineDir, "scrcpy.exe"); adbExe = Path.Combine(engineDir, "adb.exe"); }
        Log("启动 engine=" + (engineDir ?? "NULL") + " exe=" + AppDir());

        wv = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(wv);
        Load += async (s, e) => await Init();
    }

    async Task Init()
    {
        Directory.CreateDirectory(DataDir);
        var env = await CoreWebView2Environment.CreateAsync(null, DataDir, null);
        await wv.EnsureCoreWebView2Async(env);
        wv.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        wv.CoreWebView2.Settings.IsStatusBarEnabled = false;
        wv.CoreWebView2.WebMessageReceived += OnMessage;
        wv.CoreWebView2.Navigate(new Uri(Path.Combine(AppDir(), "index.html")).AbsoluteUri);
    }

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
            case "save-reward-qr": return SaveRewardQr();
            case "open-url": return OpenUrl(p);
            case "restore": return await Restore(p);
            case "to-wireless": return await ToWireless(p);
            case "to-wired": return await ToWired(p);
            case "wifi-discover": return await WifiDiscover();
            case "wifi-disconnect": return await WifiDisconnect(p);
            case "wifi-connect": return await WifiConnect(p);
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

    static string EscapeArg(string s)
    {
        // 参数里带引号时必须转义，否则整条命令被外壳拆开，报错文本会被当成结果解析
        if (s.Length == 0) return "\"\"";
        if (!s.Contains(" ") && !s.Contains("\t") && !s.Contains("\"")) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
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
            await Task.WhenAll(tModel, tRel, tWm);
            var m = await tModel; var v = await tRel; var wm = await tWm;
            var d = new Dictionary<string, object> {
                { "id", id },
                { "name", Clean(m.text).Length > 0 ? Clean(m.text) : name },
                { "android", Clean(v.text) },
                { "via", id.Contains(":") ? "wireless" : "usb" }
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
        return Spawn(a);
    }

    async Task<object> LaunchWindow(Dictionary<string, object> p)
    {
        string id = Ps(p, "id");
        // 记下启动前的虚拟屏集合，窗口起来后 diff 出本次新加的 display id
        var before = await ListDisplayIds(id);
        var a = new List<string> { "-s", id, "--new-display=" + Ps(p, "size", "1280x720/240"),
            "--video-bit-rate=" + Ps(p, "bitrate", "12M"), "--keyboard=uhid" };
        string app = Ps(p, "app");
        if (app.Length > 0)
        {
            bool isPkg = System.Text.RegularExpressions.Regex.IsMatch(app, "^[A-Za-z0-9_.]+$") && app.Contains(".");
            a.Add("--start-app=" + (isPkg ? app : "?" + app));
            if (!isPkg) Remember(app);
        }
        var res = Spawn(a);
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
        if (wh.Length != 2) return Err("读不到设备出厂分辨率");
        int pw = int.Parse(wh[0]), ph = int.Parse(wh[1]);
        int dpi0 = (int)info["physicalDpi"];
        int monW = int.Parse(Ps(p, "monW", "1920")), monH = int.Parse(Ps(p, "monH", "1080"));
        string kind = Ps(p, "kind", "factor");
        int w, h, dpi;

        if (kind == "fill")
        {
            await Adb(id, "shell", "wm", "size", monW + "x" + monH);
            await Task.Delay(900);
            var back = await ReadWm(id);
            var aw = ((string)back["currentSize"]).Split('x');
            if (aw.Length != 2) return Err("设备不接受该分辨率");
            double k = Math.Min(int.Parse(aw[0]) / (double)monW, int.Parse(aw[1]) / (double)monH);
            w = (int)Math.Round(monW * k); h = (int)Math.Round(monH * k);
            dpi = (int)Math.Round(dpi0 * (h / (double)ph));
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

        var a = new List<string> { "-s", id, "--video-bit-rate=" + Ps(p, "bitrate", "12M"),
            "--max-fps=" + Ps(p, "fps", "60"), "--keyboard=uhid", "--fullscreen" };
        var res = Spawn(a);
        var dict = res as Dictionary<string, object>;
        if (dict != null && got.Length == 2)
            dict["applied"] = got[0] + "×" + got[1] + " / " + now["currentDpi"];
        return res;
    }

    object Spawn(List<string> a)
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
            int n = 0;
            pr.OutputDataReceived += (s, e) => { if (e.Data != null && n++ < 40) Log("[scrcpy] " + e.Data); };
            pr.ErrorDataReceived += (s, e) => { if (e.Data != null && n++ < 40) Log("[scrcpy] " + e.Data); };
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
            await Adb(id, "shell", "wm", "size", string.IsNullOrEmpty(so) || so == "null" ? "reset" : so);
            await Adb(id, "shell", "wm", "density", string.IsNullOrEmpty(dobj) || dobj == "null" ? "reset" : dobj);
            try { File.Delete(StateFile); } catch { }
        }
        else
        {
            await Adb(id, "shell", "wm", "size", "reset");
            await Adb(id, "shell", "wm", "density", "reset");
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
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 2 && p[1] == "device" && p[0].Contains(":")) list.Add(p[0]);
        }
        return list;
    }

    async Task<object> WifiDiscover()
    {
        var found = new List<object>();
        if (adbExe == null) return found;
        // 已连接的无线设备优先列出：即使 mDNS 没广播也能看到并断开
        var connected = await ConnectedWireless();
        var byIp = new Dictionary<string, WifiDev>();
        foreach (var c in connected)
        {
            var ip = c.Split(':')[0];
            WifiDev dev;
            if (!byIp.TryGetValue(ip, out dev)) { dev = new WifiDev { ip = ip }; byIp[ip] = dev; }
            dev.connAddr = c; dev.connected = true;
        }
        var r = await Run(adbExe, new[] { "mdns", "services" }, 7000);
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
            name = string.Join(" ", tokens.Where(t => t != addr)
                .Select(t => t.Replace("._adb-tls-connect._tcp", "").Replace("._adb-tls-pairing._tcp", ""))
                .Where(t => t.Length > 0));
            var ip = addr.Split(':')[0];
            bool pairing = l.IndexOf("pairing", StringComparison.OrdinalIgnoreCase) >= 0;
            WifiDev dev;
            if (!byIp.TryGetValue(ip, out dev)) { dev = new WifiDev { ip = ip }; byIp[ip] = dev; }
            if (dev.name.Length == 0) dev.name = name;
            if (pairing) { if (dev.pairAddr == null) dev.pairAddr = addr; }
            else if (dev.connAddr == null) dev.connAddr = addr;
        }
        foreach (var dev in byIp.Values)
            found.Add(new Dictionary<string, object> {
                { "addr", dev.connAddr ?? dev.pairAddr ?? dev.ip },
                { "kind", dev.connAddr != null ? "connect" : "pairing" },
                { "connected", dev.connected },
                { "name", dev.name } });
        Log("wifi-discover " + found.Count + " 台");
        return found;
    }

    async Task<object> WifiDisconnect(Dictionary<string, object> p)
    {
        string addr = Ps(p, "addr").Trim();
        var a = new List<string> { "disconnect" };
        if (addr.Length > 0) a.Add(addr);
        var r = await Run(adbExe, a, 10000);
        string t = r.text.Trim();
        bool ok = t.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0 || (r.ok && t.Length == 0);
        return new Dictionary<string, object> { { "ok", ok }, { "text", t.Length > 0 ? t : (addr.Length > 0 ? "已断开 " + addr : "已断开全部无线连接") } };
    }

    class WifiDev { public string ip, name, connAddr, pairAddr; public bool connected; }

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

        // 配对端口和投屏端口不是同一个，配对完要重新发现真正能连的那个
        string target = addr;
        var disc = await WifiDiscover() as List<object>;
        if (disc != null)
        {
            var conn = disc.Select(o => o as Dictionary<string, object>)
                           .FirstOrDefault(o => o != null && Convert.ToString(o["kind"]) == "connect");
            if (conn != null)
            {
                var a = Convert.ToString(conn["addr"]);
                if (!string.IsNullOrEmpty(a)) target = a;
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
        steps.Add(ok ? "已连上 " + target : "连接失败：" + (cr.text.Trim().Length > 0 ? cr.text.Trim() : "没有回应"));
        Log("wifi-connect " + target + " ok=" + ok);
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
            new Dictionary<string, object>{ { "name", "1080P 竖屏" }, { "size", "1080x1920/320" } }
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
            { "id", id }, { "size", size }, { "app", pkg }, { "bitrate", "12M" } });
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
            { "engine", engineDir ?? "" },
            { "license", "GPL-3.0" },
            { "dir", AppDir() }
        };
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
