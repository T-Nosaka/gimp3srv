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
    /// json-escape : 文字列をJSON文字列リテラルとして安全な形にエスケープする
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
""";
}