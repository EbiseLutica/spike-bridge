using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace SpikeClient;

/// <summary>
/// キー入力を HID boot keyboard の状態として保持し、UDP で Pico W (spike-bridge) へ送る。
/// 変化のたびに全状態を送り、取りこぼし対策の追送と、押下中のハートビートも担当する。
/// タイマーは WinForms のものを使うので、UI スレッドで作成して使うこと。
/// </summary>
internal sealed class BridgeSender : IDisposable
{
    public const int Port = 9000;

    private const byte Magic = 0xA5;

    // 2 フレーム(60fps)以上にして、ゲーム側のポーリングで押下を取りこぼさないようにする
    private const int TapMs = 40;

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

    /// <summary>押下中のキーが変化したとき(送信状態の表示更新用)。</summary>
    public event Action? StateChanged;

    /// <param name="target">Pico W の送信先</param>
    /// <param name="jis">PC が JIS キーボードなら true</param>
    public BridgeSender(IPEndPoint target, bool jis)
    {
        this.target = target;
        this.jis = jis;

        timer.Tick += OnTick;
        tapTimer.Tick += OnTapTimer;
        timer.Start();
    }

    /// <summary>ホスト名または IP 文字列から、Pico W の送信先を作る。</summary>
    public static IPEndPoint CreateEndpoint(string host)
    {
        // 空文字を渡すと Dns が自分自身のアドレスを返してしまうので弾く
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host is empty", nameof(host));

        var addrs = IPAddress.TryParse(host, out var parsed)
            ? new[] { parsed }
            : Dns.GetHostAddresses(host);
        var ip = addrs.First(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(ip, Port);
    }

    /// <summary>押下中のキーと修飾キーの表示用テキスト。</summary>
    public string HeldSummary => (keys.Count == 0 && modifiers == 0)
        ? "(none)"
        : $"mod={modifiers:X2} keys={string.Join(' ', keys.Select(k => k.ToString("X2")))}";

    /// <summary>キーの押下/離上を内部状態に反映し、変化があれば送信する。</summary>
    public void HandleKey(int sc, bool ext, bool down)
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
        StateChanged?.Invoke();
    }

    /// <summary>押下中のキーと修飾キーをすべて離して送信する。</summary>
    public void ReleaseAll()
    {
        if (keys.Count == 0 && modifiers == 0) return;
        keys.Clear();
        modifiers = 0;
        Send();
        burst = 2;
        StateChanged?.Invoke();
    }

    /// <summary>全キーを離してから、タイマーとソケットを解放する。</summary>
    public void Dispose()
    {
        // 解放後の追送はないが、離上が落ちても Pico 側のタイムアウトで全キーが離される
        ReleaseAll();
        timer.Dispose();
        tapTimer.Dispose();
        udp.Dispose();
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
        StateChanged?.Invoke();
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
        StateChanged?.Invoke();
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
}
