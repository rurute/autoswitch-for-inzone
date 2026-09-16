using System.Text;
using AutoSwitch.Audio;

// 段階1の開発用コンソール。実機の音声操作を手動で確認するためのもので、製品には含めない。録音（capture）は Issue rurute/inzone-auto-switch#8 で追加。
// 終了コード: 0 = 成功、1 = 音声 API の失敗・対象デバイス不在、2 = 引数不正（音声を操作しない）

Console.OutputEncoding = Encoding.UTF8;

if (args is not ["list"] and not ["current"] and not ["set", _]
    and not ["capture", "list"] and not ["capture", "current"] and not ["capture", "set", _])
{
    Console.Error.WriteLine(
        """
        Usage:
          AudioSmoke list                有効な再生デバイスの ID・名前と現在の既定出力を表示
          AudioSmoke current             現在の既定出力を表示
          AudioSmoke set <ID>            指定の出力へ変更し、変更後の既定出力を再取得して表示
          AudioSmoke capture list        有効な録音デバイスの ID・名前と現在の既定入力を表示
          AudioSmoke capture current     現在の既定入力を表示
          AudioSmoke capture set <ID>    指定の入力へ変更し、変更後の既定入力を再取得して表示
                                         （ID の事前確認をせず、サービスの検証をそのまま通す）
        """);
    return 2;
}

try
{
    using var service = new CoreAudioEndpointService();
    if (args is ["capture", "list"])
    {
        var endpoints = service.GetEndpoints(AudioFlow.Capture);
        var current = service.GetDefault(AudioFlow.Capture);
        Console.WriteLine($"Recording devices (active): {endpoints.Count}");
        foreach (var endpoint in endpoints)
        {
            var marker = endpoint.Id == current?.Id ? "*" : " ";
            Console.WriteLine($"{marker} {Format(endpoint)}");
        }

        Console.WriteLine($"Default recording: {Format(current)}");
    }
    else if (args is ["capture", "set", var captureId])
    {
        // 既存の set と違い、ツール側で ID を確かめない。別の流れの ID を渡したときに、
        // サービスの「同じ流れの有効な一覧に ID があるか」の検証が働くことを実機で確かめるため
        service.SetDefault(AudioFlow.Capture, captureId);
        var current = service.GetDefault(AudioFlow.Capture);
        Console.WriteLine($"Requested: id={captureId}");
        Console.WriteLine($"Default recording: {Format(current)}");
        if (current?.Id != captureId)
        {
            Console.Error.WriteLine("error: the default recording device read back after the change is not the requested device");
            return 1;
        }
    }
    else if (args is ["capture", "current"])
    {
        Console.WriteLine($"Default recording: {Format(service.GetDefault(AudioFlow.Capture))}");
    }
    else if (args[0] == "list")
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
