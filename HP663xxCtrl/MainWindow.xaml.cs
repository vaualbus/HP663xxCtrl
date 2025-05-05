using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ZedGraph;

namespace HP663xxCtrl {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {

        MainWindowVm VM;

        bool ZedgraphLoggingMode = false;
        DateTime LogStartTime;

        AcquisitionData AcqDataRecord = null;

        DisplayState DisplayState = DisplayState.OFF;

        public MainWindow() {
            InitializeComponent();
            ZedGraphControl zgc = (ZedGraphControl)ZedGraphHost.Child;
            float zgcSizeScale = 1.5f;
            zgc.GraphPane.IsFontsScaled = false;
            zgc.GraphPane.IsPenWidthScaled = false;

            zgc.GraphPane.Margin.All = zgc.GraphPane.Margin.Top * zgcSizeScale;

            zgc.GraphPane.XAxis.Scale.FontSpec.Size *= zgcSizeScale;
            zgc.GraphPane.XAxis.Title.FontSpec.Size *= zgcSizeScale;

            zgc.GraphPane.YAxis.Title.FontSpec.Size *= zgcSizeScale;
            zgc.GraphPane.YAxis.Scale.FontSpec.Size *= zgcSizeScale;

            zgc.GraphPane.Title.IsVisible = false;
            zgc.GraphPane.XAxis.Title.Text = "Time (s)";

            VM = (MainWindowVm)DataContext;
            VM.Window = this;
        }

        System.Drawing.Color[] CurveColors = new System.Drawing.Color[] {
            System.Drawing.Color.Black, System.Drawing.Color.Red,
            System.Drawing.Color.Blue, System.Drawing.Color.Green
        };

        private OutputEnum OldOutState;
        private bool RestoreStateRequired;
        private OutputEnum SelectedLogChannel;
        private OutputEnum SelectedAcqChannel;

        private OutputEnum GetOutState()
        {
            return VM.InstWorker.GetOutputState();
        }

        private void RestoreState()
        {
            if (OldOutState != OutputEnum.Output_None)
            {
                if ((OldOutState & OutputEnum.Output_1) != 0)
                {
                    EnableOutputCheckbox.IsChecked = true;
                }

                if ((OldOutState & OutputEnum.Output_2) != 0)
                {
                    EnableOutputCheckbox.IsChecked = true;
                }

                OldOutState = OutputEnum.Output_None;
            } 

            //
            // Restore the last check button item for CH1/CH2 selection
            //
            if (ControlTabs.SelectedItem.Equals(AcquisitionTabItem))
            {
                if (SelectedAcqChannel != OutputEnum.Output_None)
                {
                    if (SelectedAcqChannel == OutputEnum.Output_1)
                    {
                        AcqOutChannel1.IsChecked = true;
                        AcqOutChannel2.IsChecked = false;
                    }
                    else
                    {
                        AcqOutChannel1.IsChecked = false;
                        AcqOutChannel2.IsChecked = true;
                    }

                    SelectedAcqChannel = OutputEnum.Output_None;
                }
            }
            else if (ControlTabs.SelectedItem.Equals(LoggingTabItem))
            {
                if (SelectedLogChannel != OutputEnum.Output_None)
                {
                    if (SelectedLogChannel == OutputEnum.Output_1)
                    {
                        LogOutChannel1.IsChecked = true;
                        LogOutChannel2.IsChecked = false;
                    }
                    else
                    {
                        LogOutChannel1.IsChecked = false;
                        LogOutChannel2.IsChecked = true;
                    }

                    SelectedLogChannel = OutputEnum.Output_None;
                }
            }
        }

