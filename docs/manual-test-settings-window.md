# 設定画面と再起動なしの反映 — 手動確認記録

- 対象 spec: [`superpowers/specs/2026-09-17-settings-window-design.md`](superpowers/specs/2026-09-17-settings-window-design.md)（Issue rurute/inzone-auto-switch#9）§8「実機確認」
- 記録方針: エンドポイント ID の GUID やユーザー名など、個人・端末固有の値はマスクする（`{0.0.0.00000000}.{xxxxxxxx-...-<末尾4桁>}`、`<user>`）。INZONE Hub の ActionLog.log の内容はシリアル番号を含むため、時刻と種類だけを書く。

## Task 5: 実機で設定画面を確かめ、日常利用に戻す（2026-09-17）

新版（`feat/settings-window` の `d0f8248`）を日常利用の配置先へ入れ替え、設定画面を開く・6項目を保存する・保存を再起動なしで反映する・キャンセル・保存の失敗・開いた後の外部の編集・設定画面を開いたままの終了を確かめた。最後は `config.json` を確認前のバイト列へ戻し、新版を起動した状態で日常利用に戻した。

### 環境

| 項目 | 値 |
|---|---|
| Windows | Microsoft Windows [Version 10.0.26200.9457] |
| .NET SDK（配置の publish に使用） | 10.0.300 |
| 対象のコード | `feat/settings-window` の `d0f8248`（Task 4: トレイから設定画面を開き、保存を再起動なしで反映する） |
| 配置 | worktree の `scripts/Install-TrayApp.ps1`（Release の publish）で `C:\Users\<user>\AppData\Local\AutoSwitchForInzone\bin` へ配置 |
| 設定 | `C:\Users\<user>\AppData\Local\AutoSwitchForInzone\config.json`（`logLevel` = `Info`、`startupSyncMaxAgeMinutes` = 10） |

使ったデバイス（ID はマスク済み。末尾4桁は [録音の切り替えの記録](manual-test-capture-switching.md) と同じ）:

| 流れ | デバイス | ID |
|---|---|---|
| 再生 | スピーカー (Realtek(R) Audio) | `{0.0.0.00000000}.{xxxxxxxx-...-db01}` |
| 再生 | スピーカー (INZONE Buds - Game) | `{0.0.0.00000000}.{xxxxxxxx-...-c440}` |
| 録音 | ヘッドセット マイク (INZONE Buds - Chat) | `{0.0.1.00000000}.{xxxxxxxx-...-9066}` |
| 録音 | マイク (USB Microphone) | `{0.0.1.00000000}.{xxxxxxxx-...-4ee8}` |

### 分担

この確認では、端末を変える操作を次の3者で区別して記録する。

- **作者**: 設定画面の操作（開く・選ぶ・保存・キャンセル・×）、トレイの「終了」、メモ帳での `config.json` の書き換え、`mmsys.cpl` での既定の変更、Buds のケースからの出し入れ、音の確認。作者は操作の結果と時刻を短い文で報告した（原文は下の表に引用する）。
- **ORCHESTRATOR（作者の依頼による実行）**: 作者が「こちらで実行して」と依頼したコマンドを、作業を取りまとめる側（ORCHESTRATOR）が `powershell.exe` で実行した。`config.json` の退避、配置と起動（終了コードを待つ形）、`attrib +R` / `attrib -R`、メモ帳を開くこと、Step 10 の復元・退避ファイルの削除・起動。
- **作業環境**: 読み取りだけ（AutoSwitch.App のプロセス数、`config.json` の内容・MD5・更新日時・属性、`config.json.tmp` と退避ファイルの有無、`bin` の更新時刻、Run キーの値、ログ、`tools/ReadAudioRoles.ps1` の6ロール、有効なデバイスの一覧）と記録。作業環境から端末への書き込みは無い。

有効なデバイスの一覧は `ReadAudioRoles.ps1` では読めないため、`EnumAudioEndpoints`（`DEVICE_STATE_ACTIVE`）で名前を列挙するだけの読み取り専用の PowerShell を使った。Step 5 の後の「音が出ない」件の切り分けでは、同じく読み取りだけで、エンドポイントの状態・ミュート・音量・ピーク値と、エンドポイントごとの音声セッションを読んだ。

作者の報告を受けてから作業環境が読み取り、期待と照合してから次の操作へ進んだ。Buds の出し入れ（移動）は、Step 5 の 7. の「戻す」と「出す」を除き、1回ごとに区切って移動の直後に6ロールを読み取った（下の「plan からずらした点」8）。設定画面の操作（Step 3・6 など）は、Step の区切りごとにまとめて作者の報告を受けた。

