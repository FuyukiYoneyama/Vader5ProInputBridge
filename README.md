# Vader5ProInputBridge

![アプリのアイコン](src/VaderBridge/Assets/VADERBridge.png)

VADER 5 PROの入力をvJoy（仮想ゲームコントローラー）へ、姿勢角度をOpenTrackへ送るWindowsアプリです。初期対象はUSB接続のVADER 5 PRO。現在の版は**1.0.3**です。

## 主な機能

- 通常入力と追加ボタンをvJoy 1へ統合。20ボタン、スティック、独立トリガー、十字キーを出力。
- ジャイロと加速度からYaw（左右へ向きを変える回転）、Pitch（前後の傾き）、Roll（左右の傾き）を計算し、OpenTrackへ送信。
- HOME短押しで正面を更新。
- 起動後に取得と出力を自動開始し、通知領域（時計の横のアイコン欄）へ常駐。
- 固定入力表示、表示ON／OFF、一時停止／再開、必要な区間のログ採取。
- 接続回復中は最終値を保持し、再接続後に更新を再開。

## 使用環境と起動

Windows x64、.NET 8のWindowsデスクトップ実行環境、インストール済みのvJoyを使用します。姿勢の利用にはOpenTrackを追加します。

1. vJoy 1に20ボタン以上、X/Y/Z/Rx/Ry/Slider/Dial・Slider2、Continuous POV（角度指定の十字キー）1個を設定します。
2. 配布用ZIPを展開し、`VADERBridge.exe`を起動します。ソースから作る場合は下のビルド手順を使用します。
3. 初期化の案内に従い本体を約2秒静止させ、HOMEを短く押して離します。
4. 通知領域のアイコンをダブルクリックすると状態画面が開きます。終了はアイコンメニューの「終了」を使用します。

ログの初期値はOFFです。OpenTrack側は入力を「UDP over network」、ポートを4242にして追跡を開始します。

[利用ガイドと割当](docs/USAGE.md) · [変更履歴](CHANGELOG.md)

## ビルド

.NET 8 SDK、Git、PowerShell 7を用意し、リポジトリ直下で実行します。

```powershell
.\build-apps.ps1
```

通常の起動先は`apps/VADERBridge/VADERBridge.exe`。診断用の読取りアプリも`apps/`へ生成します。vJoyは実行時にインストール済みのライブラリを読み込みます。

[開発・公開手順](docs/DEVELOPMENT.md) · [構成と通信仕様](docs/ARCHITECTURE.md) · [貢献方法](CONTRIBUTING.md)

## ソース構成

| フォルダー | 内容 |
|---|---|
| `src/VaderBridge` | 取得、入力処理、vJoy出力、姿勢計算、常駐画面 |
| `src/InputTools.Common` | 固定表示、記録、Windows入力の読取り |
| `src/DInputReader` | DInput（Windowsのゲームコントローラー読取り方式）によるvJoyの診断 |
| `src/XInputReader` | XInput（Windows標準ゲームパッドの読取り方式）の診断 |
| `src/VaderHidProbe` | HID（機器の入力・制御用インターフェース）の列挙と生入力の診断 |
| `config` | 共有する既定設定 |
| `docs` | 利用・開発・通信仕様 |
| `tools` | アイコン生成と公開内容の監査 |
| `licenses` | 参照元のライセンス本文 |

実機ログは`logs/`、個別設定は`config/application.json`へ保存します。ローカルの実験資料は`private/`に保管し、これらの保存先をGitの除外設定で扱います。

## 継続改善

Yawの微小ドリフトとUSB再接続後の安定までの時間を継続評価しています。再接続時は画面と通知領域で回復状態を確認できます。機器のファームウェア、接続方式、同時に使う設定アプリを測定条件として記録します。

## ライセンスと出典

本プロジェクトのソースと文書は[MITライセンス](LICENSE)です。著作権者はFUYUKI YONEYAMA。

SDLのFlydigi実装を参照した部分は、元の著作権表示とZlibライセンスも保持しています。参照箇所・外部アプリ・配布範囲は[第三者の著作権表示](THIRD_PARTY_NOTICES.md)にまとめています。
