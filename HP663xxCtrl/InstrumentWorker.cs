using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ivi.Visa;
using System.CodeDom;
using ZedGraph;
using System.Windows;
using System.Diagnostics;

namespace HP663xxCtrl {
    public class InstrumentWorker {

        IFastSMU dev = null;
        public string VisaAddress {
           get ; private set;
        }
        BlockingCollection<Command> EventQueue;

        public volatile uint refreshDelay_ms = 1000;

        volatile bool StopRequested = false;
        public volatile bool StopAcquireRequested = false;
        ProgramDetails LastProgramDetails;

        public volatile bool InstrumentIsConnected = false; 

        public enum StateEnum {
            Disconnected,
            Connected,
            ConnectionFailed,
            Measuring,
            InitStage
        }

        enum CommandEnum {
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
            RestoreOutState
        }

        struct Command {
            public CommandEnum cmd;
            public object arg;
        }

        public struct AcquireDetails {
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

        public struct StateEventData
        {
            public StateEnum State;
            public bool HasTwoMeasureChannels;
            public bool HasSeprateEnableChannels;
        }

        void RefreshDisplay() {
            var state = dev.ReadState();
            if (NewState != null)
                NewState(this, state);
        }

        public InstrumentWorker(string address) {
            this.VisaAddress = address;
            EventQueue = new BlockingCollection<Command>(new ConcurrentQueue<Command>());
        }

        public event EventHandler WorkerDone;
        public event EventHandler<InstrumentState> NewState;
        public event EventHandler<StateEventData> StateChanged;
        public event EventHandler<ProgramDetails> ProgramDetailsReadback;
        DateTime LastRefresh;

        private bool HasTwoMeasureChannels = false;
        private bool HasSeprateEnableChannels = false;

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

                    HasTwoMeasureChannels = dev.HasOutput2;
                    HasSeprateEnableChannels = HasTwoMeasureChannels;
                }
                else
                    throw new Exception("unsupported device");

                if (dev != null)
                {
                    InstrumentIsConnected = true;
                }
            }
            catch (Exception)
            {
                // Cannot connect to instruments.
                Debug.WriteLine($"ERROR: Cannot connect to instruments: {VisaAddress}!");

                if (StateChanged != null)
                {
                    StateChanged(this, new StateEventData { State = StateEnum.ConnectionFailed, HasTwoMeasureChannels = HasTwoMeasureChannels, HasSeprateEnableChannels = HasSeprateEnableChannels });
                }
            }

            // Send init state 
            if (InstrumentIsConnected)
            {
                if (StateChanged != null)
                {
                    StateChanged(this, new StateEventData { State = StateEnum.Connected, HasTwoMeasureChannels = HasTwoMeasureChannels, HasSeprateEnableChannels = HasSeprateEnableChannels });
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
                                DoAcquisition((AcquireDetails)cmd.arg);
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

            if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Disconnected, HasTwoMeasureChannels = HasTwoMeasureChannels });
            if (WorkerDone != null)
                WorkerDone.Invoke(this, null);
        }

        public event EventHandler<MeasArray> DataAcquired;
        void DoSetCurrentRange(double range) {
            dev.SetCurrentRange(range);
            LastProgramDetails.I1Range = range;
        }

        public void RequestIRange(double range) {
            EventQueue.Add(new Command() { cmd = CommandEnum.IRange, arg = range });
        }

        // Must set StopAcquireRequested to false before starting acquisition
        void DoAcquisition(AcquireDetails arg) {
            if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Measuring, HasTwoMeasureChannels = HasTwoMeasureChannels });

            int remaining = arg.SegmentCount;
            while (remaining > 0 && !StopRequested && !StopAcquireRequested) {
                int count = 0;
                if (arg.triggerEdge == TriggerSlopeEnum.Immediate)
                    count = 1;
                else
                    count = Math.Min(remaining, 4096 / arg.NumPoints);

                dev.StartTransientMeasurement(
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

                while (!dev.IsMeasurementFinished() && !StopAcquireRequested
                    && !StopRequested) {
                    System.Threading.Thread.Sleep(70);
                }

                if (StopAcquireRequested || StopRequested) {
                    dev.AbortMeasurement();
                    if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Connected, HasTwoMeasureChannels = HasTwoMeasureChannels });
                    return;
                }
                var data = dev.FinishTransientMeasurement(channel: arg.SelectedChannel, mode: arg.SenseMode, triggerCount: count);

                if (DataAcquired != null)
                    DataAcquired(this, data);
                remaining -= count;
            }
            if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Connected, HasTwoMeasureChannels = HasTwoMeasureChannels });
        }

        // Must set StopAcquireRequested to false before starting acquisition
        //
        // Also, the returned AcquisitionData structure will have a blank 
        // SamplingPeriod and DataSeries
        //
        public AcquisitionData RequestAcquire(AcquireDetails details) {
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
        
        public event EventHandler<LoggerDatapoint> LogerDatapointAcquired;
        void DoLog(OutputEnum channel, SenseModeEnum mode, double interval=0) {
            if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Measuring, HasTwoMeasureChannels = HasTwoMeasureChannels });
            dev.SetupLogging(channel, mode, interval);

            while (!StopRequested && !StopAcquireRequested) {

                if (StopAcquireRequested || StopRequested) {
                    dev.AbortMeasurement();
                    if (StateChanged != null) StateChanged(this, new StateEventData { State=StateEnum.Connected, HasTwoMeasureChannels = HasTwoMeasureChannels });
                    return;
                }

                var data = dev.MeasureLoggingPoint(channel, mode);
                if (LogerDatapointAcquired != null) {
                    foreach(var p in data)
                        LogerDatapointAcquired(this, p);

                }
            }
            if (StateChanged != null) StateChanged(this, new StateEventData { State = StateEnum.Connected, HasTwoMeasureChannels = HasTwoMeasureChannels });
        }

        void DoDLFirmware(string filename) {
            try {
                using (BinaryWriter bw = new BinaryWriter(File.Open(filename, FileMode.Create))) {
                    for (uint i = 0; i <= 0xFFFF && !StopAcquireRequested; i+=4) {
                        var x = ((HP663xx)dev).GetFirmwareWord(i);
                        foreach(var w in x)
                            bw.Write(w);
                    }
                }
            } catch { // mostly IO exceptions

            }
        }
        public void RequestDLFirmware(string filename) {
            if (StopAcquireRequested == true)
                return;
            EventQueue.Add(new Command() {
                cmd = CommandEnum.DLFirmware,
                arg = filename
            });
        }

        public void RequestLog(OutputEnum channel,  SenseModeEnum mode, double interval=0) {
            if (StopAcquireRequested == true)
                return;
            EventQueue.Add(new Command() {
                cmd = CommandEnum.Log,
                arg = new object[] {channel,mode,interval}
            });
        }
        void DoProgram(ProgramDetails details) {

            //
            // Disable the output.
            //
            if (!details.Enabled1) {
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

        public void RequestProgram(ProgramDetails details) {
            EventQueue.Add(new Command() {
                cmd = CommandEnum.Program,
                arg = details
            });
        }

        void DoDLFirmware() {

        }

        void DoClearProtection() {
            dev.ClearProtection();
        }

        public void RequestClearProtection() {
            EventQueue.Add(new Command() {
                cmd = CommandEnum.ClearProtection,
                arg = null
            });
        }

        public void RequestShutdown() {
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

        void DoACDCDetector(CurrentDetectorEnum detector) {
            dev.SetCurrentDetector(detector);
            LastProgramDetails.Detector = detector;
        }

        public void RequestACDCDetector(CurrentDetectorEnum detector) {
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
    }
}
