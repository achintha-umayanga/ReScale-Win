using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
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
    private static Icon? _trayIconImage;
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

    // Tracks windows recently handled on show: hwnd -> Environment.TickCount64 of last handling.
    // A cooldown (not a permanent set) means recycled HWNDs are handled again once the cooldown expires,
    // and stale entries can be pruned by age without calling IsWindow on each one.
    private static readonly ConcurrentDictionary<IntPtr, long> _handledOnShow = new();
    private const long ShowCooldownMs = 3000; // covers the full launch-retry window below
    private static int _pruneThreshold = 64;

    // Launch retry schedule: cloaked (UWP splash / other virtual desktop) or still-tiny windows get re-checked.
    private static readonly int[] LaunchRetryDelaysMs = { 200, 400, 800 };

    // PID -> resolved process name (null = could not be opened), with a TTL to tolerate PID reuse.
    private readonly record struct PidEntry(string? Name, long Tick);
    private static readonly ConcurrentDictionary<uint, PidEntry> _pidCache = new();
    private const long PidCacheTtlMs = 30_000;
    private static readonly uint _ownPid = (uint)Environment.ProcessId;

    // Shell / system hosts that must never be resized or learned.
    private static readonly FrozenSet<string> ExcludedProcesses = new[]
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI",
        "TextInputHost", "LockApp", "LogonUI", "dwm", "ScreenClippingHost", "ShellHost",
        "PeopleExperienceHost", "Widgets", "WidgetService"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private const int HOTKEY_ID_ACTIVE = 9001;
    private const int HOTKEY_ID_ALL = 9002;

    private enum Candidate { Reject, NotReady, Ok }

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
        if (!WinApi.RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_ACTIVE, WinApi.MOD_CONTROL | WinApi.MOD_ALT, (uint)Keys.S))
            Log($"[WARN] Could not register Ctrl+Alt+S (error {Marshal.GetLastWin32Error()}). It is probably used by another app.");
        if (!WinApi.RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_ALL, WinApi.MOD_CONTROL | WinApi.MOD_ALT, (uint)Keys.A))
            Log($"[WARN] Could not register Ctrl+Alt+A (error {Marshal.GetLastWin32Error()}). It is probably used by another app.");

        _moveSizeDelegate = OnMoveSizeEnd;
        _showDelegate = OnWindowShow;

        // Hook when user finishes moving or resizing: EVENT_SYSTEM_MOVESIZEEND = 0x000B
        _hookMoveSize = WinApi.SetWinEventHook(
            WinApi.EVENT_SYSTEM_MOVESIZEEND, WinApi.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _moveSizeDelegate, 0, 0,
            WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS);
        if (_hookMoveSize == IntPtr.Zero) Log("[ERROR] SetWinEventHook(MOVESIZEEND) failed. Manual resizes will not be learned.");

        // Hook when a window is shown / created: EVENT_OBJECT_SHOW = 0x8002
        _hookShow = WinApi.SetWinEventHook(
            WinApi.EVENT_OBJECT_SHOW, WinApi.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _showDelegate, 0, 0,
            WinApi.WINEVENT_OUTOFCONTEXT | WinApi.WINEVENT_SKIPOWNPROCESS);
        if (_hookShow == IntPtr.Zero) Log("[ERROR] SetWinEventHook(OBJECT_SHOW) failed. New windows will not be normalized.");

        SetupTrayIcon();

        // Learn sizes of existing open windows without altering them
        InitialScanOpenWindows();

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
        _trayIconImage?.Dispose();
        GC.KeepAlive(_moveSizeDelegate);
        GC.KeepAlive(_showDelegate);
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
    private static long _logSize = -1; // tracked in memory; -1 = not yet read from disk

    private static void Log(string msg)
    {
        lock (_logLock)
        {
            try
            {
                // Read the file size from disk once, then track it in memory (no stat call per log line)
                if (_logSize < 0)
                {
                    var fi = new FileInfo(LogPath);
                    _logSize = fi.Exists ? fi.Length : 0;
                }

                if (_logSize > MaxLogBytes)
                {
                    string[] lines = File.ReadAllLines(LogPath);
                    int skip = lines.Length / 2;
                    File.WriteAllLines(LogPath, lines[skip..]);
                    _logSize = new FileInfo(LogPath).Length;
                }

                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
                File.AppendAllText(LogPath, line);
                _logSize += Encoding.UTF8.GetByteCount(line);
            }
            catch { _logSize = -1; }
        }
    }

    private static Icon LoadTrayIcon()
    {
        // 1) app.ico next to the exe (copied by the build), loaded at the tray's small-icon size for crisp rendering
        try
        {
            string icoPath = Path.Combine(AppDir, "app.ico");
            if (File.Exists(icoPath)) return new Icon(icoPath, SystemInformation.SmallIconSize);
        }
        catch { }

        // 2) Icon embedded in the exe via <ApplicationIcon>
        try
        {
            var embedded = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
            if (embedded != null) return embedded;
        }
        catch { }

        // 3) Last resort
        return (Icon)SystemIcons.Application.Clone();
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

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
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

    // ───────────────────────────── Rule math ─────────────────────────────

    /// <summary>The user's anchored size, scaled to <paramref name="dpi"/>. Always computed from the anchor, so no drift accumulates.</summary>
    private static (int W, int H) ScaleRule(WindowDpiRule rule, uint dpi)
    {
        double baseDpi = rule.SavedDpi > 0 ? rule.SavedDpi : 96;
        return ((int)Math.Round(rule.SavedWidth * (dpi / baseDpi)),
                (int)Math.Round(rule.SavedHeight * (dpi / baseDpi)));
    }

    private static bool Within(int actual, int expected, double tolerance) =>
        expected > 0 && Math.Abs(actual - expected) / (double)expected <= tolerance;

    private static bool GetWorkArea(IntPtr monitor, out WinApi.RECT work)
    {
        var monInfo = new WinApi.MONITORINFO { cbSize = Marshal.SizeOf<WinApi.MONITORINFO>() };
        bool ok = WinApi.GetMonitorInfoW(monitor, ref monInfo);
        work = monInfo.rcWork;
        return ok && work.Right > work.Left && work.Bottom > work.Top;
    }

    /// <summary>
    /// Event: User manually moved or resized a window.
    /// Record the window's physical dimensions along with the exact DPI of the monitor it sits on.
    /// </summary>
    private static void OnMoveSizeEnd(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        if (Classify(hwnd, out string? specialProc) != Candidate.Ok) return;

        var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
        if (!WinApi.GetWindowPlacement(hwnd, ref wp) || wp.showCmd != WinApi.SW_SHOWNORMAL) return;

        string? procName = specialProc ?? ResolveProcessName(hwnd);
        if (procName == null || ExcludedProcesses.Contains(procName)) return;

        if (!WinApi.GetWindowRect(hwnd, out WinApi.RECT rc)) return;
        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w <= 120 || h <= 100) return;

        IntPtr monitor = WinApi.MonitorFromWindow(hwnd, WinApi.MONITOR_DEFAULTTONEAREST);
        uint dpi = GetMonitorDpi(monitor);

        if (_rules.TryGetValue(procName, out var existing))
        {
            // Plain drag on the anchor's own DPI with an unchanged size: nothing to do (no log, no disk write).
            if (dpi == existing.SavedDpi && existing.SavedWidth == w && existing.SavedHeight == h)
            {
                if (existing.LastAppliedDpi != 0 && existing.LastAppliedDpi != dpi)
                {
                    _rules[procName] = existing with { LastAppliedDpi = dpi };
                    SaveRulesDebounced();
                }
                return;
            }

            // Plain drag on a different DPI where the size is just the anchor scaled to this DPI (by us or by
            // Windows' own WM_DPICHANGED handling), or that size clamped to the work area. That is a move,
            // not a user resize, so the anchor stays as the user's real size. Only record which DPI it now sits on.
            if (dpi != existing.SavedDpi)
            {
                var (ew, eh) = ScaleRule(existing, dpi);
                bool matchesScaled = Within(w, ew, 0.02) && Within(h, eh, 0.02);
                bool matchesClamped = false;
                if (GetWorkArea(monitor, out var work))
                {
                    int cw = Math.Min(ew, (work.Right - work.Left) - 20);
                    int ch = Math.Min(eh, (work.Bottom - work.Top) - 20);
                    matchesClamped = Within(w, cw, 0.02) && Within(h, ch, 0.02);
                }

                if (matchesScaled || matchesClamped)
                {
                    if (existing.LastAppliedDpi != dpi)
                    {
                        _rules[procName] = existing with { LastAppliedDpi = dpi };
                        SaveRulesDebounced();
                    }
                    return;
                }
            }
        }

        // A genuine user resize: re-anchor to the user's real size on this DPI.
        _rules[procName] = new WindowDpiRule
        {
            SavedWidth = w,
            SavedHeight = h,
            SavedDpi = dpi,
            LastAppliedDpi = 0,
            LastUpdatedUtc = DateTime.UtcNow
        };
        Log($"Learned size for {procName}: {w}x{h} on DPI {dpi}");
        SaveRulesDebounced();
    }

    /// <summary>
    /// Event: A window is opened / shown.
    /// </summary>
    private static void OnWindowShow(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;

        long now = Environment.TickCount64;
        if (_handledOnShow.TryGetValue(hwnd, out long last) && now - last < ShowCooldownMs) return;

        // Cheap structural filter first (style bits reject child windows before anything else).
        // NotReady (cloaked / still tiny) still gets scheduled so the retry loop can pick it up.
        if (Classify(hwnd, out _) == Candidate.Reject) return;

        _handledOnShow[hwnd] = now;

        // Prune by age (no IsWindow calls). Only when the map has doubled since the last prune,
        // and every entry expires after the cooldown, so the map cannot grow without bound.
        if (_handledOnShow.Count >= _pruneThreshold)
        {
            foreach (var kvp in _handledOnShow)
            {
                if (now - kvp.Value >= ShowCooldownMs) _handledOnShow.TryRemove(kvp.Key, out _);
            }
            _pruneThreshold = Math.Max(64, _handledOnShow.Count * 2);
        }

        _ = NormalizeAfterLaunchAsync(hwnd);
    }

    private static async Task NormalizeAfterLaunchAsync(IntPtr hwnd)
    {
        try
        {
            foreach (int delay in LaunchRetryDelaysMs)
            {
                // Give the window a moment to finish its launch (and UWP splash / DWM uncloak)
                await Task.Delay(delay).ConfigureAwait(false);

                switch (Classify(hwnd, out _))
                {
                    case Candidate.Reject: return;
                    case Candidate.NotReady: continue;
                    default:
                        NormalizeWindowDpi(hwnd, force: false);
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Launch normalization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a window opened on a monitor with a DIFFERENT DPI than where it was saved.
    /// ONLY resizes if there is an actual DPI mismatch (e.g. saved on 96 DPI, opened on 144 DPI).
    /// If it is on the same DPI, it leaves the window completely untouched, unless it came back
    /// at the size ReScale gave it on another monitor (then it is restored to the user's real size).
    /// </summary>
    public static bool NormalizeWindowDpi(IntPtr hwnd, bool force)
    {
        if (Classify(hwnd, out string? specialProc) != Candidate.Ok) return false;

        var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
        if (!WinApi.GetWindowPlacement(hwnd, ref wp)) return false;

        // Only handle normal (restored down) windows
        if (wp.showCmd != WinApi.SW_SHOWNORMAL) return false;

        string? procName = specialProc ?? ResolveProcessName(hwnd);
        if (procName == null || ExcludedProcesses.Contains(procName)) return false;

        IntPtr monitor = WinApi.MonitorFromWindow(hwnd, WinApi.MONITOR_DEFAULTTONEAREST);
        uint currentDpi = GetMonitorDpi(monitor);
        if (!GetWorkArea(monitor, out var work)) return false;
        int workW = work.Right - work.Left;
        int workH = work.Bottom - work.Top;

        if (!WinApi.GetWindowRect(hwnd, out WinApi.RECT currentRect)) return false;
        int curW = currentRect.Right - currentRect.Left;
        int curH = currentRect.Bottom - currentRect.Top;

        if (_rules.TryGetValue(procName, out var rule))
        {
            if (!force && currentDpi == rule.SavedDpi)
            {
                // CRITICAL CHECK:
                // On the SAME DPI where the size was saved, DO NOT TOUCH the window...
                if (rule.LastAppliedDpi == 0 || rule.LastAppliedDpi == rule.SavedDpi) return false;

                // ...unless it came back at the size we scaled it to on another DPI (stale scaled size).
                // One matching dimension is enough because the other may have been clamped to a smaller work area.
                var (pw, ph) = ScaleRule(rule, rule.LastAppliedDpi);
                if (!Within(curW, pw, 0.08) && !Within(curH, ph, 0.08)) return false;
            }

            // Target is always derived from the user's anchored size, so 96 -> 144 -> 96 returns to the exact original.
            var (targetW, targetH) = ScaleRule(rule, currentDpi);

            // Clamp only the on-screen placement. The anchor in the rule is never overwritten with clamped values.
            targetW = Math.Min(targetW, workW - 20);
            targetH = Math.Min(targetH, workH - 20);
            if (targetW <= 0 || targetH <= 0) return false;

            // Only adjust if the window size differs meaningfully from target scaled size
            if (!force && Within(curW, targetW, 0.08) && Within(curH, targetH, 0.08))
            {
                // Already correct for this DPI; remember where it is so a later return is recognized.
                if (rule.LastAppliedDpi != currentDpi && currentDpi != rule.SavedDpi)
                {
                    _rules[procName] = rule with { LastAppliedDpi = currentDpi };
                    SaveRulesDebounced();
                }
                return false;
            }

            int x = currentRect.Left;
            int y = currentRect.Top;
            if (x + targetW > work.Right) x = work.Right - targetW;
            if (y + targetH > work.Bottom) y = work.Bottom - targetH;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;

            // Adjust ONLY the outer window size cleanly.
            // Do not post fake messages. Let Windows automatically adjust internal structures.
            // SWP_ASYNCWINDOWPOS: never block on a hung target app.
            bool ok = WinApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, targetW, targetH,
                WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE | WinApi.SWP_ASYNCWINDOWPOS);

            if (ok)
            {
                // Anchor (SavedWidth/Height/Dpi) stays untouched; only record which DPI we applied.
                // Replace the rule object atomically (never mutate in place) so a concurrent save never sees a half-updated rule.
                _rules[procName] = rule with { LastAppliedDpi = currentDpi };
                SaveRulesDebounced();

                double ratio = currentDpi / (double)(rule.SavedDpi > 0 ? rule.SavedDpi : 96);
                Log($"[SUCCESS] Scaled {procName} for DPI change: {curW}x{curH} -> {targetW}x{targetH} (DPI {currentDpi}, Ratio: {ratio:F2})");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Log($"[WARN] SetWindowPos failed for {procName} (Error: {err})");
            }

            return ok;
        }

        // First time seeing this window: learn its size on this monitor's DPI
        if (curW > 120 && curH > 100)
        {
            _rules[procName] = new WindowDpiRule
            {
                SavedWidth = curW,
                SavedHeight = curH,
                SavedDpi = currentDpi,
                LastAppliedDpi = 0,
                LastUpdatedUtc = DateTime.UtcNow
            };
            Log($"Recorded initial size for {procName}: {curW}x{curH} on DPI {currentDpi}");
            SaveRulesDebounced();
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
        // Coalesce rapid-fire WM_DISPLAYCHANGE events when plugging/unplugging monitors
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
            if (Classify(hwnd, out string? specialProc) != Candidate.Ok) return true;

            var wp = new WinApi.WINDOWPLACEMENT { length = Marshal.SizeOf<WinApi.WINDOWPLACEMENT>() };
            if (!WinApi.GetWindowPlacement(hwnd, ref wp) || wp.showCmd != WinApi.SW_SHOWNORMAL) return true;

            string? procName = specialProc ?? ResolveProcessName(hwnd);
            if (procName == null || ExcludedProcesses.Contains(procName) || _rules.ContainsKey(procName)) return true;

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
                        LastAppliedDpi = 0,
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    Log($"Initial scan recorded {procName}: {w}x{h} (DPI {dpi})");
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

    // ───────────────────────────── Window filter ─────────────────────────────

    /// <summary>
    /// Single-pass window filter, cheapest checks first. Allocation-free (class name is read into a stack buffer).
    /// Ok       = top-level, resizable, unowned app window that is visible and uncloaked.
    /// NotReady = structurally valid but cloaked or still tiny (launching); worth retrying shortly.
    /// Reject   = never a candidate.
    /// <paramref name="specialProc"/> is set for well-known shell classes whose process name is fixed.
    /// </summary>
    private static unsafe Candidate Classify(IntPtr hwnd, out string? specialProc)
    {
        specialProc = null;

        // One call covers child / visible / minimized / resizable. Invalid HWNDs return 0, so they fail WS_VISIBLE.
        int style = WinApi.GetWindowLongW(hwnd, WinApi.GWL_STYLE);
        if ((style & WinApi.WS_CHILD) != 0) return Candidate.Reject;
        if ((style & WinApi.WS_VISIBLE) == 0) return Candidate.Reject;
        if ((style & WinApi.WS_MINIMIZE) != 0) return Candidate.Reject;
        if ((style & WinApi.WS_THICKFRAME) == 0) return Candidate.Reject; // fixed-size dialogs, splash screens, popups

        int exStyle = WinApi.GetWindowLongW(hwnd, WinApi.GWL_EXSTYLE);
        if ((exStyle & (WinApi.WS_EX_TOOLWINDOW | WinApi.WS_EX_NOACTIVATE)) != 0) return Candidate.Reject;

        if (WinApi.GetAncestor(hwnd, WinApi.GA_ROOT) != hwnd) return Candidate.Reject;

        // Owned windows are dialogs / secondary popups unless they explicitly ask to be app windows.
        if (WinApi.GetWindow(hwnd, WinApi.GW_OWNER) != IntPtr.Zero && (exStyle & WinApi.WS_EX_APPWINDOW) == 0)
            return Candidate.Reject;

        char* buf = stackalloc char[256];
        int len = WinApi.GetClassNameW(hwnd, buf, 256);
        if (len <= 0) return Candidate.Reject;
        var cls = new ReadOnlySpan<char>(buf, len);

        if (cls is "TaskManagerWindow") specialProc = "Taskmgr";
        else if (cls is "CabinetWClass") specialProc = "explorer";
        else if (cls is "Notepad") specialProc = "notepad";
        else if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
                 "Windows.UI.Core.CoreWindow" or "DV2ControlHost" or "MsgrIMEWindowClass" or "SysShadow" or
                 "TaskListThumbnailWnd" or "SideBar_HTMLHostWindow" or "XamlExplorerHostIslandWindow" or
                 "PopupHost" or "Droplist" or "tooltips_class32" or "#32768")
        {
            return Candidate.Reject;
        }

        // Cloaked: suspended UWP/WinUI apps, windows on other virtual desktops, apps still showing a splash.
        if (WinApi.DwmGetWindowAttribute(hwnd, WinApi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return Candidate.NotReady;

        if (!WinApi.GetWindowRect(hwnd, out WinApi.RECT rc)) return Candidate.Reject;
        if (rc.Right - rc.Left < 120 || rc.Bottom - rc.Top < 100) return Candidate.NotReady;

        return Candidate.Ok;
    }

    // ───────────────────────────── Process name ─────────────────────────────

    /// <summary>
    /// Resolves the owning process name via OpenProcess + QueryFullProcessImageNameW, cached per PID with a TTL.
    /// Failures are cached too (null), so protected processes are not re-queried on every event.
    /// There is deliberately no Process.GetProcessById fallback: if PROCESS_QUERY_LIMITED_INFORMATION is denied,
    /// the process is protected and its windows could not be resized anyway.
    /// </summary>
    private static string? ResolveProcessName(IntPtr hwnd)
    {
        WinApi.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _ownPid) return null;

        long now = Environment.TickCount64;
        if (_pidCache.TryGetValue(pid, out var entry) && now - entry.Tick < PidCacheTtlMs)
            return entry.Name;

        string? name = QueryProcessName(pid);

        if (_pidCache.Count > 256)
        {
            foreach (var kvp in _pidCache)
            {
                if (now - kvp.Value.Tick >= PidCacheTtlMs) _pidCache.TryRemove(kvp.Key, out _);
            }
        }
        _pidCache[pid] = new PidEntry(name, now);
        return name;
    }

    private static unsafe string? QueryProcessName(uint pid)
    {
        IntPtr hProc = WinApi.OpenProcess(WinApi.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            const int Cap = 1024;
            char* buf = stackalloc char[Cap];
            int size = Cap;
            if (!WinApi.QueryFullProcessImageNameW(hProc, 0, buf, ref size) || size <= 0) return null;

            var path = new ReadOnlySpan<char>(buf, size);
            int slash = path.LastIndexOf('\\');
            if (slash >= 0) path = path[(slash + 1)..];
            int dot = path.LastIndexOf('.');
            if (dot > 0) path = path[..dot];
            return path.Length > 0 ? new string(path) : null;
        }
        finally
        {
            WinApi.CloseHandle(hProc);
        }
    }

    // ───────────────────────────── Persistence ─────────────────────────────

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
        catch (Exception ex)
        {
            Log($"[ERROR] Failed to load rules: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Source-generated JSON + non-blocking OS timer debounce + atomic config save
    private static readonly System.Threading.Timer _saveTimer = new(_ => SaveRules(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly object _saveLock = new();

    private static void SaveRulesDebounced()
    {
        try
        {
            _saveTimer.Change(2000, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { } // shutting down; final SaveRules() runs in Main
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
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to save rules: {ex.GetType().Name}: {ex.Message}");
            }
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
        catch (Exception ex)
        {
            Log($"[ERROR] Failed to update auto-start: {ex.Message}");
        }
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
                        bool res = NormalizeWindowDpi(fg, force: true);
                        _trayIcon?.ShowBalloonTip(1000, "ReScale", res ? "Normalized active window." : "Active window can't be normalized (not a resizable app window).", ToolTipIcon.Info);
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

/// <summary>
/// SavedWidth/SavedHeight/SavedDpi = the user's real size, anchored at the DPI it was set on (only changed by a
/// genuine user resize). LastAppliedDpi = the DPI the window was last scaled to / known to be at (0 = never scaled).
/// </summary>
public record WindowDpiRule
{
    public int SavedWidth { get; set; }
    public int SavedHeight { get; set; }
    public uint SavedDpi { get; set; } = 96;
    public uint LastAppliedDpi { get; set; }
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

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_MINIMIZE = 0x20000000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint GA_ROOT = 2;
    public const uint GW_OWNER = 4;
    public const int DWMWA_CLOAKED = 14;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int SW_SHOWNORMAL = 1;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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

    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern unsafe int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", ExactSpelling = true)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern unsafe bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);
}
