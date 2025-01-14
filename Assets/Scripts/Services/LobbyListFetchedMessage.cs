using System.Collections.Generic;
using UnityEngine;

public struct LobbyListFetchedMessage
{
    public readonly IReadOnlyList<LocalLobby> LocalLobbies;

    public LobbyListFetchedMessage(List<LocalLobby> localLobbies)
    {
        LocalLobbies = localLobbies;
    }
}