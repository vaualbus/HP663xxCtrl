﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ivi.Visa;

namespace HP663xxCtrl
{
    public class HP663xx : IFastSMU
    {
        [Flags]
        public enum OperationStatusEnum
        {
            Calibration = 1,
            WaitingForTrigger = 32,
            CV = 256,
            CV2 = 512,
            CCPositive = 1024,
            CCNegative = 2048,
            CC2 = 4096
        }

        [Flags]
        public enum QuestionableStatusEnum
        {
            OV = 1,
            OCP = 2,
            FP_Local = 8, // frontpanel local was pressed
            OverTemperature = 16,
            OpenSenseLead = 32,
            Unregulated2 = 256,
            RemoteInhibit = 512,
            Unregulated = 1024,
            OverCurrent2 = 4096,
            MeasurementOverload = 16384
        }

        [Flags]
        public enum StatusByteEnum
        {
            QuestionableStatusSummary = 8,
            MesasgeAvailable = 16,
            EventSTB = 32,
            MasterStatusSummary = 64,
            OperationStatusSummary = 128
        }

        private CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

        // PRIVATE 
        private IMessageBasedSession dev;
        private string ID;
        private string Model;

        private bool HasDataLog { get; set; }
        private double DLogFudgeOffset;
        private System.Diagnostics.Stopwatch LoggingStopwatch;

        private long LoggingN;
        private double DLogLastSW;
        private double DLogLostTime; // due to buffer overruns
        private bool DLogInOverrun;
        private double DLogPeriod;

        // PUBLIC
        public bool HasDVM { get; private set; }
        public bool HasOutput2 { get; private set; }
        public bool HasOVP { get { return true; } }
        public bool Has1ARange { get; private set; }


        //
        // PRIVATE METHODS
        //
        private void SetMeasWindowType(MeasWindowType type)
        {
            switch (type)
            {
                case MeasWindowType.Hanning:
                    WriteString("SENS:WIND HANN");
                    break;
                case MeasWindowType.Rect:
                    WriteString("SENS:WIND RECT");
                    break;

                default:
                    WriteString("SENS:WIND HANN");
                    break;
            }
        }

        // Reimplemented here versus the IVI library version because I 
        // Couldn't figure out how to read the final \n at the end of the block,
        // which was causing communications issues.
        // Note that you need to read the '#' before calling this function
        private float[] ReadFloatBlock()
        {

            byte[] x;
            int i = 0;
            x = dev.RawIO.Read();
            int lenLen = int.Parse(System.Text.Encoding.ASCII.GetString(x, i, 1));
            i++;
            int len = int.Parse(System.Text.Encoding.ASCII.GetString(x, i, lenLen));
            i += lenLen;
            // If more data is needed?
            int expectedTotalLen = i + len;
            while (x.Length < expectedTotalLen)
                x = x.Concat(dev.RawIO.Read()).ToArray();
            if (len % 4 != 0)
                throw new FormatException();
            int n = len / 4;
            var ret = new float[n];
            var be = new byte[4];
            for (int j = 0; j < n; i += 4, j++)
            {
                // swap byte order
                be[0] = x[i + 3];
                be[1] = x[i + 2];
                be[2] = x[i + 1];
                be[3] = x[i + 0];
                ret[j] = BitConverter.ToSingle(be, 0);
            }
            return ret;
        }

        private StatusFlags DecodeFlags(OperationStatusEnum opFlags, QuestionableStatusEnum questFlags)
        {
            StatusFlags flags = new StatusFlags();
            flags.Calibration = opFlags.HasFlag(OperationStatusEnum.Calibration);
            flags.CC2 = opFlags.HasFlag(OperationStatusEnum.CC2);
            flags.CCNegative = opFlags.HasFlag(OperationStatusEnum.CCNegative);
            flags.CCPositive = opFlags.HasFlag(OperationStatusEnum.CCPositive);
            flags.CV = opFlags.HasFlag(OperationStatusEnum.CV);
            flags.CV2 = opFlags.HasFlag(OperationStatusEnum.CV2);
            flags.WaitingForTrigger = opFlags.HasFlag(OperationStatusEnum.WaitingForTrigger);
            flags.FP_Local = questFlags.HasFlag(QuestionableStatusEnum.FP_Local);
            flags.MeasurementOverload = questFlags.HasFlag(QuestionableStatusEnum.MeasurementOverload);
            flags.OCP = questFlags.HasFlag(QuestionableStatusEnum.OCP);
            flags.OpenSenseLead = questFlags.HasFlag(QuestionableStatusEnum.OpenSenseLead);
            flags.OVP = questFlags.HasFlag(QuestionableStatusEnum.OV);
            flags.OCP2 = questFlags.HasFlag(QuestionableStatusEnum.OverCurrent2);
            flags.OverTemperature = questFlags.HasFlag(QuestionableStatusEnum.OverTemperature);
            flags.RemoteInhibit = questFlags.HasFlag(QuestionableStatusEnum.RemoteInhibit);
            flags.Unregulated = questFlags.HasFlag(QuestionableStatusEnum.Unregulated);
            flags.Unregulated2 = questFlags.HasFlag(QuestionableStatusEnum.Unregulated2);
            return flags;
        }

