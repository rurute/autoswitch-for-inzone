namespace AutoSwitch.Audio;

/// <summary>再生デバイス。Id は Core Audio のエンドポイント ID、Name は表示名。</summary>
public sealed record AudioEndpoint(string Id, string Name);
