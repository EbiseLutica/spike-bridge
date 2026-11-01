using System;
using System.Runtime.InteropServices;

namespace SpikeClient;

/// <summary>
/// 低レベルキーボードフック(WH_KEYBOARD_LL)のラッパー。Win32 API の呼び出しはここに閉じ込める。
/// フックのコールバックはメッセージループを持つスレッドで呼ばれるので、UI スレッドで作成すること。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>キーイベントのハンドラ。true を返すと Windows やシェル、IME に渡さず握りつぶす。</summary>
    /// <param name="scanCode">スキャンコード</param>
    /// <param name="extended">拡張キー(E0 プレフィックス)か</param>
    /// <param name="down">押下なら true、離上なら false</param>
    public delegate bool KeyHandler(int scanCode, bool extended, bool down);

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_UP = 0x80;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly KeyHandler handler;
    private readonly HookProc hookProc;   // GC に回収されないようフィールドで保持
    private IntPtr hook;

    /// <summary>フックを登録する。</summary>
    public KeyboardHook(KeyHandler handler)
    {
        this.handler = handler;
        hookProc = Callback;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
    }

    /// <summary><paramref name="window"/> が最前面のウィンドウかどうか。</summary>
    public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

    /// <summary>フックを解除する。</summary>
    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            bool swallow = handler((int)k.ScanCode, (k.Flags & LLKHF_EXTENDED) != 0, (k.Flags & LLKHF_UP) == 0);
            if (swallow) return (IntPtr)1;
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }
}
