# INZONE Auto Switch v1 — 設計仕様

- 日付: 2026-09-15
- ステータス: 承認済み（実装計画へ）
- 対象環境: Windows 11 Pro (10.0.26200) / Windows PowerShell 5.1

## 1. 目的

Sony INZONE Buds の装着・収納に追従して、Windows の既定の再生デバイスを自動で切り替える常駐ツール。

- Buds 接続時 → 既定の再生デバイスを `スピーカー (INZONE Buds - Game)` にする
- Buds 切断時 → 既定の再生デバイスを `スピーカー (Realtek(R) Audio)` にする
- Windows ログオン後にバックグラウンドで自動起動し、普段はユーザーが存在を意識しない

USB トランシーバーは常時 PC に接続されているため、Buds をケースに戻してもオーディオエンドポイントも PnP デバイスも消えない。したがってデバイスの出現・消失では接続状態を判定できない。

## 2. 実機調査で確定した事実

v1 の設計はすべて以下の実測に基づく（2026-09-15 実施）。

### 2.1 実行環境

| 項目 | 実測値 |
|---|---|
| PowerShell | Windows PowerShell 5.1.26100.9444（`pwsh` は未導入） |
| Pester | 3.4.0（OS 同梱）。Pester 5 系（`-MinimumVersion 5.5.0 -MaximumVersion 5.99.99`）を CurrentUser スコープへ追加導入する。同梱の 3.4.0 は変更しない |
| AudioDeviceCmdlets | 3.1.0.2（`%USERPROFILE%\OneDrive\ドキュメント\WindowsPowerShell\Modules` 配下） |
| PSGallery | 到達可能（`Find-Module` 成功） |
| INZONE Hub | `C:\Program Files\Sony\INZONE Hub\INZONEHub.exe` / プロセス名 `INZONEHub` / v1.0.19.0 |

### 2.2 オーディオデバイス

`Get-AudioDevice -List` で以下の名称が実在することを確認済み。両名称とも設定ファイルから変更可能にする。

- `スピーカー (INZONE Buds - Game)`
- `スピーカー (Realtek(R) Audio)`

`Get-AudioDevice -Playback` で現在の既定再生デバイスを取得できる。

### 2.3 ActionLog.log

パス: `%APPDATA%\Sony\INZONE Hub\ActionLog.log`

| 観測項目 | 内容 |
|---|---|
| 形式 | 1 行 1 JSON、UTF-8 BOM 付き、1 行あたり約 1KB |
| 行の識別子 | 各行に一意な `logId`（GUID）を持つ |
| 時刻 | `timeStamp`（epoch ミリ秒）と `localTime`（表示用文字列）の両方を持つ |
| ローテーション | **同一ファイルを in-place で truncate している。** CreationTime は 2026-07-27 のまま、内容は直近 30 秒分の 40 行のみだった |
| ロック | Hub 稼働中でも共有読み取りで開ける（`FileShare.ReadWrite, Delete` でも `FileShare.Read` でも成功） |

対象イベント:

| actionTypeId | actionTypeName | 意味 |
|---|---|---|
| 23011 | ConnectDevice | Buds 接続 |
| 23012 | DisconnectDevice | Buds 切断 |

**重要な観測**: 実ログ中で `ConnectDevice` が `DisconnectDevice` を挟まずに連続発生していた（15:36:31 と 15:36:36）。したがって「イベントごとに切り替える」実装は誤り。**状態が遷移したときのみ切り替える**必要がある。

## 3. スコープ

### v1 に含むもの

- ActionLog.log の追従監視と Connect/Disconnect 検出
- 既定再生デバイスの切り替え
- 起動時の状態同期（安全に判定できる場合のみ）
- ログオン時の自動起動登録／解除
- 自身のログ出力とローテーション

### v1 に含まないもの

- UI 一切（タスクトレイ、GUI、Windows 通知）
- HID の直接解析（将来の差し替え対象として構造だけ用意する）
- INZONE Hub 自体の起動・制御
- 既定の録音デバイスの制御
- 複数ヘッドセット・複数プロファイルの対応

