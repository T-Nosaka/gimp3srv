using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace gimp3svr;


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