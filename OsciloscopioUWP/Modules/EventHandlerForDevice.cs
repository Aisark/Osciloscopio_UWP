using System;
using System.Threading.Tasks;

using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

using Windows.ApplicationModel;
using Windows.Foundation;

using Windows.UI.Core;
using Windows.UI.Xaml;

namespace OsciloscopioUWP.Modules
{
    /// <summary>
    /// Esta clase creara una instancia singlenton para manejar un dispositivo conectado.
    /// Crea y maneja el uso de eventos DeviceWatcher para vigilar los estados de los dispostivos
    /// (Conected, Disconected, Suspencion y Resume)
    /// </summary>
    public class EventHandlerForDevice
    {
        /// <summary>
        /// Permite un singleton de EventHandlerForDevice
        /// </summary>
        private static EventHandlerForDevice eventHandlerForDevice;

        /// <summary>
        /// Usado para sincronizar los hilos de ejecucion y evitar multiples instancias de EventHandlerForDevice
        /// </summary>
        private static Object singletonCreationLock = new Object();

        private String deviceSelector;
        private DeviceWatcher deviceWatcher;

        /*************************************************
         * Estados de los DeviceWatcher
         *************************************************/
        private Boolean watcherSuspended;
        private Boolean watcherStarted;
        private Boolean isEnabledAutoReconnect;

        /************************************************
         * Dispositivo, Informarcion del dispositovo
         ************************************************/
        private DeviceInformation deviceInformation;
        private DeviceAccessInformation deviceAccessInformation;
        private SerialDevice device;

        /***********************************************
         * Delegados de los eventos a manejar
         ***********************************************/
        private SuspendingEventHandler appSuspendEventHandler;
        private EventHandler<Object> appResumeEventHandler;

        private TypedEventHandler<EventHandlerForDevice, DeviceInformation> deviceCloseCallback;
        private TypedEventHandler<EventHandlerForDevice, DeviceInformation> deviceConnectedCallback;

        private TypedEventHandler<DeviceWatcher, DeviceInformation> deviceAddedEventHandler;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> deviceRemovedEventHandler;
        private TypedEventHandler<DeviceAccessInformation, DeviceAccessChangedEventArgs> deviceAccessEventHandler;

        //Puntero de la Clase principal
        private MainPage MainPage = MainPage.Current;

        /// <summary>
        /// Fuerza el patron singlenton para que solo un objeto maneje todo los eventos de la aplicación
        /// 
        /// Permite una instancia global atravez de todo el escenario
        /// 
        /// Sí no hay una instancia de  EventHandlerForDevice creada antes de que esta propiedad es llamada, 
        /// una instancia de  EventHandlerForDevice sera creada
        /// </summary>
        public static EventHandlerForDevice Current
        {
            get
            {
                if (eventHandlerForDevice == null)
                {
                    lock (singletonCreationLock)
                    {
                        if (eventHandlerForDevice == null)
                        {
                            CreateNewEventHandlerForDevice();
                        }
                    }
                }

                return eventHandlerForDevice;
            }
        }

        /// <summary>
        /// Crea una instancia de EventHandlerForDevice, permite la auto reconección, y lo usa como la instancia actual
        /// </summary>
        public static void CreateNewEventHandlerForDevice()
        {
            eventHandlerForDevice = new EventHandlerForDevice();
        }

        /**********************************************************************************************
         * Propiedades de los deviceCloseCallback, 
         * deviceConnectedCallback, 
         * Device, 
         * IsDeviceConnected
         **********************************************************************************************/
        public TypedEventHandler<EventHandlerForDevice, DeviceInformation> OnDeviceClose
        {
            get
            {
                return deviceCloseCallback;
            }

            set
            {
                deviceCloseCallback = value;
            }
        }
        public TypedEventHandler<EventHandlerForDevice, DeviceInformation> OnDeviceConnected
        {
            get
            {
                return deviceConnectedCallback;
            }

            set
            {
                deviceConnectedCallback = value;
            }
        }
        public Boolean IsDeviceConnected
        {
            get
            {
                return (device != null);
            }
        }
        public SerialDevice Device
        {
            get
            {
                return device;
            }
        }

