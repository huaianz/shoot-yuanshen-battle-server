using UnityEngine;
using System.Collections.Generic;

/// <summary>服务器的心跳起搏器</summary>
public class BattleServerLoop : MonoBehaviour
{
    public static BattleServerLoop Instance;

    public int tickRate = 20;
    public int CurrentTick { get; private set; }

    private float _accumulator;

    private void Awake() { Instance = this; }

    private void Update()
    {
        float interval = 1f / tickRate;
        _accumulator += Time.deltaTime;
        while (_accumulator >= interval)
        {
            _accumulator -= interval;
            CurrentTick++;
            ServerWorld.Step(interval);
            BroadcastSnapshots();
        }
    }

    private void BroadcastSnapshots()
    {
        List<string> rooms = ServerWorld.RoomIds();
        foreach (string roomId in rooms)
        {
            var snap = new SnapshotDto
            {
                tick = CurrentTick,
                players = ServerWorld.BuildPlayerStates(roomId),
                enemies = ServerWorld.BuildEnemyStates(roomId)
            };
            BattleServerNetwork.Instance.BroadcastToRoom(roomId, BattleMsgId.Snapshot, JsonUtility.ToJson(snap));
        }
    }
}