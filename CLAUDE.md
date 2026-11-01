# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 概要

PC のキー入力を Wi-Fi 経由で Switch 2 に USB キーボードとして送る spike。2 つの部品で構成される。

```
PC (spike/client) --UDP:9000--> Pico W (spike/bridge) --USB HID--> Switch 2 ドック
```

詳細(パケット仕様、セットアップ手順、注意点)は [README.md](README.md) を参照。

## 使用技術

- **spike/client**: C# / .NET 8 (`net8.0-windows`, `RollForward=LatestMajor`) / WinForms。Nullable 有効、ImplicitUsings 無効(`using` は明示する)。
- **spike/bridge**: Arduino スケッチ。Raspberry Pi Pico W、Arduino-Pico コア、USB スタックは Adafruit TinyUSB。
- 通信: UDP ポート 9000、HID boot keyboard レポートを 9 バイト(`0xA5` + modifier + 予約 + key×6)で送る。
- テスト・リンタは無し。

## コマンド

```bash
# クライアントの起動 (引数: <PicoのIP> [--jis]。保存済み設定より優先)
dotnet run --project spike/client

# ファームウェアのビルドと書き込み (COM ポートは環境に合わせる)
arduino-cli compile --fqbn rp2040:rp2040:rpipicow:usbstack=tinyusb spike/bridge
arduino-cli upload -p COM3 --fqbn rp2040:rp2040:rpipicow:usbstack=tinyusb spike/bridge
```

ビルド前に `spike/bridge/secrets.h.example` を `secrets.h` にコピーして Wi-Fi 情報を書く(`secrets.h` は git 管理外)。

## 構成

- `spike/client/`
  - `Program.cs`: エントリポイント、引数の解釈
  - `AppSettings.cs`: 送信先・配列を exe と同じフォルダの `settings.json` に保存・復元
  - `MainForm.cs`: UI と、フックと送信の配線
  - `KeyboardHook.cs`: 低レベルキーボードフック (WH_KEYBOARD_LL)。Win32 呼び出しはここに閉じ込める
  - `BridgeSender.cs`: キー状態の保持と UDP 送信
  - `HidMap.cs`: スキャンコード → HID Usage ID の変換
- `spike/bridge/bridge.ino`: ファームウェア本体。ファイル名はフォルダ名と揃える Arduino の規則による。

## 設計上の要点

- **全状態送信**: クライアントは状態が変わるたびにキー状態の全体を送る。Pico 側は状態を持たず、受け取った内容をそのまま USB に反映する。UDP の取りこぼしは追送(変化後 2 回)とハートビート(押下中は約 100ms ごと)で補う。
- **タイムアウト**: Pico は最後の受信から 500ms 途絶えると全キーを離す(`HOLD_TIMEOUT_MS`)。
- **キーの握りつぶし**: `KeyboardHook` のハンドラが true を返すと Windows に渡さない。`MainForm.OnKey` は「送信中」「入力欄を編集していない」「ウィンドウが前面」のすべてを満たすときだけ握りつぶす。入力欄に移る時やウィンドウが非アクティブになる時は `ReleaseAll()` で押下中のキーを離す。
- **UI スレッド前提**: `BridgeSender` は WinForms の Timer を使い、`KeyboardHook` もメッセージループのあるスレッドで作る必要がある。
- **設定変更時**: `MainForm.Apply()` が設定を保存し、送信中なら `BridgeSender` を作り直す。
- **JIS の半角/全角**: 離上イベントが来ないため、押下 1 回を約 40ms のタップとして扱う (`BridgeSender.Tap`)。
