# AutoSwitch for INZONE — 設計仕様（C# / タスクトレイ常駐・一般公開版）

- 日付: 2026-09-15
- ステータス: 承認済み
- 関連: Issue rurute/inzone-auto-switch#1「タスクトレイ常駐アプリ化（最終ゴール）」
- 前身: `docs/superpowers/specs/2026-09-15-inzone-auto-switch-design.md`（PowerShell 版 v1。本書では「v1」と呼ぶ）
- 対象環境: Windows 11 / .NET 10（self-contained 同梱のため利用者の導入は不要）

## 1. 目的

v1（PowerShell の UI なし常駐ツール）を、**一般公開できる C# / WPF のタスクトレイ常駐アプリ**として作り直す。

- INZONE Hub 対応ヘッドセットの接続／切断に追従して、Windows の既定の再生デバイスを切り替える
- 切り替え先はユーザーが設定画面で、実在する再生デバイスの一覧から選ぶ
- タスクトレイのアイコンで状態が分かり、設定画面ですべての設定を変更できる
- GitHub で public に公開し、インストーラとポータブル zip を配布する

## 2. v1 から引き継ぐ事実と約束

v1 の実機調査（v1 spec §2）で確定した事実は本書でも前提とする。

- 検知源は `%APPDATA%\Sony\INZONE Hub\ActionLog.log`（1 行 1 JSON、UTF-8 BOM 付き、in-place truncate される、共有読み取り可）
- `actionTypeId` 23011 = ConnectDevice、23012 = DisconnectDevice
- 各行に一意な `logId`、epoch ミリ秒の `timeStamp`、`serviceInfo.targetDeviceModelName`（例: `INZONE Buds WF-G700N`）を持つ
- Hub は `ConnectDevice` を `DisconnectDevice` を挟まずに重複送出することがある

守る約束:

- **状態が遷移したときだけ切り替える。** 常時強制はせず、ユーザーの手動変更を次の抜き差しまで尊重する
- **INZONE Hub を起動しない。** Hub がいなければ待機し、後から起動したら自動で監視を始める
- **管理者権限を要求しない**
- **多重起動しない**

実機検証済みの機種は INZONE Buds のみ。検知は機種に依存しない形で実装し、README にその旨を明記する。

## 3. スコープ

### 含むもの

- ActionLog 監視による接続／切断検知と、既定の再生デバイスの切り替え（v1 のロジックの C# 移植）
- タスクトレイアイコン（4 状態）と右クリックメニュー
- 設定画面（左ナビ + ページ切り替え、WPF-UI）
- Windows 通知（切り替え時 / 問題発生時、それぞれ ON/OFF）
- ログオン時の自動起動（HKCU Run キー）
- 日本語 / 英語 UI
- インストーラ（Inno Setup、per-user）とポータブル zip の GitHub Releases 配布
- GitHub Actions による CI とリリースビルド
- 公開準備（名称変更、LICENSE、README、履歴のメールアドレス置換、public 化）

### 含まないもの（後続 Issue にする）

- 既定の録音デバイスの連動
- 一時停止
- 更新チェック
- トレイメニューからの手動切り替え
- 複数ヘッドセット・複数プロファイル
- arm64 ビルド
- コード署名
- winget 登録
- HID の直接監視
- v1 からの設定移行機能（v1 の利用者は作者のみのため、README の手順で代替する）

## 4. 名称と識別子

| 項目 | 値 |
|---|---|
| 表示名 | AutoSwitch for INZONE |
| リポジトリ名 | `autoswitch-for-inzone`（現 `inzone-auto-switch` から改名） |
| 識別子（フォルダ名等） | `AutoSwitchForInzone` |
| 実行ファイル | `AutoSwitchForInzone.exe` |
| Mutex | `Local\AutoSwitchForInzone` |
| 名前付きパイプ | `AutoSwitchForInzone-<ユーザー SID>` |
| Run キー値名 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の `AutoSwitchForInzone` |
| 設定 | `%LOCALAPPDATA%\AutoSwitchForInzone\settings.json` |
| ログ | `%LOCALAPPDATA%\AutoSwitchForInzone\logs\AutoSwitch.log` |
| ライセンス | MIT |

免責表示（README 日英、設定画面「情報」ページ）:

> 本ソフトウェアは非公式のツールであり、ソニーグループ株式会社およびその関連会社とは一切関係ありません。INZONE はソニーグループ株式会社またはその関連会社の商標です。

## 5. アーキテクチャ

単一の WPF プロセスに検知エンジンと UI を同居させる。

