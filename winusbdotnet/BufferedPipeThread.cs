using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;

namespace winusbdotnet {
    // Background thread to receive data from pipes.
    // Provides two data access mechanisms which are mutually exclusive: Packet level and byte level.
    internal class BufferedPipeThread : IPipeByteReader, IPipePacketReader {
        // Logic to enforce interface exclucivity is in WinUSBDevice
        public bool InterfaceBound; // Has the interface been bound?
        public bool PacketInterface; // Are we using the packet reader interface?

        readonly object Lock0 = new object ();
        Thread PipeThread;
        [Obsolete]
        Thread WorkerThread;
        AutoResetEvent ThreadNewData;
        WinUSBDevice Device;
        byte DevicePipeId;

        private long TotalReceived;

        private int QueuedLength;
        private Queue<byte[]> ReceivedData;
        private int SkipFirstBytes;
        public bool Stopped = false;


        ManualResetEvent ReceiveTick;

        QueuedBuffer[] BufferList;
        Queue<QueuedBuffer> PendingBuffers;
        Queue<QueuedBuffer> ReceivedBuffers;
        Queue<QueuedBuffer> RequeuePending;
        object BufferLock;


        public BufferedPipeThread (WinUSBDevice dev, byte pipeId, int bufferCount, int bufferSize) {


            int maxTransferSize = (int)dev.GetPipePolicy (pipeId, WinUsbPipePolicy.MAXIMUM_TRANSFER_SIZE);

            if (bufferSize > maxTransferSize) { bufferSize = maxTransferSize; }

            BufferLock = new object ();
            PendingBuffers = new Queue<QueuedBuffer> (bufferCount);
            ReceivedBuffers = new Queue<QueuedBuffer> (bufferCount);
            RequeuePending = new Queue<QueuedBuffer> (bufferCount);
            BufferList = new QueuedBuffer[bufferCount];
            for (int i = 0; i < bufferCount; i++) {
                BufferList[i] = new QueuedBuffer (bufferSize);
            }

            Device = dev;
            DevicePipeId = pipeId;
            QueuedLength = 0;
            ReceivedData = new Queue<byte[]> ();
            ReceiveTick = new ManualResetEvent (false);
            PipeThread = new Thread (ThreadFunc);
            PipeThread.IsBackground = true;
            ThreadNewData = new AutoResetEvent (false);


            // Start reading on all the buffers.
            foreach (QueuedBuffer qb in BufferList) {
                dev.BeginReadPipe (pipeId, qb);
                PendingBuffers.Enqueue (qb);
            }


            PipeThread.Start ();
        }

        public long TotalReceivedBytes { get { return TotalReceived; } }

        //
        // Packet Reader members
        //

        public int QueuedPackets { get { lock (Lock0) { return ReceivedBuffers.Count; } } }

        public int NextPacketLength {
            get {
                lock (BufferLock) {
                    if (ReceivedBuffers.Count > 0) return ReceivedBuffers.Peek ().CompletedSize;
                }
                return 0;
            }
        }


        public int ReadPacket (byte[] target, int offset) {
            QueuedBuffer buf = null;
            lock (BufferLock) {
                if (ReceivedBuffers.Count > 0) {
                    buf = ReceivedBuffers.Dequeue ();
                } else {
                    return 0;
                }
            }
            int length = buf.CompletedSize;
            Marshal.Copy (buf.PinnedBuffer, target, offset, buf.CompletedSize);
            lock (RequeuePending) {
                RequeuePending.Enqueue (buf);
            }
            return length;
        }

        void UpdateReceivedData () {
            lock (Lock0) {
                while (NextPacketLength > 0) {
                    byte[] buffer = new byte[NextPacketLength];
                    ReadPacket (buffer, 0);
                    ReceivedData.Enqueue (buffer);
                }
            }
        }


        //
        // Byte Reader members
        //

        public int QueuedDataLength { get { return QueuedLength; } }

        // Only returns as many as it can.
        public byte[] ReceiveBytes (int count) {
            int queue = QueuedDataLength;
            if (queue < count)
                count = queue;

            byte[] output = new byte[count];
            lock (Lock0) {
                UpdateReceivedData ();
                CopyReceiveBytes (output, 0, count);
            }
            return output;
        }

