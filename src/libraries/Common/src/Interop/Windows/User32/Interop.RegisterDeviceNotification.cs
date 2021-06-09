// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal partial class Interop
{
    internal partial class User32
    {
        [DllImport(Libraries.User32, ExactSpelling = true, SetLastError = true)]
        public static extern SafeDeviceNotificationHandle RegisterDeviceNotificationW(IntPtr hRecipient, DEV_BROADCAST_DEVICEINTERFACE NotificationFilter, uint Flags);

        [DllImport(Libraries.User32, ExactSpelling = true, SetLastError = true)]
        public static extern SafeDeviceNotificationHandle RegisterDeviceNotificationW(IntPtr hRecipient, DEV_BROADCAST_HANDLE NotificationFilter, uint Flags);
    }
}