### 作者への事前の周知と了承

Step 1 で、作者へ次の内容を伝えた。

- Step 5 では切り替え先と ON/OFF を一時的に変え、既定のデバイスを `mmsys.cpl` で手で変えてから Buds を出し入れするので、その間は音とマイクがふだんと違うデバイスへ切り替わる。
- `config.json` は Step 1 で退避したファイルで Step 10 に元のバイト列へ戻す。
- 途中で止めるときも、Step 10 の手順で元へ戻す。

作者の返答（原文）は「了承して待避した」。

### plan からずらした点（経緯と理由）

1. **Step 1 の退避は ORCHESTRATOR が実行した。** 作者は退避のコマンドを会話に貼り付けたが、作業環境の読み取り（09:53:51、09:54:29）と ORCHESTRATOR の読み取り（09:55:23）では退避ファイルができていなかった。作者がコマンドの実行を求めていると解釈し、09:55:31 に ORCHESTRATOR が `Copy-Item` を実行した。作業環境は退避ファイルの MD5 が `config.json` と一致することを確かめた（09:55:48）。
2. **Step 2 の配置と起動は ORCHESTRATOR が実行し、終了コードは待機するプロセスが書くファイルで読んだ。** 作者の依頼（「こちらで実行して」）による。ORCHESTRATOR は 12:16:51〜55 に worktree（HEAD `d0f8248`）の `Install-TrayApp.ps1` を実行し（exit 0）、12:17:40 に `Start-Process -PassThru` で起動した。同じ PowerShell が `WaitForExit` で待ち、終了したら終了コードをファイルに書く形にした。Step 9 の終了コードは、plan の「作者の PowerShell の `$autoSwitch.ExitCode`」ではなく、このファイルで読んだ。
3. **Step 4（未接続の表示）は作者の判断で省いた。** Buds をケースに入れても `スピーカー (INZONE Buds - Game)` と `ヘッドセット マイク (INZONE Buds - Chat)` は有効なデバイスの一覧に残る（Step 3 の読み取り）。「（未接続）」を表示させるには USB ドングルを抜く必要があり、作者が省くことを選んだ（原文「省く」）。代わりの根拠は Core の自動テスト `SettingsFormModelTests.Create_ConfiguredPlaybackNotInList_AddsNotConnectedChoiceFirstAndSelectsIt` と `Create_ConfiguredCaptureNotInList_OrderIsNotConnectedThenNotSetThenDevices`。
4. **Step 5 の 5.（切断時の新しい切り替え先）で、`mmsys.cpl` の再生タブの操作を省いた。** 直前の接続（Buds を出す）で、新しい接続時の再生先（Realtek）へアプリが切り替え、Render の3ロールがすでに Realtek にそろっていたため。作者は録音タブで Buds - Chat を「既定のデバイス」「既定の通信デバイス」にだけ設定した。
5. **Step 7 の「動作中の設定が変わっていない」確認の事前の既定は、`mmsys.cpl` ではなく Buds をケースへ戻して作った。** plan は「Buds をケースに入れた状態で、作者が `mmsys.cpl` で再生の既定を元の接続時の再生先以外にする」。確認の時点で Buds は出ていたので、ケースへ戻して元の切断時の設定でアプリに Render を Realtek へ切り替えさせ、Render 3 = Realtek を作業環境が読み取ってから Buds を出した。期待するログと判定は plan のまま。ORCHESTRATOR が承認した。
6. **Step 7 の `attrib +R` / `attrib -R` と、Step 8 でメモ帳を開くことは ORCHESTRATOR が実行した。** いずれも作者の依頼による。Step 8 の `logLevel` の書き換えと保存は作者がメモ帳で手で行った。
7. **Step 10 の 1〜4（プロセス数0の確認、`attrib -R`、退避ファイルでの上書き、MD5 の確認、退避ファイルの削除、起動）は ORCHESTRATOR が実行した。** 作者の依頼による。退避ファイルの削除は、ORCHESTRATOR が MD5 の一致を確かめてから行った。作業環境の独立した MD5 の照合は削除の後（14:06:57）で、そのときも Step 1 と一致した。起動は日常利用に戻すため `Start-Process` だけで行った（終了を待たない）。
8. **Step 5 の 7. の「戻す」と「出す」は、作者が続けて行い、まとめて報告した（「戻した13:52 出した 13:53」）。** plan の「移動のたびに、作業環境は移動の直前と直後の6ロールを読み取る」から外れ、切断（13:52:34）の直後の6ロールは読み取っていない。作業環境の読み取りは接続の後（13:53:37）だけである。そのため、この切断の判定（元の切断時の切り替え先が直前から既定で、切り替えの行が出ない）はログだけによる。作者にまとめて頼む形は ORCHESTRATOR が指示した（13:51 ごろの作業環境への指示「B と C は…まとめて1回で頼んで構いません（どちらも期待する結果が決まっていて、途中で作者の手作業が要らないため）」）。作業環境はこの指示に沿って、戻す・出すを続けて行う作者への文面を作った。元の6項目どおりの切断時の切り替え（再生 Realtek・録音 USB マイク へ切り替わり、6ロールもそうなる）は、Step 7 の「戻す」（13:58:51）と Step 10 の「戻す」（14:08:24）で6ロールも含めて確かめている。

