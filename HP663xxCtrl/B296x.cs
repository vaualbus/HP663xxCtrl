﻿using Ivi.Visa;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Windows;
using System.Windows.Media.Animation;
using ZedGraph;
using static System.Windows.Forms.AxHost;

namespace HP663xxCtrl
{
    internal class B296x : IFastSMU
    {
        const int NUM_OF_TRACE_DATA_POINTS = 1024;

        #region Private Methods
        private string ID;
        private string Model;
        private double LineFreq;
        private IMessageBasedSession dev;
        private CultureInfo CI = CultureInfo.InvariantCulture;

        private bool UseTraceBuffer => false;

        private Stopwatch LoggingStopwatch;
        private OutputEnum OutputStateBeforeMeasurement;

        private string ReadString()
        {
            return dev.FormattedIO.ReadLine();
        }

        private void WriteString(string msg)
        {
            dev.FormattedIO.WriteLine(msg);
        }

        private void CheckIfCommError(string msg)
        {
            return; // NO DEBUG

            //
            // NOTE: Cannot use query otherwise we will call ourself.
            // need to do this in 'raw' mode.
            //
            dev.FormattedIO.WriteLine("SYST:ERR?");
            string err = dev.FormattedIO.ReadLine().Trim();
            if (err != "+0,\"No error\"")
            {
                Debug.WriteLine($"Command {msg} failed! {err}");
            }
        }

        private string Query(string cmd)
        {
            WriteString(cmd);

            string res = ReadString();

            // DEBUG
            CheckIfCommError(cmd);

            return res;
        }

        private double[] QueryDouble(string cmd)
        {
            var res = Query(cmd).Trim()
                .Split(new char[] { ',', ';' })
                .Select(x => double.Parse(x, CI)).ToArray();

            // DEBUG
            CheckIfCommError(cmd);

            return res;
        }

        private int[] QueryInt(string cmd)
        {
            var res = Query(cmd).Trim()
                .Split(new char[] { ',', ';' })
                .Select(x => int.Parse(x, CI)).ToArray();

            // DEBUG
            CheckIfCommError(cmd);

            return res;
        }