## 4. アーキテクチャ

```
INZONE Hub
    │  ActionLog.log（1 行 1 JSON）
    ▼
ActionLogReader ─┐
                 ├─→ Sony Event Provider ──→ Connected / Disconnected
SonyEventProvider┘                                    │
                                                      ▼
                                              DeviceState（状態遷移判定）
                                                      │
                                                      ▼
                                              AudioSwitcher
                                                      ├─ INZONE Buds - Game
                                                      └─ Realtek(R) Audio
```

将来 HID 直接監視へ移行する場合、`ActionLogReader` + `SonyEventProvider` を `HidEventProvider` に差し替えるだけでよい。`DeviceState` 以降は無改修。差し替え点の契約は「`Connected` / `Disconnected` を表すイベントの列を返す」こと。

### 4.1 ファイル構成

```
inzone-auto-switch/
├── README.md
├── .gitignore
├── config.json
├── src/
│   ├── INZONE-AutoSwitch.ps1        エントリポイント
│   └── modules/
│       ├── Config.psm1
│       ├── Logging.psm1
│       ├── ActionLogReader.psm1
│       ├── SonyEventProvider.psm1
│       ├── DeviceState.psm1
│       ├── AudioSwitcher.psm1
│       └── Orchestrator.psm1
├── tests/
│   ├── fixtures/
│   ├── stubs/
│   ├── AppRoot.Tests.ps1
│   ├── Config.Tests.ps1
│   ├── Logging.Tests.ps1
│   ├── ActionLogReader.Tests.ps1
│   ├── SonyEventProvider.Tests.ps1
│   ├── DeviceState.Tests.ps1
│   ├── AudioSwitcher.Tests.ps1
│   └── Orchestrator.Tests.ps1
├── scripts/
│   ├── Common.ps1
│   ├── Install.ps1
│   ├── Uninstall.ps1
│   ├── Install-DevDependencies.ps1
│   ├── Invoke-Tests.ps1
│   └── launch-hidden.vbs
├── logs/.gitkeep
└── docs/superpowers/{specs,plans}/
```

### 4.2 モジュールの責務と契約

いずれのモジュールも「何をするか / どう使うか / 何に依存するか」が単独で説明できる粒度に保つ。

#### Config.psm1

```
Read-AutoSwitchConfig -Path <string> -> hashtable
```

- JSON を読み、既定値とマージし、`%ENV%` 形式の環境変数を展開して返す
- 未知のキーは無視、欠損キーは既定値で補完
- 数値項目が範囲外・型不正なら既定値へフォールバックし、警告メッセージのリストを併せて返す
- ファイルが存在しない／JSON が壊れている場合は全既定値を返し、警告を返す（例外を投げない）
- 依存: なし（純粋関数。ファイル読み取りのみ）

#### Logging.psm1

```
Initialize-AutoSwitchLog -Path <string> -Level <string> -MaxSizeKB <int>
Write-AutoSwitchLog -Level <Debug|Info|Warn|Error> -Message <string>
```

- 形式: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message`
- 設定レベル未満は書かない
- 書き込み前にサイズを確認し、`MaxSizeKB` 超過なら既存を `<name>.1` へ退避（既存の `.1` は上書き）。世代は 1 つのみ
- 同一メッセージの連続出力を抑制する。直前と同一文言なら書かずに抑制カウンタを進め、異なる文言が来た時点で `(同一メッセージを N 回抑制)` を 1 行出してから新しい文言を書く。時刻に依存しないのでテストで待たされない
- ログ書き込み自体の失敗はプロセスを落とさない（握りつぶす）

#### ActionLogReader.psm1

```
New-ActionLogReaderState -Path <string> -> hashtable
    @{ Path; Offset; LastLength; HeadFingerprint }
Read-ActionLogLines -State <hashtable> -> @{ State; Lines; Status }
    Status: 'Ok' | 'FileMissing' | 'Truncated' | 'Recreated' | 'Error'
