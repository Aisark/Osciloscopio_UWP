using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Devices.SerialCommunication;
using Windows.Devices.Enumeration;
using System.Collections.ObjectModel;
using OsciloscopioUWP.Modules;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml.Media;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using LiveCharts;
using LiveCharts.Uwp;
using System.Diagnostics;
using System.Runtime.InteropServices;

// La plantilla de elemento Página en blanco está documentada en https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0xc0a

namespace OsciloscopioUWP
{
    /// <summary>
    /// Página vacía que se puede usar de forma independiente o a la que se puede navegar dentro de un objeto Frame.
    /// </summary>
    public sealed partial class MainPage : Page, IDisposable
    {
        private const String ButtonNameDisconnectFromDevice = "Desconectar Dispositivo";
        private const String ButtonNameDisableReconnectToDevice = "No conectar hasta que este cerrado";

        //Elementos de la grafica
        private ChartValues<int> ValuesFromRead;
        public SeriesCollection seriesCollection { get; set; }
        private Object ValuesObject = new Object();

        //Timer
        private DispatcherTimer timer;
        
        //Tokens de cancelación
        private bool isReadTimeTokenSource = false;
        private CancellationTokenSource ReadCancellationTokenSource;
        private Object ReadLockObject = new Object();
        
        private CancellationTokenSource WriteCancellationTokenSource;
        private Object WriteLockObject = new Object();

        DataReader DataReaderObject = null;
        DataWriter DataWriteObject = null;

        private SuspendingEventHandler appSuspendEventHandler;
        private EventHandler<Object> appResumeEventHandler;

        private ObservableCollection<DeviceListEntry> listOfDevices;
        private Dictionary<DeviceWatcher, String> mapDeviceWatchersToDeviceSelector;
        private Boolean watchersSuspended;
        private Boolean watchersStarted;

        // Has all the devices enumerated by the device watcher?
        private Boolean isAllDevicesEnumerated;

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;
            timer = new DispatcherTimer();
            listOfDevices = new ObservableCollection<DeviceListEntry>();
            mapDeviceWatchersToDeviceSelector = new Dictionary<DeviceWatcher, String>();
            watchersStarted = false;
            watchersSuspended = false;
            isAllDevicesEnumerated = false;
            ValuesFromRead = new ChartValues<int>();
            seriesCollection = new SeriesCollection
            {
                new LineSeries
                {
                    Values = ValuesFromRead,
                    PointGeometry = null,
                    Fill = new SolidColorBrush(Windows.UI.Colors.Transparent)
                }
                
            };
            DataContext = this;

        }

        public static MainPage Current;
       
        private void Page_Loading(FrameworkElement sender, object args)
        {
            // If we are connected to the device or planning to reconnect, we should disable the list of devices
            // to prevent the user from opening a device without explicitly closing or disabling the auto reconnect
            if (EventHandlerForDevice.Current.IsDeviceConnected
                || (EventHandlerForDevice.Current.IsEnabledAutoReconnect
                && EventHandlerForDevice.Current.DeviceInformation != null))
            {
                UpdateConnectDisconnectButtonsAndList(false);

                // These notifications will occur if we are waiting to reconnect to device when we start the page
                EventHandlerForDevice.Current.OnDeviceConnected = this.OnDeviceConnected;
                EventHandlerForDevice.Current.OnDeviceClose = this.OnDeviceClosing;
            }
            else
            {
                UpdateConnectDisconnectButtonsAndList(true);
            }           

            if (EventHandlerForDevice.Current.Device != null)
            {
                ResetReadCancellationTokenSource();
                ResetWriteCancellationTokenSource();
            }

            //OsciloscopioControls.Visibility = Visibility.Collapsed;

            // Begin watching out for events
            StartHandlingAppEvents();

            // Initialize the desired device watchers so that we can watch for when devices are connected/removed
            InitializeDeviceWatchers();
            StartDeviceWatchers();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += ReadTimer;
        }

        private async void ConectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selections = DeviceList.SelectedItems;
            DeviceListEntry entry = null;