        private void GoButton_Click(object sender, RoutedEventArgs e) {

            if (VM.InstThread != null || VM.InstWorker != null)
                return;
           
            VM.InstWorker = new InstrumentWorker(AddressComboBox.Text);
            VM.InstWorker.WorkerDone += delegate(object sender2, EventArgs args)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    VM.InstThread = null;
                    VM.InstWorker = null;
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                }));
            };

            VM.InstWorker.NewState += delegate(object sender2, InstrumentState state)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    UpdateStateLabels(sender2, state);
                }));
            };

            VM.InstWorker.DataAcquired += delegate(object sender2, MeasArray measArray)
            {
                Dispatcher.BeginInvoke((Action)(() => { HandleDataAcquired(sender2, measArray); }));
            };

            VM.InstWorker.ProgramDetailsReadback += delegate(object sender2, ProgramDetails details)
            {
                Dispatcher.BeginInvoke((Action)(() => { HandleProgramDetailsReadback(sender2, details); }));
            };

            VM.InstWorker.LogerDatapointAcquired += delegate(object sender2, LoggerDatapoint point)
            {
                Dispatcher.BeginInvoke((Action)(() => { HandleLogDatapoint(sender2, point); }));
            };

            VM.InstWorker.StateChanged += delegate (object sender2, InstrumentWorker.StateEventData eventData)
            {
                Dispatcher.BeginInvoke((Action)(() =>
                {

                    switch (eventData.State)
                    {
                        case InstrumentWorker.StateEnum.Connected:

                            ConnectButton.IsEnabled = false;
                            DisconnectButton.IsEnabled = true;

                            ConnectionStatusBarItem.Content = "CONNECTED";
                            AcquireButton.IsEnabled = true;
                            ApplyProgramButton.IsEnabled = true;
                            StopAcquireButton.IsEnabled = false;
                            ClearProtectionButton.IsEnabled = true;
                            LogButton.IsEnabled = true;
                            StopLoggingButton.IsEnabled = false;
                            AddressComboBox.IsEnabled = false;

                            if (RestoreStateRequired)
                            {
                                //
                                // We are entering here form a stop button click,
                                // we do not need to change the enable/disable state of buttons
                                // but we just need to update their value accordingly.
                                // NOTE: This can be removed if we not call connect in this situation.
                                //
                                RestoreState();

                                RestoreStateRequired = false;
                            }
                            else
                            {
                                LogOutChannel1.IsChecked = true;
                                AcqOutChannel1.IsChecked = true;

                                if (eventData.HasTwoMeasureChannels)
                                {
                                    LogOutChannel2.IsEnabled = true;
                                    AcqOutChannel2.IsEnabled = true;
                                }

                                if (eventData.HasSeprateEnableChannels)
                                {
                                    Enable2OutputCheckbox.IsEnabled = true;
                                }
                            }

                            // Enable saving, if measurement data is non-null
                            if (AcqDataRecord != null && AcqDataRecord.DataSeries.Count != 0)
                                SaveAcquireButton.IsEnabled = true;
                            break;
                        case InstrumentWorker.StateEnum.ConnectionFailed:
                            //
                            // Connection to instruments has failed, error out and
                            // wait for a new connection.
                            //
                            // Cannot connect to instruments.
                            MessageBox.Show($"Cannot connect to instruments: {AddressComboBox.Text}!");

                            AcquireButton.IsEnabled = false;
                            ApplyProgramButton.IsEnabled = false;
                            StopAcquireButton.IsEnabled = false;
                            ClearProtectionButton.IsEnabled = false;
                            LogButton.IsEnabled = false;
                            ModelStatusBarItem.Content = "-----";
                            AddressComboBox.IsEnabled = true;
                            ConnectButton.IsEnabled = true;
                            DisconnectButton.IsEnabled = false;

                            ConnectionStatusBarItem.Content = "CONNECTION FAILED";
                            break;
                        case InstrumentWorker.StateEnum.Disconnected:
                            ConnectionStatusBarItem.Content = "DISCONNECTED";
                            AcquireButton.IsEnabled = false;
                            ApplyProgramButton.IsEnabled = false;
                            StopAcquireButton.IsEnabled = false;
                            ClearProtectionButton.IsEnabled = false;
                            LogButton.IsEnabled = false;
                            ModelStatusBarItem.Content = "-----";
                            AddressComboBox.IsEnabled = true;
                            VM.InstWorker.InstrumentIsConnected = false;
                            break;
                        case InstrumentWorker.StateEnum.Measuring:
                            ConnectionStatusBarItem.Content = "MEASURING"; break;
                    }
                }));
            };

            VM.InstThread = new Thread(VM.InstWorker.ThreadMain);
            VM.InstThread.IsBackground = true;

            VM.InstThread.Name = "Instrument Worker";
            VM.InstThread.Start();
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e) {
            ConnectionStatusBarItem.Content = "DISCONNECTING";
            VM.InstWorker.RequestShutdown();
        }

        private OutputEnum GetSelectedChannel()
        {
            // Decide which tab is clicked
            var result = OutputEnum.Output_None;
            if (ControlTabs.SelectedItem.Equals(AcquisitionTabItem))
            {
                result = AcqOutChannel1.IsChecked.Value ? OutputEnum.Output_1 : OutputEnum.Output_2;
            }
            else if (ControlTabs.SelectedItem.Equals(LoggingTabItem))
            {
                result = LogOutChannel1.IsChecked.Value ? OutputEnum.Output_1 : OutputEnum.Output_2;
            }

            Debug.WriteLine(result);

            return result;
        }

        private void UpdateStateLabels(object sender, InstrumentState state)
        {
            NumberFormatInfo nfi = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
            nfi.NumberNegativePattern = 1;
            Ch1VLabel.Text = state.V.ToString("N3", nfi).PadLeft(7) + " V";

            if (state.IRange > 1.1)
            {
                Ch1ILabel.Text = state.I.ToString("N4", nfi).PadLeft(7) + "  A";
            }
            else
            {
                Ch1ILabel.Text = (state.I * 1000).ToString("N3", nfi).PadLeft(6) + " mA";
            }

            Ch1StatusLabel.Text =
                 ((state.Flags.Unregulated) ? "UNR" : "  ") +
                " " +
                (state.OVP ? "OVP" : "   ") +
                " " +
                (state.OCP ? "OCP" : "   ") +
                " " +
                (state.Flags.CV ? "CV" : "  ") +
                " " +
                (
                    state.Flags.CCPositive ? "CC+" : (
                    (state.Flags.CCNegative ? "CC-" : (
                    state.Flags.CC ? "CC " : "   ")
                    )
                    )
                );
            if (!double.IsNaN(state.V2))
            {
                Ch2VLabel.Text = state.V2.ToString("N3", nfi).PadLeft(7) + " V";
                Ch2ILabel.Text = state.I2.ToString("N4", nfi).PadLeft(6) + "  A"; // Always in amps
            }
            else
            {
                Ch2VLabel.Text = "--.---" + " -";
                Ch2ILabel.Text = "--.----" + "  -";
            }
            Ch2StatusLabel.Text =
                 (state.Flags.Unregulated2 ? "UNR" : "  ") +
                " " +
                (state.OVP ? "   " : "   ") + // CH2 doesn't have OVP???
                " " +
                (state.OCP ? "OCP" : "   ") +
                " " +
                ((state.Flags.CV2) ? "CV" : "  ") +
                " " +
                (state.Flags.CC2 ? "CC " : "   ");

            DVMVLabel.Text = state.DVM.ToString("N3", nfi);
        }

        private void HandleDataAcquired(object sender, MeasArray result) {
            // Add data to the data record, and overwrite the sampling period (it should be the same
            // for all datapoints)
            AcqDataRecord.SamplingPeriod = result.TimeInterval;
            if (result.Data != null)
            {
                AcqDataRecord.DataSeries.AddRange(result.Data);
            }

            ZedGraphControl zgc = (ZedGraphControl)ZedGraphHost.Child;
            zgc.GraphPane.XAxis.Title.Text = "Time";
            
            zgc.GraphPane.YAxis.Title.Text = "Current";
            double[] xlist = Enumerable.Range(AcqDataRecord.AcqDetails.SampleOffset, result.Data[0].Length).Select(x => (double)((x) * result.TimeInterval)).ToArray();
            for (int i = 0; i < result.Data.Length; i++) {
                zgc.GraphPane.AddCurve("Acq" + (zgc.GraphPane.CurveList.Count).ToString(),
                    xlist, result.Data[i],
                    CurveColors[i % (CurveColors.Length)], SymbolType.None);
            }
            zgc.AxisChange();
            zgc.Invalidate();
        }

        private void OnMenuItem_Exit(object sender, RoutedEventArgs e) {
            this.Close();
        }

        void HandleProgramDetailsReadback(object sender, ProgramDetails details) {
            OCPCheckbox.IsChecked = details.OCP;
            VM.V1 = details.V1;
            VM.I1 = details.I1;
            VM.V2 = details.V2;
            VM.I2 = details.I2;
            VM.HasChannel2 = details.HasOutput2;
            VM.HasDVM = details.HasDVM;
            VM.HasOVP = details.HasOVP;

            VM.I1Ranges = details.I1Ranges.Select(x => new Current() { I = x }).ToArray();


            EnableOutputCheckbox.IsChecked = details.Enabled1;

            if (Enable2OutputCheckbox.IsEnabled)
            {
                Enable2OutputCheckbox.IsChecked = details.Enabled2;
            }

            OVPCheckbox.IsChecked = details.OVP;
            VM.OVPLevel = details.OVPVal;
            var IDSplit = details.ID.Split(new char[] {','});
            ModelStatusBarItem.Content = IDSplit[1] + " (" + IDSplit[2].ToUpper() + ")";

            // Limits for programming
            CH1VTextBox.MaxValue = details.MaxV1;
            CH1ITextBox.MaxValue = details.MaxI1;
            if (details.HasOutput2) {
                CH2VTextBox.MaxValue = details.MaxV2;
                CH2ITextBox.MaxValue = details.MaxI2;
            }

             for(int i=0; i< CurrentRangeComboBox.Items.Count; i++) {
                 if(details.I1Range ==  ((Current)CurrentRangeComboBox.Items[i]).I)
                     CurrentRangeComboBox.SelectedIndex = i;
            }

            switch (details.Detector) {
                case CurrentDetectorEnum.DC: ACDCDetectorComboBox.SelectedIndex = 0; break;
                case CurrentDetectorEnum.ACDC: ACDCDetectorComboBox.SelectedIndex = 1; break;
            }
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {

            if (VM.InstWorker != null && e.AddedItems.Count > 0) {
                Current item = (Current)e.AddedItems[0];
                VM.InstWorker.RequestIRange(item.I);
            }

        }

        void HandleLogDatapoint(object sender, LoggerDatapoint dp) {
            if(!double.IsNaN(dp.Min))
                zgc.GraphPane.CurveList[0].AddPoint(
                    dp.t,
                    dp.Min);
            if(!double.IsNaN(dp.Mean))
                zgc.GraphPane.CurveList[1].AddPoint(
                    dp.t,
                    dp.Mean);
            if(!double.IsNaN(dp.Max))
            zgc.GraphPane.CurveList[2].AddPoint(
                dp.t,
                dp.Max);
            if(!double.IsNaN(dp.RMS))
                zgc.GraphPane.CurveList[3].AddPoint(
                    dp.t,
                    dp.RMS);
            zgc.AxisChange();
            zgc.Invalidate();
        }
        private void LogButton_Click(object sender, RoutedEventArgs e) {

            var selectedChannel = GetSelectedChannel();
            if (VM.InstWorker != null) {
                LogStartTime = DateTime.Now;
                VM.InstWorker.StopAcquireRequested = false;
                AcquireButton.IsEnabled = false;
                ApplyProgramButton.IsEnabled = false;
                ClearProtectionButton.IsEnabled = false;
                StopLoggingButton.IsEnabled = true;
                zgc.GraphPane.CurveList.Clear();
                zgc.GraphPane.AddCurve("Min", new double[0], new double[0], System.Drawing.Color.Blue);
                zgc.GraphPane.AddCurve("Mean", new double[0], new double[0], System.Drawing.Color.Black);
                zgc.GraphPane.AddCurve("Max", new double[0], new double[0], System.Drawing.Color.Red);
                zgc.GraphPane.AddCurve("RMS", new double[0], new double[0], System.Drawing.Color.Red);

                zgc.GraphPane.CurveList[0].IsVisible = LoggerMinCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[1].IsVisible = LoggerMeanCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[2].IsVisible = LoggerMaxCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[3].IsVisible = LoggerRMSCheckBox.IsChecked.Value;
                ZedgraphLoggingMode = true;

                SenseModeEnum mode;
                if (LogVoltageRadioButton.IsChecked.Value) {
                    mode = SenseModeEnum.VOLTAGE;
                    zgc.GraphPane.YAxis.Title.Text = "Voltage (V)";
                } else if (LogCurrentRadioButton.IsChecked.Value) {
                    mode = SenseModeEnum.CURRENT;
                    zgc.GraphPane.YAxis.Title.Text = "Current (A)";
                } else {
                    mode = SenseModeEnum.DVM;
                    zgc.GraphPane.YAxis.Title.Text = "Voltage (V)";
                }

                double interval = 0;
                if (false == double.TryParse(LogInterval.Text, out interval))
                {
                    // Value is invalid, get default value
                    interval = 0;
                }
                
                VM.InstWorker.RequestLog(GetSelectedChannel(), mode, interval);

                SelectedLogChannel = selectedChannel;
                OldOutState = GetOutState();
            }
        }

        private void StopLoggingButton_Click(object sender, RoutedEventArgs e) {
            StopLoggingButton.IsEnabled = false;
            VM.InstWorker.RequestRestoreOutState(GetSelectedChannel());
            VM.InstWorker.StopAcquireRequested = true;
            RestoreStateRequired = true;
        }

        private MeasWindowType GetMeasWindowType(string itemSter)
        {
            MeasWindowType retType;
            if (itemSter == "Hanning")
            {
                retType = MeasWindowType.Hanning;
            }
            else if (itemSter == "Rect" )
            {
                retType = MeasWindowType.Rect;
            }
            else
            {
                retType = MeasWindowType.Null;
            }

            return retType;
        }

        private void AcquireButton_Click(object sender, RoutedEventArgs e) {

            var selectedChannel = GetSelectedChannel();
            if (VM.InstWorker != null) {
                var errors = GetTreeErrors(AcquisitionTabItem);

                if (errors.Count > 0) {
                    MessageBox.Show("Invalid Acquisition/trigger settings.");
                    return;
                }

                zgc.GraphPane.CurveList.Clear();
                ZedgraphLoggingMode = false;
                AcquireButton.IsEnabled = false;
                ApplyProgramButton.IsEnabled = false;
                ClearProtectionButton.IsEnabled = false;
                StopAcquireButton.IsEnabled = true;
                LogButton.IsEnabled = false;
                VM.InstWorker.StopAcquireRequested = false;
                SaveAcquireButton.IsEnabled = false;

                InstrumentWorker.AcquireDetails details = new InstrumentWorker.AcquireDetails();
                details.NumPoints = VM.AcqNumPoints;
                double duration = VM.AcqDuration;
                details.Interval = duration / details.NumPoints;
                details.Level = VM.TriggerLevel;
                details.SampleOffset = VM.TriggerOffset;
                details.TriggerHysteresis = VM.TriggerHysteresis;

                if (AcqVoltageRadioButton.IsChecked.Value) {
                    details.SenseMode = SenseModeEnum.VOLTAGE;
                    zgc.GraphPane.YAxis.Title.Text = "Voltage (V)";
                } else if (AcqCurrentRadioButton.IsChecked.Value) {
                    details.SenseMode = SenseModeEnum.CURRENT;
                    zgc.GraphPane.YAxis.Title.Text = "Current (A)";
                } else {
                    details.SenseMode = SenseModeEnum.DVM;
                    zgc.GraphPane.YAxis.Title.Text = "Voltage (V)";
                }

                details.SegmentCount = VM.AcqSegments;

                switch ((string)((ComboBoxItem)TriggerComboBox.SelectedItem).Tag) {
                    case "IMMED": details.triggerEdge = TriggerSlopeEnum.Immediate; break;
                    case "POS": details.triggerEdge = TriggerSlopeEnum.Positive; break;
                    case "NEG": details.triggerEdge = TriggerSlopeEnum.Negative; break;
                    case "EITHER": details.triggerEdge = TriggerSlopeEnum.Either; break;
                    default: throw new Exception();
                }

                details.windowType = GetMeasWindowType((string)((ComboBoxItem)MeasWindowTypeBox.SelectedItem).Tag);

                details.SelectedChannel = GetSelectedChannel();

                AcqDataRecord = VM.InstWorker.RequestAcquire(details);
                
                SelectedAcqChannel = selectedChannel;
                OldOutState = GetOutState();
            }
        }

        private void StopAcquireButton_Click(object sender, RoutedEventArgs e) {
            StopAcquireButton.IsEnabled = false;
            VM.InstWorker.RequestRestoreOutState(GetSelectedChannel());
            VM.InstWorker.StopAcquireRequested = true;

            RestoreStateRequired = true;
        }
        private void SaveAcquireButton_Click(object sender, RoutedEventArgs e) {
            Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog();
            sfd.DefaultExt = ".csv";
            sfd.Filter = "CSV file (.csv)|*.csv|All Files (*.*)|*.*"; // Filter files by extension
            if (sfd.ShowDialog(this) != true) {
                return;
            }
            try {
                using (StreamWriter sw = new StreamWriter(sfd.FileName)) {
                    // Use current culture; output files depend on the culture of the user's machine!
                    string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                    // Write header stuff
                    // Excel doesn't like when the file starts with "ID", so start with something else
                    sw.WriteLine("StartTime" + sep + AcqDataRecord.StartAcquisitionTime.ToString());
                    sw.WriteLine("ID" + sep + "\"" + AcqDataRecord.ProgramDetails.ID + "\"");

                    // Program Details
                    sw.WriteLine("OutputEnabled1" + sep + AcqDataRecord.ProgramDetails.Enabled1.ToString());

                    if (Enable2OutputCheckbox.IsEnabled)
                    {
                        sw.WriteLine("OutputEnabled2" + sep + AcqDataRecord.ProgramDetails.Enabled2.ToString());
                    }

                    sw.WriteLine("Detector" + sep + AcqDataRecord.ProgramDetails.Detector.ToString());
                    sw.WriteLine("V1" + sep + AcqDataRecord.ProgramDetails.V1.ToString());
                    sw.WriteLine("I1" + sep + AcqDataRecord.ProgramDetails.I1.ToString());
                    // Output second channel, even if it doesn't exist, to make header equal lengths always
                    sw.WriteLine("V2" + sep + (AcqDataRecord.ProgramDetails.HasOutput2 ?
                        AcqDataRecord.ProgramDetails.V2.ToString() : ""));
                    sw.WriteLine("I2" + sep + (AcqDataRecord.ProgramDetails.HasOutput2 ?
                        AcqDataRecord.ProgramDetails.I2.ToString() : ""));
                    sw.WriteLine("OCP" + sep + AcqDataRecord.ProgramDetails.OCP.ToString());
                    sw.WriteLine("OVP" + sep + (AcqDataRecord.ProgramDetails.OVP ?
                        AcqDataRecord.ProgramDetails.OVPVal.ToString() : "False"));
                    sw.WriteLine("CurrentRange" + sep + AcqDataRecord.ProgramDetails.I1Range.ToString());
                    // Trigger Details
                    sw.WriteLine("SamplingPeriod" + sep + AcqDataRecord.SamplingPeriod.ToString());
                    sw.WriteLine("NumPoints" + sep + AcqDataRecord.AcqDetails.NumPoints.ToString());
                    sw.WriteLine("SenseMode" + sep + AcqDataRecord.AcqDetails.SenseMode.ToString());
                    sw.WriteLine("TriggerEdge" + sep + AcqDataRecord.AcqDetails.triggerEdge.ToString());
                    sw.WriteLine("TriggerLevel" + sep + AcqDataRecord.AcqDetails.Level.ToString());
                    sw.WriteLine("TriggerHyst" + sep + AcqDataRecord.AcqDetails.TriggerHysteresis.ToString());
                    sw.WriteLine("SampleOffset" + sep + AcqDataRecord.AcqDetails.SampleOffset.ToString());

                    // Data Header
                    sw.Write("time");
                    for (int i = 0; i < AcqDataRecord.DataSeries.Count; i++)
                        sw.Write(sep + "Acq" + i.ToString());
                    sw.WriteLine("");
                    // Data
                    double t = AcqDataRecord.SamplingPeriod * AcqDataRecord.AcqDetails.SampleOffset;
                    for (int i = 0; i < AcqDataRecord.DataSeries[0].Length; i++) {
                        sw.Write(t.ToString() + sep);
                        sw.WriteLine(String.Join(sep,
                            AcqDataRecord.DataSeries.Select(x => x[i].ToString())));
                        t += AcqDataRecord.SamplingPeriod;
                    }
                }
            } catch (IOException ioex) {
                MessageBox.Show(this, "IO Exception during write",
                    "IO Exception happened during write (Abort, retry, fail?):\n\n" +
                    ioex.Message);
            }
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
            if (VM.InstWorker != null) {
                VM.InstWorker.RequestShutdown();
                // Hack... probably some better way to do this
                System.Threading.Thread.Sleep(500);
            }
        }

        private void ApplyProgramButton_Click(object sender, RoutedEventArgs e) {
            ProgramDetails details = new ProgramDetails();
            string ParseError = "";
            var blah = OVPLevelTextBox;
            if (VM.InstWorker == null)
                return;


            details.OCP = OCPCheckbox.IsChecked.Value;
            details.OVP = OVPCheckbox.IsChecked.Value;

            details.Enabled1 = EnableOutputCheckbox.IsChecked.Value;

            if (Enable2OutputCheckbox.IsEnabled)
            {
                details.Enabled2 = Enable2OutputCheckbox.IsChecked.Value;
            }

            if (Validation.GetHasError(CH1VTextBox) || Validation.GetHasError(CH1ITextBox)) {
                ParseError = ParseError + "Ch 1 Voltage or current is invalid.\n";
            }

            details.V1 = VM.V1;
            details.I1 = VM.I1;

            if (VM.HasChannel2) {

                if (Validation.GetHasError(CH2VTextBox) || Validation.GetHasError(CH2ITextBox)) {
                    ParseError = ParseError + "Ch 2 Voltage or current is invalid.\n";
                }

                details.V2 = VM.V2;
                details.I2 = VM.I2;
                details.HasOutput2 = true;
            }
            
            details.OVPVal = double.NaN;
            if (details.OVP) {
                if (Validation.GetHasError(OVPLevelTextBox)) {
                    ParseError = ParseError + "OVP level is invalid.\n";
                    // 22 V is the max valid value on a 66309D
                }
            }

            if (ParseError != "") {
                MessageBox.Show(ParseError, "Invalid Data Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            VM.InstWorker.RequestProgram(details);
        }

        private void ClearProtectionButton_Click(object sender, RoutedEventArgs e) {
            VM.InstWorker.RequestClearProtection();
        }

        private void LoggerCurveCheckBox_Checked(object sender, RoutedEventArgs e) {
            if (ZedgraphLoggingMode && zgc.GraphPane.CurveList.Count == 4) {
                zgc.GraphPane.CurveList[0].IsVisible = LoggerMinCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[1].IsVisible = LoggerMeanCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[2].IsVisible = LoggerMaxCheckBox.IsChecked.Value;
                zgc.GraphPane.CurveList[3].IsVisible = LoggerRMSCheckBox.IsChecked.Value;
                zgc.Invalidate();
            }

        }
        // From http://stackoverflow.com/questions/127477/detecting-wpf-validation-errors
        private List<ValidationError> GetTreeErrors(DependencyObject obj, bool childrenOnly=false) {
            // The dependency object is valid if it has no errors and all
            // of its children (that are dependency objects) are error-free.
            List<ValidationError> errors = new List<ValidationError>();
            errors.AddRange(Validation.GetErrors(obj));

            errors.AddRange(LogicalTreeHelper.GetChildren(obj)
            .OfType<DependencyObject>()
            .SelectMany(x => GetTreeErrors(x)));
            return errors;
        }

        private void ACDCDetectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (VM != null && VM.InstWorker != null) {
                switch ((string)((ComboBoxItem)e.AddedItems[0]).Tag) {
                    case "DC": VM.InstWorker.RequestACDCDetector(CurrentDetectorEnum.DC); break;
                    case "ACDC": VM.InstWorker.RequestACDCDetector(CurrentDetectorEnum.ACDC); break;
                }
            }
        }

        private void MeasWindowType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VM != null && VM.InstWorker != null)
            {
                switch ((string)((ComboBoxItem)e.AddedItems[0]).Tag)
                {
                    case "Rect": VM.InstWorker.SetMeasureWindowType(MeasWindowType.Rect); break;
                    case "Hanning": VM.InstWorker.SetMeasureWindowType(MeasWindowType.Hanning); break;
                }
            }
        }

        private void SendText_Btn_Click(object sender, RoutedEventArgs e)
        {
            if (VM.InstWorker != null)
            {
                VM.InstWorker.SendTextToDisplay(DisplayTextToSend.Text);
            }
        }

        private void ClearDisplay_Btn_Click(object sender, RoutedEventArgs e)
        {
            if (VM.InstWorker != null)
            {
                VM.InstWorker.ClearDisplay();
            }
        }

        private void DisableDisplay_Btn_Click(object sender, RoutedEventArgs e)
        {
            if (VM.InstWorker != null)
            {
                //
                // If we disable the display set the btn text to on.
                //
                if (DisplayState == DisplayState.ON)
                {
                    DisableDisplay_Btn.Content = "Off";
                    DisplayState = DisplayState.OFF;
                }
                else
                {
                    DisableDisplay_Btn.Content = "On";
                    DisplayState = DisplayState.ON;
                }

                UpdateLayout();

                VM.InstWorker.SetDisplayState(DisplayState);
            }
        }

        private void GetErrors_Click(object sender, RoutedEventArgs e)
        {
            if (VM.InstWorker != null)
            {
                string errorStr = VM.InstWorker.GetErrorString();
                ErrorLog.Text = errorStr;

                UpdateLayout();
            }
        }
    }
}