### 経緯

- 作者は 09:53:17 に Buds をケースへ戻し、09:53:23 にトレイの「終了」で旧い配置の新版を終了していた（Step 1 の退避を確かめる前）。作者から、これが Step 2 のつもりだったかの明示の返答は無い。その後の作者の依頼で ORCHESTRATOR が Step 2 を実行した。`config.json` はこの間変わっていない（MD5 は Step 1 と同じ）。
- アプリが止まっていた 09:53〜12:17 の間に Buds が使われ（INZONE Hub の ActionLog.log の最後の切断は 10:35:30）、Step 2 の後の読み取り（12:18）では Render Console・Multimedia = Buds - Game、Render Communications = Realtek と分かれていた。Step 5 の 5. でアプリの切り替えにより3ロールがそろうまで、この分かれた状態のまま進めた（Step 3〜5-4 の判定は「6ロールが変わらない」ことなので、分かれていても判定できた）。

### Step 1: 確認前の状態（作業環境、09:36〜09:37）

| 項目 | 値 |
|---|---|
| AutoSwitch.App のプロセス | 1 件（08:25:27 起動） |
| `bin` の更新 | 08:24:32（Task 5 の配置の前） |
| Run キー `AutoSwitchForInzone` | `"C:\Users\<user>\AppData\Local\AutoSwitchForInzone\bin\AutoSwitch.App.exe"` |
| `config.json` | 515 バイト、MD5 `AC269A4D3DA101351F6580C471201943`、更新 2026-09-16 23:44:40、作成 2026-09-16 08:27:33、属性 Archive。`config.json.tmp` と退避ファイルは無い |
| 6 ロール | Render 3 = Buds - Game、Capture 3 = Buds - Chat（そろっている） |
| ログ | 125 行、`[WARN]` 0 行、`[ERROR]` 0 行。最後の状態遷移は 09:28:23 の Connected |

元の6項目（plan の前提の表と6つとも一致）:

| キー | 値 |
|---|---|
| `budsDevice` | `スピーカー (INZONE Buds - Game)` |
| `budsInputDevice` | `ヘッドセット マイク (INZONE Buds - Chat)` |
| `speakerDevice` | `スピーカー (Realtek(R) Audio)` |
| `speakerInputDevice` | `マイク (USB Microphone)` |
| `switchOnConnect` | `true` |
| `switchOnDisconnect` | `true` |

有効なデバイス（`DEVICE_STATE_ACTIVE`、列挙の順）: 再生は Realtek、`3 - REALTEK (2- AMD High Definition Audio Device)`、`1 - AW3423DWF (2- AMD High Definition Audio Device)`、`ヘッドセット イヤフォン (INZONE Buds - Chat)`、Buds - Game。録音は Buds - Chat、USB マイク。前提の Buds - Chat・USB マイク（録音）と Realtek（再生）はそろっていた。`config.json` の6項目以外のキーの順は `budsDevice`、`speakerDevice`、`switchOnConnect`、`switchOnDisconnect`、`actionLogPath`、`pollIntervalSeconds`、`idlePollIntervalSeconds`、`startupSyncMaxAgeMinutes`、`logLevel`、`maxLogSizeKB`、`budsInputDevice`、`speakerInputDevice`。

### Step 2〜10: 結果

「実行者」は、作者の操作か、作者の依頼で ORCHESTRATOR が実行したものかを表す。「作者の報告」は原文。「結果」は作業環境の読み取り。

