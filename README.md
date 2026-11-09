# NekoNoTe

Roblox のアカウントを切り替えて起動するための Windows アプリです。

## できること

- Roblox の画面でログインしてアカウントを追加
- `.ROBLOSECURITY` を貼り付けてアカウントを追加
- Roblox Account Manager の `AccountData.json` を読み込み
- アカウントの一覧表示、メモ、ログイン切れの確認
- ゲームの URL やプレイス ID、JobID を指定して、選んだアカウントで起動

## 使い方

Windows 10 / 11(64bit)で `NekoNoTe.exe` を実行します。

- 初回に SmartScreen の警告が出たら、「詳細情報」→「実行」で起動できます。
- ログイン画面が出ない時は [Microsoft Edge WebView2 ランタイム](https://developer.microsoft.com/microsoft-edge/webview2/) を入れてください。

## ログイン情報について

ログイン情報(Cookie)は `%APPDATA%\NekoNoTe\roblox-accounts.dat` に、Windows の DPAPI で暗号化して保存します。保存した PC の同じ Windows ユーザーでしか読めません。Roblox 以外には送信しません。

Cookie はアカウントそのものと同じ価値があるので、他の人に渡さないでください。

## ビルド

.NET 8 SDK が必要です。

```
dotnet publish NekoNoTe.csproj -c Release -o dist
```

## 注意

Roblox Corporation とは関係のない非公式のツールです。複数のアカウントの使い方によっては Roblox の規約で問題になることがあるので、自己責任で使ってください。

## ライセンス

[GNU General Public License v3.0](LICENSE)

このプログラムは [Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager)(作者 ic3w0lf22、GPL-3.0)のアカウント管理の機能を元に C# / WPF で作り直し、変更を加えたものです(2026年11月)。

使用しているライブラリ:

- [WPF-UI](https://github.com/lepoco/wpfui)(MIT)
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