```

- ファイル未存在: `Offset=0` にリセットし `FileMissing` を返す
- `Length < Offset` **または** `Length < 前回の LastLength`: truncate とみなし `Offset=0` から全読み、`Truncated` を返す。後者は、truncate 直後に Hub が大量追記してポーリング前に `Length >= Offset` へ戻ってしまう競合を拾うため
- 既読部分の**先頭バイト指紋**が前回と異なる: ファイルが差し替わったとみなし `Offset=0` から全読み、`Recreated` を返す
  - 指紋は先頭 `min(256, Offset)` バイトの Base64。既読範囲に限るので、通常の追記では変化しない
  - **`CreationTimeUtc` は使えない。** NTFS の file system tunneling により、削除後 15 秒以内に同名で
    作り直すと作成日時が復元される（実機で計測して確認済み）。作成日時だけに頼ると、古いオフセットの
    まま別内容のファイルを読み進めて無言でデータを破損する
- `Length > Offset`: `Offset` から末尾までを読む
- **バイト境界で処理する**。読み出したバッファ中の最後の `0x0A` までを 1 行以上として確定し、`Offset` は `最後のLF位置+1` までしか進めない。行の途中で読んだ分は次回に持ち越す
- 各行の先頭 BOM（U+FEFF）と `\r` は除去する
- 1 回の読み出しは最大 4MB に制限する。それを超える差分は次の tick へ持ち越す（巨大ファイル出現時のメモリ確保を防ぐ）
- `FileShare.ReadWrite, Delete` で開く。IO 例外は捕捉して `Error` を返す（呼び出し側がバックオフする）
- 依存: ファイルシステムのみ。テストは一時ファイルで完結する

#### SonyEventProvider.psm1

```
New-SeenLogIdSet -Capacity <int> -> hashtable
    @{ Capacity; Lookup = @{}; Order = [System.Collections.Generic.Queue[string]] }
Add-SeenLogId -Set <hashtable> -LogId <string> -> bool   # 新規なら $true
Get-BudsEventsFromLines -Lines <string[]> -SeenLogIds <hashtable> -> object[]
    各要素: @{ LogId; TimeStampMs; State = 'Connected' | 'Disconnected' }
```

- まず正規表現 `"actionTypeId"\s*:\s*2301[12]` で候補行だけを絞り、その行のみ `ConvertFrom-Json` する（1 行 1KB × 多数行の無駄なパースを避ける）
- 23011 → `Connected`、23012 → `Disconnected`
- JSON パース失敗行は黙って読み飛ばす（Debug ログのみ）
- `logId` による重複排除。既出の `logId` は返さない
- `SeenLogIds` は挿入順を保つ有限リング（既定容量 200 件）。上限超過時は最古から破棄する。truncate による全文再読でも二重発火しないための機構。ハッシュテーブルは PowerShell では参照型なので、呼び出し側の実体がそのまま更新される
- 依存: なし（文字列配列 → イベント配列の純粋変換）

#### DeviceState.psm1

```
Resolve-StateTransition -CurrentState <string> -EventState <string> -> @{ Changed; NewState }
    CurrentState: 'Unknown' | 'Connected' | 'Disconnected'
```

- `CurrentState -eq $EventState` なら `Changed = $false`（**ConnectDevice 連発対策。かつユーザーの手動変更を尊重する根拠**）
- `Unknown` からの初回イベントは `Changed = $true`
- 依存: なし（純粋関数）

#### AudioSwitcher.psm1

```
Test-AudioSwitcherAvailable -> bool
Get-DefaultPlaybackDeviceName -> string | $null
Set-DefaultPlaybackDeviceByName -Name <string> -> @{ Result; Message }
    Result: 'Switched' | 'AlreadyDefault' | 'DeviceNotFound' | 'ModuleUnavailable' | 'Error'