| Step | 操作（実行者） | 作者の報告 | 期待 | 結果（作業環境の読み取り） |
|---|---|---|---|---|
| 1 | `config.json` を退避（ORCHESTRATOR、09:55:31） | 「了承して待避した」 | 退避ファイルの MD5 が `config.json` と一致 | 09:55:48 に退避ファイル 515 バイト、MD5 `AC269A4D…` で一致 |
| 2 | プロセス数0の確認（09:56:16）→ `Install-TrayApp.ps1`（exit 0、12:16:51〜55）→ 起動（12:17:40）（ORCHESTRATOR） | 「こちらで実行して」 | `bin` が更新される。起動後のログに `[WARN]`・`[ERROR]` が無い | `bin` の9ファイルが 12:16:54〜55 に更新。プロセス 1 件（新しい PID）。起動ログは開始・監視対象・`録音の切り替え: 接続時=ヘッドセット マイク (INZONE Buds - Chat) / 切断時=マイク (USB Microphone)`・`自動起動: 有効`・`起動時同期: 最終イベントが古すぎます（102.2 分前）。切り替えません。`。`[WARN]`・`[ERROR]` 0。`config.json` の MD5 と Run キーは Step 1 と同じ |
| 3 | メニューで開く、開いたままメニュー・ダブルクリック、キャンセル → ダブルクリックで開く → キャンセル（作者） | 「①なし ②同じ ③OK ④OK ⑤開いた」 | 待ちが無い。初期値が元の6項目。2つ開かない。ダブルクリックで開く | 作者の報告のとおり。`config.json` の MD5・更新日時は変わらず、ログの行も増えない（開く・キャンセルでは書かない） |
| 4 | 省略（作者の判断） | 「省く」 | — | 上の「plan からずらした点」3 |
| 5-1 | 6項目をすべて変えて保存（作者、13:38） | 「5-1を終えた。画面は閉じた。時刻は13:38:40くらい」 | 保存ログ1行。切り替えの行が無い。6ロールが保存の前と同じ | 13:38:36 `設定画面で保存しました: 接続時 再生=スピーカー (Realtek(R) Audio) 録音=マイク (USB Microphone) / 切断時 再生=スピーカー (INZONE Buds - Game) 録音=（未設定） / switchOnConnect=false switchOnDisconnect=false` の1行だけ。6ロールは保存の前（12:22）と一致 |
| 5-2 | ファイルの確認（作業環境）→ 開き直してキャンセル（作者） | 「同じ」 | 6項目が選んだとおりのキーに入り、6項目以外の値と順が Step 1 と同じ。再表示が保存した値 | `budsDevice` = Realtek、`budsInputDevice` = USB マイク、`speakerDevice` = Buds - Game、`speakerInputDevice` = `""`、`switchOnConnect` = `false`、`switchOnDisconnect` = `false`。6項目以外はキーの順・値とも Step 1 と一致（JSON として比較）。BOM あり・CR 0・末尾の改行1つ。468 バイト、MD5 `77A42F38…`、`.tmp` なし。開き直しの後も MD5・行数は変わらない |
| 5-3 | Buds を出す（13:43）→ 戻す（13:44）（作者） | 「出した 13:43」「戻した 13:44」 | 接続で `switchOnConnect が false のため切り替えません。`、切断で `switchOnDisconnect が false のため切り替えません。`。6ロールが変わらない | 13:43:10 `Connected` → `switchOnConnect が false のため切り替えません。`（ほかに 13:43:15 の truncate の行）、13:43:56 `Disconnected` → `switchOnDisconnect が false のため切り替えません。`。切り替えの行なし。6ロールはどちらも変わらない |
| 5-4 | 2つのチェックを ON にして保存（作者、13:45） | 「保存した 13:45」 | 2キーが `true`。切り替えの行が無い | 13:45:18 保存ログ（`switchOnConnect=true switchOnDisconnect=true`）。2キーが `true`、ほかの4項目は 5-1 のまま。6ロールは変わらない |
| 5-5 | Buds を出す（13:46）→ 録音タブで Buds - Chat を既定・既定の通信デバイスに（再生タブは省略）→ 戻す（13:49）（作者） | 「出した 13:46」「そろえた」「戻した 13:49」 | 切断で再生は新しい切り替え先（Buds - Game へ切り替え、または見つからない WARN）。録音は切り替えの行が出ず Buds - Chat のまま | 出す: 13:47:00 `Connected` → `既定の再生デバイスを切り替えました: スピーカー (Realtek(R) Audio)`（録音は USB マイク が既に既定で行なし。判定の対象外として記録）。そろえた後: Render 3 = Realtek、Capture 3 = Buds - Chat、ログの行は増えない。戻す: 13:49:16 `Disconnected` → `既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)` の2行だけ。Render 3 = Buds - Game（「切り替えました」の場合）、Capture 3 = Buds - Chat |
| 5-6 | Buds を出す（13:50）（作者）。事前の既定は 5-5 の後のまま（Render = Buds - Game、Capture = Buds - Chat）で `mmsys.cpl` は不要 | 「出した 13:50」 | `Connected` → 再生 Realtek → 録音 USB マイク の順。Render 3 = Realtek、Capture 3 = USB マイク | 13:50:11 に3行がこの順。6ロールは期待どおり |
| 5-7 | 元の6項目に戻して保存（13:51）→ 戻す（13:52）→ 出す（13:53）（作者） | 「保存した 13:51」「戻した13:52 出した 13:53」 | 6項目が元の値。切断・接続とも元の6項目どおり | 13:51:17 保存ログ（元の6項目）。`config.json` は6項目・6項目以外とも Step 1 と同じ値。戻す: 13:52:34 `Disconnected` だけ（Realtek・USB マイク が既に既定）。この切断の直後の6ロールは読み取っていない（「plan からずらした点」8。判定はログだけ）。出す: 13:53:09 `Connected` → 再生 Buds - Game → 録音 Buds - Chat（ほかに 13:53:13 の truncate の行）。Render 3 = Buds - Game、Capture 3 = Buds - Chat |
| 6 | 接続時の再生を変えてキャンセル、変えて ×、開き直してキャンセル（作者） | 「元のまま」 | `config.json` の MD5・更新日時が変わらない。保存ログが増えない。再表示が元の値 | MD5 `AC269A4D…`・更新 13:51:17 のまま、保存ログは 3 件のまま、ログ 154 行のまま |
| 7 | `attrib +R`（ORCHESTRATOR、13:56:54）→ 接続時の再生を Realtek にして保存、ダイアログ、キャンセル（作者、13:57） | 「こちらで実行して」「失敗した 13:57 ダイアログは出た。画面は残った。」 | ダイアログが出て画面が閉じない。`[WARN] 設定を保存できません:`。MD5・更新日時が変わらず、`.tmp` が残らない | 属性 ReadOnly, Archive（13:57:05）。13:57:45 `[WARN] 設定を保存できません: config.json を書き込めません: Access to the path is denied.`。保存ログは 3 件のまま。MD5・更新日時は変わらず、`.tmp` なし（フォルダは `bin`・`logs`・`config.json`・退避ファイルだけ）。6ロールは変わらない |
| 7 | Buds を戻す（13:58）→ 出す（13:59）（作者） | 「戻した 13:58」「出した 13:59」 | 出すと `既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)`（保存に失敗した値へは切り替わらない） | 戻す: 13:58:51 `Disconnected` → 再生 Realtek → 録音 USB マイク、Render 3 = Realtek（出す前の既定。「plan からずらした点」5）。出す: 13:59:29 `Connected` → `既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)` → 録音 Buds - Chat。Render 3 = Buds - Game、Capture 3 = Buds - Chat |
| 7 | `attrib -R`（ORCHESTRATOR、14:00:10） | 「こちらで実行して」 | 読み取り専用が外れる | 属性 Archive、MD5 `AC269A4D…`（14:00:28） |
| 8 | 設定画面を開いたまま、メモ帳で `logLevel` を `Debug` にして保存（作者。メモ帳は ORCHESTRATOR が 14:01:19 に開いた） | 「書き換えた 14:01」 | — | `config.json` の差分は `"logLevel": "Info",` → `"logLevel": "Debug",` の1行だけ（516 バイト、MD5 `57ADF219…`、更新 14:01:40。BOM・LF・末尾の改行は保たれた）。ログの行は増えない |
| 8 | 開いたままの設定画面で何も変えずに保存（作者、14:02） | 「保存した 14:02 画面は開いたままだった」 | `logLevel` が `Debug` のまま残り、6項目が元の6項目 | 14:02:33 保存ログ（元の6項目）。`logLevel` = `Debug` のまま、6項目は元の値。内容はメモ帳の保存後と同じ文字列（MD5 `57ADF219…` も同じ、更新日時だけ 14:02:33） |
| 8 | 再生 Realtek・録音 USB マイク を既定・既定の通信デバイスに（作者）→ Buds を戻す（14:04）（作者） | 「そろえた」「戻した 14:04」 | `Disconnected` が出て、`[DEBUG]` の行が1行も出ない | そろえた後: Render 3 = Realtek、Capture 3 = USB マイク。戻す: 14:04:44 `Disconnected` の1行だけ。`[DEBUG]` の行も切り替えの行も出ない。6ロールは変わらない |
| 9 | 設定画面を開いたまま、トレイの「終了」（作者、14:05） | 「終了した 14:05 設定画面は閉じた」 | 画面が閉じる。終了コード0。プロセス数0。最終行が `INZONE Auto Switch を終了します。`、その直前に停止を確認できない旨の行と `[ERROR]` が無い | 待機していたプロセスのファイルに exited=14:05:45、exitcode=0。プロセス数0（14:06:19）。最終行 14:05:45 `INZONE Auto Switch を終了します。`、直前は 14:04:44 の `Disconnected` |
| 10 | プロセス数0の確認 → `attrib -R` → 退避ファイルで上書き → MD5 の確認 → 退避ファイルの削除 → 起動（ORCHESTRATOR、14:06:33〜34） | 「こちらで実行して」 | MD5 が Step 1 と一致、読み取り専用なし、`.tmp` なし。起動後のログに `[WARN]`・`[ERROR]` が無い | 14:06:57: `config.json` 515 バイト、MD5 `AC269A4D3DA101351F6580C471201943`（Step 1 と一致）、更新 2026-09-16 23:44:40（Step 1 と同じ）、属性 Archive、`.tmp` なし、退避ファイルなし。プロセス 1 件（14:06:34 起動）。起動ログは開始・監視対象・`録音の切り替え:`（元の値）・`自動起動: 有効`・`起動時同期: 現在の状態を Disconnected と判定しました。`（切り替えの行なし）。`[WARN]`・`[ERROR]` なし。Run キーは Step 1 と同じ |
| 10 | Buds を出す（14:07）→ 戻す（14:08）（作者） | 「出した 14:07」「戻した 14:08」 | 元の6項目どおりに切り替わる | 出す: 14:07:40 `Connected` → 再生 Buds - Game → 録音 Buds - Chat、Render 3 = Buds - Game、Capture 3 = Buds - Chat。戻す: 14:08:24 `Disconnected` → 再生 Realtek → 録音 USB マイク、Render 3 = Realtek、Capture 3 = USB マイク |

