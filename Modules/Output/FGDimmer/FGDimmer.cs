﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vixen.Module.Controller;
using Vixen.Commands;
using System.Windows.Forms;
using System.IO.Ports;
using System.Threading;
using Vixen.Module;

namespace VixenModules.Output.FGDimmer
{
    class FGDimmer : ControllerModuleInstanceBase
    {
        SerialPort _serialPort = null;
        
        int _startChannel;
        float _multiplier;

        Thread _eventThread;
        AutoResetEvent _eventTrigger;
        
        byte[] _channelValues;
        FGDimmerControlModule[] _modules;
        ICommand[] _outputStates;
        byte[][] _packets;
        byte[] _dimmingCurve = new byte[256] {
			0,18,20,23,24,27,28,31,31,33,35,37,39,40,42,43,45,
			46,48,49,50,50,51,52,53,54,54,55,56,56,56,56,56,57,
			57,56,57,58,58,58,59,58,58,58,59,59,59,59,60,61,60,
			61,61,61,61,62,62,62,62,63,63,63,63,63,63,63,63,63,
			63,64,64,64,64,65,65,66,66,67,67,68,68,68,69,69,70,
			70,71,71,72,73,73,74,74,75,75,77,77,77,78,78,79,80,
			80,80,80,81,82,83,83,84,85,85,86,87,88,89,91,92,93,
			94,96,99,101,104,107,109,115,120,135,140,146,148,151,154,156,159,
			161,162,163,164,166,167,168,169,170,170,171,172,172,173,174,175,175,
			175,175,176,177,177,178,178,178,180,180,181,181,182,182,183,184,184,
			185,185,186,186,187,187,187,188,188,189,189,190,190,191,191,191,191,
			192,192,192,192,192,192,192,192,192,192,193,193,193,193,194,194,194,
			194,194,195,195,196,196,196,196,196,197,197,197,197,197,197,198,198,
			198,199,199,199,199,199,199,200,201,201,202,203,204,205,205,206,207,
			209,210,212,213,215,216,218,220,222,224,224,227,228,231,232,235,237,
			255};

        bool _holdPort = true;
        bool _acOperation = false;
        bool _running = false;

        FGDimmerData _moduleData;
        CommandHandler _commandHandler;

        public FGDimmer()
        {
            _commandHandler = new CommandHandler();
            DataPolicyFactory = new FGDimmerDataPolicyFactory();

            _packets = new byte[][]
            {
                new byte[34],
                new byte[34],
                new byte[34],
                new byte[34]
            };

            for (int i = 0; i < 4; i++)
            {
                _packets[i][0] = 0x55;
                _packets[i][1] = (byte)(i + 1);

            }
                _modules = new FGDimmerControlModule[4];
                for (int i = 0; i < 4; i++)
                {
                    _modules[i] = new FGDimmerControlModule(i + 1);
                }

                _eventThread = new Thread(new ThreadStart(EventThread));
                _eventTrigger = new AutoResetEvent(false);
                _multiplier = (float)100 / 255;
        }

        public override bool HasSetup
        {
            get
            {
                return true;
            }
        }

        public override bool Setup()
        {
            using (SetupDialog setupDialog = new SetupDialog(_moduleData))
            {
                if (setupDialog.ShowDialog() == DialogResult.OK)
                {
                    _serialPort = setupDialog.SelectedPort;
                    _acOperation = setupDialog.ACOperation;
                    _holdPort = setupDialog.HoldPort;

                    _moduleData.PortName = setupDialog.SelectedPort.PortName;
                    _moduleData.BaudRate = setupDialog.SelectedPort.BaudRate;
                    _moduleData.Parity = setupDialog.SelectedPort.Parity;
                    _moduleData.DataBits = setupDialog.SelectedPort.DataBits;
                    _moduleData.StopBits = setupDialog.SelectedPort.StopBits;
                    _moduleData.HoldPortOpen = setupDialog.HoldPort;
                    _moduleData.AcOperation = setupDialog.ACOperation;

                    for (int i = 0; i < 4; i++)
                    {
                        _modules[i] = setupDialog.Modules[i];
                    }
                    _moduleData.Modules = _modules;
                    return true;
                }
            }
            return false;
        }
        public override void UpdateState(int chainIndex, Vixen.Commands.ICommand[] outputStates)
        {
            _outputStates = outputStates;
            
            if (serialPortIsValid)
            {
                if (_holdPort)
                {
                    _eventTrigger.Set();
                }
                else
                {
                    if (!_serialPort.IsOpen)
                    {
                        _serialPort.Open();
                    }
                    FireEvent();
                    _serialPort.Close();
                }
            }
        }
        public override void Start()
        {
            foreach (byte[] packet in _packets)
            {
                Array.Clear(packet, 2, packet.Length - 2);
            }
            if (_holdPort)
            {
                if (!_serialPort.IsOpen)
                {
                    _serialPort.Open();
                }
                _running = true;
                _eventThread.Start();
            }
        }

