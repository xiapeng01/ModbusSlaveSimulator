using NModbus;
using NModbus.IO;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace 通用模拟器
{
    /// <summary>
    /// 监听式 RTU over TCP 从站网络：在本机指定地址/端口监听 TCP 连接，
    /// 每个接入的连接上运行一条独立的 Modbus RTU 从站协议链路。
    /// NModbus 3.0.83 只提供 CreateSlaveNetwork(TcpListener)（Modbus TCP 协议）
    /// 和 CreateRtuSlaveNetwork(IStreamResource)（RTU 协议）两种从站网络，
    /// 缺少"监听 + RTU 帧"的组合，本类补齐该能力（供 Modbus Poll 等主站工具直接连入测试）。
    /// 所有连接共享同一组从站数据；AddSlave / RemoveSlave 会同步到所有已接入的链路。
    /// </summary>
    sealed class RtuOverTcpListenerSlaveNetwork : IModbusSlaveNetwork, IDisposable
    {
        readonly TcpListener listener;
        readonly ModbusFactory factory = new();
        /// <summary>全部从站（按站号），链路接入时整体挂到该链路上</summary>
        readonly ConcurrentDictionary<byte, IModbusSlave> slaves = new();
        /// <summary>每个已接入连接对应的内部 RTU 从站网络（仅用于同步从站增删）</summary>
        readonly ConcurrentDictionary<IModbusSlaveNetwork, byte> connections = new();
        /// <summary>在途的 TCP 连接（Dispose 时只做 Shutdown 唤醒阻塞读，不释放适配器）</summary>
        readonly ConcurrentDictionary<TcpClient, byte> clients = new();
        /// <summary>Dispose 时取消所有链路的监听</summary>
        readonly CancellationTokenSource disposeCts = new();
        /// <summary>构造时缓存令牌：Dispose 流程中不再访问 CTS（避免释放后再取 Token 抛异常）</summary>
        readonly CancellationToken disposeToken;
        bool disposed;

        /// <param name="ip">监听绑定的本机地址（如 127.0.0.1 仅本机可访问，0.0.0.0 暴露到所有网卡）</param>
        /// <param name="port">监听端口。端口被占用时抛出 SocketException，由调用方按创建失败处理</param>
        public RtuOverTcpListenerSlaveNetwork(string ip, int port)
        {
            listener = new TcpListener(IPAddress.Parse(ip), port);
            listener.Start();
            disposeToken = disposeCts.Token;

            // 与包内 Create*SlaveNetwork 一致：创建即后台运行，无需调用方手动启动监听
            _ = Task.Run(() => ListenAsync(CancellationToken.None));
        }

        /// <summary>
        /// 接入循环：为每个新连接创建一条独立的 RTU 从站链路。
        /// 由 GetOrCreateNetwork 场景外的人通过 Task.Run 启动（与包内 StartListening 一致）。
        /// </summary>
        public async Task ListenAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(linked.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { break; }
                    if (disposed) break;

                    // 每个连接独立链路：单个连接断开只丢弃该链路，不影响监听与其他连接
                    _ = ServeClientAsync(client);
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        /// <summary>无参重载：与 NModbus.Extensions.Tools 的调用习惯保持一致</summary>
        public Task ListenAsync() => ListenAsync(CancellationToken.None);

        async Task ServeClientAsync(TcpClient client)
        {
            IModbusSlaveNetwork? network = null;
            try
            {
                if (disposed) return;
                client.NoDelay = true;
                clients[client] = 0;
                network = factory.CreateRtuSlaveNetwork(new TcpClientAdapter(client));
                foreach (var slave in slaves.Values) network.AddSlave(slave);
                if (disposed) return;   // 停止竞态：不再把已释放网络登记为活动链路
                connections[network] = 0;
                // NModbus 的 ListenAsync 是同步循环，只在每轮开始检查令牌，
                // 靠 Dispose 里的 Shutdown 唤醒阻塞读后自然退出
                await network.ListenAsync(disposeToken);
            }
            catch
            {
                // 连接断开 / 停止时的收尾异常：静默清理（模拟器场景无需区分）
            }
            finally
            {
                if (network is not null) connections.TryRemove(network, out _);
                clients.TryRemove(client, out _);
                try { network?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            }
        }

        public void AddSlave(IModbusSlave slave)
        {
            ThrowIfDisposed();
            if (slaves.TryAdd(slave.UnitId, slave))
            {
                foreach (var connection in connections.Keys)
                {
                    try { connection.AddSlave(slave); } catch { }
                }
            }
        }

        public void RemoveSlave(byte unitId)
        {
            ThrowIfDisposed();
            if (slaves.TryRemove(unitId, out _))
            {
                foreach (var connection in connections.Keys)
                {
                    try { connection.RemoveSlave(unitId); } catch { }
                }
            }
        }

        public IModbusSlave? GetSlave(byte unitId)
            => slaves.TryGetValue(unitId, out var slave) ? slave : null;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            disposeCts.Cancel();
            try { listener.Stop(); } catch { }

            // 只 Shutdown 套接字、不释放内部网络：
            // NModbus.TcpClientAdapter.Dispose() 会把内部 TcpClient 置为 null，而 ModbusSerialSlaveNetwork
            // 的监听循环在 IOException 分支里还会调用 DiscardInBuffer() → _tcpClient.GetStream()
            // → NullReferenceException（从 catch 块内抛出，NModbus 自己接不住）。
            // 这里让阻塞的 Read 立即返回(0 字节)，循环走到令牌检查后干净退出，
            // 各链路在 ServeClientAsync 的 finally 里自行释放网络与连接。
            foreach (var client in clients.Keys)
            {
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
            }

            connections.Clear();
            slaves.Clear();
            // disposeCts 不 Dispose：在途链路仍在读取 disposeToken，提前释放会引发访问已释放对象
        }

        void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    }
}
