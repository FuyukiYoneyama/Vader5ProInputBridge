# 開発・公開手順

## 準備とビルド

Windows x64、.NET 10 SDK、PowerShell 7、Gitを用意する。`global.json`はSDK 10.0.100を基準に、10.0の後続機能バンドへのロールフォワード（利用可能な後続SDKを選ぶ設定）を許可する。ソースのビルドは標準のSDK参照だけで行う。.NET 10はLTS（長期サポート版）で、サポート期限は2028年11月14日。[Microsoftのサポート方針](https://dotnet.microsoft.com/en-us/platform/support/policy)を参照する。

```powershell
.\build-apps.ps1
```

生成先は`apps/`。Bridge、XInput Reader、DInput Reader 1・2、VaderHidProbeの5実行物を作る。VaderHidProbeはHID（機器の入力・制御用インターフェース）の生入力を調べる開発用ツールで、通常のビルドとCI（変更ごとの自動確認）の対象に含める。個別のビルドには次を使う。

```powershell
dotnet build src/VaderHidProbe/VaderHidProbe.csproj --configuration Release --configfile NuGet.Config
```

ビルドと実機の確認を分ける。実機の確認にはvJoyとVADERを用意し、[利用ガイド](USAGE.md)に従って通常入力・追加ボタン・OpenTrackの受信を測定する。

## リポジトリの内容

公開する内容はソース、既定設定、文書、アイコン、ライセンス、ビルド・公開監査のスクリプト。以下のローカル保存先は`.gitignore`で扱う。

| 保存先 | 用途 |
|---|---|
| `apps/`、`artifacts/` | ビルドと確認用の生成物 |
| `dist/` | 配布用ZIP |
| `logs/` | 実機の測定ログ |
| `config/application.json`、`config/measurement-id.txt` | 個別設定・測定の識別 |
| `private/`、`history/` | ローカルの実験資料・旧履歴・バックアップ |

共有設定のパスは相対指定または空の既定値にする。vJoyの実行ライブラリはインストール先から読み込む。アイコンの生成にはPillow（画像を生成するPythonライブラリ）を別途用意し、`python tools/create-icons.py`を使用する。

## 版と配布物

`VERSION`を更新し、`CHANGELOG.md`へ変更を書く。ソース・文書をGitに保存し、変更が確定したコミットからビルドする。

```powershell
.\build-apps.ps1
.\package-release.ps1
```

配布先は`dist/VADERBridge-版番号.zip`。実行物の識別一覧は`apps/build-manifest.json`、ZIP内の一覧は`release-manifest.json`。版番号、コミット、SHA-256（ファイル内容の識別値）で対応付ける。識別一覧は相対パスを使用する。

配布用ZIPは収録するファイルを明示し、MIT本文、第三者の著作権表示、利用ガイドと参照する画面画像を含める。画像は`package-release.ps1`で利用ガイド用の3枚（vJoy設定、OpenTrack設定、UDP入力設定）を個別に選び、ZIP直下の`images/`へ保存してガイドの相対参照に合わせる。README用の本体写真と状態画面はリポジトリに保管する。利用ガイドへ画像を追加する際は、配布スクリプトの画像一覧も合わせて更新する。PDB（ソース位置を含むデバッグ情報）は開発用の生成物として保管する。コンパイル時のソース位置は共通のパスへ変換する。

## 公開内容の監査

Gitへ追加する内容を確認してから、標準ライブラリだけで動く監査を実行する。

```powershell
python tools/audit-publication.py
```

監査は追跡ファイルと、公開するGitの到達可能な履歴を対象にする。禁止する保存先・個別設定、秘密情報の典型的な形式、PC名・アカウント名・機器の個別識別情報の形式を調べる。出力は該当ファイルと検出の種類で、秘密情報そのものを表示する処理は省いている。

自動監査と内容の読取りを組み合わせる。新しいスクリーンショット、文書、実機ログを共有する場合は、映り込んだ情報やメタデータ（ファイルに付随する情報）も確認する。

## GitHub

公開するブランチは`main`。リポジトリ作成時の推奨名は`Vader5ProInputBridge`。GitHub上に空のリポジトリを作成した後、その実際のURLを`origin`として設定し、`main`と公開版のタグ（特定の版へ戻るための目印）を送る。

GitHub Actions（GitHub上で実行する作業）にはWindowsでの公開監査・ビルド・ZIP生成を定義している。実機操作を伴う確認はローカルで記録する。Actionsの参照は公式リポジトリのコミット番号で固定する。

GitHub Releasesには配布用ZIPとSHA-256の一覧を添付し、ソースは公開版のタグと対応付ける。
