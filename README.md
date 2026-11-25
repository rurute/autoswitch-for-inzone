# AutoSwitch for INZONE

Sony INZONE Buds の接続／切断に追従して、Windows 11 の既定の再生デバイス（と、設定すれば録音デバイス）を自動で切り替えるトレイ常駐アプリ。

USB トランシーバーが常時接続されていると、Buds をケースに戻してもオーディオデバイスは消えないため、
Windows は出力先を切り替えてくれない。このアプリは Buds の USB ドングルに直接問い合わせて接続／切断を判定し、
**状態が変わったときだけ**既定のデバイスを切り替える。INZONE Hub を常駐させなくてよい（起動していても動く）。

```
Buds をケースから出す           → 既定の再生デバイス = スピーカー (INZONE Buds - Game)
Buds をケースへ戻してふたを閉じる → 既定の再生デバイス = スピーカー (Realtek(R) Audio)
```

片耳でも着けていれば接続中とみなす。ふたを閉じてから切り替わるまでは 3 秒程度かかる（遅くとも 5 秒）。

タスクトレイにアイコンが出る。アイコンの色で、いまの状態が分かる。

| アイコン | 状態 |
|---|---|
| 紫のタイル | 接続中 |
| 灰色のタイル | 切断中 |
| 枠だけ | まだ分からない（起動直後など） |

通知は出さない。エラーも含めて、記録はログファイルにだけ残る。

## 必要なもの

- Windows 11
- .NET 10。配置スクリプトがその場でビルドするため、いまは **.NET 10 SDK** が必要
- INZONE Buds の USB ドングルを挿していること

動作を確認しているのは、作者の環境（日本語版 Windows 11、INZONE Buds WF-G700N。INZONE Hub は起動した状態と終了した状態の両方）だけである。

## インストール

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<リポジトリ>\scripts\Install-TrayApp.ps1"
Start-Process "$env:LOCALAPPDATA\AutoSwitchForInzone\bin\AutoSwitch.App.exe"
```

`dotnet publish` した結果を `%LOCALAPPDATA%\AutoSwitchForInzone\bin\` へ配置する。管理者権限は不要。
何度実行しても安全で、`config.json` とログには触らない。アプリが動いている間はエラーで止まるので、先にトレイの「終了」で終わらせる。

起動したら、トレイアイコンを右クリックして次の 2 つを行う。

1. **「設定(&S)...」で切り替え先のデバイスを選んで保存する。** 初期値は作者の環境のデバイス名なので、多くの環境ではそのままだと切り替わらない
2. **「Windows ログオン時の自動起動(&A)」を選び、チェックを付ける。** 配置や起動では自動登録されない

## アンインストール

1. トレイメニューで自動起動のチェックを外す
2. トレイメニューの「終了(&X)」を選ぶ
3. `%LOCALAPPDATA%\AutoSwitchForInzone\` を消す（設定とログも消えてよければ）

自動起動の登録先は `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値 `AutoSwitchForInzone` である。
チェックを外さずにフォルダを消した場合は、この値を手で消す。

## トレイメニュー

| 項目 | 内容 |
|---|---|
| 設定(&S)... | 設定画面を開く。トレイアイコンのダブルクリックでも開く |
| 設定ファイルを開く(&C) | `config.json` を開く |
| ログフォルダを開く(&L) | ログのフォルダを開く |
| Windows ログオン時の自動起動(&A) | 自動起動の ON/OFF |
| 終了(&X) | アプリを終了する |

## 設定

### 設定画面

接続時・切断時それぞれの再生デバイスと録音デバイス、「接続したら切り替える」「切断したら切り替える」の 6 項目を変えられる。
選択肢は、いま Windows で有効なデバイスの一覧。**保存すると再起動せずに反映する**が、その場ではデバイスを切り替えない。次の接続・切断から新しい設定を使う。

録音デバイスは初期状態では「（未設定）」で、録音は切り替えない。

### config.json

`%LOCALAPPDATA%\AutoSwitchForInzone\config.json`。初回起動時に既定値で作られる。
手で編集した内容はアプリの起動時にだけ読むので、編集したら再起動する。

| キー | 既定値 | 説明 |
|---|---|---|
| `budsDevice` | `スピーカー (INZONE Buds - Game)` | 接続時に切り替える再生デバイス |
| `speakerDevice` | `スピーカー (Realtek(R) Audio)` | 切断時に切り替える再生デバイス |
| `budsInputDevice` | （なし） | 接続時に切り替える録音デバイス。空なら切り替えない |
| `speakerInputDevice` | （なし） | 切断時に切り替える録音デバイス。空なら切り替えない |
| `switchOnConnect` | `true` | 接続時に切り替えるか（再生・録音とも） |
| `switchOnDisconnect` | `true` | 切断時に切り替えるか（再生・録音とも） |
| `pollIntervalSeconds` | `2` | ドングルへの問い合わせの間隔（1〜60）。ドングルから知らせが届いたときは、待たずに問い合わせる |
| `idlePollIntervalSeconds` | `5` | ドングルが見つからない・読めないときに探し直す間隔（1〜300） |
| `startupSyncMaxAgeMinutes` | `10` | `0` なら起動時（とドングルを読めるようになったとき）に切り替えない。1 以上なら切り替える。値の大きさに意味はない（0〜1440） |
| `logLevel` | `Info` | `Debug` / `Info` / `Warn` / `Error` |
| `maxLogSizeKB` | `1024` | このサイズを超えたら 1 世代だけ退避（64〜102400） |

