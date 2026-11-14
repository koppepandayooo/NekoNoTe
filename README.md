# NekoNoTe

ゲーマー向けの便利ツールをまとめた Windows アプリです。

## 機能

### ホーム

- 起動すると最初に開く画面。時計、直近の地震、緊急地震速報の受信の状態、Roblox のアカウント数を表示

### Roblox

- Roblox の画面でログインしてアカウントを追加
- `.ROBLOSECURITY` を貼り付けてアカウントを追加
- Roblox Account Manager の `AccountData.json` を読み込み
- アカウントの一覧表示、メモ、ログイン切れの確認
- ゲームの URL やプレイス ID、JobID を指定して、選んだアカウントで起動
- 多重起動(Roblox が先に起動していてオンにできない時は、閉じたら自動でオンになります)

### 地震

- 地震情報の一覧、震度分布の地図、詳細、統計
- 緊急地震速報の通知(警報に加えて、Wolfx Project から予報も受け取ります。設定でオフにできます)
- 緊急地震速報の通知に、強い揺れが来るまでのカウントダウン(推定)
- 通知する最小震度を選べます。«通知しない» にすると、緊急地震速報の警報だけ通知します

同じネットワークに複数の PC がある時は、自動で 1 台だけが受信して他の PC に配ります(P2P地震情報への接続は IP アドレスごとに 2 つまでのため)。初回の起動で Windows のファイアウォールの確認が出たら、プライベート ネットワークを許可してください。

遅れたり届かないことがあります。防災の判断はこのアプリだけに頼らず、スマートフォンの緊急速報もオンにしておいてください。

### LAN 共有

同じネットワークの PC とファイルを送り合えます(ブラウザは使わず、アプリの中で操作します)。

- 共有をオンにすると、ほかの PC から «ドキュメント\NekoNoTe 共有» フォルダのファイルを見る・受け取る・送り込むことができます
- 相手の PC に表示されている 4 桁の PIN を入れてつなぎます(5 回間違えると 1 分間つなげません)
- 家の中のネットワーク(プライベート IP アドレス)の PC だけを相手にします。学校やカフェなど、知らない人もいるネットワークではオンにしないでください
- 最初はオフです

### 設定

通知の表示先と位置、表示時間、通知音、背景画像、緊急地震速報のテストなど。

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
ISCC.exe /DAppVersion=0.3.2 installer\NekoNoTe.iss
```

## 注意

Roblox Corporation とは関係のない非公式のツールです。複数のアカウントや多重起動の使い方によっては Roblox の規約で問題になることがあるので、自己責任で使ってください。

## ライセンス

[GNU General Public License v3.0](LICENSE)

このプログラムは [Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager)(作者 ic3w0lf22、GPL-3.0)のアカウント管理と多重起動の機能を元に C# / WPF で作り直し、変更を加えたものです(2026年11月)。

データの出典:

- 地震情報: [P2P地震情報](https://www.p2pquake.net/)
- 緊急地震速報(予報を含む): [Wolfx Project](https://wolfx.jp/)(非公式のサービスです)
- 地図: 「[国土数値情報(行政区域データ)](https://nlftp.mlit.go.jp/ksj/gml/datalist/KsjTmplt-N03-2024.html)」(国土交通省、CC BY 4.0)を加工して作成

使用しているライブラリ:

- [WPF-UI](https://github.com/lepoco/wpfui)(MIT)
- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/)
