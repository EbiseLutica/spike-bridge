// spike-bridge: Pico W 用 USB キーボードブリッジ (spike-client のファームウェア)
//
// PC --(Wi-Fi / UDP 9000)--> Pico W --(USB HID キーボード)--> Switch 2
//
// ビルド設定
//   Board     : Raspberry Pi Pico W (Arduino-Pico コア by Earle Philhower)
//   USB Stack : Adafruit TinyUSB    (Tools > USB Stack / FQBN の usbstack=tinyusb)
//
// パケット(9 バイト): [0xA5, modifier, 0x00, key0, key1, key2, key3, key4, key5]
// PC は毎回 HID boot keyboard レポート全体を送るので、こちらは状態を持たない。
// 取りこぼしは次のパケットで補正される。キー押下中にパケットが途絶えた場合は、
// HOLD_TIMEOUT_MS 後に全キーを離す。

#include <Adafruit_TinyUSB.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include "secrets.h"  // WIFI_SSID / WIFI_PASS を定義する(git 管理外)

static const uint16_t UDP_PORT = 9000;
static const uint8_t  MAGIC = 0xA5;
static const uint32_t HOLD_TIMEOUT_MS = 500;
static const uint32_t WIFI_RETRY_MS = 10000;
static const uint32_t BLINK_MS = 50;
static const uint32_t IDLE_TIMEOUT_MS = 5 * 60 * 1000;// 受信がこの時間途絶えたら低電力モードへ
static const uint32_t LOW_POWER_LOOP_MS = 10;           // 低電力中の loop() の休止時間
static const uint32_t LOW_POWER_BLINK_PERIOD_MS = 2000;
static const uint32_t LOW_POWER_BLINK_ON_MS = 100;

static const uint8_t desc_hid_report[] = { TUD_HID_REPORT_DESC_KEYBOARD() };
static Adafruit_USBD_HID usb_hid;
static WiFiUDP udp;

static uint8_t desired[8];  // modifier, 予約, key0..key5(PC が望む状態)
static uint8_t sent[8];     // ホストに通知済みの状態
static uint32_t lastRx = 0;  // 最後に有効なパケットを受信した時刻
static uint32_t lastWifiTry = 0;
static bool udpUp = false;
static bool lowPower = false;
static uint32_t blinkUntil = 0;  // この時刻まで LED を消す(キー受信の表示)
static bool ledOn = false;

/// desired が sent と異なれば USB ホストへ送る。
static void pushHid() {
  if (!usb_hid.ready()) return;
  if (memcmp(desired, sent, sizeof(desired)) == 0) return;
  // ホストが受け取るまで loop() のたびに再試行するので、離上が失われない
  if (usb_hid.keyboardReport(0, desired[0], &desired[2])) {
    memcpy(sent, desired, sizeof(sent));
  }
}

/// 全キーを離した状態にしてホストへ送る。
static void releaseAll() {
  memset(desired, 0, sizeof(desired));
  pushHid();
}

/// next (modifier, 予約, key0..key5) に、desired で押されていないキーか修飾キーが含まれるか。
static bool hasNewPress(const uint8_t* next) {
  if (next[0] & ~desired[0]) return true;
  for (uint8_t i = 2; i < 8; i++) {
    if (next[i] && !memchr(desired + 2, next[i], 6)) return true;
  }
  return false;
}

/// LED を切り替える。Pico W の LED は CYW43 経由で通信が発生するので、変化した時だけ書く。
static void setLed(bool on) {
  if (on == ledOn) return;
  ledOn = on;
  digitalWrite(LED_BUILTIN, on ? HIGH : LOW);
}

/// 低電力モードを切り替える。低電力中は Wi-Fi の省電力を有効にし、loop() を休ませる。
static void setLowPower(bool on) {
  if (on == lowPower) return;
  lowPower = on;
  if (on) {
    WiFi.defaultLowPowerMode();
  } else {
    WiFi.noLowPowerMode();
  }
  Serial.println(on ? "enter low power mode" : "leave low power mode");
}