```

- 実機（AudioDeviceCmdlets）に触れる唯一の場所
- `Set-DefaultPlaybackDeviceByName` は先に現在の既定を取得し、一致していれば `AlreadyDefault` を返して何もしない
- 名前の一致は完全一致。`Type -eq 'Playback'` の中から探す
- `Import-Module AudioDeviceCmdlets` を遅延実行し、失敗時は `ModuleUnavailable`。切り替え要求のたびに再試行する（後から導入された場合に自動復帰するため）
- テストでは `Get-AudioDevice` / `Set-AudioDevice` を Pester Mock で差し替える

#### Orchestrator.psm1

```
Invoke-StartupSync -Config <hashtable> -Context <hashtable> -> hashtable(Context)
Invoke-AutoSwitchTick -Config <hashtable> -Context <hashtable> -> hashtable(Context)
```

`Context` が実行時の全状態を持つ:

```
@{
    ReaderState      # ActionLogReader の状態
    SeenLogIds       # 重複排除リング
    BudsState        # 'Unknown' | 'Connected' | 'Disconnected'
    ConsecutiveErrors
    NextDelaySeconds
    LastTickUtc
}
```

- `Invoke-AutoSwitchTick` は「1 回分」の処理のみを行い、スリープもループもしない。ループはエントリポイント側の責務
- これにより Orchestrator は AudioSwitcher を Mock するだけで完全にテストできる

#### INZONE-AutoSwitch.ps1（エントリポイント）

**アプリルートの解決規則**: `config.json` を含むディレクトリをアプリルートとし、`$PSScriptRoot`、次に `$PSScriptRoot\..` の順に探す。見つからなければ `$PSScriptRoot` をルートとして全既定値で動く。ログは常に `<アプリルート>\logs\INZONE-AutoSwitch.log`。

この 1 つの規則で、リポジトリから直接実行する開発時（スクリプトは `src/`、`config.json` と `logs/` はリポジトリルート）と、インストール後（すべて実行時ルート直下）の両方が同じコードで成立する。

1. 名前付き Mutex `Local\INZONE-AutoSwitch` を取得。失敗したら Info ログ 1 行を残して終了コード 0 で終了
2. アプリルートを解決して `config.json` を読み、ログを初期化
3. `Invoke-StartupSync`
4. `while ($true) { $ctx = Invoke-AutoSwitchTick ...; Start-Sleep -Seconds $ctx.NextDelaySeconds }`
5. 終了時に Mutex を解放（`finally`）

## 5. 主要な振る舞い

### 5.1 1 tick のデータフロー

```
Read-ActionLogLines
    ├─ FileMissing          → BudsState は保持、Idle 間隔で次 tick
    ├─ Error                → ConsecutiveErrors++ → 指数バックオフ
    └─ Ok/Truncated/Recreated
            ▼ 確定した行
    Get-BudsEventsFromLines（logId 重複排除込み）
            ▼ 新規イベントを時系列順に処理
    Resolve-StateTransition
            ├─ Changed = $false → 何もしない
            └─ Changed = $true
                    ├─ Connected    かつ switchOnConnect    → budsDevice へ
                    ├─ Disconnected かつ switchOnDisconnect → speakerDevice へ
                    └─ 対応する switch フラグが false        → 状態のみ更新
