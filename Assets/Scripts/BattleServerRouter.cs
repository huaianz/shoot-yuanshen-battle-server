using UnityEngine;

public static class BattleServerRouter
{
    public static void Handle(BattleClientConn conn, int msgId, string json)
    {
        switch (msgId)
        {
            case BattleMsgId.Join:
                {
                    var req = JsonUtility.FromJson<JoinRequestDto>(json);
                    string roomId = string.IsNullOrEmpty(req?.roomId) ? "default" : req.roomId;
                    string username = string.IsNullOrEmpty(req?.playerId) ? ("guest_" + conn.ConnId) : req.playerId;

                    conn.Username = username;
                    conn.RoomId = roomId;

                    Vector3 spawn = new Vector3(req != null ? req.x : 0f, req != null ? req.y : 0f, req != null ? req.z : 0f);
                    int playerId = ServerWorld.AddOrReplace(conn.ConnId, username, roomId, spawn, 0f);
                    conn.PlayerId = playerId;

                    var accepted = new JoinAcceptedDto
                    {
                        playerId = playerId,
                        roomId = roomId,
                        tick = BattleServerLoop.Instance.CurrentTick,
                        matchCoins = ServerWorld.GetMatchCoins(playerId),
                        players = ServerWorld.BuildPlayerStates(roomId)
                    };
                    BattleServerNetwork.Instance.Send(conn, BattleMsgId.JoinAccepted, JsonUtility.ToJson(accepted));

                    // 通知本房间其他玩家: 有人进来了
                    BattleServerNetwork.Instance.BroadcastToRoom(roomId, BattleMsgId.PlayerJoinLeave,
                        $"{{\"playerId\":{playerId},\"joined\":true}}");
                    break;
                }
            case BattleMsgId.Input:
                {
                    if (conn.PlayerId <= 0) return;
                    conn.LatestInput = JsonUtility.FromJson<InputCommandDto>(json);
                    ServerWorld.SetInput(conn.PlayerId, conn.LatestInput);
                    break;
                }
            case BattleMsgId.Heartbeat:
                // 原样回传, 客户端用它算 RTT(Ping)
                BattleServerNetwork.Instance.Send(conn, BattleMsgId.Heartbeat, json);
                break;
            case BattleMsgId.Fire:
                {
                    if (conn.PlayerId <= 0) return;
                    var fire = JsonUtility.FromJson<FireEventDto>(json);
                    ServerWorld.HandleFire(conn.PlayerId, fire);
                    break;
                }
            case BattleMsgId.Buy:
                {
                    if (conn.PlayerId <= 0) return;
                    var req = JsonUtility.FromJson<BuyRequestDto>(json);
                    ServerWorld.HandleBuy(conn.PlayerId, req);
                    break;
                }
            default:
                break;
        }
    }
}
