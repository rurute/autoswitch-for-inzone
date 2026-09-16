using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 終了経路でのログ書き込み。ファイル I/O 自体には期限を付けられないので、
/// 別スレッドへ出して待つ時間だけを区切る。待ち切れなければ、その行は諦めて先へ進む。
/// 諦めたことを同じロガーへ書くことはできない。
/// </summary>
internal static class BoundedLog
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    internal static void Write(IAppLogger logger, LogLevel level, string message)
    {
        try
        {
            var write = Task.Run(() => logger.Write(level, message));
            write.Wait(WriteTimeout);
        }
        catch (Exception)
        {
            // FileLogger は例外を外へ出さないのでここへは来ない想定。
            // 来たとしても、終了を止めない
        }
    }
}
