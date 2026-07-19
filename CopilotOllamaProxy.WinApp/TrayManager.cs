// TrayManager.cs
// System-tray icon + Win32 console window management.
//
// Strategy:
//   • WinExe process → no console at birth; AllocConsole() creates one.
//   • Streams are wired via CONOUT$ + SetStdHandle so *all* output
//     (Console.Write, ASP.NET ILogger, native WriteFile on stdout) flows in.
//   • SetConsoleCtrlHandler intercepts the close button (X):
//       – hides the window,  returns TRUE  → process stays alive.
//   • ShowConsole() heals itself: if the window was destroyed by a modern
//     conhost / Windows Terminal, it calls FreeConsole + AllocConsole
//     and re-wires streams so output continues seamlessly.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopilotOllamaProxy;

internal sealed class TrayManager : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    private const int    SW_HIDE           = 0;
    private const int    SW_SHOW           = 5;
    private const uint   CTRL_CLOSE_EVENT  = 2;
    private const int    WH_KEYBOARD_LL    = 13;
    private const int    WM_SYSKEYDOWN     = 0x0104;
    private const uint   VK_F4             = 0x73;
    private const uint   GENERIC_WRITE    = 0x40000000;
    private const uint   GENERIC_READ     = 0x80000000;
    private const uint   FILE_SHARE_WRITE = 0x00000002;
    private const uint   OPEN_EXISTING    = 3;
    private const int    STD_OUTPUT_HANDLE = -11;
    private const int    STD_ERROR_HANDLE  = -12;
    // SC_CLOSE / MF_GRAYED – used to grey out the X button so the user sees
    // that clicking X will hide rather than kill (optional visual hint).
    private const uint   SC_CLOSE         = 0xF060;
    private const uint   MF_BYCOMMAND                  = 0x00000000;
    private const uint   MF_GRAYED                     = 0x00000001;
    private const uint   ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly NotifyIcon             _trayIcon;
    private readonly ConsoleCtrlDelegate    _ctrlHandler;  // must stay rooted
    private readonly LowLevelKeyboardProc   _keyboardProc; // must stay rooted
    private          IntPtr                 _keyboardHook;
    private          bool                   _disposed;

    // ── Constructor ───────────────────────────────────────────────────────────
    private TrayManager()
    {
        InitConsole();

        _ctrlHandler = OnConsoleCtrl;
        SetConsoleCtrlHandler(_ctrlHandler, add: true);

        // ── Low-level keyboard hook (intercept Alt+F4 on the console window) ──
        _keyboardProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            _keyboardProc,
            GetModuleHandle(null),
            0);

        // ── Context menu ──────────────────────────────────────────────────────
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("Show Console")
        {
            // Bold font — the conventional visual marker for the default menu item.
            Font = new System.Drawing.Font(SystemFonts.MenuFont ?? new System.Drawing.Font("Segoe UI", 9f), System.Drawing.FontStyle.Bold),
        };
        showItem.Click += (_, _) => ShowConsole();
        menu.Items.Add(showItem);

        var hideItem = new ToolStripMenuItem("Hide Console");
        hideItem.Click += (_, _) => HideConsole();
        menu.Items.Add(hideItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        // ── Tray icon ─────────────────────────────────────────────────────────
        _trayIcon = new NotifyIcon
        {
            Icon             = LoadEmbeddedIcon(),
            Text             = "CopilotOllamaProxy",
            ContextMenuStrip = menu,
            Visible          = true,
        };

        _trayIcon.DoubleClick += (_, _) => ShowConsole();
    }

    // ── Load embedded icon ────────────────────────────────────────────────────
    private static System.Drawing.Icon LoadEmbeddedIcon()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = "CopilotOllamaProxy.WinApp.app.ico";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return SystemIcons.Application;
        return new System.Drawing.Icon(stream);
    }

    // ── Console init / recovery ───────────────────────────────────────────────

    /// <summary>
    /// Allocates a Win32 console, wires stdout/stderr to CONOUT$ so that
    /// all Console.* and native WriteFile(stdout) calls reach the window,
    /// then hides the window immediately.
    /// </summary>
    private static void InitConsole()
    {
        AllocConsole();
        RewireStreams();

        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
        {
            // Grey out the X button so users know clicking it hides, not kills.
            var sysMenu = GetSystemMenu(hwnd, false);
            EnableMenuItem(sysMenu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED);
            DrawMenuBar(hwnd);

            ShowWindow(hwnd, SW_HIDE);
        }
    }

    /// <summary>
    /// Opens CONOUT$ via Win32 CreateFile (reliable after AllocConsole in a
    /// WinExe process), enables ANSI color processing, and points Console.Out
    /// / Console.Error at it. SetStdHandle keeps native callers in sync too.
    /// </summary>
    private static void RewireStreams()
    {
        var rawHandle = CreateFileW(
            "CONOUT$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (rawHandle == new IntPtr(-1)) // INVALID_HANDLE_VALUE
            return;

        SetStdHandle(STD_OUTPUT_HANDLE, rawHandle);
        SetStdHandle(STD_ERROR_HANDLE, rawHandle);

        var safeHandle = new SafeFileHandle(rawHandle, ownsHandle: false);
        var stream     = new FileStream(safeHandle, FileAccess.Write);
        var writer     = new StreamWriter(stream, Console.OutputEncoding) { AutoFlush = true };

        Console.SetOut(writer);
        Console.SetError(writer);

        // Enable VT processing for ANSI color codes
        if (GetConsoleMode(rawHandle, out var mode))
            SetConsoleMode(rawHandle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    // ── CTRL_CLOSE_EVENT handler ──────────────────────────────────────────────
    // Runs on a native Windows thread injected by the OS.
    private bool OnConsoleCtrl(uint ctrlType)
    {
        if (ctrlType == CTRL_CLOSE_EVENT)
        {
            HideConsole();
            return true; // suppress default action (process termination)
        }
        return false;
    }

    // ── Keyboard hook callback ────────────────────────────────────────────────
    // Runs on the STA thread via the WinForms message pump.
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == WM_SYSKEYDOWN)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == VK_F4 && GetForegroundWindow() == GetConsoleWindow())
            {
                HideConsole();
                return new IntPtr(1); // swallow — do not forward Alt+F4
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    // ── Hide console ──────────────────────────────────────────────────────────
    private static void HideConsole()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_HIDE);
    }

    // ── Show console ──────────────────────────────────────────────────────────
    private static void ShowConsole()
    {
        var hwnd = GetConsoleWindow();

        if (hwnd == IntPtr.Zero)
        {
            // Modern conhost / Windows Terminal destroyed the window despite
            // our handler returning TRUE.  Recreate transparently.
            FreeConsole();
            InitConsole(); // AllocConsole + rewire + hide
            hwnd = GetConsoleWindow();
        }

        if (hwnd == IntPtr.Zero)
            return;

        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
    }

    // ── Exit ──────────────────────────────────────────────────────────────────
    private static void ExitApp()
    {
        System.Windows.Forms.Application.Exit();
        Environment.Exit(0);
    }

    // ── Static factory ────────────────────────────────────────────────────────
    /// <summary>
    /// Starts a dedicated STA/WinForms thread, wires the console streams,
    /// and shows the tray icon.  Blocks the caller until the icon is visible.
    /// </summary>
    public static Thread Start()
    {
        var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            using var mgr = new TrayManager();
            ready.Set();

            System.Windows.Forms.Application.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Name         = "TrayManagerThread";
        thread.IsBackground = false;
        thread.Start();

        ready.Wait();
        return thread;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        SetConsoleCtrlHandler(_ctrlHandler, add: false);
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }
}
