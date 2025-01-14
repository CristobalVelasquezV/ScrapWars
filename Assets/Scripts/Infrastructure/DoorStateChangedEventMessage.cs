using Unity.Netcode;
using UnityEngine;

public struct DoorStateChangedEventMessage : INetworkSerializeByMemcpy
{
    public bool IsDoorOpen;
}
