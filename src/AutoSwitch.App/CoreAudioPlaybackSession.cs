using AutoSwitch.Audio;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// 段階1の CoreAudioEndpointService を Core の IPlaybackSession に合わせる。
/// 切り替えのたびに作られ、Dispose でサービスを破棄する。
/// </summary>
internal sealed class CoreAudioPlaybackSession : IPlaybackSession
{
    private readonly CoreAudioEndpointService _service = new();

    public IReadOnlyList<PlaybackDevice> GetPlaybackDevices() =>
        _service.GetPlaybackEndpoints().Select(ToDevice).ToList();

    public PlaybackDevice? GetDefaultPlayback() =>
        _service.GetDefaultPlayback() is { } endpoint ? ToDevice(endpoint) : null;

    public void SetDefaultPlayback(string id) => _service.SetDefaultPlayback(id);

    public void Dispose() => _service.Dispose();

    private static PlaybackDevice ToDevice(AudioEndpoint endpoint) => new(endpoint.Id, endpoint.Name);
}
