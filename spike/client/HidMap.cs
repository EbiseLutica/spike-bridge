namespace SpikeClient;

/// <summary>
/// Windows のスキャンコード(セット 1)を USB HID の Usage ID(キーボードページ)に変換する。
/// 物理的なキー位置を送るので、US/JIS の配列は Switch 側のキーボード言語設定で決まる。
/// </summary>
internal static class HidMap
{
    // Q W E R T Y U I O P
    private static readonly byte[] RowQ = { 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13 };
    // A S D F G H J K L
    private static readonly byte[] RowA = { 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F };
    // Z X C V B N M
    private static readonly byte[] RowZ = { 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10 };

    /// <summary>
    /// 通常キーなら (Usage ID, 0)、修飾キーなら (0, 修飾ビット)、未対応なら (0, 0) を返す。
    /// </summary>
    /// <param name="sc">スキャンコード</param>
    /// <param name="ext">拡張キー(E0 プレフィックス)か</param>
    /// <param name="jis">JIS キーボードか</param>
    public static (byte Key, byte Mod) Map(int sc, bool ext, bool jis)
    {
        if (ext)
        {
            switch (sc)
            {
                case 0x1C: return (0x58, 0);      // テンキー Enter
                case 0x1D: return (0, 0x10);      // 右 Ctrl
                case 0x35: return (0x54, 0);      // テンキー /
                case 0x37: return (0x46, 0);      // PrintScreen
                case 0x38: return (0, 0x40);      // 右 Alt
                case 0x47: return (0x4A, 0);      // Home
                case 0x48: return (0x52, 0);      // Up
                case 0x49: return (0x4B, 0);      // PageUp
                case 0x4B: return (0x50, 0);      // Left
                case 0x4D: return (0x4F, 0);      // Right
                case 0x4F: return (0x4D, 0);      // End
                case 0x50: return (0x51, 0);      // Down
                case 0x51: return (0x4E, 0);      // PageDown
                case 0x52: return (0x49, 0);      // Insert
                case 0x53: return (0x4C, 0);      // Delete
                case 0x5B: return (0, 0x08);      // 左 Win
                case 0x5C: return (0, 0x80);      // 右 Win
                case 0x5D: return (0x65, 0);      // アプリケーションキー
                default: return (0, 0);
            }
        }

        if (sc >= 0x02 && sc <= 0x0A) return ((byte)(0x1E + (sc - 0x02)), 0); // 1..9
        if (sc >= 0x10 && sc <= 0x19) return (RowQ[sc - 0x10], 0);
        if (sc >= 0x1E && sc <= 0x26) return (RowA[sc - 0x1E], 0);
        if (sc >= 0x2C && sc <= 0x32) return (RowZ[sc - 0x2C], 0);
        if (sc >= 0x3B && sc <= 0x44) return ((byte)(0x3A + (sc - 0x3B)), 0); // F1..F10

        switch (sc)
        {
            case 0x01: return (0x29, 0);          // Esc
            case 0x0B: return (0x27, 0);          // 0
            case 0x0C: return (0x2D, 0);          // - (JIS: -)
            case 0x0D: return (0x2E, 0);          // = (JIS: ^)
            case 0x0E: return (0x2A, 0);          // Backspace
            case 0x0F: return (0x2B, 0);          // Tab
            case 0x1A: return (0x2F, 0);          // [ (JIS: @)
            case 0x1B: return (0x30, 0);          // ] (JIS: [)
            case 0x1C: return (0x28, 0);          // Enter
            case 0x1D: return (0, 0x01);          // 左 Ctrl
            case 0x27: return (0x33, 0);          // ;
            case 0x28: return (0x34, 0);          // ' (JIS: :)
            case 0x29: return (0x35, 0);          // ` (JIS: 半角/全角)
            case 0x2A: return (0, 0x02);          // 左 Shift
            case 0x2B: return (jis ? (byte)0x32 : (byte)0x31, 0); // \ (JIS: ])
            case 0x33: return (0x36, 0);          // ,
            case 0x34: return (0x37, 0);          // .
            case 0x35: return (0x38, 0);          // /
            case 0x36: return (0, 0x20);          // 右 Shift
            case 0x37: return (0x55, 0);          // テンキー *
            case 0x38: return (0, 0x04);          // 左 Alt
            case 0x39: return (0x2C, 0);          // Space
            case 0x3A: return (0x39, 0);          // Caps Lock
            case 0x45: return (0x53, 0);          // Num Lock
            case 0x46: return (0x47, 0);          // Scroll Lock
            case 0x47: return (0x5F, 0);          // テンキー 7
            case 0x48: return (0x60, 0);          // テンキー 8
            case 0x49: return (0x61, 0);          // テンキー 9
            case 0x4A: return (0x56, 0);          // テンキー -
            case 0x4B: return (0x5C, 0);          // テンキー 4
            case 0x4C: return (0x5D, 0);          // テンキー 5
            case 0x4D: return (0x5E, 0);          // テンキー 6
            case 0x4E: return (0x57, 0);          // テンキー +
            case 0x4F: return (0x59, 0);          // テンキー 1
            case 0x50: return (0x5A, 0);          // テンキー 2
            case 0x51: return (0x5B, 0);          // テンキー 3
            case 0x52: return (0x62, 0);          // テンキー 0
            case 0x53: return (0x63, 0);          // テンキー .
            case 0x54: return (0x46, 0);          // Alt+PrintScreen (SysRq)
            case 0x56: return (0x64, 0);          // ISO 配列の追加キー(バックスラッシュ)
            case 0x57: return (0x44, 0);          // F11
            case 0x58: return (0x45, 0);          // F12
            // JIS 専用キー
            case 0x70: return (0x88, 0);          // カタカナ/ひらがな
            case 0x73: return (0x87, 0);          // _ (ろ)
            case 0x79: return (0x8A, 0);          // 変換
            case 0x7B: return (0x8B, 0);          // 無変換
            case 0x7D: return (0x89, 0);          // ¥
            default: return (0, 0);
        }
    }
}
