using System;
using System.IO;
using System.Collections.Generic;

namespace UnityEngine.Networking
{
    class ChannelBuffer : IDisposable
    {
        NetworkConnection m_Connection;

        ChannelPacket m_CurrentPacket;

        float m_LastFlushTime;

        byte m_ChannelId;
        int m_MaxPacketSize;
        bool m_IsReliable;
        bool m_AllowFragmentation;
        bool m_IsBroken;
        int m_MaxPendingPacketCount;

        public const int MaxBufferedPackets = 512; // this is per connection. each is around 1400 bytes (MTU)

        Queue<ChannelPacket> m_PendingPackets;
        static internal int pendingPacketCount; // this is across all connections. only used for profiler metrics.

        // config
        public float maxDelay = 0.01f;

        // stats
        float m_LastBufferedMessageCountTimer = Time.realtimeSinceStartup;

        public int numMsgsOut { get; private set; }
        public int numBufferedMsgsOut { get; private set; }
        public int numBytesOut { get; private set; }

        public int numMsgsIn { get; private set; }
        public int numBytesIn { get; private set; }

        public int numBufferedPerSecond { get; private set; }
        public int lastBufferedPerSecond { get; private set; }

        // We need to reserve some space for header information, this will be taken off the total channel buffer size
        const int k_PacketHeaderReserveSize = 100;

        public ChannelBuffer(NetworkConnection conn, int bufferSize, byte cid, bool isReliable, bool isSequenced)
        {
            m_Connection = conn;
            m_MaxPacketSize = bufferSize - k_PacketHeaderReserveSize;
            m_CurrentPacket = new ChannelPacket(m_MaxPacketSize, isReliable);

            m_ChannelId = cid;
            m_MaxPendingPacketCount = MaxBufferedPackets;
            m_IsReliable = isReliable;
            m_AllowFragmentation = (isReliable && isSequenced);
            if (isReliable)
            {
                m_PendingPackets = new Queue<ChannelPacket>();
            }
        }

        // Track whether Dispose has been called.
        bool m_Disposed;

        public void Dispose()
        {
            Dispose(true);
            // Take yourself off the Finalization queue
            // to prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!m_Disposed)
            {
                if (disposing)
                {
                    if (m_PendingPackets != null)
                    {
                        pendingPacketCount = 0;
                        m_PendingPackets.Clear();
                    }
                }
            }
            m_Disposed = true;
        }

        public bool SetOption(ChannelOption option, int value)
        {
            switch (option)
            {
                case ChannelOption.MaxPendingBuffers:
                {
                    if (!m_IsReliable)
                    {
                        // not an error
                        //if (LogFilter.logError) { Debug.LogError("Cannot set MaxPendingBuffers on unreliable channel " + m_ChannelId); }
                        return false;
                    }
                    if (value < 0 || value >= MaxBufferedPackets)
                    {
                        if (LogFilter.logError) { Debug.LogError("Invalid MaxPendingBuffers for channel " + m_ChannelId + ". Must be greater than zero and less than " + MaxBufferedPackets); }
                        return false;
                    }
                    m_MaxPendingPacketCount = value;
                    return true;
                }

                case ChannelOption.AllowFragmentation:
                {
                    m_AllowFragmentation = (value != 0);
                    return true;
                }

                case ChannelOption.MaxPacketSize:
                {
                    if (!m_CurrentPacket.IsEmpty() || m_PendingPackets.Count > 0)
                    {
                        if (LogFilter.logError) { Debug.LogError("Cannot set MaxPacketSize after sending data."); }
                        return false;
                    }

                    if (value <= 0)
                    {
                        if (LogFilter.logError) { Debug.LogError("Cannot set MaxPacketSize less than one."); }
                        return false;
                    }

                    if (value > m_MaxPacketSize)
                    {
                        if (LogFilter.logError) { Debug.LogError("Cannot set MaxPacketSize to greater than the existing maximum (" + m_MaxPacketSize + ")."); }
                        return false;
                    }
                    // rebuild the packet with the new size. the packets doesn't store a size variable, just has the size of the internal buffer
                    m_CurrentPacket = new ChannelPacket(value, m_IsReliable);
                    m_MaxPacketSize = value;
                    return true;
                }
            }
            return false;
        }

        public void CheckInternalBuffer()
        {
            if (Time.realtimeSinceStartup - m_LastFlushTime > maxDelay && !m_CurrentPacket.IsEmpty())
            {
                SendInternalBuffer();
                m_LastFlushTime = Time.realtimeSinceStartup;
            }

            if (Time.realtimeSinceStartup - m_LastBufferedMessageCountTimer > 1.0f)
            {
                lastBufferedPerSecond = numBufferedPerSecond;
                numBufferedPerSecond = 0;
                m_LastBufferedMessageCountTimer = Time.realtimeSinceStartup;
            }
        }

        public bool SendWriter(NetworkWriter writer)
        {
            // write relevant data, which is until .Position
            return SendBytes(writer.ToArray(), writer.Position);
        }

        public bool Send(short msgType, MessageBase msg)
        {
            // build the stream
            NetworkWriter writer = new NetworkWriter();
            writer.StartMessage(msgType);
            msg.Serialize(writer);
            writer.FinishMessage();

            numMsgsOut += 1;
            return SendWriter(writer);
        }

        internal MemoryStream fragmentBuffer = new MemoryStream();
        bool readingFragment = false;

