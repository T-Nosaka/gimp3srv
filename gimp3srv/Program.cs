using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace gimp3svr;

/// <summary>
/// コーディング規約 
/// 
/// Script-Fuの埋め込みについて
/// 注意: $$"""...""" 内で動的コードを埋め込むには文字列連結を使うこと。
/// {targetCode} と書くと補間されずリテラル "{targetCode}" として出力される。
/// 
/// MCPツールのパラメータ定義について
/// 注意: nullable パラメータ (string?) は opencode から呼び出すと JSON パースエラーになる。
/// 空文字列をデフォルト値にすること (string = "")。
/// 
/// 実装上の注意(実機検証で判明した点):
/// ・コマンドライン引数(-b "(load ...)")にコードを直書きすると、Windowsの引数パースで
///   ダブルクォートや括弧が分割されて壊れることがある。そのため --batch=- を指定し、
///   標準入力(stdin)経由でコードを渡す方式にしている。
/// ・(display ...) の出力はこのバッチ実行方式では標準出力に出てこない。
///   結果を返したいスクリプトは (gimp-message "...") を使うこと(標準エラーに出力される)。
/// ・GIMP起動時の "GIMP-警告: Welcome to GIMP x.x.x!" は正常時にも毎回出るメッセージであり、
///   エラー判定には使わない。
/// ・GIMP 3.2.4 の TinyScheme では string-contains や gimp-pdb-query-procedures 等が未定義。
///   文字列包含は substring で比較する自前実装が必要。
/// ・GIMP 3.2.4 の Script-Fu では gimp-file-load に PNG パスを渡すと失敗する
///   (file-png-load が GFile を要求するため)。文字列パスで読めるのは XCF のみ。
/// ・GIMP 3.2.4 で gimp-text-layer-get-color は ((r g b a)) のネストpair、値は整数0-255。
///   color->hex は (car list) ではなく (car (car list)) で内側のpairを取得する必要がある。
/// ・gimp-layer-new の引数順が GIMP 3 で変更(レイヤ名の位置)。公式移行ガイドを参照。
/// ・gimp-image-insert-layer の第3引数は親レイヤID (0=ルート)。#f は不可。
/// ・バイナリデプロイは cp→mv でアトミックに。書き換え中のspawnは .NET プロセスが
///   ページイン中にクラッシュする("server unavailable" の主要因)。
/// </summary>
 
internal class Program
{
    /// <summary>
    /// エントリ
    /// </summary>
    static async Task Main(string[] args)
    {
        //コマンドライン引数を解析する
        var commandargs = Parser.Default.ParseArguments<CommandArgs>(args);

        string? gimpConsolePath = null;
        commandargs.WithParsed(parsed => gimpConsolePath = parsed.GimpConsolePath);

        // Windows(日本語環境)ではリダイレクトされたstdin/stdoutの既定コンソールコードページが
        // UTF-8ではないことがあり、MCPのJSON-RPCメッセージに含まれる非ASCII文字（日本語のウィンドウ
        // タイトル・ラベル等）が文字化けする。stdio経由でJSON-RPCをやり取りする前に明示的にUTF-8へ固定する。
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        var builder = Host.CreateApplicationBuilder(args);

        // stdout は MCP JSON-RPC 専用のため、コンソールログを完全に無効化する
        // （.NET ホストのログが stdout に混入すると Claude Desktop が JSON エラーを出す）
        builder.Logging.ClearProviders();

        // 障害調査用に標準エラーへ警告以上のログを出す(Trace/Debugは開発時のみ有効化する想定)
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // GIMP関連サービスをDIコンテナへ登録する
        builder.Services.AddSingleton(new GimpOptions { GimpConsolePath = gimpConsolePath });
        builder.Services.AddSingleton<GimpConsoleRunner>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        var host = builder.Build();
        await host.RunAsync();
    }

    /// <summary>
    /// 実行引数管理
    /// </summary>
    public class CommandArgs
    {
        /// <summary>
        /// GimpConsoleパス
        /// --gimppath
        /// </summary>
        [CommandLine.Option("gimpconsolepath")]
        public string? GimpConsolePath { get; set; } = null;

    }
}