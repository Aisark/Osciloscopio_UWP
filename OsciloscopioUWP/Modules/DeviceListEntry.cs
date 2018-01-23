using System;
using Windows.Devices.Enumeration;

namespace OsciloscopioUWP.Modules
{
    public class DeviceListEntry
    {
        private DeviceInformation device;
        private String deviceSelector;

        public String InstanceId
        {
            get
            {
                return device.Properties[DeviceProperties.DeviceInstanceId] as String;
            }
        }

        public DeviceInformation DeviceInformation
        {
            get
            {
                return device;
            }
        }

        public String DeviceSelector
        {
            get
            {
                return deviceSelector;
            }
        }

        public String InstanceName
        {
            get { return device.Name as String; }
        }

        /// <summary>
        /// The class is mainly used as a DeviceInformation wrapper so that the UI can bind to a list of these.
        /// </summary>
        /// <param name="deviceInformation"></param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        public DeviceListEntry(DeviceInformation deviceInformation, String deviceSelector)
        {
            this.device = deviceInformation;
            this.deviceSelector = deviceSelector;
        }

    }
}
