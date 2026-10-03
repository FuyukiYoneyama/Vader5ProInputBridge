# 第三者の著作権表示

本プロジェクトのC#実装・スクリプト・アイコン・文書には[MITライセンス](LICENSE)を適用する。参照元に由来する部分については、その著作権表示とライセンス本文も保持する。

## SDLのFlydigi実装

`src/VaderBridge/VaderReportDecoder.cs`のボタン位置・センサー換算と、`HidConnection.cs`の取得／解除要求の形式は、SDLのFlydigi実装を基にC#へ適応したもの。

- 著作権表示：Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>
- ライセンス：Zlib。本文は[licenses/SDL-LICENSE.txt](licenses/SDL-LICENSE.txt)。
- 参照ソース：[SDL_hidapi_flydigi.c](https://github.com/libsdl-org/SDL/blob/fa2c02bb6e21974a89ea9824bc53c9932abe5f9c/src/joystick/hidapi/SDL_hidapi_flydigi.c)。
- C#への適応、Windowsの読取り、時刻・状態管理、出力統合は本プロジェクトの実装。

SDLの元実装と本実装を区別し、元の表示を参照ファイルのヘッダーにも記載する。（現行アプリの配布物にSDLの実行ライブラリは含まれない。）

## 外部アプリとWindows

| 名称 | 関係 | 配布・参照 |
|---|---|---|
| vJoy | インストール済みの`vJoyInterface.dll`を実行時に使用 | [vJoy](https://github.com/jshafer817/vJoy)。配布元のライセンスに従い、別途インストール |
| OpenTrack | UDP（アプリ間でデータを送る通信方式）で姿勢角度を受信 | [OpenTrack](https://github.com/opentrack/opentrack)。別途インストール |
| Windows API | HID、DInput、XInput、ウィンドウ操作 | Windows SDKの関数宣言とデータ配置に従うC#相互運用実装 |
| Pillow | アイコン生成時に使用するPythonライブラリ | [Pillow](https://github.com/python-pillow/Pillow)。開発用として別途用意 |

OpenTrackへ送るデータの軸順序と形式は、[公式UDP受信処理](https://github.com/opentrack/opentrack/blob/master/tracker-udp/ftnoir_tracker_udp.cpp)を参照して実装している。

アプリの配布用ZIPには、本プロジェクトの実行物、既定設定、利用ガイド、MIT本文、本書、SDLのライセンス本文を収録する。外部アプリのインストールとライセンスは各配布元で扱う。