        public override void Stop()
        {
            if (_running)
            {
                _running = false;
                _eventTrigger.Set();
                //give it one second to terminate.
                //Trying to avoid closing the port while the thread finishes up.
                _eventThread.Join(1000);
            }

            dropExistingSerialPort();
        }

        public override IModuleDataModel ModuleData
        {
            get
            {
                return _moduleData;
            }
            set
            {
                _moduleData = (FGDimmerData)value;
                initModule();
            }
        }

        private void initModule()
        {
            dropExistingSerialPort();
            createSerialPortFromData();

            //since the controlers are sending out bytes the array starts at 0
            //and vixen starts at 1, so we need to subtract 1 to get the true start
            //channel for the controller.
            if (_moduleData.StartChannel != 0)
            {
                _startChannel = _moduleData.StartChannel - 1;
            }
            else
            {
                _startChannel = _moduleData.StartChannel;
            }

            if ( OutputCount == 0 && _moduleData.EndChannel == 0)
            {
                //set this to a max of 128 assuming everybody is using all 4 controllers.
                _moduleData.EndChannel = 128;
            }
            _channelValues = new byte[_moduleData.EndChannel];


            if (_moduleData.Modules != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (_moduleData.Modules[i] != null)
                    {
                        _modules[i].Enabled = _moduleData.Modules[i].Enabled;
                        if (_modules[i].Enabled)
                        {
                            _modules[i].StartChannel = _moduleData.Modules[i].StartChannel;
                        }
                    }
                }

            }
            else
            {
                //hasn't been configured yet so lets set some defaults.
                _moduleData.Modules = _modules;
            }
            _holdPort = _moduleData.HoldPortOpen;
            _acOperation = _moduleData.AcOperation;

            if (serialPortIsValid && IsRunning)
            {
                _serialPort.Open();
            }
        }

        private void dropExistingSerialPort()
        {
            if (serialPortIsValid)
            {
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        private void createSerialPortFromData()
        {
            if (_moduleData.IsValid)
            {
                _serialPort = new SerialPort(
                    _moduleData.PortName,
                    _moduleData.BaudRate,
                    _moduleData.Parity,
                    _moduleData.DataBits,
                    _moduleData.StopBits);

            }
            else
            {
                //create a new serial port with defaults cause we dont' have one
                _serialPort = new SerialPort("COM1", 115200, Parity.None, 8, StopBits.One);
            }

            if (_serialPort != null)
            {
                _serialPort.Handshake = Handshake.None;
                _serialPort.Encoding = Encoding.UTF8;
            }
        }

        private bool serialPortIsValid
        {
            get { return _serialPort != null; }
        }

        private void EventThread()
        {
            while (_running)
            {
                _eventTrigger.WaitOne();
                if (!_running)
                {
                    break;
                }
                FireEvent();
            }
        }

        private void FireEvent()
        {
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }

            if (_acOperation)
            {
                //with dimming curve translation
                for (int i = 0; i < _channelValues.Length; i++)
                {
                    _commandHandler.Reset();
                    ICommand command = _outputStates[i];
                    if (command != null)
                    {
                        command.Dispatch(_commandHandler);
                    }
                    if (_channelValues != null)
                    {
                        _channelValues[i] = _commandHandler.Value;
                        _channelValues[i] = (byte)(_dimmingCurve[_channelValues[i]] * _multiplier + 100);
                    }
                }
            }
            else
            {
                //no dimming curve
                for (int i = 0; i < _outputStates.Length; i++)
                {
                    _commandHandler.Reset();
                    ICommand command = _outputStates[i];
                    if (command != null)
                    {
                        command.Dispatch(_commandHandler);
                    }
                    if (_channelValues != null)
                    {
                        _channelValues[i] = _commandHandler.Value;
                        _channelValues[i] = (byte)(_channelValues[i] * _multiplier + 100);
                    }
                }
            }

            //Distribute the data
            int count = 0;
            FGDimmerControlModule module = null;
            for (int i = 0; i < 4; i++)
            {
                module = _modules[i];
                if (!module.Enabled)
                {
                    continue;
                }

                // get the max number of bytes that will be copied from the data -1
                // because module.StartChannel starts at 1 and _startChannel starts at 0
                // and is the start channel for the data to be sent to this plugin
                count = Math.Min(32, _outputStates.Length - (module.StartChannel - 1 - _startChannel));
                
                //Copy the data to the module's packet
                //but we need to check and see if the _channelValues are null first.
                if (_channelValues != null)
                {
                        Array.Copy(_channelValues, module.StartChannel - 1 - _startChannel, _packets[i], 2, count);
                        //update the hardware
                        _serialPort.Write(_packets[i], 0, _packets[i].Length);

                        //Console.WriteLine(Encoding.Default.GetString(_packets[i]));
                }
            }
        }
    }
}
