# INZONE 最小 C# トレイ版 — 運用手順

## 1. この文書の対象

C# トレイ版（新版）を日常利用するための手順書である。旧版 PowerShell 版から新版へ移す手順、新版を入れ替える手順、旧版へ戻す手順、自動起動の扱い、うまく動かないときの確認先をまとめる。

旧版 PowerShell 版のコード・設定・スクリプト・スケジュールタスクの定義は**消していない**。新版で困ったときは、この文書の「5. 復元（新版 → 旧版）」でいつでも旧版へ戻せる。

関連文書:

- [承認済み spec](superpowers/specs/2026-09-15-inzone-minimal-csharp-design.md) — 新版の設計と境界
- [実装計画](superpowers/plans/2026-09-15-inzone-minimal-csharp.md) — 段階1〜4の実行手順。本書のコマンドは §10「移行と復元の手順」から写した
- [実機確認の記録](manual-test-minimal-csharp.md) — 各段階で実機を確認した手順と結果

## 2. 前提

- Windows 11
- .NET 10 デスクトップランタイム。端末には .NET 10 SDK があり、デスクトップランタイムはそれに含まれる。新版はフレームワーク依存の publish なので、ランタイムが無いと起動しない
- 管理者権限は要らない。この文書のコマンドはすべて通常の権限で動く
- 配置先: `%LOCALAPPDATA%\AutoSwitchForInzone\bin`（exe と dll）
- 設定: `%LOCALAPPDATA%\AutoSwitchForInzone\config.json`
- ログ: `%LOCALAPPDATA%\AutoSwitchForInzone\logs\`
- 自動起動: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値名 `AutoSwitchForInzone`
- 旧版の配置先は `%LOCALAPPDATA%\INZONE-AutoSwitch` で、新版とは完全に分かれている
- 以下、`<repo>` はこのリポジトリのルートを指す

## 3. 移行（旧版 → 新版）

**旧版と新版を同時に動かさないこと。** 新版の Mutex は新版どうしの二重起動しか防がず、**旧版との同時起動は防がない**。両方が動くと、どちらも Buds の接続・切断に合わせて切り替えるので、互いに既定デバイスを奪い合う（旧版は ActionLog.log を、新版は Buds のドングルを見て判定する）。

**手順3が関門である。** プロセス数が `0` になるのを確かめるまで、手順4（配置）と手順5（起動）へ進まない。`Stop-AutoSwitchProcess` は停止の失敗を警告にとどめるため、コマンドがエラーを出さなかったことは旧版が止まった根拠にならない。

```powershell
# 1. 旧版の自動起動を止める（定義は残す）
Disable-ScheduledTask -TaskName 'INZONE Auto Switch'
(Get-ScheduledTask -TaskName 'INZONE Auto Switch').State   # Disabled になっていること

# 2. 実行中の旧版を止める
. <repo>\scripts\Common.ps1
Stop-AutoSwitchProcess

# 3. 止まったことを自分で確かめる。0 になるまで次へ進まない
#    Stop-AutoSwitchProcess は停止の失敗を警告にとどめるため、成否を戻り値では判断できない
#    powershell -Command で実行すると自分の command line もパターンに一致するため、自分（$PID）を除いて数える
@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*INZONE-AutoSwitch.ps1*' }).Count

# 4. 新版を配置する（config.json と logs\ には触らない）
powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\scripts\Install-TrayApp.ps1

# 5. 起動する。以後はトレイメニューの「Windows ログオン時の自動起動」を ON にする
Start-Process "$env:LOCALAPPDATA\AutoSwitchForInzone\bin\AutoSwitch.App.exe"
```

起動したら、トレイアイコンを右クリックして「**Windows ログオン時の自動起動(&A)**」を選び、チェックが付いたことを確かめる。配置しても起動しても自動では登録されないので、ここで ON にしないと次回のログオンでは起動しない。

設定は旧版から自動では移らない。旧版の `config.json` から必要な値を手動で `%LOCALAPPDATA%\AutoSwitchForInzone\config.json` へ転記する。設定の変更は新版の再起動で反映される。

## 4. 更新（新版の入れ替え）

トレイの「終了」で終わらせてから `Install-TrayApp.ps1` を実行し、もう一度起動する。配置先のパスが変わらないので、Run キーの値はそのままでよい。プロセスが残っている間は配置スクリプトがエラーで中断するので、そこが関門になる。

## 5. 復元（新版 → 旧版）

**手順3が関門である。** `AutoSwitch.App` のプロセス数が `0` になるのを確かめるまで、手順4（旧版の有効化と起動）へ進まない。**トレイアイコンが消えたことは、新版が止まった根拠にならない。** アイコンは「終了」を選んだ時点で消えるが、監視スレッドの停止を待つ分だけプロセスは残りうる。

```powershell
# 1. 新版のトレイメニューで自動起動を OFF にし、チェックが外れたことを確かめる
Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name AutoSwitchForInzone -ErrorAction SilentlyContinue   # 何も返らないこと

