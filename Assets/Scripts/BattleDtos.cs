using System;
using System.Collections.Generic;

///<summary>
/// 加入请求
///</summary>
[Serializable]
public class JoinRequestDto
{
    public string playerId;
    public string token;
    public string roomId;// 要加入的房间
    public float x, y, z;// 客户端当前坐标
}

/// <summary>
///  输入指令
/// </summary>
[Serializable]
public class InputCommandDto
{
    public int tick;
    public float moveX, moveZ, yaw;
    public int buttons;        // 位标志: 1=冲刺 2=瞄准 4=开火 ...
    public long clientTimeMs;   // 客户端时间戳
    public float x, y, z;
    public int animId;          // 动作状态
    public bool teleport;       // 传送落地, 跳过距离校验
}

/// <summary>
/// 单个玩家的状态
/// </summary>
[Serializable]
public class PlayerStateDto
{
    public int id;
    public float x, y, z, yaw;
    public string anim = "Idle";
    public int animId;
    public int hp;
}

/// <summary>
/// 单个敌人的状态
/// </summary>
[Serializable]
public class EnemyStateDto
{
    public int id;
    public int kind;
    public float x, y, z, yaw;
    public int hp, maxHp;
    public int state;
    public int elite;      // 0=普通 1=精英
}

/// <summary>
/// 世界快照
/// </summary>
[Serializable]
public class SnapshotDto
{
    public int tick;    //逻辑帧，保证玩家之间的同步
    public List<PlayerStateDto> players = new List<PlayerStateDto>();
    public List<EnemyStateDto> enemies = new List<EnemyStateDto>();
}

/// <summary>
/// 加入确认
/// </summary>
[Serializable]
public class JoinAcceptedDto
{
    public int playerId;
    public string roomId;
    public int tick;
    public int matchCoins;      // 本局已有金币(重连时恢复显示)
    public List<PlayerStateDto> players = new List<PlayerStateDto>();
}

/// <summary>
/// 开火
/// </summary>
[Serializable]
public class FireEventDto
{
    public int playerId;
    public float ox, oy, oz;      // 开火起点
    public float dx, dy, dz;      // 方向(归一化)
    public long fireTimeMs;       // 客户端开火时间
    public int pingMs;            // 客户端 RTT(用于回溯)
}

/// <summary>
/// 伤害结算
/// </summary>
[Serializable]
public class DamageResultDto
{
    public int attackerKind;   // 0=玩家打玩家 1=敌人打玩家
    public int shooterId;      // 玩家id 或 敌人id
    public int targetId;
    public int damage;
    public int targetHp;
    public bool isKill;
    public float hitX, hitY, hitZ;
}

/// <summary>
/// 玩家打中敌人
/// </summary>
[Serializable]
public class EnemyHitDto
{
    public int shooterId;
    public int enemyId;
    public int damage;
    public int enemyHp;
    public int enemyMaxHp;
    public bool isKill;
    public float hitX, hitY, hitZ;
}

/// <summary>
/// 波次状态
/// </summary>
[Serializable]
public class WaveStateDto
{
    public string roomId;
    public int alive;
    public bool cleared;
    public int nextWaveInMs;
    public int wave;    // 当前第几波
    public int total;   // 本波总数
}

/// <summary>
/// 远程敌人开枪的弹道表现
/// </summary>
[Serializable]
public class EnemyShotDto
{
    public int enemyId;
    public float fx, fy, fz;   // 起点
    public float tx, ty, tz;   // 终点
    public bool hit;
}

/// <summary>
/// 击杀奖励
/// </summary>
[Serializable]
public class RewardDto
{
    public int playerId;
    public int coins;      // 本次奖励
    public int total;      // 本局累计
    public bool elite;
    public string reason;  // 提示文字: 击杀/精英击杀/波次奖励
}

[Serializable]
public class BuyRequestDto
{
    public int itemId;     // 1=急救包 2=火力强化 3=全队治疗
}

[Serializable]
public class BuyResultDto
{
    public bool success;
    public int itemId;
    public string msg;
    public int coinsLeft;
    public int myHp;
    public int price;
}
