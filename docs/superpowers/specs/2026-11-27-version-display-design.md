# バージョン番号を持たせ、起動ログとトレイメニューに出す（Issue #2）

## 目的

不具合の報告を受けたときに、どの版の話かを分かるようにする。いまはバージョン番号をどこにも持っておらず、exe も既定の 1.0.0.0 のままである。

## ゴール / 非ゴール

- ゴール: 番号の元を `Directory.Build.props` の `<Version>` の 1 か所にする。最初の番号は `0.1.0`。
- ゴール: 起動ログに 1 行、コミットの短い印付きで出す（例: `AutoSwitch for INZONE 0.1.0+adba3fe を開始します。`）。開発中のビルドでもどのコミットか分かる。
- ゴール: トレイメニューの「終了」の上に、押せない項目で出す（例: `AutoSwitch for INZONE 0.1.0`）。`+` より前だけを出す。
- ゴール: 番号の読み取りと整形をテストできる形にする。
- 非ゴール: ツールチップ（63 文字までで、接続状態の表示に使っている）。
- 非ゴール: 設定画面のタイトル（メニューで見られれば足りる）。
- 非ゴール: exe のプロパティ（`<Version>` を置けば SDK が自動で入れる）。

## 制約（調べて分かったこと）

- .NET 8 以降の SDK は、`AssemblyInformationalVersion` の `+` の後ろに **40 文字のコミット SHA** を付ける。Issue の例の短い形にはならないので、ログ用にはコードで 7 文字に切る。
- git のないところでビルドすると `+` 以降が付かない。そのときは `0.1.0` だけを出す。

## 検討した案

- **A. 整形を純粋な関数にして App に置く（採用）**。`AppVersion` が informational version の文字列を受け取り、ログ用・メニュー用の文字列を返す。テストは文字列を渡すだけで書ける。
- B. MSBuild で短い SHA を作って埋め込む: `git` の呼び出しをビルドに足すことになり、git のない環境の扱いも増える。コードで切れば済むので外した。
- C. `IncludeSourceRevisionInInformationalVersion` を切って番号だけにする: 開発中のビルドでどのコミットか分からなくなり、目的に反する。

## 設計

- `Directory.Build.props` に `<Version>0.1.0</Version>`。
- `src/AutoSwitch.App/AppVersion.cs`（internal static）:
  - `ForLog(string informational)`: `+` があれば、前の部分 + `+` + 後ろの先頭 7 文字。後ろが 7 文字以下ならそのまま。`+` がなければそのまま。
  - `ForDisplay(string informational)`: `+` より前。
  - `Current`: 実行中のアセンブリの `AssemblyInformationalVersionAttribute` を読む。読めなければ `"不明"`（`ForLog` / `ForDisplay` にそのまま通る）。
- `Program.cs` の開始ログを `$"AutoSwitch for INZONE {AppVersion.ForLog(AppVersion.Current)} を開始します。"` にする。
- トレイメニュー: 自動起動 → 区切り線 → `AutoSwitch for INZONE {ForDisplay}`（`Enabled = false`）→ 終了(&X)。
- テスト: `tests/AutoSwitch.App.Tests/AppVersionTests.cs` で、40 文字 SHA 付き・`+` なし・短い印・空の後ろ、を確かめる。
