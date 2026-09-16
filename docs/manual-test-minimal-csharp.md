# 最小 C# トレイ版 — 手動確認記録

- 対象計画: [`superpowers/plans/2026-09-15-inzone-minimal-csharp.md`](superpowers/plans/2026-09-15-inzone-minimal-csharp.md)
- 記録方針: エンドポイント ID の GUID やユーザー名など、個人・端末固有の値はマスクする（`{0.0.0.00000000}.{xxxxxxxx-...-<末尾4桁>}`、`<user>`）。

## 環境

| 項目 | 値 |
|---|---|
| Windows | Microsoft Windows [Version 10.0.26200.9457] |
| 開発環境 | WSL2 上のリポジトリを Windows の `dotnet.exe` で UNC パス経由でビルド |
| .NET SDK（インストール済み） | 9.0.314 / 10.0.300 |
| .NET SDK（使用） | 10.0.300（`global.json`、`rollForward: latestPatch`） |
| ビルド出力先 | `%LOCALAPPDATA%\AutoSwitchForInzone-build\bin\<プロジェクト>\debug\`（UNC 経由のときだけ `Directory.Build.props` で切り替え） |

## 段階1 Task 1: デバイス一覧と既定出力の取得（2026-09-15）

読み取りのみの確認。既定の再生・録音デバイスは変更していない。旧版（スケジュールタスク「INZONE Auto Switch」）は停止・変更していない。

### 手順

リポジトリルートで実行する。

```bash
dotnet.exe --list-sdks
inzone_repo_win=$(wslpath -w "$PWD")
dotnet.exe build "$inzone_repo_win\\AutoSwitchForInzone.sln" -c Debug
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- list
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- current
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj"
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- foo
```

### 結果

| 確認 | 期待 | 結果 |
|---|---|---|
| SDK | .NET 10 SDK があり global.json と一致 | 10.0.300。リポジトリルートの `dotnet.exe --version` も 10.0.300 |
| ビルド | exit 0、エラー 0 | exit 0、警告 0、エラー 0。リポジトリ内に `bin` / `obj` は生成されない |
| `list` | exit 0、有効な再生デバイスの ID・名前と既定出力 | exit 0。5 件を表示し、既定出力に `*` と `Default playback:` 行 |
| `current` | exit 0、`list` の既定表示と一致 | exit 0。`list` の既定と同じ ID・名前 |
| 引数なし | ヘルプ、終了コード 2 | 標準エラーにヘルプ、終了コード 2。例外なし |
| 未知コマンド `foo` | ヘルプ、終了コード 2 | 標準エラーにヘルプ、終了コード 2。例外なし |
| 余分な引数 `list extra` | （追加確認）引数不正として扱う | 標準エラーにヘルプ、終了コード 2 |

`list` の出力（ID はマスク）:

```text
Playback devices (active): 5
* id={0.0.0.00000000}.{xxxxxxxx-...-db01} name=スピーカー (Realtek(R) Audio)
  id={0.0.0.00000000}.{xxxxxxxx-...-9494} name=3 - REALTEK (2- AMD High Definition Audio Device)
  id={0.0.0.00000000}.{xxxxxxxx-...-8571} name=1 - AW3423DWF (2- AMD High Definition Audio Device)
  id={0.0.0.00000000}.{xxxxxxxx-...-1585} name=ヘッドセット イヤフォン (INZONE Buds - Chat)
  id={0.0.0.00000000}.{xxxxxxxx-...-c440} name=スピーカー (INZONE Buds - Game)
