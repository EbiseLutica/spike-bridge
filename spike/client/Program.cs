using System;
using System.Windows.Forms;

namespace SpikeClient;

internal static class Program
{
    /// <summary>
    /// 使い方: SpikeClient.exe [ホストまたはIP] [--jis]
    /// 保存済みの設定を読み、コマンドライン引数があればそちらを優先する。
    ///   ホスト: Pico の IP。初期値はブロードキャスト(255.255.255.255)だが、IP 指定のほうが低遅延。
    ///   --jis: PC が JIS キーボードのとき指定する(\ キーの割り当てと半角/全角の扱いが変わる)。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var settings = AppSettings.Load();
        foreach (var a in args)
        {
            if (a.Equals("--jis", StringComparison.OrdinalIgnoreCase)) settings.Jis = true;
            else settings.Host = a;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(settings));
    }
}
