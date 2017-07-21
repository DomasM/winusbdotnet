/*
Copyright (c) 2014 Stephen Stair (sgstair@akkit.org)

Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
*/

using System;
using System.Linq;

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
