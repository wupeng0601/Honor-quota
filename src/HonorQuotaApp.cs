using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HonorQuotaApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "HonorQuotaApp.SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private const int HistoryLimitRows = 96;
        private readonly string appDir;
        private readonly string cliPath;
        private readonly string logPath;
        private readonly string historyPath;
        private readonly string appIconPath;
        private readonly NotifyIcon tray;
        private readonly Timer pollTimer;
        private readonly Timer backgroundTimer;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly OcgWebViewSession ocgSession;
        private readonly RelayManager relayManager;
        private Process refreshProcess;
        private Task<bool> ocgRefreshTask;
        private DateTime refreshStartedAt;
        private bool busy;
        private QuotaPopup popup;
        private IDictionary<string, object> lastJson;
        private DateTime? lastUpdated;

        public TrayContext()
        {
            appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            cliPath = Path.Combine(appDir, "honor_quota_cli.py");
            logPath = Path.Combine(appDir, "honor-quota-app.log");
            historyPath = Path.Combine(appDir, "usage_history.json");
            appIconPath = Path.Combine(appDir, "HonorQuota.ico");
            ocgSession = new OcgWebViewSession(appDir, WriteLog);
            relayManager = new RelayManager(appDir, WriteLog);

            tray = new NotifyIcon();
            tray.Icon = LoadAppIcon(Color.FromArgb(35, 99, 235));
            tray.Text = "Honor Quota";
            tray.Visible = true;
            tray.MouseClick += OnTrayMouseClick;
            tray.ContextMenuStrip = BuildMenu();

            pollTimer = new Timer();
            pollTimer.Interval = 200;
            pollTimer.Tick += PollRefresh;

            backgroundTimer = new Timer();
            backgroundTimer.Interval = 60000;
            backgroundTimer.Tick += delegate { StartRefresh(false); };
            backgroundTimer.Start();

            var startupWarm = new Timer();
            startupWarm.Interval = 3500;
            startupWarm.Tick += delegate
            {
                startupWarm.Stop();
                startupWarm.Dispose();
                StartRefresh(false);
            };
            startupWarm.Start();

            EnsureAppIconFile();
            TryCreateStartMenuShortcut();
            StartCatalogSync();
        }

        private void StartCatalogSync()
        {
            Task.Run(delegate
            {
                try
                {
                    var data = GoModelCatalog.LoadData();
                    string status;
                    var count = GoModelCatalog.RefreshOfficialCatalog(data, out status);
                    if (count > 0) GoModelCatalog.SaveData(data);
                    WriteLog("OpenCode Go 目录后台同步：" + status);
                }
                catch (Exception ex)
                {
                    WriteLog("OpenCode Go 目录后台同步失败：" + ex.Message);
                }
            });
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var show = new ToolStripMenuItem("显示并刷新");
            show.Click += delegate { ShowAndRefresh(); };
            var silent = new ToolStripMenuItem("静默刷新");
            silent.Click += delegate { StartRefresh(false); };
            var startup = new ToolStripMenuItem("开机启动") { Checked = IsStartupEnabled(), CheckOnClick = false };
            startup.Click += delegate
            {
                SetStartup(!IsStartupEnabled());
                startup.Checked = IsStartupEnabled();
            };
            var openOcg = new ToolStripMenuItem("OpenCode Go 登录/检查");
            openOcg.Click += async delegate { await ocgSession.ShowLoginWindowAsync(); };
            var modelSettings = new ToolStripMenuItem("OpenCode Go 模型与用量规则...");
            modelSettings.Click += delegate { OpenModelSettings(); };
            var relay = new ToolStripMenuItem(RelayMenuText());
            relay.Click += delegate
            {
                if (relayManager.IsRunning()) relayManager.Stop();
                else if (!relayManager.Start(true)) ShowError(relayManager.LastStatus);
                relay.Text = RelayMenuText();
            };
            var relayHealth = new ToolStripMenuItem("打开欧路 Relay 健康检查");
            relayHealth.Click += delegate { Process.Start("http://127.0.0.1:8787/health"); };
            var shortcut = new ToolStripMenuItem("创建开始菜单快捷方式");
            shortcut.Click += delegate { TryCreateStartMenuShortcut(); OpenStartMenuFolder(); };
            var folder = new ToolStripMenuItem("打开程序目录");
            folder.Click += delegate { Process.Start("explorer.exe", appDir); };
            var exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };

            menu.Items.Add(show);
            menu.Items.Add(silent);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(openOcg);
            menu.Items.Add(modelSettings);
            menu.Items.Add(relay);
            menu.Items.Add(relayHealth);
            menu.Items.Add(shortcut);
            menu.Items.Add(folder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);
            menu.Opening += delegate
            {
                startup.Checked = IsStartupEnabled();
                relay.Text = RelayMenuText();
            };
            return menu;
        }

        private string RelayMenuText()
        {
            return relayManager.IsRunning() ? "停止欧路翻译 Relay (8787)" : "启动欧路翻译 Relay (8787)";
        }

        private void OpenModelSettings()
        {
            using (var settings = new GoModelSettingsFormV2())
            {
                if (settings.ShowDialog() != DialogResult.OK) return;
                if (lastJson != null)
                {
                    EnsurePopup();
                    popup.ShowResult(lastJson, lastUpdated ?? DateTime.Now);
                    popup.ShowNearCursor();
                    popup.AutoCloseAfter(20000);
                }
            }
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowAndRefresh();
        }

        private void ShowAndRefresh()
        {
            EnsurePopup();
            popup.ShowLoading(lastUpdated);
            popup.ShowNearCursor();
            StartRefresh(true);
        }

        private void StartRefresh(bool updatePopup)
        {
            if (busy)
            {
                if (updatePopup && popup != null) popup.ShowNearCursor();
                return;
            }
            if (!File.Exists(cliPath))
            {
                if (updatePopup) ShowError("缺少刷新脚本：" + cliPath);
                return;
            }

            busy = true;
            refreshStartedAt = DateTime.Now;
            try
            {
                ocgRefreshTask = ocgSession.TryUpdateCacheAsync(false, updatePopup);
                ocgRefreshTask.ContinueWith(delegate(Task<bool> task)
                {
                    if (task.IsFaulted && task.Exception != null)
                        WriteLog("Silent OCG refresh skipped: " + task.Exception.GetBaseException().Message);
                });
                var python = GetPythonPath();
                refreshProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = Quote(cliPath) + " --pretty --fast",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = appDir
                });
                pollTimer.Start();
            }
            catch (Exception ex)
            {
                busy = false;
                WriteLog("Start refresh failed: " + ex);
                if (updatePopup) ShowError(ex.Message);
            }
        }

        private void PollRefresh(object sender, EventArgs e)
        {
            if (refreshProcess == null)
            {
                pollTimer.Stop();
                busy = false;
                return;
            }
            var age = DateTime.Now - refreshStartedAt;
            if (!refreshProcess.HasExited)
            {
                if (age.TotalSeconds > 10)
                {
                    try { refreshProcess.Kill(); } catch { }
                    WriteLog("Refresh process timed out.");
                }
                else
                {
                    if (popup != null) popup.TickLoading();
                    return;
                }
            }
            if (ocgRefreshTask != null && !ocgRefreshTask.IsCompleted && age.TotalMilliseconds < 2000)
            {
                if (popup != null) popup.TickLoading();
                return;
            }

            pollTimer.Stop();
            try
            {
                var raw = refreshProcess.StandardOutput.ReadToEnd();
                var err = refreshProcess.StandardError.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(err)) throw new Exception(err.Trim());
                if (string.IsNullOrWhiteSpace(raw)) throw new Exception("刷新进程没有写出结果。");
                var parsed = serializer.DeserializeObject(raw) as IDictionary<string, object>;
                if (parsed == null) throw new Exception("刷新结果格式不正确。");
                if (parsed.ContainsKey("error")) throw new Exception(Convert.ToString(parsed["error"]));
                if (ocgRefreshTask != null && ocgRefreshTask.IsCompleted) ObserveOcgRefreshTask();
                ReplaceOcgProviderFromCache(parsed);
                lastJson = parsed;
                lastUpdated = DateTime.Now;
                AppendHistory(parsed);
                tray.Icon = LoadAppIcon(HasFailures(parsed) ? Color.FromArgb(196, 126, 0) : Color.FromArgb(35, 99, 235));
                if (popup != null && !popup.IsDisposed)
                {
                    popup.ShowResult(parsed, lastUpdated.Value);
                    popup.AutoCloseAfter(20000);
                }
            }
            catch (Exception ex)
            {
                WriteLog("Refresh result failed: " + ex);
                tray.Icon = LoadAppIcon(Color.FromArgb(190, 58, 52));
                if (popup != null && !popup.IsDisposed)
                {
                    popup.ShowError(ex.Message);
                    popup.AutoCloseAfter(20000);
                }
            }
            finally
            {
                try { if (refreshProcess != null) refreshProcess.Dispose(); } catch { }
                refreshProcess = null;
                ocgRefreshTask = null;
                busy = false;
            }
        }

        private void ObserveOcgRefreshTask()
        {
            if (ocgRefreshTask == null) return;
            try
            {
                ocgRefreshTask.Wait(0);
            }
            catch (AggregateException ex)
            {
                WriteLog("Silent OCG refresh skipped: " + ex.GetBaseException().Message);
            }
            catch (Exception ex)
            {
                WriteLog("Silent OCG refresh skipped: " + ex.Message);
            }
        }

        private void ReplaceOcgProviderFromCache(IDictionary<string, object> root)
        {
            try
            {
                var cachePath = Path.Combine(appDir, "opencode_go_cache.json");
                if (!File.Exists(cachePath)) return;
                var cached = serializer.DeserializeObject(File.ReadAllText(cachePath, Encoding.UTF8)) as IDictionary<string, object>;
                if (cached == null || Convert.ToString(Json.Value(cached, "provider")) != "opencode_go") return;
                var providers = new List<object>();
                bool replaced = false;
                foreach (var item in Json.Items(Json.Value(root, "providers")))
                {
                    var provider = item as IDictionary<string, object>;
                    if (provider != null && Convert.ToString(Json.Value(provider, "provider")) == "opencode_go")
                    {
                        providers.Add(cached);
                        replaced = true;
                    }
                    else
                    {
                        providers.Add(item);
                    }
                }
                if (!replaced) providers.Add(cached);
                root["providers"] = providers;
            }
            catch (Exception ex)
            {
                WriteLog("Replace OCG cache failed: " + ex.Message);
            }
        }

        private void EnsurePopup()
        {
            if (popup == null || popup.IsDisposed)
            {
                popup = new QuotaPopup();
            }
        }

        private void ShowError(string message)
        {
            EnsurePopup();
            popup.ShowError(message);
            popup.ShowNearCursor();
            popup.AutoCloseAfter(20000);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private string GetPythonPath()
        {
            var candidates = new List<string>();
            var envPython = Environment.GetEnvironmentVariable("PYTHON");
            if (!string.IsNullOrWhiteSpace(envPython)) candidates.Add(envPython.Trim());
            candidates.Add("pyx.exe");
            candidates.Add("python.exe");
            candidates.Add("py.exe");
            candidates.Add(@"C:\Python314\python.exe");
            foreach (var candidate in candidates)
            {
                if (candidate.IndexOf(Path.DirectorySeparatorChar) >= 0)
                {
                    if (File.Exists(candidate)) return candidate;
                }
                else
                {
                    var resolved = FindOnPath(candidate);
                    if (resolved != null) return resolved;
                }
            }
            return "python.exe";
        }

        private static string FindOnPath(string exeName)
        {
            try
            {
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
                foreach (var dir in paths)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
            return null;
        }

        private bool HasFailures(IDictionary<string, object> root)
        {
            var providers = Json.Items(Json.Value(root, "providers"));
            bool any = false;
            foreach (var item in providers)
            {
                any = true;
                var p = item as IDictionary<string, object>;
                if (p == null || !Convert.ToBoolean(Json.Value(p, "ok"))) return true;
            }
            return !any;
        }

        private void AppendHistory(IDictionary<string, object> root)
        {
            try
            {
                var rows = LoadHistory();
                var codex = Json.Provider(root, "codex");
                var ocg = Json.Provider(root, "opencode_go");
                var ds = Json.Provider(root, "deepseek");
                var row = new Dictionary<string, object>();
                row["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                row["codex_primary_used"] = Json.Double(Json.Window(codex, "primary"), "used_percent", 0);
                row["codex_weekly_used"] = Json.Double(Json.Window(codex, "secondary"), "used_percent", 0);
                row["ocg_primary_used"] = Json.Double(Json.Window(ocg, "primary"), "used_percent", 0);
                row["ocg_weekly_used"] = Json.Double(Json.Window(ocg, "secondary"), "used_percent", 0);
                row["ocg_monthly_used"] = Json.Double(Json.Window(ocg, "monthly"), "used_percent", 0);
                row["deepseek_balance"] = Json.DeepSeekBalanceNumber(ds);
                rows.Add(row);
                TrimHistory(rows);
                File.WriteAllText(historyPath, serializer.Serialize(rows), Encoding.UTF8);
            }
            catch (Exception ex) { WriteLog("Append history failed: " + ex.Message); }
        }

        private List<IDictionary<string, object>> LoadHistory()
        {
            var rows = new List<IDictionary<string, object>>();
            try
            {
                if (!File.Exists(historyPath)) return rows;
                var parsed = serializer.DeserializeObject(File.ReadAllText(historyPath, Encoding.UTF8));
                foreach (var item in Json.Items(parsed))
                {
                    var row = item as IDictionary<string, object>;
                    if (row != null) rows.Add(new Dictionary<string, object>(row));
                }
                TrimHistory(rows);
            }
            catch { }
            return rows;
        }

        private static void TrimHistory(List<IDictionary<string, object>> rows)
        {
            if (rows.Count > HistoryLimitRows)
                rows.RemoveRange(0, rows.Count - HistoryLimitRows);
        }

        private Icon BuildTrayIcon(Color color)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new LinearGradientBrush(new Rectangle(2, 2, 28, 28), Color.FromArgb(18, 24, 34), Color.FromArgb(5, 12, 22), 45F))
                    g.FillEllipse(bg, 2, 2, 28, 28);
                using (var shadow = new Pen(Color.FromArgb(130, 0, 0, 0), 2F))
                    g.DrawEllipse(shadow, 3, 3, 26, 26);
                using (var cyan = new Pen(Color.FromArgb(54, 211, 238), 2.2F))
                    g.DrawArc(cyan, 5, 5, 22, 22, 205, 250);
                using (var gold = new Pen(Color.FromArgb(246, 185, 72), 2F))
                    g.DrawArc(gold, 3, 3, 26, 26, 292, 88);
                using (var red = new Pen(Color.FromArgb(239, 68, 68), 1.8F))
                    g.DrawArc(red, 3, 3, 26, 26, 76, 54);
                using (var core = new SolidBrush(Color.FromArgb(96, 225, 255)))
                    g.FillEllipse(core, 13, 13, 6, 6);
                using (var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(255, 223, 146)))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("H", font, brush, new RectangleF(2, 0, 28, 28), fmt);
                using (var status = new SolidBrush(color))
                    g.FillEllipse(status, 23, 23, 6, 6);
                using (var statusBorder = new Pen(Color.FromArgb(245, 248, 252), 1F))
                    g.DrawEllipse(statusBorder, 23, 23, 6, 6);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private Icon LoadAppIcon(Color fallbackStatus)
        {
            try
            {
                if (File.Exists(appIconPath)) return new Icon(appIconPath);
            }
            catch { }
            return BuildTrayIcon(fallbackStatus);
        }

        private void EnsureAppIconFile()
        {
            try
            {
                if (File.Exists(appIconPath)) return;
                using (var icon = BuildTrayIcon(Color.FromArgb(54, 211, 238)))
                using (var fs = File.Create(appIconPath))
                    icon.Save(fs);
            }
            catch (Exception ex) { WriteLog("Create app icon failed: " + ex.Message); }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    var value = key == null ? null : Convert.ToString(key.GetValue("HonorQuota"));
                    return value != null && value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        private void SetStartup(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enabled) key.SetValue("HonorQuota", Quote(Application.ExecutablePath));
                    else key.DeleteValue("HonorQuota", false);
                }
            }
            catch (Exception ex) { WriteLog("Set startup failed: " + ex); }
        }

        private void TryCreateStartMenuShortcut()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Honor Quota");
                Directory.CreateDirectory(dir);
                var lnk = Path.Combine(dir, "Honor Quota.lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = shortcut.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { appDir });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Honor Quota tray app" });
                st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { File.Exists(appIconPath) ? appIconPath : Application.ExecutablePath + ",0" });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { WriteLog("Create shortcut failed: " + ex); }
        }

        private void OpenStartMenuFolder()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Honor Quota");
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", dir);
        }

        private void WriteLog(string message)
        {
            try { File.AppendAllText(logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        private void ExitApp()
        {
            try
            {
                pollTimer.Stop();
                backgroundTimer.Stop();
          if (refreshProcess != null && !refreshProcess.HasExited) refreshProcess.Kill();
          if (relayManager != null) relayManager.StopIfStartedByThisApp();
          if (ocgSession != null) ocgSession.Dispose();
                if (popup != null && !popup.IsDisposed) popup.Close();
                tray.Visible = false;
                tray.Dispose();
                backgroundTimer.Dispose();
            }
            catch { }
            Application.Exit();
        }
    }

    internal sealed class RelayManager
    {
        private readonly string relayDir;
        private readonly string startScript;
        private readonly string stopScript;
        private readonly Action<string> log;
        private Process process;
        private bool startedByThisApp;
        public string LastStatus { get; private set; }

        public RelayManager(string appDir, Action<string> log)
        {
            relayDir = Path.Combine(appDir, "opencode-go-relay");
            startScript = Path.Combine(relayDir, "start-relay.ps1");
            stopScript = Path.Combine(relayDir, "stop-relay.ps1");
            this.log = log;
            LastStatus = "Relay not checked.";
        }

        public bool IsRunning()
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(IPAddress.Loopback, 8787);
                    return task.Wait(250) && client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        public bool Start(bool manual)
        {
            if (IsRunning())
            {
                LastStatus = "Relay is already listening on 127.0.0.1:8787.";
                return true;
            }
            if (!File.Exists(startScript))
            {
                LastStatus = "缺少 relay 启动脚本：" + startScript;
                log(LastStatus);
                return false;
            }
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(startScript) + " -PromptForKey 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = relayDir
                });
                startedByThisApp = true;
                LastStatus = "Relay start requested on 127.0.0.1:8787.";
                log(LastStatus);
                return true;
            }
            catch (Exception ex)
            {
                LastStatus = "启动 relay 失败：" + ex.Message;
                log(LastStatus);
                return false;
            }
        }

        public void Stop()
        {
            if (!File.Exists(stopScript))
            {
                LastStatus = "缺少 relay 停止脚本：" + stopScript;
                log(LastStatus);
                return;
            }
            try
            {
                using (var stopper = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(stopScript),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = relayDir
                }))
                {
                    if (stopper != null) stopper.WaitForExit(4000);
                }
                startedByThisApp = false;
                LastStatus = "Relay stop requested.";
                log(LastStatus);
            }
            catch (Exception ex)
            {
                LastStatus = "停止 relay 失败：" + ex.Message;
                log(LastStatus);
            }
        }

        public void StopIfStartedByThisApp()
        {
            if (!startedByThisApp) return;
            try { Stop(); } catch { }
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class OcgWebViewSession : IDisposable
    {
        private readonly string appDir;
        private readonly string cachePath;
        private readonly string userDataFolder;
        private readonly Action<string> log;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private Form form;
        private WebView2 web;
        private Task initTask;
        private bool disposed;

        public OcgWebViewSession(string appDir, Action<string> log)
        {
            this.appDir = appDir;
            this.log = log;
            cachePath = Path.Combine(appDir, "opencode_go_cache.json");
            userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorQuota", "WebView2");
        }

        public async Task ShowLoginWindowAsync()
        {
            await EnsureAsync();
            if (disposed || form == null || web == null) return;
            form.Opacity = 1;
            form.ShowInTaskbar = true;
            form.WindowState = FormWindowState.Normal;
            form.Size = new Size(1120, 820);
            form.StartPosition = FormStartPosition.CenterScreen;
            form.Location = new Point(Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Width - form.Width) / 2), Math.Max(0, (Screen.PrimaryScreen.WorkingArea.Height - form.Height) / 2));
            form.Show();
            form.Activate();
            if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(GetOpenCodeUrl());
        }

        public async Task<bool> TryUpdateCacheAsync(bool showIfLoginNeeded, bool forceRefresh)
        {
            await EnsureAsync();
            if (disposed || web == null || web.CoreWebView2 == null) return false;
            if (!forceRefresh && IsFreshHiddenCache(TimeSpan.FromSeconds(30))) return true;
            var url = GetOpenCodeUrl();
            try
            {
                web.CoreWebView2.Navigate(url);
            }
            catch { web.CoreWebView2.Navigate(url); }

            string text = "";
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(250);
                text = await ReadBodyTextAsync();
                if (HasUsageText(text)) break;
            }
            if (!HasUsageText(text))
            {
                try
                {
                    var snippet = string.IsNullOrEmpty(text) ? "<empty>" : text.Substring(0, Math.Min(180, text.Length)).Replace("\r", " ").Replace("\n", " ");
                    log("OCG hidden WebView usage text not found. Url=" + (web.Source == null ? "" : web.Source.ToString()) + " Text=" + snippet);
                }
                catch { }
                if (showIfLoginNeeded) await ShowLoginWindowAsync();
                return false;
            }
            WriteCache(text, url);
            HideWindow();
            return true;
        }

        private bool IsFreshHiddenCache(TimeSpan maxAge)
        {
            try
            {
                if (!File.Exists(cachePath)) return false;
                var parsed = serializer.DeserializeObject(File.ReadAllText(cachePath, Encoding.UTF8)) as IDictionary<string, object>;
                if (parsed == null) return false;
                if (Convert.ToString(Json.Value(parsed, "source")) != "hidden_webview") return false;
                var raw = Convert.ToString(Json.Value(parsed, "cached_at"));
                DateTime cachedAt;
                if (!DateTime.TryParse(raw, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out cachedAt)) return false;
                return DateTime.UtcNow - cachedAt.ToUniversalTime() <= maxAge;
            }
            catch { return false; }
        }

        private async Task EnsureAsync()
        {
            if (initTask == null) initTask = InitializeAsync();
            await initTask;
        }

        private async Task InitializeAsync()
        {
            Directory.CreateDirectory(userDataFolder);
            form = new Form();
            form.Text = "Honor Quota - OpenCode Go";
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Size = new Size(900, 700);
            form.Opacity = 0;
            form.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideWindow();
                }
            };
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            form.Controls.Add(web);
            form.Show();
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            web.CoreWebView2.Navigate(GetOpenCodeUrl());
        }

        private async Task<string> ReadBodyTextAsync()
        {
            try
            {
                var raw = await web.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                return serializer.Deserialize<string>(raw) ?? "";
            }
            catch { return ""; }
        }

        private bool HasUsageText(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Contains("滚动用量") && text.Contains("每周用量") && text.Contains("每月用量");
        }

        private void WriteCache(string text, string url)
        {
            var primary = ParseWindow(text, "滚动用量", 12.0);
            var secondary = ParseWindow(text, "每周用量", 30.0);
            var monthly = ParseWindow(text, "每月用量", 60.0);
            if (primary == null && secondary == null && monthly == null) return;
            var data = new Dictionary<string, object>();
            data["provider"] = "opencode_go";
            data["ok"] = true;
            data["source"] = "hidden_webview";
            data["workspace_id"] = WorkspaceFromUrl(url);
            data["cached_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            data["primary"] = primary;
            data["secondary"] = secondary;
            data["monthly"] = monthly;
            File.WriteAllText(cachePath, serializer.Serialize(data), Encoding.UTF8);
        }

        private Dictionary<string, object> ParseWindow(string text, string label, double limit)
        {
            var match = Regex.Match(text, Regex.Escape(label) + @"\s+([0-9]+(?:\.[0-9]+)?)%\s+重置于\s*([^\r\n]+)", RegexOptions.Singleline);
            if (!match.Success) return null;
            var used = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var remaining = Math.Max(0, Math.Min(100, 100 - used));
            var data = new Dictionary<string, object>();
            data["used_percent"] = used;
            data["reset_description"] = match.Groups[2].Value.Trim();
            data["remaining_percent"] = Math.Round(remaining, 2);
            data["limit_usd"] = limit;
            data["remaining_usd"] = Math.Round(limit * remaining / 100.0, 2);
            return data;
        }

        private string GetOpenCodeUrl()
        {
            var workspace = Environment.GetEnvironmentVariable("OPENCODE_GO_WORKSPACE_ID", EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(workspace))
                workspace = Environment.GetEnvironmentVariable("CODEXBAR_OPENCODEGO_WORKSPACE_ID", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(workspace) && workspace.StartsWith("wrk_", StringComparison.OrdinalIgnoreCase))
                return "https://opencode.ai/workspace/" + workspace.Trim() + "/go";
            return "https://opencode.ai/go";
        }

        private string WorkspaceFromUrl(string url)
        {
            var match = Regex.Match(url ?? "", @"wrk_[A-Za-z0-9_-]+");
            return match.Success ? match.Value : "";
        }

        private void HideWindow()
        {
            if (form == null || form.IsDisposed) return;
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Location = new Point(-32000, -32000);
            form.Size = new Size(1, 1);
        }

        public void Dispose()
        {
            disposed = true;
            try { if (form != null && !form.IsDisposed) form.Close(); } catch { }
            try { if (web != null) web.Dispose(); } catch { }
        }
    }

    internal sealed class QuotaPopup : Form
    {
        private readonly Timer closeTimer = new Timer();
        private readonly Timer loadingTimer = new Timer();
        private readonly Panel body = new Panel();
        private readonly Label title = new Label();
        private readonly Label updated = new Label();
        private readonly WebView2 view = new WebView2();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private Task viewInitTask;
        private string pendingHtml;
        private int loadingTick;

        public QuotaPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(248, 250, 252);
            Size = new Size(720, 840);
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Microsoft YaHei UI", 9F);
            Padding = new Padding(0);

            title.Visible = false;
            title.Text = "Honor Quota";
            title.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            title.ForeColor = Ui.Text;
            title.Location = new Point(20, 14);
            title.Size = new Size(180, 28);
            Controls.Add(title);

            updated.Visible = false;
            updated.ForeColor = Ui.Muted;
            updated.TextAlign = ContentAlignment.MiddleRight;
            updated.Location = new Point(315, 18);
            updated.Size = new Size(210, 22);
            Controls.Add(updated);

            body.Location = new Point(0, 0);
            body.Size = new Size(720, 840);
            body.Dock = DockStyle.Fill;
            body.BackColor = Color.FromArgb(248, 250, 252);
            view.Dock = DockStyle.Fill;
            view.DefaultBackgroundColor = Color.FromArgb(248, 250, 252);
            body.Controls.Add(view);
            Controls.Add(body);
            viewInitTask = InitializeViewAsync();

            closeTimer.Interval = 20000;
            closeTimer.Tick += OnCloseTimerTick;
            loadingTimer.Interval = 180;
            loadingTimer.Tick += delegate { loadingTick++; };
            Deactivate += delegate { if (!IsCursorOverPopup()) Close(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetRoundedRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Ui.Border))
                e.Graphics.DrawPath(pen, RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 14));
        }

        public void ShowNearCursor()
        {
            var cursor = Cursor.Position;
            var area = Screen.FromPoint(cursor).WorkingArea;
            var targetWidth = Math.Min(720, Math.Max(520, area.Width - 16));
            var targetHeight = Math.Min(840, Math.Max(500, area.Height - 16));
            if (Width != targetWidth || Height != targetHeight) Size = new Size(targetWidth, targetHeight);
            var x = Math.Max(area.Left + 8, Math.Min(cursor.X - Width / 2, area.Right - Width - 8));
            var y = Math.Max(area.Top + 4, Math.Min(cursor.Y - Height - 18, area.Bottom - Height - 8));
            Location = new Point(x, y);
            Show();
            Activate();
        }

        public void ShowLoading(DateTime? lastUpdated)
        {
            closeTimer.Stop();
            updated.Text = lastUpdated.HasValue ? "上次 " + lastUpdated.Value.ToString("HH:mm:ss") : "准备刷新";
            loadingTick = 0;
            loadingTimer.Start();
            SetHtml(BuildLoadingHtml(lastUpdated));
        }

        public void TickLoading()
        {
            if (!loadingTimer.Enabled) loadingTimer.Start();
        }

        public void ShowResult(IDictionary<string, object> root, DateTime refreshedAt)
        {
            loadingTimer.Stop();
            updated.Text = "更新 " + refreshedAt.ToString("HH:mm:ss");
            var codex = Json.Provider(root, "codex");
            var ocg = Json.Provider(root, "opencode_go");
            var ds = Json.Provider(root, "deepseek");
            SetHtml(BuildResultHtml(codex, ocg, ds, refreshedAt));
        }

        public void ShowError(string message)
        {
            loadingTimer.Stop();
            updated.Text = "失败";
            SetHtml(BuildErrorHtml(message));
        }

        public void AutoCloseAfter(int milliseconds)
        {
            closeTimer.Stop();
            closeTimer.Interval = milliseconds;
            closeTimer.Start();
        }

        private void OnCloseTimerTick(object sender, EventArgs e)
        {
            closeTimer.Stop();
            if (IsCursorOverPopup())
            {
                closeTimer.Start();
                return;
            }
            Close();
        }

        private bool IsCursorOverPopup()
        {
            return !IsDisposed && Visible && Bounds.Contains(Cursor.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closeTimer.Dispose();
                loadingTimer.Dispose();
                view.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task InitializeViewAsync()
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorQuota", "PopupWebView2");
                Directory.CreateDirectory(folder);
                var env = await CoreWebView2Environment.CreateAsync(null, folder);
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                if (!string.IsNullOrEmpty(pendingHtml)) view.CoreWebView2.NavigateToString(pendingHtml);
            }
            catch
            {
                body.Controls.Clear();
                AddLabel(body, "WebView2 初始化失败", 24, 24, 360, 30, new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), Ui.Bad, ContentAlignment.MiddleLeft);
                AddLabel(body, "请确认 Microsoft Edge WebView2 Runtime 可用。", 24, 60, 420, 26, new Font("Microsoft YaHei UI", 9F), Ui.Muted, ContentAlignment.MiddleLeft);
            }
        }

        private void SetHtml(string html)
        {
            pendingHtml = html;
            try
            {
                if (view.CoreWebView2 != null) view.CoreWebView2.NavigateToString(html);
            }
            catch { }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.WebMessageAsJson;
                if (!string.IsNullOrEmpty(raw) && raw.StartsWith("\"", StringComparison.Ordinal)) raw = serializer.Deserialize<string>(raw);
                var message = serializer.DeserializeObject(raw) as IDictionary<string, object>;
                if (!string.Equals(Convert.ToString(Json.Value(message, "type")), "model_order", StringComparison.Ordinal)) return;
                var ids = new List<string>();
                foreach (var item in Json.Items(Json.Value(message, "ids")))
                {
                    var id = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                }
                GoModelCatalog.SaveDisplayOrder(ids);
            }
            catch { }
        }

        private static string BuildLoadingHtml(DateTime? lastUpdated)
        {
            var subtitle = lastUpdated.HasValue ? "上次同步 " + lastUpdated.Value.ToString("HH:mm:ss") : "正在建立本地会话";
            var bodyHtml =
                "<div class='loading'>" +
                "<div class='loader-ring'></div>" +
                "<div><div class='loading-title'>正在刷新余额和限额</div>" +
                "<div class='loading-sub'>" + H(subtitle) + " · 后台读取 Codex / OpenCode Go / DeepSeek</div></div>" +
                "</div>" +
                "<div class='skeleton-grid'><div></div><div></div><div></div></div>" +
                "<div class='skeleton-wide'></div><div class='skeleton-wide small'></div>";
            return HtmlShell(bodyHtml);
        }

        private static string BuildErrorHtml(string message)
        {
            var bodyHtml =
                "<div class='header'>" + BrandHtml("刷新失败") + "<span class='pill bad'>Error</span></div>" +
                "<div class='error-card'><div class='error-title'>数据读取失败</div><div class='error-text'>" + H(message) + "</div></div>";
            return HtmlShell(bodyHtml);
        }

        private static string BrandHtml(string subtitle)
        {
            var logo = LogoDataUri();
            var mark = string.IsNullOrEmpty(logo)
                ? "<div class='app-mark-fallback'><span>H</span></div>"
                : "<img class='app-mark-img' src='" + logo + "' alt=''>";
            return "<div class='brand'>" + mark + "<div><div class='app-title'>Honor Quota</div><div class='app-sub'>" + H(subtitle) + "</div></div></div>";
        }

        private static string LogoDataUri()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HonorQuota.logo.png");
                if (!File.Exists(path)) return "";
                return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch { return ""; }
        }

        private static string BuildResultHtml(IDictionary<string, object> codex, IDictionary<string, object> ocg, IDictionary<string, object> ds, DateTime refreshedAt)
        {
            var sb = new StringBuilder();
            var codexPrimary = Json.Window(codex, "primary");
            var codexWeekly = Json.Window(codex, "secondary");
            var ocgPrimary = Json.Window(ocg, "primary");
            var ocgWeekly = Json.Window(ocg, "secondary");
            var ocgMonthly = Json.Window(ocg, "monthly");

            double codex5Used = Json.Double(codexPrimary, "used_percent", 0);
            double codexWeekUsed = Json.Double(codexWeekly, "used_percent", 0);
            double ocg5Used = Json.Double(ocgPrimary, "used_percent", 0);
            double ocgWeekUsed = Json.Double(ocgWeekly, "used_percent", 0);
            double ocgMonthUsed = Json.Double(ocgMonthly, "used_percent", 0);
            double ocg5Remain = Json.RemainingPercent(ocgPrimary);
            double ocgWeekRemain = Json.RemainingPercent(ocgWeekly);
            double ocgMonthRemain = Json.RemainingPercent(ocgMonthly);

            sb.Append("<div class='header'>");
            sb.Append(BrandHtml("本地托盘限额面板 · 点击时刷新"));
            sb.Append("<div class='header-right'><span class='pill'>").Append(H(Json.Freshness(ocg))).Append("</span><div class='updated'>更新 ").Append(H(refreshedAt.ToString("HH:mm:ss"))).Append("</div></div>");
            sb.Append("</div>");

            sb.Append("<div class='summary-grid'>");
            sb.Append(SummaryTileHtml("Codex", new[] { "week 已用 " + Pct(codexWeekUsed), "5h 已用 " + Pct(codex5Used) }, CodexSub(codex), RemainingFromUsed(codexWeekUsed), "#2563eb"));
            sb.Append(SummaryTileHtml("OpenCode Go", new[] { "week 剩余 " + Pct(ocgWeekRemain), "5h 剩余 " + Pct(ocg5Remain), "month 剩余 " + Pct(ocgMonthRemain) }, "Go 订阅 · 额度见下方", ocgWeekRemain, "#059669"));
            sb.Append(SummaryTileHtml("DeepSeek", DeepSeekSummaryLines(ds), "官方余额 API", 100, "#7c3aed"));
            sb.Append("</div>");

            sb.Append("<div class='section-title'><span>OpenCode Go 限额窗口</span><em>额度独立列出</em></div>");
            sb.Append("<div class='window-grid'>");
            sb.Append(WindowCardHtml("5h 限额", ocgPrimary, "短窗口", "#059669"));
            sb.Append(WindowCardHtml("周限额", ocgWeekly, "每周窗口", "#2563eb"));
            sb.Append(WindowCardHtml("月限额", ocgMonthly, "每月窗口", "#f59e0b"));
            sb.Append("</div>");

            sb.Append("<div class='model-card card'>");
            sb.Append("<div class='card-head'><div><div class='kicker'>ESTIMATE</div><div class='card-title'>已选模型 · 典型请求估算</div></div><div class='model-tools'><span class='chip'>可在托盘菜单调整</span><span class='drag-hint'>⠿ 拖动卡片调整顺序</span></div></div>");
            sb.Append("<div class='model-grid'>");
            sb.Append(ModelCardsHtml(ocgPrimary, ocgWeekly, ocgMonthly));
            sb.Append("</div>");
            sb.Append("</div>");

            return HtmlShell(sb.ToString());
        }

        private static string SummaryTileHtml(string title, string[] lines, string sub, double remainPercent, string color)
        {
            var value = new StringBuilder();
            foreach (var line in lines)
                value.Append("<span>").Append(H(line)).Append("</span>");
            return "<div class='summary-card card' style='--accent:" + color + ";--p:" + Clamp(remainPercent).ToString("0.##", CultureInfo.InvariantCulture) + "'>" +
                "<div><div class='summary-title'>" + H(title) + "</div><div class='summary-value'>" + value + "</div><div class='summary-sub'>" + H(sub) + "</div></div>" +
                RingHtml(remainPercent, color) + "</div>";
        }

        private static string WindowCardHtml(string title, IDictionary<string, object> window, string caption, string color)
        {
            double remain = Json.RemainingPercent(window);
            double used = Json.Double(window, "used_percent", 0);
            var reset = Convert.ToString(Json.Value(window, "reset_description"));
            return "<div class='limit-card card' style='--accent:" + color + ";--p:" + Clamp(remain).ToString("0.##", CultureInfo.InvariantCulture) + "'>" +
                "<div class='limit-top'><div><div class='kicker'>" + H(caption) + "</div><div class='limit-title'>" + H(title) + "</div></div>" + RingHtml(remain, color) + "</div>" +
                "<div class='metric-row'><span>剩余</span><b>" + MoneyPercent(window) + "</b></div>" +
                "<div class='metric-row muted'><span>已用</span><b>" + Pct(used) + "</b></div>" +
                BarHtml(remain, color) +
                "<div class='reset'>重置 " + H(string.IsNullOrWhiteSpace(reset) ? "--" : reset) + "</div>" +
                "</div>";
        }

        private static string ModelCardsHtml(IDictionary<string, object> primary, IDictionary<string, object> weekly, IDictionary<string, object> monthly)
        {
            var rows = GoModelCatalog.EnabledModels();
            double p = Json.RemainingPercent(primary);
            double w = Json.RemainingPercent(weekly);
            double m = Json.RemainingPercent(monthly);
            var sb = new StringBuilder();
            int order = 0;
            foreach (var item in rows)
            {
                sb.Append("<div class='model-tile' draggable='true' data-id='").Append(H(item.Id)).Append("' data-order='").Append(order++).Append("'><div class='model-name'>").Append(H(item.Name)).Append("</div><div class='model-id'>").Append(H(item.Id)).Append("</div><div class='model-metrics'>");
                sb.Append(ModelMetricHtml("5h", p, item.HasEstimate ? item.FiveHour * p / 100.0 : -1, "#059669"));
                sb.Append(ModelMetricHtml("周", w, item.HasEstimate ? item.Weekly * w / 100.0 : -1, "#2563eb"));
                sb.Append(ModelMetricHtml("月", m, item.HasEstimate ? item.Monthly * m / 100.0 : -1, "#f59e0b"));
                sb.Append("</div></div>");
            }
            return sb.ToString();
        }

        private static string ModelMetricHtml(string label, double percent, double requests, string color)
        {
            var count = requests < 0 ? "未配置" : N0(requests);
            return "<div class='model-metric'><div class='mini-ring' style='--accent:" + color + ";--p:" + Clamp(percent).ToString("0.##", CultureInfo.InvariantCulture) + "'><span>" + Pct(percent) + "</span></div><div class='mini-label'>" + H(label) + "</div><div class='mini-count'>" + count + "</div></div>";
        }

        private static string RingHtml(double remainPercent, string color)
        {
            return "<div class='ring' style='--accent:" + color + ";--p:" + Clamp(remainPercent).ToString("0.##", CultureInfo.InvariantCulture) + "'><span><b>" + Pct(remainPercent) + "</b><small>余</small></span></div>";
        }

        private static string BarHtml(double remainPercent, string color)
        {
            return "<div class='bar'><i style='width:" + Clamp(remainPercent).ToString("0.##", CultureInfo.InvariantCulture) + "%;background:" + color + "'></i></div>";
        }

        private static string MoneyPercent(IDictionary<string, object> window)
        {
            if (window == null) return "n/a";
            if (Json.Value(window, "remaining_usd") != null)
                return "$" + Json.Double(window, "remaining_usd", 0).ToString("N2", CultureInfo.InvariantCulture) + " · " + Pct(Json.RemainingPercent(window));
            return Pct(Json.RemainingPercent(window));
        }

        private static string[] DeepSeekSummaryLines(IDictionary<string, object> provider)
        {
            var balance = Json.DeepSeekBalance(provider);
            var parts = (balance ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return new[] { parts[0], parts[1] };
            return new[] { balance };
        }

        private static double RemainingFromUsed(double used)
        {
            return Clamp(100.0 - used);
        }

        private static string Pct(double value)
        {
            return Clamp(value).ToString("N0", CultureInfo.InvariantCulture) + "%";
        }

        private static string N0(double value)
        {
            return Math.Max(0, value).ToString("N0", CultureInfo.InvariantCulture);
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Max(0, Math.Min(100, value));
        }

        private static string H(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }

        private static string HtmlShell(string bodyHtml)
        {
            return "<!doctype html><html><head><meta charset='utf-8'><style>" + Css() + CssExtras() + "</style></head><body><main>" + bodyHtml + "</main><script>" + DragSortScript() + "</script></body></html>";
        }

        private static string CssExtras()
        {
            return ".model-tools{display:flex;align-items:center;gap:8px}.drag-hint{display:inline-flex;align-items:center;height:25px;padding:0 9px;border:1px dashed #b8c9ee;border-radius:8px;background:#f7f9ff;color:#5572b8;font-size:10px;font-weight:750}.model-tile{cursor:grab;user-select:none;transition:transform .15s ease,opacity .15s ease,border-color .15s ease,box-shadow .15s ease}.model-tile:active{cursor:grabbing}.model-tile.dragging{opacity:.4;transform:scale(.985)}.model-tile.drag-over{border-color:#6d8fe9;box-shadow:0 0 0 3px rgba(109,143,233,.16)}@media(max-width:620px){.model-tools{align-items:flex-end;flex-direction:column;gap:4px}.model-tools .chip{display:none}}";
        }

        private static string DragSortScript()
        {
            return "(function(){var grid=document.querySelector('.model-grid');if(!grid)return;var dragged=null;var changed=false;function card(e){return e.target&&e.target.closest?e.target.closest('.model-tile'):null;}function clear(){Array.prototype.forEach.call(grid.querySelectorAll('.model-tile'),function(x){x.classList.remove('dragging','drag-over');});}function send(){if(!changed)return;var ids=Array.prototype.map.call(grid.querySelectorAll('.model-tile'),function(x){return x.getAttribute('data-id');});window.chrome.webview.postMessage(JSON.stringify({type:'model_order',ids:ids}));changed=false;}grid.addEventListener('dragstart',function(e){dragged=card(e);if(!dragged)return;e.dataTransfer.effectAllowed='move';e.dataTransfer.setData('text/plain',dragged.getAttribute('data-id'));dragged.classList.add('dragging');});grid.addEventListener('dragover',function(e){e.preventDefault();if(!dragged)return;var target=card(e);if(!target||target===dragged)return;var rect=target.getBoundingClientRect();var before=e.clientY<rect.top+rect.height/2;if(before)grid.insertBefore(dragged,target);else grid.insertBefore(dragged,target.nextSibling);clear();dragged.classList.add('dragging');target.classList.add('drag-over');changed=true;});grid.addEventListener('dragend',function(){clear();send();dragged=null;});})();";
        }

        private static string Css()
        {
            return @"
 *{box-sizing:border-box} html,body{margin:0;width:100%;height:100%;overflow:auto;background:#f6f8fb;color:#111827;font-family:'Microsoft YaHei UI','Segoe UI',Arial,sans-serif;-webkit-font-smoothing:antialiased;text-rendering:optimizeLegibility} body{background:
radial-gradient(circle at 12% -12%,rgba(34,211,238,.16),transparent 30%),
radial-gradient(circle at 88% -6%,rgba(245,158,11,.15),transparent 28%),
linear-gradient(180deg,#fbfdff 0%,#f6f8fb 64%,#eef2f7 100%)} main{padding:14px}
.header{display:flex;align-items:flex-start;justify-content:space-between;margin-bottom:8px}.brand{display:flex;align-items:center;gap:11px}.app-mark-img{width:58px;height:58px;border-radius:50%;object-fit:cover;filter:drop-shadow(0 10px 22px rgba(34,211,238,.26)) drop-shadow(0 3px 8px rgba(245,158,11,.22))}.app-mark-fallback{width:58px;height:58px;border-radius:50%;display:grid;place-items:center;background:conic-gradient(#22d3ee,#f59e0b,#ef4444,#22d3ee);color:#111827;font-size:22px;font-weight:900}.app-title{font-size:21px;line-height:25px;font-weight:800;letter-spacing:0}.app-sub{margin-top:2px;color:#667085;font-size:12px}.header-right{text-align:right}.updated{margin-top:4px;color:#64748b;font-size:12px}.pill,.chip{display:inline-flex;align-items:center;border:1px solid #dbe7ff;background:#eef5ff;color:#1d4ed8;border-radius:999px;padding:4px 9px;font-size:11px;font-weight:700}.pill.bad{border-color:#fecaca;background:#fff1f2;color:#b91c1c}
.card{background:rgba(255,255,255,.94);border:1px solid #e3e8f0;border-radius:15px;box-shadow:0 14px 30px rgba(15,23,42,.08),0 2px 7px rgba(15,23,42,.04)}.summary-grid,.window-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px}.summary-card{min-height:99px;padding:10px;display:flex;justify-content:space-between;gap:8px;border-top:3px solid var(--accent)}.summary-card>div:first-child{min-width:0}.summary-title{font-size:12px;color:#475467;font-weight:700}.summary-value{margin-top:5px;display:flex;flex-direction:column;gap:0;font-size:14.5px;font-weight:850;line-height:17px;color:#101828}.summary-value span{white-space:nowrap}.summary-sub{margin-top:4px;font-size:11px;line-height:13px;color:#667085;white-space:normal;overflow:visible;max-width:166px}
.section-title{display:flex;align-items:center;justify-content:space-between;margin:9px 2px 6px}.section-title span{font-size:13px;font-weight:800;color:#111827}.section-title em{font-style:normal;font-size:11px;color:#667085}.limit-card{min-height:103px;padding:10px}.limit-top,.card-head{display:flex;align-items:flex-start;justify-content:space-between;gap:8px}.kicker{font-size:10px;font-weight:800;color:#718096;letter-spacing:.08em}.limit-title,.card-title{margin-top:2px;font-size:14px;font-weight:800;color:#101828}.metric-row{display:flex;align-items:center;justify-content:space-between;margin-top:5px;font-size:12px;color:#667085}.metric-row b{font-size:13px;color:#111827}.metric-row.muted{margin-top:2px}.reset{margin-top:5px;font-size:11px;color:#667085;white-space:normal;line-height:13px}.bar{height:5px;margin-top:6px;background:#e8edf4;border-radius:999px;overflow:hidden}.bar i{display:block;height:100%;border-radius:999px}.ring{position:relative;width:44px;height:44px;border-radius:50%;background:conic-gradient(var(--accent) calc(var(--p)*1%),#e8edf4 0);flex:0 0 44px}.ring:after{content:'';position:absolute;inset:5px;border-radius:50%;background:#fff}.ring span{position:absolute;inset:0;z-index:1;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#111827}.ring b{font-size:10px;line-height:11px}.ring small{font-size:9px;line-height:10px;color:#667085;font-weight:800}.deepseek-card{background:linear-gradient(145deg,#ffffff,#f8f5ff)}.balance-value{margin-top:8px;font-size:22px;line-height:26px;font-weight:850;color:#4c1d95}.balance-label{margin-top:5px;font-size:12px;color:#667085}.balance-line{height:6px;border-radius:999px;margin-top:12px;background:linear-gradient(90deg,#7c3aed,#2563eb,#10b981)}
 .model-card{margin-top:8px;padding:10px 12px}.model-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:7px;margin-top:7px}.model-tile{min-height:86px;border:1px solid #e7edf5;background:linear-gradient(180deg,#ffffff 0%,#f8fafc 100%);border-radius:12px;padding:7px}.model-name{font-size:11.5px;font-weight:850;color:#111827;margin-bottom:1px}.model-id{font-size:8.5px;line-height:10px;color:#94a3b8;margin-bottom:4px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.model-metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:4px}.model-metric{display:flex;flex-direction:column;align-items:center;gap:1px;min-width:0}.mini-ring{position:relative;width:30px;height:30px;border-radius:50%;background:conic-gradient(var(--accent) calc(var(--p)*1%),#e8edf4 0)}.mini-ring:after{content:'';position:absolute;inset:4px;border-radius:50%;background:#fff}.mini-ring span{position:absolute;inset:0;z-index:1;display:grid;place-items:center;font-size:8px;font-weight:900;color:#111827}.mini-label{font-size:9px;line-height:10px;color:#64748b;font-weight:800}.mini-count{font-size:10.5px;line-height:12px;color:#111827;font-weight:850;font-variant-numeric:tabular-nums}.loading{height:118px;display:flex;align-items:center;gap:16px;padding:22px;border-radius:18px;background:#fff;border:1px solid #e3e8f0;box-shadow:0 18px 38px rgba(15,23,42,.08)}.loader-ring{width:46px;height:46px;border-radius:50%;border:4px solid #dbeafe;border-top-color:#2563eb;animation:spin .9s linear infinite}.loading-title{font-size:18px;font-weight:850}.loading-sub{margin-top:6px;color:#667085;font-size:12px}.skeleton-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin-top:14px}.skeleton-grid div,.skeleton-wide{height:86px;border-radius:16px;background:linear-gradient(90deg,#eef2f7,#fff,#eef2f7);background-size:240% 100%;animation:shine 1.25s linear infinite;border:1px solid #e3e8f0}.skeleton-wide{margin-top:12px;height:170px}.skeleton-wide.small{height:110px}.error-card{padding:18px;border-radius:18px;background:#fff;border:1px solid #fecaca}.error-title{font-size:17px;font-weight:850;color:#991b1b}.error-text{margin-top:8px;color:#64748b;font-size:12px;line-height:1.55;white-space:pre-wrap}@keyframes spin{to{transform:rotate(360deg)}}@keyframes shine{to{background-position:-240% 0}}
";
        }

        private void RenderLoadingBody()
        {
            body.Controls.Clear();
            var dots = new string('.', (loadingTick % 4) + 1);
            AddLabel(body, "刷新中" + dots, 20, 20, 360, 30, new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), Ui.Text, ContentAlignment.MiddleLeft);
            AddLabel(body, "后台读取 Codex、OpenCode Go、DeepSeek", 20, 56, 360, 22, new Font("Microsoft YaHei UI", 9F), Ui.Muted, ContentAlignment.MiddleLeft);
            var track = new Panel { Location = new Point(20, 96), Size = new Size(500, 8), BackColor = Color.FromArgb(232, 235, 240) };
            var fill = new Panel { Size = new Size(92, 8), BackColor = Ui.Accent };
            fill.Left = ((loadingTick * 24) % (track.Width + fill.Width)) - fill.Width;
            track.Controls.Add(fill);
            body.Controls.Add(track);
        }

        private static string CodexSub(IDictionary<string, object> codex)
        {
            var credits = Json.Dict(codex, "reset_credits");
            var count = Json.Value(credits, "available_count");
            return count == null ? "Codex usage" : "Reset credits " + Convert.ToString(count);
        }

        private void SetRoundedRegion()
        {
            using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), 14))
                Region = new Region(path);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Label AddLabel(Control parent, string text, int x, int y, int w, int h, Font font, Color color, ContentAlignment align)
        {
            var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = font, ForeColor = color, BackColor = Color.Transparent, TextAlign = align, AutoEllipsis = true };
            parent.Controls.Add(label);
            return label;
        }
    }

    internal static class Ui
    {
        public static readonly Color Text = Color.FromArgb(27, 31, 42);
        public static readonly Color Muted = Color.FromArgb(96, 105, 123);
        public static readonly Color Border = Color.FromArgb(226, 230, 238);
        public static readonly Color Accent = Color.FromArgb(35, 99, 235);
        public static readonly Color Bad = Color.FromArgb(190, 58, 52);
    }

    internal static class Json
    {
        public static IDictionary<string, object> Provider(IDictionary<string, object> root, string id)
        {
            if (root == null) return null;
            foreach (var item in Items(Value(root, "providers")))
            {
                var p = item as IDictionary<string, object>;
                if (p != null && Convert.ToString(Value(p, "provider")) == id) return p;
            }
            return null;
        }

        public static IEnumerable<object> Items(object value)
        {
            var enumerable = value as IEnumerable;
            if (enumerable == null || value is string) yield break;
            foreach (var item in enumerable) yield return item;
        }

        public static IDictionary<string, object> Dict(IDictionary<string, object> parent, string key)
        {
            return Value(parent, key) as IDictionary<string, object>;
        }

        public static object Value(IDictionary<string, object> parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key)) return null;
            return parent[key];
        }

        public static IDictionary<string, object> Window(IDictionary<string, object> provider, string key)
        {
            return Dict(provider, key);
        }

        public static double Double(IDictionary<string, object> parent, string key, double fallback)
        {
            var value = Value(parent, key);
            if (value == null) return fallback;
            try { return Convert.ToDouble(value); } catch { return fallback; }
        }

        public static double RemainingPercent(IDictionary<string, object> window)
        {
            if (window == null) return 0;
            if (window.ContainsKey("remaining_percent")) return Double(window, "remaining_percent", 0);
            if (window.ContainsKey("used_percent")) return Math.Max(0, Math.Min(100, 100 - Double(window, "used_percent", 0)));
            return 0;
        }

        public static string Remaining(IDictionary<string, object> window)
        {
            if (window == null) return "n/a";
            if (window.ContainsKey("remaining_usd") && window.ContainsKey("remaining_percent"))
                return "$" + Double(window, "remaining_usd", 0).ToString("N2") + " / " + Double(window, "remaining_percent", 0).ToString("N0") + "%";
            return UsedPercent(window);
        }

        public static string UsedPercent(IDictionary<string, object> window)
        {
            return window == null ? "n/a" : Double(window, "used_percent", 0).ToString("N0") + "%";
        }

        public static string DeepSeekBalance(IDictionary<string, object> provider)
        {
            if (provider == null) return "不可用";
            foreach (var item in Items(Value(provider, "balance_infos")))
            {
                var first = item as IDictionary<string, object>;
                if (first == null) continue;
                return Convert.ToString(Value(first, "total_balance")) + " " + Convert.ToString(Value(first, "currency"));
            }
            return "n/a";
        }

        public static double DeepSeekBalanceNumber(IDictionary<string, object> provider)
        {
            if (provider == null) return 0;
            foreach (var item in Items(Value(provider, "balance_infos")))
            {
                var first = item as IDictionary<string, object>;
                if (first == null) continue;
                var raw = Convert.ToString(Value(first, "total_balance"));
                double value;
                return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : 0;
            }
            return 0;
        }

        public static string Freshness(IDictionary<string, object> provider)
        {
            if (provider == null) return "OCG 无缓存";
            var raw = Convert.ToString(Value(provider, "cached_at"));
            var source = Convert.ToString(Value(provider, "source"));
            if (string.IsNullOrWhiteSpace(raw)) return "OCG 缓存时间未知";
            DateTime parsed;
            if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed))
                return "OCG 缓存 " + raw;
            var local = parsed.ToLocalTime();
            var age = DateTime.Now - local;
            var prefix = (source == "edge_dashboard_uia" || source == "edge_extension_dom") ? "页面同步" : "缓存";
            if (age.TotalSeconds >= 0 && age.TotalSeconds < 90) return "刚刚同步";
            return prefix + " " + local.ToString("HH:mm");
        }
    }

    internal sealed class GoModelEstimate
    {
        public string Id;
        public string Name;
        public double FiveHour;
        public double Weekly;
        public double Monthly;
        public bool HasEstimate;
        public int SortOrder;
        public int SourceIndex;
    }

    internal static class GoModelCatalog
    {
        private const string FileName = "opencode_go_models.json";
        private const string OfficialDocsUrl = "https://opencode.ai/docs/zh-cn/go";
        private const string ModelsEndpoint = "https://opencode.ai/zen/go/v1/models";
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static List<GoModelEstimate> EnabledModels()
        {
            var result = new List<GoModelEstimate>();
            var root = LoadData();
            int sourceIndex = 0;
            foreach (var item in Json.Items(Json.Value(root, "models")))
            {
                var model = item as IDictionary<string, object>;
                if (model == null || !ToBool(Json.Value(model, "enabled"), false)) continue;
                var estimate = ToEstimate(model);
                if (estimate != null)
                {
                    estimate.SourceIndex = sourceIndex++;
                    estimate.SortOrder = Json.Value(model, "sort_order") == null ? 100000 + estimate.SourceIndex : ToInt(Json.Value(model, "sort_order"), 100000 + estimate.SourceIndex);
                    result.Add(estimate);
                }
            }
            result.Sort(delegate(GoModelEstimate left, GoModelEstimate right)
            {
                var compare = left.SortOrder.CompareTo(right.SortOrder);
                return compare != 0 ? compare : left.SourceIndex.CompareTo(right.SourceIndex);
            });
            return result;
        }

        public static void SaveDisplayOrder(IEnumerable<string> ids)
        {
            var root = LoadData();
            var models = Json.Value(root, "models") as IEnumerable;
            if (models == null) return;
            var orderById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (var id in ids ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(id) || orderById.ContainsKey(id)) continue;
                orderById[id] = order++;
            }
            foreach (var item in models)
            {
                var model = item as IDictionary<string, object>;
                if (model == null) continue;
                var id = Convert.ToString(Json.Value(model, "id"));
                int value;
                if (orderById.TryGetValue(id, out value)) model["sort_order"] = value;
            }
            SaveData(root);
        }

        public static IDictionary<string, object> LoadData()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
                if (File.Exists(path))
                {
                    var parsed = Serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>;
                    if (parsed != null && Json.Value(parsed, "models") != null) return parsed;
                }
            }
            catch { }
            return CreateDefaultData();
        }

        public static void SaveData(IDictionary<string, object> root)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
            root["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            File.WriteAllText(path, Serializer.Serialize(root), Encoding.UTF8);
        }

        public static int RefreshAvailableModels(IDictionary<string, object> root, out string status)
        {
            return RefreshOfficialCatalog(root, out status);
        }

        public static int RefreshOfficialCatalog(IDictionary<string, object> root, out string status)
        {
            int officialCount = 0;
            int liveCount = 0;
            int liveAdded = 0;
            string docsError = "";
            string apiError = "";
            try
            {
                var html = DownloadText(OfficialDocsUrl);
                officialCount = ApplyOfficialDocs(root, html);
                root["official_docs_url"] = OfficialDocsUrl;
                root["official_docs_fetched_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            catch (Exception ex)
            {
                docsError = ex.Message;
            }

            try
            {
                string apiStatus;
                liveCount = MergeLiveModels(root, out liveAdded, out apiStatus);
                if (!string.IsNullOrWhiteSpace(apiStatus) && apiStatus.StartsWith("失败", StringComparison.Ordinal)) apiError = apiStatus;
            }
            catch (Exception ex)
            {
                apiError = ex.Message;
            }

            if (officialCount > 0 || liveCount > 0)
            {
                root["catalog_source"] = OfficialDocsUrl + " + " + ModelsEndpoint;
                root["catalog_fetched_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            if (officialCount > 0 && liveCount > 0)
            {
                status = "已同步官方规则 " + officialCount + " 个，实时目录 " + liveCount + " 个（新增 " + liveAdded + " 个）。";
            }
            else if (officialCount > 0)
            {
                status = "已同步官方规则 " + officialCount + " 个；" + (string.IsNullOrWhiteSpace(apiError) ? "实时目录返回 0 个模型。" : "实时目录失败：" + apiError);
            }
            else if (liveCount > 0)
            {
                status = "已同步实时目录 " + liveCount + " 个；官方规则暂不可用。";
            }
            else
            {
                status = "同步失败：" + (string.IsNullOrWhiteSpace(docsError) ? apiError : docsError);
            }
            if (!string.IsNullOrWhiteSpace(docsError) && officialCount > 0) status += " 文档异常：" + docsError;
            if (!string.IsNullOrWhiteSpace(apiError) && liveCount > 0) status += " 接口异常：" + apiError;
            return officialCount > 0 ? officialCount : liveCount;
        }

        private static string DownloadText(string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var request = WebRequest.CreateHttp(url);
            request.Method = "GET";
            request.Timeout = 15000;
            request.UserAgent = "HonorQuota/2.0 (OpenCode Go catalog sync)";
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static int MergeLiveModels(IDictionary<string, object> root, out int added, out string status)
        {
            added = 0;
            try
            {
                object parsed = Serializer.DeserializeObject(DownloadText(ModelsEndpoint));
                var live = parsed as IDictionary<string, object>;
                var liveItems = Json.Items(live == null ? parsed : Json.Value(live, "data"));
                var models = MutableModels(root);
                var existing = new Dictionary<string, IDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in models)
                {
                    var model = item as IDictionary<string, object>;
                    var id = model == null ? "" : Convert.ToString(Json.Value(model, "id"));
                    if (model != null && !string.IsNullOrWhiteSpace(id))
                    {
                        model["available"] = false;
                        existing[id] = model;
                    }
                }

                int seen = 0;
                foreach (var item in liveItems)
                {
                    var liveModel = item as IDictionary<string, object>;
                    var id = liveModel == null ? "" : Convert.ToString(Json.Value(liveModel, "id"));
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    seen++;
                    IDictionary<string, object> target;
                    if (existing.TryGetValue(id, out target))
                    {
                        target["available"] = true;
                        continue;
                    }
                    target = new Dictionary<string, object>();
                    target["id"] = id;
                    target["name"] = PrettyName(id);
                    target["enabled"] = false;
                    target["has_estimate"] = false;
                    target["five_hour"] = 0;
                    target["weekly"] = 0;
                    target["monthly"] = 0;
                    target["source"] = "官方 /models（新模型，等待文档规则）";
                    target["available"] = true;
                    models.Add(target);
                    existing[id] = target;
                    added++;
                }
                status = "已完成";
                root["catalog_fetched_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                return seen;
            }
            catch (Exception ex)
            {
                status = "失败：" + ex.Message;
                return 0;
            }
        }

        private static int ApplyOfficialDocs(IDictionary<string, object> root, string html)
        {
            var plain = WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", " "));
            plain = Regex.Replace(plain, "\\s+", " ").Trim();
            var start = plain.IndexOf("下表提供了基于典型 Go 使用模式的预估请求数", StringComparison.Ordinal);
            var end = plain.IndexOf("预估值基于观察到的请求模式", start < 0 ? 0 : start, StringComparison.Ordinal);
            if (start < 0 || end <= start) throw new InvalidDataException("未找到官方请求估算表");
            var table = plain.Substring(start, end - start);
            var matches = Regex.Matches(table, @"(?<name>[A-Za-z][A-Za-z0-9 .-]*?)\s+(?<five>[0-9,]+)\s+(?<weekly>[0-9,]+)\s+(?<monthly>[0-9,]+)(?=\s|$)");
            var models = MutableModels(root);
            int count = 0;
            foreach (Match match in matches)
            {
                var name = match.Groups["name"].Value.Trim();
                var id = KnownModelId(name);
                var target = FindModelByIdOrName(models, id, name);
                if (target == null)
                {
                    target = new Dictionary<string, object>();
                    target["id"] = id;
                    target["name"] = name;
                    target["enabled"] = false;
                    models.Add(target);
                }
                var five = ParseCount(match.Groups["five"].Value);
                var weekly = ParseCount(match.Groups["weekly"].Value);
                var monthly = ParseCount(match.Groups["monthly"].Value);
                var hasManual = ToBool(Json.Value(target, "manual_override"), false);
                if (!hasManual && HasOfficialBaseline(target))
                {
                    hasManual = Math.Abs(Number(Json.Value(target, "five_hour")) - Number(Json.Value(target, "official_five_hour"))) > 0.01
                        || Math.Abs(Number(Json.Value(target, "weekly")) - Number(Json.Value(target, "official_weekly"))) > 0.01
                        || Math.Abs(Number(Json.Value(target, "monthly")) - Number(Json.Value(target, "official_monthly"))) > 0.01;
                }
                target["official_five_hour"] = five;
                target["official_weekly"] = weekly;
                target["official_monthly"] = monthly;
                target["manual_override"] = hasManual;
                if (!hasManual)
                {
                    target["five_hour"] = five;
                    target["weekly"] = weekly;
                    target["monthly"] = monthly;
                }
                target["has_estimate"] = true;
                target["source"] = "OpenCode Go 中文官方文档（自动同步）";
                count++;
            }
            ApplyLimitsFromDocs(root, plain);
            return count;
        }

        private static void ApplyLimitsFromDocs(IDictionary<string, object> root, string plain)
        {
            var limits = Json.Value(root, "limits") as IDictionary<string, object>;
            if (limits == null)
            {
                limits = new Dictionary<string, object>();
                root["limits"] = limits;
            }
            SetLimitFromText(limits, "five_hour_usd", plain, "5 小时限制");
            SetLimitFromText(limits, "weekly_usd", plain, "每周限制");
            SetLimitFromText(limits, "monthly_usd", plain, "每月限制");
        }

        private static IList MutableModels(IDictionary<string, object> root)
        {
            var current = Json.Value(root, "models") as IList;
            var mutable = current as List<object>;
            if (mutable != null) return mutable;
            mutable = new List<object>();
            if (current != null)
            {
                foreach (var item in current) mutable.Add(item);
            }
            root["models"] = mutable;
            return mutable;
        }

        private static void SetLimitFromText(IDictionary<string, object> limits, string key, string text, string label)
        {
            var match = Regex.Match(text, Regex.Escape(label) + @"\s*[—-]\s*\$?\s*([0-9]+(?:\.[0-9]+)?)\s*美元");
            if (match.Success) limits[key] = Number(match.Groups[1].Value);
        }

        private static int ParseCount(string value)
        {
            int result;
            return Int32.TryParse((value ?? "").Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        internal static bool HasOfficialBaseline(IDictionary<string, object> model)
        {
            return Json.Value(model, "official_five_hour") != null && Json.Value(model, "official_weekly") != null && Json.Value(model, "official_monthly") != null;
        }

        private static IDictionary<string, object> FindModelByIdOrName(IList models, string id, string name)
        {
            foreach (var item in models)
            {
                var model = item as IDictionary<string, object>;
                if (model == null) continue;
                if (string.Equals(Convert.ToString(Json.Value(model, "id")), id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Convert.ToString(Json.Value(model, "name")), name, StringComparison.OrdinalIgnoreCase)) return model;
            }
            return null;
        }

        private static string KnownModelId(string name)
        {
            var key = (name ?? "").Trim().ToLowerInvariant();
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "grok 4.5", "grok-4.5" }, { "gpt 5.6 luna", "gpt-5.6-luna" },
                { "glm-5.3", "glm-5.3" }, { "glm-5.2", "glm-5.2" }, { "glm-5.1", "glm-5.1" },
                { "kimi k3", "kimi-k3" }, { "kimi k2.7 code", "kimi-k2.7-code" }, { "kimi k2.6", "kimi-k2.6" },
                { "mimo-v2.5", "mimo-v2.5" }, { "mimo-v2.5-pro", "mimo-v2.5-pro" },
                { "minimax m3", "minimax-m3" }, { "minimax m2.7", "minimax-m2.7" },
                { "qwen3.8 max", "qwen3.8-max" }, { "qwen3.7 max", "qwen3.7-max" },
                { "qwen3.7 plus", "qwen3.7-plus" }, { "qwen3.6 plus", "qwen3.6-plus" },
                { "deepseek v4 pro", "deepseek-v4-pro" }, { "deepseek v4 flash", "deepseek-v4-flash" }, { "hy3", "hy3" }
            };
            string id;
            if (known.TryGetValue(key, out id)) return id;
            return Regex.Replace(key, @"[^a-z0-9]+", "-").Trim('-');
        }

        public static IDictionary<string, object> CreateDefaultData()
        {
            var root = new Dictionary<string, object>();
            root["version"] = 2;
            root["catalog_source"] = "https://opencode.ai/docs/zh-cn/go";
            root["updated_at"] = "2026-08-16T00:00:00Z";
            var limits = new Dictionary<string, object>();
            limits["five_hour_usd"] = 12;
            limits["weekly_usd"] = 30;
            limits["monthly_usd"] = 60;
            root["limits"] = limits;
            var models = new List<object>();
            root["models"] = models;
            AddModel(models, "grok-4.5", "Grok 4.5", 120, 300, 600, true);
            AddModel(models, "glm-5.3", "GLM-5.3", 220, 540, 1080, true);
            AddModel(models, "glm-5.2", "GLM-5.2", 880, 2150, 4300, true);
            AddModel(models, "glm-5.1", "GLM-5.1", 880, 2150, 4300, false);
            AddModel(models, "gpt-5.6-luna", "GPT 5.6 Luna", 2050, 5100, 10250, false);
            AddModel(models, "kimi-k3", "Kimi K3", 110, 250, 490, false);
            AddModel(models, "kimi-k2.7-code", "Kimi K2.7 Code", 1350, 3380, 6750, true);
            AddModel(models, "kimi-k2.6", "Kimi K2.6", 1150, 2880, 5750, false);
            AddModel(models, "mimo-v2.5", "MiMo-V2.5", 30100, 75200, 150400, false);
            AddModel(models, "mimo-v2.5-pro", "MiMo-V2.5-Pro", 3250, 8150, 16300, false);
            AddModel(models, "minimax-m3", "MiniMax M3", 3200, 8000, 16000, true);
            AddModel(models, "minimax-m2.7", "MiniMax M2.7", 3400, 8500, 17000, false);
            AddModel(models, "qwen3.8-max", "Qwen3.8 Max", 160, 400, 810, false);
            AddModel(models, "qwen3.7-max", "Qwen3.7 Max", 340, 840, 1690, false);
            AddModel(models, "qwen3.7-plus", "Qwen3.7 Plus", 4300, 10800, 21600, true);
            AddModel(models, "qwen3.6-plus", "Qwen3.6 Plus", 3300, 8200, 16300, false);
            AddModel(models, "deepseek-v4-pro", "DeepSeek V4 Pro", 1050, 2600, 5200, true);
            AddModel(models, "deepseek-v4-flash", "DeepSeek V4 Flash", 3800, 9450, 18900, true);
            AddModel(models, "hy3", "Hy3", 4300, 10750, 21500, false);
            return root;
        }

        private static void AddModel(IList models, string id, string name, double fiveHour, double weekly, double monthly, bool enabled)
        {
            var model = new Dictionary<string, object>();
            model["id"] = id;
            model["name"] = name;
            model["enabled"] = enabled;
            model["has_estimate"] = true;
            model["five_hour"] = fiveHour;
            model["weekly"] = weekly;
            model["monthly"] = monthly;
            model["source"] = "OpenCode Go 官方文档（2026-08-16）";
            models.Add(model);
        }

        private static GoModelEstimate ToEstimate(IDictionary<string, object> model)
        {
            var id = Convert.ToString(Json.Value(model, "id"));
            if (string.IsNullOrWhiteSpace(id)) return null;
            var result = new GoModelEstimate();
            result.Id = id;
            result.Name = Convert.ToString(Json.Value(model, "name"));
            if (string.IsNullOrWhiteSpace(result.Name)) result.Name = PrettyName(id);
            result.FiveHour = Number(Json.Value(model, "five_hour"));
            result.Weekly = Number(Json.Value(model, "weekly"));
            result.Monthly = Number(Json.Value(model, "monthly"));
            result.HasEstimate = ToBool(Json.Value(model, "has_estimate"), result.FiveHour > 0 && result.Weekly > 0 && result.Monthly > 0);
            return result;
        }

        internal static double Number(object value)
        {
            try { return value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static int ToInt(object value, int fallback)
        {
            try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null) return fallback;
            try { return Convert.ToBoolean(value); } catch { return fallback; }
        }

        private static string PrettyName(string id)
        {
            var value = (id ?? "").Replace('-', ' ').Replace('.', ' ');
            if (value.Length == 0) return "未命名模型";
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);
        }
    }

    internal sealed class GoModelSettingsForm : Form
    {
        private readonly NumericUpDown fiveHourLimit = LimitBox();
        private readonly NumericUpDown weeklyLimit = LimitBox();
        private readonly NumericUpDown monthlyLimit = LimitBox();
        private readonly TextBox searchBox = new TextBox();
        private readonly Label resultLabel = new Label();
        private readonly FlowLayoutPanel cards = new FlowLayoutPanel();
        private readonly List<GoModelCard> modelCards = new List<GoModelCard>();
        private IDictionary<string, object> data;

        public GoModelSettingsForm()
        {
            Text = "Honor Quota  ·  OpenCode Go 模型与用量规则";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1120, 780);
            MinimumSize = new Size(900, 620);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BackColor = Color.FromArgb(244, 247, 251);
            Font = new Font("Segoe UI", 9.5F);
            DoubleBuffered = true;
            data = GoModelCatalog.LoadData();
            BuildUi();
            LoadLimits();
            RebuildCards();
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(24, 18, 24, 8), BackColor = Color.White };
            var title = new Label { Text = "OpenCode Go 模型与用量规则", Dock = DockStyle.Top, Height = 31, Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold), ForeColor = Color.FromArgb(18, 30, 52) };
            var subtitle = new Label { Text = "选择要显示的模型；规则按 Go 的美元额度窗口换算，保存后主面板立即更新。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(100, 116, 139), AutoEllipsis = true };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);
            Controls.Add(header);

            var limitPanel = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(24, 10, 24, 10), BackColor = Color.FromArgb(244, 247, 251) };
            var limitCard = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
            limitCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            limitCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            limitCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            limitCard.Controls.Add(LimitTile("5 小时额度", fiveHourLimit, "短窗口 · USD"), 0, 0);
            limitCard.Controls.Add(LimitTile("每周额度", weeklyLimit, "滚动周窗口 · USD"), 1, 0);
            limitCard.Controls.Add(LimitTile("每月额度", monthlyLimit, "滚动月窗口 · USD"), 2, 0);
            limitPanel.Controls.Add(limitCard);
            Controls.Add(limitPanel);

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(24, 10, 24, 10), BackColor = Color.FromArgb(244, 247, 251) };
            searchBox.Location = new Point(24, 12);
            searchBox.Size = new Size(300, 32);
            searchBox.Font = new Font("Segoe UI", 10F);
            searchBox.BorderStyle = BorderStyle.FixedSingle;
            searchBox.TextChanged += delegate { ApplyFilter(); };
            toolbar.Controls.Add(searchBox);
            var searchHint = new Label { Text = "搜索模型或 Model ID", Location = new Point(34, 18), AutoSize = true, ForeColor = Color.FromArgb(148, 163, 184), BackColor = Color.White };
            searchHint.Click += delegate { searchBox.Focus(); };
            toolbar.Controls.Add(searchHint);
            searchBox.GotFocus += delegate { searchHint.Visible = false; };
            searchBox.LostFocus += delegate { searchHint.Visible = string.IsNullOrEmpty(searchBox.Text); };
            resultLabel.Location = new Point(342, 18);
            resultLabel.AutoSize = true;
            resultLabel.ForeColor = Color.FromArgb(100, 116, 139);
            toolbar.Controls.Add(resultLabel);
            Controls.Add(toolbar);

            cards.Dock = DockStyle.Fill;
            cards.AutoScroll = true;
            cards.WrapContents = true;
            cards.FlowDirection = FlowDirection.LeftToRight;
            cards.Padding = new Padding(16, 8, 16, 20);
            cards.BackColor = Color.FromArgb(244, 247, 251);
            cards.Resize += delegate { ResizeCards(); };
            Controls.Add(cards);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(24, 12, 24, 12), BackColor = Color.White };
            var save = FlatButton("保存并立即应用", Color.FromArgb(37, 99, 235), Color.White, 138);
            save.Dock = DockStyle.Right;
            save.Click += delegate { SaveAndClose(); };
            var cancel = FlatButton("取消", Color.White, Color.FromArgb(51, 65, 85), 82);
            cancel.Dock = DockStyle.Right;
            cancel.DialogResult = DialogResult.Cancel;
            var restore = FlatButton("恢复官方推荐", Color.White, Color.FromArgb(51, 65, 85), 116);
            restore.Dock = DockStyle.Left;
            restore.Click += delegate { data = GoModelCatalog.CreateDefaultData(); LoadLimits(); RebuildCards(); };
            var refresh = FlatButton("刷新官方模型目录", Color.White, Color.FromArgb(37, 99, 235), 132);
            refresh.Dock = DockStyle.Left;
            refresh.Click += delegate
            {
                string status;
                GoModelCatalog.RefreshAvailableModels(data, out status);
                RebuildCards();
                resultLabel.Text = status;
            };
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            footer.Controls.Add(restore);
            footer.Controls.Add(refresh);
            Controls.Add(footer);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static Panel LimitTile(string title, NumericUpDown box, string caption)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(5), Padding = new Padding(14, 10, 14, 8) };
            panel.Controls.Add(new Label { Text = caption, Dock = DockStyle.Bottom, Height = 18, ForeColor = Color.FromArgb(148, 163, 184), Font = new Font("Segoe UI", 8.5F) });
            box.Dock = DockStyle.Right;
            box.Width = 88;
            box.Margin = new Padding(0);
            box.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
            panel.Controls.Add(box);
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(30, 41, 59), Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
            return panel;
        }

        private static Button FlatButton(string text, Color back, Color fore, int width)
        {
            return new Button { Text = text, Width = width, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), FlatAppearance = { BorderColor = Color.FromArgb(203, 213, 225), BorderSize = 1 } };
        }

        private static NumericUpDown LimitBox()
        {
            return new NumericUpDown { Minimum = 0, Maximum = 1000000, DecimalPlaces = 2, Increment = 1, Value = 0, ThousandsSeparator = true, BorderStyle = BorderStyle.FixedSingle };
        }

        private void LoadLimits()
        {
            var limits = Json.Value(data, "limits") as IDictionary<string, object>;
            fiveHourLimit.Value = ClampLimit(Number(Json.Value(limits, "five_hour_usd"), 12));
            weeklyLimit.Value = ClampLimit(Number(Json.Value(limits, "weekly_usd"), 30));
            monthlyLimit.Value = ClampLimit(Number(Json.Value(limits, "monthly_usd"), 60));
        }

        private void RebuildCards()
        {
            cards.SuspendLayout();
            cards.Controls.Clear();
            modelCards.Clear();
            foreach (var item in Json.Items(Json.Value(data, "models")))
            {
                var model = item as IDictionary<string, object>;
                if (model == null) continue;
                var card = new GoModelCard(model);
                card.Margin = new Padding(8);
                modelCards.Add(card);
                cards.Controls.Add(card);
            }
            cards.ResumeLayout(true);
            ResizeCards();
            ApplyFilter();
        }

        private void ResizeCards()
        {
            var width = Math.Max(300, (cards.ClientSize.Width - 72) / 3);
            foreach (var card in modelCards) card.Width = width;
        }

        private void ApplyFilter()
        {
            var query = (searchBox.Text ?? "").Trim();
            int visible = 0;
            foreach (var card in modelCards)
            {
                card.Visible = card.Matches(query);
                if (card.Visible) visible++;
            }
            resultLabel.Text = visible + " / " + modelCards.Count + " 个模型";
        }

        private void SaveAndClose()
        {
            var limits = Json.Value(data, "limits") as IDictionary<string, object>;
            if (limits == null)
            {
                limits = new Dictionary<string, object>();
                data["limits"] = limits;
            }
            limits["five_hour_usd"] = fiveHourLimit.Value;
            limits["weekly_usd"] = weeklyLimit.Value;
            limits["monthly_usd"] = monthlyLimit.Value;
            foreach (var card in modelCards) card.Commit();
            try
            {
                GoModelCatalog.SaveData(data);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "Honor Quota", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static double Number(object value, double fallback)
        {
            try { return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static decimal ClampLimit(double value)
        {
            return (decimal)Math.Max(0, Math.Min(1000000, value));
        }
    }

    internal sealed class GoModelCard : Panel
    {
        private readonly IDictionary<string, object> model;
        private readonly CheckBox enabled;
        private readonly NumericUpDown fiveHour;
        private readonly NumericUpDown weekly;
        private readonly NumericUpDown monthly;
        private readonly string modelName;
        private readonly string modelId;

        public GoModelCard(IDictionary<string, object> model)
        {
            this.model = model;
            modelName = Convert.ToString(Json.Value(model, "name"));
            modelId = Convert.ToString(Json.Value(model, "id"));
            Height = 154;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Padding = new Padding(14, 11, 14, 10);
            enabled = new CheckBox { Text = modelName, Checked = ToBool(Json.Value(model, "enabled"), false), Dock = DockStyle.Top, Height = 28, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold), ForeColor = Color.FromArgb(15, 23, 42), AutoEllipsis = true };
            Controls.Add(enabled);
            var idLabel = new Label { Text = modelId, Dock = DockStyle.Top, Height = 19, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Consolas", 8.5F), AutoEllipsis = true };
            Controls.Add(idLabel);
            var source = Convert.ToString(Json.Value(model, "source"));
            var sourceLabel = new Label { Text = string.IsNullOrWhiteSpace(source) ? "需要手动配置估算" : source, Dock = DockStyle.Top, Height = 19, ForeColor = Color.FromArgb(148, 163, 184), Font = new Font("Segoe UI", 8F), AutoEllipsis = true };
            Controls.Add(sourceLabel);
            var metrics = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 60, ColumnCount = 3, RowCount = 2, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(6, 4, 6, 4) };
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            metrics.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            fiveHour = CountBox(Json.Value(model, "five_hour"));
            weekly = CountBox(Json.Value(model, "weekly"));
            monthly = CountBox(Json.Value(model, "monthly"));
            metrics.Controls.Add(MetricLabel("5h"), 0, 0);
            metrics.Controls.Add(MetricLabel("周"), 1, 0);
            metrics.Controls.Add(MetricLabel("月"), 2, 0);
            metrics.Controls.Add(fiveHour, 0, 1);
            metrics.Controls.Add(weekly, 1, 1);
            metrics.Controls.Add(monthly, 2, 1);
            Controls.Add(metrics);
        }

        private static Label MetricLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold) };
        }

        private static NumericUpDown CountBox(object value)
        {
            double number;
            try { number = value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { number = 0; }
            var box = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100000000, DecimalPlaces = 0, Increment = 1, ThousandsSeparator = true, Value = (decimal)Math.Max(0, Math.Min(100000000, number)), Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), BorderStyle = BorderStyle.FixedSingle };
            return box;
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return modelName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || modelId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void Commit()
        {
            model["enabled"] = enabled.Checked;
            model["five_hour"] = fiveHour.Value;
            model["weekly"] = weekly.Value;
            model["monthly"] = monthly.Value;
            model["has_estimate"] = fiveHour.Value > 0 && weekly.Value > 0 && monthly.Value > 0;
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null) return fallback;
            try { return Convert.ToBoolean(value); } catch { return fallback; }
        }
    }

    internal sealed class GoModelSettingsFormV2 : Form
    {
        private readonly WebView2 view = new WebView2();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private IDictionary<string, object> data;
        private Task initTask;

        public GoModelSettingsFormV2()
        {
            Text = "Honor Quota  ·  OpenCode Go 模型与用量规则";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1120, 780);
            MinimumSize = new Size(900, 620);
            BackColor = Color.FromArgb(244, 247, 251);
            view.Dock = DockStyle.Fill;
            view.DefaultBackgroundColor = Color.FromArgb(244, 247, 251);
            Controls.Add(view);
            data = GoModelCatalog.LoadData();
            Load += async delegate { await InitializeAsync(); };
        }

        private async Task InitializeAsync()
        {
            if (initTask != null) { await initTask; return; }
            initTask = InitializeViewAsync();
            await initTask;
        }

        private async Task InitializeViewAsync()
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HonorQuota", "SettingsWebView2");
                Directory.CreateDirectory(folder);
                var env = await CoreWebView2Environment.CreateAsync(null, folder);
                await view.EnsureCoreWebView2Async(env);
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                Render("正在检查官方文档与实时模型目录…");
                Task.Run(delegate
                {
                    string syncStatus;
                    GoModelCatalog.RefreshOfficialCatalog(data, out syncStatus);
                    return syncStatus;
                }).ContinueWith(delegate(Task<string> completed)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)delegate { Render(completed.IsFaulted ? "自动同步失败，请点击右侧按钮重试。" : completed.Result); });
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Controls.Clear();
                Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(28), Text = "设置页初始化失败\r\n" + ex.Message, ForeColor = Color.FromArgb(185, 28, 28), Font = new Font("Segoe UI", 11F) });
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.WebMessageAsJson;
                if (!string.IsNullOrEmpty(raw) && raw.StartsWith("\"", StringComparison.Ordinal)) raw = serializer.Deserialize<string>(raw);
                var message = serializer.DeserializeObject(raw) as IDictionary<string, object>;
                var type = Convert.ToString(Json.Value(message, "type"));
                if (type == "cancel")
                {
                    Close();
                    return;
                }
                if (type == "refresh")
                {
                    string status;
                    GoModelCatalog.RefreshAvailableModels(data, out status);
                    Render(status);
                    return;
                }
                if (type == "restore")
                {
                    data = GoModelCatalog.CreateDefaultData();
                    Render("已恢复官方推荐规则；点击保存后生效。\n");
                    return;
                }
                if (type == "save")
                {
                    ApplyMessage(message);
                    GoModelCatalog.SaveData(data);
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                Render("保存失败：" + ex.Message);
            }
        }

        private void ApplyMessage(IDictionary<string, object> message)
        {
            var incomingLimits = Json.Value(message, "limits") as IDictionary<string, object>;
            var limits = Json.Value(data, "limits") as IDictionary<string, object>;
            if (limits == null)
            {
                limits = new Dictionary<string, object>();
                data["limits"] = limits;
            }
            if (incomingLimits != null)
            {
                limits["five_hour_usd"] = Number(Json.Value(incomingLimits, "five_hour_usd"), 12);
                limits["weekly_usd"] = Number(Json.Value(incomingLimits, "weekly_usd"), 30);
                limits["monthly_usd"] = Number(Json.Value(incomingLimits, "monthly_usd"), 60);
            }
            var models = Json.Value(data, "models") as IList;
            foreach (var item in Json.Items(Json.Value(message, "models")))
            {
                var incoming = item as IDictionary<string, object>;
                if (incoming == null) continue;
                var target = FindModel(models, Convert.ToString(Json.Value(incoming, "id")));
                if (target == null) continue;
                target["enabled"] = ToBool(Json.Value(incoming, "enabled"), false);
                var five = Number(Json.Value(incoming, "five_hour"), 0);
                var weekly = Number(Json.Value(incoming, "weekly"), 0);
                var monthly = Number(Json.Value(incoming, "monthly"), 0);
                var hasOfficial = GoModelCatalog.HasOfficialBaseline(target);
                var manual = ToBool(Json.Value(target, "manual_override"), false);
                if (hasOfficial)
                {
                    manual = Math.Abs(five - GoModelCatalog.Number(Json.Value(target, "official_five_hour"))) > 0.01
                        || Math.Abs(weekly - GoModelCatalog.Number(Json.Value(target, "official_weekly"))) > 0.01
                        || Math.Abs(monthly - GoModelCatalog.Number(Json.Value(target, "official_monthly"))) > 0.01;
                }
                else if (five > 0 || weekly > 0 || monthly > 0)
                {
                    manual = true;
                }
                target["five_hour"] = five;
                target["weekly"] = weekly;
                target["monthly"] = monthly;
                target["manual_override"] = manual;
                target["has_estimate"] = five > 0 && weekly > 0 && monthly > 0;
            }
        }

        private static IDictionary<string, object> FindModel(IList models, string id)
        {
            if (models == null) return null;
            foreach (var item in models)
            {
                var model = item as IDictionary<string, object>;
                if (model != null && string.Equals(Convert.ToString(Json.Value(model, "id")), id, StringComparison.OrdinalIgnoreCase)) return model;
            }
            return null;
        }

        private void Render(string status)
        {
            if (view.CoreWebView2 != null) view.CoreWebView2.NavigateToString(BuildHtml(data, status));
        }

        private static string BuildHtml(IDictionary<string, object> root, string status)
        {
            var limits = Json.Value(root, "limits") as IDictionary<string, object>;
            var models = Json.Items(Json.Value(root, "models"));
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset='utf-8'><style>");
            sb.Append(CssV2());
            sb.Append("</style></head><body><div class='app'>");
            sb.Append("<header class='hero'><div class='hero-copy'><div class='eyebrow'>HONOR QUOTA <span>/</span> OPENCODE GO</div><h1>模型与用量规则</h1><p>选择主面板要展示的模型；官方规则会自动同步，手动调整会被单独保留。</p><div class='hero-meta'><span class='hero-chip'>19 个官方支持模型</span><span>官方文档 + 实时模型目录</span></div></div><div class='hero-mark'><div>GO</div><small>SYNC</small></div></header>");
            sb.Append("<section class='limits'>");
            sb.Append(LimitCard("5 小时额度", "短窗口", Number(Json.Value(limits, "five_hour_usd"), 12), "five_hour_usd"));
            sb.Append(LimitCard("每周额度", "滚动周窗口", Number(Json.Value(limits, "weekly_usd"), 30), "weekly_usd"));
            sb.Append(LimitCard("每月额度", "滚动月窗口", Number(Json.Value(limits, "monthly_usd"), 60), "monthly_usd"));
            sb.Append("</section>");
            sb.Append("<section class='toolbar'><div class='toolbar-left'><label class='search'><span>⌕</span><input id='search' placeholder='搜索模型或 Model ID'></label><span id='count' class='count'></span></div><span class='status'>").Append(H(status)).Append("</span><button class='ghost' onclick='refreshCatalog()'>↻ 同步官方数据</button><button class='ghost' onclick='restoreOfficial()'>恢复推荐</button></section>");
            sb.Append("<main class='scroll'><section id='grid' class='grid'>");
            foreach (var item in models)
            {
                var model = item as IDictionary<string, object>;
                if (model == null) continue;
                var id = Convert.ToString(Json.Value(model, "id"));
                var name = Convert.ToString(Json.Value(model, "name"));
                var source = Convert.ToString(Json.Value(model, "source"));
                var configured = ToBool(Json.Value(model, "has_estimate"), Number(Json.Value(model, "monthly"), 0) > 0);
                sb.Append("<article class='model' data-id='").Append(H(id)).Append("' data-name='").Append(H(name)).Append(" ").Append(H(id)).Append("'><div class='model-head'><label class='switch'><input class='enabled' type='checkbox'").Append(ToBool(Json.Value(model, "enabled"), false) ? " checked" : "").Append("><span></span></label><div class='model-title'><strong>").Append(H(name)).Append("</strong><small>").Append(H(id)).Append("</small></div><em class='").Append(configured ? "official" : "pending").Append("'>").Append(configured ? "官方估算" : "待配置").Append("</em></div>");
                var available = ToBool(Json.Value(model, "available"), true);
                var manual = ToBool(Json.Value(model, "manual_override"), false);
                sb.Append("<div class='source'><span class='availability ").Append(available ? "live" : "offline").Append("'>").Append(available ? "● 可用" : "○ 未在实时目录").Append("</span><span class='source-text'>").Append(H(string.IsNullOrWhiteSpace(source) ? "官方模型目录" : source)).Append("</span>");
                if (manual) sb.Append("<span class='override'>手动覆盖</span>");
                sb.Append("</div><div class='metrics'>");
                sb.Append(MetricInput("5h", "five_hour", Json.Value(model, "five_hour")));
                sb.Append(MetricInput("周", "weekly", Json.Value(model, "weekly")));
                sb.Append(MetricInput("月", "monthly", Json.Value(model, "monthly")));
                sb.Append("</div></article>");
            }
            sb.Append("</section></main><footer><span><i class='footer-dot'></i>修改只在点击保存后生效</span><div><button class='cancel' onclick='cancelEdit()'>取消</button><button class='primary' onclick='saveAll()'><span>保存并立即应用</span><b>→</b></button></div></footer>");
            sb.Append("<script>");
            sb.Append("const send=(type,data)=>window.chrome.webview.postMessage(JSON.stringify(Object.assign({type:type},data||{})));const cards=()=>Array.from(document.querySelectorAll('.model'));function filter(){const q=document.getElementById('search').value.toLowerCase().trim();let n=0;cards().forEach(c=>{const ok=!q||c.dataset.name.toLowerCase().includes(q);c.hidden=!ok;if(ok)n++;});document.getElementById('count').textContent=n+' / '+cards().length+' 个模型';}function saveAll(){const limits={};document.querySelectorAll('[data-limit]').forEach(e=>limits[e.dataset.limit]=Number(e.value)||0);const models=cards().map(c=>({id:c.dataset.name.split(' ').pop(),enabled:c.querySelector('.enabled').checked,five_hour:Number(c.querySelector('[data-key=five_hour]').value)||0,weekly:Number(c.querySelector('[data-key=weekly]').value)||0,monthly:Number(c.querySelector('[data-key=monthly]').value)||0}));models.forEach((m,i)=>m.id=cards()[i].getAttribute('data-id'));send('save',{limits:limits,models:models});}function refreshCatalog(){send('refresh');}function restoreOfficial(){send('restore');}function cancelEdit(){send('cancel');}document.getElementById('search').addEventListener('input',filter);filter();");
            sb.Append("</script></div></body></html>");
            return sb.ToString();
        }

        private static string LimitCard(string title, string caption, double value, string key)
        {
            return "<label class='limit'><span class='limit-caption'>" + H(caption) + "</span><strong>" + H(title) + "</strong><div class='money'><span>$</span><input data-limit='" + H(key) + "' type='number' min='0' step='0.01' value='" + value.ToString("0.##", CultureInfo.InvariantCulture) + "'></div></label>";
        }

        private static string MetricInput(string label, string key, object value)
        {
            return "<label><span>" + H(label) + "</span><input data-key='" + key + "' type='number' min='0' step='1' value='" + Number(value, 0).ToString("0", CultureInfo.InvariantCulture) + "'></label>";
        }

        private static string CssV2()
        {
            return @"*{box-sizing:border-box}html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f5f7fb;color:#152238;font-family:'Segoe UI','Microsoft YaHei UI',Arial,sans-serif;-webkit-font-smoothing:antialiased;text-rendering:optimizeLegibility}button,input{font:inherit}.app{height:100vh;display:flex;flex-direction:column}.hero{flex:0 0 auto;display:flex;justify-content:space-between;align-items:center;padding:25px 32px 21px;background:radial-gradient(circle at 88% 12%,rgba(99,102,241,.14),transparent 29%),linear-gradient(135deg,#fff 0%,#f9fbff 70%,#eef4ff 100%);border-bottom:1px solid #e3e9f2}.hero-copy{min-width:0}.eyebrow{font-size:10px;letter-spacing:.16em;font-weight:850;color:#3159d8}.eyebrow span{color:#b6c2d4;margin:0 3px}.hero h1{margin:7px 0 5px;font-size:27px;line-height:32px;letter-spacing:-.035em;color:#13213a}.hero p{margin:0;color:#6e7d92;font-size:13px;line-height:20px}.hero-meta{display:flex;align-items:center;gap:11px;margin-top:13px;color:#91a0b5;font-size:11px}.hero-chip{padding:5px 9px;border:1px solid #d6e1ff;border-radius:999px;background:#f4f7ff;color:#4364cf;font-weight:750}.hero-mark{width:68px;height:68px;display:flex;flex-direction:column;align-items:center;justify-content:center;border-radius:22px;color:#fff;background:linear-gradient(145deg,#3457dc,#4b82ee 56%,#25b8c2);box-shadow:0 14px 28px rgba(50,88,218,.24);letter-spacing:.08em}.hero-mark div{font-size:17px;font-weight:900}.hero-mark small{margin-top:2px;font-size:8px;font-weight:800;opacity:.76;letter-spacing:.18em}.limits{flex:0 0 auto;display:grid;grid-template-columns:repeat(3,1fr);gap:14px;padding:17px 32px 13px;background:#f5f7fb}.limit{min-height:96px;position:relative;display:block;padding:15px 17px;border:1px solid #e2e8f2;border-radius:17px;background:linear-gradient(145deg,#fff,#fbfcff);box-shadow:0 7px 18px rgba(31,48,78,.055)}.limit:after{content:'';position:absolute;left:17px;right:17px;bottom:0;height:2px;border-radius:2px;background:linear-gradient(90deg,#4b72e8,#9fb6ff);opacity:.7}.limit-caption{display:block;font-size:11px;color:#91a0b5;font-weight:700}.limit strong{display:block;margin-top:5px;font-size:15px;color:#1e2d47}.money{position:absolute;right:17px;bottom:17px;display:flex;align-items:center;color:#72819a}.money span{font-size:14px;font-weight:800;margin-right:3px}.money input{width:92px;border:0;border-bottom:2px solid #c6d5ff;background:transparent;color:#315bd4;font-size:21px;font-weight:850;outline:0;text-align:right}.money input:focus{border-color:#315bd4}.toolbar{flex:0 0 auto;display:flex;align-items:center;gap:11px;padding:8px 32px 14px;background:#f5f7fb}.toolbar-left{display:flex;align-items:center;gap:11px;min-width:0}.search{height:38px;width:295px;display:flex;align-items:center;gap:8px;padding:0 12px;border:1px solid #dce4ef;border-radius:11px;background:#fff;box-shadow:0 3px 10px rgba(31,48,78,.045)}.search span{font-size:22px;line-height:1;color:#8190a5}.search input{width:100%;border:0;outline:0;color:#172033;background:transparent;font-size:12px}.search input::placeholder{color:#a0adbd}.count{white-space:nowrap;font-size:11px;color:#7d8da4;font-weight:700}.status{flex:1;min-width:0;color:#7d8da4;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.ghost,.cancel,.primary{height:38px;padding:0 14px;border-radius:10px;cursor:pointer;font-size:11px;font-weight:750;white-space:nowrap}.ghost,.cancel{border:1px solid #dce4ef;background:rgba(255,255,255,.88);color:#52627a}.ghost:hover,.cancel:hover{border-color:#9bb4f5;color:#315bd4;background:#fff}.scroll{flex:1;min-height:0;overflow:auto;padding:0 25px 24px 32px}.scroll::-webkit-scrollbar{width:10px}.scroll::-webkit-scrollbar-thumb{background:#ccd6e5;border:3px solid #f5f7fb;border-radius:99px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:14px}.model{min-height:169px;padding:16px;border:1px solid #e1e8f2;border-radius:17px;background:#fff;box-shadow:0 7px 18px rgba(31,48,78,.045);transition:transform .15s ease,box-shadow .15s ease,border-color .15s ease}.model:hover{transform:translateY(-2px);border-color:#cbd9f8;box-shadow:0 13px 26px rgba(31,48,78,.09)}.model[hidden]{display:none}.model-head{display:flex;align-items:center;gap:10px;min-width:0}.model-title{min-width:0;flex:1}.model-title strong{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:15px;color:#172943;letter-spacing:-.01em}.model-title small{display:block;margin-top:4px;color:#9aa8ba;font-family:Consolas,monospace;font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.model-head em{font-style:normal;padding:5px 8px;border-radius:999px;font-size:10px;font-weight:800;white-space:nowrap}.official{color:#08795f;background:#eafaf4}.pending{color:#a6650b;background:#fff6df}.source{display:flex;align-items:center;gap:7px;height:21px;margin-top:10px;color:#a0adbd;font-size:10px;min-width:0;white-space:nowrap}.availability{font-size:10px;font-weight:800;white-space:nowrap}.availability.live{color:#0b9b77}.availability.offline{color:#a5afbd}.source-text{min-width:0;overflow:hidden;text-overflow:ellipsis}.override{flex:0 0 auto;padding:3px 6px;border-radius:5px;background:#fff4e5;color:#b76b11;font-size:9px;font-weight:800}.metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:11px;padding:10px;background:#f7f9fc;border:1px solid #edf1f7;border-radius:11px}.metrics label{display:block;text-align:center}.metrics label span{display:block;margin-bottom:5px;color:#8190a5;font-size:10px;font-weight:800}.metrics input{width:100%;height:30px;border:1px solid #dce4ef;border-radius:8px;background:#fff;color:#172943;text-align:center;font-size:12px;font-weight:750;outline:0}.metrics input:focus{border-color:#7194ef;box-shadow:0 0 0 3px rgba(113,148,239,.14)}.switch{width:21px;height:21px;position:relative;display:block;flex:0 0 21px}.switch input{position:absolute;opacity:0}.switch span{display:block;width:21px;height:21px;border:2px solid #c8d2e1;border-radius:7px;background:#fff;transition:all .15s ease}.switch input:checked+span{border-color:#416ce4;background:#416ce4;box-shadow:0 3px 8px rgba(65,108,228,.25)}.switch input:checked+span:after{content:'✓';display:block;color:#fff;font-size:14px;font-weight:900;line-height:17px;text-align:center}footer{flex:0 0 auto;display:flex;align-items:center;justify-content:space-between;gap:15px;padding:14px 32px;background:rgba(255,255,255,.96);border-top:1px solid #e2e8f2;color:#99a6b8;font-size:11px}footer div{display:flex;gap:9px}.footer-dot{display:inline-block;width:6px;height:6px;margin:0 6px 1px 0;border-radius:50%;background:#45bd9b}.primary{display:flex;align-items:center;gap:11px;border:0;background:linear-gradient(135deg,#3d6ee7,#3158d2);color:#fff;box-shadow:0 7px 15px rgba(49,88,210,.22)}.primary b{font-size:17px;font-weight:500;line-height:1}.primary:hover{background:linear-gradient(135deg,#315fd8,#294ac0)}@media(max-width:930px){.hero{padding-left:22px;padding-right:22px}.limits{padding-left:22px;padding-right:22px}.toolbar,.scroll,footer{padding-left:22px;padding-right:22px}.toolbar{flex-wrap:wrap}.toolbar-left{flex:1}.status{order:5;flex-basis:100%}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:700px){.limits{grid-template-columns:1fr}.hero-meta{display:none}.hero-mark{width:56px;height:56px;border-radius:18px}.toolbar-left{width:100%;flex-basis:100%}.search{flex:1;width:auto}.grid{grid-template-columns:1fr}footer{align-items:flex-end}footer>span{display:none}}";
        }

        private static string Css()
        {
            return @"
*{box-sizing:border-box}html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#f4f7fb;color:#172033;font-family:'Segoe UI','Microsoft YaHei UI',Arial,sans-serif;-webkit-font-smoothing:antialiased;text-rendering:optimizeLegibility}button,input{font:inherit}.app{height:100vh;display:flex;flex-direction:column}.hero{flex:0 0 auto;display:flex;justify-content:space-between;align-items:center;padding:24px 30px 18px;background:linear-gradient(135deg,#ffffff 0%,#f8fbff 62%,#eef6ff 100%);border-bottom:1px solid #e2e8f0}.eyebrow{font-size:10px;letter-spacing:.16em;font-weight:800;color:#2563eb}.hero h1{margin:5px 0 4px;font-size:25px;line-height:30px;letter-spacing:-.02em}.hero p{margin:0;color:#64748b;font-size:13px}.hero-mark{width:54px;height:54px;border-radius:17px;display:grid;place-items:center;color:#fff;font-size:15px;font-weight:900;letter-spacing:.08em;background:linear-gradient(145deg,#2563eb,#06b6d4);box-shadow:0 10px 22px rgba(37,99,235,.22)}.limits{flex:0 0 auto;display:grid;grid-template-columns:repeat(3,1fr);gap:12px;padding:16px 30px 12px;background:#f4f7fb}.limit{min-height:88px;position:relative;display:block;padding:14px 16px;border:1px solid #e2e8f0;border-radius:15px;background:#fff;box-shadow:0 8px 20px rgba(15,23,42,.05)}.limit-caption{display:block;font-size:11px;color:#94a3b8;font-weight:700}.limit strong{display:block;margin-top:4px;font-size:15px}.money{position:absolute;right:15px;bottom:17px;display:flex;align-items:center;color:#64748b}.money span{font-size:14px;font-weight:700;margin-right:3px}.money input{width:94px;border:0;border-bottom:2px solid #bfdbfe;background:transparent;color:#1d4ed8;font-size:21px;font-weight:800;outline:0;text-align:right}.toolbar{flex:0 0 auto;display:flex;align-items:center;gap:10px;padding:7px 30px 13px;background:#f4f7fb}.search{height:36px;width:290px;display:flex;align-items:center;gap:8px;padding:0 11px;border:1px solid #dbe4ef;border-radius:10px;background:#fff;box-shadow:0 2px 7px rgba(15,23,42,.04)}.search span{font-size:21px;line-height:1;color:#64748b}.search input{width:100%;border:0;outline:0;color:#172033;background:transparent}.search input::placeholder{color:#94a3b8}.count{font-size:12px;color:#64748b}.status{flex:1;min-width:0;font-size:12px;color:#64748b;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.ghost,.cancel,.primary{height:36px;padding:0 14px;border-radius:9px;cursor:pointer;font-weight:700}.ghost,.cancel{border:1px solid #dbe4ef;background:#fff;color:#475569}.ghost:hover,.cancel:hover{border-color:#93c5fd;color:#1d4ed8}.scroll{flex:1;min-height:0;overflow:auto;padding:0 22px 22px 30px}.scroll::-webkit-scrollbar{width:10px}.scroll::-webkit-scrollbar-thumb{background:#cbd5e1;border:3px solid #f4f7fb;border-radius:99px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:13px}.model{min-height:165px;padding:15px;border:1px solid #e2e8f0;border-radius:15px;background:#fff;box-shadow:0 8px 20px rgba(15,23,42,.045);transition:transform .15s ease,box-shadow .15s ease}.model:hover{transform:translateY(-1px);box-shadow:0 12px 25px rgba(15,23,42,.08)}.model[hidden]{display:none}.model-head{display:flex;align-items:center;gap:10px}.model-title{min-width:0;flex:1}.model-title strong{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:15px}.model-title small{display:block;margin-top:3px;color:#94a3b8;font-family:Consolas,monospace;font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.model-head em{font-style:normal;padding:4px 7px;border-radius:99px;font-size:10px;font-weight:800}.official{color:#047857;background:#ecfdf5}.pending{color:#b45309;background:#fffbeb}.source{height:20px;margin-top:10px;color:#94a3b8;font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:7px;margin-top:11px;padding:9px;background:#f8fafc;border-radius:10px}.metrics label{display:block;text-align:center}.metrics label span{display:block;margin-bottom:4px;color:#64748b;font-size:10px;font-weight:800}.metrics input{width:100%;height:29px;border:1px solid #dbe4ef;border-radius:7px;background:#fff;color:#172033;text-align:center;font-size:12px;font-weight:700;outline:0}.metrics input:focus{border-color:#60a5fa;box-shadow:0 0 0 3px rgba(96,165,250,.16)}.switch{width:20px;height:20px;position:relative;display:block;flex:0 0 20px}.switch input{position:absolute;opacity:0}.switch span{display:block;width:20px;height:20px;border:2px solid #cbd5e1;border-radius:6px;background:#fff}.switch input:checked+span{border-color:#2563eb;background:#2563eb}.switch input:checked+span:after{content:'✓';display:block;color:#fff;font-size:14px;font-weight:900;line-height:16px;text-align:center}footer{flex:0 0 auto;display:flex;align-items:center;justify-content:space-between;padding:14px 30px;background:#fff;border-top:1px solid #e2e8f0;color:#94a3b8;font-size:11px}footer div{display:flex;gap:9px}.primary{border:0;background:#2563eb;color:#fff;box-shadow:0 6px 14px rgba(37,99,235,.2)}.primary:hover{background:#1d4ed8}@media(max-width:820px){.limits{grid-template-columns:1fr}.hero{padding:18px}.toolbar,.scroll,footer{padding-left:18px;padding-right:18px}.toolbar{flex-wrap:wrap}.search{width:100%}.status{order:5;flex-basis:100%}}
";
        }

        private static string H(string text)
        {
            return WebUtility.HtmlEncode(text ?? "");
        }

        private static double Number(object value, double fallback)
        {
            try { return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null) return fallback;
            try { return Convert.ToBoolean(value); } catch { return fallback; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { view.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
