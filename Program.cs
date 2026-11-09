using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ReScale;

internal static class Program
{
    private static NotifyIcon? _trayIcon;
    private static ContextMenuStrip? _trayMenu;
    private static IntPtr _hookMoveSize = IntPtr.Zero;
    private static IntPtr _hookShow = IntPtr.Zero;
    private static WinApi.WinEventDelegate? _moveSizeDelegate;
    private static WinApi.WinEventDelegate? _showDelegate;
    private static MessageOnlyWindow? _msgWindow;

    // ProcessName -> Saved Window Dimensions & DPI
    private static readonly ConcurrentDictionary<string, WindowDpiRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(AppDir, "window_dpi_rules.json");
    private static readonly string LogPath = Path.Combine(AppDir, "activity.log");

    // Tracks windows already handled on launch to prevent double-resizing: hwnd -> true
    private static readonly ConcurrentDictionary<IntPtr, bool> _handledOnShow = new();

    private const int HOTKEY_ID_ACTIVE = 9001;
    private const int HOTKEY_ID_ALL = 9002;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "ReScale_GlobalDpiNormalizer_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ReScale is already running in your system tray.", "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Log("ReScale started. Administrator: " + IsRunningAsAdmin());
        LoadRules();

        _msgWindow = new MessageOnlyWindow();

