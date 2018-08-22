﻿// wraps UNET's LLAPI for use as HLAPI TransportLayer
using System;
using UnityEngine.Networking.Types;

namespace UnityEngine.Networking
{
    public class LLAPITransport : TransportLayer
    {
        ConnectionConfig connectionConfig;
        int channelId = 0; // always use first channel
        byte error;

        int clientId = -1;
        int clientConnectionId = -1;
        byte[] clientReceiveBuffer = new byte[4096];

        int serverHostId = -1;
        byte[] serverReceiveBuffer = new byte[4096];

        public LLAPITransport(GlobalConfig globalConfig = null, ConnectionConfig connectionConfig = null)
        {
            // create global config if none passed
            // -> settings copied from uMMORPG configuration for best results
            if (globalConfig == null)
            {
                globalConfig = new GlobalConfig();
                globalConfig.ReactorModel = ReactorModel.SelectReactor;
                globalConfig.ThreadAwakeTimeout = 1;
                globalConfig.ReactorMaximumSentMessages = 4096;
                globalConfig.ReactorMaximumReceivedMessages = 4096;
                globalConfig.MaxPacketSize = 2000;
                globalConfig.MaxHosts = 16;
                globalConfig.ThreadPoolSize = 3;
                globalConfig.MinTimerTimeout = 1;
                globalConfig.MaxTimerTimeout = 12000;
            }
            NetworkTransport.Init(globalConfig);

            // create connection config if none passed
            // -> settings copied from uMMORPG configuration for best results
            if (connectionConfig == null)
            {
                connectionConfig = new ConnectionConfig();
                connectionConfig.PacketSize = 1500;
                connectionConfig.FragmentSize = 500;
                connectionConfig.ResendTimeout = 1200;
                connectionConfig.DisconnectTimeout = 6000;
                connectionConfig.ConnectTimeout = 6000;
                connectionConfig.MinUpdateTimeout = 1;
                connectionConfig.PingTimeout = 2000;
                connectionConfig.ReducedPingTimeout = 100;
                connectionConfig.AllCostTimeout = 20;
                connectionConfig.NetworkDropThreshold = 80;
                connectionConfig.OverflowDropThreshold = 80;
                connectionConfig.MaxConnectionAttempt = 10;
                connectionConfig.AckDelay = 33;
                connectionConfig.SendDelay = 10;
                connectionConfig.MaxCombinedReliableMessageSize = 100;
                connectionConfig.MaxCombinedReliableMessageCount = 10;
                connectionConfig.MaxSentMessageQueueSize = 512;
                connectionConfig.AcksType = ConnectionAcksType.Acks128;
                connectionConfig.InitialBandwidth = 0;
                connectionConfig.BandwidthPeakFactor = 2;
                connectionConfig.WebSocketReceiveBufferMaxSize = 0;
                connectionConfig.UdpSocketReceiveBufferMaxSize = 0;
                channelId = connectionConfig.AddChannel(QosType.UnreliableFragmented);
            }
            this.connectionConfig = connectionConfig;

            Debug.Log("LLAPITransport initialized!");
        }

        // client //////////////////////////////////////////////////////////////
        public bool ClientConnected()
        {
            return clientConnectionId != -1;
        }

        public void ClientConnect(string address, int port)
        {
            HostTopology hostTopology = new HostTopology(connectionConfig, 1);

            // important:
            //   AddHost(topology) doesn't work in WebGL.
            //   AddHost(topology, port) works in standalone and webgl if port=0
            clientId = NetworkTransport.AddHost(hostTopology, 0);

            clientConnectionId = NetworkTransport.Connect(clientId, address, port, 0, out error);
            NetworkError networkError = (NetworkError) error;
            if (networkError != NetworkError.Ok)
            {
                Debug.LogWarning("NetworkTransport.Connect failed: clientId=" + clientId + " address= " + address + " port=" + port + " error=" + error);
                clientConnectionId = -1;
            }
        }

        public bool ClientSend(byte[] data)
        {
            return NetworkTransport.Send(clientId, clientConnectionId, channelId, data, data.Length, out error);
        }

