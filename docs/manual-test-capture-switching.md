# 録音デバイスの切り替え — 手動確認記録

- 対象計画: [`superpowers/plans/2026-09-16-capture-device-switching.md`](superpowers/plans/2026-09-16-capture-device-switching.md)
- 対象 spec: [`superpowers/specs/2026-09-16-capture-device-switching-design.md`](superpowers/specs/2026-09-16-capture-device-switching-design.md)（Issue rurute/inzone-auto-switch#8）
- 記録方針: エンドポイント ID の GUID やユーザー名など、個人・端末固有の値はマスクする（`{0.0.0.00000000}.{xxxxxxxx-...-<末尾4桁>}`、`<user>`）。

## 環境

| 項目 | 値 |
|---|---|
| Windows | Microsoft Windows [Version 10.0.26200.9457] |
| 開発環境 | WSL2 上のリポジトリを Windows の `dotnet.exe` で UNC パス経由で実行 |
| .NET SDK（使用） | 10.0.300 |
| 対象のコード | `feat/capture-switching` の `1cdbd07`（Task 1: Audio 層で録音デバイスを扱えるようにする） |
| 新版（配置済みの AutoSwitch.App） | 日常利用のまま稼働（プロセス 1 件。確認の間は Buds をケースから出し入れしていない） |

## Task 2: Audio 単体の確認（2026-09-16）

`AudioSmoke` の `capture list / current / set` で録音の既定を USB マイク ⇄ Buds - Chat に切り替え、`tools/ReadAudioRoles.ps1` で 6 ロールを読み取った。あわせて、再生の ID を `capture set` に渡したときのサービスの検証と、既存の `list / current / set` が段階1と変わらないことを確かめた。

### 分担

- **作者**: 既定を変えるコマンド（`capture set` 3 回、`set` 3 回）の実行。
- **作業環境**: 読み取り（`list` / `current` / `capture list` / `capture current` / `ReadAudioRoles.ps1`、AutoSwitch.App のプロセス数）と記録。作者のコマンドは出力と終了コードをファイルに保存する形で実行してもらい、作業環境がそのファイルを読んだ。

### 手順

リポジトリルートで実行する（以下 `smoke` と書く）。Step 3 以降の作者のコマンドは、実行前に `test "$(git branch --show-current)" = feat/capture-switching` でブランチを確かめた（Step 2 のコマンドにはこの確認を付けていない。下の「経緯」を参照）。

```bash
inzone_repo_win=$(wslpath -w "$PWD")
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- <引数>
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '$inzone_repo_win\\tools\\ReadAudioRoles.ps1'"
```

使った ID（マスク済み）:

| 流れ | デバイス | ID |
|---|---|---|
| 再生 | スピーカー (Realtek(R) Audio) | `{0.0.0.00000000}.{xxxxxxxx-...-db01}` |
| 再生 | スピーカー (INZONE Buds - Game) | `{0.0.0.00000000}.{xxxxxxxx-...-c440}` |
| 録音 | ヘッドセット マイク (INZONE Buds - Chat) | `{0.0.1.00000000}.{xxxxxxxx-...-9066}` |
| 録音 | マイク (USB Microphone) | `{0.0.1.00000000}.{xxxxxxxx-...-4ee8}` |

### Step 1: 確認前の状態（作業環境、22:05）

| コマンド | 期待 | 結果 |
|---|---|---|
| `smoke list` | exit 0、段階1と同じ形式 | exit 0。5 件、既定は スピーカー (Realtek(R) Audio) |
| `smoke current` | exit 0 | exit 0。`Default playback:` は Realtek |
| `smoke capture list` | exit 0、録音 2 件（Buds - Chat と USB マイク） | exit 0。`Recording devices (active): 2`、既定（`*`）は Buds - Chat |
| `smoke capture current` | exit 0 | exit 0。`Default recording:` は Buds - Chat |
| AutoSwitch.App のプロセス数 | 記録 | 1 |

`capture list` の出力（ID はマスク）:

