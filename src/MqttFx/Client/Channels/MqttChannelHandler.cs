﻿using DotNetty.Codecs.MqttFx.Packets;
using DotNetty.Transport.Channels;
using MqttFx.Client;
using System.Threading.Tasks;

namespace MqttFx.Channels
{
    /// <summary>
    /// 发送和接收数据处理器
    /// </summary>
    public class MqttChannelHandler : SimpleChannelInboundHandler<Packet>
    {
        private readonly MqttClient client;
        private readonly TaskCompletionSource<MqttConnectResult> connectFuture;

        public MqttChannelHandler(MqttClient client, TaskCompletionSource<MqttConnectResult> connectFuture)
        {
            this.client = client;
            this.connectFuture = connectFuture;
        }

        /// <summary>
        /// 通道激活时触发，当客户端connect成功后，服务端就会接收到这个事件，从而可以把客户端的Channel记录下来
        /// </summary>
        /// <param name="context"></param>
        public override void ChannelActive(IChannelHandlerContext context)
        {
            var packet = new ConnectPacket();
            var variableHeader = (ConnectVariableHeader)packet.VariableHeader;
            var payload = (ConnectPayload)packet.Payload;

            variableHeader.ConnectFlags.CleanSession = client.Options.CleanSession;
            variableHeader.KeepAlive = client.Options.KeepAlive;
            payload.ClientId = client.Options.ClientId;
            if (client.Options.Credentials != null)
            {
                variableHeader.ConnectFlags.UsernameFlag = true;
                payload.UserName = client.Options.Credentials.Username;
                payload.Password = client.Options.Credentials.Username;
            }
            if (client.Options.WillMessage != null)
            {
                variableHeader.ConnectFlags.WillFlag = true;
                variableHeader.ConnectFlags.WillQos = client.Options.WillMessage.Qos;
                variableHeader.ConnectFlags.WillRetain = client.Options.WillMessage.Retain;
                payload.WillTopic = client.Options.WillMessage.Topic;
                payload.WillMessage = client.Options.WillMessage.Payload;
            }
            context.WriteAndFlushAsync(packet);
        }

        /// <summary>
        /// 当收到对方发来的数据后，就会触发，参数msg就是发来的信息，可以是基础类型，也可以是序列化的复杂对象
        /// </summary>
        /// <param name="ctx"></param>
        /// <param name="packet"></param>
        protected override void ChannelRead0(IChannelHandlerContext ctx, Packet packet)
        {
            switch (packet)
            {
                case ConnAckPacket connAckPacket:
                    ProcessMessage(ctx.Channel, connAckPacket);
                    break;
                case PublishPacket publishPacket:
                    ProcessMessage(ctx.Channel, publishPacket);
                    break;
                case PubRecPacket pubRecPacket:
                    ProcessMessage(ctx.Channel, pubRecPacket);
                    break;
                case PubRelPacket pubRelPacket:
                    ProcessMessage(ctx.Channel, pubRelPacket);
                    break;
                case PubAckPacket pubAckPacket:
                    ProcessMessage(ctx.Channel, pubAckPacket);
                    break;
                case SubAckPacket subAckPacket:
                    ProcessMessage(ctx.Channel, subAckPacket);
                    break;
                case UnsubAckPacket unsubAckPacket:
                    ProcessMessage(ctx.Channel, unsubAckPacket);
                    break;
                case DisconnectPacket:
                    break;
                default:
                    break;
            }
        }

        async void ProcessMessage(IChannel channel, ConnAckPacket packet)
        {
            var variableHeader = (ConnAckVariableHeader)packet.VariableHeader;

            switch (variableHeader.ConnectReturnCode)
            {
                case ConnectReturnCode.CONNECTION_ACCEPTED:
                    connectFuture.TrySetResult(new MqttConnectResult(ConnectReturnCode.CONNECTION_ACCEPTED));

                    if (client.ConnectedHandler != null)
                        client.ConnectedHandler.OnConnected();

                    await client.OnConnected(new MqttConnectResult(ConnectReturnCode.CONNECTION_ACCEPTED));

                    break;

                case ConnectReturnCode.CONNECTION_REFUSED_BAD_USER_NAME_OR_PASSWORD:
                case ConnectReturnCode.CONNECTION_REFUSED_IDENTIFIER_REJECTED:
                case ConnectReturnCode.CONNECTION_REFUSED_SERVER_UNAVAILABLE:
                case ConnectReturnCode.CONNECTION_REFUSED_UNACCEPTABLE_PROTOCOL_VERSION:
                    connectFuture.TrySetResult(new MqttConnectResult(variableHeader.ConnectReturnCode));
                    await channel.CloseAsync();
                    break;
            }
        }

        void ProcessMessage(IChannel channel, PublishPacket packet)
        {
            switch (packet.Qos)
            {
                case MqttQos.AtMostOnce:
                    InvokeProcessForIncomingPublish(packet);
                    break;

                case MqttQos.AtLeastOnce:
                    InvokeProcessForIncomingPublish(packet);
                    if (packet.PacketId > 0)
                        channel.WriteAndFlushAsync(new PubAckPacket(packet.PacketId));
                    break;

                case MqttQos.ExactlyOnce:
                    break;
            }
        }

        void InvokeProcessForIncomingPublish(PublishPacket packet)
        {
            var handler = client.MessageReceivedHandler;
            if (handler != null)
            {
                handler.OnMesage(packet.ToMessage());
            }
        }

        void ProcessMessage(IChannel channel, PubRecPacket packet)
        {
            channel.WriteAndFlushAsync(new PubRelPacket(packet.PacketId));
        }

        void ProcessMessage(IChannel channel, PubRelPacket packet)
        {
            channel.WriteAndFlushAsync(new PubCompPacket(packet.PacketId));
        }

        void ProcessMessage(IChannel channel, PubAckPacket message)
        {
        }

        void ProcessMessage(IChannel channel, SubAckPacket message)
        {
        }

        void ProcessMessage(IChannel channel, UnsubAckPacket message)
        {
        }
    }
}
