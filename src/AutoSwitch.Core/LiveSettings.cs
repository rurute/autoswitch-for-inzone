namespace AutoSwitch.Core;

/// <summary>
/// 動作中の設定の入れ物（Issue rurute/inzone-auto-switch#9、spec §6）。中身は不変の AppSettings で、参照の入れ替えだけを行う。
/// 監視スレッドが読み、UI スレッドだけが入れ替える。Volatile で読み書きし、古い参照を読み続けないようにする。
/// </summary>
public sealed class LiveSettings
{
    private AppSettings _current;

    public LiveSettings(AppSettings initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _current, settings);
    }
}