```

イベントが 1 tick に複数含まれる場合は、`SonyEventProvider` が返した順（= ファイルへの追記順 = 時系列順）にそのまま適用する。PowerShell 5.1 の `Sort-Object` は安定ソートではなく、同一ミリ秒のイベント順を壊しうるため並べ替えない。結果として最後の遷移だけがデバイス切り替えを起こす。

### 5.2 手動変更の尊重

切り替えは**状態遷移が起きた瞬間の 1 回だけ**。常時強制はしない。

- Buds 接続 → 自動で Buds へ切替
- ユーザーが手動で Realtek へ変更 → 内部状態は `Connected` のまま。以降のどの tick でも Buds へは戻さない
- Hub が `ConnectDevice` を再送 → 既に `Connected` なので `Changed = $false` → 何もしない
- Buds をケースへ戻す → `Disconnected` へ遷移 → Realtek へ切替（既に Realtek なら `AlreadyDefault` で無動作）

### 5.3 起動時同期

0. `startupSyncMaxAgeMinutes` が `0` なら起動時同期そのものを行わない。ただしその場合も ActionLog を末尾まで読み飛ばして `Reader.Offset` を進め、既存行の `logId` を `SeenLogIds` に登録する（起動直後に過去ログが一斉発火しないようにするため）
1. `Get-Process INZONEHub` が取得できなければ何もしない。`BudsState` は `Unknown` のまま待機
2. ActionLog を先頭から全読みし、`23011`/`23012` の**最後の 1 件**を取得
3. 以下を**両方**満たす場合のみ現在状態として採用する
   - `TimeStampMs >= INZONEHub プロセスの StartTime`
   - `now - TimeStampMs <= startupSyncMaxAgeMinutes`（既定 10 分）
4. 採用した場合のみ `BudsState` を設定し、対応するデバイスへ 1 回切り替える（`AlreadyDefault` なら無動作）
5. 採用可否にかかわらず、**読んだ全行の `logId` は `SeenLogIds` に登録する**。これがないと次 tick で同じイベントが「新規」として再発火する
6. `Reader.Offset` は読み終えた位置へ進めておく

条件を外れた場合は「安全に判定できない」として一切切り替えない。何日も前のイベントを現在状態とみなすことはない。

### 5.4 INZONE Hub の起動順序

- Auto Switch は **INZONE Hub を起動しない**
- Hub 未起動・ログ未存在なら Idle 間隔で静かに待機する
- 後から Hub が起動して ActionLog.log が生成／更新されたら、次の tick で自動的に通常監視へ移行する（`FileMissing` → `Ok` の遷移を特別扱いする必要はなく、Reader の状態リセットで自然に成立する）

## 6. 堅牢性

| 状況 | 挙動 |
|---|---|
| 多重起動 | 名前付き Mutex `Local\INZONE-AutoSwitch`。取得失敗で Info ログ 1 行を残し即終了 |
| Hub が後から起動 | Idle ポーリングで待機し、ログ出現後に自動で通常監視へ |
| ActionLog.log が未存在 | `FileMissing`。状態は保持、Idle 間隔 |
| 削除・再作成 | 既読部分の先頭バイト指紋の変化で検知、`Offset=0` から再読。`logId` 重複排除で二重発火しない |
| truncate | `Length < Offset` または `Length < 前回の LastLength` で検知、`Offset=0` から再読。同上 |
| 一時的なファイルアクセス失敗 | 例外を捕捉、`ConsecutiveErrors` を加算して指数バックオフ。成功でリセット |
| AudioDeviceCmdlets が無い | 起動時に Warn 1 回。切替要求のたびに再 Import を試行し、後から導入されれば自動復帰 |
| デバイス名が存在しない | `DeviceNotFound` を Warn。ループは継続。内部状態は遷移済みとして記録する（再試行で暴れない） |
| 既に目的のデバイス | `AlreadyDefault`。`Set-AudioDevice` を呼ばない |
| 監視の一時的失敗 | 上記バックオフののち自動復帰 |
| 自身のログ肥大化 | `maxLogSizeKB` 超過で 1 世代ローテーション + 同一メッセージの連続抑制（件数方式） |
| スリープ / 復帰 | ポーリング方式のため自然に再開。加えて前回 tick からの経過が 5 分を超えていたらギャップとみなし、Info ログを 1 行残して `ConsecutiveErrors` と遅延を通常値へリセットする（復帰直後にバックオフを引きずらない） |
| 高頻度リトライの抑止 | 通常 1 秒 / Idle 5 秒 / エラー時は 1→2→4→…→最大 60 秒 |

### 6.1 バックオフの規則

```
成功            : NextDelay = pollIntervalSeconds
FileMissing     : NextDelay = idlePollIntervalSeconds
Error (n 回連続): NextDelay = min(pollIntervalSeconds * 2^(n-1), 60)
```

`Error` から 1 回でも成功したら `ConsecutiveErrors = 0` に戻す。

## 7. 設定

`config.json`（アプリルート直下。開発時はリポジトリルート、インストール後は実行時ルート）。

```json
{
  "budsDevice": "スピーカー (INZONE Buds - Game)",
  "speakerDevice": "スピーカー (Realtek(R) Audio)",
  "switchOnConnect": true,
  "switchOnDisconnect": true,
  "actionLogPath": "%APPDATA%\\Sony\\INZONE Hub\\ActionLog.log",
  "pollIntervalSeconds": 1,
  "idlePollIntervalSeconds": 5,
  "startupSyncMaxAgeMinutes": 10,
  "logLevel": "Info",
  "maxLogSizeKB": 1024
}
```

| キー | 既定値 | 許容範囲 |
|---|---|---|
| `budsDevice` | `スピーカー (INZONE Buds - Game)` | 空でない文字列 |
| `speakerDevice` | `スピーカー (Realtek(R) Audio)` | 空でない文字列 |
| `switchOnConnect` | `true` | bool |
| `switchOnDisconnect` | `true` | bool |
| `actionLogPath` | `%APPDATA%\Sony\INZONE Hub\ActionLog.log` | 空でない文字列（`%ENV%` 展開あり） |
| `pollIntervalSeconds` | `1` | 1–60 |
| `idlePollIntervalSeconds` | `5` | 1–300 |
| `startupSyncMaxAgeMinutes` | `10` | 0–1440（0 は起動時同期を無効化） |
| `logLevel` | `Info` | `Debug` / `Info` / `Warn` / `Error` |
| `maxLogSizeKB` | `1024` | 64–102400 |

ログの出力先はアプリルート配下の `logs\INZONE-AutoSwitch.log` に固定する（設定項目を不要に増やさないため）。

## 8. インストールと自動起動

### 8.1 配置

| | パス |
|---|---|
| 開発リポジトリ | `/home/<ユーザー名>/workspaces/inzone-auto-switch`（WSL, git） |
| 実行時ルート | `%LOCALAPPDATA%\INZONE-AutoSwitch\` |
| 実行時ログ | `%LOCALAPPDATA%\INZONE-AutoSwitch\logs\INZONE-AutoSwitch.log` |

実行時ルートの構成:

```
%LOCALAPPDATA%\INZONE-AutoSwitch\
├── INZONE-AutoSwitch.ps1
├── modules\*.psm1
├── launch-hidden.vbs
├── config.json
└── logs\INZONE-AutoSwitch.log
```

リポジトリは WSL 側に置き、`Install.ps1` が Windows 側の実行時ルートへ配置する。Windows からは UNC パス `\\wsl.localhost\Ubuntu\home\<ユーザー名>\workspaces\inzone-auto-switch\` で到達でき、UNC からの `.ps1` 実行・PSGallery 到達はいずれも実機確認済み。

### 8.2 Install.ps1

1. **`src/` の中身**（`INZONE-AutoSwitch.ps1` と `modules/`）と `scripts/launch-hidden.vbs` を実行時ルート直下へコピー（毎回上書き）。`src` というディレクトリ階層は実行時ルートには作らない
2. `config.json` は**実行時ルートに存在しない場合のみ**コピーする。ユーザーの編集を上書きしない
3. `logs/` ディレクトリを作成
4. 既存の同名タスクがあれば解除してから登録し直す（再実行を安全にする）
5. スケジュールタスク `INZONE Auto Switch` を登録する

タスク定義:

| 項目 | 値 |
|---|---|
| トリガー | ログオン時（現在のユーザー）、遅延 15 秒 |
| 操作 | `wscript.exe //B //Nologo "%LOCALAPPDATA%\INZONE-AutoSwitch\launch-hidden.vbs"` |
| プリンシパル | 現在のユーザー / `Interactive` / 最上位特権なし |
| 設定 | `Hidden` / `ExecutionTimeLimit = 0` / `StartWhenAvailable` / `DisallowStartIfOnBatteries = $false` / `StopIfGoingOnBatteries = $false` / `MultipleInstances = IgnoreNew` |
| 失敗時 | `RestartInterval = 1 分` / `RestartCount = 3` |

