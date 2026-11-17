using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 位置采样
/// </summary>
public struct PosSample
{
    public long TimeMs;
    public Vector3 Pos;
}
public class ServerPlayerState
{
    public int PlayerId;          // 发给客户端的稳定 id
    public string Username;       // 玩家身份
    public int ConnId;            // 当前连接号(断线会变)
    public string RoomId;         // 所在房间
    public Vector3 Pos;
    public float Yaw;
    public int Hp = ServerWorld.MatchMaxHp;
    public int AnimId;            // 最近上报的动作编号
    public int WeaponId = -1;     // 当前装备武器模板 id
    public long LastInputMs;      // 最近输入时间戳(校验用)
    public InputCommandDto Input; // 最近一次输入
    public readonly List<PosSample> History = new List<PosSample>();  // 位置历史(回溯用)
    public long LastFireMs;      // 上次开火时间(射速限制)
    public long RespawnAtMs;     // 复活时间(0=活着)
    public int MatchCoins;       // 本局累计金币
    public long DamageBoostUntilMs;   // 火力强化到期时间(0=没有)
    public int Team = -1;             // 0=进攻 1=防守 (-1=合作模式)
    public int Slot;                  // 大厅席位号
    public bool EliminationDead;      // 战术模式回合内阵亡, 不自动复活
    public int Kills;
    public int Deaths;
    public int Assists;
    public int LastAttackerId = -1;
    public long LastAttackMs;
    public bool Ready;
    public long LastSwapRequestMs;
    public int CharacterId = -1;
    public bool CharacterConfirmed;
    public int Armor;                 // 0..50
    public int MatchWeaponId;         // 0=默认手枪, 101/102/103=局内购买的武器
    public int MatchWeaponDamage = 20;
    public int MatchWeaponIntervalMs = 200;
    public long LastPlantLogMs;
}

public static class ServerWorld
{
    public enum RoundPhase { Warmup = 0, Buy = 1, Combat = 2, RoundEnd = 3, CharacterSelect = 4 }

    public class RoomRound
    {
        public RoundPhase Phase;
        public long PhaseEndMs;
        public int RoundNo = 1;
        public int AttackScore;
        public int DefenseScore;
        public bool MatchOver;

        // 下包/拆包
        public bool SpikePlanted;
        public Vector3 SpikePos;
        public int SpikeState;        // 0=地上 1=被携带 2=已下包
        public int SpikeCarrierId = -1;
        public bool SpikePositioned;
        public long FuseEndMs;
        public int PlantProgressMs;
        public int DefuseProgressMs;
        public long LastPlantReqMs;
        public long LastDefuseReqMs;
    }

    // 按设计文档: 准备 1 分钟, 战斗 3 分钟, 先到 7 分, 6 回合换边
    public const int PrepSeconds = 60;
    public const int CombatSeconds = 180;
    public const int RoundEndSeconds = 5;
    public const int CharacterSelectSeconds = 60;
    public const int RoundsToWin = 7;
    public const int SwapAfterRounds = 6;
    public const int OvertimeTarget = 9;
    public const int PlantNeedMs = 3000;
    public const int DefuseNeedMs = 6000;
    public const int FuseMs = 20000;
    public const float DefuseRange = 3f;

    public const int MatchMaxHp = 100;
    public const int HalfArmor = 25;
    public const int FullArmor = 50;

    // 包点(可由启动参数 -plantsite x,y,z 配置; 未配置则不校验)
    public static bool HasPlantSite;
    public static Vector3 PlantSiteCenter;
    public static float PlantSiteRadius = 3f;

    public static bool HasSpikeSpawn;
    public static Vector3 SpikeSpawnCenter;

    public static void SetSpikeSpawn(Vector3 center)
    {
        HasSpikeSpawn = true;
        SpikeSpawnCenter = center;
        Debug.Log($"[Spike] 初始包位置已配置: {center}");
    }

    public static void SetPlantSite(Vector3 center, float radius)
    {
        HasPlantSite = true;
        PlantSiteCenter = center;
        PlantSiteRadius = Mathf.Max(1f, radius);
        Debug.Log($"[Spike] 服务器包点已配置: {center}, 半径={PlantSiteRadius}");
    }

    public const int RoundBaseMoney = 1000;
    public const int KillMoney = 200;
    public const int RoundWinMoney = 500;

    private static readonly Dictionary<string, RoomRound> _rounds =
        new Dictionary<string, RoomRound>();
    private static readonly Dictionary<string, RoomLobby> _lobbies =
        new Dictionary<string, RoomLobby>();
    private static readonly Dictionary<string, PendingSwap> _pendingSwaps =
        new Dictionary<string, PendingSwap>();
    private static int _nextSwapRequestId;
    private static float _roundBroadcastTimer;

    public class RoomLobby
    {
        public int HostId;
        public int Capacity = 2;
        public bool Started;
    }

    public class PendingSwap
    {
        public int RequesterId;
        public int TargetId;
        public int RequestId;
        public long ExpiresAtMs;
    }

    public const float MoveSpeed = 4f;

    public static bool ServerAuthoritativeMovement = false;

    // ===== 移动校验(客户端权威 + 服务器校验) =====
    public static float MaxMoveSpeed = 8f;        // 允许的最大移动速度(米/秒)
    public static float MoveTolerance = 0.5f;     // 每包容差(米)
    public static float TeleportThreshold = 50f;  // 超过这个距离视为瞬移
    public static int RejectedCount { get; private set; }
    public static int SoftAnomalies { get; private set; }

    private static readonly Dictionary<int, ServerPlayerState> _playersById = new Dictionary<int, ServerPlayerState>();
    private static readonly Dictionary<string, int> _idByUsername = new Dictionary<string, int>();
    private static readonly Dictionary<string, string> _roomModes = new Dictionary<string, string>();
    private static int _nextPlayerId = 0;

    public static int PlayerCount => _playersById.Count;
    public static int ShotsReceived { get; private set; }
    public static int HitsLanded { get; private set; }
    private static long _lastStatLogMs;

    /// <summary>当前房间是不是战术(PvP)模式</summary>
    public static bool IsPvPRoom(string roomId)
    {
        return !string.IsNullOrEmpty(roomId)
            && _roomModes.TryGetValue(roomId, out var m)
            && m == "pvp";
    }

    // ===== 准备大厅 =====
    private static RoomLobby EnsureLobby(string roomId)
    {
        if (_lobbies.TryGetValue(roomId, out var l)) return l;

        l = new RoomLobby { Capacity = 2 };
        _lobbies[roomId] = l;
        return l;
    }

    public static void SetReady(int playerId, bool ready)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;

