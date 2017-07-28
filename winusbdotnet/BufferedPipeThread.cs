using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;

namespace winusbdotnet {
    public class BufferedPipeThread : IDisposable {

        Thread PipeThread { get; }
        WinUSBDevice Device { get; } //todo there is no need for explicit device, could do away with minimal interface?
        byte DevicePipeId { get; }


        public bool Stopped { get; private set; }
        public bool StoppInitiated { get; private set; }
        ManualResetEvent StopEvent { get; }

        QueuedBuffer[] Buffers { get; }
        ManualResetEvent[] ReadEvents { get; }//read events have one extra element at the end compared to Buffers, to signal exit

        readonly Subject<Exception> PipeReadExceptionSub = new Subject<Exception> ();
        public IObservable<Exception> PipeReadException { get { return this.PipeReadExceptionSub.AsObservable (); } }

        readonly Subject<byte[]> PipeReadReceivedSub = new Subject<byte[]> ();
        public IObservable<byte[]> PipeReadReceived { get { return this.PipeReadReceivedSub.AsObservable (); } }


        internal BufferedPipeThread (WinUSBDevice dev, byte pipeId, int bufferCount, int bufferSize) {
            Device = dev;
            DevicePipeId = pipeId;
            int maxTransferSize = (int)dev.GetPipePolicy (pipeId, WinUsbPipePolicy.MAXIMUM_TRANSFER_SIZE);
            if (bufferSize > maxTransferSize) { bufferSize = maxTransferSize; }

            Buffers = new QueuedBuffer[bufferCount];
            ReadEvents = new ManualResetEvent[bufferCount + 1];
            for (int i = 0; i < bufferCount; i++) {
                Buffers[i] = new QueuedBuffer (bufferSize);
                ReadEvents[i] = Buffers[i].Overlapped.WaitEvent;
            }
            StopEvent = new ManualResetEvent (false);
            ReadEvents[bufferCount] = StopEvent;

            PipeThread = new Thread (ThreadFunc);
            PipeThread.IsBackground = true;

            foreach (QueuedBuffer qb in Buffers) {
                Device.BeginReadPipe (pipeId, qb);
            }
            PipeThread.Start ();
        }



        void ThreadFunc (object context) {
            while (true) {
                try {
                    try {
                        var signaledIndex = ManualResetEvent.WaitAny (ReadEvents, 200);
                        if (signaledIndex == ManualResetEvent.WaitTimeout) {
                            //no events in 200 ms, should I let somebody know? Or will they spin their own timer?
                        } else if (signaledIndex == Buffers.Length) {
                            //stop event
                            break;
                        } else {
                            var buf = Buffers[signaledIndex];
                            EndReadingBuffer (buf);
                        }
                    }
                    finally {
                        // Unless we're exiting, ensure we always indicate the data, even if some operation failed.
                        // todo really do this
                    }
                } catch (Exception ex) {
                    if (StoppInitiated == false) {
                        PipeReadExceptionSub.OnNext (ex);//could use BufferredReadPipeExceptionSub.OnError, but that would kill the observable
                        Thread.Sleep (15);
                    }
                }
            }
            Stopped = true;
        }

        private void EndReadingBuffer (QueuedBuffer buf) {
            if (Device.EndReadPipe (buf)) {
                PipeReadReceivedSub.OnNext (buf.GetBufferCopy ());
                buf.Overlapped.WaitEvent.Reset ();
                Device.BeginReadPipe (DevicePipeId, buf);
            } else {
                //read failed due timeout
                //todo handle this case
                //use PipeReadExceptionSub??
            }
        }



        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose (bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    StoppInitiated = true;
                    StopEvent.Set ();
                    for (int i = 0; i < 200; i++) {
                        if (Stopped) break;
                        Thread.Sleep (10);
                    }
                }
                disposedValue = true;
            }
        }

        public void Dispose () {
            Dispose (true);
        }
        #endregion
    }
}


