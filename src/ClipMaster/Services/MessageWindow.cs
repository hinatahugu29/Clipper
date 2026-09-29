using System.Windows.Interop;
using ClipMaster.Native;

namespace ClipMaster.Services;

/// <summary>クリップボード更新・ホットキー受信用の不可視メッセージ専用ウィンドウ。</summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;
    public IntPtr Handle => _source.Handle;

    public event Action? ClipboardUpdated;
    public event Action<int>? HotkeyPressed;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("ClipMasterMsgWindow")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0, Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE) ClipboardUpdated?.Invoke();
        else if (msg == NativeMethods.WM_HOTKEY) HotkeyPressed?.Invoke(wParam.ToInt32());
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
