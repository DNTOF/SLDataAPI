using Exiled.API.Interfaces;

public class Config : IConfig
{
    public bool IsEnabled { get; set; } = true;
    public bool Debug { get; set; } = false;
    public string VerifyToken { get; set; } = "your_secret_token";
    public int HttpPort { get; set; } = 8081;
    public int PushIntervalSeconds { get; set; } = 8;
}
