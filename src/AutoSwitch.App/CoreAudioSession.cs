using AutoSwitch.Audio;
using AutoSwitch.Core;

namespace AutoSwitch.App;

/// <summary>
/// Audio の CoreAudioEndpointService を Core の IAudioSession に合わせる。DeviceFlow を AudioFlow に対応づける。
/// 切り替えのたびに作られ、Dispose でサービスを破棄する。
/// </summary>
internal sealed class CoreAudioSession : IAudioSession
{
    private readonly CoreAudioEndpointService _service = new();

    public IReadOnlyList<AudioDevice> GetDevices(DeviceFlow flow) =>
        _service.GetEndpoints(ToAudioFlow(flow)).Select(ToDevice).ToList();

    public IReadOnlyList<AudioDevice?> GetDefaults(DeviceFlow flow) =>
        _service.GetDefaults(ToAudioFlow(flow)).Select(endpoint => endpoint is null ? null : ToDevice(endpoint)).ToList();

    public void SetDefault(DeviceFlow flow, string id) => _service.SetDefault(ToAudioFlow(flow), id);

    public void Dispose() => _service.Dispose();

    private static AudioFlow ToAudioFlow(DeviceFlow flow) => flow switch
    {
        DeviceFlow.Playback => AudioFlow.Render,
        DeviceFlow.Capture => AudioFlow.Capture,
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, "未知の DeviceFlow です。"),
    };

    private static AudioDevice ToDevice(AudioEndpoint endpoint) => new(endpoint.Id, endpoint.Name);
}