        //
        // PUBLIC METHODS
        //
        public void Reset()
        {
            WriteString("*RST");
            WriteString("*CLS");
            WriteString("STAT:PRES");
            WriteString("*SRE 0");
            WriteString("*ESE 0");
        }

        public void SetCurrentRange(double range) {
            WriteString("SENS:CURR:RANG " + range.ToString(CI));
        }

        public ProgramDetails ReadProgramDetails() {

            string response = Query("OUTP?;VOLT?;CURR?;"
                + ":VOLT:PROT:STAT?;:VOLT:PROT?;:CURR:PROT:STAT?" +
                (HasOutput2? ";:VOLT2?;CURR2?":"")).Trim();

            string[] parts = response.Split(new char[] { ';' });
            ProgramDetails details = new ProgramDetails() {
                Enabled1 = (parts[0] == "1"),
                Enabled2 = false,
                V1 = double.Parse(parts[1],CI),
                I1 = double.Parse(parts[2],CI),
                OVP = (parts[3] == "1"),
                OVPVal = double.Parse(parts[4],CI),
                OCP = (parts[5] == "1"),
                V2 = HasOutput2 ? double.Parse(parts[6],CI):double.NaN,
                I2 = HasOutput2 ? double.Parse(parts[7], CI) : double.NaN,
                HasDVM = HasDVM,
                HasOutput2 = HasOutput2,
                HasOVP = this.HasOVP,
                ID = ID
            };

            // Maximums
            details.I1Ranges = GetCurrentRanges();
            parts = Query("VOLT? MAX; CURR? MAX").Trim().Split(new char[] {';'});
            details.MaxV1 = double.Parse(parts[0],CI);
            details.MaxI1 = double.Parse(parts[1],CI);
            if (HasOutput2) {
                parts = Query("VOLT2? MAX; CURR2? MAX").Trim().Split(new char[] { ';' });
                details.MaxV2 = double.Parse(parts[0],CI);
                details.MaxI2 = double.Parse(parts[1],CI);

            }
            details.I1Range = Double.Parse(Query(":sense:curr:range?").Trim(),CI);

            string detector = Query("SENSE:CURR:DET?").Trim();
            switch (detector) {
                case "DC": details.Detector = CurrentDetectorEnum.DC; break;
                case "ACDC": details.Detector = CurrentDetectorEnum.ACDC; break;
                default: throw new Exception();
            }
            return details;
        }

