# spike

PC のキー入力を Wi-Fi 経由で Switch 2 に USB キーボードとして送る仕組み。

```
PC (spike-client) --UDP:9000--> Pico W (spike-bridge) --USB HID--> Switch 2 ドック (USB-A)
```

- **spike-client** (`spike/client`): C# WinForms。PC のキー入力を低レベルフックで捕捉し、HID boot keyboard レポートとして UDP で送る。
- **spike-bridge** (`spike/bridge`): Pico W のファームウェア (Arduino-Pico + Adafruit TinyUSB)。UDP で受けたレポートを USB キーボードとして出力する。

## ディレクトリ構成

```
spike/
  bridge/
    bridge.ino          ファームウェア本体
    secrets.h.example   Wi-Fi 設定のひな形 (secrets.h にコピーして使う)
  client/
    SpikeClient.csproj
    Program.cs        エントリポイント(引数の解釈)
    MainForm.cs       UI と、フック / 送信の配線
    KeyboardHook.cs   低レベルキーボードフック(Win32 呼び出し)
    BridgeSender.cs   キー状態の保持と Pico への UDP 送信
    HidMap.cs         スキャンコード → HID Usage ID の変換
```

`bridge.ino` というファイル名なのは、Arduino のスケッチは「フォルダ名と同名の .ino」が必須なため。

## パケット仕様

9 バイト固定。HID boot keyboard レポートをそのまま送る。

| offset | 内容 |
|---|---|
| 0 | マジック `0xA5` |
| 1 | modifier |
| 2 | 予約 (0) |
| 3-8 | key0 〜 key5 (HID usage ID) |

- PC は状態が変わるたびに全状態を送る。変化後は 2 回追送し、キー押下中は約 100ms ごとにハートビートを送る。
- Pico は最後に受信してから 500ms 途絶えると全キーを離す。

## セットアップ

### ファームウェア

1. arduino-cli と rp2040 コアを入れる。

   ```
   winget install ArduinoSA.CLI
   arduino-cli config add board_manager.additional_urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
   arduino-cli core install rp2040:rp2040
   ```

2. `spike/bridge/secrets.h.example` を `secrets.h` にコピーし、SSID とパスワードを書く。Pico W は 2.4GHz のみ対応。

3. ビルドして書き込む。初回は BOOTSEL を押しながら USB 接続し、出てきた `RPI-RP2` ドライブに UF2 をコピーする。2 回目以降は COM ポート経由で書ける。

   ```
   arduino-cli compile --fqbn rp2040:rp2040:rpipicow:usbstack=tinyusb spike/bridge
   arduino-cli upload -p COM3 --fqbn rp2040:rp2040:rpipicow:usbstack=tinyusb spike/bridge
   ```

   USB Stack は `Adafruit TinyUSB` (`usbstack=tinyusb`) にする。

4. Pico の IP は、USB シリアル (115200bps) に 3 秒ごとに出る `wifi=3 ip=...` で確認できる。ルータで DHCP 予約しておくと楽。

### クライアント

.NET 8 以降が必要 (Windows)。

```
dotnet run --project spike/client -- <PicoのIP> [--jis]
```

- `--jis`: PC が JIS キーボードのとき指定する。`\` キーの割り当てが変わり、半角/全角が使える。
- IP を省略するとブロードキャスト (255.255.255.255) で送る。IP を指定するほうが低遅延。

ウィンドウが前面にあり「送信」にチェックが入っている間だけ、全キー入力を握りつぶして Pico に送る。マウスでチェックを外すと解除できる。`Ctrl+Alt+Del` と `Win+L` は Windows が先に処理するため捕捉できない。

## 注意点

- Pico は USB の CDC (シリアル) と HID キーボードの複合デバイスとして見える。Switch 2 では動作確認済み。
- 半角/全角キーは、JIS ドライバが離上イベントを出さないため、押下 1 回を約 40ms のタップとして送っている。
- UDP の欠落対策は、全状態の再送 (追送とハートビート) のみ。認証もないため、同じ LAN の誰でもパケットを送れる。
- Pico を PC に挿したままクライアントを動かすと、Pico が打った入力をクライアント自身が拾ってループする。Switch に挿して使うこと。
