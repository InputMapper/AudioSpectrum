using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Un4seen.Bass;
using Un4seen.BassWasapi;
using ODIF;
using ODIF.Extensions;

namespace AudioSpectrum
{
    [PluginInfo(
    PluginName = "Audio Spectrum",
    PluginDescription = "",
    PluginID = 48,
    PluginAuthorName = "InputMapper",
    PluginAuthorEmail = "jhebbel@gmail.com",
    PluginAuthorURL = "http://inputmapper.com",
    PluginIconPath = @"pack://application:,,,/AudioSpectrum;component/Resources/audio.png"
    )]
    public class AudioSpectrum : InputDevicePlugin, pluginSettings
    {
        Setting selectedDevice,channelCount;
        public AudioSpectrum()
        {
            Init();
        }

        public SettingGroup settings
        {
            get; set;
        }

        // initialization
        private void Init()
        {
            settings = new SettingGroup("Audio Spectrum Settings", "");

            selectedDevice = new Setting("Selected Device", "", SettingControl.Dropdown, SettingType.Text, "");
            selectedDevice.configuration["options"] = new List<string>();
            settings.settings.Add(selectedDevice);

            channelCount = new Setting("Channels", "", SettingControl.Numeric, SettingType.Integer, 8);
            channelCount.configuration["interval"] = 1;
            settings.settings.Add(channelCount);

            bool result = false;

            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATETHREADS, false);
            result = Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
            
            if (!result) throw new Exception("Init Error");

            for (int i = 0; i < BassWasapi.BASS_WASAPI_GetDeviceCount(); i++)
            {
                var device = BassWasapi.BASS_WASAPI_GetDeviceInfo(i);
                if (device.IsEnabled && device.IsLoopback)
                {
                    //AudioSpectrumDevice audioDevice = new AudioSpectrumDevice(device,i);
                    //this.Devices.Add(audioDevice);
                    selectedDevice.configuration["options"].Add(string.Format("{0} - {1}", i, device.name));
                }
                //Console.WriteLine(string.Format("{0} - {1}", i, device.name));
            }
            settings.loadSettings();
            if (!string.IsNullOrWhiteSpace(selectedDevice.settingValue))
            {
                var str = (selectedDevice.settingValue as string);
                var array = str.Split(' ');
                int devindex = Convert.ToInt32(array[0]);

                var myDevice = BassWasapi.BASS_WASAPI_GetDeviceInfo(devindex);
                AudioSpectrumDevice audioDevice = new AudioSpectrumDevice(myDevice, devindex, (int)Math.Floor((double)channelCount.settingValue));
                this.Devices.Add(audioDevice);
            }
            selectedDevice.PropertyChanged += SelectedDevice_PropertyChanged;

        }