```
INZONE Hub ── ActionLog.log
                  │
                  ▼
┌────────────── AutoSwitch.Core ──────────────┐
│ ActionLogReader → HubEventParser(+SeenLogIdSet)
│        │                                    │
│        ▼                                    │
│ AutoSwitchEngine ── StateTransition         │
│   │  Tick() / StartupSync()                 │
│   │  EngineStatus / StatusChanged           │
│   ├── IAudioEndpointService ◄──────────┐    │
│   ├── IHubProcessProbe                 │    │
│   └── IClock                           │    │
│ SettingsStore / AppSettings / FileLogger    │
└──────────────────────────────────────┼──────┘
                                        │
AutoSwitch.Audio: CoreAudioEndpointService (COM)
                                        
AutoSwitch.App (WPF): EngineHost, Tray, SettingsWindow,
                      SingleInstance, StartupRegistration, Notifier
```

### 5.1 ソリューション構成

```
autoswitch-for-inzone/
├── AutoSwitchForInzone.sln
├── Directory.Build.props         共通設定（net10.0-windows、Nullable、TreatWarningsAsErrors）
├── .editorconfig                 dotnet format の基準
├── .gitattributes                改行を LF に固定（Windows ランナーでの format 差分を防ぐ）
├── global.json                   SDK とテストランナー（Microsoft.Testing.Platform）の指定
├── src/
│   ├── AutoSwitch.Core/          ロジック。UI・COM・レジストリに依存しない
│   ├── AutoSwitch.Audio/         Core Audio COM 実装（実機境界）
│   └── AutoSwitch.App/           WPF アプリ本体
├── tests/
│   ├── AutoSwitch.Core.Tests/    xUnit
│   └── fixtures/actionlog-sample.log   v1 から移設
├── tools/IconGen/                AppIcon.ico の生成ツール
├── installer/AutoSwitchForInzone.iss
├── .github/workflows/{ci.yml,release.yml}
├── .github/ISSUE_TEMPLATE/{bug_report.yml,feature_request.yml}
├── docs/manual-test.md
├── docs/superpowers/{specs,plans}/   v1 の文書は履歴として残す
├── README.md / README.en.md
├── LICENSE
└── THIRD-PARTY-NOTICES.md
```

v1 の `src/`（PowerShell）、`tests/*.Tests.ps1`、`tests/stubs/`、`scripts/`、`config.json`、`logs/` は削除する。削除前の状態にタグ `powershell-v1` を打つ。

### 5.2 依存パッケージ

| パッケージ | 用途 | 使用プロジェクト |
|---|---|---|
| WPF-UI（lepoco/wpfui） | Fluent スタイル、NavigationView | App |
| H.NotifyIcon.Wpf | トレイアイコン、通知 | App |
| CommunityToolkit.Mvvm | ViewModel | App |
| xunit.v3（Microsoft.Testing.Platform で実行） | テスト（配布物には含めない） | Core.Tests |

Core と Audio は外部パッケージに依存しない。JSON は `System.Text.Json` の `JsonDocument` / `Utf8JsonWriter` で読み書きする（項目ごとに既定値へフォールバックするため、シリアライザは使わない）。

## 6. Core の構成要素と契約

### 6.1 ActionLogReader

```csharp
sealed class ActionLogReader(string path)
ReadResult Read();   // ReadResult { ReadStatus Status; IReadOnlyList<string> Lines; string? Error }
enum ReadStatus { Ok, FileMissing, Truncated, Recreated, Error }
```

v1 spec §4.2「ActionLogReader.psm1」の挙動をそのまま移植する。

- ファイル未存在で状態をリセットし `FileMissing`
- `Length < Offset` または `Length < LastLength` で `Truncated`、先頭から読む
- 既読部分の先頭 `min(256, Offset)` バイトの指紋が変わったら `Recreated`、先頭から読む（`CreationTimeUtc` は使わない）
- 長さが同じでも毎回開いて指紋を照合する
- 読み出したバイト列の最後の LF までを確定行とし、`Offset` はそこまでしか進めない
- 1 回の読み出しは 4MB まで
- 行頭 BOM と行末 CR を除去する
- `FileShare.ReadWrite | FileShare.Delete` で開く。IO 例外は `Error` として返し、外に漏らさない

`ActionLogReader` はパスごとに状態を持つ。設定でパスが変わったらエンジンが新しいインスタンスに差し替える。

### 6.2 HubEventParser / SeenLogIdSet

```csharp
sealed class SeenLogIdSet(int capacity = 200) { bool Add(string logId); }
static class HubEventParser
    IReadOnlyList<HubEvent> Parse(IEnumerable<string> lines, SeenLogIdSet seen);
record HubEvent(string LogId, long TimeStampMs, HeadsetState State, string? ModelName);
enum HeadsetState { Unknown, Connected, Disconnected }
```

