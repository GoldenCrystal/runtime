// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Win32.SafeHandles
{
    internal sealed class SafeDeviceNotificationHandle : SafeHandle
    {
        internal SafeDeviceNotificationHandle() : base(IntPtr.Zero, true)
        {
        }

        protected override bool ReleaseHandle()
        {
            Interop.User32.UnregisterDeviceNotification(handle);
            return false;
        }

        public override bool IsInvalid => handle == IntPtr.Zero;
    }
}
