# 録音デバイスの切り替え — 設計仕様

- 日付: 2026-09-16
- ステータス: 承認済み・実装済み（2026-09-16。実機確認は [手動確認記録](../../manual-test-capture-switching.md)）
- 実装計画: `../plans/2026-09-16-capture-device-switching.md`（本書の承認後に作成）
- 関連: Issue rurute/inzone-auto-switch#8（本書の対象）、Issue rurute/inzone-auto-switch#9（設定画面。本書の後に行う）、Issue rurute/inzone-auto-switch#1
- 前提: 最小 C# トレイ版（`2026-09-15-inzone-minimal-csharp-design.md`）の段階1〜4 が完了し、作者が日常利用している
- 対象: Windows 11 / INZONE Buds / INZONE Hub、作者の日常利用

## 1. 目的

Buds の接続・切断に合わせて、**再生デバイスだけでなく録音デバイス（マイク）の既定も**切り替える。

最小版は録音の既定を変えない（最小版 spec §2 で範囲外）。Buds をケースに入れても「ヘッドセット マイク (INZONE Buds - Chat)」が有効なまま残るため、Windows に任せても PC 側のマイクへ戻らない（2026-09-16、実機の録音デバイスの一覧で確認。有効なのは「ヘッドセット マイク (INZONE Buds - Chat)」と「マイク (USB Microphone)」の2つ）。

作者の使い方で期待する結果は次のとおり。

| 状態 | 再生（既存） | 録音（本書で追加） |
|---|---|---|
| 接続 | スピーカー (INZONE Buds - Game) | ヘッドセット マイク (INZONE Buds - Chat) |
| 切断 | スピーカー (Realtek(R) Audio) | マイク (USB Microphone) |

## 2. 最小版 spec との関係

- 最小版 spec §2 が「今回含めないもの」とした「録音デバイスの切り替え」を、本書で追加する。
- 最小版 spec §3「維持する動作」は**再生について引き続き維持する**。再生の挙動・判定の順序・ログの文言は一切変えない。
- 録音は旧版 PowerShell 版に無い機能であり、旧版との等価性の対象ではない。録音の規則は本書 §4 が基準で、「再生と同じ規則」を再生の現在の実装（C#）から写す。
- 最小版 spec §4 の「設定は起動時に確定し、実行中の設定更新は扱わない」「既存の config.json は上書きしない」は維持する。

## 3. 設定

`config.json` に2つのキーを追加する。

| キー | 意味 | 既定値 |
|---|---|---|
| `budsInputDevice` | 接続時の録音の切り替え先（デバイス名） | 空文字（未設定） |
| `speakerInputDevice` | 切断時の録音の切り替え先（デバイス名） | 空文字（未設定） |

読み込みの規則:

- 文字列ならそのまま受け入れる。**空文字（空白だけを含む）は警告なしで受け入れ、未設定として扱う。**既存の文字列の項目（`budsDevice` など）は空を警告して既定値へ戻すが、この2キーは空が正当な値なので扱いを分ける。
- キーが無ければ空（未設定）。
- 文字列以外（数値・真偽値・null・配列など）は、既存の項目と同じ書式で警告を残し、空（未設定）として扱う。
- キー名の照合・重複キーの解決は既存の項目と同じ（大文字小文字を区別せず、後の値で解決する）。

既定ファイルとリポジトリ直下の `config.json` には**この2キーを足さない。**リポジトリ直下の `config.json` は旧版の配置（`scripts/Install.ps1`）にも使われ、足すと旧版が「未知の設定キー」の警告を出すため。既存の「既定ファイルの内容がリポジトリ直下の `config.json` と一致する」検査もそのまま成り立つ。キーの書き方は `docs/operations-minimal-csharp.md` に載せ、Issue rurute/inzone-auto-switch#9 の設定画面ができたら画面から設定する。

## 4. 切り替えの規則