管理者権限は不要（現在のユーザーのタスクのみを扱う）。

`launch-hidden.vbs` は `WScript.Shell.Run` をウィンドウスタイル `0`、待機なしで呼ぶだけの数行。これにより `powershell.exe` のコンソールが一瞬もちらつかない。

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "<root>\INZONE-AutoSwitch.ps1"
```

### 8.3 Uninstall.ps1

1. スケジュールタスクを解除（存在しなくてもエラーにしない）
2. 実行中の Auto Switch プロセスがあれば停止
3. 既定ではファイルを残す。`-RemoveFiles` 指定時のみ実行時ルートを削除する（config とログを誤って失わないため）

## 9. テスト戦略

Pester 5 系を CurrentUser スコープへ導入する（`scripts/Install-DevDependencies.ps1`）。同梱の Pester 3.4.0 には触れない。

実行方法（WSL から）:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '\\wsl.localhost\Ubuntu\home\<ユーザー名>\workspaces\inzone-auto-switch\scripts\Invoke-Tests.ps1'
```

実機・実ハードウェアに依存する箇所は `AudioSwitcher.psm1` の内部と `Install.ps1` / `Uninstall.ps1` だけに封じ込める。それ以外は INZONE Buds も INZONE Hub も無い状態で全てテストできる。

