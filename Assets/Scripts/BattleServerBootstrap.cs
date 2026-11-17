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
        string[] startupArgs = System.Environment.GetCommandLineArgs();

        foreach (string a in startupArgs)
        {
            if (a == "-norewind")
            {
                ServerWorld.RewindEnabled = false;
                Debug.Log("[BattleServer] 延迟补偿已关闭(-norewind)");
            }
            if (a == "-nopvpdeath")
            {
                ServerWorld.PvPDeathEnabled = false;
                Debug.Log("[BattleServer] PvP 不死模式已开启(-nopvpdeath)");
            }
        }

        // -plantsite x,y,z [-plantradius 3]
        for (int i = 0; i < startupArgs.Length - 1; i++)
        {
            if (startupArgs[i] != "-plantsite") continue;

            string[] parts = startupArgs[i + 1].Split(',');
            if (parts.Length != 3
                || !float.TryParse(parts[0], out float x)
                || !float.TryParse(parts[1], out float y)
                || !float.TryParse(parts[2], out float z))
            {
                Debug.LogWarning("[BattleServer] -plantsite 格式错误, 应为 x,y,z");
                break;
            }

            float radius = 3f;
            if (i + 3 < startupArgs.Length
                && startupArgs[i + 2] == "-plantradius"
                && float.TryParse(startupArgs[i + 3], out float r))
            {
                radius = r;
            }

            ServerWorld.SetPlantSite(new Vector3(x, y, z), radius);
            break;
        }
        Debug.Log($"[BattleServer] RewindEnabled={ServerWorld.RewindEnabled}");
        Debug.Log($"[BattleServer] PvPDeathEnabled={ServerWorld.PvPDeathEnabled}");
    }

    private static int ParsePort(int def)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-port") return int.Parse(args[i + 1]);
        return def;
    }
}
