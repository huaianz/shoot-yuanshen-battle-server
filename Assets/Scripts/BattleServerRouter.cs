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
                    int playerId = ServerWorld.AddOrReplace(conn.ConnId, username, roomId, spawn, 0f,
                        req?.mode, req?.maxPlayers ?? 2);
                    conn.PlayerId = playerId;

                    var accepted = new JoinAcceptedDto
                    {
                        playerId = playerId,
                        roomId = roomId,
                        tick = BattleServerLoop.Instance.CurrentTick,
                        matchCoins = ServerWorld.GetMatchCoins(playerId),
                        wave = ServerEnemyRegistry.CurrentWave(roomId),
                        players = ServerWorld.BuildPlayerStates(roomId)
                    };
                    BattleServerNetwork.Instance.Send(conn, BattleMsgId.JoinAccepted, JsonUtility.ToJson(accepted));

                    // 此时 conn.PlayerId 已设置, 再广播一次大厅, 保证刚加入的人也能收到
                    ServerWorld.BroadcastLobby(roomId);

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
            case BattleMsgId.StartWave:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.RequestStartWave(conn.PlayerId);
                    break;
                }
            case BattleMsgId.SetReady:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.SetReady(conn.PlayerId, json.Contains("true"));
                    break;
                }
            case BattleMsgId.StartMatch:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.StartMatch(conn.PlayerId);
                    break;
                }
            case BattleMsgId.MoveSlot:
                {
                    if (conn.PlayerId <= 0) return;
                    var dto = JsonUtility.FromJson<MoveSlotDto>(json);
                    ServerWorld.MoveSlot(conn.PlayerId, dto.team, dto.slot);
                    break;
                }
            case BattleMsgId.SwapRequest:
                {
                    if (conn.PlayerId <= 0) return;
                    var dto = JsonUtility.FromJson<SwapRequestDto>(json);
                    ServerWorld.RequestSwap(conn.PlayerId, dto.targetId);
                    break;
                }
            case BattleMsgId.SwapResponse:
                {
                    if (conn.PlayerId <= 0) return;
                    var dto = JsonUtility.FromJson<SwapResponseDto>(json);
                    ServerWorld.RespondSwap(conn.PlayerId, dto.requestId, dto.accept);
                    break;
                }
            case BattleMsgId.SelectCharacter:
                {
                    if (conn.PlayerId <= 0) return;
                    var dto = JsonUtility.FromJson<SelectCharacterDto>(json);
                    ServerWorld.SetCharacter(conn.PlayerId, dto.characterId);
                    break;
                }
            case BattleMsgId.ConfirmCharacter:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.ConfirmCharacter(conn.PlayerId);
                    break;
                }
            case BattleMsgId.BuyItem:
                {
                    if (conn.PlayerId <= 0) return;
                    var dto = JsonUtility.FromJson<BuyItemDto>(json);
                    ServerWorld.HandleBuyItem(conn.PlayerId, dto);
                    break;
                }
            case BattleMsgId.ReturnToLobby:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.ReturnToLobby(conn.PlayerId);
                    break;
                }
            case BattleMsgId.PlantSpike:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.RequestPlant(conn.PlayerId);
                    break;
                }
            case BattleMsgId.DefuseSpike:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.RequestDefuse(conn.PlayerId);
                    break;
                }
            case BattleMsgId.PickupSpike:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.RequestPickup(conn.PlayerId);
                    break;
                }
            case BattleMsgId.DropSpike:
                {
                    if (conn.PlayerId <= 0) return;
                    ServerWorld.RequestDrop(conn.PlayerId);
                    break;
                }
            default:
                break;
        }
    }
}