/// 状態に応じて LED を更新する。
/// 未接続なら消灯、低電力中はゆっくり点滅、キー押下直後は一瞬消灯、それ以外は点灯。
static void updateLed(bool up) {
  if (!up) {
    setLed(false);
  } else if (lowPower) {
    setLed(millis() % LOW_POWER_BLINK_PERIOD_MS < LOW_POWER_BLINK_ON_MS);
  } else {
    setLed((int32_t)(millis() - blinkUntil) >= 0);
  }
}

/// USB HID キーボードの初期化と Wi-Fi 接続の開始。
void setup() {
  usb_hid.setBootProtocol(HID_ITF_PROTOCOL_KEYBOARD);
  usb_hid.setPollInterval(2);
  usb_hid.setReportDescriptor(desc_hid_report, sizeof(desc_hid_report));
  usb_hid.begin();

  // この USB スタックは、IDE からの自動リセット書き込みに Serial.begin() が必要
  Serial.begin(115200);

  pinMode(LED_BUILTIN, OUTPUT);
  WiFi.noLowPowerMode();  // Wi-Fi の省電力を切って遅延を下げる
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  lastWifiTry = millis();
}

/// Wi-Fi 接続の維持、UDP パケットの受信、HID レポートの送出を繰り返す。
void loop() {
  const bool up = (WiFi.status() == WL_CONNECTED);

  if (up && !udpUp) {
    udp.begin(UDP_PORT);
    udpUp = true;
    Serial.print("UDP listening on ");
    Serial.print(WiFi.localIP());
    Serial.print(':');
    Serial.println(UDP_PORT);
    // WiFi.begin() が省電力を切るので、低電力中なら掛け直す
    if (lowPower) WiFi.defaultLowPowerMode();
  } else if (!up) {
    if (udpUp) {
      udp.stop();
      udpUp = false;
    }
    if (millis() - lastWifiTry > WIFI_RETRY_MS) {
      releaseAll();  // WiFi.begin() がブロックしうるので、その間キーを押したままにしない
      lastWifiTry = millis();
      WiFi.begin(WIFI_SSID, WIFI_PASS);
    }
  }

  // IP をいつでも確認できるよう、状態をシリアルに定期出力する
  static uint32_t lastStat = 0;
  if (millis() - lastStat > 3000) {
    lastStat = millis();
    Serial.print("wifi=");
    Serial.print(WiFi.status());
    if (up) {
      Serial.print(" ip=");
      Serial.print(WiFi.localIP());
    }
    Serial.println();
  }

  if (udpUp && udp.parsePacket() > 0) {
    uint8_t buf[16];
    const int len = udp.read(buf, sizeof(buf));
    if (len == 9 && buf[0] == MAGIC) {
      buf[2] = 0;  // 予約バイト
      // 追送・ハートビート・離上では光らせず、新たな押下があった時だけ一瞬消灯する
      if (hasNewPress(buf + 1)) blinkUntil = millis() + BLINK_MS;
      memcpy(desired, buf + 1, 8);
      lastRx = millis();
      setLowPower(false);
    }
  }
  if (up && millis() - lastRx > IDLE_TIMEOUT_MS) setLowPower(true);
  updateLed(up);

  // キー押下中にパケットが途絶えたら全キーを離す
  bool anyDown = false;
  for (uint8_t i = 0; i < sizeof(desired); i++) {
    if (desired[i]) anyDown = true;
  }
  if (anyDown && millis() - lastRx > HOLD_TIMEOUT_MS) {
    memset(desired, 0, sizeof(desired));
  }

  pushHid();

  // 低電力中は CPU を休ませる。TinyUSB のタスクは loop() 後の yield() で回るので USB は維持される
  if (lowPower) delay(LOW_POWER_LOOP_MS);
}
