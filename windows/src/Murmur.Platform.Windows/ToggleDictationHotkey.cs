using System.ComponentModel;
using System.Runtime.InteropServices;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>Registers Win+Shift+D with Windows, without installing a keyboard hook.</summary>
/// <remarks>
/// MOD_NOREPEAT prevents a held chord from toggling repeatedly. Activation is delivered
/// after release so a fast transcription cannot type while Win or Shift is still held.
/// Registration and cleanup run on the same message-loop thread, as required by Win32.
/// </remarks>
public sealed class ToggleDictationHotkey : IHotkeySource
{
    private const int HotkeyId = 1;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyD = 0x44;
    private const uint WmHotkey = 0x0312;
    private const uint WmTimer = 0x0113;
    private const uint WmQuit = 0x0012;
    private const int AlreadyRegistered = 1409;
    private const uint ReleasePollMilliseconds = 10;

    private Thread? _thread;
    private uint _threadId;

    /// <inheritdoc />
    public bool IsToggle => true;

    /// <inheritdoc />
    public string? RegistrationError { get; private set; }

    /// <inheritdoc />
    public event EventHandler? Pressed;

    /// <summary>Unused: a registered toggle shortcut has no release action.</summary>
    public event EventHandler? Released { add { } remove { } }

    /// <inheritdoc />
    public bool Start()
    {
        StopListening();
        RegistrationError = null;
        using var ready = new ManualResetEventSlim(false);
        var registered = false;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            // Ensure PostThreadMessage can reach this thread even before GetMessage starts.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            registered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModWin | ModShift | ModNoRepeat, VirtualKeyD);
            if (!registered)
            {
                var error = Marshal.GetLastWin32Error();
                RegistrationError = error == AlreadyRegistered
                    ? "Win + Shift + D is already in use. Close the other app or choose another shortcut in Settings."
                    : $"Win + Shift + D could not be registered: {new Win32Exception(error).Message}";
            }
            ready.Set();
            if (!registered) return;

            nuint timer = 0;
            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Message == WmHotkey && message.WParam == (nuint)HotkeyId && timer == 0)
                    {
                        timer = SetTimer(IntPtr.Zero, 0, ReleasePollMilliseconds, IntPtr.Zero);
                    }
                    else if (message.Message == WmTimer && message.WParam == timer && timer != 0)
                    {
                        if (IsHeld((int)VirtualKeyD) || IsHeld(0x10) || IsHeld(0x5B) || IsHeld(0x5C)) continue;
                        KillTimer(IntPtr.Zero, timer);
                        timer = 0;
                        Pressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            finally
            {
                if (timer != 0) KillTimer(IntPtr.Zero, timer);
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            }
        }) { IsBackground = true, Name = "Murmur toggle shortcut" };
        _thread.Start();
        ready.Wait();
        return registered;
    }

    /// <inheritdoc />
    public void StopListening()
    {
        if (_thread is null) return;
        if (_thread.IsAlive) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
        _thread = null;
        _threadId = 0;
    }

    /// <inheritdoc />
    public void Dispose() => StopListening();

    private static bool IsHeld(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageData
    {
        public IntPtr Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out MessageData message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MessageData message, IntPtr window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr window, nuint id, uint milliseconds, IntPtr callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr window, nuint id);
}
