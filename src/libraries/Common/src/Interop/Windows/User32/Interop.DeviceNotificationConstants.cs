// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

internal partial class Interop
{
    internal partial class User32
    {
        public const int DEVICE_NOTIFY_SERVICE_HANDLE = 0x00000001;
        public const int DEVICE_NOTIFY_ALL_INTERFACE_CLASSES = 0x00000004;

        public const int BROADCAST_QUERY_DENY = 0x424D5144;

        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEQUERYREMOVE = 0x8001;
        public const int DBT_DEVICEQUERYREMOVEFAILED = 0x8002;
        public const int DBT_DEVICEREMOVEPENDING = 0x8003;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_CUSTOMEVENT = 0x8006;

        public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        public const int DBT_DEVTYP_HANDLE = 0x00000006;
    }
}
