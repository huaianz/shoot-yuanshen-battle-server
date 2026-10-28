using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 一个客户端连接
/// </summary>
public class BattleClientConn
{
    public int ConnId;                 // 服务器分配, 每条连接唯一
    public int PlayerId;
    public string Username;            // 网关校验过的用户
    public string RoomId;              // 所在房间
    public TcpClient Tcp;
    public NetworkStream Stream;
    public InputCommandDto LatestInput;   // 缓存的玩家最近一次输入，用于tick同步
    public long LastRecvMs; //最后一次收到的包的时间戳，用于超时检测或者心跳检测
}

/// <summary>
/// TCP战斗服务器网络层
/// </summary>
public class BattleServerNetwork : MonoBehaviour
{
    public static BattleServerNetwork Instance;

    public int port = 7777;
    [Header("心跳超时(秒): 超过这个时间没收到任何消息就断开")]
    public float heartbeatTimeoutSec = 5f;
    private TcpListener _listener;
    private bool _running;
    private int _nextPlayerId = 1;
    private float _sweepTimer;

    //存放所有在线的客户端
    private readonly List<BattleClientConn> _clients = new List<BattleClientConn>();
    //主线程队列
    private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
    //只读
    public IReadOnlyList<BattleClientConn> Clients => _clients;

    private void Awake() { Instance = this; }

    public void StartServer(int listenPort)
    {
        port = listenPort;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;
        _ = AcceptLoop();
        Debug.Log($"[BattleServer] 已启动, 监听 {port}");
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            TcpClient tcp = await _listener.AcceptTcpClientAsync();
            var conn = new BattleClientConn
            {
                PlayerId = 0,
                Tcp = tcp,
                Stream = tcp.GetStream(),
                LastRecvMs = NowMs()
            };
            lock (_clients) _clients.Add(conn);
            Debug.Log("[BattleServer] 新连接");
            _ = ReadLoop(conn);
        }
    }

    private async Task ReadLoop(BattleClientConn conn)
    {
        try
        {
            while (_running)
            {
                byte[] header = await ReadExactly(conn.Stream, 8);
                if (header == null) break;
                int len = BitConverter.ToInt32(header, 0);
                int msgId = BitConverter.ToInt32(header, 4);
                if (len <= 0 || len > 65536) break;
                byte[] body = await ReadExactly(conn.Stream, len);
                if (body == null) break;

                string json = Encoding.UTF8.GetString(body);
                conn.LastRecvMs = NowMs();//更新时间戳
                // 收包在异步线程, 只入队; 逻辑在主线程执行
                _mainThread.Enqueue(() => BattleServerRouter.Handle(conn, msgId, json));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BattleServer] 读循环异常: " + e.Message);
        }
        finally
        {
            RemoveClient(conn);
        }
    }

    private void Update()
    {
        // 主线程执行
        while (_mainThread.TryDequeue(out Action act)) act?.Invoke();

        // 每 1 秒扫一次心跳超时的连接
        _sweepTimer += Time.deltaTime;
        if (_sweepTimer < 1f) return;
        _sweepTimer = 0f;

        long now = NowMs();
        List<BattleClientConn> stale = null;
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                if ((now - c.LastRecvMs) / 1000.0 > heartbeatTimeoutSec)
                    (stale ??= new List<BattleClientConn>()).Add(c);
            }
        }

        if (stale == null) return;
        foreach (var c in stale)
        {
            Debug.LogWarning($"[BattleServer] connId={c.ConnId} 玩家 {c.Username} 心跳超时({heartbeatTimeoutSec}s), 断开");
            RemoveClient(c);
        }
    }

    #region 发送消息给所有客户端(服务器广播)
    public void Send(BattleClientConn conn, int msgId, string json)
    {
        try
        {
            byte[] data = BattlePack.Pack(msgId, json);
            conn.Stream.Write(data, 0, data.Length);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BattleServer] 发送失败: " + e.Message);
        }
    }

    public void BroadcastToRoom(string roomId, int msgId, string json)
    {
        lock (_clients)
        {
            foreach (var c in _clients)
                if (c.PlayerId > 0 && c.RoomId == roomId) Send(c, msgId, json);
        }
    }
    #endregion 

    #region 工具方法
    public void SendToPlayer(int playerId, int msgId, string json)
    {
        lock (_clients)
        {
            foreach (var c in _clients)
                if (c.PlayerId == playerId) { Send(c, msgId, json); return; }
        }
    }

    private void RemoveClient(BattleClientConn conn)
    {
        bool removed;
        lock (_clients) removed = _clients.Remove(conn);
        if (!removed) return;                      // 已处理过, 避免重复清理

        try { conn.Tcp?.Close(); } catch { }

        if (conn.PlayerId > 0)
        {
            // 1) 通知同房间玩家: 此人离开
            BroadcastToRoom(conn.RoomId, BattleMsgId.PlayerJoinLeave,
                "{\"playerId\":" + conn.PlayerId + ",\"joined\":false}");

            // 2) 从世界状态移除(否则快照里会一直是幽灵玩家)
            ServerWorld.RemoveByConn(conn.ConnId);

            Debug.Log($"[BattleServer] 玩家 {conn.Username}(id={conn.PlayerId}) 离开");
        }
    }

    //获取所需要的字节数
    private static async Task<byte[]> ReadExactly(NetworkStream s, int count)
    {
        byte[] buf = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = await s.ReadAsync(buf, total, count - total);
            if (n <= 0) return null;
            total += n;
        }
        return buf;
    }

    //UTC时间戳
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    #endregion
}