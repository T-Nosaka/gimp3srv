using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace gimp3svr;

/// <summary>
/// GIMP 3 を操作するためのMCPツール群。
///
/// 構成方針:
/// ・RunScriptFu       : 何でもできるエスケープハッチ(生のScript-Fuコードを実行)
/// ・GetImageInfo      : 画像とレイヤーツリーの構造をJSONで取得(グループを再帰展開)
/// ・ExportPreview     : PNG画像として書き出す(AIが目視確認するため)
///
/// 高レベルツールは「AIが自力では書きにくい/間違えやすい処理」を固定化することを目的とする。
/// 特にGIMP 3では gimp-image-get-layers がルートレイヤーのみを返すため、
/// レイヤーグループの中身を再帰的に辿る処理をツール側に持たせている。
/// </summary>
[McpServerToolType]
public class GimpTools
{
    private readonly GimpConsoleRunner _runner;

    public GimpTools(GimpConsoleRunner runner)
    {
        _runner = runner;
    }

    [McpServerTool, Description(
        "GIMP 3 の Script-Fu(Scheme)コードを実行する。画像の読み込み・加工・保存などをScript-Fuの式で" +
        "自由に記述できる。" +
        "重要: (display ...) の出力は結果として取得できない。値や状態を返したい場合は" +
        "必ず (gimp-message (string-append ...)) を使うこと(標準エラー出力として返る)。" +
        "また GIMP 3 の一部PDB関数(例: gimp-image-get-layers)は戻り値の形が" +
        "GIMP 2系から変わっている(count+arrayではなくarrayのみ等)ため注意すること。" +
        "PNG書き出しは gimp-file-save ではなく (file-png-export RUN-NONINTERACTIVE image path (vector drawable)) を使う。" +
        "戻り値は [status]/[exitCode]/[stdout]/[stderr] の形式で返る。")]
    public async Task<string> RunScriptFu(
        [Description("実行するScript-Fu(Scheme)のコード全体。結果はgimp-messageで出力すること。")] string code,
        [Description("タイムアウト秒数(既定60秒)")] int timeoutSeconds = 60)
    {
        var result = await _runner.RunScriptFuAsync(code, timeoutSeconds);

        var status = result.TimedOut
            ? "TIMEOUT"
            : result.Success ? "SUCCESS" : "FAILED";

        return $"[status] {status}\n" +
               $"[exitCode] {result.ExitCode}\n" +
               $"[stdout]\n{result.StdOut}\n" +
               $"[stderr]\n{result.StdErr}";
    }

