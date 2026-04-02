using System.Collections.Generic;

public class PlayerInfo
{
    public string nickname { get; set; } = "";
    public string role { get; set; } = "";
    public string team { get; set; } = "";
}

public class ServerData
{
    public bool success { get; set; } = true;
    public string server_name { get; set; } = "";
    public bool online { get; set; }
    public int players_count { get; set; }
    public int max_players { get; set; }
    public bool round_started { get; set; }
    public int round_duration { get; set; }
    public string current_phase { get; set; } = "";
    public string nuke_status { get; set; } = "";
    public int nuke_countdown { get; set; }
    public int d_count { get; set; }
    public int foundation_count { get; set; }
    public int scp_count { get; set; }
    public int spectator_count { get; set; }
    public int ping { get; set; }
    public List<PlayerInfo> players { get; set; } = new();
}
