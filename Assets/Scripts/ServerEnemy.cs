using System.Collections.Generic;
using UnityEngine;

/// <summary>服务器上的敌人状态(纯数据)</summary>
public class ServerEnemy
{
    public int Id;
    public string RoomId;
    public int Kind;              // 0=近战 1=远程 2=史莱姆
    public bool IsElite;
    public Vector3 SpawnPos;
    public Vector3 Pos;
    public float Yaw;
    public int Hp = 100;
    public int MaxHp = 100;
    public int State;             // 0=Idle 1=Reposition 2=Attack 3=Dead
    public int TargetPlayerId = -1;
    public long LastAttackMs;

    public float Speed = 2.8f;
    public float DetectRange = 15f;
    public float AttackRange = 2.2f;
    public float PreferredRange = 0f;   // 远程怪想保持的距离
    public int AttackDamage = 8;
    public int AttackCooldownMs = 1000;
}

public static class ServerEnemyRegistry
{
    public const int RespawnDelayMs = 8000;
    public const int MaxWaveSize = 6;
    public const float SpawnRadius = 8f;

    private static readonly Dictionary<int, ServerEnemy> _byId = new Dictionary<int, ServerEnemy>();
    private static readonly Dictionary<string, List<int>> _byRoom = new Dictionary<string, List<int>>();
    private static readonly Dictionary<string, long> _nextWaveAt = new Dictionary<string, long>();
    private static readonly Dictionary<string, Vector3> _waveCenter = new Dictionary<string, Vector3>();
    private static readonly Dictionary<string, int> _waveNo = new Dictionary<string, int>();
    private static readonly HashSet<string> _spawnedRooms = new HashSet<string>();
    private static int _nextId = 0;

    private static long NowMs() => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static int WaveSizeFor(int wave) => Mathf.Min(3 + (wave - 1) / 2, MaxWaveSize);
    public static int HpFor(int wave) => 100 + 25 * (wave - 1);

    public static int Count(string roomId)
    {
        return _byRoom.TryGetValue(roomId, out var list) ? list.Count : 0;
    }

    public static int CountAll() => _byId.Count;

    public static int CurrentWave(string roomId)
    {
        return _waveNo.TryGetValue(roomId, out int w) ? w : 1;
    }

    public static void EnsureRoom(string roomId, Vector3 center)
    {
        if (string.IsNullOrEmpty(roomId)) return;
        if (_spawnedRooms.Contains(roomId)) return;

        _spawnedRooms.Add(roomId);
        _waveCenter[roomId] = center;
        _waveNo[roomId] = 1;
        SpawnWave(roomId, center, 1);
    }

    public static void SpawnWave(string roomId, Vector3 center, int wave)
    {
        if (!_byRoom.TryGetValue(roomId, out var list))
        {
            list = new List<int>();
            _byRoom[roomId] = list;
        }

        int count = WaveSizeFor(wave);
        int hp = HpFor(wave);
        bool eliteWave = wave % 3 == 0;   // 每 3 波出精英

        for (int i = 0; i < count; i++)
        {
            bool isElite = eliteWave && i == count - 1;   // 最后一只当精英

            if (i < list.Count && _byId.TryGetValue(list[i], out var exist))
            {
                ResetEnemy(exist, center, i, hp, isElite);
                continue;
            }

            var created = CreateEnemy(roomId, center, i, hp, isElite);
            list.Add(created.Id);
        }

        for (int i = count; i < list.Count; i++)
        {
            if (_byId.TryGetValue(list[i], out var extra))
            {
                extra.Hp = 0;
                extra.State = 3;
            }
        }

        BroadcastWave(roomId, count, false, 0, wave, count);
        Debug.Log($"[ServerEnemy] 房间 {roomId} 第 {wave} 波: {count} 只, 基础 HP={hp}{(eliteWave ? " [含精英]" : "")}");
    }