        private string[] QueryString(string cmd)
        {
            var res = Query(cmd).Trim().Split(new char[] { ',', ';' });

            // DEBUG
            CheckIfCommError(cmd);

            return res;
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
            {
                x = x.Concat(dev.RawIO.Read()).ToArray();
            }

            if (len % 4 != 0)
            {
                throw new FormatException();
            }

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

        private double[] GetCurrentRanges()
        {
            double[] ret = null;
            ret = new double[] { 10e-9, 100e-9, 1e-6, 10e-6, 100e-6, 1e-3, 10e-3, 100e-3, 1, 1.5, 3, 10 };

            return ret;
        }

        private uint ReadHexString(string str)
        {
            uint val = 0;
            if (str.Length > 2 && str[0] == '#' && str[1] == 'H')
            {
                str = str.Substring(2);
                val = uint.Parse(str, CI);
            }

            return val;
        }

        private StatusFlags DecodeFlags()
        {
            var statusFlags = new StatusFlags();

            var voltStateBit = QueryInt(":STAT:QUES:VOLT:COND?")[0];
            var currStateBit = QueryInt(":STAT:QUES:CURR:COND?")[0];

            statusFlags.OVP =  voltStateBit == 0x1;
            statusFlags.OCP  = currStateBit == 0x1;
            statusFlags.OVP2 = voltStateBit == 0x2;
            statusFlags.OCP2 = currStateBit == 0x2;
            statusFlags.Calibration = QueryInt(":STAT:QUES:CAL:COND?")[0] != 0; // Ch1 == Ch2
            statusFlags.OverTemperature = QueryInt(":STAT:QUES:TEMP:COND?")[0] != 0; // Ch1 == Ch2
            // flags.WaitingForTrigger = ???


            //
            // TODO: How we handle CC, CV indication?
            //
            return statusFlags;
        }

        #endregion

        #region Public Methods and fields

        public bool HasOutput2 { get; private set; }

        public bool HasDVM => false;

        public bool HasOVP => true;

        public bool HasDataLog => false;

        public B296x(IMessageBasedSession visaDev)
        {
            dev = visaDev;
            dev.Clear();
            dev.TimeoutMilliseconds = 5000;

            //
            // Read device ID
            //
            ID = Query("*IDN?");
            var IDParts = ID.Trim().Split(new char[] { ',', ';' });
            if (IDParts.Length != 4)
            {
                dev.Dispose();
                dev = null;
                throw new InvalidOperationException("Not a known B296x supply!");
            }


            // Select proper model
            Model = IDParts[1];
            switch (Model.ToUpper())
            {
                case "B2961A":
                case "B2961B":
                    HasOutput2 = false;
                    break;


                case "B2962A":
                case "B2962B":
                    HasOutput2 = true;
                    break;

                default:
                    dev.Dispose();
                    dev = null;
                    throw new InvalidOperationException("Not a known B296x supply!");
            }

            // Clear PTR/NTR/ENABLE register
            WriteString("STATUS:PRESET");

            // clear status registers
            WriteString("*CLS");
            // Abort all pending trigger actions
            WriteString("ABOR:ALL");
            ClearErrors();

            // WriteString(":FORMAT REAL");
            WriteString(":FORMat:BORDer NORMAL");

            // Perform initial measure setup
            WriteString(":SENS:CURR:NPLC 0.1");
            WriteString(":SENS:VOLT:NPLC 0.1");

            // Set default display view.
            WriteString(":DISP:ENAB ON");
            WriteString(":DISP:VIEW DUAL");

            // Default NPLC set to not auto mode.
            // WriteString($":SENS:NPLC:AUTO 0");
            // WriteString($":SENS2:NPLC:AUTO 0");

            // Get the current line frequency, used for NPLC settings.
            LineFreq = QueryDouble(":SYST:LFR?")[0];
        }

        public void ClearErrors()
        {
            string msg;
            while (!((msg = Query("SYSTem:ERRor?")).StartsWith("+0,")))
            {
            }
        }

        public void Reset()
        {
            WriteString("*RST");
            WriteString("*CLS");
            WriteString("STAT:PRES");
            WriteString("*SRE 0");
            WriteString("*ESE 0");
        }

        public void Close(bool goToLocal = true)
        {
            if (dev != null)
            {
                if (goToLocal)
                {
                    if (dev is IGpibSession)
                    {
                        ((IGpibSession)dev).SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
                    }
                }
                dev.Dispose();
                dev = null;
            }
        }

        public void SetIV(int chNum, double voltage, double current)
        {
            // Set output mode, no wave form, fixed value.
            WriteString(":SOUR:VOLT:MODE FIX");

            //
            // TODO: Detect if we want CC or CV, than
            // If we awant CC than SOURC:CURR x; SENS:VOLT:PROT y!
            //
            WriteString(":SOUR:FUNC:MODE VOLT");
            WriteString($":SENS{chNum}:CURR:PROT {current.ToString(CI)}");
            WriteString($":SOUR{chNum}:VOLT {voltage.ToString(CI)}");
        }

        public void SetOVP(double ovp)
        {
            if (double.IsNaN(ovp))
            {
                WriteString(":SENS:VOLT:PROT DEF");

                if (HasOutput2)
                {
                    WriteString(":SENS2:VOLT:PROT DEF");
                }
            }
            else
            {
                WriteString(":SENS:VOLT:PROT " + ovp.ToString(CI));

                if (HasOutput2)
                {
                    WriteString(":SENS2:VOLT:PROT " + ovp.ToString(CI));
                }
            }
        }

        public void SetOCP(bool enabled)
        {
            WriteString(":OUT:PROT:STAT " + (enabled ? "1" : "0"));
        }

        public void SetOutputCompensation(OutputCompensationEnum comp)
        {
            if (comp == OutputCompensationEnum.HighCap)
            {
                WriteString(":OUT:HCAP 1");

                if (HasOutput2)
                {
                    WriteString(":OUT2:HCAP 1");
                }
            }
        }

        public void SetCurrentRange(double range) // TODO: Also this should pass the channel? 
        {
            WriteString(":SOUR:CURR:RANG:AUTO OFF");
            WriteString($":SOUR:CURR:RANG {range.ToString(CI)}");

            if (HasOutput2)
            {
                WriteString(":SOUR2:CURR:RANG:AUTO OFF");
                WriteString($":SOUR2:CURR:RANG {range.ToString(CI)}");
            }
        }

        public void EnableOutput(OutputEnum channel, bool enabled)
        {
           if (enabled)
            {
                // HIZ so voltage/current settings are not altered
                if (channel == OutputEnum.Output_1)
                {
                    WriteString(":OUTP ON");
                }
                else if (channel == OutputEnum.Output_2)
                {
                    WriteString(":OUTP2 ON");
                }
                else
                {
                    WriteString(":OUTP ON");
                    WriteString(":OUTP2 ON");
                }
            }
            else
            {
                if (channel == OutputEnum.Output_1)
                {
                    //WriteString(":OUTP:OFF:MODE HIZ");
                    WriteString(":OUTP1 OFF");
                }
                else if (channel == OutputEnum.Output_2)
                {
                    //WriteString(":OUTP2:OFF:MODE HIZ");
                    WriteString(":OUTP2 OFF");
                }
                else
                {
                    //WriteString(":OUTP:OFF:MODE HIZ");
                    //WriteString(":OUTP2:OFF:MODE HIZ");
                    WriteString(":OUTP OFF");
                    WriteString(":OUTP2 OFF");
                }
            }
        }

        public static bool SupportsIDN(string idn)
        {
            return idn.Contains(",B2961A,") || idn.Contains(",B2962A,") ||
                   idn.Contains(",B2961B,") || idn.Contains(",B2962B,");
        }

        public string GetSystemErrorStr()
        {

            string firstError = Query("SYST:ERR?");

            //
            // TODO parse all errors not only the first one!
            //
            return firstError;
        }

        public InstrumentState ReadState(bool measureCh2 = true, bool measureDVM = true)
        {
            var state = new InstrumentState();
            var timeStart = DateTime.Now;

            // Setup a measurement to read the curent I/V values.
            state.OutputEnabled1 = QueryString(":OUTP:STAT?")[0] == "1" ? true : false;

            if (HasOutput2)
            {
                state.OutputEnabled2 = QueryString(":OUTP2:STAT?")[0] == "1" ? true : false;
            }

            // Set sens mode command
            WriteString(":SENS:FUNC \"CURR\",\"VOLT\"");

            // Set default NPLC and default trigger config.
            WriteString($":SENS:NPLC 1");
          
            //
            // Setup measure triggering.
            //
            WriteString(":TRIG:SOUR TIM");
            WriteString(":TRIG:TIM 46.8e-6");
            WriteString(":TRIG:COUN 1");

            if (state.OutputEnabled1)
            {
                // Trigger the acquisition
                WriteString(":ARM:ACQ (@1)");

                // Populate the result stete.
                state.I = QueryDouble(":MEAS:CURR? (@1)")[0];
                state.V = QueryDouble(":MEAS:VOLT? (@1)")[0];
            }

            if (measureCh2 && HasOutput2)
            {
                WriteString($":SENS2:NPLC 1");

                if (state.OutputEnabled2)
                {
                    // Trigger the acquisition
                    WriteString(":ARM:ACQ (@2)");

                    state.I2 = QueryDouble(":MEAS:CURR? (@2)")[0];
                    state.V2 = QueryDouble(":MEAS:VOLT? (@2)")[0];
                }
                else
                {
                    state.V2 = double.NaN;
                    state.I2 = double.NaN;
                }
            }
            else
            {
                state.V2 = double.NaN;
                state.I2 = double.NaN;
            }

            // TODO: Handle the state.Flasg
            state.DVM = Double.NaN;
            state.Flags = DecodeFlags();
            state.IRange = QueryDouble(":SOUR:CURR:RANG?")[0];
            state.duration = DateTime.Now.Subtract(timeStart).TotalMilliseconds;

            return state;
        }

        public ProgramDetails ReadProgramDetails()
        {
            var outStateStr = new string[2];
            if (HasOutput2)
            {
                outStateStr = QueryString(":OUTP:STAT?;:OUTP2:STAT?");
            }
            else
            {
                outStateStr = QueryString(":OUTP:STAT?;");
            }
            
            var outValue1 = QueryDouble(":SOUR:VOLT?; :SOUR:CURR?");
            var outValue2 = new double[2];
            if (HasOutput2)
            {
                outValue2 = QueryDouble(":SOUR2:VOLT?; :SOUR2:CURR?");
            }

            var details = new ProgramDetails()
            {
                Enabled1 = (outStateStr[0] == "1"),
                Enabled2 = HasOutput2 ? (outStateStr[1] == "1") : false,
                V1 = outValue1[0],
                I1 = outValue1[1],
                /* OVPVal = double.Parse(parts[4], CI), */
                V2 = HasOutput2 ? outValue2[0] : double.NaN,
                I2 = HasOutput2 ? outValue2[1] : double.NaN,
                HasDVM = HasDVM,
                HasOutput2 = HasOutput2,
                HasOVP = this.HasOVP,
                ID = ID
            };

            var currProtRegBitsStr = QueryString(":STAT:QUES:CURR:COND?")[0];
            var currProtRegBits = ReadHexString(currProtRegBitsStr);
            details.OCP = (currProtRegBits & 0x1) != 0 || (currProtRegBits & 0x1) != 0;

            var voltProtRegBitsStr = QueryString(":STAT:QUES:VOLT:COND?")[0];
            var voltProtRegBits = ReadHexString(voltProtRegBitsStr);
            details.OVP = (voltProtRegBits & 0x1) != 0 || (voltProtRegBits & 0x1) != 0;
            details.OVPVal = QueryDouble(":SENS:VOLT:PROT?")[0];

            // Maximums
            details.I1Ranges = GetCurrentRanges();
           
            var outMaxValues = QueryDouble(":SOUR:VOLT? MAX;:SOUR:CURR? MAX");
            details.MaxV1 = outMaxValues[0];
            details.MaxI1 = outMaxValues[1];
           
            if (HasOutput2)
            {
                outMaxValues = QueryDouble(":SOUR2:VOLT? MAX;:SOUR2:CURR? MAX");
                details.MaxV2 = outMaxValues[0];
                details.MaxI2 = outMaxValues[1];
            }

            details.I1Range = QueryDouble(":SOUR:CURR:RANGE?")[0];

            /*string detector = Query("SENSE:CURR:DET?").Trim();
            switch (detector)
            {
                case "DC": details.Detector = CurrentDetectorEnum.DC; break;
                case "ACDC": details.Detector = CurrentDetectorEnum.ACDC; break;
                default: throw new Exception();
            }*/
            details.Detector = CurrentDetectorEnum.DC;

            return details;
        }

        // LOGGING
        public void SetupLogging(OutputEnum channel, SenseModeEnum mode, double interval)
        {
            int numPoints = 4096;
            double acqInterval = interval;
            if (interval == 0)
            {
                acqInterval = 15.6e-6;
            }

            var chNum = (channel == OutputEnum.Output_1) ? 1 : 2;

            OutputStateBeforeMeasurement = GetOutputState();

            string modeString = "";
            switch (mode)
            {
                case SenseModeEnum.CURRENT: modeString = "CURR"; break;
                case SenseModeEnum.VOLTAGE: modeString = "VOLT"; break;
                case SenseModeEnum.DVM: modeString = "DVM"; break;
                default: throw new InvalidOperationException("Unknown transient measurement mode");
            }

            // double nplc = (interval - 400e-6) * 0.9;
            double nplc = interval * LineFreq;
            if (nplc < 4E-4)
                nplc = 4E-4;
            else if (nplc > 100)
                nplc = 100;
            else if (nplc > 1)
                nplc = Math.Floor(nplc);
                          
            WriteString($":SENS:FUNC \"{modeString}\"");

            // 
            // Setup NPLC
            //
            WriteString($":SENS:{modeString}:NPLC:AUTO 0");
            WriteString($":SENS:{modeString}:NPLC {nplc}");
            WriteString($":SENS:{modeString}:NPLC {nplc}");
           

            // WriteString(":TRIG:COUN INF");

            if (UseTraceBuffer)
            {
                numPoints = NUM_OF_TRACE_DATA_POINTS;

                // Select the measure to perform.
                WriteString($":form:elem:sens {modeString},time");

                // Lock writing to the buffer, so we can reset it.
                WriteString($":TRAC{chNum}:FEED:CONT NEV");
                // Clear all buffer value before starting
                WriteString($":TRAC{chNum}:CLE");
                // COnfig the number of samples to acquire
                WriteString($":TRAC{chNum}:POINts {numPoints}");
                // Select the data to be used.
                WriteString(":TRAC:FEED SENS");
                // Enable writes to the buffer
                WriteString(":TRAC:FEED:CONT NEXT");
            }
            else
            {
                //
                // Setup measure trigger. 
                //
                WriteString($":TRIG{chNum}:SOUR TIM");
                WriteString($":TRIG{chNum}:TIM {acqInterval}");
                WriteString($":TRIG{chNum}:COUN {numPoints}");

                // Start the measure by trigger it.
                // WriteString(":init (@1)"); // TODO: Handle channel 2...
                WriteString($":ARM:ACQ (@{chNum})");
            }

            LoggingStopwatch = new System.Diagnostics.Stopwatch();
            LoggingStopwatch.Start();
            Query("*OPC?");
        }

        public LoggerDatapoint[] MeasureLoggingPoint(OutputEnum selChannel, SenseModeEnum mode)
        {
            var ret = new LoggerDatapoint();
            var chNum = (selChannel == OutputEnum.Output_1) ? 1 : 2;

            if (UseTraceBuffer)
            {
                var numOfPoints = QueryInt($":TRAC{chNum}:POINts:ACTual?")[0];

                // Only process data when the buffer is full.
                if (numOfPoints == NUM_OF_TRACE_DATA_POINTS)
                {
                    // Read all data.
                    WriteString(":TRAC:DATA?");
                    for (int idx = 0; idx < numOfPoints; ++idx)
                    {
                        var lineStr = dev.RawIO.ReadString();
                        Debug.WriteLine(lineStr);
                    }

                    // Disable the tracebuff until next start action
                    WriteString($":TRAC{chNum}:FEED:CONT NEV");
                }
            }
            else
            {
                // Start the acquisiton
                switch (mode)
                {
                    case SenseModeEnum.CURRENT:
                        {
                            // response = ReadWrite($":fetc:arr:volt? (@{channel})").Trim();
                            ret.Mean = QueryDouble($":MEAS:CURR? (@{chNum})")[0];

                            // Do not support MATH op simply.
                            ret.Min = ret.Max = ret.RMS = ret.Mean;
                        }
                        break;

                    case SenseModeEnum.VOLTAGE:
                        {
                            // response = ReadWrite($":fetc:arr:volt? (@{channel})").Trim();
                            ret.Mean = QueryDouble($":MEAS:VOLT? (@{chNum})")[0];

                            // Do not support MATH op simply.
                            ret.Min = ret.Max = ret.RMS = ret.Mean;
                        }
                        break;
                }
            }

            ret.t = LoggingStopwatch.Elapsed.TotalSeconds;
            ret.RecordTime = DateTime.Now;

            return new LoggerDatapoint[] { ret };
        }

        public bool IsMeasurementFinished()
        {
            return (QueryInt("*ESR?")[0] & 1) == 1;
        }

        public void RestoreOutState(OutputEnum slectedChannel)
        {
            // TODO: Handle selectedChannel!

            //
            // Restore the instrument output to the old state.
            // the instrument automatically turn on the output
            // when the meas command is triggered. We want to 
            // return the output state to what it was before any
            // acquire/logging command was issued
            //
            if (OutputStateBeforeMeasurement != OutputEnum.Output_None)
            {
                if ((OutputStateBeforeMeasurement & OutputEnum.Output_1) == 0)
                {
                    EnableOutput(OutputEnum.Output_1, false);
                }

                if (HasOutput2)
                {
                    if ((OutputStateBeforeMeasurement & OutputEnum.Output_2) == 0)
                    {
                        EnableOutput(OutputEnum.Output_2, false);
                    }
                }
            }
            else
            {
                //
                // Both channel was disabled so disable them again
                // 
                EnableOutput(OutputEnum.Output_1, false);
                EnableOutput(OutputEnum.Output_2, false);
            }
        }

        public void AbortMeasurement()
        {
            Query("ABORT;*OPC?");
        }

        public void SetDisplayState(DisplayState state)
        {
            switch (state)
            {
                case DisplayState.ON:
                    WriteString(":DISP:ENAB ON");
                    break;

                case DisplayState.OFF:
                    WriteString(":DISP:ENAB OFF");
                    break;
            }
        }

        public void SetDisplayText(string val, bool clearIt = false)
        {
            if (clearIt)
            {
                WriteString(":DISP:TEXT:STAT 0");
            }
            else
            {
                WriteString(":DISP:TEXT:STAT 1");
            }

            WriteString($":DISP:TEXT:DATA \"{val}\"");
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

            if (outStateStr[1] == "1")
            {
                result |= OutputEnum.Output_2;
            }

            return result;
        }

        // TODO IMPLEMENT        
        public void StartTransientMeasurement(
            OutputEnum channel, SenseModeEnum mode, int numPoints = 4096,
            double interval = 1.56E-05, double level = double.NaN,
            double hysteresis = 0, int triggerCount = 1,
            TriggerSlopeEnum triggerEdge = TriggerSlopeEnum.Positive,
            int triggerOffset = 0,
            MeasWindowType windowType = MeasWindowType.Null)
        {
            Debug.WriteLine("StartTransientMeasurement: NOT IMPLEMENT!");

            OutputStateBeforeMeasurement = GetOutputState();
        }

        public MeasArray FinishTransientMeasurement(
            OutputEnum channel, SenseModeEnum mode, int triggerCount = 1)
        {
            Debug.WriteLine("FinishTransientMeasurement: NOT IMPLEMENT!");
            return new MeasArray();
        }

        public void ClearProtection()
        {
            Debug.WriteLine("ClearProtection: NOT IMPLEMENT!");
        }

        // NOT SUPPORTED
        public void SetCurrentDetector(CurrentDetectorEnum detector)
        {
            Console.WriteLine("SetCurrentDetector: NOT SUPPORTED!");
        }
        public void SetMeasureWindowType(MeasWindowType type)
        {
            Debug.WriteLine("SetMeasureWindowType: NOT SUPPORTED!");
        }

        #endregion
    }
}