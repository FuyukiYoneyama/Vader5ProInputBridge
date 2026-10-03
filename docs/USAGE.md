# 利用ガイド

## 準備

| 用意するもの | 内容・入手先 |
|---|---|
| PCとパッド | Windows x64（64ビット）と、USB接続のVADER 5 PRO |
| .NET 10 | .NET Desktop Runtime（Windowsアプリの実行環境）10のWindows x64版。[Microsoftのダウンロードページ](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)で「.NET Desktop Runtime」→「Windows」→「x64」を選ぶ |
| vJoy（仮想ゲームコントローラー） | [配布元のReleases](https://github.com/BrunnerInnovation/vJoy/releases)。設定画面例は2.2.2.0 |
| OpenTrack | 姿勢をゲームの視点移動へ使う場合に用意する。[配布元のReleases](https://github.com/opentrack/opentrack/releases) |

配布用ZIPを使う場合は、この実行環境を用意する。インストーラーがPC再起動を案内した場合は、再起動後に設定を進める。

### 配布物の確認

（配布EXEにはAuthenticode（Windowsで発行元を確認するコード署名）を付けていない。）Windows SmartScreen（ダウンロードしたアプリの評判を確認する機能）が警告を表示する場合がある。[Microsoftの説明](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/)を参照する。GitHubのReleaseからZIPと同名の`.sha256`を入手し、展開前にファイルのSHA-256（ファイル内容の識別値）を照合する。

```powershell
Get-FileHash .\VADERBridge-1.0.6.zip -Algorithm SHA256
```

表示されたHashを`.sha256`内の識別値と照合する。SHA-256はZIPの内容を照合するための値で、発行元の確認は入手したGitHubリポジトリとReleaseの対応で行う。

### vJoyの設定

vJoyはWindowsに仮想のゲームパッドを登録するドライバー。BridgeがVADERの入力をvJoyへ書き込み、ゲームがDirectInput（Windowsのゲームコントローラー読取り方式）で読み取る。通常入力と追加ボタンの出力先はDevice 1。

1. vJoyをインストールする。
2. Bridgeを終了した状態でvJoyConf（vJoyの機器設定ツール）を開く。
3. 上部の「1」を選び、次の構成にする。
4. 左下の「Enable vJoy」をONにして「Apply」を押し、画面を閉じる。

| 項目 | 設定 |
|---|---|
| 軸 | X、Y、Z、Rx、Ry、Slider、Dial/Slider2 |
| Number of Buttons | 20以上 |
| POV（十字キーの方向入力） | Continuous（角度指定）1個 |
| Rz | OFF。有効な場合は中央を保持 |
| Enable Effects | Force Feedback（振動・力覚のフィードバック）用。入力割当と独立した設定。画面例ではON |

（このBridgeはゲームからの振動・力覚をVADERへ中継しない。）vJoy 2への出力は既定でOFF。

<details>
<summary>vJoyの設定画面例（2.2.2.0）</summary>

![vJoyConf：Device 1、7軸、20ボタン、Continuous POV 1個](images/vjoy-device1-settings.jpg)

</details>

## 起動と常駐

BridgeはVADERの拡張入力（追加ボタン・ジャイロなどの入力）を取得し、通常入力と追加ボタンをvJoy 1へ送る。**Bridge使用中は、ゲーム側でvJoy 1のDirectInput入力を割り当てて使用する。**

（拡張入力の取得中は、VADER側の仕様により、パッドの標準XInput（Windows標準ゲームパッドの読取り方式）出力が停止する。これは拡張入力の取得要求に対するVADER側の動作であり、BridgeにXInput出力を停止させる処理を実装したものではない。）

ZIPを書込み可能なフォルダーへ展開し、`VADERBridge.exe`を起動する。設定と測定IDは展開先の`config/`へ保存する。ソースからビルドした場合は`apps/VADERBridge/VADERBridge.exe`。

入力の取得とvJoy出力を自動開始し、通知領域（時計の横のアイコン欄）に常駐する。初期化の案内に従って約2秒静止させ、使用する持ち方でHOMEを短く押して離す。

アイコンのダブルクリックまたは通知のクリックで状態画面を開く。再度EXEを起動しても既存の状態画面を開く。画面の×は常駐へ戻る操作。

| メニュー | 操作 |
|---|---|
| 状態画面を開く | 入力と書込み値、姿勢角度、処理状態を表示 |
| 一時停止／再開 | 取得と出力を終了／自動取得と出力を再開 |
| ログ採取開始／停止 | 必要な区間を記録／保存待ちの行を書き終えて閉じる |
| 設定 | ログオン時起動、OpenTrack同時起動、姿勢送信、診断用の送信方式 |
| 終了 | ボタン・十字キー解放、軸の休止値、機器と通信資源の解放を済ませて終了 |

アイコンの印は、青が初期化、緑が使用可能、黄が待機・再接続、赤がエラー。起動後5秒を超える待機は画面にも理由を表示する。

## ボタンと軸

| VADER入力 | vJoy 1 |
|---|---|
| A/B/X/Y/LB/RB/Back/Start/LS/RS | ボタン1～10の順 |
| M1/M2/M3/M4 | ボタン11/12/13/14 |
| LM/RM/C/Z/Fn/HOME | ボタン15/16/17/18/19/20 |
| 左スティック左右／上下 | X／Y |
| 右スティック左右／上下 | Rx／Ry |
| LT／RT | スライダー1／スライダー2 |
| LTとRTの差 | Z |
| 十字キー | POV 1 |

スティックは左・上が最小、右・下が最大。スライダーは解放時が最小、押し切りが最大。Zは`(LT − RT) / 255`を全範囲へ変換し、同量なら中央になる。

既定構成ではvJoy 1に入力を統合し、vJoy 2への出力はOFF。

## OpenTrack

Bridgeから受けた姿勢角度を、OpenTrackがゲームの視点移動へ変換する。入力設定はUDP（アプリ間でデータを送る通信方式）を使う。

| 項目 | 設定 |
|---|---|
| Input（入力方式） | UDP over network |
| Input横の工具ボタン → Port（受信ポート） | 4242。Bridgeの既定送信先は同じPCの`127.0.0.1:4242` |
| Add to axis（受信角度へ加える補正） | yaw、pitch、rollをすべて0 |
| Output（ゲームへの出力方式） | 使用するゲームに合わせる。画面例はfreetrack 2.0 Enhanced |
| Filter（動きを滑らかにする処理） | 画面例はAccela。操作感に合わせて調整 |

1. 上の項目を設定し、「Start」で追跡を開始する。
2. Bridgeの初期化後、使用する持ち方でHOMEを短く押して離す。
3. パッドを動かし、Bridgeの送信角度と「Raw tracker data（受信した姿勢角度）」の変化を確認する。
4. 「Game data（ゲームへ送る値）」にも反映され、ゲームの視点が動くことを確認する。

視点の動く量は「Mapping（入力角度とゲーム内の視点角度の対応）」で調整する。詳しい設定は[OpenTrackの公式設定ガイド](https://github.com/opentrack/opentrack/wiki/Quick-Start-Guide-(WIP))を参照する。

<details>
<summary>OpenTrackの設定画面例（2023.3.0）</summary>

![OpenTrack：UDP over network、freetrack 2.0 Enhanced、Accela](images/opentrack-main-settings.jpg)

![UDP入力：Port 4242、yaw・pitch・rollへの追加角度0](images/opentrack-udp-settings.jpg)

</details>

Yaw（左右へ向きを変える回転）はジャイロ角速度の積分、Pitch（前後の傾き）とRoll（左右の傾き）はジャイロと重力方向による計算を使用する。停止した位置を保持し、HOME短押しの解放で正面を更新する。既定の短押しは600 ms未満。（HOME長押しには追加処理を割り当てない。）

初期値は100回/秒の送信、30 msの平滑化、Yaw±180°、Pitch/Roll±89°。角度上限では端の値に飽和し、逆方向の回転で戻る。

設定画面のOpenTrack同時起動はEXEを起動する機能。入力方式と追跡開始はOpenTrack側で設定する。

## 設定とログ

共有する既定設定は`config/bridge.json`。変更は次の起動で読み込む。常駐の個別設定は`config/application.json`へ保存する。Windowsログオン時起動とOpenTrack同時起動の初期値はOFF。

ログの起動時の初期値はOFF。「ログ採取開始」でレポート（入力データ一式）の生データ、通常入力と全ボタン、ジャイロ・加速度、姿勢計算の結果、出力状態をJSONL（1行ごとのJSON記録）で`logs/測定ID/`に保存する。レポートの連番と受信時刻を各記録へ付ける。回復中に保持したレポートは、生データと保持理由を記録する。

採取を再開した場合は別ファイルを作る。停止時に保存待ちの行を書き終えて閉じる。表示OFFや画面を閉じた状態でも、開始済みの出力と採取は続く。記録量は入力頻度と採取時間に応じて増えるため、診断する操作の前に開始し、終了後に停止する。画面で採取件数・書出し件数・破棄件数と保存エラーを確認する。

ログにはWindowsアカウント名、実行パス、機器の識別子、例外の詳細が含まれる。問い合わせではまずアプリ版、接続方式、操作手順、状態表示を共有し、ログを添付する際は個別の識別情報を確認する。

## 接続回復と継続改善

USB有線を初期対象とし、USBドングルの再接続も評価対象とする。外部アプリの設定画面例はvJoy 2.2.2.0、OpenTrack 2023.3.0。測定時はWindows、ファームウェア、外部アプリの版、接続方式、同時に使う設定アプリを記録する。

読取り・入力処理の回復中はvJoyとOpenTrackの最終値を保持し、入力再開後に更新する。入力処理で例外が発生した場合は次の入力から処理を再開する。vJoyの取得状態を定期確認し、取得の変化や書込みエラーの際は取得をやり直して最新入力を書き戻す。vJoyの回復では、復帰用の休止値書込みを省いて再取得へ進む。

これまでのUSB再接続の確認では、動作と停止を繰り返し、安定まで約20～30秒かかる例があった。この時間を各版の実機確認で継続評価する。通知領域と画面で初期化・回復の状況を確認し、5秒を超える待機は表示された理由を確認する。

Yawは角速度の積算による微小ドリフトがあり、HOME短押しで正面を更新できる。VADER側の拡張入力取得時の仕様と使用する入力経路は、[起動と常駐](#起動と常駐)に記載している。取得方式や機器の版による標準XInputと拡張入力の同時更新は、継続評価の対象とする。

## 版を更新した後の確認

1. 現在のBridgeをアイコンメニューから終了し、新しいZIPを書込み可能なフォルダーへ展開して起動する。
2. 初期化後、スティック・両トリガー・十字キー・20ボタンのvJoy 1への出力と、OpenTrackの受信角度を確認する。
3. HOME短押しによる正面更新、一時停止／再開、終了を確認する。
4. USBを再接続し、回復状態の表示、値の保持、更新再開、安定までの時間を確認する。
5. 診断が必要な区間でログを開始し、表示OFFのまま操作した後に採取を停止する。入力と処理・出力の連番、書出し件数、破棄件数、保存結果を照合する。

1.0.6では.NET 10 Desktop Runtimeを用意して確認する。入力処理回復とvJoy再取得は、例外時と取得状態が変わった時の実機確認で結果を追記する。