        // Only returns as many as it can.
        public byte[] PeekBytes (int count) {
            int queue = QueuedDataLength;
            if (queue < count)
                count = queue;

            byte[] output = new byte[count];
            lock (Lock0) {
                UpdateReceivedData ();
                CopyPeekBytes (output, 0, count);
            }
            return output;
        }

        public byte[] ReceiveExactBytes (int count) {
            byte[] output = new byte[count];
            if (QueuedDataLength >= count) {
                lock (Lock0) {
                    UpdateReceivedData ();
                    CopyReceiveBytes (output, 0, count);
                }
                return output;
            }
            int failedcount = 0;
            int haveBytes = 0;
            while (haveBytes < count) {
                ReceiveTick.Reset ();
                lock (Lock0) {
                    UpdateReceivedData ();
                    int thisBytes = QueuedLength;

                    if (thisBytes == 0) {
                        failedcount++;
                        if (failedcount > 3) {
                            throw new Exception ("Timed out waiting to receive bytes");
                        }
                    } else {
                        failedcount = 0;
                        if (thisBytes + haveBytes > count) thisBytes = count - haveBytes;
                        CopyReceiveBytes (output, haveBytes, thisBytes);
                    }
                    haveBytes += (int)thisBytes;
                }
                if (haveBytes < count) {
                    if (Stopped) throw new Exception ("Not going to have enough bytes to complete request.");
                    ReceiveTick.WaitOne ();
                }
            }
            return output;
        }

        public void SkipBytes (int count) {
            lock (Lock0) {
                UpdateReceivedData ();
                int queue = QueuedLength;
                if (queue < count)
                    throw new ArgumentException ("count must be less than the data length");

                int copied = 0;
                while (copied < count) {
                    byte[] firstData = ReceivedData.Peek ();
                    int available = firstData.Length - SkipFirstBytes;
                    int toCopy = count - copied;
                    if (toCopy > available) toCopy = available;

                    if (toCopy == available) {
                        ReceivedData.Dequeue ();
                        SkipFirstBytes = 0;
                    } else {
                        SkipFirstBytes += toCopy;
                    }

                    copied += toCopy;
                    QueuedLength -= toCopy;
                }
            }
        }

        //
        // Internal functionality
        //

        // Must be called under lock with enough bytes in the buffer.
        void CopyReceiveBytes (byte[] target, int start, int count) {
            int copied = 0;
            while (copied < count) {
                byte[] firstData = ReceivedData.Peek ();
                int available = firstData.Length - SkipFirstBytes;
                int toCopy = count - copied;
                if (toCopy > available) toCopy = available;

                Array.Copy (firstData, SkipFirstBytes, target, start, toCopy);

                if (toCopy == available) {
                    ReceivedData.Dequeue ();
                    SkipFirstBytes = 0;
                } else {
                    SkipFirstBytes += toCopy;
                }

                copied += toCopy;
                start += toCopy;
                QueuedLength -= toCopy;
            }
        }

        // Must be called under lock with enough bytes in the buffer.
        void CopyPeekBytes (byte[] target, int start, int count) {
            int copied = 0;
            int skipBytes = SkipFirstBytes;

            foreach (byte[] firstData in ReceivedData) {
                int available = firstData.Length - skipBytes;
                int toCopy = count - copied;
                if (toCopy > available) toCopy = available;

                Array.Copy (firstData, skipBytes, target, start, toCopy);

                skipBytes = 0;

                copied += toCopy;
                start += toCopy;

                if (copied >= count) {
                    break;
                }
            }
        }




