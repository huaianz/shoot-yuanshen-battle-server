using System;
using System.Text;

//协议
public static class BattleMsgId
{
    public const int Heartbeat = 1;        // 心跳
    public const int Input = 400;          // C→S 输入上报
    public const int Snapshot = 401;       // S→C 状态快照
    public const int Fire = 402;      // C→S 开火上报
    public const int Damage = 403;    // S→C 伤害结果
    public const int PlayerJoinLeave = 404;// S→C 玩家进出
    public const int Join = 405;           // C→S 加入
    public const int JoinAccepted = 406;   // S→C 加入成功
    public const int EnemyHit = 407;      // S→C 敌人受伤/死亡表现
    public const int WaveState = 408;     // S→C 波次状态(剩余数量/是否通关)
    public const int EnemyShot = 410;     // S→C 远程敌人弹道
    public const int Reward = 411;        // S→C 击杀金币奖励
    public const int Buy = 412;           // C→S 购买请求
    public const int BuyResult = 413;     // S→C 购买结果
}

public static class BattlePack
{
    public static byte[] Pack(int msgId, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(json) ? "{}" : json);
        byte[] result = new byte[8 + body.Length];
        Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, result, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(msgId), 0, result, 4, 4);
        Buffer.BlockCopy(body, 0, result, 8, body.Length);
        return result;
    }
}