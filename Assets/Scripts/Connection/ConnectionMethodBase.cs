using UnityEngine;
using System;
using System.Threading.Tasks;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay.Models;
using System.Net;
using Unity.Networking.Transport;
using Unity.Services.Relay;

/// <summary>
/// ConnectionMethod contains all setup needed to setup NGO to be ready to start a connection, either host or client side.
/// Please override this abstract class to add a new transport or way of connecting.
/// </summary>
public abstract class ConnectionMethodBase
{
    protected ConnectionManager m_ConnectionManager;
    readonly ProfileManager m_ProfileManager;
    protected readonly string m_PlayerName;
    protected const string k_DtlsConnType = "dtls";

    /// <summary>
    /// Setup the host connection prior to starting the NetworkManager
    /// </summary>
    /// <returns></returns>
    public abstract Task SetupHostConnectionAsync();


    /// <summary>
    /// Setup the client connection prior to starting the NetworkManager
    /// </summary>
    /// <returns></returns>
    public abstract Task SetupClientConnectionAsync();

    /// <summary>
    /// Setup the client for reconnection prior to reconnecting
    /// </summary>
    /// <returns>
    /// success = true if succeeded in setting up reconnection, false if failed.
    /// shouldTryAgain = true if we should try again after failing, false if not.
    /// </returns>
    public abstract Task<(bool success, bool shouldTryAgain)> SetupClientReconnectionAsync();

    public ConnectionMethodBase(ConnectionManager connectionManager, ProfileManager profileManager, string playerName)
    {
        m_ConnectionManager = connectionManager;
        m_ProfileManager = profileManager;
        m_PlayerName = playerName;
    }

    protected void SetConnectionPayload(string playerId, string playerName)
    {
        var payload = JsonUtility.ToJson(new ConnectionPayload()
        {
            playerId = playerId,
            playerName = playerName,
            isDebug = Debug.isDebugBuild
        });

        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);

        m_ConnectionManager.NetworkManager.NetworkConfig.ConnectionData = payloadBytes;
    }

    /// Using authentication, this makes sure your session is associated with your account and not your device. This means you could reconnect
    /// from a different device for example. A playerId is also a bit more permanent than player prefs. In a browser for example,
    /// player prefs can be cleared as easily as cookies.
    /// The forked flow here is for debug purposes and to make UGS optional in Boss Room. This way you can study the sample without
    /// setting up a UGS account. It's recommended to investigate your own initialization and IsSigned flows to see if you need
    /// those checks on your own and react accordingly. We offer here the option for offline access for debug purposes, but in your own game you
    /// might want to show an error popup and ask your player to connect to the internet.
    protected string GetPlayerId()
    {
        if (Unity.Services.Core.UnityServices.State != ServicesInitializationState.Initialized)
        {
            return ClientPrefs.GetGuid() + m_ProfileManager.Profile;
        }

        return AuthenticationService.Instance.IsSignedIn ? AuthenticationService.Instance.PlayerId : ClientPrefs.GetGuid() + m_ProfileManager.Profile;
    }
}

/// <summary>
/// Simple IP connection setup with UTP
/// </summary>
class ConnectionMethodIP : ConnectionMethodBase
{
    string m_Ipaddress;
    ushort m_Port;

    public ConnectionMethodIP(string ip, ushort port, ConnectionManager connectionManager, ProfileManager profileManager, string playerName)
        : base(connectionManager, profileManager, playerName)
    {
        m_Ipaddress = ip;
        m_Port = port;
        m_ConnectionManager = connectionManager;
    }

    public override async Task SetupClientConnectionAsync()
    {
        SetConnectionPayload(GetPlayerId(), m_PlayerName);
        var utp = (UnityTransport)m_ConnectionManager.NetworkManager.NetworkConfig.NetworkTransport;
        utp.SetConnectionData(m_Ipaddress, m_Port);
    }

    public override async Task<(bool success, bool shouldTryAgain)> SetupClientReconnectionAsync()
    {
        // Nothing to do here
        return (true, true);
    }

