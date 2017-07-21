namespace winusbdotnet {
    public interface IPipeByteReader {
        /// <summary>
        /// Receive a number of bytes from the incoming data stream.
        /// If there are not enough bytes available, only the available bytes will be returned.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] ReceiveBytes (int count);

        /// <summary>
        /// Receive a number of bytes from the incoming data stream, but don't remove them from the queue.
        /// If there are not enough bytes available, only the available bytes will be returned.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] PeekBytes (int count);

        /// <summary>
        /// Receive a specific number of bytes from the incoming data stream.
        /// This call will block until the requested bytes are available, or will eventually throw on timeout.
        /// </summary>
        /// <param name="count">Number of bytes to request</param>
        /// <returns>Byte data from the USB pipe</returns>
        byte[] ReceiveExactBytes (int count);

        /// <summary>
        /// Drop bytes from the incoming data stream without reading them.
        /// If you try to drop more bytes than are available, the buffer will be cleared.
        /// Returns immediately.
        /// </summary>
        /// <param name="count">Number of bytes to drop.</param>
        void SkipBytes (int count);

        /// <summary>
        /// Current number of bytes that are queued and available to read.
        /// </summary>
        int QueuedDataLength { get; }
    }
}