    [McpServerTool, Description(
        "画像ファイル(.xcf/.png/.jpg等)の構造をJSONで取得する。" +
        "画像サイズと、全レイヤーのツリー構造(名前・ID・種別・表示状態・不透明度・" +
        "サイズ・オフセット)を返す。レイヤーグループの中身も children として再帰的に含まれるため、" +
        "このツールを使えば全レイヤーを漏れなく把握できる。" +
        "テキストレイヤーの場合は type=\"text\" になり、font/fontSize/color/justification/" +
        "lineSpacing/letterSpacing/text 等の属性も併せて返す。" +
        "画像の内容そのものを見たい場合は ExportPreview を使うこと。")]
    public async Task<string> GetImageInfo(
        [Description("読み込む画像ファイルの絶対パス")] string filePath,
        [Description("タイムアウト秒数(既定60秒)")] int timeoutSeconds = 60)
    {
        var path = GimpScriptHelper.ToSchemeString(filePath);

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

(define (text-attrs-json item)
  (let* ((font-size-pair (gimp-text-layer-get-font-size item))
         (font-size (car font-size-pair))
         ; GIMP 3 では gimp-text-layer-get-font は フォントID(数値)を返す。
         ; 名前文字列を得るには gimp-resource-get-name でフォントIDを渡す。
         (font-id (car (gimp-text-layer-get-font item)))
         (font-name (car (gimp-resource-get-name font-id)))
         (just (car (gimp-text-layer-get-justification item)))
         (color (gimp-text-layer-get-color item))
         (line-spacing (car (gimp-text-layer-get-line-spacing item)))
         (letter-spacing (car (gimp-text-layer-get-letter-spacing item)))
         (aa (car (gimp-text-layer-get-antialias item)))
         (text (car (gimp-text-layer-get-text item))))
    (string-append
      ",\"font\":\"" (json-escape font-name) "\""
      ",\"fontSize\":" (number->string font-size)
      ",\"color\":\"" (color->hex color) "\""
      ",\"justification\":\"" (justification->name just) "\""
      ",\"lineSpacing\":" (number->string line-spacing)
      ",\"letterSpacing\":" (number->string letter-spacing)
      ",\"antialias\":" (bool->json aa)
      ",\"text\":\"" (json-escape text) "\"")))

;; layer-obj: ベクタ(60要素)。先頭 (vector-ref layer-obj 0) が数値ID。
(define (layer->json layer-obj)
  (let* ((id (vector-ref layer-obj 0))
         (name (car (gimp-item-get-name id)))
         (is-group (= (car (gimp-item-is-group id)) TRUE))
         (is-text (= (car (gimp-item-id-is-text-layer id)) TRUE))
         (ltype (get-layer-type layer-obj))
         (offsets (gimp-drawable-get-offsets id)))
    (string-append
      "{\"id\":" (number->string id)
      ",\"name\":\"" (json-escape name) "\""
      ",\"type\":\"" ltype "\""
      ",\"isGroup\":" (if is-group "true" "false")
      ",\"isText\":" (if is-text "true" "false")
      ",\"visible\":" (bool->json (car (gimp-item-get-visible id)))
      ",\"opacity\":" (number->string (car (gimp-layer-get-opacity id)))
      ",\"width\":" (number->string (car (gimp-drawable-get-width id)))
      ",\"height\":" (number->string (car (gimp-drawable-get-height id)))
      ",\"offsetX\":" (number->string (car offsets))
      ",\"offsetY\":" (number->string (cadr offsets))
      (if is-text (text-attrs-json id) "")
      (if is-group
          (string-append ",\"children\":" (layers->json (gimp-item-get-children id)))
          "")
      "}")))

;; GIMP 3.2.4: gimp-image-get-layers はレイヤーオブジェクト(ベクタ)のpairを返す。
(define (layers->json layer-list)
  (let loop ((lst layer-list) (acc "[") (first #t))
    (if (null? lst)
        (string-append acc "]")
        (loop (cdr lst)
              (string-append acc
                             (if first "" ",")
                             (layer->json (car lst)))
              #f))))

(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{{path}}" "{{path}}"))))
  (gimp-message (string-append
    "RESULT_JSON:{"
    "\"width\":" (number->string (car (gimp-image-get-width image)))
    ",\"height\":" (number->string (car (gimp-image-get-height image)))
    ",\"layers\":" (layers->json (gimp-image-get-layers image))
    "}"))
  (gimp-image-delete image))
""";

        var result = await _runner.RunScriptFuAsync(code, timeoutSeconds);
        var json = GimpScriptHelper.ExtractResultJson(result.StdErr);

        if (json != null)
        {
            return json;
        }

        return $"[status] FAILED (結果JSONを取得できませんでした)\n" +
               $"[exitCode] {result.ExitCode}\n" +
               $"[stdout]\n{result.StdOut}\n" +
               $"[stderr]\n{result.StdErr}";
    }

    [McpServerTool, Description(
        "画像ファイル内のテキストレイヤーのみを一覧にしてJSONで返す。" +
        "GetImageInfo は全レイヤー(背景・グループ・ラスタライズ済み等)を含むため重くなるが、" +
        "本ツールはテキストレイヤーだけを平坦リストで返し、id/name/font/fontSize/color/" +
        "justification/lineSpacing/letterSpacing/text 等の属性を併せて返す。" +
        "レイヤーグループ内のテキストレイヤーも再帰的に収集する。" +
        "EditTextLayers を適用する前の対象把握に使うこと。")]
    public async Task<string> ListTextLayers(
        [Description("読み込む画像ファイルの絶対パス")] string filePath,
        [Description("タイムアウト秒数(既定60秒)")] int timeoutSeconds = 60)
    {
        var path = GimpScriptHelper.ToSchemeString(filePath);

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

;; layer-obj: ベクタ(60要素)。先頭 (vector-ref layer-obj 0) が数値ID。
(define (text-layer->json layer-obj)
  (let* ((id (vector-ref layer-obj 0))
         (name (car (gimp-item-get-name id)))
         (font-size-pair (gimp-text-layer-get-font-size id))
         (font-size (car font-size-pair))
         (font-id (car (gimp-text-layer-get-font id)))
         (font-name (car (gimp-resource-get-name font-id)))
         (just (car (gimp-text-layer-get-justification id)))
         (color (gimp-text-layer-get-color id))
         (line-spacing (car (gimp-text-layer-get-line-spacing id)))
         (letter-spacing (car (gimp-text-layer-get-letter-spacing id)))
         (aa (car (gimp-text-layer-get-antialias id)))
         (text (car (gimp-text-layer-get-text id)))
         (offsets (gimp-drawable-get-offsets id)))
    (string-append
      "{\"id\":" (number->string id)
      ",\"name\":\"" (json-escape name) "\""
      ",\"font\":\"" (json-escape font-name) "\""
      ",\"fontSize\":" (number->string font-size)
      ",\"color\":\"" (color->hex color) "\""
      ",\"justification\":\"" (justification->name just) "\""
      ",\"lineSpacing\":" (number->string line-spacing)
      ",\"letterSpacing\":" (number->string letter-spacing)
      ",\"antialias\":" (bool->json aa)
      ",\"visible\":" (bool->json (car (gimp-item-get-visible id)))
      ",\"opacity\":" (number->string (car (gimp-layer-get-opacity id)))
      ",\"width\":" (number->string (car (gimp-drawable-get-width id)))
      ",\"height\":" (number->string (car (gimp-drawable-get-height id)))
      ",\"offsetX\":" (number->string (car offsets))
      ",\"offsetY\":" (number->string (cadr offsets))
      ",\"text\":\"" (json-escape text) "\"")))

; レイヤー群(pair)からテキストレイヤーだけを集めてJSON配列を組み立てる。
(define (collect-text-layers layer-list acc first)
  (let loop ((lst layer-list) (acc acc) (first first))
    (if (null? lst)
        acc
        (let* ((layer-obj (car lst))
               (id (vector-ref layer-obj 0))
               (is-group (= (car (gimp-item-is-group id)) TRUE))
               (is-text  (= (car (gimp-item-id-is-text-layer id)) TRUE)))
          (cond
            (is-text
              (loop (cdr lst)
                    (string-append acc (if first "" ",") (text-layer->json layer-obj))
                    #f))
            (is-group
              (loop (cdr lst) (collect-text-layers (gimp-item-get-children id) acc first) first))
            (else
              (loop (cdr lst) acc first)))))))

(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{{path}}" "{{path}}"))))
  (gimp-message (string-append
    "RESULT_JSON:{"
    "\"textLayers\":" (string-append "[" (collect-text-layers (gimp-image-get-layers image) "" #t) "]")
    "}"))
  (gimp-image-delete image))
""";

        var result = await _runner.RunScriptFuAsync(code, timeoutSeconds);
        var json = GimpScriptHelper.ExtractResultJson(result.StdErr);

        if (json != null)
        {
            return json;
        }

        return $"[status] FAILED (結果JSONを取得できませんでした)\n" +
               $"[exitCode] {result.ExitCode}\n" +
               $"[stdout]\n{result.StdOut}\n" +
               $"[stderr]\n{result.StdErr}";
    }

    [McpServerTool, Description(
        "画像ファイル内のテキストレイヤーをバッチ編集する。" +
        "edits に編集対象レイヤーIDごとに変更箇所を指定する。指定した項目だけが更新され、" +
        "省略した項目は現状維持される。編集後は savePath に保存される(savePath省略時は元ファイルへ上書き)。" +
        "autoResize=true(既定)の場合は各テキストレイヤーのサイズをテキスト内容に合わせて再計算し、" +
        "戻り値に更新後の width/height/offset を含める。" +
        "対象レイヤーIDや現在の属性は ListTextLayers / GetImageInfo で取得すること。")]
    public async Task<string> EditTextLayers(
        [Description("編集対象の画像ファイル(.xcf)の絶対パス")] string filePath,
        [Description(
            "編集内容のJSON配列。各要素は {\"layerId\":<id>, " +
            "\"text\":<string>?, \"font\":<string>?, \"fontSize\":<number>?, " +
            "\"color\":<#RRGGBB|#RRGGBBAA>?, \"justification\":<left|right|center|fill>?, " +
            "\"lineSpacing\":<number>?, \"letterSpacing\":<number>?, " +
            "\"antialias\":<bool>?, \"visible\":<bool>?, \"opacity\":<0-100>?}。" +
            "省略した項目は変更しない。layerId は必須。")] string edits,
        [Description(
            "保存先の絶対パス。省略時は filePath に上書き保存する。" +
            "上書き保存は内部的にテンポラリファイルへ保存してから置換するため、" +
            "安全に保存できる。")] string? savePath = null,
        [Description(
            "テキスト変更後にレイヤーサイズを本文に合わせて再計算するか。既定 true。" +
            "レイアウト崩れを嫌う場合は false を指定。")] bool autoResize = true,
        [Description("タイムアウト秒数(既定120秒)")] int timeoutSeconds = 120)
    {
        var inPath = GimpScriptHelper.ToSchemeString(filePath);

        // 上書き保存の場合は GIMP が開いているファイルへ書き込めないため、
        // 一旦テンポラリファイルへ保存し、スクリプト終了後に元の場所へ置換する。
        var finalOutPath = savePath ?? filePath;
        bool overwrite = string.Equals(finalOutPath, filePath, StringComparison.OrdinalIgnoreCase);
        var tempPath = overwrite
            ? Path.Combine(Path.GetTempPath(), $"gimp3srv_{Guid.NewGuid():N}.xcf")
            : finalOutPath;
        var schemeOutPath = GimpScriptHelper.ToSchemeString(tempPath);

        // edits(JSON配列)をパースして、1レイヤー分のScheme編集コードを組み立てる。
        var editLines = BuildEditLines(edits, autoResize);

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

; HEX文字列(#RRGGBB または #RRGGBBAA)を R/G/B/A の整数リストに変換。
; GIMP 3.2.4: gimp-text-layer-set-color は整数(0-255)を要求する。
(define (hex->color hex)
  (let ((h (if (char=? (string-ref hex 0) #\#) (substring hex 1) hex)))
    (let ((r (string->number (substring h 0 2) 16))
          (g (string->number (substring h 2 4) 16))
          (b (string->number (substring h 4 6) 16))
          (a (if (= (string-length h) 8)
                 (string->number (substring h 6 8) 16)
                 255)))
      (list r g b a))))

(define (justification->enum name)
  (cond ((string=? name "left") 0)
        ((string=? name "right") 1)
        ((string=? name "center") 2)
        ((string=? name "fill") 3)
        (else 0)))

; 1件分の編集適用シーケンス。各 blending は『値が渡されていれば変更する』形。
; gimp-text-layer-set-font は GIMP 3 ではフォントID(数値)を要求するため、
; ユーザが指定した「フォント名文字列」は gimp-font-get-by-name でIDに変換する。
(define (apply-edit layer-id text font font-size color justification line-spacing letter-spacing antialias visible opacity auto-resize)
  (let* ((item layer-id)
         (is-text (= (car (gimp-item-id-is-text-layer item)) TRUE)))
    (if (not is-text)
        (gimp-message (string-append "SKIP(id=" (number->string layer-id) "): テキストレイヤーではない"))
        (begin
          (if (string? text)         (gimp-text-layer-set-text item text))
          (if (string? font)         (gimp-text-layer-set-font item (car (gimp-font-get-by-name font))))
          (if (number? font-size)    (gimp-text-layer-set-font-size item font-size UNIT-PIXEL))
          (if (string? color)        (let ((rgba (hex->color color)))
                                      (gimp-text-layer-set-color item (car rgba) (cadr rgba) (caddr rgba) (cadddr rgba))))
          (if (string? justification) (gimp-text-layer-set-justification item (justification->enum justification)))
          (if (number? line-spacing) (gimp-text-layer-set-line-spacing item line-spacing))
          (if (number? letter-spacing) (gimp-text-layer-set-letter-spacing item letter-spacing))
          (if (boolean? antialias)   (gimp-text-layer-set-antialias item antialias))
          (if (boolean? visible)     (gimp-item-set-visible item (if visible TRUE FALSE)))
          (if (number? opacity)      (gimp-layer-set-opacity item opacity))
          (if auto-resize
              (gimp-text-layer-resize item))
          (let* ((offsets (gimp-drawable-get-offsets item)))
            (gimp-message (string-append
              "RESULT_EDIT:" (number->string layer-id)
              "," (number->string (car (gimp-drawable-get-width item)))
              "," (number->string (car (gimp-drawable-get-height item)))
              "," (number->string (car offsets))
              "," (number->string (cadr offsets)))))))))

(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{{inPath}}" "{{inPath}}"))))
  {{editLines}}
  ; 保存。GIMP 3 では gimp-xcf-save-image ではなく gimp-xcf-save。
  ; 上書き保存の場合は一旦テンポラリファイルへ保存し、C#側で元ファイルを置換する。
  (gimp-xcf-save RUN-NONINTERACTIVE image "{{schemeOutPath}}")
  (gimp-message (string-append "RESULT_JSON:{\"savedPath\":\"" (json-escape "{{schemeOutPath}}") "\"}"))
  (gimp-image-delete image))
""";

        var result = await _runner.RunScriptFuAsync(code, timeoutSeconds);
        var json = GimpScriptHelper.ExtractResultJson(result.StdErr);

        // 保存に成功し、かつ上書きモードならテンポラリファイルを元の位置へ置換する。
        if (json != null)
        {
            if (overwrite && File.Exists(tempPath))
            {
                try
                {
                    File.Move(tempPath, finalOutPath, overwrite: true);
                }
                catch
                {
                    // 置換に失敗した場合でもテンポラリを残す。
                    return json + $" (注意: 元ファイルの置換に失敗しました。テンポラリ: {tempPath})";
                }
            }
            return json;
        }

        // 保存失敗時はテンポラリを掃除しておく。
        if (overwrite && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* 排他の都合上 Ignore */ }
        }

        return $"[status] FAILED (編集または保存に失敗しました)\n" +
               $"[exitCode] {result.ExitCode}\n" +
               $"[stdout]\n{result.StdOut}\n" +
               $"[stderr]\n{result.StdErr}";
    }

    /// <summary>
    /// EditTextLayers に渡された edits(JSON配列)をパースし、
    /// 各要素に対する apply-edit 呼出の Scheme コードを連結して返す。
    /// 渡されなかった項目は Scheme 側で '() (nil) を渡すことで「変更しない」を表現する。
    /// </summary>
    private static string BuildEditLines(string editsJson, bool autoResize)
    {
        using var doc = JsonDocument.Parse(editsJson);
        var sb = new StringBuilder();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("layerId", out var idEl))
            {
                continue;
            }
            int layerId = idEl.GetInt32();

            string text = el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? SchemeStr(t.GetString()) : "()";
            string font = el.TryGetProperty("font", out var f) && f.ValueKind == JsonValueKind.String ? SchemeStr(f.GetString()) : "()";
            string fontSize = el.TryGetProperty("fontSize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetDouble().ToString(CultureInfo.InvariantCulture) : "()";
            string color = el.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? SchemeStr(c.GetString()) : "()";
            string just = el.TryGetProperty("justification", out var j) && j.ValueKind == JsonValueKind.String ? SchemeStr(j.GetString()) : "()";
            string line = el.TryGetProperty("lineSpacing", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetDouble().ToString(CultureInfo.InvariantCulture) : "()";
            string letter = el.TryGetProperty("letterSpacing", out var lt) && lt.ValueKind == JsonValueKind.Number ? lt.GetDouble().ToString(CultureInfo.InvariantCulture) : "()";
            string aa = el.TryGetProperty("antialias", out var aab) && aab.ValueKind == JsonValueKind.False ? "#f" : aab.ValueKind == JsonValueKind.True ? "#t" : "()";
            string visible = el.TryGetProperty("visible", out var vb) && vb.ValueKind == JsonValueKind.False ? "#f" : vb.ValueKind == JsonValueKind.True ? "#t" : "()";
            string opacity = el.TryGetProperty("opacity", out var op) && op.ValueKind == JsonValueKind.Number ? op.GetDouble().ToString(CultureInfo.InvariantCulture) : "()";
            string autoR = autoResize ? "#t" : "#f";

            sb.AppendLine($"  (apply-edit {layerId} {text} {font} {fontSize} {color} {just} {line} {letter} {aa} {visible} {opacity} {autoR})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// C#文字列をSchemeのダブルクォート文字列リテラルとして埋め込める形に変換。
    /// </summary>
    private static string SchemeStr(string? s)
    {
        return "\"" + GimpScriptHelper.ToSchemeTextLiteral(s ?? string.Empty) + "\"";
    }

    [McpServerTool, Description(
        "画像ファイル(.xcf等)をPNGとして書き出す。書き出した画像は別途画像表示ツールで開いて" +
        "内容を目視確認できる。maxWidth を指定すると縦横比を保ったまま縮小して書き出すため、" +
        "大きな画像でも扱いやすい。" +
        "visibleLayerNames を指定すると、そこに挙げたレイヤーのみを表示して書き出す" +
        "(特定のレイヤーだけの状態を確認したい場合に使う)。" +
        "元のファイルは変更されない。")]
    public async Task<string> ExportPreview(
        [Description("読み込む画像ファイルの絶対パス")] string filePath,
        [Description("書き出し先のPNGファイル絶対パス")] string outputPath,
        [Description("書き出す最大幅(px)。これを超える場合は縦横比を保って縮小する。0で縮小しない。既定1000")] int maxWidth = 1000,
        [Description("表示するレイヤー名の配列。指定した場合、ここに無いレイヤーは非表示にして書き出す。未指定なら元の表示状態のまま。")] string[]? visibleLayerNames = null,
        [Description("タイムアウト秒数(既定120秒)")] int timeoutSeconds = 120)
    {
        var inPath = GimpScriptHelper.ToSchemeString(filePath);
        var outPath = GimpScriptHelper.ToSchemeString(outputPath);

        // 表示レイヤー指定がある場合、Scheme のリストリテラルを組み立てる
        var visibilityCode = "";
        if (visibleLayerNames is { Length: > 0 })
        {
            var names = string.Join(" ",
                visibleLayerNames.Select(n => $"\"{GimpScriptHelper.ToSchemeString(n)}\""));
            visibilityCode = $$"""
  (let ((target-names (list {{names}})))
    (apply-visibility (gimp-image-get-layers image) target-names))
""";
        }

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

(define (name-in-list? name lst)
  (cond ((null? lst) #f)
        ((string=? name (car lst)) #t)
        (else (name-in-list? name (cdr lst)))))

(define (apply-visibility layer-list target-names)
  (let loop ((lst layer-list))
    (if (not (null? lst))
        (let* ((layer-obj (car lst))
               (id (vector-ref layer-obj 0))
               (name (car (gimp-item-get-name id)))
               (is-group (= (car (gimp-item-is-group id)) TRUE))
               (hit (name-in-list? name target-names)))
          (gimp-item-set-visible id (if hit TRUE FALSE))
          (if (and is-group hit)
              (gimp-item-set-visible id TRUE))
          (if is-group
              (apply-visibility (gimp-item-get-children id) target-names))
          (loop (cdr lst))))))

(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{{inPath}}" "{{inPath}}")))
       (w (car (gimp-image-get-width image)))
       (h (car (gimp-image-get-height image)))
       (max-w {{maxWidth}})
       (do-scale (and (> max-w 0) (> w max-w)))
       (new-w (if do-scale max-w w))
       (new-h (if do-scale (round (* h (/ max-w w))) h)))
{{visibilityCode}}
  (if do-scale
      (gimp-image-scale image new-w new-h))
  (let ((flat (car (gimp-image-flatten image))))
    (file-png-export RUN-NONINTERACTIVE image "{{outPath}}" (vector flat)))
  (gimp-message (string-append
    "RESULT_JSON:{\"outputPath\":\"" (json-escape "{{outPath}}") "\""
    ",\"width\":" (number->string new-w)
    ",\"height\":" (number->string new-h)
    ",\"sourceWidth\":" (number->string w)
    ",\"sourceHeight\":" (number->string h)
    "}"))
  (gimp-image-delete image))
""";

        var result = await _runner.RunScriptFuAsync(code, timeoutSeconds);
        var json = GimpScriptHelper.ExtractResultJson(result.StdErr);

        if (json != null)
        {
            return json;
        }

        return $"[status] FAILED (書き出しに失敗しました)\n" +
               $"[exitCode] {result.ExitCode}\n" +
               $"[stdout]\n{result.StdOut}\n" +
               $"[stderr]\n{result.StdErr}";
    }
}