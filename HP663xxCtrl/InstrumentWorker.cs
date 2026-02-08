using Ivi.Visa;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace HP663xxCtrl 
{
    public class InstrumentWorker 
    {

        //
        // Private Types
        //
        private enum CommandEnum
        {
            IRange,
            Acquire,
            Program,
            ClearProtection,
            Log,
            SetACDCDetector,
            DLFirmware,
            SendTextToDisplay,
            ClearDisplay,
            SetDisplayState,
            SetMeasureWindow,
            RestoreOutState,
            SetOutputComp
        }
        private struct Command
        {
            public CommandEnum cmd;
            public object arg;
        }

        //
        // Public Types
        //
        public struct AcquireDetails
        {
            public int NumPoints;
            public double Interval;
            public SenseModeEnum SenseMode;
            public double Level;
            public double TriggerHysteresis;
            public TriggerSlopeEnum triggerEdge;
            public int SegmentCount;
            public int SampleOffset;
            public MeasWindowType windowType;
            public OutputEnum SelectedChannel;
        }
        public enum StateEnum
        {
            Disconnected,
            Connected,
            ConnectionFailed,
            InitStage,
            Measuring,
            StopMeasuring
        }

        public struct StateEventData
        {
            public StateEnum State;
            public bool HasOutputCompensation;
            public bool HasTwoMeasureChannels;
            public bool HasSeprateEnableChannels;
        }

        //
        // Private Fields
        //
        private IFastSMU dev = null;
        private BlockingCollection<Command> EventQueue;
        private volatile bool StopRequested = false;
        private bool HasOutputCompensation = false;
        private bool HasTwoMeasureChannels = false;
        private bool HasSeprateEnableChannels = false;
        private DateTime LastRefresh;
        private ProgramDetails LastProgramDetails;

        //
        // Public Fields
        //
        public volatile bool InstrumentIsConnected = false;

        public volatile uint refreshDelay_ms = 1000;
        public volatile bool StopAcquireRequested = false;
        public volatile OutputEnum CurrentSelectedChannel = OutputEnum.Output_None;
        
        public event EventHandler WorkerDone;
        public event EventHandler<MeasArray> DataAcquired;
        public event EventHandler<InstrumentState> NewState;
        public event EventHandler<StateEventData> StateChanged;
        public event EventHandler<ProgramDetails> ProgramDetailsReadback;
        public event EventHandler<LoggerDatapoint> LogerDatapointAcquired;
        public string VisaAddress { get; private set; }

        //
        // Private Functions
        //
        private StateEventData GetStateData( StateEnum state )
        {
            return new StateEventData
            {
                State = state,
                HasOutputCompensation = HasOutputCompensation,
                HasTwoMeasureChannels = HasTwoMeasureChannels,
                HasSeprateEnableChannels = HasSeprateEnableChannels,
            };
        }

        private void RefreshDisplay()
        {
            var state = dev.ReadState();
            if (NewState != null)
                NewState(this, state);
        }

        private void DoSetCurrentRange(double range)
        {
            dev.SetCurrentRange(range);
            LastProgramDetails.I1Range = range;
        }

        private void DoDLFirmware(string filename)
        {
            try
            {
                using (BinaryWriter bw = new BinaryWriter(File.Open(filename, FileMode.Create)))
                {
                    for (uint i = 0; i <= 0xFFFF && !StopAcquireRequested; i += 4)
                    {
                        var x = ((HP663xx)dev).GetFirmwareWord(i);
                        foreach (var w in x)
                            bw.Write(w);
                    }
                }
            }
            catch
            {
                // mostly IO exceptions
            }
        }
        private void DoClearProtection()
        {
            dev.ClearProtection();
        }
        private void DoACDCDetector(CurrentDetectorEnum detector)
        {
            dev.SetCurrentDetector(detector);
            LastProgramDetails.Detector = detector;
        }

        private void DoProgram(ProgramDetails details)
        {
            //
            // Disable the output.
            //
            if (!details.Enabled1)
            {
                dev.EnableOutput(OutputEnum.Output_1, false);
            }

            if (!details.Enabled2)
            {
                dev.EnableOutput(OutputEnum.Output_2, false);
            }

            if (!details.Enabled1 || !details.Enabled2)
            {
                dev.SetOCP(details.OCP);
            }

            if (dev.HasOVP)
            {
                dev.SetOVP(details.OVP ? details.OVPVal : double.NaN);
            }

            dev.SetIV(1, details.V1, details.I1);

            if (details.HasOutput2)
            {
                dev.SetIV(2, details.V2, details.I2);
            }

            if (details.Enabled1 || details.Enabled2)
            {
                dev.SetOCP(details.OCP);
            }

            //
            // Re-enable the output.
            //
            if (details.Enabled1)
            {
                dev.EnableOutput(OutputEnum.Output_1, details.Enabled1);
            }

            if (HasSeprateEnableChannels)
            {
                if (details.Enabled2)
                {
                    dev.EnableOutput(OutputEnum.Output_2, details.Enabled2);
                }
            }

            LastRefresh = DateTime.MinValue;

            // Copy element by element to keep old value of detector, etc....
            LastProgramDetails.V1 = details.V1;
            LastProgramDetails.I1 = details.I1;
            LastProgramDetails.V2 = details.V2;
            LastProgramDetails.I2 = details.I2;
            LastProgramDetails.OVP = details.OVP;
            LastProgramDetails.OVPVal = details.OVPVal;
            LastProgramDetails.Enabled1 = details.Enabled1;
            LastProgramDetails.Enabled2 = details.Enabled2;
            LastProgramDetails.OCP = details.OCP;
        }

        // Must set StopAcquireRequested to false before starting acquisition
        private void DoMeasure(AcquireDetails arg)
        {
            if (StateChanged != null)
            {
                StateChanged(this, GetStateData(StateEnum.Measuring));
            }

            int remaining = arg.SegmentCount;
            while (remaining > 0 && !StopRequested && !StopAcquireRequested)
            {

                int count = 0;
                if (arg.triggerEdge == TriggerSlopeEnum.Immediate)
                {
                    count = 1;
                }
                else
                {
                    count = Math.Min(remaining, 4096 / arg.NumPoints);
                }

                dev.StartMeasure(
                    channel: arg.SelectedChannel,
                    mode: arg.SenseMode,
                    numPoints: arg.NumPoints,
                    interval: arg.Interval,
                    triggerEdge: arg.triggerEdge,
                    level: arg.Level,
                    hysteresis: arg.TriggerHysteresis,
                    triggerCount: count,
                    triggerOffset: arg.SampleOffset,
                    windowType: arg.windowType);

                while (!dev.IsMeasurementFinished() && !StopAcquireRequested && !StopRequested)
                {
                    Thread.Sleep(70);
                }

                if (StopAcquireRequested || StopRequested)
                {
                    dev.AbortMeasurement();
                    if (StateChanged != null)
                    {
                        StateChanged(this, GetStateData(StateEnum.Connected));
                    }
                    return;
                }
                var data = dev.EndMeasure(channel: arg.SelectedChannel, mode: arg.SenseMode, triggerCount: count);

                if (DataAcquired != null)
                {
                    DataAcquired(this, data);
                }

                remaining -= count;
            }

            if (StateChanged != null)
            {
                StateChanged(this, GetStateData(StateEnum.Connected));
            }
        }

        private void DoLog(OutputEnum channel, SenseModeEnum mode, double interval = 0)
        {
            if (StateChanged != null)
            {
                StateChanged(this, GetStateData(StateEnum.Measuring));
            }

            dev.StartLogging(channel, mode, interval);

            var hasExitLoop = false;
            while (true)
            {
                if (StopRequested || StopAcquireRequested)
                {
                    hasExitLoop = true;
                    break;
                }

                if (StopAcquireRequested || StopRequested)
                {
                    dev.AbortMeasurement(); // TODO: Why call measure here? Are measure and log stopped the same?=Rename this than StopAcquisition()
                    if (StateChanged != null) StateChanged(this, GetStateData(StateEnum.Connected));
                    return;
                }

                var data = dev.EndLogging(channel, mode);
                if (LogerDatapointAcquired != null)
                {
                    foreach (var p in data)
                    {
                        LogerDatapointAcquired(this, p);
                    }
                }
            }

            if (hasExitLoop)
            {
                if (StateChanged != null)
                {
                    StateChanged(this, GetStateData(StateEnum.StopMeasuring));
                }

                if (dev != null)
                {
                    if (CurrentSelectedChannel != OutputEnum.Output_None)
                    {
                        dev.RestoreOutState(CurrentSelectedChannel);
                        CurrentSelectedChannel = OutputEnum.Output_None;
                    }
                }
            }

            if (StateChanged != null)
            {
                StateChanged(this, GetStateData(StateEnum.Connected));
            }
        }

        //
        // Public functions
        //
        public InstrumentWorker(string address)
        {
            this.VisaAddress = address;
            EventQueue = new BlockingCollection<Command>(new ConcurrentQueue<Command>());
        }
        public void RequestIRange(double range)
        {
            EventQueue.Add(new Command() { cmd = CommandEnum.IRange, arg = range });
        }

        // Must set StopAcquireRequested to false before starting acquisition
        //
        // Also, the returned AcquisitionData structure will have a blank 
        // SamplingPeriod and DataSeries
        //
        public AcquisitionData RequestAcquire(AcquireDetails details)
        {
            AcquisitionData data = new AcquisitionData();
            data.AcqDetails = details;
            data.ProgramDetails = LastProgramDetails;
            data.StartAcquisitionTime = DateTime.Now;

            if (StopAcquireRequested == true)
                return data;
            EventQueue.Add(new Command() {
                cmd = CommandEnum.Acquire,
                arg = details
            });
            return data;
        }
        public void RequestDLFirmware(string filename) 
        {
            if (StopAcquireRequested == true)
            {
                return;
            }
            
            EventQueue.Add(new Command() 
            {
                cmd = CommandEnum.DLFirmware,
                arg = filename
            });
        }

        public void RequestLog(OutputEnum channel,  SenseModeEnum mode, double interval=0) 
        {
            if (StopAcquireRequested == true)
            {
                return;
            }

            EventQueue.Add(new Command() {
                cmd = CommandEnum.Log,
                arg = new object[] {channel,mode,interval}
            });
        }
        public void RequestProgram(ProgramDetails details) 
        {
            EventQueue.Add(new Command() {
                cmd = CommandEnum.Program,
                arg = details
            });
        }

        public void RequestClearProtection() 
        {
            EventQueue.Add(new Command() {
                cmd = CommandEnum.ClearProtection,
                arg = null
            });
        }

        public void RequestShutdown() 
        {
            StopRequested = true;
        }

        public void RequestRestoreOutState(OutputEnum selectedChannel)
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.RestoreOutState,
                arg = selectedChannel
            });

            //
            // Refresh the display labels so we see which channel is 
            // enabled.
            //
            RefreshDisplay();
        }

        public void RequestACDCDetector(CurrentDetectorEnum detector) 
        {
            EventQueue.Add(new Command() {
                cmd = CommandEnum.SetACDCDetector,
                arg = detector
            });
        }

        public void SendTextToDisplay(string text)
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.SendTextToDisplay,
                arg = text
            });
        }

        public void SetDisplayState(DisplayState state)
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.SetDisplayState,
                arg = state
            }) ;
        }

        public void SetOutputComp(OutputCompensationEnum outComp )
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.SetOutputComp,
                arg = outComp
            });
        }

        public void ClearDisplay()
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.ClearDisplay
            });
        }

        public void SetMeasureWindowType(MeasWindowType type)
        {
            EventQueue.Add(new Command()
            {
                cmd = CommandEnum.SetMeasureWindow,
                arg = type
            });
        }

        public OutputEnum GetOutputState()
        {
            OutputEnum result = OutputEnum.Output_None;
            if (dev != null)
            {
                result = dev.GetOutputState();
            }

            return result;
        }

        public string GetErrorString()
        {
            if (dev != null)
            {
                return dev.GetSystemErrorStr();
            }
            else
            {
                return "";
            }
        }

        public void ThreadMain()
        {
            // have to open the device to find the ID 
            try
            {
                IMessageBasedSession visaDev = (IMessageBasedSession)GlobalResourceManager.Open(VisaAddress, AccessModes.None, 1000);
                visaDev.Clear();


                visaDev.FormattedIO.WriteLine("*IDN?");
                string idn = visaDev.FormattedIO.ReadLine();
                if (K2304.SupportsIDN(idn))
                {
                    dev = new K2304(visaDev);
                }
                else if (HP663xx.SupportsIDN(idn))
                {
                    dev = new HP663xx(visaDev);
                }
                else if (B296x.SupportsIDN(idn))
                {
                    dev = new B296x(visaDev);

                    // Copied from example code.
                    visaDev.TerminationCharacter = 10;
                    visaDev.TerminationCharacterEnabled = true;
                }
                else
                    throw new Exception("unsupported device");

                if (dev != null)
                {
                    InstrumentIsConnected = true;


                    HasOutputCompensation = dev.HasOutputComp;
                    HasTwoMeasureChannels = dev.HasTwoMeasureChannels;
                    HasSeprateEnableChannels = dev.HasOutput2;
                }
                else
                {
                    throw new Exception("Cannot create isntrument!");
                }
            }
            catch (Exception)
            {
                // Cannot connect to instruments.
                Debug.WriteLine($"ERROR: Cannot connect to instruments: {VisaAddress}!");

                if (StateChanged != null)
                {
                    HasOutputCompensation = false;
                    HasTwoMeasureChannels = false;
                    HasSeprateEnableChannels = false;
                    StateChanged(this, GetStateData(StateEnum.ConnectionFailed));
                }
            }

            // Send init state 
            if (InstrumentIsConnected)
            {
                if (StateChanged != null)
                {
                    StateChanged(this, GetStateData(StateEnum.Connected));
                }

                if (ProgramDetailsReadback != null)
                {
                    ProgramDetails progDetails = dev.ReadProgramDetails();
                    LastProgramDetails = progDetails;
                    ProgramDetailsReadback(this, LastProgramDetails);
                }
                RefreshDisplay();
                LastRefresh = DateTime.Now;

                while (!StopRequested)
                {
                    Command cmd;
                    int timeout = (int)LastRefresh.AddMilliseconds(refreshDelay_ms).Subtract(DateTime.Now).TotalMilliseconds;
                    while (EventQueue.TryTake(out cmd, timeout < 10 ? 30 : timeout))
                    {
                        switch (cmd.cmd)
                        {
                            case CommandEnum.IRange:
                                DoSetCurrentRange((double)cmd.arg);
                                break;
                            case CommandEnum.Acquire:
                                DoMeasure((AcquireDetails)cmd.arg);
                                break;
                            case CommandEnum.Log:
                                var args = (object[])cmd.arg;
                                DoLog((OutputEnum)args[0], (SenseModeEnum)args[1], (double)args[2]);
                                break;
                            case CommandEnum.Program:
                                DoProgram((ProgramDetails)cmd.arg);
                                break;
                            case CommandEnum.ClearProtection:
                                DoClearProtection();
                                break;
                            case CommandEnum.SetACDCDetector:
                                DoACDCDetector((CurrentDetectorEnum)cmd.arg);
                                break;
                            case CommandEnum.DLFirmware:
                                DoDLFirmware((string)cmd.arg);
                                break;

                            case CommandEnum.SendTextToDisplay:
                                dev.SetDisplayText((string)cmd.arg);
                                break;

                            case CommandEnum.ClearDisplay:
                                dev.SetDisplayText("", true);
                                break;

                            case CommandEnum.SetDisplayState:
                                dev.SetDisplayState((DisplayState)cmd.arg);
                                break;

                            case CommandEnum.RestoreOutState:
                                dev.RestoreOutState((OutputEnum)cmd.arg);
                                break;

                            case CommandEnum.SetMeasureWindow:
                                ((HP663xx)dev).SetMeasureWindowType((MeasWindowType)cmd.arg);
                                break;

                            case CommandEnum.SetOutputComp:
                                dev.SetOutputCompensation((OutputCompensationEnum)cmd.arg);
                                break;

                            default:
                                throw new Exception("Unhandled command in InstrumentWorker");
                        }
                    }
                    RefreshDisplay();
                    LastRefresh = DateTime.Now;
                }

                try
                {
                    EventQueue.Dispose();
                    EventQueue = null;
                }
                catch { }

                dev.Close();
            }

            if (StateChanged != null)
            {
                StateChanged(this, GetStateData(StateEnum.Disconnected));
            }

            if (WorkerDone != null)
            {
                WorkerDone.Invoke(this, null);
            }
        }
    }
}
