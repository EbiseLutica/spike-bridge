using System;
using System.IO;
using System.Text.Json;

namespace SpikeClient;

/// <summary>
/// UI で設定する送信先と配列。exe と同じフォルダの settings.json に保存し、次回起動時に復元する。
/// </summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Pico W の IP またはホスト名。</summary>
    public string Host { get; set; } = "255.255.255.255";

    /// <summary>PC が JIS キーボードなら true。</summary>
    public bool Jis { get; set; }

    /// <summary>設定ファイルを読む。無い・壊れている場合は既定値を返す。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new AppSettings();
    }

    /// <summary>設定ファイルに書く。書けない場所に置かれていても動作は止めない。</summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