        p.Ready = ready;
        BroadcastLobby(p.RoomId);
    }

    public static void StartMatch(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_lobbies.TryGetValue(p.RoomId, out var lobby)) return;

        if (lobby.HostId != playerId)
        {
            Debug.Log("[Lobby] 非房主不能开始比赛");
            return;
        }

        int attack = 0, defense = 0;
        foreach (var other in RoomPlayers(p.RoomId))
        {
            if (other.Team == 0) attack++;
            else if (other.Team == 1) defense++;

            if (!other.Ready)
            {
                Debug.Log($"[Lobby] {other.Username} 还没准备");
                return;
            }
        }

        if (attack == 0 || defense == 0)
        {
            Debug.Log("[Lobby] 双方都必须有人");
            return;
        }

        lobby.Started = true;
        BroadcastLobby(p.RoomId);

        // 进入角色选择阶段(1 分钟), 选完再开第一回合
        if (_rounds.TryGetValue(p.RoomId, out var r))
        {
            r.Phase = RoundPhase.CharacterSelect;
            r.PhaseEndMs = NowMs() + CharacterSelectSeconds * 1000L;

            foreach (var other in RoomPlayers(p.RoomId))
            {
                other.CharacterId = -1;
                other.CharacterConfirmed = false;
            }

            BroadcastRound(p.RoomId, r);
            BroadcastCharacterSelect(p.RoomId, r);
        }

        Debug.Log($"[Lobby] 房主 {p.Username} 开始比赛");
    }

    public static void BroadcastLobby(string roomId)
    {
        if (BattleServerNetwork.Instance == null) return;

        var lobby = EnsureLobby(roomId);
        var dto = new LobbyStateDto
        {
            roomId = roomId,
            hostId = lobby.HostId,
            capacity = lobby.Capacity,
            started = lobby.Started
        };

        foreach (var p in RoomPlayers(roomId))
        {
            if (p.Team < 0) continue;
            dto.players.Add(new LobbyPlayerDto
            {
                id = p.PlayerId,
                name = p.Username,
                team = p.Team,
                slot = p.Slot,
                ready = p.Ready
            });
        }

        BattleServerNetwork.Instance.BroadcastToRoom(
            roomId, BattleMsgId.LobbyState, JsonUtility.ToJson(dto));
    }

    // ===== 角色选择 =====
    public static void SetCharacter(int playerId, int characterId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (p.CharacterConfirmed) return;

        p.CharacterId = Mathf.Max(0, characterId);

        if (_rounds.TryGetValue(p.RoomId, out var r) && r.Phase == RoundPhase.CharacterSelect)
            BroadcastCharacterSelect(p.RoomId, r);
    }

    public static void ConfirmCharacter(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (p.CharacterConfirmed) return;

        if (p.CharacterId < 0) p.CharacterId = 0;
        p.CharacterConfirmed = true;

        if (!_rounds.TryGetValue(p.RoomId, out var r)) return;

        if (AllCharactersConfirmed(p.RoomId))
            StartRound(p.RoomId, r);
        else
            BroadcastCharacterSelect(p.RoomId, r);
    }

    private static bool AllCharactersConfirmed(string roomId)
    {
        foreach (var p in RoomPlayers(roomId))
        {
            if (p.Team < 0) continue;
            if (!p.CharacterConfirmed) return false;
        }
        return true;
    }

    private static void BroadcastCharacterSelect(string roomId, RoomRound r)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new CharacterSelectStateDto
        {
            timeLeftMs = (int)System.Math.Max(0, r.PhaseEndMs - NowMs())
        };

        foreach (var p in RoomPlayers(roomId))
        {
            if (p.Team < 0) continue;
            dto.players.Add(new CharacterSelectPlayerDto
            {
                id = p.PlayerId,
                name = p.Username,
                characterId = p.CharacterId,
                confirmed = p.CharacterConfirmed
            });
        }

        BattleServerNetwork.Instance.BroadcastToRoom(
            roomId, BattleMsgId.CharacterSelectState, JsonUtility.ToJson(dto));
    }

    // ===== 回合购买: 护甲 / 血包 =====
    public static void RequestPlant(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || r.Phase != RoundPhase.Combat)
        {
            LogPlantReject(p, "当前不是战斗阶段");
            return;
        }

        if (p.Team != 0) { LogPlantReject(p, "不是进攻方"); return; }
        if (r.SpikePlanted) { LogPlantReject(p, "已经下包"); return; }
        if (r.SpikeState != 1 || r.SpikeCarrierId != playerId)
        {
            LogPlantReject(p, "没有携带包, 先去拾取");
            return;
        }
        if (p.Hp <= 0) { LogPlantReject(p, "已阵亡"); return; }

        if (HasPlantSite && Vector3.Distance(p.Pos, PlantSiteCenter) > PlantSiteRadius)
        {
            r.PlantProgressMs = 0;
            LogPlantReject(p, $"不在包点内(距离 {Vector3.Distance(p.Pos, PlantSiteCenter):F1})");
            return;
        }

        long now = NowMs();
        if (now - r.LastPlantReqMs > 300) r.PlantProgressMs = 0;
        else r.PlantProgressMs += (int)Mathf.Clamp(now - r.LastPlantReqMs, 0, 500);
        r.LastPlantReqMs = now;

        if (r.PlantProgressMs >= PlantNeedMs)
        {
            r.SpikePlanted = true;
            r.SpikeState = 2;
            r.SpikeCarrierId = -1;
            r.SpikePos = p.Pos;
            r.FuseEndMs = now + FuseMs;
            r.PlantProgressMs = PlantNeedMs;
            r.DefuseProgressMs = 0;
            Debug.Log($"[Spike] {p.Username} 在 {p.Pos} 下包");
            BroadcastSpike(p.RoomId, r);
        }
    }

    private static void LogPlantReject(ServerPlayerState p, string reason)
    {
        long now = NowMs();
        if (now - p.LastPlantLogMs < 2000) return;

        p.LastPlantLogMs = now;
        Debug.Log($"[Spike] {p.Username} 下包被拒: {reason} (team={p.Team})");
    }

    public static void RequestPickup(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || r.Phase != RoundPhase.Combat) return;
        if (p.Team != 0 || p.Hp <= 0 || r.SpikeState != 0) return;
        if (Vector3.Distance(p.Pos, r.SpikePos) > 3f) return;

        r.SpikeState = 1;
        r.SpikeCarrierId = playerId;
        Debug.Log($"[Spike] {p.Username} 拾取了包");
        BroadcastSpike(p.RoomId, r);
    }

    public static void RequestDrop(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || r.Phase != RoundPhase.Combat) return;
        if (r.SpikeState != 1 || r.SpikeCarrierId != playerId) return;

        r.SpikeState = 0;
        r.SpikeCarrierId = -1;
        r.SpikePositioned = false;
        r.SpikePos = p.Pos;
        Debug.Log($"[Spike] {p.Username} 丢下了包");
        BroadcastSpike(p.RoomId, r);
    }

    public static void RequestDefuse(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || r.Phase != RoundPhase.Combat) return;
        if (p.Team != 1 || !r.SpikePlanted || p.Hp <= 0) return;
        if (Vector3.Distance(p.Pos, r.SpikePos) > DefuseRange) return;

        long now = NowMs();
        if (now - r.LastDefuseReqMs > 300) r.DefuseProgressMs = 0;
        else r.DefuseProgressMs += (int)Mathf.Clamp(now - r.LastDefuseReqMs, 0, 500);
        r.LastDefuseReqMs = now;

        if (r.DefuseProgressMs >= DefuseNeedMs)
        {
            r.SpikePlanted = false;
            Debug.Log($"[Spike] {p.Username} 拆除炸弹, 防守方得分");
            BroadcastSpike(p.RoomId, r);
            EndRound(p.RoomId, r, 1);
        }
    }

    private static void BroadcastSpike(string roomId, RoomRound r)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new SpikeStateDto
        {
            planted = r.SpikePlanted,
            spikeState = r.SpikeState,
            carrierId = r.SpikeCarrierId,
            x = r.SpikePos.x, y = r.SpikePos.y, z = r.SpikePos.z,
            fuseLeftMs = r.SpikePlanted ? (int)System.Math.Max(0, r.FuseEndMs - NowMs()) : 0,
            plantProgressMs = r.PlantProgressMs,
            defuseProgressMs = r.DefuseProgressMs
        };

        BattleServerNetwork.Instance.BroadcastToRoom(
            roomId, BattleMsgId.SpikeState, JsonUtility.ToJson(dto));
    }

    public static void HandleBuyItem(int playerId, BuyItemDto request)
    {
        if (request == null) return;
        if (!_playersById.TryGetValue(playerId, out var p)) return;

        int itemId = request.itemId;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || r.Phase != RoundPhase.Buy)
        {
            ReplyBuyItem(p, itemId, false, "只能在回合准备阶段购买");
            return;
        }

        string category = string.IsNullOrEmpty(request.category)
            ? InferCategory(itemId)
            : request.category.Trim().ToLowerInvariant();

        int price = 0;
        if (category == "armor")
        {
            if (itemId == 1) price = 500;
            else if (itemId == 2) price = 1000;
            else
            {
                ReplyBuyItem(p, itemId, false, "没有这个护甲商品");
                return;
            }
        }
        else if (category == "supply")
        {
            if (itemId != 3)
            {
                ReplyBuyItem(p, itemId, false, "没有这个补给商品");
                return;
            }
            price = 300;
        }
        else if (category == "weapon")
        {
            if (request.matchPrice <= 0 || request.matchDamage <= 0
                || request.matchFireIntervalMs <= 0)
            {
                ReplyBuyItem(p, itemId, false, "该武器尚未填写完整对局数据");
                return;
            }
            price = Mathf.Clamp(request.matchPrice, 1, 100000);
        }
        else
        {
            ReplyBuyItem(p, itemId, false, "未知商品类别");
            return;
        }

        if (p.MatchCoins < price)
        {
            ReplyBuyItem(p, itemId, false, "金币不足");
            return;
        }

        string msg;

        if (category == "armor")
        {
            if (itemId == 1)
            {
                if (p.Armor >= HalfArmor)
                {
                    ReplyBuyItem(p, itemId, false, "已有同等或更好的护甲");
                    return;
                }
                p.Armor = HalfArmor;
                msg = "购买半甲";
            }
            else
            {
                if (p.Armor >= FullArmor)
                {
                    ReplyBuyItem(p, itemId, false, "已有全甲");
                    return;
                }
                p.Armor = FullArmor;
                msg = "购买全甲";
            }
        }
        else if (category == "supply")
        {
            if (itemId == 3)
            {
                if (p.Hp >= MatchMaxHp)
                {
                    ReplyBuyItem(p, itemId, false, "血量已满");
                    return;
                }
                p.Hp = Mathf.Min(MatchMaxHp, p.Hp + 50);
                msg = "使用急救包";
            }
            else
            {
                ReplyBuyItem(p, itemId, false, "没有这个补给商品");
                return;
            }
        }
        else
        {
            if (p.MatchWeaponId == itemId)
            {
                ReplyBuyItem(p, itemId, false, "已拥有该武器");
                return;
            }

            p.MatchWeaponId = itemId;
            p.MatchWeaponDamage = Mathf.Clamp(request.matchDamage, 1, 1000);
            p.MatchWeaponIntervalMs = Mathf.Clamp(request.matchFireIntervalMs, 50, 3000);
            msg = $"购买武器，伤害 {p.MatchWeaponDamage}";
        }

        p.MatchCoins -= price;
        ReplyBuyItem(p, itemId, true, $"{msg}, 花费 {price}");
    }

    private static string InferCategory(int itemId)
    {
        if (itemId == 1 || itemId == 2) return "armor";
        if (itemId == 3) return "supply";
        return "weapon";
    }

    private static void ReplyBuyItem(ServerPlayerState p, int itemId, bool success, string msg)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new BuyItemResultDto
        {
            success = success,
            itemId = itemId,
            msg = msg,
            coins = p.MatchCoins,
            armor = p.Armor,
            matchWeaponId = p.MatchWeaponId,
            matchDamage = p.MatchWeaponDamage,
            hp = p.Hp
        };

        BattleServerNetwork.Instance.SendToPlayer(
            p.PlayerId, BattleMsgId.BuyItemResult, JsonUtility.ToJson(dto));
    }

    /// <summary>护甲先吸收伤害, 剩下的才扣血</summary>
    private static void ApplyArmorDamage(ServerPlayerState target, int damage)
    {
        damage = Mathf.Max(0, damage);
        if (target.Armor > 0)
        {
            int absorbed = Mathf.Min(target.Armor, damage);
            target.Armor -= absorbed;
            damage -= absorbed;
        }

        if (damage > 0)
            target.Hp = Mathf.Max(0, target.Hp - damage);
    }

    // ===== 席位移动与换位请求 =====
    public static void MoveSlot(int playerId, int team, int slot)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_lobbies.TryGetValue(p.RoomId, out var lobby) || lobby.Started) return;
        if (team < 0 || team > 1) return;

        int teamSize = Mathf.Clamp(lobby.Capacity / 2, 1, 3);
        if (slot < 0 || slot >= teamSize) return;

        foreach (var other in RoomPlayers(p.RoomId))
        {
            if (other.PlayerId == playerId) continue;
            if (other.Team == team && other.Slot == slot)
            {
                SendSwapResult(playerId, false, "该位置已有人");
                return;
            }
        }

        p.Team = team;
        p.Slot = slot;
        p.Ready = false;
        BroadcastLobby(p.RoomId);
    }

    public static void RequestSwap(int playerId, int targetId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_playersById.TryGetValue(targetId, out var t)) return;
        if (p.RoomId != t.RoomId) return;
        if (!_lobbies.TryGetValue(p.RoomId, out var lobby) || lobby.Started) return;
        if (p.Team == t.Team) return;

        if (!AllSlotsFull(p.RoomId))
        {
            SendSwapResult(playerId, false, "还有空位, 直接点空位移动");
            return;
        }

        long now = NowMs();
        if (now - p.LastSwapRequestMs < 5000)
        {
            SendSwapResult(playerId, false, "请求太频繁, 请稍后再试");
            return;
        }

        if (_pendingSwaps.ContainsKey(p.RoomId))
        {
            SendSwapResult(playerId, false, "已有换位请求在处理");
            return;
        }

        p.LastSwapRequestMs = now;

        var req = new PendingSwap
        {
            RequesterId = playerId,
            TargetId = targetId,
            RequestId = ++_nextSwapRequestId,
            ExpiresAtMs = now + 5000
        };
        _pendingSwaps[p.RoomId] = req;

        var dto = new SwapIncomingDto
        {
            requesterId = playerId,
            requesterName = p.Username,
            requestId = req.RequestId,
            expiresInMs = 5000
        };
        BattleServerNetwork.Instance.SendToPlayer(targetId, BattleMsgId.SwapIncoming, JsonUtility.ToJson(dto));
        SendSwapResult(playerId, true, "换位请求已发送");
    }

    public static void RespondSwap(int playerId, int requestId, bool accept)
    {
        if (!_playersById.TryGetValue(playerId, out var responder)) return;
        if (!_pendingSwaps.TryGetValue(responder.RoomId, out var req)) return;
        if (req.TargetId != playerId || req.RequestId != requestId) return;

        _pendingSwaps.Remove(responder.RoomId);

        if (NowMs() > req.ExpiresAtMs)
        {
            SendSwapResult(req.RequesterId, false, "对方未在 5 秒内响应");
            return;
        }

        if (accept && _playersById.TryGetValue(req.RequesterId, out var requester))
        {
            int team = requester.Team, slot = requester.Slot;
            requester.Team = responder.Team;
            requester.Slot = responder.Slot;
            responder.Team = team;
            responder.Slot = slot;
            requester.Ready = false;
            responder.Ready = false;
        }

        SendSwapResult(req.RequesterId, accept, accept ? "对方同意了换位" : "对方拒绝了换位");
        BroadcastLobby(responder.RoomId);
    }

    private static bool AllSlotsFull(string roomId)
    {
        if (!_lobbies.TryGetValue(roomId, out var lobby)) return false;

        int teamSize = Mathf.Clamp(lobby.Capacity / 2, 1, 3);

        for (int team = 0; team <= 1; team++)
            for (int slot = 0; slot < teamSize; slot++)
            {
                bool used = false;
                foreach (var p in RoomPlayers(roomId))
                    if (p.Team == team && p.Slot == slot) { used = true; break; }
                if (!used) return false;
            }

        return true;
    }

    private static void SendSwapResult(int playerId, bool accepted, string reason)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new SwapResultDto { accepted = accepted, reason = reason };
        BattleServerNetwork.Instance.SendToPlayer(playerId, BattleMsgId.SwapResult, JsonUtility.ToJson(dto));
    }

    private static void SweepSwapRequests(long now)
    {
        List<string> expired = null;

        foreach (var kv in _pendingSwaps)
            if (now >= kv.Value.ExpiresAtMs)
                (expired ??= new List<string>()).Add(kv.Key);

        if (expired == null) return;

        foreach (string roomId in expired)
        {
            var req = _pendingSwaps[roomId];
            _pendingSwaps.Remove(roomId);
            SendSwapResult(req.RequesterId, false, "对方未在 5 秒内响应");
        }
    }

    // ===== 战术模式: 队伍与回合 =====
    private static RoomRound EnsureRound(string roomId)
    {
        if (_rounds.TryGetValue(roomId, out var r)) return r;

        r = new RoomRound
        {
            Phase = RoundPhase.Warmup,
            PhaseEndMs = NowMs() + 3000
        };
        _rounds[roomId] = r;
        return r;
    }

    private static void JoinRound(string roomId, int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;

        var r = EnsureRound(roomId);

        int attack = 0, defense = 0;
        foreach (var other in _playersById.Values)
        {
            if (other.RoomId != roomId || other.PlayerId == playerId) continue;
            if (other.Team == 0) attack++;
            else if (other.Team == 1) defense++;
        }

        p.Team = attack <= defense ? 0 : 1;
        p.Slot = FirstFreeSlot(roomId, p.Team);
        p.Hp = MatchMaxHp;
        p.Armor = 0;
        p.EliminationDead = false;
        p.RespawnAtMs = 0;

        // 中途加入的玩家默认用第一个角色
        if (p.CharacterId < 0)
        {
            p.CharacterId = 0;
            p.CharacterConfirmed = true;
        }

        Debug.Log($"[Round] {p.Username} 加入 {roomId}, 队伍={(p.Team == 0 ? "进攻" : "防守")}");
        BroadcastRound(roomId, r);
    }

    private static int FirstFreeSlot(string roomId, int team)
    {
        int teamSize = 1;
        if (_lobbies.TryGetValue(roomId, out var lobby))
            teamSize = Mathf.Clamp(lobby.Capacity / 2, 1, 3);

        for (int slot = 0; slot < teamSize; slot++)
        {
            bool used = false;
            foreach (var other in RoomPlayers(roomId))
                if (other.Team == team && other.Slot == slot) { used = true; break; }

            if (!used) return slot;
        }
        return 0;
    }

    private static List<ServerPlayerState> RoomPlayers(string roomId)
    {
        var list = new List<ServerPlayerState>();
        foreach (var p in _playersById.Values)
            if (p.RoomId == roomId) list.Add(p);
        return list;
    }

    private static void StartRound(string roomId, RoomRound r)
    {
        // 6 回合后换边; 加时赛(第 13 回合起)每 2 回合换一次
        bool swap = r.RoundNo == SwapAfterRounds + 1
            || (r.RoundNo >= SwapAfterRounds * 2 + 1 && (r.RoundNo - (SwapAfterRounds * 2 + 1)) % 2 == 0);

        foreach (var p in RoomPlayers(roomId))
        {
            if (swap && p.Team >= 0) p.Team = 1 - p.Team;

            p.Hp = MatchMaxHp;
            p.Armor = 0;
            p.MatchWeaponId = 0;
            p.MatchWeaponDamage = 20;
            p.MatchWeaponIntervalMs = 200;
            p.EliminationDead = false;
            p.RespawnAtMs = 0;
            p.History.Clear();
            p.MatchCoins = RoundBaseMoney;
        }

        r.Phase = RoundPhase.Buy;
        r.PhaseEndMs = NowMs() + PrepSeconds * 1000L;
        r.SpikePlanted = false;
        r.SpikeState = 0;
        r.SpikeCarrierId = -1;
        r.PlantProgressMs = 0;
        r.DefuseProgressMs = 0;
        r.LastPlantReqMs = 0;
        r.LastDefuseReqMs = 0;

        // 初始包位置: 优先使用配置点, 否则放第一个进攻方脚下
        if (HasSpikeSpawn)
        {
            r.SpikePos = SpikeSpawnCenter;
        }
        else
        {
            foreach (var p in RoomPlayers(roomId))
                if (p.Team == 0) { r.SpikePos = p.Pos; break; }
        }

        Debug.Log($"[Round] {roomId} 第 {r.RoundNo} 回合准备, 比分 {r.AttackScore}:{r.DefenseScore}");
        BroadcastRound(roomId, r);
    }

    private static void EndRound(string roomId, RoomRound r, int winnerTeam)
    {
        if (r.Phase == RoundPhase.RoundEnd || r.MatchOver) return;

        if (winnerTeam == 0) r.AttackScore++;
        else r.DefenseScore++;

        foreach (var p in RoomPlayers(roomId))
            if (p.Team == winnerTeam) p.MatchCoins += RoundWinMoney;

        bool overtime = r.AttackScore >= 6 && r.DefenseScore >= 6;
        int target = overtime ? OvertimeTarget : RoundsToWin;
        r.MatchOver = r.AttackScore >= target || r.DefenseScore >= target;

        r.Phase = RoundPhase.RoundEnd;
        r.PhaseEndMs = NowMs() + RoundEndSeconds * 1000L;

        if (!r.MatchOver) r.RoundNo++;

        Debug.Log($"[Round] {roomId} 回合结束, {(winnerTeam == 0 ? "进攻" : "防守")}胜, " +
                  $"比分 {r.AttackScore}:{r.DefenseScore}{(r.MatchOver ? " 比赛结束" : "")}");
        BroadcastRound(roomId, r);

        if (r.MatchOver) BroadcastMatchResult(roomId, r);
    }

    private static void BroadcastMatchResult(string roomId, RoomRound r)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new MatchResultDto
        {
            attackScore = r.AttackScore,
            defenseScore = r.DefenseScore,
            winnerTeam = r.AttackScore > r.DefenseScore ? 0 : 1
        };

        foreach (var p in RoomPlayers(roomId))
        {
            if (p.Team < 0) continue;
            dto.players.Add(new MatchResultPlayerDto
            {
                id = p.PlayerId,
                name = p.Username,
                team = p.Team,
                kills = p.Kills,
                deaths = p.Deaths,
                assists = p.Assists
            });
        }

        BattleServerNetwork.Instance.BroadcastToRoom(
            roomId, BattleMsgId.MatchResult, JsonUtility.ToJson(dto));
    }

    /// <summary>结算后返回准备大厅</summary>
    public static void ReturnToLobby(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (!_lobbies.TryGetValue(p.RoomId, out var lobby)) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r) || !r.MatchOver) return;

        lobby.Started = false;

        r.Phase = RoundPhase.Warmup;
        r.PhaseEndMs = NowMs() + 3600_000L;
        r.AttackScore = 0;
        r.DefenseScore = 0;
        r.RoundNo = 1;
        r.MatchOver = false;

        foreach (var other in RoomPlayers(p.RoomId))
        {
            other.Ready = false;
            other.Hp = MatchMaxHp;
            other.Armor = 0;
            other.EliminationDead = false;
            other.RespawnAtMs = 0;
            other.Kills = 0;
            other.Deaths = 0;
            other.Assists = 0;
            other.CharacterConfirmed = false;
            other.MatchCoins = 0;
        }

        BroadcastLobby(p.RoomId);
        BroadcastRound(p.RoomId, r);
    }

    private static void CheckElimination(string roomId, RoomRound r)
    {
        int attackAlive = 0, defenseAlive = 0, attackTotal = 0, defenseTotal = 0;

        foreach (var p in RoomPlayers(roomId))
        {
            if (p.Team == 0) attackTotal++;
            else if (p.Team == 1) defenseTotal++;
            else continue;

            bool alive = p.Hp > 0 && !p.EliminationDead;
            if (!alive) continue;

            if (p.Team == 0) attackAlive++;
            else defenseAlive++;
        }

        if (attackTotal == 0 || defenseTotal == 0) return;

        if (attackAlive == 0) EndRound(roomId, r, 1);
        else if (defenseAlive == 0) EndRound(roomId, r, 0);
    }

    private static void TickRounds(long now)
    {
        foreach (var kv in _rounds)
        {
            string roomId = kv.Key;
            var r = kv.Value;

            if (r.Phase == RoundPhase.CharacterSelect && AllCharactersConfirmed(roomId))
            {
                StartRound(roomId, r);
                continue;
            }

            if (r.Phase == RoundPhase.Combat && r.SpikePlanted && now >= r.FuseEndMs)
            {
                r.SpikePlanted = false;
                Debug.Log($"[Spike] 房间 {roomId} 炸弹爆炸, 进攻方得分");
                BroadcastSpike(roomId, r);
                EndRound(roomId, r, 0);
                continue;
            }

            if (now < r.PhaseEndMs)
            {
                if (r.Phase == RoundPhase.Combat) CheckElimination(roomId, r);
                continue;
            }

            switch (r.Phase)
            {
                case RoundPhase.Warmup:
                {
                    // 等房主点"开始比赛"
                    if (!_lobbies.TryGetValue(roomId, out var lobby) || !lobby.Started)
                    {
                        r.PhaseEndMs = now + 1000;
                        break;
                    }

                    int attack = 0, defense = 0;
                    foreach (var p in RoomPlayers(roomId))
                    {
                        if (p.Team == 0) attack++;
                        else if (p.Team == 1) defense++;
                    }

                    if (attack > 0 && defense > 0) StartRound(roomId, r);
                    else r.PhaseEndMs = now + 2000;
                    break;
                }

                case RoundPhase.Buy:
                    r.Phase = RoundPhase.Combat;
                    r.PhaseEndMs = now + CombatSeconds * 1000L;

                    // 包放在进攻方现在的出生位置旁边(此时已经传送完成)
                    if (!HasSpikeSpawn && r.SpikeState == 0)
                    {
                        foreach (var p in RoomPlayers(roomId))
                        {
                            if (p.Team != 0) continue;
                            r.SpikePos = p.Pos + new Vector3(1f, 0f, 1f);
                            break;
                        }
                        BroadcastSpike(roomId, r);
                    }

                    BroadcastRound(roomId, r);
                    break;

                case RoundPhase.CharacterSelect:
                {
                    // 超时: 没确认的随机选一个
                    foreach (var p in RoomPlayers(roomId))
                    {
                        if (p.Team < 0 || p.CharacterConfirmed) continue;
                        p.CharacterId = UnityEngine.Random.Range(0, 2);
                        p.CharacterConfirmed = true;
                    }
                    StartRound(roomId, r);
                    break;
                }

                case RoundPhase.Combat:
                    if (r.SpikePlanted)
                    {
                        // 已下包: 由 20 秒引信决定胜负
                        r.PhaseEndMs = now + 3600_000L;
                        break;
                    }
                    EndRound(roomId, r, 1);   // 时间到且守方还有人: 守方胜
                    break;

                case RoundPhase.RoundEnd:
                    if (r.MatchOver) r.PhaseEndMs = now + 3600_000L;
                    else StartRound(roomId, r);
                    break;
            }
        }
    }

    private static void BroadcastRound(string roomId, RoomRound r)
    {
        if (BattleServerNetwork.Instance == null) return;

        var dto = new RoundStateDto
        {
            phase = (int)r.Phase,
            round = r.RoundNo,
            timeLeftMs = (int)System.Math.Max(0, r.PhaseEndMs - NowMs()),
            attackScore = r.AttackScore,
            defenseScore = r.DefenseScore,
            matchOver = r.MatchOver
        };

        BattleServerNetwork.Instance.BroadcastToRoom(
            roomId, BattleMsgId.RoundState, JsonUtility.ToJson(dto));
    }

    /// <summary>
    /// 加入或重连: 同一用户名只保留一份状态
    /// </summary>
    public static int AddOrReplace(int connId, string username, string roomId, Vector3 pos, float yaw,
                                   string mode = "coop", int maxPlayers = 2)
    {
        if (string.IsNullOrEmpty(username)) username = "player_" + connId;

        string finalMode = mode == "pvp" ? "pvp" : "coop";
        _roomModes[roomId] = finalMode;

        var lobby = EnsureLobby(roomId);
        lobby.Capacity = Mathf.Clamp(maxPlayers <= 0 ? 2 : maxPlayers, 2, 6);

        // 找出这个用户名已有的 id(重连时复用, 保证其他玩家看到的 id 不变)
        if (!_idByUsername.TryGetValue(username, out int playerId))
        {
            playerId = ++_nextPlayerId;
            _idByUsername[username] = playerId;
        }

        if (_playersById.TryGetValue(playerId, out var exist))
        {
            // 重连: 只更新连接号/房间/位置, 不新增
            exist.ConnId = connId;
            exist.RoomId = roomId;
            exist.Pos = pos;
            exist.Yaw = yaw;
            exist.Ready = false;
            if (lobby.HostId <= 0) lobby.HostId = playerId;

            // 换房时新连接可能比旧连接断开先到, 这里也要登记新房间
            if (finalMode != "pvp") ServerEnemyRegistry.RegisterRoom(roomId, pos);
            else JoinRound(roomId, playerId);

            BroadcastLobby(roomId);

            Debug.Log($"[ServerWorld] 玩家 {username}(id={playerId}) 重连到房间 {roomId}, conn={connId}");
            return playerId;
        }

        _playersById[playerId] = new ServerPlayerState
        {
            PlayerId = playerId,
            Username = username,
            ConnId = connId,
            RoomId = roomId,
            Pos = pos,
            Yaw = yaw
        };
        if (lobby.HostId <= 0) lobby.HostId = playerId;
        Debug.Log($"[ServerWorld] 玩家 {username}(id={playerId}) 加入房间 {roomId}, conn={connId}, 当前 {PlayerCount} 人");

        // 合作模式才登记 PvE 刷怪房间; 战术模式不刷 AI
        if (finalMode != "pvp") ServerEnemyRegistry.RegisterRoom(roomId, pos);
        else JoinRound(roomId, playerId);

        BroadcastLobby(roomId);

        return playerId;
    }

    /// <summary>
    /// 连接断开时调用
    /// </summary>
    public static void RemoveByConn(int connId)
    {
        int pid = -1;
        foreach (var kv in _playersById)
            if (kv.Value.ConnId == connId) { pid = kv.Key; break; }

        if (pid > 0)
        {
            string user = _playersById[pid].Username;
            string roomId = _playersById[pid].RoomId;
            _playersById.Remove(pid);
            Debug.Log($"[ServerWorld] 玩家 {user}(id={pid}) 离开, 剩余 {PlayerCount} 人");

            // 这个房间还有人吗? 没有就把怪清掉, 下次有人进来重新刷
            bool roomStillHasPlayer = false;
            foreach (var p in _playersById.Values)
            {
                if (p.RoomId == roomId)
                {
                    roomStillHasPlayer = true;
                    break;
                }
            }

            if (!roomStillHasPlayer)
            {
                ServerEnemyRegistry.RemoveRoom(roomId);
                _roomModes.Remove(roomId);
                _lobbies.Remove(roomId);
                _rounds.Remove(roomId);
                _pendingSwaps.Remove(roomId);
            }
            else
            {
                // 房主离开: 移交给房间里剩下的第一个玩家
                if (_lobbies.TryGetValue(roomId, out var lobby) && lobby.HostId == pid)
                {
                    foreach (var other in _playersById.Values)
                    {
                        if (other.RoomId != roomId) continue;
                        lobby.HostId = other.PlayerId;
                        break;
                    }
                }

                BroadcastLobby(roomId);
            }
        }
    }

    public static void SetInput(int playerId, InputCommandDto input)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        p.Input = input;
        p.AnimId = input.animId;
        p.WeaponId = input.weaponId;

        if (!ServerAuthoritativeMovement)
        {
            Vector3 reported = new Vector3(input.x, input.y, input.z);

            // 传送落地: 直接接受(场景切换/传送门/死亡回城)
            if (input.teleport)
            {
                p.Pos = reported;
                p.Yaw = input.yaw;
                p.LastInputMs = input.clientTimeMs;
                p.History.Clear();
                AppendHistory(p);
                return;
            }

            float dt = Mathf.Clamp((input.clientTimeMs - p.LastInputMs) / 1000f, 0.01f, 0.5f);

            float dx = reported.x - p.Pos.x;
            float dy = reported.y - p.Pos.y;
            float dz = reported.z - p.Pos.z;
            float horizDist = Mathf.Sqrt(dx * dx + dz * dz);   // 只看水平位移
            float allowed = MaxMoveSpeed * dt + MoveTolerance;

            if (horizDist > TeleportThreshold)
            {
                // 只有大范围瞬移才硬拒
                RejectedCount++;
                Debug.LogWarning($"[ServerWorld] 玩家 {p.Username} 瞬移 {horizDist:F1}m, 已拒绝 #{RejectedCount}");
            }
            else
            {
                if (horizDist > allowed)
                {
                    SoftAnomalies++;
                    // 只打前几条详细信息, 避免刷屏
                    if (SoftAnomalies <= 5 || SoftAnomalies % 100 == 0)
                    {
                        Debug.LogWarning(
                            $"[ServerWorld] {p.Username} 位移偏快 #{SoftAnomalies}: " +
                            $"dt={dt * 1000f:F0}ms 水平={horizDist:F2}m 允许={allowed:F2}m " +
                            $"dx={dx:F2} dy={dy:F2} dz={dz:F2}");
                    }
                }

                p.Pos = reported;
                AppendHistory(p);
            }

            // 朝向独立更新: 位置被拒也不该把转身一起丢掉
            p.Yaw = input.yaw;
            p.LastInputMs = input.clientTimeMs;
        }

        TryPositionSpikeAtAttacker(p);
    }

    /// <summary>第一次拿到进攻方的真实位置(传送后)时, 把包放到他旁边</summary>
    private static void TryPositionSpikeAtAttacker(ServerPlayerState p)
    {
        if (p.Team != 0) return;
        if (!_rounds.TryGetValue(p.RoomId, out var r)) return;
        if (r.SpikePositioned || r.SpikeState != 0) return;
        if (r.Phase != RoundPhase.Buy && r.Phase != RoundPhase.Combat) return;

        r.SpikePos = p.Pos + new Vector3(1f, 0f, 1f);
        r.SpikePositioned = true;
        BroadcastSpike(p.RoomId, r);
        Debug.Log($"[Spike] 包放在进攻方 {p.Username} 旁边 {r.SpikePos}");
    }

    /// <summary>
    /// 每个 tick 调用
    /// </summary>
    public static void Step(float dt)
    {
        long now = NowMs();

        // 战术模式回合推进
        TickRounds(now);

        _roundBroadcastTimer += dt;
        if (_roundBroadcastTimer >= 1f)
        {
            _roundBroadcastTimer = 0f;
            SweepSwapRequests(now);
            if (_rounds.Count > 0)
                foreach (var kv in _rounds)
                {
                    BroadcastRound(kv.Key, kv.Value);
                    if (kv.Value.Phase == RoundPhase.CharacterSelect)
                        BroadcastCharacterSelect(kv.Key, kv.Value);
                    if (kv.Value.Phase == RoundPhase.Combat)
                        BroadcastSpike(kv.Key, kv.Value);
                }
        }

        //玩家复活检查
        foreach (var p in _playersById.Values)
        {
            if (p.Hp <= 0 && p.RespawnAtMs > 0 && !p.EliminationDead && now >= p.RespawnAtMs)
            {
                p.Hp = MatchMaxHp;
                p.RespawnAtMs = 0;
                p.History.Clear();
                Debug.Log($"[ServerWorld] 玩家 {p.Username} 已复活");
            }
        }

        //敌人 AI 推进
        ServerEnemyRegistry.Tick(dt);

        //统计
        if (now - _lastStatLogMs > 5000)
        {
            _lastStatLogMs = now;
            float rate = ShotsReceived > 0 ? HitsLanded * 100f / ShotsReceived : 0f;
            Debug.Log($"[Stats] 开火={ShotsReceived} 命中={HitsLanded} 命中率={rate:F1}% 延迟补偿={(RewindEnabled ? "开" : "关")} 敌人={ServerEnemyRegistry.CountAll()}");
        }

        //服务器权威移动
        if (!ServerAuthoritativeMovement) return;
        foreach (var p in _playersById.Values)
        {
            if (p.Input == null) continue;
            Vector3 dir = new Vector3(p.Input.moveX, 0f, p.Input.moveZ);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            p.Pos += dir * MoveSpeed * dt;
            p.Yaw = p.Input.yaw;
        }
    }

    /// <summary>
    /// 某个房间的玩家状态列表
    /// </summary>
    public static List<PlayerStateDto> BuildPlayerStates(string roomId)
    {
        var list = new List<PlayerStateDto>();
        foreach (var p in _playersById.Values)
        {
            if (p.RoomId != roomId) continue;
            list.Add(new PlayerStateDto
            {
                id = p.PlayerId,
                x = p.Pos.x,
                y = p.Pos.y,
                z = p.Pos.z,
                yaw = p.Yaw,
                anim = "Idle",
                animId = p.AnimId,
                weaponId = p.WeaponId,
                team = p.Team,
                matchCoins = p.MatchCoins,
                characterId = p.CharacterId,
                armor = p.Armor,
                matchWeaponId = p.MatchWeaponId,
                matchWeaponDamage = p.MatchWeaponDamage,
                hp = p.Hp
            });
        }
        return list;
    }

    /// <summary>
    /// 某个房间的敌人状态列表
    /// </summary>
    public static List<EnemyStateDto> BuildEnemyStates(string roomId)
    {
        var list = new List<EnemyStateDto>();
        foreach (var e in ServerEnemyRegistry.ListInRoom(roomId))
        {
            list.Add(new EnemyStateDto
            {
                id = e.Id,
                kind = e.Kind,
                x = e.Pos.x,
                y = e.Pos.y,
                z = e.Pos.z,
                yaw = e.Yaw,
                hp = e.Hp,
                maxHp = e.MaxHp,
                state = e.State,
                elite = e.IsElite ? 1 : 0
            });
        }
        return list;
    }

    /// <summary>
    /// 当前有哪些房间(用于按房间广播)
    /// </summary>
    public static List<string> RoomIds()
    {
        var set = new HashSet<string>();
        foreach (var p in _playersById.Values) set.Add(p.RoomId);
        return new List<string>(set);
    }

    /// <summary>
    /// 找房间里离某点最近的活着的玩家(不进堆, 供敌人 AI 每 tick 调用)
    /// </summary>
    public static ServerPlayerState FindNearestAlivePlayer(string roomId, Vector3 from, out float distance)
    {
        ServerPlayerState best = null;
        float bestSqr = float.MaxValue;

        foreach (var p in _playersById.Values)
        {
            if (p.RoomId != roomId) continue;
            if (p.Hp <= 0) continue;

            float dx = p.Pos.x - from.x;
            float dz = p.Pos.z - from.z;
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = p;
            }
        }

        distance = best != null ? Mathf.Sqrt(bestSqr) : float.MaxValue;
        return best;
    }

    #region 开火与延迟补偿
    public static bool RewindEnabled = true;   // 延迟补偿开关
    public static bool PvPDeathEnabled = true; // 实验用: false 时玩家不会被真正打死
    public static int RewindPaddingMs = 100;   // 客户端插值延迟补偿（其他客户端本地会把网络位置延迟约 100ms 渲染，通过补偿回溯这部分来防止打到却没有伤害的问题）
    public static int WeaponDamage = 25;       // P3 固定伤害(P4 再按武器表算)
    public const float HitRadius = 0.45f;      // 命中圆柱半径
    public const float HitHeight = 1.8f;       // 命中圆柱高度
    public const float MaxFireDistance = 300f;//子弹飞行最远距离
    public const float ShotVisualDistance = 40f; // 未命中时子弹视觉飞行距离

    private static long NowMs() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();//返回当前UTC 毫秒时间戳

    /// <summary>
    /// 记录一次位置样本(保留最近 1 秒)
    /// </summary>
    private static void AppendHistory(ServerPlayerState p)
    {
        long now = NowMs();
        p.History.Add(new PosSample { TimeMs = now, Pos = p.Pos });
        while (p.History.Count > 0 && now - p.History[0].TimeMs > 1000)
            p.History.RemoveAt(0);//超过1秒的旧样本删除
    }

    /// <summary>
    /// 取某玩家在指定时刻的位置(延迟补偿用)
    /// </summary>
    public static Vector3 GetPositionAt(int playerId, long timeMs)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return Vector3.zero;
        if (p.History.Count == 0) return p.Pos;//刚加入的玩家没有历史位置，就先用当前位置

        Vector3 best = p.History[0].Pos;
        long bestDiff = long.MaxValue;
        //通过历史位置样本，找离我们所需要的时间最近的一个
        for (int i = 0; i < p.History.Count; i++)
        {
            long d = System.Math.Abs(p.History[i].TimeMs - timeMs);
            if (d < bestDiff) { bestDiff = d; best = p.History[i].Pos; }
        }
        return best;
    }

    /// <summary>
    /// 不同体型的敌人用不同的判定体(史莱姆矮宽, 石头人高宽)
    /// </summary>
    private static void GetEnemyHitShape(ServerEnemy e, out float radius, out float height)
    {
        switch (e.Kind)
        {
            case 1:   // 石头人
                radius = 1.05f;
                height = 2.60f;
                break;

            case 2:   // 史莱姆
                radius = 0.95f;
                height = 0.90f;
                break;

            default:  // 丘丘人
                radius = EnemyHitRadius;
                height = EnemyHitHeight;
                break;
        }

        if (e.IsElite)
        {
            radius *= 1.4f;
            height *= 1.25f;
        }
    }

    /// <summary>
    /// 射线 vs 竖直圆柱(胶囊简化)
    /// </summary>
    private static bool RayVsCapsule(Vector3 origin, Vector3 dir, float maxDist,
                                    Vector3 basePos, float radius, float height, out float hitDist)
    {
        hitDist = 0f;
        float y0 = basePos.y, y1 = basePos.y + height;

        //射线起点相对于圆柱中心的水平偏移
        Vector3 oc = new Vector3(origin.x - basePos.x, 0f, origin.z - basePos.z);
        //射线方向的水平分量
        Vector3 dc = new Vector3(dir.x, 0f, dir.z);
        //水平方向长度的平方
        float a = Vector3.Dot(dc, dc);
        //朝圆心方向前进
        float b = 2f * Vector3.Dot(oc, dc);
        //c > 0 表示起点在圆外；c < 0 表示起点在圆内（贴脸开枪）。24.7975 > 0，我们站在圆外 5 米处
        float c = Vector3.Dot(oc, oc) - radius * radius;

        if (a < 1e-5f)   // 竖直射线
        {
            if (Vector3.Dot(oc, oc) > radius * radius) return false;
            if (Mathf.Abs(dir.y) < 1e-5f) return false;
            float ta = (y0 - origin.y) / dir.y;
            float tb = (y1 - origin.y) / dir.y;
            float t = Mathf.Min(ta, tb);
            if (t < 0f || t > maxDist) return false;
            hitDist = t; return true;
        }

        float disc = b * b - 4f * a * c;
        if (disc < 0f) return false;
        float sq = Mathf.Sqrt(disc);
        float t1 = (-b - sq) / (2f * a);
        float t2 = (-b + sq) / (2f * a);

        if (t1 >= 0f && t1 <= maxDist)
        {
            float y = origin.y + dir.y * t1;
            if (y >= y0 && y <= y1)
            {
                hitDist = t1; return true;
            }
        }
        if (t2 >= 0f && t2 <= maxDist)
        {
            float y = origin.y + dir.y * t2;
            if (y >= y0 && y <= y1)
            {
                hitDist = t2; return true;
            }
        }
        return false;
    }

    public const float EnemyHitRadius = 0.6f;
    public const float EnemyHitHeight = 1.8f;

    /// <summary>
    /// 处理一次开火: 回溯玩家位置 → 射线判定(玩家/敌人) → 扣血 → 广播
    /// </summary>
    public static void HandleFire(int shooterId, FireEventDto fire)
    {
        if (!_playersById.TryGetValue(shooterId, out var shooter)) return;

        long now = NowMs();
        if (now - shooter.LastFireMs < shooter.MatchWeaponIntervalMs) return;
        ShotsReceived++;
        shooter.LastFireMs = now;



        long rewindMs = RewindEnabled ? (fire.pingMs / 2 + RewindPaddingMs) : 0;

        // 火力强化: 服务器判定, 客户端不参与
        int damage = Mathf.RoundToInt(
            shooter.MatchWeaponDamage * (now < shooter.DamageBoostUntilMs ? 1.5f : 1f));

        long targetTime = now - rewindMs;

        Vector3 origin = new Vector3(fire.ox, fire.oy, fire.oz);
        Vector3 dir = new Vector3(fire.dx, fire.dy, fire.dz).normalized;

        int bestPlayerId = 0;
        int bestEnemyId = 0;
        float bestDist = float.MaxValue;
        Vector3 bestHit = Vector3.zero;

        // 玩家目标(回溯): 只有战术模式才允许玩家互相伤害
        if (IsPvPRoom(shooter.RoomId))
        {
            foreach (var p in _playersById.Values)
            {
                if (p.PlayerId == shooterId) continue;
                if (p.RoomId != shooter.RoomId) continue;
                if (p.Hp <= 0) continue;

                Vector3 basePos = GetPositionAt(p.PlayerId, targetTime);
                if (RayVsCapsule(origin, dir, MaxFireDistance, basePos, HitRadius, HitHeight, out float d)
                    && d < bestDist)
                {
                    bestDist = d;
                    bestPlayerId = p.PlayerId;
                    bestEnemyId = 0;
                    bestHit = origin + dir * d;
                }
            }
        }

        // 敌人目标(用当前位置, 精英的判定体放大)
        foreach (var e in ServerEnemyRegistry.ListInRoom(shooter.RoomId))
        {
            if (e.Hp <= 0) continue;

            GetEnemyHitShape(e, out float radius, out float height);

            if (RayVsCapsule(origin, dir, MaxFireDistance, e.Pos, radius, height, out float d)
                && d < bestDist)
            {
                bestDist = d;
                bestPlayerId = 0;
                bestEnemyId = e.Id;
                bestHit = origin + dir * d;
            }
        }

        bool didHit = bestPlayerId != 0 || bestEnemyId != 0;

        // 广播弹道给同房间其他玩家(自己的子弹由本地表现)
        Vector3 shotEnd = didHit ? bestHit : origin + dir * ShotVisualDistance;
        BroadcastPlayerShot(shooter, shotEnd, didHit);

        if (!didHit)
        {
            Debug.Log($"[Fire] {shooter.Username} 未命中 (回溯 {rewindMs}ms)");
            return;
        }

        HitsLanded++;

        // 打中敌人
        if (bestEnemyId > 0)
        {
            var enemy = ServerEnemyRegistry.Get(bestEnemyId);
            if (enemy == null) return;

            enemy.Hp -= damage;
            bool killed = enemy.Hp <= 0;
            if (killed)
            {
                enemy.Hp = 0;
                enemy.State = 3;
            }

            var hitDto = new EnemyHitDto
            {
                shooterId = shooterId,
                enemyId = enemy.Id,
                damage = damage,
                enemyHp = enemy.Hp,
                enemyMaxHp = enemy.MaxHp,
                isKill = killed,
                hitX = bestHit.x,
                hitY = bestHit.y,
                hitZ = bestHit.z
            };
            BattleServerNetwork.Instance.BroadcastToRoom(
                shooter.RoomId, BattleMsgId.EnemyHit, JsonUtility.ToJson(hitDto));

            Debug.Log($"[Fire] {shooter.Username} → 敌人#{enemy.Id} 命中 {WeaponDamage}, HP={enemy.Hp}");

            if (killed)
            {
                int reward = enemy.IsElite ? 50 : 10;
                shooter.MatchCoins += reward;

                var rewardDto = new RewardDto
                {
                    playerId = shooterId,
                    coins = reward,
                    total = shooter.MatchCoins,
                    elite = enemy.IsElite,
                    reason = enemy.IsElite ? "精英击杀" : "击杀"
                };
                BattleServerNetwork.Instance.BroadcastToRoom(
                    shooter.RoomId, BattleMsgId.Reward, JsonUtility.ToJson(rewardDto));

                ServerEnemyRegistry.OnEnemyKilled(enemy);
            }
            return;
        }

        // 打中玩家
        var target = _playersById[bestPlayerId];

        int previousAttacker = target.LastAttackerId;
        long previousAttackMs = target.LastAttackMs;
        target.LastAttackerId = shooterId;
        target.LastAttackMs = now;

        ApplyArmorDamage(target, damage);
        bool isKill = target.Hp <= 0;

        if (isKill && !PvPDeathEnabled)
        {
            // 实验模式: 血量卡在 1, 避免死亡后 3 秒空窗污染命中率统计
            target.Hp = 1;
            isKill = false;
        }
        else if (isKill)
        {
            target.Hp = 0;

            if (IsPvPRoom(shooter.RoomId))
            {
                // 战术模式: 回合内阵亡不复活, 等下一回合
                target.EliminationDead = true;
                target.RespawnAtMs = 0;

                shooter.Kills++;
                target.Deaths++;

                // 携带者阵亡: 包掉在原地
                if (_rounds.TryGetValue(shooter.RoomId, out var round)
                    && round.SpikeState == 1 && round.SpikeCarrierId == target.PlayerId)
                {
                    round.SpikeState = 0;
                    round.SpikeCarrierId = -1;
                    round.SpikePos = target.Pos;
                    Debug.Log($"[Spike] 携带者 {target.Username} 阵亡, 包掉落");
                    BroadcastSpike(shooter.RoomId, round);
                }

                // 5 秒内最后一个其他伤害来源记一次助攻
                if (previousAttacker > 0 && previousAttacker != shooterId
                    && now - previousAttackMs <= 5000
                    && _playersById.TryGetValue(previousAttacker, out var assistant))
                {
                    assistant.Assists++;
                }

                shooter.MatchCoins += KillMoney;

                var killReward = new RewardDto
                {
                    playerId = shooterId,
                    coins = KillMoney,
                    total = shooter.MatchCoins,
                    elite = false,
                    reason = "击杀奖励"
                };
                BattleServerNetwork.Instance.SendToPlayer(
                    shooterId, BattleMsgId.Reward, JsonUtility.ToJson(killReward));
            }
            else
            {
                target.RespawnAtMs = now + 3000;
            }
        }

        Debug.Log($"[Fire] {shooter.Username} → {target.Username} 命中 {WeaponDamage} 伤害, HP={target.Hp}, 回溯={rewindMs}ms");

        var res = new DamageResultDto
        {
            attackerKind = 0,
            shooterId = shooterId,
            targetId = bestPlayerId,
            damage = damage,
            targetHp = target.Hp,
            targetArmor = target.Armor,
            isKill = isKill,
            hitX = bestHit.x,
            hitY = bestHit.y,
            hitZ = bestHit.z
        };
        BattleServerNetwork.Instance.BroadcastToRoom(shooter.RoomId, BattleMsgId.Damage, JsonUtility.ToJson(res));
    }

    /// <summary>把玩家这一次开火的弹道广播给同房间其他人</summary>
    private static void BroadcastPlayerShot(ServerPlayerState shooter, Vector3 end, bool hit)
    {
        // 表现层从角色胸口发出, 而不是相机位置(相机在别人眼里是悬空的)
        Vector3 visualStart = new Vector3(shooter.Pos.x, shooter.Pos.y + 1.35f, shooter.Pos.z);

        var dto = new PlayerShotDto
        {
            shooterId = shooter.PlayerId,
            weaponId = shooter.WeaponId,
            ox = visualStart.x, oy = visualStart.y, oz = visualStart.z,
            ex = end.x, ey = end.y, ez = end.z,
            hit = hit
        };
        BattleServerNetwork.Instance.BroadcastToRoom(
            shooter.RoomId, BattleMsgId.PlayerShot, JsonUtility.ToJson(dto));
    }

    /// <summary>
    /// 敌人打玩家,damage<0 时用敌人自身攻击力
    /// </summary>
    public static void DamagePlayerByEnemy(ServerEnemy enemy, ServerPlayerState target, int damage = -1)
    {
        if (enemy == null || target == null || target.Hp <= 0) return;
        if (damage < 0) damage = enemy.AttackDamage;

        ApplyArmorDamage(target, damage);
        bool isKill = target.Hp <= 0;
        if (isKill)
        {
            target.Hp = 0;
            target.RespawnAtMs = NowMs() + 3000;
        }

        var res = new DamageResultDto
        {
            attackerKind = 1,
            shooterId = enemy.Id,
            targetId = target.PlayerId,
            damage = damage,
            targetHp = target.Hp,
            targetArmor = target.Armor,
            isKill = isKill,
            hitX = target.Pos.x,
            hitY = target.Pos.y + 1f,
            hitZ = target.Pos.z
        };
        BattleServerNetwork.Instance.BroadcastToRoom(
            target.RoomId, BattleMsgId.Damage, JsonUtility.ToJson(res));

        Debug.Log($"[EnemyAttack] 敌人#{enemy.Id}(kind={enemy.Kind}) → {target.Username} {damage} 伤害, HP={target.Hp}");
    }

    /// <summary>
    /// 远程敌人开枪: 服务器算命中, 再广播弹道给客户端画
    /// </summary>
    public static void EnemyShoot(ServerEnemy enemy, ServerPlayerState target, float dist)
    {
        if (enemy == null || target == null || target.Hp <= 0) return;

        Vector3 from = enemy.Pos + Vector3.up * 1.4f;
        Vector3 aim = target.Pos + Vector3.up * 1.2f;

        // 距离越远越容易打偏: 90% → 30%
        float hitChance = Mathf.Clamp(0.9f - dist * 0.03f, 0.3f, 0.9f);
        bool hit = UnityEngine.Random.value <= hitChance;

        if (!hit)
        {
            aim += new Vector3(
                UnityEngine.Random.Range(-1.5f, 1.5f),
                UnityEngine.Random.Range(-0.5f, 0.8f),
                UnityEngine.Random.Range(-1.5f, 1.5f));
        }

        var shot = new EnemyShotDto
        {
            enemyId = enemy.Id,
            fx = from.x,
            fy = from.y,
            fz = from.z,
            tx = aim.x,
            ty = aim.y,
            tz = aim.z,
            hit = hit
        };
        BattleServerNetwork.Instance.BroadcastToRoom(
            enemy.RoomId, BattleMsgId.EnemyShot, JsonUtility.ToJson(shot));

        if (hit) DamagePlayerByEnemy(enemy, target);
    }

    /// <summary>
    /// 史莱姆死亡爆炸: 3 米内所有人受伤, 越靠外伤害越低
    /// </summary>
    public static void EnemyExplosion(ServerEnemy enemy)
    {
        if (enemy == null) return;

        const float radius = 3f;
        const int maxDamage = 20;

        foreach (var p in _playersById.Values)
        {
            if (p.RoomId != enemy.RoomId) continue;
            if (p.Hp <= 0) continue;

            float dx = p.Pos.x - enemy.Pos.x;
            float dz = p.Pos.z - enemy.Pos.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d > radius) continue;

            int dmg = Mathf.RoundToInt(maxDamage * (1f - d / radius * 0.5f));
            DamagePlayerByEnemy(enemy, p, dmg);
        }

        Debug.Log($"[EnemyExplosion] 敌人#{enemy.Id} 在 {enemy.Pos} 爆炸");
    }

    /// <summary>取某个玩家本局金币(入房时同步给客户端)</summary>
    public static int GetMatchCoins(int playerId)
    {
        return _playersById.TryGetValue(playerId, out var p) ? p.MatchCoins : 0;
    }

    /// <summary>客户端点击"开始挑战": 由服务器决定是否开波</summary>
    public static void RequestStartWave(int playerId)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        if (string.IsNullOrEmpty(p.RoomId)) return;

        if (IsPvPRoom(p.RoomId))
        {
            Debug.Log("[ServerWorld] 战术模式不需要开波, 忽略请求");
            return;
        }

        if (!ServerEnemyRegistry.StartWave(p.RoomId))
        {
            Debug.Log($"[ServerWorld] 玩家 {p.Username} 请求开波被忽略(已开始或未登记)");
            return;
        }

        Debug.Log($"[ServerWorld] 玩家 {p.Username} 触发房间 {p.RoomId} 开波");
    }

    public const int WaveClearReward = 15;

    /// <summary>一波清空: 房间里每个玩家都拿奖励, 强化合作</summary>
    public static void AwardWaveClear(string roomId)
    {
        foreach (var p in _playersById.Values)
        {
            if (p.RoomId != roomId) continue;

            p.MatchCoins += WaveClearReward;

            var dto = new RewardDto
            {
                playerId = p.PlayerId,
                coins = WaveClearReward,
                total = p.MatchCoins,
                elite = false,
                reason = "波次奖励"
            };
            BattleServerNetwork.Instance.SendToPlayer(
                p.PlayerId, BattleMsgId.Reward, JsonUtility.ToJson(dto));
        }

        Debug.Log($"[Reward] 房间 {roomId} 清空一波, 每人 +{WaveClearReward}");
    }
    #endregion

    #region 局内商店
    public const int PriceHeal = 30;
    public const int PriceDamageBoost = 60;
    public const int PriceTeamHeal = 80;
    public const int DamageBoostDurationMs = 10000;

    // 战斗中禁止购买(波次清空后的 8 秒间隙才能买)
    public static bool ShopOnlyBetweenWaves = true;

    public static void HandleBuy(int playerId, BuyRequestDto req)
    {
        if (!_playersById.TryGetValue(playerId, out var buyer)) return;
        if (req == null) return;

        int price;
        switch (req.itemId)
        {
            case 1: price = PriceHeal; break;
            case 2: price = PriceDamageBoost; break;
            case 3: price = PriceTeamHeal; break;
            default:
                ReplyBuy(buyer, req.itemId, false, "没有这个商品", 0);
                return;
        }

        if (ShopOnlyBetweenWaves && ServerEnemyRegistry.AliveCount(buyer.RoomId) > 0)
        {
            ReplyBuy(buyer, req.itemId, false, "战斗中无法购买", price);
            return;
        }

        if (buyer.MatchCoins < price)
        {
            ReplyBuy(buyer, req.itemId, false, "金币不足", price);
            return;
        }

        string okMsg;

        switch (req.itemId)
        {
            case 1:
                if (buyer.Hp >= MatchMaxHp)
                {
                    ReplyBuy(buyer, req.itemId, false, "血量已满", price);
                    return;
                }
                buyer.Hp = Mathf.Min(MatchMaxHp, buyer.Hp + 35);
                okMsg = "回复 35 点生命";
                break;

            case 2:
                buyer.DamageBoostUntilMs = NowMs() + DamageBoostDurationMs;
                okMsg = "火力强化 10 秒 (伤害 x1.5)";
                break;

            default:
                foreach (var p in _playersById.Values)
                {
                    if (p.RoomId != buyer.RoomId) continue;
                    if (p.Hp <= 0) continue;
                    p.Hp = Mathf.Min(MatchMaxHp, p.Hp + 25);
                }
                okMsg = "全队回复 25 点生命";
                break;
        }

        buyer.MatchCoins -= price;
        ReplyBuy(buyer, req.itemId, true, okMsg, price);
        Debug.Log($"[Shop] {buyer.Username} 购买 item={req.itemId}, 花费 {price}, 剩余 {buyer.MatchCoins}");
    }

    private static void ReplyBuy(ServerPlayerState buyer, int itemId, bool success, string msg, int price)
    {
        var res = new BuyResultDto
        {
            success = success,
            itemId = itemId,
            msg = msg,
            coinsLeft = buyer.MatchCoins,
            myHp = buyer.Hp,
            price = price
        };
        BattleServerNetwork.Instance.SendToPlayer(
            buyer.PlayerId, BattleMsgId.BuyResult, JsonUtility.ToJson(res));
    }
    #endregion

}