```text
Recording devices (active): 2
* id={0.0.1.00000000}.{xxxxxxxx-...-9066} name=ヘッドセット マイク (INZONE Buds - Chat)
  id={0.0.1.00000000}.{xxxxxxxx-...-4ee8} name=マイク (USB Microphone)
Default recording: id={0.0.1.00000000}.{xxxxxxxx-...-9066} name=ヘッドセット マイク (INZONE Buds - Chat)
```

ロールの分かれ方: 再生・録音とも 3 ロールがそろっていた（下の表の「Step 1」）。分かれている流れが無いため、Step 6 の復元に `mmsys.cpl` は不要と判定した。plan 作成時（19:09）の読み取りでは録音が分かれていたが、この時点ではそろっていた。

### Step 2〜5: 結果

| Step | 操作（作者） | 期待 | 結果 |
|---|---|---|---|
| 2 | `smoke capture set <USB マイク>` | exit 0、`Default recording:` が USB マイク。録音 3 ロール = USB マイク、再生 3 ロールは不変 | exit 0。stdout は `Requested: …-4ee8` / `Default recording: …-4ee8 name=マイク (USB Microphone)`、stderr 空。6 ロールは期待どおり |
| 3 | `smoke capture set <Buds - Chat>` | exit 0。録音 3 ロール = Buds - Chat、再生 3 ロールは不変 | exit 0。stdout は `Requested: …-9066` / `Default recording: …-9066 name=ヘッドセット マイク (INZONE Buds - Chat)`、stderr 空。6 ロールは期待どおり |
| 4 | `smoke capture set <Realtek（再生の既定）>` | exit 1。stderr に `No active recording device has id=` と `the default recording device was not changed`（ツールの事前確認の文言ではない）。6 ロールは Step 3 のあとと同じ | exit 1。stdout 空。stderr は `error: No active recording device has id={0.0.0.00000000}.{xxxxxxxx-...-db01}; the default recording device was not changed (HRESULT 0x80070490)`。6 ロールは不変 |
| 5 | （作業環境）`smoke list` / `smoke current` | exit 0、Step 1 と同じ形式 | どちらも exit 0、stderr 空。stdout は Step 1 とバイト単位で一致 |
| 5 | `smoke set <Buds - Game>` | exit 0。再生 3 ロール = Buds - Game、録音 3 ロールは不変 | exit 0。stdout は `Requested: …-c440` / `Default playback: …-c440 name=スピーカー (INZONE Buds - Game)`、stderr 空。6 ロールは期待どおり |
| 5 | `smoke set <Realtek>` | exit 0。再生 3 ロール = Realtek、録音 3 ロールは不変 | exit 0。stdout は `Requested: …-db01` / `Default playback: …-db01 name=スピーカー (Realtek(R) Audio)`、stderr 空。6 ロールは期待どおり |
| 5 | `smoke set no-such-id` | exit 1、stderr が `error: no active playback device has id=no-such-id (run 'list' to see the IDs)` | exit 1。stdout 空。stderr はその 1 行（段階1と同じ）。6 ロールは不変 |

Step 2・3 の stdout が使い方の表示ではなく `Requested:` / `Default recording:` であることから、`capture` コマンドを持つ Task 1 のコードで動いたことを確かめた。

### 6 ロールの推移（作業環境の読み取り）

| 時点 | Render Console | Render Multimedia | Render Communications | Capture Console | Capture Multimedia | Capture Communications |
|---|---|---|---|---|---|---|
| Step 1（確認前、22:05） | Realtek | Realtek | Realtek | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 2 `capture set` USB マイク（22:20） | Realtek | Realtek | Realtek | **USB マイク** | **USB マイク** | **USB マイク** |
| Step 3 `capture set` Buds - Chat（22:21） | Realtek | Realtek | Realtek | **Buds - Chat** | **Buds - Chat** | **Buds - Chat** |
| Step 4 `capture set` 再生の ID（22:23） | Realtek | Realtek | Realtek | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 5 `set` Buds - Game（22:24） | **Buds - Game** | **Buds - Game** | **Buds - Game** | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 5 `set` Realtek（22:26） | **Realtek** | **Realtek** | **Realtek** | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 5 `set no-such-id`（22:27） | Realtek | Realtek | Realtek | Buds - Chat | Buds - Chat | Buds - Chat |