Step 10 の 5.（確認の全期間 09:53〜14:08 のログ）: `[ERROR]` は 0 行。`[WARN]` はログ全体で Step 7 の意図した1行（13:57:45 `設定を保存できません: config.json を書き込めません: Access to the path is denied.`）だけ。Step 5 の 5. の「見つからない」場合の `[WARN]` は出ていない（Buds - Game へ切り替わったため）。ログ全体の `[DEBUG]` は 2026-09-16 08:39〜08:50 の6行だけで、この確認の期間には無い。

一覧を取得できないとき（spec §8 実機確認8）は、実機で列挙だけを失敗させる手段が無いため確認していない（plan どおり）。取得の失敗を画面へ渡す経路は Core の `SettingsFormModelTests.Create_ListUnavailable_OffersOnlyConfiguredNamesAndReportsNotice` で確かめている。

### 6 ロールの推移（作業環境の読み取り）

| 時点 | Render Console | Render Multimedia | Render Communications | Capture 3 ロール |
|---|---|---|---|---|
| Step 1（09:37） | Buds - Game | Buds - Game | Buds - Game | Buds - Chat |
| Step 2 起動後（12:18） | **Buds - Game** | **Buds - Game** | **Realtek** | **USB マイク** |
| Step 3 の後（12:21）、Step 5-1 保存前（12:22）・保存後（13:39） | Buds - Game | Buds - Game | Realtek | USB マイク |
| Step 5-3 出す（13:43）・戻す（13:44） | Buds - Game | Buds - Game | Realtek | USB マイク |
| Step 5-4 保存後（13:45） | Buds - Game | Buds - Game | Realtek | USB マイク |
| Step 5-5 出す（13:47） | **Realtek** | **Realtek** | Realtek | USB マイク |
| Step 5-5 `mmsys.cpl` の後（13:48） | Realtek | Realtek | Realtek | **Buds - Chat** |
| Step 5-5 戻す（13:49） | **Buds - Game** | **Buds - Game** | **Buds - Game** | Buds - Chat |
| Step 5-6 出す（13:50） | **Realtek** | **Realtek** | **Realtek** | **USB マイク** |
| Step 5-7 保存後（13:51） | Realtek | Realtek | Realtek | USB マイク |
| Step 5-7 戻す → 出す（13:53。戻した直後は読み取っていない） | **Buds - Game** | **Buds - Game** | **Buds - Game** | **Buds - Chat** |
| Step 6 の後（13:55）、Step 7 保存の失敗の後（13:58） | Buds - Game | Buds - Game | Buds - Game | Buds - Chat |
| Step 7 戻す（13:59） | **Realtek** | **Realtek** | **Realtek** | **USB マイク** |
| Step 7 出す（13:59） | **Buds - Game** | **Buds - Game** | **Buds - Game** | **Buds - Chat** |
| Step 8 保存の後（14:03） | Buds - Game | Buds - Game | Buds - Game | Buds - Chat |
| Step 8 `mmsys.cpl` の後（14:04） | **Realtek** | **Realtek** | **Realtek** | **USB マイク** |
| Step 8 戻す（14:05）、Step 9 終了後（14:06）、Step 10 起動後（14:06） | Realtek | Realtek | Realtek | USB マイク |
| Step 10 出す（14:08） | **Buds - Game** | **Buds - Game** | **Buds - Game** | **Buds - Chat** |
| Step 10 戻す（14:08） | **Realtek** | **Realtek** | **Realtek** | **USB マイク** |

