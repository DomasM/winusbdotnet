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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Subjects;
using System.Reactive.Linq;

namespace winusbdotnet {
    public enum UsbStandardRequestCode {
        GetStatus = 0,
        ClearFeature = 1,
        SetFeature = 3,
        SetAddress = 5,
        GetDescriptor = 6,
        SetDescriptor = 7,
        GetConfiguration = 8,
        SetConfiguration = 9,
        GetInterface = 10,
        SetInterface = 11
    }

    public class WinUSBDevice : IDisposable {
        public static IEnumerable<WinUSBEnumeratedDevice> EnumerateDevices (Guid deviceInterfaceGuid) {
            foreach (EnumeratedDevice devicePath in NativeMethods.EnumerateDevicesByInterface (deviceInterfaceGuid)) {
                yield return new WinUSBEnumeratedDevice (devicePath);
            }
        }

        public static IEnumerable<WinUSBEnumeratedDevice> EnumerateAllDevices () {
            foreach (EnumeratedDevice devicePath in NativeMethods.EnumerateAllWinUsbDevices ()) {
                yield return new WinUSBEnumeratedDevice (devicePath);
            }
        }
        public delegate void NewDataCallback ();

        string myDevicePath;
        SafeFileHandle deviceHandle;
        IntPtr WinusbHandle;

        internal bool Stopping = false;