- `switchOnConnect` / `switchOnDisconnect` は、その向きの**再生と録音の両方**に効く。false の向きでは、どちらも切り替えない（ログの文言は現在のまま）。
- 状態遷移のたびに、また起動時同期で切り替えるときに、**まず再生、次に録音**を切り替える。
- 録音は、その向きの切り替え先が未設定なら何もしない（切り替えを試みず、ログも残さない）。
- **再生と録音は互いに独立する。**片方の結果が「見つからない」「操作できない（セッションを開けない）」「失敗」でも、もう片方は試みる。結果はそれぞれログに残し、レベルは結果ごとに再生と同じ（切り替えた = Info、既に既定 = Debug、見つからない・操作できない = Warn、失敗 = Error）。
- **例外の境界は現在のまま保つ。**音声操作で起きる通常の失敗（セッションを開けない、一覧・既定の取得や変更の例外）は `AudioSwitcher` が結果（`SwitchOutcome`）に変換し、外へ出さない。一方、`AudioSwitcher` 自身が想定外の例外を投げた場合は、エンジンは現在と同じくそれを捕まえずに外へ出す（監視ループが記録する。既存テスト `Tick_SwitcherThrows_PropagatesAfterUpdatingState` の契約）。このとき、再生で例外が出れば録音は試みない。独立性が及ぶのは「結果として返る失敗」までであり、エンジンに新しい catch は足さない（spec レビュー F1）。
- 判定の順序は再生と同じで、**1か所の実装を共有する**。
  1. 既定のデバイスが Console・Multimedia・Communications の3ロールとも目的の名前なら何もしない。1ロールでも違う（既定が無いロールを含む）なら先へ進む。取得に失敗したら判定せず先へ進む（当初は Multimedia ロールだけで判定していた。Windows の設定で録音を選ぶと Communications だけが残るため、Issue rurute/inzone-auto-switch#14 で3ロールに改めた、2026-09-17）
  2. 有効なデバイスの一覧から、名前が一致する（大文字小文字を区別しない）最初のものを選ぶ。無ければ「見つからない」
  3. Console・Multimedia・Communications の3ロールを変更する
- 状態遷移のときだけ切り替える、手動変更を次の遷移まで尊重する、同じ状態のイベントが続いても切り替えない、という最小版の規則は録音にもそのまま及ぶ（切り替えの引き金を再生と共有するため）。
- 音声操作のセッションは、再生と録音でそれぞれ開いて閉じる（最小版と同じく、切り替えのたびに開いて破棄する）。
- **終了の契約を保つ。**最小版の段階3は「終了が要求されたあとに監視スレッドが行うのは、多くとも1回の音声操作」と定めている（最小版 plan §6「spec §4『終了時の解放』の読み方」）。1回の切り替えが再生と録音の2回の音声操作になるため、**再生を終えたあと、録音を始める前に取り消しを確かめ、要求されていれば録音を行わない**（Info で `終了が要求されたため、録音の切り替えを行いません。` を残す）。これで取り消しのあとの音声操作は、引き続き多くとも1回に収まる（plan 作成時に追加、2026-09-16）。

### ログの文言

再生の文言は変えない。録音は「再生」を「録音」に置き換える。

| 結果 | 再生（現在のまま） | 録音（追加） |
|---|---|---|
| 切り替えた | `既定の再生デバイスを切り替えました: {名前}` | `既定の録音デバイスを切り替えました: {名前}` |
| 既に既定 | `既に既定の再生デバイスです: {名前}` | `既に既定の録音デバイスです: {名前}` |
| 見つからない | `再生デバイスが見つかりません: {名前}` | `録音デバイスが見つかりません: {名前}` |
| 操作できない | `音声デバイスを操作できません。切り替えを見送ります: {詳細}` | `音声デバイスを操作できません。録音の切り替えを見送ります: {詳細}` |
| 失敗 | `切り替えに失敗しました ({名前}): {詳細}` | `録音の切り替えに失敗しました ({名前}): {詳細}` |

起動時に Info で1行、録音の設定を残す。未設定の向きは `（未設定）` と書く。

```text
録音の切り替え: 接続時=ヘッドセット マイク (INZONE Buds - Chat) / 切断時=マイク (USB Microphone)
録音の切り替え: 接続時=（未設定） / 切断時=（未設定）
```

## 5. 構成

最小版の Core / Audio / App の分担を保つ。

### Audio（`AutoSwitch.Audio`。Core を参照しない）

- `public enum AudioFlow { Render, Capture }` を Audio 内に定義する。
- `CoreAudioEndpointService` に流れを受け取るメソッドを足す。
  - `GetEndpoints(AudioFlow)`: 有効（DEVICE_STATE_ACTIVE）なデバイスの一覧
  - `GetDefault(AudioFlow)`: Multimedia ロールの既定。デバイスが無ければ null
  - `GetDefaults(AudioFlow)`: Console・Multimedia・Communications の順の既定。既定が無いロールは null（Issue rurute/inzone-auto-switch#14 で追加。Core の `IAudioSession.GetDefaults` が使う）
  - `SetDefault(AudioFlow, id)`: Console・Multimedia・Communications の順に変更する。**変更の前に、同じ流れの有効なデバイスの一覧に ID があるかを確かめ、無ければ何も変更せずに例外を投げる**（再生の ID で録音を変える、またはその逆の取り違えもここで止まる）。2つ目以降のロールで失敗したときに変更済みのロールをメッセージに含める扱いも再生と同じ