        public InstrumentState ReadState(bool measureCh2=true, bool measureDVM=true) {
            InstrumentState ret = new InstrumentState();
            DateTime start = DateTime.Now;
            // ~23 ms
            string statusStr = Query("stat:oper:cond?;:stat:ques:cond?;:sense:curr:range?;" +
                ":OUTP1?;VOLTage:PROTection:STAT?;:CURR:PROT:STAT?").Trim();
            string[] statuses = statusStr.Split(new char[] { ';' });
            ret.Flags = DecodeFlags(
                (OperationStatusEnum)int.Parse(statuses[0], CI),
                (QuestionableStatusEnum)int.Parse(statuses[1], CI));
            ret.IRange = double.Parse(statuses[2],CI);
            ret.OutputEnabled1 = statuses[3] == "1";
            ret.OutputEnabled2 = true; // Out2 follow out 1.
            ret.OVP = statuses[4] == "1";
            ret.OCP = statuses[5] == "1";
            // Must measure each thing individually
            // Default is 2048 points, with 46.8us rate
            // This is 95.8 ms; about 6 PLC in America, or 5 in other places.
            // But, might be better to do one PLC?
            // For CH1:
            // Setting  time
            //      1    30
            //  2048/46.8    230
            //   4096    168
            // 
            WriteString("TRIG:ACQ:SOUR INT;COUNT:VOLT 1;:TRIG:ACQ:COUNT:CURR 1");
            WriteString("SENS:SWE:POIN 2048; TINT 46.8e-6");
            WriteString("SENS:SWE:OFFS:POIN 0;:SENS:WIND HANN");
            // Channel is about 30 ms
            ret.V = Double.Parse(Query("MEAS:VOLT?"),CI);
            ret.I = Double.Parse(Query("MEAS:CURR?"),CI);
            // Ch2 is about 100 ms
            if (measureCh2 && HasOutput2) {
                ret.V2 = Double.Parse(Query("MEAS:VOLT2?"),CI);
                ret.I2 = Double.Parse(Query("MEAS:CURR2?"),CI); // Fixed at 2048*(15.6us)
            } else {
                ret.V2 = double.NaN;
                ret.I2 = double.NaN;
            }

            // RMS is also available using MEAS:DVM:ACDC
            if(measureDVM && HasDVM)
                ret.DVM = Double.Parse(Query("MEAS:DVM?"),CI); // 2048*(15.6us) => 50 ms
            else
                ret.DVM = Double.NaN;
            ret.duration = DateTime.Now.Subtract(start).TotalMilliseconds;

            return ret;
        }


        public StatusFlags GetStatusFlags()
        {
            string val = Query("stat:oper:cond?;:stat:ques:cond?");
            int[] statuses = val.Split(new char[] { ';' }).Select(x => int.Parse(x,CI)).ToArray();
            return DecodeFlags((OperationStatusEnum)statuses[0],(QuestionableStatusEnum)statuses[1]);
        }

        public OperationStatusEnum GetOperationStatus()
        {
            return (OperationStatusEnum)int.Parse(Query("STAT:OPER:COND?"),CI);
        }

        public QuestionableStatusEnum GetQuestionableStatus()
        {
            return (QuestionableStatusEnum)int.Parse(Query("STAT:QUES:COND?"),CI);
        }

        string Query(string cmd)
        {
            WriteString(cmd);
            return ReadString();
        }

        private string[] QueryString(string cmd)
        {
            return Query(cmd).Trim().Split(new char[] { ',', ';' });
        }

        public void ClearErrors()
        {
            string msg;
            while(!( (msg = Query("SYSTem:ERRor?")).StartsWith("+0,"))) {
            }
        }

        // Return 4 32-bit words
        public UInt32[] GetFirmwareWord(uint w) {
            
            WriteString(String.Format(
                "DIAG:PEEK? #H{0:X4}; PEEK? #H{1:X4}; PEEK? #H{2:X4}; PEEK? #H{3:X4}",
                w,w+1,w+2,w+3));
            string s = ReadString();
            var parts = s.Split(new char[] { ';' }).Select(x => x.Trim().Substring(2));
            return parts.Select(x => UInt32.Parse(x, System.Globalization.NumberStyles.HexNumber)).ToArray();
        }

