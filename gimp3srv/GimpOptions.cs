namespace gimp3svr;

/// <summary>
/// gimp-console 呼び出しに関する設定。
/// 起動引数 --gimpconsolepath から Program.cs で設定され、DIコンテナにSingletonとして登録される。
/// </summary>
public class GimpOptions
{
    /// <summary>
    /// gimp-console(またはgimp-console-x.y.exe)の絶対パス
    /// </summary>
    public string? GimpConsolePath { get; set; }
}