### 9.1 テストケース

**Config.Tests.ps1**
- 完全な JSON を読める
- 欠損キーが既定値で補完される
- 未知のキーが無視される
- `%APPDATA%` が展開される
- 範囲外の数値・型不正が既定値へフォールバックし警告が返る
- ファイル未存在で全既定値 + 警告
- JSON 破損で全既定値 + 警告（例外を投げない）

**Logging.Tests.ps1**
- 行フォーマット
- `logLevel` による出力フィルタ
- `maxLogSizeKB` 超過で `.1` へローテーション、世代は 1 つのみ
- 同一メッセージの連続抑制: 2 回目以降は書かれない
- 抑制中に別の文言が来ると `(同一メッセージを N 回抑制)` が先に出力される
- 書き込み不能でも例外を投げない

**ActionLogReader.Tests.ps1**
- 初回全読み
- 2 回目は追記分のみを返す
- 末尾が改行で終わらない場合、途中行を返さず `Offset` も進めない。次回に追記が完結したら 1 行として返す
- truncate（`Length < Offset`）の検知と全読み
- 縮小したが `Length >= Offset` のままの truncate（`Length < LastLength`）も検知する
- 削除 → 再生成（先頭バイト指紋の変化）の検知と全読み。**削除の 50ms 後に作り直しても検知できること**
  （NTFS の file system tunneling で作成日時が復元される窓の中）
- truncate 後に元より長い別内容へ差し替わっても `Recreated` として先頭から読み直す
- 通常の追記では `Recreated` と誤判定しない
- ファイル未存在で `FileMissing`、状態がリセットされる
- UTF-8 BOM が除去される
- CRLF の `\r` が除去される
- マルチバイト文字を含む行でバイトオフセットがずれない
- 読み取り例外で `Error` を返し、例外を外に漏らさない

**SonyEventProvider.Tests.ps1**
- 23011 → `Connected`、23012 → `Disconnected`
- 23004 / 23014 / `DisplayedScreen` など他 actionType を無視する
- 不正 JSON 行を読み飛ばし、同じ配列内の正常行は処理する
- 同一 `logId` を二度返さない
- `SeenLogIds` が 200 件を超えると最古から破棄される
- 実ログ由来の fixture（`ConnectDevice` が連続する 40 行）で、期待どおり 3 件の Connected と 1 件の Disconnected を時系列順に返す

