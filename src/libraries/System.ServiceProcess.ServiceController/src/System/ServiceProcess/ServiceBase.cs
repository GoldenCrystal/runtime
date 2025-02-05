// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static Interop.Advapi32;
using static Interop.User32;

namespace System.ServiceProcess
{
    /// <summary>
    /// <para>Provides a base class for a service that will exist as part of a service application. <see cref='System.ServiceProcess.ServiceBase'/>
    /// must be derived when creating a new service class.</para>
    /// </summary>
    public class ServiceBase : Component
    {
        private SERVICE_STATUS _status;
        private IntPtr _statusHandle;
        private ServiceControlCallbackEx? _commandCallbackEx;
        private ServiceMainCallback? _mainCallback;
        private ManualResetEvent? _startCompletedSignal;
        private ExceptionDispatchInfo? _startFailedException;
        private int _acceptedCommands;
        private string _serviceName;
        private bool _nameFrozen;          // set to true once we've started running and ServiceName can't be changed any more.
        private bool _commandPropsFrozen;  // set to true once we've use the Can... properties.
        private bool _disposed;
        private bool _initialized;
        private EventLog? _eventLog;
        private ConcurrentDictionary<IntPtr, DeviceFileNotificationRegistration>? _fileHandleToRegistrationMappings; // Keep a mapping from device file HANDLE values to .net registration objects.

        /// <summary>
        /// Indicates the maximum size for a service name.
        /// </summary>
        public const int MaxNameLength = 80;

        /// <summary>
        /// Creates a new instance of the <see cref='System.ServiceProcess.ServiceBase()'/> class.
        /// </summary>
        public ServiceBase()
        {
            _acceptedCommands = AcceptOptions.ACCEPT_STOP;
            ServiceName = string.Empty;
            AutoLog = true;
        }

        /// <summary>
        /// When this method is called from OnStart, OnStop, OnPause or OnContinue,
        /// the specified wait hint is passed to the
        /// Service Control Manager to avoid having the service marked as not responding.
        /// </summary>
        /// <param name="milliseconds"></param>
        public unsafe void RequestAdditionalTime(int milliseconds)
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                if (_status.currentState != ServiceControlStatus.STATE_CONTINUE_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_START_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_STOP_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_PAUSE_PENDING)
                {
                    throw new InvalidOperationException(SR.NotInPendingState);
                }