`capture set` は録音の 3 ロールだけを、`set` は再生の 3 ロールだけをまとめて変更した。流れの取り違え（録音の操作で再生が変わる、またはその逆）は無かった。

### Step 6: 元の状態へ戻す

Step 3 で録音が、Step 5 の 2 回目の `set` で再生が確認前のデバイスへ戻ったため、作者による復元操作は行っていない。作業環境が 22:27 に `ReadAudioRoles.ps1` で読み取り、6 ロールがロールごとに Step 1 と一致すること（タイムスタンプ行を除いて差分なし）を確かめた。`smoke list` と `smoke capture list` の出力も Step 1 と一致した。AutoSwitch.App のプロセスは 1 件のまま。

### 経緯

- 22:12:59〜22:16:38 に、別のターミナルでこのリポジトリが `feat/tray-icon` へ切り替えられ、作業ツリーが一時的に Task 1 前の状態になっていた。22:16:38 に `feat/capture-switching`（`1cdbd07`）へ戻った。戻ったことと Task 1 の 5 ファイルが `1cdbd07` と一致することは、作業を取りまとめる側（ORCHESTRATOR）が git reflog と `git diff` で確かめた。Step 1 の読み取り（22:05）は切り替えより前に行った。Step 2 の作者の実行（22:19:57〜59）は戻ったあとで、コマンドにブランチの確認は付けていない。Step 2 が Task 1 のコードで動いたことは、stdout が使い方の表示ではなく `Requested:` / `Default recording:` だったことと、上の ORCHESTRATOR の確認による。Step 3 以降の作者のコマンドは、実行前にブランチを確かめた。

### 未確認の項目

- 音とマイクを作者が実際に聞いて・話して確かめることは、この Task では行っていない（組み込み後の確認で行う）。
- Windows の「サウンドの設定」や `mmsys.cpl` の画面での照合は行っていない（6 ロールは `ReadAudioRoles.ps1` の読み取りで確かめた）。
- 3 ロールが分かれた状態からの `capture set` は試していない（確認前の状態がそろっていたため）。
- エンジンへの組み込み後の動作（Buds の出し入れによる切り替え、未設定のとき）は、plan Task 7 で確かめる。

## Task 7: 組み込み後の確認（2026-09-16）

新版（Task 6 までを含む `feat/capture-switching` の `6759ec0`）を日常利用の配置先へ入れ替え、Buds の出し入れで再生と録音の 6 ロールが両方向に切り替わること、録音の手動変更を次の状態遷移まで尊重すること、録音の切り替え先が未設定（キーが無い・値が空）のときは録音が変わらず再生は従来どおり切り替わることを確かめた。最後は 2 キーを設定した状態で新版を起動したまま、日常利用に戻した。

### 環境

| 項目 | 値 |
|---|---|
| Windows | Task 2 と同じ |
| 対象のコード | `feat/capture-switching` の `6759ec0`（Task 6: 起動ログと運用手順） |
| 配置 | `scripts/Install-TrayApp.ps1`（Release の publish）で `C:\Users\<user>\AppData\Local\AutoSwitchForInzone\bin` へ配置 |
| 設定 | `C:\Users\<user>\AppData\Local\AutoSwitchForInzone\config.json`（`logLevel` = `Info`、`startupSyncMaxAgeMinutes` = 10） |
| 旧版 PowerShell 版 | スケジュールタスクは `Disabled`、プロセス 0 件（Step 1 と Step 8 で読み取り） |

### 分担

