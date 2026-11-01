using System;
using System.Drawing;
using System.Net.Sockets;
using System.Windows.Forms;

namespace SpikeClient;

/// <summary>
/// 前面にあって送信中の間、全キー入力を横取りして Pico W へ送るウィンドウ。
/// 送信先と配列を画面上で設定でき、変更すると送信を作り直す。
/// 入力欄にフォーカスがある間だけは、キーを横取りせず PC に普通に入力させる。
/// Ctrl+Alt+Del と Win+L はフックより先に Windows が処理するため捕捉できない。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings settings;

    private KeyboardHook? hook;
    private BridgeSender? sender;   // 送信中だけ存在する
    private string? error;          // 直近の開始失敗の理由
    private bool editing;           // 入力欄(送信先・配列)にフォーカスがあるか

    private readonly TextBox hostBox = new();
    private readonly ComboBox layoutBox = new();
    private readonly CheckBox capture = new();
    private readonly Label status = new();

    public MainForm(AppSettings settings)
    {
        this.settings = settings;

        Text = "Spike Client";
        ClientSize = new Size(380, 178);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ImeMode = ImeMode.Disable;

        var hostLabel = new Label { Text = "送信先", AutoSize = true, Location = new Point(12, 16) };
        hostBox.Location = new Point(70, 12);
        hostBox.Width = 298;
        hostBox.ImeMode = ImeMode.Disable;
        hostBox.Text = settings.Host;
        hostBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ApplyAndReturnFocus();
        };
        hostBox.Leave += (_, _) => Apply();

        var layoutLabel = new Label { Text = "配列", AutoSize = true, Location = new Point(12, 48) };
        layoutBox.Location = new Point(70, 44);
        layoutBox.Width = 100;
        layoutBox.DropDownStyle = ComboBoxStyle.DropDownList;
        layoutBox.Items.AddRange(new object[] { "US", "JIS" });
        layoutBox.SelectedIndex = settings.Jis ? 1 : 0;
        layoutBox.SelectionChangeCommitted += (_, _) => ApplyAndReturnFocus();

        foreach (Control box in new Control[] { hostBox, layoutBox })
        {
            box.Enter += (_, _) =>
            {
                editing = true;
                sender?.ReleaseAll();   // 入力欄に移る前に押下中のキーを離しておく
                UpdateStatus();
            };
            box.Leave += (_, _) =>
            {
                editing = false;
                UpdateStatus();
            };
        }

        capture.Text = "キー入力を Switch へ送る(解除はマウスでクリック)";
        capture.AutoSize = true;
        capture.Location = new Point(12, 80);
        capture.Checked = true;
        capture.ImeMode = ImeMode.Disable;
        capture.CheckedChanged += (_, _) =>
        {
            if (capture.Checked) StartSending();
            else StopSending();
        };

        status.AutoSize = false;
        status.Location = new Point(12, 112);
        status.Size = new Size(356, 60);

        Controls.AddRange(new Control[] { hostLabel, hostBox, layoutLabel, layoutBox, capture, status });
        ActiveControl = capture;

        // 入力欄以外をクリックしたら、フォーカスを送信チェックに戻して横取りを再開する
        MouseDown += (_, _) => ActiveControl = capture;
        status.MouseDown += (_, _) => ActiveControl = capture;
        hostLabel.MouseDown += (_, _) => ActiveControl = capture;
        layoutLabel.MouseDown += (_, _) => ActiveControl = capture;

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

    /// <summary>入力欄の値が変わっていれば保存し、送信を新しい設定で作り直す。</summary>
    private void Apply()
    {
        string host = hostBox.Text.Trim();
        bool jis = layoutBox.SelectedIndex == 1;
        if (host == settings.Host && jis == settings.Jis) return;

        settings.Host = host;
        settings.Jis = jis;
        settings.Save();

        StopSending();
        if (capture.Checked) StartSending();
        else if (error != null) capture.Checked = true;   // 前回の開始に失敗していたなら、直した設定で再開する
    }

    /// <summary>設定を反映して、フォーカスを送信チェックに戻す(=横取りを再開する)。</summary>
    private void ApplyAndReturnFocus()
    {
        Apply();
        ActiveControl = capture;
    }

    /// <summary>現在の設定で送信を開始する。宛先を解決できなければ停止状態にしてエラーを表示する。</summary>
    private void StartSending()
    {
        if (sender != null) return;
        try
        {
            var target = BridgeSender.CreateEndpoint(settings.Host);
            sender = new BridgeSender(target, settings.Jis);
            sender.StateChanged += UpdateStatus;
            error = null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
        {
            error = $"宛先を解決できません: {settings.Host}";
            capture.Checked = false;
        }
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

    /// <summary>送信中かつ前面で、入力欄を編集していないときだけキーを <see cref="BridgeSender"/> に渡し、握りつぶす。</summary>
    private bool OnKey(int scanCode, bool extended, bool down)
    {
        if (sender == null || editing || !KeyboardHook.IsForeground(Handle)) return false;
        sender.HandleKey(scanCode, extended, down);
        return true;
    }

    /// <summary>送信先・送信状態・押下中のキーをウィンドウに表示する。</summary>
    private void UpdateStatus()
    {
        string state = error
            ?? (sender == null ? "停止中"
            : editing ? "入力欄の編集中(キーは PC に入力される)"
            : "送信中");
        status.Text = $"→ {settings.Host}:{BridgeSender.Port}\n{state}\n{sender?.HeldSummary}";
    }
}