        void ThreadFunc (object context) {
            int recvBytes;
            while (true) {
                if (Device.Stopping)
                    break;

                try {
                    recvBytes = 0;
                    if (PendingBuffers.Count > 0) {
                        PendingBuffers.Peek ().Wait ();
                    }
                    // Process a large group of received buffers in a batch, if available.
                    int n = 0;
                    bool shortcut = PendingBuffers.Count > 0;
                    try {
                        lock (RequeuePending) {
                            // Requeue buffers that were drained.
                            while (RequeuePending.Count > 0) {
                                QueuedBuffer buf = RequeuePending.Dequeue ();
                                Device.BeginReadPipe (DevicePipeId, buf);
                                // Todo: If this operation fails during normal operation, the buffer is lost from rotation.
                                // Should never happen during normal operation, but should confirm and mitigate if it's possible.
                                PendingBuffers.Enqueue (buf);
                            }
                        }
                        if (PendingBuffers.Count == 0) {
                            Thread.Sleep (0);
                        } else {
                            lock (BufferLock) {
                                while (n < BufferList.Length && PendingBuffers.Count > 0) {
                                    QueuedBuffer buf = PendingBuffers.Peek ();
                                    if (shortcut || buf.Ready) {
                                        shortcut = false;
                                        PendingBuffers.Dequeue ();
                                        if (Device.EndReadPipe (buf)) {
                                            ReceivedBuffers.Enqueue (buf);
                                            recvBytes += buf.CompletedSize;
                                        } else {
                                            // Timeout condition. Requeue.
                                            Device.BeginReadPipe (DevicePipeId, buf);
                                            // Todo: If this operation fails during normal operation, the buffer is lost from rotation.
                                            // Should never happen during normal operation, but should confirm and mitigate if it's possible.
                                            PendingBuffers.Enqueue (buf);
                                        }
                                    }
                                    n++;
                                }
                            }
                        }
                    }
                    finally {
                        // Unless we're exiting, ensure we always indicate the data, even if some operation failed.
                        if (!Device.Stopping && recvBytes > 0) {
                            lock (Lock0) {
                                QueuedLength += recvBytes;
                                TotalReceived += recvBytes;
                            }
                            PipeReadBytesReceivedSub.OnNext (recvBytes);
                            //ThreadPool.QueueUserWorkItem(RaiseNewData);

                        }
                    }
                } catch (Exception ex) {
                    System.Diagnostics.Debug.Print ("Exception in background thread. {0}", ex.ToString ());
                    PipeReadExceptionSub.OnNext (ex);//could use BufferredReadPipeExceptionSub.OnError, but that would kill the observable
                    Thread.Sleep (15);
                }

                ReceiveTick.Set ();

            }
            Stopped = true;
        }
        [Obsolete]
        public event WinUSBDevice.NewDataCallback NewDataEvent;

        readonly Subject<Exception> PipeReadExceptionSub = new Subject<Exception> ();
        public IObservable<Exception> PipeReadException { get { return this.PipeReadExceptionSub.AsObservable (); } }

        readonly Subject<int> PipeReadBytesReceivedSub = new Subject<int> ();
        public IObservable<int> PipeReadBytesReceived { get { return this.PipeReadBytesReceivedSub.AsObservable (); } }

        [Obsolete]
        void WorkerThreadFunc () {
            // Attempt to set processor affinity to everything but the first two. (todo: come up with something smarter.)

            Thread.BeginThreadAffinity ();
            if (Environment.ProcessorCount > 2) {
#pragma warning disable 618
                // warning CS0618: 'System.AppDomain.GetCurrentThreadId()' is obsolete: 'AppDomain.GetCurrentThreadId has been deprecated because it does not provide a stable Id when managed threads are running on fibers (aka lightweight threads). To get a stable identifier for a managed thread, use the ManagedThreadId property on Thread.  http://go.microsoft.com/fwlink/?linkid=14202'
                int threadId = AppDomain.GetCurrentThreadId ();
#pragma warning restore 618
                int cpuCount = Environment.ProcessorCount;
                long cpuMask = -4;
                if (cpuCount == 63) {
                    cpuMask = 0x7FFFFFFFFFFFFFFCL;
                }
                if (cpuCount < 63) {
                    cpuMask = (2 << cpuCount) - 1;
                    cpuMask -= 3;
                }
                ProcessThread thread = Process.GetCurrentProcess ().Threads.Cast<ProcessThread> ().Where (t => t.Id == threadId).Single ();
                thread.ProcessorAffinity = new IntPtr (cpuMask);
            }

            while (true) {
                if (Device.Stopping)
                    break;


                if (ThreadNewData.WaitOne (1000)) {
                    RaiseNewData (null);
                }
            }
            Thread.EndThreadAffinity ();
        }
        [Obsolete]
        void RaiseNewData (object context) {
            WinUSBDevice.NewDataCallback cb = NewDataEvent;
            if (cb != null) {
                long dataMarker = -1;
                while (dataMarker != TotalReceivedBytes) {
                    dataMarker = TotalReceivedBytes;
                    cb ();
                }
            }
        }

    }
}