        private void SelectedDevice_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(selectedDevice.settingValue))
            {
                foreach (var dev in Devices)
                    dev.Dispose();
                this.Devices.Clear();
                var str = (selectedDevice.settingValue as string);
                var array = str.Split(' ');
                int devindex = Convert.ToInt32(array[0]);

                var myDevice = BassWasapi.BASS_WASAPI_GetDeviceInfo(devindex);
                AudioSpectrumDevice audioDevice = new AudioSpectrumDevice(myDevice, devindex, (int)Math.Floor((double)channelCount.settingValue));
                this.Devices.Add(audioDevice);
            }
        }
        protected override void Dispose(bool disposing)
        {
            settings.saveSettings();
            Bass.BASS_Free();
            
            base.Dispose(disposing);
        }
    }

    public class AudioSpectrumDevice : InputDevice
    {
        private bool _enable;               //enabled status
        private DispatcherTimer _t;         //timer that refreshes the display
        private float[] _fft;               //buffer for fft data
        private WASAPIPROC _process;        //callback function to obtain data
        private int _lastlevel;             //last output level
        private int _hanctr;                //last output level counter
        private List<double> _spectrumdata;   //spectrum data buffer
        private bool _initialized;          //initialized flag
        private int devindex;               //used device index

        private int _lines = 16;            // number of spectrum lines

        private BASS_WASAPI_DEVICEINFO device;
        private int i;

        public AudioSpectrumDevice(BASS_WASAPI_DEVICEINFO device, int devindex, int channels)
        {
            _lines = channels;
            for (int i = 0; i < channels; i++)
            {
                InputChannelTypes.JoyAxis channel = new InputChannelTypes.JoyAxis("Channel "+(i+1).ToString(), "") { min_Value = 0, max_Value = 1 };
                InputChannels.Add(channel);
            }

            this.devindex = devindex;
            this.device = device;
            this.DeviceName = device.name;
            this.StatusIcon = Properties.Resources.audio.ToImageSource();
            _fft = new float[1024];
            _lastlevel = 0;
            _hanctr = 0;
            _t = new DispatcherTimer();
            _t.Tick += _t_Tick;
            _t.Interval = TimeSpan.FromMilliseconds(25); //40hz refresh rate
            //_t.IsEnabled = true;
            _process = new WASAPIPROC(Process);
            _spectrumdata = new List<double>();
            _initialized = false;


            //var str = (_devicelist.Items[_devicelist.SelectedIndex] as string)
            //            var array = str.Split(' ');
            //devindex = Convert.ToInt32(array[0]);
            bool result = BassWasapi.BASS_WASAPI_Init(devindex, 0, 0,
                                                      BASSWASAPIInit.BASS_WASAPI_BUFFER,
                                                      1f, 0.05f,
                                                      _process, IntPtr.Zero);
            if (!result)
            {
                var error = Bass.BASS_ErrorGetCode();
                MessageBox.Show(error.ToString());
            }
            else
            {
                _initialized = true;
            }
            BassWasapi.BASS_WASAPI_Start();
            _t.IsEnabled = true;
            _t.Start();
        }
        private double maxy = 0d;
        private void _t_Tick(object sender, EventArgs e)
        {
            // get fft data. Return value is -1 on error
            int ret = BassWasapi.BASS_WASAPI_GetData(_fft, (int)BASSData.BASS_DATA_FFT2048);
            if (ret < 0) return;
            int x;
            double y;
            int b0 = 0;

            //computes the spectrum data, the code is taken from a bass_wasapi sample.
            for (x = 0; x < _lines; x++)
            {
                float peak = 0;
                int b1 = (int)Math.Pow(2, x * 10.0 / (_lines - 1));
                if (b1 > 1023) b1 = 1023;
                if (b1 <= b0) b1 = b0 + 1;
                for (; b0 < b1; b0++)
                {
                    if (peak < _fft[1 + b0]) peak = _fft[1 + b0];
                }
                y = (Math.Sqrt(peak));

                InputChannels[x].Value = y;
            }



            int level = BassWasapi.BASS_WASAPI_GetLevel();
            //_l.Value = Utils.LowWord32(level);
            //_r.Value = Utils.HighWord32(level);
            //if (level == _lastlevel && level != 0) _hanctr++;
            _lastlevel = level;

            //Required, because some programs hang the output. If the output hangs for a 75ms
            //this piece of code re initializes the output
            //so it doesn't make a gliched sound for long.
            if (_hanctr > 3)
            {
                _hanctr = 0;
                //_l.Value = 0;
                //_r.Value = 0;
                Free();
                Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
                _initialized = false;
                //Enable = true;
            }
        }
        // WASAPI callback, required for continuous recording
        private int Process(IntPtr buffer, int length, IntPtr user)
        {
            return length;
        }

        //cleanup
        public void Free()
        {
            BassWasapi.BASS_WASAPI_Stop(true);
            BassWasapi.BASS_WASAPI_Free();
            //Bass.BASS_Free();
        }
        protected override void Dispose(bool disposing)
        {
            Free();
            base.Dispose(disposing);
        }
    }
}