- 文字列の事前判定（`"actionTypeId"` と `2301` を含む行）で候補を絞ってから JSON パースする
- 23011 → `Connected`、23012 → `Disconnected`、それ以外は無視
- パース失敗行、`logId` 欠落行は読み飛ばす
- `logId` で重複排除。`SeenLogIdSet` は挿入順の有限リングで、上限超過時は最古から捨てる
- 入力の順序（= 追記順 = 時系列順）を保って返す。並べ替えない

### 6.3 StateTransition

```csharp
static (bool Changed, HeadsetState NewState) Resolve(HeadsetState current, HeadsetState incoming);
```

同じ状態なら `Changed = false`。`Unknown` からの初回イベントは遷移とみなす。

### 6.4 IAudioEndpointService と DeviceResolver

```csharp
interface IAudioEndpointService
{
    IReadOnlyList<AudioEndpoint> GetPlaybackEndpoints();   // 有効な再生デバイスのみ
    AudioEndpoint? GetDefaultPlayback();
    void SetDefaultPlayback(string endpointId);            // 失敗時は AudioServiceException
    event EventHandler? EndpointsChanged;                  // 追加・削除・状態変化・既定変更
}
record AudioEndpoint(string Id, string Name);
```

```csharp
static DeviceResolution DeviceResolver.Resolve(DeviceTarget target, IReadOnlyList<AudioEndpoint> endpoints);
record DeviceTarget(string? Id, string? Name);
// DeviceResolution { ResolutionKind Kind; AudioEndpoint? Endpoint }
enum ResolutionKind { MatchedById, MatchedByName, NotConfigured, NotFound }
```

- `Id` が一覧にあれば `MatchedById`
- なければ `Name` の完全一致で探し、1 件以上あれば先頭を `MatchedByName`。エンジンは設定の `Id` をその端末の ID へ更新して保存する
- `Id` も `Name` も空なら `NotConfigured`、どちらも一致しなければ `NotFound`

### 6.5 AutoSwitchEngine

```csharp
sealed class AutoSwitchEngine(
    IAudioEndpointService audio, IHubProcessProbe hub, IClock clock, IAppLogger logger,
    Func<AppSettings> settings, Action<Func<AppSettings, AppSettings>> updateSettings)
    // 設定の更新は「現在値 → 新しい値」の関数で渡す。UI からの変更と競合しても上書きしないため

TimeSpan StartupSync();
TimeSpan Tick();
void Refresh();                        // Tick を待たずにデバイスの状態と Issue を再評価する                       // 次に待つ時間を返す。ループもスリープもしない
EngineStatus Status { get; }
event EventHandler<EngineStatus>? StatusChanged;   // Status が変化したときだけ

interface IHubProcessProbe { HubProcessInfo? Probe(); }   // 未起動なら null。起動時刻が取れなければ StartTimeUtc = null
record HubProcessInfo(DateTime? StartTimeUtc);
interface IClock { DateTime UtcNow { get; } }

record EngineStatus(
    HeadsetState HeadsetState,
    string? ModelName,
    bool? HubRunning,          // まだ確認していなければ null
    AudioEndpoint? CurrentOutput,
    IssueKind Issue);

enum IssueKind { None, ActionLogMissing, HubNotRunning, AudioError, DeviceNotFound, NotConfigured }
```

`Issue` が複数該当する場合は、上の列挙の**後ろにあるものを優先**する（`NotConfigured` が最優先、`ActionLogMissing` が最低）。

#### Tick の処理

1. 前回の Tick から 300 秒超の間隔があれば、スリープ復帰とみなして連続エラー数を 0 に戻し、Info ログを残す
2. 設定の `actionLogPath` が前回と異なれば Reader を作り直す（`SeenLogIdSet` は維持する）
3. Hub の起動確認は、前回の確認から `idlePollIntervalSeconds` 以上経過したときだけ行う
4. `Read()` の結果で分岐する
   - `FileMissing`: 連続エラー数を 0 に戻し、`idlePollIntervalSeconds` を返す
   - `Error`: 連続エラー数を加算し、`min(pollIntervalSeconds × 2^(n-1), 60)` 秒を返す
   - `Ok` / `Truncated` / `Recreated`: 連続エラー数を 0 に戻し、パースしたイベントを順に `StateTransition` へ通す。遷移したものについてだけ §6.6 の切り替えを行い、`pollIntervalSeconds` を返す
