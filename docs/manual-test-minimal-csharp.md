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