太字は直前の読み取りから変わったロール。Capture は全期間で3ロールがそろっていた。Step 2 の変化は、アプリが止まっていた間のもの（上の「経緯」）。

### `config.json` の推移（作業環境の読み取り）

| 時点 | バイト数 | MD5 | 更新日時 | 内容 |
|---|---|---|---|---|
| Step 1 | 515 | `AC269A4D3DA101351F6580C471201943` | 2026-09-16 23:44:40 | 元の6項目 |
| Step 5-1 保存後 | 468 | `77A42F380D6427FCCFC2A3DD255FFA8A` | 13:38:36 | 6項目をすべて変えた値 |
| Step 5-4 保存後 | 466 | `CC35AD3135BD0002E4B38C9AFC4781F9` | 13:45:18 | 2キーを `true` |
| Step 5-7 保存後 | 515 | `AC269A4D3DA101351F6580C471201943` | 13:51:17 | 元の6項目（Step 1 とバイト列が一致） |
| Step 7 保存の失敗の後 | 515 | `AC269A4D3DA101351F6580C471201943` | 13:51:17 | 変わらず（読み取り専用） |
| Step 8 メモ帳の保存後 | 516 | `57ADF219EE3B14339E21BB57D67B326A` | 14:01:40 | `logLevel` = `Debug` |
| Step 8 設定画面の保存後 | 516 | `57ADF219EE3B14339E21BB57D67B326A` | 14:02:33 | 同上（6項目は元の値） |
| Step 10 復元後 | 515 | `AC269A4D3DA101351F6580C471201943` | 2026-09-16 23:44:40 | Step 1 と一致 |