- **作者**: 新版の終了・配置スクリプトの実行・起動、`config.json` の編集、Buds のケースからの出し入れ、録音の既定の手動変更と `AudioSmoke capture set`、音とマイク入力の確認。作者のコマンドは、ブランチの確認を付け、出力・終了コード・時刻を保存する形で実行した。
- **作業環境**: 読み取り（AutoSwitch.App のプロセス、Run キーの値、`config.json` の内容と MD5、`tools/ReadAudioRoles.ps1` の 6 ロール、ログ、`bin` の更新時刻、旧版の状態）と記録。作業環境から端末への書き込みは無い。
- 作者の操作の前に時刻を記録し、ログの行はその時刻以降で切り出した。1 回の出し入れは「出す → 読み取り → 戻す → 読み取り」と 1 操作ごとに区切った。

### 作者への事前の周知と了承

確認の前に、作者へ次の内容を伝えた。

- この機能は録音の 3 ロール（Console・Multimedia・Communications）をまとめて切り替える。
- 確認の途中と確認後の日常利用で、Buds を出すと 3 ロールとも Buds - Chat に、戻すと 3 ロールとも USB マイク に上書きされる。
- 確認の最後に、確認前の状態（Buds をケースに入れたまま録音が Buds - Chat）へは戻さない。

作者の返答（原文）は「了承します、そのまま進めて」。この返答は Step 2（入れ替え）の起動の後に届き、作者は周知を読んだうえで先に Step 2 を実行していた。この順序についても作者は「Step2も了承」と返答した。Step 2 は録音を切り替えない（キーが未設定の）状態での入れ替えで、周知の対象の変更は起きていない（Step 2 の 6 ロールは Step 1 と同じ）。

### plan からずらした点（経緯と理由）

1. **Step 4 で、起動の前に録音を USB マイク にそろえた。** Step 3 はキーが未設定なので、録音は確認前の Buds - Chat のまま残る。そのまま Step 4 で再起動すると、Step 5 で Buds を出したときに録音は「既に既定」（Debug で、`logLevel` = `Info` のログには出ない）となり、plan Step 5 が期待する `既定の録音デバイスを切り替えました: ヘッドセット マイク (INZONE Buds - Chat)` が出ない。一方、Step 3 の最後のイベントから 10 分以内に起動すると、起動時同期が録音を USB マイク へ切り替え、plan に無い行が Step 4 の起動ログに加わる。どちらになるかが経過時間に左右されるため、Step 4 では「終了 → プロセス数 0 の確認 → 作者が `AudioSmoke capture set <USB マイク>` で録音の 3 ロールをそろえる → `config.json` の編集 → 起動」の順にし、Step 4 の期待に「`既定の録音デバイスを切り替えました` は出ない」を加えた。アプリが止まっている間の変更なので、アプリの判定には影響しない。
2. **Step 3・Step 5〜7 の出し入れは 1 操作ごとに区切った。** Render が切り替わったことは Buds を出した直後にしか読み取れないため。
3. **Step 6 の手動変更は、3 ロールがそろうまで操作を足した。** 下の「Step 6」を参照。

### Step 1: 確認前の状態（作業環境、23:10）

| 項目 | 値 |
|---|---|
| AutoSwitch.App のプロセス | 1 件（17:40:30 起動、段階4で配置した版） |
| Run キー `AutoSwitchForInzone` | `"C:\Users\<user>\AppData\Local\AutoSwitchForInzone\bin\AutoSwitch.App.exe"` |
| `config.json` | 381 バイト、MD5 `775312E154F254EA6B3DFC87318FC9B4`（段階4の記録と同じ）。`budsInputDevice` / `speakerInputDevice` は無い |
| 6 ロール | Render 3 = Realtek、Capture 3 = Buds - Chat（そろっている） |
| ログ | `[WARN]` 0 行、`[ERROR]` 0 行。最後の状態遷移は 17:43:03 の Disconnected |

### Step 2〜8: 結果

