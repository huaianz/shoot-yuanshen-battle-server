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
    public string mode;  // coop / pvp
    public int maxPlayers;
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
    public int weaponId;        // 当前武器模板 id(-1 = 没装备)
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
    public int weaponId;
    public int team;        // 0=进攻 1=防守 -1=合作
    public int matchCoins;
    public int characterId;
    public int armor;
    public int matchWeaponId;
    public int matchWeaponDamage;
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
    public string roomId;
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
    public int wave;            // 当前波次, 0=还没开始
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
    public int targetArmor;
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

/// <summary>玩家开火弹道(用于其他客户端显示)</summary>
[Serializable]
public class PlayerShotDto
{
    public int shooterId;
    public int weaponId;
    public float ox, oy, oz;
    public float ex, ey, ez;
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

/// <summary>战术模式回合状态</summary>
[Serializable]
public class RoundStateDto
{
    public int phase;        // 0=准备 1=购买 2=战斗 3=结算
    public int round;
    public int timeLeftMs;
    public int attackScore;
    public int defenseScore;
    public bool matchOver;
}

/// <summary>大厅里的一个玩家</summary>
[Serializable]
public class LobbyPlayerDto
{
    public int id;
    public string name;
    public int team;     // 0=我方 1=敌方
    public int slot;
    public bool ready;
}

/// <summary>准备大厅状态</summary>
[Serializable]
public class LobbyStateDto
{
    public string roomId;
    public int hostId;
    public int capacity;   // 2 / 4 / 6
    public bool started;
    public List<LobbyPlayerDto> players = new List<LobbyPlayerDto>();
}

[Serializable]
public class MoveSlotDto { public int team; public int slot; }

[Serializable]
public class SwapRequestDto { public int targetId; }

[Serializable]
public class SwapIncomingDto
{
    public int requesterId;
    public string requesterName;
    public int requestId;
    public int expiresInMs;
}

[Serializable]
public class SwapResponseDto
{
    public int requestId;
    public bool accept;
}

[Serializable]
public class SwapResultDto
{
    public bool accepted;
    public string reason;
}

[Serializable]
public class SelectCharacterDto { public int characterId; }

[Serializable]
public class CharacterSelectPlayerDto
{
    public int id;
    public string name;
    public int characterId;
    public bool confirmed;
}

[Serializable]
public class CharacterSelectStateDto
{
    public int timeLeftMs;
    public List<CharacterSelectPlayerDto> players = new List<CharacterSelectPlayerDto>();
}

[Serializable]
public class BuyItemDto
{
    public int itemId;
    public string category;
    public int matchDamage;
    public int matchPrice;
    public int matchFireIntervalMs;
}

[Serializable]
public class BuyItemResultDto
{
    public bool success;
    public int itemId;
    public string msg;
    public int coins;
    public int armor;
    public int matchWeaponId;
    public int matchDamage;
    public int hp;
}

[Serializable]
public class MatchResultPlayerDto
{
    public int id;
    public string name;
    public int team;
    public int kills;
    public int deaths;
    public int assists;
}

[Serializable]
public class MatchResultDto
{
    public int attackScore;
    public int defenseScore;
    public int winnerTeam;
    public List<MatchResultPlayerDto> players = new List<MatchResultPlayerDto>();
}

[Serializable]
public class SpikeStateDto
{
    public bool planted;
    public int spikeState;   // 0=地上 1=被携带 2=已下包
    public int carrierId;
    public float x, y, z;
    public int fuseLeftMs;
    public int plantProgressMs;
    public int defuseProgressMs;
}