        internal bool HandleFragment(NetworkReader reader)
        {
            int state = reader.ReadByte();
            if (state == 0)
            {
                if (readingFragment == false)
                {
                    fragmentBuffer.Position = 0;
                    readingFragment = true;
                }

                byte[] data = reader.ReadBytesAndSize();
                fragmentBuffer.Write(data, 0, data.Length);
                return false;
            }
            else
            {
                readingFragment = false;
                return true;
            }
        }

        internal bool SendFragmentBytes(byte[] bytes, int bytesToSend)
        {
            const int fragmentHeaderSize = 32;
            int pos = 0;
            while (bytesToSend > 0)
            {
                int diff = Math.Min(bytesToSend, m_MaxPacketSize - fragmentHeaderSize);

                // send fragment
                NetworkWriter fragmentWriter = new NetworkWriter();
                fragmentWriter.StartMessage(MsgType.Fragment);
                fragmentWriter.Write((byte)0);
                fragmentWriter.WriteBytesAndSize(bytes, pos, diff);
                fragmentWriter.FinishMessage();
                SendWriter(fragmentWriter);

                pos += diff;
                bytesToSend -= diff;
            }

            // send finish
            NetworkWriter finishWriter = new NetworkWriter();
            finishWriter.StartMessage(MsgType.Fragment);
            finishWriter.Write((byte)1);
            finishWriter.FinishMessage();
            SendWriter(finishWriter);

            return true;
        }

        internal bool SendBytes(byte[] bytes, int bytesToSend)
        {
/*#if UNITY_EDITOR
            UnityEditor.NetworkDetailStats.IncrementStat(
                UnityEditor.NetworkDetailStats.NetworkDirection.Outgoing,
                MsgType.HLAPIMsg, "msg", 1);
#endif*/
            if (bytesToSend > UInt16.MaxValue)
            {
                if (LogFilter.logError) { Debug.LogError("ChannelBuffer:SendBytes cannot send packet larger than " + UInt16.MaxValue + " bytes"); }
                return false;
            }

            if (bytesToSend <= 0)
            {
                // zero length packets getting into the packet queues are bad.
                if (LogFilter.logError) { Debug.LogError("ChannelBuffer:SendBytes cannot send zero bytes"); }
                return false;
            }

            if (bytesToSend > m_MaxPacketSize)
            {
                if (m_AllowFragmentation)
                {
                    return SendFragmentBytes(bytes, bytesToSend);
                }
                else
                {
                    // cannot do HLAPI fragmentation on this channel
                    if (LogFilter.logError) { Debug.LogError("Failed to send big message of " + bytesToSend + " bytes. The maximum is " + m_MaxPacketSize + " bytes on channel:" + m_ChannelId); }
                    return false;
                }
            }

            if (!m_CurrentPacket.HasSpace(bytesToSend))
            {
                if (m_IsReliable)
                {
                    if (m_PendingPackets.Count == 0)
                    {
                        // nothing in the pending queue yet, just flush and write
                        if (!m_CurrentPacket.SendToTransport(m_Connection, m_ChannelId))
                        {
                            QueuePacket();
                        }
                        m_CurrentPacket.Write(bytes, bytesToSend);
                        return true;
                    }

                    if (m_PendingPackets.Count >= m_MaxPendingPacketCount)
                    {
                        if (!m_IsBroken)
                        {
                            // only log this once, or it will spam the log constantly
                            if (LogFilter.logError) { Debug.LogError("ChannelBuffer buffer limit of " + m_PendingPackets.Count + " packets reached."); }
                        }
                        m_IsBroken = true;
                        return false;
                    }

                    // calling SendToTransport here would write out-of-order data to the stream. just queue
                    QueuePacket();
                    m_CurrentPacket.Write(bytes, bytesToSend);
                    return true;
                }

                if (!m_CurrentPacket.SendToTransport(m_Connection, m_ChannelId))
                {
                    if (LogFilter.logError) { Debug.Log("ChannelBuffer SendBytes no space on unreliable channel " + m_ChannelId); }
                    return false;
                }

                m_CurrentPacket.Write(bytes, bytesToSend);
                return true;
            }

            m_CurrentPacket.Write(bytes, bytesToSend);
            if (maxDelay == 0.0f)
            {
                return SendInternalBuffer();
            }
            return true;
        }

        void QueuePacket()
        {
            pendingPacketCount += 1;
            m_PendingPackets.Enqueue(m_CurrentPacket);

            // create new m_currentPacket so that the one in the queue isn't touched anymore
            // (calling .Reset would reset it in the queue too)
            m_CurrentPacket = new ChannelPacket(m_MaxPacketSize, m_IsReliable);
        }

        public bool SendInternalBuffer()
        {
/*#if UNITY_EDITOR
            UnityEditor.NetworkDetailStats.IncrementStat(
                UnityEditor.NetworkDetailStats.NetworkDirection.Outgoing,
                MsgType.LLAPIMsg, "msg", 1);
#endif*/
            if (m_IsReliable && m_PendingPackets.Count > 0)
            {
                // send until transport can take no more
                while (m_PendingPackets.Count > 0)
                {
                    var packet = m_PendingPackets.Dequeue();
                    if (!packet.SendToTransport(m_Connection, m_ChannelId))
                    {
                        m_PendingPackets.Enqueue(packet);
                        break;
                    }
                    pendingPacketCount -= 1;

                    if (m_IsBroken && m_PendingPackets.Count < (m_MaxPendingPacketCount / 2))
                    {
                        if (LogFilter.logWarn) { Debug.LogWarning("ChannelBuffer recovered from overflow but data was lost."); }
                        m_IsBroken = false;
                    }
                }
                return true;
            }
            return m_CurrentPacket.SendToTransport(m_Connection, m_ChannelId);
        }
    }
}