デバイス名は Windows のサウンド設定に表示される名前と一致させる（大文字小文字は区別しない）。
未知のキーは無視され、型やレンジの不正は既定値にフォールバックする（どちらもログに警告が出る）。
以前の版の `actionLogPath` は使わなくなった。書かれていても警告を出さずに無視する。

## 設計上の約束

- **常時強制はしない。** 切り替えるのは接続／切断の状態が変わった瞬間の 1 回だけ。
  接続後に手動で別の出力へ変えても、次の抜き差しまで干渉しない。
- **INZONE Hub を必要としない（起動もしない）。** Hub がいなくても動き、Hub が動いていても Hub の表示を乱さない。
- **通知を出さない。** 状態はトレイアイコンで示し、エラーも含めてすべて自分のログにだけ記録する。

- **ドングルを読めないときは切り替えない。** ドングルが見つからない・開けない・読み書きに失敗したときは、状態を変えずに
  `idlePollIntervalSeconds` おきに探し直す。読めるようになったら、前と同じ状態でも今の状態に合わせて 1 回切り替える。

## 既知の制限

- **スリープから復帰した直後と、片耳をケースに戻した直後に、Buds を着けたままでも短い時間スピーカーとマイクへ切り替わり、すぐ Buds に戻ることがある。**
  Buds のドングルが一時的に問い合わせに返答しなくなる（または左右とも報告なしを返す）ためと考えられる。このアプリは、2 回続けて返答が無いときと、左右とも報告なしのときを切断とみなす。
  片耳の場合は返答しなかったことを確かめたが、復帰の直後についてはどちらだったかを確かめていない。
  作者の環境では、復帰の直後に約 1.5 秒スピーカーへ切り替わった。片耳では、ドングルが約 2 秒返答しないことがあった（そのときアプリは切り替えなかった）。
  どのくらいの頻度で起きるか、最長でどのくらい続くかは確かめていない。作者の判断で、この動きは受け入れている（[実機確認の記録](docs/manual-test-dongle-detection.md)）。

## ログ

`%LOCALAPPDATA%\AutoSwitchForInzone\logs\AutoSwitchForInzone.log`

`maxLogSizeKB` を超えると `.log.1` へ退避する（世代は 1 つだけ）。

## うまく動かないとき

| 症状 | 確認すること |
|---|---|
| トレイアイコンが出ない | `@(Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue).Count` で動いているか見る。すでに動いているときに起動しても、何もせず終わる |
| `再生デバイスが見つかりません` / `録音デバイスが見つかりません` | 設定画面で切り替え先を選び直す |
| `INZONE Buds のドングルを読めません` | ドングルが挿さっているか、ほかのソフトがドングルを独占していないか確認する |
| 切り替わらない | ログに `状態遷移を検知しました` の行が出ているか見る |
| ログオン時に起動しない | トレイメニューの自動起動にチェックが付いているか見る |

見るべきログの行や、自動起動・exe の移動の詳しい扱いは [運用手順](docs/operations-minimal-csharp.md) にまとめてある。

## 開発

Windows の `dotnet` でビルド・テストする（SDK のバージョンは `global.json` で固定）。

```powershell
dotnet build AutoSwitchForInzone.sln -c Debug
dotnet test --project tests\AutoSwitch.Core.Tests\AutoSwitch.Core.Tests.csproj
dotnet test --project tests\AutoSwitch.App.Tests\AutoSwitch.App.Tests.csproj
```

| プロジェクト | 役割 |
|---|---|
| `src/AutoSwitch.Core` | ドングルとのやり取りの形（問い合わせ・返答の照合）・判定・状態遷移・設定。実機なしでテストできる |
| `src/AutoSwitch.Hid` | Win32 の HID API でドングルを探して開き、読み書きする |
| `src/AutoSwitch.Audio` | Core Audio API で既定デバイスを読み書きする |
| `src/AutoSwitch.App` | トレイ常駐・設定画面・自動起動 |
| `tools/AudioSmoke` | 実機確認用のツール（デバイス一覧の表示など） |
| `tools/DongleSmoke` | 実機確認用のツール（ドングルに問い合わせて結果を表示する。切り替えはしない） |

設計の詳細は `docs/superpowers/specs/` を参照。

## 旧版（PowerShell 版）について

`src/INZONE-AutoSwitch.ps1`、`src/modules/`、`scripts/Install.ps1` などは、最初に作った PowerShell 版（AudioDeviceCmdlets とスケジュールタスクで動く）である。
現在は C# 版を使っており、旧版はいつでも戻せるように残してある。旧版からの移行と、旧版へ戻す手順は [運用手順](docs/operations-minimal-csharp.md) を参照。
**旧版と C# 版を同時に動かさないこと**（互いに既定デバイスを奪い合う）。
