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
using System.Text.RegularExpressions;

namespace winusbdotnet {
    public class WinUSBEnumeratedDevice {
        internal string DevicePath;
        internal EnumeratedDevice EnumeratedData;
        internal WinUSBEnumeratedDevice (EnumeratedDevice enumDev) {
            DevicePath = enumDev.DevicePath;
            EnumeratedData = enumDev;
            Match m = Regex.Match (DevicePath, @"vid_([\da-f]{4})");
            if (m.Success) { VendorID = Convert.ToUInt16 (m.Groups[1].Value, 16); }
            m = Regex.Match (DevicePath, @"pid_([\da-f]{4})");
            if (m.Success) { ProductID = Convert.ToUInt16 (m.Groups[1].Value, 16); }
            m = Regex.Match (DevicePath, @"mi_([\da-f]{2})");
            if (m.Success) { UsbInterface = Convert.ToByte (m.Groups[1].Value, 16); }
        }

        public string Path { get { return DevicePath; } }
        public UInt16 VendorID { get; private set; }
        public UInt16 ProductID { get; private set; }
        public Byte UsbInterface { get; private set; }
        public Guid InterfaceGuid { get { return EnumeratedData.InterfaceGuid; } }


        public override string ToString () {
            return string.Format ("WinUSBEnumeratedDevice({0},{1})", DevicePath, InterfaceGuid);
        }
    }
}
