using System.Text;
using System.Text.RegularExpressions;

namespace gimp3svr;

/// <summary>
/// 高レベルツールが生成するScript-Fuコードの共通処理。
/// ・パス文字列をSchemeの文字列リテラルとして安全に埋め込む
/// ・gimp-messageの出力から "RESULT_JSON:" 行だけを取り出す
/// ・Script-Fu側で使う共通のヘルパー関数(JSONエスケープ等)を提供する
/// という共通処理をここに集約する。
/// </summary>
public static class GimpScriptHelper
{
    private const string ResultMarker = "RESULT_JSON:";

    /// <summary>
    /// ファイルパスをScheme文字列リテラルとして安全な形に変換する。
    /// (バックスラッシュを/に統一し、ダブルクォートをエスケープする)
    /// </summary>
    public static string ToSchemeString(string path)
    {
        return path.Replace("\\", "/").Replace("\"", "\\\"");
    }

    /// <summary>
    /// ユーザ入力文字列(テキストレイヤー本文等)をScheme文字列リテラルとして安全な形に変換する。
    /// ToSchemeString に加え、改行・タブ・制御文字をSchemeの文字列エスケープ表現に変換する。
    /// </summary>
    public static string ToSchemeTextLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append($"\\u{((int)c):x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// stderr全体から "RESULT_JSON:" で始まる行を探し、そのJSON部分だけを返す。
    /// 見つからない場合は null。
    /// </summary>
    public static string? ExtractResultJson(string stdErr)
    {
        foreach (var line in stdErr.Split('\n'))
        {
            var trimmed = line.Trim();
            var idx = trimmed.IndexOf(ResultMarker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return trimmed[(idx + ResultMarker.Length)..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Script-Fu側で使う共通ヘルパー関数の定義。
    /// 各高レベルツールが生成するスクリプトの先頭に付与する。
    /// ・json-escape       : 文字列をJSON文字列リテラルとして安全な形にエスケープする
    /// ・bool->json       : TRUE/FALSE を true/false にする
    /// ・justification->name : PDBの整数enumを left/right/center/fill の文字列にする
    /// ・color->hex       : GIMP 3 の ((r g b a)) pair を #RRGGBB または #RRGGBBAA にする
    /// ・get-layer-type   : itemが text / raster / group / layermask / channel のいずれかを返す
    ///                       引数はレイヤーオブジェクト(ベクタ)。内部で (vector-ref obj 0) からIDを取得する。
    /// </summary>
    public const string CommonSchemePrelude = """
(define (json-escape s)
  (let loop ((chars (string->list s)) (acc ""))
    (if (null? chars)
        acc
        (let ((c (car chars)))
          (loop (cdr chars)
                (string-append acc
                  (cond ((char=? c #\") "\\\"")
                        ((char=? c #\\) "\\\\")
                        ((char=? c #\newline) "\\n")
                        ((char=? c #\tab) "\\t")
                        (else (string c)))))))))
(define (bool->json b) (if (= b TRUE) "true" "false"))
(define (justification->name n)
  (cond ((= n 0) "left")
        ((= n 1) "right")
        ((= n 2) "center")
        ((= n 3) "fill")
        (else "unknown")))
; GIMP 3.2.4: gimp-text-layer-get-color は ((r g b a)) のネストpairを返す。
; 値は整数(0-255)。
(define (color->hex rgba-list)
  (let* ((inner (car rgba-list))
         (r (car inner))
         (g (cadr inner))
         (b (caddr inner))
         (a (if (>= (length inner) 4) (cadddr inner) 255))
         (hex2 (lambda (n)
                 (let ((s (number->string n 16)))
                   (if (< (string-length s) 2) (string-append "0" s) s)))))
    (string-append "#" (hex2 r) (hex2 g) (hex2 b)
                   (if (< a 255) (hex2 a) ""))))
; GIMP 3.2.4 でのレイヤー型判定。
; gimp-image-get-layers はレイヤーオブジェクト(ベクタ)のpairを返す。
; ベクタの先頭要素 ((vector-ref obj 0)) が数値ID。
; gimp-item-id-is-* 関数に ID を渡し、pair 戻り値から car で bool 値を取得する。
(define (get-layer-type layer-obj)
  (let ((id (vector-ref layer-obj 0)))
    (cond ((= (car (gimp-item-id-is-layer-mask id)) TRUE) "layermask")
          ((= (car (gimp-item-id-is-channel id)) TRUE) "channel")
          ((= (car (gimp-item-is-group id)) TRUE) "group")
          ((= (car (gimp-item-id-is-text-layer id)) TRUE) "text")
          (else "raster"))))
""";
}