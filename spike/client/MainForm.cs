using System;
using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace SpikeClient;

/// <summary>
/// 前面にあって送信中の間、全キー入力を横取りして Pico W へ送るウィンドウ。
/// Ctrl+Alt+Del と Win+L はフックより先に Windows が処理するため捕捉できない。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly IPEndPoint target;
    private readonly bool jis;

    private KeyboardHook? hook;
    private BridgeSender? sender;   // 送信中だけ存在する

    private readonly CheckBox capture = new();
    private readonly Label status = new();

    /// <param name="host">Pico W の IP またはホスト名</param>
    /// <param name="jis">PC が JIS キーボードなら true</param>
    public MainForm(string host, bool jis)
    {
        this.jis = jis;
        target = BridgeSender.CreateEndpoint(host);

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
            if (capture.Checked) StartSending();
            else StopSending();
        };

        status.AutoSize = false;
        status.Location = new Point(12, 44);
        status.Size = new Size(356, 56);

        Controls.Add(capture);
        Controls.Add(status);

        Deactivate += (_, _) => sender?.ReleaseAll();   // フォーカスを失ったらキーを押したままにしない
        FormClosing += (_, _) => StopSending();

        StartSending();
    }

    /// <summary>ウィンドウ作成時にキーボードフックを登録する。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        hook = new KeyboardHook(OnKey);
    }

    /// <summary>ウィンドウ破棄時にキーボードフックを解除する。</summary>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        hook?.Dispose();
        hook = null;
        base.OnHandleDestroyed(e);
    }

    /// <summary>送信を開始する。</summary>
    private void StartSending()
    {
        if (sender != null) return;
        sender = new BridgeSender(target, jis);
        sender.StateChanged += UpdateStatus;
        UpdateStatus();
    }

    /// <summary>送信を止める。押下中のキーはすべて離される。</summary>
    private void StopSending()
    {
        if (sender == null) return;
        sender.Dispose();
        sender = null;
        UpdateStatus();
    }

    /// <summary>送信中かつ前面のときだけキーを <see cref="BridgeSender"/> に渡し、握りつぶす。</summary>
    private bool OnKey(int scanCode, bool extended, bool down)
    {
        if (sender == null || !KeyboardHook.IsForeground(Handle)) return false;
        sender.HandleKey(scanCode, extended, down);
        return true;
    }

    /// <summary>送信先・送信状態・押下中のキーをウィンドウに表示する。</summary>
    private void UpdateStatus()
    {
        string held = sender?.HeldSummary ?? "(none)";
        status.Text = $"→ {target}\n{(sender != null ? "送信中" : "停止中")}  {held}";
    }
}