5. `ModelName` は最後に見たイベントの値で更新する
6. `CurrentOutput` は `GetDefaultPlayback()` で更新する
7. `Issue` を再計算する
   - `NotConfigured` / `DeviceNotFound`: `switch = true` の `onConnect` / `onDisconnect` それぞれを、キャッシュ済みの再生デバイス一覧に対して `DeviceResolver` で解決し、どちらかが該当すれば立てる。切り替えの瞬間を待たず常時判定するので、設定ミスに事前に気づける
   - `AudioError`: §6.6 の 3 で立て、次に切り替えが成功するか設定が変わるまで保持する
   - `HubNotRunning`: 直近の Hub 起動確認が「未起動」
   - `ActionLogMissing`: 直近の `Read()` が `FileMissing`

再生デバイス一覧はエンジン起動時に取得してキャッシュし、`EndpointsChanged` を受けたら取り直す（毎 tick COM で列挙しない）。
8. `Status` が前回と異なれば `StatusChanged` を発火する

Tick 全体を try/catch で包む。想定外の例外は Error ログに残し、`Error` と同じバックオフを返す。**エンジンのループを例外で止めない。**

#### StartupSync

v1 spec §5.3 と同じ。

- `startupSyncMaxAgeMinutes = 0` なら同期しない。ただし既存行を読み飛ばし、`logId` を既読登録する
- Hub が起動していなければ切り替えない
- 最後のイベントが Hub の起動時刻以降、かつ `startupSyncMaxAgeMinutes` 以内の場合だけ、その状態を採用して 1 回切り替える
- 採用するかどうかにかかわらず、読んだ全行の `logId` を既読登録し、Reader のオフセットを末尾まで進める

### 6.6 切り替え処理

状態が `Connected` に遷移したら `onConnect`、`Disconnected` に遷移したら `onDisconnect` の設定を使う。

1. `switch = false` なら状態だけ更新し、Info ログを残す
2. `DeviceResolver.Resolve` の結果で分岐する
   - `NotConfigured` / `NotFound`: 切り替えずに Warn ログを残す（`Issue` は §6.5 の 7 で常時判定される）。状態は遷移済みとして記録する（再試行で暴れない）
   - 一致した場合: `GetDefaultPlayback()` の ID が一致していれば何もしない（Debug ログ）。一致していなければ `SetDefaultPlayback(id)` を呼ぶ
3. `SetDefaultPlayback` が `AudioServiceException` を投げたら `Issue = AudioError` にして Error ログを残す。次に切り替えが成功するか、設定が変わるまでこの `Issue` を保持する
4. 成功したら `SwitchPerformed` イベントを発火する（通知用。§8.3）

### 6.7 設定

```csharp
sealed record AppSettings;          // 下の settings.json と同じ構造を持つ不変 record。変更は with 式で作り直す
sealed record SwitchRule(bool Switch, string? DeviceId, string? DeviceName);   // onConnect / onDisconnect
sealed class SettingsStore(string path)
    SettingsLoadResult Load();         // (Settings, Warnings, WasCorrupt)。例外を投げない
    bool TrySave(AppSettings settings, out string? error);
```

`settings.json`:

```json
{
  "schemaVersion": 1,
  "language": "system",
  "onConnect":    { "switch": true, "deviceId": null, "deviceName": null },
  "onDisconnect": { "switch": true, "deviceId": null, "deviceName": null },
  "notifyOnSwitch": false,
  "notifyOnIssue": true,
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
| `schemaVersion` | `1` | `1` のみ。それ以外は警告して既定値で読む |
| `language` | `system` | `system` / `ja` / `en` |
| `onConnect.switch` / `onDisconnect.switch` | `true` | bool |
| `onConnect.deviceId` / `deviceName` など | `null` | 文字列または null |
| `notifyOnSwitch` | `false` | bool |
| `notifyOnIssue` | `true` | bool |
| `actionLogPath` | `%APPDATA%\Sony\INZONE Hub\ActionLog.log` | 空でない文字列（読み込み時に `%ENV%` を展開する） |
| `pollIntervalSeconds` | `1` | 1–60 |
| `idlePollIntervalSeconds` | `5` | 1–300 |
| `startupSyncMaxAgeMinutes` | `10` | 0–1440（0 で起動時同期を無効化） |
| `logLevel` | `Info` | `Debug` / `Info` / `Warn` / `Error` |
| `maxLogSizeKB` | `1024` | 64–102400 |

- 未知のキーは無視する。欠損キーは既定値で補う。範囲外や型不正の値は既定値に戻し、警告を返す
- ファイルが無ければ全既定値で動く（警告なし。初回起動）
- JSON が壊れていれば `settings.json.bad` へ退避し、全既定値と警告を返す。**例外を投げない**
- 保存は `settings.json.tmp` に書いてから `File.Replace` / `File.Move(overwrite)` で置き換える
- `actionLogPath` は展開前の文字列のまま保存する
- 自動起動の有無は settings.json に持たず、Run キーの有無そのものを正とする

#### 初回起動時の推奨値

`onConnect` と `onDisconnect` のデバイスがどちらも未設定の状態で起動したら、`DeviceSuggester`（Core、純粋関数）で推奨値を決めて保存する。設定画面を自動で開く条件は §8.1。

- `onConnect`: 名前に `INZONE` を含む再生デバイスが 1 件以上あれば、そのうち名前に `Game` を含むもの、なければ先頭
- `onDisconnect`: 現在の既定の再生デバイスが `INZONE` を含まなければそれ。含む場合は `INZONE` を含まない最初の再生デバイス
- 候補が無ければ未設定のまま（`Issue = NotConfigured`）

### 6.8 FileLogger

v1 spec §4.2「Logging.psm1」を移植する。

- 形式: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message`
- 設定レベル未満は書かない
- `maxLogSizeKB` を超えたら `.1` へ 1 世代だけ退避する
- 直前と同じ文言は抑制し、別の文言が来た時点で `(suppressed N identical messages)` を先に出す
- 書き込み失敗で例外を投げない

