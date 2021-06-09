// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.ServiceProcess
{
    public enum DeviceBroadcastType
    {
        Arrival = Interop.User32.DBT_DEVICEARRIVAL,
        QueryRemove = Interop.User32.DBT_DEVICEQUERYREMOVE,
        QueryRemoveFailed = Interop.User32.DBT_DEVICEQUERYREMOVEFAILED,
        RemovePending = Interop.User32.DBT_DEVICEREMOVEPENDING,
        RemoveComplete = Interop.User32.DBT_DEVICEREMOVECOMPLETE,
        CustomEvent = Interop.User32.DBT_CUSTOMEVENT,
    }
}