        public void SetupLogging(
            OutputEnum channel,
            SenseModeEnum mode,
            double interval
            ) {
            int numPoints = 4096;
            double AcqInterval = 15.6e-6;
            string modeString;
            int triggerOffset = 0;

            if (mode == SenseModeEnum.DVM && !HasDVM)
                throw new Exception();
            switch (mode) {
                case SenseModeEnum.CURRENT: modeString = "CURR"; break;
                case SenseModeEnum.VOLTAGE: modeString = "VOLT"; break;
                case SenseModeEnum.DVM: modeString = "DVM"; break;
                default: throw new InvalidOperationException("Unknown transient measurement mode");
            }

            if (HasDataLog) {
                var currRange = Query("SENS:CURR:RANG?").Trim();
                string detector = Query("SENSe:CURRent:DETector?").Trim();
                if(interval > 1.0)
                    interval = 1.0;
                // Official GUI used 0.00500760 as minimum.
                // Less than about 3 ms causes nearly immediate buffer overruns
                // Less than about 5 ms causes eventual buffer overruns.
               /* if(interval < 0.003)
                    interval = 0.003;*/

                WriteString(String.Format(
                    "CONF:DLOG {0},{1},{2},{3},1024,IMM",
                    modeString, currRange, detector,interval));
                var dlogConfReadback = Query("CONF:DLOG?").Trim();
                var dlogConfReadbackParts = dlogConfReadback.Split(new char[] { ',' });
                DLogPeriod = double.Parse(dlogConfReadbackParts[3]);
                WriteString("INIT:NAME DLOG");
                WriteString("TRIG:ACQ");
                Query("*ESR?");
            } else {
                // Immediate always has a trigger count of 1
                WriteString("SENSe:FUNCtion \"" + modeString + "\"");
                WriteString("SENSe:SWEEP:POINTS " + numPoints.ToString(CI) + "; " +
                    "TINTerval " + AcqInterval.ToString(CI) + ";" +
                    "OFFSET:POINTS " + triggerOffset.ToString(CI));
                WriteString("TRIG:ACQ:SOURCE BUS");
                WriteString("ABORT;*WAI");
                //WriteString("INIT:NAME ACQ;:TRIG:ACQ");

                Query("*OPC?");
            }
            LoggingStopwatch = new System.Diagnostics.Stopwatch();
            LoggingN = 0;
            LoggingStopwatch.Start();
            DLogLostTime = 0;
            DLogInOverrun = false;
        }

        public LoggerDatapoint[] MeasureLoggingPoint(OutputEnum channel, SenseModeEnum mode) {
            LoggerDatapoint ret = new LoggerDatapoint();
            string rsp;
            string[] parts;
            if (HasDataLog) {
                var retList = new List<LoggerDatapoint>();
                WriteString("FETC:ARR:DLOG?");
                try
                {
                    byte[] x = dev.RawIO.Read(1);
                    if (x.Length != 1 || x[0] != '#')
                        throw new FormatException();
                    double swTime = LoggingStopwatch.Elapsed.TotalSeconds;
                    var data = ReadFloatBlock();
                    if (data[0] != data[1] || data[0] != data[2])
                        throw new Exception("Unexpected block format");
                    DateTime recordTime = DateTime.Now;
                    // data[0,1,2] is -1 if there is a buffer overrun.
                    if (LoggingN == 0)
                        DLogFudgeOffset = swTime - (data[0] - 1) * DLogPeriod;
                    if (data[0] < 0)
                    {
                        System.Diagnostics.Trace.WriteLine("Buffer overrun");
                        DLogInOverrun = true;
                        WriteString("ABORT;*WAI");
                        Query("*OPC?");
                        WriteString("INIT:NAME DLOG");
                        WriteString("TRIG:ACQ");
                    }
                    if (DLogInOverrun && data[0] >= 1)
                    {
                        DLogLostTime += swTime - DLogLastSW - data[0] * DLogPeriod;
                        DLogInOverrun = false;
                    }
                    for (int i = 3, n = 0; i < data.Length; i += 3, n++)
                    {
                        ret = new LoggerDatapoint();
                        ret.Mean = data[i];
                        ret.Min = data[i + 1];
                        ret.Max = data[i + 2];
                        ret.RMS = double.NaN;
                        ret.t = DLogLostTime + LoggingN * DLogPeriod;
                        ret.RecordTime = recordTime;
                        LoggingN++;
                        retList.Add(ret);
                    }
                    double deltaDuration = 0;
                    if (data[0] > 0)
                    {
                        var realDuration = swTime - DLogFudgeOffset;
                        deltaDuration = DLogLostTime + (LoggingN - 1) * DLogPeriod - realDuration;
                        System.Diagnostics.Trace.WriteLine(String.Format("N={0}, dt={1} s, rate={2} ppm", data[0],
                            deltaDuration,
                            deltaDuration / realDuration * 1.0e6
                            ));
                        DLogLastSW = swTime;
                    }
                    var a = retList.ToArray();
                    for (int i = 0; i < a.Length; i++)
                        a[i].Max = deltaDuration;

                    return a;
                } 
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                    return new LoggerDatapoint[] { }; 
                }
            }

