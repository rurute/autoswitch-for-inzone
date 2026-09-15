# INZONE Auto Switch

Sony INZONE Buds の接続／切断に追従して、Windows 11 の既定の再生デバイスを自動で切り替える常駐ツール。

USB トランシーバーが常時接続されていると、Buds をケースに戻してもオーディオデバイスは消えないため、
Windows は出力先を切り替えてくれない。このツールは INZONE Hub が書き出す `ActionLog.log` を監視して、
接続／切断の**状態が変わったときだけ**既定の再生デバイスを切り替える。

```
Buds をケースから出す → 既定の再生デバイス = スピーカー (INZONE Buds - Game)
Buds をケースへ戻す   → 既定の再生デバイス = スピーカー (Realtek(R) Audio)
```

UI は無い。タスクトレイアイコンも通知も出ない。ログオン後にバックグラウンドで静かに動く。

## 必要なもの

- Windows 11 / Windows PowerShell 5.1
- [AudioDeviceCmdlets](https://github.com/frgnca/AudioDeviceCmdlets)
  ```powershell
  Install-Module -Name AudioDeviceCmdlets -Scope CurrentUser
  ```
- INZONE Hub がタスクトレイに常駐していること（このツールは Hub を起動しない）

## インストール

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<リポジトリ>\scripts\Install.ps1"
```

`%LOCALAPPDATA%\INZONE-AutoSwitch\` へ配置し、ログオン時に自動起動するスケジュールタスク（タスク名 `INZONE Auto Switch`）を登録して、その場で起動する。
管理者権限は不要。何度実行しても安全で、既にある `config.json` は上書きしない。

すぐに起動せず次回ログオンから動かしたい場合は `-NoStart` を付ける。

## アンインストール

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<リポジトリ>\scripts\Uninstall.ps1"
```

自動起動の登録を解除してプロセスを止める。ファイルは残る。配置先ごと消すには `-RemoveFiles` を付ける。

## 設定

`%LOCALAPPDATA%\INZONE-AutoSwitch\config.json` を編集して、ツールを再起動する。

| キー | 既定値 | 説明 |
|---|---|---|
| `budsDevice` | `スピーカー (INZONE Buds - Game)` | 接続時に切り替える先 |
| `speakerDevice` | `スピーカー (Realtek(R) Audio)` | 切断時に切り替える先 |
| `switchOnConnect` | `true` | 接続時に切り替えるか |
| `switchOnDisconnect` | `true` | 切断時に切り替えるか |
| `actionLogPath` | `%APPDATA%\Sony\INZONE Hub\ActionLog.log` | 監視するログ（`%ENV%` を展開する） |
| `pollIntervalSeconds` | `1` | 通常時のポーリング間隔（1〜60） |
| `idlePollIntervalSeconds` | `5` | Hub 未起動・ログ未存在のときの間隔（1〜300） |
| `startupSyncMaxAgeMinutes` | `10` | 起動時同期で採用するイベントの鮮度（0〜1440。`0` で同期を無効化） |
| `logLevel` | `Info` | `Debug` / `Info` / `Warn` / `Error` |
| `maxLogSizeKB` | `1024` | このサイズを超えたら 1 世代だけ退避（64〜102400） |

デバイス名は `Get-AudioDevice -List` の `Name` と完全一致させる。未知のキーは無視され、型やレンジの不正は既定値にフォールバックする（どちらもログに警告が出る）。

## 設計上の約束

- **常時強制はしない。** 切り替えるのは接続／切断の状態が変わった瞬間の 1 回だけ。
  接続後に手動で別の出力へ変えても、次の抜き差しまで干渉しない。
- **INZONE Hub を起動しない。** Hub がいなければ静かに待機し、後から起動したら自動的に監視を始める。
- **通知を出さない。** エラーも含めてすべて自分のログにだけ記録する。

このほか、実装上の判断で分かりにくい点が 2 つある。

- **ログの再生成は「作成日時」ではなく「既読部分の指紋」で検知する。** NTFS のファイルシステムトンネリングにより、
  削除後 15 秒以内に同名ファイルを作り直すと作成日時が元の値のまま復元されてしまうため、`CreationTime` を見る素朴な実装は
  新しいファイルを古いオフセットから読んでしまう。既読済みバイト列のハッシュ（指紋）が変わったかどうかで判定することで、
  この誤検知を避けている。
- **イベントは `logId` で重複排除する。** これにより、Hub がログを truncate してリーダーが先頭から読み直しても、
  過去に処理済みのイベントが再発火しない。

## ログ

`%LOCALAPPDATA%\INZONE-AutoSwitch\logs\INZONE-AutoSwitch.log`

`maxLogSizeKB` を超えると `.log.1` へ退避する（世代は 1 つだけ）。同じメッセージが続く場合は抑制して件数だけを記録する。
ログはこのツールの唯一の診断手段。ウィンドウも通知も出ないので、動作確認や不具合調査は必ずこのファイルを見る。

## うまく動かないとき

| 症状 | 確認すること |
|---|---|
| ログが出ない | タスクが登録されているか: `Get-ScheduledTask -TaskName 'INZONE Auto Switch'` |
| `AudioDeviceCmdlets を読み込めません` | `Get-Module -ListAvailable AudioDeviceCmdlets` で導入を確認する |
| `再生デバイスが見つかりません` | `Get-AudioDevice -List` の `Name` と `config.json` を突き合わせる |
| `ActionLog.log がまだありません` | INZONE Hub がタスクトレイで動いているか確認する |
| 切り替わらない | `logLevel` を `Debug` にして再起動し、`状態遷移を検知しました` の行が出ているか見る |

手動で起動して様子を見る場合:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\INZONE-AutoSwitch\INZONE-AutoSwitch.ps1"
```

多重起動は名前付き Mutex（`Local\INZONE-AutoSwitch`）で防いでいるため、タスク経由のプロセスが動いていると即座に終了する。

## 開発

```bash
# Pester 5 の導入（初回のみ）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<リポジトリ>\scripts\Install-DevDependencies.ps1'

# テスト実行
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '<リポジトリ>\scripts\Invoke-Tests.ps1'
```

現在 99 件のテストがすべて成功する状態（`Tests Passed: 99, Failed: 0`）。

実機に触れるのは `src/modules/AudioSwitcher.psm1` と `scripts/Install.ps1` / `scripts/Uninstall.ps1` だけ。
それ以外は INZONE Buds も INZONE Hub も無い環境でテストできる。

設計の詳細は `docs/superpowers/specs/2026-09-15-inzone-auto-switch-design.md` を参照。

### 将来の拡張

`ActionLogReader` + `SonyEventProvider` を HID 直接監視へ差し替えれば、`DeviceState` 以降は無改修で動く。
差し替え点の契約は「`Connected` / `Disconnected` を表すイベントの列を返すこと」だけ。
