using System.Diagnostics;
using System.Text;

namespace gimp3svr;

/// <summary>
/// Script-Fu(Scheme)コードを gimp-console のバッチモードで実行するランナー。
/// AIから渡された生のScript-Fuコードをそのまま実行し、
/// 標準出力・標準エラー・終了コードを構造化して返すことに専念する。
///
/// 実装上の注意(実機検証で判明した点):
/// ・コマンドライン引数(-b "(load ...)")にコードを直書きすると、Windowsの引数パースで
///   ダブルクォートや括弧が分割されて壊れることがある。そのため --batch=- を指定し、
///   標準入力(stdin)経由でコードを渡す方式にしている。
/// ・(display ...) の出力はこのバッチ実行方式では標準出力に出てこない。
///   結果を返したいスクリプトは (gimp-message "...") を使うこと(標準エラーに出力される)。
/// ・GIMP起動時の "GIMP-警告: Welcome to GIMP x.x.x!" は正常時にも毎回出るメッセージであり、
///   エラー判定には使わない。
/// </summary>
public class GimpConsoleRunner
{
    private readonly GimpOptions _options;

    public GimpConsoleRunner(GimpOptions options)
    {
        _options = options;
    }

    public async Task<GimpScriptResult> RunScriptFuAsync(
        string scriptFuCode,
        int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.GimpConsolePath))
        {
            throw new InvalidOperationException(
                "GimpConsolePath が設定されていません。起動引数 --gimpconsolepath を確認してください。");
        }

        if (!File.Exists(_options.GimpConsolePath))
        {
            throw new FileNotFoundException(
                $"gimp-console の実行ファイルが見つかりません: {_options.GimpConsolePath}");
        }

        var utf8NoBom = new UTF8Encoding(false);

        var psi = new ProcessStartInfo
        {
            FileName = _options.GimpConsolePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = utf8NoBom,
            StandardOutputEncoding = utf8NoBom,
            StandardErrorEncoding = utf8NoBom,
        };
        psi.ArgumentList.Add("-i");                       // GUIウィンドウを開かない
        psi.ArgumentList.Add("-d");                        // データ(タイル等)を読み込まない高速化オプション
        //psi.ArgumentList.Add("-f");                      // フォント未読込(テキスト編集で必要になるため外す)
        psi.ArgumentList.Add("--batch-interpreter=plug-in-script-fu-eval");
        psi.ArgumentList.Add("--batch=-");                  // 標準入力からScheme式を読み込む

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdOutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stdErrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // コマンドライン引数への直書きではなく、標準入力経由でコードを渡す
        await process.StandardInput.WriteLineAsync(scriptFuCode);
        await process.StandardInput.WriteLineAsync("(gimp-quit 0)");
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* 既に終了済みの場合など */ }
        }

        var stdOut = stdOutBuilder.ToString();
        var stdErr = stdErrBuilder.ToString();

        // "GIMP-警告: Welcome to GIMP x.x.x!" は正常時にも毎回出るため誤検知しないよう、
        // "Error:" 系の文字列のみをエラー判定に使う。
        var hasErrorMarker =
            stdErr.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            stdOut.Contains("Error:", StringComparison.OrdinalIgnoreCase);

        return new GimpScriptResult
        {
            Success = !timedOut && process.ExitCode == 0 && !hasErrorMarker,
            ExitCode = timedOut ? -1 : process.ExitCode,
            StdOut = stdOut,
            StdErr = stdErr,
            TimedOut = timedOut,
        };
    }
}

/// <summary>
/// gimp-console実行結果
/// </summary>
public class GimpScriptResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
}