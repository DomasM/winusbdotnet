using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;

namespace winusbdotnet {
    internal class BufferedPipeThread {

        Thread PipeThread { get; }
        WinUSBDevice Device { get; } //todo there is no need for explicit device, could do away with minimal interface?
        byte DevicePipeId { get; }


        public bool Stopped { get; private set; }


        QueuedBuffer[] BufferList { get; }
        ManualResetEvent[] ReadEvents { get; }


        public BufferedPipeThread (WinUSBDevice dev, byte pipeId, int bufferCount, int bufferSize) {
            Device = dev;
            DevicePipeId = pipeId;
            int maxTransferSize = (int)dev.GetPipePolicy (pipeId, WinUsbPipePolicy.MAXIMUM_TRANSFER_SIZE);
            if (bufferSize > maxTransferSize) { bufferSize = maxTransferSize; }

            BufferList = new QueuedBuffer[bufferCount];
            ReadEvents = new ManualResetEvent[bufferCount];
            for (int i = 0; i < bufferCount; i++) {
                BufferList[i] = new QueuedBuffer (bufferSize);
                ReadEvents[i] = BufferList[i].Overlapped.WaitEvent;
            }

            
            PipeThread = new Thread (ThreadFunc);
            PipeThread.IsBackground = true;

            foreach (QueuedBuffer qb in BufferList) {
                Device.BeginReadPipe (pipeId, qb);
            }
            PipeThread.Start ();
        }



        void ThreadFunc (object context) {
            while (true) {
                try {
                    if (Device.Stopping) break;
                    try {
                        var signaledIndex = ManualResetEvent.WaitAny (ReadEvents, 200);
                        if (signaledIndex != ManualResetEvent.WaitTimeout) {
                            var buf = BufferList[signaledIndex];
                            EndReadingBuffer (buf);
                        } else {
                            //no events in 200 ms, should I let somebody know? Or will they spin their own timer?
                        }
                    }
                    finally {
                        // Unless we're exiting, ensure we always indicate the data, even if some operation failed.
                        // todo really do this
                    }
                } catch (Exception ex) {
                    if (Device.Stopping == false) {
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
                Device.BeginReadPipe (DevicePipeId, buf);//read again in any case?? can I start reading again as unfinished read is still there somewhere
            } else {
                //read failed due timeout
                //todo handle this case
                //use PipeReadExceptionSub??
            }
        }

        readonly Subject<Exception> PipeReadExceptionSub = new Subject<Exception> ();
        public IObservable<Exception> PipeReadException { get { return this.PipeReadExceptionSub.AsObservable (); } }


        readonly Subject<byte[]> PipeReadReceivedSub = new Subject<byte[]> ();
        public IObservable<byte[]> PipeReadReceived { get { return this.PipeReadReceivedSub.AsObservable (); } }
    }
}