**ログの文言は英語固定**とする（UI 言語に依存させず、公開 Issue に貼られたログを誰でも読めるようにするため）。

## 7. Audio（実機境界）

`CoreAudioEndpointService : IAudioEndpointService, IDisposable`

- `IMMDeviceEnumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)` で一覧を取る。名前は `PKEY_Device_FriendlyName`
- `GetDefaultAudioEndpoint(eRender, eMultimedia)` で現在の既定を取る
- `IPolicyConfig.SetDefaultEndpoint(id, role)` を `eConsole` / `eMultimedia` / `eCommunications` の 3 ロールすべてに対して呼ぶ（v1 の AudioDeviceCmdlets と同じ挙動）
  - `IPolicyConfig` は非公開インターフェイスなので、Windows 11 で動作する既知の GUID を使う。失敗時は HRESULT を含む `AudioServiceException` にする
- `IMMNotificationClient` を登録し、`OnDeviceAdded` / `OnDeviceRemoved` / `OnDeviceStateChanged` / `OnDefaultDeviceChanged` を `EndpointsChanged` にまとめる。通知は COM スレッドから届くため、イベントを発火するだけで重い処理はしない
- COM 定義は `[GeneratedComInterface]`（source-generated COM）で書く。trimming と単一ファイル発行に対応するため
- 呼び出しは専用の STA スレッドに寄せず、MTA から呼ぶ（Core Audio は free-threaded）

## 8. App（WPF）

### 8.1 起動と終了

1. Mutex `Local\AutoSwitchForInzone` を取得する。取得できなければ、名前付きパイプで既存プロセスへ `show-settings` を送り、終了コード 0 で終了する
2. 設定を読み、ログを初期化し、言語を適用する（`system` は `CultureInfo.CurrentUICulture` が `ja` 系なら日本語、それ以外は英語）
3. `CoreAudioEndpointService` と `AutoSwitchEngine` を作り、`EngineHost` を開始する
   - `EngineHost` は専用のバックグラウンドスレッドで `StartupSync()` を行い、以後は `Tick()` → 返された時間だけ待機、を繰り返す
   - 待機中に「設定変更」または「終了」の合図が来たら、待機を打ち切る
4. トレイアイコンを表示する
5. 初回起動、または `--minimized`（Run キー登録時に付与）なしで起動した場合は設定画面を開く
6. 終了はトレイメニューの「終了」だけで行う。設定画面の × は画面を閉じるだけで、常駐は続ける
7. 終了時にエンジンを止め、COM の通知登録を解除し、Mutex を解放する

UI スレッドの未処理例外（`DispatcherUnhandledException`）は、fatal ログに残し、MessageBox で知らせてから終了する（終了時にトレイアイコンを破棄すると、トースト通知も一緒に消えてしまうため）。`AppDomain.UnhandledException` はプロセスが続行できないので fatal ログだけを残す。自動の再起動はしない。

設定画面を自動で開くのは、初回起動（`settings.json` が無かった）と、`--minimized` なしで起動したとき（スタートメニューからの起動やインストーラの「起動する」）。ログオン時の自動起動（`--minimized`）では開かない。

### 8.2 トレイ

アイコンは状態ごとに 4 種類ある。タスクバーのテーマ（`HKCU\...\Themes\Personalize\SystemUsesLightTheme`）に合わせて白系と黒系を切り替え、テーマの変更も追従する。

| 表示 | 条件 | 見た目 |
|---|---|---|
| 要確認 | `Issue != None` | ヘッドセット + 黄色バッジ |
| 接続中 | `Connected` | ヘッドセット + 緑バッジ |
| 未接続 | `Disconnected` | ヘッドセットのみ |
| 不明 | `Unknown` | ヘッドセット（半透明） |

