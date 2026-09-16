namespace AutoSwitch.Audio;

/// <summary>音声デバイス（再生または録音）。Id は Core Audio のエンドポイント ID、Name は表示名。</summary>
public sealed record AudioEndpoint(string Id, string Name);