| Step | 操作（作者） | 期待 | 結果（作業環境の読み取り） |
|---|---|---|---|
| 2 | トレイの「終了」→ プロセス数 0（23:16:17）→ `Install-TrayApp.ps1`（exit 0）→ 起動（23:16:37） | `bin` が更新される。起動ログに `録音の切り替え: 接続時=（未設定） / 切断時=（未設定）` | `bin` の dll/exe は 23:16:19〜20 に更新。プロセス 1 件（新しい PID）。起動ログは期待の行と `起動時同期: 最終イベントが古すぎます（333.6 分前）。切り替えません。`。Run キー・`config.json` の MD5・6 ロールは Step 1 と同じ |
| 3 | Buds を出す（23:17:57） | 再生の切り替えの行は出て、録音の行は出ない。Render 3 = Buds - Game、Capture 3 は Step 1 のまま | `状態遷移を検知しました: Connected`、`既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)` の 2 行だけ。Render 3 = Buds - Game、Capture 3 = Buds - Chat |
| 3 | Buds を戻す（23:19:12） | 同上。Render 3 = Realtek、Capture 3 は Step 1 のまま | `Disconnected`、`既定の再生デバイスを切り替えました: スピーカー (Realtek(R) Audio)` の 2 行だけ。Render 3 = Realtek、Capture 3 = Buds - Chat（USB マイク にならない） |
| 4 | 終了 → プロセス数 0（23:20:54）→ `AudioSmoke capture set <USB マイク>`（exit 0）→ `config.json` に 2 キーを追記 → 起動（23:22:50） | 起動ログに `録音の切り替え: 接続時=ヘッドセット マイク (INZONE Buds - Chat) / 切断時=マイク (USB Microphone)`。`[WARN]` なし。`既定の録音デバイスを切り替えました` は出ない | 期待の行が出た。`[WARN]` なし。切り替えの行は出ず、`起動時同期: 現在の状態を Disconnected と判定しました。`（再生・録音とも既に既定）。`config.json` の差分は 2 キーの追加（と直前の行のカンマ）だけで、MD5 は `AC269A4D3DA101351F6580C471201943`。Render 3 = Realtek、Capture 3 = USB マイク |
| 5 | Buds を出す（23:24:04） | `Connected` → 再生 Buds - Game → 録音 Buds - Chat の順。Render 3 = Buds - Game、Capture 3 = Buds - Chat | 23:24:15 に 3 行がこの順で出た。6 ロールは期待どおり。ほかに 23:24:19 の `ActionLog.log の truncate を検知しました。先頭から読み直します。`（下の「観察」を参照） |
| 5 | Buds を戻す（23:25:36） | `Disconnected` → 再生 Realtek → 録音 USB マイク の順。Render 3 = Realtek、Capture 3 = USB マイク | 23:25:43 に 3 行がこの順で出た（ほかの行なし）。6 ロールは期待どおり |
| 6 | ケースに入れたまま、録音を手で Buds - Chat に変える（23:27:03、23:31:22） | 新版が戻さない（ログに行が増えず、Capture は手で変えたまま） | 23:27:03〜23:37:05 の約 10 分間、ログの行は 0。Capture は手で変えたとおりのまま（下の「Step 6」を参照） |
| 6 | Buds を出す（23:36:56） | 再生の切り替え。録音は既に既定なので行は出ない | `Connected`、`既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)` の 2 行だけ。Render 3 = Buds - Game、Capture 3 = Buds - Chat |
| 6 | Buds を戻す（23:37:56） | 次の遷移で Capture が Buds - Chat → USB マイク | `Disconnected` → 再生 Realtek → `既定の録音デバイスを切り替えました: マイク (USB Microphone)`。Render 3 = Realtek、Capture 3 = USB マイク |
| 7 | 終了 → プロセス数 0（23:39:34）→ 2 キーの値を `""` にする → 起動（23:40:44） | 起動ログに `録音の切り替え: 接続時=（未設定） / 切断時=（未設定）`。`[WARN]` なし | 期待の行が出た。`[WARN]` なし。`起動時同期: 現在の状態を Disconnected と判定しました。`（切り替えの行なし）。`config.json` の差分は 2 つの値だけで、MD5 は `9FC939669EE04510ECD5EAB22BE0F976` |
| 7 | Buds を出す（23:41:39） | 再生の切り替えの行は出て、録音の行は出ない。Capture 3 は空にする直前（USB マイク）のまま | `Connected`、再生 Buds - Game の 2 行だけ。Render 3 = Buds - Game、**Capture 3 = USB マイク**（Step 5 ではこの接続で Buds - Chat へ切り替わった） |
| 7 | Buds を戻す（23:42:45） | 同上。Render 3 = Realtek、Capture 3 = USB マイク | `Disconnected`、再生 Realtek の 2 行だけ。Render 3 = Realtek、Capture 3 = USB マイク |
| 7 | 終了 → プロセス数 0（23:44:16）→ 2 キーを Step 4 の値へ戻す → 起動（23:45:08） | `録音の切り替え:` の行が Step 4 と同じ | 同じ行が出た。`[WARN]` なし。`config.json` は Step 4 の後とバイト単位で一致（MD5 `AC269A4D3DA101351F6580C471201943`） |
| 8 | （作業環境のみ）23:45:38 | 確認の全期間で `[ERROR]` 0 行。`[WARN]` の有無 | 23:10:46〜23:45:38 のログ 44 行はすべて `[INFO]`。`[ERROR]` 0 行、`[WARN]` 0 行（ログファイル全体でも 0）。プロセス 1 件、Run キーは Step 1 と同じ、旧版は `Disabled`・プロセス 0 件 |