        // Hotkeys: Ctrl+Alt+S (Active window), Ctrl+Alt+A (All windows)
        WinApi.RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_ACTIVE, WinApi.MOD_CONTROL | WinApi.MOD_ALT, (uint)Keys.S);
        WinApi.RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_ALL, WinApi.MOD_CONTROL | WinApi.MOD_ALT, (uint)Keys.A);

        _moveSizeDelegate = OnMoveSizeEnd;
        _showDelegate = OnWindowShow;

        // Hook when user finishes moving or resizing: EVENT_SYSTEM_MOVESIZEEND = 0x000B
        _hookMoveSize = WinApi.SetWinEventHook(
            WinApi.EVENT_SYSTEM_MOVESIZEEND, WinApi.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _moveSizeDelegate, 0, 0,
            WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS);

        // Hook when a window is shown / created: EVENT_OBJECT_SHOW = 0x8002
        _hookShow = WinApi.SetWinEventHook(
            WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _showDelegate, 0, 0,
            WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS);

        SetupTrayIcon();

        // Learn sizes of existing open windows without altering them
        InitialScanOpenWindows();

        // Optimization 7: Trim startup working set pages before entering idle tray message loop
        try { WinApi.SetProcessWorkingSetSize(WinApi.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); } catch { }

        Application.Run();

        // Cleanup
        _saveTimer.Dispose();
        SaveRules();
        if (_hookMoveSize != IntPtr.Zero) WinApi.UnhookWinEvent(_hookMoveSize);
        if (_hookShow != IntPtr.Zero) WinApi.UnhookWinEvent(_hookShow);
        if (_msgWindow != null)
        {
            WinApi.UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID_ACTIVE);
            WinApi.UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID_ALL);
            _msgWindow.Dispose();
        }
        _trayIcon?.Dispose();
    }

    private static readonly bool _isAdmin = CheckIsRunningAsAdmin();
    private static bool IsRunningAsAdmin() => _isAdmin;

    private static bool CheckIsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static readonly object _logLock = new();
    private const long MaxLogBytes = 256 * 1024; // 256 KB max log size

    private static void Log(string msg)
    {
        lock (_logLock)
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    string[] lines = File.ReadAllLines(LogPath);
                    int skip = lines.Length / 2;
                    File.WriteAllLines(LogPath, lines[skip..]);
                }
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            }
            catch { }
        }
    }

    private static void SetupTrayIcon()
    {
        _trayMenu = new ContextMenuStrip();

        string adminStatus = IsRunningAsAdmin() ? "(Admin)" : "(Standard)";
        var header = new ToolStripMenuItem($"● ReScale {adminStatus}")
        {
            Font = new Font(_trayMenu.Font, FontStyle.Bold),
            Enabled = false
        };
        _trayMenu.Items.Add(header);
        _trayMenu.Items.Add(new ToolStripSeparator());

        _trayMenu.Items.Add("Normalize Active Window (Ctrl+Alt+S)", null, (s, e) =>
        {
            IntPtr hwnd = WinApi.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                bool res = NormalizeWindowDpi(hwnd, force: true);
                _trayIcon?.ShowBalloonTip(1000, "ReScale", res ? "Normalized active window size." : "Window already matches active monitor.", ToolTipIcon.Info);
            }
        });

        _trayMenu.Items.Add("Normalize All Open Windows (Ctrl+Alt+A)", null, (s, e) =>
        {
            int count = NormalizeAllWindows();
            _trayIcon?.ShowBalloonTip(1200, "ReScale", $"Checked open windows ({count} scaled).", ToolTipIcon.Info);
        });

        _trayMenu.Items.Add(new ToolStripSeparator());

        var autoStartItem = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStartEnabled() };
        autoStartItem.Click += (s, e) =>
        {
            bool newState = !autoStartItem.Checked;
            SetAutoStart(newState);
            autoStartItem.Checked = newState;
        };
        _trayMenu.Items.Add(autoStartItem);

        _trayMenu.Items.Add("View Activity Log", null, (s, e) =>
        {
            try
            {
                if (File.Exists(LogPath))
                {
                    Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
                }
            }
            catch { }
        });

        _trayMenu.Items.Add("Open Saved Rules (JSON)", null, (s, e) =>
        {
            try
            {
                SaveRules();
                if (File.Exists(ConfigPath))
                {
                    Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true });
                }
            }
            catch { }
        });

        _trayMenu.Items.Add("Reset Learned Memory", null, (s, e) =>
        {
            _rules.Clear();
            SaveRules();
            InitialScanOpenWindows();
            _trayIcon?.ShowBalloonTip(1200, "ReScale", "Cleared learned memory. Re-learning current sizes.", ToolTipIcon.Info);
        });

        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (s, e) => Application.Exit());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Text = $"ReScale {adminStatus}",
            Visible = true
        };

        _trayIcon.DoubleClick += (s, e) =>
        {
            int count = NormalizeAllWindows();
            _trayIcon.ShowBalloonTip(1200, "ReScale", $"Checked windows ({count} scaled).", ToolTipIcon.Info);
        };
    }

    /// <summary>
    /// Event: User manually moved or resized a window.
    /// Record the window's physical dimensions along with the exact DPI of the monitor it sits on.
    /// </summary>
    private static void OnMoveSizeEnd(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || !IsCandidateAppWindow(hwnd, out string cls)) return;

        var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
        if (!WinApi.GetWindowPlacement(hwnd, ref wp) || wp.showCmd != WinApi.SW_SHOWNORMAL) return;

        string? procName = GetProcessName(hwnd, cls);
        if (string.IsNullOrEmpty(procName)) return;

        IntPtr monitor = WinApi.MonitorFromWindow(hwnd, WinApi.MONITOR_DEFAULTTONEAREST);
        uint dpi = GetMonitorDpi(monitor);

        if (WinApi.GetWindowRect(hwnd, out WinApi.RECT rc))
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;

            if (w > 120 && h > 100)
            {
                // Optimization 1: Skip redundant rule updates, logging, and disk writes if size & DPI haven't changed (window was just moved)
                if (_rules.TryGetValue(procName, out var existingRule) &&
                    existingRule.SavedWidth == w &&
                    existingRule.SavedHeight == h &&
                    existingRule.SavedDpi == dpi)
                {
                    return;
                }

                _rules[procName] = new WindowDpiRule
                {
                    SavedWidth = w,
                    SavedHeight = h,
                    SavedDpi = dpi,
                    LastUpdatedUtc = DateTime.UtcNow
                };
                Log($"Learned size for {procName}: {w}x{h} on DPI {dpi}");
                SaveRulesDebounced();
            }
        }
    }

    /// <summary>
    /// Event: A window is opened / created.
    /// Only triggers on initial window launch.
    /// </summary>
    private static void OnWindowShow(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Optimization 3: Fast integer & memory lookup BEFORE making Win32 P/Invoke calls or allocations
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (_handledOnShow.ContainsKey(hwnd)) return;
        if (!IsCandidateAppWindow(hwnd)) return;

        if (_handledOnShow.Count > 64)
        {
            foreach (var key in _handledOnShow.Keys)
            {
                if (!WinApi.IsWindow(key)) _handledOnShow.TryRemove(key, out _);
            }
        }

        if (_handledOnShow.TryAdd(hwnd, true))
        {
            Task.Run(async () =>
            {
                // Give window a moment to finish initial launch painting
                await Task.Delay(200);
                if (!WinApi.IsWindow(hwnd)) return;
                NormalizeWindowDpi(hwnd, force: false);
            });
        }
    }

    /// <summary>
    /// Checks if a window opened on a monitor with a DIFFERENT DPI than where it was saved.
    /// ONLY resizes if there is an actual DPI mismatch (e.g. saved on 96 DPI, opened on 144 DPI).
    /// If it is on the same DPI, it leaves the window completely untouched!
    /// </summary>
    public static bool NormalizeWindowDpi(IntPtr hwnd, bool force)
    {
        if (!IsCandidateAppWindow(hwnd, out string cls)) return false;

        var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
        if (!WinApi.GetWindowPlacement(hwnd, ref wp)) return false;

        // Only handle normal (restored down) windows
        if (wp.showCmd != WinApi.SW_SHOWNORMAL) return false;

        string? procName = GetProcessName(hwnd, cls);
        if (string.IsNullOrEmpty(procName)) return false;

        IntPtr monitor = WinApi.MonitorFromWindow(hwnd, WinApi.MONITOR_DEFAULTTONEAREST);
        uint currentDpi = GetMonitorDpi(monitor);

        var monInfo = new WinApi.MONITORINFO { cbSize = Marshal.SizeOf<WinApi.MONITORINFO>() };
        WinApi.GetMonitorInfo(monitor, ref monInfo);
        int workW = monInfo.rcWork.Right - monInfo.rcWork.Left;
        int workH = monInfo.rcWork.Bottom - monInfo.rcWork.Top;

        if (workW <= 0 || workH <= 0) return false;

        if (_rules.TryGetValue(procName, out var rule))
        {
            // CRITICAL CHECK:
            // If the window is on the SAME DPI monitor where it was saved, DO NOT TOUCH IT!
            // Let the window and Windows manage its natural size and internal layout.
            if (!force && currentDpi == rule.SavedDpi)
            {
                return false;
            }

            // An actual DPI change occurred (e.g. saved on 96 DPI, opened on 144 DPI)!
            double dpiRatio = (double)currentDpi / (rule.SavedDpi > 0 ? rule.SavedDpi : 96);

            int targetW = (int)Math.Round(rule.SavedWidth * dpiRatio);
            int targetH = (int)Math.Round(rule.SavedHeight * dpiRatio);

            targetW = Math.Min(targetW, workW - 20);
            targetH = Math.Min(targetH, workH - 20);

            if (!WinApi.GetWindowRect(hwnd, out WinApi.RECT currentRect)) return false;
            int curW = currentRect.Right - currentRect.Left;
            int curH = currentRect.Bottom - currentRect.Top;

            double diffW = Math.Abs(curW - targetW) / (double)targetW;
            double diffH = Math.Abs(curH - targetH) / (double)targetH;

            // Only adjust if the window size differs meaningfully from target scaled size
            if (force || diffW > 0.08 || diffH > 0.08)
            {
                int x = currentRect.Left;
                int y = currentRect.Top;

                if (x + targetW > monInfo.rcWork.Right) x = monInfo.rcWork.Right - targetW;
                if (y + targetH > monInfo.rcWork.Bottom) y = monInfo.rcWork.Bottom - targetH;
                if (x < monInfo.rcWork.Left) x = monInfo.rcWork.Left;
                if (y < monInfo.rcWork.Top) y = monInfo.rcWork.Top;

                // Adjust ONLY the outer window size cleanly.
                // Do not post fake messages. Let Windows automatically adjust internal structures.
                bool ok = WinApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, targetW, targetH,
                    WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);

                if (ok)
                {
                    // Calibrate rule to the new monitor DPI so it stays untouched while on this monitor
                    rule.SavedWidth = targetW;
                    rule.SavedHeight = targetH;
                    rule.SavedDpi = currentDpi;
                    rule.LastUpdatedUtc = DateTime.UtcNow;
                    SaveRulesDebounced();

                    Log($"[SUCCESS] Scaled {procName} for DPI change: {curW}x{curH} -> {targetW}x{targetH} (DPI {currentDpi}, Ratio: {dpiRatio:F2})");
                }
                else
                {
                    uint err = WinApi.GetLastError();
                    Log($"[WARN] SetWindowPos failed for {procName} (Error: {err})");
                }

                return ok;
            }
        }
        else
        {
            // First time seeing this window: learn its size on this monitor's DPI
            if (WinApi.GetWindowRect(hwnd, out WinApi.RECT currentRect))
            {
                int w = currentRect.Right - currentRect.Left;
                int h = currentRect.Bottom - currentRect.Top;
                if (w > 120 && h > 100)
                {
                    _rules[procName] = new WindowDpiRule
                    {
                        SavedWidth = w,
                        SavedHeight = h,
                        SavedDpi = currentDpi,
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    Log($"Recorded initial size for {procName}: {w}x{h} on DPI {currentDpi}");
                    SaveRulesDebounced();
                }
            }
        }

        return false;
    }

    public static int NormalizeAllWindows()
    {
        int count = 0;
        WinApi.EnumWindows((hwnd, lParam) =>
        {
            if (NormalizeWindowDpi(hwnd, force: false))
            {
                count++;
            }
            return true;
        }, IntPtr.Zero);

        return count;
    }

    private static int _topologyChangeVersion = 0;
    public static void OnDisplayTopologyChanged()
    {
        // Optimization 2: Coalesce rapid-fire WM_DISPLAYCHANGE events when plugging/unplugging monitors
        int version = Interlocked.Increment(ref _topologyChangeVersion);
        _handledOnShow.Clear();

        Task.Run(async () =>
        {
            await Task.Delay(500);
            if (Volatile.Read(ref _topologyChangeVersion) != version) return;

            Log("Display topology changed (monitor connect/disconnect).");
            NormalizeAllWindows();

            await Task.Delay(1000);
            if (Volatile.Read(ref _topologyChangeVersion) != version) return;

            NormalizeAllWindows();
        });
    }

    private static void InitialScanOpenWindows()
    {
        WinApi.EnumWindows((hwnd, lParam) =>
        {
            if (IsCandidateAppWindow(hwnd, out string cls))
            {
                var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
                if (WinApi.GetWindowPlacement(hwnd, ref wp) && wp.showCmd == WinApi.SW_SHOWNORMAL)
                {
                    string? procName = GetProcessName(hwnd, cls);
                    if (!string.IsNullOrEmpty(procName) && !_rules.ContainsKey(procName))
                    {
                        if (WinApi.GetWindowRect(hwnd, out WinApi.RECT rc))
                        {
                            int w = rc.Right - rc.Left;
                            int h = rc.Bottom - rc.Top;
                            if (w > 120 && h > 100)
                            {
                                IntPtr mon = WinApi.MonitorFromWindow(hwnd, WinApi.MONITOR_DEFAULTTONEAREST);
                                uint dpi = GetMonitorDpi(mon);

                                _rules[procName] = new WindowDpiRule
                                {
                                    SavedWidth = w,
                                    SavedHeight = h,
                                    SavedDpi = dpi,
                                    LastUpdatedUtc = DateTime.UtcNow
                                };
                                Log($"Initial scan recorded {procName}: {w}x{h} (DPI {dpi})");
                            }
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        SaveRulesDebounced();
    }

    private static uint GetMonitorDpi(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero) return 96;
        int result = WinApi.GetDpiForMonitor(hMonitor, WinApi.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
        if (result == 0 && dpiX > 0) return dpiX;
        return 96;
    }

    [ThreadStatic]
    private static StringBuilder? _sharedSb;

    private static StringBuilder GetSharedStringBuilder(int minCapacity = 260)
    {
        var sb = _sharedSb;
        if (sb == null)
        {
            sb = new StringBuilder(minCapacity);
            _sharedSb = sb;
        }
        else
        {
            sb.Clear();
            if (sb.Capacity < minCapacity) sb.EnsureCapacity(minCapacity);
        }
        return sb;
    }

    private static string? GetProcessName(IntPtr hwnd, string? className = null)
    {
        string cls = className ?? string.Empty;
        if (string.IsNullOrEmpty(cls))
        {
            var sb = GetSharedStringBuilder(128);
            WinApi.GetClassName(hwnd, sb, sb.Capacity);
            cls = sb.ToString();
        }

        if (cls == "TaskManagerWindow") return "Taskmgr";
        if (cls == "CabinetWClass") return "explorer";
        if (cls == "Notepad") return "notepad";

        WinApi.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        // Optimization 5: Fast native QueryFullProcessImageName lookup (avoids heavy Process.GetProcessById allocations & exceptions)
        IntPtr hProc = WinApi.OpenProcess(WinApi.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != IntPtr.Zero)
        {
            try
            {
                var sb = GetSharedStringBuilder(260);
                int capacity = sb.Capacity;
                if (WinApi.QueryFullProcessImageName(hProc, 0, sb, ref capacity) && capacity > 0)
                {
                    return Path.GetFileNameWithoutExtension(sb.ToString());
                }
            }
            finally
            {
                WinApi.CloseHandle(hProc);
            }
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCandidateAppWindow(IntPtr hwnd) => IsCandidateAppWindow(hwnd, out _);

    private static bool IsCandidateAppWindow(IntPtr hwnd, out string className)
    {
        className = string.Empty;
        if (!WinApi.IsWindow(hwnd) || !WinApi.IsWindowVisible(hwnd) || WinApi.IsIconic(hwnd))
            return false;

        int exStyle = WinApi.GetWindowLong(hwnd, WinApi.GWL_EXSTYLE);
        if ((exStyle & WinApi.WS_EX_TOOLWINDOW) != 0)
            return false;

        var sb = GetSharedStringBuilder(128);
        WinApi.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        className = cls;

        if (cls is "TaskManagerWindow" or "CabinetWClass" or "Notepad")
            return true;

        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
            "Windows.UI.Core.CoreWindow" or "DV2ControlHost" or "MsgrIMEWindowClass" or "SysShadow" or
            "TaskListThumbnailWnd" or "SideBar_HTMLHostWindow" or "XamlExplorerHostIslandWindow" or
            "PopupHost" or "Droplist" or "tooltips_class32" or "#32768")
        {
            return false;
        }

        if (WinApi.GetWindowRect(hwnd, out WinApi.RECT rc))
        {
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w < 120 || h < 100) return false;
        }

        return true;
    }

    private static void LoadRules()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize(json, ReScaleJsonContext.Default.DictionaryStringWindowDpiRule);
                if (loaded != null)
                {
                    foreach (var kvp in loaded)
                    {
                        _rules[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch { }
    }

    // Optimization 6 & 8: Source-generated JSON + non-blocking OS timer debounce + atomic config save
    private static readonly System.Threading.Timer _saveTimer = new(_ => SaveRules(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly object _saveLock = new();

    private static void SaveRulesDebounced()
    {
        try
        {
            _saveTimer.Change(2000, Timeout.Infinite);
        }
        catch { }
    }

    private static void SaveRules()
    {
        lock (_saveLock)
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_rules, ReScaleJsonContext.Default.ConcurrentDictionaryStringWindowDpiRule);
                string tmpPath = ConfigPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, ConfigPath, overwrite: true);
            }
            catch { }
        }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("ReScale") != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue("ReScale", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("ReScale", false);
            }
        }
        catch { }
    }

    private class MessageOnlyWindow : NativeWindow, IDisposable
    {
        public MessageOnlyWindow()
        {
            CreateHandle(new CreateParams { Parent = WinApi.HWND_MESSAGE });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WinApi.WM_DISPLAYCHANGE)
            {
                OnDisplayTopologyChanged();
            }
            else if (m.Msg == WinApi.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID_ACTIVE)
                {
                    IntPtr fg = WinApi.GetForegroundWindow();
                    if (fg != IntPtr.Zero)
                    {
                        NormalizeWindowDpi(fg, force: true);
                        _trayIcon?.ShowBalloonTip(1000, "ReScale", "Normalized active window.", ToolTipIcon.Info);
                    }
                }
                else if (id == HOTKEY_ID_ALL)
                {
                    int c = NormalizeAllWindows();
                    _trayIcon?.ShowBalloonTip(1200, "ReScale", $"Normalized {c} windows.", ToolTipIcon.Info);
                }
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}

public class WindowDpiRule
{
    public int SavedWidth { get; set; }
    public int SavedHeight { get; set; }
    public uint SavedDpi { get; set; } = 96;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, WindowDpiRule>))]
[JsonSerializable(typeof(ConcurrentDictionary<string, WindowDpiRule>))]
[JsonSerializable(typeof(WindowDpiRule))]
internal partial class ReScaleJsonContext : JsonSerializerContext
{
}

internal static class WinApi
{
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_HOTKEY = 0x0312;
    public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;

    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 2;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int SW_SHOWNORMAL = 1;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("kernel32.dll")] public static extern uint GetLastError();
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
}
