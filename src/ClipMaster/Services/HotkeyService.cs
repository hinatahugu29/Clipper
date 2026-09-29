using System.Diagnostics;
using ClipMaster.Native;

namespace ClipMaster.Services;

/// <summary>RegisterHotKey による通常ホットキーと、低レベルフックによる Ctrl 2回押し検知。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4D31;

    private readonly MessageWindow _window;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private NativeMethods.LowLevelKeyboardProc? _proc; // GC されないよう保持
    private IntPtr _hook = IntPtr.Zero;
    private bool _hotkeyRegistered;

    // Ctrl 2回押し状態
    private bool _ctrlDown;
    private bool _otherKeyWhileCtrl;
    private long _lastTapTicks;
    public bool DoubleCtrlEnabled { get; set; }
    public int DoubleCtrlIntervalMs { get; set; } = 300;

    public event Action? Triggered;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _window.HotkeyPressed += id => { if (id == HotkeyId) Triggered?.Invoke(); };
    }

    public void Apply(AppConfig cfg)
    {
        DoubleCtrlEnabled = cfg.DoubleCtrlEnabled;
        DoubleCtrlIntervalMs = cfg.DoubleCtrlIntervalMs;

        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
        if (cfg.HotkeyEnabled && TryParse(cfg.Hotkey, out var mod, out var vk))
            _hotkeyRegistered = NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, mod | NativeMethods.MOD_NOREPEAT, vk);

        if (cfg.DoubleCtrlEnabled) InstallHook(); else RemoveHook();
    }

    public bool HotkeyRegistered => _hotkeyRegistered;

    public static bool TryParse(string s, out uint mod, out uint vk)
    {
        mod = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        foreach (var raw in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mod |= NativeMethods.MOD_CONTROL; break;
                case "shift": mod |= NativeMethods.MOD_SHIFT; break;
                case "alt": mod |= NativeMethods.MOD_ALT; break;
                case "win": mod |= NativeMethods.MOD_WIN; break;
                default:
                    if (Enum.TryParse<System.Windows.Forms.Keys>(raw, true, out var k) && k != System.Windows.Forms.Keys.None)
                        vk = (uint)k;
                    else return false;
                    break;
            }
        }
        return vk != 0 && mod != 0;
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookProc;
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule!;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(m.ModuleName), 0);
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool injected = kb.dwExtraInfo == NativeMethods.OwnExtraInfo; // 自前の SendInput のみ無視（他ツールの注入は有効）
            if (!injected)
            {
                int msg = wParam.ToInt32();
                bool down = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool up = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
                bool isCtrl = kb.vkCode is NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL or NativeMethods.VK_CONTROL;

                if (isCtrl)
                {
                    if (down)
                    {
                        if (!_ctrlDown) { _ctrlDown = true; _otherKeyWhileCtrl = false; }
                    }
                    else if (up)
                    {
                        if (_ctrlDown && !_otherKeyWhileCtrl) OnCtrlTap();
                        else _lastTapTicks = 0;
                        _ctrlDown = false;
                    }
                }
                else if (down)
                {
                    _otherKeyWhileCtrl = true;
                    _lastTapTicks = 0;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnCtrlTap()
    {
        long now = Environment.TickCount64;
        if (_lastTapTicks != 0 && now - _lastTapTicks <= DoubleCtrlIntervalMs)
        {
            _lastTapTicks = 0;
            // フック内では重い処理をしない
            _dispatcher.BeginInvoke(() => Triggered?.Invoke());
        }
        else _lastTapTicks = now;
    }

    public void Dispose()
    {
        RemoveHook();
        if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
    }
}
