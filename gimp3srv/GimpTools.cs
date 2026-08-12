using System.ComponentModel;
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
        "画像サイズと、全レイヤーのツリー構造(名前・ID・グループか否か・表示状態・不透明度・" +
        "サイズ・オフセット)を返す。レイヤーグループの中身も children として再帰的に含まれるため、" +
        "このツールを使えば全レイヤーを漏れなく把握できる。" +
        "画像の内容そのものを見たい場合は ExportPreview を使うこと。")]
    public async Task<string> GetImageInfo(
        [Description("読み込む画像ファイルの絶対パス")] string filePath,
        [Description("タイムアウト秒数(既定60秒)")] int timeoutSeconds = 60)
    {
        var path = GimpScriptHelper.ToSchemeString(filePath);

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

(define (layer->json item)
  (let* ((name (car (gimp-item-get-name item)))
         (is-group (= (car (gimp-item-is-group item)) TRUE))
         (offsets (gimp-drawable-get-offsets item)))
    (string-append
      "{\"id\":" (number->string item)
      ",\"name\":\"" (json-escape name) "\""
      ",\"isGroup\":" (if is-group "true" "false")
      ",\"visible\":" (bool->json (car (gimp-item-get-visible item)))
      ",\"opacity\":" (number->string (car (gimp-layer-get-opacity item)))
      ",\"width\":" (number->string (car (gimp-drawable-get-width item)))
      ",\"height\":" (number->string (car (gimp-drawable-get-height item)))
      ",\"offsetX\":" (number->string (car offsets))
      ",\"offsetY\":" (number->string (cadr offsets))
      (if is-group
          (string-append ",\"children\":" (layers->json (car (gimp-item-get-children item))))
          "")
      "}")))

(define (layers->json layer-vec)
  (let ((num (vector-length layer-vec)))
    (let loop ((i 0) (acc "["))
      (if (>= i num)
          (string-append acc "]")
          (loop (+ i 1)
                (string-append acc
                               (if (> i 0) "," "")
                               (layer->json (vector-ref layer-vec i))))))))

(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{{path}}" "{{path}}"))))
  (gimp-message (string-append
    "RESULT_JSON:{"
    "\"width\":" (number->string (car (gimp-image-get-width image)))
    ",\"height\":" (number->string (car (gimp-image-get-height image)))
    ",\"layers\":" (layers->json (car (gimp-image-get-layers image)))
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
    (apply-visibility (car (gimp-image-get-layers image)) target-names))
""";
        }

        var code = GimpScriptHelper.CommonSchemePrelude + $$"""

(define (name-in-list? name lst)
  (cond ((null? lst) #f)
        ((string=? name (car lst)) #t)
        (else (name-in-list? name (cdr lst)))))

(define (apply-visibility layer-vec target-names)
  (let ((num (vector-length layer-vec)))
    (let loop ((i 0))
      (if (< i num)
          (let* ((item (vector-ref layer-vec i))
                 (name (car (gimp-item-get-name item)))
                 (is-group (= (car (gimp-item-is-group item)) TRUE))
                 (hit (name-in-list? name target-names)))
            (gimp-item-set-visible item (if hit TRUE FALSE))
            (if (and is-group hit)
                (gimp-item-set-visible item TRUE))
            (if is-group
                (apply-visibility (car (gimp-item-get-children item)) target-names))
            (loop (+ i 1)))))))

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