Step 3（キーが無い）と Step 7（値が空）の両方で、録音の切り替え先が未設定のときに録音が変わらないことを確かめた。Step 7 では、Capture が接続時の切り替え先（Buds - Chat）と違う USB マイク の状態で出し入れしたので、録音を切り替えなかったことを 6 ロールでも区別できた。

### 6 ロールの推移（作業環境の読み取り）

| 時点 | Render 3 ロール | Capture Console | Capture Multimedia | Capture Communications |
|---|---|---|---|---|
| Step 1（23:10） | Realtek | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 2 入れ替え後（23:16） | Realtek | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 3 出す（23:18） | **Buds - Game** | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 3 戻す（23:19） | **Realtek** | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 4 再起動後（23:23） | Realtek | **USB マイク** | **USB マイク** | **USB マイク** |
| Step 5 出す（23:24） | **Buds - Game** | **Buds - Chat** | **Buds - Chat** | **Buds - Chat** |
| Step 5 戻す（23:26） | **Realtek** | **USB マイク** | **USB マイク** | **USB マイク** |
| Step 6 設定アプリで変更後（23:28、23:30） | Realtek | **Buds - Chat** | **Buds - Chat** | USB マイク |
| Step 6 `mmsys.cpl` で通信デバイスも変更後（23:34、23:36） | Realtek | Buds - Chat | Buds - Chat | **Buds - Chat** |
| Step 6 出す（23:37） | **Buds - Game** | Buds - Chat | Buds - Chat | Buds - Chat |
| Step 6 戻す（23:38） | **Realtek** | **USB マイク** | **USB マイク** | **USB マイク** |
| Step 7 空で再起動後（23:40） | Realtek | USB マイク | USB マイク | USB マイク |
| Step 7 出す（23:42） | **Buds - Game** | USB マイク | USB マイク | USB マイク |
| Step 7 戻す（23:43） | **Realtek** | USB マイク | USB マイク | USB マイク |
| Step 7 値を戻して再起動後（23:45） | Realtek | USB マイク | USB マイク | USB マイク |

ID の末尾 4 桁は Task 2 の表と同じ（Realtek `db01`、Buds - Game `c440`、Buds - Chat `9066`、USB マイク `4ee8`）。太字は直前の読み取りから変わったロール。

### Step 6: 手動変更の手段と 3 ロールのそろい方