            switch(mode) {
                case SenseModeEnum.CURRENT:
                    rsp = Query("MEAS:CURR?;:FETCH:CURR:MIN?;MAX?;ACDC?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    ret.Min = double.Parse(parts[1], CI);
                    ret.Max = double.Parse(parts[2], CI);
                    ret.RMS = double.Parse(parts[3], CI);
                    break;
                case SenseModeEnum.VOLTAGE:
                    rsp = Query("MEAS:VOLT?;:FETCH:VOLT:MIN?;MAX?;ACDC?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    ret.Min = double.Parse(parts[1], CI);
                    ret.Max = double.Parse(parts[2],CI);
                    ret.RMS = double.Parse(parts[3],CI);
                    break;
                case SenseModeEnum.DVM:
                    rsp = Query("MEAS:DVM?").Trim();
                    parts = rsp.Split(new char[] { ';' });
                    ret.Mean = double.Parse(parts[0], CI);
                    break;
            }

            ret.t = LoggingStopwatch.Elapsed.TotalSeconds;
            ret.RecordTime = DateTime.Now;
            return new LoggerDatapoint[] {ret};
        }

        public void StartTransientMeasurement(
            OutputEnum channel,
            SenseModeEnum mode,
            int numPoints = 4096,
            double interval = 15.6e-6,
            double level = double.NaN,
            double hysteresis = 0.0,
            int triggerCount = 1,
            TriggerSlopeEnum triggerEdge = TriggerSlopeEnum.Positive,
            int triggerOffset = 0,
            MeasWindowType windowType = MeasWindowType.Null
            )
        {
            if (triggerCount * numPoints > 4096) {
                throw new InvalidOperationException();
            }

            string modeString;
            if (mode == SenseModeEnum.DVM && !HasDVM)
                throw new Exception();

            switch (mode)
            {
                case SenseModeEnum.CURRENT: modeString = "CURR"; break;
                case SenseModeEnum.VOLTAGE:  modeString = "VOLT";  break;
                case SenseModeEnum.DVM:  modeString = "DVM"; break;
                default: throw new InvalidOperationException("Unknown transient measurement mode");
            }

            // Set the window type
            SetMeasWindowType(windowType);

            WriteString("SENSe:FUNCtion \"" + modeString + "\"");
            if (numPoints < 1 || numPoints > 4096)
                throw new InvalidOperationException("Number of points must be betweer 1 and 4096");
            // Immediate always has a trigger count of 1
            if (triggerEdge == TriggerSlopeEnum.Immediate)
                triggerCount = 1;
            if (interval < 15.6e-6)
                interval = 15.6e-6;
            if (interval > 1e4)
                interval = 1e4;
            WriteString("SENSe:SWEEP:POINTS " + numPoints.ToString(CI) + "; " +
                "TINTerval " + interval.ToString(CI) + ";" +
                "OFFSET:POINTS " + triggerOffset.ToString(CI));
            if(triggerEdge== TriggerSlopeEnum.Immediate || double.IsNaN(level)) {
                WriteString("TRIG:ACQ:SOURCE BUS");
                WriteString("ABORT;*WAI");
                WriteString("INIT:NAME ACQ;:TRIG:ACQ");
            } else {
                string slopeStr = "EITH";
                switch (triggerEdge) {
                    case TriggerSlopeEnum.Either: slopeStr = "EITH"; break;
                    case TriggerSlopeEnum.Positive: slopeStr = "POS"; break;
                    case TriggerSlopeEnum.Negative: slopeStr = "NEG"; break;
                }
                WriteString("TRIG:ACQ:COUNT:" + modeString + " " + triggerCount.ToString(CI) + ";" +
                    ":TRIG:ACQ:LEVEL:" + modeString + " " + level.ToString(CI) + ";" +
                    ":TRIG:ACQ:SLOPE:" + modeString + " " + slopeStr + ";" +
                    ":TRIG:ACQ:HYST:" + modeString + " " + hysteresis.ToString(CI));
                WriteString("TRIG:ACQ:SOURCE INT");
                WriteString("ABORT;*WAI");
                WriteString("INIT:NAME ACQ");
            }

            // Clear status byte
            Query("*ESR?");
            WriteString("*OPC");
        }

        public bool IsMeasurementFinished() {
            return (((int.Parse(Query("*ESR?").Trim(), CI) & 1) == 1));
        }

        public void AbortMeasurement() {
            Query("ABORT;*OPC?");
        }

        public MeasArray FinishTransientMeasurement(
            OutputEnum channel,
            SenseModeEnum mode,
            int triggerCount = 1) {
            /*StatusByteEnum stb;
            do {
                System.Threading.Thread.Sleep(50);
                stb = (StatusByteEnum)dev.IO.ReadSTB();
            } while (!stb.HasFlag(StatusByteEnum.MesasgeAvailable));
            dev.ReadString(); // read the +1 from *OPC?*/

            MeasArray res = new MeasArray();

            bool isNormalMeasureFetched = false;
            switch (mode)
            {
                case SenseModeEnum.VOLTAGE:
                    WriteString("FETCH:ARRay:VOLTage?");
                    break;

                case SenseModeEnum.CURRENT:
                    WriteString("FETCH:ARRay:CURRent?");
                    break;

                case SenseModeEnum.DVM:
                    isNormalMeasureFetched = true;
                    break;

                default:
                    break;
            }

            float[] data = null;
            if (isNormalMeasureFetched)
            {
                int sampleCount = 128;
                data = new float[sampleCount];

                res.Mode = SenseModeEnum.DVM;
                res.Data = new double[triggerCount][];

                for (int i = 0; i < sampleCount; i++)
                {
                    WriteString("FETCH:DVM:ACDC?");
                    data[i] = (float)dev.FormattedIO.ReadDouble();
                }
            }
            else
            { 
                data = dev.FormattedIO.ReadBinaryBlockOfSingle();
                res.Mode = mode;
                res.Data = new double[triggerCount][];
            }

            int numPoints = data.Length / triggerCount;
            for (int i = 0; i < triggerCount; i++)
            {
                res.Data[i] = data.Skip(numPoints * i)
                    .Take(numPoints)
                    .Select(x => (double)x)
                    .ToArray();

            }

            // Might be rounded, so return the actual value, not the requested value
            try
            {
                res.TimeInterval = double.Parse(Query("SENSE:SWEEP:TINT?"), CI);
            }
            catch
            {
                // If we fail try parsing float instead.
                try
                {
                    res.TimeInterval = (double) float.Parse(Query("SENSE:SWEEP:TINT?"), CI);
                }
                catch
                {
                    res.TimeInterval = 0;
                }
            }
            
            return res;
        }

        public void ClearProtection() {
            WriteString("OUTPut:PROTection:CLEar");
        }

        public void EnableOutput(OutputEnum channel, bool enabled)
        {
            WriteString("OUTPUT  " + (enabled ? "ON" : "OFF") );
        }

        public void SetIV(int channel, double voltage, double current) {
            WriteString("VOLT" +
                (channel == 2 ? "2 " : " ") + voltage.ToString(CI) +
                ";:CURR" +
                (channel == 2 ? "2 " : " ") + current.ToString(CI) 
                );
        }
        /// <summary>
        /// Set to Double.NaN to disable OVP
        /// </summary>
        /// <param name="ovp"></param>
        public void SetOVP(double ovp) {
            if (double.IsNaN(ovp))
                WriteString("VOLTage:PROTection:STATe OFF");
            else {
                WriteString("VOLTAGE:PROTECTION " + ovp.ToString(CI));
                WriteString("VOLTage:PROTection:STATe ON");
            }
        }
        public void SetOCP(bool enabled) {
            WriteString("CURR:PROT:STAT " + (enabled ? "1":"0"));
        }

        // PSC causes too much writing to non-volatile RAM. Automatically disable it, if active.
        // People _probably_ won't depend on it....
        void EnsurePSCOne()
        {
            int psc = int.Parse(Query("*PSC?"), CI);
            if (psc == 0)
           
                WriteString("*PSC 1"); ;
        }
        public static bool SupportsIDN(string idn) {
            if (idn.Contains(",66309B,") || idn.Contains(",66319B,") ||
                idn.Contains(",66309D,") || idn.Contains(",66319D,") ||
                idn.Contains(",66311B,") || idn.Contains(",66321B,") ||
                idn.Contains(",66311D,") || idn.Contains(",66321D,"))
                return true;
            return false;
        }

        public HP663xx(IMessageBasedSession visaDev)
        {
            dev = visaDev;
            dev.Clear(); // clear I/O buffer
            dev.TimeoutMilliseconds = 5000; // 5 seconds

            WriteString("*IDN?");
            ID = ReadString().Trim();
            var IDParts = ID.Trim().Split(new char[] { ',' });
            if(IDParts.Length != 4) {
                dev.Dispose();
                dev = null;
                throw new InvalidOperationException("Not a known 663xx supply!");
            }

            Model = IDParts[1];
            switch (Model.ToUpper()) {
                case "66309B":
                case "66319B":
                    HasDVM = false; HasOutput2 = true;
                    break;
                case "66309D":
                case "66319D":
                    HasDVM = true; HasOutput2 = true;
                    break;
                case "66311B":
                case "66321B":
                    HasDVM = false; HasOutput2 = false;
                    break;
                case "66311D":
                case "66321D":
                    HasDVM = true; HasOutput2 = true;
                    break;
                default:
                    dev.Dispose();
                    dev = null;
                    throw new InvalidOperationException("Not a known 663xx supply!");
            }
            
            HasDataLog = IDParts[3].ToUpper().StartsWith("A.03");
            if (Model.StartsWith("66319") || Model.StartsWith("66321"))
            {
                Has1ARange = true;
            }

            WriteString("STATUS:PRESET"); // Clear PTR/NTR/ENABLE register
            EnsurePSCOne();
            WriteString("*CLS"); // clear status registers
            WriteString("ABORT");
            ClearErrors();
            WriteString("FORMAT REAL");
            WriteString("FORMat:BORDer NORMAL");
            // Enable the detection of open sense leads
            WriteString("SENSe:PROTection:STAT ON");
            
        }

        public void SetDisplayText(string text, bool clearIt = false)
        {
            if (clearIt)
            {
                WriteString("DISPLAY:MODE NORM");
            }
            else
            {
                WriteString("DISPLAY:MODE TEXT");
            }

            WriteString("DISP:TEXT " + "'" + text + "'");
        }
        
        public void SetDisplayState(DisplayState state)
        {
            switch (state)
            {
                case DisplayState.ON:
                    WriteString("DISP:STATE OFF");
                    break;

                case DisplayState.OFF:
                    WriteString("DISP:STATE ON");
                    break;
            }
        }

        public void SetCurrentDetector(CurrentDetectorEnum detector) {
            switch (detector) {
                case CurrentDetectorEnum.ACDC: WriteString("SENSe:CURRent:DETector ACDC"); break;
                case CurrentDetectorEnum.DC: WriteString("SENSe:CURRent:DETector DC"); break;
            }
        }

        // Usually use low capacitance mode, so it's always stable. Manual says high requires C_in >5uF
        public void SetOutputCompensation(OutputCompensationEnum comp) {
            switch (comp) {
                case OutputCompensationEnum.HighCap:
                    WriteString("OUTPUT:TYPE HIGH");
                    break;
                case OutputCompensationEnum.LowCap:
                    WriteString("OUTPUT:TYPE LOW");
                    break;
            }
        }

        string ReadString() {
            return dev.FormattedIO.ReadLine();
        }

        void WriteString(string msg) {
            dev.FormattedIO.WriteLine(msg);
        }

        public void Close(bool goToLocal = true)
        {
            if (dev != null ) {
                if (goToLocal) {
                    if (dev is IGpibSession) {
                        ((IGpibSession)dev).SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
                    }
                }
                dev.Dispose();
                dev = null;
            }
        }

        public void SetMeasureWindowType(MeasWindowType type)
        {
            SetMeasWindowType(type);
        }

        public string GetSystemErrorStr()
        {

            string firstError = Query("SYST:ERR?");

            //
            // TODO parse all errors not only the first one!
            //
            return firstError;
        }

        public OutputEnum GetOutputState()
        {
            OutputEnum result = OutputEnum.Output_None;
            var outStateStr = QueryString(":OUTP:STAT?;:OUTP2:STAT?");

            // Setup a measurement to read the curent I/V values.
            if (outStateStr[0] == "1")
            {
                result |= OutputEnum.Output_1;
            }

            if (HasOutput2)
            {
                if (outStateStr[1] == "1")
                {
                    result |= OutputEnum.Output_2;
                }
            }

            return result;
        }

        public void RestoreOutState(OutputEnum slectedChannel)
        { 
        }

        private double[] GetCurrentRanges()
        {
            double[] ret = null;
            if (Has1ARange)
                ret = new double[] { 0.02, 1, 3 };
            else
                ret = new double[] { 0.02, 3 };

            return ret;
        }
    }
}