        /// <summary>
        /// Este DeviceInformation representa el dispositivo conectado y cual va a ser reconectado cuando el 
        /// dispositivo es conectado otra vez (Si IsEnabledAutoReconnect es true)
        /// </summary>
        public DeviceInformation DeviceInformation
        {
            get
            {
                return deviceInformation;
            }
        }

        /// <summary>
        /// Regresa el DeviceAccessInformation para el dispositivo que esta actualmente conectado usando este
        /// EventHandlerForDevice
        /// </summary>
        public DeviceAccessInformation DeviceAccessInformation
        {
            get
            {
                return deviceAccessInformation;
            }
        }

        /// <summary>
        /// El DeviceSelector AQS usado para encontrar este dispositivo
        /// </summary>
        public String DeviceSelector
        {
            get
            {
                return deviceSelector;
            }
        }

        /// <summary>
        /// Sera verdadero si EventHandlerForDevice intentara reconectar al dispositivo una vez es reconectado
        /// a la computadora otra vez
        /// </summary>
        public Boolean IsEnabledAutoReconnect
        {
            get
            {
                return isEnabledAutoReconnect;
            }
            set
            {
                isEnabledAutoReconnect = value;
            }
        }

        /// <summary>
        /// Este metodo abre el dispositivo, despues de que el dispositivo es abierto, guarda el dispositivo
        /// para que pueda ser usado en multiples escenarios
        /// 
        /// Este metodo es lanzado desde el hilo principal del UI
        /// 
        /// Es usado para re-abrir el dispositivo despues de que el dispositivo se reconecta a la computadora y cuando
        /// la app se reanuda.
        /// </summary>
        /// <param name="deviceInfo">Device information of the device to be opened</param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        /// <returns>Es verdadero si fue abierto exitosamente, Falso si por razones desconocidas no se abrio, y lanza una
        /// exepción si no abrio por razones extraordinarias.</returns>
        public async Task<Boolean> OpenDeviceAsync(DeviceInformation deviceInfo, String deviceSelector)
        {
            device = await SerialDevice.FromIdAsync(deviceInfo.Id);

            Boolean successfullyOpenedDevice = false;
            NotifyType notificationStatus;
            String notificationMessage = null;

            // Device could have been blocked by user or the device has already been opened by another app.
            if (device != null)
            {
                successfullyOpenedDevice = true;

                deviceInformation = deviceInfo;
                this.deviceSelector = deviceSelector;

                notificationStatus = NotifyType.StatusMessage;
                notificationMessage = "Device " + deviceInformation.Name + " opened";

                // Notify registered callback handle that the device has been opened
                if (deviceConnectedCallback != null)
                {
                    deviceConnectedCallback(this, deviceInformation);
                }

                if (appSuspendEventHandler == null || appResumeEventHandler == null)
                {
                    RegisterForAppEvents();
                }

                // Register for DeviceAccessInformation.AccessChanged event and react to any changes to the
                // user access after the device handle was opened.
                if (deviceAccessEventHandler == null)
                {
                    RegisterForDeviceAccessStatusChange();
                }

                // Create and register device watcher events for the device to be opened unless we're reopening the device
                if (deviceWatcher == null)
                {
                    deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);

                    RegisterForDeviceWatcherEvents();
                }

                if (!watcherStarted)
                {
                    // Start the device watcher after we made sure that the device is opened.
                    StartDeviceWatcher();
                }
            }
            else
            {
                successfullyOpenedDevice = false;

                notificationStatus = NotifyType.ErrorMessage;

                var deviceAccessStatus = DeviceAccessInformation.CreateFromId(deviceInfo.Id).CurrentStatus;

                if (deviceAccessStatus == DeviceAccessStatus.DeniedByUser)
                {
                    notificationMessage = deviceInfo.Name + ": El acceso fue bloqueado por el usuario";
                }
                else if (deviceAccessStatus == DeviceAccessStatus.DeniedBySystem)
                {
                    // This status is most likely caused by app permissions (did not declare the device in the app's package.appxmanifest)
                    // This status does not cover the case where the device is already opened by another app.
                    notificationMessage = deviceInfo.Name + ": El acceso fue bloqueado por sistema";
                }
                else
                {
                    // Most likely the device is opened by another app, but cannot be sure
                    notificationMessage = deviceInfo.Name + ": Error desconocido, posiblemente fue abierto por otra app ";
                }
            }
        

