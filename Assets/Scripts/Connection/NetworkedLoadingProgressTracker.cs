using UnityEngine;
using Unity.Netcode;
public class NetworkedLoadingProgressTracker : NetworkBehaviour
{
    /// <summary>
    /// The current loading progress associated with the owner of this NetworkBehavior
    /// </summary>
    public NetworkVariable<float> Progress { get; } = new NetworkVariable<float>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    void Awake()
    {
        DontDestroyOnLoad(this);
    }
}