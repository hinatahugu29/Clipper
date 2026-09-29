# ClipMaster (仮称)

Clibor 風の操作感で、**テキストと画像（スクリーンショット含む）** の履歴を扱えるクリップボード管理ツール。
仕様は [REQUIREMENTS.md](REQUIREMENTS.md) を参照。

## ビルド・実行

前提: .NET 8 SDK（`winget install Microsoft.DotNet.SDK.8`）

```powershell
cd src\ClipMaster
dotnet run -c Release
```

単一 exe の発行:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

テスト:

```powershell
dotnet test
```

## 使い方

| 操作 | 内容 |
| :--- | :--- |
| `Ctrl` 2回押し / `Ctrl+Shift+V` | ポップアップを呼び出し（設定で変更可） |
| `↑` `↓` / クリック | 選択 |
| `Enter` | 直前のウィンドウへ貼り付け |
| `1`〜`9`（検索欄が空のとき） | その番号の行を即貼り付け（`Shift`+数字でコピーのみ。設定で無効化可） |
| `Space`（検索欄が空のとき） | プレビュー（画像は大きく、テキストは全文）。もう一度 `Space` / `Esc` で閉じる |
| `Shift+Enter` | クリップボードへ入れるだけ |
| `Delete` | 履歴を削除（検索欄が空のとき。`Ctrl+Delete` は常時） |
| `Tab` | 履歴 / 定型文タブの切替 |
| そのまま入力 / `Ctrl+F` | インクリメンタル検索 |
| 右クリック（履歴の行） | 「定型文に登録」（テキストのみ。タイトルは 1 行目から自動、重複は登録しない）/「削除」（定型文タブの行は「削除」のみ） |
| `Ctrl+P` / 📌 ボタン | ピン留め。枠外クリックや貼り付けでも閉じず、連続して貼り付けられる（貼り付け先は最後に触ったウィンドウ） |
| `Esc` / 枠外クリック | 閉じる（ピン中は `Esc` かホットキーで閉じ、ピンも解除） |

トレイアイコンの右クリックで 表示 / 定型文編集 / 履歴全消去 / 設定 / 終了。定型文では `{DATE}` `{TIME}` が貼り付け時に展開されます。

### 定型文のインポート / エクスポート

定型文編集画面の左下のボタンから CSV / TXT で入出力できます（拡張子 `.csv` は CSV、それ以外は TXT）。同一内容（カテゴリ・タイトル・本文）は重複としてスキップされます。

- **CSV**: `カテゴリ,タイトル,本文`（UTF-8 BOM 付き、ヘッダー行は任意）
- **TXT**: `=====` 区切りのブロック。区切りのない TXT はファイル全体を 1 件の定型文として取り込みます。

```
=====
カテゴリ: 業務
タイトル: 挨拶

本文（複数行可）
```

## データ

既定の保管先は `%LOCALAPPDATA%\ClipMaster\`（設定画面で変更可。変更時は移動の確認あり）。

- `clipmaster.db` … 履歴・定型文 (SQLite)
- `images/` … 原寸PNG（ファイル名は SHA-256）、`thumbs/` … サムネイル
- `config.json` … 設定（保管先を変えても見つかるよう、常に既定位置に置く）
- `error.log` … 例外ログ

環境変数 `CLIPMASTER_HOME` で既定位置を上書きできます（テスト・ポータブル運用用）。
`CLIPMASTER_DEBUG=1` で `debug.log` に診断ログを出力します。

## 構成

```
src/ClipMaster
├─ Native/      Win32 P/Invoke
├─ Services/    クリップボード監視・ホットキー・貼り付け・画像保管・設定
├─ Data/        SQLite / モデル
└─ UI/          ポップアップ・設定・定型文エディタ
tests/ClipMaster.Tests   xUnit（DB・重複排除・上限削除・保管先移動・ホットキー解析など）
```