            MainPage.NotifyUser(notificationMessage, notificationStatus);

            return successfullyOpenedDevice;
        }

        /// <summary>
        /// Closes the device, stops the device watcher, stops listening for app events, and resets object state to before a device
        /// was ever connected.
        /// </summary>
        public void CloseDevice()
        {
            if (IsDeviceConnected)
            {
                CloseCurrentlyConnectedDevice();
            }

            if (deviceWatcher != null)
            {
                if (watcherStarted)
                {
                    StopDeviceWatcher();

                    UnregisterFromDeviceWatcherEvents();
                }

                deviceWatcher = null;
            }

            if (deviceAccessInformation != null)
            {
                UnregisterFromDeviceAccessStatusChange();

                deviceAccessInformation = null;
            }

            if (appSuspendEventHandler != null || appResumeEventHandler != null)
            {
                UnregisterFromAppEvents();
            }

            deviceInformation = null;
            deviceSelector = null;

            deviceConnectedCallback = null;
            deviceCloseCallback = null;

            isEnabledAutoReconnect = true;
        }

        public void CloseCurrentlyConnectedDevicePublic()
        {
            CloseCurrentlyConnectedDevice();
        }

        /// <summary>
        /// Configura los parametros para el dispositivo que esta actualmente conectado
        /// </summary>
        public void ConfigureCurrentlyConnectedDevice()
        {
            if (IsDeviceConnected)
            {
                if (device != null)
                {
                    device.BaudRate = 115200;
                    device.Parity = SerialParity.None;
                    device.StopBits = SerialStopBitCount.One;
                    device.Handshake = SerialHandshake.None;
                    device.DataBits = 8;
                    device.ReadTimeout = TimeSpan.FromMilliseconds(95);
                    device.WriteTimeout = TimeSpan.FromMilliseconds(100);
                }
            }

        }

        /// <summary>
        /// This method demonstrates how to close the device properly using the WinRT Serial API.
        ///
        /// When the SerialDevice is closing, it will cancel all IO operations that are still pending (not complete).
        /// The close will not wait for any IO completion callbacks to be called, so the close call may complete before any of
        /// the IO completion callbacks are called.
        /// The pending IO operations will still call their respective completion callbacks with either a task 
        /// cancelled error or the operation completed.
        /// </summary>
        private void CloseCurrentlyConnectedDevice()
        {
            if (device != null)
            {
                // Notify callback that we're about to close the device
                if (deviceCloseCallback != null)
                {
                    deviceCloseCallback(this, deviceInformation);
                }

                // This closes the handle to the device
                device.Dispose();

                device = null;

                // Save the deviceInformation.Id in case deviceInformation is set to null when closing the
                // device
                String deviceName = deviceInformation.Name;

            }
        }

        private EventHandlerForDevice()
        {
            watcherStarted = false;
            watcherSuspended = false;
            isEnabledAutoReconnect = true;
        }

        //Registro y desregistro para los eventos de la app (Suspending y Resuming)
        private void RegisterForAppEvents()
        {
            //Inicializa los eventos para la suspencion y reanudación de la app
            appSuspendEventHandler = new SuspendingEventHandler(Current.OnAppSuspension);
            appResumeEventHandler = new EventHandler<object>(Current.OnAppResumen);

            //Los eventos son lanzados cuando se cierra la app o se supende
            App.Current.Suspending += appSuspendEventHandler;
            App.Current.Resuming += appResumeEventHandler;

        }
        private void UnregisterFromAppEvents()
        {
            App.Current.Suspending -= appSuspendEventHandler;
            appSuspendEventHandler = null;
            App.Current.Resuming -= appResumeEventHandler;
            appResumeEventHandler = null;
        }

        //Registro y desregistro para los eventos add y remove del DeviceWatcher
        private void RegisterForDeviceWatcherEvents()
        {
            deviceAddedEventHandler = new TypedEventHandler<DeviceWatcher, DeviceInformation>(this.OnDeviceAdd);
            deviceWatcher.Added += deviceAddedEventHandler;

            deviceRemovedEventHandler = new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(this.OnDeviceRemove);
            deviceWatcher.Removed += deviceRemovedEventHandler;
        }
        private void UnregisterFromDeviceWatcherEvents()
        {
            deviceWatcher.Added -= deviceAddedEventHandler;
            deviceAddedEventHandler = null;

            deviceWatcher.Removed -= deviceRemovedEventHandler;
            deviceRemovedEventHandler = null;
        }