上の行ほど優先する。

- ツールチップ: `AutoSwitch for INZONE — <機種名> <状態>`。要確認の場合は問題の内容を表示する
- 右クリックメニュー:
  - （無効項目）`● <機種名> — <状態>`
  - （無効項目）`出力: <現在の既定デバイス名>`
  - 区切り
  - `設定を開く…`（アイコンのダブルクリックでも開く）
  - `ログフォルダを開く`
  - 区切り
  - `終了`

### 8.3 通知

`H.NotifyIcon` の `ShowNotification` を使う。

- **問題発生時**（`notifyOnIssue`）: `Issue` が `None` から別の値、または別の種類に変わったときに 1 回だけ出す。同じ `Issue` が続いている間は再通知しない
  - `HubNotRunning` と `ActionLogMissing` は、アプリ起動から 60 秒以内には通知しない（ログオン直後の起動順の差による誤報を避けるため）
  - 通知をクリックしたら設定画面を開く
- **切り替え時**（`notifyOnSwitch`）: `SwitchPerformed` のたびに「<デバイス名> に切り替えました」

### 8.4 設定画面

WPF-UI の `FluentWindow` + `NavigationView`（左ナビ）。最小サイズは 720×520。すべての変更はその場で `AppSettings` に反映して保存する（「適用」ボタンなし）。

| ページ | 項目 |
|---|---|
| デバイス | 現在の状態カード（機種名・状態・現在の出力先・Issue があればその説明）/「接続したとき」: 切り替えるトグル、切り替え先のドロップダウン /「切断したとき」: 同じ構成 |
| 全般 | Windows の起動時に開始（Run キーの登録と解除）/ 言語（システムに合わせる / 日本語 / English。トレイメニューとツールチップは即時に、設定画面は開き直して反映する） |
| 通知 | 切り替えたときに通知 / 問題が起きたときに通知 |
| 詳細 | ActionLog.log のパス（テキスト + 参照ボタン + 「見つかりました / 見つかりません」表示 + 既定に戻す）/ ポーリング間隔 / Hub 不在時の間隔 / 起動時同期の鮮度 / ログレベル / ログフォルダを開く |
| 情報 | バージョン / GitHub へのリンク / ライセンス（MIT）と依存 OSS の一覧 / 免責表示（§4） |

- ドロップダウンは `GetPlaybackEndpoints()` の結果で、`EndpointsChanged` を受けたら更新する
- 保存済みのデバイスが一覧に無い場合は、その名前を「（見つかりません）」付きで選択状態のまま表示し、警告文を出す
- 数値項目は `NumberBox` で範囲を制限する。範囲外の入力は保存しない
- ViewModel は Core の型を画面へ写すだけにし、判断ロジック（デバイス解決、推奨値、Issue 判定）は Core に置く

### 8.5 自動起動