- 既存の `GetPlaybackEndpoints` / `GetDefaultPlayback` / `SetDefaultPlayback` は、`AudioFlow.Render` を渡す包みとして残す。段階1の契約と `tools/AudioSmoke` の既存コマンドを壊さない。再生の例外の文言も変えない。録音の例外の文言は、再生の文言の `playback` を `recording` に置き換えたものにする。

### Core（`AutoSwitch.Core`）

- `enum DeviceFlow { Playback, Capture }`
- `record AudioDevice(string Id, string Name)`（現在の `PlaybackDevice` を改名）
- `interface IAudioSession : IDisposable`（現在の `IPlaybackSession` を一般化）
  - `IReadOnlyList<AudioDevice> GetDevices(DeviceFlow flow)`
  - `IReadOnlyList<AudioDevice?> GetDefaults(DeviceFlow flow)`（Issue rurute/inzone-auto-switch#14 で `AudioDevice? GetDefault(DeviceFlow flow)` から変更。Console・Multimedia・Communications の順）
  - `void SetDefault(DeviceFlow flow, string id)`
- `interface IAudioSwitcher`（現在の `IPlaybackSwitcher` を一般化）
  - `SwitchOutcome SwitchTo(DeviceFlow flow, string deviceName)`
- `sealed class AudioSwitcher : IAudioSwitcher`（現在の `PlaybackSwitcher` を改名して一般化）。判定の順序は §4 のとおり1か所に置き、文言は流れで切り替える。
- `SwitchResult` と `SwitchOutcome` は変えない。
- `AppSettings` の末尾に `BudsInputDevice` と `SpeakerInputDevice` を足す。既定値は空文字。
- `SettingsLoader` に、空を受け入れる文字列の読み方を足す（§3）。
- `AutoSwitchEngine` は `IAudioSwitcher` を受け取り、§4 の規則で再生 → 録音の順に呼ぶ。

### App（`AutoSwitch.App`）

- `CoreAudioPlaybackSession` を `CoreAudioSession : IAudioSession` に改名し、`DeviceFlow` を `AudioFlow` に対応づける。
- `Program` の起動ログに §4 の「録音の切り替え:」の1行を足す。
- トレイの表示・メニュー・自動起動・終了処理は変えない。トレイが示すのは検知したヘッドセットの状態であり、切り替えの成否ではない（最小版 spec §2 のまま）。

### tools/AudioSmoke（実機確認用の開発ツール）

- `AudioSmoke capture list` / `AudioSmoke capture current` / `AudioSmoke capture set <id>` を足す。エンジンに組み込む前に、録音の操作だけを実機で確かめるため（最小版の段階1で再生を確かめたのと同じ使い方）。
- **`capture set <id>` は、既存の `set` と違ってツール側で ID を事前に確かめず、`SetDefault(AudioFlow.Capture, id)` へそのまま渡す。**サービスが投げた例外の文言をそのまま表示し、終了コード1で終わる。再生デバイスの ID を渡したときに、ツールの事前確認ではなく**サービス側の「同じ流れの一覧に ID があるか」の検証**が働くことを実機で確かめるため（spec レビュー F2）。
- 既存の `list` / `current` / `set` は変えない（事前確認と終了コードも含む）。
- 6ロールの読み取りには、最小版の実機確認（段階1〜4）で使ってきた読み取り専用の PowerShell スクリプトを `tools/ReadAudioRoles.ps1` としてリポジトリに置いて使う。`IMMDeviceEnumerator.GetDefaultAudioEndpoint` を Render/Capture × 3ロールの6通り呼ぶだけで、既定を変更しない。これまでは `.ai` 配下（git 管理外）にあり、レビューで中身を確かめられなかった（spec レビュー F2）。

## 6. 検証

### 自動テスト（Core、失敗するテストから書く）