**DeviceState.Tests.ps1**
- `Unknown` → `Connected` は遷移
- `Unknown` → `Disconnected` は遷移
- `Connected` → `Connected` は非遷移
- `Disconnected` → `Disconnected` は非遷移
- `Connected` → `Disconnected`、`Disconnected` → `Connected` は遷移

**AudioSwitcher.Tests.ps1**（`Get-AudioDevice` / `Set-AudioDevice` を Mock）
- 目的デバイスが既定でないとき `Switched` で `Set-AudioDevice` が 1 回呼ばれる
- 既に既定のとき `AlreadyDefault` で `Set-AudioDevice` が呼ばれない
- 名前が一覧に無いとき `DeviceNotFound`、`Set-AudioDevice` は呼ばれない
- `Playback` 以外の同名デバイスを選ばない
- モジュール未導入のとき `ModuleUnavailable` を返し例外を投げない
- `Set-AudioDevice` が例外を投げたとき `Error` を返し例外を外に漏らさない

**Orchestrator.Tests.ps1**（AudioSwitcher を Mock、ActionLog は一時ファイル）
- 起動時同期: Hub 未起動なら切り替えない、`BudsState` は `Unknown`
- 起動時同期: 最終イベントが Hub 起動前 → 切り替えない
- 起動時同期: 最終イベントが 10 分より古い → 切り替えない
- 起動時同期: 条件を満たす → 1 回切り替える
- 起動時同期後の最初の tick で、同期時に読んだイベントが再発火しない
- Connected イベントで `budsDevice` へ切り替わる
- Disconnected イベントで `speakerDevice` へ切り替わる
- `switchOnConnect = false` で切り替えないが内部状態は更新される
- 接続 → 手動変更相当（Mock の既定デバイスを変更）→ `ConnectDevice` 再送、で切り替えが起きない
- 1 tick に複数イベントが含まれるとき時系列順に処理され、最終状態へ落ち着く
- truncate 後の全文再読で切り替えが再発火しない
- ファイル未存在 tick で `NextDelaySeconds = idlePollIntervalSeconds`
- 読み取りエラー連続で遅延が 1→2→4 と伸び、上限 60 で頭打ちになる
- エラー後に成功したら遅延が `pollIntervalSeconds` に戻る

### 9.2 fixture

`tests/fixtures/` に実ログから採取した行を置く。個人・端末識別情報（`logId` 以外の GUID、`clientId`、シリアル番号、ハードウェアモデル）はダミー値へ置換する。`logId` は重複排除のテストに必要なので形式を保った一意値にする。

## 10. 完了条件

以下が実機で成立すること。

```
Windows ログオン
      ↓
INZONE Auto Switch が自動起動（ウィンドウは一切表示されない）
      ↓
INZONE Hub がいなければ静かに待機
      ↓
INZONE Hub 起動
      ↓
Buds をケースから取り出す → ConnectDevice
      ↓
既定の再生デバイス = スピーカー (INZONE Buds - Game)
      ↓
Buds をケースへ戻す → DisconnectDevice
      ↓
既定の再生デバイス = スピーカー (Realtek(R) Audio)
```

加えて:

- 接続後にユーザーが手動で出力を変えたら、次の抜き差しまで干渉しないこと
- Auto Switch と INZONE Hub の起動順序が入れ替わっても最終的に監視が始まること
- ActionLog の truncate / 再生成で二重切り替えが起きないこと
- スリープ / 復帰後に監視が継続すること
- 異常時に高頻度リトライで CPU を消費しないこと

## 11. 将来の拡張（v1 では実装しない）

- `SonyEventProvider` を HID 直接監視へ差し替える。`DeviceState` 以降は無改修
- 既定の録音デバイス（`INZONE Buds - Chat`）の連動
- タスクトレイ常駐 UI
- 複数プロファイル／複数ヘッドセット