                _status.waitHint = milliseconds;
                _status.checkPoint++;
                SetServiceStatus(_statusHandle, pStatus);
            }
        }

        /// <summary>
        /// Indicates whether to report Start, Stop, Pause, and Continue commands in the event.
        /// </summary>
        [DefaultValue(true)]
        public bool AutoLog { get; set; }

        /// <summary>
        /// The termination code for the service.  Set this to a non-zero value before
        /// stopping to indicate an error to the Service Control Manager.
        /// </summary>
        public int ExitCode
        {
            get
            {
                return _status.win32ExitCode;
            }
            set
            {
                _status.win32ExitCode = value;
            }
        }

        /// <summary>
        ///  Indicates whether the service can be handle notifications on
        ///  computer power status changes.
        /// </summary>
        [DefaultValue(false)]
        public bool CanHandlePowerEvent
        {
            get
            {
                return (_acceptedCommands & AcceptOptions.ACCEPT_POWEREVENT) != 0;
            }
            set
            {
                if (_commandPropsFrozen)
                    throw new InvalidOperationException(SR.CannotChangeProperties);

                if (value)
                {
                    _acceptedCommands |= AcceptOptions.ACCEPT_POWEREVENT;
                }
                else
                {
                    _acceptedCommands &= ~AcceptOptions.ACCEPT_POWEREVENT;
                }
            }
        }

        /// <summary>
        /// Indicates whether the service can handle Terminal Server session change events.
        /// </summary>
        [DefaultValue(false)]
        public bool CanHandleSessionChangeEvent
        {
            get
            {
                return (_acceptedCommands & AcceptOptions.ACCEPT_SESSIONCHANGE) != 0;
            }
            set
            {
                if (_commandPropsFrozen)
                    throw new InvalidOperationException(SR.CannotChangeProperties);

                if (value)
                {
                    _acceptedCommands |= AcceptOptions.ACCEPT_SESSIONCHANGE;
                }
                else
                {
                    _acceptedCommands &= ~AcceptOptions.ACCEPT_SESSIONCHANGE;
                }
            }
        }

        /// <summary>
        ///   Indicates whether the service can be paused and resumed.
        /// </summary>
        [DefaultValue(false)]
        public bool CanPauseAndContinue
        {
            get
            {
                return (_acceptedCommands & AcceptOptions.ACCEPT_PAUSE_CONTINUE) != 0;
            }
            set
            {
                if (_commandPropsFrozen)
                    throw new InvalidOperationException(SR.CannotChangeProperties);

                if (value)
                {
                    _acceptedCommands |= AcceptOptions.ACCEPT_PAUSE_CONTINUE;
                }
                else
                {
                    _acceptedCommands &= ~AcceptOptions.ACCEPT_PAUSE_CONTINUE;
                }
            }
        }

        /// <summary>
        /// Indicates whether the service should be notified when the system is shutting down.
        /// </summary>
        [DefaultValue(false)]
        public bool CanShutdown
        {
            get
            {
                return (_acceptedCommands & AcceptOptions.ACCEPT_SHUTDOWN) != 0;
            }
            set
            {
                if (_commandPropsFrozen)
                    throw new InvalidOperationException(SR.CannotChangeProperties);

                if (value)
                {
                    _acceptedCommands |= AcceptOptions.ACCEPT_SHUTDOWN;
                }
                else
                {
                    _acceptedCommands &= ~AcceptOptions.ACCEPT_SHUTDOWN;
                }
            }
        }

        /// <summary>
        /// Indicates whether the service can be stopped once it has started.
        /// </summary>
        [DefaultValue(true)]
        public bool CanStop
        {
            get
            {
                return (_acceptedCommands & AcceptOptions.ACCEPT_STOP) != 0;
            }
            set
            {
                if (_commandPropsFrozen)
                    throw new InvalidOperationException(SR.CannotChangeProperties);

                if (value)
                {
                    _acceptedCommands |= AcceptOptions.ACCEPT_STOP;
                }
                else
                {
                    _acceptedCommands &= ~AcceptOptions.ACCEPT_STOP;
                }
            }
        }

        /// <summary>
        /// can be used to write notification of service command calls, such as Start and Stop, to the Application event log. This property is read-only.
        /// </summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public virtual EventLog EventLog
        {
            get
            {
                if (_eventLog == null)
                {
                    _eventLog = new EventLog("Application")
                    {
                        Source = ServiceName
                    };
                }

                return _eventLog;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected IntPtr ServiceHandle
        {
            get
            {
                return _statusHandle;
            }
        }

        /// <summary>
        /// Indicates the short name used to identify the service to the system.
        /// </summary>
        public string ServiceName
        {
            get
            {
                return _serviceName;
            }
            [MemberNotNull(nameof(_serviceName))]
            set
            {
                if (_nameFrozen)
                    throw new InvalidOperationException(SR.CannotChangeName);

                // For component properties, "" is a special case.
                if (value != "" && !ValidServiceName(value))
                    throw new ArgumentException(SR.Format(SR.ServiceName, value, ServiceBase.MaxNameLength.ToString(CultureInfo.CurrentCulture)));

                _serviceName = value;
            }
        }

        internal static bool ValidServiceName(string serviceName)
        {
            if (serviceName == null)
                return false;

            // not too long and check for empty name as well.
            if (serviceName.Length > ServiceBase.MaxNameLength || serviceName.Length == 0)
                return false;

            // no slashes or backslash allowed
            foreach (char c in serviceName)
            {
                if (c == '\\' || c == '/')
                    return false;
            }

            return true;
        }

        /// <summary>
        ///    <para>Disposes of the resources (other than memory ) used by
        ///       the <see cref='System.ServiceProcess.ServiceBase'/>.</para>
        ///    This is called from <see cref="Run(ServiceBase[])"/> when all
        ///    services in the process have entered the SERVICE_STOPPED state.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            _nameFrozen = false;
            _commandPropsFrozen = false;
            _disposed = true;
            base.Dispose(disposing);
        }

        /// <summary>
        ///    <para> When implemented in a
        ///       derived class,
        ///       executes when a Continue command is sent to the service
        ///       by the
        ///       Service Control Manager. Specifies the actions to take when a
        ///       service resumes normal functioning after being paused.</para>
        /// </summary>
        protected virtual void OnContinue()
        {
        }

        /// <summary>
        ///    <para> When implemented in a
        ///       derived class, executes when a Pause command is sent
        ///       to
        ///       the service by the Service Control Manager. Specifies the
        ///       actions to take when a service pauses.</para>
        /// </summary>
        protected virtual void OnPause()
        {
        }

        /// <summary>
        ///    <para>
        ///         When implemented in a derived class, executes when the computer's
        ///         power status has changed.
        ///    </para>
        /// </summary>
        protected virtual bool OnPowerEvent(PowerBroadcastStatus powerStatus)
        {
            return true;
        }

        /// <summary>
        ///    <para>When implemented in a derived class,
        ///       executes when a Terminal Server session change event is received.</para>
        /// </summary>
        protected virtual void OnSessionChange(SessionChangeDescription changeDescription)
        {
        }

        /// <summary>
        ///    <para>When implemented in a derived class,
        ///       executes when the system is shutting down.
        ///       Specifies what should
        ///       happen just prior
        ///       to the system shutting down.</para>
        /// </summary>
        protected virtual void OnShutdown()
        {
        }

        /// <summary>
        ///    <para> When implemented in a
        ///       derived class, executes when a Start command is sent
        ///       to the service by the Service
        ///       Control Manager. Specifies the actions to take when the service starts.</para>
        ///    <note type="rnotes">
        ///       Tech review note:
        ///       except that the SCM does not allow passing arguments, so this overload will
        ///       never be called by the SCM in the current version. Question: Is this true even
        ///       when the string array is empty? What should we say, then. Can
        ///       a ServiceBase derived class only be called programmatically? Will
        ///       OnStart never be called if you use the SCM to start the service? What about
        ///       services that start automatically at boot-up?
        ///    </note>
        /// </summary>
        protected virtual void OnStart(string[] args)
        {
        }

        /// <summary>
        ///    <para> When implemented in a
        ///       derived class, executes when a Stop command is sent to the
        ///       service by the Service Control Manager. Specifies the actions to take when a
        ///       service stops
        ///       running.</para>
        /// </summary>
        protected virtual void OnStop()
        {
        }

        /// <summary>
        ///    <para>When implemented in a derived class,
        ///    executes when a device notification is received.</para>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected virtual void OnDeviceNotification(DeviceBroadcastType eventType, SafeFileHandle handle, object? userToken)
        {
        }

        /// <summary>
        ///    <para>When implemented in a derived class,
        ///    executes when a device notification is received.</para>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected virtual void OnDeviceNotification(DeviceBroadcastType eventType, Guid interfaceClassGuid, string? deviceName)
        {
        }

        /// <summary>
        ///    <para> When implemented in a derived class,
        ///    executes when a device is about to be removed.</para>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected virtual bool OnDeviceQueryRemove(SafeFileHandle handle, object? userToken)
        {
            return true;
        }

        /// <summary>
        ///    <para> When implemented in a derived class,
        ///    executes when a device is about to be removed.</para>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected virtual bool OnDeviceQueryRemove(Guid interfaceClassGuid, string? deviceName)
        {
            return true;
        }

        private unsafe void DeferredContinue()
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                try
                {
                    OnContinue();
                    WriteLogEntry(SR.ContinueSuccessful);
                    _status.currentState = ServiceControlStatus.STATE_RUNNING;
                }
                catch (Exception e)
                {
                    _status.currentState = ServiceControlStatus.STATE_PAUSED;
                    WriteLogEntry(SR.Format(SR.ContinueFailed, e), EventLogEntryType.Error);

                    // We re-throw the exception so that the advapi32 code can report
                    // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                    throw;
                }
                finally
                {
                    SetServiceStatus(_statusHandle, pStatus);
                }
            }
        }

        private void DeferredCustomCommand(int command)
        {
            try
            {
                OnCustomCommand(command);
                WriteLogEntry(SR.CommandSuccessful);
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.CommandFailed, e), EventLogEntryType.Error);

                // We should re-throw the exception so that the advapi32 code can report
                // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                throw;
            }
        }

        private unsafe void DeferredPause()
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                try
                {
                    OnPause();
                    WriteLogEntry(SR.PauseSuccessful);
                    _status.currentState = ServiceControlStatus.STATE_PAUSED;
                }
                catch (Exception e)
                {
                    _status.currentState = ServiceControlStatus.STATE_RUNNING;
                    WriteLogEntry(SR.Format(SR.PauseFailed, e), EventLogEntryType.Error);

                    // We re-throw the exception so that the advapi32 code can report
                    // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                    throw;
                }
                finally
                {
                    SetServiceStatus(_statusHandle, pStatus);
                }
            }
        }

        private void DeferredPowerEvent(int eventType, IntPtr eventData)
        {
            // Note: The eventData pointer might point to an invalid location
            // This might happen because, between the time the eventData ptr was
            // captured and the time this deferred code runs, the ptr might have
            // already been freed.
            try
            {
                OnPowerEvent((PowerBroadcastStatus)eventType);

                WriteLogEntry(SR.PowerEventOK);
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.PowerEventFailed, e), EventLogEntryType.Error);

                // We rethrow the exception so that advapi32 code can report
                // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                throw;
            }
        }

        private unsafe int ImmediateDeviceEvent(int eventType, IntPtr eventData)
        {
            switch (((DEV_BROADCAST_HDR*)eventData)->dbch_devicetype)
            {
                case DBT_DEVTYP_DEVICEINTERFACE:
                    var broadcastDevInterface = (DEV_BROADCAST_DEVICEINTERFACE*)eventData;
                    string? deviceName = null;

                    if (broadcastDevInterface->dbcc_size > sizeof(DEV_BROADCAST_DEVICEINTERFACE))
                    {
                        int extraLength = broadcastDevInterface->dbcc_size - sizeof(DEV_BROADCAST_DEVICEINTERFACE);

                        var nameSpan = new ReadOnlySpan<char>((byte*)eventData + sizeof(DEV_BROADCAST_DEVICEINTERFACE), extraLength);
                        int endIndex = nameSpan.IndexOf('\0');
                        if (endIndex >= 0)
                        {
                            nameSpan = nameSpan.Slice(0, endIndex);
                        }
                        deviceName = nameSpan.ToString();
                    }

                    if (eventType == DBT_DEVICEQUERYREMOVE)
                    {
                        return OnDeviceQueryRemove(broadcastDevInterface->dbcc_classguid, deviceName)
                            ? 0
                            : BROADCAST_QUERY_DENY;
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => DefferedDeviceEvent((DeviceBroadcastType)eventType, broadcastDevInterface->dbcc_classguid, deviceName));
                    }
                    break;
                case DBT_DEVTYP_HANDLE:
                    var broadcastHandle = (DEV_BROADCAST_HANDLE*)eventData;

                    if (_fileHandleToRegistrationMappings is not null && _fileHandleToRegistrationMappings.TryGetValue(broadcastHandle->dbch_handle, out DeviceFileNotificationRegistration? registration))
                    {
                        if (eventType == DBT_DEVICEQUERYREMOVE)
                        {
                            return OnDeviceQueryRemove(registration.DeviceFileHandle, registration.UserToken)
                                ? 0
                                : BROADCAST_QUERY_DENY;
                        }
                        else
                        {
                            // TODO: Handle extra data for device custom events
                            ThreadPool.QueueUserWorkItem(_ => DefferedDeviceEvent((DeviceBroadcastType)eventType, registration.DeviceFileHandle, registration.UserToken));
                        }
                    }
                    break;
                default:
                    break;
            }
            return 0;
        }

        private void DefferedDeviceEvent(DeviceBroadcastType eventType, Guid interfaceClassGuid, string? deviceName)
        {
            try
            {
                OnDeviceNotification(eventType, interfaceClassGuid, deviceName);
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.SessionChangeFailed, e), EventLogEntryType.Error);

                // We rethrow the exception so that advapi32 code can report
                // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                throw;
            }
        }

        private void DefferedDeviceEvent(DeviceBroadcastType eventType, SafeFileHandle safeFileHandle, object? userToken)
        {
            try
            {
                OnDeviceNotification(eventType, safeFileHandle, userToken);
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.SessionChangeFailed, e), EventLogEntryType.Error);

                // We rethrow the exception so that advapi32 code can report
                // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                throw;
            }
        }

        private void DeferredSessionChange(int eventType, int sessionId)
        {
            try
            {
                OnSessionChange(new SessionChangeDescription((SessionChangeReason)eventType, sessionId));
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.SessionChangeFailed, e), EventLogEntryType.Error);

                // We rethrow the exception so that advapi32 code can report
                // ERROR_EXCEPTION_IN_SERVICE as it would for native services.
                throw;
            }
        }

        // We mustn't call OnStop directly from the command callback, as this will
        // tie up the command thread for the duration of the OnStop, which can be lengthy.
        // This is a problem when multiple services are hosted in a single process.
        private unsafe void DeferredStop()
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                int previousState = _status.currentState;

                _status.checkPoint = 0;
                _status.waitHint = 0;
                _status.currentState = ServiceControlStatus.STATE_STOP_PENDING;
                SetServiceStatus(_statusHandle, pStatus);
                try
                {
                    OnStop();
                    WriteLogEntry(SR.StopSuccessful);
                    _status.currentState = ServiceControlStatus.STATE_STOPPED;
                    SetServiceStatus(_statusHandle, pStatus);
                }
                catch (Exception e)
                {
                    _status.currentState = previousState;
                    SetServiceStatus(_statusHandle, pStatus);
                    WriteLogEntry(SR.Format(SR.StopFailed, e), EventLogEntryType.Error);
                    throw;
                }
            }
        }

        private unsafe void DeferredShutdown()
        {
            try
            {
                OnShutdown();
                WriteLogEntry(SR.ShutdownOK);

                if (_status.currentState == ServiceControlStatus.STATE_PAUSED || _status.currentState == ServiceControlStatus.STATE_RUNNING)
                {
                    fixed (SERVICE_STATUS* pStatus = &_status)
                    {
                        _status.checkPoint = 0;
                        _status.waitHint = 0;
                        _status.currentState = ServiceControlStatus.STATE_STOPPED;
                        SetServiceStatus(_statusHandle, pStatus);
                    }
                }
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.ShutdownFailed, e), EventLogEntryType.Error);
                throw;
            }
        }

        /// <summary>
        /// <para>When implemented in a derived class, <see cref='System.ServiceProcess.ServiceBase.OnCustomCommand'/>
        /// executes when a custom command is passed to
        /// the service. Specifies the actions to take when
        /// a command with the specified parameter value occurs.</para>
        /// <note type="rnotes">
        ///    Previously had "Passed to the
        ///    service by
        ///    the SCM", but the SCM doesn't pass custom commands. Do we want to indicate an
        ///    agent here? Would it be the ServiceController, or is there another way to pass
        ///    the int into the service? I thought that the SCM did pass it in, but
        ///    otherwise ignored it since it was an int it doesn't recognize. I was under the
        ///    impression that the difference was that the SCM didn't have default processing, so
        ///    it transmitted it without examining it or trying to performs its own
        ///    default behavior on it. Please correct where my understanding is wrong in the
        ///    second paragraph below--what, if any, contact does the SCM have with a
        ///    custom command?
        /// </note>
        /// </summary>
        protected virtual void OnCustomCommand(int command)
        {
        }

        /// <summary>
        ///    <para>Provides the main entry point for an executable that
        ///       contains multiple associated services. Loads the specified services into memory so they can be
        ///       started.</para>
        /// </summary>
        public static unsafe void Run(ServiceBase[] services)
        {
            if (services == null || services.Length == 0)
                throw new ArgumentException(SR.NoServices);

            IntPtr entriesPointer = Marshal.AllocHGlobal(checked((services.Length + 1) * sizeof(SERVICE_TABLE_ENTRY)));
            Span<SERVICE_TABLE_ENTRY> entries = new Span<SERVICE_TABLE_ENTRY>((void*)entriesPointer, services.Length + 1);
            entries.Clear();
            try
            {
                bool multipleServices = services.Length > 1;

                // The members of the last entry in the table must have NULL values to designate the end of the table.
                // Leave the last element in the entries span to be zeroed out.
                for (int index = 0; index < services.Length; ++index)
                {
                    ServiceBase service = services[index];
                    service.Initialize(multipleServices);
                    // This method allocates on unmanaged heap; Make sure that the contents are freed after use.
                    entries[index] = service.GetEntry();
                }

                // While the service is running, this function will never return. It will return when the service
                // is stopped.
                // After it returns, SCM might terminate the process at any time
                // (so subsequent code is not guaranteed to run).
                bool res = StartServiceCtrlDispatcher(entriesPointer);

                foreach (ServiceBase service in services)
                {
                    // Propagate exceptions throw during OnStart.
                    // Note that this same exception is also thrown from ServiceMainCallback
                    // (so SCM can see it as well).
                    service._startFailedException?.Throw();
                }

                string errorMessage = string.Empty;

                if (!res)
                {
                    errorMessage = new Win32Exception().Message;
                    Console.WriteLine(SR.CantStartFromCommandLine);
                }

                foreach (ServiceBase service in services)
                {
                    service.Dispose();
                    if (!res)
                    {
                        service.WriteLogEntry(SR.Format(SR.StartFailed, errorMessage), EventLogEntryType.Error);
                    }
                }
            }
            finally
            {
                // Free the pointer to the name of the service on the unmanaged heap.
                for (int i = 0; i < entries.Length; i++)
                {
                    Marshal.FreeHGlobal(entries[i].name);
                }

                // Free the unmanaged array containing the entries.
                Marshal.FreeHGlobal(entriesPointer);
            }
        }

        /// <summary>
        ///    <para>Provides the main
        ///       entry point for an executable that contains a single
        ///       service. Loads the service into memory so it can be
        ///       started.</para>
        /// </summary>
        public static void Run(ServiceBase service)
        {
            if (service == null)
                throw new ArgumentException(SR.NoServices);

            Run(new ServiceBase[] { service });
        }

        public void Stop()
        {
            DeferredStop();
        }

        private void Initialize(bool multipleServices)
        {
            if (!_initialized)
            {
                //Cannot register the service with NT service manatger if the object has been disposed, since finalization has been suppressed.
                if (_disposed)
                    throw new ObjectDisposedException(GetType().Name);

                if (!multipleServices)
                {
                    _status.serviceType = ServiceTypeOptions.SERVICE_TYPE_WIN32_OWN_PROCESS;
                }
                else
                {
                    _status.serviceType = ServiceTypeOptions.SERVICE_TYPE_WIN32_SHARE_PROCESS;
                }

                _status.currentState = ServiceControlStatus.STATE_START_PENDING;
                _status.controlsAccepted = 0;
                _status.win32ExitCode = 0;
                _status.serviceSpecificExitCode = 0;
                _status.checkPoint = 0;
                _status.waitHint = 0;

                _mainCallback = ServiceMainCallback;
                _commandCallbackEx = this.ServiceCommandCallbackEx;

                _initialized = true;
            }
        }

        // Make sure that the name field is freed after use. We allocate a new string to avoid holding one central handle,
        // which may lead to dangling pointer if Dispose is called in other thread.
        private SERVICE_TABLE_ENTRY GetEntry()
        {
            _nameFrozen = true;
            return new SERVICE_TABLE_ENTRY()
            {
                callback = Marshal.GetFunctionPointerForDelegate(_mainCallback!),
                name = Marshal.StringToHGlobalUni(_serviceName)
            };
        }

        private int ServiceCommandCallbackEx(int command, int eventType, IntPtr eventData, IntPtr eventContext)
        {
            switch (command)
            {
                case ControlOptions.CONTROL_DEVICEEVENT:
                    {
                        // We need to decode the event data synchronously, and at least the DBT_DEVICEQUERYREMOVE event should be treated immediately.
                        return ImmediateDeviceEvent(eventType, eventData);
                    }

                case ControlOptions.CONTROL_POWEREVENT:
                    {
                        ThreadPool.QueueUserWorkItem(_ => DeferredPowerEvent(eventType, eventData));
                        break;
                    }

                case ControlOptions.CONTROL_SESSIONCHANGE:
                    {
                        // The eventData pointer can be released between now and when the DeferredDelegate gets called.
                        // So we capture the session id at this point
                        WTSSESSION_NOTIFICATION sessionNotification = new WTSSESSION_NOTIFICATION();
                        Marshal.PtrToStructure(eventData, sessionNotification);
                        ThreadPool.QueueUserWorkItem(_ => DeferredSessionChange(eventType, sessionNotification.sessionId));
                        break;
                    }

                default:
                    {
                        ServiceCommandCallback(command);
                        break;
                    }
            }

            return 0;
        }

        /// <summary>
        ///     Command Handler callback is called by NT .
        ///     Need to take specific action in response to each
        ///     command message. There is usually no need to override this method.
        ///     Instead, override OnStart, OnStop, OnCustomCommand, etc.
        /// </summary>
        /// <internalonly/>
        private unsafe void ServiceCommandCallback(int command)
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                if (command == ControlOptions.CONTROL_INTERROGATE)
                    SetServiceStatus(_statusHandle, pStatus);
                else if (_status.currentState != ServiceControlStatus.STATE_CONTINUE_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_START_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_STOP_PENDING &&
                    _status.currentState != ServiceControlStatus.STATE_PAUSE_PENDING)
                {
                    switch (command)
                    {
                        case ControlOptions.CONTROL_CONTINUE:
                            if (_status.currentState == ServiceControlStatus.STATE_PAUSED)
                            {
                                _status.currentState = ServiceControlStatus.STATE_CONTINUE_PENDING;
                                SetServiceStatus(_statusHandle, pStatus);

                                ThreadPool.QueueUserWorkItem(_ => DeferredContinue());
                            }

                            break;

                        case ControlOptions.CONTROL_PAUSE:
                            if (_status.currentState == ServiceControlStatus.STATE_RUNNING)
                            {
                                _status.currentState = ServiceControlStatus.STATE_PAUSE_PENDING;
                                SetServiceStatus(_statusHandle, pStatus);

                                ThreadPool.QueueUserWorkItem(_ => DeferredPause());
                            }

                            break;

                        case ControlOptions.CONTROL_STOP:
                            int previousState = _status.currentState;
                            //
                            // Can't perform all of the service shutdown logic from within the command callback.
                            // This is because there is a single ScDispatcherLoop for the entire process.  Instead, we queue up an
                            // asynchronous call to "DeferredStop", and return immediately.  This is crucial for the multiple service
                            // per process scenario, such as the new managed service host model.
                            //
                            if (_status.currentState == ServiceControlStatus.STATE_PAUSED || _status.currentState == ServiceControlStatus.STATE_RUNNING)
                            {
                                _status.currentState = ServiceControlStatus.STATE_STOP_PENDING;
                                SetServiceStatus(_statusHandle, pStatus);
                                // Set our copy of the state back to the previous so that the deferred stop routine
                                // can also save the previous state.
                                _status.currentState = previousState;

                                ThreadPool.QueueUserWorkItem(_ => DeferredStop());
                            }

                            break;

                        case ControlOptions.CONTROL_SHUTDOWN:
                            //
                            // Same goes for shutdown -- this needs to be very responsive, so we can't have one service tying up the
                            // dispatcher loop.
                            //
                            ThreadPool.QueueUserWorkItem(_ => DeferredShutdown());
                            break;

                        default:
                            ThreadPool.QueueUserWorkItem(_ => DeferredCustomCommand(command));
                            break;
                    }
                }
            }
        }

        // Need to execute the start method on a thread pool thread.
        // Most applications will start asynchronous operations in the
        // OnStart method. If such a method is executed in MainCallback
        // thread, the async operations might get canceled immediately.
        private void ServiceQueuedMainCallback(object state)
        {
            string[] args = (string[])state;

            try
            {
                OnStart(args);
                WriteLogEntry(SR.StartSuccessful);
                _status.checkPoint = 0;
                _status.waitHint = 0;
                _status.currentState = ServiceControlStatus.STATE_RUNNING;
            }
            catch (Exception e)
            {
                WriteLogEntry(SR.Format(SR.StartFailed, e), EventLogEntryType.Error);
                _status.currentState = ServiceControlStatus.STATE_STOPPED;

                // We capture the exception so that it can be propagated
                // from ServiceBase.Run.
                // We also use the presence of this exception to inform SCM
                // that the service failed to start successfully.
                _startFailedException = ExceptionDispatchInfo.Capture(e);
            }
            _startCompletedSignal!.Set();
        }

        /// <summary>
        ///     ServiceMain callback is called by NT .
        ///     It is expected that we register the command handler,
        ///     and start the service at this point.
        /// </summary>
        /// <internalonly/>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public unsafe void ServiceMainCallback(int argCount, IntPtr argPointer)
        {
            fixed (SERVICE_STATUS* pStatus = &_status)
            {
                string[]? args = null;

                if (argCount > 0)
                {
                    char** argsAsPtr = (char**)argPointer.ToPointer();

                    //Lets read the arguments
                    // the first arg is always the service name. We don't want to pass that in.
                    args = new string[argCount - 1];

                    for (int index = 0; index < args.Length; ++index)
                    {
                        // we increment the pointer first so we skip over the first argument.
                        argsAsPtr++;
                        args[index] = Marshal.PtrToStringUni((IntPtr)(*argsAsPtr))!;
                    }
                }

                // If we are being hosted, then Run will not have been called, since the EXE's Main entrypoint is not called.
                if (!_initialized)
                {
                    Initialize(true);
                }

                _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _commandCallbackEx, (IntPtr)0);

                _nameFrozen = true;
                if (_statusHandle == (IntPtr)0)
                {
                    string errorMessage = new Win32Exception().Message;
                    WriteLogEntry(SR.Format(SR.StartFailed, errorMessage), EventLogEntryType.Error);
                }

                _status.controlsAccepted = _acceptedCommands;
                _commandPropsFrozen = true;
                if ((_status.controlsAccepted & AcceptOptions.ACCEPT_STOP) != 0)
                {
                    _status.controlsAccepted = _status.controlsAccepted | AcceptOptions.ACCEPT_SHUTDOWN;
                }

                _status.currentState = ServiceControlStatus.STATE_START_PENDING;

                bool statusOK = SetServiceStatus(_statusHandle, pStatus);

                if (!statusOK)
                {
                    return;
                }

                // Need to execute the start method on a thread pool thread.
                // Most applications will start asynchronous operations in the
                // OnStart method. If such a method is executed in the current
                // thread, the async operations might get canceled immediately
                // since NT will terminate this thread right after this function
                // finishes.
                _startCompletedSignal = new ManualResetEvent(false);
                _startFailedException = null;
                ThreadPool.QueueUserWorkItem(new WaitCallback(this.ServiceQueuedMainCallback!), args);
                _startCompletedSignal.WaitOne();

                if (_startFailedException != null)
                {
                    // Inform SCM that the service could not be started successfully.
                    // (Unless the service has already provided another failure exit code)
                    if (_status.win32ExitCode == 0)
                    {
                        _status.win32ExitCode = ServiceControlStatus.ERROR_EXCEPTION_IN_SERVICE;
                    }
                }

                statusOK = SetServiceStatus(_statusHandle, pStatus);
                if (!statusOK)
                {
                    WriteLogEntry(SR.Format(SR.StartFailed, new Win32Exception().Message), EventLogEntryType.Error);
                    _status.currentState = ServiceControlStatus.STATE_STOPPED;
                    SetServiceStatus(_statusHandle, pStatus);
                }
            }
        }

        private void WriteLogEntry(string message, EventLogEntryType type = EventLogEntryType.Information)
        {
            // EventLog failures shouldn't affect the service operation
            try
            {
                if (AutoLog)
                {
                    EventLog.WriteEntry(message, type);
                }
            }
            catch
            {
                // Do nothing.  Not having the event log is bad, but not starting the service as a result is worse.
            }
        }

        /// <summary>
        /// Register to receive device notifications for the specified file handle.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public unsafe IDisposable RegisterDeviceNotifications(SafeFileHandle handle, object? userToken)
        {
            return DeviceFileNotificationRegistration.RegisterDeviceNotifications(
                LazyInitializer.EnsureInitialized(ref _fileHandleToRegistrationMappings),
                ServiceHandle,
                handle,
                userToken);
        }

        /// <summary>
        /// Register to receive device notifications for the specified device interface class.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public IDisposable RegisterDeviceNotifications(Guid interfaceClassGuid) => DeviceInterfaceClassNotificationRegistration.RegisterDeviceNotifications(ServiceHandle, interfaceClassGuid);

        /// <summary>
        /// Register to receive device notifications for all device interface classes.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public IDisposable RegisterDeviceNotifications() => DeviceInterfaceClassNotificationRegistration.RegisterDeviceNotifications(ServiceHandle);

        // Class used to manage handle-based device registrations.
        // It would be possible to shove everything within SafeDeviceNotificationHandle to save one object allocation, but this feels a bit cleaner.
        internal sealed class DeviceFileNotificationRegistration : IDisposable
        {
            public static unsafe DeviceFileNotificationRegistration RegisterDeviceNotifications(ConcurrentDictionary<IntPtr, DeviceFileNotificationRegistration> registrationDictionary, IntPtr serviceHandle, SafeFileHandle fileHandle, object? userToken)
            {
                var registration = new DeviceFileNotificationRegistration(registrationDictionary, fileHandle, userToken);

                bool success = false;
                // Prevent the device file handle from being released while it is being used for notifications.
                fileHandle.DangerousAddRef(ref success);

                if (!success)
                {
                    throw new InvalidOperationException(SR.InvalidDeviceFileHandle);
                }

                var rawFileHandle = fileHandle.DangerousGetHandle();

                // Register the current (yet not fully initialized) instance so that notifications can provide the correct UserToken.
                registrationDictionary[rawFileHandle] = registration;

                try
                {
                    var notificationHandle = Interop.User32.RegisterDeviceNotificationW(
                        serviceHandle,
                        new Interop.User32.DEV_BROADCAST_HANDLE
                        {
                            dbch_size = sizeof(Interop.User32.DEV_BROADCAST_HANDLE),
                            dbch_devicetype = Interop.User32.DBT_DEVTYP_HANDLE,
                            dbch_handle = rawFileHandle,
                        },
                        Interop.User32.DEVICE_NOTIFY_SERVICE_HANDLE);
                    if (notificationHandle.IsInvalid)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    registration._deviceNotificationHandle = notificationHandle;

                    return registration;
                }
                catch
                {
                    registration.Dispose();
                    throw;
                }
            }

            private readonly ConcurrentDictionary<IntPtr, DeviceFileNotificationRegistration> _registrationDictionary;
            private SafeDeviceNotificationHandle? _deviceNotificationHandle;
            public SafeFileHandle DeviceFileHandle { get; }
            private int _isDisposed;

            /// <summary>
            /// A user-supplied object used to track this instance.
            /// </summary>
            public object? UserToken { get; }

            private DeviceFileNotificationRegistration(ConcurrentDictionary<IntPtr, DeviceFileNotificationRegistration> registrationDictionary, SafeFileHandle fileHandle, object? userToken)
            {
                _registrationDictionary = registrationDictionary;
                DeviceFileHandle = fileHandle;
                UserToken = userToken;
            }

            ~DeviceFileNotificationRegistration() => Dispose(false);

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 0)
                {
                    // Whenever possible, this instance should be removed from the mapping dictionary to avoid a permanent leak.
                    _registrationDictionary?.TryRemove(DeviceFileHandle?.DangerousGetHandle() ?? default, out _);

                    if (disposing)
                    {
                        _deviceNotificationHandle?.Dispose();
                    }
                }
            }
        }

        // Class used to manage device interface class notification registrations.
        // These are easier to manage, as they are bound to an easily comaprable GUID and not a HANDLE.
        internal sealed class DeviceInterfaceClassNotificationRegistration : IDisposable
        {
            public static unsafe DeviceInterfaceClassNotificationRegistration RegisterDeviceNotifications(IntPtr serviceHandle, Guid interfaceClassGuid)
            {
                var handle = RegisterDeviceNotificationW(
                    serviceHandle,
                    new DEV_BROADCAST_DEVICEINTERFACE
                    {
                        dbcc_size = sizeof(DEV_BROADCAST_DEVICEINTERFACE),
                        dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                        dbcc_classguid = interfaceClassGuid,
                    },
                    DEVICE_NOTIFY_SERVICE_HANDLE);
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return new DeviceInterfaceClassNotificationRegistration(handle);
            }

            public static unsafe DeviceInterfaceClassNotificationRegistration RegisterDeviceNotifications(IntPtr serviceHandle)
            {
                var handle = RegisterDeviceNotificationW(
                    serviceHandle,
                    new DEV_BROADCAST_DEVICEINTERFACE
                    {
                        dbcc_size = sizeof(DEV_BROADCAST_DEVICEINTERFACE),
                        dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    },
                    DEVICE_NOTIFY_SERVICE_HANDLE | DEVICE_NOTIFY_ALL_INTERFACE_CLASSES);
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return new DeviceInterfaceClassNotificationRegistration(handle);
            }

            private SafeDeviceNotificationHandle _safeDeviceNotificationHandle;

            private DeviceInterfaceClassNotificationRegistration(SafeDeviceNotificationHandle safeDeviceNotificationHandle) => _safeDeviceNotificationHandle = safeDeviceNotificationHandle;

            public void Dispose() => _safeDeviceNotificationHandle.Dispose();
        }
    }
}
