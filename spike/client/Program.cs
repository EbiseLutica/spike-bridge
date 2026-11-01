using System;
using System.Windows.Forms;

namespace SpikeClient;

internal static class Program
{
    /// <summary>
    /// 使い方: SpikeClient.exe [ホストまたはIP] [--jis]
    ///   ホスト省略時はブロードキャスト(255.255.255.255)で送る。Pico の IP を指定したほうが低遅延。
    ///   --jis: PC が JIS キーボードのとき指定する(\ キーの割り当てと半角/全角の扱いが変わる)。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        string host = "255.255.255.255";
        bool jis = false;
        foreach (var a in args)
        {
            if (a.Equals("--jis", StringComparison.OrdinalIgnoreCase)) jis = true;
            else host = a;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(host, jis));
    }
}
