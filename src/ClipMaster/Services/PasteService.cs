using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipMaster.Data;
using ClipMaster.Native;

namespace ClipMaster.Services;

/// <summary>直前ウィンドウの記憶・復帰と、クリップボードセット＋Ctrl+V送出。</summary>
public sealed class PasteService
{
    private readonly ImageStore _images;
    private readonly HistoryService _history;
    private IntPtr _target;
    private readonly IntPtr _self;

    public PasteService(ImageStore images, HistoryService history, IntPtr selfWindow)
    {
        _images = images; _history = history; _self = selfWindow;
    }

    public IntPtr Target => _target;

    /// <summary>呼び出し時点のアクティブウィンドウを記録する。</summary>
    public void RememberForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        // タスクバー/デスクトップ/自分自身が前面のとき（トレイからの呼び出し等）は、Zオーダー上で次にある実ウィンドウを使う
        var hwnd = fg;
        for (int i = 0; i < 40 && hwnd != IntPtr.Zero && !IsPasteTarget(hwnd, requireTitle: hwnd != fg); i++)
            hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
        _target = IsPasteTarget(hwnd, requireTitle: hwnd != fg) ? hwnd : IntPtr.Zero;
        Diag.Log($"remember foreground 0x{fg:X} -> target 0x{_target:X}");
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
    };

    private static bool IsPasteTarget(IntPtr hwnd, bool requireTitle)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return false;
        var sb = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        if (ShellClasses.Contains(sb.ToString())) return false;
        if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;
        return !requireTitle || NativeMethods.GetWindowTextLength(hwnd) > 0;
    }

    /// <summary>アイテムをクリップボードへ。paste=true なら直前ウィンドウへ貼り付ける。</summary>
    public async Task SendAsync(ClipItem item, bool paste)
    {
        if (item.IsImage)
        {
            var png = _images.LoadPng(item.Hash);
            if (png == null) return; // 保管先にアクセスできない場合は何もしない
            await SetClipboardRetry(() => SetImage(png));
        }
        else
        {
            var text = item.Text ?? "";
            await SetClipboardRetry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));
        }
        if (paste) await PasteToTargetAsync();
    }

    public async Task SendTextAsync(string text, bool paste)
    {
        await SetClipboardRetry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));
        if (paste) await PasteToTargetAsync();
    }

    private static void SetImage(byte[] png)
    {
        var dobj = new DataObject();
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        dobj.SetImage(bmp);                       // DIB 系（幅広く対応）
        dobj.SetData("PNG", new MemoryStream(png)); // 透過を保つPNG
        Clipboard.SetDataObject(dobj, true);
    }

    private async Task SetClipboardRetry(Action set)
    {
        _history.SuppressFor(600);
        for (int i = 0; i < 6; i++)
        {
            try { set(); return; }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                await Task.Delay(40 * (i + 1));
            }
        }
    }

    private async Task PasteToTargetAsync()
    {
        var hwnd = _target;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        ActivateWindow(hwnd);
        // フォーカスが戻るのを待つ（最大 ~400ms）
        for (int i = 0; i < 40 && NativeMethods.GetForegroundWindow() != hwnd; i++)
            await Task.Delay(10);
        await Task.Delay(30);
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            Diag.Log($"paste skipped: target 0x{hwnd:X} not foreground");
            return; // 誤った窓へ貼り付けないため送出しない（クリップボードには入っている）
        }
        Diag.Log("paste: send Ctrl+V");

        SendCtrlV();
    }


    public static bool ActivateWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var fg = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint cur = NativeMethods.GetCurrentThreadId();
            bool attached = fgThread != cur && NativeMethods.AttachThreadInput(cur, fgThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(cur, fgThread, false);
            }
            if (NativeMethods.GetForegroundWindow() == hwnd) return true;

            // フォアグラウンドロックで拒否された場合: Alt の空打ちでロックを解除して再試行
            var alt = new[] { Key(0x12, false), Key(0x12, true) };
            NativeMethods.SendInput((uint)alt.Length, alt, Marshal.SizeOf<NativeMethods.INPUT>());
        }
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    private static void SendCtrlV()
    {
        // ユーザーが押しっぱなしの Shift/Alt 等が貼り付けに影響しないよう一旦解除
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var vk in new ushort[] { 0x10, 0x12 }) // Shift, Alt
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0) inputs.Add(Key(vk, true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, false));
        inputs.Add(Key(NativeMethods.VK_V, false));
        inputs.Add(Key(NativeMethods.VK_V, true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, true));
        var arr = inputs.ToArray();
        NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(int vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0, dwExtraInfo = NativeMethods.OwnExtraInfo }
        }
    };
}