- **切り替え（`AudioSwitcherTests`。`PlaybackSwitcherTests` を改名）**
  - 再生の既存ケースは、期待する文言も含めて変えずに通す（一般化で挙動が変わっていないことの根拠にする）
  - 録音: 切り替える、既に既定、見つからない、セッションを開けない、変更に失敗する
  - 流れの取り違えを防ぐ: フェイクは流れごとに別の一覧と既定を持つ。再生の一覧にだけある名前を録音で指定すると「見つからない」。`SetDefault` に正しい流れが渡る
  - 再生のセッションを開けなくても、録音は別に開いて試みる（セッションは流れごとに開く）
- **設定（`SettingsLoaderTests`）**: 2キーの読み込み、空と空白だけを警告なしで受け入れる、キーが無ければ空、文字列以外は警告して空、キー名の大文字小文字を区別しない、重複キーは後の値。既定ファイルの内容が変わらないことは既存のテストで守る
- **エンジン（`AutoSwitchEngineTests`）**
  - 接続・切断で再生 → 録音の順に呼ぶ（向きごとの切り替え先が渡る）
  - 録音の切り替え先が未設定なら録音を呼ばない（向きごとに独立）
  - `switchOnConnect` / `switchOnDisconnect` が false なら、その向きの再生も録音も呼ばない
  - 再生の結果が「見つからない」「操作できない（セッションを開けない）」「失敗」のいずれでも録音は呼ぶ。録音の結果も結果ごとのレベルでログに残る
  - Switcher が想定外の例外を投げた場合は、既存の契約どおりエンジンの外へ出て、録音は呼ばれない（既存テストを録音込みで保つ）
  - 起動時同期でも両方を切り替える
  - 同じ状態のイベントが続いても録音を切り替えない
  - 再生の切り替えのあとで終了が要求されていたら、録音を切り替えない（Tick と起動時同期の両方）

Audio の COM 操作と App の対応づけには自動テストを作らない（最小版 spec §6 と同じく実機で確かめる）。

### 実機確認

最小版と同じく、端末を変える操作は作者が行い、作業環境は読み取りと記録を行う。日常利用中の新版を止めて入れ替えるときは、`docs/operations-minimal-csharp.md` の「4. 更新」に従う。

1. **Audio 単体**: `AudioSmoke capture list / current / set` で録音を USB マイク ⇄ Buds - Chat に切り替え、録音の3ロールが変わり、再生の3ロールが変わらないことを `tools/ReadAudioRoles.ps1` の6ロールの読み取りで確かめる。再生デバイスの ID を `capture set` に渡すと、サービスの検証で例外になり（終了コード1）、6ロールが何も変わらないことを確かめる。**既存の `list` / `current` / `set` も実行し、再生の一覧・既定の表示、再生の切り替え、存在しない ID での終了コード1が一般化の前と変わらないことを確かめる**（Core の自動テストはフェイクを使うため、Audio の包みの流れの取り違えを捕まえられない。spec レビュー F2）。最後に元の状態へ戻す
2. **組み込み後**: 新版を入れ替え、`config.json` に2キーを書いて起動する。起動ログの「録音の切り替え:」の行を確かめる。Buds の出し入れで再生と録音の6ロールが両方向に切り替わること（音とマイクの確認は作者）、録音を手で変えたら次の遷移まで変わらないことを確かめる
3. **未設定のとき**: 2キーを空にして再起動し、出し入れしても録音の3ロールが変わらないことと、再生は従来どおり切り替わることを確かめる。最後は2キーを設定した状態で日常利用に戻す

### 完了条件

- 実機で、Buds の出し入れにより再生・録音の6ロールが両方向に期待どおり切り替わる
- 録音の切り替え先が未設定なら録音は変わらず、再生は従来どおり切り替わる
- 録音の手動変更を次の状態遷移まで尊重する
- 再生の挙動とログの文言が変わっていない（既存の自動テストがすべて通り、`AudioSmoke` の既存コマンドの実機での結果も変わらない）

## 7. 範囲外

- 録音だけを OFF にする専用の設定（利用場面がはっきりしないため初期では作らない。Issue rurute/inzone-auto-switch#8 に課題として保持。必要なら2キーを空にすれば実質的に止められる）
- 設定画面、再起動なしの反映（Issue rurute/inzone-auto-switch#9）
- 既定ファイルとリポジトリ直下の `config.json` へのキーの追加
- Windows 通知、デバイス ID による指定、切り替え先の自動推奨
- 再生と録音で別々の ON/OFF（`switchOnConnect` / `switchOnDisconnect` を流れごとに分けること）
- 旧版 PowerShell 版への録音の切り替えの追加