    public static void Tick(float dt)
    {
        long now = NowMs();
        foreach (var kv in _byRoom) TryRespawnWave(kv.Key, now);

        foreach (var e in _byId.Values)
        {
            if (e.Hp <= 0)
            {
                SetState(e, 3);
                continue;
            }

            ServerPlayerState target =
                ServerWorld.FindNearestAlivePlayer(e.RoomId, e.Pos, out float dist);

            if (target == null)
            {
                SetState(e, 0);
                continue;
            }

            switch (e.Kind)
            {
                case 1: TickRanged(e, target, dist, dt, now); break;
                case 2: TickSlime(e, target, dist, dt, now); break;
                default: TickMelee(e, target, dist, dt, now); break;
            }
        }
    }

    // ===== 近战: 直线冲锋, 贴身砍 =====
    private static void TickMelee(ServerEnemy e, ServerPlayerState target, float dist, float dt, long now)
    {
        if (dist <= e.AttackRange)
        {
            SetState(e, 2);
            FaceTo(e, target.Pos);
            if (now - e.LastAttackMs >= e.AttackCooldownMs)
            {
                e.LastAttackMs = now;
                e.TargetPlayerId = target.PlayerId;
                ServerWorld.DamagePlayerByEnemy(e, target);
            }
            return;
        }

        if (dist <= e.DetectRange)
        {
            SetState(e, 1);
            MoveToward(e, target.Pos, e.AttackRange, dt);
            return;
        }

        SetState(e, 0);
    }

    // ===== 远程: 拉扯 + 横移 + 开枪 =====
    private static void TickRanged(ServerEnemy e, ServerPlayerState target, float dist, float dt, long now)
    {
        if (dist > e.DetectRange)
        {
            SetState(e, 0);
            return;
        }

        // 贴太近: 后撤
        if (dist < e.PreferredRange - 1.5f)
        {
            SetState(e, 1);
            FaceTo(e, target.Pos);
            MoveAway(e, target.Pos, dt);
            return;
        }

        // 太远: 靠近到射程内
        if (dist > e.AttackRange)
        {
            SetState(e, 1);
            MoveToward(e, target.Pos, e.PreferredRange, dt);
            FaceTo(e, target.Pos);
            return;
        }

        // 射程内: 侧移拉扯 + 周期开枪
        SetState(e, 2);
        FaceTo(e, target.Pos);
        Strafe(e, target.Pos, dt);

        if (now - e.LastAttackMs >= e.AttackCooldownMs)
        {
            e.LastAttackMs = now;
            e.TargetPlayerId = target.PlayerId;
            ServerWorld.EnemyShoot(e, target, dist);
        }
    }

    // ===== 史莱姆: 慢速逼近, 死亡爆炸在 OnEnemyKilled 里处理 =====
    private static void TickSlime(ServerEnemy e, ServerPlayerState target, float dist, float dt, long now)
    {
        if (dist <= e.AttackRange)
        {
            SetState(e, 2);
            FaceTo(e, target.Pos);
            if (now - e.LastAttackMs >= e.AttackCooldownMs)
            {
                e.LastAttackMs = now;
                e.TargetPlayerId = target.PlayerId;
                ServerWorld.DamagePlayerByEnemy(e, target);
            }
            return;
        }

        if (dist <= e.DetectRange)
        {
            SetState(e, 1);
            MoveToward(e, target.Pos, e.AttackRange, dt);
            return;
        }

        SetState(e, 0);
    }

    public static void OnEnemyKilled(ServerEnemy enemy)
    {
        if (enemy == null) return;
        string roomId = enemy.RoomId;

        // 史莱姆自爆
        if (enemy.Kind == 2)
            ServerWorld.EnemyExplosion(enemy);

        int alive = AliveCount(roomId);
        int wave = CurrentWave(roomId);
        int total = WaveSizeFor(wave);

        if (alive > 0)
        {
            BroadcastWave(roomId, alive, false, 0, wave, total);
            return;
        }

        _nextWaveAt[roomId] = NowMs() + RespawnDelayMs;
        BroadcastWave(roomId, 0, true, RespawnDelayMs, wave, total);

        // 清波奖励: 房间里每个人都能拿(包括刚倒下的)
        ServerWorld.AwardWaveClear(roomId);

        Debug.Log($"[ServerEnemy] 房间 {roomId} 第 {wave} 波清空, {RespawnDelayMs}ms 后下一波");
    }

