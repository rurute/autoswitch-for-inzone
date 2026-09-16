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

**旧版と新版を同時に動かさないこと。** 新版の Mutex は新版どうしの二重起動しか防がず、**旧版との同時起動は防がない**。両方が動くと、同じ ActionLog.log を見て互いに既定デバイスを奪い合う。

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
- `[WARN]` — ActionLog.log を読めない、登録状態を読み取れない、といった行
- `自動起動:` — 起動時に Run キーを読めたときに `[INFO]` で1行出る。そのとき Run キーがどうだったかが分かる（例: `自動起動: 有効（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）`）。読めなかったときはこの行の代わりに `[WARN] 自動起動の登録状態を読み取れません` が出る。また `config.json` の `logLevel` を `Warn` か `Error` にしているとこの `[INFO]` の行は落ちる（既定は `Info`）。**この行が無いことは、新版が起動しなかった証拠にはならない。** いま動いているかどうかは、`@(Get-Process -Name 'AutoSwitch.App' -ErrorAction SilentlyContinue).Count` で見る
- `状態遷移を検知しました:` — Buds の接続・切断を拾えているか
- `既定の再生デバイスを切り替えました:` — 実際に切り替えたか

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
