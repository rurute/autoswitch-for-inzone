using System.Text;
using AutoSwitch.Audio;

// 段階1の開発用コンソール。実機の音声操作を手動で確認するためのもので、製品には含めない。
// 終了コード: 0 = 成功、1 = 音声 API の失敗・対象デバイス不在、2 = 引数不正（音声を操作しない）

Console.OutputEncoding = Encoding.UTF8;

if (args is not ["list"] and not ["current"] and not ["set", _])
{
    Console.Error.WriteLine(
        """
        Usage:
          AudioSmoke list        有効な再生デバイスの ID・名前と現在の既定出力を表示
          AudioSmoke current     現在の既定出力を表示
          AudioSmoke set <ID>    指定の出力へ変更し、変更後の既定出力を再取得して表示
        """);
    return 2;
}

try
{
    using var service = new CoreAudioEndpointService();
    if (args[0] == "list")
    {
        var endpoints = service.GetPlaybackEndpoints();
        var current = service.GetDefaultPlayback();
        Console.WriteLine($"Playback devices (active): {endpoints.Count}");
        foreach (var endpoint in endpoints)
        {
            var marker = endpoint.Id == current?.Id ? "*" : " ";
            Console.WriteLine($"{marker} {Format(endpoint)}");
        }

        Console.WriteLine($"Default playback: {Format(current)}");
    }
    else if (args[0] == "set")
    {
        var target = service.GetPlaybackEndpoints().FirstOrDefault(endpoint => endpoint.Id == args[1]);
        if (target is null)
        {
            Console.Error.WriteLine($"error: no active playback device has id={args[1]} (run 'list' to see the IDs)");
            return 1;
        }

        service.SetDefaultPlayback(target.Id);
        var current = service.GetDefaultPlayback();
        Console.WriteLine($"Requested: {Format(target)}");
        Console.WriteLine($"Default playback: {Format(current)}");
        if (current?.Id != target.Id)
        {
            Console.Error.WriteLine("error: the default playback device read back after the change is not the requested device");
            return 1;
        }
    }
    else
    {
        Console.WriteLine($"Default playback: {Format(service.GetDefaultPlayback())}");
    }

    return 0;
}
catch (AudioServiceException ex)
{
    Console.Error.WriteLine($"error: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: unexpected {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
    return 1;
}

static string Format(AudioEndpoint? endpoint) =>
    endpoint is null ? "(none)" : $"id={endpoint.Id} name={endpoint.Name}";
