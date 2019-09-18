﻿using DotNetty.Codecs.MqttFx;
using DotNetty.Codecs.MqttFx.Packets;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MqttFx.Extensions;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MqttFx
{
    /// <summary>
    /// Mqtt客户端
    /// </summary>
    public class MqttClient : IMqttClient
    {
        private readonly ILogger _logger;
        private readonly IEventLoopGroup _group;
        private readonly MqttClientOptions _options;
        private readonly PacketIdProvider _packetIdProvider = new PacketIdProvider();
        private readonly PacketDispatcher _packetDispatcher = new PacketDispatcher();

        private IChannel _clientChannel;
        private CancellationTokenSource _cancellationTokenSource;

        public Action<ConnectReturnCode> OnConnected;
        public Action OnDisconnected;
        public Action<Message> OnMessageReceived;

        public MqttClient(
            ILogger<MqttClient> logger,
            IOptions<MqttClientOptions> options)
        {
            _options = options.Value;
            _logger = logger ?? NullLogger<MqttClient>.Instance;
            _group = new MultithreadEventLoopGroup();
        }

        /// <summary>
        /// 连接
        /// </summary>
        /// <returns></returns>
        public async Task<ConnectReturnCode> ConnectAsync()
        {
            var clientReadListener = new ReadListeningHandler();
            var bootstrap = new Bootstrap();
            bootstrap
                .Group(_group)
                .Channel<TcpSocketChannel>()
                .Option(ChannelOption.TcpNodelay, true)
                .Handler(new ActionChannelInitializer<ISocketChannel>(channel =>
                {
                    var pipeline = channel.Pipeline;
                    pipeline.AddLast(MqttEncoder.Instance, new MqttDecoder(false, 256 * 1024), clientReadListener);
                }));

            try
            {
                _packetDispatcher.Reset();
                _packetIdProvider.Reset();
                _cancellationTokenSource = new CancellationTokenSource();
                _clientChannel = await bootstrap.ConnectAsync(new IPEndPoint(IPAddress.Parse(_options.Host), _options.Port));

                StartReceivingPackets(clientReadListener, _cancellationTokenSource.Token);

                var connectResponse = await AuthenticateAsync(clientReadListener, _cancellationTokenSource.Token); ;
                if (connectResponse.ConnectReturnCode == ConnectReturnCode.ConnectionAccepted)
                {
                    OnConnected?.Invoke(connectResponse.ConnectReturnCode);
                }
                return connectResponse.ConnectReturnCode;
            }
            catch (Exception ex)
            {
                await DisconnectAsync();
                _logger.LogError(ex.Message, ex);
                throw new MqttException("BrokerUnavailable");
            }
        }

        private Task<ConnAckPacket> AuthenticateAsync(ReadListeningHandler readListener, CancellationToken cancellationToken)
        {
            var packet = new ConnectPacket
            {
                ClientId = _options.ClientId,
                CleanSession = _options.CleanSession,
                KeepAlive = _options.KeepAlive,
            };
            if (_options.Credentials != null)
            {
                packet.UsernameFlag = true;
                packet.UserName = _options.Credentials.Username;
                packet.Password = _options.Credentials.Username;
            }
            if (_options.WillMessage != null)
            {
                packet.WillFlag = true;
                packet.WillQos = _options.WillMessage.Qos;
                packet.WillRetain = _options.WillMessage.Retain;
                packet.WillTopic = _options.WillMessage.Topic;
                packet.WillMessage = _options.WillMessage.Payload;
            }
            return SendAndReceiveAsync<ConnAckPacket>(packet, cancellationToken);
        }

        private void StartReceivingPackets(ReadListeningHandler clientReadListener, CancellationToken cancellationToken)
        {
            Task.Run(() => ReceivePacketsAsync(clientReadListener, cancellationToken));
        }

        private async Task ReceivePacketsAsync(ReadListeningHandler clientReadListener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (await clientReadListener.ReceiveAsync() is Packet packet)
                {
                    await ProcessReceivedPacketAsync(packet);
                }
            }
        }

        private Task ProcessReceivedPacketAsync(Packet packet)
        {
            _logger.LogInformation("ProcessReceivedPacketAsync:" + packet.PacketType);

            if (packet is PingReqPacket)
                return _clientChannel.WriteAndFlushAsync(new PingRespPacket());

            if (packet is DisconnectPacket)
                return DisconnectAsync();

            if (packet is PubAckPacket)
                return Task.CompletedTask;

            if (packet is PublishPacket publishPacket)
                return ProcessReceivedPublishPacketAsync(publishPacket);

            if (packet is PubRecPacket pubRecPacket)
                return _clientChannel.WriteAndFlushAsync(new PubRelPacket(pubRecPacket.PacketId));

            if (packet is PubRelPacket pubRelPacket)
                return _clientChannel.WriteAndFlushAsync(new PubCompPacket(pubRelPacket.PacketId));

            return _packetDispatcher.Dispatch(packet);
        }

        private Task ProcessReceivedPublishPacketAsync(PublishPacket publishPacket)
        {
            OnMessageReceived?.Invoke(new Message
            {
                Topic = publishPacket.TopicName,
                Payload = publishPacket.Payload,
                Qos = publishPacket.Qos,
                Retain = publishPacket.Retain
            });

            switch (publishPacket.Qos)
            {
                case MqttQos.AtMostOnce:
                    return Task.CompletedTask;
                case MqttQos.AtLeastOnce:
                    return _clientChannel.WriteAndFlushAsync(new PubAckPacket(publishPacket.PacketId));
                case MqttQos.ExactlyOnce:
                    return _clientChannel.WriteAndFlushAsync(new PubRecPacket(publishPacket.PacketId));
                default:
                    throw new MqttException("Received a not supported QoS level.");
            }
        }

        private async Task<TResponsePacket> SendAndReceiveAsync<TResponsePacket>(Packet requestPacket, CancellationToken cancellationToken) where TResponsePacket : Packet
        {
            cancellationToken.ThrowIfCancellationRequested();

            ushort identifier = 0;
            if (requestPacket is PacketWithId packetWithId)
            {
                identifier = packetWithId.PacketId;
            }

            var awaiter = _packetDispatcher.AddPacketAwaiter<TResponsePacket>(identifier);
            try
            {
                await _clientChannel.WriteAndFlushAsync(requestPacket);
                //var respone = await Extensions.TaskExtensions.TimeoutAfterAsync(ct => packetAwaiter.Task, _options.Timeout, cancellationToken);
                //return (TResponsePacket)respone;

                using (var timeoutCts = new CancellationTokenSource(_options.Timeout))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                {
                    linkedCts.Token.Register(() =>
                    {
                        if (!awaiter.Task.IsCompleted && !awaiter.Task.IsFaulted && !awaiter.Task.IsCanceled)
                            awaiter.TrySetCanceled();
                    });

                    try
                    {
                        var result = await awaiter.Task.ConfigureAwait(false);
                        timeoutCts.Cancel(false);
                        return (TResponsePacket)result;
                    }
                    catch (OperationCanceledException exception)
                    {
                        if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            throw new MqttTimeoutException(exception);
                        else
                            throw;
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _packetDispatcher.RemovePacketAwaiter<TResponsePacket>(identifier);
            }
        }

        /// <summary>
        /// 发布消息
        /// </summary>
        /// <param name="topic">主题</param>
        /// <param name="payload">有效载荷</param>
        /// <param name="qos">服务质量等级</param>
        public Task PublishAsync(string topic, byte[] payload, MqttQos qos = MqttQos.AtMostOnce)
        {
            var packet = new PublishPacket(qos)
            {
                TopicName = topic,
                Payload = payload
            };
            if (qos > MqttQos.AtMostOnce)
                packet.PacketId = _packetIdProvider.GetNewPacketId();

            return _clientChannel.WriteAndFlushAsync(packet);
        }

        /// <summary>
        /// 订阅主题
        /// </summary>
        /// <param name="topic">主题</param>
        /// <param name="qos">服务质量等级</param>
        public Task<SubAckPacket> SubscribeAsync(string topic, MqttQos qos = MqttQos.AtMostOnce)
        {
            var packet = new SubscribePacket
            {
                PacketId = _packetIdProvider.GetNewPacketId(),
            };
            packet.Add(topic, qos);

            return SendAndReceiveAsync<SubAckPacket>(packet, _cancellationTokenSource.Token);
        }

        ///// <summary>
        ///// 取消订阅
        ///// </summary>
        ///// <param name="topics">主题</param>
        //public Task<UnsubscribeAckMessage> UnsubscribeAsync(params string[] topics)
        //{
        //    var packet = new UnsubscribePacket();
        //    packet.AddRange(topics);

        //    return SendAndReceiveAsync<UnsubscribeAckMessage>(packet, _cancellationTokenSource.Token);
        //}

        /// <summary>
        /// 断开连接
        /// </summary>
        /// <returns></returns>
        public async Task DisconnectAsync()
        {
            if (_clientChannel != null)
                await _clientChannel.CloseAsync();
            await _group.ShutdownGracefullyAsync(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
            OnDisconnected?.Invoke();
        }
    }
}