- 作者が「設定 > システム > サウンド > 入力」で録音を Buds - Chat に変えた（23:27:03、作者の報告「設定から変えた、1分待った。」）。**変わったのは Capture の Console と Multimedia だけで、Communications は USB マイク のまま残った。** 3 ロールがそろっていないので、ここで止めた。
- この分かれた状態の間も（23:28:40 と 23:30:03 の 2 回の読み取り）、新版はログに何も書かず、6 ロールも変わらなかった。
- plan Step 6 は 3 ロールとも Buds - Chat の状態を前提にしているため、作者が `mmsys.cpl` の「録音」タブで Buds - Chat を「既定の通信デバイスとして設定」にした（23:31:22、作者の報告「設定した」「1分待った」）。報告を受けてから 1 分以上後（23:34:47）と、さらに約 80 秒後（23:36:11）に読み取り、3 ロールがそろっていることと、ログの行が 0 のままであることを確かめてから出し入れへ進んだ。
- 分かれた状態のまま Buds を出していたら、既定の判定は Multimedia だけで行うため（spec §4 の判定の順序 1。再生も同じ）録音は「既に既定」となり、Communications は USB マイク のまま残ったと考えられる。これはコードからの予測で、実機では試していない。

### 作者確認

| 時点 | 確認した内容 | 作者の報告（原文） |
|---|---|---|
| Step 5 出す | 音が Buds から出てスピーカーからは出ないこと。「設定 > システム > サウンド > 入力」の入力デバイスが Buds - Chat で、話すと入力の音量バーが動くこと | 「どちらも想定通り」 |
| Step 5 戻す | 音が PC のスピーカーから出ること。入力デバイスが USB マイク で、話すと入力の音量バーが動くこと | 「どちらも想定通り」 |

### 観察

- **INZONE Hub による `ActionLog.log` の作り直し。** Step 5 で Buds を出した直後（23:24:19）に `[INFO] ActionLog.log の truncate を検知しました。先頭から読み直します。` が出た。plan に記載の無い行だが、最小版の監視の既存の挙動で、段階2の実機確認でも観察している（[manual-test-minimal-csharp.md](manual-test-minimal-csharp.md)）。この後に状態遷移や切り替えの行は増えておらず、次の出し入れも正しく検知された。
- **起動時同期。** Step 4・Step 7 の 2 回の再起動は、直前の状態遷移から 10 分以内だったため、どちらも `起動時同期: 現在の状態を Disconnected と判定しました。` となった。再生も録音も既に既定か未設定だったため、切り替えの行は出ていない。
- **`config.json` の扱い。** 配置の前後（Step 1・Step 2）で MD5 は変わらなかった。変わったのは作者が編集した Step 4・Step 7 だけで、最後は Step 4 の後とバイト単位で同じ内容に戻った。Run キーの値は確認の全期間を通じて変わらなかった。

### 未確認の項目

- 「既に既定」の結果（Debug）は、`logLevel` = `Info` のため実機のログでは観測していない。録音の行が出ないことと 6 ロールで代替した。
- 録音の「見つからない」「操作できない」「失敗」の結果と、再生と録音の独立（片方が失敗してももう片方を試みる）は、実機では発生させていない（Core の自動テストで確かめている）。
- 再生の切り替えのあとで終了が要求されたときに録音を切り替えないこと（終了の契約）は、実機では狙って起こしていない（Core の自動テストで確かめている）。
- 起動時同期で録音が実際に切り替わる場合（Step 4 では、起動前に録音をそろえてこの場合を避けた）。
- `switchOnConnect` / `switchOnDisconnect` を `false` にした向き、片方のキーだけを設定した場合、録音デバイスの名前の大文字小文字の違い。
- 3 ロールが分かれた状態から Buds を出し入れしたときの挙動（上の「Step 6」のとおり、コードからの予測だけ）。
- `mmsys.cpl` の「既定の通信デバイスとして設定」が Console・Multimedia を変えないこと。変更の前から Console・Multimedia は Buds - Chat だったため、区別できていない。
- 音とマイクの作者確認は Step 5 の 2 回だけで、Step 6・Step 7 では行っていない。
- スリープ復帰とサインアウト・サインインをまたいだ動作は、この Task では確かめていない。