        /// <summary>
        /// Listen for any changed in device access permission. The user can block access to the device while the device is in use.
        /// If the user blocks access to the device while the device is opened, the device's handle will be closed automatically by
        /// the system; it is still a good idea to close the device explicitly so that resources are cleaned up.
        /// 
        /// Note that by the time the AccessChanged event is raised, the device handle may already be closed by the system.
        /// </summary>
        private void RegisterForDeviceAccessStatusChange()
        {
            // Enable the following registration ONLY if the Serial device under test is non-internal.
            //

            //deviceAccessInformation = DeviceAccessInformation.CreateFromId(deviceInformation.Id);
            //deviceAccessEventHandler = new TypedEventHandler<DeviceAccessInformation, DeviceAccessChangedEventArgs>(this.OnDeviceAccessChanged);
            //deviceAccessInformation.AccessChanged += deviceAccessEventHandler;
        }
        private void UnregisterFromDeviceAccessStatusChange()
        {
            deviceAccessInformation.AccessChanged -= deviceAccessEventHandler;

            deviceAccessEventHandler = null;
        }

        //Callbacks para los eventos de la app (Suspending y Resuming)
        private void OnAppSuspension(Object sender, SuspendingEventArgs eventHandler)
        {
            if (watcherStarted)
            {
                watcherSuspended = true;
                StopDeviceWatcher();
            }
            else
            {
                watcherSuspended = false;
            }
        }
        private void OnAppResumen(Object sender, Object args)
        {
            if (watcherSuspended)
            {
                watcherSuspended = false;
                StartDeviceWatcher();
            }
        }

        //Callbacks para los eventos Add y Remove del DeviceWatcher
        private async void OnDeviceAdd(DeviceWatcher device, DeviceInformation deviceInfo)
        {
            if ((deviceInformation != null) && (deviceInfo.Id == deviceInformation.Id)
                && !IsDeviceConnected && isEnabledAutoReconnect)
            {
                await MainPage.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    new DispatchedHandler(async () =>
                    {
                        await OpenDeviceAsync(deviceInformation, deviceSelector);

                        // Any app specific device intialization should be done here because we don't know the state of the device when it is re-enumerated.
                    }));
            }
        }
        private void OnDeviceRemove(DeviceWatcher device, DeviceInformationUpdate deviceInformationUpdate)
        {
            if (IsDeviceConnected && (deviceInformationUpdate.Id == deviceInformation.Id))
            {
                // The main reasons to close the device explicitly is to clean up resources, to properly handle errors,
                // and stop talking to the disconnected device.
                CloseCurrentlyConnectedDevice();
            }
        }

        private async void OnDeviceAccessChanged(DeviceAccessInformation accessInformation, DeviceAccessChangedEventArgs eventArgs)
        {
            if ((eventArgs.Status == DeviceAccessStatus.DeniedBySystem)
                || (eventArgs.Status == DeviceAccessStatus.DeniedByUser))
            {
                CloseCurrentlyConnectedDevice();
            }
            else if ((eventArgs.Status == DeviceAccessStatus.Allowed) && (deviceInformation != null) && isEnabledAutoReconnect)
            {
                await MainPage.Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    new DispatchedHandler(async () =>
                    {
                        await OpenDeviceAsync(deviceInformation, deviceSelector);

                        // Any app specific device intialization should be done here because we don't know the state of the device when it is re-enumerated.
                    }));
            }
        }

        private void StartDeviceWatcher()
        {
            watcherStarted = true;

            if ((deviceWatcher.Status != DeviceWatcherStatus.Started)
                && (deviceWatcher.Status != DeviceWatcherStatus.EnumerationCompleted))
            {
                deviceWatcher.Start();
            }
        }

        private void StopDeviceWatcher()
        {
            if ((deviceWatcher.Status == DeviceWatcherStatus.Started)
               || (deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted))
            {
                deviceWatcher.Stop();
            }

            watcherStarted = false;
        }

    }
}