### 観察

- **保存で `config.json` の作成日時が変わる。** 作業環境が `config.json` の作成日時を読み取った時点と値は次のとおり。
  - Step 1 の読み取り（09:36:58）、退避の確認の読み取り（09:54:29、09:55:48）: 2026-09-16 08:27:33（最初の保存の前）
  - Step 5-1 の保存の後の読み取り（13:39:40）: 2026-09-17 13:38:36（その保存の時刻）
  - Step 10 の復元（`Copy-Item -Force` による上書き）の後の読み取り（14:06:57）: 2026-09-17 14:02:33（Step 8 の設定画面の保存の時刻と同じ）

  Step 5-4・5-7・8 の保存の後には作成日時を読み取っていない。Step 10 の復元の後も作成日時は最初の保存の前の値に戻っておらず、内容と更新日時は Step 1 と一致した。spec §5 で「置き換えのあと、元の `config.json` の作成日時が保たれることは保証しない」としている範囲である。なお、09:55:48 の読み取りでは、退避ファイル `config.json.before-settings-window` の作成日時は退避の時刻 2026-09-17 09:55:31 だった。
- **Step 5-7 の保存で、`config.json` が Step 1 とバイト単位で一致した。** plan は「設定画面で値を戻すだけでは元のバイト列に戻らない」ことを前提に Step 10 の復元を置いているが、今回の元のファイルはすでに保存と同じ書式（BOM 付き UTF-8・インデント・LF・末尾の改行1つ、キーの順も同じ）だったため一致したと考えられる。Step 10 の復元は手順どおり行った。同じ理由で、Step 8 の設定画面の保存後の内容はメモ帳の保存後と同じバイト列だった。
- **Step 5-7 の後に作者から「Budsから音が出ない」と報告があり、その後「ごめん。出た。」と報告があった。** 原因の説明は無い。作業環境の読み取り（13:54:46、13:55:04）では、既定は Render 3 = Buds - Game、`スピーカー (INZONE Buds - Game)` は有効・ミュートなし・音量 100% で、エンドポイントのピーク値は 0.360・0.457、Chrome の音声セッションが同じエンドポイントで Active（ピーク値 0.436・0.462）だった。Windows 側は Buds - Game へ音を送っており、アプリの切り替えは期待どおりだった。
- **INZONE Hub による `ActionLog.log` の作り直し。** Buds を出した直後に `[INFO] ActionLog.log の truncate を検知しました。先頭から読み直します。` が出た（13:43:15、13:53:13）。最小版の監視の既存の挙動で、この後の状態遷移も正しく検知された。
- **起動時同期。** Step 2 の起動は直前の遷移から 102.2 分後で切り替えず、Step 10 の起動は直前の遷移（14:04:44）から10分以内で `現在の状態を Disconnected と判定しました。` となった（Realtek・USB マイク が既に既定で、切り替えの行なし）。
- **設定画面の操作とログ。** 開く・キャンセル・× はログに行を残さない。これらの操作が行われたことは作者の報告による。

