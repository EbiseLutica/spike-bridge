using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpikeClient;

/// <summary>
/// 前面にある間、低レベルキーボードフックで全キー入力を横取りし、
/// HID boot keyboard レポートとして Pico W へ UDP 送信するウィンドウ。
/// Ctrl+Alt+Del と Win+L はフックより先に Windows が処理するため捕捉できない。
/// </summary>
internal sealed class MainForm : Form
{
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

    private const byte Magic = 0xA5;
    private const int Port = 9000;

    // 2 フレーム(60fps)以上にして、ゲーム側のポーリングで押下を取りこぼさないようにする
    private const int TapMs = 40;

    private readonly HookProc hookProc;   // GC に回収されないようフィールドで保持
    private IntPtr hook;

    private readonly UdpClient udp = new() { EnableBroadcast = true };
    private readonly IPEndPoint target;
    private readonly bool jis;

    private readonly List<byte> keys = new();
    private byte modifiers;
    private int burst;   // 状態変化後に追送する残り回数(UDP の取りこぼし対策)
    private int tick;
    private byte tapKey;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 20 };
    private readonly System.Windows.Forms.Timer tapTimer = new() { Interval = TapMs };
    private readonly CheckBox capture = new();
    private readonly Label status = new();

    /// <param name="host">Pico W の IP またはホスト名</param>
    /// <param name="jis">PC が JIS キーボードなら true</param>
    public MainForm(string host, bool jis)
    {
        this.jis = jis;

        var addrs = IPAddress.TryParse(host, out var parsed)
            ? new[] { parsed }
            : Dns.GetHostAddresses(host);
        var ip = addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        target = new IPEndPoint(ip, Port);

        Text = "Spike Client";
        ClientSize = new Size(380, 110);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ImeMode = ImeMode.Disable;

        capture.Text = "キー入力を Switch へ送る(解除はマウスでクリック)";
        capture.AutoSize = true;
        capture.Location = new Point(12, 12);
        capture.Checked = true;
        capture.ImeMode = ImeMode.Disable;
        capture.CheckedChanged += (_, _) =>
        {
            if (!capture.Checked) ReleaseAll();
            UpdateStatus();
        };

        status.AutoSize = false;
        status.Location = new Point(12, 44);
        status.Size = new Size(356, 56);

        Controls.Add(capture);
        Controls.Add(status);

        hookProc = HookCallback;
        timer.Tick += OnTick;
        tapTimer.Tick += OnTapTimer;
        timer.Start();

        Deactivate += (_, _) => ReleaseAll();   // フォーカスを失ったらキーを押したままにしない
        FormClosing += (_, _) => ReleaseAll();

        UpdateStatus();
    }

    /// <summary>ウィンドウ作成時にキーボードフックを登録する。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
    }

    /// <summary>ウィンドウ破棄時にキーボードフックを解除する。</summary>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// フックのコールバック。送信中かつ前面のときだけキーを処理し、
    /// Windows・シェル・IME に渡さず握りつぶす。
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && capture.Checked && GetForegroundWindow() == Handle)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            HandleKey((int)k.ScanCode, (k.Flags & LLKHF_EXTENDED) != 0, down: (k.Flags & LLKHF_UP) == 0);
            return (IntPtr)1;
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>キーの押下/離上を内部状態に反映し、変化があれば送信する。</summary>
    private void HandleKey(int sc, bool ext, bool down)
    {
        // 一部の拡張キーの前後に Windows が差し込む偽の Shift (E0 2A / E0 36) は無視する
        if (ext && (sc == 0x2A || sc == 0x36)) return;

        // JIS の半角/全角は、押下時に F3 離上 + F4 押下のペアが来るだけで
        // 物理的な離上イベントがない。固着を避けるため押下 1 回をタップとして扱う。
        if (jis && !ext && sc == 0x29)
        {
            if (down) Tap(0x35);
            return;
        }

        var (key, mod) = HidMap.Map(sc, ext, jis);
        if (key == 0 && mod == 0) return;

        bool changed;
        if (mod != 0)
        {
            byte next = down ? (byte)(modifiers | mod) : (byte)(modifiers & ~mod);
            changed = next != modifiers;
            modifiers = next;
        }
        else if (down)
        {
            changed = !keys.Contains(key) && keys.Count < 6;
            if (changed) keys.Add(key);
        }
        else
        {
            changed = keys.Remove(key);
        }

        if (!changed) return;
        Send();
        burst = 2;
        UpdateStatus();
    }

    /// <summary><paramref name="key"/> を押し、<see cref="TapMs"/> ms 後に離す。</summary>
    private void Tap(byte key)
    {
        if (tapTimer.Enabled) return;
        if (!keys.Contains(key))
        {
            if (keys.Count >= 6) return;
            keys.Add(key);
        }
        tapKey = key;
        Send();
        burst = 2;
        tapTimer.Start();
        UpdateStatus();
    }

    /// <summary>タップ中のキーを離す。</summary>
    private void OnTapTimer(object? sender, EventArgs e)
    {
        tapTimer.Stop();
        if (tapKey == 0) return;
        keys.Remove(tapKey);
        tapKey = 0;
        Send();
        burst = 2;
        UpdateStatus();
    }

    /// <summary>押下中のキーと修飾キーをすべて離して送信する。</summary>
    private void ReleaseAll()
    {
        if (keys.Count == 0 && modifiers == 0) return;
        keys.Clear();
        modifiers = 0;
        Send();
        burst = 2;
        UpdateStatus();
    }

    /// <summary>20ms ごとに、追送とハートビート(キー押下中は約 100ms ごと)を送る。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        tick++;
        if (burst > 0)
        {
            Send();
            burst--;
        }
        else if ((keys.Count > 0 || modifiers != 0) && tick % 5 == 0)
        {
            Send();
        }
    }

    /// <summary>現在の全キー状態を 9 バイトのパケットにして Pico W へ送る。</summary>
    private void Send()
    {
        Span<byte> pkt = stackalloc byte[9];
        pkt[0] = Magic;
        pkt[1] = modifiers;
        pkt[2] = 0;
        for (int i = 0; i < keys.Count && i < 6; i++) pkt[3 + i] = keys[i];
        try
        {
            udp.Send(pkt, target);
        }
        catch (SocketException)
        {
            // 次の追送かハートビートで再送されるので握りつぶす
        }
    }

    /// <summary>送信先・送信状態・押下中のキーをウィンドウに表示する。</summary>
    private void UpdateStatus()
    {
        string held = (keys.Count == 0 && modifiers == 0)
            ? "(none)"
            : $"mod={modifiers:X2} keys={string.Join(' ', keys.Select(k => k.ToString("X2")))}";
        status.Text = $"→ {target}\n{(capture.Checked ? "送信中" : "停止中")}  {held}";
    }
}
