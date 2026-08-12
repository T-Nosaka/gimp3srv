# gimp3srv

GIMP 3 を Model Context Protocol (MCP) から操作する MCP サーバーです。
Claude Desktop など MCP をサポートするクライアントから接続すると、LLM が GIMP 3 の
Script-Fu (Scheme) を実行して画像の加工・構造の把握・プレビューの書き出しなどを行えるように
なります。プロセス間通信は標準入出力の stdio トランスポートを使用します。

## 提供する MCP ツール

| ツール | 概要 |
| --- | --- |
| `RunScriptFu` | 生の Script-Fu (Scheme) コードを実行するエスケープハッチ。画像の読み込み・加工・保存などを自由に記述できる |
| `GetImageInfo` | 画像ファイルの構成（サイズ・全レイヤーのツリー、グループは再帰展開）を JSON で返す。テキストレイヤーは `type:"text"` となり、font/fontSize/color/justification/text 等の属性も併せて返す |
| `ListTextLayers` | 画像内のテキストレイヤーのみを平坦リストで返す。`GetImageInfo` より軽量で、`EditTextLayers` を適用する前の対象把握に使う |
| `EditTextLayers` | 複数のテキストレイヤーを1呼出でバッチ編集し、`savePath`（省略時は上書き）に保存する。省略した項目は現状維持 |
| `ExportPreview` | 画像を PNG として書き出す（縦横比を保った縮小・表示レイヤー絞り込み可） |

`GimpTools` の各ツールは `gimp-console` のバッチ実行を介して GIMP 3 を操作します。
LLM が自力では書きにくい処理（レイヤーグループの再帰走査・テキスト属性の設定等）をツール側に
固定化するのが高レベルツールの狙いです。各ツールの詳細な利用条件はツールの Description を
参照してください。

### テキストレイヤーの編集フロー

```
GetImageInfo / ListTextLayers   →  EditTextLayers       →  ExportPreview
  対象レイヤーIDと属性を把握       指定項目のみ更新し保存    書き出して目視確認
```

`EditTextLayers` の `edits` 引数は JSON 配列で、各要素に `layerId` と変更したい項目だけを
指定します。省略した項目は現状維持されるため、フォントだけ変えたい場合等に便利です。

```jsonc
[
  { "layerId": 4,  "text": "新しいテキスト", "fontSize": 64 },
  { "layerId": 16, "color": "#FF8800", "justification": "center" }
]
```

指定可能な項目: `text`, `font`, `fontSize`, `color`(`#RRGGBB` or `#RRGGBBAA`),
`justification`(`left`/`right`/`center`/`fill`), `lineSpacing`, `letterSpacing`,
`antialias`, `visible`, `opacity`

## 前提 / 要件

- GIMP 3 がインストール済みで `gimp-console` (Windows では `gimp-console-3.0.exe`) が実行できること
- .NET 10 SDK（ビルド・publish で使用）
- MCP クライアント：Claude Desktop など

## インストール

### 1. ビルド

```bash
dotnet build gimp3srv/gimp3srv.csproj
```

### 2. 単一実行ファイルの生成（Publish）

実行環境に合わせて、.NET ランタイム不要で動く自己完結型の単一実行ファイルを生成します。

```bash
# macOS (Apple Silicon)
dotnet publish gimp3srv/gimp3srv.csproj -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true

# macOS (Intel)
dotnet publish gimp3srv/gimp3srv.csproj -c Release -r osx-x64 --self-contained true /p:PublishSingleFile=true

# Windows
dotnet publish gimp3srv/gimp3srv.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

# Linux
dotnet publish gimp3srv/gimp3srv.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
```

生成された単一実行ファイルは `gimp3srv/bin/Release/net10.0/<RID>/publish/` に出力されます。

## OpenCode での登録

OpenCode を利用中の環境で、publish した `gimp3srv` 実行ファイルを直接指定して MCP サーバー
として登録します。`~/.config/opencode/opencode.jsonc` に以下を追加してください。

**macOS の場合:**

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "gimp3srv": {
      "type": "local",
      "command": [
        "/Users/<username>/gimp3srv/gimp3srv",
        "--gimpconsolepath",
        "/Applications/GIMP.app/Contents/MacOS/gimp-console"
      ],
      "enabled": true
    }
  }
}
```

**Windows の場合:**

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "gimp3srv": {
      "type": "local",
      "command": [
        "C:\\gimp3srv\\gimp3srv.exe",
        "--gimpconsolepath",
        "C:\\Program Files\\GIMP 3\\bin\\gimp-console-3.0.exe"
      ],
      "enabled": true
    }
  }
}
```

- `command[0]` に publish で生成した `gimp3srv` 実行ファイルの絶対パスを指定します
- `command[1..]` に `--gimpconsolepath` と GIMP 3 の `gimp-console` 実行ファイルの絶対パスを指定します
  - 上記例は典型的なインストール先のパスです。実際の環境に合わせて読み替えてください

### 起動引数

- `--gimpconsolepath <PATH>`: `gimp-console` の実行ファイルの絶対パス（必須）

## Claude Desktop での登録

`claude/gimp3srv/` 配下に Claude Desktop 用の MCP バンドル一式（実行ファイル配置用の
ディレクトリと `manifest.json`）が含まれています。配布先が Windows の場合は、publish で
生成した `win-x64/gimp3srv.exe` を `claude/gimp3srv/win-x64/` に配置するだけで利用できます。

`manifest.json`:

```json
{
  "manifest_version": "0.2",
  "name": "gimp3srv",
  "version": "1.0.0",
  "description": "Gimp3 mcp server",
  "author": { "name": "T.Nosaka" },
  "user_config": {
    "gimpconsolepath": {
      "type": "string",
      "title": "gimpconsole path",
      "description": "required gimp console execute path",
      "sensitive": false,
      "required": true
    }
  },
  "server": {
    "type": "binary",
    "entry_point": "win-x64/gimp3srv.exe",
    "mcp_config": {
      "command": "${__dirname}/win-x64/gimp3srv.exe",
      "args": [
        "--gimpconsolepath",
        "${user_config.gimpconsolepath}"
      ]
    }
  },
  "license": "MIT"
}
```

ユーザーは Claude Desktop 上で「gimp-console の実行ファイルパス」を入力するだけで
gimp3srv が起動します。

> **Note:** 現状の `manifest.json` は Windows 向けのパスになっています。
> macOS / Linux のクライアントで利用する場合は、`entry_point` および
> `mcp_config.command` を対象 RID の実行ファイルパス（`osx-arm64/gimp3srv` など）に
> 書き換えてください。

## ライセンス

MIT (see [LICENSE](LICENSE))