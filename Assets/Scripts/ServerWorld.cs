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
    public int Hp = 100;
    public int AnimId;            // 最近上报的动作编号
    public long LastInputMs;      // 最近输入时间戳(校验用)
    public InputCommandDto Input; // 最近一次输入
    public readonly List<PosSample> History = new List<PosSample>();  // 位置历史(回溯用)
    public long LastFireMs;      // 上次开火时间(射速限制)
    public long RespawnAtMs;     // 复活时间(0=活着)
    public int MatchCoins;       // 本局累计金币
    public long DamageBoostUntilMs;   // 火力强化到期时间(0=没有)
}

public static class ServerWorld
{
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
    private static int _nextPlayerId = 0;

    public static int PlayerCount => _playersById.Count;
    public static int ShotsReceived { get; private set; }
    public static int HitsLanded { get; private set; }
    private static long _lastStatLogMs;

    /// <summary>
    /// 加入或重连: 同一用户名只保留一份状态
    /// </summary>
    public static int AddOrReplace(int connId, string username, string roomId, Vector3 pos, float yaw)
    {
        if (string.IsNullOrEmpty(username)) username = "player_" + connId;

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
            Debug.Log($"[ServerWorld] 玩家 {username}(id={playerId}) 重连到房间 {roomId}");
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
        Debug.Log($"[ServerWorld] 玩家 {username}(id={playerId}) 加入房间 {roomId}, 当前 {PlayerCount} 人");

        // 房间第一次进人: 刷一波怪
        ServerEnemyRegistry.EnsureRoom(roomId, pos);

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
                ServerEnemyRegistry.RemoveRoom(roomId);
        }
    }

    public static void SetInput(int playerId, InputCommandDto input)
    {
        if (!_playersById.TryGetValue(playerId, out var p)) return;
        p.Input = input;
        p.AnimId = input.animId;

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
                p.Yaw = input.yaw;
                AppendHistory(p);
            }
            p.LastInputMs = input.clientTimeMs;
        }
    }

    /// <summary>
    /// 每个 tick 调用
    /// </summary>
    public static void Step(float dt)
    {
        long now = NowMs();

        //玩家复活检查
        foreach (var p in _playersById.Values)
        {
            if (p.Hp <= 0 && p.RespawnAtMs > 0 && now >= p.RespawnAtMs)
            {
                p.Hp = 100;
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
    public static int RewindPaddingMs = 100;   // 客户端插值延迟补偿（其他客户端本地会把网络位置延迟约 100ms 渲染，通过补偿回溯这部分来防止打到却没有伤害的问题）
    public static int WeaponDamage = 25;       // P3 固定伤害(P4 再按武器表算)
    public const float HitRadius = 0.45f;      // 命中圆柱半径
    public const float HitHeight = 1.8f;       // 命中圆柱高度
    public const float MaxFireDistance = 300f;//子弹飞行最远距离

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
        if (now - shooter.LastFireMs < 50) return;
        ShotsReceived++;
        shooter.LastFireMs = now;



        long rewindMs = RewindEnabled ? (fire.pingMs / 2 + RewindPaddingMs) : 0;

        // 火力强化: 服务器判定, 客户端不参与
        int damage = Mathf.RoundToInt(
            WeaponDamage * (now < shooter.DamageBoostUntilMs ? 1.5f : 1f));

        long targetTime = now - rewindMs;

        Vector3 origin = new Vector3(fire.ox, fire.oy, fire.oz);
        Vector3 dir = new Vector3(fire.dx, fire.dy, fire.dz).normalized;

        int bestPlayerId = 0;
        int bestEnemyId = 0;
        float bestDist = float.MaxValue;
        Vector3 bestHit = Vector3.zero;

        // 玩家目标(回溯)
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

        // 敌人目标(用当前位置, 精英的判定体放大)
        foreach (var e in ServerEnemyRegistry.ListInRoom(shooter.RoomId))
        {
            if (e.Hp <= 0) continue;

            float radius = e.IsElite ? EnemyHitRadius * 1.4f : EnemyHitRadius;
            float height = e.IsElite ? EnemyHitHeight * 1.25f : EnemyHitHeight;

            if (RayVsCapsule(origin, dir, MaxFireDistance, e.Pos, radius, height, out float d)
                && d < bestDist)
            {
                bestDist = d;
                bestPlayerId = 0;
                bestEnemyId = e.Id;
                bestHit = origin + dir * d;
            }
        }

        if (bestPlayerId == 0 && bestEnemyId == 0)
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
        target.Hp -= damage;
        bool isKill = target.Hp <= 0;
        if (isKill)
        {
            target.Hp = 0;
            target.RespawnAtMs = now + 3000;
        }

        Debug.Log($"[Fire] {shooter.Username} → {target.Username} 命中 {WeaponDamage} 伤害, HP={target.Hp}, 回溯={rewindMs}ms");

        var res = new DamageResultDto
        {
            attackerKind = 0,
            shooterId = shooterId,
            targetId = bestPlayerId,
            damage = damage,
            targetHp = target.Hp,
            isKill = isKill,
            hitX = bestHit.x,
            hitY = bestHit.y,
            hitZ = bestHit.z
        };
        BattleServerNetwork.Instance.BroadcastToRoom(shooter.RoomId, BattleMsgId.Damage, JsonUtility.ToJson(res));
    }

    /// <summary>
    /// 敌人打玩家,damage<0 时用敌人自身攻击力
    /// </summary>
    public static void DamagePlayerByEnemy(ServerEnemy enemy, ServerPlayerState target, int damage = -1)
    {
        if (enemy == null || target == null || target.Hp <= 0) return;
        if (damage < 0) damage = enemy.AttackDamage;

        target.Hp -= damage;
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
                if (buyer.Hp >= 100)
                {
                    ReplyBuy(buyer, req.itemId, false, "血量已满", price);
                    return;
                }
                buyer.Hp = Mathf.Min(100, buyer.Hp + 35);
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
                    p.Hp = Mathf.Min(100, p.Hp + 25);
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