# 2. トレイメニューの「終了」を選ぶ
# 3. 新版が終わったことを自分で確かめる。0 になるまで次へ進まない
#    アイコンは終了を選んだ時点で消えるが、監視スレッドの停止を待つ分だけプロセスは残りうる（§6）
@(Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue).Count

# 4. 旧版を戻す
Enable-ScheduledTask -TaskName 'INZONE Auto Switch'
Start-ScheduledTask  -TaskName 'INZONE Auto Switch'
```

新版のファイルと設定はそのまま残るので、あとでもう一度「3. 移行」を踏めば新版へ戻せる。

## 6. 困ったとき

### ログを読む

`%LOCALAPPDATA%\AutoSwitchForInzone\logs\AutoSwitchForInzone.log`。トレイメニューの「ログフォルダを開く(&L)」からも開ける。各行は `yyyy-MM-dd HH:mm:ss.fff [レベル] メッセージ` の形式である。見どころ:

- `[ERROR]` — 切り替えや自動起動の登録に失敗した行
- `[WARN]` — Buds のドングルを読めない、登録状態を読み取れない、といった行
- `自動起動:` — 起動時に Run キーを読めたときに `[INFO]` で1行出る。そのとき Run キーがどうだったかが分かる（例: `自動起動: 有効（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）`）。読めなかったときはこの行の代わりに `[WARN] 自動起動の登録状態を読み取れません` が出る。また `config.json` の `logLevel` を `Warn` か `Error` にしているとこの `[INFO]` の行は落ちる（既定は `Info`）。**この行が無いことは、新版が起動しなかった証拠にはならない。** いま動いているかどうかは、`@(Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue).Count` で見る
- `録音の切り替え:` — 起動時に `[INFO]` で1行出る。録音の切り替え先の設定が分かる（例: `録音の切り替え: 接続時=ヘッドセット マイク (INZONE Buds - Chat) / 切断時=（未設定）`）。「7. 録音デバイスも切り替える」を参照
- `監視対象: INZONE Buds の USB ドングル（054c:0ec2）` — 起動時に `[INFO]` で1行出る。新版は INZONE Hub の `ActionLog.log` ではなく、Buds の USB ドングルに直接問い合わせて接続・切断を判定する（Hub は起動していなくてよい）
- `INZONE Buds のドングルを読めません。` — `[WARN]`。ドングルが見つからない・開けない・読み書きに失敗したときに、読めなくなった時点で1回だけ出る（例: `INZONE Buds のドングルを読めません。5 秒おきに探し直します: INZONE Buds のドングルが見つかりません`。秒数は `idlePollIntervalSeconds`）。読めない間は状態を変えず、切り替えもしない。ドングルが挿さっているか、ほかのソフトが独占していないかを確かめる
- `INZONE Buds のドングルを読めるようになりました。` — `[INFO]`。読めない状態から戻った印。このあと今の状態に合わせて1回切り替える
- `現在の状態を Connected と判定しました。` / `現在の状態を Disconnected と判定しました。` — `[INFO]`。起動時と、ドングルを読めるようになったときに出る。前の状態と同じでも、今の状態に合わせて1回切り替える。`config.json` の `startupSyncMaxAgeMinutes` が `0` のときは、続けて `startupSyncMaxAgeMinutes が 0 のため切り替えません。` が付き、切り替えない
- `状態遷移を検知しました:` — Buds の接続・切断を拾えているか。片耳でも着けていれば Connected のまま。ふたを閉じてから遅くとも 5 秒で Disconnected になる
- `INZONE Buds のドングルを 2 秒で閉じられませんでした。` — `[WARN]`。終了のときにドングルを閉じるのを 2 秒で打ち切った印。待たずに終了処理を進める
- `既定の再生デバイスを切り替えました:` — 実際に切り替えたか
- `既定の録音デバイスを切り替えました:` — 録音も実際に切り替えたか。`録音デバイスが見つかりません:` は、設定した名前が有効な録音デバイスの一覧に無いことを表す
- `設定画面で保存しました:` — 設定画面で保存した6項目。この行より後の接続・切断から新しい設定で切り替わる。保存に失敗したときは `[WARN] 設定を保存できません:` が出る。「8. 設定画面」を参照

設定ファイルはトレイメニューの「設定ファイルを開く(&C)」から開ける。

### 自動起動が効かない

次を確認する。

```powershell
Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name AutoSwitchForInzone -ErrorAction SilentlyContinue
```

- キー: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- 値名: `AutoSwitchForInzone`
- 値: exe のフルパスを引用符で囲んだもの（引数なし）

メニューのチェックは、メニューを開くたびに Run キーを読み直して合わせている。値が無ければ OFF である。メニュー項目が選べず「（状態を読み取れません）」と付いているときは、登録状態を読めていない。その場合はチェックの表示を信用せず、上のコマンドで実際の値を見る。

### exe を別のフォルダへ移すとき

移動の検知と自動修復は作っていない。フォルダを変えるときは、次の順で行う。

1. トレイメニューで自動起動を **OFF** にする
2. トレイメニューの「**終了**」を選ぶ
3. `AutoSwitch.App` のプロセス数が **0** になったことを確かめる。0 になるまで次へ進まない

   ```powershell
   @(Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue).Count
   ```

4. フォルダを移す
5. 移した先の exe を起動し、メニューで自動起動を **ON** にし直す

OFF にせずに移すと、Run キーには古いパスが残ったままになり、次のログオンで起動に失敗する。

**手順2と手順3を飛ばさないこと。** 自動起動を OFF にしても、動いている新版はそのまま動き続ける。古いプロセスが残ったまま移動先の exe を起動しても、単一起動の Mutex を古いプロセスが握っているので、2つ目は何もせず終了コード0 で即座に終わる。新しいインスタンスにはならない。そのうえで古いプロセスのメニューから自動起動を ON にすると、そのプロセスが**起動したときのパス**（＝移動前の古いパス）が Run キーへ書かれるので、直したつもりで直っていない状態になる。ここでも「5. 復元」と同じく、トレイアイコンが消えたことは新版が止まった根拠にならない。

## 7. 録音デバイスも切り替える

再生デバイスに加えて、録音デバイス（マイク）の既定も接続・切断に合わせて切り替えられる。**初期状態では録音は切り替えない。**使うには「8. 設定画面」で録音デバイスを選んで保存する（再起動は要らない）。手で書く場合は、`%LOCALAPPDATA%\AutoSwitchForInzone\config.json` に次の2つのキーを書き足し、新版を再起動する（手で編集した内容はアプリの起動時にだけ読む）。

```json
"budsInputDevice": "ヘッドセット マイク (INZONE Buds - Chat)",
"speakerInputDevice": "マイク (USB Microphone)"
```

- `budsInputDevice` は接続時、`speakerInputDevice` は切断時の録音の切り替え先。値は Windows のサウンド設定に表示される録音デバイスの名前をそのまま書く（大文字小文字は区別しない）
- キーが無い、または値が空（空白だけを含む）の向きでは、録音は切り替えない。片方だけ書けば、その向きだけ切り替わる
- `switchOnConnect` / `switchOnDisconnect` を `false` にした向きでは、再生も録音も切り替えない
- 切り替えは再生 → 録音の順に行い、片方が見つからない・失敗しても、もう片方は試みる
- 録音だけを止めたいときは、2つのキーの値を空にして再起動する

名前を正確に確かめるには、開発用ツールで有効な録音デバイスの一覧を表示する（読み取りだけで、既定は変えない）。

```powershell
dotnet run --project <repo>\tools\AudioSmoke -- capture list
```

## 8. 設定画面

トレイメニューの「設定(&S)...」、またはトレイアイコンのダブルクリックで開く。開いているときにもう一度開くと、同じ画面が前面に出る。

- 変えられるのは、接続時・切断時それぞれの再生デバイスと録音デバイス、「接続したら切り替える」「切断したら切り替える」の6項目だけ。ポーリング間隔・ログの設定などは従来どおり `config.json` を手で編集し、再起動で反映する
- 選択肢は、いま Windows で有効なデバイスの一覧。設定されているデバイスが一覧に無い（Buds をつないでいないなど）ときは「（未接続）」付きで表示され、そのまま保存すれば設定は変わらない
- 録音の「（未設定）」を選ぶと、その向きの録音は切り替えない
- **保存すると再起動せずに反映する。ただし、その場ではデバイスを切り替えない。**次の接続・切断から新しい設定を使う。すぐ変えたいときは Windows のサウンド設定で変える
- 保存では `config.json` の6項目のキーだけを書き換え、ほかのキーの値と順は残す（空白や改行の位置は整え直す）
- 画面を開いている間に `config.json` を手で編集した場合、保存すると6項目は画面の値、それ以外は保存した時点のファイルの値になる。6項目以外の手の編集は再起動するまで効かない
- `config.json` が無い・壊れているときは、画面の上部に理由が出て保存できない。手で直してから開き直す
- 保存に失敗したときはダイアログが出て、画面は閉じず、動作中の設定も変わらない。`config.json.tmp` が残ったと表示されたら、アプリを終了してから消してよい（次の保存でも上書きされる）