    public override async Task SetupHostConnectionAsync()
    {
        SetConnectionPayload(GetPlayerId(), m_PlayerName); // Need to set connection payload for host as well, as host is a client too
        var utp = (UnityTransport)m_ConnectionManager.NetworkManager.NetworkConfig.NetworkTransport;
        utp.SetConnectionData(m_Ipaddress, m_Port);
    }
}

/// <summary>
/// UTP's Relay connection setup using the Lobby integration
/// </summary>
class ConnectionMethodRelay : ConnectionMethodBase
{
    LobbyServiceFacade m_LobbyServiceFacade;
    LocalLobby m_LocalLobby;

    public ConnectionMethodRelay(LobbyServiceFacade lobbyServiceFacade, LocalLobby localLobby, ConnectionManager connectionManager, ProfileManager profileManager, string playerName)
        : base(connectionManager, profileManager, playerName)
    {
        m_LobbyServiceFacade = lobbyServiceFacade;
        m_LocalLobby = localLobby;
        m_ConnectionManager = connectionManager;
    }

    public override async Task SetupClientConnectionAsync()
    {
        Debug.Log("Setting up Unity Relay client");

        SetConnectionPayload(GetPlayerId(), m_PlayerName);

        if (m_LobbyServiceFacade.CurrentUnityLobby == null)
        {
            throw new Exception("Trying to start relay while Lobby isn't set");
        }

        Debug.Log($"Setting Unity Relay client with join code {m_LocalLobby.RelayJoinCode}");

        // Create client joining allocation from join code
        var joinedAllocation = await RelayService.Instance.JoinAllocationAsync(m_LocalLobby.RelayJoinCode);
        Debug.Log($"client: {joinedAllocation.ConnectionData[0]} {joinedAllocation.ConnectionData[1]}, " +
            $"host: {joinedAllocation.HostConnectionData[0]} {joinedAllocation.HostConnectionData[1]}, " +
            $"client: {joinedAllocation.AllocationId}");

        await m_LobbyServiceFacade.UpdatePlayerDataAsync(joinedAllocation.AllocationId.ToString(), m_LocalLobby.RelayJoinCode);

        // Configure UTP with allocation
        var utp = (UnityTransport)m_ConnectionManager.NetworkManager.NetworkConfig.NetworkTransport;


        string host = "";
        ushort port = 0;
        bool IsSecure = true;
        string HostString;
        bool IsWebSocket = false;
        foreach (RelayServerEndpoint endpoint in joinedAllocation.ServerEndpoints)
        {
            if (endpoint.ConnectionType == k_DtlsConnType)
            {
                IsWebSocket = k_DtlsConnType == "ws" || k_DtlsConnType == "wss" ? true : false;
                host = endpoint.Host;
                port = (ushort)endpoint.Port;
                IsSecure = endpoint.Secure;
                HostString = endpoint.Host;
                break;
            }
            Debug.LogError($"Did not find connection type {k_DtlsConnType}");
        }

       // (string host, ushort port, byte[] allocationId, byte[] connectionData,
            //                   byte[] hostConnectionData, byte[] key, bool isSecure, bool isWebSocket)
        utp.SetRelayServerData(new RelayServerData(host,port,joinedAllocation.AllocationId.ToByteArray(),joinedAllocation.ConnectionData,joinedAllocation.HostConnectionData,joinedAllocation.Key, IsSecure, IsWebSocket));
    }

    private static NetworkEndpoint HostToEndpoint(string host, ushort port)
    {
        NetworkEndpoint endpoint;

        if (NetworkEndpoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv4))
            return endpoint;

        if (NetworkEndpoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv6))
            return endpoint;

        // If IPv4 and IPv6 parsing didn't work, we're dealing with a hostname. In this case,
        // perform a DNS resolution to figure out what its underlying IP address is. For WebGL,
        // use a hardcoded IP address since most browsers don't support making DNS resolutions
        // directly from JavaScript. This is safe to do since on WebGL the network interface
        // will never make use of actual endpoints (other than to put in the connection list).