`StartupRegistration`（App）が `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値 `AutoSwitchForInzone` を読み書きする。

- 値は `"<exe のフルパス>" --minimized`
- 設定画面の表示時に、登録済みの値の exe パスが現在の exe と異なっていれば、現在のパスで書き直す（ポータブル版を移動した場合への対応）

### 8.6 多言語化

- UI 文字列は `Resources/Strings.resx`（英語、ニュートラル）と `Strings.ja.resx`
- 通知・トレイメニュー・ツールチップも resx から取る
- ログは英語固定（§6.8）

## 9. エラー処理のまとめ

| 状況 | Issue | アイコン | 通知（notifyOnIssue） | ログ |
|---|---|---|---|---|
| 切り替え先が未設定 | NotConfigured | 要確認 | 出す | Warn |
| 切り替え先のデバイスが無い | DeviceNotFound | 要確認 | 出す | Warn |
| 音声 API の失敗 | AudioError | 要確認 | 出す | Error |
| INZONE Hub 未起動 | HubNotRunning | 要確認 | 起動 60 秒後以降に出す | Info |
| ActionLog.log が無い | ActionLogMissing | 要確認 | 起動 60 秒後以降に出す | Info |
| 読み取りの一時的な失敗 | 変えない | 変えない | 出さない | Warn（バックオフ） |
| Tick 内の想定外例外 | 変えない | 変えない | 出さない | Error（バックオフ） |
| settings.json 破損 | 変えない | 変えない | 出す（1 回） | Warn |
| UI スレッドの未処理例外 | — | — | MessageBox で知らせて終了 | Fatal |
| スリープ / 復帰 | 変えない | 変えない | 出さない | Info（ギャップ検知） |

`HubNotRunning` の判定はプロセス名 `INZONEHub` の有無で行う。複数の Issue が同時に該当する場合は §6.5 の優先順位に従う（表の上ほど優先）。

## 10. テスト

### 10.1 Core.Tests（xUnit）

v1 spec §9.1 の観点をすべて C# に移植し、以下を追加する。テストは INZONE Hub も実機の音声デバイスも無い環境で完結させる（`IAudioEndpointService` / `IHubProcessProbe` / `IClock` はテスト用のフェイク実装を使う）。

移植する観点（v1 spec §9.1 の対応節）:

- ActionLogReader: 初回全読み、追記分のみ、途中行の持ち越し、2 種類の truncate、削除 50ms 後の再作成、同じ長さの差し替え、追記を誤判定しない、FileMissing、BOM、CRLF、マルチバイト、例外を漏らさない、4MB 上限
- HubEventParser: 23011/23012 の対応、他の actionType の無視、不正 JSON の読み飛ばし、logId 重複排除、リング容量、実ログ fixture で Connected 3 件と Disconnected 1 件を時系列順に返す
- StateTransition: 6 通りの遷移
- AutoSwitchEngine（v1 の Orchestrator 相当）: 起動時同期の 4 条件、同期後の再発火なし、接続・切断での切り替え、`switch = false`、手動変更の尊重、1 tick 内の複数イベント、truncate 後の再発火なし、FileMissing の間隔、バックオフ 1→2→4→…→60、成功で戻る、失敗継続中のギャップ検知によるリセット（成功経路と区別できる形で）
- SettingsStore（v1 の Config 相当）: 完全な JSON、欠損キーの補完、未知キーの無視、`%ENV%` 展開、範囲外・型不正のフォールバックと警告、ファイル無し、JSON 破損（`.bad` 退避と例外なし）
- FileLogger: 形式、レベルフィルタ、ローテーション、同一メッセージ抑制、書き込み失敗で例外なし

追加する観点:

- HubEventParser が `targetDeviceModelName` を取り出す。欠落していても `null` でイベントを返す
- DeviceResolver: ID 一致 / ID 不一致で名前一致 / 両方空で NotConfigured / 不一致で NotFound
- 名前一致で解決したとき、エンジンが設定の `deviceId` を更新して保存する
- DeviceSuggester: INZONE + Game を優先 / INZONE のみ / 既定が INZONE の場合 / 候補なし
- Issue の優先順位
- `SetDefaultPlayback` の例外で `AudioError` になり、ループが止まらない
- Tick 内の想定外例外でバックオフし、例外が外に出ない
- `StatusChanged` は変化したときだけ発火する
- `actionLogPath` の変更で Reader が差し替わり、既読の `logId` は維持される
- SettingsStore の保存と読み込みの往復、`actionLogPath` を展開前の文字列で保存する
- `Strings.resx` と `Strings.ja.resx` のキー集合と書式のプレースホルダが一致する（Core.Tests から App プロジェクトは参照せず、テスト出力へコピーした 2 つの resx を XML として読み比べる）
- `IcoWriter` が正しい .ico を書く（ディレクトリ、DIB ヘッダ、行の上下反転）

### 10.2 実機確認（docs/manual-test.md）

`AutoSwitch.Audio` と `AutoSwitch.App` は自動テストの対象外とし、次のチェックリストで確認する。

- インストーラでのインストール → 初回起動で設定画面が開き、推奨値が入っている
- Buds をケースから出す → INZONE Buds に切り替わり、アイコンが接続中になる
- Buds をケースへ戻す → スピーカーに切り替わり、アイコンが未接続になる
- 接続中に手動で出力を変える → 次の抜き差しまで戻されない
- Hub を終了する → 要確認アイコンと通知、Hub を再起動すると自動復帰
- 切り替え先のデバイスを無効化する → 要確認と通知
- 2 回目の起動 → 既存の設定画面が開き、プロセスは増えない
- 「Windows の起動時に開始」を ON にしてサインアウト・サインイン → 常駐し、画面は開かない
- ダーク / ライトのタスクバーでアイコンが見える
- 言語の切り替え
- スリープ / 復帰後も監視が続く
- アンインストール → Run キーが消える。設定を消すかどうかを聞かれる
- ポータブル zip を別フォルダへ移動して起動 → Run キーのパスが更新される

## 11. ビルドと CI

- ローカル: WSL から Windows 側の `dotnet.exe` で `build` / `test` を行う。
  - UNC パス（`\\wsl.localhost\...`）上でのビルド（WPF・WPF-UI・H.NotifyIcon を含む）と xunit.v3 のテストは、計画作成時（2026-09-15）に実機で成立を確認済み
  - ただし UNC（9P）上の `bin` に apphost の `.exe` を書き出すと `MSB3021`（アクセス拒否）で失敗する。そのため `Directory.Build.props` で、`\\` から始まるパスでビルドするときだけ `UseArtifactsOutput` を有効にし、出力を `%LOCALAPPDATA%\AutoSwitchForInzone-build\` に置く。CI のビルドには影響しない
  - .NET 10 SDK では xunit.v3 の VSTest モードが拒否されるため、`global.json` で Microsoft.Testing.Platform のランナーを指定し、`dotnet test --project` で実行する
- `ci.yml`: `windows-latest` で、`pull_request` と `main` への push のたびに実行する
  - `dotnet restore` → `dotnet format --verify-no-changes` → `dotnet build -c Release` → `dotnet test -c Release`
- `release.yml`: `v[0-9]+.[0-9]+.[0-9]+` 形式のタグ push で実行する。PR と手動実行では、手順 7 の代わりに成果物を Actions の artifact として保存し、公開前の検証に使う
  1. テスト
  2. `dotnet publish src/AutoSwitch.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
  3. バージョンはタグから取り、`-p:Version=` で渡す
  4. Inno Setup でインストーラを作る（§12.1）
  5. zip を作る（exe、README.md、README.en.md、LICENSE、THIRD-PARTY-NOTICES.md）
  6. SHA256 チェックサムファイルを作る
  7. **下書き（draft）の** GitHub Release を作り、成果物を添付する