            if (selections.Count>0)
            {
                ConectedButton.IsEnabled = false;
                var obj = selections[0];
                entry = (DeviceListEntry)obj;

                if (entry!=null)
                {
                    // Create an EventHandlerForDevice to watch for the device we are connecting to
                    EventHandlerForDevice.CreateNewEventHandlerForDevice();

                    // Get notified when the device was successfully connected to or about to be closed
                    EventHandlerForDevice.Current.OnDeviceConnected = this.OnDeviceConnected;
                    EventHandlerForDevice.Current.OnDeviceClose = this.OnDeviceClosing;
                    
                    // It is important that the FromIdAsync call is made on the UI thread because the consent prompt, when present,
                    // can only be displayed on the UI thread. Since this method is invoked by the UI, we are already in the UI thread.
                    Boolean openSuccess = await EventHandlerForDevice.Current.OpenDeviceAsync(entry.DeviceInformation, entry.DeviceSelector);
                                        
                }

            }
            else
            {
                NotifyUser("Conecte un dispositivo",NotifyType.NoDeviceConected);
            }

        }

        private async void DisconectButton_Click(object sender, RoutedEventArgs e)
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
            }
            CancelAllIoTasks();
            ValuesFromRead.Clear();
            await WriteReadTaskAsync("s");
            Disconect();
        }

        private void Disconect()
        {
            var selection = DeviceList.SelectedItems;
            DeviceListEntry entry = null;

            // Prevent auto reconnect because we are voluntarily closing it
            // Re-enable the ConnectDevice list and ConnectToDevice button if the connected/opened device was removed.
            EventHandlerForDevice.Current.IsEnabledAutoReconnect = false;

            if (selection.Count > 0)
            {
                var obj = selection[0];
                entry = (DeviceListEntry)obj;

                if (entry != null)
                {
                    EventHandlerForDevice.Current.CloseDevice();
                }
            }

            UpdateConnectDisconnectButtonsAndList(true);
        }

        private void InitializeDeviceWatchers()
        {
            string deviceSelector = SerialDevice.GetDeviceSelectorFromUsbVidPid(ArduinoDevice.Vid,ArduinoDevice.Pid);

            var deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
            AddDeviceWatcher(deviceWatcher, deviceSelector);

        }

        private void StartHandlingAppEvents()
        {
            appSuspendEventHandler = new SuspendingEventHandler(this.OnAppSuspension);
            App.Current.Suspending += appSuspendEventHandler;

            appResumeEventHandler = new EventHandler<Object>(this.OnAppResumen);
            App.Current.Resuming += appResumeEventHandler;
        }

        private void StopHandlingAppEvents()
        {
            App.Current.Suspending -= appSuspendEventHandler;
            App.Current.Resuming -= appResumeEventHandler;
        }

        /// <summary>
        /// We must stop the DeviceWatchers because device watchers will continue to raise events even if
        /// the app is in suspension, which is not desired (drains battery). We resume the device watcher once the app resumes again.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="eventArgs"></param>
        private async  void OnAppSuspension(Object sender, SuspendingEventArgs args)
        {
            if (timer.IsEnabled)
            {
                timer.Stop();
            }

            CancelAllIoTasks();
            if (EventHandlerForDevice.Current.Device!=null)
            {
                await WriteReadTaskAsync("s");
                EventHandlerForDevice.Current.CloseCurrentlyConnectedDevicePublic();
            }


            if (watchersStarted)
            {
                watchersSuspended = true;
                StopDeviceWatchers();
            }
            else
            {
                watchersSuspended = false;
            }
        }

        /// <summary>
        /// See OnAppSuspension for why we are starting the device watchers again
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void OnAppResumen(Object sender, Object args)
        {
            if (watchersSuspended)
            {
                watchersSuspended = false;
                StartDeviceWatchers();
            }
        }

        /// <summary>
        /// Registers for Added, Removed, and Enumerated events on the provided deviceWatcher before adding it to an internal list.
        /// </summary>
        /// <param name="deviceWatcher"></param>
        /// <param name="deviceSelector">The AQS used to create the device watcher</param>
        private void AddDeviceWatcher(DeviceWatcher deviceWatcher, String deviceSelector)
        {
            deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(this.OnDeviceAdded);
            deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(this.OnDeviceRemoved);
            deviceWatcher.EnumerationCompleted += new TypedEventHandler<DeviceWatcher, Object>(this.OnDeviceEnumerationComplete);

            mapDeviceWatchersToDeviceSelector.Add(deviceWatcher, deviceSelector);
        }
        /// <summary>
        /// We will remove the device from the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformationUpdate"></param>
        private async void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInformationUpdate)
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(async () =>
                {
                    if (timer.IsEnabled)
                    {
                        timer.Stop();
                    }
                    if (ReadCancellationTokenSource!=null)
                    {
                        CancelReadTask();
                    }
                    if (WriteCancellationTokenSource!=null)
                    {
                        CancelWriteTask();
                    }

                    var device = await DeviceInformation.CreateFromIdAsync(deviceInformationUpdate.Id);
                    NotifyUser(device.Name + " se ha removido", NotifyType.DeviceRemoved);

                    RemoveDeviceFromList(deviceInformationUpdate.Id);
                }));
        }

        /// <summary>
        /// This function will add the device to the listOfDevices so that it shows up in the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(() =>
                {
                                        
                    NotifyUser(deviceInformation.Name +" se ha añadido", NotifyType.StatusMessage);

                    AddDeviceToList(deviceInformation, mapDeviceWatchersToDeviceSelector[sender]);
                }));
        }

        /// <summary>
        /// Notify the UI whether or not we are connected to a device
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void OnDeviceEnumerationComplete(DeviceWatcher sender, Object args)
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(() =>
                {
                    isAllDevicesEnumerated = true;

                    // If we finished enumerating devices and the device has not been connected yet, the OnDeviceConnected method
                    // is responsible for selecting the device in the device list (UI); otherwise, this method does that.
                    if (EventHandlerForDevice.Current.IsDeviceConnected)
                    {
                        //OsciloscopioControls.Visibility = Visibility.Visible;

                        SelectDeviceInList(EventHandlerForDevice.Current.DeviceInformation.Id);

                        DisconectButton.Content = ButtonNameDisconnectFromDevice;

                        NotifyUser("Conectado a - " +
                                            EventHandlerForDevice.Current.DeviceInformation.Name, NotifyType.StatusMessage);

                        EventHandlerForDevice.Current.ConfigureCurrentlyConnectedDevice();
                    }
                    else if (EventHandlerForDevice.Current.IsEnabledAutoReconnect && EventHandlerForDevice.Current.DeviceInformation != null)
                    {
                        // We will be reconnecting to a device
                        DisconectButton.Content = ButtonNameDisableReconnectToDevice;

                        NotifyUser("Esperadon para conectar a -  " + EventHandlerForDevice.Current.DeviceInformation.Name, NotifyType.StatusMessage);
                    }
                    else
                    {
                        if (DeviceList.Items.Count>0)
                        {
                            NotifyUser("Conecte Dispositivo", NotifyType.WaitDevice);
                        }
                        else
                        {
                            NotifyUser("No hay dispositivo conectado", NotifyType.NoDeviceConected);
                        }
                        
                    }
                }));
        }

        /// <summary>
        /// Starts all device watchers including ones that have been individually stopped.
        /// </summary>
        private void StartDeviceWatchers()
        {
            // Start all device watchers
            watchersStarted = true;
            isAllDevicesEnumerated = false;

            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status != DeviceWatcherStatus.Started)
                    && (deviceWatcher.Status != DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Start();
                }
            }
        }

        /// <summary>
        /// Stops all device watchers.
        /// </summary>
        private void StopDeviceWatchers()
        {
            // Stop all device watchers
            foreach (DeviceWatcher deviceWatcher in mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status == DeviceWatcherStatus.Started)
                    || (deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Stop();
                }
            }

            // Clear the list of devices so we don't have potentially disconnected devices around
            ClearDeviceEntries();

            watchersStarted = false;
        }

        /// <summary>
        /// Creates a DeviceListEntry for a device and adds it to the list of devices in the UI
        /// </summary>
        /// <param name="deviceInformation">DeviceInformation on the device to be added to the list</param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        private void AddDeviceToList(DeviceInformation deviceInformation, String deviceSelector)
        {
            // search the device list for a device with a matching interface ID
            var match = FindDevice(deviceInformation.Id);

            // Add the device if it's new
            if (match == null)
            {
                // Create a new element for this device interface, and queue up the query of its
                // device information
                match = new DeviceListEntry(deviceInformation, deviceSelector);

                // Add the new element to the end of the list of devices
                listOfDevices.Add(match);
            }
        }

        private void RemoveDeviceFromList(String deviceId)
        {
            // Removes the device entry from the interal list; therefore the UI
            var deviceEntry = FindDevice(deviceId);

            listOfDevices.Remove(deviceEntry);
        }

        private void ClearDeviceEntries()
        {
            listOfDevices.Clear();
        }

        private DeviceListEntry FindDevice(String deviceId)
        {
            if (deviceId != null)
            {
                foreach (DeviceListEntry entry in listOfDevices)
                {
                    if (entry.DeviceInformation.Id == deviceId)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// If all the devices have been enumerated, select the device in the list we connected to. Otherwise let the EnumerationComplete event
        /// from the device watcher handle the device selection
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private async void OnDeviceConnected(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            // Find and select our connected device
            if (isAllDevicesEnumerated)
            {
                SelectDeviceInList(EventHandlerForDevice.Current.DeviceInformation.Id);

                DisconectButton.Content = ButtonNameDisconnectFromDevice;
            }
            NotifyUser("Conectado a - " +
                                EventHandlerForDevice.Current.DeviceInformation.Name, NotifyType.StatusMessage);

            if (EventHandlerForDevice.Current.Device != null)
            {
                EventHandlerForDevice.Current.ConfigureCurrentlyConnectedDevice();
                ResetReadCancellationTokenSource();
                ResetWriteCancellationTokenSource();
                await WriteReadTaskAsync("square");
            }

            
        }

        /// <summary>
        /// The device was closed. If we will autoreconnect to the device, reflect that in the UI
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private async void OnDeviceClosing(EventHandlerForDevice sender, DeviceInformation deviceInformation)
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                new DispatchedHandler(() =>
                {
                    // We were connected to the device that was unplugged, so change the "Disconnect from device" button
                    // to "Do not reconnect to device"
                    if (DisconectButton.IsEnabled && EventHandlerForDevice.Current.IsEnabledAutoReconnect)
                    {
                        DisconectButton.Content = ButtonNameDisableReconnectToDevice;
                    }
                    ResetReadCancellationTokenSource();
                    ResetWriteCancellationTokenSource();
                }));
        }

        /// <summary>
        /// Selects the item in the UI's listbox that corresponds to the provided device id. If there are no
        /// matches, we will deselect anything that is selected.
        /// </summary>
        /// <param name="deviceIdToSelect">The device id of the device to select on the list box</param>
        private void SelectDeviceInList(String deviceIdToSelect)
        {
            // Don't select anything by default.
            DeviceList.SelectedIndex = -1;

            for (int deviceListIndex = 0; deviceListIndex < listOfDevices.Count; deviceListIndex++)
            {
                if (listOfDevices[deviceListIndex].DeviceInformation.Id == deviceIdToSelect)
                {
                    DeviceList.SelectedIndex = deviceListIndex;

                    break;
                }
            }
        }

        /// <summary>
        /// When ButtonConnectToDevice is disabled, ConnectDevices list will also be disabled.
        /// </summary>
        /// <param name="enableConnectButton">The state of ButtonConnectToDevice</param>
        private void UpdateConnectDisconnectButtonsAndList(Boolean enableConnectButton)
        {
            ConectedButton.IsEnabled = enableConnectButton;
            DisconectButton.IsEnabled = !ConectedButton.IsEnabled;

            DeviceList.IsEnabled = ConectedButton.IsEnabled;
        }

        /// <summary>
        /// Display a message to the user.
        /// This method may be called from any thread.
        /// </summary>
        /// <param name="strMessage"></param>
        /// <param name="type"></param>
        internal void NotifyUser(string strMessage, NotifyType type)
        {
            // If called from the UI thread, then update immediately.
            // Otherwise, schedule a task on the UI thread to perform the update.
            if (Dispatcher.HasThreadAccess)
            {
                UpdateStatus(strMessage, type);
            }
            else
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateStatus(strMessage, type));
            }
        }

        private void UpdateStatus(string strMessage, NotifyType type)
        {
            switch (type)
            {
                case NotifyType.StatusMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Green);
                    break;
                case NotifyType.ErrorMessage:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Red);
                    break;
                case NotifyType.NoDeviceConected:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.OrangeRed);
                    break;
                case NotifyType.DeviceRemoved:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Orange);
                    break;
                case NotifyType.WaitDevice:
                    StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.CadetBlue);
                    break;
            }

            txtStatus.Text = strMessage;

            // Collapse the StatusBlock if it has no text to conserve real estate.
            StatusBorder.Visibility = (txtStatus.Text != String.Empty) ? Visibility.Visible : Visibility.Collapsed;
            if (txtStatus.Text != String.Empty)
            {
                StatusBorder.Visibility = Visibility.Visible;
                txtStatus.Visibility = Visibility.Visible;
            }
            else
            {
                StatusBorder.Visibility = Visibility.Collapsed;
                txtStatus.Visibility = Visibility.Collapsed;
            }
        }

        public void Dispose()
        {
            if (ReadCancellationTokenSource != null)
            {
                ReadCancellationTokenSource.Dispose();
                ReadCancellationTokenSource = null;
            }
            if (WriteCancellationTokenSource != null)
            {
                WriteCancellationTokenSource.Dispose();
                WriteCancellationTokenSource = null;
            }

        }

        private void ReadDataButton_Click(object sender, RoutedEventArgs e)
        {
            //await WriteReadTaskAsync("r");

            if ((bool)ReadDataButton.IsChecked)
            {
                timer.Start();
                NotifyUser("Graficando ...", NotifyType.StatusMessage);
            }
            else
            {
                timer.Stop();
                CancelAllIoTasks();
                //count = 0;
                NotifyUser("Graficacion Detenida", NotifyType.NoDeviceConected);
            }
        }

        private async Task WriteReadTaskAsync(String comando)
        {
            if (EventHandlerForDevice.Current.Device != null)
            {
                bool isSendConfig = await ConfigureComandWriteAsync(comando);
                if (comando != "s")
                {
                    if (isSendConfig)
                    {
                        await ConfigureCommandReadAsync(comando);
                    }
                }
            }

        }

        private async Task WriteAsync(CancellationToken token)
        {
            Task<UInt32> storeAsyncTask;

            lock (WriteLockObject)
            {
                token.ThrowIfCancellationRequested();

                storeAsyncTask = DataWriteObject.StoreAsync().AsTask(token);
            }

            UInt32 storewrite = await storeAsyncTask;
        }

        //int count = 0;
        private async Task ReadAsync(CancellationToken token)
        {
            
            Task<UInt32> loadAsyncTask;
            uint ReadBufferLength = 255;

            lock (ReadLockObject)
            {
                token.ThrowIfCancellationRequested();
                DataReaderObject.InputStreamOptions = InputStreamOptions.ReadAhead;
                loadAsyncTask = DataReaderObject.LoadAsync(ReadBufferLength).AsTask(token);
            }

            UInt32 bytesRead = await loadAsyncTask;

            while (DataReaderObject.UnconsumedBufferLength > 0)
            {
                var read = DataReaderObject.ReadByte();
                int readconvert = (read*5) / 255;
                ValuesFromRead.Add(readconvert);
                //resultado.Text = ((int)read).ToString() + " : " + count;
            }
        }

        private async Task<bool> ConfigureComandWriteAsync(String Command)
        {
            bool isConfigureSend = false;
            try
            {
                DataWriteObject = new DataWriter(EventHandlerForDevice.Current.Device.OutputStream);
                DataWriteObject.WriteString(Command);
                await WriteAsync(WriteCancellationTokenSource.Token);
                isConfigureSend = true;
            }
            catch (OperationCanceledException)
            {
                NotifyUser("Se ha detenido la Escritura de datos", NotifyType.DeviceRemoved);
            }
            catch (Exception exep)
            {
                NotifyUser(exep.Message, NotifyType.ErrorMessage);
            }
            finally
            {
                if (DataWriteObject != null)
                {
                    DataWriteObject.DetachStream();
                    DataWriteObject = null;
                }
               
            }
            return isConfigureSend;
        }       

        private async Task ConfigureCommandReadAsync(String Command)
        {
            try
            {
                DataReaderObject = new DataReader(EventHandlerForDevice.Current.Device.InputStream);
                if (DataReaderObject == null)
                {
                    NotifyUser("Es null", NotifyType.WaitDevice);
                }
                if (Command == "square")
                {
                    ReadCancellationTokenSource.CancelAfter(3000);
                    isReadTimeTokenSource = true;
                    char character = await CheckConectConfigurationAsync(ReadCancellationTokenSource.Token);
                    isReadTimeTokenSource = false;
                    ResetReadCancellationTokenSource();
                    UpdateConnectDisconnectButtonsAndList((character!= 'a'));
                    if (character== 'a')
                    {
                        NotifyUser("Listo para graficar", NotifyType.WaitDevice);
                        OsciloscopioControls.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        
                        Disconect();
                        NotifyUser("El dispositivo no esta configurado", NotifyType.DeviceRemoved);
                        OsciloscopioControls.Visibility = Visibility.Collapsed;
                    }
                }
                else if (Command == "r")
                {
                    await ReadAsync(ReadCancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (isReadTimeTokenSource)
                {
                    UpdateConnectDisconnectButtonsAndList(true);
                    Disconect();
                    NotifyUser("El dispositivo no esta configurado true", NotifyType.ErrorMessage);
                    OsciloscopioControls.Visibility = Visibility.Collapsed;
                    isReadTimeTokenSource = false;
                }
                else
                {
                    NotifyUser("Se ha detenido la lectura", NotifyType.DeviceRemoved);
                }
            }
            catch (COMException)
            {
                NotifyUser("Error Com before Deatch", NotifyType.ErrorMessage);
            }
            catch (Exception exception)
            {
                NotifyUser(exception.Message, NotifyType.ErrorMessage);
                Debug.WriteLine(exception.Message.ToString());
            }            finally
            {
                if (DataReaderObject != null)
                {
                    try
                    {
                        DataReaderObject.DetachStream();
                    }
                    catch(COMException)
                    {
                        NotifyUser("Error Com Deatch",NotifyType.ErrorMessage);
                    }
                    DataReaderObject.Dispose();
                    DataReaderObject = null;
                }
            }
        }

        /// <summary>
        /// Tarea de lectura de caracteres
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<char> CheckConectConfigurationAsync(CancellationToken token)
        {
            Task<UInt32> charAsyncTask;
            char Caracter = 'b';
            uint ReadBufferLength = 8;

            lock (ReadLockObject)
            {
                token.ThrowIfCancellationRequested();
                DataReaderObject.InputStreamOptions = InputStreamOptions.Partial;
                charAsyncTask = DataReaderObject.LoadAsync(ReadBufferLength).AsTask(token);
            }

            UInt32 bytesRead = await charAsyncTask;

            if (bytesRead > 0)
            {
                var caracter = DataReaderObject.ReadByte();
                Caracter = (char)caracter;
                //resultado.Text = Caracter.ToString();
                return Caracter;
            }
            return Caracter;
        }

        /// <summary>
        /// Reinicia el token para cancelar lecturas
        /// </summary>
        private void ResetReadCancellationTokenSource()
        {
            ReadCancellationTokenSource = new CancellationTokenSource();
            //NotifyUser("Lectura detenida",NotifyType.DeviceRemoved);
        }

        private void ResetWriteCancellationTokenSource()
        {
            // Create a new cancellation token source so that can cancel all the tokens again
            WriteCancellationTokenSource = new CancellationTokenSource();

            // Hook the cancellation callback (called whenever Task.cancel is called)
            //WriteCancellationTokenSource.Token.Register(() => NotifyWriteCancelingTask());
        }
        
        private async void ReadTimer(object sender,object e)
        {
            try
            {
                //count++;
                await WriteReadTaskAsync("r");
                if (ValuesFromRead.Count > 50)
                {
                    ValuesFromRead.RemoveAt(0);
                }
            }
            catch (Exception exep)
            {

                NotifyUser(exep.Message, NotifyType.DeviceRemoved);
            }
            
        }

        
        
        private void CancelReadTask()
        {
            lock (ReadLockObject)
            {
                if (ReadCancellationTokenSource != null)
                {
                    if (!ReadCancellationTokenSource.IsCancellationRequested)
                    {
                        ReadCancellationTokenSource.Cancel();

                        // Existing IO already has a local copy of the old cancellation token so this reset won't affect it
                        ResetReadCancellationTokenSource();
                    }
                }
            }
        }
        private void CancelWriteTask()
        {
            lock (WriteLockObject)
            {
                if (WriteCancellationTokenSource != null)
                {
                    if (!WriteCancellationTokenSource.IsCancellationRequested)
                    {
                        WriteCancellationTokenSource.Cancel();

                        // Existing IO already has a local copy of the old cancellation token so this reset won't affect it
                        ResetWriteCancellationTokenSource();
                    }
                }
            }
        }
        private void CancelAllIoTasks()
        {
            CancelReadTask();
            CancelWriteTask();
        }

        private void CartesianChart_Loaded(object sender, RoutedEventArgs e)
        {
            ProgressRingChart.Visibility = Visibility.Collapsed;
        }

        private void voltDiv_TextChanged(object sender, TextChangedEventArgs e)
        {
            //TestTextBlocks.Text = voltDiv.Text;
        }
    }

    public enum NotifyType
    {
        StatusMessage,
        ErrorMessage,
        NoDeviceConected,
        DeviceRemoved,
        WaitDevice
    };
}