Default playback: id={0.0.0.00000000}.{xxxxxxxx-...-db01} name=スピーカー (Realtek(R) Audio)
```

`AudioSmoke` の既定出力は Multimedia ロールの既定を表示する。

### Windows 側との照合（読み取りのみの代替手段）

GUI のサウンド設定は作業環境から見られないため、次の読み取り専用コマンドで照合した。

- `Get-AudioDevice -List`（AudioDeviceCmdlets 3.1.0.2、導入済みのものを使用）の `Type=Playback`: 5 件。ID と名前が `list` の 5 件とすべて一致。`Default=True` のデバイスは `list` / `current` の既定と同じ。
- `Get-AudioDevice -Playback`: `list` / `current` の既定と同じ ID・名前。
- `Get-PnpDevice -Class AudioEndpoint -PresentOnly`: 7 件。再生側（`{0.0.0.…}`）の 5 件が `list` と ID（大文字小文字の違いのみ）・名前で一致。残り 2 件は録音側（`{0.0.1.…}`）で、再生デバイス一覧に含まれないのは正しい。

**作者確認（2026-09-15）:** 作者が Windows の「サウンドの設定」と `mmsys.cpl` の再生タブで確認し、有効な再生デバイス 5 件の表示名（スピーカー (Realtek(R) Audio) / 3 - REALTEK (2- AMD High Definition Audio Device) / 1 - AW3423DWF (2- AMD High Definition Audio Device) / ヘッドセット イヤフォン (INZONE Buds - Chat) / スピーカー (INZONE Buds - Game)）と既定のデバイス（スピーカー (Realtek(R) Audio)）が上の一覧と一致した。

### 既存 PowerShell 版への影響

- 旧版の自動テスト `scripts/Invoke-Tests.ps1`（Pester 5.9.1）: `Tests Passed: 100, Failed: 0`。README の記載は 99 件だが、README 追加後のコミットでテストが 1 件増えており（`It` の数は 100）、今回の変更による差ではない。
- 既存 PowerShell ファイル（`src/INZONE-AutoSwitch.ps1`、`src/modules/*`、`scripts/*`、`tests/*`、`config.json`、`README.md`）に差分はない。

### 既知の差異

- `scripts/Install.ps1` は `src\*` を配置先へ再帰コピーするため、旧版を再インストールすると `src/AutoSwitch.Audio/` も配置先へコピーされる。旧版スクリプトを変更しない制約のため、今回は対処しない。
- .NET 10 の `dotnet new sln` の既定形式は `.slnx` のため、計画どおり `.sln` にするには `--format sln` を指定する。

## 段階1 Task 2: Buds とスピーカーへの切り替え（2026-09-15）

`CoreAudioEndpointService.SetDefaultPlayback` は非公開の `IPolicyConfig.SetDefaultEndpoint` で再生の Console → Multimedia → Communications を順に変更する。最初の変更の前に、ID が有効な再生デバイスの一覧に含まれるかをサービス内で確認し、含まれなければ何も変更せずに例外にする（録音デバイスの ID もここで拒否される）。2 つ目以降のロールで失敗した場合は、変更済みのロールを含む例外にする（部分変更の可能性を残す）。`AudioSmoke set <ID>` は ID を有効な再生デバイスの一覧と完全一致で照合し、不在なら終了コード 1 で音声を変更しない。変更後は既定出力を再取得し、要求したデバイスでなければ終了コード 1 にする。

### 比較基準と確認方法

- 旧版 `src/modules/AudioSwitcher.psm1` の `Set-DefaultPlaybackDeviceByName` は、`Get-AudioDevice -List` から名前一致の再生デバイスを探し、`Set-AudioDevice`（AudioDeviceCmdlets 3.1.0.2）へ渡す。どのロールが変わるかはモジュールのコードからは読めないため、実機で比較した。
- 3 ロールと録音の既定は、リポジトリ外の使い捨ての読み取り専用スクリプトで確認した。`IMMDeviceEnumerator.GetDefaultAudioEndpoint` を Render / Capture × Console / Multimedia / Communications の 6 通り呼ぶだけで、既定を変更しない。
- 旧版方式の切り替えは、リポジトリの `AudioSwitcher.psm1` を import して `Set-DefaultPlaybackDeviceByName` を呼んだ。旧版の監視スクリプトは起動していない。
- Windows PowerShell 5.1 は BOM なしのファイルを ANSI コードページで読むため、確認用スクリプトは ASCII のみで書いた（日本語のコメントがあるとヒアドキュメントが壊れた）。

### 手順

リポジトリルートで実行する。`<ID>` は `list` で取得した実際の ID。

```bash
inzone_repo_win=$(wslpath -w "$PWD")
dotnet.exe build "$inzone_repo_win\\AutoSwitchForInzone.sln" -c Debug
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- set
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- set '{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}'
# 旧版を停止してから
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- set '<Buds (Game) の ID>'
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- set '<スピーカー (Realtek) の ID>'
dotnet.exe run --project "$inzone_repo_win\\tools\\AudioSmoke\\AudioSmoke.csproj" -- set '<スピーカー (Realtek) の ID>'
```

### 結果

| 確認 | 期待 | 結果 |
|---|---|---|
| ビルド | exit 0、エラー 0、警告 0 | exit 0、警告 0、エラー 0 |
| `set`（ID 欠落） | ヘルプ、終了コード 2、音声を変更しない | 標準エラーにヘルプ（`set <ID>` を含む）、終了コード 2。6 ロールは前後で同一 |
| `set` 存在しない ID | 終了コード 1、音声を変更しない | `error: no active playback device has id=...`、終了コード 1。6 ロールは前後で同一 |
| Task 1 の再確認 | `list` / `current` exit 0 で一致、引数なし・`foo` で終了コード 2 | すべて期待どおり（5 件、既定は Realtek） |
| 旧版の停止 | 旧版のプロセスが 0 件 | 作者が `Stop-Process -Id <PID>` で停止（作業環境からの停止は権限の仕組みで拒否されたため）。コマンドラインに INZONE-AutoSwitch を含むプロセス 0 件を確認 |
| `set` Buds (Game) | exit 0、再取得が Buds | exit 0。`Default playback` は スピーカー (INZONE Buds - Game) |
| `set` スピーカー (Realtek) | exit 0、再取得がスピーカー | exit 0。`Default playback` は スピーカー (Realtek(R) Audio) |
| 同じ ID（Realtek）を再指定 | exit 0、異常終了しない | exit 0。6 ロールは変わらない |
| API 失敗・比較不一致 | 無し | 無し |

**作者確認（2026-09-15）:** `set` で Buds (Game) へ切り替えた後、作者が音を再生し「Buds から聞こえる」と回答した。スピーカー (Realtek) へ切り替えた後は「スピーカーから聞こえる」（Buds からは出ていない）と回答した。

### 3 ロールと録音の既定の比較

ID はマスクする（再生: Realtek `…-db01`、Buds (Game) `…-c440`。録音: Buds (Chat) マイク `{0.0.1.00000000}.{xxxxxxxx-...-9066}`）。

| 時点 | Render Console | Render Multimedia | Render Communications | Capture（3 ロール） |
|---|---|---|---|---|
| 変更前（旧版停止後） | Realtek | Realtek | Realtek | Buds (Chat) マイク |
| AudioSmoke `set` Buds (Game) | Buds (Game) | Buds (Game) | Buds (Game) | Buds (Chat) マイク |
| AudioSmoke `set` Realtek | Realtek | Realtek | Realtek | Buds (Chat) マイク |
| AudioSmoke `set` Realtek（再指定） | Realtek | Realtek | Realtek | Buds (Chat) マイク |
| 旧版方式 → Buds (Game)（`Result: Switched`） | Buds (Game) | Buds (Game) | Buds (Game) | Buds (Chat) マイク |
| 旧版方式 → Realtek（`Result: Switched`） | Realtek | Realtek | Realtek | Buds (Chat) マイク |

AudioSmoke と旧版方式は、同じデバイスへの切り替えで 6 ロールの結果が完全に一致した。どちらも再生の 3 ロールをまとめて変更し、録音の既定は変更しない。

### 復元

- 最後の切り替え（旧版方式 → Realtek）の後、6 ロールが変更前と一致することを確認した。
- `Start-ScheduledTask -TaskName 'INZONE Auto Switch'` で旧版を再開した。15 秒後、旧版のプロセス（`powershell.exe -File "C:\Users\<user>\AppData\Local\INZONE-AutoSwitch\INZONE-AutoSwitch.ps1"`）が 1 件だけ動いていることを確認した。
- 再開の 15 秒後にも 6 ロールは変更前と一致していた（起動時同期による切り替えは 15 秒以内には起きなかった）。

### 独立レビューへの対応と再確認（2026-09-15）

独立レビューで「公開の音声サービスが再生デバイスに限る境界を担保せず、呼び出し側の照合に任せている。サービスを直接呼ぶと録音デバイスの ID が既定の変更に渡る」と指摘された。これに対し、`SetDefaultPlayback` の最初の変更より前に、有効な再生デバイスの一覧（`EnumAudioEndpoints(Render, Active)`）との照合をサービス内に追加した。一覧に無い ID は `AudioServiceException`（HRESULT `0x80070490`）にし、既定を変更しない。`AudioSmoke` 側の照合（表示名の取得を兼ねる）はそのまま残し、コンソールの契約は変わらない。

修正後に次を再確認した。変更は変更前の照合の追加だけで、既定を変更する呼び出し経路は変わらないため、Buds への切り替えと旧版の停止はやり直していない。旧版は稼働したまま（1 プロセス）で確認した。

| 確認 | 期待 | 結果 |
|---|---|---|
| ビルド | exit 0、エラー 0、警告 0 | exit 0、警告 0、エラー 0 |
| `list` / `current` / 引数なし / `foo` | exit 0 / 0（一致）/ 2 / 2 | すべて期待どおり |
| `set`（ID 欠落） | ヘルプ、終了コード 2 | 終了コード 2 |
| `set` 存在しない ID | 終了コード 1、変更なし | 終了コード 1 |
| `set` 録音デバイス（Buds (Chat) マイク）の ID | 終了コード 1、変更なし | 終了コード 1（`AudioSmoke` の照合で拒否） |
| `set` 現在の既定（Realtek）の ID | exit 0、再取得が Realtek、変更なし | exit 0、再取得は Realtek |
| 6 ロール | 上の操作の前後で不変 | 不変 |

### 未確認・既知の差異

- サービス内の照合は、コードの読解だけで確認している。録音デバイスの ID は `AudioSmoke` の照合で先に拒否されるため、実機ではサービス内の照合まで到達していない。サービスを直接呼んで録音デバイスの ID を渡す実機実行は、照合が効かなかった場合に承認範囲外の録音変更になるため行っていない。
- ロールの途中で失敗した場合の部分変更の例外、API 失敗時の終了コード 1、再取得した既定が要求と異なる場合の終了コード 1 は、実機で発生させておらず、コードの読解だけで確認している。
- Windows のスケジュールタスクは wscript 経由で旧版を起動するため、旧版が動いていても状態は `Ready` のまま。プロセスはコマンドラインで確認する。コマンドラインで検索するとき、検索する PowerShell 自身も一致するので除外が必要。
- 作業環境（エージェント）からの旧版の停止は権限の仕組みで拒否された。停止は作者が実行した。

## 段階2 Task 11: 実機で自動切り替えを確認する（2026-09-16）

新版（`AutoSwitch.App`）のコンソール常駐を実機で動かした確認。旧版の監視は確認の前に停止し、確認の後に再開した。旧版と新版の監視を同時に動かしていない。

作業の分担: **旧版の停止と再開、App の起動と Ctrl+C、INZONE Hub の終了と再起動、Buds のケースからの出し入れ、Windows の設定での手動切り替え、そして音の出力先の確認は、すべて作者が実施した。** プロセス、ログ、6 ロールの確認は作業環境からの読み取りだけで行った。以下では、作者の報告によるものを「作者確認」と明記する。

**ただし書き込みを 2 回行っている。** `%LOCALAPPDATA%\AutoSwitchForInzone\config.json` の `logLevel` を 「既存設定を上書きしない」確認のために `Info` から `Debug` へ変えた操作と、確認の後に `Info` へ戻した操作は、**どちらも作業環境から実施した**（作者ではない）。この 2 回以外に、作業環境からファイルや OS の設定を書き換えていない。

### 環境

| 項目 | 値 |
|---|---|
| 実行した exe | `%LOCALAPPDATA%\AutoSwitchForInzone-build\bin\AutoSwitch.App\debug\AutoSwitch.App.exe`（Task 10 のビルド成果物） |
| 新版のデータ | `%LOCALAPPDATA%\AutoSwitchForInzone`（`config.json`、`logs\AutoSwitchForInzone.log`） |
| INZONE Hub | 1.0.19.0。監視対象は `%APPDATA%\Sony\INZONE Hub\ActionLog.log` |
| 旧版 | スケジュールタスク「INZONE Auto Switch」。確認の前に作者が `Stop-Process` で停止した |

6 ロールの確認は、リポジトリ外の読み取り専用スクリプトで行った。`IMMDeviceEnumerator.GetDefaultAudioEndpoint` を Render / Capture × Console / Multimedia / Communications の 6 通り呼ぶだけで、既定を変更しない（段階1 Task 2 と同じ方法。Windows PowerShell 5.1 で動くよう ASCII のみで書いた）。

### 確認前の状態と復元の目標値

| 項目 | 値 |
|---|---|
| 旧版のプロセス | 停止後 0 件（スケジュールタスクは `Ready` のまま。段階1と同じく状態では判断できない） |
| INZONE Hub | 1 件 |
| AutoSwitch.App | 0 件 |
| `%LOCALAPPDATA%\AutoSwitchForInzone` | 未作成 |
| Render 3 ロール | すべて `{0.0.0.00000000}.{xxxxxxxx-...-db01}` スピーカー (Realtek(R) Audio) |
| Capture 3 ロール | すべて `{0.0.1.00000000}.{xxxxxxxx-...-9066}` ヘッドセット マイク (INZONE Buds - Chat) |

6 ロールの読み取りは、作者の旧版停止が完了した後に行った。停止前の値は記録していない（停止そのものは既定を変更しない）。

### 手順

作者が Windows のターミナルで App を前面で起動し、Ctrl+C で終了する。

```powershell
& "$env:LOCALAPPDATA\AutoSwitchForInzone-build\bin\AutoSwitch.App\debug\AutoSwitch.App.exe"
```

作者の操作と作業環境からの読み取りを交互に行った。plan の項目の一部は 1 回の操作にまとめた（既存設定を上書きしない確認と起動時同期は同じ再起動で、ケースから出す確認と手動変更の尊重は同じ一続きの操作で行った）。

### 結果

| 確認 | 期待 | 結果 |
|---|---|---|
| 旧版の停止 | 旧版のプロセスが 0 件 | 作者が `Stop-Process -Id <PID>` で停止。0 件を確認 |
| 初回起動 | `config.json` と `logs\AutoSwitchForInzone.log` が作られる | どちらも作成された。コンソールに設定・ログのパスと `Ctrl+C で終了します。` の 3 行（作者確認）、エラーなし。プロセス 1 件 |
| 既定値の設定ファイル | 既定値のまま。デバイス名が作者の環境と一致 | 10 項目すべて既定値。リポジトリルートの旧版 `config.json` と**バイト単位で一致**（UTF-8 BOM 付き）。`budsDevice` = スピーカー (INZONE Buds - Game)、`speakerDevice` = スピーカー (Realtek(R) Audio) で、段階1 Task 1 で作者が確認した表示名と一致 |
| 開始時のログ | 開始・監視対象・起動時同期の判定が出る | 3 行とも出た。起動時同期は「判定できるイベントがありません。切り替えません。」。このとき `ActionLog.log` には `23009`（DisplayInfo）の 2 行しかなく、接続 `23011` / 切断 `23012` が 0 件だったため、正しい判定 |
| 既存設定を上書きしない | 値が保持される | `logLevel` を `Debug` に変えて再起動した後も `Debug` のまま。他の 9 項目も変化なし。「既定値の設定ファイルを作成しました」は再度出ない |
| Debug の行 | Debug の行がログに出る | 出た（`状態は Connected のままです。切り替えません。` と `既に既定の再生デバイスです: スピーカー (Realtek(R) Audio)`）。いずれも後続のケース操作の中で発生した。再起動した時点では Debug の行が出る場面（ログ未存在・同一状態の再イベント・切り替え先が既に既定）のいずれも起きていなかったため、確認は後続の操作へ持ち越した |
| 起動時同期（10 分以内） | Buds へ 1 回だけ切り替わる | Buds をケースから出した約 6 秒後に App を起動。`起動時同期: 現在の状態を Connected と判定しました。` と `既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)`。接続イベントが連続 2 件でも**切り替えの行は 1 行だけ**だった。**作者確認: Buds から聞こえる**（スピーカーからは出ていない） |
| 切り替え（ケースへ戻す） | スピーカーへ切り替わる | `状態遷移を検知しました: Disconnected` と `既定の再生デバイスを切り替えました: スピーカー (Realtek(R) Audio)`。Hub のイベントから約 0.2 秒で反応。**作者確認: スピーカーから聞こえる** |
| 切り替え（ケースから出す） | Buds へ切り替わる | `状態遷移を検知しました: Connected` と `既定の再生デバイスを切り替えました: スピーカー (INZONE Buds - Game)`。**作者確認: Buds から音が出た** |
| 6 ロールの値 | 再生 3 ロールが対象デバイスへそろう | 切り替えのたびに再生 3 ロールがまとめて変わった。Buds へは `{0.0.0.00000000}.{xxxxxxxx-...-c440}`、スピーカーへは `{0.0.0.00000000}.{xxxxxxxx-...-db01}`。段階1 Task 2 で記録した ID と一致 |
| 録音の既定 | 変わらない | Capture 3 ロールは確認の全期間を通じて `{0.0.1.00000000}.{xxxxxxxx-...-9066}` のまま不変 |
| 手動変更の尊重 | 1 分以上待っても Buds へ戻らない | 作者が Buds 接続中に Windows の設定でスピーカーへ手動変更。App の次のログ行はその**約 1 分 48 秒後**で、その間に切り替えを一切行っていない。**作者確認: 手動変更の約 1 分 30 秒後まで切替は発生せず** |
| 手動変更の後のケース操作 | 通常どおり切り替わる | ケースへ戻す・出すを繰り返し、いずれも期待どおり切り替わった。**作者確認: 期待通りの動きをしました** |
| Hub 未起動での待機 | App が終了しない。異常終了の記録が無い | 作者がトレイから Hub を終了し、約 60 秒待機してから再起動した（**作者確認**）。App は同じプロセスのまま動き続けた（**作者確認: 動いている・エラーなし**）。読み取りで言えるのは、**Hub の終了と再起動をまたぐ 08:49:05.564 〜 08:50:34.018 の約 88 秒のあいだ、App のログ行が 1 行も無かった**ことである（このうち再起動後の Hub のプロセスの起動時刻 08:50:08 より後が約 26 秒を占める）。**Hub が実際に停止していた時間を示すプロセスの読み取りは無い。**`ActionLog.log` は削除されなかったため、バックオフの記録も「ActionLog.log がありません」の Debug 行も出ず、静かに待機した |
| Hub 再起動後の切り替え | 再び切り替わる | 作者が Hub を再起動。その**後**に `状態遷移を検知しました: Connected` と Buds への切り替えが出た（Hub の新しいプロセスの起動時刻と App のログの時刻で確認）。**作者確認: 無事に切り替わりました** |
| App が Hub を起動しない | Hub を起動しない | 起動していないと判断した（根拠は後述。**Hub 停止中の直接の読み取りは未実施**） |
| Ctrl+C で終了 | 終了の行が出てプロセスが残らない | ログ末尾に `INZONE Auto Switch を終了します。`。AutoSwitch.App のプロセス 0 件。**作者確認: コンソールにエラーらしき表示なし** |
| ログの異常 | `[ERROR]` `[WARN]` が無い | 確認の全期間を通じて `[ERROR]` 0 行、`[WARN]` 0 行（INFO 32 行、DEBUG 6 行） |

ログは UTF-8（BOM なし）・CRLF だった。

### App が Hub を起動しないことの確認方法

**Hub が停止しているあいだにプロセスを読み取っていない**（作者が Hub の終了から再起動までを一続きで実施したため）。したがって「その時点で INZONEHub が 0 件だった」という直接の観察は無い。代わりに、コードの読解を主たる根拠として次のとおり判断した。

**主たる根拠はコードの読解である。** 製品コードにプロセスを起動する手段が無い。`src/AutoSwitch.App`、`src/AutoSwitch.Core`、`src/AutoSwitch.Audio` に `Process.Start` / `ProcessStartInfo` / `ShellExecute` / `Start-Process` は 1 件も無く、`Process` の使用箇所は `src/AutoSwitch.Core/AutoSwitchEngine.cs` の `Process.GetProcessesByName("INZONEHub")`（起動時刻を読むだけ）の 1 か所だけである。この経路しか無い以上、App が Hub を起動することはできない。

補強として、再起動後の Hub のプロセスの起動時刻（08:50:08）が、作者が手動で再起動したと報告したタイミングと矛盾しないことを確認した。ただし**これは補強にとどまる**。Hub が停止していた時間のプロセスの読み取りが無いため、起動時刻だけでは「App が起動したのではない」とは言い切れない。

### 手動変更の後に再生 3 ロールが分かれた件（旧版と同じ挙動）

手動変更の直後、再生 3 ロールが分かれた状態になった。

| ロール | 値 |
|---|---|
| Render Console | スピーカー (Realtek(R) Audio) |
| Render Multimedia | スピーカー (Realtek(R) Audio) |
| Render Communications | スピーカー (INZONE Buds - Game) |

経緯は次のとおり。

1. Windows の設定での手動変更は Console と Multimedia だけを変え、Communications は Buds のまま残った。
2. その後 Buds が切断され（作者はケースへ戻していないので Buds 側の自動切断とみられる）、App は切り替え先のスピーカーがすでに既定だと判定して `AlreadyDefault` を返し、どのロールも変更しなかった。

この短絡は旧版の再現である。新版の `PlaybackSwitcher` は `GetDefaultPlayback`（`ERole.Multimedia` だけを読む）で現在の既定を取得し、名前が一致すれば何もしない。旧版 `src/modules/AudioSwitcher.psm1` の `Set-DefaultPlaybackDeviceByName` も `Get-AudioDevice -Playback` で単一の既定だけを読んで同じ判定をする。したがって段階2で持ち込んだ差異ではなく、spec §3 の「維持する動作」に沿う。今回は修正していない。

この状態は、次に実際の切り替えが 1 回起きた時点で解消した。`SetDefaultPlayback` が再生 3 ロールをまとめて書き換えるため、ケースから出す操作で 3 ロールとも Buds になり、戻す操作で 3 ロールとも Realtek へ戻った。

### 実機でも観察できた、計画では自動テストの範囲としていた挙動

INZONE Hub が確認の途中で `ActionLog.log` を作り直したため、App が `ActionLog.log の truncate を検知しました。先頭から読み直します。` を出して監視を継続する様子を 2 回観察した（いずれも `[WARN]` `[ERROR]` は出ていない）。truncate への追従は plan では自動テストで確認する項目であり、実機確認の必須項目ではないが、実機でも正しく動いた。

### 復元

- 最後のケース操作（ケースへ戻す）で App が自動的にスピーカーへ戻したため、手動での出力の復元は不要だった。Ctrl+C の後の 6 ロールは、確認前に記録した値と完全に一致した。
- `config.json` の `logLevel` は「既存設定を上書きしない」確認のために一時的に `Debug` へ変え、確認の後に既定値の `Info` へ戻した。**この 2 回の書き込みは、作者ではなく作業環境から実施した**（実機確認のうち、作業環境が書き込みを行ったのはこの 2 回だけである）。どちらも `logLevel` の 1 行だけを変え、他の 9 項目と UTF-8 BOM は変えていない。書き戻した結果のファイルは、初回起動時に作られた既定の `config.json` とバイト単位で一致する。
- 作者が `Start-ScheduledTask -TaskName 'INZONE Auto Switch'` で旧版を再開した。旧版のプロセスが 1 件だけ動いていること、AutoSwitch.App が 0 件のままであることを確認した。
- 再開の約 20 秒後にも 6 ロールは確認前の値と一致していた（起動時同期による切り替えは起きなかった）。

### 未確認・既知の差異

- **起動時同期の「古すぎます」の側は実機で確認していない。** 今回は最終イベントの約 6 秒後に App を起動した「10 分以内」の条件だけを実施した。`startupSyncMaxAgeMinutes` を超えた場合に切り替えず「古すぎます」を出す経路は自動テストで確認している。
- **Hub 停止中のプロセスの読み取りを行っていない。** App が Hub を起動しないことは、上記のとおりコードの読解と Hub の起動時刻から確認した。
- 手動変更の後に再生 3 ロールが分かれる件は、旧版と同じ挙動として今回は修正していない（上記の節を参照）。
- plan の「段階2で実機確認しないもの」のうち、ログのローテーション、読み取り失敗時のバックオフ、スリープ復帰によるバックオフ解除、ログ未存在からの監視再開、切り替え失敗後の監視継続、監視ループの想定外例外からの復帰は、今回も実機では発生させていない（自動テストで確認済み）。スリープ復帰の実機確認は段階4で行う。
- 旧版の `scripts/Install.ps1` は `src\*` を配置先へ再帰コピーするため、旧版を再インストールすると段階2で追加した `src/AutoSwitch.Core` と `src/AutoSwitch.App` も配置先へ入る。旧版のファイルを変更しない方針のため、段階4の移行時に扱いを決める。