    public static int AliveCount(string roomId)
    {
        int n = 0;
        if (!_byRoom.TryGetValue(roomId, out var list)) return 0;
        foreach (int id in list)
            if (_byId.TryGetValue(id, out var e) && e.Hp > 0) n++;
        return n;
    }

    public static ServerEnemy Get(int id)
    {
        return _byId.TryGetValue(id, out var e) ? e : null;
    }

    public static List<ServerEnemy> ListInRoom(string roomId)
    {
        var result = new List<ServerEnemy>();
        if (!_byRoom.TryGetValue(roomId, out var list)) return result;
        foreach (int id in list)
            if (_byId.TryGetValue(id, out var e)) result.Add(e);
        return result;
    }

    public static void RemoveRoom(string roomId)
    {
        if (!_byRoom.TryGetValue(roomId, out var list)) return;
        foreach (int id in list) _byId.Remove(id);

        _byRoom.Remove(roomId);
        _nextWaveAt.Remove(roomId);
        _waveCenter.Remove(roomId);
        _waveNo.Remove(roomId);
        _spawnedRooms.Remove(roomId);
        Debug.Log($"[ServerEnemy] 房间 {roomId} 已清理");
    }

    // ===== 工厂与工具 =====
    private static ServerEnemy CreateEnemy(string roomId, Vector3 center, int index, int baseHp, bool isElite)
    {
        int id = ++_nextId;
        Vector3 spawn = SpawnPosFor(center, index);

        var e = new ServerEnemy
        {
            Id = id,
            RoomId = roomId,
            Kind = index % 3,
            IsElite = isElite,
            SpawnPos = spawn,
            Pos = spawn
        };
        ApplyKindStats(e, baseHp, isElite);
        _byId[id] = e;
        return e;
    }

    private static void ResetEnemy(ServerEnemy e, Vector3 center, int index, int baseHp, bool isElite)
    {
        e.SpawnPos = SpawnPosFor(center, index);
        e.Pos = e.SpawnPos;
        e.Kind = index % 3;
        e.IsElite = isElite;
        e.State = 0;
        e.Yaw = 0f;
        e.TargetPlayerId = -1;
        e.LastAttackMs = 0;
        ApplyKindStats(e, baseHp, isElite);
    }

    /// <summary>
    /// 按种类套用属性; baseHp 是波次基础血量
    /// </summary>
    private static void ApplyKindStats(ServerEnemy e, int baseHp, bool isElite)
    {
        switch (e.Kind)
        {
            case 1:
                e.MaxHp = Mathf.RoundToInt(baseHp * 0.8f);
                e.Speed = 2.2f;
                e.DetectRange = 18f;
                e.AttackRange = 12f;
                e.PreferredRange = 7f;
                e.AttackDamage = 10;
                e.AttackCooldownMs = 1800;
                break;

            case 2:
                e.MaxHp = Mathf.RoundToInt(baseHp * 1.6f);
                e.Speed = 1.6f;
                e.DetectRange = 12f;
                e.AttackRange = 2.0f;
                e.PreferredRange = 0f;
                e.AttackDamage = 14;
                e.AttackCooldownMs = 1500;
                break;

            default:
                e.MaxHp = baseHp;
                e.Speed = 2.8f;
                e.DetectRange = 15f;
                e.AttackRange = 2.2f;
                e.PreferredRange = 0f;
                e.AttackDamage = 8;
                e.AttackCooldownMs = 1000;
                break;
        }

        // 精英强化
        if (isElite)
        {
            e.MaxHp = Mathf.RoundToInt(e.MaxHp * 2f);
            e.AttackDamage = Mathf.RoundToInt(e.AttackDamage * 1.5f);
            e.Speed *= 0.8f;                                  // 更肉更慢, 给玩家拉扯空间
            e.AttackCooldownMs = Mathf.RoundToInt(e.AttackCooldownMs * 0.8f); // 出手更快
        }

        e.Hp = e.MaxHp;
    }

