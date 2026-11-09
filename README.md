# NekoNoTe

ゲーマー向けの便利ツールをまとめた Windows アプリです。

## 機能

### Roblox

- Roblox の画面でログインしてアカウントを追加
- `.ROBLOSECURITY` を貼り付けてアカウントを追加
- Roblox Account Manager の `AccountData.json` を読み込み
- アカウントの一覧表示、メモ、ログイン切れの確認
- ゲームの URL やプレイス ID、JobID を指定して、選んだアカウントで起動
- 多重起動

### 地震

- 地震情報の一覧、震度分布の地図、詳細、統計
- 緊急地震速報の通知

同じネットワークに複数の PC がある時は、自動で 1 台だけが受信して他の PC に配ります(P2P地震情報への接続は IP アドレスごとに 2 つまでのため)。初回の起動で Windows のファイアウォールの確認が出たら、プライベート ネットワークを許可してください。

遅れたり届かないことがあります。防災の判断はこのアプリだけに頼らず、スマートフォンの緊急速報もオンにしておいてください。

### アップデート

起動時に新しいバージョンを確認して、アプリの中からアップデートできます。

## 使い方

[Releases](https://github.com/koppepandayooo/NekoNoTe/releases/latest) から `NekoNoTe-Setup-○○.exe` をダウンロードして実行してください(Windows 10 / 11、64bit)。管理者権限はいりません。

- 署名していないので、初回に SmartScreen の警告が出ることがあります。「詳細情報」→「実行」で起動できます。
- ログイン画面が出ない時は [Microsoft Edge WebView2 ランタイム](https://developer.microsoft.com/microsoft-edge/webview2/) を入れてください。

## ログイン情報について

ログイン情報(Cookie)は `%APPDATA%\NekoNoTe\roblox-accounts.dat` に、Windows の DPAPI で暗号化して保存します。保存した PC の同じ Windows ユーザーでしか読めません。Roblox 以外には送信しません。

Cookie はアカウントそのものと同じ価値があるので、他の人に渡さないでください。

## ビルド

.NET 8 SDK と Inno Setup 6 が必要です。

```
dotnet publish NekoNoTe.csproj -c Release -o dist
ISCC.exe /DAppVersion=0.2.0 installer\NekoNoTe.iss
```

## 注意

Roblox Corporation とは関係のない非公式のツールです。複数のアカウントや多重起動の使い方によっては Roblox の規約で問題になることがあるので、自己責任で使ってください。

## ライセンス

[GNU General Public License v3.0](LICENSE)

このプログラムは [Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager)(作者 ic3w0lf22、GPL-3.0)のアカウント管理と多重起動の機能を元に C# / WPF で作り直し、変更を加えたものです(2026年11月)。

データの出典:

- 地震情報: [P2P地震情報](https://www.p2pquake.net/)
- 地図: 「[国土数値情報(行政区域データ)](https://nlftp.mlit.go.jp/ksj/gml/datalist/KsjTmplt-N03-2024.html)」(国土交通省、CC BY 4.0)を加工して作成

使用しているライブラリ:

- [WPF-UI](https://github.com/lepoco/wpfui)(MIT)
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
