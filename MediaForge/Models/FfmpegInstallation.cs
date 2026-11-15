namespace MediaForge.Models;

public sealed record FfmpegRelease(string Version, string Sha256);

public sealed record FfmpegInstallation(
    string Version, string FfmpegPath, string FfprobePath, string PackageSha256, string? FfplayPath = null);

public sealed record FfmpegSetupProgress(
    string Stage, string Detail, double Percent = 0, bool Indeterminate = true, bool WriteLog = true);
