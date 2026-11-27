# バージョン番号の表示 — 実機確認記録

- 対象 spec: [`superpowers/specs/2026-11-27-version-display-design.md`](superpowers/specs/2026-11-27-version-display-design.md)（Issue #2）
- 記録方針: シリアル番号・ユーザー名など、個人・端末固有の値は写さない。ログの行は、日時・レベル・本文をそのまま写す。

## 分担

- **作者**: `scripts/Install-TrayApp.ps1` での配置と起動、トレイメニューとエクスプローラーの表示の確認。
- **作業環境**: ビルドした成果物とログ（`%LOCALAPPDATA%\AutoSwitchForInzone\logs\AutoSwitchForInzone.log`）の読み取り、判定と記録。

## 結果

| # | 確かめること | 結果 | 証跡・メモ |
|---|---|---|---|
| 1 | ビルドした `AutoSwitch.App.dll` の informational version が `0.1.0+<40 文字のコミット SHA>` になる | 合格 | 2026-11-27。`cf918a4` でビルドした dll の文字列に `0.1.0+cf918a4060988124c79f7cc16b5eeb0a79a4809b` がある（ファイルのバージョンは `0.1.0.0`） |
| 2 | 起動するとログに `[INFO] AutoSwitch for INZONE 0.1.0+<7 文字> を開始します。` が 1 行出て、7 文字が配置したコミットと合う | 未確認 | |
| 3 | トレイメニューの「終了(&X)」のすぐ上に `AutoSwitch for INZONE 0.1.0` が灰色で出て、押しても何も起きない | 未確認 | |
| 4 | エクスプローラーで exe のプロパティ →「詳細」タブに、ファイル バージョン `0.1.0.0`、製品バージョン `0.1.0+…` が出る | 未確認 | |