        public WinUSBDevice (WinUSBEnumeratedDevice deviceInfo) {
            myDevicePath = deviceInfo.DevicePath;

            deviceHandle = NativeMethods.CreateFile (myDevicePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_ATTRIBUTE_NORMAL | NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (deviceHandle.IsInvalid) {
                throw new Exception ("Could not create file. " + (new Win32Exception ()).ToString ());
            }

            if (!NativeMethods.WinUsb_Initialize (deviceHandle, out WinusbHandle)) {
                WinusbHandle = IntPtr.Zero;
                throw new Exception ("Could not Initialize WinUSB. " + (new Win32Exception ()).ToString ());
            }


        }


        public byte AlternateSetting {
            get {
                byte alt;
                if (!NativeMethods.WinUsb_GetCurrentAlternateSetting (WinusbHandle, out alt)) {
                    throw new Exception ("GetCurrentAlternateSetting failed. " + (new Win32Exception ()).ToString ());
                }
                return alt;
            }
            set {
                if (!NativeMethods.WinUsb_SetCurrentAlternateSetting (WinusbHandle, value)) {
                    throw new Exception ("SetCurrentAlternateSetting failed. " + (new Win32Exception ()).ToString ());
                }
            }
        }

        public DeviceDescriptor GetDeviceDescriptor () {
            return DeviceDescriptor.Parse (GetDescriptor (DescriptorType.Device, 0, 32));
        }

        public ConfigurationDescriptor GetConfigurationDescriptor () {
            // Todo: more than just the first configuration.
            return ConfigurationDescriptor.Parse (GetDescriptor (DescriptorType.Configuration, 0));
        }


        public byte[] GetDescriptor (DescriptorType descriptorType, byte descriptorIndex, ushort length = 1024) {
            UInt16 value = (UInt16)(descriptorIndex | ((byte)descriptorType) << 8);
            return ControlTransferIn (ControlTypeStandard | ControlRecipientDevice, (byte)UsbStandardRequestCode.GetDescriptor, value, 0, length);
        }

        public byte GetPipeId (byte pipeIndex) {
            USB_INTERFACE_DESCRIPTOR intefaceDescription = new USB_INTERFACE_DESCRIPTOR ();
            WINUSB_PIPE_INFORMATION pipeInfo = new WINUSB_PIPE_INFORMATION ();
            if (NativeMethods.WinUsb_QueryInterfaceSettings (WinusbHandle, 0, ref intefaceDescription)) {
                if (NativeMethods.WinUsb_QueryPipe (WinusbHandle, 0, pipeIndex, ref pipeInfo)) {
                    var pipeId = pipeInfo.PipeId;
                    return pipeId;
                } else {
                    throw new Exception ("WinUsb_QueryPipe failed. " + (new Win32Exception ()).ToString ());
                }
            } else {
                throw new Exception ("WinUsb_QueryInterfaceSettings failed. " + (new Win32Exception ()).ToString ());
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose (bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    // TODO: dispose managed state (managed objects).
                }

                Stopping = true;
                
                // Close handles which will cause background theads to stop working & exit.
                if (WinusbHandle != IntPtr.Zero) {
                    NativeMethods.WinUsb_Free (WinusbHandle);
                    WinusbHandle = IntPtr.Zero;
                }
                deviceHandle.Close ();

                // Wait for pipe threads to quit
                foreach (BufferedPipeThread th in bufferedPipes.Values) {
                    while (!th.Stopped) Thread.Sleep (5);
                }

                disposedValue = true;
            }
        }

        ~WinUSBDevice () {
            Dispose (false);
        }

        public void Dispose () {
            Dispose (true);
            GC.SuppressFinalize (this);
        }
        #endregion



        public void ResetPipe (byte pipeId) {
            if (!NativeMethods.WinUsb_ResetPipe (WinusbHandle, pipeId)) {
                throw new Exception ("ResetPipe failed. " + (new Win32Exception ()).ToString ());
            }
        }

        public void FlushPipe (byte pipeId) {
            if (!NativeMethods.WinUsb_FlushPipe (WinusbHandle, pipeId)) {
                throw new Exception ("FlushPipe failed. " + (new Win32Exception ()).ToString ());
            }
        }

        public UInt32 GetPipePolicy (byte pipeId, WinUsbPipePolicy policyType) {
            UInt32[] data = new UInt32[1];
            UInt32 length = 4;

            if (!NativeMethods.WinUsb_GetPipePolicy (WinusbHandle, pipeId, (uint)policyType, ref length, data)) {
                throw new Exception ("GetPipePolicy failed. " + (new Win32Exception ()).ToString ());
            }

            return data[0];
        }

        public void SetPipePolicy (byte pipeId, WinUsbPipePolicy policyType, UInt32 newValue) {
            UInt32[] data = new UInt32[1];
            UInt32 length = 4;
            data[0] = newValue;

            if (!NativeMethods.WinUsb_SetPipePolicy (WinusbHandle, pipeId, (uint)policyType, length, data)) {
                throw new Exception ("SetPipePolicy failed. " + (new Win32Exception ()).ToString ());
            }
        }

        Dictionary<byte, BufferedPipeThread> bufferedPipes = new Dictionary<byte, BufferedPipeThread> ();
        // Todo: Compute better value for bufferLength. Based on pipe transfer size.
        public void EnableBufferedRead (byte pipeId, int bufferCount = 16, int bufferLength = 32) {
            if (!bufferedPipes.ContainsKey (pipeId)) {
                bufferedPipes.Add (pipeId, new BufferedPipeThread (this, pipeId, bufferCount, bufferLength));
            }
        }

        public void StopBufferedRead (byte pipeId) {
            throw new NotImplementedException ();
        }
        [Obsolete]
        public void BufferedReadNotifyPipe (byte pipeId, NewDataCallback callback) {
            if (!bufferedPipes.ContainsKey (pipeId)) {
                throw new Exception ("Pipe not enabled for buffered reads!");
            }
            bufferedPipes[pipeId].NewDataEvent += callback;
        }

        public IObservable<int> BufferredReadPipeBytesReceived (byte pipeId) {
            if (!bufferedPipes.ContainsKey (pipeId)) throw new Exception ("Pipe not enabled for buffered reads!");
            return bufferedPipes[pipeId].PipeReadBytesReceived;
        }

        public IObservable<Exception> BufferredReadPipeExceptionOccured (byte pipeId) {
            if (!bufferedPipes.ContainsKey (pipeId)) throw new Exception ("Pipe not enabled for buffered reads!");
            return bufferedPipes[pipeId].PipeReadException;
        }


        BufferedPipeThread GetInterface (byte pipeId, bool packetInterface) {
            if (!bufferedPipes.ContainsKey (pipeId)) {
                throw new Exception ("Pipe not enabled for buffered reads!");
            }
            BufferedPipeThread th = bufferedPipes[pipeId];
            if (!th.InterfaceBound) {
                th.InterfaceBound = true;
                th.PacketInterface = packetInterface;
            } else {
                if (th.PacketInterface != packetInterface) {
                    string message = string.Format ("Pipe is already bound as a {0} interface - cannot bind to both Packet and Byte interfaces",
                                                   packetInterface ? "Byte" : "Packet");
                    throw new Exception (message);
                }
            }
            return th;
        }
        public IPipeByteReader BufferedGetByteInterface (byte pipeId) {
            return GetInterface (pipeId, false);
        }

        public IPipePacketReader BufferedGetPacketInterface (byte pipeId) {
            return GetInterface (pipeId, true);
        }



        public byte[] BufferedReadPipe (byte pipeId, int byteCount) {
            return BufferedGetByteInterface (pipeId).ReceiveBytes (byteCount);
        }

        public byte[] BufferedPeekPipe (byte pipeId, int byteCount) {
            return BufferedGetByteInterface (pipeId).PeekBytes (byteCount);
        }

        public void BufferedSkipBytesPipe (byte pipeId, int byteCount) {
            BufferedGetByteInterface (pipeId).SkipBytes (byteCount);
        }

        public byte[] BufferedReadExactPipe (byte pipeId, int byteCount) {
            return BufferedGetByteInterface (pipeId).ReceiveExactBytes (byteCount);
        }

        public int BufferedByteCountPipe (byte pipeId) {
            return BufferedGetByteInterface (pipeId).QueuedDataLength;
        }


        public byte[] ReadExactPipe (byte pipeId, int byteCount) {
            int read = 0;
            byte[] accumulate = null;
            while (read < byteCount) {
                byte[] data = ReadPipe (pipeId, byteCount - read);
                if (data.Length == 0) {
                    // Timeout happened in ReadPipe.
                    throw new Exception ("Timed out while trying to read data.");
                }
                if (data.Length == byteCount) return data;
                if (accumulate == null) {
                    accumulate = new byte[byteCount];
                }
                Array.Copy (data, 0, accumulate, read, data.Length);
                read += data.Length;
            }
            return accumulate;
        }

        // basic synchronous read
        public byte[] ReadPipe (byte pipeId, int byteCount) {

            byte[] data = new byte[byteCount];

            UInt32 transferSize = 0;
            if (!NativeMethods.WinUsb_ReadPipe (WinusbHandle, pipeId, data, (uint)byteCount, ref transferSize, IntPtr.Zero)) {
                if (Marshal.GetLastWin32Error () == NativeMethods.ERROR_SEM_TIMEOUT) {
                    // This was a pipe timeout. Return an empty byte array to indicate this case.
                    return new byte[0];
                }
                throw new Exception ("ReadPipe failed. " + (new Win32Exception ()).ToString ());
            }

            byte[] newdata = new byte[transferSize];
            Array.Copy (data, newdata, transferSize);
            return newdata;

        }

        // Asynchronous read bits, only for use with buffered reader for now.
        internal void BeginReadPipe (byte pipeId, QueuedBuffer buffer) {
            buffer.Overlapped.WaitEvent.Reset ();

            if (!NativeMethods.WinUsb_ReadPipe (WinusbHandle, pipeId, buffer.PinnedBuffer, (uint)buffer.BufferSize, IntPtr.Zero, buffer.Overlapped.OverlappedStruct)) {
                if (Marshal.GetLastWin32Error () != NativeMethods.ERROR_IO_PENDING) {
                    throw new Exception ("ReadPipe failed. " + (new Win32Exception ()).ToString ());
                }
            }
        }

        internal bool EndReadPipe (QueuedBuffer buf) {
            UInt32 transferSize;

            if (!NativeMethods.WinUsb_GetOverlappedResult (WinusbHandle, buf.Overlapped.OverlappedStruct, out transferSize, true)) {
                if (Marshal.GetLastWin32Error () == NativeMethods.ERROR_SEM_TIMEOUT) {
                    // This was a pipe timeout. Return an empty byte array to indicate this case.
                    //System.Diagnostics.Debug.WriteLine("Timed out");
                    return false;
                }
                throw new Exception ("ReadPipe's overlapped result failed. " + (new Win32Exception ()).ToString ());
            }
            buf.CompletedSize = (int)transferSize;
            return true;
        }


        // basic synchronous send.
        public void WritePipe (byte pipeId, byte[] pipeData) {

            int remainingbytes = pipeData.Length;
            while (remainingbytes > 0) {

                UInt32 transferSize = 0;
                if (!NativeMethods.WinUsb_WritePipe (WinusbHandle, pipeId, pipeData, (uint)pipeData.Length, ref transferSize, IntPtr.Zero)) {
                    throw new Exception ("WritePipe failed. " + (new Win32Exception ()).ToString ());
                }
                if (transferSize == pipeData.Length) return;

                remainingbytes -= (int)transferSize;

                // Need to retry. Copy the remaining data to a new buffer.
                byte[] data = new byte[remainingbytes];
                Array.Copy (pipeData, transferSize, data, 0, remainingbytes);

                pipeData = data;
            }
        }



        public void ControlTransferOut (byte requestType, byte request, UInt16 value, UInt16 index, byte[] data) {
            NativeMethods.WINUSB_SETUP_PACKET setupPacket = new NativeMethods.WINUSB_SETUP_PACKET ();
            setupPacket.RequestType = (byte)(requestType | ControlDirectionOut);
            setupPacket.Request = request;
            setupPacket.Value = value;
            setupPacket.Index = index;
            if (data != null) {
                setupPacket.Length = (ushort)data.Length;
            }

            UInt32 actualLength = 0;

            if (!NativeMethods.WinUsb_ControlTransfer (WinusbHandle, setupPacket, data, setupPacket.Length, out actualLength, IntPtr.Zero)) {
                throw new Exception ("ControlTransfer failed. " + (new Win32Exception ()).ToString ());
            }

            if (data != null && actualLength != data.Length) {
                throw new Exception ("Not all data transferred");
            }
        }

        public byte[] ControlTransferIn (byte requestType, byte request, UInt16 value, UInt16 index, UInt16 length) {
            NativeMethods.WINUSB_SETUP_PACKET setupPacket = new NativeMethods.WINUSB_SETUP_PACKET ();
            setupPacket.RequestType = (byte)(requestType | ControlDirectionIn);
            setupPacket.Request = request;
            setupPacket.Value = value;
            setupPacket.Index = index;
            setupPacket.Length = length;

            byte[] output = new byte[length];
            UInt32 actualLength = 0;

            if (!NativeMethods.WinUsb_ControlTransfer (WinusbHandle, setupPacket, output, (uint)output.Length, out actualLength, IntPtr.Zero)) {
                throw new Exception ("ControlTransfer failed. " + (new Win32Exception ()).ToString ());
            }

            if (actualLength != output.Length) {
                byte[] copyTo = new byte[actualLength];
                Array.Copy (output, copyTo, actualLength);
                output = copyTo;
            }
            return output;
        }

        const byte ControlDirectionOut = 0x00;
        const byte ControlDirectionIn = 0x80;

        public const byte ControlTypeStandard = 0x00;
        public const byte ControlTypeClass = 0x20;
        public const byte ControlTypeVendor = 0x40;

        public const byte ControlRecipientDevice = 0;
        public const byte ControlRecipientInterface = 1;
        public const byte ControlRecipientEndpoint = 2;
        public const byte ControlRecipientOther = 3;




    }


    internal class QueuedBuffer : IDisposable {
        public readonly int BufferSize;
        public int CompletedSize;
        public Overlapped Overlapped;
        public IntPtr PinnedBuffer;
        public QueuedBuffer (int bufferSizeBytes) {
            BufferSize = bufferSizeBytes;
            Overlapped = new Overlapped ();
            PinnedBuffer = Marshal.AllocHGlobal (BufferSize);
        }

        public void Dispose () {
            Overlapped.Dispose ();
            Marshal.FreeHGlobal (PinnedBuffer);
            GC.SuppressFinalize (this);
        }

        public void Wait () {
            Overlapped.WaitEvent.WaitOne ();
        }

        public bool Ready {
            get {
                return Overlapped.WaitEvent.WaitOne (0);
            }
        }

    }
}