- trimming は WPF で非対応のため有効にしない

## 12. 配布

### 12.1 インストーラ（Inno Setup）

| 項目 | 値 |
|---|---|
| 権限 | `PrivilegesRequired=lowest`（管理者不要） |
| インストール先 | `{localappdata}\Programs\AutoSwitchForInzone` |
| ショートカット | スタートメニューのみ |
| 最終画面 | 「Windows の起動時に開始」（既定 ON）、「今すぐ起動」（既定 ON） |
| 自動起動 | チェック時にアプリと同じ Run キー値を書く |
| 上書きインストール | 実行中のプロセスがあれば終了を促す（`CloseApplications`） |
| アンインストール | プロセスを終了し、Run キー値を削除する。`%LOCALAPPDATA%\AutoSwitchForInzone`（設定とログ）を削除するかを確認する |
| 言語 | 日本語 / 英語 |

### 12.2 ポータブル zip

exe を任意のフォルダに置いて起動する。設定とログはインストーラ版と同じ `%LOCALAPPDATA%\AutoSwitchForInzone` を使う。

### 12.3 署名しないことへの対応

README（日英）に、SmartScreen の「詳細情報 → 実行」の手順と、Release に添付した SHA256 での検証方法を書く。

## 13. 公開手順

実装とは別の作業として、以下の順で行う。**2・3・5・6 は取り消しにくい、または外部公開を伴うため、各手順の実行直前に作者の確認を取る。**

1. 本 branch の実装を PR 経由で main へ squash merge する
2. **履歴のメールアドレス置換**: `git filter-repo --mailmap` で、全履歴の author / committer の `<個人のメールアドレス>` を `53891204+rurute@users.noreply.github.com` に置き換え、main と `powershell-v1` タグを force push する。事前に、他の clone やオープン中の PR が無いことを確認する。以後、このリポジトリの `user.email` を noreply に設定する
3. リポジトリ名を `autoswitch-for-inzone` に変更し、ローカルの remote URL を更新する
4. README（日英）、LICENSE、THIRD-PARTY-NOTICES、Issue テンプレート、スクリーンショットがそろっていることを確認する（ファイル自体は 1 の PR に含める）
5. visibility を public にする
6. `v1.0.0` タグを push し、下書きの Release を確認してから公開する

README には次の節を置く: 概要 / 動作要件（Windows 11、INZONE Hub）/ インストール（インストーラ・ポータブル・SmartScreen）/ 使い方 / 設定項目 / 対応機種（検証済みは INZONE Buds のみ）/ 仕組みと設計上の約束 / トラブルシューティング / PowerShell 版からの移行 / 開発 / ライセンスと免責。

「PowerShell 版からの移行」節の内容: v1 の `scripts\Uninstall.ps1 -RemoveFiles` を実行するか、スケジュールタスク `INZONE Auto Switch` を削除する。v1 のコードはタグ `powershell-v1` で参照できる。

## 14. 完了条件

- §10.1 の自動テストがすべて通り、CI が緑である
- §10.2 のチェックリストが実機ですべて成立する
- タグ push でインストーラ・zip・チェックサムを添付した下書き Release が自動で作られる
- §13 の手順を終え、public リポジトリで `v1.0.0` が公開されている