### 作者確認

| 時点 | 確認した内容 | 作者の報告（原文） |
|---|---|---|
| Step 3 | 開くまでの待ち、初期値、2つ開かないこと（メニュー・ダブルクリック）、ダブルクリックで開くこと | 「①なし ②同じ ③OK ④OK ⑤開いた」 |
| Step 5-1 | 保存で画面が閉じること | 「5-1を終えた。画面は閉じた。時刻は13:38:40くらい」 |
| Step 5-2 | 開き直した表示が保存した値と一致すること | 「同じ」 |
| Step 5-7 の後 | Buds からの音 | 「Budsから音が出ない」→「ごめん。出た。」 |
| Step 6 | キャンセル・× の後に開き直すと元の値 | 「元のまま」 |
| Step 7 | 保存の失敗のダイアログと、画面が閉じないこと | 「失敗した 13:57 ダイアログは出た。画面は残った。」 |
| Step 8 | 書き換えの前から設定画面を開いたままだったこと | 「保存した 14:02 画面は開いたままだった」 |
| Step 9 | 設定画面を開いたままの終了で、画面も閉じること | 「終了した 14:05 設定画面は閉じた」 |

### 未確認の項目

- 「（未接続）」の表示（spec §8 実機確認2）。作者の判断で省いた（上の「plan からずらした点」3）。Core の自動テストで確かめている。
- 一覧を取得できないとき（spec §8 実機確認8）。実機で列挙だけを失敗させる手段が無い。Core の自動テストで確かめている。
- 一時ファイルを消せないとき（「一時ファイルが残っています」）と、削除を共有しない開き方で別のプロセスが `config.json` を開いているときの保存の失敗。実機では発生させていない（Core の自動テストで確かめている）。
- `config.json` が無い・壊れている状態で設定画面を開くこと（保存ボタンの無効化と上部の理由の表示）。実機では試していない（Core の自動テストと、plan 作成時の作業コピーでのフォームの画像で確かめている）。
- 切り替えの途中での保存（その回は入れ替え前の設定で最後まで行う）。実機では狙って起こしていない（Core の自動テストで確かめている）。
- 音の作者の確認は、Step 5-7 の後の1回（「Budsから音が出ない」→「ごめん。出た。」）だけで、ほかの出し入れでは行っていない。マイク入力の作者の確認は、この Task では行っていない。録音の既定が切り替わったことは、作業環境の6ロールの読み取り（と、音が出ない件の切り分けでの録音のピーク値の読み取り）によるもので、作者の確認ではない。
- 保存による `config.json` の作成日時以外の属性・アクセス権の変化は読み取っていない。
- スリープ復帰とサインアウト・サインインをまたいだ動作、自動起動（ログオン時）での設定画面の動作は、この Task では確かめていない。
