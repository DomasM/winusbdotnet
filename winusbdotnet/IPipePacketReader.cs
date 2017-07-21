namespace winusbdotnet {
    public interface IPipePacketReader {
        /// <summary>
        /// Number of received packets that can be read.
        /// </summary>
        int QueuedPackets { get; }

        /// <summary>
        /// Length of the next packet waiting to be dequeued.
        /// </summary>
        int NextPacketLength { get; }

        /// <summary>
        /// Retrieve the next packet from the receive queue into a buffer.
        /// </summary>
        /// <returns>The length that was copied</returns>
        int ReadPacket (byte[] target, int offset);
    }
}
