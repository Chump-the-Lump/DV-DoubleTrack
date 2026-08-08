namespace DoubleTrack.MP;

using System.Runtime.CompilerServices;


public class MPConfig
{
    private static object? Server;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SetupServer(MPAPI.Interfaces.IServer server)
    {
        Server = server;
        server.OnPlayerConnected += ConfigSend;
    }
    
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SetupClient(MPAPI.Interfaces.IClient client)
    {
        client.RegisterPacket<ConfigPacket>(ConfigReceive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ConfigSendAll()
    {
        foreach (MPAPI.Interfaces.IPlayer player in MPAPI.MultiplayerAPI.Server.Players)
        {
            ConfigSend(player);
        }
    }
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConfigSend(MPAPI.Interfaces.IPlayer receiver)
    {
        if (Server is MPAPI.Interfaces.IServer s)
        {
            ConfigPacket packet = new ConfigPacket();
            packet.Init();
            s.SendPacketToPlayer(packet, receiver);
            UnityEngine.Debug.Log("Sending DoubleTrack config to clients.");

        }
    }
    
    
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ConfigReceive(ConfigPacket packet)
    {
        UnityEngine.Debug.Log("Setting DoubleTrack config from server.");
        
        Settings.StaticJunctionMode = packet.junctionMode;
        Settings.LoadedTrackMode = packet.trackMode;
        
        Settings.UpdateSettings();
    }
    private class ConfigPacket : MPAPI.Interfaces.Packets.IPacket
    {
        public Settings.JunctionMode junctionMode { get; set; }
        public Settings.TrackMode trackMode { get; set; }
        
        public void Init()
        {
            junctionMode = Settings.StaticJunctionMode;
            trackMode = Settings.LoadedTrackMode;
        }
    }
}