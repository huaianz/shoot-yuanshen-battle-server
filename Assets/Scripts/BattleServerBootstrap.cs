using UnityEngine;

/// <summary>
/// 服务器自动启动
/// </summary>
public static class BattleServerBootstrap
{
    //游戏一启动，在任何场景加载之前，自动调用这个方法
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Boot()
    {
        var go = new GameObject("BattleServer");
        Object.DontDestroyOnLoad(go);
        var net = go.AddComponent<BattleServerNetwork>();
        var loop = go.AddComponent<BattleServerLoop>();
        int port = ParsePort(7777);
        net.StartServer(port);
        Debug.Log($"[BattleServer] tickRate={loop.tickRate}, port={port}");
        // 命令行加 -norewind 关闭延迟补偿(做对比实验用)
        foreach (string a in System.Environment.GetCommandLineArgs())
        {
            if (a == "-norewind")
            {
                ServerWorld.RewindEnabled = false;
                Debug.Log("[BattleServer] 延迟补偿已关闭(-norewind)");
            }
        }
        Debug.Log($"[BattleServer] RewindEnabled={ServerWorld.RewindEnabled}");
    }

    private static int ParsePort(int def)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-port") return int.Parse(args[i + 1]);
        return def;
    }
}