#if UNITY_WEBGL && !UNITY_EDITOR
            return NetworkEndpoint.AnyIpv4.WithPort(port);
#else
        var addresses = Dns.GetHostEntry(host).AddressList;
        if (addresses.Length > 0)
        {
            var address = addresses[0].ToString();
            var family = addresses[0].AddressFamily;
            return NetworkEndpoint.Parse(address, port, (NetworkFamily)family);
        }

        Debug.LogError(host);
        return default;
#endif
    }

    public override async Task<(bool success, bool shouldTryAgain)> SetupClientReconnectionAsync()
    {
        if (m_LobbyServiceFacade.CurrentUnityLobby == null)
        {
            Debug.Log("Lobby does not exist anymore, stopping reconnection attempts.");
            return (false, false);
        }

        // When using Lobby with Relay, if a user is disconnected from the Relay server, the server will notify the
        // Lobby service and mark the user as disconnected, but will not remove them from the lobby. They then have
        // some time to attempt to reconnect (defined by the "Disconnect removal time" parameter on the dashboard),
        // after which they will be removed from the lobby completely.
        // See https://docs.unity.com/lobby/reconnect-to-lobby.html
        var lobby = await m_LobbyServiceFacade.ReconnectToLobbyAsync();
        var success = lobby != null;
        Debug.Log(success ? "Successfully reconnected to Lobby." : "Failed to reconnect to Lobby.");
        return (success, true); // return a success if reconnecting to lobby returns a lobby
    }

    public override async Task SetupHostConnectionAsync()
    {
        Debug.Log("Setting up Unity Relay host");

        SetConnectionPayload(GetPlayerId(), m_PlayerName); // Need to set connection payload for host as well, as host is a client too

        // Create relay allocation
        Allocation hostAllocation = await RelayService.Instance.CreateAllocationAsync(m_ConnectionManager.MaxConnectedPlayers, region: null);
        var joinCode = await RelayService.Instance.GetJoinCodeAsync(hostAllocation.AllocationId);

        Debug.Log($"server: connection data: {hostAllocation.ConnectionData[0]} {hostAllocation.ConnectionData[1]}, " +
            $"allocation ID:{hostAllocation.AllocationId}, region:{hostAllocation.Region}");

        m_LocalLobby.RelayJoinCode = joinCode;

        // next line enables lobby and relay services integration
        await m_LobbyServiceFacade.UpdateLobbyDataAndUnlockAsync();
        await m_LobbyServiceFacade.UpdatePlayerDataAsync(hostAllocation.AllocationIdBytes.ToString(), joinCode);

        // Setup UTP with relay connection info
        var utp = (UnityTransport)m_ConnectionManager.NetworkManager.NetworkConfig.NetworkTransport;

         //public RelayServerData(string host, ushort port, byte[] allocationId, byte[] connectionData,
                         //      byte[] hostConnectionData, byte[] key, bool isSecure)

        bool  IsWebSocket = k_DtlsConnType == "ws" || k_DtlsConnType == "wss" ? true: false;
        NetworkEndpoint Endpoint;
        bool IsSecure = true;
        string HostString = "";
        ushort port = 0;
        foreach (var endpoint in hostAllocation.ServerEndpoints)
        {
            if (endpoint.ConnectionType == k_DtlsConnType)
            {
                Endpoint = HostToEndpoint(endpoint.Host, (ushort)endpoint.Port);
                IsSecure = endpoint.Secure ? true : false;
                HostString = endpoint.Host;
                port=(ushort)endpoint.Port;
      
            }
        }

        utp.SetRelayServerData(new RelayServerData(HostString, port, hostAllocation.AllocationId.ToByteArray(), hostAllocation.ConnectionData, hostAllocation.ConnectionData,hostAllocation.Key,IsSecure)); // This is with DTLS enabled for a secure connection

        Debug.Log($"Created relay allocation with join code {m_LocalLobby.RelayJoinCode}");
    }
}