        public bool ClientGetNextMessage(out TransportEvent transportEvent, out byte[] data)
        {
            transportEvent = TransportEvent.Disconnected;
            data = null;
            int connectionId;
            int channel;
            int receivedSize;
            NetworkEventType networkEvent = NetworkTransport.ReceiveFromHost(clientId, out connectionId, out channel, clientReceiveBuffer, clientReceiveBuffer.Length, out receivedSize, out error);

            // note: 'error' is used for extra information, e.g. the reason for
            // a disconnect. we don't necessarily have to throw an error if
            // error != 0. but let's log it for easier debugging.
            //
            // DO NOT return after error != 0. otherwise Disconnect won't be
            // registered.
            NetworkError networkError = (NetworkError)error;
            if (networkError != NetworkError.Ok)
            {
                Debug.Log("NetworkTransport.Receive failed: hostid=" + clientId + " connId=" + connectionId + " channelId=" + channel + " error=" + networkError);
            }

            switch (networkEvent)
            {
                case NetworkEventType.Nothing:
                    return false;
                case NetworkEventType.ConnectEvent:
                    transportEvent = TransportEvent.Connected;
                    break;
                case NetworkEventType.DataEvent:
                    transportEvent = TransportEvent.Data;
                    break;
                case NetworkEventType.DisconnectEvent:
                    transportEvent = TransportEvent.Disconnected;
                    break;
            }

            // assign rest of the values and return true
            data = new byte[receivedSize];
            Array.Copy(clientReceiveBuffer, data, receivedSize);
            //Debug.Log("LLAPITransport.ClientGetNextMessage: clientid=" + clientId + " connid=" + connectionId + " event=" + networkEvent + " data=" + BitConverter.ToString(data) + " error=" + error);
            return true;
        }

        public float ClientGetRTT()
        {
            return NetworkTransport.GetCurrentRTT(clientId, clientConnectionId, out error);
        }

        public void ClientDisconnect()
        {
            if (clientId != -1)
            {
                NetworkTransport.RemoveHost(clientId);
                clientId = -1;
            }
        }

        // server //////////////////////////////////////////////////////////////
        public bool ServerActive()
        {
            return serverHostId != -1;
        }

        public void ServerStart(string address, int port, int maxConnections)
        {
            HostTopology topology = new HostTopology(connectionConfig, maxConnections);
            serverHostId = NetworkTransport.AddHost(topology, port);
            //Debug.Log("LLAPITransport.ServerStart port=" + port + " max=" + maxConnections + " hostid=" + serverHostId);
        }

        public void ServerStartWebsockets(string address, int port, int maxConnections)
        {
            HostTopology topology = new HostTopology(connectionConfig, maxConnections);
            serverHostId = NetworkTransport.AddWebsocketHost(topology, port);
            //Debug.Log("LLAPITransport.ServerStartWebsockets port=" + port + " max=" + maxConnections + " hostid=" + serverHostId);
        }

        public bool ServerSend(int connectionId, byte[] data)
        {
            return NetworkTransport.Send(serverHostId, connectionId, channelId, data, data.Length, out error);
        }

        public bool ServerGetNextMessage(out int connectionId, out TransportEvent transportEvent, out byte[] data)
        {
            connectionId = -1;
            transportEvent = TransportEvent.Disconnected;
            data = null;
            int channel;
            int receivedSize;
            NetworkEventType networkEvent = NetworkTransport.ReceiveFromHost(serverHostId, out connectionId, out channel, serverReceiveBuffer, serverReceiveBuffer.Length, out receivedSize, out error);

            // note: 'error' is used for extra information, e.g. the reason for
            // a disconnect. we don't necessarily have to throw an error if
            // error != 0. but let's log it for easier debugging.
            //
            // DO NOT return after error != 0. otherwise Disconnect won't be
            // registered.
            NetworkError networkError = (NetworkError)error;
            if (networkError != NetworkError.Ok)
            {
                Debug.Log("NetworkTransport.Receive failed: hostid=" + serverHostId + " connId=" + connectionId + " channelId=" + channel + " error=" + networkError);
            }

            // LLAPI client sends keep alive messages (75-6C-6C) on channel=110.
            // ignore all messages that aren't for our selected channel.
            if (channel != channelId)
            {
                return false;
            }

            switch (networkEvent)
            {
                case NetworkEventType.Nothing:
                    return false;
                case NetworkEventType.ConnectEvent:
                    transportEvent = TransportEvent.Connected;
                    break;
                case NetworkEventType.DataEvent:
                    transportEvent = TransportEvent.Data;
                    break;
                case NetworkEventType.DisconnectEvent:
                    transportEvent = TransportEvent.Disconnected;
                    break;
            }

            // assign rest of the values and return true
            data = new byte[receivedSize];
            Array.Copy(serverReceiveBuffer, data, receivedSize);
            //Debug.Log("LLAPITransport.ServerGetNextMessage: hostId=" + serverHostId + " event=" + networkEvent + "connid=" + connectionId + " channel=" + channel + " data=" + BitConverter.ToString(data) + " error=" + error);
            return true;
        }

        public bool GetConnectionInfo(int connectionId, out string address)
        {
            int port;
            NetworkID networkId;
            NodeID node;
            NetworkTransport.GetConnectionInfo(serverHostId, connectionId, out address, out port, out networkId, out node, out error);
            return true;
        }

        public void ServerStop()
        {
            NetworkTransport.RemoveHost(serverHostId);
            serverHostId = -1;
            Debug.Log("LLAPITransport.ServerStop");
        }

        // common //////////////////////////////////////////////////////////////
        public void Shutdown()
        {
            NetworkTransport.Shutdown();
            serverHostId = -1;
            clientConnectionId = -1;
            Debug.Log("LLAPITransport.Shutdown");
        }
    }
}