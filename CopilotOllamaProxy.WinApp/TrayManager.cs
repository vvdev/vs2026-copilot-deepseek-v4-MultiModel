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

    // Console stream redirection
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    // Window visibility
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Console control handler
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    // Console system menu (for greying out close button)
    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    // Keyboard hook for Alt+F4 and Ctrl+C interception
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleSelectionInfo(out CONSOLE_SELECTION_INFO lpConsoleSelectionInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleOutputCharacter(IntPtr hConsoleOutput, [Out] char[] lpCharacter, uint nLength, COORD dwReadCoord, out uint lpNumberOfCharsRead);

    // Window flashing for visual feedback
    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Window management
    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLong64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return (int)GetWindowLong64(hWnd, nIndex);
        return GetWindowLong32(hWnd, nIndex);
    }

    private static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
    {
        if (IntPtr.Size == 8)
            return (int)SetWindowLong64(hWnd, nIndex, new IntPtr(dwNewLong));
        return SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SELECTION_INFO
    {
        public uint dwFlags;
        public COORD dwSelectionAnchor;
        public SMALL_RECT srSelection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public short wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint  cbSize;
        public IntPtr hwnd;
        public uint  dwFlags;
        public uint  uCount;
        public uint  dwTimeout;
    }

    private const int    SW_HIDE           = 0;
    private const int    SW_SHOW           = 5;
    private const uint   CTRL_C_EVENT      = 0;
    private const uint   CTRL_CLOSE_EVENT  = 2;
    private const int    WH_KEYBOARD_LL    = 13;
    private const int    WM_KEYDOWN        = 0x0100;
    private const int    WM_SYSKEYDOWN     = 0x0104;
    private const int    VK_CONTROL        = 0x11;
    private const uint   VK_F4             = 0x73;
    private const uint   VK_C              = 0x43;
    private const uint   CONSOLE_SELECTION_IN_PROGRESS = 0x0001;
    private const int    RESIZE_JIGGLE_PIXELS = 1;
    private const uint   GENERIC_WRITE    = 0x40000000;
    private const uint   GENERIC_READ     = 0x80000000;
    private const uint   FILE_SHARE_WRITE = 0x00000002;
    private const uint   OPEN_EXISTING    = 3;
    private const int    STD_OUTPUT_HANDLE = -11;
    private const int    STD_ERROR_HANDLE  = -12;
    // SC_CLOSE / MF_GRAYED — used to grey out the X button as a visual hint
    // that clicking X will hide rather than kill the console window.
    private const uint   SC_CLOSE         = 0xF060;
    private const uint   MF_BYCOMMAND                  = 0x00000000;
    private const uint   MF_GRAYED                     = 0x00000001;
    private const uint   ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    // FlashWindow flags
    private const uint   FLASHW_ALL        = 0x00000003;
    private const uint   FLASHW_TIMERNOFG  = 0x0000000C;

    // Window styles for embedding console
    private const int    GWL_STYLE                     = -16;
    private const uint   WS_CAPTION                    = 0x00C00000;
    private const uint   WS_THICKFRAME                 = 0x00040000;
    private const uint   WS_CHILD                      = 0x40000000;
    private const uint   SWP_NOZORDER                  = 0x0004;
    private const uint   SWP_FRAMECHANGED              = 0x0020;

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly NotifyIcon             _trayIcon;
    private readonly ConsoleCtrlDelegate    _ctrlHandler;  // must stay rooted
    private readonly LowLevelKeyboardProc   _keyboardProc; // must stay rooted
    private readonly Form                   _hostWindow;   // host form that contains console
    private          IntPtr                 _keyboardHook;
    private          bool                   _disposed;
    private static   int                    _runningModelsCount;
    private static   TrayManager?           _instance;

    // ── Constructor ───────────────────────────────────────────────────────────
    private TrayManager()
    {
        // Allocate console first to get its natural dimensions
        InitConsole();

        // Get the console window's natural size
        var consoleHwnd = GetConsoleWindow();
        System.Drawing.Size hostSize = new System.Drawing.Size(1000, 600); // fallback

        if (consoleHwnd != IntPtr.Zero && GetWindowRect(consoleHwnd, out var consoleRect))
        {
            // Use console's current window size (includes borders/caption)
            hostSize = new System.Drawing.Size(
                consoleRect.Right - consoleRect.Left,
                consoleRect.Bottom - consoleRect.Top
            );
        }

        // Create host window that will contain the console as a child
        _hostWindow = new Form
        {
            Text          = "CopilotOllamaProxy — Console",
            ClientSize    = hostSize, // Use ClientSize so borders are added on top
            MinimumSize   = new System.Drawing.Size(400, 300),
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true,
            BackColor     = System.Drawing.Color.Black,
            Icon          = LoadEmbeddedIcon(),
        };
        _hostWindow.FormClosing += OnHostWindowClosing;
        _hostWindow.Resize      += OnHostWindowResize;
        _hostWindow.Activated   += OnHostWindowActivated;
        _hostWindow.MouseDown   += OnHostWindowMouseDown;

        // Embed console into the host window
        EmbedConsoleInHostWindow();

        _ctrlHandler = OnConsoleCtrl;
        SetConsoleCtrlHandler(_ctrlHandler, add: true);

        // ── Low-level keyboard hook (intercept Alt+F4 on the host window) ──
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

        menu.Items.Add(new ToolStripSeparator());

        var copyAllItem = new ToolStripMenuItem("Copy All Console Text");
        copyAllItem.Click += (_, _) => CopyAllConsoleText();
        menu.Items.Add(copyAllItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        // ── Tray icon ─────────────────────────────────────────────────────────
        _trayIcon = new NotifyIcon
        {
            Icon             = LoadEmbeddedIcon(),
            Text             = GetTrayTooltipText(),
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
    /// Embeds the console window as a child of the host Form.
    /// </summary>
    private void EmbedConsoleInHostWindow()
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd == IntPtr.Zero)
            return;

        // Remove console window decorations (caption, borders) and make it a child window
        var style = GetWindowLong(consoleHwnd, GWL_STYLE);
        style &= ~((int)WS_CAPTION | (int)WS_THICKFRAME);
        style |= (int)WS_CHILD;
        SetWindowLong(consoleHwnd, GWL_STYLE, style);

        // Make the console a child of our host window
        SetParent(consoleHwnd, _hostWindow.Handle);

        // Resize console to fill the host window's client area
        ResizeConsoleToFillHost();

        // Show the console child window
        ShowWindow(consoleHwnd, SW_SHOW);
    }

    /// <summary>
    /// Resizes the console window to fill the host window's client area.
    /// </summary>
    private void ResizeConsoleToFillHost()
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd == IntPtr.Zero)
            return;

        GetClientRect(_hostWindow.Handle, out var rect);
        SetWindowPos(consoleHwnd, IntPtr.Zero, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Host window closing event — hide instead of close.
    /// </summary>
    private void OnHostWindowClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideConsole();
        }
    }

    /// <summary>
    /// Host window resize event — keep console sized to fill.
    /// </summary>
    private void OnHostWindowResize(object? sender, EventArgs e)
    {
        ResizeConsoleToFillHost();
    }

    /// <summary>
    /// Host window activated event — forward keyboard focus to console.
    /// </summary>
    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd != IntPtr.Zero)
            SetFocus(consoleHwnd);
    }

    /// <summary>
    /// Host window mouse down event — forward focus to console for text selection.
    /// </summary>
    private void OnHostWindowMouseDown(object? sender, MouseEventArgs e)
    {
        var consoleHwnd = GetConsoleWindow();
        if (consoleHwnd != IntPtr.Zero)
            SetFocus(consoleHwnd);
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

    // ── Console control handler ───────────────────────────────────────────────
    // Runs on a native Windows thread injected by the OS.
    private bool OnConsoleCtrl(uint ctrlType)
    {
        if (ctrlType == CTRL_C_EVENT)
        {
            // Suppress Ctrl+C when nothing is selected (copy still works when text is selected)
            // because copy is handled by the console host itself, not via this handler
            return true; // suppress default action (process termination)
        }

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
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var fgWnd = GetForegroundWindow();
            bool isConsoleForeground = fgWnd == _hostWindow.Handle || fgWnd == GetConsoleWindow();

            // Handle Alt+F4
            if (wParam == WM_SYSKEYDOWN && kb.vkCode == VK_F4 && isConsoleForeground)
            {
                HideConsole();
                return new IntPtr(1); // swallow — do not forward Alt+F4
            }

            // Handle Ctrl+C
            if (wParam == WM_KEYDOWN && kb.vkCode == VK_C && isConsoleForeground)
            {
                // Check if Ctrl key is pressed
                if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    // Check if there's an active selection in the console
                    if (GetConsoleSelectionInfo(out var selInfo))
                    {
                        // If selection is in progress, allow the copy (don't swallow)
                        if ((selInfo.dwFlags & CONSOLE_SELECTION_IN_PROGRESS) != 0)
                        {
                            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                        }
                    }

                    // No selection - suppress Ctrl+C to prevent app shutdown
                    return new IntPtr(1); // swallow
                }
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    // ── Show / hide console ───────────────────────────────────────────────────
    private void HideConsole()
    {
        _hostWindow.Hide();
    }

    private void ShowConsole()
    {
        var hwnd = GetConsoleWindow();

        if (hwnd == IntPtr.Zero)
        {
            // Modern conhost / Windows Terminal destroyed the window despite
            // our handler returning TRUE.  Recreate transparently.
            Console.WriteLine("[TrayManager] Console window was destroyed externally, recreating...");
            FreeConsole();
            InitConsole(); // AllocConsole + rewire + hide
            EmbedConsoleInHostWindow();
            hwnd = GetConsoleWindow();
            Console.WriteLine("[TrayManager] Console window recreated and re-embedded");
        }

        if (hwnd == IntPtr.Zero)
            return;

        // Visual feedback: if already visible, flash the window
        if (_hostWindow.Visible)
        {
            FlashWindow();
            return;
        }

        // Show the host window which contains the console
        _hostWindow.Show();
        _hostWindow.Activate();

        // Give focus to the console for text selection
        SetFocus(hwnd);

        // Force console to redraw by triggering a tiny resize.
        // Console windows don't respond to standard paint messages because they
        // have their own internal rendering pipeline managed by conhost.exe.
        // The resize forces the console to recalculate and redraw its screen buffer,
        // which is the only reliable way to trigger a repaint.
        GetClientRect(_hostWindow.Handle, out var rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        // Resize slightly smaller then back to full size
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width - RESIZE_JIGGLE_PIXELS, height - RESIZE_JIGGLE_PIXELS, SWP_NOZORDER);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_NOZORDER);
    }

    // ── Copy all console text ──────────────────────────────────────────────────
    private void CopyAllConsoleText()
    {
        try
        {
            // Open CONOUT$ to read console screen buffer
            var outHandle = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (outHandle == IntPtr.Zero || outHandle == new IntPtr(-1))
            {
                Console.WriteLine("[TrayManager] Failed to open CONOUT$");
                _trayIcon.ShowBalloonTip(2000, "Copy Failed",
                    "Could not access console buffer.", ToolTipIcon.Error);
                return;
            }

            try
            {
                // Get console screen buffer info
                if (!GetConsoleScreenBufferInfo(outHandle, out var bufferInfo))
                {
                    Console.WriteLine("[TrayManager] Failed to get console buffer info");
                    _trayIcon.ShowBalloonTip(2000, "Copy Failed",
                        "Could not read console information.", ToolTipIcon.Error);
                    return;
                }

                // Calculate the size of the buffer
                int width = bufferInfo.dwSize.X;
                int height = bufferInfo.dwCursorPosition.Y + 1; // Only read up to cursor position

                if (height <= 0 || width <= 0)
                {
                    _trayIcon.ShowBalloonTip(2000, "Console Empty",
                        "Console buffer is empty.", ToolTipIcon.Info);
                    return;
                }

                var sb = new System.Text.StringBuilder();
                var buffer = new char[width];

                // Read each line
                for (short y = 0; y < height; y++)
                {
                    var coord = new COORD { X = 0, Y = y };
                    if (ReadConsoleOutputCharacter(outHandle, buffer, (uint)width, coord, out uint charsRead))
                    {
                        // Trim trailing spaces from each line
                        string line = new string(buffer, 0, (int)charsRead).TrimEnd();
                        sb.AppendLine(line);
                    }
                }

                string consoleText = sb.ToString().TrimEnd();

                if (string.IsNullOrWhiteSpace(consoleText))
                {
                    _trayIcon.ShowBalloonTip(2000, "Console Empty",
                        "Console buffer is empty.", ToolTipIcon.Info);
                    return;
                }

                // Copy to clipboard
                System.Windows.Forms.Clipboard.SetText(consoleText);

                // Show feedback
                _trayIcon.ShowBalloonTip(2000, "Console Text Copied",
                    $"Copied {height} lines to clipboard.", ToolTipIcon.Info);
            }
            finally
            {
                CloseHandle(outHandle);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrayManager] Error copying console text: {ex.Message}");
            _trayIcon.ShowBalloonTip(2000, "Copy Failed",
                "Failed to copy console text.", ToolTipIcon.Error);
        }
    }

    // ── Flash window for visual feedback ───────────────────────────────────────
    private void FlashWindow()
    {
        // Flash the taskbar button and window caption
        var flashInfo = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _hostWindow.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 3,
            dwTimeout = 0
        };
        FlashWindowEx(ref flashInfo);

        // Also bring window to front and activate it
        _hostWindow.Activate();
        SetForegroundWindow(_hostWindow.Handle);
    }

    // ── Get tooltip text with model count ──────────────────────────────────────
    private static string GetTrayTooltipText()
    {
        if (_runningModelsCount == 0)
            return "CopilotOllamaProxy";

        return $"CopilotOllamaProxy ({_runningModelsCount} model{(_runningModelsCount == 1 ? "" : "s")} running)";
    }

    // ── Update model count and refresh tooltip ────────────────────────────────
    public static void SetRunningModelsCount(int count)
    {
        _runningModelsCount = count;
        // Update tooltip if instance exists (called after Start())
        if (_instance != null)
        {
            _instance.UpdateTooltip();
        }
    }

    public void UpdateTooltip()
    {
        // Must be called on UI thread
        if (_hostWindow.InvokeRequired)
        {
            _hostWindow.BeginInvoke(() => _trayIcon.Text = GetTrayTooltipText());
        }
        else
        {
            _trayIcon.Text = GetTrayTooltipText();
        }
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

            _instance = new TrayManager();
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
        _hostWindow.Dispose();
    }
}