    private static Vector3 SpawnPosFor(Vector3 center, int index)
    {
        float angle = index * (Mathf.PI * 2f / MaxWaveSize);
        return center + new Vector3(
            Mathf.Cos(angle) * SpawnRadius,
            0f,
            Mathf.Sin(angle) * SpawnRadius);
    }

    private static void MoveToward(ServerEnemy e, Vector3 targetPos, float stopDist, float dt)
    {
        Vector3 to = targetPos - e.Pos;
        to.y = 0f;
        float len = to.magnitude;
        if (len <= 0.01f) return;

        float gap = len - stopDist;
        if (gap <= 0f) return;

        Vector3 dir = to / len;
        float step = Mathf.Min(e.Speed * dt, gap);
        e.Pos += dir * step;
        e.Pos.y = targetPos.y;
        e.Yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
    }

    private static void MoveAway(ServerEnemy e, Vector3 targetPos, float dt)
    {
        Vector3 from = e.Pos - targetPos;
        from.y = 0f;
        float len = from.magnitude;
        if (len <= 0.01f) return;

        Vector3 dir = from / len;
        e.Pos += dir * (e.Speed * dt);
        e.Pos.y = targetPos.y;
        e.Yaw = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;
    }

    private static void Strafe(ServerEnemy e, Vector3 targetPos, float dt)
    {
        Vector3 to = targetPos - e.Pos;
        to.y = 0f;
        if (to.sqrMagnitude < 0.01f) return;

        Vector3 right = Vector3.Cross(Vector3.up, to.normalized);
        float sign = (e.Id % 2 == 0) ? 1f : -1f;
        e.Pos += right * (sign * 1.2f * dt);
        e.Pos.y = targetPos.y;
    }

    private static void TryRespawnWave(string roomId, long now)
    {
        if (!_nextWaveAt.TryGetValue(roomId, out long at)) return;
        if (now < at) return;
        _nextWaveAt.Remove(roomId);

        int wave = CurrentWave(roomId) + 1;
        _waveNo[roomId] = wave;

        Vector3 center = _waveCenter.TryGetValue(roomId, out var c) ? c : Vector3.zero;
        SpawnWave(roomId, center, wave);
    }

    private static void BroadcastWave(string roomId, int alive, bool cleared, int nextInMs, int wave, int total)
    {
        if (BattleServerNetwork.Instance == null) return;
        var dto = new WaveStateDto
        {
            roomId = roomId,
            alive = alive,
            cleared = cleared,
            nextWaveInMs = nextInMs,
            wave = wave,
            total = total
        };
        BattleServerNetwork.Instance.BroadcastToRoom(roomId, BattleMsgId.WaveState, JsonUtility.ToJson(dto));
    }

    private static void SetState(ServerEnemy e, int state)
    {
        if (e.State == state) return;
        e.State = state;

        string name;
        if (state == 0) name = "待机";
        else if (state == 1) name = "移动";
        else if (state == 2) name = "攻击";
        else name = "死亡";

        Debug.Log($"[ServerEnemy] #{e.Id}(kind={e.Kind}) 进入 {name}");
    }

    private static void FaceTo(ServerEnemy e, Vector3 targetPos)
    {
        float dx = targetPos.x - e.Pos.x;
        float dz = targetPos.z - e.Pos.z;
        if (dx * dx + dz * dz > 0.0001f)
            e.Yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
    }
}
